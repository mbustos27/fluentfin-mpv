using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.5 qacheck (the first run against a real Jellyfin server): a playlist's Play plays the playlist in its own order. Before,
/// the first item alone decided the queue: "QA Playlist" (an episode, a film, a music video) played the episode's whole
/// series, and Shift+N went to the series' next episode instead of the film.
/// </summary>
public sealed class PlaylistPlayTests
{
	private static readonly Guid SeriesId = Guid.NewGuid();

	private static List<BaseItemDto> QaPlaylist(bool episodeWatched = false, long filmPosition = 0, bool filmWatched = false) =>
	[
		new() { Id = Guid.NewGuid(), Name = "Where Is Everybody?", Type = BaseItemDto_Type.Episode, SeriesId = SeriesId, SeriesName = "The Twilight Zone",
			UserData = new UserItemDataDto { Played = episodeWatched } },
		new() { Id = Guid.NewGuid(), Name = "Big Buck Bunny", Type = BaseItemDto_Type.Movie,
			UserData = new UserItemDataDto { Played = filmWatched, PlaybackPositionTicks = filmPosition } },
		// a song in a video playlist: nothing the page plays (CollectionBrowse.IsPlayable), so not in the queue
		new() { Id = Guid.NewGuid(), Name = "A song", Type = BaseItemDto_Type.Audio, UserData = new UserItemDataDto { Played = false } },
		new() { Id = Guid.NewGuid(), Name = "Bars and Tone", Type = BaseItemDto_Type.MusicVideo, UserData = new UserItemDataDto { Played = false } },
	];

	private static async Task<(CollectionViewModel Vm, Mock<INavigationServiceCore> Navigation, Mock<IJellyfinClient> Client)> Page(
		List<BaseItemDto> items, MediaPlayerType player = MediaPlayerType.Mpv, BaseItemDto_Type type = BaseItemDto_Type.Playlist)
	{
		var list = new BaseItemDto { Id = Guid.NewGuid(), Type = type, Name = "QA Playlist" };
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetItem(list.Id!.Value)).ReturnsAsync(list);
		client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = items });
		client.Setup(x => x.GetPlaylistItems(list.Id!.Value)).ReturnsAsync(new BaseItemDtoQueryResult { Items = items });
		var settings = new Mock<ISettings>();
		settings.SetupGet(x => x.MediaPlayer).Returns(player);
		var navigation = new Mock<INavigationServiceCore>();
		var vm = new CollectionViewModel(client.Object, new GlobalCommands(navigation.Object, client.Object, Mock.Of<INavigationViewServiceCore>(), settings.Object));
		await vm.OnNavigatedTo(list);
		return (vm, navigation, client);
	}

	private static BridgePlayRequest Played(Mock<INavigationServiceCore> navigation) => Queued(navigation).Queue;

	// the queue and the item it starts with (the loading screen names it)
	private static QueuePlayRequest Queued(Mock<INavigationServiceCore> navigation) =>
		Assert.IsType<QueuePlayRequest>(Assert.Single(navigation.Invocations, i => i.Method.Name == nameof(INavigationServiceCore.NavigateTo)).Arguments[1]);

	private static string[] Ids(List<BaseItemDto> items, params int[] at) => [.. at.Select(i => BridgePlayRequest.ToBridgeId(items[i].Id!.Value))];

	[Fact]
	public async Task Play_PlaysThePlaylistInItsOrder_NotTheEpisodesSeries()
	{
		var items = QaPlaylist();
		var (vm, navigation, client) = await Page(items);

		await vm.Play();

		var played = Played(navigation);
		Assert.Equal(Ids(items, 0, 1, 3), played.ItemIds);
		Assert.Equal(0, played.StartIndex);
		Assert.Null(played.AudioStreamIndex);
		Assert.Null(played.SubtitleStreamIndex);
		// the series is never asked for: the queue is the playlist's
		client.Verify(x => x.GetSeriesEpisodes(It.IsAny<Guid>()), Times.Never);
	}

	[Fact]
	public async Task Play_StartsAtTheFirstItemNotWatched_AtItsResumePoint()
	{
		var items = QaPlaylist(episodeWatched: true, filmPosition: 300_000_000);
		var (vm, navigation, _) = await Page(items);

		Assert.Equal("Resume Big Buck Bunny", vm.PlayLabel);
		await vm.Play();

		var played = Played(navigation);
		Assert.Equal(Ids(items, 0, 1, 3), played.ItemIds);
		Assert.Equal(1, played.StartIndex);
		Assert.Equal(300_000_000, played.StartPositionTicks);
		Assert.Same(items[1], Queued(navigation).First);
	}

	[Fact]
	public async Task Play_AllWatched_PlaysTheWholePlaylistAgainFromTheStart()
	{
		var items = QaPlaylist(episodeWatched: true, filmWatched: true);
		items[2].UserData!.Played = true;
		items[3].UserData!.Played = true;
		items[0].UserData!.PlaybackPositionTicks = 120_000_000;
		var (vm, navigation, _) = await Page(items);

		await vm.Play();

		var played = Played(navigation);
		Assert.Equal(0, played.StartIndex);
		Assert.Equal(0, played.StartPositionTicks);
	}

	[Fact]
	public async Task Shuffle_PlaysTheWholePlaylist_ThePickFirst_TheRestInARandomOrder()
	{
		var items = QaPlaylist();
		var (vm, navigation, client) = await Page(items);
		vm.Pick = n => n - 1;   // the last of what is left, each time

		await vm.Shuffle();

		// the pick: the last item not watched (the music video); then the film, then the episode; never the song
		var played = Played(navigation);
		Assert.Equal(Ids(items, 3, 1, 0), played.ItemIds);
		Assert.Equal(0, played.StartIndex);
		client.Verify(x => x.GetSeriesEpisodes(It.IsAny<Guid>()), Times.Never);
	}

	[Fact]
	public async Task ABoxSet_AndAnotherPlayer_KeepTheirPlay()
	{
		// a box set: its first film not watched, as before (the item page's Play)
		var films = QaPlaylist();
		var (vm, navigation, _) = await Page(films, type: BaseItemDto_Type.BoxSet);
		await vm.Play();
		Assert.IsNotType<QueuePlayRequest>(Assert.Single(navigation.Invocations, i => i.Method.Name == nameof(INavigationServiceCore.NavigateTo)).Arguments[1]);

		// FluentFin's own players build their own playlist from the item
		var (vm2, navigation2, _) = await Page(QaPlaylist(), MediaPlayerType.Flyleaf);
		await vm2.Play();
		Assert.IsNotType<QueuePlayRequest>(Assert.Single(navigation2.Invocations, i => i.Method.Name == nameof(INavigationServiceCore.NavigateTo)).Arguments[1]);
	}
}
