using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Settings;

namespace FluentFin.Core.Outside;

/// <summary>1.7: what a public list is about: an award's winners, or a finer genre.</summary>
public enum OutsideListKind
{
	Award,
	Genre,
}

/// <summary>1.7: one title of a public list: its ids, and its year where the list has one (an award's ceremony).</summary>
public sealed record OutsideListItem(OutsideRef Ref, int? Year = null);

/// <summary>
/// 1.7: a public list Nixie fetched (the same for everyone): Wikidata's Best Picture winners, its heist films. Matched to
/// the library on this PC (<see cref="OutsideMatcher"/>). <see cref="KeywordTag"/>: a genre list joins the keyword collection
/// of that TMDb keyword ("heist").
/// </summary>
public sealed record OutsideList(string Id, OutsideSourceId Source, OutsideListKind Kind, string Title, string Why, IReadOnlyList<OutsideListItem> Items)
{
	public string? KeywordTag { get; init; }
}

/// <summary>
/// 1.7: the public lists Nixie has, parsed, for the builders: TMDb's franchise and keyword names (when "Franchise and keyword
/// names from TMDb" is on) and Wikidata's award and genre lists (when "Awards and genres from Wikidata" is on). Empty for a
/// source that is off, so turning one off takes its names and lists away at once.
/// </summary>
public sealed record OutsideListsSnapshot
{
	public static OutsideListsSnapshot Empty { get; } = new();

	/// <summary>TMDb collection id ("1733") → its name ("The Mummy Collection").</summary>
	public IReadOnlyDictionary<string, string> FranchiseNames { get; init; } = new Dictionary<string, string>();

	/// <summary>Every TMDb keyword's name, lower case (each mapped to itself): it confirms the allowlist's keywords, adds none.</summary>
	public IReadOnlyDictionary<string, string> KeywordNames { get; init; } = new Dictionary<string, string>();

	/// <summary>Wikidata's lists (awards, finer genres).</summary>
	public IReadOnlyList<OutsideList> Lists { get; init; } = [];

	public bool IsEmpty => FranchiseNames.Count == 0 && KeywordNames.Count == 0 && Lists.Count == 0;

	/// <summary>
	/// The app's lists, which the collections are built from (<see cref="SmartCollections.SmartCollectionCatalog.BuildAll"/>).
	/// Set by
	/// <see cref="OutsideLists"/>; tests pass their own.
	/// </summary>
	public static OutsideListsSnapshot Shared
	{
		get => Volatile.Read(ref s_shared);
		set
		{
			Volatile.Write(ref s_shared, value ?? Empty);
			SharedChanged?.Invoke(value ?? Empty);
		}
	}

	private static OutsideListsSnapshot s_shared = Empty;

	/// <summary>Raised (on the thread that set it) when <see cref="Shared"/> changes: the Collections page and Home build again.</summary>
	public static event Action<OutsideListsSnapshot>? SharedChanged;
}

/// <summary>
/// 1.7: keeps <see cref="OutsideListsSnapshot.Shared"/> current: on start (from the cache, fetching what is stale), when a
/// source is turned on or off, and when the Collections page opens. One refresh at a time; each source's requests go one at a
/// time through its <see cref="OutsideSource"/>. What is fetched depends on the switches alone, never on the library (plan §3).
/// </summary>
public sealed class OutsideLists
{
	private readonly OutsideHub _hub;
	private readonly ILocalSettingsService? _settings;
	private readonly Func<DateTimeOffset> _clock;
	private readonly SemaphoreSlim _one = new(1, 1);
	private readonly bool _publish;
	private OutsideListsSnapshot _current = OutsideListsSnapshot.Empty;

	/// <param name="publish">The app's instance sets <see cref="OutsideListsSnapshot.Shared"/>; a test's keeps its lists to itself.</param>
	public OutsideLists(OutsideHub hub, ILocalSettingsService? settings, Func<DateTimeOffset>? clock = null, bool publish = false)
	{
		_hub = hub;
		_settings = settings;
		_clock = clock ?? (() => DateTimeOffset.UtcNow);
		_publish = publish;
		hub.SwitchChanged += _ => RefreshInBackground();
	}

	/// <summary>The app's instance (set at start): the Collections page asks it for a refresh when it opens.</summary>
	public static OutsideLists? App { get; set; }

	public OutsideListsSnapshot Current => _current;

	/// <summary>Wikidata's endpoint as Settings has it (<see cref="WikidataLists.EndpointFor"/>).</summary>
	public Uri WikidataEndpoint => WikidataEndpointFor(_settings);

	/// <summary>The endpoint <paramref name="settings"/> name; today's service when they name none, or a custom one that isn't valid.</summary>
	public static Uri WikidataEndpointFor(ILocalSettingsService? settings) => WikidataLists.EndpointFor(
		settings?.ReadSetting(SettingKeys.WikidataEndpoint) is { } choice && Enum.TryParse<Outside.WikidataEndpoint>(choice, out var e) ? e : Outside.WikidataEndpoint.Wikidata,
		settings?.ReadSetting(SettingKeys.WikidataCustomEndpoint));

	/// <summary>The lists changed (a source turned on or off, a fresh list arrived).</summary>
	public event Action<OutsideListsSnapshot>? Changed;

	/// <summary>
	/// <see cref="RefreshAsync"/> off the calling thread (a switch flips on the UI thread; reading a cached keyword list takes a
	/// moment), its failure noted rather than thrown. The task is the refresh's (tests wait for it).
	/// </summary>
	public Task RefreshInBackground() => Background = Task.Run(async () =>
	{
		try
		{
			await RefreshAsync().ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			Failed?.Invoke(ex);
		}
	});

	/// <summary>The last background refresh (tests wait for the one a page asked for).</summary>
	internal Task Background { get; private set; } = Task.CompletedTask;

	/// <summary>A background refresh failed (a cache that couldn't be written): the app logs it.</summary>
	public event Action<Exception>? Failed;

	/// <summary>The refresh in progress, or a new one: each enabled source from its cache, fetching what is stale.</summary>
	public async Task<OutsideListsSnapshot> RefreshAsync(CancellationToken cancellation = default)
	{
		await _one.WaitAsync(cancellation).ConfigureAwait(false);
		try
		{
			// each source on its own (review #3): one that fails keeps what it had, counts the failure, and the other still runs
			var next = OutsideListsSnapshot.Empty;
			// BH-170-M1: each source's switch is read before each of its requests, not once a round (PRIVACY.md: turned off,
			// "Nixie stops asking it at once")
			bool TmdbOn() => _hub.IsOn(OutsideSourceId.TmdbExports);
			bool WikidataOn() => _hub.IsOn(OutsideSourceId.Wikidata);
			if (TmdbOn())
			{
				try
				{
					var (franchises, _) = await TmdbExports.LoadAsync(_hub, TmdbExports.Collections, _clock, cancellation, TmdbOn).ConfigureAwait(false);
					var (keywords, _) = await TmdbExports.LoadAsync(_hub, TmdbExports.Keywords, _clock, cancellation, TmdbOn).ConfigureAwait(false);
					next = next with
					{
						FranchiseNames = franchises?.ToDictionary(x => x.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), x => x.Value) ?? new Dictionary<string, string>(),
						KeywordNames = KeywordSet(keywords?.Values),
					};
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					_hub[OutsideSourceId.TmdbExports].LoadFailed();
					Failed?.Invoke(ex);
					next = next with { FranchiseNames = _current.FranchiseNames, KeywordNames = _current.KeywordNames };
				}
			}

			if (WikidataOn())
			{
				try
				{
					var (lists, _) = await WikidataLists.LoadAsync(_hub, WikidataEndpoint, cancellation, WikidataOn).ConfigureAwait(false);
					next = next with { Lists = lists };
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					_hub[OutsideSourceId.Wikidata].LoadFailed();
					Failed?.Invoke(ex);
					next = next with { Lists = _current.Lists };
				}
			}

			// BH-170-M1: a source turned off during the round takes its names and lists away now, not at the next round
			if (!TmdbOn())
			{
				next = next with { FranchiseNames = new Dictionary<string, string>(), KeywordNames = new Dictionary<string, string>() };
			}

			if (!WikidataOn())
			{
				next = next with { Lists = [] };
			}

			if (!Same(next, _current))
			{
				_current = next;
				if (_publish)
				{
					OutsideListsSnapshot.Shared = next;
				}

				Changed?.Invoke(next);
			}

			return _current;
		}
		finally
		{
			_one.Release();
		}
	}

	/// <summary>The keyword names, lower case, each mapped to itself (one string the index entries share).</summary>
	public static Dictionary<string, string> KeywordSet(IEnumerable<string>? names)
	{
		var set = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var name in names ?? [])
		{
			var key = name.Trim().ToLowerInvariant();
			if (key.Length > 0)
			{
				set.TryAdd(key, key);
			}
		}

		return set;
	}

	private static bool Same(OutsideListsSnapshot a, OutsideListsSnapshot b) =>
		a.FranchiseNames.Count == b.FranchiseNames.Count && a.KeywordNames.Count == b.KeywordNames.Count
		&& a.Lists.Count == b.Lists.Count
		&& a.Lists.Zip(b.Lists).All(x => x.First.Id == x.Second.Id && x.First.Items.Count == x.Second.Items.Count)
		&& a.FranchiseNames.All(x => b.FranchiseNames.TryGetValue(x.Key, out var v) && v == x.Value);
}
