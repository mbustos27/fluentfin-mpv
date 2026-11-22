using System.Diagnostics;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Builders;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>Nixie 1.6 (smart collections phase 1): the builders on fixture libraries, the franchise names, the index read and its budget.</summary>
public sealed class SmartCollectionsTests
{
	private static readonly DateTimeOffset November = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
	private static int s_next;

	private static IndexEntry Film(string name, int year = 2000, string? tmdbCollection = null, string[]? genres = null, string[]? tags = null,
		string[]? studios = null, string[]? countries = null, double? rating = null, double? minutes = 120, bool played = false, IndexPerson[]? people = null) => new()
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
			PrimaryTag = "p",
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
		Assert.Equal("Your films that are both Comedy and Horror", mix.Why);
	}

	[Fact]
	public void ShortFilms_AreUnder90Minutes_ToTheSecond()
	{
		var under = Film("Under", minutes: 89 + 59 / 60.0);
		var at = Film("At", minutes: 90);
		var none = Film("None", minutes: null);
		var tiny = Film("Tiny", minutes: 1.5);
		var short2 = Film("Short", minutes: 45);

		var shorts = Assert.Single(Built(new ShortFilmsBuilder(), Index([under, at, none, tiny, short2])));

		Assert.Equal([tiny.Id, short2.Id, under.Id], shorts.ItemIds);
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

	[Fact]
	public void HighlyRated_IsUnwatchedAndBestFirst()
	{
		var best = Film("Best", rating: 8.9);
		var good = Film("Good", rating: 7.5);
		var watched = Film("Watched", rating: 9.5, played: true);
		var low = Film("Low", rating: 7.4);
		var show = Show("A show") with { Rating = 8.0 };

		var picks = Assert.Single(Built(new HighlyRatedBuilder(), Index([good, best, watched, low, show])));

		Assert.Equal([best.Id, show.Id, good.Id], picks.ItemIds);
		Assert.Equal("3 titles", picks.CountText);
	}

	[Fact]
	public void Keywords_AreOnlyTheAllowlist_FiveOrMore()
	{
		var heists = Enumerable.Range(0, 5).Select(i => Film($"H{i}", tags: ["heist"])).ToList();
		var handTyped = Enumerable.Range(0, 6).Select(i => Film($"M{i}", tags: ["my favourites"])).ToList();
		var few = Enumerable.Range(0, 4).Select(i => Film($"T{i}", tags: ["time travel"])).ToList();

		var keyword = Assert.Single(Built(new KeywordBuilder(), Index([.. heists, .. handTyped, .. few])));

		Assert.Equal("Heists", keyword.Title);
		Assert.Equal("Your titles with TMDb's keyword “heist”", keyword.Why);
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
	public void TheSeasonalRow_IsHalloweenInOctober_AndNothingInNovember()
	{
		var films = new List<IndexEntry>
		{
			Film("Night of the Living Dead", 1968, tags: ["halloween", "zombie"], genres: ["Horror"], rating: 7.6),
			Film("Scream", 1996, genres: ["Horror"], rating: 7.4),
			Film("Halloween", 1978, tags: ["halloween"], genres: ["Horror"], rating: 7.5),
			Film("Home Alone", 1990, tags: ["christmas"]),
		};

		var october = Assert.Single(Built(new SeasonalBuilder(), Index(films), new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero)));
		Assert.Equal("Halloween", october.Title);
		Assert.Equal(3, october.ItemIds.Count);
		Assert.Empty(Built(new SeasonalBuilder(), Index(films), November));
	}

	[Fact]
	public void KindsTurnedOff_AreLeftOut()
	{
		var films = new[] { Film("The Matrix", 1999, "2344", rating: 8.3), Film("The Matrix Reloaded", 2003, "2344", rating: 7.9), Film("Amélie", rating: 7.9) };

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
		var films = Enumerable.Range(0, 30).Select(i => Film($"F{i}", 1980 + i, tmdbCollection: $"{i / 2}")).ToList();
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

		var now = SmartCollectionCatalog.Now(evening);
		Assert.Equal((10, 31, 17), (now.Month, now.Day, now.Hour));
		var all = SmartCollectionCatalog.BuildAll(Index(films), now);
		var rows = FluentFin.Core.ViewModels.HomeViewModel.PickNixieRows(all, new HashSet<NixieHomeRow>(), now);

		Assert.Contains(rows, x => x.Kind == SmartCollectionKind.Seasonal && x.Title == "Halloween");
		var morningsRow = SmartCollectionCatalog.OfTheDay(all, SmartCollectionCatalog.Now(morning));
		Assert.Equal(morningsRow, SmartCollectionCatalog.OfTheDay(all, now));
		Assert.Equal(morningsRow, rows[^1]);
	}

	/// <summary>Home, the Collections page, the Libraries page, the presets and a Nixie collection found by id: one clock.</summary>
	[Fact]
	public void EveryPageBuildsNixiesCollections_ByTheOneLocalClock()
	{
		var core = Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin.Core");
		var callers = Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
			.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.EndsWith("SmartCollectionCatalog.cs", StringComparison.Ordinal))
			.SelectMany(f => File.ReadAllLines(f).Select(line => (File: Path.GetFileName(f), Line: line)))
			.Where(x => x.Line.Contains("BuildAllAsync(", StringComparison.Ordinal) || x.Line.Contains("NixiePresets.For(", StringComparison.Ordinal) || x.Line.Contains("PickNixieRows(all", StringComparison.Ordinal))
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
		Assert.Contains("BuildAllAsync(own, SmartCollectionCatalog.Now())", File.ReadAllText(Path.Combine(core, "ViewModels", "CollectionSources.cs")));
		var presets = File.ReadAllText(Path.Combine(core, "SmartCollections", "NixiePresets.cs"));
		Assert.Equal(2, presets.Split("SmartCollectionCatalog.Now()").Length - 1);
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
	public async Task TheIndex_ReadsTheMediaLibraries_WithTheirPeople()
	{
		var client = ServerWith(250, out var libraryId);

		var index = await LibraryIndex.ReadIndex(client.Object, TimeProvider.System, CancellationToken.None);

		Assert.NotNull(index);
		Assert.Equal(250, index!.Entries.Count);
		Assert.True(index.HasPeople);
		var first = index.Entries[0];
		Assert.Equal(libraryId, first.LibraryId);
		Assert.Equal("0", first.TmdbCollection);
		Assert.Equal(["heist"], first.Tags); // the hand-typed tag is not kept
		Assert.Equal(6, first.People.Length); // the director and the first five actors; no writer
		Assert.Equal("Director", first.People[0].Kind);
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
				],
				TotalRecordCount = 2,
			});

		var index = await LibraryIndex.ReadIndex(client.Object, TimeProvider.System, CancellationToken.None);

		Assert.Equal(new SeriesPlays(2, November.AddDays(-40)), index!.SeriesPlays[series]);
		Assert.Equal("Unfinished series", Assert.Single(SmartCollectionCatalog.BuildAll(index, November)).Title);
	}

	[Fact]
	public async Task TheIndexAt1334Films_StaysUnderItsTimeBudget()
	{
		// the QA-size library the plan names (1,334 films), people and all, read and built in well under a second here
		var client = ServerWith(1334, out _);
		var watch = Stopwatch.StartNew();

		var index = await LibraryIndex.ReadIndex(client.Object, TimeProvider.System, CancellationToken.None);
		var all = SmartCollectionCatalog.BuildAll(index!, November);

		watch.Stop();
		Assert.Equal(1334, index!.Entries.Count);
		Assert.NotEmpty(all);
		Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed.TotalMilliseconds:0} ms");
		Assert.Contains(all, x => x.Kind == SmartCollectionKind.Director);
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
	public void ALibrarysPresets_AreMadeOfItsOwnTitles_OneOfEachKindFirst()
	{
		var movies = Guid.NewGuid();
		var other = Guid.NewGuid();
		var films = Enumerable.Range(0, 12).Select(i => Film($"F{i}", 1980 + (i % 10), tmdbCollection: i < 2 ? "1" : i < 4 ? "2" : null, tags: ["heist"]) with { LibraryId = movies }).ToList();
		films.AddRange(Enumerable.Range(0, 12).Select(i => Film($"D{i}", 1990, tags: ["zombie"]) with { LibraryId = other }));

		var presets = NixiePresets.For(Index(films), movies, November);

		// two franchises, a decade, a keyword: one of each kind first, then the second franchise
		Assert.Equal([SmartCollectionKind.Franchise, SmartCollectionKind.Decade, SmartCollectionKind.Keyword, SmartCollectionKind.Franchise], presets.Select(p => p.Kind));
		Assert.Equal("1980s", presets[1].Label); // short: the strip scrolls with up to twelve
		Assert.Equal("Heists", presets[2].Label);
		Assert.Equal("Heists, made by Nixie, keyword, 12 films", presets[2].AutomationName);
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
	[InlineData(SmartCollectionKind.HighlyRated, "Highly rated, not watched", "Highly rated")]
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
}
