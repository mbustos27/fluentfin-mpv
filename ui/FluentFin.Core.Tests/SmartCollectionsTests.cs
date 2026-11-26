using System.Diagnostics;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Builders;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>Nixie 1.6 (smart collections phase 1): the builders on fixture libraries, the franchise names, the index read and its budget.</summary>
public sealed class SmartCollectionsTests
{
	private static readonly DateTimeOffset November = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
	private static int s_next;

	private static IndexEntry Film(string name, int year = 2000, string? tmdbCollection = null, string[]? genres = null, string[]? tags = null,
		string[]? studios = null, string[]? countries = null, double? rating = null, double? minutes = 120, bool played = false, IndexPerson[]? people = null,
		string? tmdb = null, bool poster = true, int tagCount = 0, float? critic = null) => new()
		{
			Id = Guid.Parse($"{Interlocked.Increment(ref s_next):D8}-0000-0000-0000-000000000001"),
			Name = name,
			Kind = IndexKind.Movie,
			Year = year,
			TmdbCollection = tmdbCollection,
			Genres = genres ?? [],
			Tags = tags ?? [],
			Studios = studios ?? [],
			Countries = countries ?? [],
			Rating = rating,
			RunTimeTicks = minutes is { } m ? TimeSpan.FromMinutes(m).Ticks : null,
			Played = played,
			People = people ?? [],
			PrimaryTag = poster ? "p" : null,
			Tmdb = tmdb, // 1.7.3
			TagCount = tagCount, // 1.7.3
			Critic = critic, // 1.7.3
		};

	private static IndexEntry Show(string name, string[]? studios = null, int? unplayed = 5, string[]? genres = null) => new()
	{
		Id = Guid.Parse($"{Interlocked.Increment(ref s_next):D8}-0000-0000-0000-000000000002"),
		Name = name,
		Kind = IndexKind.Series,
		Year = 2010,
		Studios = studios ?? [],
		Unplayed = unplayed,
		Genres = genres ?? [],
	};

	private static IndexSnapshot Index(IEnumerable<IndexEntry> entries, Dictionary<Guid, SeriesPlays>? plays = null) =>
		new([], [.. entries], plays ?? [], HasPeople: true, November);

	private static List<SmartCollection> Built(ISmartCollectionBuilder builder, IndexSnapshot index, DateTimeOffset? now = null, IReadOnlySet<string>? boxed = null) =>
		[.. builder.Build(new BuildContext(index, now ?? November, boxed ?? new HashSet<string>()))];

	[Fact]
	public void AFranchise_IsItsFilmsInReleaseOrder_NamedFromThem()
	{
		var reloaded = Film("The Matrix Reloaded", 2003, "2344");
		var matrix = Film("The Matrix", 1999, "2344");
		var alone = Film("Night of the Living Dead", 1968, "261590");

		var franchise = Assert.Single(Built(new FranchiseBuilder(), Index([reloaded, matrix, alone, Film("Amélie", 2001)])));

		Assert.Equal("The Matrix", franchise.Title);
		Assert.Equal([matrix.Id, reloaded.Id], franchise.ItemIds);
		Assert.True(franchise.HasOwnOrder);
		Assert.Equal("2344", franchise.TmdbCollection);
		Assert.Equal("Made by Nixie · Franchise", franchise.Eyebrow);
		Assert.Equal("The Matrix, made by Nixie, franchise, 2 films", franchise.AutomationName);
	}

	[Fact]
	public void AFranchiseTheAdminBoxed_ShowsOnce_AndItsBoxSetHearsWhatItLeavesOut()
	{
		var a = Film("Copper and Glass", 2001, "9001");
		var b = Film("Copper and Glass II", 2004, "9001");
		var loose = Film("Copper and Glass: Origins", 2010, "9001");
		var index = Index([a, b, loose]);

		Assert.Empty(Built(new FranchiseBuilder(), index, boxed: new HashSet<string> { "9001" }));
		Assert.Equal([loose.Id], FranchiseBuilder.AlsoInLibrary(index, "9001", [a.Id, b.Id]).Select(x => x.Id));
		Assert.Empty(FranchiseBuilder.AlsoInLibrary(index, null, [a.Id]));
	}

	[Theory]
	[InlineData("The Matrix|The Matrix Reloaded|The Matrix Revolutions", "The Matrix")]
	[InlineData("Harry Potter and the Philosopher's Stone|Harry Potter and the Chamber of Secrets", "Harry Potter")]
	[InlineData("The Lord of the Rings: The Fellowship of the Ring|The Lord of the Rings: The Two Towers", "The Lord of the Rings")]
	[InlineData("The Mummy|The Mummy Returns|The Mummy: Tomb of the Dragon Emperor", "The Mummy")]
	[InlineData("Toy Story|Toy Story 2|Toy Story 3", "Toy Story")]
	[InlineData("Alien|Aliens|Alien³", "Alien films")]
	[InlineData("The Godfather|The Godfather Part II", "The Godfather")]
	[InlineData("The Dark Knight|The Dark Knight Rises", "The Dark Knight")]
	[InlineData("A Quiet Place|A Quiet Place Part II", "A Quiet Place")]
	[InlineData("The Thing|The Fly", "The Thing films")]
	public void FranchiseNames_FromTheFilms(string films, string name)
	{
		var list = films.Split('|').Select((x, i) => Film(x, 1979 + i)).Reverse().ToList();
		Assert.Equal(name, FranchiseNames.Name(list));
	}

	[Fact]
	public void Decades_NeedTen_AndKeepFilmsAndShowsApart()
	{
		var eighties = Enumerable.Range(0, 10).Select(i => Film($"F{i}", 1980 + i)).ToList();
		var nineties = Enumerable.Range(0, 9).Select(i => Film($"G{i}", 1990 + i)).ToList();

		var decade = Assert.Single(Built(new DecadeBuilder(), Index([.. eighties, .. nineties])));

		Assert.Equal("1980s films", decade.Title);
		Assert.Equal("decade:movie:1980", decade.Id);
		Assert.Equal(10, decade.ItemIds.Count);
		Assert.False(decade.HasOwnOrder);
	}

	[Fact]
	public void ShowStudios_On1011And12_AreStudiosAndNetworks()
	{
		// 10.11 sends networks only; 12 networks and production companies, mixed
		var on1011 = Index([Show("A", ["CBS"]), Show("B", ["CBS"]), Show("C", ["CBS"]), Show("D", ["TV Tokyo"])]);
		var on12 = Index([Show("A", ["CBS", "Cayuga Productions"]), Show("B", ["CBS"]), Show("C", ["CBS", "Cayuga Productions"]), Show("D", ["Cayuga Productions"])]);

		var old = Assert.Single(Built(new StudioBuilder(IndexKind.Series), on1011));
		Assert.Equal(("CBS", SmartCollectionKind.Network), (old.Title, old.Kind));
		Assert.Equal("Studios and networks", SmartCollectionKinds.Plural(old.Kind));
		Assert.Equal(["CBS", "Cayuga Productions"], Built(new StudioBuilder(IndexKind.Series), on12).Select(x => x.Title).Order(StringComparer.Ordinal));
		Assert.Equal("3 shows", old.CountText);
	}

	[Fact]
	public void FilmStudios_NeedFive_AndMergeCaseAndPunctuation()
	{
		var films = Enumerable.Range(0, 5).Select(i => Film($"F{i}", studios: [i % 2 == 0 ? "Walt Disney Pictures" : "Walt Disney Pictures."])).ToList();
		films.AddRange(Enumerable.Range(0, 5).Select(i => Film($"A{i}", studios: ["Walt Disney Animation Studios"])));
		films.AddRange(Enumerable.Range(0, 4).Select(i => Film($"B{i}", studios: ["Blender Foundation"])));

		var studios = Built(new StudioBuilder(IndexKind.Movie), Index(films));

		Assert.Equal(["Walt Disney Animation Studios", "Walt Disney Pictures"], studios.Select(x => x.Title).Order());
	}

	[Fact]
	public void GenreMixes_FollowTheThresholds()
	{
		var films = new List<IndexEntry>();
		// Comedy·Horror: 8 of 20 horror films are comedies (40 %): a mix
		films.AddRange(Enumerable.Range(0, 8).Select(i => Film($"CH{i}", genres: ["Comedy", "Horror"])));
		films.AddRange(Enumerable.Range(0, 12).Select(i => Film($"H{i}", genres: ["Horror"])));
		films.AddRange(Enumerable.Range(0, 12).Select(i => Film($"C{i}", genres: ["Comedy"])));
		// Animation·Family: 9 of 10 animations are family films (90 %): says nothing new
		films.AddRange(Enumerable.Range(0, 9).Select(i => Film($"AF{i}", genres: ["Animation", "Family"])));
		films.AddRange(Enumerable.Range(0, 1).Select(i => Film($"A{i}", genres: ["Animation"])));
		films.AddRange(Enumerable.Range(0, 10).Select(i => Film($"F{i}", genres: ["Family"])));
		// Drama·War: 7 in both: too few
		films.AddRange(Enumerable.Range(0, 7).Select(i => Film($"DW{i}", genres: ["Drama", "War"])));
		films.AddRange(Enumerable.Range(0, 10).Select(i => Film($"D{i}", genres: ["Drama"])));
		films.AddRange(Enumerable.Range(0, 10).Select(i => Film($"W{i}", genres: ["War"])));

		var mix = Assert.Single(Built(new GenreMixBuilder(), Index(films)));

		Assert.Equal("Comedy · Horror", mix.Title);
		Assert.Equal(8, mix.ItemIds.Count);
		Assert.Equal("Both Comedy and Horror", mix.Why); // 1.8 (P1-4): no "Your"
	}

	[Fact]
	public void Under90Minutes_IsFeaturesOf60To90_NotWatched_BestFirst()
	{
		// 1.7.3 (P0-4): 1.6's "Short films" took every film under 90 minutes, shortest first: its first posters were shorts
		var under = Film("Under", minutes: 89 + 59 / 60.0, rating: 6.0);
		var at = Film("At", minutes: 90, rating: 9.0);
		var sixty = Film("Sixty", minutes: 60, rating: 7.0);
		var best = Film("Best", minutes: 75, rating: 8.4);
		var unrated = Film("Unrated", minutes: 80);
		var none = Film("None", minutes: null, rating: 9.0);
		var twenty = Film("Twenty", minutes: 20, rating: 9.5);
		var ninetyFive = Film("Ninety-five", minutes: 95, rating: 9.5);
		var watched = Film("Watched", minutes: 85, rating: 9.5, played: true);
		var show = Show("A show") with { RunTimeTicks = TimeSpan.FromMinutes(70).Ticks, Rating = 9.0 };

		var shorts = Assert.Single(Built(new ShortFilmsBuilder(), Index([under, at, sixty, best, unrated, none, twenty, ninetyFive, watched, show])));

		Assert.Equal([best.Id, sixty.Id, under.Id, unrated.Id], shorts.ItemIds); // no 20, 95 or 90 minutes, nothing watched, no show; unrated last
		Assert.Equal(("pick:short-films", "Under 90 minutes"), (shorts.Id, shorts.Title)); // the id stays (Settings, pins, Home's saved rows)
		Assert.Equal("4 films", shorts.CountText);
	}

	[Fact]
	public void Under90Minutes_And_Acclaimed_Show20()
	{
		var films = Enumerable.Range(0, 30).Select(i => Film($"F{i:D2}", minutes: 61 + i, rating: 7.5 + (i / 20.0), tagCount: 10)).ToList(); // backed (1.7.3)

		var all = SmartCollectionCatalog.BuildAll(Index(films), November);

		var under = all.Single(x => x.Kind == SmartCollectionKind.ShortFilms);
		var acclaimed = all.Single(x => x.Kind == SmartCollectionKind.HighlyRated);
		Assert.Equal(20, under.ItemIds.Count); // 29 qualify (61 to 89 minutes)
		Assert.Equal(20, acclaimed.ItemIds.Count); // 30 qualify
		Assert.Equal(films[29].Id, acclaimed.ItemIds[0]); // the best rated first
		Assert.Equal(films[28].Id, under.ItemIds[0]); // the best of 89 minutes or less
	}

	[Fact]
	public void UnfinishedSeries_AreShowsLeftForOverAMonth()
	{
		var left = Show("Left a while ago");
		var recent = Show("Watched last week");
		var done = Show("Finished", unplayed: 0);
		var never = Show("Never started");
		var plays = new Dictionary<Guid, SeriesPlays>
		{
			[left.Id] = new(3, November.AddDays(-31)),
			[recent.Id] = new(3, November.AddDays(-7)),
			[done.Id] = new(10, November.AddDays(-90)),
		};

		var unfinished = Assert.Single(Built(new UnfinishedSeriesBuilder(), Index([left, recent, done, never], plays)));

		Assert.Equal([left.Id], unfinished.ItemIds);
		Assert.Equal("1 show", unfinished.CountText);
	}

	// 1.8.1 (the owner's library, fix 5): a show sampled (one or two episodes: 9 of the owner's 35) isn't one left unfinished, and
	// one finished by the anchors' rule (90 % of its episodes: Community at 102 of 110, also "You finished Community") isn't either
	[Fact]
	public void UnfinishedSeries_NeedThreeEpisodesPlayed_AndNotFinishedByTheRule()
	{
		var sampled = Show("Sampled", unplayed: 20);
		var two = Show("Two in", unplayed: 20);
		var three = Show("Three in", unplayed: 20);
		var nearly = Show("Nearly done", unplayed: 8);
		var plays = new Dictionary<Guid, SeriesPlays>
		{
			[sampled.Id] = new(1, November.AddDays(-40)),
			[two.Id] = new(2, November.AddDays(-40)),
			[three.Id] = new(3, November.AddDays(-50)),
			[nearly.Id] = new(102, November.AddDays(-35)),
		};

		var unfinished = Assert.Single(Built(new UnfinishedSeriesBuilder(), Index([sampled, two, three, nearly], plays)));

		Assert.Equal([three.Id], unfinished.ItemIds);
		Assert.Equal("1 show", unfinished.CountText);
	}

	[Fact]
	public void HighlyRated_IsUnwatchedAndBestFirst()
	{
		// 1.7.3 (M1's review): each backed by ten tags, as a matched title is (Acclaimed takes only backed titles)
		var best = Film("Best", rating: 8.9, tagCount: 10);
		var good = Film("Good", rating: 7.5, tagCount: 10);
		var watched = Film("Watched", rating: 9.5, played: true, tagCount: 10);
		var low = Film("Low", rating: 7.4, tagCount: 10);
		var show = Show("A show") with { Rating = 8.0, TagCount = 10 };

		var picks = Assert.Single(Built(new HighlyRatedBuilder(), Index([good, best, watched, low, show])));

		Assert.Equal([best.Id, show.Id, good.Id], picks.ItemIds);
		Assert.Equal("3 titles", picks.CountText);
		Assert.Equal(("pick:highly-rated", "Acclaimed, not seen yet"), (picks.Id, picks.Title)); // 1.7.3: the id stays
	}

	[Fact]
	public void Acclaimed_AHighRatingLittleBacksUp_DoesNotLead()
	{
		// 1.7.3 (P0-4): a 9.8 with no artwork, no tags, no critics' rating and one copy (an unidentified file, a handful of votes)
		var shaky = Film("Shaky", rating: 9.8, poster: false);
		var known = Film("Known", rating: 8.6, tagCount: 24, critic: 91);
		var others = Enumerable.Range(0, 12).Select(i => Film($"O{i:D2}", rating: 7.6 + (i % 4 / 10.0), tagCount: 10)).ToList();
		var index = Index([shaky, known, .. others]);
		var context = new BuildContext(index, November, new HashSet<string>());

		var acclaimed = Assert.Single(Built(new HighlyRatedBuilder(), index));

		Assert.Equal(known.Id, acclaimed.ItemIds[0]);
		Assert.NotEqual(shaky.Id, acclaimed.ItemIds[0]);
		Assert.Equal(0, context.Quality.Confidence(shaky));
		Assert.Equal(context.Quality.MeanOf(IndexKind.Movie), context.Quality.Of(shaky)!.Value, 6); // no confidence: the mean
		Assert.Equal(2, context.Quality.Confidence(known)); // critics, tags (whole); 1.7.3 (M1's review): not the artwork
		Assert.Equal(1, context.Quality.M); // the median: tags
		Assert.DoesNotContain(shaky.Id, acclaimed.ItemIds); // nothing backs it (no critics, tags or outside list)
		Assert.Null(context.Quality.Of(Film("No rating")));
	}

	[Fact]
	public void Acclaimed_ObscureTitlesWithAPoster_DoNotLead_WithNoCriticsRatings()
	{
		// 1.7.3 (M1's review, Major): nearly every rated title has a poster, and artwork counted as a whole point of
		// confidence, so an obscure 9.8 with a poster and two tags (8.65) outranked an 8.6 with 24 (8.28). No critics here,
		// as on the servers the owner uses.
		var shaky = Film("Shaky", rating: 9.8, tagCount: 2);
		var known = Film("Known", rating: 8.6, tagCount: 24);
		var others = Enumerable.Range(0, 12).Select(i => Film($"O{i:D2}", rating: 7.6 + (i % 4 / 10.0), tagCount: 10)).ToList();
		var small = Assert.Single(Built(new HighlyRatedBuilder(), Index([shaky, known, .. others])));
		Assert.Equal(known.Id, small.ItemIds[0]);
		Assert.DoesNotContain(shaky.Id, small.ItemIds);

		// the reviewer's probe: well-known films (7.5 to 8.7, 12 to 31 tags), obscure high-rated ones (8.5 to 10, 0 to 3
		// tags), and many lower ones; every one with a poster. 1.7.3 before the fix gave all 20 places to obscure titles.
		var random = new Random(7);
		var famous = Enumerable.Range(0, 1500).Select(i => Film($"Famous{i}", rating: Math.Round(7.5 + (random.NextDouble() * 1.2), 1), tagCount: 12 + random.Next(20))).ToList();
		var obscure = Enumerable.Range(0, 300).Select(i => Film($"Obscure{i}", rating: Math.Round(8.5 + (random.NextDouble() * 1.5), 1), tagCount: random.Next(4))).ToList();
		var low = Enumerable.Range(0, 2000).Select(i => Film($"Low{i}", rating: Math.Round(4 + (random.NextDouble() * 3.4), 1), tagCount: random.Next(25))).ToList();
		var index = Index([.. famous, .. obscure, .. low]);

		var acclaimed = Assert.Single(Built(new HighlyRatedBuilder(), index));

		Assert.Equal(HighlyRatedBuilder.Most, acclaimed.ItemIds.Count);
		var obscureIds = obscure.Select(x => x.Id).ToHashSet();
		Assert.DoesNotContain(acclaimed.ItemIds, obscureIds.Contains);
		var quality = new BuildContext(index, November, new HashSet<string>()).Quality;
		Assert.Equal(0, quality.Confidence(Film("A poster alone", rating: 9.9))); // artwork is no confidence
		Assert.False(quality.IsBacked(Film("Four tags", rating: 9.9, tagCount: 4)));
		Assert.True(quality.IsBacked(Film("Five tags", rating: 9.9, tagCount: QualityScore.TagsBacking)));
		Assert.True(quality.IsBacked(Film("Critics", rating: 9.9, critic: 80)));
		var listed = Film("On a list", rating: 9.9);
		Assert.True(QualityScore.Of(CanonicalTitles.Of([listed]), new HashSet<Guid> { listed.Id }).IsBacked(listed));
	}

	[Fact]
	public void PlaceholderIds_AreNoIds_SoDifferentFilmsStayApart()
	{
		// 1.7.3 (M1's review): TMDb "0" (an unmatched file's) made three different films one title
		Assert.Equal(3, CanonicalTitles.Of([Film("Alpha", 1990, tmdb: "0"), Film("Beta", 2011, tmdb: "0"), Film("Gamma", 2015, tmdb: "0")]).Entries.Count);
		Assert.Equal(3, CanonicalTitles.Of([Film("Alpha", 1990, tmdb: "-1"), Film("Beta", 2011, tmdb: " 0 "), Film("Gamma", 2015, tmdb: "-1")]).Entries.Count);
		Assert.Equal(2, CanonicalTitles.Of([Film("Alpha", 1990) with { Imdb = "tt" }, Film("Beta", 2011) with { Imdb = "tt" }]).Entries.Count);
		Assert.Equal(2, CanonicalTitles.Of([Film("Alpha", 1990) with { Imdb = "0" }, Film("Beta", 2011) with { Imdb = "0" }]).Entries.Count);
		Assert.Equal(2, CanonicalTitles.Of([Show("Alpha") with { Tvdb = "0" }, Show("Beta") with { Tvdb = "0" }]).Entries.Count);
		// real ids still join
		var heat = Film("Heat", 1995, tmdb: "949");
		Assert.Single(CanonicalTitles.Of([heat, Copy(heat)]).Entries);
		Assert.Single(CanonicalTitles.Of([Film("Heat", 1995) with { Imdb = "TT0113277" }, Film("Heat (4K)", 1995) with { Imdb = "tt0113277" }]).Entries);
		Assert.Single(CanonicalTitles.Of([Show("Severance") with { Tvdb = "371980" }, Show("Severance (2022)") with { Tvdb = "371980" }]).Entries);
	}

	[Fact]
	public void AShowInTwoLibraries_HalfWatchedInOne_StaysUnfinishedAndAcclaimed()
	{
		// 1.7.3 (M1's review; a regression from 1.7.2): one copy with its season 1 all watched, the other half-watched. The
		// copies' lowest count of episodes left (0) and "played when any copy is" lost the show from both
		var watched = Show("Severance") with { Tmdb = "95396", Unplayed = 0, Played = true, Rating = 8.7, Critic = 90 };
		var half = Show("Severance") with { Tmdb = "95396", Unplayed = 9, LibraryId = Guid.NewGuid(), Rating = 8.7, Critic = 90 };
		var plays = new Dictionary<Guid, SeriesPlays> { [half.Id] = new(9, November.AddDays(-40)) };

		var title = Assert.Single(CanonicalTitles.Of([watched, half]).Entries);
		Assert.Equal((false, (int?)9), (title.Played, title.Unplayed));
		var unfinished = Assert.Single(Built(new UnfinishedSeriesBuilder(), Index([watched, half], plays)));
		Assert.Equal([watched.Id], unfinished.ItemIds); // once, by the copy that stands for it
		var others = Enumerable.Range(0, 2).Select(i => Film($"Backed{i}", rating: 8.0, critic: 80)).ToList();
		var acclaimed = Assert.Single(Built(new HighlyRatedBuilder(), Index([watched, half, .. others])));
		Assert.Contains(watched.Id, acclaimed.ItemIds);
		// a show every copy of which is watched is played; a film is played when any copy is
		Assert.True(CanonicalTitles.Of([watched, half with { Played = true, Unplayed = 0 }]).Entries.Single().Played);
		var heat = Film("Heat", 1995, tmdb: "949");
		Assert.True(CanonicalTitles.Of([heat, Copy(heat) with { Played = true }]).Entries.Single().Played);
	}

	[Fact]
	public void Keywords_AreOnlyTheAllowlist_FiveOrMore()
	{
		var heists = Enumerable.Range(0, 5).Select(i => Film($"H{i}", tags: ["heist"])).ToList();
		var handTyped = Enumerable.Range(0, 6).Select(i => Film($"M{i}", tags: ["my favourites"])).ToList();
		var few = Enumerable.Range(0, 4).Select(i => Film($"T{i}", tags: ["time travel"])).ToList();

		var keyword = Assert.Single(Built(new KeywordBuilder(), Index([.. heists, .. handTyped, .. few])));

		Assert.Equal("Heists", keyword.Title);
		Assert.Equal("Titles with TMDb's keyword “heist”", keyword.Why); // 1.8 (P1-4): no "Your"
		Assert.Equal(["heist"], Keywords.Kept(["  Heist ", "my favourites", "simulated reality ", "halloween"]).Take(1));
		Assert.Equal(["heist", "simulated reality", "halloween"], Keywords.Kept(["  Heist ", "my favourites", "simulated reality ", "halloween"]));
	}

	[Fact]
	public void DirectorsAndActors_ComeFromThePeoplePass()
	{
		var director = new IndexPerson("Ava Abara", Guid.NewGuid(), "Director");
		var actor = new IndexPerson("Otto Always", Guid.NewGuid(), "Actor");
		var films = Enumerable.Range(0, 3).Select(i => Film($"D{i}", 2000 + i, people: [director, actor])).ToList();
		films.AddRange(Enumerable.Range(0, 2).Select(i => Film($"A{i}", 2010 + i, people: [actor])));

		var directed = Assert.Single(Built(new DirectorBuilder(), Index(films)));
		var with = Assert.Single(Built(new ActorBuilder(), Index(films)));

		Assert.Equal("Ava Abara", directed.Title);
		Assert.True(directed.HasOwnOrder);
		Assert.Equal(5, with.ItemIds.Count);
		Assert.Equal(films[^1].Id, with.ItemIds[0]); // newest first
		Assert.Empty(Built(new DirectorBuilder(), Index(films.Take(2))));
	}

	[Fact]
	public void Countries_LeaveOutTheLibrarysOwn()
	{
		var films = Enumerable.Range(0, 8).Select(i => Film($"U{i}", countries: ["United States of America"])).ToList();
		films.AddRange(Enumerable.Range(0, 5).Select(i => Film($"F{i}", countries: ["France", "Germany"])));
		films.AddRange(Enumerable.Range(0, 4).Select(i => Film($"J{i}", countries: ["Japan"])));

		var country = Assert.Single(Built(new CountryBuilder(), Index(films)));

		Assert.Equal("Films from France", country.Title);
	}

	[Fact]
	public void TheSeasonalRow_IsHalloweenInOctober_AndNothingEarlyInNovember()
	{
		var films = new List<IndexEntry>
		{
			Film("Night of the Living Dead", 1968, tags: ["halloween", "zombie"], genres: ["Horror"], rating: 7.6),
			Film("Scream", 1996, genres: ["Horror"], rating: 7.4),
			Film("Halloween", 1978, tags: ["halloween"], genres: ["Horror"], rating: 7.5),
			Film("Home Alone", 1990, tags: ["christmas"]),
			Film("Elf", 2003, tags: ["christmas"]),
			Film("Klaus", 2019, tags: ["santa claus"]),
		};

		var october = Assert.Single(Built(new SeasonalBuilder(), Index(films), new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero)));
		Assert.Equal("Halloween", october.Title);
		Assert.Equal(3, october.ItemIds.Count);
		Assert.Empty(Built(new SeasonalBuilder(), Index(films), new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero)));
	}

	// 1.8.1 (the owner's library, fix 4; the owner's choice: "Fixed 20 November"): the Christmas row opens on 20 November on the
	// PC's own date (it opened on 1 December: the week of Thanksgiving had no seasonal row, with 551 Christmas titles indexed),
	// and shows to the end of December as before
	[Theory]
	[InlineData("2026-11-19T23:30:00-08:00", false)] // the evening of the 19th in California (already the 20th in UTC)
	[InlineData("2026-11-20T00:10:00-08:00", true)]
	[InlineData("2026-11-20T00:10:00+09:00", true)]  // the 20th in Tokyo (still the 19th in UTC)
	[InlineData("2026-11-19T20:00:00+09:00", false)]
	[InlineData("2026-12-31T23:00:00-08:00", true)]
	[InlineData("2027-01-01T00:30:00-08:00", false)]
	public void TheChristmasRow_OpensOn20November_OnThePcsOwnDate_AndShowsToTheEndOfDecember(string when, bool open)
	{
		var christmas = new[] { Film("Home Alone", 1990, tags: ["christmas"]), Film("Elf", 2003, tags: ["christmas"]), Film("Klaus", 2019, tags: ["santa claus"]) };
		var now = DateTimeOffset.Parse(when, System.Globalization.CultureInfo.InvariantCulture);

		var made = Built(new SeasonalBuilder(), Index(christmas), now);

		Assert.Equal(open, made.Count == 1);
		Assert.Equal(open, SeasonalBuilder.IsChristmas(now));
		if (open)
		{
			Assert.Equal("seasonal:christmas", made[0].Id);
			Assert.Equal(new DateOnly(now.Year, 12, 31), made[0].Reason?.Until); // its last day as before
		}
	}

	// 1.8.1 (the owner's library, fix 4): Christmas and Halloween best first by the quality score, the unwatched first (by the raw
	// rating a 2026 release with a handful of votes led them), and a watched Halloween title is out, as watched horror films are
	[Fact]
	public void TheSeasonalRows_ComeBestFirstByScore_UnwatchedFirst_AndHalloweensWatchedTitlesAreOut()
	{
		var fewVotes = Film("New and Barely Rated", 2026, tags: ["christmas"], rating: 9.8);
		var classic = Film("Classic", 1946, tags: ["christmas"], rating: 8.0, tagCount: 20, critic: 94);
		var solid = Film("Solid", 1990, tags: ["christmas"], rating: 7.6, tagCount: 12, critic: 70);
		var seen = Film("Seen Already", 1983, tags: ["christmas"], rating: 9.9, played: true, tagCount: 20, critic: 90);
		var others = Enumerable.Range(0, 10).Select(i => Film($"Other {i}", rating: 6.5, tagCount: 8)).ToList(); // the library's mean

		var christmas = Assert.Single(Built(new SeasonalBuilder(), Index([fewVotes, classic, solid, seen, .. others]), new DateTimeOffset(2026, 12, 5, 12, 0, 0, TimeSpan.Zero)));
		Assert.Equal([classic.Id, solid.Id, fewVotes.Id, seen.Id], christmas.ItemIds);

		var watchedHalloween = Film("Halloween (watched)", 1978, tags: ["halloween"], genres: ["Horror"], rating: 9.5, played: true, tagCount: 20);
		var tagged = Film("Trick", 2007, tags: ["halloween"], genres: ["Horror"], rating: 8.0, tagCount: 15, critic: 80);
		var barely = Film("Barely", 2026, tags: ["halloween"], genres: ["Horror"], rating: 9.9);
		var horror = Film("The Thing", 1982, genres: ["Horror"], rating: 8.1, tagCount: 20, critic: 85);
		var halloween = Assert.Single(Built(new SeasonalBuilder(), Index([watchedHalloween, tagged, barely, horror, .. others]), new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero)));
		Assert.Equal([tagged.Id, barely.Id, horror.Id], halloween.ItemIds); // the tagged first, each part best first; the watched one out
	}

	[Fact]
	public void KindsTurnedOff_AreLeftOut()
	{
		var films = new[] { Film("The Matrix", 1999, "2344", rating: 8.3, tagCount: 10), Film("The Matrix Reloaded", 2003, "2344", rating: 7.9, tagCount: 10), Film("The Matrix Revolutions", 2003, "2344", rating: 6.8, tagCount: 10), Film("Amélie", rating: 7.9, tagCount: 10) };

		var all = SmartCollectionCatalog.BuildAll(Index(films), November);
		var some = SmartCollectionCatalog.BuildAll(Index(films), November, kindsOff: new HashSet<SmartCollectionKind> { SmartCollectionKind.Franchise });

		Assert.Contains(all, x => x.Kind == SmartCollectionKind.Franchise);
		Assert.Contains(all, x => x.Kind == SmartCollectionKind.HighlyRated);
		Assert.DoesNotContain(some, x => x.Kind == SmartCollectionKind.Franchise);
		Assert.Contains(some, x => x.Kind == SmartCollectionKind.HighlyRated);
	}

	[Fact]
	public void TheCollectionOfTheDay_StaysAllDay_AndChangesTheNext()
	{
		var films = Enumerable.Range(0, 30).Select(i => Film($"F{i}", 1980 + i, tmdbCollection: $"{i / 3}")).ToList(); // 1.8.1 (fix 7): threes
		var all = SmartCollectionCatalog.BuildAll(Index(films), November);

		var morning = SmartCollectionCatalog.OfTheDay(all, November.Date);
		var evening = SmartCollectionCatalog.OfTheDay(all, November.Date.AddHours(23));
		var tomorrow = SmartCollectionCatalog.OfTheDay(all, November.Date.AddDays(1));

		Assert.Equal(morning, evening);
		Assert.NotEqual(morning!.Id, tomorrow!.Id);
		Assert.Null(SmartCollectionCatalog.OfTheDay([], November));
	}

	// --- Nixie 1.6.1 (bug hunt BH-160-m1): the local date, on every page -------------------------------------------------

	/// <summary>A PC in California (UTC-7 in October), at a moment the test names.</summary>
	private sealed class PacificClock(DateTimeOffset utc) : TimeProvider
	{
		private static readonly TimeZoneInfo Pacific = TimeZoneInfo.CreateCustomTimeZone("nixie-test-pdt", TimeSpan.FromHours(-7), "UTC-7", "UTC-7");

		public override DateTimeOffset GetUtcNow() => utc;

		public override TimeZoneInfo LocalTimeZone => Pacific;
	}

	[Fact]
	public void HalloweenEvening_InCalifornia_StillHasTheHalloweenRow_AndTheDaysRowOfThatDay()
	{
		// 17:00 PDT on 31 October is 1 November in UTC: the row left Home and the Collections page then
		var evening = new PacificClock(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
		var morning = new PacificClock(new DateTimeOffset(2026, 10, 31, 16, 0, 0, TimeSpan.Zero)); // 09:00 PDT
		var films = new List<IndexEntry>
		{
			Film("Night of the Living Dead", 1968, tags: ["halloween"], genres: ["Horror"], rating: 7.6),
			Film("Scream", 1996, genres: ["Horror"], rating: 7.4),
			Film("Halloween", 1978, tags: ["halloween"], genres: ["Horror"], rating: 7.5),
		};
		films.AddRange(Enumerable.Range(0, 30).Select(i => Film($"F{i}", 1980 + i, tmdbCollection: $"{i / 2}")));
		films.AddRange(Enumerable.Range(0, 8).Select(i => Film($"Horror {i}", 1980 + i, genres: ["Horror"], rating: 6.5))); // 1.8: a row's worth (the lineup's floor)

		var now = SmartCollectionCatalog.Now(evening);
		Assert.Equal((10, 31, 17), (now.Month, now.Day, now.Hour));
		var all = SmartCollectionCatalog.BuildAll(Index(films), now);
		// 1.8 (P1-1): Home's rows are the lineup's, made for the local date
		Lineup LineupAt(DateTimeOffset at) => CollectionLineup.Assemble(CollectionLineup.Candidates(SmartCollectionCatalog.BuildAll(Index(films), at)), LineupData.Of(films), at);
		var lineup = LineupAt(now);
		var rows = FluentFin.Core.ViewModels.HomeViewModel.PickNixieRows(lineup, all, new HashSet<NixieHomeRow>(), new HashSet<SmartCollectionKind>(), CollectionLineup.HomeDefault);

		Assert.Contains(rows, x => x.Kind == SmartCollectionKind.Seasonal && x.Title == "Halloween");
		var morningsRow = SmartCollectionCatalog.OfTheDay(all, SmartCollectionCatalog.Now(morning));
		Assert.Equal(morningsRow, SmartCollectionCatalog.OfTheDay(all, now));
		static string Shown(Lineup l) => string.Join(" | ", l.Picks.Select(x => $"{x.Collection.Id}: {string.Join(",", x.Collection.ItemIds)}"));
		Assert.Equal(Shown(LineupAt(SmartCollectionCatalog.Now(morning))), Shown(lineup)); // the morning's lineup, that evening
	}

	/// <summary>Home, the Collections page, the Libraries page, the presets and a Nixie collection found by id: one clock.</summary>
	[Fact]
	public void EveryPageBuildsNixiesCollections_ByTheOneLocalClock()
	{
		var core = Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin.Core");
		var callers = Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
			.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.EndsWith("SmartCollectionCatalog.cs", StringComparison.Ordinal))
			.SelectMany(f => File.ReadAllLines(f).Select(line => (File: Path.GetFileName(f), Line: line)))
			.Where(x => x.Line.Contains("BuildAllAsync(", StringComparison.Ordinal) || x.Line.Contains("NixiePresets.For(", StringComparison.Ordinal) || x.Line.Contains("PickNixieRows(all", StringComparison.Ordinal)
				|| x.Line.Contains("SmartCollectionCatalog.Find(", StringComparison.Ordinal) || x.Line.Contains("SmartCollectionCatalog.BuildKinds(", StringComparison.Ordinal) // 1.7.3
				|| x.Line.Contains("CollectionLineup.BuildAsync(", StringComparison.Ordinal)) // 1.8: Home's collections and lineup
			.ToList();

		Assert.True(callers.Count >= 6, string.Join(Environment.NewLine, callers));
		foreach (var (file, line) in callers)
		{
			Assert.DoesNotContain("GetUtcNow()", line);
			Assert.DoesNotContain("DateTimeOffset.Now", line);
			Assert.DoesNotContain("DateTimeOffset.UtcNow", line);
		}

		var home = File.ReadAllText(Path.Combine(core, "ViewModels", "HomeViewModel.Nixie.cs"));
		Assert.Contains("var now = SmartCollectionCatalog.Now(_time);", home);
		Assert.Contains("SmartCollectionCatalog.Now(_time), null", File.ReadAllText(Path.Combine(core, "ViewModels", "CollectionsViewModel.cs"))); // 1.7.1: boxed ones left out after
		Assert.Contains("SmartCollectionCatalog.Now(), boxed", File.ReadAllText(Path.Combine(core, "ViewModels", "LibrariesLandingPageViewModel.cs")));
		// 1.7.3: a saved filter's preset may be found by its id (SmartCollectionCatalog.Find), and the presets' kinds built apart
		// (BuildKinds): by the same clock either way
		Assert.Contains("SmartCollectionCatalog.Now()", File.ReadAllText(Path.Combine(core, "ViewModels", "CollectionSources.cs")));
		var presets = File.ReadAllText(Path.Combine(core, "SmartCollections", "NixiePresets.cs"));
		Assert.True(presets.Split("SmartCollectionCatalog.Now()").Length - 1 >= 2);
	}

	// --- the index read ---------------------------------------------------------------------------------------------

	private static BaseItemDto ServerFilm(int i, Guid library) => new()
	{
		Id = Guid.Parse($"{i:D8}-1111-0000-0000-000000000000"),
		Name = $"Film {i:D5}",
		SortName = $"film {i:D5}",
		Type = BaseItemDto_Type.Movie,
		ProductionYear = 1950 + (i % 70),
		CommunityRating = i % 10,
		RunTimeTicks = TimeSpan.FromMinutes(70 + (i % 80)).Ticks,
		Genres = [new[] { "Action", "Comedy", "Drama", "Horror", "Romance" }[i % 5], new[] { "Thriller", "Family", "War" }[i % 3]],
		Tags = [new[] { "heist", "time travel", "zombie", "something else" }[i % 4], "Hand typed"],
		Studios = [new NameGuidPair { Name = $"Studio {i % 30}" }],
		ProductionLocations = [new[] { "United States of America", "France", "Japan" }[i % 3]],
		ProviderIds = new BaseItemDto_ProviderIds { AdditionalData = new Dictionary<string, object> { ["Tmdb"] = $"{i}", ["TmdbCollection"] = $"{i / 3}" } },
		UserData = new UserItemDataDto { Played = i % 4 == 0 },
	};

	private static Mock<IJellyfinClient> ServerWith(int films, out Guid libraryId, bool peopleFail = false)
	{
		var id = libraryId = Guid.NewGuid();
		var movies = new BaseItemDto { Id = id, Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		var music = new BaseItemDto { Id = Guid.NewGuid(), Name = "Music", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Music };
		var all = Enumerable.Range(0, films).Select(i => ServerFilm(i, id)).ToList();
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([movies, music]);
		client.Setup(x => x.GetLibraryIndexPage(It.Is<BaseItemDto>(l => l.Id == id), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto _, int start, int limit, bool total, CancellationToken _) => new BaseItemDtoQueryResult { Items = [.. all.Skip(start).Take(limit)], TotalRecordCount = total ? films : 0 });
		client.Setup(x => x.GetLibraryPeoplePage(It.Is<BaseItemDto>(l => l.Id == id), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto _, int start, int limit, bool total, CancellationToken _) => peopleFail ? null : new BaseItemDtoQueryResult
			{
				Items = [.. all.Skip(start).Take(limit).Select((x, i) => new BaseItemDto
				{
					Id = x.Id,
					Type = BaseItemDto_Type.Movie,
					People =
					[
						new BaseItemPerson { Name = $"Director {(start + i) % 50}", Type = BaseItemPerson_Type.Director },
						.. Enumerable.Range(0, 8).Select(a => new BaseItemPerson { Name = $"Actor {(start + i + a) % 200}", Type = BaseItemPerson_Type.Actor }),
						new BaseItemPerson { Name = "A writer", Type = BaseItemPerson_Type.Writer },
					],
				})],
				TotalRecordCount = total ? films : 0,
			});
		return client;
	}

	[Fact]
	public async Task TheIndex_ReadsTheMediaLibraries()
	{
		// 1.7.3: no people expected (the owner's answer 1: the index reads no people; stream I's branch never asks for them)
		var client = ServerWith(250, out var libraryId);

		var index = await LibraryIndex.ReadIndex(client.Object, TimeProvider.System, CancellationToken.None);

		Assert.NotNull(index);
		Assert.Equal(250, index!.Entries.Count);
		var first = index.Entries[0];
		Assert.Equal(libraryId, first.LibraryId);
		Assert.True(first.InLibrary(libraryId));
		Assert.Equal("0", first.TmdbCollection);
		Assert.Equal(["heist"], first.Tags); // the hand-typed tag is not kept
		Assert.Single(index.Libraries); // music is not indexed
		client.Verify(x => x.GetLibraryIndexPage(It.Is<BaseItemDto>(l => l.CollectionType == BaseItemDto_CollectionType.Music), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task AFailedPeoplePass_LeavesTheOtherKinds()
	{
		var client = ServerWith(60, out _, peopleFail: true);

		var index = await LibraryIndex.ReadIndex(client.Object, TimeProvider.System, CancellationToken.None);

		Assert.False(index!.HasPeople);
		Assert.Equal(60, index.Entries.Count);
		var all = SmartCollectionCatalog.BuildAll(index, November);
		Assert.DoesNotContain(all, x => x.Kind is SmartCollectionKind.Director or SmartCollectionKind.Actor);
		Assert.Contains(all, x => x.Kind == SmartCollectionKind.Franchise);
	}

	[Fact]
	public async Task PlayedEpisodes_GiveEachShowItsLastPlay()
	{
		var shows = new BaseItemDto { Id = Guid.NewGuid(), Name = "Shows", CollectionType = BaseItemDto_CollectionType.Tvshows };
		var series = Guid.NewGuid();
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([shows]);
		client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new BaseItemDtoQueryResult { Items = [new BaseItemDto { Id = series, Name = "A show", Type = BaseItemDto_Type.Series, UserData = new UserItemDataDto { UnplayedItemCount = 4 } }], TotalRecordCount = 1 });
		client.Setup(x => x.GetLibraryPeoplePage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 });
		client.Setup(x => x.GetPlayedEpisodesPage(0, It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new BaseItemDtoQueryResult
			{
				Items =
				[
					new BaseItemDto { Id = Guid.NewGuid(), SeriesId = series, UserData = new UserItemDataDto { LastPlayedDate = November.AddDays(-40) } },
					new BaseItemDto { Id = Guid.NewGuid(), SeriesId = series, UserData = new UserItemDataDto { LastPlayedDate = November.AddDays(-45) } },
					new BaseItemDto { Id = Guid.NewGuid(), SeriesId = series, UserData = new UserItemDataDto { LastPlayedDate = November.AddDays(-46) } }, // 1.8.1: three (fix 5)
				],
				TotalRecordCount = 3,
			});

		var index = await LibraryIndex.ReadIndex(client.Object, TimeProvider.System, CancellationToken.None);

		Assert.Equal(new SeriesPlays(3, November.AddDays(-40)), index!.SeriesPlays[series]);
		Assert.Equal("Unfinished series", Assert.Single(SmartCollectionCatalog.BuildAll(index, November)).Title);
	}

	[Fact]
	public async Task TheIndexAt1334Films_StaysUnderItsTimeBudget()
	{
		// the QA-size library the plan names (1,334 films), read and built in well under a second here. 1.7.3: nothing here needs
		// the people (the read has no people pass on stream I's branch; Find makes a decade, which needs none)
		var client = ServerWith(1334, out _);
		var watch = Stopwatch.StartNew();

		var index = await LibraryIndex.ReadIndex(client.Object, TimeProvider.System, CancellationToken.None);
		var all = SmartCollectionCatalog.BuildAll(index!, November);

		watch.Stop();
		Assert.Equal(1334, index!.Entries.Count);
		Assert.NotEmpty(all);
		Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed.TotalMilliseconds:0} ms");
		Assert.DoesNotContain(all, x => x.Kind == SmartCollectionKind.Director); // 1.7.3 (P0-3): on demand only
		Assert.DoesNotContain(all, x => x.Kind == SmartCollectionKind.Decade);
		Assert.NotNull(SmartCollectionCatalog.Find(index!, "decade:movie:1980", November));
		Assert.Contains(all, x => x.Kind == SmartCollectionKind.Keyword && x.Title == "Heists");
	}

	[Fact]
	public async Task A25800FilmLibrary_IsBuiltOffTheUiThread_WithinItsBudget()
	{
		// the owner-sized library: read page by page (at most Services.LibraryPageReader.MaxPageSize a request), then every collection
		// made by BuildAllAsync on another thread, so a page awaiting it never freezes
		var client = ServerWith(25_800, out _);
		var index = await LibraryIndex.ReadIndex(client.Object, TimeProvider.System, CancellationToken.None);
		var watch = Stopwatch.StartNew();

		var all = SmartCollectionCatalog.BuildAll(index!, November);
		var built = watch.Elapsed;
		// 1.6.1 (the 1.6 review: the test had no thread check): from a UI thread, BuildAllAsync hands the work to another thread at
		// once: the UI thread is free again long before the collections are made, and they are made on another thread
		using var ui = new UiThread();
		var uiThread = await ui.Invoke(() => Environment.CurrentManagedThreadId);
		var handed = Stopwatch.StartNew();
		var building = await ui.Invoke(() => SmartCollectionCatalog.BuildAllAsync(index!, November));
		var freeAfter = handed.Elapsed;
		var answered = await ui.Invoke(() => Environment.CurrentManagedThreadId);
		var viaAsync = await building;
		var asyncTook = handed.Elapsed;

		watch.Stop();
		Assert.Equal(25_800, index!.Entries.Count);
		Assert.Equal(all.Count, viaAsync.Count);
		Assert.True(watch.Elapsed < TimeSpan.FromSeconds(6), $"built twice in {watch.Elapsed.TotalMilliseconds:0} ms");
		Assert.Equal(uiThread, answered);
		Assert.True(freeAfter < asyncTook / 2 && freeAfter < built / 2, $"the UI thread was free after {freeAfter.TotalMilliseconds:0} ms of a {asyncTook.TotalMilliseconds:0} ms build ({built.TotalMilliseconds:0} ms on the test's thread)");
		client.Verify(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.Is<int>(l => l > Services.LibraryPageReader.MaxPageSize), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task ACancelledRead_StopsWithoutAnIndex()
	{
		var client = ServerWith(3000, out _);
		using var cancel = new CancellationTokenSource();
		cancel.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LibraryIndex.ReadIndex(client.Object, TimeProvider.System, cancel.Token));
	}

	[Fact]
	public void ALibrarysPresets_AreMadeOfItsOwnTitles()
	{
		// 1.7.3: the strip's kinds and their order (decades, genre mixes and countries kept as filters there) are stream M2's,
		// tested in LibraryPresetsTests; here, only that a library's presets are made of its own titles
		var movies = Guid.NewGuid();
		var other = Guid.NewGuid();
		var films = Enumerable.Range(0, 12).Select(i => Film($"F{i}", 1980 + (i % 10), tmdbCollection: i < 2 ? "1" : i < 4 ? "2" : null, tags: ["heist"]) with { LibraryId = movies }).ToList();
		films.AddRange(Enumerable.Range(0, 12).Select(i => Film($"D{i}", 1990, tags: ["zombie"]) with { LibraryId = other }));

		var presets = NixiePresets.For(Index(films), movies, November);

		Assert.Equal(2, presets.Count(p => p.Kind == SmartCollectionKind.Franchise));
		var heists = Assert.Single(presets, p => p.Label == "Heists");
		Assert.Equal("Heists, made by Nixie, keyword, 12 films", heists.AutomationName);
		Assert.DoesNotContain(presets, p => p.Label == "Zombies"); // another library's
		Assert.Equal(2, NixiePresets.For(Index(films), movies, November, most: 2).Count);
		Assert.Empty(NixiePresets.For(Index(films), Guid.NewGuid(), November));
	}

	[Theory]
	[InlineData(SmartCollectionKind.Decade, "1980s films", "1980s")]
	[InlineData(SmartCollectionKind.Country, "Films from the United States", "USA")]
	[InlineData(SmartCollectionKind.Country, "Films from the United Kingdom", "UK")]
	[InlineData(SmartCollectionKind.Country, "Films from the Netherlands", "Netherlands")]
	[InlineData(SmartCollectionKind.Country, "Films from France", "France")]
	[InlineData(SmartCollectionKind.HighlyRated, "Acclaimed, not seen yet", "Acclaimed")] // 1.7.3 (P0-4): was "Highly rated"
	[InlineData(SmartCollectionKind.ShortFilms, "Under 90 minutes", "Under 90 minutes")] // 1.7.3 (P0-4)
	[InlineData(SmartCollectionKind.UnfinishedSeries, "Unfinished series", "Unfinished")]
	[InlineData(SmartCollectionKind.Keyword, "Heists", "Heists")]
	[InlineData(SmartCollectionKind.Franchise, "The Lord of the Rings: The Rings of Power", "The Lord of the Rings…")]
	public void PresetLabels_AreShort(SmartCollectionKind kind, string title, string label)
	{
		var collection = new SmartCollection("x", kind, title, "", false, [Guid.NewGuid()]);

		Assert.Equal(label, NixiePresets.ShortLabel(collection));
		Assert.True(NixiePresets.ShortLabel(collection).Length <= NixiePresets.LabelMost);
	}

	[Fact]
	public void TheIndexQuery_NamesRealFieldsOnly()
	{
		// the server drops a Fields name it doesn't know and answers 200: the index's names are the SDK's own, and exactly these
		Assert.Equal(["ProviderIds", "Tags", "Genres", "Studios", "ProductionLocations", "SortName", "DateCreated"], Services.JellyfinClient.IndexFields.Select(x => x.ToString()));
		Assert.All(Services.JellyfinClient.IndexFields, f => Assert.True(Enum.IsDefined(f)));
		Assert.Equal([ItemFields.People], Services.JellyfinClient.PeopleFields);
	}

	// --- Nixie 1.7.3: the P0s of "Useful Made by Nixie collections" ------------------------------------------------------

	/// <summary>Another file of the same title: its own id, by default no artwork.</summary>
	private static IndexEntry Copy(IndexEntry of, bool poster = false, Guid? library = null) => of with
	{
		Id = Guid.Parse($"{Interlocked.Increment(ref s_next):D8}-0000-0000-0000-000000000003"),
		PrimaryTag = poster ? "p" : null,
		LibraryId = library ?? of.LibraryId,
	};

	private const string CensusLine = @"^Made by Nixie census: (\d+) entries, (\d+) titles once copies are one; kind candidates/floor/cap: ((\w+ (\d+/\d+/\d+( \[[^\]]*\])?|off|failed))(, |;))+ (\d+) collections in \d+ ms$";

	[Fact]
	public void TheCensus_IsOneLineAWholeIndex_CountsAndKindsOnly()
	{
		// P0-1: three files of Heat (a one-film "franchise" before 1.7.3), four other heists
		var heat = Film("Heat Secretname", 1995, tmdbCollection: "77", tags: ["heist"], rating: 8.3, tmdb: "949");
		var films = new List<IndexEntry> { heat, Copy(heat), Copy(heat) };
		films.AddRange(Enumerable.Range(0, 4).Select(i => Film($"Heist Secretname {i}", 2000 + i, tags: ["heist"], rating: 7.9)));
		var index = Index(films) with { Key = "census:" + Guid.NewGuid() };
		var log = new CoreListLogger<SmartCollectionsTests>();
		void Build(IndexSnapshot of, IReadOnlySet<SmartCollectionKind>? off = null, bool census = true) =>
			SmartCollectionCatalog.BuildAll(SmartCollectionCatalog.PublishedBuilders, of, November, null, off, FluentFin.Core.Outside.OutsideListsSnapshot.Empty, log, census);

		Build(index);
		Build(index, off: new HashSet<SmartCollectionKind> { SmartCollectionKind.Keyword }); // another page, the same index: no second line
		Build(index with { Key = "partial:" + Guid.NewGuid(), Reading = new IndexProgress(7, 20, null) }); // a first read's partial index: none
		Build(index with { Key = "library:" + Guid.NewGuid() }, census: false); // a library's own titles (BuildKinds), Find: none

		var line = Assert.Single(log.Entries);
		Assert.Equal(LogLevel.Information, line.Level);
		Assert.Matches(CensusLine, line.Message);
		// fix round (BH-173-m1): no title has a critics' rating or 5+ tags, so Acclaimed ranks by the rating alone, and says so
		Assert.StartsWith("Made by Nixie census: 7 entries, 5 titles once copies are one; kind candidates/floor/cap: NextIn 0/0/0, MoreLike 0/0/0, MoreFrom 0/0/0, Franchise 1/0/0, Keyword 1/1/1, "
			+ "HighlyRated 5/5/5 [rating only, no title has a critics' rating or 5+ tags: critic 0, tags5+ 0, listed 0], "
			+ "UnfinishedSeries 0/0/0, Seasonal 0/0/0, ShortFilms 0/0/0, Award 0/0/0; 2 collections in ", line.Message); // 1.8: the Spotlight is made after the first rows (BuildLaterAsync)
		Assert.DoesNotContain("Secretname", line.Message);
		Assert.DoesNotContain("Heat", line.Message);
	}

	[Fact]
	public async Task TheCensus_IsSaidByThePagesBuild_NeverByALibrarysPresetsOrFind()
	{
		// the app's log (here the test run's, CoreTestsModule): the pages' build of a whole index (BuildAllAsync: Home, the
		// Collections and Libraries pages) says it, once; a library's presets (BuildAll on its own titles), BuildKinds and Find don't
		var whole = Index(Enumerable.Range(0, 1703).Select(i => Film($"W{i}", 1900 + (i % 120)))) with { Key = "census-whole:" + Guid.NewGuid() };
		var library = Index(Enumerable.Range(0, 1711).Select(i => Film($"L{i}", 1900 + (i % 120)))) with { Key = "census-library:" + Guid.NewGuid() };

		await SmartCollectionCatalog.BuildAllAsync(whole, November);
		await SmartCollectionCatalog.BuildAllAsync(whole, November);
		SmartCollectionCatalog.BuildAll(library, November);
		SmartCollectionCatalog.BuildKinds(library, November, SmartCollectionCatalog.Builders.Select(x => x.Kind).ToHashSet());
		SmartCollectionCatalog.Find(library, "decade:movie:1980", November);

		var lines = MadeByNixieLog.Instance.Entries.Select(x => x.Message).Where(x => x.StartsWith("Made by Nixie census: ", StringComparison.Ordinal)).ToList();
		Assert.Single(lines, x => x.StartsWith("Made by Nixie census: 1703 entries, 1703 titles", StringComparison.Ordinal));
		Assert.DoesNotContain(lines, x => x.StartsWith("Made by Nixie census: 1711 entries", StringComparison.Ordinal));
		Assert.All(lines, x => Assert.Matches(CensusLine, x));
	}

	[Fact]
	public void ThreeFilesOfAFilm_CountOnce_TheCopyWithArtworkStandsForThem()
	{
		// P0-2b: one by TMDb id, one by the IMDb id the TMDb title has; the third (with the artwork) has only that IMDb id
		var heat = Film("Heat", 1995, tags: ["heist"], rating: 8.3, tmdb: "949", poster: false, tagCount: 5) with { Imdb = "tt0113277" };
		var copy = Copy(heat);
		var art = Copy(heat, poster: true) with { Tmdb = null, Tags = ["heist", "los angeles"] };
		var heists = Enumerable.Range(0, 3).Select(i => Film($"H{i}", 2000 + i, tags: ["heist"], rating: 7.9, tagCount: 5)).ToList(); // backed: Acclaimed's
		var four = Index([heat, copy, art, .. heists]);

		Assert.Empty(Built(new KeywordBuilder(), four)); // four titles, not six files: under the keyword floor of five
		var titles = new BuildContext(four, November, new HashSet<string>()).Titles;
		Assert.Equal((6, 4), (titles.EntryCount, titles.Entries.Count));
		var title = titles.TitleOf(copy.Id)!;
		Assert.Equal(art.Id, title.Id); // the copy with artwork
		Assert.Equal(3, titles.CopiesOf(heat.Id));
		Assert.Equal([art.Id, heat.Id, copy.Id], titles.CopyIds(copy.Id));
		Assert.Equal(["heist", "los angeles"], title.Tags); // merged
		Assert.Equal(("949", "tt0113277"), (title.Tmdb, title.Imdb));
		Assert.True(CanonicalTitles.Of([heat, copy with { Played = true }, art]).Entries.Single().Played); // played when any copy is

		var five = Index([heat, copy, art, .. heists, Film("H3", 2010, tags: ["heist"])]);
		var keyword = Assert.Single(Built(new KeywordBuilder(), five));
		Assert.Equal(5, keyword.ItemIds.Count);
		Assert.Contains(art.Id, keyword.ItemIds);
		var acclaimed = Assert.Single(Built(new HighlyRatedBuilder(), five));
		Assert.Equal(art.Id, Assert.Single(acclaimed.ItemIds, id => titles.CopyIds(art.Id).Contains(id))); // once
	}

	[Fact]
	public void AOneFilmFranchiseFromTwoFiles_IsGone_AndABoxSetsCopyIsNotAlsoInTheLibrary()
	{
		var alien = Film("Alien", 1979, tmdbCollection: "8091", tmdb: "348");
		var other = Copy(alien, library: Guid.NewGuid());

		Assert.Empty(Built(new FranchiseBuilder(), Index([alien, other]))); // 1.7.2: "Alien films", one film twice

		var aliens = Film("Aliens", 1986, tmdbCollection: "8091", tmdb: "679");
		var third = Film("Alien³", 1992, tmdbCollection: "8091", tmdb: "8077");
		var franchise = Assert.Single(Built(new FranchiseBuilder(), Index([alien, other, aliens])));
		Assert.Equal([alien.Id, aliens.Id], franchise.ItemIds);
		// an admin's box set holds the other file of Alien, and Aliens: Alien is in it, not "also in your library"
		Assert.Equal([third.Id], FranchiseBuilder.AlsoInLibrary(Index([alien, other, aliens, third]), "8091", [other.Id, aliens.Id]).Select(x => x.Id));
	}

	[Fact]
	public void TwoFilmsWithTheSameTitleAndYear_StayTwo()
	{
		var hamlet = Film("Hamlet", 1990, tmdb: "10264");
		var another = Film("Hamlet", 1990, tmdb: "45269");
		var noIds = Film("Hamlet", 1990);
		var noIdsAgain = Film("Hamlet!", 1990, poster: false);
		var show = Show("Hamlet") with { Year = 1990, Tmdb = "10264" }; // TMDb numbers films and shows apart

		var titles = CanonicalTitles.Of([hamlet, another, noIds, noIdsAgain, show]);

		Assert.Equal(4, titles.Entries.Count); // two films by their ids, the two with no id (one name and year), the show
		Assert.Equal(1, titles.CopiesOf(hamlet.Id));
		Assert.Equal(1, titles.CopiesOf(another.Id));
		Assert.Equal(noIds.Id, titles.TitleOf(noIdsAgain.Id)!.Id);
		Assert.Equal(1, titles.CopiesOf(show.Id));
		// with no year, or another, never joined by name
		Assert.Equal(2, CanonicalTitles.Of([Film("Untitled") with { Year = null }, Film("Untitled") with { Year = null }]).Entries.Count);
		Assert.Equal(2, CanonicalTitles.Of([Film("Hamlet", 1948), Film("Hamlet", 1990)]).Entries.Count);
		// a damaged saved copy (no name, no lists) is merged as far as it can be, and never throws
		var damaged = Copy(hamlet) with { Name = null!, Tags = null!, Genres = null!, People = null!, LibraryIds = null! };
		Assert.Equal(hamlet.Id, Assert.Single(CanonicalTitles.Of([hamlet, damaged]).Entries).Id);
	}

	[Fact]
	public void TheSevenKinds_AreNeverPublished_NorEverTheCollectionOfTheDay()
	{
		// P0-3: an index that makes every kind
		var director = new IndexPerson("Ava Abara", Guid.NewGuid(), "Director");
		var actor = new IndexPerson("Otto Always", Guid.NewGuid(), "Actor");
		var films = Enumerable.Range(0, 24).Select(i => Film($"F{i:D2}", 1980 + (i % 10), tmdbCollection: $"{i / 3}", // 1.8.1 (fix 7): threes
			genres: (i % 3) switch { 0 => ["Comedy", "Horror"], 1 => ["Comedy"], _ => ["Horror"] }, tags: ["heist"], studios: ["Studio A"],
			countries: [i < 14 ? "United States of America" : "France"], rating: 8, people: [director, actor], tagCount: 10)).ToList();
		var index = Index([.. films, .. Enumerable.Range(0, 3).Select(i => Show($"S{i}", ["CBS"]))]);

		var published = SmartCollectionCatalog.BuildAll(index, November);
		var onDemand = SmartCollectionCatalog.BuildKinds(index, November, SmartCollectionCatalog.OnDemandOnly);

		Assert.Equal(SmartCollectionCatalog.OnDemandOnly.Order(), onDemand.Select(x => x.Kind).Distinct().Order()); // all seven can be made
		Assert.DoesNotContain(published, x => SmartCollectionCatalog.OnDemandOnly.Contains(x.Kind));
		Assert.Contains(published, x => x.Kind == SmartCollectionKind.Franchise);
		Assert.Contains(published, x => x.Kind == SmartCollectionKind.Keyword);
		Assert.Contains(published, x => x.Kind == SmartCollectionKind.HighlyRated);
		// the day's collection, from everything there is (a director's made on demand too): never one of the seven
		IReadOnlyList<SmartCollection> all = [.. published, .. onDemand];
		for (var day = 0; day < 400; day++)
		{
			var today = SmartCollectionCatalog.OfTheDay(all, November.AddDays(day));
			Assert.NotNull(today);
			Assert.Contains(today!.Kind, SmartCollectionCatalog.Published);
		}

		Assert.Null(SmartCollectionCatalog.OfTheDay(onDemand, November));
	}

	[Fact]
	public void Published_IsEveryKindButTheSeven_AndTheBuildersAreApart()
	{
		SmartCollectionKind[] seven = [SmartCollectionKind.Decade, SmartCollectionKind.Director, SmartCollectionKind.Actor, SmartCollectionKind.Studio,
			SmartCollectionKind.Network, SmartCollectionKind.GenreMix, SmartCollectionKind.Country];

		Assert.Equal(seven.Order(), SmartCollectionCatalog.OnDemandOnly.Order());
		Assert.Equal(Enum.GetValues<SmartCollectionKind>().Except(seven).Order(), SmartCollectionCatalog.Published.Order());
		Assert.Equal(seven.Order(), SmartCollectionCatalog.OnDemandBuilders.Select(x => x.Kind).Order());
		Assert.All(SmartCollectionCatalog.PublishedBuilders, x => Assert.Contains(x.Kind, SmartCollectionCatalog.Published));
		Assert.Equal(SmartCollectionCatalog.PublishedBuilders.Count + SmartCollectionCatalog.OnDemandBuilders.Count + SmartCollectionCatalog.LaterBuilders.Count, SmartCollectionCatalog.Builders.Count); // 1.8: the Spotlight, later
		// Find knows every builder's ids (1.8: the anchored slots' in AnchorsTests)
		Assert.All(SmartCollectionCatalog.Builders.Except(FluentFin.Core.SmartCollections.Anchors.AnchoredSlots.Builders), x => Assert.Contains((SmartCollectionKind?)x.Kind, new[] { "franchise:1", "decade:movie:1980", "director:a", "actor:a", "studio:a", "network:a",
			"genres:movie:a+b", "keyword:heist", "country:france", "pick:highly-rated", "pick:short-films", "pick:unfinished-series", "seasonal:halloween", "award:q1",
			"spotlight:director:a" /* 1.8 (P1-3) */ }
			.Select(SmartCollectionCatalog.KindOf)));
		Assert.Null(SmartCollectionCatalog.KindOf("saved:1234"));
	}

	[Fact]
	public void Find_StillBuildsADirector_OnDemand()
	{
		var director = new IndexPerson("Ava Abara", Guid.NewGuid(), "Director");
		var films = Enumerable.Range(0, 3).Select(i => Film($"D{i}", 2000 + i, rating: 8, people: [director], tagCount: 10)).ToList();
		var index = Index(films);

		var found = SmartCollectionCatalog.Find(index, "director:avaabara", November);

		Assert.NotNull(found);
		Assert.Equal((SmartCollectionKind.Director, "Ava Abara"), (found!.Kind, found.Title));
		Assert.Equal(films.Select(x => x.Id), found.ItemIds);
		Assert.DoesNotContain(SmartCollectionCatalog.BuildAll(index, November), x => x.Kind == SmartCollectionKind.Director);
		Assert.Null(SmartCollectionCatalog.Find(index, "director:nobody", November));
		Assert.Null(SmartCollectionCatalog.Find(index, "saved:1234", November)); // no builder makes it
		Assert.Null(SmartCollectionCatalog.Find(index, "", November));
		Assert.Equal("Acclaimed, not seen yet", SmartCollectionCatalog.Find(index, "pick:highly-rated", November)?.Title); // a published kind too
	}

	[Fact]
	public void ABuilderThatThrows_InAnyCoreTest_IsSeen()
	{
		// CoreTestsModule: the app sets Made by Nixie's log at start; the tests had none, so a builder's throw was silent there
		var broken = Film("Broken", tags: ["heist"]) with { Tags = [null!] }; // fix round (BH-173-q3): a null list is an empty one now
		var films = Enumerable.Range(0, 5).Select(i => Film($"H{i}", tags: ["heist"], rating: 8, tagCount: 10)).ToList();

		var all = SmartCollectionCatalog.BuildAll(Index([.. films, broken]), November);

		Assert.DoesNotContain(all, x => x.Kind == SmartCollectionKind.Keyword); // its kind is left out
		Assert.Contains(all, x => x.Kind == SmartCollectionKind.HighlyRated); // the others still made
		Assert.Same(MadeByNixieLog.Instance, SmartCollectionCatalog.Log);
		Assert.Contains(MadeByNixieLog.Instance.Throws, x => x.StartsWith("Made by Nixie: the Keyword collections couldn't be made (", StringComparison.Ordinal));
	}
	// --- 1.7.3 fix round: the bug hunt's BH-173-M1, M2, m1 and q3 ---------------------------------------------------------

	[Fact]
	public void Acclaimed_WithNothingInTheIndexThatCanBackARating_RanksByTheRatingAlone_KeepingItsFloorAndItsTwenty()
	{
		// BH-173-m1: the throwaway's 25,736 films had no tags and no critics' ratings: 6,207 rated 7.5 or more, none backed, and
		// Acclaimed never came. BH-173-M1: an index saved by 1.7.2 keeps neither, so every title read as "nothing backs it"
		var films = Enumerable.Range(0, 30).Select(i => Film($"R{i}", rating: 7.0 + (i * 0.1), tagCount: i % 5)).ToList(); // 0 to 4 tags
		var made = Assert.Single(Built(new HighlyRatedBuilder(), Index(films)));
		Assert.Equal(HighlyRatedBuilder.Most, made.ItemIds.Count);
		Assert.Equal(films[29].Id, made.ItemIds[0]); // the best rated first
		Assert.DoesNotContain(films[0].Id, made.ItemIds); // the 7.5 floor holds (7.0)

		var saved172 = Index(films.Select(x => x with { TagCount = 0 })) with { BackingKnown = false };
		Assert.Equal(HighlyRatedBuilder.Most, Assert.Single(Built(new HighlyRatedBuilder(), saved172)).ItemIds.Count);
		// even an old index whose titles say they have tags: what it kept isn't known to back anything
		var said = Index(films.Select(x => x with { TagCount = 9 })) with { BackingKnown = false };
		Assert.Equal(new RatingBacking(false, 0, 30, 0).Census, BuildContext.For(said, November).Backing.Census);

		// with something that can back a rating anywhere in the index, only backed titles (1.7.3's rule)
		var backed = Index([.. films, .. Enumerable.Range(0, 3).Select(i => Film($"B{i}", rating: 7.6, critic: 80))]);
		Assert.Equal(3, Assert.Single(Built(new HighlyRatedBuilder(), backed)).ItemIds.Count);
	}

	[Fact]
	public void ASavedFiltersPreset_KeepsEveryTitleOfItsRule_AndOneSavedBefore173Keeps172sMeaning()
	{
		// BH-173-M2: 100 short comedies of 30 to 80 minutes, every fifth watched, 60 rated 8 with six tags (backed)
		var films = Enumerable.Range(0, 100).Select(i => Film($"S{i}", rating: i < 60 ? 8 : 6, minutes: 30 + (i % 51), played: i % 5 == 0, tagCount: 6, genres: ["Comedy"])).ToList();
		var index = Index(films);
		var notWatchedRated = films.Count(x => x.Rating >= 7.5 && !x.Played);
		var feature = films.Count(x => !x.Played && x.RunTimeTicks >= ShortFilmsBuilder.AtLeast);
		Assert.True(notWatchedRated > HighlyRatedBuilder.Most && feature > ShortFilmsBuilder.Most);

		// saved by 1.7.2: "Short films" was every film under 90 minutes, watched or not; "Highly rated, not watched" every
		// title rated 7.5 or more and not watched
		Assert.Equal(100, SavedFilterPresets.Titles(index, "nixie:pick:short-films", 0, November).Count);
		Assert.Equal(notWatchedRated, SavedFilterPresets.Titles(index, "nixie:pick:highly-rated", 0, November).Count);
		// saved now: the rules of now, every title of them (no cap of 20)
		Assert.Equal(feature, SavedFilterPresets.Titles(index, "nixie:pick:short-films", SavedFilter.CurrentRevision, November).Count);
		Assert.Equal(notWatchedRated, SavedFilterPresets.Titles(index, "pick:highly-rated", SavedFilter.CurrentRevision, November).Count);
		// the published collections keep their twenty
		Assert.Equal(ShortFilmsBuilder.Most, SmartCollectionCatalog.Find(index, "pick:short-films", November)!.ItemIds.Count);
		Assert.Equal(HighlyRatedBuilder.Most, SmartCollectionCatalog.Find(index, "pick:highly-rated", November)!.ItemIds.Count);
		// any other preset is found by its id, whatever the filter's revision
		Assert.Empty(SavedFilterPresets.Titles(index, "nixie:franchise:404", 0, November));
	}

	[Fact]
	public void TheStripsChip_AndAFilterSavedOnIt_KeepTheSameTitles()
	{
		// the re-review of the fix round (BH-173-M2's other half): 100 unwatched films of 60 to 89 minutes, 80 comedies, the 20
		// best rated dramas. The strip's "Under 90 minutes" held Under 90 minutes' best 20 (the dramas), so Under 90 minutes +
		// Comedy showed 0 titles in the library, and 80 once saved as a filter
		var films = Enumerable.Range(0, 100).Select(i => i < 20
			? Film($"Drama {i}", rating: 9.5, minutes: 60 + (i % 30), genres: ["Drama"], critic: 90)
			: Film($"Comedy {i}", rating: 6.0, minutes: 60 + (i % 30), genres: ["Comedy"])).ToList();
		var index = Index(films);

		var chip = Assert.Single(NixiePresets.For(index, Guid.Empty, November, most: 50), p => p.Kind == SmartCollectionKind.ShortFilms);
		var saved = SavedFilterPresets.Titles(index, chip.Id, SavedFilter.CurrentRevision, November);

		Assert.Equal(100, chip.Items.Count);
		Assert.True(chip.Items.SetEquals(saved));
		var comedies = films.Where(x => x.Genres.Contains("Comedy")).Select(x => x.Id).ToHashSet();
		Assert.Equal(80, chip.Items.Count(comedies.Contains));
		// the published collection keeps its twenty, the dramas
		Assert.Equal(ShortFilmsBuilder.Most, SmartCollectionCatalog.Find(index, "pick:short-films", November)!.ItemIds.Count(id => !comedies.Contains(id)));
	}

	[Fact]
	public void ASavedIndexsNullLists_AreEmpty()
	{
		// BH-173-q3: a damaged saved index's "LibraryIds": null threw in InLibrary (and in the load's Once on a repeat)
		var json = """{"Id":"00000001-0000-0000-0000-000000000173","Name":"Damaged","LibraryId":"00000002-0000-0000-0000-000000000173","LibraryIds":null,"Tags":null,"Genres":null,"Studios":null,"Countries":null,"People":null}""";
		var entry = System.Text.Json.JsonSerializer.Deserialize<IndexEntry>(json)!;

		Assert.Empty(entry.LibraryIds);
		Assert.True(entry.InLibrary(Guid.Parse("00000002-0000-0000-0000-000000000173")));
		Assert.False(entry.InLibrary(Guid.NewGuid()));
		Assert.All(new object[] { entry.Tags, entry.Genres, entry.Studios, entry.Countries, entry.People }, Assert.NotNull);
	}
}
