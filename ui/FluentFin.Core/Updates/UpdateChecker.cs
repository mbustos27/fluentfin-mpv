using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

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

/// <param name="Reason">Nixie 1.3.1: why a check failed, in plain words for "Check now" ("no connection to GitHub").</param>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, ReleaseInfo? Release = null, DateTimeOffset? RetryAfter = null, string? Reason = null);

/// <summary>
/// Seaglass: the opt-in update check. One request, to GitHub's "latest release" API for this repository, with a plain
/// User-Agent ("Nixie/x.y.z") and nothing else about the user or the PC. It fails silently, and honours GitHub's rate limit
/// headers (Retry-After, x-ratelimit-remaining/reset): a limited answer is reported, never retried here.
/// </summary>
public sealed class UpdateChecker(HttpClient http, string currentVersion, Uri? api = null)
{
	/// <summary>Nixie 1.3.1: where the check asks (GitHub's latest-release API; the harness's fake server in its dev runs: <see cref="UpdateSource.ApiFor"/>).</summary>
	public Uri Api { get; } = api ?? new Uri(UpdateSource.LatestReleaseApi);

	public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

	/// <summary>BH-150-m8: the API version the last answer says it used (GitHub's <see cref="UpdateSource.ApiVersionSelectedHeader"/>), or null.</summary>
	public string? ApiVersionSelected { get; private set; }

	public async Task<UpdateCheckResult> CheckAsync(DateTimeOffset now, CancellationToken cancellation = default)
	{
		try
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, Api);
			request.Headers.UserAgent.Add(new ProductInfoHeaderValue(AppIdentity.Name, currentVersion));
			request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
			request.Headers.Add(UpdateSource.ApiVersionHeader, UpdateSource.ApiVersion);   // BH-150-m8: the answer's shape, pinned
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
			timeout.CancelAfter(Timeout);
			using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
			ApiVersionSelected = response.Headers.TryGetValues(UpdateSource.ApiVersionSelectedHeader, out var selected) ? selected.FirstOrDefault()?.Trim() : null;

			if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
			{
				// Nixie 1.5: a 403 that is not the rate limit is GitHub refusing the request (a missing User-Agent is one)
				return RateLimit(response, now) is { } until
					? new UpdateCheckResult(UpdateCheckStatus.RateLimited, RetryAfter: until)
					: new UpdateCheckResult(UpdateCheckStatus.Failed, Reason: response.StatusCode == HttpStatusCode.Forbidden
						? "GitHub refused the request (403)" : $"GitHub answered {(int)response.StatusCode}");
			}

			if (!response.IsSuccessStatusCode)
			{
				return new UpdateCheckResult(UpdateCheckStatus.Failed, Reason: $"GitHub answered {(int)response.StatusCode}");
			}

			// Nixie 1.5 (BH-140X-m3): GitHub answers a renamed or moved repository's API through a redirect (to
			// /repositories/<id>/...): only then is the release taken from the repository its page names
			var release = ReleaseInfo.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false), Redirected(Api, response.RequestMessage?.RequestUri));
			if (release is null)
			{
				return new UpdateCheckResult(UpdateCheckStatus.Failed, Reason: "GitHub's answer could not be read");
			}

			return UpdateVersion.IsNewer(release.Version, currentVersion)
				? new UpdateCheckResult(UpdateCheckStatus.Available, release)
				: new UpdateCheckResult(UpdateCheckStatus.UpToDate);
		}
		catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
		{
			return new UpdateCheckResult(UpdateCheckStatus.Failed, Reason: "GitHub did not answer in time");
		}
		catch (HttpRequestException)
		{
			return new UpdateCheckResult(UpdateCheckStatus.Failed, Reason: "no connection to GitHub");
		}
		catch (Exception)
		{
			return new UpdateCheckResult(UpdateCheckStatus.Failed, Reason: "the check did not finish");
		}
	}

	/// <summary>
	/// Nixie 1.5 (BH-140X-m3): whether the answer came from another address than the one asked, on the same https (or the
	/// harness's loopback) origin: GitHub's own redirect for a renamed repository. A redirect to another host never counts.
	/// </summary>
	public static bool Redirected(Uri asked, Uri? answered) =>
		answered is { } final && final.IsAbsoluteUri && final.Scheme == asked.Scheme
		&& string.Equals(final.Host, asked.Host, StringComparison.OrdinalIgnoreCase) && final.Port == asked.Port
		&& !string.Equals(final.AbsolutePath, asked.AbsolutePath, StringComparison.Ordinal);

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

/// <param name="Integrity">
/// Nixie 1.5 (review m4): the release's own files refused it (its checksum file, GitHub's digest, a download that never
/// matches, a host that is not GitHub): fetching it again gets the same, so that version is not fetched again by itself.
/// </param>
public sealed class UpdateException(string message, bool integrity = false) : Exception(message)
{
	public bool Integrity { get; } = integrity;
}
