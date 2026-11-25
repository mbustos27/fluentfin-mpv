using System.Globalization;

namespace FluentFin.Core.SmartCollections;

/// <summary>Nixie 1.6: the kinds of collection Nixie makes from the server's own data (the smart collections plan, 1.3).</summary>
public enum SmartCollectionKind
{
	Franchise,
	Decade,
	Director,
	Actor,
	Studio,
	Network,
	GenreMix,
	Keyword,
	HighlyRated,
	ShortFilms,
	UnfinishedSeries,
	Country,
	Seasonal,
	Saved,

	// 1.7 keyless
	/// <summary>1.7: an award's winners or nominees, from Wikidata (Settings &gt; Look up lists online).</summary>
	Award,
	// end 1.7 keyless

	// 1.7 trending: the public lists' kinds (Core\Outside\TrendingBuilder), numbered apart so that a Home page saved with one
	// keeps its kind whatever other kinds join the list
	Trending = 100,
	Popular = 101,
	Anticipated = 102,
	BoxOffice = 103,
	// end 1.7 trending

	// 1.8 (P1-2, P1-3; stream t's shared model): the anchored slots and the weekly Spotlight, numbered apart as the trending
	// kinds are (Home's saved page keeps a kind by its number)
	/// <summary>1.8: "Next in Mission: Impossible": the next film of a franchise the user has started (stream a).</summary>
	NextIn = 200,

	/// <summary>1.8: "More like Heat": titles like one the user finished (stream a; the report's "Because you watched").</summary>
	MoreLike = 201,

	/// <summary>1.8: "More from Denis Villeneuve": a person of a title the user finished (stream a).</summary>
	MoreFrom = 202,

	/// <summary>1.8: "Spotlight: Jean-Pierre Melville": the week's person or studio (stream s).</summary>
	Spotlight = 203,
	// end 1.8
}

/// <summary>
/// Nixie 1.6: a collection Nixie made: what it is (<see cref="Kind"/>), its name, the "why" line its page shows under the
/// summary, whether its order means something (a franchise's release order: position chips), and its titles in that
/// order. Lives on this PC; nothing is written to the server (but "Save as playlist", the user's own action).
/// </summary>
/// <param name="Id">Stable across refreshes: "franchise:2344", "decade:movie:1980", "keyword:heist".</param>
public sealed record SmartCollection(string Id, SmartCollectionKind Kind, string Title, string Why, bool HasOwnOrder, IReadOnlyList<Guid> ItemIds)
{
	/// <summary>A franchise's TMDb collection id (an admin's box set with the same TMDb id is the same franchise).</summary>
	public string? TmdbCollection { get; init; }

	/// <summary>A saved filter's library and filter.</summary>
	public SavedFilter? Filter { get; init; }

	/// <summary>The first titles, for the 2×2 mosaic a Nixie collection shows instead of a poster (at most four).</summary>
	public IReadOnlyList<IndexEntry> Cover { get; init; } = [];

	/// <summary>Over the title: "Made by Nixie · Franchise".</summary>
	public string Eyebrow => $"Made by Nixie · {SmartCollectionKinds.Singular(Kind)}";

	/// <summary>Films only, shows only, or null: both.</summary>
	public IndexKind? Only { get; init; }

	// 1.7 trending
	/// <summary>
	/// The page keeps <see cref="ItemIds"/>' order as the collection's own (a list's rank: trending, the box office), where
	/// <see cref="HasOwnOrder"/> alone means release order (a franchise).
	/// </summary>
	public bool InListOrder { get; init; }

	/// <summary>
	/// 1.8 (BH-180-m1): a pin Nixie no longer makes (last week's Spotlight, a pick from what the user watched that moved on): Saved
	/// keeps it by its title, said to have ended (no titles), until the user unpins it.
	/// </summary>
	public bool Ended { get; init; }
	// end 1.7 trending

	/// <summary>"12 films", "3 shows", "8 titles".</summary>
	public string CountText => SmartCollectionKinds.CountText(ItemIds.Count, Only);

	// 1.8 (P1-4, stream t): the reason line and the award badges every Made by Nixie tile shows
	/// <summary>
	/// 1.8: the tile's and the Home row's reason line: one or two facts, then the count (and an end date): "TMDb keyword
	/// “heist” · 14 titles, 12 unwatched". Builders set it through <c>Make.Of</c>. Null in a collection saved before 1.8
	/// (Home's saved page) and in a saved filter: <see cref="ReasonText"/> says the count alone.
	/// </summary>
	public ReasonLine? Reason { get; init; }

	/// <summary>1.8: the tile's award badges ("3 Best Picture winners"), at most <see cref="AwardFacts.MostBadges"/>; none without Wikidata's lists.</summary>
	public IReadOnlyList<CollectionBadge> Badges { get; init; } = [];

	/// <summary>1.8: the reason line as the tile shows it; the count alone (<see cref="CountText"/>) where there is none.</summary>
	public string ReasonText => Reason?.Text ?? CountText;

	/// <summary>1.8: the badges' words on one line, for Narrator ("" with none).</summary>
	public string BadgesText => string.Join(", ", Badges.Select(x => x.Text));

	/// <summary>
	/// 1.8: a tile's Narrator name, what the tile shows in its order: "Heists, made by Nixie, keyword, TMDb keyword “heist”, 12
	/// titles, 10 unwatched, 2 Best Picture winners". <see cref="AutomationName"/> stays the short one (a library's preset chip
	/// shows the name alone).
	/// </summary>
	public string TileAutomationName =>
		$"{Title}, made by Nixie, {SmartCollectionKinds.Singular(Kind).ToLowerInvariant()}, {Reason?.Spoken ?? CountText}{(Badges.Count > 0 ? ", " + BadgesText : "")}";
	// end 1.8

	/// <summary>Narrator: "Heists, made by Nixie, keyword, 12 films".</summary>
	public string AutomationName => $"{Title}, made by Nixie, {SmartCollectionKinds.Singular(Kind).ToLowerInvariant()}, {CountText}";
}

/// <summary>The words for each kind.</summary>
public static class SmartCollectionKinds
{
	/// <summary>
	/// The kinds in the order the Collections page lists them (each a row of its own), and the kinds Home's collection a day
	/// and Settings' switches draw from.
	/// </summary>
	/// 1.7.3 (P0-3, the owner: "too many, too generic"): franchises, keywords and awards only. The seven single-fact kinds
	/// (<see cref="SmartCollectionCatalog.OnDemandOnly"/>) are no longer published.
	public static IReadOnlyList<SmartCollectionKind> PageOrder { get; } =
	[
		SmartCollectionKind.Franchise, SmartCollectionKind.Keyword,
		SmartCollectionKind.Award, // 1.7 keyless: the last of the page's kinds, as the plan's 4.1 orders them
	];

	/// <summary>The kinds that make one collection each, together in the page's "Picks" row.</summary>
	public static IReadOnlyList<SmartCollectionKind> Picks { get; } =
	[
		SmartCollectionKind.HighlyRated, SmartCollectionKind.UnfinishedSeries, SmartCollectionKind.Seasonal, SmartCollectionKind.ShortFilms,
	];

	/// <summary>1.8 (BH-180-M2): the kinds made of what the user watched (stream a), and the week's Spotlight (stream s).</summary>
	public static IReadOnlyList<SmartCollectionKind> FromWhatYouWatched { get; } =
	[
		SmartCollectionKind.NextIn, SmartCollectionKind.MoreLike, SmartCollectionKind.MoreFrom, SmartCollectionKind.Spotlight,
	];

	/// <summary>
	/// 1.8 (BH-180-M2): every kind Settings > Collections has a switch for: the page's kinds (awards follow Wikidata's switch),
	/// the picks, and 1.8's. Every one of them off is Made by Nixie off.
	/// </summary>
	public static IReadOnlyList<SmartCollectionKind> Switched { get; } =
		[.. PageOrder.Where(x => x != SmartCollectionKind.Award), .. Picks, .. FromWhatYouWatched];

	/// <summary>The eyebrow's word: "Franchise".</summary>
	public static string Singular(SmartCollectionKind kind) => kind switch
	{
		SmartCollectionKind.Franchise => "Franchise",
		SmartCollectionKind.Decade => "Decade",
		SmartCollectionKind.Director => "Director",
		SmartCollectionKind.Actor => "Actor",
		SmartCollectionKind.Studio => "Studio",
		SmartCollectionKind.Network => "Studio or network",
		SmartCollectionKind.GenreMix => "Genre mix",
		SmartCollectionKind.Keyword => "Keyword",
		SmartCollectionKind.HighlyRated => "Acclaimed", // 1.7.3 (P0-4): was "Highly rated"
		SmartCollectionKind.ShortFilms => "Under 90 minutes", // 1.7.3 (P0-4): was "Short films"
		SmartCollectionKind.UnfinishedSeries => "Unfinished series",
		SmartCollectionKind.Country => "Country",
		SmartCollectionKind.Seasonal => "In season",
		SmartCollectionKind.Saved => "Saved filter",
		SmartCollectionKind.Award => "Award", // 1.7 keyless
		// 1.7 trending
		SmartCollectionKind.Trending => "Trending",
		SmartCollectionKind.Popular => "Popular",
		SmartCollectionKind.Anticipated => "Anticipated",
		SmartCollectionKind.BoxOffice => "Box office",
		// end 1.7 trending
		// 1.8 (stream t's shared model)
		SmartCollectionKind.NextIn => "Next in a franchise",
		SmartCollectionKind.MoreLike => "More like a title",
		SmartCollectionKind.MoreFrom => "More from a person",
		SmartCollectionKind.Spotlight => "Spotlight",
		// end 1.8
		_ => "Collection",
	};

	/// <summary>A row's title on the Collections page and a Settings switch: "Franchises".</summary>
	public static string Plural(SmartCollectionKind kind) => kind switch
	{
		SmartCollectionKind.Franchise => "Franchises",
		SmartCollectionKind.Decade => "Decades",
		SmartCollectionKind.Director => "Directors",
		SmartCollectionKind.Actor => "Actors",
		SmartCollectionKind.Studio => "Studios",
		SmartCollectionKind.Network => "Studios and networks",
		SmartCollectionKind.GenreMix => "Genre mixes",
		SmartCollectionKind.Keyword => "Keywords",
		SmartCollectionKind.HighlyRated => "Acclaimed, not seen yet", // 1.7.3 (P0-4): was "Highly rated, not watched"
		SmartCollectionKind.ShortFilms => "Under 90 minutes", // 1.7.3 (P0-4): was "Short films"
		SmartCollectionKind.UnfinishedSeries => "Unfinished series",
		SmartCollectionKind.Country => "Countries",
		SmartCollectionKind.Seasonal => "In season",
		SmartCollectionKind.Saved => "Saved",
		SmartCollectionKind.Award => "Awards", // 1.7 keyless
		// 1.7 trending
		SmartCollectionKind.Trending => "Trending", // not "this week": Trakt's is right now, TMDb's the week (TrendingBuilder.Windows)
		SmartCollectionKind.Popular => "Popular",
		SmartCollectionKind.Anticipated => "Most anticipated",
		SmartCollectionKind.BoxOffice => "Box office this week",
		// end 1.7 trending
		// 1.8 (stream t's shared model)
		SmartCollectionKind.NextIn => "Next in a franchise",
		SmartCollectionKind.MoreLike => "More like",
		SmartCollectionKind.MoreFrom => "More from",
		SmartCollectionKind.Spotlight => "Spotlight",
		// end 1.8
		_ => "Collections",
	};

	/// <summary>"12 films", "1 show", "8 titles" (films and shows mixed).</summary>
	public static string CountText(int count, IndexKind? only)
	{
		var (one, many) = only switch { IndexKind.Movie => ("film", "films"), IndexKind.Series => ("show", "shows"), _ => ("title", "titles") };
		return $"{count.ToString("N0", CultureInfo.CurrentCulture)} {(count == 1 ? one : many)}";
	}
}

/// <summary>
/// Nixie 1.6 (plan 4.4): a library's applied filters saved under a name ("Save as collection"): opened, it shows the
/// library's titles that pass them, the way the library page filters (values of one kind OR, kinds AND).
/// </summary>
public sealed record SavedFilter(
	Guid LibraryId,
	string LibraryName,
	bool Unwatched = false,
	bool Favorites = false,
	IReadOnlyList<string>? Genres = null,
	IReadOnlyList<string>? Tags = null,
	IReadOnlyList<string>? OfficialRatings = null,
	IReadOnlyList<string>? Years = null,
	string? PresetId = null,
	string? PresetLabel = null,
	int Revision = 0)
{
	/// <summary>
	/// 1.7.3 fix round (BH-173-M2): <see cref="Revision"/> of a filter saved now (the store sets it). 0, a filter saved before
	/// 1.7.3: its "Highly rated" or "Short films" preset keeps 1.7.2's meaning (<see cref="SavedFilterPresets"/>).
	/// </summary>
	public const int CurrentRevision = 3;

	/// <summary>
	/// The fix round's re-review: a new saved filter's id, its revision in it ("saved:r3-…"). 1.7.2 keeps a filter's id when it
	/// rewrites the file (and drops <see cref="Revision"/>, which it doesn't know), so the revision survives a return to 1.7.2.
	/// </summary>
	public static string NewId() => $"saved:r{CurrentRevision}-{Guid.NewGuid():N}";

	/// <summary>The revision a saved filter's id says (<see cref="NewId"/>); 0 for an id of 1.7.2's ("saved:" and 32 hex digits).</summary>
	public static int RevisionOfId(string? id)
	{
		const string Prefix = "saved:r";
		if (id is null || !id.StartsWith(Prefix, StringComparison.Ordinal))
		{
			return 0;
		}

		var dash = id.IndexOf('-', Prefix.Length);
		return dash > Prefix.Length && int.TryParse(id.AsSpan(Prefix.Length, dash - Prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var revision) ? revision : 0;
	}

	/// <summary>"Comedy, Horror · Unwatched": what the filter keeps, for its why line.</summary>
	public string Describe()
	{
		var parts = new List<string>();
		if (!string.IsNullOrEmpty(PresetLabel))
		{
			parts.Add(PresetLabel);
		}

		foreach (var list in new[] { Genres, Tags, OfficialRatings, Years })
		{
			if (list is { Count: > 0 })
			{
				parts.Add(string.Join(", ", list));
			}
		}

		if (Unwatched)
		{
			parts.Add("Unwatched");
		}

		if (Favorites)
		{
			parts.Add("Favorites");
		}

		return parts.Count == 0 ? "Everything" : string.Join(" · ", parts);
	}
}

/// <summary>
/// Nixie 1.7.3 fix round (BH-173-M2): the titles a saved filter's Nixie preset keeps, of one library's titles. Every title of
/// the preset's rule, never a published collection's display cap (<see cref="Builders.BuildContext.Whole"/>). A filter saved
/// before 1.7.3 (<see cref="SavedFilter.Revision"/> 0) on "Highly rated" or "Short films" keeps 1.7.2's meaning: those two
/// rules, kept here for saved filters only, behind their old ids (1.7.3 renamed the picks and changed their rules: "Comedy +
/// Short films" went from 100 titles to 0). Pure: tests, and CollectionSources' saved filter.
/// </summary>
public static class SavedFilterPresets
{
	/// <param name="own">The library's titles (a title the server lists in two libraries is in both).</param>
	/// <param name="presetId">"nixie:pick:short-films", or the collection's own id.</param>
	public static IReadOnlySet<Guid> Titles(IndexSnapshot own, string presetId, int revision, DateTimeOffset now)
	{
		var id = presetId.StartsWith(NixiePreset.IdPrefix, StringComparison.Ordinal) ? presetId[NixiePreset.IdPrefix.Length..] : presetId;
		if (revision < SavedFilter.CurrentRevision && Before173(own, id) is { } kept)
		{
			return kept;
		}

		return SmartCollectionCatalog.Find(own, id, now, whole: true)?.ItemIds.ToHashSet() ?? [];
	}

	// 1.7.2's two picks, as its builders made them: "Highly rated, not watched" (every title rated 7.5 or more and not
	// watched) and "Short films" (every film with a runtime under 90 minutes); null for any other id. 1.7.2 made neither with
	// fewer than three titles: then the filter kept nothing
	private static HashSet<Guid>? Before173(IndexSnapshot own, string id)
	{
		const int Fewest = 3;
		IEnumerable<IndexEntry>? titles = id switch
		{
			"pick:highly-rated" => own.Entries.Where(x => x.Rating >= Builders.HighlyRatedBuilder.Least && !x.Played),
			"pick:short-films" => own.Entries.Where(x => x.Kind == IndexKind.Movie && x.RunTimeTicks is > 0 && x.RunTimeTicks < Builders.ShortFilmsBuilder.Under),
			_ => null,
		};
		if (titles is null)
		{
			return null;
		}

		var ids = titles.Select(x => x.Id).ToHashSet();
		return ids.Count >= Fewest ? ids : [];
	}
}
