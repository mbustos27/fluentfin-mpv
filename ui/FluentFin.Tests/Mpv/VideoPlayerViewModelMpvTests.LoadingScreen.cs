using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// fluentfin-mpv: the player page's loading screen (docs/research/ux-review.md V10): what is starting, each step, and a
/// Cancel that leaves the page the way Back does, so the start goes no further.
/// </summary>
public sealed partial class VideoPlayerViewModelMpvTests
{
	[Fact]
	public async Task TheLoadingScreen_NamesTheEpisode_AndShowsUntilTheBackendTakesThePlay()
	{
		var show = new FakeLibrary(_client).AddSeries(seasons: 1, episodesPerSeason: 3);
		var episode = FakeLibrary.PageCopy(show.Episodes[1]);
		episode.SeriesName = "A show";
		episode.ParentIndexNumber = 1;
		episode.IndexNumber = 2;
		var started = new TaskCompletionSource();
		_backend.StartGate = started.Task;
		var vm = CreateViewModel();

		var navigating = vm.OnNavigatedTo(episode);
		await BridgeClientTests.WaitUntil(() => _backend.Starts == 1);

		Assert.True(vm.IsMpvStarting);
		Assert.Equal("A show", vm.MpvStartingTitle);
		Assert.Equal("S1:E2 - S01E02", vm.MpvStartingSubtitle);
		Assert.Same(episode, vm.MpvStartingItem);
		Assert.Equal("Starting the player…", vm.MpvStatusText);
		Assert.Equal("Cancel", vm.MpvLeaveText);
		Assert.False(vm.IsMpvVideoVisible);

		started.SetResult();
		await navigating;

		Assert.False(vm.IsMpvStarting);
		Assert.True(vm.IsMpvVideoVisible);
	}

	[Fact]
	public async Task TheLoadingScreen_NamesAMovie_TheSelfTest_AndNothingForARemotePlay()
	{
		var movie = Movie();
		movie.ProductionYear = 1999;
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);

		var moviePage = CreateViewModel();
		var gate = new TaskCompletionSource();
		_backend.StartGate = gate.Task;
		var navigating = moviePage.OnNavigatedTo(movie);
		await BridgeClientTests.WaitUntil(() => _backend.Starts == 1);
		Assert.Equal(("A movie", "1999"), (moviePage.MpvStartingTitle, moviePage.MpvStartingSubtitle));
		gate.SetResult();
		await navigating;

		var remote = CreateViewModel();
		await remote.OnNavigatedTo(new BridgePlayRequest(["a1"], 0, 0, null, null, null));
		Assert.Equal(("", ""), (remote.MpvStartingTitle, remote.MpvStartingSubtitle));
		Assert.Null(remote.MpvStartingItem);

		var selfTest = CreateViewModel();
		await selfTest.OnNavigatedTo(MpvSelfTestRequest.Default);
		Assert.Equal(MpvSelfTestRequest.Default.Title, selfTest.MpvStartingTitle);
	}

	[Fact]
	public async Task TheLoadingScreen_NamesASeriesOrSeason_WithoutTheirYears()
	{
		var show = new FakeLibrary(_client).AddSeries(seasons: 2, episodesPerSeason: 2);
		var series = FakeLibrary.PageCopy(show.Series);
		series.ProductionYear = 2005;
		var season = FakeLibrary.PageCopy(show.Seasons[1]);
		season.SeriesName = "A show";

		var seriesPage = CreateViewModel();
		await seriesPage.OnNavigatedTo(series);
		var seasonPage = CreateViewModel();
		await seasonPage.OnNavigatedTo(season);

		Assert.Equal(("A show", ""), (seriesPage.MpvStartingTitle, seriesPage.MpvStartingSubtitle));
		Assert.Equal(("A show", "Season 2"), (seasonPage.MpvStartingTitle, seasonPage.MpvStartingSubtitle));
	}

	[Fact]
	public async Task Cancel_WhileTheQueueLoads_LeavesThePage_AndTheStartGoesNoFurther()
	{
		var library = new FakeLibrary(_client);
		var show = library.AddSeries(seasons: 2, episodesPerSeason: 3);
		var answer = new TaskCompletionSource();
		library.Gate = answer.Task;
		var vm = CreateViewModel();
		// the frame's Back: the page is told it was left
		_navigation.Setup(x => x.GoBack()).Callback(() => vm.OnNavigatedFrom()).Returns(true);

		var navigating = vm.OnNavigatedTo(FakeLibrary.PageCopy(show.Episodes[2]));
		await BridgeClientTests.WaitUntil(() => library.Requests.Length == 1);
		await vm.LeaveMpvCommand.ExecuteAsync(null);
		answer.SetResult();
		await navigating.WaitAsync(TimeSpan.FromSeconds(5));

		_navigation.Verify(x => x.GoBack(), Times.Once);
		Assert.Equal(0, _backend.Starts);
		Assert.Empty(_backend.Commands);
		Assert.False(vm.IsMpvStarting);
		Assert.Contains("Player page (generation 1): Cancel pressed", _pageLog.Messages);
	}

	[Fact]
	public async Task Cancel_WhileConnecting_SendsNoPlay()
	{
		var movie = new FakeLibrary(_client).AddMovie();
		var connect = new TaskCompletionSource();
		_backend.ConnectGate = connect.Task;
		var vm = CreateViewModel();
		_navigation.Setup(x => x.GoBack()).Callback(() => vm.OnNavigatedFrom()).Returns(true);

		var navigating = vm.OnNavigatedTo(movie);
		await BridgeClientTests.WaitUntil(() => _backend.Identities.Count == 1);
		Assert.Equal("Connecting to the server…", vm.MpvStatusText);
		await vm.LeaveMpvCommand.ExecuteAsync(null);
		connect.SetResult();
		await navigating.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.DoesNotContain("play", _backend.Commands);
		Assert.False(vm.IsMpvVideoVisible);
	}

	[Fact]
	public async Task Cancel_WithNothingToGoBackTo_StopsTheStartHere_AndSaysSo()
	{
		var movie = new FakeLibrary(_client).AddMovie();
		var connect = new TaskCompletionSource();
		_backend.ConnectGate = connect.Task;
		var vm = CreateViewModel();
		vm.GoBack = () => false;

		var navigating = vm.OnNavigatedTo(movie);
		await BridgeClientTests.WaitUntil(() => _backend.Identities.Count == 1);
		await vm.LeaveMpvCommand.ExecuteAsync(null);
		connect.SetResult();
		await navigating.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.DoesNotContain("play", _backend.Commands);
		Assert.False(vm.IsMpvStarting);
		// the step the start reached after the cancel does not take the page's words back
		Assert.Equal("Playback cancelled", vm.MpvStatusText);
		Assert.Contains("Player page (generation 1): the start was cancelled with nothing to go back to", _pageLog.Messages);
	}

	[Fact]
	public async Task AStartThatFails_EndsTheLoadingScreen_AndOffersGoBack()
	{
		_backend.StartFailure = new BridgeException("The mpv backend was not found (looked for x)");
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModel();

		await vm.OnNavigatedTo(movie);

		Assert.False(vm.IsMpvStarting);
		Assert.Equal("Go back", vm.MpvLeaveText);
		Assert.Equal("A movie", vm.MpvStartingTitle);

		await vm.LeaveMpvCommand.ExecuteAsync(null);
		_navigation.Verify(x => x.GoBack(), Times.Once);
	}

	[Fact]
	public async Task OnceTheVideoIsShown_TheLoadingScreenAndItsButtonAreGone_UntilItIsHiddenAgain()
	{
		// the reviewer's finding: the button kept the keyboard under mpv's window, so Space or Enter while the item opened
		// left the page and stopped the playback
		var movie = new FakeLibrary(_client).AddMovie();
		var vm = CreateViewModel();

		await vm.OnNavigatedTo(movie);

		Assert.True(vm.IsMpvVideoVisible);
		Assert.False(vm.IsMpvStartScreenVisible);
		Assert.False(vm.LeaveMpvCommand.CanExecute(null));

		// a crash hides the video: the screen and its Go back are back
		vm.OnMpvStatus(vm.MpvGeneration, new MpvBackendStatus(MpvBackendState.Crashed, LastError: "The mpv backend stopped unexpectedly (exit code 3)"));
		Assert.True(vm.IsMpvStartScreenVisible);
		Assert.True(vm.LeaveMpvCommand.CanExecute(null));
		Assert.Equal("Go back", vm.MpvLeaveText);
	}

	[Fact]
	public async Task OtherPlayers_ShowNoLoadingScreen()
	{
		_settings.SetupGet(x => x.MediaPlayer).Returns(MediaPlayerType.Vlc);
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModel();

		await vm.OnNavigatedTo(movie);

		Assert.False(vm.IsMpvStarting);
		Assert.Equal("", vm.MpvStartingTitle);
	}
}
