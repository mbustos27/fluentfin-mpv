using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// The movie and episode pages preselect their stream pickers. With the mpv player only a pick the user made goes to the
/// backend (else its remembered tracks and language defaults apply); the other players get the pickers as upstream.
/// </summary>
public sealed class ItemPageTrackPickTests
{
	private const int DefaultAudio = 1;
	private const int OtherAudio = 2;
	private const int DefaultSubtitle = 3;

	private readonly Mock<IJellyfinClient> _client = new();
	private readonly Mock<INavigationServiceCore> _navigation = new();
	private readonly Mock<ISettings> _settings = new();
	private readonly List<object?> _played = [];

	public ItemPageTrackPickTests()
	{
		SessionInfo.GroupId = null;
		SessionInfo.RemoteSessionId = null!;
		_navigation.Setup(x => x.NavigateTo(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<bool>()))
				   .Callback<string, object?, bool>((_, parameter, _) => _played.Add(parameter))
				   .Returns(true);
	}

	public static TheoryData<string> Pages => ["movie", "episode"];

	[Theory]
	[MemberData(nameof(Pages))]
	public async Task Mpv_PickersLeftAlone_SendsNoStreams(string page)
	{
		var (vm, play) = await Open(page, MediaPlayerType.Mpv);

		await play();

		var parameter = Assert.Single(_played);
		Assert.IsType<BaseItemDto>(parameter);
	}

	[Theory]
	[MemberData(nameof(Pages))]
	public async Task Mpv_AudioPicked_SendsBothStreams(string page)
	{
		var (vm, play) = await Open(page, MediaPlayerType.Mpv);

		Pick(vm, audio: OtherAudio);
		await play();

		var request = Assert.IsType<PlaybackRequest>(Assert.Single(_played));
		Assert.Equal(new PlaybackSelection(OtherAudio, DefaultSubtitle), request.Selection);
	}

	[Theory]
	[MemberData(nameof(Pages))]
	public async Task Mpv_SubtitlesSwitchedOff_SendsBothStreams(string page)
	{
		var (vm, play) = await Open(page, MediaPlayerType.Mpv);

		Pick(vm, subtitleNone: true);
		await play();

		var request = Assert.IsType<PlaybackRequest>(Assert.Single(_played));
		Assert.Equal(new PlaybackSelection(DefaultAudio, PlaybackSelection.NoSubtitles), request.Selection);
	}

	[Theory]
	[MemberData(nameof(Pages))]
	public async Task Mpv_APickOnThePreviousItem_DoesNotCarryOverToTheNextOne(string page)
	{
		// the same page shows another item: its pickers are preselected afresh, and nothing counts as picked yet
		var (vm, play) = await Open(page, MediaPlayerType.Mpv);
		Pick(vm, audio: OtherAudio);

		var next = Item(page == "movie" ? BaseItemDto_Type.Movie : BaseItemDto_Type.Episode);
		_client.Setup(x => x.GetItem(next.Id!.Value)).ReturnsAsync(next);
		await vm.OnNavigatedTo(next);
		await play();

		Assert.Equal(next, Assert.IsType<BaseItemDto>(Assert.Single(_played)));
	}

	[Theory]
	[MemberData(nameof(Pages))]
	public async Task OtherPlayers_GetThePickersAsUpstream(string page)
	{
		var (vm, play) = await Open(page, MediaPlayerType.Vlc);

		await play();
		Pick(vm, audio: OtherAudio);
		await play();

		Assert.Equal(2, _played.Count);
		Assert.Equal(new PlaybackSelection(DefaultAudio, DefaultSubtitle), Assert.IsType<PlaybackRequest>(_played[0]).Selection);
		Assert.Equal(new PlaybackSelection(OtherAudio, DefaultSubtitle), Assert.IsType<PlaybackRequest>(_played[1]).Selection);
	}

	[Fact]
	public void APick_FillsTheSideThePageHadNoChoiceFor_WithTheServerDefault()
	{
		var item = Item(BaseItemDto_Type.Movie);

		Assert.Null(MpvTrackPick.ForPlay(item, new PlaybackSelection(OtherAudio, DefaultSubtitle), userPicked: false));
		Assert.Equal(new PlaybackSelection(OtherAudio, DefaultSubtitle), MpvTrackPick.ForPlay(item, new PlaybackSelection(OtherAudio, null), userPicked: true));
		Assert.Equal(new PlaybackSelection(DefaultAudio, 4), MpvTrackPick.ForPlay(item, new PlaybackSelection(null, 4), userPicked: true));

		item.MediaSources = [];
		Assert.Equal(new PlaybackSelection(DefaultAudio, PlaybackSelection.NoSubtitles), MpvTrackPick.ForPlay(item, new PlaybackSelection(null, null), userPicked: true));
	}

	private async Task<(INavigationAware Page, Func<Task> Play)> Open(string page, MediaPlayerType player)
	{
		_settings.SetupGet(x => x.MediaPlayer).Returns(player);
		var commands = new GlobalCommands(_navigation.Object, _client.Object, Mock.Of<INavigationViewServiceCore>(), _settings.Object);

		if (page == "movie")
		{
			var movie = Item(BaseItemDto_Type.Movie);
			_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
			var vm = new MovieViewModel(_client.Object, commands);
			await vm.OnNavigatedTo(movie);
			return (vm, () => vm.PlayCommand.ExecuteAsync(null));
		}
		else
		{
			var episode = Item(BaseItemDto_Type.Episode);
			_client.Setup(x => x.GetItem(episode.Id!.Value)).ReturnsAsync(episode);
			var vm = new EpisodeViewModel(_client.Object, commands);
			await vm.OnNavigatedTo(episode);
			return (vm, () => vm.PlayCommand.ExecuteAsync(null));
		}
	}

	/// <summary>What the page's ComboBoxes do when the user chooses an entry.</summary>
	private static void Pick(INavigationAware page, int? audio = null, bool subtitleNone = false)
	{
		switch (page)
		{
			case MovieViewModel movie:
				if (audio is { } a) movie.SelectedAudio = movie.AudioStreams.Single(x => x.Index == a);
				if (subtitleNone) movie.SelectedSubtitle = movie.SubtitleStreams[0];
				break;
			case EpisodeViewModel episode:
				if (audio is { } b) episode.SelectedAudio = episode.AudioStreams.Single(x => x.Index == b);
				if (subtitleNone) episode.SelectedSubtitle = episode.SubtitleStreams[0];
				break;
		}
	}

	private static BaseItemDto Item(BaseItemDto_Type type) => new()
	{
		Id = Guid.NewGuid(),
		Name = "An item",
		Type = type,
		MediaStreams =
		[
			new MediaStream { Index = 0, Type = MediaStream_Type.Video },
			new MediaStream { Index = DefaultAudio, Type = MediaStream_Type.Audio, IsDefault = true },
			new MediaStream { Index = OtherAudio, Type = MediaStream_Type.Audio },
			new MediaStream { Index = DefaultSubtitle, Type = MediaStream_Type.Subtitle },
			new MediaStream { Index = 4, Type = MediaStream_Type.Subtitle },
		],
		MediaSources = [new MediaSourceInfo { DefaultAudioStreamIndex = DefaultAudio, DefaultSubtitleStreamIndex = DefaultSubtitle }],
	};
}
