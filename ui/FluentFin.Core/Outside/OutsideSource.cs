using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using FluentFin.Core.Updates;

namespace FluentFin.Core.Outside;

// 1.7 keyless: the shared outside layer (docs\reports "Nixie smart collections plan" §2, §3, §6 phase 2). Every list Nixie
// fetches from outside goes through an OutsideSource: one User-Agent naming the app and its repository, a timeout, gzip, one
// request at a time per source, a pause on 429 that honours Retry-After (never a retry inside the request), and Nixie's own
// words when a source refuses. A request is chosen from the enabled settings alone, never from the library (§3).

/// <summary>1.7: the outside sources Nixie can look lists up from. Each is off until the user turns it on (<see cref="Settings.SettingKeys"/>).</summary>
public enum OutsideSourceId
{
	/// <summary>TMDb's daily export files (files.tmdb.org): franchise and keyword names. No key.</summary>
	TmdbExports,

	/// <summary>Wikidata's query service: awards and finer genres. No key; the endpoint is a setting.</summary>
	Wikidata,

	/// <summary>Trakt (api.trakt.tv): trending, popular, anticipated, box office. Stream T's.</summary>
	Trakt,

	/// <summary>TMDb's API (api.themoviedb.org): trending, with the user's own token. Stream T's.</summary>
	TmdbTrending,
}

/// <summary>
/// 1.7: what Nixie knows about one outside source: its name on screen, its real address, how long a request may take, and how
/// long a list from it stays fresh (<see cref="RefreshAfter"/>) and may be kept at most (<see cref="MaxAge"/>).
/// </summary>
public sealed record OutsideSourceInfo(
	OutsideSourceId Id,
	string Name,
	Uri RealBase,
	TimeSpan Timeout,
	TimeSpan RefreshAfter,
	TimeSpan MaxAge)
{
	/// <summary>The folder name of the source's cached lists (<see cref="OutsideCache"/>).</summary>
	public string Key => Id.ToString().ToLowerInvariant();
}

/// <summary>1.7: the four sources and the rules every request to them follows.</summary>
public static class OutsideSources
{
	/// <summary>
	/// The contact address in the User-Agent: the repository's page, from the one constant the updater uses
	/// (<see cref="UpdateSource.RepositoryUrl"/>), so a repository rename can't leave a stale contact URL. Wikidata refuses a
	/// request without a contact in its User-Agent (403).
	/// </summary>
	public const string ContactUrl = UpdateSource.RepositoryUrl;

	/// <summary>"Nixie/1.7.0 (https://github.com/mbustos27/fluentfin-mpv)": the User-Agent of every outside request.</summary>
	public static string UserAgent(string version) => $"{AppIdentity.Name}/{(string.IsNullOrWhiteSpace(version) ? "0.0.0" : version.Trim())} ({ContactUrl})";

	/// <summary>How long a source pauses after a 429 that gives no Retry-After (Trakt's security systems send a bare 429).</summary>
	public static TimeSpan DefaultPause { get; } = TimeSpan.FromMinutes(5);

	/// <summary>The longest pause a Retry-After can ask for; anything longer is cut to this.</summary>
	public static TimeSpan LongestPause { get; } = TimeSpan.FromHours(24);

	/// <summary>
	/// After this many failed requests in a row (refused, timed out, unreachable) a source pauses for <see cref="ErrorPause"/>:
	/// a run of failures stops well before Wikidata's limit of 30 error queries a minute (the terms re-check, 2026-11-21).
	/// </summary>
	public const int ErrorsInARow = 3;

	/// <summary>How long a source pauses after <see cref="ErrorsInARow"/> failures in a row.</summary>
	public static TimeSpan ErrorPause { get; } = TimeSpan.FromMinutes(15);

	/// <summary>
	/// After a failed request (refused, timed out, unreadable), a source's stale lists are kept as they are this long before
	/// it is asked again (review #3: a bad file was downloaded again on every Collections visit). A success ends it.
	/// </summary>
	public static TimeSpan RetryAfterFailure { get; } = TimeSpan.FromHours(1);

	/// <summary>The biggest answer read from a source (an export file is a few MB); more stops the request.</summary>
	public const long MaxBodyBytes = 64L * 1024 * 1024;

	/// <summary>
	/// TMDb's daily export files. Franchise and keyword names barely change, so Nixie asks for them once a week (the owner,
	/// 2026-11-22: a gentler refresh); the API terms keep TMDb data at most 6 months, so the last good copy is used up to 182
	/// days when TMDb can't be reached (TMDb itself deletes the files after 3 months).
	/// </summary>
	public static OutsideSourceInfo TmdbExports { get; } = new(OutsideSourceId.TmdbExports, "TMDb export files", new Uri("https://files.tmdb.org/"),
		TimeSpan.FromSeconds(120), TimeSpan.FromDays(7), TimeSpan.FromDays(182));

	/// <summary>Wikidata's query service: 60 s per query on its side, so a little longer here; lists cached a week.</summary>
	public static OutsideSourceInfo Wikidata { get; } = new(OutsideSourceId.Wikidata, "Wikidata", new Uri("https://query.wikidata.org/"),
		TimeSpan.FromSeconds(65), TimeSpan.FromDays(7), TimeSpan.FromDays(60));

	/// <summary>Trakt: lists cached 6 to 24 hours (its Use Policy: no mirroring).</summary>
	public static OutsideSourceInfo Trakt { get; } = new(OutsideSourceId.Trakt, "Trakt", new Uri("https://api.trakt.tv/"),
		TimeSpan.FromSeconds(30), TimeSpan.FromHours(6), TimeSpan.FromHours(24));

	/// <summary>TMDb's API (trending): cached daily, never past the terms' 6 months.</summary>
	public static OutsideSourceInfo TmdbTrending { get; } = new(OutsideSourceId.TmdbTrending, "TMDb", new Uri("https://api.themoviedb.org/"),
		TimeSpan.FromSeconds(30), TimeSpan.FromDays(1), TimeSpan.FromDays(7));

	public static IReadOnlyList<OutsideSourceInfo> All { get; } = [TmdbExports, Wikidata, Trakt, TmdbTrending];

	public static OutsideSourceInfo Get(OutsideSourceId id) => All.First(x => x.Id == id);
}

/// <summary>1.7: how an outside request ended.</summary>
public enum OutsideOutcome
{
	/// <summary>A 2xx answer; <see cref="OutsideResult.Body"/> holds it, gzip already undone.</summary>
	Ok,

	/// <summary>The source answered with an error status (401, 403, 404, 5xx).</summary>
	Refused,

	/// <summary>The source answered 429: it is paused until <see cref="OutsideResult.PausedUntil"/>.</summary>
	RateLimited,

	/// <summary>Not sent: the source is still paused after an earlier 429.</summary>
	Paused,

	/// <summary>No answer within the source's timeout.</summary>
	TimedOut,

	/// <summary>The network failed (no connection, DNS, TLS, a cut answer, an answer too big).</summary>
	Failed,

	/// <summary>Not sent: the address breaks a rule (not https, credentials in it, a harness run asking a real host).</summary>
	Blocked,
}

/// <summary>
/// 1.7: one outside request's result. <see cref="Message"/> is Nixie's own sentence for the user when it isn't
/// <see cref="OutsideOutcome.Ok"/> (never a stranger's error page).
/// </summary>
public sealed record OutsideResult(OutsideOutcome Outcome, int? Status, byte[]? Body, string? Message, DateTimeOffset? PausedUntil = null)
{
	public bool Ok => Outcome == OutsideOutcome.Ok;

	/// <summary>The answer's headers, names lowercase (several values joined with ", "); empty when nothing came back.</summary>
	public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

	/// <summary>The body as UTF-8 text; "" without one.</summary>
	public string Text => Body is null ? "" : System.Text.Encoding.UTF8.GetString(Body);

	public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : null;
}

/// <summary>
/// 1.7: where outside requests really go. A normal run asks the real hosts over https. A harness run (a dev build under the
/// off-screen harness, never a release) sends every source to the fake server: <c>http://127.0.0.1:port/__harness/outside/&lt;host&gt;/&lt;path&gt;</c>,
/// so no automated run reaches a real service; with no fake given, a closed loopback port, so it fails rather than leaks.
/// </summary>
public sealed class OutsideRouting
{
	/// <summary>The harness's variable (<c>NIXIE_OUTSIDE_BASE</c>, read through <see cref="AppIdentity.ReadEnvironment"/>): the fake server's origin.</summary>
	public const string HarnessVariable = "OUTSIDE_BASE";

	/// <summary>The fake server's path prefix for outside sources (tools\ui-harness\fakeserver\outside.py).</summary>
	public const string HarnessPrefix = "/__harness/outside/";

	/// <summary>Where a harness run with no fake server given sends outside requests: the discard port on loopback (nothing answers).</summary>
	public static Uri ClosedOrigin { get; } = new("http://127.0.0.1:9/");

	private OutsideRouting(Uri? harnessOrigin) => HarnessOrigin = harnessOrigin;

	/// <summary>Real hosts, https only.</summary>
	public static OutsideRouting Real { get; } = new(null);

	/// <summary>The fake server's origin in a harness run; null in a normal one.</summary>
	public Uri? HarnessOrigin { get; }

	public bool IsHarness => HarnessOrigin is not null;

	/// <summary>
	/// The routing for this run: <see cref="Real"/> for a release package, or when the harness is off; in a harness run, the fake
	/// server named by <paramref name="overrideValue"/> when it is http on loopback with no credentials, else <see cref="ClosedOrigin"/>.
	/// </summary>
	public static OutsideRouting For(string? overrideValue, bool harnessOn, bool releasePackage)
	{
		if (releasePackage || !harnessOn)
		{
			return Real;
		}

		return Uri.TryCreate(overrideValue?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback
			&& uri.UserInfo.Length == 0
			? new OutsideRouting(new Uri(uri.GetLeftPart(UriPartial.Authority) + "/"))
			: new OutsideRouting(ClosedOrigin);
	}

	/// <summary>The off-screen harness's own variable: the glass kept active (FluentFin's GlassActivity.AlwaysActiveVariable).</summary>
	public const string GlassAlwaysActiveVariable = "FLUENTFIN_MPV_GLASS_ALWAYS_ACTIVE";

	/// <summary>
	/// The routing for this run from its environment (<paramref name="environment"/> stands for
	/// <see cref="Environment.GetEnvironmentVariable(string)"/>): a dev build is a harness run when <c>NIXIE_OUTSIDE_BASE</c> is set
	/// (drive.ps1 sets it for every run, with or without -GlassFollowsActivity) or the harness keeps the glass active
	/// (<see cref="GlassAlwaysActiveVariable"/>); then every source goes to the fake (or the closed port), never a real host. A
	/// release package always asks the real hosts.
	/// </summary>
	public static OutsideRouting FromEnvironment(Func<string, string?> environment, bool releasePackage)
	{
		var overrideValue = AppIdentity.ReadEnvironment(HarnessVariable, environment);
		var harnessOn = overrideValue is not null || environment(GlassAlwaysActiveVariable) == "1";
		return For(overrideValue, harnessOn, releasePackage);
	}

	/// <summary>A routing to <paramref name="origin"/> (tests: a fake on loopback).</summary>
	public static OutsideRouting Harness(Uri origin) => new(new Uri(origin.GetLeftPart(UriPartial.Authority) + "/"));

	/// <summary>
	/// The address <paramref name="real"/> is sent to, or null with <paramref name="refusal"/> saying why it isn't sent: a real
	/// address must be https with no credentials; in a harness run it becomes the fake's
	/// <c>/__harness/outside/&lt;host&gt;&lt;path and query&gt;</c>.
	/// </summary>
	public Uri? Resolve(Uri real, out string? refusal)
	{
		refusal = null;
		if (!real.IsAbsoluteUri || real.UserInfo.Length > 0)
		{
			refusal = "the address has credentials in it";
			return null;
		}

		if (HarnessOrigin is { } origin)
		{
			return new Uri(origin, HarnessPrefix + real.IdnHost.ToLowerInvariant() + real.PathAndQuery);
		}

		if (real.Scheme != Uri.UriSchemeHttps)
		{
			refusal = "the address isn't https";
			return null;
		}

		return real;
	}

	/// <summary>Whether an answer may come from <paramref name="final"/> (after redirects): the fake server in a harness run, else https.</summary>
	public bool IsAllowedFinal(Uri? final) =>
		final is not null && (HarnessOrigin is { } origin
			? final.Scheme == origin.Scheme && final.IsLoopback && final.Port == origin.Port
			: final.Scheme == Uri.UriSchemeHttps && final.UserInfo.Length == 0);
}

/// <summary>
/// 1.7: one outside source's requests: a User-Agent naming Nixie and its repository (<see cref="OutsideSources.UserAgent"/>),
/// gzip, the source's timeout, one request at a time, and on 429 a pause (Retry-After, else <see cref="OutsideSources.DefaultPause"/>)
/// during which nothing is sent. A 429 is never retried inside the request. Every refusal comes back with Nixie's own words.
/// </summary>
public sealed class OutsideSource(OutsideSourceInfo info, HttpClient http, OutsideRouting routing, string version, Func<DateTimeOffset>? clock = null)
{
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
	private DateTimeOffset? _pausedUntil;
	private OutsideResult? _pausedBy;
	private int _errorsInARow;
	private int _errorsBeforeOk;

	public OutsideSourceInfo Info => info;

	public OutsideRouting Routing => routing;

	/// <summary>The User-Agent this source sends.</summary>
	public string UserAgent { get; } = OutsideSources.UserAgent(version);

	/// <summary>Until when nothing is sent, after a 429; null when the source isn't paused.</summary>
	public DateTimeOffset? PausedUntil => _pausedUntil is { } until && until > _clock() ? until : null;

	/// <summary>The last request's result (Settings shows its message), null before the first.</summary>
	public OutsideResult? Last { get; private set; }

	/// <summary>When the last failed request ended (refused, timed out, unreachable, blocked, unreadable); null after a success.</summary>
	public DateTimeOffset? LastFailedAt { get; private set; }

	/// <summary>Whether the source failed within <paramref name="window"/> and hasn't answered well since.</summary>
	public bool FailedWithin(TimeSpan window) => LastFailedAt is { } at && _clock() - at < window;

	/// <summary>
	/// An answer that came back 200 but couldn't be read (a cut or corrupt file, not the list it should be): counted as a
	/// failure like a refusal (the run of failures, <see cref="LastFailedAt"/>), with Nixie's words for it.
	/// </summary>
	public OutsideResult Unreadable(Uri url, OutsideResult answer)
	{
		_errorsInARow = _errorsBeforeOk; // the 200 that came first wasn't a success after all: the run of failures goes on
		return Finish(new OutsideResult(OutsideOutcome.Failed, answer.Status, null, OutsideMessages.Unreadable(info)) { Headers = answer.Headers }, url);
	}

	/// <summary>Loading this source's lists failed past its requests (an exception the loader didn't expect): counted the same way.</summary>
	public OutsideResult LoadFailed() => Finish(new OutsideResult(OutsideOutcome.Failed, null, null, OutsideMessages.Unreadable(info)), info.RealBase);

	/// <summary>Logs <paramref name="note"/> with the source's log lines (a cache that couldn't be written).</summary>
	public void Note(string note) => Noted?.Invoke($"[outside] {info.Name}: {note}");

	/// <summary>Raised after each request with its result.</summary>
	public event Action<OutsideSource, OutsideResult>? Answered;

	/// <summary>What the app logs about a request: the source, the path (never the query) and how it ended.</summary>
	public event Action<string>? Noted;

	/// <summary>
	/// GET <paramref name="url"/> (a real address: <see cref="OutsideRouting.Resolve"/> sends a harness run to its fake) with the
	/// source's rules and <paramref name="headers"/> (Accept, a key header). Never throws for a network or HTTP failure: the
	/// result says what happened. Cancelling <paramref name="cancellation"/> does throw.
	/// </summary>
	public async Task<OutsideResult> GetAsync(Uri url, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellation = default)
	{
		if (PausedUntil is { } paused)
		{
			return Finish(new OutsideResult(OutsideOutcome.Paused, null, null, PausedMessage(paused), paused), url);
		}

		await _gate.WaitAsync(cancellation).ConfigureAwait(false);
		try
		{
			// a request that waited behind one that got a 429 is not sent either
			if (PausedUntil is { } pausedNow)
			{
				return Finish(new OutsideResult(OutsideOutcome.Paused, null, null, PausedMessage(pausedNow), pausedNow), url);
			}

			var target = routing.Resolve(url, out var refusal);
			if (target is null)
			{
				return Finish(new OutsideResult(OutsideOutcome.Blocked, null, null, OutsideMessages.Blocked(info, refusal!)), url);
			}

			return Finish(await SendAsync(target, headers, cancellation).ConfigureAwait(false), url);
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <summary>The most redirects one request follows, each checked before it is followed (<see cref="OutsideRouting.IsAllowedFinal"/>).</summary>
	public const int MostRedirects = 3;

	private static HttpRequestMessage Request(Uri target, string userAgent, IReadOnlyDictionary<string, string>? headers, bool sameHost)
	{
		var request = new HttpRequestMessage(HttpMethod.Get, target);
		request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
		request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
		foreach (var (name, value) in headers ?? new Dictionary<string, string>())
		{
			// 1.7 trending: Trakt asks for Content-Type: application/json on every request (its required-headers guide, the
			// terms re-check). A content header can't go on the request itself, so it goes on an empty body.
			if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
			{
				request.Content ??= new ByteArrayContent([]);
				request.Content.Headers.TryAddWithoutValidation(name, value);
				continue;
			}
			// end 1.7 trending

			// a key never follows a redirect to another host (as HttpClient's own redirects drop Authorization)
			// 1.7 trending: Trakt's Client ID header is a key too (the review, #5)
			if (!string.Equals(name, "User-Agent", StringComparison.OrdinalIgnoreCase) && (sameHost || !IsKeyHeader(name)))
			{
				request.Headers.TryAddWithoutValidation(name, value);
			}
		}

		return request;
	}

	// 1.7 trending
	/// <summary>The headers that carry a key, dropped on a redirect to another host: Authorization (TMDb's token) and Trakt's Client ID.</summary>
	public static bool IsKeyHeader(string name) =>
		string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "trakt-api-key", StringComparison.OrdinalIgnoreCase);
	// end 1.7 trending

	private async Task<OutsideResult> SendAsync(Uri target, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellation)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
		timeout.CancelAfter(info.Timeout);
		try
		{
			// redirects are followed here, one at a time, each address checked first: a harness run never leaves the fake, a
			// real one never leaves https (the app's client follows none itself: OutsideHub.CreateHttpClient)
			var address = target;
			HttpResponseMessage? response = null;
			for (var hop = 0; ; hop++)
			{
				using var request = Request(address, UserAgent, headers, address.Host == target.Host);
				response?.Dispose();
				response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
				var final = response.RequestMessage?.RequestUri ?? address;
				if (!routing.IsAllowedFinal(final))
				{
					var blocked = new OutsideResult(OutsideOutcome.Blocked, (int)response.StatusCode, null, OutsideMessages.Blocked(info, "the answer came from somewhere else")) { Headers = HeadersOf(response) };
					response.Dispose();
					return blocked;
				}

				if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } location)
				{
					break;
				}

				var next = location.IsAbsoluteUri ? location : new Uri(final, location);
				if (hop >= MostRedirects || !routing.IsAllowedFinal(next))
				{
					var why = hop >= MostRedirects ? "it was sent on too many times" : "it was sent on to somewhere else";
					var stopped = new OutsideResult(OutsideOutcome.Blocked, (int)response.StatusCode, null, OutsideMessages.Blocked(info, why)) { Headers = HeadersOf(response) };
					response.Dispose();
					return stopped;
				}

				address = next;
			}

			using var answer = response;
			var answered = HeadersOf(response);
			var status = (int)response.StatusCode;

			if (response.StatusCode == HttpStatusCode.TooManyRequests)
			{
				var until = _clock() + PauseFor(response.Headers.RetryAfter, _clock());
				_pausedUntil = until;
				_pausedBy = null;
				return new OutsideResult(OutsideOutcome.RateLimited, status, null, OutsideMessages.Paused(info, until, _clock()), until) { Headers = answered };
			}

			if (!response.IsSuccessStatusCode)
			{
				// read a little of a refusal's body: Wikidata's 403 and query-next's "Unauthorized User-Agent" say why in it
				var why = await ReadAsync(response, 16 * 1024, truncate: true, timeout.Token).ConfigureAwait(false);
				return new OutsideResult(OutsideOutcome.Refused, status, why, OutsideMessages.Refused(info, status)) { Headers = answered };
			}

			var body = await ReadAsync(response, OutsideSources.MaxBodyBytes, truncate: false, timeout.Token).ConfigureAwait(false);
			if (body is null)
			{
				return new OutsideResult(OutsideOutcome.Failed, status, null, OutsideMessages.Failed(info)) { Headers = answered };
			}

			return new OutsideResult(OutsideOutcome.Ok, status, body, null) { Headers = answered };
		}
		catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
		{
			return new OutsideResult(OutsideOutcome.TimedOut, null, null, OutsideMessages.TimedOut(info));
		}
		catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
		{
			return new OutsideResult(OutsideOutcome.Failed, null, null, OutsideMessages.Failed(info));
		}
	}

	// a request not sent while paused: after a 429, when Nixie asks again; after a run of refusals, the last one's words
	private string PausedMessage(DateTimeOffset until) => _pausedBy?.Message ?? OutsideMessages.Paused(info, until, _clock());

	/// <summary>The pause after a 429: Retry-After's seconds or date, else <see cref="OutsideSources.DefaultPause"/>; 1 s to <see cref="OutsideSources.LongestPause"/>.</summary>
	public static TimeSpan PauseFor(RetryConditionHeaderValue? retryAfter, DateTimeOffset now)
	{
		var pause = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - now : OutsideSources.DefaultPause);
		return pause < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : pause > OutsideSources.LongestPause ? OutsideSources.LongestPause : pause;
	}

	/// <summary>
	/// The body, gzip undone when the answer says Content-Encoding: gzip. Past <paramref name="limit"/> bytes: its first
	/// <paramref name="limit"/> when <paramref name="truncate"/>, else null.
	/// </summary>
	private static async Task<byte[]?> ReadAsync(HttpResponseMessage response, long limit, bool truncate, CancellationToken cancellation)
	{
		await using var raw = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
		var gzip = response.Content.Headers.ContentEncoding.Any(x => string.Equals(x, "gzip", StringComparison.OrdinalIgnoreCase));
		await using var stream = gzip ? new GZipStream(raw, CompressionMode.Decompress) : raw;
		using var buffer = new MemoryStream();
		var chunk = new byte[81920];
		int read;
		while ((read = await stream.ReadAsync(chunk, cancellation).ConfigureAwait(false)) > 0)
		{
			if (buffer.Length + read > limit)
			{
				return truncate ? buffer.ToArray() : null;
			}

			buffer.Write(chunk, 0, read);
		}

		return buffer.ToArray();
	}

	private static Dictionary<string, string> HeadersOf(HttpResponseMessage response)
	{
		var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
		{
			all[name.ToLowerInvariant()] = string.Join(", ", values);
		}

		return all;
	}

	private OutsideResult Finish(OutsideResult result, Uri url)
	{
		if (result.Outcome is OutsideOutcome.Refused or OutsideOutcome.TimedOut or OutsideOutcome.Failed or OutsideOutcome.Blocked)
		{
			LastFailedAt = _clock();
		}
		else if (result.Outcome is OutsideOutcome.Ok)
		{
			LastFailedAt = null;
		}

		if (result.Outcome is OutsideOutcome.Refused or OutsideOutcome.TimedOut or OutsideOutcome.Failed)
		{
			if (++_errorsInARow >= OutsideSources.ErrorsInARow)
			{
				_errorsInARow = 0;
				var until = _clock() + OutsideSources.ErrorPause;
				_pausedUntil = _pausedUntil is { } later && later > until ? later : until;
				_pausedBy = result;
				result = result with { PausedUntil = _pausedUntil };
			}
		}
		else if (result.Outcome is OutsideOutcome.Ok)
		{
			_errorsBeforeOk = _errorsInARow;
			_errorsInARow = 0;
		}

		Last = result;
		Noted?.Invoke($"[outside] {info.Name} {url.Host}{url.AbsolutePath}: {result.Outcome}{(result.Status is { } s ? " " + s.ToString(CultureInfo.InvariantCulture) : "")}");
		Answered?.Invoke(this, result);
		return result;
	}
}

/// <summary>1.7: Nixie's own sentences for an outside source that didn't give a list (Settings shows them under the source).</summary>
public static class OutsideMessages
{
	public static string Paused(OutsideSourceInfo source, DateTimeOffset until, DateTimeOffset now)
	{
		var wait = until - now;
		var when = wait < TimeSpan.FromMinutes(1) ? "in a minute"
			: wait < TimeSpan.FromHours(1) ? $"in {Math.Ceiling(wait.TotalMinutes):0} minutes"
			: wait < TimeSpan.FromHours(2) ? "in about an hour"
			: $"in about {Math.Round(wait.TotalHours):0} hours";
		return $"{source.Name} asked Nixie to slow down. Nixie asks again {when} and keeps the lists it has.";
	}

	public static string Refused(OutsideSourceInfo source, int status) => status switch
	{
		401 or 403 => $"{source.Name} refused the request ({status}). Nixie keeps the lists it has and tries again later.",
		404 or 410 => $"{source.Name} doesn't have that list right now ({status}). Nixie tries again later.",
		>= 500 => $"{source.Name} is having trouble ({status}). Nixie keeps the lists it has and tries again later.",
		_ => $"{source.Name} didn't give the list ({status}). Nixie keeps the lists it has and tries again later.",
	};

	public static string TimedOut(OutsideSourceInfo source) =>
		$"{source.Name} didn't answer within {source.Timeout.TotalSeconds:0} seconds. Nixie keeps the lists it has and tries again later.";

	public static string Failed(OutsideSourceInfo source) =>
		$"Nixie couldn't reach {source.Name}. Check your connection; Nixie keeps the lists it has and tries again later.";

	public static string Blocked(OutsideSourceInfo source, string why) => $"Nixie didn't ask {source.Name}: {why}.";

	public static string Unreadable(OutsideSourceInfo source) =>
		$"{source.Name} sent a list Nixie couldn't read. Nixie keeps the lists it has and tries again later.";
}
