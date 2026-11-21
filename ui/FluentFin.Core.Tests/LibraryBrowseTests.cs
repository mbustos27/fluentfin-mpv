using FluentFin.Core.Contracts.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Seaglass 1.1.4 (UX review, fixes 9 and 16): the Library says its sort, its filters and its count; its A-Z jump takes the
/// grid to the first title of a letter (only the letters there are); the filters no longer hold the grid back a round trip;
/// and Back from an item puts the user on the card they left, with the sort and filters they had.
/// </summary>
public sealed class LibraryBrowseTests
{
	static LibraryBrowseTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	private static readonly BaseItemDto Movies = new() { Id = Guid.NewGuid(), Name = "Movies", CollectionType = BaseItemDto_CollectionType.Movies };

	private readonly Mock<IJellyfinClient> _client = new();

	public LibraryBrowseTests()
	{
		_client.SetupGet(x => x.UserId).Returns(Guid.NewGuid());
	}

	private LibraryViewModel Library(IEnumerable<BaseItemDto> items)
	{
		_client.SetupLibrary([.. items]);
		return new LibraryViewModel(_client.Object);
	}

	private static BaseItemDto Movie(string name, string? genre = null, float rating = 0) => new()
	{
		Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Movie, CommunityRating = rating, Genres = genre is null ? [] : [genre],
	};

	// ---- the bar's words

	[Fact]
	public async Task TheSortButton_SaysTheSortInUseAndItsDirection()
	{
		var vm = Library([Movie("A")]);
		await vm.OnNavigatedTo(Movies);

		// Nixie 1.4.0 (item 8): the direction in words, on the button and first in its name (WCAG 2.5.3)
		Assert.Equal(("Title", "Title · A to Z", "Title, A to Z, Sort", ""), (vm.SortName, vm.SortText, vm.SortButtonName, vm.SortGlyph));

		var changed = new List<string?>();
		vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
		vm.UpdateSortBy(ItemSortBy.DateCreated);

		// a date opens newest first (it opened oldest first)
		Assert.Equal(SortOrder.Descending, vm.Order);
		Assert.Equal(("Date added", "Date added · newest first", "Date added, newest first, Sort", ""), (vm.SortName, vm.SortText, vm.SortButtonName, vm.SortGlyph));
		Assert.Contains(nameof(LibraryViewModel.SortText), changed);
		Assert.Contains(nameof(LibraryViewModel.SortName), changed);
		Assert.Contains(nameof(LibraryViewModel.SortButtonName), changed);
		Assert.Contains(nameof(LibraryViewModel.SortGlyph), changed);
	}

	[Fact]
	public async Task TheFilterButton_CountsTheFiltersOn()
	{
		var vm = Library([Movie("A", "Drama"), Movie("B", "Comedy")]);
		await vm.OnNavigatedTo(Movies);
		Assert.Equal(("Filter", "Filter (none on)"), (vm.FilterText, vm.FilterButtonName));

		vm.Filter.Genres.Add("Drama");
		Assert.Equal(("Filter (1)", "Filter (1 on)"), (vm.FilterText, vm.FilterButtonName));

		vm.Filter.Years.Add("2020");
		vm.Filter.Tags.Add("4K");
		Assert.Equal(("Filter (3)", "Filter (3 on)"), (vm.FilterText, vm.FilterButtonName));
	}

	[Fact]
	public async Task TheCount_IsTheLibrarysAndWhatTheFiltersLeave()
	{
		var vm = Library([Movie("A", "Drama"), Movie("B", "Comedy"), Movie("C", "Drama")]);
		await vm.OnNavigatedTo(Movies);
		Assert.Equal("3 movies", vm.CountText);

		vm.Filter.Genres.Add("Drama");
		Assert.Equal("2 of 3 movies", vm.CountText);
	}

	[Theory]
	[InlineData(1, 1, BaseItemDto_CollectionType.Movies, "1 movie")]
	[InlineData(100, 100, BaseItemDto_CollectionType.Tvshows, "100 shows")]
	[InlineData(1200, 1200, null, "1,200 items")]
	[InlineData(0, 4, BaseItemDto_CollectionType.Movies, "0 of 4 movies")]
	public void CountText(int shown, int total, BaseItemDto_CollectionType? collection, string expected) =>
		Assert.Equal(expected, LibraryBrowse.CountText(shown, total, collection));

	// ---- A-Z

	[Theory]
	[InlineData("Amélie", "A")]
	[InlineData("élan", "E")]
	[InlineData("  zorro", "Z")]
	[InlineData("12 Angry Men", "#")]
	[InlineData("(500) Days", "#")]
	[InlineData("", "#")]
	[InlineData(null, "#")]
	public void ATitlesLetter(string? name, string letter) => Assert.Equal(letter, LibraryBrowse.LetterOf(name));

	[Fact]
	public void TheLetters_AreOnlyThoseThere_EachAtItsFirstTitle()
	{
		var letters = LibraryBrowse.Letters(["10 Things", "Alien", "Amélie", "Brazil", "Ébène", "Zodiac"]);

		Assert.Equal([new LetterJump("#", 0), new LetterJump("A", 1), new LetterJump("B", 3), new LetterJump("E", 4), new LetterJump("Z", 5)], letters);
		Assert.Equal("Jump to B", letters[2].Name);
		Assert.Equal("Jump to titles that start with a number or a sign", letters[0].Name);
	}

	[Fact]
	public async Task ALetter_AsksForItsFirstTitlesCard()
	{
		// 250 titles: A..., then M..., then Z...: M's first is the 120th
		var titles = Enumerable.Range(0, 119).Select(i => $"A{i:D3}").Concat(Enumerable.Range(0, 100).Select(i => $"M{i:D3}")).Concat(Enumerable.Range(0, 31).Select(i => $"Z{i:D3}"));
		var vm = Library(titles.Select(x => Movie(x)));
		await vm.OnNavigatedTo(Movies);
		Assert.Equal(["A", "M", "Z"], vm.Letters.Select(x => x.Letter));

		vm.JumpTo(vm.Letters[1]);
		Assert.Equal(119, vm.FocusRequest);
		Assert.Equal("M000", vm.Items[vm.FocusRequest].Name);

		vm.JumpTo(vm.Letters[2]);
		Assert.Equal("Z000", vm.Items[vm.FocusRequest].Name);

		// the same letter again asks again (the user moved away meanwhile)
		var asked = 0;
		vm.PropertyChanged += (_, e) => asked += e.PropertyName == nameof(LibraryViewModel.FocusRequest) ? 1 : 0;
		vm.JumpTo(vm.Letters[2]);
		Assert.Equal(1, asked);
	}

	[Fact]
	public async Task TheLetters_FollowTheFilters_AndGoWhenTheSortIsNotByName()
	{
		var vm = Library([Movie("Alien", "Horror"), Movie("Brazil", "Comedy"), Movie("Cube", "Horror")]);
		await vm.OnNavigatedTo(Movies);
		Assert.Equal(["A", "B", "C"], vm.Letters.Select(x => x.Letter));

		vm.Filter.Genres.Add("Horror");
		Assert.Equal(["A", "C"], vm.Letters.Select(x => x.Letter));

		vm.UpdateSortOrder(SortOrder.Descending);
		Assert.Equal(["C", "A"], vm.Letters.Select(x => x.Letter));

		vm.UpdateSortBy(ItemSortBy.CommunityRating);
		Assert.Empty(vm.Letters);
	}

	// ---- speed: one request; the grid waited for a second one (the filters' lists), asked for after the first

	[Fact]
	public async Task TheLibrary_IsOneRequest_TheFiltersListsComeFromItsTitles()
	{
		var vm = Library([Movie("A", "Drama"), Movie("B", "Drama"), Movie("C", "Comedy")]);

		await vm.OnNavigatedTo(Movies);

		Assert.False(vm.IsLoading);
		_client.VerifyLibraryLoads(Times.Once);
		_client.Verify(x => x.GetFilters(It.IsAny<BaseItemDto>()), Times.Never);
		Assert.Equal(["Comedy (1)", "Drama (2)"], vm.Facets.Single(f => f.Title == "Genres").Options.Select(o => o.Label));
	}

	// ---- the Filter menu: lists of check boxes with counts, and Clear filters (interaction audit, item 12)

	[Fact]
	public async Task TheFilterLists_CountTheTitles_AndTheirBoxesAreTheFilter()
	{
		var a = Movie("A", "Drama"); a.ProductionYear = 1999; a.OfficialRating = "R"; a.Tags = ["4K"];
		var b = Movie("B", "Drama"); b.ProductionYear = 2020; b.OfficialRating = "PG"; b.UserData = new UserItemDataDto { Played = true, IsFavorite = true };
		var c = Movie("C", "Comedy"); c.ProductionYear = 2020; c.Genres = ["Comedy", "Comedy"];
		var vm = Library([a, b, c]);
		await vm.OnNavigatedTo(Movies);

		Assert.Equal(["Genres", "Years", "Ratings", "Tags"], vm.Facets.Select(f => f.Title));
		Assert.Equal(["2020 (2)", "1999 (1)"], vm.Facets[1].Options.Select(o => o.Label)); // newest first
		Assert.Equal(["Comedy (1)", "Drama (2)"], vm.Facets[0].Options.Select(o => o.Label)); // a title counts once
		Assert.Equal("Drama, 2 titles", vm.Facets[0].Options[1].Name);

		var drama = vm.Facets[0].Options[1];
		var changed = 0;
		drama.PropertyChanged += (_, e) => changed += e.PropertyName == nameof(FilterOption.IsChecked) ? 1 : 0;
		drama.IsChecked = true;
		Assert.Equal(["Drama"], vm.Filter.Genres);
		Assert.Equal(["A", "B"], vm.Items.Select(x => x.Name));
		Assert.Equal("Filter (1)", vm.FilterText);

		// Unwatched and Favorites (layout audit, item 15): what jellyfin-web and Plex offer first; each counts as one filter
		vm.Filter.Unwatched = true;
		Assert.Equal(["A"], vm.Items.Select(x => x.Name));
		Assert.Equal("Filter (2)", vm.FilterText);
		vm.Filter.Unwatched = false;
		vm.Filter.Favorites = true;
		Assert.Equal(["B"], vm.Items.Select(x => x.Name));

		vm.ClearFilters();
		Assert.False(vm.Filter.Favorites);
		Assert.False(drama.IsChecked);
		Assert.True(changed >= 2); // the box follows a change of the filter made elsewhere
		Assert.Equal(3, vm.Items.Count);
		Assert.Equal("Filter", vm.FilterText);
	}

	[Fact]
	public async Task TheDirection_IsOneClick_EachWay()
	{
		var vm = Library([Movie("A"), Movie("B")]);
		await vm.OnNavigatedTo(Movies);
		Assert.Equal("A to Z, switch to Z to A", vm.SortOrderButtonName);

		vm.ToggleSortOrder();
		Assert.Equal((SortOrder.Descending, "Z to A, switch to A to Z"), (vm.Order, vm.SortOrderButtonName));
		Assert.Equal(["B", "A"], vm.Items.Select(x => x.Name));

		vm.ToggleSortOrder();
		Assert.Equal(SortOrder.Ascending, vm.Order);
	}

	// ---- Back from an item: the card that was left

	[Fact]
	public async Task BackFromAnItem_PutsTheSortTheFiltersAndTheCardBack()
	{
		var library = new BaseItemDto { Id = Guid.NewGuid(), Name = "Films", CollectionType = BaseItemDto_CollectionType.Movies };
		var all = Enumerable.Range(0, 250).Select(i => Movie($"T{i:D3}", i % 2 == 0 ? "Drama" : "Comedy", rating: i % 7)).ToList();
		var vm = Library(all);
		await vm.OnNavigatedTo(library);
		vm.UpdateSortBy(ItemSortBy.CommunityRating);
		vm.Filter.Genres.Add("Drama");
		vm.Filter.Unwatched = true;
		var left = vm.Items[110];
		vm.LeftAt(left, offset: 812.5);

		var back = Library(all);
		back.ComingBack = true;
		await back.OnNavigatedTo(library);

		Assert.Equal(ItemSortBy.CommunityRating, back.SortBy);
		Assert.Equal(["Drama"], back.Filter.Genres);
		Assert.True(back.Filter.Unwatched);
		Assert.Equal(110, back.FocusRequest);
		Assert.Equal(left.Id, back.Items[back.FocusRequest].Id);
		Assert.Equal(812.5, back.ReturnOffset); // how far down the grid was: the page scrolls there before it focuses the card
	}

	[Fact]
	public async Task OpeningTheLibraryAnew_StartsAtTheTop()
	{
		var library = new BaseItemDto { Id = Guid.NewGuid(), Name = "Films" };
		var all = Enumerable.Range(0, 250).Select(i => Movie($"T{i:D3}")).ToList();
		var vm = Library(all);
		await vm.OnNavigatedTo(library);
		vm.LeftAt(vm.Items[203], offset: 4000);

		var again = Library(all);
		await again.OnNavigatedTo(library);

		// Nixie 1.4.0 (item 4): opened anew, the first poster gets the focus (by the program), not the card left last time
		Assert.Equal((0, true), (again.FocusRequest, again.ArrivalFocus));
		Assert.Null(again.ReturnOffset);
	}
}

/// <summary>The review of wip/114-library: the year filter, the A-Z keys of every script, the Filter menu's one list.</summary>
public sealed class LibraryReviewTests
{
	private static async Task<LibraryViewModel> Library(IEnumerable<BaseItemDto> items)
	{
		var client = new Mock<IJellyfinClient>();
		client.SetupGet(x => x.UserId).Returns(Guid.NewGuid());
		client.SetupLibrary([.. items]);
		var vm = new LibraryViewModel(client.Object);
		await vm.OnNavigatedTo(new BaseItemDto { Id = Guid.NewGuid(), Name = "Movies", CollectionType = BaseItemDto_CollectionType.Movies });
		return vm;
	}

	private static BaseItemDto Movie(string name, int? year = null) => new() { Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Movie, ProductionYear = year };

	[Fact]
	public async Task AYear_ShowsOnlyTheTitlesOfThatYear_AsItsCountSays()
	{
		var vm = await Library([Movie("Y1", 2020), Movie("Y2", 2020), Movie("Y3", 2020), Movie("N1"), Movie("N2")]);
		var year = vm.Facets.Single(f => f.Title == "Years").Options.Single();

		year.IsChecked = true;

		Assert.Equal("2020 (3)", year.Label);
		Assert.Equal(3, vm.Items.Count); // the two without a year were let through
	}

	[Theory]
	[InlineData("Alien", "A")]
	[InlineData("Ángel", "A")]
	[InlineData("Æon Flux", "A")]
	[InlineData("Évian", "E")]
	[InlineData("Łódź", "L")]
	[InlineData("Ølstykke", "O")]
	[InlineData("Œuvre", "O")]
	[InlineData("ß", "S")]
	[InlineData("the Thing", "T")]
	[InlineData("(500) Days", "#")]
	[InlineData("2001", "#")]
	[InlineData("Брат", "Б")]
	[InlineData("ёлка", "Е")]
	[InlineData("Ωmega", "Ω")]
	[InlineData("千と千尋", "漢")]
	[InlineData("となりのトトロ", "あ")]
	[InlineData("올드보이", "한")]
	public void ATitlesKey_IsItsFoldedFirstLetter_OrItsScript(string name, string key) => Assert.Equal(key, LibraryBrowse.LetterOf(name));

	[Fact]
	public async Task EveryLetter_LeadsToATitleWithIt_AndEveryTitleCanBeReached()
	{
		string[] names = ["Alien", "Æon Flux", "Batman", "Ølstykke", "Zorro", "Ángel", "千と千尋", "Брат", "Łódź", "Ocean", "the Thing", "Up", "(500) Days", "Évian", "ß", "Батя"];
		var vm = await Library(names.Select(n => Movie(n)));

		Assert.All(vm.Letters, l => Assert.All(l.Runs, i => Assert.Equal(l.Letter, LibraryBrowse.LetterOf(vm.Items[i].Name))));
		// every title is in a run of its key that its letter reaches (the run's first title is one of the letter's stops)
		for (var i = 0; i < vm.Items.Count; i++)
		{
			var key = LibraryBrowse.LetterOf(vm.Items[i].Name);
			var start = i;
			while (start > 0 && LibraryBrowse.LetterOf(vm.Items[start - 1].Name) == key)
			{
				start--;
			}

			Assert.Contains(start, vm.Letters.Single(l => l.Letter == key).Runs);
		}

		Assert.DoesNotContain(vm.Letters, l => l.Letter == "#" && l.Runs.Count > 1);
		Assert.Contains(vm.Letters, l => l.Letter == "L");
		Assert.Contains(vm.Letters, l => l.Letter == "Б");
	}

	[Fact]
	public void AKeyTheSortSplits_StepsThroughItsRuns_WhenPressedAgain()
	{
		var letters = LibraryBrowse.Letters(["Apple", "Ølstykke", "Banana", "Øresund"]);
		var o = letters.Single(l => l.Letter == "O");

		Assert.Equal([1, 3], o.Runs);
		Assert.Equal(1, o.Index);
	}

	[Fact]
	public async Task TheSameLetterAgain_GoesToItsNextRun_ThenRound()
	{
		// a culture that sorts Ø after Z (Danish) splits O: the letter reaches both runs
		var vm = await Library([Movie("Ocean"), Movie("Zorro")]);
		var o = new LetterJump("O", 0) { Runs = [0, 1] };

		vm.JumpTo(o);
		Assert.Equal(0, vm.FocusRequest);
		vm.JumpTo(o);
		Assert.Equal(1, vm.FocusRequest);
		vm.JumpTo(o);
		Assert.Equal(0, vm.FocusRequest);
	}

	[Fact]
	public async Task TheFilterMenu_IsOneList_OfEachKindThenItsValues()
	{
		var a = Movie("A", 2020); a.Genres = ["Drama"];
		var b = Movie("B", 2021); b.Genres = ["Comedy"];
		var vm = await Library([a, b]);

		Assert.Equal(["Genres", "Comedy (1)", "Drama (1)", "Years", "2021 (1)", "2020 (1)"],
			vm.FilterRows.Select(r => r switch { FilterFacet f => f.Title, FilterOption o => o.Label, _ => "?" }));
	}
}

/// <summary>Seaglass 1.1.4 (interaction audit, item 5): Back to a row's "See all" grid puts the user where they were.</summary>
public sealed class ItemGridPlaceTests
{
	private static ItemGridViewModel Grid(Guid user, string title, IReadOnlyList<BaseItemViewModel> items)
	{
		var client = new Mock<IJellyfinClient>();
		client.SetupGet(x => x.UserId).Returns(user);
		var vm = new ItemGridViewModel(client.Object);
		vm.OnNavigatedTo(new ItemGridRequest(title, items));
		return vm;
	}

	[Fact]
	public void BackToTheGrid_FindsTheCardThatWasLeft_AndHowFarDown()
	{
		var user = Guid.NewGuid();
		var items = Enumerable.Range(0, 30).Select(i => BaseItemViewModel.FromDto(new BaseItemDto { Id = Guid.NewGuid(), Name = $"Item {i}" })).ToList();
		var grid = Grid(user, "Continue Watching", items);
		Assert.Null(grid.ReturnPlace()); // never left

		grid.LeftAt(items[17], 940);

		Assert.Equal((17, 940.0), Grid(user, "Continue Watching", items).ReturnPlace());
		Assert.Null(Grid(user, "Next Up", items).ReturnPlace());             // another row's grid
		Assert.Null(Grid(Guid.NewGuid(), "Continue Watching", items).ReturnPlace()); // another user
		Assert.Equal((-1, 940.0), Grid(user, "Continue Watching", items.Take(10).ToList()).ReturnPlace()); // the card is gone: just the scroll
	}
}
