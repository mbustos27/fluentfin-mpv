using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using FluentFin.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// fluentfin-mpv: the movie and episode pages' named play button, "Resume at 12:34" with "Play from beginning" next to it,
/// and what "Play from beginning" asks of each player (docs/research/ux-review.md I2). Sets SessionInfo (static): one at a time.
/// </summary>
[Collection(SessionInfoCollection.Name)]
public sealed class ItemPagePlayTests
{
	private const long ResumeTicks = 7_540_000_000; // 12:34

	private readonly Mock<IJellyfinClient> _client = new();
	private readonly Mock<INavigationServiceCore> _navigation = new();
	private readonly Mock<ISettings> _settings = new();
	private readonly List<object?> _played = [];

	// the SyncPlay path builds FluentFin's playlist, a ReactiveUI object
	static ItemPagePlayTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public ItemPagePlayTests()
	{
		SessionInfo.GroupId = null;
		SessionInfo.RemoteSessionId = null!;
		_navigation.Setup(x => x.NavigateTo(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<bool>()))
				   .Callback<string, object?, bool>((_, parameter, _) => _played.Add(parameter))
				   .Returns(true);
	}

	public static TheoryData<string, MediaPlayerType> PagesAndPlayers => new()
	{
		{ "movie", MediaPlayerType.Mpv },
		{ "episode", MediaPlayerType.Mpv },
		{ "movie", MediaPlayerType.Vlc },
		{ "episode", MediaPlayerType.Vlc },
	};

	[Theory]
	[InlineData("movie")]
	[InlineData("episode")]
	public async Task WithAResumePoint_TheButtonSaysWhere_AndOffersTheBeginning(string page)
	{
		var (vm, _, _) = await Open(page, MediaPlayerType.Mpv, ResumeTicks);

		Assert.Equal("Resume at 12:34", Label(vm));
		Assert.True(HasResumePoint(vm));
	}

	[Theory]
	[InlineData("movie")]
	[InlineData("episode")]
	public async Task WithoutAResumePoint_TheButtonSaysPlay_AndNoBeginningButton(string page)
	{
		var (vm, _, _) = await Open(page, MediaPlayerType.Mpv, 0);

		Assert.Equal("Play", Label(vm));
		Assert.False(HasResumePoint(vm));
	}

	[Theory]
	[InlineData("movie")]
	[InlineData("episode")]
	public async Task TheLabel_FollowsTheItemThePageShows(string page)
	{
		var (vm, _, _) = await Open(page, MediaPlayerType.Mpv, ResumeTicks);
		var changed = new List<string?>();
		((System.ComponentModel.INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

		var next = Item(page, 0);
		_client.Setup(x => x.GetItem(next.Id!.Value)).ReturnsAsync(next);
		await vm.OnNavigatedTo(next);

		Assert.Equal("Play", Label(vm));
		Assert.Contains("PlayLabel", changed);
		Assert.Contains("HasResumePoint", changed);
	}

	[Theory]
	[MemberData(nameof(PagesAndPlayers))]
	public async Task PlayFromBeginning_AsksThePlayerToStartAtZero(string page, MediaPlayerType player)
	{
		var (_, _, fromBeginning) = await Open(page, player, ResumeTicks);

		await fromBeginning();

		var request = Assert.IsType<PlaybackRequest>(Assert.Single(_played));
		Assert.True(request.FromBeginning);
		if (player == MediaPlayerType.Mpv)
		{
			// pickers left alone: no streams for the mpv player, as for Play (ItemPageTrackPickTests)
			Assert.Null(request.Selection);
		}
		else
		{
			Assert.NotNull(request.Selection);
		}
	}

	[Theory]
	[MemberData(nameof(PagesAndPlayers))]
	public async Task Resume_IsNotFromTheBeginning(string page, MediaPlayerType player)
	{
		var (_, play, _) = await Open(page, player, ResumeTicks);

		await play();

		Assert.False(Assert.Single(_played) is PlaybackRequest { FromBeginning: true });
	}

	[Fact]
	public async Task SyncPlay_FromBeginning_StartsTheGroupAtZero()
	{
		var movie = Item("movie", ResumeTicks);
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		_settings.SetupGet(x => x.MediaPlayer).Returns(MediaPlayerType.Vlc);
		PlayRequestDto? sent = null;
		_client.Setup(x => x.SignalNewPlaylist(It.IsAny<PlayRequestDto>())).Callback<PlayRequestDto>(r => sent = r).Returns(Task.CompletedTask);
		var commands = new GlobalCommands(_navigation.Object, _client.Object, Mock.Of<INavigationViewServiceCore>(), _settings.Object);

		SessionInfo.GroupId = Guid.NewGuid();
		try
		{
			await commands.Play(movie, null, fromBeginning: true);
			Assert.Equal(0L, sent!.StartPositionTicks);

			await commands.Play(movie, null);
			Assert.Equal(ResumeTicks, sent!.StartPositionTicks);
		}
		finally
		{
			SessionInfo.GroupId = null;
		}
	}

	[Fact]
	public void PlaylistItem_FromBeginning_SkipsTheResumePointOnce()
	{
		var item = new PlaylistItem { Title = "x", Dto = Item("movie", ResumeTicks), StartFromBeginning = true };

		Assert.Null(item.TakeStartPosition());
		Assert.Equal(TimeSpan.FromTicks(ResumeTicks), item.TakeStartPosition());
	}

	[Fact]
	public void PlaylistItem_SeeksToItsResumePoint_AsUpstream()
	{
		Assert.Equal(TimeSpan.FromTicks(ResumeTicks), new PlaylistItem { Title = "x", Dto = Item("movie", ResumeTicks) }.TakeStartPosition());
		Assert.Equal(TimeSpan.Zero, new PlaylistItem { Title = "x", Dto = Item("movie", 0) }.TakeStartPosition());
		Assert.Null(new PlaylistItem { Title = "x", Dto = new BaseItemDto() }.TakeStartPosition());
	}

	private async Task<(INavigationAware Page, Func<Task> Play, Func<Task> FromBeginning)> Open(string page, MediaPlayerType player, long resumeTicks)
	{
		_settings.SetupGet(x => x.MediaPlayer).Returns(player);
		var commands = new GlobalCommands(_navigation.Object, _client.Object, Mock.Of<INavigationViewServiceCore>(), _settings.Object);
		var item = Item(page, resumeTicks);
		_client.Setup(x => x.GetItem(item.Id!.Value)).ReturnsAsync(item);

		if (page == "movie")
		{
			var vm = new MovieViewModel(_client.Object, commands);
			await vm.OnNavigatedTo(item);
			return (vm, () => vm.PlayCommand.ExecuteAsync(null), () => vm.PlayFromBeginningCommand.ExecuteAsync(null));
		}
		else
		{
			var vm = new EpisodeViewModel(_client.Object, commands);
			await vm.OnNavigatedTo(item);
			return (vm, () => vm.PlayCommand.ExecuteAsync(null), () => vm.PlayFromBeginningCommand.ExecuteAsync(null));
		}
	}

	private static string Label(INavigationAware page) => page switch
	{
		MovieViewModel movie => movie.PlayLabel,
		EpisodeViewModel episode => episode.PlayLabel,
		_ => throw new ArgumentException(null, nameof(page)),
	};

	private static bool HasResumePoint(INavigationAware page) => page switch
	{
		MovieViewModel movie => movie.HasResumePoint,
		EpisodeViewModel episode => episode.HasResumePoint,
		_ => throw new ArgumentException(null, nameof(page)),
	};

	private static BaseItemDto Item(string page, long resumeTicks) => new()
	{
		Id = Guid.NewGuid(),
		Name = "An item",
		Type = page == "movie" ? BaseItemDto_Type.Movie : BaseItemDto_Type.Episode,
		UserData = new UserItemDataDto { PlaybackPositionTicks = resumeTicks },
		MediaStreams =
		[
			new MediaStream { Index = 0, Type = MediaStream_Type.Video },
			new MediaStream { Index = 1, Type = MediaStream_Type.Audio, IsDefault = true },
			new MediaStream { Index = 2, Type = MediaStream_Type.Subtitle },
		],
		MediaSources = [new MediaSourceInfo { DefaultAudioStreamIndex = 1, DefaultSubtitleStreamIndex = 2 }],
	};
}
