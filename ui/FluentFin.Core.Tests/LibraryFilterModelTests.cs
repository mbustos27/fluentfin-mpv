using FluentFin.Core.Contracts.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.4.0, the library stream (the UX review's items 2, 8 and 13): one filter model, visible and honest (OR within a
/// kind, AND across kinds; counts against the other filters; the filters on as chips; nothing matching said), a sort that says
/// what it does (each key's natural direction, the direction in words, the key picked again turns it), and the A to Z in one
/// shape with the letter in view, a position readout, type-to-jump and each library's view.
/// </summary>
public sealed class LibraryFilterModelTests
{
	static LibraryFilterModelTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	private static readonly BaseItemDto Movies = new() { Id = Guid.NewGuid(), Name = "Movies", CollectionType = BaseItemDto_CollectionType.Movies };

	private static BaseItemDto Movie(string name, string[]? genres = null, int? year = null, bool played = false, bool favorite = false, string? rating = null) => new()
	{
		Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Movie, Genres = [.. genres ?? []], ProductionYear = year, OfficialRating = rating,
		UserData = new UserItemDataDto { Played = played, IsFavorite = favorite },
	};

	private static async Task<LibraryViewModel> Library(IEnumerable<BaseItemDto> items, BaseItemDto? library = null, ILocalSettingsService? settings = null)
	{
		var client = new Mock<IJellyfinClient>();
		client.SetupGet(x => x.UserId).Returns(Guid.NewGuid());
		client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [.. items] });
		var vm = new LibraryViewModel(client.Object, settings);
		await vm.OnNavigatedTo(library ?? Movies);
		return vm;
	}

	private static BaseItemDto[] Sample() =>
	[
		Movie("Alpha", ["Comedy"], 1999, played: true),
		Movie("Bravo", ["Drama"], 1999),
		Movie("Charlie", ["Comedy", "Drama"], 2004),
		Movie("Delta", ["Horror"], 2004, favorite: true),
		Movie("Echo", ["Comedy"], 2010),
	];

	// ---- item 2: OR within a kind, AND across kinds

	[Fact]
	public async Task TwoGenres_AreEither_AndAYear_NarrowsThem()
	{
		var vm = await Library(Sample());

		vm.Filter.Genres.Add("Comedy");
		vm.Filter.Genres.Add("Horror");
		Assert.Equal(["Alpha", "Charlie", "Delta", "Echo"], vm.Items.Select(x => x.Name));

		vm.Filter.Years.Add("2004");
		Assert.Equal(["Charlie", "Delta"], vm.Items.Select(x => x.Name));
	}

	[Fact]
	public async Task TheCounts_AreAgainstTheOtherFilters_AndAValueThatLeavesNothing_IsGreyed()
	{
		var vm = await Library(Sample());
		var genres = vm.Facets.Single(f => f.Group == FilterGroup.Genres);
		var years = vm.Facets.Single(f => f.Group == FilterGroup.Years);
		Assert.Equal(3, genres.Options.Single(o => o.Value == "Comedy").Count);

		vm.Filter.Years.Add("1999");

		// the genres count what 1999 leaves; their own picks do not narrow them (OR within)
		Assert.Equal(("Comedy (1)", "Drama (1)", 0), (genres.Options.Single(o => o.Value == "Comedy").Label, genres.Options.Single(o => o.Value == "Drama").Label,
			genres.Options.Single(o => o.Value == "Horror").Count));
		Assert.False(genres.Options.Single(o => o.Value == "Horror").IsAvailable);
		// the years count against the genres (none picked): every year keeps its count, 1999 picked stays available
		Assert.Equal(2, years.Options.Single(o => o.Value == "2004").Count);
		Assert.True(years.Options.Single(o => o.Value == "1999").IsAvailable);

		vm.Filter.Unwatched = true;
		Assert.Equal(0, genres.Options.Single(o => o.Value == "Comedy").Count);
		Assert.Equal(2, years.Options.Single(o => o.Value == "2004").Count); // Charlie and Delta, both unwatched
	}

	[Fact]
	public async Task TheUnwatchedCount_FollowsTheOtherFilters()
	{
		var vm = await Library(Sample());
		Assert.Equal(4, vm.UnwatchedCount);

		vm.Filter.Genres.Add("Comedy");
		Assert.Equal(2, vm.UnwatchedCount);
	}

	[Fact]
	public async Task TheFiltersOn_AreChips_EachTakesItselfOff_AndTheCountLineSaysWhatIsShown()
	{
		var vm = await Library(Sample());
		Assert.False(vm.HasAppliedFilters);

		vm.Filter.Unwatched = true;
		vm.Filter.Genres.Add("Comedy");
		vm.Filter.OfficialRatings.Add("PG");

		Assert.Equal(["Unwatched", "Comedy", "Rated PG"], vm.AppliedFilters.Select(f => f.Label));
		Assert.Equal("Remove filter Comedy", vm.AppliedFilters[1].Name);
		Assert.True(vm.HasAppliedFilters);

		vm.RemoveFilter(vm.AppliedFilters.Single(f => f.Label == "Rated PG"));
		Assert.Equal(["Unwatched", "Comedy"], vm.AppliedFilters.Select(f => f.Label));
		Assert.Equal("Showing 2 of 5 movies", vm.ShowingText);

		vm.RemoveFilter(vm.AppliedFilters[0]);
		Assert.False(vm.Filter.Unwatched);
		vm.ClearFilters();
		Assert.Empty(vm.AppliedFilters);
		Assert.Equal("Showing 5 movies", vm.ShowingText);
	}

	[Fact]
	public async Task AFilterThatLeavesNothing_SaysSo_NotABlankGrid()
	{
		var vm = await Library(Sample());
		Assert.False(vm.NoMatches);

		vm.Filter.Genres.Add("Comedy");
		vm.Filter.Favorites = true;

		Assert.True(vm.NoMatches);
		Assert.Equal("No favorite movies in Comedy", vm.NoMatchesText);
		Assert.Equal(LetterRowMode.None, vm.LetterRow);
		Assert.Equal(LoadStatus.Loaded, vm.State.Status); // not Empty: the library has titles

		vm.Filter.Genres.Add("Horror"); // Comedy or Horror: Delta
		Assert.False(vm.NoMatches);
	}

	[Theory]
	[InlineData(true, false, new[] { "Comedy" }, new string[0], "No unwatched movies in Comedy")]
	[InlineData(true, true, new string[0], new string[0], "No unwatched favorite movies")]
	[InlineData(false, false, new[] { "Comedy", "Drama", "War" }, new string[0], "No movies in Comedy, Drama or War")]
	[InlineData(false, false, new string[0], new[] { "1999" }, "No movies from 1999")]
	[InlineData(false, false, new[] { "Comedy" }, new[] { "1999" }, "No movies match these filters")]
	public void TheNoMatchesWords(bool unwatched, bool favorites, string[] genres, string[] years, string expected)
	{
		var filter = new LibraryFilter { Unwatched = unwatched, Favorites = favorites };
		foreach (var genre in genres)
		{
			filter.Genres.Add(genre);
		}

		foreach (var year in years)
		{
			filter.Years.Add(year);
		}

		Assert.Equal(expected, LibraryNoMatches.Title(filter, BaseItemDto_CollectionType.Movies));
	}

	// ---- item 8: a sort that says what it does

	[Theory]
	[InlineData(ItemSortBy.Name, SortOrder.Ascending, "Title · A to Z")]
	[InlineData(ItemSortBy.DateCreated, SortOrder.Descending, "Date added · newest first")]
	[InlineData(ItemSortBy.PremiereDate, SortOrder.Descending, "Release date · newest first")]
	[InlineData(ItemSortBy.DatePlayed, SortOrder.Descending, "Date played · newest first")]
	[InlineData(ItemSortBy.CommunityRating, SortOrder.Descending, "Community rating · highest first")]
	[InlineData(ItemSortBy.CriticRating, SortOrder.Descending, "Critic rating · highest first")]
	[InlineData(ItemSortBy.PlayCount, SortOrder.Descending, "Play count · most played first")]
	[InlineData(ItemSortBy.Runtime, SortOrder.Ascending, "Runtime · shortest first")]
	public void EachKey_OpensInItsNaturalDirection_Said(ItemSortBy key, SortOrder natural, string words)
	{
		Assert.Equal(natural, LibraryBrowse.NaturalOrder(key));
		Assert.Equal(words, LibraryBrowse.SortText(key, natural));
	}

	[Fact]
	public async Task ANewKey_TakesItsDirection_TheKeyInUsePickedAgain_TurnsIt()
	{
		var vm = await Library(Sample());
		vm.UpdateSortOrder(SortOrder.Descending);

		vm.UpdateSortBy(ItemSortBy.DateCreated);
		Assert.Equal(SortOrder.Descending, vm.Order);
		vm.UpdateSortBy(ItemSortBy.DateCreated);
		Assert.Equal((SortOrder.Ascending, "Date added · oldest first", "Oldest first, switch to newest first"), (vm.Order, vm.SortText, vm.SortOrderButtonName));

		vm.UpdateSortBy(ItemSortBy.Name);
		Assert.Equal(SortOrder.Ascending, vm.Order);
		vm.ToggleSortOrder();
		Assert.Equal(("Title · Z to A", "Z to A", "A to Z"), (vm.SortText, vm.DirectionWords, vm.OtherDirectionWords));

		vm.SetSort(ItemSortBy.DateCreated, SortOrder.Descending);
		vm.SetSort(ItemSortBy.DateCreated, SortOrder.Descending);
		Assert.Equal(SortOrder.Descending, vm.Order); // a preset never turns
	}

	// ---- item 13: the A to Z, the letter in view, where the view is, type-to-jump

	private static IEnumerable<BaseItemDto> Many(int count) =>
		Enumerable.Range(0, count).Select(i => Movie($"{(char)('A' + i % 20)}{i:D4}"));

	[Fact]
	public async Task TheLetters_ShowFrom48Titles_SortedByTitle_ElseTheRowHoldsTheDirections()
	{
		var few = await Library(Many(47));
		Assert.Equal(LetterRowMode.Directions, few.LetterRow);
		Assert.Empty(few.LetterTiles);

		var many = await Library(Many(48));
		Assert.Equal(LetterRowMode.Letters, many.LetterRow);

		many.UpdateSortBy(ItemSortBy.DateCreated);
		Assert.Equal((LetterRowMode.Directions, true), (many.LetterRow, many.ShowsDirections));
		Assert.Empty(many.LetterTiles);
	}

	[Fact]
	public async Task TheLetters_AreAlwaysHashAndAToZ_TheAbsentOnesResting_InOneShape()
	{
		var vm = await Library(Many(60));

		Assert.Equal("#ABCDEFGHIJKLMNOPQRSTUVWXYZ", string.Concat(vm.LetterTiles.Select(t => t.Letter)));
		Assert.All(vm.LetterTiles.Where(t => t.Letter is "#" or "U" or "Z"), t => Assert.False(t.HasTitles));
		var m = vm.LetterTiles.Single(t => t.Letter == "M");
		Assert.Equal((true, "Jump to M", 13, 20), (m.HasTitles, m.Name, m.Position, m.Size));
		Assert.Equal("No titles start with Z", vm.LetterTiles.Single(t => t.Letter == "Z").Name);
	}

	[Fact]
	public async Task TheLetterInView_IsMarkedOnItsTile_AndTheReadoutSaysWhere()
	{
		var vm = await Library(Enumerable.Range(0, 1000).Select(i => Movie($"{(char)('A' + i / 40)} {i:D4}")));

		vm.ShowingFrom(323);

		Assert.Equal("I", vm.CurrentLetter);
		Assert.Equal("I · 324 of 1,000", vm.PositionText);
		Assert.Equal(["I"], vm.LetterTiles.Where(t => t.IsCurrent).Select(t => t.Letter));

		vm.UpdateSortBy(ItemSortBy.DateCreated);
		vm.ShowingFrom(0);
		Assert.Equal((null, "1 of 1,000"), (vm.CurrentLetter, vm.PositionText));
	}

	[Theory]
	[InlineData("m", 0, 2)]
	[InlineData("MAT", 0, 2)]
	[InlineData("the m", 0, 2)]
	[InlineData("e", 0, 3)]
	[InlineData("a", 1, 0)]
	[InlineData("zz", 0, null)]
	public void TypeToJump_FindsTheFirstTitleFromHere_ByItsSortNameOrName(string typed, int from, int? expected)
	{
		BaseItemViewModel[] items =
		[
			BaseItemViewModel.FromDto(new BaseItemDto { Id = Guid.NewGuid(), Name = "Alien" }),
			BaseItemViewModel.FromDto(new BaseItemDto { Id = Guid.NewGuid(), Name = "Brazil" }),
			BaseItemViewModel.FromDto(new BaseItemDto { Id = Guid.NewGuid(), Name = "The Matrix", SortName = "Matrix" }),
			BaseItemViewModel.FromDto(new BaseItemDto { Id = Guid.NewGuid(), Name = "Éclair" }),
		];

		Assert.Equal(expected, LibraryBrowse.TypeAhead(items, typed, from));
	}

	// ---- items 12 and 13: each library's view, kept

	[Fact]
	public async Task EachLibrarysView_IsKept_ByLibrary()
	{
		var store = new Dictionary<string, string>();
		var settings = new Mock<ILocalSettingsService>();
		settings.Setup(x => x.ReadSetting(LibraryViews.Key, It.IsAny<string>())).Returns(() => store.GetValueOrDefault(LibraryViews.Key, ""));
		settings.Setup(x => x.SaveSetting(LibraryViews.Key, It.IsAny<string>())).Callback<string, string>((k, v) => store[k] = v);

		var shows = new BaseItemDto { Id = Guid.NewGuid(), Name = "Shows", CollectionType = BaseItemDto_CollectionType.Tvshows };
		var vm = await Library(Sample(), Movies, settings.Object);
		Assert.Equal((LibraryDensity.Medium, LibraryLayout.Grid, LibraryArt.Stills), (vm.Density, vm.Layout, vm.Art));
		vm.SetDensity(LibraryDensity.Small);
		vm.SetLayout(LibraryLayout.List);
		vm.SetArt(LibraryArt.Posters);

		var again = await Library(Sample(), Movies, settings.Object);
		Assert.Equal((LibraryDensity.Small, LibraryLayout.List, LibraryArt.Posters, true), (again.Density, again.Layout, again.Art, again.IsList));
		var other = await Library(Sample(), shows, settings.Object);
		Assert.Equal((LibraryDensity.Medium, LibraryLayout.Grid), (other.Density, other.Layout));
	}

	[Theory]
	[InlineData("small,list,posters", LibraryDensity.Small, LibraryLayout.List, LibraryArt.Posters)]
	[InlineData("huge,,", LibraryDensity.Medium, LibraryLayout.Grid, LibraryArt.Stills)]
	[InlineData("", LibraryDensity.Medium, LibraryLayout.Grid, LibraryArt.Stills)]
	public void AViewReadsBack_AndAnOddOneIsTheDefault(string text, LibraryDensity density, LibraryLayout layout, LibraryArt art)
	{
		Assert.Equal(new LibraryView(density, layout, art), LibraryViews.Decode(text));
		Assert.Equal(new LibraryView(density, layout, art), LibraryViews.Decode(LibraryViews.Encode(new LibraryView(density, layout, art))));
	}

	// R or F5 mid-filter (a new view model for the same library): the sort and the filters come back, as for Back; a plain
	// visit starts fresh
	[Fact]
	public async Task ARefresh_PutsTheSortAndTheFiltersBack_AFreshVisitDoesNot()
	{
		var library = new BaseItemDto { Id = Guid.NewGuid(), Name = "Movies", CollectionType = BaseItemDto_CollectionType.Movies };
		var user = Guid.NewGuid();
		var client = new Mock<IJellyfinClient>();
		client.SetupGet(x => x.UserId).Returns(user);
		client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [.. Sample()] });
		var vm = new LibraryViewModel(client.Object);
		await vm.OnNavigatedTo(library);
		vm.Filter.Unwatched = true;
		vm.UpdateSortBy(ItemSortBy.DateCreated);
		vm.LeftAt(vm.Items[1]);

		var refreshed = new LibraryViewModel(client.Object) { ReloadedInPlace = () => true };
		await refreshed.OnNavigatedTo(library);
		Assert.Equal((true, ItemSortBy.DateCreated, SortOrder.Descending, 1, false), (refreshed.Filter.Unwatched, refreshed.SortBy, refreshed.Order, refreshed.FocusRequest, refreshed.ArrivalFocus));

		var fresh = new LibraryViewModel(client.Object) { ReloadedInPlace = () => false };
		await fresh.OnNavigatedTo(library);
		Assert.Equal((false, ItemSortBy.Name, 0, true), (fresh.Filter.Unwatched, fresh.SortBy, fresh.FocusRequest, fresh.ArrivalFocus));
	}
}
