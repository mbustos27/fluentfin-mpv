using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;
using FluentFin.ViewModels;
using Moq;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// signout-ux item 3: a non-fatal <c>error</c> once the item plays (a track or quality change the backend could not apply:
/// docs/BRIDGE.md §5, the item plays on) is shown as a brief notice that hides itself or can be dismissed; playback is not
/// touched. Before playback, or fatal, it stays the page's message as before.
/// </summary>
public sealed partial class VideoPlayerViewModelMpvTests
{
	private const string NotApplied = "A movie: the track or quality change could not be applied (the server offered no stream this player can play); it plays on as before";

	/// <summary>The notice's timers, in the order they were started; a test ends each when it wants.</summary>
	private readonly List<TaskCompletionSource> _noticeTimers = [];

	private async Task<VideoPlayerViewModel> PagePlayingWithNoticeTimers()
	{
		var vm = await PagePlaying();
		vm.MpvNoticeDelay = wait =>
		{
			Assert.Equal(vm.MpvNoticeDuration, wait);
			var timer = new TaskCompletionSource();
			_noticeTimers.Add(timer);
			return timer.Task;
		};
		return vm;
	}

	private static void Error(VideoPlayerViewModel vm, string message, bool fatal = false, string code = "mpv_failed") =>
		vm.OnMpvEvent(vm.MpvGeneration, new BridgeEvent("error", new JsonObject { ["code"] = code, ["message"] = message, ["fatal"] = fatal }));

	[Theory]
	[InlineData("mpv_failed")]
	[InlineData("server_unreachable")]
	public async Task AChangeThatCouldNotBeApplied_IsShownBriefly_AndPlaybackGoesOn(string code)
	{
		var vm = await PagePlayingWithNoticeTimers();
		vm.MpvNoticeDuration = TimeSpan.FromSeconds(6);

		Error(vm, NotApplied, code: code);

		Assert.Equal($"MPV: {NotApplied}", vm.MpvNotice);
		// not the page's message (that one is for what ended or never started), and nothing stops
		Assert.Null(vm.MpvMessage);
		Assert.True(vm.IsMpvVideoVisible);
		Assert.Equal(["set_fullscreen", "play"], _backend.Commands);
		_navigation.Verify(x => x.GoBack(), Times.Never);

		var timer = Assert.Single(_noticeTimers);
		timer.SetResult();

		Assert.Null(vm.MpvNotice);
		Assert.True(vm.IsMpvVideoVisible);
		Assert.Equal(["set_fullscreen", "play"], _backend.Commands);
	}

	[Fact]
	public async Task TheNotice_IsRedacted()
	{
		var vm = await PagePlayingWithNoticeTimers();

		Error(vm, "A movie: the track or quality change could not be applied (GET http://jf:8096/Items/x/PlaybackInfo?api_key=secret-1 failed); it plays on as before", code: "server_unreachable");

		Assert.DoesNotContain("secret-1", vm.MpvNotice);
		Assert.Contains("api_key=***", vm.MpvNotice);
	}

	[Fact]
	public async Task ANewerNotice_TakesThePlaceOfTheOlder_AndStaysItsOwnTime()
	{
		var vm = await PagePlayingWithNoticeTimers();

		Error(vm, "first");
		Error(vm, "second");
		_noticeTimers[0].SetResult();

		Assert.Equal("MPV: second", vm.MpvNotice);
		_noticeTimers[1].SetResult();
		Assert.Null(vm.MpvNotice);
	}

	[Fact]
	public async Task ADismissedNotice_IsGone_AndTheSameOneShowsAgainNextTime()
	{
		var vm = await PagePlayingWithNoticeTimers();
		var shown = 0;
		vm.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(VideoPlayerViewModel.MpvNotice) && vm.MpvNotice is not null)
			{
				shown++;
			}
		};

		Error(vm, NotApplied);
		vm.DismissMpvNotice();
		Assert.Null(vm.MpvNotice);
		Error(vm, NotApplied);

		// the bar hears it again (it was closed by its button: the text alone would not reopen it)
		Assert.Equal(2, shown);
		Assert.Equal($"MPV: {NotApplied}", vm.MpvNotice);
		// the first one's time running out does not take the second away
		_noticeTimers[0].SetResult();
		Assert.Equal($"MPV: {NotApplied}", vm.MpvNotice);
	}

	[Fact]
	public async Task AMessageAboutTheEnd_ReplacesTheNotice()
	{
		var vm = await PagePlayingWithNoticeTimers();
		Error(vm, NotApplied);

		vm.OnMpvStatus(vm.MpvGeneration, new MpvBackendStatus(MpvBackendState.Crashed, LastError: "The mpv backend stopped unexpectedly (exit code 3)"));

		Assert.Null(vm.MpvNotice);
		Assert.Contains("exit code 3", vm.MpvMessage);
	}

	[Fact]
	public async Task AFatalError_StaysThePagesMessage()
	{
		var vm = await PagePlayingWithNoticeTimers();

		Error(vm, "mpv is gone", fatal: true);

		Assert.Equal("MPV: mpv is gone", vm.MpvMessage);
		Assert.True(vm.MpvMessageIsError);
		Assert.Null(vm.MpvNotice);
		Assert.Empty(_noticeTimers);
	}

	[Fact]
	public async Task AnErrorBeforePlayback_StaysThePagesMessage()
	{
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModel();
		await vm.OnNavigatedTo(movie);

		Error(vm, "A movie could not be opened");

		Assert.Equal("MPV: A movie could not be opened", vm.MpvMessage);
		Assert.Null(vm.MpvNotice);
	}
}
