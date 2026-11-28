using System.Globalization;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Builders;
using Xunit.Abstractions;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (Phase 3, R5): a library of the owner's current shape for the Spotlight (the anchors stream's, for the
/// categories, is OwnerShape190), from the report "Collections 2 on the owner's library" (26 Nov): thin metadata (no keywords, no critics, ratings that back nothing, so every title scores its
/// kind's mean), ISO country codes, shows' studios that are production companies, and thousands of people past the count, so
/// the candidates are many and most of them have nothing to do with what the user watches: 1970s and 80s anime voice actors,
/// 1990s kickboxing films, Chinese animation, telenovelas, westerns. The user's 87 played shows lean to the six kinds the
/// report names (workplace sitcoms, adult animation, superhero, anime, horror, prestige drama), with a few others, and their
/// films to superhero and horror. Seeded: the same library every run.
/// </summary>
internal static class SpotlightShape190
{
	/// <summary>The report's "now": 10:30 PM on Thursday 26 November 2026, ISO week 48.</summary>
	public static readonly DateTimeOffset Now = new(2026, 11, 26, 22, 30, 0, TimeSpan.FromHours(-8));

	/// <summary>
	/// A part of the library: its titles' kind, genres (the first always, each other one most of the time), countries, years and
	/// studios, how many actors are billed across it and how many directors (films), and whether it is one of the six kinds of
	/// show the owner watches (<paramref name="Tied"/>).
	/// </summary>
	public sealed record Area(string Code, bool Tied, IndexKind Kind, string[] Genres, string[] Countries, int From, int To, string[] Studios, int Actors, int Titles, int Directors = 0);

	public static readonly Area[] Areas =
	[
		// the six the owner's played shows lean to
		new("Workplace", true, IndexKind.Series, ["Comedy"], ["US"], 2005, 2024, ["Universal Television", "Deedle-Dee Productions", "3 Arts Entertainment"], 30, 110),
		new("AdultAnimation", true, IndexKind.Series, ["Animation", "Comedy"], ["US"], 1997, 2024, ["Comedy Central Productions", "Williams Street", "Bento Box Entertainment"], 24, 90),
		new("SuperheroShow", true, IndexKind.Series, ["Action & Adventure", "Sci-Fi & Fantasy", "Drama"], ["US"], 2010, 2025, ["Marvel Television", "Sony Pictures Television", "Berlanti Productions"], 20, 75),
		new("SuperheroFilm", true, IndexKind.Movie, ["Action", "Science Fiction", "Adventure"], ["US"], 2008, 2025, ["Marvel Studios", "DC Studios", "Columbia Pictures"], 20, 75, 8),
		new("Anime", true, IndexKind.Series, ["Animation", "Action & Adventure", "Sci-Fi & Fantasy"], ["JP"], 2010, 2025, ["ufotable", "MAPPA", "Bones", "Wit Studio", "Studio Pierrot"], 40, 150),
		new("HorrorShow", true, IndexKind.Series, ["Horror", "Drama", "Mystery"], ["US"], 2005, 2025, ["FX Productions", "Ryan Murphy Productions", "Amblin Television"], 16, 60),
		new("HorrorFilm", true, IndexKind.Movie, ["Horror", "Thriller"], ["US"], 2005, 2025, ["Blumhouse Productions", "A24", "New Line Cinema"], 20, 75, 8),
		new("Prestige", true, IndexKind.Series, ["Drama", "Crime"], ["US", "GB"], 2008, 2025, ["HBO", "AMC Studios", "Apple Studios", "Sony Pictures Television"], 30, 110),

		// the rest of a big library: most of the candidates
		new("OldAnime", false, IndexKind.Series, ["Animation", "Family", "Kids"], ["JP"], 1970, 1989, ["Nippon Animation", "Tatsunoko Production", "Toei Animation", "Madhouse"], 300, 1100),
		new("Donghua", false, IndexKind.Series, ["Animation", "Action & Adventure", "Sci-Fi & Fantasy"], ["CN"], 2018, 2025, ["Tencent Penguin Pictures", "BiliBili", "Original Force"], 150, 550),
		new("Kickboxing", false, IndexKind.Movie, ["Action", "Thriller"], ["US"], 1988, 1999, ["Imperial Entertainment", "Nu Image", "Trimark Pictures"], 150, 550, 60),
		new("Telenovela", false, IndexKind.Series, ["Drama", "Soap"], ["MX", "BR"], 1990, 2015, ["Televisa", "TV Globo"], 150, 550),
		new("Western", false, IndexKind.Movie, ["Western"], ["US"], 1945, 1965, ["Republic Pictures", "Universal International Pictures"], 100, 370, 45),
		new("Bollywood", false, IndexKind.Movie, ["Drama", "Romance", "Music"], ["IN"], 1995, 2020, ["Yash Raj Films", "Dharma Productions"], 150, 550, 60),
		new("NatureDoc", false, IndexKind.Series, ["Documentary"], ["GB", "US"], 1990, 2025, ["BBC Natural History Unit", "National Geographic"], 40, 150),
		new("KDrama", false, IndexKind.Series, ["Drama", "Romance"], ["KR"], 2010, 2025, ["Studio Dragon", "JTBC Studios"], 150, 550),
		new("PostwarBritish", false, IndexKind.Movie, ["Comedy", "Crime", "Drama"], ["GB"], 1940, 1959, ["Ealing Studios", "Rank Organisation"], 80, 300, 35),
		new("EightiesComedy", false, IndexKind.Movie, ["Comedy"], ["US"], 1980, 1992, ["Paramount Pictures", "Orion Pictures"], 120, 440, 50),
		new("KidsSitcom", false, IndexKind.Series, ["Comedy", "Family", "Kids"], ["US"], 2008, 2024, ["Nickelodeon Productions", "It's a Laugh Productions"], 100, 370),
		new("Wuxia", false, IndexKind.Series, ["Drama", "Action & Adventure"], ["CN"], 2015, 2025, ["Croton Media", "iQIYI"], 120, 440),
		new("Giallo", false, IndexKind.Movie, ["Horror", "Thriller", "Mystery"], ["IT"], 1968, 1982, ["Titanus", "Hammer Films"], 60, 220, 25),
		new("Hallmark", false, IndexKind.Movie, ["Romance", "TV Movie"], ["US", "CA"], 2010, 2024, ["Crown Media", "Hallmark Media"], 100, 370, 40),
		new("Reality", false, IndexKind.Series, ["Reality"], ["US"], 2005, 2024, ["51 Minds", "Bunim/Murray Productions"], 30, 110),
	];

	/// <summary>The shows played in each area: 87 in all, as the owner's, most in the six.</summary>
	private static readonly Dictionary<string, int> s_playedShows = new(StringComparer.Ordinal)
	{
		["Workplace"] = 20, ["AdultAnimation"] = 12, ["SuperheroShow"] = 12, ["Anime"] = 18, ["HorrorShow"] = 10, ["Prestige"] = 13, ["KidsSitcom"] = 1, ["KDrama"] = 1,
	};

	/// <summary>The films played in each area (and 20 more started, anywhere).</summary>
	private static readonly Dictionary<string, int> s_playedFilms = new(StringComparer.Ordinal)
	{
		["SuperheroFilm"] = 30, ["HorrorFilm"] = 25, ["Western"] = 2, ["EightiesComedy"] = 2, ["Bollywood"] = 2, ["Kickboxing"] = 2,
	};

	/// <summary>The curated studios in the library, by the area they are in.</summary>
	public static readonly Dictionary<string, string> CuratedStudios = new(StringComparer.Ordinal)
	{
		["ufotable"] = "Anime", ["blumhouseproductions"] = "HorrorFilm", ["a24"] = "HorrorFilm", ["madhouse"] = "OldAnime", ["ealingstudios"] = "PostwarBritish", ["hammerfilms"] = "Giallo",
	};

	public sealed record Library(List<IndexEntry> Titles, Dictionary<Guid, SeriesPlays> Plays);

	/// <param name="history">The user's plays (false: a user who played nothing, the same titles).</param>
	/// <param name="extra">More titles (a test's planted subjects).</param>
	/// <param name="scale">
	/// Each area's titles and people, and the bulk, this many times over (the same 87 shows played): 9 gives about 11,500
	/// candidates, the owner's count.
	/// </param>
	public static Library Make(bool history = true, IEnumerable<IndexEntry>? extra = null, int scale = 1)
	{
		var random = new Random(19005);
		var titles = new List<IndexEntry>(16_000);
		var plays = new Dictionary<Guid, SeriesPlays>();
		var n = 0;
		foreach (var area in Areas)
		{
			var made = new List<IndexEntry>(area.Titles * scale);
			for (var i = 0; i < area.Titles * scale; i++)
			{
				var people = new List<IndexPerson>(4);
				if (area.Kind == IndexKind.Movie && area.Directors > 0)
				{
					people.Add(new IndexPerson(Name(area.Code, "director", random.Next(area.Directors * scale)), null, "Director"));
				}

				var billed = new HashSet<int>();
				while (billed.Count < 3)
				{
					billed.Add((int)Math.Floor(Math.Pow(random.NextDouble(), 1.25) * area.Actors * scale));
				}

				people.AddRange(billed.Select(a => new IndexPerson(Name(area.Code, "actor", a), null, "Actor")));
				string[] genres = [area.Genres[0], .. area.Genres.Skip(1).Where(_ => random.NextDouble() < 0.7)];
				made.Add(Title(++n, $"{area.Code} title {i + 1}", area.Kind, area.From + random.Next(area.To - area.From + 1), genres,
					area.Countries[random.Next(area.Countries.Length)], area.Studios[random.Next(area.Studios.Length)], [.. people], random));
			}

			if (history)
			{
				var shows = s_playedShows.GetValueOrDefault(area.Code);
				var films = s_playedFilms.GetValueOrDefault(area.Code);
				for (var i = 0; i < made.Count && (shows > 0 || films > 0); i += 3)
				{
					var title = made[i];
					if (title.Kind == IndexKind.Series && shows-- > 0)
					{
						// episodes played: most a few, some a binge
						var episodes = 1 + (int)Math.Floor(Math.Pow(random.NextDouble(), 2) * 170);
						made[i] = title with { Unplayed = Math.Max(0, (title.Unplayed ?? 0) + 180 - episodes) };
						plays[title.Id] = new SeriesPlays(episodes, Now.AddDays(-1 - random.Next(300)));
					}
					else if (title.Kind == IndexKind.Movie && films-- > 0)
					{
						made[i] = title with { Played = true, PlayCount = 1, LastPlayed = Now.AddDays(-1 - random.Next(700)) };
					}
				}
			}

			titles.AddRange(made);
		}

		titles.AddRange(Filler(random, ref n, 8_000 * scale));
		if (history)
		{
			// 20 films started and left, anywhere
			for (var i = 0; i < 20; i++)
			{
				var at = random.Next(titles.Count);
				if (titles[at] is { Kind: IndexKind.Movie, Played: false } film)
				{
					titles[at] = film with { PlayCount = 1, LastPlayed = Now.AddDays(-1 - random.Next(200)) };
				}
			}
		}

		titles.AddRange(extra ?? []);
		return new Library(titles, plays);
	}

	public static IndexSnapshot Index(Library library) => new([], library.Titles, library.Plays, HasPeople: true, Now) { Key = "owner190" };

	public static BuildContext Context(Library library) => new(Index(library), Now, new HashSet<string>());

	/// <summary>A person's name: "Anime actor 012".</summary>
	public static string Name(string area, string role, int i) => string.Create(CultureInfo.InvariantCulture, $"{area} {role} {i:D3}");

	/// <summary>The area a candidate is of (its people's names say it; a curated studio's is where its titles are).</summary>
	public static string AreaOf(SpotlightCandidate candidate) => candidate.Subject == SpotlightSubject.Studio
		? CuratedStudios.GetValueOrDefault(candidate.Id["spotlight:studio:".Length..], "?")
		: candidate.Name.Split(' ')[0];

	/// <summary>Whether a candidate is tied to what the owner watches: its titles are of the six.</summary>
	public static bool Tied(SpotlightCandidate candidate) => Areas.FirstOrDefault(x => x.Code == AreaOf(candidate))?.Tied == true;

	/// <summary>Which of the six a tied candidate is of (superhero and horror have shows and films).</summary>
	public static string KindOf(SpotlightCandidate candidate) => AreaOf(candidate) switch
	{
		"SuperheroShow" or "SuperheroFilm" => "superhero",
		"HorrorShow" or "HorrorFilm" => "horror",
		var area => area,
	};

	public static IndexEntry Title(int n, string name, IndexKind kind, int year, string[] genres, string country, string studio, IndexPerson[] people, Random random) => new()
	{
		Id = new Guid(0x19050000 + n, 0, 0, [1, 9, 0, 5, 0, 0, 0, 0]),
		Name = name,
		Kind = kind,
		Year = year,
		PremiereDate = string.Create(CultureInfo.InvariantCulture, $"{year:D4}-{1 + random.Next(12):D2}-{1 + random.Next(28):D2}"),
		// thin metadata: a community rating and nothing that backs it (no critics, no tags), so the score is the kind's mean
		Rating = Math.Round(Math.Clamp((kind == IndexKind.Series ? 7.0 : 6.0) + ((random.NextDouble() - 0.5) * 2.4), 1, 9.9), 1),
		Unplayed = kind == IndexKind.Series ? 10 + random.Next(60) : null,
		Genres = genres,
		Tags = [],
		Countries = [country],
		Studios = [studio],
		People = people,
		PrimaryTag = "p",
	};

	// 8,000 titles (times the scale) of everything, each with its own people (nobody past the count): the library's bulk, for the features' rarity
	private static IEnumerable<IndexEntry> Filler(Random random, ref int n, int count)
	{
		string[] filmGenres = ["Drama", "Comedy", "Action", "Thriller", "Romance", "Horror", "Documentary", "Crime", "Adventure", "Family", "Animation", "Science Fiction", "Fantasy", "Mystery", "Music", "History", "War", "Western"];
		string[] showGenres = ["Drama", "Comedy", "Reality", "Documentary", "Crime", "Animation", "Action & Adventure", "Sci-Fi & Fantasy", "Kids", "Family", "Mystery", "Soap", "Talk", "News"];
		string[] countries = ["US", "US", "US", "US", "GB", "FR", "DE", "JP", "IN", "CA", "IT", "ES", "MX", "KR", "CN", "BR", "AU"];
		var made = new List<IndexEntry>(count);
		for (var i = 0; i < count; i++)
		{
			var kind = random.NextDouble() < 0.7 ? IndexKind.Movie : IndexKind.Series;
			var pool = kind == IndexKind.Movie ? filmGenres : showGenres;
			string[] genres = [.. new[] { pool[random.Next(pool.Length)], pool[random.Next(pool.Length)] }.Distinct()];
			var people = new List<IndexPerson>(4);
			if (kind == IndexKind.Movie)
			{
				people.Add(new IndexPerson(Name("Filler", "director", i), null, "Director"));
			}

			people.AddRange(Enumerable.Range(0, 3).Select(k => new IndexPerson(Name("Filler", "actor", (i * 3) + k), null, "Actor")));
			var year = 1950 + (int)Math.Floor(Math.Sqrt(random.NextDouble()) * 76);
			made.Add(Title(++n, $"Filler title {i + 1}", kind, year, genres, countries[random.Next(countries.Length)], $"Filler studio {i % 2_000}", [.. people], random));
		}

		return made;
	}
}

/// <summary>
/// Nixie 1.9 (Phase 3, R5): the week's Spotlight drawn from the candidates nearest the user's taste, so on a big library with
/// thin metadata it isn't close to random. The report: 11,812 candidates, the 983 with a taste of 2 or more holding a quarter
/// of the draw; this week an anime voice actor of the 1970s, next week a 1990s kickboxing actor.
/// </summary>
public sealed class SpotlightTop190Tests(ITestOutputHelper output)
{
	private const int Weeks = 26;

	// the weeks' picks as the app makes them: each kept on this PC (the store keeps the newest 8), and with nothing kept (a first
	// week, the TV)
	private static (List<SpotlightCandidate> Kept, List<SpotlightCandidate> Plain) HalfAYear(IReadOnlyList<SpotlightCandidate> candidates)
	{
		var kept = new Dictionary<string, string>(StringComparer.Ordinal);
		var keptPicks = new List<SpotlightCandidate>();
		var plainPicks = new List<SpotlightCandidate>();
		for (var w = 0; w < Weeks; w++)
		{
			var week = SpotlightWeek.Of(SpotlightShape190.Now.AddDays(7 * w));
			plainPicks.Add(SpotlightBuilder.Pick(candidates, week)!);
			var pick = SpotlightBuilder.Pick(candidates, week, kept)!;
			keptPicks.Add(pick);
			kept[week.Seed] = pick.Id;
			foreach (var old in kept.Keys.OrderByDescending(x => x, StringComparer.Ordinal).Skip(SmartCollectionStore.SpotlightWeeksKept).ToList())
			{
				kept.Remove(old);
			}
		}

		return (keptPicks, plainPicks);
	}

	private static string Say(IEnumerable<SpotlightCandidate> picks) =>
		string.Join(", ", picks.GroupBy(SpotlightShape190.AreaOf).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"));

	// the shortest gap between two weeks with the same pick (int.MaxValue: none came back)
	private static int ShortestRepeat(List<SpotlightCandidate> picks)
	{
		var shortest = int.MaxValue;
		for (var i = 0; i < picks.Count; i++)
		{
			for (var j = i + 1; j < picks.Count; j++)
			{
				if (picks[i].Id == picks[j].Id)
				{
					shortest = Math.Min(shortest, j - i);
				}
			}
		}

		return shortest;
	}

	// measured on 391ffc56 (1.9's draw weighted by taste over every candidate): 8 of 26 weeks tied, kept or not (Donghua 4,
	// OldAnime 4, then one or two of each of 15 more areas); with the nearest only, 26 of 26 from all six kinds (Anime 8,
	// SuperheroShow 6, Workplace 5, Prestige 4, AdultAnimation 2, HorrorShow 1). The nearest by fix 8's cosine against all
	// they watched at once gave 26 of 26 too, but from 4 of the kinds: no horror, no prestige drama
	[Fact]
	public void OverHalfAYear_TheSpotlightIsOfWhatTheyWatch_FromEachKindTheyWatch_NoneAgainWithinTheWeeksKept()
	{
		var candidates = SpotlightBuilder.Candidates(SpotlightShape190.Context(SpotlightShape190.Make()));
		var tied = candidates.Count(SpotlightShape190.Tied);
		var (kept, plain) = HalfAYear(candidates);
		var keptTied = kept.Count(SpotlightShape190.Tied);
		var plainTied = plain.Count(SpotlightShape190.Tied);
		var kinds = kept.Where(SpotlightShape190.Tied).Select(SpotlightShape190.KindOf).Distinct().Count();
		var said = string.Create(CultureInfo.InvariantCulture,
			$"{candidates.Count} candidates, {tied} tied ({100.0 * tied / candidates.Count:0.0} %); kept {keptTied} of {Weeks} tied, {kinds} of the six kinds ({Say(kept)}); nothing kept {plainTied} of {Weeks} ({Say(plain)}); shortest repeat kept {ShortestRepeat(kept)}");
		output.WriteLine(said);

		Assert.True(candidates.Count >= 1_000 && tied < candidates.Count / 5, said); // many candidates, most of them nothing to do with what they watch
		Assert.True(keptTied >= 23, said);
		Assert.True(plainTied >= 23, said);
		Assert.True(kinds >= 5, said); // not only the kinds that share the most genres
		Assert.True(ShortestRepeat(kept) > SmartCollectionStore.SpotlightWeeksKept, said);
	}

	// the rebuild's blocker 3: inside the nearest the draw was nearly flat (the owner's 394: the 20 most alike got 7 % of a
	// week; tastes 2.42 to 4.00, mean 2.86), so the weeks went to the loose fits as often as the good ones. Weighted by each
	// one's place among the nearest by likeness, most of half a year goes to the 20 most alike, kept or not, none again
	// within the weeks kept
	[Fact]
	public void OverHalfAYear_MostWeeksGoToTheTwentyMostAlike_NoneAgainWithinTheWeeksKept()
	{
		var candidates = SpotlightBuilder.Candidates(SpotlightShape190.Context(SpotlightShape190.Make(scale: 9)));
		var nearest = candidates.Where(x => x.Nearest).ToList();
		var top = nearest.OrderByDescending(x => x.Likeness ?? 0).ThenBy(x => x.Id, StringComparer.Ordinal).Take(20).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
		var (kept, plain) = HalfAYear(candidates);
		var said = string.Create(CultureInfo.InvariantCulture,
			$"{candidates.Count} candidates, {nearest.Count} nearest; of {Weeks} weeks the 20 most alike got {kept.Count(x => top.Contains(x.Id))} kept, {plain.Count(x => top.Contains(x.Id))} with nothing kept; shortest repeat kept {ShortestRepeat(kept)}");
		output.WriteLine(said);

		Assert.True(nearest.Count >= 100, said); // the owner's had 394
		Assert.True(kept.Count(x => top.Contains(x.Id)) > Weeks / 2, said);
		Assert.True(plain.Count(x => top.Contains(x.Id)) > Weeks / 2, said);
		Assert.True(ShortestRepeat(kept) > SmartCollectionStore.SpotlightWeeksKept, said);
	}

	// the Phase 3 review (item 8): a show sampled once weighed in the nearness like a habit (a third of a binge's): thirty
	// 1970s anime tried for an episode each pulled their voice actors among the nearest. A show counts toward the nearness from
	// three episodes played, or finished
	[Fact]
	public void ShowsSampledOnce_DontPullTheirPeopleAmongTheNearest()
	{
		// every show of the five 1970s anime voice actors with the most shows, tried for one episode each
		var library = SpotlightShape190.Make(scale: 9);
		var actors = library.Titles.Where(x => x.Kind == IndexKind.Series && x.Name.StartsWith("OldAnime ", StringComparison.Ordinal))
			.SelectMany(x => x.People.Where(p => p.Kind == "Actor").Select(p => p.Name)).GroupBy(x => x, StringComparer.Ordinal)
			.OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(5).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
		foreach (var show in library.Titles.Where(x => x.Kind == IndexKind.Series && x.People.Any(p => actors.Contains(p.Name))))
		{
			library.Plays[show.Id] = new SeriesPlays(1, SpotlightShape190.Now.AddDays(-2));
		}

		var candidates = SpotlightBuilder.Candidates(SpotlightShape190.Context(library));
		var nearest = candidates.Where(x => x.Nearest).OrderByDescending(x => x.Likeness ?? 0).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
		var sampledIn = nearest.Count(x => actors.Contains(x.Name));
		var top = nearest.Take(20).Count(x => actors.Contains(x.Name));
		var said = string.Create(CultureInfo.InvariantCulture, $"{nearest.Count} nearest; of the 5 actors sampled once, {sampledIn} among them, {top} in the 20 most alike");
		output.WriteLine(said);

		Assert.True(nearest.Count >= 100, said);
		Assert.Equal(0, sampledIn);
	}

	// at the owner's count of candidates (about 11,500 of 138,000 titles, the same 87 shows played): measured on 391ffc56, 8 of
	// 26 weeks tied; with the nearest only, 24 of 26 (the other 2 near the one kids' show played). The build's time is printed,
	// not checked (the census line logs it): 1.6 to 2.2 s here, before and after alike
	[Fact]
	public void AtTheOwnersCountOfCandidates_TheSpotlightIsStillOfWhatTheyWatch()
	{
		var library = SpotlightShape190.Make(scale: 9);
		var watch = System.Diagnostics.Stopwatch.StartNew();
		var candidates = SpotlightBuilder.Candidates(SpotlightShape190.Context(library));
		var built = watch.Elapsed;
		var (kept, plain) = HalfAYear(candidates);
		var said = string.Create(CultureInfo.InvariantCulture,
			$"{library.Titles.Count} titles, {candidates.Count} candidates in {built.TotalMilliseconds:0} ms; kept {kept.Count(SpotlightShape190.Tied)} of {Weeks} tied ({Say(kept)}); nothing kept {plain.Count(SpotlightShape190.Tied)} ({Say(plain)})");
		output.WriteLine(said);

		Assert.True(candidates.Count >= 10_000, said);
		Assert.True(kept.Count(SpotlightShape190.Tied) >= 22, said);
		Assert.True(plain.Count(SpotlightShape190.Tied) >= 22, said);
		Assert.True(ShortestRepeat(kept) > SmartCollectionStore.SpotlightWeeksKept, said);
	}

	// with no history, 1.8's draw week by week, kept or not; with history, every pick in the top 5 % by taste. Measured on
	// 391ffc56 (1.9's draw weighted by taste over everyone): 46 of the 52 picks under that line
	[Fact]
	public void WithNoHistory_TheDrawIs18s_WithHistory_OnlyTheNearest()
	{
		var none = SpotlightBuilder.Candidates(SpotlightShape190.Context(SpotlightShape190.Make(history: false)));
		Assert.True(none.Count >= 1_000);
		Assert.All(none, x => Assert.Equal(1, x.Taste));
		var kept = new Dictionary<string, string>(StringComparer.Ordinal);
		for (var w = 0; w < 52; w++)
		{
			var week = SpotlightWeek.Of(SpotlightShape190.Now.AddDays(7 * w));
			Assert.Equal(Draw18(none, week, SpotlightBuilder.Rounds).Id, SpotlightBuilder.Pick(none, week)!.Id);

			// kept on this PC: 1.8's too, the highest weight but last week's
			var last = kept.GetValueOrDefault(week.Before(1).Seed);
			var expected = none.Where(x => x.Id != last).OrderByDescending(x => SpotlightBuilder.Weight(week.Seed, x.Id)).ThenBy(x => x.Id, StringComparer.Ordinal).First();
			var pick = SpotlightBuilder.Pick(none, week, kept)!;
			Assert.Equal(expected.Id, pick.Id);
			kept[week.Seed] = pick.Id;
		}

		var candidates = SpotlightBuilder.Candidates(SpotlightShape190.Context(SpotlightShape190.Make()));
		var line = candidates.Select(x => x.Taste).OrderDescending().ElementAt((int)Math.Ceiling(candidates.Count * SpotlightBuilder.NearestMost) - 1);
		var (keptPicks, plainPicks) = HalfAYear(candidates);
		Assert.All(keptPicks.Concat(plainPicks), x => Assert.True(x.Taste >= line, $"{x.Id} {x.Taste:0.00} under the top 5 %'s {line:0.00}"));
	}

	// 1.8.1's draw, as it was: the highest weight by the week's seed, leaving out what last week's rounds could have drawn
	private static SpotlightCandidate Draw18(IReadOnlyList<SpotlightCandidate> candidates, SpotlightWeek week, int round)
	{
		var left = candidates;
		if (round > 0)
		{
			var before = week.Before(1);
			var out_ = Enumerable.Range(0, round).Select(r => Draw18(candidates, before, r).Id).ToHashSet(StringComparer.Ordinal);
			var fresh = candidates.Where(x => !out_.Contains(x.Id)).ToList();
			left = fresh.Count > 0 ? fresh : candidates;
		}

		return left.OrderByDescending(x => SpotlightBuilder.Weight(week.Seed, x.Id)).ThenBy(x => x.Id, StringComparer.Ordinal).First();
	}

	// "taste must beat raw size": two voice actors of anime like the anime the user plays, none of it played, one with 300
	// titles and one with 10, are as near as each other; one of 300 titles, 6 of them anime the user played and 294 anime of
	// the 1970s and 80s, stays far behind one of 10, 4 of them played, and out of the draw
	[Fact]
	public void ASubjectOfThreeHundredTitles_DoesNotWinOnVolume()
	{
		var random = new Random(300);
		var n = 900_000;
		IndexEntry Show(string name, string person, bool old, bool played) => SpotlightShape190.Title(++n, name, IndexKind.Series, old ? 1970 + random.Next(20) : 2012 + random.Next(13),
			old ? ["Animation", "Family", "Kids"] : ["Animation", "Action & Adventure", "Sci-Fi & Fantasy"], "JP", old ? "Toei Animation" : "MAPPA",
			[new IndexPerson(person, null, "Actor"), new IndexPerson($"{name} co-star", null, "Actor")], random) with { Played = played };
		IEnumerable<IndexEntry> Titles(string person, int count, Func<int, bool> old, Func<int, bool> played) =>
			Enumerable.Range(0, count).Select(i => Show(string.Create(CultureInfo.InvariantCulture, $"{person} title {i:D3}"), person, old(i), played(i)));

		const string ManyLike = "Planted voice actor of 300 like";
		const string FewLike = "Planted voice actor of 10 like";
		const string ManyFar = "Planted voice actor of 300 far";
		const string FewPlayed = "Planted voice actor of 10 played";
		List<IndexEntry> planted =
		[
			.. Titles(ManyLike, 300, _ => false, _ => false),
			.. Titles(FewLike, 10, _ => false, _ => false),
			.. Titles(ManyFar, 300, i => i % 50 != 25, i => i % 50 == 25),
			.. Titles(FewPlayed, 10, _ => false, i => i % 3 == 0),
		];
		var candidates = SpotlightBuilder.Candidates(SpotlightShape190.Context(SpotlightShape190.Make(extra: planted)));
		var (manyLike, fewLike, manyFar, fewPlayed) = (Assert.Single(candidates, x => x.Name == ManyLike), Assert.Single(candidates, x => x.Name == FewLike),
			Assert.Single(candidates, x => x.Name == ManyFar), Assert.Single(candidates, x => x.Name == FewPlayed));
		var said = string.Create(CultureInfo.InvariantCulture,
			$"300 like {manyLike.Likeness:0.000}, 10 like {fewLike.Likeness:0.000}; 300 far with 6 played {manyFar.Likeness:0.000} (taste {manyFar.Taste:0.00}, nearest {manyFar.Nearest}), 10 with 4 played {fewPlayed.Likeness:0.000}; the nearest's least {candidates.Where(x => x.Nearest).Min(x => x.Likeness):0.000}");
		output.WriteLine(said);

		Assert.InRange(manyLike.Likeness!.Value / fewLike.Likeness!.Value, 0.8, 1.25);
		Assert.True(fewPlayed.Likeness >= 2 * manyFar.Likeness, said);
		Assert.True(fewPlayed.Nearest, said);
		Assert.False(manyFar.Nearest, said);
		var (kept, plain) = HalfAYear(candidates);
		Assert.DoesNotContain(kept.Concat(plain), x => x.Id == manyFar.Id);
	}

	// how many the draw takes from: 1 to 5 % of the candidates as the evidence grows (Taste's shrink), at least NearestFewest;
	// the census line says it, a count only (rule 10). With no history, none and nothing said
	[Fact]
	public void TheNearest_AreOneToFivePercent_AndTheCensusSaysHowMany()
	{
		var context = SpotlightShape190.Context(SpotlightShape190.Make());
		var counts = new SpotlightCounts();
		var candidates = SpotlightBuilder.Candidates(context, counts);
		var nearest = candidates.Count(x => x.Nearest);
		var total = Taste.Of(context.Titles, context.Index.SeriesPlays).Total;
		var share = Math.Clamp(Taste.Smoothing / (total + Taste.Smoothing), SpotlightBuilder.NearestLeast, SpotlightBuilder.NearestMost);
		var census = counts.Census(SpotlightWeek.Of(SpotlightShape190.Now), picked: true);

		Assert.Equal(Math.Max(SpotlightBuilder.NearestFewest, (int)Math.Ceiling(share * candidates.Count)), nearest);
		Assert.InRange(nearest, 0.01 * candidates.Count, 0.05 * candidates.Count);
		Assert.True(candidates.Where(x => x.Nearest).Min(x => x.Likeness) >= candidates.Where(x => !x.Nearest).Max(x => x.Likeness), "the nearest are the most alike");
		Assert.EndsWith(string.Create(CultureInfo.InvariantCulture, $", {nearest} nearest; a pick"), census, StringComparison.Ordinal);

		var noneCounts = new SpotlightCounts();
		var none = SpotlightBuilder.Candidates(SpotlightShape190.Context(SpotlightShape190.Make(history: false)), noneCounts);
		Assert.DoesNotContain(none, x => x.Nearest || x.Likeness is not null);
		Assert.DoesNotContain("nearest", noneCounts.Census(SpotlightWeek.Of(SpotlightShape190.Now), picked: true), StringComparison.Ordinal);
	}

	// a week kept before the update (the owner's Ichiro Nagai) holds to its Sunday, wherever it ranks by taste now; next
	// Monday's is of the nearest
	[Fact]
	public void TheWeeksKeptPick_HoldsToItsSunday_TheNextIsOfTheNearest()
	{
		var candidates = SpotlightBuilder.Candidates(SpotlightShape190.Context(SpotlightShape190.Make()));
		var farthest = candidates.MinBy(x => x.Taste)!;
		var week = SpotlightWeek.Of(SpotlightShape190.Now);
		var kept = new Dictionary<string, string>(StringComparer.Ordinal) { [week.Seed] = farthest.Id };

		Assert.Equal(farthest.Id, SpotlightBuilder.Pick(candidates, week, kept)!.Id);
		var next = SpotlightBuilder.Pick(candidates, SpotlightWeek.Of(SpotlightShape190.Now.AddDays(7)), kept)!;
		Assert.NotEqual(farthest.Id, next.Id);
		Assert.True(SpotlightShape190.Tied(next), next.Id);
	}
}
