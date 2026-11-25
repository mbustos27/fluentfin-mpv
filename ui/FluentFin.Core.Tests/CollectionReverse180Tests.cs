using System.Xml.Linq;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.8 (the reverse toggle; the owner, 2026-11-23: "we should be able to reverse the direction of the sort in
/// collections"): the Collection page's direction, next to Order: it reverses whichever order is chosen (a collection's, a
/// playlist's, a Made by Nixie collection's), its Narrator name says the direction, and it survives Back, Forward and R as
/// Order and Hide watched do.
/// </summary>
public sealed class CollectionReverse180Tests
{
	private static List<BaseItemDto> CopperAndGlass(params int[] watched) =>
	[
		.. new[] { ("Forest of Brightwater", 2000), ("Painted Forests", 1986), ("The Machine and the Mirror", 2016), ("The Mirror and the Engine", 2009), ("The Silent Letter", 2012), ("Wild Canyons", 1979) }
			.Select((f, i) => new BaseItemDto
			{
				Id = Guid.NewGuid(),
				Name = f.Item1,
				SortName = f.Item1.StartsWith("The ", StringComparison.Ordinal) ? f.Item1[4..] : f.Item1,
				Type = BaseItemDto_Type.Movie,
				ProductionYear = f.Item2,
				PremiereDate = new DateTimeOffset(f.Item2, 6, 1, 0, 0, 0, TimeSpan.Zero),
				DateCreated = new DateTimeOffset(2026, 1, 1 + i, 0, 0, 0, TimeSpan.Zero),
				UserData = new UserItemDataDto { Played = watched.Contains(f.Item2) },
			}),
	];

	private static string Years(CollectionViewModel vm) => string.Join(",", vm.Entries.Select(e => e.Item.Dto.ProductionYear));

	private static (CollectionViewModel Vm, Mock<IJellyfinClient> Client) Page(BaseItemDto set, List<BaseItemDto> items, Guid? user = null)
	{
		var client = new Mock<IJellyfinClient>();
		client.SetupGet(x => x.UserId).Returns(user ?? Guid.Empty);
		client.Setup(x => x.GetItem(set.Id!.Value)).ReturnsAsync(set);
		client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = items });
		client.Setup(x => x.GetPlaylistItems(set.Id!.Value)).ReturnsAsync(new BaseItemDtoQueryResult { Items = items });
		client.Setup(x => x.GetItemsByIds(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(items);
		var commands = new GlobalCommands(Mock.Of<INavigationServiceCore>(), client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());
		return (new CollectionViewModel(client.Object, commands), client);
	}

	private static BaseItemDto BoxSet(string name = "Copper and Glass") => new() { Id = Guid.NewGuid(), Type = BaseItemDto_Type.BoxSet, Name = name };

	[Fact]
	public async Task ABoxSet_InItsOwnOrder_ReversesToNewestFirst_ItsPlacesCountingDown()
	{
		var set = BoxSet();
		var (vm, _) = Page(set, CopperAndGlass(watched: 1979));
		await vm.OnNavigatedTo(set);

		Assert.False(vm.Reversed);
		Assert.Equal("Oldest first", vm.DirectionWords);
		Assert.Equal("Oldest first, switch to newest first", vm.DirectionButtonName);
		Assert.Equal("Switch to newest first", vm.DirectionToolTip);

		vm.ToggleDirection();

		Assert.True(vm.Reversed);
		Assert.Equal("2016,2012,2009,2000,1986,1979", Years(vm));
		Assert.Equal(["6", "5", "4", "3", "2", "1"], vm.Entries.Select(e => e.PositionText)); // the places stay the collection's
		Assert.Equal("Newest first", vm.DirectionWords);
		Assert.Equal("Newest first, switch to oldest first", vm.DirectionButtonName);
		Assert.Equal(SortDirections.Glyph(OrderRuns.OldestFirst, reversed: true), vm.DirectionGlyph);
		Assert.Equal("Play Painted Forests", vm.PlayLabel); // Play is still the first not watched in the collection's order

		vm.HideWatched = true; // both: newest first, the watched one out
		Assert.Equal("2016,2012,2009,2000,1986", Years(vm));
		vm.ToggleDirection();
		Assert.Equal("1986,2000,2009,2012,2016", Years(vm));
	}

	[Theory]
	[InlineData(CollectionOrder.Name, "A to Z", "Z to A")]
	[InlineData(CollectionOrder.ReleaseDate, "Oldest first", "Newest first")]
	[InlineData(CollectionOrder.DateAdded, "Newest first", "Oldest first")]
	public async Task EveryOrder_Reverses_AndSaysItsDirection(CollectionOrder order, string words, string reversed)
	{
		var set = BoxSet();
		var (vm, _) = Page(set, CopperAndGlass());
		await vm.OnNavigatedTo(set);
		vm.SetOrder(order);
		var forward = vm.Entries.Select(e => e.Item.Dto.Name).ToList();

		Assert.Equal(words, vm.DirectionWords);
		vm.ToggleDirection();

		Assert.Equal(reversed, vm.DirectionWords);
		Assert.Equal(Enumerable.Reverse(forward), vm.Entries.Select(e => e.Item.Dto.Name));
		Assert.Equal($"{reversed}, switch to {(words == "A to Z" ? words : words.ToLowerInvariant())}", vm.DirectionButtonName);

		// another order keeps the flip (it reverses whichever order is chosen)
		vm.SetOrder(order == CollectionOrder.Name ? CollectionOrder.ReleaseDate : CollectionOrder.Name);
		Assert.True(vm.Reversed);
		Assert.Equal(order == CollectionOrder.Name ? "Newest first" : "Z to A", vm.DirectionWords);
	}

	[Theory]
	[InlineData(CollectionOrder.Collection)]
	[InlineData(CollectionOrder.ReleaseDate)]
	[InlineData(CollectionOrder.DateAdded)]
	public async Task NewestFirst_StartsWithTheNewest_UndatedFilmsStayLast(CollectionOrder order)
	{
		// review (Minor 4): the order puts films with no date last; mirrored, they led "Newest first"
		var set = BoxSet();
		var films = CopperAndGlass();
		films.Add(new BaseItemDto { Id = Guid.NewGuid(), Name = "Undated Reel", Type = BaseItemDto_Type.Movie, UserData = new UserItemDataDto() });
		var (vm, _) = Page(set, films);
		await vm.OnNavigatedTo(set);
		vm.SetOrder(order);
		Assert.Equal("Undated Reel", vm.Entries[^1].Item.Dto.Name);

		vm.ToggleDirection();

		Assert.Equal("Undated Reel", vm.Entries[^1].Item.Dto.Name);
		Assert.Equal(order == CollectionOrder.DateAdded ? "Forest of Brightwater" : "The Machine and the Mirror", vm.Entries[0].Item.Dto.Name); // the oldest added / the newest
		// A to Z has no undated: plainly mirrored
		vm.SetOrder(CollectionOrder.Name);
		Assert.Equal("Wild Canyons", vm.Entries[0].Item.Dto.Name);
	}

	[Fact]
	public async Task APlaylist_RunsFirstToLast_AndReversesToLastToFirst()
	{
		var playlist = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Playlist, Name = "Rainy Sunday" };
		var films = CopperAndGlass();
		var (vm, _) = Page(playlist, films);
		await vm.OnNavigatedTo(playlist);

		Assert.Equal("First to last, switch to last to first", vm.DirectionButtonName);
		vm.ToggleDirection();
		Assert.Equal("Last to first", vm.DirectionWords);
		Assert.Equal(Enumerable.Reverse(films).Select(x => x.Name), vm.Entries.Select(e => e.Item.Dto.Name));
	}

	[Fact]
	public async Task AnAToZOfThirty_ReversedRunsItsLettersZToA()
	{
		var set = BoxSet("Thirty");
		var films = Enumerable.Range(0, 30).Select(i => new BaseItemDto { Id = Guid.NewGuid(), Name = $"{(char)('A' + (i % 26))} film {i:D2}", Type = BaseItemDto_Type.Movie, ProductionYear = 1990 + i }).ToList();
		var (vm, _) = Page(set, films);
		await vm.OnNavigatedTo(set);
		vm.SetOrder(CollectionOrder.Name);
		vm.ToggleDirection();

		Assert.Equal("Z", vm.Letters[0].Letter);
		Assert.Equal("A", vm.Letters[^1].Letter);
		Assert.All(vm.Letters, l => Assert.StartsWith(l.Letter, vm.Entries[l.Index].Item.Dto.Name, StringComparison.Ordinal)); // each jump lands on its letter
	}

	[Fact]
	public async Task RAndBack_KeepTheDirection_AFreshVisitStartsUnreversed_PerUserAndCollection()
	{
		var set = BoxSet();
		var films = CopperAndGlass();
		var user = Guid.NewGuid();
		var (vm, _) = Page(set, films, user);
		await vm.OnNavigatedTo(set);
		vm.ToggleDirection();

		// R (a new view model, the page says it reloads in place) and Back: newest first still
		var (again, _) = Page(set, films, user);
		again.KeepsPlace = () => true;
		await again.OnNavigatedTo(set);
		Assert.True(again.Reversed);
		Assert.Equal("2016,2012,2009,2000,1986,1979", Years(again));
		Assert.Equal("Newest first, switch to oldest first", again.DirectionButtonName);

		// opened anew: as its order runs; and that visit saves nothing (Back to the earlier one is still reversed)
		var (fresh, _) = Page(set, films, user);
		fresh.KeepsPlace = () => false;
		await fresh.OnNavigatedTo(set);
		Assert.False(fresh.Reversed);
		var (back, _) = Page(set, films, user);
		back.KeepsPlace = () => true;
		await back.OnNavigatedTo(set);
		Assert.True(back.Reversed);

		// flipped back is kept too
		back.ToggleDirection();
		var (later, _) = Page(set, films, user);
		later.KeepsPlace = () => true;
		await later.OnNavigatedTo(set);
		Assert.False(later.Reversed);

		// another user, another collection: their own
		vm.ToggleDirection(); // (vm's flip: reversed again for this user)
		var (other, _) = Page(set, films, Guid.NewGuid());
		other.KeepsPlace = () => true;
		await other.OnNavigatedTo(set);
		Assert.False(other.Reversed);
		var elsewhereSet = BoxSet("Winter Films");
		var (elsewhere, _) = Page(elsewhereSet, films, user);
		elsewhere.KeepsPlace = () => true;
		await elsewhere.OnNavigatedTo(elsewhereSet);
		Assert.False(elsewhere.Reversed);
	}

	[Fact]
	public async Task BackAndForward_KeepTheViewModel_ItsRefreshBehindKeepsTheDirectionAndTheCards()
	{
		// 1.7.5's PageKeep: Back and Forward show the same view model (IKeptOnBack.Returned); a stale one is asked again behind
		// the cards. The direction, the cards' order and the cards themselves stay; a film marked watched meanwhile shows
		var set = BoxSet();
		var films = CopperAndGlass();
		var (vm, client) = Page(set, films);
		await vm.OnNavigatedTo(set);
		vm.SetOrder(CollectionOrder.Name);
		vm.ToggleDirection();
		var cards = vm.Entries.Select(e => e.Item).ToList();
		var names = vm.Entries.Select(e => e.Item.Dto.Name).ToList();

		await vm.Returned(set, stale: false);
		Assert.Equal((true, "Z to A, switch to A to Z"), (vm.Reversed, vm.DirectionButtonName));
		Assert.Equal(names, vm.Entries.Select(e => e.Item.Dto.Name));

		var later = CopperAndGlass(watched: 2016).Select((x, i) => { x.Id = films[i].Id; return x; }).ToList();
		client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = later });
		await vm.Returned(set, stale: true);
		Assert.True(vm.Reversed);
		Assert.Equal(names, vm.Entries.Select(e => e.Item.Dto.Name));
		Assert.Equal(cards, vm.Entries.Select(e => e.Item)); // the same cards (the keyboard and the scroll stay)
		Assert.True(vm.Entries.Single(e => e.Item.Dto.ProductionYear == 2016).Item.Dto.UserData?.Played);
	}

	[Fact]
	public async Task AMadeByNixieCollection_InItsListsOrder_ReversesAndSurvivesR()
	{
		var films = CopperAndGlass();
		var spotlight = new SmartCollection("spotlight:director:ann", SmartCollectionKind.Spotlight, "Spotlight: Ann", "Films directed by Ann", false, [.. films.Select(x => x.Id!.Value)]) { InListOrder = true };
		var nixie = new NixieCollection(spotlight);
		var user = Guid.NewGuid();
		var (vm, _) = Page(BoxSet(), films, user);
		await vm.OnNavigatedTo(nixie);

		Assert.Equal("First to last, switch to last to first", vm.DirectionButtonName); // the Spotlight's own order: best first
		vm.ToggleDirection();
		Assert.Equal(Enumerable.Reverse(films).Select(x => x.Name), vm.Entries.Select(e => e.Item.Dto.Name));

		var (again, _) = Page(BoxSet(), films, user);
		again.KeepsPlace = () => true;
		await again.OnNavigatedTo(nixie);
		Assert.True(again.Reversed);
		Assert.Equal(Enumerable.Reverse(films).Select(x => x.Name), again.Entries.Select(e => e.Item.Dto.Name));
	}

	private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

	private static string RepoRoot { get; } = FindRepoRoot();

	private static string FindRepoRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FluentFin.sln")))
		{
			dir = dir.Parent;
		}

		return dir?.FullName ?? throw new DirectoryNotFoundException("FluentFin.sln not found above " + AppContext.BaseDirectory);
	}

	/// <summary>
	/// The button the user sees: next to Order, in the toolbar's next column; a press is the view model's toggle (a Button: Space,
	/// Enter, a click and a gamepad's A); Narrator hears the direction first; both directions' words share its cell (the other
	/// invisible and raw to Narrator), so it keeps its width when it flips (rule 7); the theme's compact style, as Order's.
	/// </summary>
	[Fact]
	public void TheDirectionButton_SitsNextToOrder_SaysTheDirection_AndHoldsItsWidth()
	{
		var page = XDocument.Load(Path.Combine(RepoRoot, "FluentFin/Views/CollectionPage.xaml"));
		XElement Named(string name) => page.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);
		var order = Named("OrderButton");
		var button = Named("DirectionButton");

		Assert.Same(order.Parent, button.Parent); // the toolbar
		Assert.Equal(int.Parse((string)order.Attribute("Grid.Column")!) + 1, int.Parse((string)button.Attribute("Grid.Column")!));
		Assert.Equal(4, order.Parent!.Elements().Single(e => e.Name.LocalName == "Grid.ColumnDefinitions").Elements().Count()); // what is shown, Hide watched, Order, the direction
		Assert.Equal("Button", button.Name.LocalName);
		Assert.Equal((string?)order.Attribute("Style"), (string?)button.Attribute("Style"));
		Assert.Contains("ViewModel.ToggleDirectionCommand", (string?)button.Attribute("Command"), StringComparison.Ordinal);
		Assert.Contains("ViewModel.DirectionButtonName", (string?)button.Attribute("AutomationProperties.Name"), StringComparison.Ordinal);
		Assert.Contains("ViewModel.DirectionToolTip", (string?)button.Attribute("ToolTipService.ToolTip"), StringComparison.Ordinal);
		Assert.Contains(button.Descendants(), e => e.Name.LocalName == "FontIcon" && ((string?)e.Attribute("Glyph"))?.Contains("ViewModel.DirectionGlyph", StringComparison.Ordinal) == true);
		var words = button.Descendants().Single(e => e.Name.LocalName == "Grid").Elements().Where(e => e.Name.LocalName == "TextBlock").ToList();
		Assert.Equal(2, words.Count);
		Assert.Contains("ViewModel.DirectionWords", (string?)words[0].Attribute("Text"), StringComparison.Ordinal);
		Assert.Null(words[0].Attribute("Opacity"));
		Assert.Contains("ViewModel.OtherDirectionWords", (string?)words[1].Attribute("Text"), StringComparison.Ordinal);
		Assert.Equal("0", (string?)words[1].Attribute("Opacity"));
		Assert.Equal("Raw", (string?)words[1].Attribute("AutomationProperties.AccessibilityView"));

		// review (Minor 5, rule 7): Order and the direction as wide as their widest words, so changing Order moves nothing (the
		// harness, then-180-reverse.ps1, measures every order both ways in each theme)
		Assert.Contains("CollectionPage.OrderWidth", (string?)order.Attribute("MinWidth"), StringComparison.Ordinal);
		Assert.Contains("CollectionPage.DirectionWidth", (string?)button.Attribute("MinWidth"), StringComparison.Ordinal);
		var code = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Views/CollectionPage.xaml.cs"));
		double Width(string name) => double.Parse(System.Text.RegularExpressions.Regex.Match(code, $@"public static double {name} => (\d+);").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
		Assert.True(Width("OrderWidth") >= 210, "Order: Release date (about 203 at 14 px) fits");
		Assert.True(Width("DirectionWidth") >= 130, "the arrow and Newest first (120 measured) fit");
	}

	[Fact]
	public async Task BothWords_AreAlwaysTheTwoDirections_OfTheOrderShown()
	{
		var set = BoxSet();
		var (vm, _) = Page(set, CopperAndGlass());
		await vm.OnNavigatedTo(set);
		foreach (var order in Enum.GetValues<CollectionOrder>())
		{
			vm.SetOrder(order);
			foreach (var _ in new[] { 1, 2 })
			{
				Assert.Equal(SortDirections.Words(vm.Runs, !vm.Reversed), vm.OtherDirectionWords);
				Assert.StartsWith(vm.DirectionWords + ", switch to ", vm.DirectionButtonName, StringComparison.Ordinal);
				vm.ToggleDirection();
			}
		}
	}
}
