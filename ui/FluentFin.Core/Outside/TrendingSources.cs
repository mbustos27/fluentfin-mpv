using System.Text.Json;

namespace FluentFin.Core.Outside;

/// <summary>1.7 trending: one public list as Nixie read it: which source and list, when, and its titles in the list's order.</summary>
public sealed record TrendingListRead(OutsideSourceId Source, TrendingList List, ListedKind Kind, DateTimeOffset FetchedAt, IReadOnlyList<ListedTitle> Titles);

/// <summary>1.7 trending: the lists of the sources that are on, as they are now; <see cref="Version"/> moves whenever they change.</summary>
public sealed record TrendingSnapshot(IReadOnlyList<TrendingListRead> Lists, int Version)
{
	public static TrendingSnapshot Empty { get; } = new([], 0);

	/// <summary>The sources these lists came from, Trakt first.</summary>
	public IReadOnlyList<OutsideSourceId> Sources => [.. Lists.Select(x => x.Source).Distinct().Order()];
}

/// <summary>
/// 1.7 trending: Trakt's lists (trending, popular, anticipated, box office) and TMDb's trending week, read through the outside
/// layer and kept in its cache of public lists (the same for everyone: nothing of the user's in them).
/// <para>
/// Privacy (the smart collections plan, section 3, binding): which lists are asked for, and when, depends only on the switches
/// (Settings &gt; Look up lists online), whether this build has a Trakt Client ID, whether the user gave a TMDb token, and how
/// old the cached lists are. Never on the library: the requests are the fixed addresses of <see cref="TraktLists"/> and
/// <see cref="TmdbLists"/>, and matching happens elsewhere, on this PC (<see cref="TrendingBuilder"/>).
/// </para>
/// A source asks only when it is on and able to: Trakt with a Client ID in this build (<see cref="TraktClientId"/>), TMDb with
/// the user's token (<see cref="TmdbToken"/>). Turning one off forgets its lists, in memory and in the cache.
/// </summary>
public sealed class TrendingSources
{
	private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

	private readonly OutsideHub _hub;
	private readonly TmdbToken? _token;
	private readonly string? _appToken;
	private readonly Func<DateTimeOffset> _clock;
	private readonly Lock _lock = new();
	private readonly Dictionary<string, TrendingListRead> _held = [];
	private readonly Dictionary<OutsideSourceId, string> _problems = [];
	private Task? _refreshing;
	private int _version = 1;
	private volatile bool _appTokenRefused;

	/// <param name="tmdbAppToken">Nixie's own TMDb token, built into a release (<see cref="TmdbAppToken"/>); the user's own wins.</param>
	public TrendingSources(OutsideHub hub, TmdbToken? token, string? traktClientId, Func<DateTimeOffset>? clock = null, string? tmdbAppToken = null)
	{
		_hub = hub;
		_token = token;
		_appToken = string.IsNullOrWhiteSpace(tmdbAppToken) || TmdbLists.TokenProblem(tmdbAppToken) is not null ? null : tmdbAppToken.Trim();
		_clock = clock ?? (() => DateTimeOffset.UtcNow);
		TraktClientIdValue = string.IsNullOrWhiteSpace(traktClientId) ? null : traktClientId.Trim();
		hub.SwitchChanged += OnSwitchChanged;
		if (token is not null)
		{
			token.Changed += (_, _) => OnTokenChanged();
		}
	}

	/// <summary>The two sources this class reads.</summary>
	public static IReadOnlyList<OutsideSourceId> Ids { get; } = [OutsideSourceId.Trakt, OutsideSourceId.TmdbTrending];

	public OutsideHub Hub => _hub;

	private string? TraktClientIdValue { get; }

	/// <summary>Whether this build carries a Trakt Client ID (a release does; a dev build or a fork's may not).</summary>
	public bool TraktAvailable => TraktClientIdValue is not null;

	/// <summary>Whether TMDb trending has a token to send: the user's own, else Nixie's (a release's, unless TMDb refused it).</summary>
	public bool HasTmdbToken => HasUserTmdbToken || (HasAppTmdbToken && !AppTmdbTokenRefused);

	/// <summary>
	/// TMDb refused Nixie's own key (401 or 403): it isn't sent again while Nixie runs (its rotation isn't self-service: the
	/// terms re-check of 2026-11-22), so nothing retries in a loop; the user can enter their own token.
	/// </summary>
	public bool AppTmdbTokenRefused => _appTokenRefused;

	/// <summary>Whether the user gave a TMDb token this Windows user can read (it wins over Nixie's).</summary>
	public bool HasUserTmdbToken => _token?.HasToken == true;

	/// <summary>Whether this build carries Nixie's own TMDb token (a release does; a dev build or a fork's may not).</summary>
	public bool HasAppTmdbToken => _appToken is not null;

	// the token TMDb's requests carry: the user's own, else Nixie's (unless refused); only ever in the Authorization header
	private string? TmdbTokenToSend() => _token?.Read() ?? (_appTokenRefused ? null : _appToken);

	/// <summary>Whether <paramref name="id"/> asks and shows: on in Settings, and able to (Trakt: a Client ID; TMDb: a token).</summary>
	public bool IsActive(OutsideSourceId id) => id switch
	{
		OutsideSourceId.Trakt => _hub.IsOn(id) && TraktAvailable,
		OutsideSourceId.TmdbTrending => _hub.IsOn(id) && HasTmdbToken,
		_ => false,
	};

	/// <summary>Whether any of the two is active.</summary>
	public bool AnyActive => Ids.Any(IsActive);

	/// <summary>The lists, a switch or the token changed (raised off the UI thread).</summary>
	public event Action? Changed;

	/// <summary>
	/// Nixie's words for the last thing that kept a source from giving its lists (a refusal in the source's own terms, an answer
	/// that wasn't a list); null once a list came in, or when nothing went wrong.
	/// </summary>
	public string? Problem(OutsideSourceId id)
	{
		lock (_lock)
		{
			return _problems.GetValueOrDefault(id);
		}
	}

	private sealed record Spec(OutsideSourceId Source, TrendingList List, ListedKind Kind, string CacheName, TraktLists.Endpoint? Trakt)
	{
		public string Key => $"{Source}/{CacheName}";
	}

	private static readonly IReadOnlyList<Spec> TraktSpecs =
		[.. TraktLists.All.Select(e => new Spec(OutsideSourceId.Trakt, e.List, e.Kind, e.Name.Replace('/', '-'), e))];

	private static readonly IReadOnlyList<Spec> TmdbSpecs =
		[.. TmdbLists.Kinds.Select(k => new Spec(OutsideSourceId.TmdbTrending, TrendingList.Trending, k, k == ListedKind.Movie ? "trending-movie-week" : "trending-tv-week", null))];

	private static IReadOnlyList<Spec> SpecsOf(OutsideSourceId id) => id == OutsideSourceId.Trakt ? TraktSpecs : TmdbSpecs;

	/// <summary>
	/// The lists to show now, of the active sources: from memory, else from the cache (which never gives one past its source's
	/// maximum age). No request is made here.
	/// </summary>
	public TrendingSnapshot Current()
	{
		var lists = new List<TrendingListRead>();
		int version;
		lock (_lock)
		{
			version = _version;
		}

		foreach (var id in Ids.Where(IsActive))
		{
			var info = OutsideSources.Get(id);
			foreach (var spec in SpecsOf(id))
			{
				if (Held(spec, info) is { } read && read.Titles.Count > 0)
				{
					lists.Add(read);
				}
			}
		}

		return new TrendingSnapshot(lists, version);
	}

	// the list in memory while it is younger than its source's maximum age, else the cache's (read into memory)
	private TrendingListRead? Held(Spec spec, OutsideSourceInfo info)
	{
		lock (_lock)
		{
			if (_held.TryGetValue(spec.Key, out var held))
			{
				if (_clock() - held.FetchedAt <= info.MaxAge)
				{
					return held;
				}

				_held.Remove(spec.Key);
			}
		}

		if (_hub.Cache.Read(info, spec.CacheName) is not { } entry)
		{
			return null;
		}

		try
		{
			var titles = JsonSerializer.Deserialize<List<ListedTitle>>(entry.Data, Json) ?? [];
			var read = new TrendingListRead(spec.Source, spec.List, spec.Kind, entry.FetchedAt, titles);
			lock (_lock)
			{
				_held[spec.Key] = read;
			}

			return read;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	/// <summary>
	/// Asks again, one request at a time, every list of the active sources that isn't fresh any more (Trakt: 6 hours, the box
	/// office at its Monday update; TMDb: a day). A list that fails keeps the one Nixie has; a source that pauses (a 429) or
	/// is refused stops for this round. <see cref="Changed"/> is raised when a list came in. One round runs at a time: a second
	/// call while one runs gets that one.
	/// </summary>
	public Task RefreshAsync(CancellationToken cancellation = default)
	{
		lock (_lock)
		{
			if (_refreshing is { IsCompleted: false } running)
			{
				return running;
			}

			return _refreshing = Task.Run(() => Refresh(cancellation), CancellationToken.None);
		}
	}

	private async Task Refresh(CancellationToken cancellation)
	{
		var changed = false;
		foreach (var id in Ids)
		{
			if (!IsActive(id))
			{
				continue;
			}

			var info = OutsideSources.Get(id);
			foreach (var spec in SpecsOf(id))
			{
				cancellation.ThrowIfCancellationRequested();
				if (!IsActive(id))
				{
					break; // turned off meanwhile
				}

				var held = Held(spec, info);
				if (held is not null && _clock() < FreshUntil(spec, held.FetchedAt))
				{
					continue;
				}

				var (titles, stop) = spec.Trakt is { } endpoint ? await ReadTrakt(endpoint, cancellation).ConfigureAwait(false) : await ReadTmdb(spec.Kind, cancellation).ConfigureAwait(false);
				if (titles is not null && IsActive(id))
				{
					var now = _clock();
					try
					{
						_hub.Cache.Write(info, spec.CacheName, JsonSerializer.SerializeToUtf8Bytes(titles, Json), now);
					}
					catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
					{
						// kept in memory for this run; the next start asks again
					}

					lock (_lock)
					{
						_held[spec.Key] = new TrendingListRead(spec.Source, spec.List, spec.Kind, now, titles);
						_problems.Remove(id);
					}

					changed = true;
				}

				if (stop)
				{
					break;
				}
			}
		}

		if (changed)
		{
			Bump();
		}
	}

	private static DateTimeOffset FreshUntil(Spec spec, DateTimeOffset fetchedAt) =>
		spec.Trakt is { } endpoint ? TraktLists.FreshUntil(endpoint, fetchedAt) : TmdbLists.FreshUntil(fetchedAt);

	// one Trakt list, paged as its headers say (TraktLists.ReadNext); null when a page failed (the list isn't written half);
	// stop: the source paused or refused, so its other lists aren't asked this round
	private async Task<(List<ListedTitle>? Titles, bool Stop)> ReadTrakt(TraktLists.Endpoint endpoint, CancellationToken cancellation)
	{
		var source = _hub[OutsideSourceId.Trakt];
		var headers = TraktLists.Headers(TraktClientIdValue!).ToDictionary(h => h.Key, h => h.Value);
		var titles = new List<ListedTitle>();
		for (var page = 1; ; page++)
		{
			// the switch is read before every request: a source turned off mid-list sends nothing more (the review, #2)
			if (!IsActive(OutsideSourceId.Trakt))
			{
				return (null, true);
			}

			var result = await source.GetAsync(new Uri(TraktLists.ApiBase + TraktLists.PathAndQuery(endpoint, page)), headers, cancellation).ConfigureAwait(false);
			if (!result.Ok)
			{
				return (null, Failed(OutsideSourceId.Trakt, result, TraktLists.Refusal));
			}

			TraktLists.Page parsed;
			try
			{
				parsed = TraktLists.ParsePage(result.Text, endpoint, titles.Count + 1);
			}
			catch (ListUnreadable unreadable)
			{
				SetProblem(OutsideSourceId.Trakt, unreadable.Message);
				return (null, false);
			}

			var added = TraktLists.Join(titles, parsed.Titles);
			var paging = TraktLists.ParsePaging(result.Headers.Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, [h.Value])));
			if (!TraktLists.ReadNext(endpoint, page, paging, parsed.Entries, added))
			{
				return (titles, false);
			}
		}
	}

	// TMDb's trending week of one kind: its first pages (TmdbLists.ReadNext)
	private async Task<(List<ListedTitle>? Titles, bool Stop)> ReadTmdb(ListedKind kind, CancellationToken cancellation)
	{
		if (TmdbTokenToSend() is not { } token)
		{
			return (null, true);
		}

		var source = _hub[OutsideSourceId.TmdbTrending];
		var usingAppToken = !HasUserTmdbToken;
		var headers = TmdbLists.Headers(token).ToDictionary(h => h.Key, h => h.Value);
		var titles = new List<ListedTitle>();
		for (var page = 1; ; page++)
		{
			// as Trakt's: turned off, or its token removed, mid-list: nothing more is sent
			if (!IsActive(OutsideSourceId.TmdbTrending))
			{
				return (null, true);
			}

			var result = await source.GetAsync(new Uri(TmdbLists.ApiBase + TmdbLists.PathAndQuery(kind, page)), headers, cancellation).ConfigureAwait(false);
			if (!result.Ok)
			{
				if (usingAppToken && result.Outcome == OutsideOutcome.Refused && result.Status is 401 or 403)
				{
					// Nixie's own key refused (revoked, or TMDb's mind changed): not sent again this run, said plainly
					_appTokenRefused = true;
					SetProblem(OutsideSourceId.TmdbTrending, TrendingStatus.AppKeyRefused);
					return (null, true);
				}

				return (null, Failed(OutsideSourceId.TmdbTrending, result, TmdbLists.Refusal));
			}

			TmdbLists.Page parsed;
			try
			{
				parsed = TmdbLists.ParsePage(result.Text, kind, titles.Count + 1);
			}
			catch (ListUnreadable unreadable)
			{
				SetProblem(OutsideSourceId.TmdbTrending, unreadable.Message);
				return (null, false);
			}

			var added = 0;
			var seen = titles.Select(t => t.Tmdb).ToHashSet(StringComparer.Ordinal);
			foreach (var title in parsed.Titles.Where(t => seen.Add(t.Tmdb)))
			{
				titles.Add(title with { Rank = titles.Count + 1 });
				added++;
			}

			if (added == 0 || !TmdbLists.ReadNext(page, parsed))
			{
				return (titles, false);
			}
		}
	}

	// a request that didn't give a page: the source's own words for a refusal (Trakt's lapsed registration, TMDb's token),
	// the layer's for the rest; true when the source shouldn't be asked again this round (401, 403, 429, a pause, a block)
	private bool Failed(OutsideSourceId id, OutsideResult result, Func<int, string> refusal)
	{
		var message = result.Outcome == OutsideOutcome.Refused && result.Status is { } status ? refusal(status) : result.Message;
		if (message is not null)
		{
			SetProblem(id, message);
		}

		// the round ends only when the source refused the key or the app (401, 403), asked to slow down (429, a pause), or the
		// address was blocked; any other refusal (a 404 for one list, a 502) is that list's alone, and the next list is asked
		// (the review, #4). A run of them pauses the source (OutsideSource), which ends the round as a pause.
		return result.Outcome is OutsideOutcome.Paused or OutsideOutcome.RateLimited or OutsideOutcome.Blocked
			|| (result.Outcome == OutsideOutcome.Refused && result.Status is 401 or 403)
			|| result.PausedUntil is not null;
	}

	private void SetProblem(OutsideSourceId id, string message)
	{
		lock (_lock)
		{
			_problems[id] = message;
		}
	}

	private void OnSwitchChanged(OutsideSourceId id)
	{
		if (!Ids.Contains(id))
		{
			return;
		}

		if (!_hub.IsOn(id))
		{
			Forget(id);
		}

		Bump();
		if (IsActive(id))
		{
			_ = RefreshAsync();
		}
	}

	private void OnTokenChanged()
	{
		lock (_lock)
		{
			_problems.Remove(OutsideSourceId.TmdbTrending);
		}

		Bump();
		if (IsActive(OutsideSourceId.TmdbTrending))
		{
			_ = RefreshAsync();
		}
	}

	/// <summary>A source turned off: its lists go, from memory and from the cache, and its last problem with them.</summary>
	public void Forget(OutsideSourceId id)
	{
		lock (_lock)
		{
			foreach (var key in _held.Keys.Where(k => k.StartsWith(id + "/", StringComparison.Ordinal)).ToList())
			{
				_held.Remove(key);
			}

			_problems.Remove(id);
		}

		try
		{
			_hub.Cache.Clear(id);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// the files age out on their own (the cache's purge at the next start)
		}
	}

	private void Bump()
	{
		lock (_lock)
		{
			_version++;
		}

		Changed?.Invoke();
	}
}

/// <summary>1.7 trending: a trending source's status line in Settings.</summary>
public static class TrendingStatus
{
	public const string Off = "Off. Nothing is sent.";
	/// <summary>TMDb trending in a build without Nixie's own key, before the user enters theirs.</summary>
	public const string TmdbNotAvailable = "Not available in this build. Enter your own TMDb token below to use it.";

	/// <summary>TMDb refused Nixie's own key (401 or 403): said plainly, and the key isn't tried again while Nixie runs.</summary>
	public const string AppKeyRefused = "TMDb refused Nixie's key. You can enter your own TMDb token below.";
	public const string UpToDate = "Lists up to date.";
	public const string Waiting = "On. Nixie fetches the lists the next time you open Home or Collections.";

	public static string Describe(TrendingSources sources, OutsideSourceId id, DateTimeOffset? now = null)
	{
		if (id == OutsideSourceId.Trakt && !sources.TraktAvailable)
		{
			return TraktClientId.NotAvailable + ".";
		}

		if (id == OutsideSourceId.TmdbTrending && !sources.HasTmdbToken)
		{
			return sources.AppTmdbTokenRefused ? AppKeyRefused : TmdbNotAvailable;
		}

		if (!sources.Hub.IsOn(id))
		{
			return Off;
		}

		// the last problem's own words first (a refusal in the source's terms, or its 429's pause); a pause without one
		// (a refusal run another page caused) in the layer's words
		if (sources.Problem(id) is { } problem)
		{
			return problem;
		}

		var source = sources.Hub[id];
		if (source.PausedUntil is { } paused)
		{
			return OutsideMessages.Paused(source.Info, paused, now ?? DateTimeOffset.UtcNow);
		}

		return source.Last is { Ok: true } || sources.Current().Lists.Any(x => x.Source == id) ? UpToDate : Waiting;
	}
}
