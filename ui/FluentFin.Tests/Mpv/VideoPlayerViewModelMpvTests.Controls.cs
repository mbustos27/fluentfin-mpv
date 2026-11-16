using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;
using FluentFin.Core.Settings;
using FluentFin.ViewModels;
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
		// before the play: mpv's OSC never shows; after it was taken: from then on where the bar is
		Assert.Equal(["set_fullscreen", "set_controls", "play", "set_controls"], _backend.Commands);
		Assert.True(JsonNode.DeepEquals(new JsonObject { ["controls"] = "ui", ["subtitleInset"] = 0.0 }, _backend.Args("set_controls")), _backend.Args("set_controls").ToJsonString());
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
	public async Task MpvsControls_AreTheDefault_AndSendNoSetControls()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);

		// the real choice: the setting (a mock's default, mpv), so libmpv is not even looked for
		var vm = CreateViewModel();
		await vm.OnNavigatedTo(movie);

		Assert.False(vm.UsesFluentFinControls);
		Assert.Null(vm.MpvControls);
		Assert.DoesNotContain("set_controls", _backend.Commands);
		Assert.Equal(MpvPlayerControls.Mpv, default(MpvPlayerControls));
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

	/// <summary>Nothing replaced at all: the setting and the probe of the libmpv next to FluentFin (the tests' folder) decide.</summary>
	[Fact]
	public async Task FluentFinControls_Chosen_TheRealCheckOfTheLibmpvNextToFluentFinDecides()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		_settings.SetupGet(x => x.MpvPlayerControls).Returns(MpvPlayerControls.FluentFin);
		var vm = CreateViewModel();

		await vm.OnNavigatedTo(movie);

		var there = File.Exists(Path.Combine(AppContext.BaseDirectory, FluentFin.Mpv.Libmpv.Dll));
		Assert.Equal(there, vm.UsesFluentFinControls);
		Assert.Equal(there, _backend.Commands.Contains("set_controls"));
		if (!there)
		{
			Assert.Contains("libmpv-2.dll was not found next to FluentFin", vm.MpvNotice);
		}
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
