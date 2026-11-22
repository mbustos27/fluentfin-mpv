using FluentFin.Core.SmartCollections;

namespace FluentFin.Core.Outside;

/// <summary>
/// 1.7 trending: Nixie's collections from the public lists, matched against the library index in memory, on this PC
/// (<see cref="OutsideMatcher"/>: TMDb ids of the same kind, IMDb, TheTVDB). Pure: nothing is fetched or sent here.
/// <list type="bullet">
/// <item>The Collections page's rows: Trending (Trakt and TMDb merged), Popular, Most anticipated (films and shows
/// each) and Box office this week (films), each collection in the list's order, empty ones left out.</item>
/// <item>Home's "Trending in your library": films and shows of both sources' trending lists, merged by rank.</item>
/// </list>
/// Trakt's trending is what is being watched right now and TMDb's is the week's, so no title says "this week"; the why
/// line says each source's window (<see cref="Windows"/>). Trakt's API Use Policy (2026-10-20): "Label discovery lists
/// accurately so users understand what they represent."
/// </summary>
public static class TrendingBuilder
{
	/// <summary>Home's row: its collection's id.</summary>
	public const string HomeRowId = "trending:week";

	/// <summary>Home's row: its title.</summary>
	public const string HomeRowTitle = "Trending in your library";

	/// <summary>
	/// The trending why line: each source's window, for the sources given (those whose lists are in), Trakt first:
	/// "Trakt: watched right now · TMDb: this week", "Trakt: watched right now" or "TMDb: this week".
	/// </summary>
	public static string Windows(IEnumerable<OutsideSourceId> sources) =>
		string.Join(" · ", sources.Distinct().Order().Select(x => x == OutsideSourceId.Trakt ? "Trakt: watched right now" : "TMDb: this week"));

	/// <summary>The kinds in the order the Collections page lists their rows, after the kinds Nixie makes from the library.</summary>
	public static IReadOnlyList<SmartCollectionKind> PageOrder { get; } =
		[SmartCollectionKind.Trending, SmartCollectionKind.Popular, SmartCollectionKind.Anticipated, SmartCollectionKind.BoxOffice];

	/// <summary>"Trakt", "TMDb" or "Trakt and TMDb": the source credit a why line carries.</summary>
	public static string SourceNames(IEnumerable<OutsideSourceId> sources)
	{
		var names = sources.Distinct().Order().Select(x => x == OutsideSourceId.Trakt ? "Trakt" : "TMDb").ToList();
		return names.Count switch { 0 => "", 1 => names[0], _ => string.Join(" and ", names) };
	}

	private static IndexKind KindOf(ListedKind kind) => kind == ListedKind.Movie ? IndexKind.Movie : IndexKind.Series;

	private static OutsideRef Ref(ListedTitle t) => new(t.Tmdb, t.Imdb, t.Tvdb, KindOf(t.Kind));

	/// <summary>
	/// The library's titles on <paramref name="lists"/>, best place first: a title's place is its best rank on any of them, and
	/// a tie goes to the list given first (Trakt before TMDb, films before shows). Each title once.
	/// </summary>
	public static IReadOnlyList<IndexEntry> Merge(OutsideMatcher matcher, IReadOnlyList<IReadOnlyList<ListedTitle>> lists)
	{
		var found = new List<(int Rank, int List, int Order, IndexEntry Entry)>();
		for (var i = 0; i < lists.Count; i++)
		{
			var order = 0;
			foreach (var title in lists[i])
			{
				if (matcher.Match(Ref(title)) is { } entry)
				{
					found.Add((title.Rank, i, order++, entry));
				}
			}
		}

		var seen = new HashSet<Guid>();
		return [.. found.OrderBy(x => x.Rank).ThenBy(x => x.List).ThenBy(x => x.Order).Select(x => x.Entry).Where(x => seen.Add(x.Id))];
	}

	/// <summary>Every collection of the Collections page's trending rows, kind by kind in <see cref="PageOrder"/>; none empty.</summary>
	public static IReadOnlyList<SmartCollection> Build(IndexSnapshot index, TrendingSnapshot lists) => Build(new OutsideMatcher(index), lists);

	internal static IReadOnlyList<SmartCollection> Build(OutsideMatcher matcher, TrendingSnapshot lists)
	{
		var made = new List<SmartCollection>();
		foreach (var list in PageOrder.Select(ListOf))
		{
			foreach (var kind in new[] { ListedKind.Movie, ListedKind.Series })
			{
				var reads = lists.Lists.Where(x => x.List == list && x.Kind == kind).OrderBy(x => x.Source).ToList();
				if (reads.Count == 0)
				{
					continue;
				}

				var entries = Merge(matcher, [.. reads.Select(x => x.Titles)]);
				if (entries.Count > 0)
				{
					made.Add(Collection(list, kind, reads.Select(x => x.Source), entries));
				}
			}
		}

		return made;
	}

	/// <summary>Home's row: both sources' trending films and shows merged by rank; null when no list is on or none of its titles is in the library.</summary>
	public static SmartCollection? HomeRow(IndexSnapshot index, TrendingSnapshot lists) => HomeRow(new OutsideMatcher(index), lists);

	internal static SmartCollection? HomeRow(OutsideMatcher matcher, TrendingSnapshot lists)
	{
		var reads = lists.Lists.Where(x => x.List == TrendingList.Trending).OrderBy(x => x.Source).ThenBy(x => x.Kind).ToList();
		var entries = reads.Count == 0 ? [] : Merge(matcher, [.. reads.Select(x => x.Titles)]);
		if (entries.Count == 0)
		{
			return null;
		}

		return new SmartCollection(HomeRowId, SmartCollectionKind.Trending, HomeRowTitle, Windows(reads.Select(x => x.Source)), HasOwnOrder: true,
			[.. entries.Select(x => x.Id)])
		{
			Cover = [.. entries.Take(4)],
			InListOrder = true,
			Only = entries.All(x => x.Kind == IndexKind.Movie) ? IndexKind.Movie : entries.All(x => x.Kind == IndexKind.Series) ? IndexKind.Series : null,
		};
	}

	private static TrendingList ListOf(SmartCollectionKind kind) => kind switch
	{
		SmartCollectionKind.Popular => TrendingList.Popular,
		SmartCollectionKind.Anticipated => TrendingList.Anticipated,
		SmartCollectionKind.BoxOffice => TrendingList.BoxOffice,
		_ => TrendingList.Trending,
	};

	private static SmartCollectionKind KindOf(TrendingList list) => list switch
	{
		TrendingList.Popular => SmartCollectionKind.Popular,
		TrendingList.Anticipated => SmartCollectionKind.Anticipated,
		TrendingList.BoxOffice => SmartCollectionKind.BoxOffice,
		_ => SmartCollectionKind.Trending,
	};

	private static SmartCollection Collection(TrendingList list, ListedKind kind, IEnumerable<OutsideSourceId> from, IReadOnlyList<IndexEntry> entries)
	{
		var films = kind == ListedKind.Movie;
		var sources = SourceNames(from);
		var (id, title, why) = list switch
		{
			TrendingList.Popular => ("popular", films ? "Popular films" : "Popular shows", $"Popular on {sources}"),
			TrendingList.Anticipated => ("anticipated", films ? "Most anticipated films" : "Most anticipated shows", $"Most anticipated on {sources}"),
			TrendingList.BoxOffice => ("boxoffice", "Box office this week", $"The US box office this weekend, from {sources}"),
			_ => ("trending", films ? "Trending films" : "Trending shows", Windows(from)),
		};
		return new SmartCollection($"{id}:{(films ? "movie" : "series")}", KindOf(list), title, why, HasOwnOrder: true, [.. entries.Select(x => x.Id)])
		{
			Cover = [.. entries.Take(4)],
			Only = films ? IndexKind.Movie : IndexKind.Series,
			InListOrder = true,
		};
	}
}

/// <summary>
/// 1.7 trending: the app's trending collections. What matched is kept per server and user (it is made of that user's index,
/// <see cref="LibraryIndex"/>, which is per server and user) and forgotten when the user signs out or the saved login is
/// removed (<see cref="LibraryIndex.Forgotten"/>). The public lists themselves are <see cref="TrendingSources"/>'.
/// </summary>
public sealed class TrendingCollections
{
	private readonly Lock _lock = new();
	private (IndexSnapshot Index, string Lists, IReadOnlyList<SmartCollection> Made, SmartCollection? Home)? _memo;
	// BH-170-q4: bumped by Forget; a match that began before a sign-out isn't kept after it (as TopNavigation's trail session)
	private int _session;

	/// <param name="homeCache">
	/// Home's saved page: marked stale when the lists change, so that the next visit to Home asks again and shows the row
	/// (Home skips its refresh while its saved page is fresh, and the lists often come in while another page shows).
	/// </param>
	public TrendingCollections(TrendingSources sources, LibraryIndex? index = null, Services.HomeCache? homeCache = null)
	{
		Sources = sources;
		if (index is not null)
		{
			index.Forgotten += (_, _) => Forget();
		}

		sources.Changed += () =>
		{
			homeCache?.MarkStale();
			Changed?.Invoke();
		};
	}

	public TrendingSources Sources { get; }

	/// <summary>The lists, a switch or the token changed: the pages show their rows again (raised off the UI thread).</summary>
	public event Action? Changed;

	/// <summary>The Collections page's trending collections for this index (none while no source is active).</summary>
	public IReadOnlyList<SmartCollection> For(IndexSnapshot index) => Matched(index).Made;

	/// <summary>Home's "Trending in your library", or null (no source active, or none of its titles in the library).</summary>
	public SmartCollection? HomeRowFor(IndexSnapshot index) => Matched(index).Home;

	/// <summary>Asks again the lists that aren't fresh (in the background); <see cref="Changed"/> when they came in.</summary>
	public Task RefreshAsync() => Sources.AnyActive ? Sources.RefreshAsync() : Task.CompletedTask;

	/// <summary>Raised as the lists are matched anew (not on a kept match): tests see which thread matched (never Home's own).</summary>
	internal event Action? Matching;

	/// <summary>Whether matches are held (tests: forgotten on sign-out).</summary>
	public bool HoldsMatches
	{
		get
		{
			lock (_lock)
			{
				return _memo is not null;
			}
		}
	}

	/// <summary>Sign-out, a removed login: what matched goes.</summary>
	public void Forget()
	{
		lock (_lock)
		{
			_memo = null;
			_session++;
		}
	}

	private (IReadOnlyList<SmartCollection> Made, SmartCollection? Home) Matched(IndexSnapshot index)
	{
		var lists = Sources.Current();
		// the matches are kept for these very lists: a list that aged out (past its source's maximum age, while the source
		// keeps refusing) changes the key and takes its matches with it (the review, #3); a version alone wouldn't move
		var key = Key(lists);
		int session;
		lock (_lock)
		{
			session = _session;
			if (_memo is { } memo && ReferenceEquals(memo.Index, index) && memo.Lists == key)
			{
				return (memo.Made, memo.Home);
			}

			if (lists.Lists.Count == 0)
			{
				_memo = null;
				return ([], null);
			}
		}

		Matching?.Invoke();
		var matcher = new OutsideMatcher(index);
		var made = TrendingBuilder.Build(matcher, lists);
		var home = TrendingBuilder.HomeRow(matcher, lists);
		lock (_lock)
		{
			// BH-170-q4: signed out while this matched: the caller gets its answer, but nothing of it is kept
			if (session == _session)
			{
				_memo = (index, key, made, home);
			}
		}

		return (made, home);
	}

	// which lists, and which read of each: their sources, lists, kinds and fetch times, and the snapshot's version
	private static string Key(TrendingSnapshot lists) =>
		lists.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
		+ string.Join(";", lists.Lists.Select(x => $"{x.Source}/{x.List}/{x.Kind}@{x.FetchedAt.UtcTicks}"));
}

/// <summary>1.7 trending: the app's trending collections, for the pages that aren't made by the container (Home, Collections).</summary>
public static class TrendingServices
{
	public static TrendingCollections? Collections { get; set; }
}
