using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace FluentFin.Core.Updates;

public enum UpdateCheckStatus
{
	/// <summary>This version is the latest (or newer than the latest release).</summary>
	UpToDate,

	/// <summary>A newer release exists: <see cref="UpdateCheckResult.Release"/>.</summary>
	Available,

	/// <summary>No answer, a bad answer or an error: nothing to say to the user.</summary>
	Failed,

	/// <summary>GitHub's rate limit: nothing was asked again before <see cref="UpdateCheckResult.RetryAfter"/>.</summary>
	RateLimited,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, ReleaseInfo? Release = null, DateTimeOffset? RetryAfter = null);

/// <summary>
/// Seaglass: the opt-in update check. One request, to GitHub's "latest release" API for this repository, with a plain
/// User-Agent ("Nixie/x.y.z") and nothing else about the user or the PC. It fails silently, and honours GitHub's rate limit
/// headers (Retry-After, x-ratelimit-remaining/reset): a limited answer is reported, never retried here.
/// </summary>
public sealed class UpdateChecker(HttpClient http, string currentVersion)
{
	public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

	public async Task<UpdateCheckResult> CheckAsync(DateTimeOffset now, CancellationToken cancellation = default)
	{
		try
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, UpdateSource.LatestReleaseApi);
			request.Headers.UserAgent.Add(new ProductInfoHeaderValue(AppIdentity.Name, currentVersion));
			request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
			timeout.CancelAfter(Timeout);
			using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);

			if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
			{
				return RateLimit(response, now) is { } until
					? new UpdateCheckResult(UpdateCheckStatus.RateLimited, RetryAfter: until)
					: new UpdateCheckResult(UpdateCheckStatus.Failed);
			}

			if (!response.IsSuccessStatusCode)
			{
				return new UpdateCheckResult(UpdateCheckStatus.Failed);
			}

			var release = ReleaseInfo.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
			if (release is null)
			{
				return new UpdateCheckResult(UpdateCheckStatus.Failed);
			}

			return UpdateVersion.IsNewer(release.Version, currentVersion)
				? new UpdateCheckResult(UpdateCheckStatus.Available, release)
				: new UpdateCheckResult(UpdateCheckStatus.UpToDate);
		}
		catch (Exception)
		{
			return new UpdateCheckResult(UpdateCheckStatus.Failed);
		}
	}

	/// <summary>When GitHub says to wait: a 403 or 429 with Retry-After, or with no requests remaining and the time the limit resets; else null.</summary>
	internal static DateTimeOffset? RateLimit(HttpResponseMessage response, DateTimeOffset now)
	{
		if (response.Headers.RetryAfter is { } retry)
		{
			return retry.Date ?? now + (retry.Delta ?? TimeSpan.FromMinutes(1));
		}

		if (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault()?.Trim() == "0")
		{
			if (response.Headers.TryGetValues("x-ratelimit-reset", out var reset)
				&& long.TryParse(reset.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
			{
				return DateTimeOffset.FromUnixTimeSeconds(epoch);
			}

			return now + TimeSpan.FromHours(1);
		}

		return response.StatusCode == HttpStatusCode.TooManyRequests ? now + TimeSpan.FromHours(1) : null;
	}
}

public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// Seaglass: "Download and install". Takes the release's installer and its .sha256 (only from this repository's releases) into a
/// folder of its own, checks the installer's SHA-256 against the file, and only then returns the installer's path.
/// A file that does not match is deleted and nothing runs.
/// </summary>
public sealed class UpdateDownloader(HttpClient http, string folder)
{
	private const int MaxHashFileBytes = 4096;

	public async Task<string> DownloadAsync(ReleaseInfo release, CancellationToken cancellation = default)
	{
		if (!release.HasInstaller || !UpdateSource.IsDownload(release.MsiUrl) || !UpdateSource.IsDownload(release.Sha256Url)
			|| release.MsiName is not { } name || name != Path.GetFileName(name) || !name.EndsWith(".msi", StringComparison.Ordinal))
		{
			throw new UpdateException($"This release has no installer from the {AppIdentity.Name} releases.");
		}

		Directory.CreateDirectory(folder);
		var hashText = await FetchTextAsync(release.Sha256Url!, cancellation).ConfigureAwait(false);
		var expected = Sha256File.Parse(hashText, name) ?? throw new UpdateException("The checksum file is not a SHA-256 for this installer.");

		var target = Path.Combine(folder, name);
		var part = target + ".part";
		try
		{
			using (var response = await http.GetAsync(release.MsiUrl, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
			{
				response.EnsureSuccessStatusCode();
				if (!UpdateSource.IsDownloadHost(response.RequestMessage?.RequestUri))
				{
					throw new UpdateException("The installer was served from somewhere that is not GitHub, so it was not used.");
				}

				await using var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
				await using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
				await source.CopyToAsync(output, cancellation).ConfigureAwait(false);
			}

			if (!Matches(part, expected))
			{
				throw new UpdateException("The downloaded installer does not match its checksum, so it was not run.");
			}

			File.Move(part, target, overwrite: true);
			return target;
		}
		finally
		{
			if (File.Exists(part))
			{
				File.Delete(part);
			}
		}
	}

	public static bool Matches(string file, string expectedSha256)
	{
		using var stream = File.OpenRead(file);
		return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expectedSha256, StringComparison.OrdinalIgnoreCase);
	}

	private async Task<string> FetchTextAsync(string url, CancellationToken cancellation)
	{
		using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		if (!UpdateSource.IsDownloadHost(response.RequestMessage?.RequestUri))
		{
			throw new UpdateException("The checksum was served from somewhere that is not GitHub, so it was not used.");
		}

		await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
		var buffer = new byte[MaxHashFileBytes + 1];
		var read = 0;
		while (read < buffer.Length)
		{
			var n = await stream.ReadAsync(buffer.AsMemory(read), cancellation).ConfigureAwait(false);
			if (n == 0)
			{
				break;
			}

			read += n;
		}

		return read > MaxHashFileBytes ? throw new UpdateException("The checksum file is too big.") : System.Text.Encoding.UTF8.GetString(buffer, 0, read);
	}
}
