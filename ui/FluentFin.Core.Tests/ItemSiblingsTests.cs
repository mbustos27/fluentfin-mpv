using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Services;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>Seaglass: the season picker of the season page and "Up next" on the episode page.</summary>
[Collection(SessionInfoCollection.Name)]
public sealed class ItemSiblingsTests
{
	private static readonly Guid SeriesId = Guid.NewGuid();

	private static BaseItemDto Season(int n, Guid? id = null) => new() { Id = id ?? Guid.NewGuid(), Type = BaseItemDto_Type.Season, IndexNumber = n, Name = $"Season {n}", SeriesId = SeriesId };

	private static BaseItemDto Episode(int n, Guid? id = null) => new() { Id = id ?? Guid.NewGuid(), Type = BaseItemDto_Type.Episode, IndexNumber = n, Name = $"Episode {n}" };

	[Fact]
	public void Pills_AreInSeasonOrder_AndMarkTheCurrentOne()
	{
		var two = Season(2);
		var pills = ItemSiblings.SeasonPills([Season(3), two, Season(1)], two.Id!.Value);

		Assert.Equal(["Season 1", "Season 2", "Season 3"], pills.Select(p => p.Name));
		Assert.Equal([false, true, false], pills.Select(p => p.IsCurrent));
		Assert.Equal("Season 2, current season", pills[1].AutomationName);
		Assert.Equal("Season 1", pills[0].AutomationName);
	}

	[Fact]
	public void OneSeason_NeedsNoPicker_AndOnlySeasonsAreListed()
	{
		var only = Season(1);

		Assert.Empty(ItemSiblings.SeasonPills([only, Episode(1)], only.Id!.Value));
		Assert.Empty(ItemSiblings.SeasonPills(null, Guid.NewGuid()));
	}

	[Fact]
	public void NextEpisode_IsTheOneAfterByNumber_NotByListOrder()
	{
		var two = Episode(2);
		var three = Episode(3);

		Assert.Same(three, ItemSiblings.NextEpisode([three, Episode(1), two], two.Id!.Value));
		Assert.Null(ItemSiblings.NextEpisode([three, two], three.Id!.Value));
		Assert.Null(ItemSiblings.NextEpisode([three], Guid.NewGuid()));
		Assert.Null(ItemSiblings.NextEpisode(null, Guid.NewGuid()));
	}

	[Fact]
	public async Task TheSeasonViewModel_ListsTheSeriesSeasons_AndHidesThePickerWhenTheyCannotBeListed()
	{
		var current = Season(2);
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetItem(current.Id!.Value)).ReturnsAsync(current);
		client.Setup(x => x.GetItems(It.Is<BaseItemDto>(p => p.Id == current.Id), false)).ReturnsAsync(new BaseItemDtoQueryResult { Items = [Episode(1)] });
		client.Setup(x => x.GetItems(It.Is<BaseItemDto>(p => p.Id == SeriesId), false)).ReturnsAsync(new BaseItemDtoQueryResult { Items = [Season(1), current] });
		var vm = new SeasonViewModel(client.Object);

		await vm.OnNavigatedTo(current);
		Assert.Equal([false, true], vm.SeasonPills.Select(p => p.IsCurrent));

		// a new page (Nixie 1.4.1: a page keeps the lists it read, so the failure is a new page's, as in the app)
		client.Setup(x => x.GetItems(It.Is<BaseItemDto>(p => p.Id == SeriesId), false)).ReturnsAsync((BaseItemDtoQueryResult?)null);
		vm = new SeasonViewModel(client.Object);
		await vm.OnNavigatedTo(current);
		Assert.Empty(vm.SeasonPills);
		Assert.Single(vm.Episodes);
	}

	[Fact]
	public async Task TheEpisodeViewModel_KnowsTheNextEpisode_AndStillLoadsWhenTheSeasonCannotBeListed()
	{
		var seasonId = Guid.NewGuid();
		var one = Episode(1);
		one.SeasonId = seasonId;
		var two = Episode(2);
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetItem(one.Id!.Value)).ReturnsAsync(one);
		client.Setup(x => x.GetItems(It.Is<BaseItemDto>(p => p.Id == seasonId), false)).ReturnsAsync(new BaseItemDtoQueryResult { Items = [two, one] });
		var commands = new GlobalCommands(Mock.Of<INavigationServiceCore>(), client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());
		var vm = new EpisodeViewModel(client.Object, commands);

		await vm.OnNavigatedTo(one);
		Assert.Same(two, vm.NextEpisode);
		Assert.True(vm.HasNextEpisode);
		Assert.Equal(LoadStatus.Loaded, vm.State.Status);

		client.Setup(x => x.GetItems(It.Is<BaseItemDto>(p => p.Id == seasonId), false)).ReturnsAsync((BaseItemDtoQueryResult?)null);
		await vm.OnNavigatedTo(one);
		Assert.Null(vm.NextEpisode);
		Assert.Equal(LoadStatus.Loaded, vm.State.Status);
	}
}
