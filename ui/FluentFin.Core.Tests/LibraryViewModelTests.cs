using FluentFin.Core.Contracts.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

public sealed class LibraryViewModelTests
{
	static LibraryViewModelTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	[Fact]
	public async Task ChangingSort_OrdersTheWholeLibrary()
	{
		// Seaglass 1.1.4 (layout audit, item 6): one continuously scrolling grid of every title, no pages of 100
		var random = new Random(1);
		var dtos = Enumerable.Range(0, 250)
			.Select(i => new BaseItemDto { Id = Guid.NewGuid(), Name = $"Item {i:D3}", CommunityRating = random.Next(0, 4) })
			.ToList();

		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = dtos });
		var vm = new LibraryViewModel(client.Object);
		await vm.OnNavigatedTo(new BaseItemDto());
		Assert.Equal(250, vm.Items.Count);

		vm.UpdateSortBy(ItemSortBy.CommunityRating);
		AssertOrder(vm, dtos);

		vm.UpdateSortOrder(SortOrder.Descending);
		AssertOrder(vm, dtos, descending: true);
	}

	// Nixie 1.3.1 (the UX review's CL-M6): the name sort and the A-Z jump go by Jellyfin's sort name: "The Matrix" files under M
	[Fact]
	public async Task TheNameSort_AndTheLetters_GoBySortName()
	{
		var dtos = new[] { ("The Matrix", "matrix"), ("Alien", "alien"), ("Zodiac", "zodiac"), ("Brazil", "") }
			.Select(t => new BaseItemDto { Id = Guid.NewGuid(), Name = t.Item1, SortName = t.Item2 == "" ? null : t.Item2 }).ToList();
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = dtos });
		var vm = new LibraryViewModel(client.Object);

		await vm.OnNavigatedTo(new BaseItemDto());

		Assert.Equal(["Alien", "Brazil", "The Matrix", "Zodiac"], vm.Items.Select(x => x.Name));
		Assert.Equal(["A", "B", "M", "Z"], vm.Letters.Select(x => x.Letter));
		Assert.Equal(2, vm.Letters.Single(x => x.Letter == "M").Index);
		vm.UpdateSortOrder(SortOrder.Descending);
		Assert.Equal(["Zodiac", "The Matrix", "Brazil", "Alien"], vm.Items.Select(x => x.Name));
	}

	// Nixie 1.3.1 (the UX review's B1): a playlist opens, and shows its items in the playlist's order
	[Fact]
	public void APlaylist_Opens_InTheLibraryPage()
	{
		var navigation = new Mock<Services.INavigationServiceCore>();
		var commands = new GlobalCommands(navigation.Object, Mock.Of<IJellyfinClient>(), Mock.Of<Services.INavigationViewServiceCore>(), Mock.Of<Settings.ISettings>());
		var playlist = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Playlist, Name = "Road trip" };

		commands.DisplayDto(playlist);

		navigation.Verify(x => x.NavigateTo(typeof(LibraryViewModel).FullName!, playlist, false), Times.Once);
	}

	[Fact]
	public async Task APlaylist_KeepsItsOwnOrder_UntilASortIsPicked()
	{
		var id = Guid.NewGuid();
		var dtos = new[] { "Zulu", "Alpha", "Mike", "Bravo" }.Select(n => new BaseItemDto { Id = Guid.NewGuid(), Name = n }).ToList();
		var client = new Mock<IJellyfinClient>(MockBehavior.Strict);
		client.Setup(x => x.GetPlaylistItems(id)).ReturnsAsync(new BaseItemDtoQueryResult { Items = dtos });
		client.SetupGet(x => x.UserId).Returns(Guid.NewGuid());
		var vm = new LibraryViewModel(client.Object);

		await vm.OnNavigatedTo(new BaseItemDto { Id = id, Type = BaseItemDto_Type.Playlist, Name = "Road trip" });

		Assert.Equal(["Zulu", "Alpha", "Mike", "Bravo"], vm.Items.Select(x => x.Name));
		Assert.Equal("Playlist order", vm.SortName);
		Assert.Empty(vm.Letters);
		client.Verify(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>()), Times.Never);

		vm.UpdateSortBy(ItemSortBy.Name);
		Assert.Equal(["Alpha", "Bravo", "Mike", "Zulu"], vm.Items.Select(x => x.Name));
		Assert.Equal("Name", vm.SortName);
	}

	[Fact]
	public async Task TheTitleIsTheLibrarysName()
	{
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [new BaseItemDto { Id = Guid.NewGuid(), Name = "A" }] });
		var vm = new LibraryViewModel(client.Object);

		await vm.OnNavigatedTo(new BaseItemDto { Name = "Documentaries" });

		Assert.Equal("Documentaries", vm.Title);
	}

	[Fact]
	public async Task TheCount_IsEveryTitle_AsFiltered()
	{
		// Seaglass scrolling: Narrator's "n of m" for a card is its place in the library
		var dtos = Enumerable.Range(0, 250).Select(i => new BaseItemDto { Id = Guid.NewGuid(), Name = $"Item {i:D3}", Genres = i % 2 == 0 ? ["Drama"] : [] }).ToList();
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = dtos });
		var vm = new LibraryViewModel(client.Object);
		await vm.OnNavigatedTo(new BaseItemDto());

		Assert.Equal((250, 250), (vm.TotalCount, vm.Items.Count));

		vm.Filter.Genres.Add("Drama");
		Assert.Equal((125, 125), (vm.TotalCount, vm.Items.Count));
	}

	private static void AssertOrder(LibraryViewModel vm, List<BaseItemDto> dtos, bool descending = false)
	{
		var sorted = descending
			? dtos.OrderByDescending(x => x.CommunityRating).ThenBy(x => x.Name)
			: dtos.OrderBy(x => x.CommunityRating).ThenBy(x => x.Name);

		Assert.Equal(sorted.Select(x => x.Name), vm.Items.Select(x => x.Name));
	}
}
