using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;
using FluentFin.ViewModels;
using Moq;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// 1.6 player on the player page: Settings' default quality at home and away goes before every play
/// (<c>set_quality_defaults</c>), a quality the server won't convert is said in Nixie's own words while the item plays on
/// (<c>not_allowed</c>), the Quality menu hears how the item is served (<c>stream</c>), and the Playback stats ask through
/// this page's generation.
/// </summary>
public sealed partial class VideoPlayerViewModelMpvTests
{
	[Fact]
	public async Task TheDefaultQuality_AtHomeAndAway_GoesBeforeThePlay()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		_settings.SetupGet(x => x.QualityAtHome).Returns(new QualityChoice(8_000_000));
		_settings.SetupGet(x => x.QualityAway).Returns(QualityChoice.Original);
		var vm = CreateViewModel();

		await vm.OnNavigatedTo(movie);

		var all = _backend.AllCommands.ToList();
		Assert.True(all.IndexOf("set_quality_defaults") >= 0 && all.IndexOf("set_quality_defaults") < all.IndexOf("play"), string.Join(",", all));
		Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"home":{"bitrate":8000000,"original":false},"away":{"bitrate":null,"original":true}}"""),
			_backend.Args("set_quality_defaults")), _backend.Args("set_quality_defaults").ToJsonString());
	}

	[Fact]
	public async Task NothingSetInSettings_SendsAutoForBoth()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModel();

		await vm.OnNavigatedTo(movie);

		Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"home":{"bitrate":null,"original":false},"away":{"bitrate":null,"original":false}}"""),
			_backend.Args("set_quality_defaults")), _backend.Args("set_quality_defaults").ToJsonString());
	}

	[Fact]
	public async Task AQualityTheServerWontConvert_IsSaidInPlainWords_AndPlaybackGoesOn()
	{
		var vm = await PagePlayingWithNoticeTimers();

		Error(vm, QualityChoices.RefusedText, code: "not_allowed");

		Assert.Equal(QualityChoices.RefusedText, vm.MpvNotice);
		Assert.Null(vm.MpvMessage);
		Assert.True(vm.IsMpvVideoVisible);
		Assert.Equal(["set_fullscreen", "play"], _backend.Commands);
		_navigation.Verify(x => x.GoBack(), Times.Never);
		_noticeTimers.Single().SetResult();
		Assert.Null(vm.MpvNotice);
	}

	[Fact]
	public async Task TheStreamEvent_ReachesTheQualityMenu()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModelWithControls(use: true);
		await vm.OnNavigatedTo(movie);
		await PushState(vm, "playing", movie.Id!.Value);
		var id = movie.Id!.Value.ToString("N");

		vm.OnMpvEvent(vm.MpvGeneration, new BridgeEvent(Bridge.Events.Item, JsonNode.Parse($$"""{"itemId": "{{id}}", "name": "A movie", "type": "Movie"}""")));
		vm.OnMpvEvent(vm.MpvGeneration, new BridgeEvent(Bridge.Events.Stream, JsonNode.Parse($$"""
			{"itemId": "{{id}}", "playMethod": "Transcode", "transcodeReasons": ["ContainerBitrateExceedsLimit"], "bitrate": 8000000,
			 "sourceWidth": 1920, "sourceHeight": 1080, "quality": {"bitrate": 8000000, "original": false}, "isLocal": true}
			""")));

		Assert.Equal("Transcoding · 8 Mbps · the file is over the bitrate limit", vm.MpvControls!.QualityHeader);
		Assert.Equal("Quality, 8 Mbps", vm.MpvControls.QualityName);
	}

	[Fact]
	public async Task ThePlaybackStats_AskThroughThisPage_AndTheSettingShowsTheButton()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		_settings.SetupGet(x => x.ShowPlaybackStatsButton).Returns(true);
		var vm = CreateViewModelWithControls(use: true);
		await vm.OnNavigatedTo(movie);

		Assert.True(vm.MpvControls!.ShowStatsButton);
		Assert.DoesNotContain("get_stats", _backend.AllCommands);
		vm.MpvControls.ToggleStats();
		Assert.Contains("get_stats", _backend.AllCommands);
	}
}
