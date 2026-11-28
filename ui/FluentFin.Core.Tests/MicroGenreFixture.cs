using System.Globalization;
using System.Text;
using FluentFin.Core.SmartCollections;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (stream match): a library of the Collections 2 survey's shape (the owner's index, counts only: 25,877 films and
/// 4,975 shows), its genres, countries, decades, runtimes and ratings in the survey's shares, and tags as the index will keep
/// them once the matchable keywords are kept (films 8.9 a tagged film, shows few). Plus generated catalogues of rules of the
/// shapes the cat stream writes. Seeded: the same library every run.
/// </summary>
internal static class MicroGenreFixture
{
	public const int SurveyFilms = 25_877;
	public const int SurveyShows = 4_975;

	// the survey's §2 counts
	public static readonly (string Name, int Count)[] FilmGenres =
	[
		("Drama", 10_958), ("Comedy", 8_962), ("Thriller", 5_336), ("Romance", 4_373), ("Action", 4_213), ("Horror", 4_072), ("Crime", 3_483),
		("Adventure", 2_899), ("Family", 2_413), ("Science Fiction", 2_370), ("Fantasy", 2_174), ("Mystery", 2_022), ("Animation", 1_984),
		("Documentary", 1_701), ("TV Movie", 1_374), ("Music", 1_203), ("History", 1_086), ("War", 910), ("Western", 589),
	];

	public static readonly (string Name, int Count)[] ShowGenres =
	[
		("Drama", 2_048), ("Comedy", 1_945), ("Animation", 1_768), ("Anime", 1_281), ("Action", 1_241), ("Adventure", 1_105), ("Fantasy", 1_099),
		("Romance", 817), ("Crime", 755), ("Science Fiction", 724), ("Reality", 654), ("Mystery", 641), ("Thriller", 606), ("Family", 596),
		("Documentary", 580), ("Children", 386), ("Mini-Series", 323), ("Horror", 293), ("History", 200), ("Game Show", 198),
	];

	// first countries; the rest of the 97.9 % of films with one spread over 104 more
	public static readonly (string Name, int Count)[] Countries =
	[
		("United States of America", 14_823), ("United Kingdom", 2_629), ("Canada", 1_265), ("France", 1_022), ("Japan", 864), ("Germany", 571),
		("Australia", 482), ("Italy", 336), ("South Korea", 298), ("Denmark", 260), ("Spain", 241), ("Hong Kong", 225),
	];

	// years: 1900-29 the rest, 1930-69 3,720, then each decade's count
	private static readonly (int From, int To, int Count)[] s_years =
	[
		(1900, 1929, 240), (1930, 1969, 3_720), (1970, 1979, 1_517), (1980, 1989, 2_461), (1990, 1999, 3_534), (2000, 2009, 5_660), (2010, 2019, 5_565),
		(2020, 2025, 8_155),
	];

	/// <summary>The keywords the library's tags are drawn from: the allowlist, the seasonal ones, and 300 of the matchable tier's shape.</summary>
	public static readonly string[] Vocabulary =
	[
		.. Keywords.Allowed.Keys.Select(x => x.ToLowerInvariant()), .. Keywords.Seasonal.Select(x => x.ToLowerInvariant()),
		.. Enumerable.Range(1, 300).Select(i => string.Create(CultureInfo.InvariantCulture, $"matchable {i:D3}")),
	];

	/// <summary>The matchable keywords of <see cref="Vocabulary"/> (a validator's list for generated catalogues).</summary>
	public static IReadOnlySet<string> Matchable { get; } = new HashSet<string>(Vocabulary, StringComparer.Ordinal);

	private static readonly string[] s_studioHead = [.. Enumerable.Range(1, 200).Select(i => $"Studio {i}"), "HBO", "A24", "Pixar Animation Studios"];
	private static readonly string[] s_networks = [.. Enumerable.Range(1, 300).Select(i => $"Network {i}"), "HBO", "BBC One", "AMC"];

	/// <summary>A library of <paramref name="films"/> films and <paramref name="shows"/> shows of the survey's shape (each title once).</summary>
	public static List<IndexEntry> Library(int films = SurveyFilms, int shows = SurveyShows, int seed = 190)
	{
		var random = new Random(seed);
		var library = new Guid(seed, 0, 0, new byte[8]);
		var titles = new List<IndexEntry>(films + shows);
		var tail = 0;
		for (var i = 0; i < films; i++)
		{
			titles.Add(new IndexEntry
			{
				Id = Id(seed, i),
				Name = $"Film {i}",
				Kind = IndexKind.Movie,
				LibraryId = library,
				Tmdb = (i + 1).ToString(CultureInfo.InvariantCulture),
				Year = Year(random),
				Genres = Genres(random, FilmGenres, SurveyFilms),
				Countries = random.NextDouble() < 0.979 ? FilmCountries(random) : [],
				Tags = random.NextDouble() < 0.917 ? Tags(random, 1 + Geometric(random, 8.0)) : [],
				Studios = random.NextDouble() < 0.969 ? [.. Enumerable.Range(0, 1 + random.Next(3)).Select(_ => random.Next(2) == 0 ? Zipf(random, s_studioHead) : $"Company {tail++}")] : [],
				RunTimeTicks = TimeSpan.FromMinutes(random.Next(20) == 0 ? 8 + random.Next(35) : 75 + random.Next(30) + random.Next(30) + random.Next(40)).Ticks,
				Rating = random.NextDouble() < 0.998 ? Math.Round(3 + (random.NextDouble() * 3.2) + (random.NextDouble() * 3.2), 1) : null,
				Critic = random.NextDouble() < 0.648 ? random.Next(10, 101) : null,
				Played = random.Next(400) == 0,
				PrimaryTag = random.Next(30) == 0 ? null : "p",
			});
		}

		for (var i = 0; i < shows; i++)
		{
			// episode lengths in the survey's shares: 2,642 of 35 minutes or less, 1,372 of 36 to 50, 926 over 50, the rest none
			var roll = random.NextDouble() * SurveyShows;
			int? minutes = roll < 2_642 ? 20 + random.Next(16) : roll < 2_642 + 1_372 ? 36 + random.Next(15) : roll < 2_642 + 1_372 + 926 ? 51 + random.Next(30) : null;
			titles.Add(new IndexEntry
			{
				Id = Id(seed, films + i),
				Name = $"Show {i}",
				Kind = IndexKind.Series,
				LibraryId = library,
				Tmdb = (i + 1).ToString(CultureInfo.InvariantCulture),
				Year = Year(random),
				Genres = Genres(random, ShowGenres, SurveyShows),
				Tags = random.NextDouble() < 0.30 ? Tags(random, 1 + random.Next(2)) : [],
				Studios = random.NextDouble() < 0.986 ? [.. Enumerable.Range(0, random.Next(5) == 0 ? 2 : 1).Select(_ => Zipf(random, s_networks))] : [],
				RunTimeTicks = minutes is { } m ? TimeSpan.FromMinutes(m).Ticks : null,
				Rating = random.NextDouble() < 0.869 ? Math.Round(4 + (random.NextDouble() * 2.6) + (random.NextDouble() * 2.6), 1) : null,
				Played = random.Next(100) == 0,
				Unplayed = 1 + random.Next(80),
				PrimaryTag = "p",
			});
		}

		return titles;
	}

	/// <summary>
	/// <paramref name="count"/> rules of the shapes the catalogue will have (about three films a show): genres with a decade,
	/// a country, keywords, a length or a rating; shows by genres, episode length, decade and network. Each passes the
	/// validator's checks (its keywords are <see cref="Matchable"/>).
	/// </summary>
	public static List<MicroGenre> Rules(int count = 300, int seed = 19)
	{
		var random = new Random(seed);
		var rules = new List<MicroGenre>(count);
		for (var i = 0; i < count; i++)
		{
			var films = i % 4 != 3;
			var genres = films ? FilmGenres : ShowGenres;
			var all = Enumerable.Range(0, 1 + random.Next(2)).Select(_ => Weighted(random, genres)).Distinct().ToList();
			var rule = new MicroGenreRule
			{
				GenresAll = all,
				GenresAny = random.Next(8) == 0 ? [.. Enumerable.Range(0, 2).Select(_ => Weighted(random, genres)).Distinct().Except(all)] : [],
				GenresNone = random.Next(2) == 0 ? (films ? new[] { "Animation", "Documentary" } : ["Children", "Family"]).Except(all).ToList() : [],
				Years = random.Next(5) < 3 ? Decade(random) : default,
				CountriesAny = films && random.Next(10) < 3 ? [.. Enumerable.Range(0, 1 + random.Next(2)).Select(_ => Countries[random.Next(Countries.Length)].Name).Distinct()] : [],
				KeywordsAny = random.Next(10) < (films ? 4 : 1) ? [.. Enumerable.Range(0, 1 + random.Next(3)).Select(_ => Vocabulary[random.Next(100)]).Distinct()] : [],
				KeywordsNone = random.Next(10) == 0 ? [Vocabulary[random.Next(100)]] : [],
				StudiosAny = films ? random.Next(20) == 0 ? [.. Enumerable.Range(0, 2 + random.Next(3)).Select(_ => s_studioHead[random.Next(40)])] : []
					: random.Next(10) < 3 ? [.. Enumerable.Range(0, 1 + random.Next(4)).Select(_ => s_networks[random.Next(40)])] : [],
				LengthMinutes = films
					? random.Next(7) == 0 ? random.Next(2) == 0 ? new MicroRange(null, 90) : new MicroRange(150, null) : default
					: random.Next(5) < 3 ? random.Next(3) switch { 0 => new MicroRange(null, 35), 1 => new MicroRange(36, 50), _ => new MicroRange(40, null) } : default,
				CommunityMin = random.Next(7) == 0 ? 7 : null,
				CriticMin = films && random.Next(10) == 0 ? 80 : null,
				TextAny = random.Next(films ? 50 : 10) == 0 ? ["office", "boss"] : [],
			};
			var id = string.Create(CultureInfo.InvariantCulture, $"rule-{i:D3}");
			rules.Add(new MicroGenre(id, $"Generated Category {i}", films ? IndexKind.Movie : IndexKind.Series, $"family-{i % 37}", null, rule, $"Generated rule {i}"));
		}

		return rules;
	}

	/// <summary>A catalogue file of the shipped format holding <paramref name="categories"/> (JSON objects as written).</summary>
	public static Stream File(params string[] categories) =>
		new MemoryStream(Encoding.UTF8.GetBytes($$"""{ "version": 1, "categories": [ {{string.Join(",\n", categories)}} ] }"""));

	/// <summary>A fixture file of the test project (Fixtures\MicroGenres), opened.</summary>
	public static Stream Fixture(string name) =>
		System.IO.File.OpenRead(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin.Core.Tests", "Fixtures", "MicroGenres", name));

	/// <summary>The test catalogue (Fixtures\MicroGenres\test-catalogue.json), checked against the test matchable list.</summary>
	public static MicroGenreCatalogue TestCatalogue() => MicroGenres.Load(Fixture("test-catalogue.json"), TestMatchable());

	/// <summary>The test matchable list (Fixtures\MicroGenres\test-keywords-matchable.json) beside the allowlist.</summary>
	public static IReadOnlySet<string> TestMatchable() => MicroGenres.LoadMatchable(Fixture("test-keywords-matchable.json"));

	private static Guid Id(int seed, int i) => new(i + 1, (short)(seed & 0x7fff), 0x190, 0, 0, 0, 0, 0, 0, 0, 0);

	private static int Year(Random random)
	{
		var roll = random.Next(s_years.Sum(x => x.Count));
		foreach (var (from, to, count) in s_years)
		{
			if (roll < count)
			{
				return from + random.Next(to - from + 1);
			}

			roll -= count;
		}

		return 2024;
	}

	// each genre with its survey share; a title with none gets the commonest
	private static string[] Genres(Random random, (string Name, int Count)[] genres, int of)
	{
		var picked = genres.Where(g => random.NextDouble() < g.Count / (double)of).Select(g => g.Name).ToArray();
		return picked.Length > 0 ? picked : [genres[random.Next(2)].Name];
	}

	private static string[] FilmCountries(Random random)
	{
		var first = random.NextDouble() * SurveyFilms * 0.979;
		string country = $"Country {random.Next(104)}";
		foreach (var (name, count) in Countries)
		{
			if (first < count)
			{
				country = name;
				break;
			}

			first -= count;
		}

		return random.Next(4) == 0 ? [country, Countries[random.Next(Countries.Length)].Name] : [country];
	}

	private static string[] Tags(Random random, int count) => [.. Enumerable.Range(0, count).Select(_ => Zipf(random, Vocabulary)).Distinct()];

	private static int Geometric(Random random, double mean) => (int)Math.Min(40, Math.Floor(-Math.Log(1 - random.NextDouble()) * mean));

	// rank r drawn with weight 1/r
	private static string Zipf(Random random, string[] items)
	{
		var h = Math.Log(items.Length) + 0.5772;
		var r = (int)Math.Floor(Math.Exp(random.NextDouble() * h));
		return items[Math.Clamp(r - 1, 0, items.Length - 1)];
	}

	private static string Weighted(Random random, (string Name, int Count)[] items)
	{
		var roll = random.Next(items.Sum(x => x.Count));
		foreach (var (name, count) in items)
		{
			if (roll < count)
			{
				return name;
			}

			roll -= count;
		}

		return items[0].Name;
	}

	// a whole decade, or one of five that straddle two
	private static MicroRange Decade(Random random)
	{
		var decade = 1930 + (10 * random.Next(10));
		return random.Next(5) == 0 ? new MicroRange(decade + 5, decade + 14) : new MicroRange(decade, decade + 9);
	}
}
