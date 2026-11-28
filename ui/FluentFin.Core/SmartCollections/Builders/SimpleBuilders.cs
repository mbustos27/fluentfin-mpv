namespace FluentFin.Core.SmartCollections.Builders;

// 1.7.3 (P0-2b): every builder counts the titles with their copies collapsed (BuildContext.Titles), and (P0-1) says what it
// counted (BuildContext.Gates): its candidates, those past its floor, those past its cap
// 1.8 (P1-4, stream t): every collection has a reason line (one or two facts, then the count: Make.Of(context, …)), award
// badges from its titles' award facts, and no title or line that starts with "Your" or speaks of the viewer

/// <summary>Decades: films (and shows, apart) of one decade, ten or more: "1980s films". On demand only since 1.7.3.</summary>
public sealed class DecadeBuilder : ISmartCollectionBuilder
{
	public const int Least = 10;

	public SmartCollectionKind Kind => SmartCollectionKind.Decade;

	public IEnumerable<SmartCollection> Build(BuildContext context)
	{
		var groups = context.Titles.Entries
			.Where(x => x.Year is >= 1900 and < 2100)
			.GroupBy(x => (x.Kind, Decade: x.Year!.Value / 10 * 10))
			.ToList();
		var made = groups.Where(g => g.Count() >= Least)
			.OrderBy(g => g.Key.Kind).ThenByDescending(g => g.Key.Decade)
			.Select(g => (g.Key.Kind, g.Key.Decade, Titles: Make.ByName(g)))
			.Select(g => Make.Of(context, $"decade:{g.Kind.ToString().ToLowerInvariant()}:{g.Decade}", Kind, $"{g.Decade}s {Make.Noun(g.Kind, 2)}",
				ReasonLine.Of($"Released {g.Decade} to {g.Decade + 9}", ReasonCount.Of(g.Titles)), hasOwnOrder: false, g.Titles))
			.ToList();
		context.Gates.Count(Kind, groups.Count, made.Count, made.Count);
		return made;
	}
}

/// <summary>Directors with three or more owned films (the people pass). On demand only since 1.7.3.</summary>
public sealed class DirectorBuilder : ISmartCollectionBuilder
{
	public const int Least = 3;

	public SmartCollectionKind Kind => SmartCollectionKind.Director;

	public IEnumerable<SmartCollection> Build(BuildContext context)
	{
		var groups = context.Titles.Entries
			.Where(x => x.Kind == IndexKind.Movie)
			.SelectMany(x => (x.People ?? []).Where(p => p.Kind == "Director").Select(p => (p.Name, Entry: x)))
			.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
			.Select(g => (Name: g.First().Name, Films: g.Select(x => x.Entry).DistinctBy(x => x.Id).ToList()))
			.ToList();
		var made = groups.Where(g => g.Films.Count >= Least)
			.OrderByDescending(g => g.Films.Count).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
			.Select(g => (g.Name, Films: Make.ByRelease(g.Films)))
			.Select(g => Make.Of(context, $"director:{Make.Key(g.Name)}", Kind, g.Name, ReasonLine.Of("Director", ReasonCount.Of(g.Films)), hasOwnOrder: true, g.Films,
				why: $"Films directed by {g.Name}, in release order"))
			.ToList();
		context.Gates.Count(Kind, groups.Count, made.Count, made.Count);
		return made;
	}
}

/// <summary>Actors among the first five billed, in five or more owned titles (the people pass). On demand only since 1.7.3.</summary>
public sealed class ActorBuilder : ISmartCollectionBuilder
{
	public const int Least = 5;

	public SmartCollectionKind Kind => SmartCollectionKind.Actor;

	public IEnumerable<SmartCollection> Build(BuildContext context)
	{
		var groups = context.Titles.Entries
			.SelectMany(x => (x.People ?? []).Where(p => p.Kind == "Actor").Select(p => (p.Name, Entry: x)))
			.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
			.Select(g => (Name: g.First().Name, Titles: g.Select(x => x.Entry).DistinctBy(x => x.Id).ToList()))
			.ToList();
		var made = groups.Where(g => g.Titles.Count >= Least)
			.OrderByDescending(g => g.Titles.Count).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
			.Select(g => (Name: g.Name, Titles: (IReadOnlyList<IndexEntry>)[.. Make.ByRelease(g.Titles).AsEnumerable().Reverse()]))
			.Select(g => Make.Of(context, $"actor:{Make.Key(g.Name)}", Kind, g.Name, ReasonLine.Of("Among the first five billed", ReasonCount.Of(g.Titles)), hasOwnOrder: false,
				g.Titles, why: $"Films and shows with {g.Name} among the first five billed, the newest first"))
			.ToList();
		context.Gates.Count(Kind, groups.Count, made.Count, made.Count);
		return made;
	}
}

/// <summary>
/// Film studios with five or more films; shows' studios (networks, and on Jellyfin 12 production companies too, which can't
/// be told apart) with three or more, as "Studios and networks". Names that differ only in case or punctuation are one.
/// On demand only since 1.7.3.
/// </summary>
public sealed class StudioBuilder(IndexKind kind) : ISmartCollectionBuilder
{
	public int Least => kind == IndexKind.Movie ? 5 : 3;

	public SmartCollectionKind Kind => kind == IndexKind.Movie ? SmartCollectionKind.Studio : SmartCollectionKind.Network;

	public IEnumerable<SmartCollection> Build(BuildContext context)
	{
		var groups = context.Titles.Entries
			.Where(x => x.Kind == kind)
			.SelectMany(x => (x.Studios ?? []).Select(s => (Studio: s, Entry: x)))
			.GroupBy(x => Make.Key(x.Studio))
			.Where(g => g.Key.Length > 0)
			.Select(g => (Key: g.Key, Name: g.GroupBy(x => x.Studio).OrderByDescending(n => n.Count()).First().Key, Titles: g.Select(x => x.Entry).DistinctBy(x => x.Id).ToList()))
			.ToList();
		var made = groups.Where(g => g.Titles.Count >= Least)
			.OrderByDescending(g => g.Titles.Count).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
			.Select(g => (g.Key, g.Name, Titles: Make.ByName(g.Titles)))
			.Select(g => Make.Of(context, $"{(kind == IndexKind.Movie ? "studio" : "network")}:{g.Key}", Kind, g.Name,
				ReasonLine.Of(kind == IndexKind.Movie ? "Studio" : "Studio or network", ReasonCount.Of(g.Titles)), hasOwnOrder: false, g.Titles,
				why: kind == IndexKind.Movie ? $"Films from {g.Name}" : $"Shows from {g.Name}"))
			.ToList();
		context.Gates.Count(Kind, groups.Count, made.Count, made.Count);
		return made;
	}
}

/// <summary>
/// Genre mixes: two genres, eight or more titles in both, where the pair is under 80 % of the smaller genre (so a pair that
/// says nothing new, like most Animation being Family, is skipped). Films and shows apart; the twelve largest. On demand
/// only since 1.7.3.
/// </summary>
public sealed class GenreMixBuilder : ISmartCollectionBuilder
{
	public const int Least = 8;
	public const double MostOfSmaller = 0.8;
	public const int Most = 12;

	public SmartCollectionKind Kind => SmartCollectionKind.GenreMix;

	public IEnumerable<SmartCollection> Build(BuildContext context)
	{
		var found = new List<(IndexKind Kind, string A, string B, List<IndexEntry> Titles)>();
		var pairs = 0;
		foreach (var group in context.Titles.Entries.GroupBy(x => x.Kind))
		{
			var byGenre = group.SelectMany(x => (x.Genres ?? []).Select(g => (Genre: g, Entry: x)))
				.GroupBy(x => x.Genre, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.First().Genre, g => g.Select(x => x.Entry.Id).ToHashSet(), StringComparer.OrdinalIgnoreCase);
			var genres = byGenre.Keys.Where(g => byGenre[g].Count >= Least).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
			var entries = group.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First()); // 1.7.2: a title listed twice is one (ToDictionary threw)
			for (var i = 0; i < genres.Count; i++)
			{
				for (var j = i + 1; j < genres.Count; j++)
				{
					pairs++;
					var both = byGenre[genres[i]].Intersect(byGenre[genres[j]]).ToList();
					var smaller = Math.Min(byGenre[genres[i]].Count, byGenre[genres[j]].Count);
					if (both.Count >= Least && both.Count < MostOfSmaller * smaller)
					{
						found.Add((group.Key, genres[i], genres[j], [.. both.Select(id => entries[id])]));
					}
				}
			}
		}

		var made = found.OrderByDescending(x => x.Titles.Count).ThenBy(x => x.A).ThenBy(x => x.B).Take(context.Whole ? int.MaxValue : Most) // fix round (BH-173-M2)
			.Select(x => (x.Kind, x.A, x.B, Titles: Make.ByName(x.Titles)))
			.Select(x => Make.Of(context, $"genres:{x.Kind.ToString().ToLowerInvariant()}:{Make.Key(x.A)}+{Make.Key(x.B)}", Kind, $"{x.A} · {x.B}",
				ReasonLine.Of($"Both {x.A} and {x.B}", ReasonCount.Of(x.Titles)), hasOwnOrder: false, x.Titles))
			.ToList();
		context.Gates.Count(Kind, pairs, found.Count, made.Count);
		return made;
	}
}

/// <summary>Keywords: TMDb keywords on the allowlist (<see cref="Keywords"/>), five or more titles.</summary>
public sealed class KeywordBuilder : ISmartCollectionBuilder
{
	public const int Least = 5;

	public SmartCollectionKind Kind => SmartCollectionKind.Keyword;

	public IEnumerable<SmartCollection> Build(BuildContext context)
	{
		var tmdb = context.Outside.KeywordNames;
		var groups = context.Titles.Entries
			.SelectMany(x => x.Tags.Where(t => Keywords.NameOf(t, tmdb) is not null).Select(t => (Tag: t, Entry: x)))
			.GroupBy(x => x.Tag, StringComparer.OrdinalIgnoreCase)
			.Select(g => (Tag: g.Key, Titles: g.Select(x => x.Entry).DistinctBy(x => x.Id).ToList()))
			.ToList();
		// 1.7 keyless: Wikidata's finer genres join the keyword of the same name ("heist"): its titles the library has, matched
		// on this PC, added to the tagged ones (or alone, when nothing carries the tag)
		var genres = new Dictionary<string, List<IndexEntry>>(StringComparer.OrdinalIgnoreCase);
		var genreWhy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var lists = context.Outside.Lists.Where(x => x.Kind == Outside.OutsideListKind.Genre && x.KeywordTag is not null).ToList();
		if (lists.Count > 0)
		{
			var matcher = context.Matcher; // 1.7.3 (P0-2b): matched to the titles, copies collapsed
			foreach (var list in lists)
			{
				genres[list.KeywordTag!] = [.. matcher.MatchAll(list.Items.Select(x => x.Ref))];
				genreWhy[list.KeywordTag!] = list.Why;
			}
		}

		var merged = groups.Select(g => genres.TryGetValue(g.Tag, out var more) ? (g.Tag, Titles: g.Titles.Concat(more).DistinctBy(x => x.Id).ToList(), FromWikidata: more.Count > 0, Tagged: true) : (g.Tag, g.Titles, FromWikidata: false, Tagged: true))
			.Concat(genres.Where(x => !groups.Any(g => string.Equals(g.Tag, x.Key, StringComparison.OrdinalIgnoreCase))).Select(x => (Tag: x.Key, Titles: x.Value, FromWikidata: x.Value.Count > 0, Tagged: false)))
			.ToList();
		// end 1.7 keyless
		// only the allowlist's keywords, each confirmed by TMDb's names when its export is on (Keywords.NameOf)
		var made = merged.Where(g => Keywords.NameOf(g.Tag, tmdb) is not null && g.Titles.Count >= Least)
			.OrderByDescending(g => g.Titles.Count).ThenBy(g => g.Tag)
			.Select(g => (g.Tag, g.Tagged, Wikidata: g.FromWikidata ? genreWhy[g.Tag] : null, Titles: Make.ByName(g.Titles)))
			.Select(g => Make.Of(context, $"keyword:{Make.Key(g.Tag)}", Kind, Keywords.NameOf(g.Tag, tmdb)!, Reason(g.Tag, g.Tagged, g.Wikidata, g.Titles), hasOwnOrder: false,
				g.Titles, why: Why(g.Tag, g.Tagged, g.Wikidata)))
			.ToList();
		context.Gates.Count(Kind, merged.Count, made.Count, made.Count);
		return made;
	}

	// 1.7 keyless: the why line names where the titles came from: TMDb's keyword, Wikidata's genre, or both. 1.8 (P1-4):
	// without "Your"; the reason line says the same in short, then the count
	private static string Why(string tag, bool tagged, string? wikidata) => (tagged, wikidata) switch
	{
		(true, { } w) => $"Titles with TMDb's keyword “{tag}”, and {w}",
		(false, { } w) => Capital(w),
		_ => $"Titles with TMDb's keyword “{tag}”",
	};

	private static ReasonLine Reason(string tag, bool tagged, string? wikidata, IReadOnlyList<IndexEntry> titles) => (tagged, wikidata) switch
	{
		(true, { } w) => ReasonLine.Of($"TMDb keyword “{tag}”", Capital(w), ReasonCount.Of(titles)),
		(false, { } w) => ReasonLine.Of(Capital(w), ReasonCount.Of(titles)),
		_ => ReasonLine.Of($"TMDb keyword “{tag}”", ReasonCount.Of(titles)),
	};

	private static string Capital(string text) => text.Length == 0 ? text : char.ToUpper(text[0], System.Globalization.CultureInfo.CurrentCulture) + text[1..];
}

/// <summary>
/// 1.7.3 (P0-4): "Acclaimed, not seen yet" (the kind and id of 1.6's "Highly rated, not watched", so Settings, pins and Home's
/// saved rows carry over): titles rated 7.5 or more, not watched and backed by something beyond their own rating
/// (<see cref="QualityScore.IsBacked"/>), ranked by <see cref="QualityScore"/> (a high rating little backs up doesn't lead),
/// the best <see cref="Most"/>. Fix round (BH-173-M1, m1): when nothing in the index can back a rating (an index saved before
/// 1.7.3, a server that keeps no critics' ratings or tags: <see cref="RatingBacking"/>), every title rated 7.5 or more and not
/// watched, ranked the same way; and (M2) every title of its rule, uncapped, for a saved filter (<see cref="BuildContext.Whole"/>).
/// </summary>
public sealed class HighlyRatedBuilder : ISmartCollectionBuilder
{
	public const double Least = 7.5;
	public const int Fewest = 3;
	public const int Most = 20;
	public const string Title = "Acclaimed, not seen yet";

	public SmartCollectionKind Kind => SmartCollectionKind.HighlyRated;

	public IEnumerable<SmartCollection> Build(BuildContext context)
	{
		var quality = context.Quality;
		var backing = context.Backing;
		var rated = context.Titles.Entries.Where(x => x.Rating >= Least && !x.Played).ToList();
		// 1.7.3 (M1's review, Major): only a title something backs (critics, five or more tags, an outside list): an obscure
		// 9.8 with a poster and two tags took the places of the films everyone knows. Fix round (BH-173-M1, m1): only where
		// the index has something that can back a rating (missing data is not a decision, rule 4)
		var pool = backing.Any ? rated.Where(quality.IsBacked).ToList() : rated;
		var titles = Best.Of(pool, quality).Take(context.Whole ? int.MaxValue : Most).ToList();
		context.Gates.Note(Kind, backing.Census);
		var made = pool.Count >= Fewest
			? [Make.Of(context, "pick:highly-rated", Kind, Title, ReasonLine.Of("Rated 7.5 or more, weighted by what backs each rating", ReasonCount.OfAll(titles)), hasOwnOrder: false,
				titles, why: "Rated 7.5 or more and not watched yet, the best first, weighted by how much backs each rating")]
			: new List<SmartCollection>();
		context.Gates.Count(Kind, rated.Count, pool.Count >= Fewest ? pool.Count : 0, made.Count == 0 ? 0 : titles.Count);
		return made;
	}
}

/// <summary>
/// 1.7.3 (P0-4): "Under 90 minutes" (the kind and id of 1.6's "Short films"): feature films of 60 minutes up to (not
/// including) 90, not watched, best first by <see cref="QualityScore"/> (unrated ones last), the first <see cref="Most"/>.
/// A title with no runtime is left out. 1.6's took every film under 90, shortest first: its first posters were shorts.
/// </summary>
public sealed class ShortFilmsBuilder : ISmartCollectionBuilder
{
	public static readonly long Under = TimeSpan.FromMinutes(90).Ticks;
	public static readonly long AtLeast = TimeSpan.FromMinutes(60).Ticks;
	public const int Fewest = 3;
	public const int Most = 20;
	public const string Title = "Under 90 minutes";

	public SmartCollectionKind Kind => SmartCollectionKind.ShortFilms;

	public IEnumerable<SmartCollection> Build(BuildContext context)
	{
		var pool = context.Titles.Entries.Where(x => x.Kind == IndexKind.Movie && !x.Played && x.RunTimeTicks >= AtLeast && x.RunTimeTicks < Under).ToList();
		var films = Best.Of(pool, context.Quality).Take(context.Whole ? int.MaxValue : Most).ToList(); // fix round (BH-173-M2): a saved filter's, uncapped
		var made = pool.Count >= Fewest
			? [Make.Of(context, "pick:short-films", Kind, Title, ReasonLine.Of("Feature films of 60 to 90 minutes, best rated first", ReasonCount.OfAll(films)), hasOwnOrder: false,
				films, why: "Feature films of 60 to 90 minutes, not watched yet, the best rated first")]
			: new List<SmartCollection>();
		context.Gates.Count(Kind, pool.Count, pool.Count >= Fewest ? pool.Count : 0, made.Count == 0 ? 0 : films.Count);
		return made;
	}
}

/// <summary>1.7.3 (P0-4): best first: by score (titles with none last), then rating, name, id (the same order every time).</summary>
internal static class Best
{
	public static IEnumerable<IndexEntry> Of(IEnumerable<IndexEntry> titles, QualityScore quality) =>
		titles.Select(x => (Entry: x, Q: quality.Of(x)))
			.OrderBy(x => x.Q is null)
			.ThenByDescending(x => x.Q ?? 0)
			.ThenByDescending(x => x.Entry.Rating ?? 0)
			.ThenBy(x => x.Entry.OrderName, StringComparer.CurrentCultureIgnoreCase)
			.ThenBy(x => x.Entry.Id)
			.Select(x => x.Entry);
}

/// <summary>
/// Unfinished series: shows with episodes played and episodes left, the last one played over 30 days ago (the shows you
/// drifted away from: Next Up has the rest); the most recently left first. 1.8.1 (fix 5): <see cref="FewestEpisodes"/> or more
/// played, and not finished by the anchors' rule.
/// </summary>
public sealed class UnfinishedSeriesBuilder : ISmartCollectionBuilder
{
	public static readonly TimeSpan Away = TimeSpan.FromDays(30);
	public const int Fewest = 1;

	/// <summary>
	/// 1.8.1 (the owner's library, fix 5): the episodes a show needs played to be one the user drifted away from, not one they
	/// sampled: 9 of the owner's 35 had one or two.
	/// </summary>
	public const int FewestEpisodes = 3;

	public SmartCollectionKind Kind => SmartCollectionKind.UnfinishedSeries;

	public IEnumerable<SmartCollection> Build(BuildContext context)
	{
		var plays = context.Index.SeriesPlays;
		var titles = context.Titles;
		// 1.7.3 (P0-2b): a show in two libraries is one, its plays the latest of either copy's
		SeriesPlays? PlaysOf(IndexEntry show) => titles.CopyIds(show.Id).Select(id => plays.TryGetValue(id, out var p) ? p : null).OfType<SeriesPlays>().MaxBy(p => p.LastPlayed);
		// 1.8.1 (fix 5): three episodes or more, and not finished by the anchors' rule (90 % of its episodes): Community at 102
		// of 110 was both "You finished Community" and this row's first tile
		var shows = titles.Entries
			.Where(x => x.Kind == IndexKind.Series && x.Unplayed > 0 && !Anchors.AnchorFinder.Finished(x, titles, plays))
			.Select(x => (Show: x, Plays: PlaysOf(x)))
			.Where(x => x.Plays is { Played: >= FewestEpisodes } p && context.Now - p.LastPlayed > Away)
			.OrderByDescending(x => x.Plays!.LastPlayed)
			.Select(x => x.Show)
			.ToList();
		var made = shows.Count >= Fewest
			? [Make.Of(context, "pick:unfinished-series", Kind, "Unfinished series", ReasonLine.Of("Episodes left, none watched for a month", ReasonCount.OfAll(shows)), hasOwnOrder: false,
				shows, why: "Shows with episodes left and none watched for a month or more, the most recently left first")]
			: new List<SmartCollection>();
		context.Gates.Count(Kind, shows.Count, made.Count == 0 ? 0 : shows.Count, made.Count == 0 ? 0 : shows.Count);
		return made;
	}
}

/// <summary>Countries: films by their first production country, five or more, except the library's most common one. On demand only since 1.7.3.</summary>
public sealed class CountryBuilder : ISmartCollectionBuilder
{
	public const int Least = 5;

	public SmartCollectionKind Kind => SmartCollectionKind.Country;

	public IEnumerable<SmartCollection> Build(BuildContext context)
	{
		var groups = context.Titles.Entries.Where(x => x.Kind == IndexKind.Movie && x.Countries.Length > 0)
			.GroupBy(x => x.Countries[0], StringComparer.OrdinalIgnoreCase)
			.Select(g => (Country: g.Key, Films: g.ToList()))
			.OrderByDescending(g => g.Films.Count).ThenBy(g => g.Country).ToList();
		var home = groups.FirstOrDefault().Country;
		var made = groups.Where(g => g.Country != home && g.Films.Count >= Least)
			.Select(g => (g.Country, Films: Make.ByName(g.Films)))
			.Select(g => Make.Of(context, $"country:{Make.Key(g.Country)}", Kind, $"Films from {Short(g.Country)}", ReasonLine.Of("First production country", ReasonCount.Of(g.Films)),
				hasOwnOrder: false, g.Films, why: $"Films made in {Short(g.Country)}"))
			.ToList();
		context.Gates.Count(Kind, groups.Count, made.Count, made.Count);
		return made;
	}

	private static string Short(string country) => country switch
	{
		"United States of America" => "the United States",
		"United Kingdom" => "the United Kingdom",
		"Netherlands" => "the Netherlands",
		"Czech Republic" => "the Czech Republic",
		_ => country,
	};
}

/// <summary>
/// The seasonal row (Kometa's date windows): Halloween in October (titles with the "halloween" keyword first, then horror
/// films not watched), Christmas from <see cref="ChristmasOpens"/> to the end of December (the Christmas keywords). Nothing
/// out of season. 1.8.1 (the owner's library, fix 4): each part best first by the quality score, unwatched first
/// (<see cref="Best"/>; the raw rating put a new release with a few votes first), and Halloween's tagged titles watched are out
/// as its horror films are. 1.9 (stream wire): Thanksgiving from <see cref="ThanksgivingOpens"/> to US Thanksgiving Day
/// (<see cref="ThanksgivingDay"/>), TMDb's "thanksgiving" keyword (kept since the index's revision 4); in its last week beside
/// Christmas, Thanksgiving first (it ends first).
/// </summary>
public sealed class SeasonalBuilder : ISmartCollectionBuilder
{
	public const int Fewest = 3;
	public const int Most = 40;

	/// <summary>
	/// 1.8.1 (the owner's choice, 25 November 2026: "Fixed 20 November"): the day the Christmas row opens, each year, on the PC's
	/// own date; it shows until 31 December. The one place this date is set.
	/// </summary>
	public static DateOnly ChristmasOpens(int year) => new(year, 11, 20);

	/// <summary>1.8.1: whether <paramref name="now"/> (the local date) is in the Christmas row's window: from <see cref="ChristmasOpens"/> to 31 December.</summary>
	public static bool IsChristmas(DateTimeOffset now) => DateOnly.FromDateTime(now.Date) >= ChristmasOpens(now.Year);

	/// <summary>1.8.1: the day it opens as Settings says it ("20 November", "November 20"), in the PC's own words.</summary>
	public static string ChristmasOpensText =>
		ChristmasOpens(2000).ToString(System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.MonthDayPattern, System.Globalization.CultureInfo.CurrentCulture);

	// 1.9 (stream wire): the Thanksgiving row (the 1.9 plan's folded-in item; the owner's library has Thanksgiving "[HUB]"
	// libraries, and 1.8.1's seasonal row was empty until 20 November)
	/// <summary>1.9: the day the Thanksgiving row opens, each year, on the PC's own date: 1 November. The one place it is set.</summary>
	public static DateOnly ThanksgivingOpens(int year) => new(year, 11, 1);

	/// <summary>1.9: US Thanksgiving Day, the fourth Thursday of November: the Thanksgiving row's last day.</summary>
	public static DateOnly ThanksgivingDay(int year)
	{
		var first = new DateOnly(year, 11, 1);
		return first.AddDays((((int)DayOfWeek.Thursday - (int)first.DayOfWeek) + 7) % 7 + 21);
	}

	/// <summary>1.9: whether <paramref name="now"/> (the local date) is in the Thanksgiving row's window: from 1 November to Thanksgiving Day.</summary>
	public static bool IsThanksgiving(DateTimeOffset now) =>
		DateOnly.FromDateTime(now.Date) is var day && day >= ThanksgivingOpens(now.Year) && day <= ThanksgivingDay(now.Year);

	/// <summary>1.9: whether <paramref name="now"/> (the local date) is in the Halloween row's window: October.</summary>
	public static bool IsHalloween(DateTimeOffset now) => now.Month == 10;

	/// <summary>1.9: a Christmas keyword, as the Christmas row takes them: "christmas…" and "santa claus".</summary>
	public static bool IsChristmasTag(string tag) =>
		tag.Trim() is var t && (t.StartsWith("christmas", StringComparison.OrdinalIgnoreCase) || t.Equals("santa claus", StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// 1.9 (the review of wip/190-wire, Minor 4): whether a category of the seasonal family (a rule on
	/// <paramref name="keywords"/>) is in season on <paramref name="now"/>, by the rows' own windows, so their dates live here
	/// only: Halloween's in October, Christmas's from <see cref="ChristmasOpens"/> to the end of December, Thanksgiving's
	/// up to the day. A rule on none of those (another seasonal word, "holiday") is in season while any of them is open.
	/// </summary>
	public static bool InSeason(IEnumerable<string> keywords, DateTimeOffset now)
	{
		var (halloween, christmas, thanksgiving) = (false, false, false);
		foreach (var keyword in keywords)
		{
			halloween |= keyword.Trim().Equals("halloween", StringComparison.OrdinalIgnoreCase);
			christmas |= IsChristmasTag(keyword);
			thanksgiving |= keyword.Trim().Equals("thanksgiving", StringComparison.OrdinalIgnoreCase);
		}

		return halloween || christmas || thanksgiving
			? (halloween && IsHalloween(now)) || (christmas && IsChristmas(now)) || (thanksgiving && IsThanksgiving(now))
			: IsHalloween(now) || IsChristmas(now) || IsThanksgiving(now);
	}

	/// <summary>1.9: the seasonal rows' windows as Settings says them (its "In season" switch).</summary>
	public static string SeasonsText => $"Halloween in October, Thanksgiving in November up to the day, Christmas from {ChristmasOpensText} to the end of December.";
	// end 1.9

	public SmartCollectionKind Kind => SmartCollectionKind.Seasonal;

	public IEnumerable<SmartCollection> Build(BuildContext context)
	{
		var entries = context.Titles.Entries;
		var made = new List<SmartCollection>();
		var candidates = 0;
		var shown = 0;
		// 1.9 (stream wire): Thanksgiving, in November up to the day; its last week beside Christmas, so not one case of the
		// month's switch
		if (IsThanksgiving(context.Now))
		{
			var thanksgiving = entries.Where(x => x.Tags.Any(t => t.Trim().Equals("thanksgiving", StringComparison.OrdinalIgnoreCase))).ToList();
			List<IndexEntry> all = [.. Best.Of(thanksgiving.Where(x => !x.Played), context.Quality), .. Best.Of(thanksgiving.Where(x => x.Played), context.Quality)];
			candidates += all.Count;
			if (all.Count >= Fewest)
			{
				var titles = all.Take(context.Whole ? int.MaxValue : Most).ToList();
				shown += titles.Count;
				made.Add(Make.Of(context, "seasonal:thanksgiving", Kind, "Thanksgiving",
					ReasonLine.Of("Thanksgiving films and shows", ReasonCount.Of(titles)) with { Until = ThanksgivingDay(context.Now.Year) }, hasOwnOrder: false, titles,
					why: "Thanksgiving films and shows, up to Thanksgiving Day"));
			}
		}

		switch (context.Now.Month)
		{
			case 10 when IsHalloween(context.Now): // 1.9: its window in one place (InSeason)
				{
					// 1.8.1 (fix 4): the tagged ones not watched, as the horror films (a watched one led the row), each best first
					var tagged = Best.Of(entries.Where(x => !x.Played && x.Tags.Contains("halloween", StringComparer.OrdinalIgnoreCase)), context.Quality);
					var horror = Best.Of(entries.Where(x => x.Kind == IndexKind.Movie && !x.Played && x.Genres.Contains("Horror", StringComparer.OrdinalIgnoreCase)), context.Quality);
					var all = tagged.Concat(horror).DistinctBy(x => x.Id).ToList();
					candidates += all.Count;
					if (all.Count >= Fewest)
					{
						var titles = all.Take(context.Whole ? int.MaxValue : Most).ToList(); // fix round (BH-173-M2)
						shown += titles.Count;
						made.Add(Make.Of(context, "seasonal:halloween", Kind, "Halloween",
							ReasonLine.Of("Halloween and horror", ReasonCount.Of(titles)) with { Until = new DateOnly(context.Now.Year, 10, 31) }, hasOwnOrder: false, titles,
							why: "Halloween and horror, for October"));
					}

					break;
				}

			case 11 or 12 when IsChristmas(context.Now): // 1.8.1 (fix 4): from 20 November
				{
					var christmas = entries.Where(x => x.Tags.Any(IsChristmasTag)).ToList(); // 1.9: the Christmas keywords in one place
					// 1.8.1 (fix 4): best first by the quality score, the unwatched before the watched
					List<IndexEntry> all = [.. Best.Of(christmas.Where(x => !x.Played), context.Quality), .. Best.Of(christmas.Where(x => x.Played), context.Quality)];
					candidates += all.Count;
					if (all.Count >= Fewest)
					{
						var titles = all.Take(context.Whole ? int.MaxValue : Most).ToList(); // fix round (BH-173-M2)
						shown += titles.Count;
						made.Add(Make.Of(context, "seasonal:christmas", Kind, "Christmas",
							ReasonLine.Of("Christmas films and shows", ReasonCount.Of(titles)) with { Until = new DateOnly(context.Now.Year, 12, 31) }, hasOwnOrder: false, titles,
							why: "Christmas films and shows, for the holidays"));
					}

					break;
				}
		}

		context.Gates.Count(Kind, candidates, made.Count == 0 ? 0 : candidates, shown);
		return made;
	}
}
