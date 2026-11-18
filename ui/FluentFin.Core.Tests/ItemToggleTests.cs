using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.2.0 (1.1.3 UX audit, interaction item 3): marking an item watched or a favourite, from a card, the item page,
/// an episode row or the More menu, goes through ItemUserData: the server's answer replaces the item's copy and every
/// surface showing the item is told. Before, each surface sent "the opposite of its copy", which nothing updated: Watched
/// on then off sent "mark watched" twice, and the menu kept saying "Mark as watched".
/// </summary>
public sealed class ItemToggleTests
{
	private readonly Mock<IJellyfinClient> _client = new();
	private readonly List<bool> _played = [];
	private readonly List<bool> _favorite = [];

	public ItemToggleTests()
	{
		// the server answers with the item's user data after the change, as Jellyfin does
		_client.Setup(x => x.SetPlayed(It.IsAny<BaseItemDto>(), It.IsAny<bool>()))
			.Callback<BaseItemDto, bool>((_, played) => _played.Add(played))
			.ReturnsAsync((BaseItemDto dto, bool played) => new UserItemDataDto { Played = played, IsFavorite = dto.UserData?.IsFavorite, PlaybackPositionTicks = 0 });
		_client.Setup(x => x.SetIsFavorite(It.IsAny<BaseItemDto>(), It.IsAny<bool>()))
			.Callback<BaseItemDto, bool>((_, favorite) => _favorite.Add(favorite))
			.ReturnsAsync((BaseItemDto dto, bool favorite) => new UserItemDataDto { IsFavorite = favorite, Played = dto.UserData?.Played });
	}

	private GlobalCommands Commands() => new(Mock.Of<INavigationServiceCore>(), _client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());

	private static BaseItemDto Movie(bool played = false, bool favorite = false) =>
		new() { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Movie, Name = "Heat", UserData = new UserItemDataDto { Played = played, IsFavorite = favorite, PlaybackPositionTicks = 12_000 } };

	private static List<string> MenuTexts(BaseItemDto dto) => [.. ItemMenu.For(dto, user: null, includePlay: false, canEditSegments: false).SelectMany(g => g).Select(e => e.Text)];

	[Fact]
	public async Task TheMenusWatched_TwoClicks_MarkThenUnmark_AndTheMenuFollows()
	{
		var dto = Movie();
		var commands = Commands();

		await commands.ToggleWatched(dto);
		Assert.Contains("Mark as unwatched", MenuTexts(dto));
		await commands.ToggleWatched(dto);

		Assert.Equal([true, false], _played);
		Assert.False(dto.UserData!.Played);
		Assert.Contains("Mark as watched", MenuTexts(dto));
	}

	[Fact]
	public async Task TheMenusFavorite_TwoClicks_AddThenRemove_EvenWithoutUserData()
	{
		var dto = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Episode };
		var commands = Commands();

		await commands.ToggleFavorite(dto);
		Assert.Contains("Remove from favorites", MenuTexts(dto));
		await commands.ToggleFavorite(dto);

		Assert.Equal([true, false], _favorite);
		Assert.False(dto.UserData!.IsFavorite);
	}

	[Fact]
	public async Task AToggle_SendsTheStateItShows_TwoQuickClicksEndWhereTheSecondLeftIt()
	{
		// the second click comes before the first one's answer: the toggle shows off, so off is sent
		var dto = Movie();
		var commands = Commands();

		var first = commands.SetWatched(dto, true);
		var second = commands.SetWatched(dto, false);
		await Task.WhenAll(first, second);

		Assert.Equal([true, false], _played);
	}

	[Fact]
	public async Task TheServersAnswer_ReplacesTheItemsCopy_AndEverySurfaceIsTold()
	{
		var dto = Movie();
		var told = new List<ItemUserDataChangedEventArgs>();
		void OnChanged(object? s, ItemUserDataChangedEventArgs e)
		{
			if (e.ItemId == dto.Id)
			{
				told.Add(e);
			}
		}

		ItemUserData.Changed += OnChanged;
		try
		{
			Assert.True(await ItemUserData.SetPlayedAsync(_client.Object, dto, true));
		}
		finally
		{
			ItemUserData.Changed -= OnChanged;
		}

		var change = Assert.Single(told);
		Assert.False(change.Failed);
		Assert.Same(dto.UserData, change.UserData);
		Assert.True(dto.UserData!.Played);
		// what the server says, not only the flag: marking watched clears the resume point
		Assert.Equal(0, dto.UserData.PlaybackPositionTicks);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task AFailedRequest_KeepsTheOldState_AndStillTellsEverySurface_SoAFlippedToggleSpringsBack(bool throws)
	{
		var client = new Mock<IJellyfinClient>();
		var setup = client.Setup(x => x.SetIsFavorite(It.IsAny<BaseItemDto>(), It.IsAny<bool>()));
		if (throws)
		{
			setup.ThrowsAsync(new HttpRequestException("offline"));
		}
		else
		{
			setup.ReturnsAsync((UserItemDataDto?)null);
		}

		var dto = Movie(favorite: false);
		ItemUserDataChangedEventArgs? told = null;
		void OnChanged(object? s, ItemUserDataChangedEventArgs e)
		{
			if (e.ItemId == dto.Id)
			{
				told = e;
			}
		}

		ItemUserData.Changed += OnChanged;
		try
		{
			Assert.False(await ItemUserData.SetFavoriteAsync(client.Object, dto, true));
		}
		finally
		{
			ItemUserData.Changed -= OnChanged;
		}

		Assert.NotNull(told);
		Assert.True(told!.Failed);
		Assert.False(told.UserData.IsFavorite);
		Assert.False(dto.UserData!.IsFavorite);
	}

	[Fact]
	public async Task ASeasonPage_FollowsAnEpisodeMarkedWatched_PlayNamesTheNextOne()
	{
		var seriesId = Guid.NewGuid();
		var season = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Season, Name = "Season 1", SeriesId = seriesId };
		var episodes = Enumerable.Range(1, 3).Select(n => new BaseItemDto
		{
			Id = Guid.NewGuid(), Type = BaseItemDto_Type.Episode, SeriesId = seriesId, ParentIndexNumber = 1, IndexNumber = n, Name = $"Episode {n}",
			UserData = new UserItemDataDto { Played = false },
		}).ToList();
		_client.Setup(x => x.GetItem(season.Id!.Value)).ReturnsAsync(season);
		_client.Setup(x => x.GetItems(It.Is<BaseItemDto>(d => d.Id == season.Id), It.IsAny<bool>())).ReturnsAsync(() => new BaseItemDtoQueryResult { Items = episodes });
		_client.Setup(x => x.GetSeriesEpisodes(seriesId)).ReturnsAsync(() => new BaseItemDtoQueryResult { Items = episodes });
		var vm = new SeasonViewModel(_client.Object);
		await vm.OnNavigatedTo(season);
		Assert.Equal("Play S1 E1", vm.PlayTarget.Label);

		// the first episode's row: Watched (the server's answer goes into the episode, which the next request lists)
		await Commands().SetWatched(episodes[0], true);
		await WaitUntil(() => vm.PlayTarget.Label == "Play S1 E2");

		// the season itself: every episode is listed again (their rows show the server's state)
		_client.Invocations.Clear();
		await Commands().SetWatched(season, true);
		await WaitUntil(() => _client.Invocations.Any(i => i.Method.Name == nameof(IJellyfinClient.GetItems)));

		// gone from the page: no more refreshes (the page's own fetch of its season; marking the season reads its list for the
		// Undo, which is not the page's)
		await vm.OnNavigatedFrom();
		_client.Invocations.Clear();
		await Commands().SetWatched(season, false);
		await Task.Delay(100);
		Assert.DoesNotContain(_client.Invocations, i => i.Method.Name is nameof(IJellyfinClient.GetItem) or nameof(IJellyfinClient.GetSeriesEpisodes));
	}

	[Fact]
	public async Task ASeriesPage_FollowsItsNextEpisodeMarkedWatched()
	{
		var series = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Series, Name = "Lost" };
		var next = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Episode, SeriesId = series.Id, ParentIndexNumber = 1, IndexNumber = 1, Name = "Pilot", UserData = new UserItemDataDto() };
		var after = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Episode, SeriesId = series.Id, ParentIndexNumber = 1, IndexNumber = 2, Name = "Two", UserData = new UserItemDataDto() };
		_client.Setup(x => x.GetItem(series.Id!.Value)).ReturnsAsync(series);
		_client.Setup(x => x.GetNextUp(It.IsAny<BaseItemDto>())).ReturnsAsync(() => new BaseItemDtoQueryResult { Items = [next.UserData!.Played == true ? after : next] });
		_client.Setup(x => x.GetSeriesEpisodes(series.Id!.Value)).ReturnsAsync(() => new BaseItemDtoQueryResult { Items = [next, after] });
		var vm = new SeriesViewModel(_client.Object);
		await vm.OnNavigatedTo(series);
		Assert.Equal("Play S1 E1", vm.PlayTarget.Label);

		await Commands().SetWatched(next, true);

		// Play's label follows; Next Up only picks the season the page opens on, so it is not asked again (items review, item 6)
		await WaitUntil(() => vm.PlayTarget.Label == "Play S1 E2");
		await vm.OnNavigatedFrom();
	}

	private static async Task WaitUntil(Func<bool> condition)
	{
		var watch = System.Diagnostics.Stopwatch.StartNew();
		while (!condition())
		{
			Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "timed out");
			await Task.Delay(10);
		}
	}
}
