using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace FluentFin.Core.Updates;

/// <summary>
/// Seaglass: "Download and install". Takes the release's installer and its .sha256 (only from this repository's releases) into a
/// folder of its own, checks the installer's SHA-256 against the file, and only then returns the installer's path.
/// A file that does not match is deleted and nothing runs.
/// Nixie 1.5: into Nixie's own data folder (<see cref="KnownFolders.Updates"/>, never a shared temp name), with its progress
/// (<see cref="DownloadProgress"/>), resumed where a stopped download left off (an HTTP Range from the <c>.part</c> file's
/// length; a server that sends the whole file again starts it over), started over once from the first byte when the whole
/// file does not match its checksum, and cancelled at any time (the part stays for the next try). The checked installer
/// keeps its <c>.sha256</c> beside it, so a later start finds it ready (<see cref="Downloaded"/>) without downloading it again.
/// Nixie 1.5 (second review): the .sha256 must also agree with the digest GitHub's API reports for the asset
/// (<see cref="ReleaseInfo.MsiDigest"/>); with no digest, <see cref="Noted"/> says so and the .sha256 alone decides.
/// </summary>
public sealed class UpdateDownloader(HttpClient http, string folder, string? userAgentVersion = null, Uri? harnessOrigin = null, Func<DateTimeOffset>? clock = null)
{
	private const int MaxHashFileBytes = 4096;

	public const string PartSuffix = ".part";
	public const string HashSuffix = ".sha256";

	/// <summary>How often progress is reported while bytes arrive.</summary>
	public static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(250);

	/// <summary>A download that sends nothing for this long has stalled: it stops (the part stays) and says so.</summary>
	public static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);

	/// <summary>Nixie 1.5: what the app logs about a download (which checks it passed); never a URL's query or anything of the user's.</summary>
	public event Action<string>? Noted;

	/// <summary>The folder the installer goes to (<see cref="KnownFolders.Updates"/>).</summary>
	public string Folder => folder;

	private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

	/// <summary>The installer of <paramref name="release"/> where <see cref="DownloadAsync"/> puts it.</summary>
	public string TargetFor(ReleaseInfo release) => Path.Combine(folder, release.MsiName ?? UpdateSource.InstallerName(release.Version));

	/// <summary>
	/// The installer of <paramref name="release"/> when it is already here and still matches the checksum kept beside it (a
	/// download finished in an earlier run); else null.
	/// </summary>
	public string? Downloaded(ReleaseInfo release)
	{
		try
		{
			if (!release.HasInstaller || release.MsiName is not { } name)
			{
				return null;
			}

			var target = TargetFor(release);
			var hash = target + HashSuffix;
			return File.Exists(target) && File.Exists(hash) && Sha256File.Parse(File.ReadAllText(hash), name) is { } expected
				&& (release.MsiDigest is null || string.Equals(release.MsiDigest, expected, StringComparison.OrdinalIgnoreCase)) && Matches(target, expected)
				? target : null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	public Task<string> DownloadAsync(ReleaseInfo release, CancellationToken cancellation = default) => DownloadAsync(release, null, null, cancellation);

	/// <summary>
	/// Downloads and checks the installer: <paramref name="progress"/> hears the bytes as they come, <paramref name="verifying"/>
	/// when the whole file is being checked. Throws <see cref="UpdateException"/> with what went wrong in plain words, or
	/// <see cref="OperationCanceledException"/> when cancelled.
	/// </summary>
	public async Task<string> DownloadAsync(ReleaseInfo release, IProgress<DownloadProgress>? progress, Action? verifying, CancellationToken cancellation = default)
	{
		var source = release.Source;
		if (!release.HasInstaller || !UpdateSource.IsDownload(release.MsiUrl, source) || !UpdateSource.IsDownload(release.Sha256Url, source)
			|| release.MsiName is not { } name || name != Path.GetFileName(name) || !name.EndsWith(".msi", StringComparison.Ordinal))
		{
			throw new UpdateException($"This release has no installer from the {AppIdentity.Name} releases.");
		}

		Directory.CreateDirectory(folder);
		var hashText = await Guard(() => FetchTextAsync(release.Sha256Url!, cancellation), cancellation).ConfigureAwait(false);
		var expected = Sha256File.Parse(hashText, name) ?? throw new UpdateException("The checksum file is not a SHA-256 for this installer.", integrity: true);
		// Nixie 1.5 (second review): GitHub's own record of the asset is the second witness; the two must agree before anything
		// is downloaded, and the download must match both (they are then the same hash)
		if (release.MsiDigest is { } digest)
		{
			if (!string.Equals(digest, expected, StringComparison.OrdinalIgnoreCase))
			{
				throw new UpdateException("The checksum file and GitHub's record of the installer disagree, so it was not used.", integrity: true);
			}

			Noted?.Invoke($"{name}: GitHub's digest and the .sha256 agree");
		}
		else
		{
			Noted?.Invoke($"{name}: GitHub's API gave no digest for it; checked against its .sha256 only");
		}

		var target = TargetFor(release);
		var part = target + PartSuffix;
		var hashFile = target + HashSuffix;
		if (File.Exists(target))
		{
			verifying?.Invoke();
			if (Matches(target, expected))
			{
				await File.WriteAllTextAsync(hashFile, $"{expected} *{name}\n", cancellation).ConfigureAwait(false);
				return target;
			}

			File.Delete(target);
		}

		// a mismatch starts over once, from the first byte (a resumed part that went wrong, or a bad copy on the way)
		for (var attempt = 0; attempt < 2; attempt++)
		{
			await Guard(() => FetchToPartAsync(release, part, progress, cancellation), cancellation).ConfigureAwait(false);
			verifying?.Invoke();
			if (Matches(part, expected))
			{
				File.Move(part, target, overwrite: true);
				await File.WriteAllTextAsync(hashFile, $"{expected} *{name}\n", CancellationToken.None).ConfigureAwait(false);
				return target;
			}

			File.Delete(part);
		}

		throw new UpdateException("The download didn't match its checksum twice, so it was not used.", integrity: true);
	}

	/// <summary>Turns what a download can throw into a sentence for the user; a cancel stays a cancel (the part stays).</summary>
	private static async Task<T> Guard<T>(Func<Task<T>> run, CancellationToken cancellation)
	{
		try
		{
			return await run().ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
		{
			throw;
		}
		catch (UpdateException)
		{
			throw;
		}
		catch (OperationCanceledException)
		{
			throw new UpdateException("The download stalled: GitHub stopped sending.");
		}
		catch (HttpRequestException ex) when (ex.StatusCode is { } status)
		{
			throw new UpdateException($"GitHub answered {(int)status}.");
		}
		catch (HttpRequestException)
		{
			throw new UpdateException("The download stopped: no connection to GitHub.");
		}
		catch (IOException ex) when (IsDiskFull(ex))
		{
			throw new UpdateException("There isn't enough room on the disk for the update.");
		}
		catch (IOException)
		{
			throw new UpdateException("The download stopped before the end.");
		}
		catch (UnauthorizedAccessException)
		{
			throw new UpdateException($"{AppIdentity.Name} couldn't write the update into its data folder.");
		}
	}

	private static async Task Guard(Func<Task> run, CancellationToken cancellation) => await Guard(async () => { await run().ConfigureAwait(false); return 0; }, cancellation).ConfigureAwait(false);

	/// <summary>ERROR_DISK_FULL (112) or ERROR_HANDLE_DISK_FULL (39).</summary>
	private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 112 or 39;

	private async Task FetchToPartAsync(ReleaseInfo release, string part, IProgress<DownloadProgress>? progress, CancellationToken cancellation)
	{
		for (var tries = 0; ; tries++)
		{
			var have = File.Exists(part) ? new FileInfo(part).Length : 0;
			using var request = new HttpRequestMessage(HttpMethod.Get, Resolve(release.MsiUrl!));
			AddUserAgent(request);
			if (have > 0)
			{
				request.Headers.Range = new RangeHeaderValue(have, null);
			}

			using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
			stall.CancelAfter(StallTimeout);
			using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
			if (have > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
			{
				return;   // the part already holds the whole file (or more): the checksum decides
			}

			response.EnsureSuccessStatusCode();
			CheckHost(response, "installer");

			var append = have > 0 && response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From == have;
			if (response.StatusCode == HttpStatusCode.PartialContent && !append)
			{
				// a range we did not ask for: start over from the first byte, once
				File.Delete(part);
				if (tries == 0)
				{
					continue;
				}

				throw new UpdateException("GitHub sent a part of the installer that doesn't fit, so the download stopped.");
			}

			var start = append ? have : 0;
			var total = response.Content.Headers.ContentRange?.Length ?? (response.Content.Headers.ContentLength is { } length ? start + length : release.MsiSize);
			var meter = new ProgressMeter(_clock);
			progress?.Report(meter.Sample(start, total));
			await using var stream = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
			await using var output = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
			var buffer = new byte[1 << 16];
			var done = start;
			var reported = _clock();
			while (true)
			{
				stall.CancelAfter(StallTimeout);
				var n = await stream.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
				if (n == 0)
				{
					break;
				}

				await output.WriteAsync(buffer.AsMemory(0, n), stall.Token).ConfigureAwait(false);
				done += n;
				if (_clock() - reported >= ReportEvery)
				{
					reported = _clock();
					progress?.Report(meter.Sample(done, total));
				}
			}

			progress?.Report(meter.Sample(done, total));
			if (total is { } whole && done < whole)
			{
				throw new UpdateException("The download stopped before the end.");
			}

			return;
		}
	}

	public static bool Matches(string file, string expectedSha256)
	{
		using var stream = File.OpenRead(file);
		return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expectedSha256, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Where a release asset is fetched from: GitHub, or in a harness run (a dev build pointed at its fake server:
	/// <see cref="UpdateSource.ApiFor"/>) the fake server's copy of the same path under <c>/__harness/release/download/</c>.
	/// </summary>
	private Uri Resolve(string url)
	{
		if (harnessOrigin is null)
		{
			return new Uri(url);
		}

		var marker = url.IndexOf("/releases/download/", StringComparison.Ordinal);
		return new Uri(harnessOrigin, "/__harness/release/download/" + url[(marker + "/releases/download/".Length)..]);
	}

	private void CheckHost(HttpResponseMessage response, string what)
	{
		var final = response.RequestMessage?.RequestUri;
		var ok = harnessOrigin is { } origin
			? final is not null && final.Scheme == origin.Scheme && final.Host == origin.Host && final.Port == origin.Port
			: UpdateSource.IsDownloadHost(final);
		if (!ok)
		{
			throw new UpdateException($"The {what} was served from somewhere that is not GitHub, so it was not used.", integrity: true);
		}
	}

	private void AddUserAgent(HttpRequestMessage request) =>
		request.Headers.UserAgent.Add(userAgentVersion is { Length: > 0 } version ? new ProductInfoHeaderValue(AppIdentity.Name, version) : new ProductInfoHeaderValue(AppIdentity.Name, null));

	private async Task<string> FetchTextAsync(string url, CancellationToken cancellation)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, Resolve(url));
		AddUserAgent(request);
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
		timeout.CancelAfter(StallTimeout);
		using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		CheckHost(response, "checksum");

		await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
		var buffer = new byte[MaxHashFileBytes + 1];
		var read = 0;
		while (read < buffer.Length)
		{
			var n = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token).ConfigureAwait(false);
			if (n == 0)
			{
				break;
			}

			read += n;
		}

		return read > MaxHashFileBytes ? throw new UpdateException("The checksum file is too big.", integrity: true) : System.Text.Encoding.UTF8.GetString(buffer, 0, read);
	}
}
