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
	// end 1.7 trending

	/// <summary>"12 films", "3 shows", "8 titles".</summary>
	public string CountText => SmartCollectionKinds.CountText(ItemIds.Count, Only);

	/// <summary>Narrator: "Heists, made by Nixie, keyword, 12 films".</summary>
	public string AutomationName => $"{Title}, made by Nixie, {SmartCollectionKinds.Singular(Kind).ToLowerInvariant()}, {CountText}";
}

/// <summary>The words for each kind.</summary>
public static class SmartCollectionKinds
{
	/// <summary>The kinds in the order the Collections page lists them.</summary>
	public static IReadOnlyList<SmartCollectionKind> PageOrder { get; } =
	[
		SmartCollectionKind.Franchise, SmartCollectionKind.Decade, SmartCollectionKind.Director, SmartCollectionKind.Actor,
		SmartCollectionKind.Studio, SmartCollectionKind.Network, SmartCollectionKind.GenreMix, SmartCollectionKind.Keyword,
		SmartCollectionKind.Country,
		SmartCollectionKind.Award, // 1.7 keyless: after Countries, as the plan's 4.1 orders them
	];

	/// <summary>The kinds that make one collection each, together in the page's "Picks" row.</summary>
	public static IReadOnlyList<SmartCollectionKind> Picks { get; } =
	[
		SmartCollectionKind.HighlyRated, SmartCollectionKind.UnfinishedSeries, SmartCollectionKind.Seasonal, SmartCollectionKind.ShortFilms,
	];

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
		SmartCollectionKind.HighlyRated => "Highly rated",
		SmartCollectionKind.ShortFilms => "Short films",
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
		SmartCollectionKind.HighlyRated => "Highly rated, not watched",
		SmartCollectionKind.ShortFilms => "Short films",
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
	string? PresetLabel = null)
{
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
