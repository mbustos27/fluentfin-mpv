namespace FluentFin.Core.SmartCollections.Builders;

/// <summary>Decades: films (and shows, apart) of one decade, ten or more: "1980s films".</summary>
public sealed class DecadeBuilder : ISmartCollectionBuilder
{
	public const int Least = 10;

	public SmartCollectionKind Kind => SmartCollectionKind.Decade;

	public IEnumerable<SmartCollection> Build(BuildContext context) =>
		context.Index.Entries
			.Where(x => x.Year is >= 1900 and < 2100)
			.GroupBy(x => (x.Kind, Decade: x.Year!.Value / 10 * 10))
			.Where(g => g.Count() >= Least)
			.OrderBy(g => g.Key.Kind).ThenByDescending(g => g.Key.Decade)
			.Select(g => Make.Of($"decade:{g.Key.Kind.ToString().ToLowerInvariant()}:{g.Key.Decade}", Kind, $"{g.Key.Decade}s {Make.Noun(g.Key.Kind, 2)}",
				$"Your {Make.Noun(g.Key.Kind, 2)} from {g.Key.Decade} to {g.Key.Decade + 9}", hasOwnOrder: false, Make.ByName(g)));
}

/// <summary>Directors with three or more owned films (the people pass).</summary>
public sealed class DirectorBuilder : ISmartCollectionBuilder
{
	public const int Least = 3;

	public SmartCollectionKind Kind => SmartCollectionKind.Director;

	public IEnumerable<SmartCollection> Build(BuildContext context) =>
		context.Index.Entries
			.Where(x => x.Kind == IndexKind.Movie)
			.SelectMany(x => x.People.Where(p => p.Kind == "Director").Select(p => (p.Name, Entry: x)))
			.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
			.Select(g => (Name: g.First().Name, Films: g.Select(x => x.Entry).DistinctBy(x => x.Id).ToList()))
			.Where(g => g.Films.Count >= Least)
			.OrderByDescending(g => g.Films.Count).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
			.Select(g => Make.Of($"director:{Make.Key(g.Name)}", Kind, g.Name, $"Your films directed by {g.Name}, in release order", hasOwnOrder: true, Make.ByRelease(g.Films)));
}

/// <summary>Actors among the first five billed, in five or more owned titles (the people pass).</summary>
public sealed class ActorBuilder : ISmartCollectionBuilder
{
	public const int Least = 5;

	public SmartCollectionKind Kind => SmartCollectionKind.Actor;

	public IEnumerable<SmartCollection> Build(BuildContext context) =>
		context.Index.Entries
			.SelectMany(x => x.People.Where(p => p.Kind == "Actor").Select(p => (p.Name, Entry: x)))
			.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
			.Select(g => (Name: g.First().Name, Titles: g.Select(x => x.Entry).DistinctBy(x => x.Id).ToList()))
			.Where(g => g.Titles.Count >= Least)
			.OrderByDescending(g => g.Titles.Count).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
			.Select(g => Make.Of($"actor:{Make.Key(g.Name)}", Kind, g.Name, $"Your films and shows with {g.Name}", hasOwnOrder: false,
				[.. Make.ByRelease(g.Titles).AsEnumerable().Reverse()]));
}

/// <summary>
/// Film studios with five or more films; shows' studios (networks, and on Jellyfin 12 production companies too, which can't
/// be told apart) with three or more, as "Studios and networks". Names that differ only in case or punctuation are one.
/// </summary>
public sealed class StudioBuilder(IndexKind kind) : ISmartCollectionBuilder
{
	public int Least => kind == IndexKind.Movie ? 5 : 3;

	public SmartCollectionKind Kind => kind == IndexKind.Movie ? SmartCollectionKind.Studio : SmartCollectionKind.Network;

	public IEnumerable<SmartCollection> Build(BuildContext context) =>
		context.Index.Entries
			.Where(x => x.Kind == kind)
			.SelectMany(x => x.Studios.Select(s => (Studio: s, Entry: x)))
			.GroupBy(x => Make.Key(x.Studio))
			.Where(g => g.Key.Length > 0)
			.Select(g => (Key: g.Key, Name: g.GroupBy(x => x.Studio).OrderByDescending(n => n.Count()).First().Key, Titles: g.Select(x => x.Entry).DistinctBy(x => x.Id).ToList()))
			.Where(g => g.Titles.Count >= Least)
			.OrderByDescending(g => g.Titles.Count).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
			.Select(g => Make.Of($"{(kind == IndexKind.Movie ? "studio" : "network")}:{g.Key}", Kind, g.Name,
				kind == IndexKind.Movie ? $"Your films from {g.Name}" : $"Your shows from {g.Name}", hasOwnOrder: false, Make.ByName(g.Titles)));
}

/// <summary>
/// Genre mixes: two genres, eight or more titles in both, where the pair is under 80 % of the smaller genre (so a pair that
/// says nothing new, like most Animation being Family, is skipped). Films and shows apart; the twelve largest.
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
		foreach (var group in context.Index.Entries.GroupBy(x => x.Kind))
		{
			var byGenre = group.SelectMany(x => x.Genres.Select(g => (Genre: g, Entry: x)))
				.GroupBy(x => x.Genre, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.First().Genre, g => g.Select(x => x.Entry.Id).ToHashSet(), StringComparer.OrdinalIgnoreCase);
			var genres = byGenre.Keys.Where(g => byGenre[g].Count >= Least).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
			var entries = group.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First()); // 1.7.2: a title listed twice is one (ToDictionary threw)
			for (var i = 0; i < genres.Count; i++)
			{
				for (var j = i + 1; j < genres.Count; j++)
				{
					var both = byGenre[genres[i]].Intersect(byGenre[genres[j]]).ToList();
					var smaller = Math.Min(byGenre[genres[i]].Count, byGenre[genres[j]].Count);
					if (both.Count >= Least && both.Count < MostOfSmaller * smaller)
					{
						found.Add((group.Key, genres[i], genres[j], [.. both.Select(id => entries[id])]));
					}
				}
			}
		}

		return found.OrderByDescending(x => x.Titles.Count).ThenBy(x => x.A).ThenBy(x => x.B).Take(Most)
			.Select(x => Make.Of($"genres:{x.Kind.ToString().ToLowerInvariant()}:{Make.Key(x.A)}+{Make.Key(x.B)}", Kind, $"{x.A} · {x.B}",
				$"Your {Make.Noun(x.Kind, 2)} that are both {x.A} and {x.B}", hasOwnOrder: false, Make.ByName(x.Titles)));
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
		var groups = context.Index.Entries
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
			var matcher = new Outside.OutsideMatcher(context.Index);
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
		return merged.Where(g => Keywords.NameOf(g.Tag, tmdb) is not null && g.Titles.Count >= Least)
			.OrderByDescending(g => g.Titles.Count).ThenBy(g => g.Tag)
			.Select(g => Make.Of($"keyword:{Make.Key(g.Tag)}", Kind, Keywords.NameOf(g.Tag, tmdb)!, Why(g.Tag, g.Tagged, g.FromWikidata ? genreWhy[g.Tag] : null), hasOwnOrder: false, Make.ByName(g.Titles)));
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

/// <summary>Highly rated, not watched: community rating 7.5 or more, not played; best first.</summary>
public sealed class HighlyRatedBuilder : ISmartCollectionBuilder
{
	public const double Least = 7.5;
	public const int Fewest = 3;

	public SmartCollectionKind Kind => SmartCollectionKind.HighlyRated;

	public IEnumerable<SmartCollection> Build(BuildContext context)
	{
		var titles = context.Index.Entries.Where(x => x.Rating >= Least && !x.Played).OrderByDescending(x => x.Rating).ThenBy(x => x.OrderName, StringComparer.CurrentCultureIgnoreCase).ToList();
		if (titles.Count >= Fewest)
		{
			yield return Make.Of("pick:highly-rated", Kind, "Highly rated, not watched", "Rated 7.5 or more on the server, and not watched yet", hasOwnOrder: false, titles);
		}
	}
}

/// <summary>Short films: films with a runtime under 90 minutes (a title with none is left out); shortest first.</summary>
public sealed class ShortFilmsBuilder : ISmartCollectionBuilder
{
	public static readonly long Under = TimeSpan.FromMinutes(90).Ticks;
	public const int Fewest = 3;

	public SmartCollectionKind Kind => SmartCollectionKind.ShortFilms;

	public IEnumerable<SmartCollection> Build(BuildContext context)
	{
		var films = context.Index.Entries.Where(x => x.Kind == IndexKind.Movie && x.RunTimeTicks is > 0 && x.RunTimeTicks < Under)
			.OrderBy(x => x.RunTimeTicks).ThenBy(x => x.OrderName, StringComparer.CurrentCultureIgnoreCase).ToList();
		if (films.Count >= Fewest)
		{
			yield return Make.Of("pick:short-films", Kind, "Short films", "Films under 90 minutes", hasOwnOrder: false, films);
		}
	}
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
		var shows = context.Index.Entries
			.Where(x => x.Kind == IndexKind.Series && x.Unplayed > 0 && plays.TryGetValue(x.Id, out var p) && p.Played > 0 && context.Now - p.LastPlayed > Away)
			.OrderByDescending(x => plays[x.Id].LastPlayed).ToList();
		if (shows.Count >= Fewest)
		{
			yield return Make.Of("pick:unfinished-series", Kind, "Unfinished series", "Shows you started and haven't watched for a month", hasOwnOrder: false, shows);
		}
	}
}

/// <summary>Countries: films by their first production country, five or more, except the library's most common one.</summary>
public sealed class CountryBuilder : ISmartCollectionBuilder
{
	public const int Least = 5;

	public SmartCollectionKind Kind => SmartCollectionKind.Country;

	public IEnumerable<SmartCollection> Build(BuildContext context)
	{
		var groups = context.Index.Entries.Where(x => x.Kind == IndexKind.Movie && x.Countries.Length > 0)
			.GroupBy(x => x.Countries[0], StringComparer.OrdinalIgnoreCase)
			.Select(g => (Country: g.Key, Films: g.ToList()))
			.OrderByDescending(g => g.Films.Count).ThenBy(g => g.Country).ToList();
		var home = groups.FirstOrDefault().Country;
		return groups.Where(g => g.Country != home && g.Films.Count >= Least)
			.Select(g => Make.Of($"country:{Make.Key(g.Country)}", Kind, $"Films from {Short(g.Country)}", $"Your films made in {Short(g.Country)}", hasOwnOrder: false, Make.ByName(g.Films)));
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
		var entries = context.Index.Entries;
		switch (context.Now.Month)
		{
			case 10:
				{
					var tagged = entries.Where(x => x.Tags.Contains("halloween", StringComparer.OrdinalIgnoreCase));
					var horror = entries.Where(x => x.Kind == IndexKind.Movie && !x.Played && x.Genres.Contains("Horror", StringComparer.OrdinalIgnoreCase)).OrderByDescending(x => x.Rating);
					var titles = tagged.Concat(horror).DistinctBy(x => x.Id).Take(Most).ToList();
					if (titles.Count >= Fewest)
					{
						yield return Make.Of("seasonal:halloween", Kind, "Halloween", "Halloween and horror, for October", hasOwnOrder: false, titles);
					}

					break;
				}

			case 12:
				{
					var titles = entries.Where(x => x.Tags.Any(t => t.StartsWith("christmas", StringComparison.OrdinalIgnoreCase) || t.Equals("santa claus", StringComparison.OrdinalIgnoreCase)))
						.OrderByDescending(x => x.Rating).Take(Most).ToList();
					if (titles.Count >= Fewest)
					{
						yield return Make.Of("seasonal:christmas", Kind, "Christmas", "Christmas films and shows, for December", hasOwnOrder: false, titles);
					}

					break;
				}
		}
	}
}
