using System.Diagnostics;
using System.Text.Json.Nodes;
using FluentFin.Contracts.Services;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using FluentFin.UI.Core.Contracts.Services;
using FluentFin.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// Round 6, item 1: Log out and Switch user navigate the window's root frame, which takes the shell away with the player
/// page and tells the page nothing. The mpv playback must still stop, exactly once, and its Stopped report must go out
/// before the token is revoked (docs/BRIDGE.md §6). Any other removal of the page from the window is a leave too.
/// </summary>
public sealed partial class VideoPlayerViewModelMpvTests
{
	private readonly Mock<INavigationService> _setupNavigation = new();

	private TitleBarViewModel CreateTitleBar()
	{
		_setupNavigation.Setup(x => x.NavigateTo(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<bool>()))
			.Callback<string, object?, bool>((key, _, _) => _backend.Timeline.Enqueue("navigate to " + key.Split('.')[^1]))
			.Returns(true);
		_client.Setup(x => x.Logout()).Callback(() => _backend.Timeline.Enqueue("revoke the token")).Returns(Task.CompletedTask);
		return new TitleBarViewModel(new Mock<INavigationService>().Object, _setupNavigation.Object, Mock.Of<INavigationViewService>(), _client.Object, _playback);
	}

	private async Task<VideoPlayerViewModel> PagePlaying()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModel();
		await vm.OnNavigatedTo(movie);
		await PushState(vm, "playing", movie.Id!.Value);
		return vm;
	}

	private void VerifyNoStopFromFluentFin()
	{
		_client.Verify(x => x.Stop(), Times.Never);
		_client.Verify(x => x.Stop(It.IsAny<PlaybackStopInfo>()), Times.Never);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task SigningOut_WhilePlaying_StopsThePlayback_AndWaitsForItsReport_BeforeLeavingAndBeforeTheTokenIsRevoked(bool switchUser)
	{
		var vm = await PagePlaying();
		var report = new TaskCompletionSource();
		_backend.ReportsDone = report.Task;
		// only the report decides here, not the clock (a loaded test run took the 2 s while the test was still checking)
		_playback.SignOutTimeout = TimeSpan.FromSeconds(30);
		var titleBar = CreateTitleBar();

		var signingOut = switchUser ? titleBar.SwitchUser() : titleBar.Logout();
		await BridgeClientTests.WaitUntil(() => _backend.Timeline.Contains("wait for the stop report"));
		await Task.Delay(150);
		Assert.DoesNotContain(_backend.Timeline, x => x.StartsWith("navigate", StringComparison.Ordinal) || x == "revoke the token");
		// the stop's idle reaches the page that was left: it does not go back on its own
		await PushState(vm, "idle", null);
		report.SetResult();
		await signingOut.WaitAsync(TimeSpan.FromSeconds(5));

		// the root frame's navigation took the page out of the window (its Unloaded); a later navigation of the old shell's
		// frame would tell it again: neither sends anything more
		await vm.OnPageRemoved(navigatedFrom: false, loadedAgain: false);
		await vm.OnNavigatedFrom();

		Assert.Equal(["send set_fullscreen", "send play", "send stop", "wait for the stop report", "stop report done",
					  switchUser ? "navigate to LoginViewModel" : "navigate to SelectServerViewModel", "revoke the token"], _backend.Timeline);
		Assert.Single(_backend.Commands, x => x == "stop");
		_navigation.Verify(x => x.GoBack(), Times.Never);
		Assert.False(vm.IsMpvVideoVisible);
		VerifyNoStopFromFluentFin();
	}

	[Fact]
	public async Task SigningOut_GoesOnAfterAboutTwoSeconds_WhenTheStopReportNeverComes()
	{
		await PagePlaying();
		_backend.ReportsDone = new TaskCompletionSource().Task;
		var titleBar = CreateTitleBar();

		var watch = Stopwatch.StartNew();
		await titleBar.Logout().WaitAsync(TimeSpan.FromSeconds(10));

		Assert.InRange(watch.Elapsed.TotalSeconds, 1.8, 4.0);
		Assert.Equal(["send set_fullscreen", "send play", "send stop", "wait for the stop report", "stop report timed out",
					  "navigate to SelectServerViewModel", "revoke the token"], _backend.Timeline);
		VerifyNoStopFromFluentFin();
	}

	[Fact]
	public async Task SigningOut_AfterThePageWasLeft_SendsNoSecondStop_ButStillWaitsForTheReport()
	{
		// the page was left the usual way (its stop is out, its report maybe not yet): the sign-out stops nothing more
		var vm = await PagePlaying();
		await vm.OnNavigatedFrom();
		var titleBar = CreateTitleBar();

		await titleBar.Logout().WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(["send set_fullscreen", "send play", "send stop", "wait for the stop report", "stop report done",
					  "navigate to SelectServerViewModel", "revoke the token"], _backend.Timeline);
	}

	[Fact]
	public async Task SigningOut_WithNothingEverPlayed_SendsNothing()
	{
		var titleBar = CreateTitleBar();

		await titleBar.SwitchUser().WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Empty(_backend.Commands);
		Assert.Equal(["wait for the stop report", "stop report done", "navigate to LoginViewModel", "revoke the token"], _backend.Timeline);
	}

	[Fact]
	public async Task APageRemovedFromTheWindow_WithoutANavigation_IsLeft_Once()
	{
		// any other navigation of the root frame: the page hears only its Unloaded
		var vm = await PagePlaying();

		Assert.True(await vm.OnPageRemoved(navigatedFrom: false, loadedAgain: false));
		Assert.False(await vm.OnPageRemoved(navigatedFrom: false, loadedAgain: false));
		await vm.OnNavigatedFrom();
		await PushState(vm, "idle", null);

		Assert.Equal(["set_fullscreen", "play", "stop"], _backend.Commands);
		_navigation.Verify(x => x.GoBack(), Times.Never);
		Assert.False(vm.IsMpvVideoVisible);
		VerifyNoStopFromFluentFin();
	}

	[Fact]
	public async Task APageNavigatedFrom_IsLeftByItsNavigation_NotByItsRemoval()
	{
		// its frame navigated away from it: the navigation service tells the view model (after the page's Unloaded may be
		// looked at), and that is the one stop
		var vm = await PagePlaying();

		Assert.False(await vm.OnPageRemoved(navigatedFrom: true, loadedAgain: false));
		Assert.Equal(["set_fullscreen", "play"], _backend.Commands);
		await vm.OnNavigatedFrom();

		Assert.Equal(["set_fullscreen", "play", "stop"], _backend.Commands);
	}

	[Fact]
	public async Task APageMovedWithinTheWindow_IsNotLeft()
	{
		// unloaded and loaded again (a move in the visual tree): still the page that plays
		var vm = await PagePlaying();

		Assert.False(await vm.OnPageRemoved(navigatedFrom: false, loadedAgain: true));

		Assert.Equal(["set_fullscreen", "play"], _backend.Commands);
		Assert.True(vm.IsMpvVideoVisible);
		await PushState(vm, "idle", null);
		_navigation.Verify(x => x.GoBack(), Times.Once);
	}

	[Fact]
	public async Task APageRemovedInFullScreen_RestoresTheWindow_AndTellsTheBackend_BeforeTheStop()
	{
		var vm = await PagePlaying();
		var fullScreen = true;
		vm.IsFullScreen = () => fullScreen;
		vm.SetFullScreen = value => fullScreen = value;

		Assert.True(await vm.OnPageRemoved(navigatedFrom: false, loadedAgain: false));

		Assert.False(fullScreen);
		Assert.Equal(["set_fullscreen", "play", "set_fullscreen", "stop"], _backend.Commands);
		Assert.Equal(false, (bool?)_backend.Args("set_fullscreen")["fullscreen"]);
	}

	// signout-ux item 1: the wait is shown, and one sign-out runs at a time

	[Fact]
	public async Task WhileSigningOut_TheTitleBarShowsIt_AndBothCommandsAreDisabled_UntilItIsDone()
	{
		await PagePlaying();
		var report = new TaskCompletionSource();
		_backend.ReportsDone = report.Task;
		_playback.SignOutTimeout = TimeSpan.FromSeconds(30);
		var titleBar = CreateTitleBar();
		var heard = new List<string>();
		titleBar.LogoutCommand.CanExecuteChanged += (_, _) => heard.Add("log out");
		titleBar.SwitchUserCommand.CanExecuteChanged += (_, _) => heard.Add("switch user");
		Assert.True(titleBar.LogoutCommand.CanExecute(null));
		Assert.True(titleBar.SwitchUserCommand.CanExecute(null));
		Assert.False(titleBar.IsSigningOut);

		var signingOut = titleBar.LogoutCommand.ExecuteAsync(null);
		await BridgeClientTests.WaitUntil(() => _backend.Timeline.Contains("wait for the stop report"));

		Assert.True(titleBar.IsSigningOut);
		Assert.Equal("Stopping playback…", titleBar.SignOutStatus);
		Assert.False(titleBar.LogoutCommand.CanExecute(null));
		Assert.False(titleBar.SwitchUserCommand.CanExecute(null));
		// the other button hears it too (the title bar's flyout greys it out)
		Assert.Contains("switch user", heard);

		report.SetResult();
		await signingOut.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.False(titleBar.IsSigningOut);
		Assert.Equal("", titleBar.SignOutStatus);
		Assert.True(titleBar.LogoutCommand.CanExecute(null));
		Assert.True(titleBar.SwitchUserCommand.CanExecute(null));
	}

	[Fact]
	public async Task WhileSigningOut_WithNothingPlaying_TheTitleBarSaysSigningOut()
	{
		var report = new TaskCompletionSource();
		_backend.ReportsDone = report.Task;
		var titleBar = CreateTitleBar();

		var signingOut = titleBar.SwitchUser();
		await BridgeClientTests.WaitUntil(() => _backend.Timeline.Contains("wait for the stop report"));

		Assert.True(titleBar.IsSigningOut);
		Assert.Equal("Signing out…", titleBar.SignOutStatus);
		report.SetResult();
		await signingOut.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.False(titleBar.IsSigningOut);
	}

	[Fact]
	public async Task TheSignOut_LastsUntilTheTokenIsRevoked()
	{
		var titleBar = CreateTitleBar();
		var revoked = new TaskCompletionSource();
		_client.Setup(x => x.Logout()).Callback(() => _backend.Timeline.Enqueue("revoke the token")).Returns(revoked.Task);

		var signingOut = titleBar.Logout();
		await BridgeClientTests.WaitUntil(() => _backend.Timeline.Contains("revoke the token"));

		Assert.True(titleBar.IsSigningOut);
		Assert.Equal("Signing out…", titleBar.SignOutStatus);
		Assert.False(titleBar.SwitchUserCommand.CanExecute(null));
		revoked.SetResult();
		await signingOut.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.False(titleBar.IsSigningOut);
		Assert.True(titleBar.SwitchUserCommand.CanExecute(null));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task LogOutAndSwitchUser_BackToBack_RunOneSignOut(bool switchUserFirst)
	{
		await PagePlaying();
		var report = new TaskCompletionSource();
		_backend.ReportsDone = report.Task;
		_playback.SignOutTimeout = TimeSpan.FromSeconds(30);
		var titleBar = CreateTitleBar();

		var first = switchUserFirst ? titleBar.SwitchUser() : titleBar.Logout();
		await BridgeClientTests.WaitUntil(() => _backend.Timeline.Contains("wait for the stop report"));
		// the flyout opened again and the other one invoked anyway (its button is disabled by then): it does nothing
		var second = switchUserFirst ? titleBar.Logout() : titleBar.SwitchUser();
		await second.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.False(first.IsCompleted);
		report.SetResult();
		await first.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(["send set_fullscreen", "send play", "send stop", "wait for the stop report", "stop report done",
					  switchUserFirst ? "navigate to LoginViewModel" : "navigate to SelectServerViewModel", "revoke the token"], _backend.Timeline);
		_client.Verify(x => x.Logout(), Times.Once);
	}

	// signout-ux item 2: a cast that arrives while signing out plays nothing (it would play past the sign-out, and its
	// Stopped would go out with the revoked token)

	[Fact]
	public async Task ACastThatArrivesWhileSigningOut_PlaysNothing()
	{
		var vm = await PagePlaying();
		var report = new TaskCompletionSource();
		_backend.ReportsDone = report.Task;
		_playback.SignOutTimeout = TimeSpan.FromSeconds(30);
		var titleBar = CreateTitleBar();

		var signingOut = titleBar.Logout();
		await BridgeClientTests.WaitUntil(() => _backend.Timeline.Contains("wait for the stop report"));
		// the websocket handler opens a player page with the controller's queue (WebSocketMessageHandler.HandleRemotePlay)
		var cast = CreateViewModel();
		await cast.OnNavigatedTo(new BridgePlayRequest(["a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1"]));
		report.SetResult();
		await signingOut.WaitAsync(TimeSpan.FromSeconds(5));
		// the root frame's navigation took both pages out of the window
		await cast.OnPageRemoved(navigatedFrom: false, loadedAgain: false);
		await vm.OnPageRemoved(navigatedFrom: false, loadedAgain: false);

		Assert.Single(_backend.Commands, x => x == "play");
		Assert.Single(_backend.Identities);
		Assert.Equal(["send set_fullscreen", "send play", "send stop", "wait for the stop report", "stop report done",
					  "navigate to SelectServerViewModel", "revoke the token",
					  // the cast's page, left: a stop to an idle backend (nothing to report)
					  "send stop"], _backend.Timeline);
		Assert.Contains(_playbackLog.Messages, x => x.Contains("during a sign-out: it plays nothing", StringComparison.Ordinal));
		Assert.Contains(_playbackLog.Messages, x => x.StartsWith("Not starting the mpv backend for generation 3: FluentFin is signing out", StringComparison.Ordinal));
		VerifyNoStopFromFluentFin();
	}

	[Fact]
	public async Task APageOpenedWhileSigningOut_PlaysNothing_EvenWhenItsQueueArrivesAfterTheSignOut()
	{
		// Play pressed on an item page just as the sign-out began: the queue comes from the server after the sign-out is over,
		// before the page is taken out of the window (and the token may be revoked by then)
		var library = new FakeLibrary(_client);
		var movie = library.AddMovie();
		var report = new TaskCompletionSource();
		_backend.ReportsDone = report.Task;
		var titleBar = CreateTitleBar();
		var signingOut = titleBar.Logout();
		await BridgeClientTests.WaitUntil(() => _backend.Timeline.Contains("wait for the stop report"));
		var answer = new TaskCompletionSource();
		library.Gate = answer.Task;
		var page = CreateViewModel();

		var navigating = page.OnNavigatedTo(movie);
		await BridgeClientTests.WaitUntil(() => library.Requests.Length == 1);
		report.SetResult();
		await signingOut.WaitAsync(TimeSpan.FromSeconds(5));
		answer.SetResult();
		await navigating.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Empty(_backend.Commands);
		Assert.Empty(_backend.Identities);
		Assert.Equal(0, _backend.Starts);
	}

	[Fact]
	public async Task APageOpenedAfterTheSignOut_PlaysAsUsual()
	{
		await PagePlaying();
		await CreateTitleBar().Logout().WaitAsync(TimeSpan.FromSeconds(5));

		// the next login's
		await PagePlaying();

		Assert.Equal(["set_fullscreen", "play", "stop", "set_fullscreen", "play"], _backend.Commands);
	}

	[Fact]
	public async Task APageThatOpensWhileTheSignOutWaitsForTheGate_IsTheOneItStops()
	{
		// a remote seek holds the service's gate when the sign-out begins, and a cast opens a page before the gate is free:
		// the sign-out stops whatever is newest then, so the playback the first page started does not go on
		var vm = await PagePlaying();
		var held = new TaskCompletionSource();
		_backend.Hold = command => command == "seek" ? held.Task : null;
		var seeking = _playback.SendIfCurrentAsync(vm.MpvGeneration, "seek", new JsonObject { ["positionTicks"] = 42 });
		await BridgeClientTests.WaitUntil(() => _backend.Commands.Contains("seek"));
		var titleBar = CreateTitleBar();

		var signingOut = titleBar.Logout();
		var cast = CreateViewModel();
		var casting = cast.OnNavigatedTo(new BridgePlayRequest(["a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1"]));
		held.SetResult();
		await seeking.WaitAsync(TimeSpan.FromSeconds(5));
		await casting.WaitAsync(TimeSpan.FromSeconds(5));
		await signingOut.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(["send set_fullscreen", "send play", "send seek", "send stop", "wait for the stop report", "stop report done",
					  "navigate to SelectServerViewModel", "revoke the token"], _backend.Timeline);
	}

	[Fact]
	public async Task APageOfAnotherPlayer_RemovedFromTheWindow_IsLeftAlone()
	{
		_settings.SetupGet(x => x.MediaPlayer).Returns(MediaPlayerType.Vlc);
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModel();
		await vm.OnNavigatedTo(movie);

		Assert.False(await vm.OnPageRemoved(navigatedFrom: false, loadedAgain: false));

		Assert.Empty(_backend.Commands);
		_client.Verify(x => x.Stop(), Times.Never);
	}
}
