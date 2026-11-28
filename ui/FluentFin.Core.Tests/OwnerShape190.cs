using System.Globalization;
using FluentFin.Core.SmartCollections;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (Phase 3, stream anchors): a library of the shape of the one the owner's 1.9 test copy uses (the report
/// "Collections 2 on the owner's library", 26 November 2026, counts only): 152,849 films and 37,675 shows with thin metadata
/// (no TMDb keywords, no critics' ratings, no franchises, an episode length on 3 % of the shows, the shows' studios mostly
/// production companies, countries as ISO codes) and the summaries' matches read whole. The 87 shows with an episode played
/// are stand-ins numbered 01 to 87 (the titles are the owner's own and stay off the repository), in the four groups the
/// report sorted them into (shown in the row, in a made category, made by fix B, missed by every category), each given the
/// genres, network and summary words that put it where the report found it, its episodes and when it was last played (16 in
/// the last 30 days). 1.9 (the Phase 3 review's privacy fix): anonymised, so no real show can be recognised in it: each year
/// moved by one within its decade, episodes and episodes left of ten or more scaled by 0.8 and rounded to fives (a show's
/// share watched kept), every last play a day or more later (the last 30 days kept apart), and the networks dropped where
/// no category the test reads needs them (the shown shows' come from their summaries; the missed are in none); only the
/// shape stays. Every category of the shipped catalogue has background
/// shows built to fit its rule, at the report's sizes where it gives them. About 95 films played. Seeded: the same library
/// every run. "Now" is the report's: 10:30 PM on Thursday 26 November 2026 (ISO week 48).
/// </summary>
internal static class OwnerShape190
{
	public static readonly DateTimeOffset Now = new(2026, 11, 26, 22, 30, 0, TimeSpan.FromHours(-8));

	public const int Films = 152_849;
	public const int Shows = 37_675;

	/// <summary>The report's groups: shown in the row or This week, in a made category, made once fix B landed, missed.</summary>
	public enum Group
	{
		Shown,
		Made,
		FixB,
		Missed,
	}

	/// <summary>A played show: its facts, its summary's matches (the catalogue's ids), its episodes and its last play.</summary>
	public sealed record PlayedShow(string Name, int Year, string[] Genres, string[] Studios, string[] Words, double Rating, int Episodes, int Left, int DaysAgo, Group Group, string Country = "US");

	private static PlayedShow P(string name, int year, string genres, string studios, string words, double rating, int episodes, int left, int daysAgo, Group group, string country = "US") =>
		new(name, year, Split(genres), Split(studios), [.. Split(words).Select(x => "tv-" + x)], rating, episodes, left, daysAgo, group, country);

	private static string[] Split(string list) => list.Length == 0 ? [] : [.. list.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

	/// <summary>The 87 shows with an episode played (the report's "The key test"), newest play first within each group.</summary>
	public static readonly PlayedShow[] PlayedShows =
	[
		// shown in the row or This week (20)
		P("Played 01", 2005, "Comedy|Drama|Mystery", "", "family-sitcoms", 7.8, 30, 110, 123, Group.Shown),
		P("Played 02", 2018, "Comedy|Fantasy|Horror", "", "mockumentary-sitcoms|vampires|supernatural-comedies", 8.2, 50, 0, 53, Group.Shown),
		P("Played 03", 2006, "Animation|Comedy|Drama|Action", "", "", 8.3, 25, 20, 203, Group.Shown),
		P("Played 04", 2008, "Comedy", "", "mockumentary-sitcoms|family-sitcoms", 7.9, 80, 120, 51, Group.Shown),
		P("Played 05", 2025, "Drama|Science Fiction|Action|Adventure", "", "post-apocalyptic", 8.2, 10, 1, 43, Group.Shown),
		P("Played 06", 2026, "Comedy", "", "mockumentary-sitcoms", 7.4, 8, 0, 13, Group.Shown),
		P("Played 07", 2012, "Drama|Horror|Mystery|Thriller", "", "horror-anthologies|post-apocalyptic", 8.0, 30, 80, 60, Group.Shown),
		P("Played 08", 2012, "Animation|Action|Adventure|Children|Family|Fantasy", "", "lego", 7.6, 80, 80, 16, Group.Shown),
		P("Played 09", 2006, "Comedy", "", "mockumentary-sitcoms|workplace-comedies", 8.6, 160, 0, 48, Group.Shown),
		P("Played 10", 2018, "Anime|Action|Adventure|Fantasy", "", "demon-hunter-anime", 8.6, 45, 8, 21, Group.Shown, "JP"),
		P("Played 11", 2008, "Comedy", "", "mockumentary-sitcoms|small-town-comedies", 8.2, 100, 0, 103, Group.Shown),
		P("Played 12", 2017, "Anime|Action|Adventure|Comedy", "", "superhero-anime", 8.0, 70, 60, 38, Group.Shown, "JP"),
		P("Played 13", 2018, "Action|Comedy|Crime|Science Fiction|Drama", "", "superhero-series", 8.4, 30, 1, 61, Group.Shown),
		P("Played 14", 2021, "Comedy|Action|Children", "", "superhero-series", 6.9, 3, 55, 303, Group.Shown),
		P("Played 15", 2026, "Comedy|Drama|Mystery|Crime", "", "political-dramas", 7.4, 8, 0, 153, Group.Shown),
		P("Played 16", 2018, "Anime|Action|Adventure|Comedy|Fantasy", "", "", 8.4, 65, 70, 213, Group.Shown, "JP"),
		P("Played 17", 2009, "Animation|Action|Adventure|Science Fiction|Drama", "", "", 8.5, 30, 75, 403, Group.Shown),
		P("Played 18", 2023, "Comedy|Drama|Mystery|Fantasy", "", "teen-dramas|supernatural-comedies", 8.4, 15, 0, 26, Group.Shown),
		P("Played 19", 2018, "Drama|History|Mini-Series|Thriller", "", "true-story-mini-series", 8.7, 5, 0, 503, Group.Shown),
		P("Played 20", 2018, "Comedy|Fantasy|Drama", "", "post-apocalyptic|supernatural-comedies", 8.1, 9, 3, 83, Group.Shown, "GB"),

		// in a made category, not in the week's row (20)
		P("Played 21", 2006, "Comedy", "", "bar-restaurant-comedies|friends-comedies", 8.5, 140, 10, 1, Group.Made),
		P("Played 22", 2011, "Drama|Horror|Action|Science Fiction", "AMC", "zombies", 8.1, 50, 95, 368, Group.Made),
		P("Played 23", 2016, "Crime|Drama", "AMC", "legal-dramas", 8.7, 50, 0, 253, Group.Made),
		P("Played 24", 1998, "Animation|Comedy", "", "school-comedies", 8.4, 40, 225, 183, Group.Made),
		P("Played 25", 2016, "Comedy|Game Show", "Dave", "", 8.6, 15, 135, 58, Group.Made, "GB"),
		P("Played 26", 2011, "Anime|Action|Horror|Thriller", "", "zombies", 7.3, 10, 0, 603, Group.Made, "JP"),
		P("Played 27", 2011, "Animation|Action|Adventure|Science Fiction", "", "animated-superheroes", 8.2, 20, 20, 703, Group.Made),
		P("Played 28", 2026, "Drama|Romance", "", "lgbtq", 8.3, 6, 0, 21, Group.Made, "CA"),
		P("Played 29", 2006, "Drama|Science Fiction|Adventure", "", "time-travel|space-series", 7.9, 65, 65, 333, Group.Made, "GB"),
		P("Played 30", 2024, "Drama|Science Fiction|Adventure", "", "time-travel", 7.0, 8, 0, 263, Group.Made, "GB"),
		P("Played 31", 2021, "Drama|Romance|History", "", "", 8.1, 20, 8, 163, Group.Made),
		P("Played 32", 2018, "Science Fiction|Action|Adventure|Western|Drama", "", "space-series", 8.4, 20, 0, 453, Group.Made),
		P("Played 33", 2012, "Anime|Science Fiction|Thriller|Drama", "", "time-travel", 8.6, 20, 0, 803, Group.Made, "JP"),
		P("Played 34", 2016, "Anime|Mystery|Thriller|Drama", "", "", 8.0, 10, 0, 903, Group.Made, "JP"),
		P("Played 35", 2017, "Drama|Fantasy|Science Fiction|Horror|Mystery", "Netflix", "small-town-mysteries", 8.6, 25, 8, 343, Group.Made),
		P("Played 36", 2015, "Drama|Mystery|Fantasy", "HBO", "small-town-mysteries", 8.0, 20, 0, 1003, Group.Made),
		P("Played 37", 2018, "Drama|Science Fiction", "", "dystopian", 8.2, 25, 30, 603, Group.Made),
		P("Played 38", 2004, "Animation|Action|Adventure|Comedy", "", "animated-superheroes", 7.9, 8, 45, 1203, Group.Made),
		P("Played 39", 2026, "Western|Drama|Action|History", "Netflix", "", 7.7, 6, 0, 293, Group.Made),
		P("Played 40", 1998, "Anime|Animation|Action|Adventure|Children|Comedy|Fantasy", "", "", 7.4, 25, 935, 1103, Group.Made, "JP"),

		// made once fix B landed (a missing episode length passes a length bound) (13)
		P("Played 41", 2007, "Comedy", "NBC", "", 8.2, 90, 20, 55, Group.FixB),
		P("Played 42", 2016, "Comedy", "NBC", "workplace-comedies", 7.9, 80, 8, 43, Group.FixB),
		P("Played 43", 2014, "Comedy|Crime", "NBC", "", 8.2, 50, 70, 133, Group.FixB),
		P("Played 44", 2025, "Comedy", "NBC", "", 7.3, 15, 15, 6, Group.FixB),
		P("Played 45", 2025, "Comedy", "Prime Video", "workplace-comedies", 6.8, 3, 5, 11, Group.FixB, "MX"),
		P("Played 46", 2003, "Comedy", "", "workplace-comedies|family-sitcoms", 7.1, 15, 80, 38, Group.FixB),
		P("Played 47", 2001, "Comedy", "HBO", "", 8.3, 25, 70, 48, Group.FixB),
		P("Played 48", 2017, "Comedy|Drama", "BBC Three", "", 8.4, 10, 0, 58, Group.FixB, "GB"),
		P("Played 49", 1995, "Comedy|Romance", "NBC", "friends-comedies", 8.5, 190, 0, 403, Group.FixB),
		P("Played 50", 1994, "Comedy", "NBC", "", 8.0, 30, 180, 503, Group.FixB),
		P("Played 51", 2017, "Comedy|Fantasy", "NBC", "supernatural-comedies", 8.1, 40, 0, 703, Group.FixB),
		P("Played 52", 2018, "Crime|Drama|Thriller", "Netflix", "", 8.4, 15, 0, 368, Group.FixB),
		P("Played 53", 2007, "Crime|Drama|Mystery", "Showtime", "cop-dramas", 8.2, 75, 0, 903, Group.FixB),

		// missed by every category (34)
		P("Played 54", 2026, "Comedy", "", "friends-comedies", 7.0, 8, 0, 9, Group.Missed),
		P("Played 55", 2012, "Comedy|Romance", "", "friends-comedies", 7.9, 50, 70, 213, Group.Missed),
		P("Played 56", 2006, "Comedy", "", "", 7.8, 15, 60, 368, Group.Missed),
		P("Played 57", 2014, "Comedy|Drama", "", "family-sitcoms", 7.6, 10, 125, 403, Group.Missed),
		P("Played 58", 2019, "Comedy", "", "school-comedies", 8.4, 15, 0, 483, Group.Missed, "GB"),
		P("Played 59", 2006, "Crime|Drama|Mystery|Thriller", "", "", 8.2, 25, 240, 153, Group.Missed),
		P("Played 60", 2022, "Comedy", "", "school-comedies", 8.1, 70, 10, 21, Group.Missed),
		P("Played 61", 2008, "Comedy", "", "school-comedies|friends-comedies", 8.2, 80, 8, 303, Group.Missed),
		P("Played 62", 2015, "Action|Adventure|Drama|Science Fiction", "", "", 7.8, 30, 115, 603, Group.Missed),
		P("Played 63", 2023, "Comedy|Action|Adventure|Crime", "", "", 8.2, 15, 0, 98, Group.Missed),
		P("Played 64", 2023, "Drama|Fantasy|Mystery", "", "", 7.8, 9, 0, 453, Group.Missed),
		P("Played 65", 2023, "Science Fiction|Action|Adventure|Drama", "", "", 8.3, 20, 0, 203, Group.Missed),
		P("Played 66", 2024, "Anime|Adventure|Drama|Fantasy", "", "", 8.8, 20, 0, 123, Group.Missed, "JP"),
		P("Played 67", 2025, "Anime|Adventure|Comedy|Fantasy", "", "", 8.1, 20, 0, 253, Group.Missed, "JP"),
		P("Played 68", 2017, "Anime|Action|Children", "", "", 6.5, 4, 240, 703, Group.Missed, "JP"),
		P("Played 69", 1998, "Animation|Comedy|Kids|Horror", "", "", 8.0, 8, 35, 903, Group.Missed),
		P("Played 70", 2022, "Drama|Thriller|Mystery|Action", "", "", 7.9, 20, 0, 503, Group.Missed, "KR"),
		P("Played 71", 2023, "Drama|Mystery|Science Fiction|Thriller", "", "", 8.4, 15, 0, 283, Group.Missed),
		P("Played 72", 2026, "Science Fiction|Drama", "", "", 8.0, 9, 0, 7, Group.Missed),
		P("Played 73", 2024, "Adventure|Fantasy|Drama|Family", "", "", 7.3, 8, 0, 263, Group.Missed),
		P("Played 74", 2026, "Drama|Mystery|Sci-Fi & Fantasy", "", "", 7.9, 8, 0, 15, Group.Missed),
		P("Played 75", 2014, "Drama|Mystery|Thriller|Horror", "", "", 8.0, 15, 25, 703, Group.Missed),
		P("Played 76", 2025, "Drama|Science Fiction", "", "", 7.5, 4, 6, 4, Group.Missed),
		P("Played 77", 2026, "Comedy", "", "", 7.9, 8, 0, 103, Group.Missed),
		P("Played 78", 2025, "Comedy", "", "family-sitcoms", 7.6, 7, 0, 353, Group.Missed),
		P("Played 79", 2014, "Animation|Comedy|Action|Kids", "", "", 6.2, 10, 310, 803, Group.Missed),
		P("Played 80", 2012, "Animation|Comedy|Family|Kids", "", "", 8.2, 15, 175, 403, Group.Missed),
		P("Played 81", 2025, "Science Fiction|Action|Drama", "", "", 7.2, 4, 4, 3, Group.Missed),
		P("Played 82", 2025, "Animation|Action|Adventure|Fantasy", "", "", 7.8, 3, 7, 10, Group.Missed),
		P("Played 83", 2025, "Drama|Mystery|Science Fiction|Crime", "", "", 7.4, 5, 3, 2, Group.Missed),
		P("Played 84", 2025, "Comedy|Crime|Drama|Thriller", "", "", 7.2, 6, 0, 333, Group.Missed, "GB"),
		P("Played 85", 2023, "Drama|Mystery|Science Fiction|History", "", "", 7.6, 8, 0, 1003, Group.Missed, "DE"),
		P("Played 86", 2025, "Documentary|Comedy|Talk Show", "", "", 8.0, 4, 8, 453, Group.Missed),
		P("Played 87", 2023, "Reality|Documentary", "", "", 8.0, 2, 15, 603, Group.Missed, "AU"),
	];

	/// <summary>
	/// The shows of each category of the catalogue (titles; their own fits, before the played ones): the report's sizes where
	/// it gives them (Mockumentary Sitcoms 43, Historical Mini-Series 378, and the rest of the row), over the ceiling of 377
	/// where it says so (School Comedies, Romantic Comedy Series from the 2010s, BBC Documentary Series, and with fix B Comedies
	/// About Friends and Family Sitcoms), under the floor or empty where the studios name no network (Korean dramas, Preschool,
	/// Investigation Discovery, Apple's sci-fi); the others at a plausible size for their kind of rule. Unlisted: 60.
	/// </summary>
	private static readonly Dictionary<string, int> s_sizes = new(StringComparer.Ordinal)
	{
		["tv-nbc-sitcoms"] = 60, ["tv-abc-sitcoms"] = 70, ["tv-cbs-sitcoms"] = 80, ["tv-fox-sitcoms"] = 45, ["tv-fx-comedies"] = 25, ["tv-hbo-comedies"] = 30,
		["tv-bbc-sitcoms"] = 90, ["tv-channel-4-comedies"] = 40, ["tv-sitcoms-1980s"] = 150, ["tv-sitcoms-1990s"] = 200,
		["tv-workplace-comedies"] = 231, ["tv-friends-comedies"] = 546, ["tv-family-sitcoms"] = 1_055, ["tv-school-comedies"] = 868, ["tv-bar-restaurant-comedies"] = 60,
		["tv-mockumentary-sitcoms"] = 43, ["tv-small-town-comedies"] = 70, ["tv-horror-comedies"] = 80, ["tv-supernatural-comedies"] = 120,
		["tv-romantic-comedies-2010s"] = 498, ["tv-lgbtq"] = 150, ["tv-historical-romance"] = 40,
		["tv-adult-swim"] = 50, ["tv-fox-animation"] = 30, ["tv-netflix-adult-animation"] = 25, ["tv-animated-sitcoms-1990s"] = 60, ["tv-adult-animated-action"] = 141,
		["tv-animated-superheroes"] = 70, ["tv-superhero-series"] = 44,
		["tv-cartoon-network"] = 60, ["tv-nicktoons"] = 80, ["tv-disney-cartoons"] = 50, ["tv-disney-sitcoms"] = 40, ["tv-nickelodeon-live-action"] = 45, ["tv-preschool"] = 5, ["tv-lego"] = 12,
		["tv-cartoons-1980s"] = 200, ["tv-action-cartoons-1990s"] = 120,
		["tv-anime-1980s"] = 250, ["tv-adventure-anime-1990s"] = 150, ["tv-sci-fi-anime-1990s"] = 100,
		["tv-sports-anime"] = 60, ["tv-anime-mystery-thrillers"] = 70, ["tv-horror-anime-2010s"] = 60, ["tv-music-anime"] = 50, ["tv-martial-arts-anime"] = 40, ["tv-rated-action-anime"] = 72,
		["tv-mecha-anime"] = 90, ["tv-isekai-anime"] = 120, ["tv-school-romance-anime"] = 150, ["tv-demon-hunter-anime"] = 60, ["tv-superhero-anime"] = 20, ["tv-post-apocalyptic-anime"] = 40,
		["tv-korean-dramas"] = 6,
		["tv-hbo-dramas-2010s"] = 35, ["tv-hbo-dramas-2020s"] = 30, ["tv-amc-dramas"] = 40, ["tv-fx-dramas"] = 20, ["tv-showtime-dramas"] = 30, ["tv-netflix-crime"] = 60,
		["tv-prime-dramas"] = 25, ["tv-apple-dramas"] = 15, ["tv-cbs-crime"] = 25, ["tv-abc-dramas"] = 40, ["tv-bbc-crime"] = 70, ["tv-rated-crime-dramas"] = 50,
		["tv-medical-dramas"] = 110, ["tv-legal-dramas"] = 130, ["tv-political-dramas"] = 140, ["tv-teen-dramas"] = 250, ["tv-small-town-mysteries"] = 120,
		["tv-mob-dramas"] = 90, ["tv-cop-dramas"] = 300, ["tv-mystery-dramedies"] = 192,
		["tv-hbo-mini-series"] = 30, ["tv-historical-mini-series"] = 377, ["tv-true-story-mini-series"] = 150, ["tv-rated-mini-series"] = 200,
		["tv-syfy"] = 30, ["tv-sci-fi-dramas-1990s"] = 60, ["tv-prime-sci-fi"] = 9, ["tv-apple-sci-fi"] = 6, ["tv-hbo-fantasy"] = 15, ["tv-fantasy-adventure-dramas"] = 80,
		["tv-space-series"] = 140, ["tv-time-travel"] = 90, ["tv-post-apocalyptic"] = 38, ["tv-dystopian"] = 50, ["tv-zombies"] = 45, ["tv-vampires"] = 132, ["tv-aliens"] = 120,
		["tv-horror-anthologies"] = 92, ["tv-netflix-horror"] = 50, ["tv-supernatural-horror"] = 100, ["tv-fx-amc-horror"] = 20,
		["tv-cooking-competitions"] = 80, ["tv-dating-shows"] = 120, ["tv-british-panel-shows"] = 60,
		["tv-netflix-true-crime"] = 50, ["tv-investigation-discovery"] = 3, ["tv-bbc-docuseries"] = 708, ["tv-paranormal"] = 150,
		["tv-sports-comedies"] = 20, ["tv-sports-dramas"] = 40, ["tv-war-series"] = 120, ["tv-western-series"] = 150,
	};

	// the generic shows' genres: one, or a pair no rule of the catalogue asks for, so they fall in no category
	private static readonly string[][] s_plainShowGenres =
	[
		["Drama"], ["Drama"], ["Comedy"], ["Comedy"], ["Reality"], ["Documentary"], ["Documentary"], ["Talk Show"], ["News"], ["Soap"], ["Animation"],
		["Kids"], ["Anime"], ["Drama", "Romance"], ["Crime", "Drama"], ["Comedy", "Drama"], ["Action", "Drama"], ["Family"], ["Children"],
	];

	private static readonly (string Name, int Share)[] s_filmGenres =
	[
		("Drama", 10_958), ("Comedy", 8_962), ("Thriller", 5_336), ("Romance", 4_373), ("Action", 4_213), ("Horror", 4_072), ("Crime", 3_483),
		("Adventure", 2_899), ("Family", 2_413), ("Science Fiction", 2_370), ("Fantasy", 2_174), ("Mystery", 2_022), ("Animation", 1_984),
		("Documentary", 1_701), ("TV Movie", 1_374), ("Music", 1_203), ("History", 1_086), ("War", 910), ("Western", 589),
	];

	private static readonly (int From, int To, int Share)[] s_filmYears =
	[
		(1900, 1929, 240), (1930, 1969, 3_720), (1970, 1979, 1_517), (1980, 1989, 2_461), (1990, 1999, 3_534), (2000, 2009, 5_660), (2010, 2019, 5_565), (2020, 2026, 8_155),
	];

	private static readonly string[] s_isoCountries = ["US", "US", "US", "US", "US", "US", "GB", "CA", "FR", "JP", "DE", "AU", "IT", "KR", "ES", "HK", "IN", "MX", "CN", "SE"];

	/// <summary>What the fixture is: the index, the summaries' matches, and the played shows by name.</summary>
	public sealed class Library
	{
		public required IndexSnapshot Index { get; init; }

		public required TextMatches Text { get; init; }

		/// <summary>The played shows' entries, by name.</summary>
		public required IReadOnlyDictionary<string, IndexEntry> Played { get; init; }

		/// <summary>The played films' entries.</summary>
		public required IReadOnlyList<IndexEntry> PlayedFilms { get; init; }
	}

	private static int s_id;

	private static Guid NextId() => new(0x19D00000 + Interlocked.Increment(ref s_id), 0x0190, 0x0003, [3, 1, 9, 0, 0, 0, 0, 0]);

	/// <summary>The library (made once a test run: it is about 190,000 titles).</summary>
	public static Library Make() => s_made.Value;

	private static readonly Lazy<Library> s_made = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

	private static Library Build()
	{
		var random = new Random(26_11_2026);
		var entries = new List<IndexEntry>(Films + Shows);
		var plays = new Dictionary<Guid, SeriesPlays>();
		var text = new Dictionary<Guid, string[]?>();
		var library = new Guid(0x19D0, 0, 0, [9, 0, 0, 0, 0, 0, 0, 1]);
		var shows = new Guid(0x19D0, 0, 0, [9, 0, 0, 0, 0, 0, 0, 2]);

		// the played shows, and around each the shows that share its production company and two of its cast (More like's rare
		// features): fifteen, with its genres and no summary words
		var played = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
		foreach (var show in PlayedShows)
		{
			var circle = $"Circle {show.Name}";
			var entry = new IndexEntry
			{
				Id = NextId(),
				Name = show.Name,
				Kind = IndexKind.Series,
				LibraryId = shows,
				Year = show.Year,
				PremiereDate = $"{show.Year}-01-15",
				Genres = show.Genres,
				Studios = [.. show.Studios, circle],
				Countries = [show.Country],
				Rating = show.Rating,
				Played = show.Left == 0,
				Unplayed = show.Left,
				LastPlayed = Now.AddDays(-show.DaysAgo).AddHours(-1),
				PrimaryTag = "p",
				People = [new($"Actor {show.Name} 1", null, "Actor"), new($"Actor {show.Name} 2", null, "Actor"), new($"Actor {show.Name} 3", null, "Actor")],
			};
			entries.Add(entry);
			played[show.Name] = entry;
			plays[entry.Id] = new SeriesPlays(show.Episodes, Now.AddDays(-show.DaysAgo).AddHours(-1));
			text[entry.Id] = show.Words;
			for (var i = 0; i < 15; i++)
			{
				var alike = PlainShow(random, shows, show.Genres.Where(g => g is not ("Mini-Series" or "History" or "Western")).ToArray()) with
				{
					Studios = [circle],
					Countries = [show.Country],
					People = [new($"Actor {show.Name} {1 + (i % 3)}", null, "Actor"), Actor(random), Actor(random)],
				};
				entries.Add(alike);
			}
		}

		// each category's own shows, built to fit its rule
		foreach (var category in MicroGenres.Shipped.Categories.Where(x => x.Kind == IndexKind.Series))
		{
			var size = s_sizes.GetValueOrDefault(category.Id, 60);
			var rule = category.Rule;
			for (var i = 0; i < size; i++)
			{
				var genres = rule.GenresAll.Concat(rule.GenresAny.Count > 0 ? [rule.GenresAny[i % rule.GenresAny.Count]] : []).Distinct().ToArray();
				if (genres.Length == 0)
				{
					genres = ["Drama"];
				}

				var from = rule.Years.From ?? (random.Next(4) == 0 ? 1965 : 2000);
				var to = rule.Years.To ?? 2026;
				int? minutes = random.NextDouble() < 0.03 ? Inside(random, rule.LengthMinutes) : null;
				var rating = rule.CommunityMin is { } least ? Math.Round(least + (random.NextDouble() * (9.4 - least)), 1) : Math.Round(5.6 + (random.NextDouble() * 2.6), 1);
				var studio = rule.StudiosAny.Count > 0 ? rule.StudiosAny[i % rule.StudiosAny.Count] : $"Productions {random.Next(9_000)}";
				var entry = new IndexEntry
				{
					Id = NextId(),
					Name = string.Create(CultureInfo.InvariantCulture, $"{category.Name} {i + 1}"),
					Kind = IndexKind.Series,
					LibraryId = shows,
					Year = from + random.Next(Math.Max(1, to - from + 1)),
					Genres = genres,
					Studios = [studio],
					Countries = [studio.StartsWith("BBC", StringComparison.Ordinal) || studio is "Channel 4" or "E4" or "Dave" ? "GB" : "US"],
					Rating = rating,
					Unplayed = 6 + random.Next(80),
					RunTimeTicks = minutes is { } m ? TimeSpan.FromMinutes(m).Ticks : null,
					PrimaryTag = "p",
					People = [Actor(random), Actor(random), Actor(random)],
				};
				entries.Add(entry);
				if (rule.WaitsForText)
				{
					text[entry.Id] = [category.Id];
				}
			}
		}

		// the rest of the shows: genres no rule asks for
		for (var made = entries.Count; made < Shows; made++)
		{
			entries.Add(PlainShow(random, shows, s_plainShowGenres[random.Next(s_plainShowGenres.Length)]));
		}

		// the films: the survey's genres and decades at 152,849, ISO countries, no keywords, no critics; about 95 played
		var filmTotal = s_filmGenres.Sum(x => x.Share);
		var yearTotal = s_filmYears.Sum(x => x.Share);
		var playedFilms = new List<IndexEntry>();
		for (var i = 0; i < Films; i++)
		{
			var genres = new List<string>();
			var count = 1 + (random.Next(10) < 6 ? random.Next(2) : random.Next(3));
			while (genres.Count < count)
			{
				var roll = random.Next(filmTotal);
				foreach (var (name, share) in s_filmGenres)
				{
					if ((roll -= share) < 0)
					{
						if (!genres.Contains(name))
						{
							genres.Add(name);
						}

						break;
					}
				}
			}

			var yearRoll = random.Next(yearTotal);
			var year = 2000;
			foreach (var (from, to, share) in s_filmYears)
			{
				if ((yearRoll -= share) < 0)
				{
					year = from + random.Next(to - from + 1);
					break;
				}
			}

			// a viewer of comedies and action from 1995 to 2020: about 95 films finished, a few lately
			var watched = year is >= 1995 and <= 2020 && (genres.Contains("Comedy") || genres.Contains("Action") || genres.Contains("Horror")) && random.Next(320) == 0;
			var film = new IndexEntry
			{
				Id = NextId(),
				Name = string.Create(CultureInfo.InvariantCulture, $"Film {i}"),
				Kind = IndexKind.Movie,
				LibraryId = library,
				Year = year,
				Genres = [.. genres],
				Countries = random.NextDouble() < 0.98 ? [s_isoCountries[random.Next(s_isoCountries.Length)]] : [],
				Studios = [$"Pictures {random.Next(40_000)}"],
				RunTimeTicks = TimeSpan.FromMinutes(random.Next(20) == 0 ? 10 + random.Next(30) : 78 + random.Next(30) + random.Next(40)).Ticks,
				Rating = Math.Round(3.5 + (random.NextDouble() * 2.5) + (random.NextDouble() * 2.5), 1),
				Played = watched,
				PlayCount = watched ? 1 : 0,
				LastPlayed = watched ? Now.AddDays(-(30 + random.Next(1_500))) : null,
				PrimaryTag = random.Next(25) == 0 ? null : "p",
				People = [new($"Director {random.Next(20_000)}", null, "Director"), Actor(random), Actor(random), Actor(random)],
			};
			entries.Add(film);
			if (watched)
			{
				playedFilms.Add(film);
			}
		}

		// one film watched four days ago (the report's This week had a More like of a film)
		var watchedFilm = entries.First(x => x.Kind == IndexKind.Movie && x.Played);
		var at = entries.IndexOf(watchedFilm);
		entries[at] = watchedFilm with { Name = "Played film", Year = 2001, Genres = ["Comedy"], LastPlayed = Now.AddDays(-4) };
		playedFilms[0] = entries[at];

		var index = new IndexSnapshot([new IndexLibrary(library, "Movies", IndexKind.Movie), new IndexLibrary(shows, "Shows", IndexKind.Series)], entries, plays, HasPeople: true, Now)
		{
			Key = "owner-shape-190",
		};
		var matches = new TextMatches(index.Key, "owner-shape", Now, text) { CompleteFor = new HashSet<IndexKind> { IndexKind.Series } };
		return new Library { Index = index, Text = matches, Played = played, PlayedFilms = playedFilms };
	}

	private static IndexEntry PlainShow(Random random, Guid library, string[] genres)
	{
		var year = genres.Contains("Comedy") || genres.Contains("Anime") ? 2000 + random.Next(27) : 1960 + random.Next(67);
		return new IndexEntry
		{
			Id = NextId(),
			Name = string.Create(CultureInfo.InvariantCulture, $"Show {s_id}"),
			Kind = IndexKind.Series,
			LibraryId = library,
			Year = year,
			Genres = genres.Length == 0 ? ["Drama"] : genres,
			Studios = [$"Productions {random.Next(9_000)}"],
			Countries = [s_isoCountries[random.Next(s_isoCountries.Length)]],
			Rating = Math.Round(4.6 + (random.NextDouble() * 1.8) + (random.NextDouble() * 1.8), 1),
			Unplayed = 6 + random.Next(80),
			RunTimeTicks = random.NextDouble() < 0.03 ? TimeSpan.FromMinutes(22 + random.Next(40)).Ticks : null,
			PrimaryTag = "p",
			People = [Actor(random), Actor(random), Actor(random)],
		};
	}

	private static IndexPerson Actor(Random random) => new(string.Create(CultureInfo.InvariantCulture, $"Actor {(int)Math.Floor(Math.Pow(random.NextDouble(), 2.2) * 60_000):D5}"), null, "Actor");

	// a length inside a rule's bound (none: a sitcom's or a drama's)
	private static int Inside(Random random, MicroRange range)
	{
		if (range.IsOpen)
		{
			return random.Next(2) == 0 ? 22 : 45;
		}

		var least = range.From ?? 10;
		var most = range.To ?? least + 30;
		return least + random.Next(Math.Max(1, most - least + 1));
	}

	/// <summary>The played shows in at least one of <paramref name="categories"/>, by name.</summary>
	public static IReadOnlyList<string> Covered(Library library, IEnumerable<SmartCollection> categories)
	{
		var shown = new HashSet<Guid>(categories.SelectMany(x => x.ItemIds));
		return [.. library.Played.Where(x => shown.Contains(x.Value.Id)).Select(x => x.Key)];
	}
}
