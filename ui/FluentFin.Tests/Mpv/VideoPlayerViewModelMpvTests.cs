using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json.Nodes;
using FluentFin.Contracts.Services;
using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Settings;
using FluentFin.Services;
using FluentFin.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;
using Xunit.Abstractions;

namespace FluentFin.Tests.Mpv;

/// <summary>The player page with the mpv player selected. Uses SessionInfo (static), so the tests of this class run one at a time.</summary>
public sealed partial class VideoPlayerViewModelMpvTests
{
	private readonly Mock<IJellyfinClient> _client = new();
	private readonly Mock<ISettings> _settings = new();
	private readonly Mock<INavigationService> _navigation = new();
	private readonly Subject<IOutboundWebSocketMessage> _webSocket = new();
	private readonly RecordingBackend _backend = new();
	private readonly MpvPlaybackService _playback;
	private readonly ListLogger<MpvPlaybackService> _playbackLog = new();
	private readonly ListLogger<VideoPlayerViewModel> _pageLog = new();
	private readonly ITestOutputHelper _output;

	static VideoPlayerViewModelMpvTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public VideoPlayerViewModelMpvTests(ITestOutputHelper output)
	{
		_output = output;
		_playback = new MpvPlaybackService(_backend, _client.Object, _playbackLog);
		_settings.SetupGet(x => x.MediaPlayer).Returns(MediaPlayerType.Mpv);
		_settings.SetupGet(x => x.Servers).Returns(new ObservableCollection<SavedServer> { new() { Id = "server-1", DisplayName = "Home" } });
		_client.SetupGet(x => x.BaseUrl).Returns("http://jf:8096");

		SessionInfo.BaseUrl = "http://jf:8096";
		SessionInfo.ServerId = "server-1";
		SessionInfo.AccessToken = "the-token";
		SessionInfo.CurrentUser = new UserDto { Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Name = "alice" };
		SessionInfo.DeviceId = "device-1";
		SessionInfo.DeviceName = "PC";
		SessionInfo.ClientName = "FluentFin";
		SessionInfo.ClientVersion = "1.0.0.0";
		SessionInfo.GroupId = null;
	}

	[Fact]
	public async Task MpvPath_NeverUsesFluentFinsOwnPlaybackOrReporting()
	{
		var movie = Movie(resumeTicks: 5_000_000);
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModel();

		await vm.OnNavigatedTo(movie);
		await PushState(vm, "playing", movie.Id!.Value);
		await vm.OnNavigatedFrom();

		_client.Verify(x => x.GetMediaUrl(It.IsAny<BaseItemDto>(), It.IsAny<PlaybackSelection?>()), Times.Never);
		_client.Verify(x => x.GetMediaSegments(It.IsAny<BaseItemDto>(), It.IsAny<MediaSegmentType[]?>()), Times.Never);
		_client.Verify(x => x.Playing(It.IsAny<BaseItemDto>()), Times.Never);
		_client.Verify(x => x.Progress(It.IsAny<PlaybackProgressInfo>()), Times.Never);
		_client.Verify(x => x.Stop(), Times.Never);
		_client.Verify(x => x.Stop(It.IsAny<PlaybackStopInfo>()), Times.Never);

		Assert.Equal(["set_fullscreen", "play", "stop"], _backend.Commands);
		var play = _backend.Args("play");
		Assert.Equal(movie.Id.Value.ToString("N"), (string?)play["itemIds"]![0]);
		Assert.Equal(5_000_000L, (long?)play["startPositionTicks"]);
		Assert.True(vm.IsMpv);
		Assert.Null(vm.MediaPlayer);
	}

	[Fact]
	public async Task Connect_UsesFluentFinsLoginAndDevice()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);

		await CreateViewModel().OnNavigatedTo(movie);

		var identity = Assert.Single(_backend.Identities);
		Assert.Equal(new BridgeIdentity("http://jf:8096", "server-1", "Home", "aaaaaaaa000000000000000000000001", "alice", "the-token", "device-1", "PC", "FluentFin", "1.0.0.0"), identity);
	}

	[Fact]
	public async Task EpisodePageRequest_StartsAtTheEpisode_WithThePickedStreams()
	{
		var show = new FakeLibrary(_client).AddSeries(seasons: 1, episodesPerSeason: 3, played: 1);
		var episodes = show.Episodes;

		await CreateViewModel().OnNavigatedTo(new PlaybackRequest(FakeLibrary.PageCopy(episodes[1]), new PlaybackSelection(2, PlaybackSelection.NoSubtitles)));

		var play = _backend.Args("play");
		Assert.Equal(episodes.Select(x => x.Id!.Value.ToString("N")), play["itemIds"]!.AsArray().Select(x => (string?)x));
		Assert.Equal(1, (int?)play["startIndex"]);
		Assert.Equal(2000L, (long?)play["startPositionTicks"]);
		Assert.Equal(2, (int?)play["audioStreamIndex"]);
		Assert.Equal(-1, (int?)play["subtitleStreamIndex"]);
	}

	[Fact]
	public async Task PlayFromBeginning_StartsTheEpisodeAtZero_InItsPlaceInTheSeries()
	{
		// fluentfin-mpv: "Play from beginning" on the item page (ux-review I2); the episode has a resume point of 2000 ticks
		var show = new FakeLibrary(_client).AddSeries(seasons: 1, episodesPerSeason: 3, played: 1);
		var episodes = show.Episodes;

		await CreateViewModel().OnNavigatedTo(new PlaybackRequest(FakeLibrary.PageCopy(episodes[1]), null, FromBeginning: true));

		var play = _backend.Args("play");
		Assert.Equal(episodes.Select(x => x.Id!.Value.ToString("N")), play["itemIds"]!.AsArray().Select(x => (string?)x));
		Assert.Equal(1, (int?)play["startIndex"]);
		Assert.Equal(0L, (long?)play["startPositionTicks"]);
		Assert.Null(play["audioStreamIndex"]);
		Assert.Null(play["subtitleStreamIndex"]);
	}

	[Fact]
	public async Task PlayFromBeginning_StartsTheMovieAtZero()
	{
		var movie = Movie(resumeTicks: 5_000_000);
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);

		await CreateViewModel().OnNavigatedTo(new PlaybackRequest(movie, null, FromBeginning: true));

		Assert.Equal(0L, (long?)_backend.Args("play")["startPositionTicks"]);
	}

	[Fact]
	public async Task RemotePlay_IsPlayedAsSent()
	{
		var request = new BridgePlayRequest(["a1", "b2"], 1, 99, "source-1", 3, 4);

		await CreateViewModel().OnNavigatedTo(request);

		var play = _backend.Args("play");
		Assert.True(JsonNode.DeepEquals(BridgeCommands.Play(request), play), play.ToJsonString());
		_client.Verify(x => x.GetItem(It.IsAny<Guid>()), Times.Never);
	}

	[Fact]
	public async Task RemoteControl_IsForwardedToTheBackend()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModel();
		await vm.OnNavigatedTo(movie);

		_webSocket.OnNext(new PlaystateMessage { Data = new PlaystateRequest { Command = PlaystateRequest_Command.Pause } });
		_webSocket.OnNext(new PlaystateMessage { Data = new PlaystateRequest { Command = PlaystateRequest_Command.Seek, SeekPositionTicks = 42 } });

		await BridgeClientTests.WaitUntil(() => _backend.Commands.Contains("seek"));
		Assert.Equal(["set_fullscreen", "play", "pause", "seek"], _backend.Commands);
		Assert.Equal(42L, (long?)_backend.Args("seek")["positionTicks"]);
		_client.Verify(x => x.SignalPauseForSyncPlay(), Times.Never);
	}

	[Fact]
	public async Task SyncPlayQueue_IsNotPlayed_AndSaysWhy()
	{
		var vm = CreateViewModel();

		await vm.OnNavigatedTo(new PlayQueueUpdate { PlayingItemIndex = 0, Playlist = [new SyncPlayQueueItem { ItemId = Guid.NewGuid() }] });

		Assert.DoesNotContain("play", _backend.Commands);
		Assert.Contains("SyncPlay", vm.MpvMessage);
		_client.Verify(x => x.SignalReadyForSyncPlay(It.IsAny<ReadyRequestDto>()), Times.Never);
	}

	[Fact]
	public async Task LeavingAReplacedPage_DoesNotStopTheNewPagesPlayback()
	{
		var first = Movie();
		var second = Movie();
		_client.Setup(x => x.GetItem(first.Id!.Value)).ReturnsAsync(first);
		_client.Setup(x => x.GetItem(second.Id!.Value)).ReturnsAsync(second);
		var oldPage = CreateViewModel();
		var newPage = CreateViewModel();

		await oldPage.OnNavigatedTo(first);
		await newPage.OnNavigatedTo(second);
		await oldPage.OnNavigatedFrom();

		Assert.Equal(["set_fullscreen", "play", "set_fullscreen", "play"], _backend.Commands);

		await newPage.OnNavigatedFrom();

		Assert.Equal(["set_fullscreen", "play", "set_fullscreen", "play", "stop"], _backend.Commands);
	}

	[Fact]
	public async Task IdleAfterPlayback_GoesBack()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModel();
		await vm.OnNavigatedTo(movie);

		// an idle state before this page's item played (the connect, a previous page) is not the end
		await PushState(vm, "idle", null);
		_navigation.Verify(x => x.GoBack(), Times.Never);

		await PushState(vm, "opening", movie.Id!.Value);
		await PushState(vm, "playing", movie.Id!.Value);
		Assert.True(vm.IsMpvVideoVisible);
		vm.OnMpvEvent(vm.MpvGeneration, new BridgeEvent("ended", new JsonObject { ["itemId"] = movie.Id.Value.ToString("N"), ["reason"] = "eof" }));
		await PushState(vm, "idle", null);

		_navigation.Verify(x => x.GoBack(), Times.Once);
		Assert.False(vm.IsMpvVideoVisible);
	}

	[Fact]
	public async Task IdleAfterPlayback_GoesBackThePagesWay_WhenThePageSaysHow()
	{
		// the self-test's page sits on the window's root frame, not the shell's: the page goes back with that frame's service
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModel();
		var backs = 0;
		vm.GoBack = () =>
		{
			backs++;
			return false;
		};
		await vm.OnNavigatedTo(movie);

		await PushState(vm, "opening", movie.Id!.Value);
		await PushState(vm, "playing", movie.Id!.Value);
		await PushState(vm, "idle", null);

		Assert.Equal(1, backs);
		_navigation.Verify(x => x.GoBack(), Times.Never);
		Assert.Equal("Playback ended", vm.MpvStatusText);
	}

	[Fact]
	public async Task FullscreenRequest_IsDoneByThePage_ThenToldToTheBackend()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModel();
		var requested = new List<bool>();
		vm.SetFullScreen = requested.Add;
		await vm.OnNavigatedTo(movie);

		vm.OnMpvEvent(vm.MpvGeneration, new BridgeEvent("fullscreen_request", new JsonObject { ["fullscreen"] = true }));

		await BridgeClientTests.WaitUntil(() => _backend.Commands.Contains("set_fullscreen"));
		Assert.Equal([true], requested);
		Assert.Equal(true, (bool?)_backend.Args("set_fullscreen")["fullscreen"]);
	}

	[Fact]
	public async Task OtherPlayers_DoNotTouchTheBackend()
	{
		_settings.SetupGet(x => x.MediaPlayer).Returns(MediaPlayerType.Vlc);
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModel();

		await vm.OnNavigatedTo(movie);

		Assert.False(vm.IsMpv);
		Assert.Equal(0, _backend.Starts);
		Assert.Empty(_backend.Commands);
	}

	[Fact]
	public async Task SelfTest_PlaysTheTestSourceWithADevBackend_WithoutLogin()
	{
		SessionInfo.AccessToken = null!;
		_settings.SetupGet(x => x.MediaPlayer).Returns(MediaPlayerType.Vlc);
		var vm = CreateViewModel();

		await vm.OnNavigatedTo(MpvSelfTestRequest.Default);

		Assert.True(vm.IsMpv);
		Assert.True(_backend.Dev);
		Assert.Equal(new BridgeIdentity?[] { null }, _backend.Identities);
		Assert.Equal(["set_fullscreen", "play_file"], _backend.Commands);
		Assert.Equal(MpvSelfTestRequest.TestSource, (string?)_backend.Args("play_file")["url"]);
		Assert.True(vm.IsMpvVideoVisible);
	}

	[Fact]
	public async Task BackendFailure_IsShownOnThePage()
	{
		_backend.StartFailure = new BridgeException("The mpv backend was not found (looked for x)");
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModel();

		await vm.OnNavigatedTo(movie);

		Assert.Equal("Starting mpv failed: The mpv backend was not found (looked for x)", vm.MpvMessage);
		Assert.True(vm.MpvMessageIsError);
		Assert.False(vm.IsMpvVideoVisible);
	}

	[Fact]
	public async Task ForeignPlayback_WhileThisPageIsStillLoadingItsItem_IsNotAdopted()
	{
		// the first await of the page (the item lookup): the backend still plays the page before, then stops it
		var movie = Movie();
		var lookup = new TaskCompletionSource<BaseItemDto?>();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).Returns(lookup.Task);
		var vm = CreateViewModel();

		var navigating = vm.OnNavigatedTo(movie);
		await PushState(vm, "playing", Guid.NewGuid());
		await PushState(vm, "idle", null);
		lookup.SetResult(movie);
		await navigating;

		_navigation.Verify(x => x.GoBack(), Times.Never);
		Assert.Equal(["set_fullscreen", "play"], _backend.Commands);
	}

	[Fact]
	public async Task ForeignPlayback_BeforeTheBackendTookThisPagesPlay_IsNotAdopted()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var start = new TaskCompletionSource();
		_backend.StartGate = start.Task;
		var vm = CreateViewModel();

		var navigating = vm.OnNavigatedTo(movie);
		await PushState(vm, "playing", Guid.NewGuid());
		await PushState(vm, "idle", null);
		start.SetResult();
		await navigating;

		_navigation.Verify(x => x.GoBack(), Times.Never);

		// this page's own playback ending does go back
		await PushState(vm, "playing", movie.Id!.Value);
		await PushState(vm, "idle", null);
		_navigation.Verify(x => x.GoBack(), Times.Once);
	}

	[Fact]
	public async Task EventsForAPageThatWasReplaced_AreIgnored_BeforeItIsLeft()
	{
		// the frame starts the new page before it tells the old one it was left
		var first = Movie();
		var second = Movie();
		_client.Setup(x => x.GetItem(first.Id!.Value)).ReturnsAsync(first);
		_client.Setup(x => x.GetItem(second.Id!.Value)).ReturnsAsync(second);
		var oldPage = CreateViewModel();
		var newPage = CreateViewModel();
		await oldPage.OnNavigatedTo(first);
		await newPage.OnNavigatedTo(second);

		await PushState(oldPage, "playing", first.Id!.Value);
		await PushState(oldPage, "idle", null);

		_navigation.Verify(x => x.GoBack(), Times.Never);
		Assert.True(oldPage.IsMpvVideoVisible);
	}

	[Fact]
	public async Task ACrashReportedToAPageThatWasReplaced_IsIgnored()
	{
		var first = Movie();
		var second = Movie();
		_client.Setup(x => x.GetItem(first.Id!.Value)).ReturnsAsync(first);
		_client.Setup(x => x.GetItem(second.Id!.Value)).ReturnsAsync(second);
		var oldPage = CreateViewModel();
		var newPage = CreateViewModel();
		await oldPage.OnNavigatedTo(first);
		await newPage.OnNavigatedTo(second);
		var crashed = new MpvBackendStatus(MpvBackendState.Crashed, LastError: "The mpv backend stopped unexpectedly (exit code 3)");

		oldPage.OnMpvStatus(oldPage.MpvGeneration, crashed);
		newPage.OnMpvStatus(newPage.MpvGeneration, crashed);

		Assert.Null(oldPage.MpvMessage);
		Assert.True(oldPage.IsMpvVideoVisible);
		Assert.Contains("exit code 3", newPage.MpvMessage);
		Assert.False(newPage.IsMpvVideoVisible);
	}

	[Fact]
	public async Task TheKeyboardGoesToMpv_OnceTheItemStarts_EvenWhenAQueuedPauseSkipsPlaying()
	{
		// the backend holds "opening" until a pause sent meanwhile lands, then reports "paused" (docs/BRIDGE.md §5)
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModel();
		var focused = 0;
		vm.FocusVideo = () => focused++;
		await vm.OnNavigatedTo(movie);

		await PushState(vm, "opening", movie.Id!.Value);
		Assert.Equal(0, focused);
		await PushState(vm, "paused", movie.Id!.Value);
		Assert.Equal(1, focused);
		await PushState(vm, "playing", movie.Id!.Value);

		Assert.Equal(1, focused);
		Assert.True(vm.IsMpvVideoVisible);
		_navigation.Verify(x => x.GoBack(), Times.Never);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task APageThatBegins_TellsTheBackendWhetherTheWindowIsFullScreen(bool fullScreen)
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModel();
		vm.IsFullScreen = () => fullScreen;

		await vm.OnNavigatedTo(movie);

		Assert.Equal(["set_fullscreen", "play"], _backend.Commands);
		Assert.Equal(fullScreen, (bool?)_backend.Args("set_fullscreen")["fullscreen"]);
	}

	[Fact]
	public async Task LeavingInFullScreen_RestoresTheWindow_AndTellsTheBackend_BeforeTheStop()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModel();
		var fullScreen = false;
		vm.IsFullScreen = () => fullScreen;
		vm.SetFullScreen = value => fullScreen = value;
		await vm.OnNavigatedTo(movie);
		vm.OnMpvEvent(vm.MpvGeneration, new BridgeEvent("fullscreen_request", new JsonObject { ["fullscreen"] = true }));
		await BridgeClientTests.WaitUntil(() => _backend.Commands.Length == 3);
		Assert.True(fullScreen);

		await vm.LeaveFullScreen();
		await vm.OnNavigatedFrom();

		Assert.False(fullScreen);
		Assert.Equal(["set_fullscreen", "play", "set_fullscreen", "set_fullscreen", "stop"], _backend.Commands);
		Assert.Equal(false, (bool?)_backend.Args("set_fullscreen")["fullscreen"]);
	}

	[Fact]
	public async Task LeavingInAWindow_ChangesNothing()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModel();
		var requested = new List<bool>();
		vm.IsFullScreen = () => false;
		vm.SetFullScreen = requested.Add;
		await vm.OnNavigatedTo(movie);

		await vm.LeaveFullScreen();
		await vm.OnNavigatedFrom();

		Assert.Empty(requested);
		Assert.Equal(["set_fullscreen", "play", "stop"], _backend.Commands);
	}

	[Fact]
	public async Task ARemoteControlMessageThatCannotBeRead_DoesNotEscape()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModel();
		await vm.OnNavigatedTo(movie);

		// a SetVolume whose number does not fit an int: the translation throws, and the caller is an async void subscriber
		var message = new GeneralCommandMessage { Data = new GeneralCommand { Name = GeneralCommand_Name.SetVolume, Arguments = new GeneralCommand_Arguments { AdditionalData = { ["Volume"] = 1e20m } } } };
		var error = await Record.ExceptionAsync(() => vm.OnMpvRemoteControl(vm.MpvGeneration, message));

		Assert.Null(error);
	}

	private static async Task PushState(VideoPlayerViewModel vm, string state, Guid? itemId)
	{
		vm.OnMpvEvent(vm.MpvGeneration, new BridgeEvent("state", new JsonObject { ["state"] = state, ["itemId"] = itemId?.ToString("N") }));
		await Task.Yield();
	}

	private static BaseItemDto Movie(long resumeTicks = 0) => new()
	{
		Id = Guid.NewGuid(),
		Name = "A movie",
		Type = BaseItemDto_Type.Movie,
		UserData = new UserItemDataDto { PlaybackPositionTicks = resumeTicks },
	};

	private VideoPlayerViewModel CreateViewModel() => new(
		_client.Object,
		new TrickplayViewModel(_client.Object),
		_pageLog,
		_webSocket,
		_navigation.Object,
		_settings.Object,
		Mock.Of<ITaskBarProgress>(),
		_playback);
}
