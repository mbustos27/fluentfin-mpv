using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;
using FluentFin.Core.Settings;
using FluentFin.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Tests.Mpv;

/// <summary>fluentfin-mpv player controls: "Player controls: FluentFin" on the player page (docs/research/player-controls.md).</summary>
public sealed partial class VideoPlayerViewModelMpvTests
{
	private VideoPlayerViewModel CreateViewModelWithControls(bool use, string? whyNot = null)
	{
		var vm = CreateViewModel();
		vm.FluentFinControlsChoice = () => (use, whyNot);
		return vm;
	}

	[Fact]
	public async Task FluentFinControls_AreSetUpBeforeThePlay_SoMpvsOwnNeverShows()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModelWithControls(use: true);

		await vm.OnNavigatedTo(movie);

		Assert.True(vm.UsesFluentFinControls);
		Assert.NotNull(vm.MpvControls);
		// before the play: mpv's OSC never shows; after it was taken: the remembered subtitle size, this page's speed and
		// subtitle delay (normal and none: 1.2.0), then where the bar is
		Assert.Equal(["set_fullscreen", "set_controls", "play", "set_subtitle_size", "set_speed", "set_subtitle_delay", "set_controls"], _backend.Commands);
		Assert.True(JsonNode.DeepEquals(new JsonObject { ["speed"] = 1.0 }, _backend.Args("set_speed")), _backend.Args("set_speed").ToJsonString());
		Assert.True(JsonNode.DeepEquals(new JsonObject { ["seconds"] = 0.0 }, _backend.Args("set_subtitle_delay")), _backend.Args("set_subtitle_delay").ToJsonString());
		Assert.True(JsonNode.DeepEquals(new JsonObject { ["controls"] = "ui", ["subtitleInset"] = 0.0 }, _backend.Args("set_controls")), _backend.Args("set_controls").ToJsonString());
	}

	[Theory]
	[InlineData(false, true)]
	[InlineData(true, false)]
	public async Task ThePlay_IsPrecededByTheSkipIntroSwitches_FromSettings(bool online, bool learn)
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		_settings.SetupGet(x => x.LookUpIntrosOnline).Returns(online);
		_settings.SetupGet(x => x.LearnIntros).Returns(learn);
		var vm = CreateViewModel();

		await vm.OnNavigatedTo(movie);

		var all = _backend.AllCommands.ToList();
		Assert.True(all.IndexOf("set_skip_options") >= 0 && all.IndexOf("set_skip_options") < all.IndexOf("play"), string.Join(",", all));
		var args = _backend.Args("set_skip_options");
		Assert.Equal(online, (bool)args["onlineLookup"]!);
		Assert.Equal(learn, (bool)args["learnIntros"]!);
		// nothing else: no server, user, token or path rides along
		Assert.Equal(2, args.Count);
	}

	/// <summary>
	/// 1.2.0 (the interaction audit, item 7): the user's "play the next episode automatically" reaches the backend before every
	/// play, in either controls mode (until 1.1.3 the mpv player always played on), and the controls' countdown follows it.
	/// </summary>
	[Theory]
	[InlineData(false, true)]
	[InlineData(false, false)]
	[InlineData(true, true)]
	[InlineData(true, false)]
	public async Task ThePlay_IsPrecededByTheAutoPlayChoice(bool nixieControls, bool autoPlay)
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModelWithControls(use: nixieControls);
		vm.PlaysNextItem = () => autoPlay;

		await vm.OnNavigatedTo(movie);

		var all = _backend.AllCommands.ToList();
		Assert.True(all.IndexOf("set_auto_play") >= 0 && all.IndexOf("set_auto_play") < all.IndexOf("play"), string.Join(",", all));
		Assert.True(JsonNode.DeepEquals(new JsonObject { ["autoPlay"] = autoPlay }, _backend.Args("set_auto_play")), _backend.Args("set_auto_play").ToJsonString());
		Assert.Equal(nixieControls ? autoPlay : null, vm.MpvControls?.AutoPlay);
	}

	[Fact]
	public async Task FluentFinControls_TakeTheWheelAndStepFromPlaybackSettings()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		_settings.SetupGet(x => x.MouseWheelVolume).Returns(false);
		_settings.SetupGet(x => x.VolumeStep).Returns(12);
		var vm = CreateViewModelWithControls(use: true);

		await vm.OnNavigatedTo(movie);

		Assert.False(vm.MpvControls!.WheelChangesVolume);
		Assert.Equal(12, vm.MpvControls.Step);
	}

	[Fact]
	public async Task FluentFinControls_StartWithTheRememberedSubtitleSize_AndRememberTheNewOne()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		_settings.SetupGet(x => x.SubtitleSize).Returns(130);
		var vm = CreateViewModelWithControls(use: true);

		await vm.OnNavigatedTo(movie);
		Assert.Equal(130, vm.MpvControls!.SubtitleSize);

		await vm.MpvControls.SetSubtitleSize(160);
		_settings.VerifySet(x => x.SubtitleSize = 160, Times.Once);
	}

	[Fact]
	public async Task MpvsControls_WhenChosen_SendNoSetControls()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		_settings.SetupGet(x => x.MpvPlayerControls).Returns(MpvPlayerControls.Mpv);

		// the real choice: the setting says mpv, so libmpv is not even looked for
		var vm = CreateViewModel();
		vm.LibmpvUnavailable = () => throw new InvalidOperationException("libmpv is not looked for when mpv's controls are chosen");
		await vm.OnNavigatedTo(movie);

		Assert.False(vm.UsesFluentFinControls);
		Assert.Null(vm.MpvControls);
		Assert.Null(vm.MpvNotice);
		Assert.DoesNotContain("set_controls", _backend.Commands);
	}

	[Fact]
	public async Task FluentFinControls_ChosenButUnavailable_FallBackToMpvsAndSaySo()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModelWithControls(use: false, whyNot: "libmpv-2.dll was not found next to FluentFin");

		await vm.OnNavigatedTo(movie);

		Assert.False(vm.UsesFluentFinControls);
		Assert.Null(vm.MpvControls);
		Assert.Contains("libmpv-2.dll was not found", vm.MpvNotice);
		Assert.Contains("mpv's controls are used", vm.MpvNotice);
		Assert.StartsWith("Nixie's player controls", vm.MpvNotice);
		Assert.DoesNotContain("FluentFin's", vm.MpvNotice);
		Assert.DoesNotContain("set_controls", _backend.Commands);
		Assert.Contains("play", _backend.Commands);
	}

	/// <summary>The same fallback through the real choice and the real libmpv probe (not a stubbed answer), of a DLL that is not libmpv.</summary>
	[Fact]
	public async Task FluentFinControls_ChosenButLibmpvIsNotUsable_TheRealCheckFallsBackToMpvsAndSaysSo()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		_settings.SetupGet(x => x.MpvPlayerControls).Returns(MpvPlayerControls.FluentFin);
		var vm = CreateViewModel();
		vm.LibmpvUnavailable = () => FluentFin.Mpv.Libmpv.ProbeFile(Path.Combine(Environment.SystemDirectory, "version.dll"));

		await vm.OnNavigatedTo(movie);

		Assert.False(vm.UsesFluentFinControls);
		Assert.Null(vm.MpvControls);
		Assert.Contains("version.dll is not libmpv", vm.MpvNotice);
		Assert.Contains("mpv's controls are used", vm.MpvNotice);
		Assert.DoesNotContain("set_controls", _backend.Commands);
		Assert.Contains("play", _backend.Commands);
	}

	[Fact]
	public async Task FluentFinControls_FollowThisPagesPlayback_AndSendThroughIt()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModelWithControls(use: true);
		await vm.OnNavigatedTo(movie);
		var controls = vm.MpvControls!;

		vm.OnMpvEvent(vm.MpvGeneration, new BridgeEvent("item", new JsonObject { ["itemId"] = movie.Id!.Value.ToString("N"), ["name"] = "A movie", ["type"] = "Movie" }));
		await PushState(vm, "paused", movie.Id.Value);
		vm.OnMpvEvent(vm.MpvGeneration, new BridgeEvent("position", new JsonObject { ["positionTicks"] = 600_000_000L, ["durationTicks"] = 6_000_000_000L }));

		Assert.Equal("A movie", controls.Title);
		Assert.True(controls.IsPaused);
		Assert.Equal("1:00", controls.ElapsedText);

		await controls.TogglePause();
		Assert.Equal("toggle_pause", _backend.Commands[^1]);
	}

	/// <summary>
	/// The backend's segments and next events reach the controls: the Skip button follows its offer and sends skip_segment,
	/// Settings > Playback's "Show Skip intro and Skip credits" reaches them, and the next item is named from the server.
	/// </summary>
	[Fact]
	public async Task FluentFinControls_OfferTheBackendsSkip_AndNameTheNextEpisode()
	{
		// played as a film (a queue of one, no series to load); the backend's events below are an episode's
		var episode = Movie();
		var second = new BaseItemDto { Id = Guid.NewGuid(), Name = "Second", Type = BaseItemDto_Type.Episode, SeriesName = "Spike Show", ParentIndexNumber = 1, IndexNumber = 2 };
		_client.Setup(x => x.GetItem(episode.Id!.Value)).ReturnsAsync(episode);
		_client.Setup(x => x.GetItem(second.Id!.Value)).ReturnsAsync(second);
		_settings.SetupGet(x => x.ShowSkipPrompt).Returns(true);
		var vm = CreateViewModelWithControls(use: true);
		await vm.OnNavigatedTo(episode);
		var controls = vm.MpvControls!;
		var id = episode.Id!.Value.ToString("N");

		vm.OnMpvEvent(vm.MpvGeneration, new BridgeEvent("item", new JsonObject { ["itemId"] = id, ["name"] = "Pilot", ["type"] = "Episode" }));
		await PushState(vm, "playing", episode.Id.Value);
		vm.OnMpvEvent(vm.MpvGeneration, new BridgeEvent("segments", JsonNode.Parse($$"""
			{"itemId": "{{id}}", "segments": [{"type": "Outro", "startTicks": 0, "endTicks": 300000000, "action": "ask"}], "current": 0}
			""")));
		vm.OnMpvEvent(vm.MpvGeneration, new BridgeEvent("next", new JsonObject { ["itemId"] = id, ["nextItemId"] = second.Id!.Value.ToString("N"), ["autoPlay"] = true }));
		await Task.Yield();

		Assert.True(controls.ShowSkipPrompt);
		Assert.True(controls.IsSkipVisible);
		Assert.Equal("Skip credits", controls.SkipText);
		Assert.True(controls.CanNext);
		Assert.Equal("Next: S1 E2 · Second", controls.NextToolTip);

		await controls.SkipSegment();
		Assert.Equal("skip_segment", _backend.Commands[^1]);
		Assert.False(controls.IsSkipVisible);
	}

	[Fact]
	public async Task FluentFinControls_TakeTheSkipSettingOff()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		_settings.SetupGet(x => x.ShowSkipPrompt).Returns(false);
		var vm = CreateViewModelWithControls(use: true);
		await vm.OnNavigatedTo(movie);
		var id = movie.Id!.Value.ToString("N");

		vm.OnMpvEvent(vm.MpvGeneration, new BridgeEvent("item", new JsonObject { ["itemId"] = id, ["name"] = "A movie", ["type"] = "Movie" }));
		vm.OnMpvEvent(vm.MpvGeneration, new BridgeEvent("segments", JsonNode.Parse($$"""
			{"itemId": "{{id}}", "segments": [{"type": "Intro", "startTicks": 0, "endTicks": 300000000, "action": "ask"}], "current": 0}
			""")));

		Assert.False(vm.MpvControls!.ShowSkipPrompt);
		Assert.False(vm.MpvControls.IsSkipVisible);
	}

	[Fact]
	public async Task FluentFinControls_OfALeftPage_DriveNothing()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModelWithControls(use: true);
		await vm.OnNavigatedTo(movie);
		var controls = vm.MpvControls!;
		await vm.OnNavigatedFrom();

		// a newer page owns the backend now
		var next = CreateViewModelWithControls(use: true);
		await next.OnNavigatedTo(movie);
		var before = _backend.Commands.Length;
		await controls.TogglePause();
		await controls.SetVolume(10);

		Assert.Equal(before, _backend.Commands.Length);
	}

	[Fact]
	public async Task FluentFinControls_FullScreenButton_SetsTheWindowAndTellsTheBackend()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModelWithControls(use: true);
		var window = new List<bool>();
		vm.SetFullScreen = window.Add;
		vm.IsFullScreen = () => window.Count > 0 && window[^1];
		await vm.OnNavigatedTo(movie);

		vm.MpvControls!.ToggleFullScreen();
		await Task.Yield();

		Assert.Equal([true], window);
		Assert.True(vm.MpvControls.IsFullScreen);
		Assert.Equal(true, (bool?)_backend.Args("set_fullscreen")["fullscreen"]);

		await vm.LeaveFullScreen();
		Assert.Equal([true, false], window);
		Assert.False(vm.MpvControls.IsFullScreen);
	}
}
