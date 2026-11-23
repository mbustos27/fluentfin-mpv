namespace FluentFin.Core.SmartCollections.Builders;

// 1.7.3 (P0-2b): every builder counts the titles with their copies collapsed (BuildContext.Titles), and (P0-1) says what it
// counted (BuildContext.Gates): its candidates, those past its floor, those past its cap

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
			.Select(g => Make.Of($"decade:{g.Key.Kind.ToString().ToLowerInvariant()}:{g.Key.Decade}", Kind, $"{g.Key.Decade}s {Make.Noun(g.Key.Kind, 2)}",
				$"Your {Make.Noun(g.Key.Kind, 2)} from {g.Key.Decade} to {g.Key.Decade + 9}", hasOwnOrder: false, Make.ByName(g)))
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
			.Select(g => Make.Of($"director:{Make.Key(g.Name)}", Kind, g.Name, $"Your films directed by {g.Name}, in release order", hasOwnOrder: true, Make.ByRelease(g.Films)))
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
			.Select(g => Make.Of($"actor:{Make.Key(g.Name)}", Kind, g.Name, $"Your films and shows with {g.Name}", hasOwnOrder: false,
				[.. Make.ByRelease(g.Titles).AsEnumerable().Reverse()]))
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
			.Select(g => Make.Of($"{(kind == IndexKind.Movie ? "studio" : "network")}:{g.Key}", Kind, g.Name,
				kind == IndexKind.Movie ? $"Your films from {g.Name}" : $"Your shows from {g.Name}", hasOwnOrder: false, Make.ByName(g.Titles)))
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
			.Select(x => Make.Of($"genres:{x.Kind.ToString().ToLowerInvariant()}:{Make.Key(x.A)}+{Make.Key(x.B)}", Kind, $"{x.A} · {x.B}",
				$"Your {Make.Noun(x.Kind, 2)} that are both {x.A} and {x.B}", hasOwnOrder: false, Make.ByName(x.Titles)))
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
			.Select(g => Make.Of($"keyword:{Make.Key(g.Tag)}", Kind, Keywords.NameOf(g.Tag, tmdb)!, Why(g.Tag, g.Tagged, g.FromWikidata ? genreWhy[g.Tag] : null), hasOwnOrder: false, Make.ByName(g.Titles)))
			.ToList();
		context.Gates.Count(Kind, merged.Count, made.Count, made.Count);
		return made;
	}

	// 1.7 keyless: the why line names where the titles came from: TMDb's keyword, Wikidata's genre, or both
	private static string Why(string tag, bool tagged, string? wikidata) => (tagged, wikidata) switch
	{
		(true, null) => $"Your titles with TMDb's keyword “{tag}”",
		(true, { } w) => $"Your titles with TMDb's keyword “{tag}”, and {w}",
		(false, { } w) => $"Your {w}",
		_ => $"Your titles with TMDb's keyword “{tag}”",
	};
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
			? [Make.Of("pick:highly-rated", Kind, Title, "Rated 7.5 or more and not watched yet, the best first, weighted by how much backs each rating", hasOwnOrder: false, titles)]
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
			? [Make.Of("pick:short-films", Kind, Title, "Feature films of 60 to 90 minutes, not watched yet, the best rated first", hasOwnOrder: false, films)]
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
/// drifted away from: Next Up has the rest); the most recently left first.
/// </summary>
public sealed class UnfinishedSeriesBuilder : ISmartCollectionBuilder
{
	public static readonly TimeSpan Away = TimeSpan.FromDays(30);
	public const int Fewest = 1;

	public SmartCollectionKind Kind => SmartCollectionKind.UnfinishedSeries;

	public IEnumerable<SmartCollection> Build(BuildContext context)
	{
		var plays = context.Index.SeriesPlays;
		var titles = context.Titles;
		// 1.7.3 (P0-2b): a show in two libraries is one, its plays the latest of either copy's
		SeriesPlays? PlaysOf(IndexEntry show) => titles.CopyIds(show.Id).Select(id => plays.TryGetValue(id, out var p) ? p : null).OfType<SeriesPlays>().MaxBy(p => p.LastPlayed);
		var shows = titles.Entries
			.Where(x => x.Kind == IndexKind.Series && x.Unplayed > 0)
			.Select(x => (Show: x, Plays: PlaysOf(x)))
			.Where(x => x.Plays is { Played: > 0 } p && context.Now - p.LastPlayed > Away)
			.OrderByDescending(x => x.Plays!.LastPlayed)
			.Select(x => x.Show)
			.ToList();
		var made = shows.Count >= Fewest
			? [Make.Of("pick:unfinished-series", Kind, "Unfinished series", "Shows you started and haven't watched for a month", hasOwnOrder: false, shows)]
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
			.Select(g => Make.Of($"country:{Make.Key(g.Country)}", Kind, $"Films from {Short(g.Country)}", $"Your films made in {Short(g.Country)}", hasOwnOrder: false, Make.ByName(g.Films)))
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
/// films not watched), Christmas in December (the Christmas keywords). Nothing out of season.
/// </summary>
public sealed class SeasonalBuilder : ISmartCollectionBuilder
{
	public const int Fewest = 3;
	public const int Most = 40;

	public SmartCollectionKind Kind => SmartCollectionKind.Seasonal;

	public IEnumerable<SmartCollection> Build(BuildContext context)
	{
		var entries = context.Titles.Entries;
		var made = new List<SmartCollection>();
		var candidates = 0;
		var shown = 0;
		switch (context.Now.Month)
		{
			case 10:
				{
					var tagged = entries.Where(x => x.Tags.Contains("halloween", StringComparer.OrdinalIgnoreCase));
					var horror = entries.Where(x => x.Kind == IndexKind.Movie && !x.Played && x.Genres.Contains("Horror", StringComparer.OrdinalIgnoreCase)).OrderByDescending(x => x.Rating);
					var all = tagged.Concat(horror).DistinctBy(x => x.Id).ToList();
					candidates = all.Count;
					if (all.Count >= Fewest)
					{
						var titles = all.Take(context.Whole ? int.MaxValue : Most).ToList(); // fix round (BH-173-M2)
						shown = titles.Count;
						made.Add(Make.Of("seasonal:halloween", Kind, "Halloween", "Halloween and horror, for October", hasOwnOrder: false, titles));
					}

					break;
				}

			case 12:
				{
					var all = entries.Where(x => x.Tags.Any(t => t.StartsWith("christmas", StringComparison.OrdinalIgnoreCase) || t.Equals("santa claus", StringComparison.OrdinalIgnoreCase)))
						.OrderByDescending(x => x.Rating).ToList();
					candidates = all.Count;
					if (all.Count >= Fewest)
					{
						var titles = all.Take(context.Whole ? int.MaxValue : Most).ToList(); // fix round (BH-173-M2)
						shown = titles.Count;
						made.Add(Make.Of("seasonal:christmas", Kind, "Christmas", "Christmas films and shows, for December", hasOwnOrder: false, titles));
					}

					break;
				}
		}

		context.Gates.Count(Kind, candidates, made.Count == 0 ? 0 : candidates, shown);
		return made;
	}
}
