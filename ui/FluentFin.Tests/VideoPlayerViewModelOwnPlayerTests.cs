using System.Reactive;
using System.Reactive.Linq;
using FluentFin.Contracts.Services;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Settings;
using FluentFin.Services;
using FluentFin.Tests.Mpv;
using FluentFin.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FluentFin.Tests;

/// <summary>
/// fluentfin-mpv: an item page's play with FluentFin's own players (not mpv): the item asked for starts, at its resume point or,
/// with "Play from beginning", at 0 (docs/research/ux-review.md I2). The keyboard hook is off (ListenToKeyboard).
/// </summary>
public sealed class VideoPlayerViewModelOwnPlayerTests
{
	private readonly Mock<IJellyfinClient> _client = new();
	private readonly Mock<IMediaPlayerController> _player = new();
	private readonly Mock<ISettings> _settings = new();
	private readonly TaskCompletionSource<BaseItemDto> _playing = new(TaskCreationOptions.RunContinuationsAsynchronously);

	static VideoPlayerViewModelOwnPlayerTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public VideoPlayerViewModelOwnPlayerTests()
	{
		_settings.SetupGet(x => x.MediaPlayer).Returns(MediaPlayerType.Vlc);
		_client.SetupGet(x => x.BaseUrl).Returns("http://jf:8096");
		_client.Setup(x => x.GetMediaUrl(It.IsAny<BaseItemDto>(), It.IsAny<PlaybackSelection?>()))
			   .ReturnsAsync(new MediaResponse(new Uri("http://jf:8096/videos/x/stream"), PlaybackProgressInfo_PlayMethod.DirectPlay, "session", "source", new MediaSourceInfo { MediaStreams = [] }));
		_client.Setup(x => x.Playing(It.IsAny<BaseItemDto>())).Callback<BaseItemDto>(item => _playing.TrySetResult(item)).Returns(Task.CompletedTask);

		_player.SetupGet(x => x.DurationChanged).Returns(Observable.Never<TimeSpan>());
		_player.SetupGet(x => x.PositionChanged).Returns(Observable.Never<TimeSpan>());
		_player.SetupGet(x => x.Playing).Returns(Observable.Never<Unit>());
		_player.SetupGet(x => x.Paused).Returns(Observable.Never<Unit>());
		_player.SetupGet(x => x.Ended).Returns(Observable.Never<Unit>());
		_player.SetupGet(x => x.Errored).Returns(Observable.Never<Unit>());
		_player.SetupGet(x => x.Stopped).Returns(Observable.Never<Unit>());
		_player.SetupGet(x => x.MediaLoaded).Returns(Observable.Never<Unit>());
		_player.SetupGet(x => x.VolumeChanged).Returns(Observable.Never<double>());
		_player.SetupGet(x => x.SubtitleText).Returns(Observable.Never<string>());
	}

	[Fact]
	public async Task Movie_PlayFromBeginning_NeverSeeks()
	{
		var movie = new FakeLibrary(_client).AddMovie(resumeTicks: 5_000_000);

		await Play(new PlaybackRequest(movie, new PlaybackSelection(1, 2), FromBeginning: true));

		_player.Verify(x => x.Play(It.IsAny<Uri>(), It.IsAny<int>(), It.IsAny<int?>()), Times.Once);
		_player.Verify(x => x.SeekTo(It.IsAny<TimeSpan>()), Times.Never);
	}

	[Fact]
	public async Task Movie_Resume_SeeksToTheResumePoint()
	{
		var movie = new FakeLibrary(_client).AddMovie(resumeTicks: 5_000_000);

		await Play(new PlaybackRequest(movie, new PlaybackSelection(1, 2)));

		_player.Verify(x => x.SeekTo(TimeSpan.FromTicks(5_000_000)), Times.Once);
	}

	[Fact]
	public async Task Episode_TheOneAskedForStarts_NotTheSeriesFirstUnwatched()
	{
		// the first episode is played, so the series' first unwatched one is the second; the page asked for the third
		var show = new FakeLibrary(_client).AddSeries(seasons: 1, episodesPerSeason: 4, played: 1);
		var asked = show.Episodes[2];

		var started = await Play(new PlaybackRequest(FakeLibrary.PageCopy(asked), new PlaybackSelection(1, 2)));

		Assert.Equal(asked.Id, started.Id);
		_client.Verify(x => x.GetMediaUrl(It.Is<BaseItemDto>(d => d.Id == asked.Id), new PlaybackSelection(1, 2)), Times.Once);
		_player.Verify(x => x.SeekTo(TimeSpan.FromTicks(3000)), Times.Once);
	}

	[Fact]
	public async Task Episode_PlayFromBeginning_StartsTheOneAskedFor_AtZero()
	{
		var show = new FakeLibrary(_client).AddSeries(seasons: 1, episodesPerSeason: 4, played: 1);
		var asked = show.Episodes[2];

		var started = await Play(new PlaybackRequest(FakeLibrary.PageCopy(asked), new PlaybackSelection(1, 2), FromBeginning: true));

		Assert.Equal(asked.Id, started.Id);
		_player.Verify(x => x.SeekTo(It.IsAny<TimeSpan>()), Times.Never);
	}

	[Fact]
	public async Task ACardsPlay_StillStartsAtTheSeriesFirstUnwatched()
	{
		// a bare item (a card's Play, the season and series pages) is not a page's pick: FluentFin's own choice, as upstream
		var show = new FakeLibrary(_client).AddSeries(seasons: 1, episodesPerSeason: 4, played: 1);

		var started = await Play(FakeLibrary.PageCopy(show.Episodes[2]));

		Assert.Equal(show.Episodes[1].Id, started.Id);
	}

	/// <summary>Opens the player page with a player already there (as the page's host gives it) and waits for the item to start.</summary>
	private async Task<BaseItemDto> Play(object parameter)
	{
		var vm = new VideoPlayerViewModel(
			_client.Object,
			new TrickplayViewModel(_client.Object),
			NullLogger<VideoPlayerViewModel>.Instance,
			Observable.Never<IOutboundWebSocketMessage>(),
			Mock.Of<INavigationService>(),
			_settings.Object,
			Mock.Of<ITaskBarProgress>(),
			new Core.Mpv.MpvPlaybackService(new Mock<Core.Mpv.IMpvBackend> { DefaultValue = DefaultValue.Mock }.Object, _client.Object, NullLogger<Core.Mpv.MpvPlaybackService>.Instance))
		{
			ListenToKeyboard = false,
			MediaPlayer = _player.Object,
		};

		try
		{
			await vm.OnNavigatedTo(parameter);
			return await _playing.Task.WaitAsync(TimeSpan.FromSeconds(10));
		}
		finally
		{
			await vm.OnNavigatedFrom();
		}
	}
}
