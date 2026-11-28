using System.Globalization;
using FluentFin.Core.SmartCollections;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (stream lineup): a library of the owner's shape for measuring what the ranking changes move: the survey's films
/// and shows (<see cref="MicroGenreFixture"/>), with what the score reads made like a real server's: critics' ratings that
/// follow the community's, tag counts, release dates, directors and the first three billed, about 2 % of the films released
/// in the last 12 months (rated high on few votes, as new titles are), concert films, and named titles planted from the
/// report "Made by Nixie on the owner's library" (the 2026 concert film that led Acclaimed, the Ealing retrospective that
/// opened on the wrong one of two 7.6s). Seeded: the same library every run. "Now" is the report's: 8:00 PM on Wednesday
/// 25 November 2026, ISO week 48.
/// </summary>
internal static class LibraryShape190
{
	public static readonly DateTimeOffset Now = new(2026, 11, 25, 20, 0, 0, TimeSpan.FromHours(-8));

	/// <summary>The planted titles' names, as the measurement prints them.</summary>
	public static class Names
	{
		public const string NewConcert = "Planted: a 2026 concert film rated 10, critics 100";
		public const string NewMusical = "Planted: a 2026 filmed musical rated 9.1, critics 95";
		public const string NewBiopic = "Planted: a 2026 biopic rated 8.6, critics 40";
		public const string NewDrama = "Planted: a 2026 drama rated 8.8, no critics";
		public const string NewComedy = "Planted: a 2026 comedy rated 8.4, no critics";
		public const string OldConcert = "Planted: a 2025 concert film rated 9.0";
		public const string Goodfellas = "GoodFellas";
		public const string ForrestGump = "Forrest Gump";
		public const string Interstellar = "Interstellar";
		public const string KindHearts = "Kind Hearts and Coronets";
		public const string ImagineMe = "Imagine Me & You";
	}

	/// <summary>Ealing's studio name (a curated Spotlight studio).</summary>
	public const string Ealing = "Ealing Studios";

	/// <summary>The library: <paramref name="films"/> films and <paramref name="shows"/> shows, then the planted titles.</summary>
	public static List<IndexEntry> Library(int films = MicroGenreFixture.SurveyFilms, int shows = MicroGenreFixture.SurveyShows)
	{
		var random = new Random(1909);
		var titles = new List<IndexEntry>(films + shows + 40);
		var newFrom = DateOnly.FromDateTime(Now.Date).AddDays(-360);
		foreach (var title in MicroGenreFixture.Library(films, shows))
		{
			var year = title.Year ?? 2000;
			var premiere = new DateOnly(year, 1 + random.Next(12), 1 + random.Next(28));
			var rating = title.Rating;
			var tagCount = title.Kind == IndexKind.Movie ? title.Tags.Length + random.Next(4 + Math.Max(0, year - 1950) / 4) : random.Next(3);
			float? critic = title.Critic is null || rating is null ? null : (float)Math.Clamp(Math.Round((rating.Value * 10) - 5 + (Normal(random) * 15)), 5, 100);
			var tags = title.Tags;
			var genres = title.Genres;
			if (title.Kind == IndexKind.Movie && random.Next(50) == 0)
			{
				// released in the last 12 months: rated high on few votes, few tags, critics for some
				premiere = newFrom.AddDays(random.Next(355));
				year = premiere.Year;
				rating = Math.Round(6.0 + (random.NextDouble() * 4.0), 1);
				critic = random.Next(10) < 4 ? 60 + random.Next(41) : null;
				tagCount = 2 + random.Next(7);
			}
			else if (title.Kind == IndexKind.Movie && random.Next(250) == 0)
			{
				// a concert film: rated high by its fans
				genres = ["Music", "Documentary"];
				tags = [.. tags, "concert film"];
				rating = Math.Round(7.5 + (random.NextDouble() * 2.0), 1);
				critic = random.Next(3) == 0 ? 80 + random.Next(21) : null;
			}

			titles.Add(title with
			{
				Year = year,
				PremiereDate = premiere.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
				Rating = rating,
				Critic = critic,
				TagCount = tagCount,
				Tags = tags,
				Genres = genres,
				People = title.Kind == IndexKind.Movie ? People(random) : [],
			});
		}

		// the allowlisted keywords at the owner's sizes (40 to about 2,100 titles each, most of them under the theme ceiling):
		// the generator's Zipf tags put every one of them over it
		var allowed = Keywords.Allowed.Keys.Select(x => x.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
		var tagged = titles.Select(x => x.Tags.Where(t => !allowed.Contains(t)).ToList()).ToList();
		foreach (var keyword in allowed.Order(StringComparer.Ordinal))
		{
			var size = (int)Math.Round(Math.Exp(Math.Log(40) + (random.NextDouble() * (Math.Log(2_100) - Math.Log(40)))));
			for (var i = 0; i < size; i++)
			{
				var at = random.Next(films);
				if (!tagged[at].Contains(keyword))
				{
					tagged[at].Add(keyword);
				}
			}
		}

		for (var i = 0; i < titles.Count; i++)
		{
			titles[i] = titles[i] with { Tags = [.. tagged[i]] };
		}

		titles.AddRange(Planted());
		return titles;
	}

	/// <summary>The index of <see cref="Library"/>: people in, plays in, read now.</summary>
	public static IndexSnapshot Index(IReadOnlyList<IndexEntry> titles, IReadOnlyDictionary<Guid, SeriesPlays>? plays = null) =>
		new([], titles, plays ?? new Dictionary<Guid, SeriesPlays>(), HasPeople: true, Now) { Key = "shape190" };

	/// <summary>Whether a title is a planted or generated one released in the 12 months before <see cref="Now"/>.</summary>
	public static bool IsNew(IndexEntry title) =>
		DateOnly.TryParseExact(title.PremiereDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day > DateOnly.FromDateTime(Now.Date).AddYears(-1);

	/// <summary>Whether a title is tagged a concert film.</summary>
	public static bool IsConcert(IndexEntry title) => title.Tags.Contains("concert film", StringComparer.Ordinal);

	private static IndexEntry[] Planted()
	{
		var n = 0;
		IndexEntry Film(string name, int year, string premiere, double rating, float? critic, int tagCount, string[] genres, string[]? tags = null, string[]? studios = null) => new()
		{
			Id = new Guid(0x19090000 + ++n, 0, 0, [1, 9, 0, 0, 0, 0, 0, 0]),
			Name = name,
			Kind = IndexKind.Movie,
			Year = year,
			PremiereDate = premiere,
			Rating = rating,
			Critic = critic,
			TagCount = tagCount,
			Genres = genres,
			Tags = tags ?? [],
			Studios = studios ?? [],
			Countries = ["United States of America"],
			PrimaryTag = "p",
			RunTimeTicks = TimeSpan.FromMinutes(110).Ticks,
		};

		List<IndexEntry> planted =
		[
			Film(Names.NewConcert, 2026, "2026-10-02", 10.0, 100, 6, ["Music", "Documentary"], ["concert film"]),
			Film(Names.NewMusical, 2026, "2026-09-05", 9.1, 95, 8, ["Music", "Drama"], ["musical"]),
			Film(Names.NewBiopic, 2026, "2026-04-24", 8.6, 40, 12, ["Drama", "Music", "History"], ["biography"]),
			Film(Names.NewDrama, 2026, "2026-02-13", 8.8, null, 5, ["Drama", "Romance"]),
			Film(Names.NewComedy, 2026, "2026-07-10", 8.4, null, 5, ["Comedy"]),
			Film(Names.OldConcert, 2025, "2025-06-20", 9.0, null, 4, ["Music", "Documentary"], ["concert film"]),
			Film(Names.Goodfellas, 1990, "1990-09-12", 8.5, 96, 30, ["Drama", "Crime"], ["based on a true story"]),
			Film(Names.ForrestGump, 1994, "1994-07-06", 8.5, 71, 30, ["Comedy", "Drama", "Romance"]),
			Film(Names.Interstellar, 2014, "2014-11-05", 8.4, 73, 30, ["Adventure", "Drama", "Science Fiction"], ["space"]),
		];

		// Ealing's retrospective: two 7.6s with critics and ten tags (tied on 1.8's score: the name chose), and ten more
		planted.Add(Film(Names.ImagineMe, 2006, "2006-01-27", 7.6, 34, 10, ["Comedy", "Romance"], studios: [Ealing]));
		planted.Add(Film(Names.KindHearts, 1949, "1949-06-13", 7.6, 100, 10, ["Comedy", "Crime"], studios: [Ealing]));
		string[] ealing = ["The Ladykillers", "Dead of Night", "The Lavender Hill Mob", "Scott of the Antarctic", "The Cruel Sea", "Passport to Pimlico", "Whisky Galore!",
			"The Man in the White Suit", "Went the Day Well?", "It Always Rains on Sunday"];
		for (var i = 0; i < ealing.Length; i++)
		{
			planted.Add(Film(ealing[i], 1945 + i, $"{1945 + i}-03-01", 7.5 - (i * 0.05), 80 + (i % 3 * 5), 10, ["Comedy", i % 2 == 0 ? "Crime" : "Drama"], studios: [Ealing]));
		}

		return [.. planted];
	}

	// a film's director and first three billed: 4,000 directors and 30,000 actors, a few with many titles
	private static IndexPerson[] People(Random random)
	{
		var director = (int)Math.Floor(Math.Pow(random.NextDouble(), 1.8) * 4000);
		var people = new List<IndexPerson> { new(string.Create(CultureInfo.InvariantCulture, $"Director {director:D4}"), null, "Director") };
		for (var i = 0; i < 3; i++)
		{
			var actor = (int)Math.Floor(Math.Pow(random.NextDouble(), 2.4) * 30000);
			people.Add(new(string.Create(CultureInfo.InvariantCulture, $"Actor {actor:D5}"), null, "Actor"));
		}

		return [.. people];
	}

	private static double Normal(Random random) => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}
