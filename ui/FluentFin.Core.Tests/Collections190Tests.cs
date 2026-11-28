using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Builders;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 "Collections 2", the ui stream: the Collections page's Franchises row by relevance (the owner's library, fix 6:
/// it opened on "[REC]", "100% Wolf" and "101 Dalmatians", A to Z); See all staying A to Z; and a name two franchises share
/// said with its years ("Godzilla (1954–1974)"), alike on the page, in a pin and in what Narrator hears.
/// </summary>
[Collection(CollectionNamesSerial.Name)] // SmartCollectionServices and the catalog's log are static
public sealed class Collections190Tests : IDisposable
{
	private static readonly DateTimeOffset Tuesday = new(2026, 11, 24, 12, 0, 0, TimeSpan.FromHours(-8));
	private static int s_next;
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-190-ui-" + Guid.NewGuid().ToString("N"));
	private readonly List<HomeCache> _homes = [];

	static Collections190Tests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public void Dispose()
	{
		SmartCollectionCatalog.Log = null;
		SmartCollectionServices.Index = null;
		SmartCollectionServices.Store = null;
		foreach (var home in _homes)
		{
			home.Dispose();
		}

		TestFolder.Delete(_folder);
	}

	private static IndexEntry Film(string name, int? year, string? franchise, double? rating = 7.0, bool played = false, DateTimeOffset? last = null) => new()
	{
		Id = Guid.Parse($"{Interlocked.Increment(ref s_next):D8}-0000-0000-0000-000000000190"),
		Name = name,
		Kind = IndexKind.Movie,
		Year = year,
		TmdbCollection = franchise,
		Played = played,
		PlayCount = played || last is not null ? 1 : 0,
		LastPlayed = last,
		Rating = rating,
		Critic = 80, // something backs every rating: the scores differ by the rating
		PrimaryTag = "p",
	};

	private static IEnumerable<IndexEntry> Run(string name, string franchise, int films, double rating, int firstYear = 2000, int played = 0, DateTimeOffset? last = null) =>
		Enumerable.Range(0, films).Select(i => Film(i == 0 ? name : $"{name} {Roman(i + 1)}", firstYear + (3 * i), franchise, rating,
			played: i < played, last: i < played ? last : null));

	private static string Roman(int n) => n switch { 2 => "II", 3 => "III", 4 => "IV", 5 => "V", _ => n.ToString(System.Globalization.CultureInfo.InvariantCulture) };

	// two started with films left (Zephyr Run played four days ago, Yarrow Bay twenty), one watched whole (Willow Creek), and
	// three nobody started: three good films (Ember Falls), four average (Fox Hollow), five poor (Dust Road); three loose films
	private static List<IndexEntry> Library() =>
	[
		.. Run("Zephyr Run", "9301", 2, 7.0, played: 1, last: Tuesday.AddDays(-4)),
		.. Run("Yarrow Bay", "9302", 3, 7.0, played: 1, last: Tuesday.AddDays(-20)),
		.. Run("Willow Creek", "9303", 3, 7.0, played: 3, last: Tuesday.AddDays(-60)),
		.. Run("Ember Falls", "9304", 3, 8.5),
		.. Run("Fox Hollow", "9305", 4, 7.0),
		.. Run("Dust Road", "9306", 5, 5.0),
		Film("Amélie", 2001, null, 6.0), Film("Sintel", 2010, null, 6.0), Film("Heat", 1995, null, 6.0),
	];

	// 1.9 (the pre-release fix I, BH-190-m4): the one watched whole last, after those nobody started
	private static readonly string[] Relevance = ["Zephyr Run", "Yarrow Bay", "Ember Falls", "Fox Hollow", "Dust Road", "Willow Creek"];

	private static IndexSnapshot Index(IEnumerable<IndexEntry> entries) => new([], [.. entries], new Dictionary<Guid, SeriesPlays>(), HasPeople: true, Tuesday);

	[Fact]
	public void TheRank_PutsStartedFranchisesFirst_ThenTheLargestAndBestScored()
	{
		var index = Index(Library());
		var franchises = SmartCollectionCatalog.BuildAll(index, Tuesday).Where(x => x.Kind == SmartCollectionKind.Franchise).ToList();

		var rank = FranchiseRelevance.Rank(franchises, index);

		Assert.Equal(Relevance, franchises.OrderBy(x => rank[x.Id]).Select(x => x.Title));
	}

	// the pre-release fix I (BH-190-m4): finished franchises led the row before every one nobody started, so a user who finished
	// twelve had a row of nothing left to watch
	[Fact]
	public void FinishedFranchises_ComeAfterTheUnstarted_SoTheRowsFirstTwelveHaveSomethingToWatch()
	{
		List<IndexEntry> films = [.. Run("Going", "9400", 4, 7.0, played: 2, last: Tuesday.AddDays(-2))];
		for (var i = 0; i < 15; i++)
		{
			films.AddRange(Run($"Finished {i:D2}", $"94{10 + i}", 3, 8.0, played: 3, last: Tuesday.AddDays(-10 - i)));
		}

		for (var i = 0; i < 20; i++)
		{
			films.AddRange(Run($"Unstarted {i:D2}", $"94{30 + i}", 3, 6.0 + (i / 10.0)));
		}

		var index = Index(films);
		var franchises = SmartCollectionCatalog.BuildAll(index, Tuesday).Where(x => x.Kind == SmartCollectionKind.Franchise).ToList();
		var rank = FranchiseRelevance.Rank(franchises, index);
		var row = franchises.OrderBy(x => rank[x.Id]).Select(x => x.Title).ToList();

		Assert.Equal("Going", row[0]); // in progress first
		Assert.All(row.Skip(1).Take(20), x => Assert.StartsWith("Unstarted", x, StringComparison.Ordinal)); // then those nobody started
		Assert.Equal([.. Enumerable.Range(0, 15).Select(i => $"Finished {i:D2}")], row.Skip(21)); // then the finished, played last first
		Assert.Equal("Unstarted 19", row[1]); // the best of the unstarted first
	}

	[Fact]
	public void WithNoScores_TheLargestComeFirst_ThenAToZ()
	{
		// a server that keeps no ratings: missing data isn't a verdict, the size alone orders them
		List<IndexEntry> films = [.. Run("Cobalt", "9311", 3, 0), .. Run("Amber", "9312", 3, 0), .. Run("Basalt", "9313", 5, 0)];
		films = [.. films.Select(x => x with { Rating = null })];
		var index = Index(films);
		var franchises = SmartCollectionCatalog.BuildAll(index, Tuesday).Where(x => x.Kind == SmartCollectionKind.Franchise).ToList();

		var rank = FranchiseRelevance.Rank(franchises, index);

		Assert.Equal(["Basalt", "Amber", "Cobalt"], franchises.OrderBy(x => rank[x.Id]).Select(x => x.Title));
	}

	[Fact]
	public async Task TheFranchisesRow_IsByRelevance_AndSeeAll_StaysAToZ()
	{
		var films = Library();
		var (vm, _) = Page(films);

		await vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie));
		await vm.Reading;

		var row = Assert.Single(vm.Rows, r => r.Kind == SmartCollectionKind.Franchise);
		Assert.Equal(Relevance, row.Tiles.Select(x => x.Title));
		// Narrator hears the row's first tile as it is drawn: the franchise started last
		Assert.StartsWith("Zephyr Run, made by Nixie, franchise", row.Tiles[0].AutomationName, StringComparison.Ordinal);

		var (grid, _) = Page(films);
		await grid.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie, SmartCollectionKind.Franchise, SeeAll: true));
		await grid.Reading;

		Assert.Equal(Relevance.Order(StringComparer.CurrentCultureIgnoreCase), grid.GridTiles.Select(x => x.Title));
		Assert.Equal(OrderRuns.AToZ, grid.GridRuns);
	}

	[Fact]
	public async Task TheRowsOrder_FollowsANewerIndex_InPlace()
	{
		// Dust Road begun since: the index read again (R) puts it first, in the same row (its tile moved, not made again)
		var page = Open(Library());
		await page.Vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie));
		await page.Vm.Reading;
		var row = Assert.Single(page.Vm.Rows, r => r.Kind == SmartCollectionKind.Franchise);
		var dust = row.Tiles.Single(x => x.Title == "Dust Road");

		var begun = page.Films.Single(x => x.Name == "Dust Road");
		begun.UserData = new UserItemDataDto { Played = false, PlayCount = 1, LastPlayedDate = Tuesday.AddDays(-1) };
		await page.Index.RefreshAsync();
		await WaitFor(() => row.Tiles[0].Title == "Dust Road");

		Assert.Same(row, Assert.Single(page.Vm.Rows, r => r.Kind == SmartCollectionKind.Franchise));
		Assert.Same(dust, row.Tiles[0]);
		Assert.Equal(["Dust Road", .. Relevance.Where(x => x != "Dust Road")], row.Tiles.Select(x => x.Title));
	}

	// ---- repeated names, with their years ------------------------------------------------------------------------------------

	// three franchises named "Godzilla" by their films (one a pair nobody started: not published, but its name counts), and Kestrel Bay
	private static List<IndexEntry> Godzillas() =>
	[
		Film("Godzilla", 1954, "9201"), Film("Godzilla Raids Again", 1955, "9201"), Film("Godzilla vs. Mechagodzilla", 1974, "9201"),
		Film("Godzilla", 2014, "9202"), Film("Godzilla: King of the Monsters", 2019, "9202"), Film("Godzilla x Kong", 2024, "9202"),
		Film("Godzilla", 1998, "9203"), Film("Godzilla 2000", 1999, "9203"),
		Film("Kestrel Bay", 1990, "9204"), Film("Kestrel Bay II", 1993, "9204"), Film("Kestrel Bay III", 1996, "9204"),
	];

	[Fact]
	public void AShareName_TakesItsYears_AndAnUnsharedOneDoesnt()
	{
		var index = Index(Godzillas());

		var published = SmartCollectionCatalog.BuildAll(index, Tuesday).Where(x => x.Kind == SmartCollectionKind.Franchise).ToDictionary(x => x.Id, x => x.Title);

		Assert.Equal("Godzilla (1954–1974)", published["franchise:9201"]);
		Assert.Equal("Godzilla (2014–2024)", published["franchise:9202"]);
		Assert.Equal("Kestrel Bay", published["franchise:9204"]);
		Assert.DoesNotContain("franchise:9203", published.Keys); // a pair nobody started isn't published (1.8.1, fix 7) ...
		// ... but made by its id (a pin, a title page), it is named as the page would name it, its years too
		Assert.Equal("Godzilla (1998–1999)", SmartCollectionCatalog.Find(index, "franchise:9203", Tuesday)!.Title);
		Assert.Equal("Godzilla (1954–1974)", SmartCollectionCatalog.Find(index, "franchise:9201", Tuesday)!.Title);
		Assert.All(published.Values, x => Assert.Null(NixieTitles.Check(x)));
	}

	[Fact]
	public void TheYears_AreOneYearWhenTheFilmsShareIt_AndNoneWithoutYears()
	{
		Assert.Equal("Halloween (2018)", FranchiseNames.WithYears("Halloween", [Film("Halloween", 2018, "1"), Film("Halloween Kills", 2018, "1")]));
		Assert.Equal("Halloween", FranchiseNames.WithYears("Halloween", [Film("Halloween", null, "1"), Film("Halloween Kills", null, "1")]));
		Assert.Equal(["Batman"], FranchiseNames.Repeated(["Batman", "batman", "Alien", " ", ""]));
	}

	[Fact]
	public async Task ThePage_SaysTheYears_OnTheTileAndToNarrator()
	{
		var (vm, _) = Page(Godzillas());

		await vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie));
		await vm.Reading;

		var row = Assert.Single(vm.Rows, r => r.Kind == SmartCollectionKind.Franchise);
		var godzilla = Assert.Single(row.Tiles, x => x.Nixie!.Id == "franchise:9202");
		Assert.Equal("Godzilla (2014–2024)", godzilla.Title);
		Assert.StartsWith("Godzilla (2014–2024), made by Nixie, franchise, ", godzilla.Nixie!.TileAutomationName, StringComparison.Ordinal);
		Assert.Equal(2, row.Tiles.Count(x => x.Title.StartsWith("Godzilla (", StringComparison.Ordinal)));
	}

	// ---- the Categories row ----------------------------------------------------------------------------------------------------

	// the shipped categories whose rules are genres and years alone (a fixture fits them whatever the owner renames), sixteen of
	// them, each with nine unwatched films that fit it, and 300 films that fit nothing (so a category is no filter)
	private static (List<MicroGenre> Categories, List<IndexEntry> Films) CategoryLibrary()
	{
		// 1.9 (Phase 3, R3): genres and a country (a category of genres and a decade alone is in the row only with two played
		// titles inside, and these tests' rows have none)
		var plain = MicroGenres.Shipped.Categories.Where(x => x.Kind == IndexKind.Movie && x.Rule.GenresAll.Count > 0 && x.Rule.CountriesAny.Count > 0
			&& x.Rule is { GenresAny.Count: 0, KeywordsAny.Count: 0, KeywordsAll.Count: 0, StudiosAny.Count: 0, CommunityMin: null, CriticMin: null, TextAny.Count: 0 }
			&& x.Rule.LengthMinutes.IsOpen).Take(16).ToList();
		Assert.True(plain.Count >= 13, $"only {plain.Count} plain categories shipped");
		var films = new List<IndexEntry>();
		foreach (var category in plain)
		{
			var year = category.Rule.Years.From ?? category.Rule.Years.To ?? 2005;
			for (var i = 0; i < 9; i++)
			{
				var film = Film($"{category.Id} {i}", year, null, 6.0 + (i % 4)) with { Genres = [.. category.Rule.GenresAll], Countries = [category.Rule.CountriesAny[0]], RunTimeTicks = TimeSpan.FromMinutes(100).Ticks };
				Assert.True(MicroGenreMatcher.Fits(category, film));
				films.Add(film);
			}
		}

		films.AddRange(Enumerable.Range(0, 300).Select(i => Film($"Other {i}", 1900 + (i % 20), null, 6.5) with { Genres = ["Filler"] }));
		return (plain, films);
	}

	[Fact]
	public async Task TheCategoriesRow_IsTheLineupsWeek_FirstOfTheKinds_WithSeeAll_AndNarratorNames()
	{
		var (_, films) = CategoryLibrary();
		var page = Open(films);

		await page.Vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie));
		await page.Vm.Reading;

		var index = (await page.Index.SavedAsync())!;
		var made = SmartCollectionCatalog.BuildAll(index, DateTimeOffset.Now).Where(x => x.Kind == SmartCollectionKind.MicroGenre).ToList();
		var lineup = (await CollectionLineup.BuildAsync(index, SmartCollectionCatalog.Now(TimeProvider.System), kindsOff: new HashSet<SmartCollectionKind>(), extra: [])).Lineup;
		Assert.True(made.Count > lineup.Categories.Count && lineup.Categories.Count > 1, $"{made.Count} categories made, {lineup.Categories.Count} in the week");
		var row = Assert.Single(page.Vm.Rows, r => r.Kind == SmartCollectionKind.MicroGenre);
		Assert.Equal("Categories", row.Title);
		// the first of the kinds' rows (after This week and Picks)
		Assert.Same(row, page.Vm.Rows.First(r => r.Kind is not null));
		// the lineup's categories of the week, in its order (This week's first), and See all for every one made
		Assert.Equal(lineup.Categories.Select(x => x.Id).Take(CollectionsViewModel.RowLength), row.Tiles.Select(x => x.Nixie!.Id));
		Assert.True(row.HasSeeAll);
		var week = page.Vm.Rows.Single(r => r.Title == CollectionsViewModel.ThisWeekTitle).Tiles.Where(x => x.Nixie!.Kind == SmartCollectionKind.MicroGenre).Select(x => x.Nixie!.Id).ToList();
		Assert.Equal(week, row.Tiles.Take(week.Count).Select(x => x.Nixie!.Id));
		Assert.NotEqual(made.Take(row.Tiles.Count).Select(x => x.Id), row.Tiles.Select(x => x.Nixie!.Id)); // not the catalogue's first
		// Narrator: "<name>, made by Nixie, category, <reason> · <count>"
		Assert.All(row.Tiles, x => Assert.Contains(", made by Nixie, category, ", x.Nixie!.TileAutomationName, StringComparison.Ordinal));
		Assert.All(row.Tiles, x => Assert.Null(NixieTitles.Check(x.Title)));
	}

	// the owner, trying the 1.9 test copy: "i dont think the categories should be alphabetical": See all opens For you, A to Z is
	// the other choice, the direction button reverses either, and the choice is kept (R makes a new page that recalls it). The
	// critical review's cut 1 (the owner: the long list "isnt very useful"): with no watch history, as here, See all is the row's
	// twelve, in the row's order, not every category made
	[Fact]
	public async Task SeeAll_WithNoHistory_IsTheRowsTwelve_ForYou_AToZWhenChosen_KeptForR()
	{
		var (_, films) = CategoryLibrary();
		var place = new CollectionsPlace(CollectionsSection.MadeByNixie, SmartCollectionKind.MicroGenre, SeeAll: true);
		var page = Open(films);
		await page.Vm.OnNavigatedTo(place);
		await page.Vm.Reading;
		var index = (await page.Index.SavedAsync())!;
		var made = SmartCollectionCatalog.BuildAll(index, DateTimeOffset.Now).Where(x => x.Kind == SmartCollectionKind.MicroGenre).Select(x => x.Id).ToList();
		var lineup = (await CollectionLineup.BuildAsync(index, SmartCollectionCatalog.Now(TimeProvider.System), kindsOff: new HashSet<SmartCollectionKind>(), extra: [])).Lineup;
		var row = lineup.Categories.Select(x => x.Id).Take(CollectionsViewModel.RowLength).ToList();
		try
		{
			Assert.Equal("Categories", page.Vm.Title);
			Assert.Empty(lineup.CategoryRanking); // nothing watched lifts a category
			Assert.True(made.Count > CollectionsViewModel.RowLength, $"only {made.Count} categories made");
			// For you: exactly the row (twelve at most; here fewer, one a family), in its order (the page's own row is the same:
			// TheCategoriesRow_... checks it), not every category made
			var shown = page.Vm.GridTiles.Select(x => x.Nixie!.Id).ToList();
			Assert.True(row.Count > 1 && row.Count < made.Count, $"the row has {row.Count} of {made.Count}");
			Assert.Equal(row, shown);
			var ranked = row;
			var titles = page.Vm.GridTiles.Select(x => x.Title).ToList();
			Assert.NotEqual(titles.Order(StringComparer.CurrentCultureIgnoreCase), titles);
			Assert.True(page.Vm.HasGridOrder);
			Assert.Equal(OrderRuns.ForYou, page.Vm.GridRuns);
			Assert.Equal(("For you", "For you, switch to A to Z", "Switch to A to Z"), (page.Vm.GridOrderWords, page.Vm.GridOrderName, page.Vm.GridOrderTip));
			Assert.Equal("Best fit first, switch to best fit last", page.Vm.GridDirectionName);

			// A to Z when chosen, and reversed by the direction button
			page.Vm.SwitchGridOrder();
			Assert.Equal(titles.Order(StringComparer.CurrentCultureIgnoreCase), page.Vm.GridTiles.Select(x => x.Title));
			Assert.Equal(("A to Z", "A to Z, switch to For you"), (page.Vm.GridOrderWords, page.Vm.GridOrderName));
			Assert.Equal(OrderRuns.AToZ, page.Vm.GridRuns);
			Assert.Equal("A to Z, switch to Z to A", page.Vm.GridDirectionName);
			page.Vm.ReverseGrid();
			Assert.Equal(titles.Order(StringComparer.CurrentCultureIgnoreCase).Reverse(), page.Vm.GridTiles.Select(x => x.Title));

			// R: a new page recalls A to Z and the direction
			var again = Open(films);
			await again.Vm.OnNavigatedTo(place);
			await again.Vm.Reading;
			Assert.True(again.Vm.GridAToZ);
			Assert.Equal(titles.Order(StringComparer.CurrentCultureIgnoreCase).Reverse(), again.Vm.GridTiles.Select(x => x.Title));

			// and For you again, the direction too: the lineup's ranking
			again.Vm.SwitchGridOrder();
			again.Vm.ReverseGrid();
			Assert.Equal(ranked, again.Vm.GridTiles.Select(x => x.Nixie!.Id).Take(ranked.Count));

			// Franchises' See all stays A to Z, with no order button
			var franchises = Open(films);
			await franchises.Vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie, SmartCollectionKind.Franchise, SeeAll: true));
			Assert.False(franchises.Vm.HasGridOrder);
			Assert.Equal(OrderRuns.AToZ, franchises.Vm.GridRuns);
		}
		finally
		{
			SortDirections.Remember(page.Vm.JellyfinClient.UserId, SortDirections.ForGrid(CollectionsSection.MadeByNixie, SmartCollectionKind.MicroGenre), false);
			SortDirections.Remember(page.Vm.JellyfinClient.UserId, SortDirections.ForGrid(CollectionsSection.MadeByNixie, SmartCollectionKind.MicroGenre) + ":order", false);
		}
	}

	// the critical review's cut 1: the user watched three more films of the first category, two of the second and one of the
	// third (lifts about 2.3, 1.9 and 1.4; each keeps its nine unwatched): See all holds the first two (a lift of 1.5 or more)
	// and the week's explore pick, nothing else
	private static (List<MicroGenre> Categories, List<IndexEntry> Films) CategoryLibraryWatched()
	{
		var (categories, films) = CategoryLibrary();
		for (var c = 0; c < 3; c++)
		{
			var like = films.First(f => f.Name == $"{categories[c].Id} 0");
			for (var i = 0; i < 3 - c; i++)
			{
				films.Add(Film($"{categories[c].Id} watched {i}", like.Year, null, 7.0, played: true, last: Tuesday.AddDays(-3)) with { Genres = like.Genres, Countries = like.Countries, RunTimeTicks = like.RunTimeTicks });
			}
		}

		return (categories, films);
	}

	[Fact]
	public async Task SeeAll_WithHistory_HoldsOnlyTheCategoriesItLifts_AndTheExplorePick_InFitOrder()
	{
		var (categories, films) = CategoryLibraryWatched();
		var index = Index(films);
		var lineup = (await CollectionLineup.BuildAsync(index, Tuesday, kindsOff: new HashSet<SmartCollectionKind>(), extra: [])).Lineup;
		var made = SmartCollectionCatalog.BuildAll(index, Tuesday).Where(x => x.Kind == SmartCollectionKind.MicroGenre).Select(x => x.Id).ToHashSet();
		string Id(int i) => made.Single(x => x == $"micro:{categories[i].Id}");
		Assert.True(made.Count > CollectionsViewModel.RowLength, $"only {made.Count} categories made");

		// the lineup: the two lifted, the more lifted first, then the explore pick (nothing watched lifts it); not the third (1.4)
		Assert.NotNull(lineup.Explore);
		Assert.Equal([Id(0), Id(1), lineup.Explore!], lineup.CategoryRanking);
		Assert.DoesNotContain(Id(2), lineup.CategoryRanking);

		// the page's See all: those three, in that order; A to Z sorts the same three
		var place = new CollectionsPlace(CollectionsSection.MadeByNixie, SmartCollectionKind.MicroGenre, SeeAll: true);
		var page = Open(films);
		await page.Vm.OnNavigatedTo(place);
		await page.Vm.Reading;
		var shown = page.Vm.GridTiles.Select(x => x.Nixie!.Id).ToList();
		Assert.Equal(3, shown.Count);
		Assert.Equal([Id(0), Id(1)], shown.Take(2));
		var seen = (await page.Index.SavedAsync())!;
		var pageLineup = (await CollectionLineup.BuildAsync(seen, SmartCollectionCatalog.Now(TimeProvider.System), kindsOff: new HashSet<SmartCollectionKind>(), extra: [])).Lineup;
		Assert.Equal(pageLineup.CategoryRanking, shown);
	}

	[Fact]
	public async Task SeeAllsRanking_HasNoWeeklyDraw_TheSameLiftedOnesInTheSameOrder_WeekAfterWeek()
	{
		// the review: "For you" was mostly the week's draw (0.6 to 1) where quality is flat; with no draw the lifted ones keep
		// their order whatever the week (the explore pick is the week's own)
		var (_, films) = CategoryLibraryWatched();
		var index = Index(films);
		List<IReadOnlyList<string>> weeks = [];
		foreach (var days in new[] { 0, 7, 14, 21, 35 })
		{
			var lineup = (await CollectionLineup.BuildAsync(index, Tuesday.AddDays(days), kindsOff: new HashSet<SmartCollectionKind>(), extra: [])).Lineup;
			weeks.Add([.. lineup.CategoryRanking.Where(x => x != lineup.Explore)]);
		}

		Assert.Equal(2, weeks[0].Count);
		Assert.All(weeks, x => Assert.Equal(weeks[0], x));
	}

	// 1.9 (Phase 3, R1): the page's More like row is the lineup's: one a title played lately, the newest first, and none that
	// opens on the posters a category of the row opens on (here the first category's own films: the film played two days ago is
	// one of it, and its likeness is that category); See all still has every one
	[Fact]
	public async Task TheMoreLikeRow_IsTheLineups_NoneOpeningOnARowCategorysPosters()
	{
		var (categories, films) = CategoryLibrary();
		var first = films.First(f => f.Name == $"{categories[0].Id} 0");
		films.Add(Film($"{categories[0].Id} seen lately", first.Year, null, 7.0, played: true, last: DateTimeOffset.Now.AddDays(-2)) with { Genres = first.Genres, Countries = first.Countries, RunTimeTicks = first.RunTimeTicks });
		films.Add(Film($"{categories[0].Id} seen before", first.Year, null, 7.0, played: true, last: DateTimeOffset.Now.AddDays(-200)) with { Genres = first.Genres, Countries = first.Countries, RunTimeTicks = first.RunTimeTicks });
		var page = Open(films);

		await page.Vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie));
		await page.Vm.Reading;

		var index = (await page.Index.SavedAsync())!;
		var build = await CollectionLineup.BuildAsync(index, SmartCollectionCatalog.Now(TimeProvider.System), kindsOff: new HashSet<SmartCollectionKind>(), extra: []);
		var likes = build.All.Where(x => x.Kind == SmartCollectionKind.MoreLike).Select(x => x.Id).ToList();
		var lately = Assert.Single(likes, x => x == $"like:{films[^2].Id:N}");
		Assert.Equal($"micro:{categories[0].Id}", build.Lineup.WeekOrder[0]); // reached through the film seen lately
		Assert.Contains(build.Lineup.Categories, x => x.Id == $"micro:{categories[0].Id}"); // (This week holds its More like instead)
		Assert.DoesNotContain(lately, build.Lineup.MoreLike); // it opens on that category's films
		Assert.NotEmpty(build.Lineup.MoreLike);

		var row = Assert.Single(page.Vm.Rows, r => r.Kind == SmartCollectionKind.MoreLike);
		Assert.Equal(build.Lineup.MoreLike, row.Tiles.Select(x => x.Nixie!.Id));
		Assert.True(row.HasSeeAll); // every one in See all
	}

	[Fact]
	public async Task TheCategoriesSwitch_IsInSettings_AndOffTakesTheRowAndThisWeeksCategoriesAway_BackKeepsTheRest()
	{
		var (_, films) = CategoryLibrary();
		var settings = new MemorySettings();
		var switches = new CollectionsSettingsViewModel(settings);
		var categories = switches.Kinds[0];
		Assert.Equal("Categories", categories.Name);
		Assert.Equal(CollectionsSettingsViewModel.CategoriesText, categories.Description);
		Assert.True(categories.IsOn);

		var page = Open(films, settings);
		await page.Vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie));
		await page.Vm.Reading;
		var row = Assert.Single(page.Vm.Rows, r => r.Kind == SmartCollectionKind.MicroGenre);
		var tiles = row.Tiles.ToList();

		// Back with nothing changed: the same row and the same tiles, in their places (the keyboard's tile is still there)
		await page.Vm.OnNavigatedFrom();
		await page.Vm.Returned(new CollectionsPlace(CollectionsSection.MadeByNixie), stale: false);
		Assert.Same(row, Assert.Single(page.Vm.Rows, r => r.Kind == SmartCollectionKind.MicroGenre));
		Assert.Equal(tiles, row.Tiles);

		// turned off in Settings meanwhile: Back shows the page without the row, and This week holds no category
		await page.Vm.OnNavigatedFrom();
		categories.IsOn = false;
		Assert.Contains(SmartCollectionKind.MicroGenre, CollectionSettings.KindsOff(settings));
		await page.Vm.Returned(new CollectionsPlace(CollectionsSection.MadeByNixie), stale: false);
		Assert.DoesNotContain(page.Vm.Rows, r => r.Kind == SmartCollectionKind.MicroGenre);
		Assert.DoesNotContain(page.Vm.Rows.SelectMany(r => r.Tiles), x => x.Nixie?.Kind == SmartCollectionKind.MicroGenre);
		Assert.Contains(page.Vm.Rows, r => r.Kind == SmartCollectionKind.Franchise || r.Title == "Picks" || r.Title == CollectionsViewModel.ThisWeekTitle);

		// and on again: the row comes back
		await page.Vm.OnNavigatedFrom();
		categories.IsOn = true;
		await page.Vm.Returned(new CollectionsPlace(CollectionsSection.MadeByNixie), stale: false);
		Assert.Single(page.Vm.Rows, r => r.Kind == SmartCollectionKind.MicroGenre);
	}

	// wire's review (Minor 9): every 1.8.1 kind off was Made by Nixie off; the categories, a new kind on by default, came on after
	// the update and the page stopped saying every kind is off
	[Fact]
	public async Task EveryKindOffIn181_KeepsMadeByNixieOff_TheCategoriesToo_Once()
	{
		var (_, films) = CategoryLibrary();
		var settings = new MemorySettings();
		var off181 = string.Join(",", CollectionSettings.Kinds181);
		settings.SaveSetting(CollectionSettingKeys.KindsOff.Name, off181);

		CollectionSettings.Migrate(settings); // the first start of 1.9

		Assert.Contains(SmartCollectionKind.MicroGenre, CollectionSettings.KindsOff(settings));
		Assert.False(new CollectionsSettingsViewModel(settings).Kinds.Single(x => x.Name == "Categories").IsOn);
		var page = Open(films, settings);
		await page.Vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie));
		await page.Vm.Reading;
		Assert.DoesNotContain(page.Vm.Rows, r => r.Section == CollectionsSection.MadeByNixie);
		Assert.True(page.Vm.ShowsReadingLine);
		Assert.Equal(CollectionsViewModel.NixieOffText, page.Vm.ReadingText);

		// once: the user turns Categories on afterwards, everything else still off: the next start keeps it on
		new CollectionsSettingsViewModel(settings).Kinds.Single(x => x.Name == "Categories").IsOn = true;
		CollectionSettings.Migrate(settings);
		Assert.DoesNotContain(SmartCollectionKind.MicroGenre, CollectionSettings.KindsOff(settings));
	}

	[Fact]
	public void SomeKindOnIn181_OrAFreshStart_LeavesTheCategoriesOn()
	{
		var some = new MemorySettings();
		some.SaveSetting(CollectionSettingKeys.KindsOff.Name, string.Join(",", CollectionSettings.Kinds181.Skip(1))); // Franchises still on
		CollectionSettings.Migrate(some);
		Assert.DoesNotContain(SmartCollectionKind.MicroGenre, CollectionSettings.KindsOff(some));

		var fresh = new MemorySettings();
		CollectionSettings.Migrate(fresh);
		Assert.Empty(CollectionSettings.KindsOff(fresh));
		// and later, every kind off but the categories, by the user's hand: never taken away at a start
		fresh.SaveSetting(CollectionSettingKeys.KindsOff.Name, string.Join(",", CollectionSettings.Kinds181));
		CollectionSettings.Migrate(fresh);
		Assert.DoesNotContain(SmartCollectionKind.MicroGenre, CollectionSettings.KindsOff(fresh));
	}

	// the text stream's step 1b: the shows' summaries matched anew (the app's pass, or deleted with the categories off): the page
	// makes its collections again while it shows, from the index it has (Home marks itself stale on the same news)
	[Fact]
	public async Task NewSummaryMatches_MakeThePageAgain_WhileItShows_NotAfter()
	{
		var made = 0;
		var page = Open(Library(), making: _ => { Interlocked.Increment(ref made); return Task.CompletedTask; });
		await page.Vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie));
		await page.Vm.Reading;
		await page.Vm.SpotlightLater;
		await Task.Delay(300);
		var before = Volatile.Read(ref made);
		try
		{
			TextMatches.Say(); // what the app's pass says when its matches change
			await WaitFor(() => Volatile.Read(ref made) > before);

			await page.Vm.OnNavigatedFrom();
			await Task.Delay(300);
			var left = Volatile.Read(ref made);
			TextMatches.Say();
			await Task.Delay(500);
			Assert.Equal(left, Volatile.Read(ref made)); // a page left listens no more
		}
		finally
		{
			TextMatches.Say();
		}
	}

	// the review's Minor 2 (its probe D): the kept page left before the pass's new matches came listens to none of them; Back
	// showed the page made of the old ones until R
	[Fact]
	public async Task Back_AfterNewSummaryMatchesWhileAway_MakesThePageAgain()
	{
		var made = 0;
		var page = Open(Library(), making: _ => { Interlocked.Increment(ref made); return Task.CompletedTask; });
		await page.Vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie));
		await page.Vm.Reading;
		await page.Vm.SpotlightLater;
		await Task.Delay(300);
		await page.Vm.OnNavigatedFrom();
		var before = Volatile.Read(ref made);
		try
		{
			// the app's pass finished while the user was on a title page: new matches
			TextMatches.Put(new TextMatches("k", "f", DateTimeOffset.UtcNow, new Dictionary<Guid, string[]?> { [Guid.NewGuid()] = ["tv-space"] }));
			TextMatches.Say();
			await page.Vm.Returned(new CollectionsPlace(CollectionsSection.MadeByNixie), stale: false);
			await Task.Delay(600);
			Assert.True(Volatile.Read(ref made) > before, "Back after new summary matches showed the page made from the old ones");

			// and Back with nothing new since: the page as it was, nothing made again
			await page.Vm.OnNavigatedFrom();
			var kept = Volatile.Read(ref made);
			await page.Vm.Returned(new CollectionsPlace(CollectionsSection.MadeByNixie), stale: false);
			await Task.Delay(300);
			Assert.Equal(kept, Volatile.Read(ref made));
		}
		finally
		{
			TextMatches.Put(TextMatches.Empty);
		}
	}

	private static async Task WaitFor(Func<bool> done)
	{
		for (var i = 0; i < 200 && !done(); i++)
		{
			await Task.Delay(25);
		}

		Assert.True(done());
	}

	private sealed record OpenPage(CollectionsViewModel Vm, LibraryIndex Index, List<BaseItemDto> Films, MemorySettings Settings);

	private (CollectionsViewModel Vm, LibraryIndex Index) Page(IReadOnlyList<IndexEntry> films)
	{
		var page = Open(films);
		return (page.Vm, page.Index);
	}

	/// <summary>Settings in memory: what was saved, else the default asked for (as LocalSettingsService answers).</summary>
	private sealed class MemorySettings : ILocalSettingsService
	{
		private readonly Dictionary<string, object?> _values = [];

		public System.Text.Json.Nodes.JsonNode? ReadSettingRaw(string key) => null;

		public T ReadSetting<T>(string key, T defaultValue) => _values.TryGetValue(key, out var v) && v is T t ? t : defaultValue;

		public void SaveSetting<T>(string key, T value) => _values[key] = value;

		public void RemoveSetting(string key) => _values.Remove(key);

		public byte[] GetEntropyBytes() => [];
	}

	// the Collections page on a fake server with these films (its index read through the mocked client); the films' records
	// can change before the index is read again
	private OpenPage Open(IReadOnlyList<IndexEntry> films, MemorySettings? settings = null, Func<IndexSnapshot, Task>? making = null)
	{
		settings ??= new MemorySettings();
		var client = new Mock<IJellyfinClient>();
		client.SetupGet(x => x.BaseUrl).Returns("http://server");
		client.SetupGet(x => x.UserId).Returns(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000190"));
		var movies = new BaseItemDto { Id = Guid.NewGuid(), Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([movies]);
		client.Setup(x => x.GetUserViews()).ReturnsAsync([movies]);
		List<BaseItemDto> dtos = [.. films.Select(f => new BaseItemDto
		{
			Id = f.Id,
			Name = f.Name,
			Type = BaseItemDto_Type.Movie,
			ProductionYear = f.Year,
			Genres = [.. f.Genres],
			ProductionLocations = [.. f.Countries], // 1.9 (Phase 3): the country categories of CategoryLibrary
			RunTimeTicks = f.RunTimeTicks,
			CommunityRating = f.Rating is { } r ? (float)r : null,
			CriticRating = f.Critic is { } c ? (float)c : null,
			ProviderIds = f.TmdbCollection is null ? null : new BaseItemDto_ProviderIds { AdditionalData = new Dictionary<string, object> { ["TmdbCollection"] = f.TmdbCollection } },
			ImageTags = new BaseItemDto_ImageTags { AdditionalData = new Dictionary<string, object> { ["Primary"] = "p" } },
			UserData = new UserItemDataDto { Played = f.Played, PlayCount = f.PlayCount, LastPlayedDate = f.LastPlayed },
		})];
		client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto _, int start, int limit, bool _, CancellationToken _) => new BaseItemDtoQueryResult { Items = [.. dtos.Skip(start).Take(limit)], TotalRecordCount = dtos.Count });
		client.Setup(x => x.GetLibraryPeoplePage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 });
		client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		var folder = Path.Combine(_folder, Guid.NewGuid().ToString("N"));
		var home = new HomeCache(Path.Combine(folder, "HomeCache"), new Subject<IOutboundWebSocketMessage>(), new CoreListLogger<HomeCache>(), TimeProvider.System);
		_homes.Add(home);
		// (no people step: its index, a moment after the titles', would show the page again behind a test's next step)
		var index = new LibraryIndex(client.Object, home, Path.Combine(folder, "LibraryIndex"), new Subject<IOutboundWebSocketMessage>(), new CoreListLogger<LibraryIndex>(), TimeProvider.System)
		{
			PeopleWanted = () => false,
			WholeReadEvery = TimeSpan.Zero, // 1.9 (the index's load): R reads every title again, as before (a play's marks come with it)
		};
		var store = new SmartCollectionStore(client.Object, home, Path.Combine(folder, "SmartCollections"), new CoreListLogger<SmartCollectionStore>());
		var commands = new GlobalCommands(Mock.Of<INavigationServiceCore>(), client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());
		return new OpenPage(new CollectionsViewModel(client.Object, commands, index, store, settings) { Making = making }, index, dtos, settings);
	}
}
