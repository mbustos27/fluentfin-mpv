using System.Diagnostics;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Settings;
using FluentFin.Mpv;
using FluentFin.ViewModels;
using Moq;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// signout-ux item 4: the player page's <c>Unloaded</c> rule (<see cref="PlayerPageRemoval"/>, what VideoPlayerPage runs),
/// driven with the real view model: an mpv page taken out of the window without its frame navigating from it is left once
/// layout has settled (queued at low priority, then 250 ms), judged by what is true then.
/// </summary>
public sealed partial class VideoPlayerViewModelMpvTests
{
	/// <summary>What the page gives the rule: its state, its dispatcher queue and its clock, all in the test's hands.</summary>
	private sealed class PageStandIn
	{
		public bool NavigatedFrom { get; set; }
		public bool Loaded { get; set; }
		public bool QueueWorks { get; set; } = true;
		public List<Func<Task>> Queued { get; } = [];
		public List<TimeSpan> Waits { get; } = [];
		public TaskCompletionSource Settled { get; } = new();
		public List<Exception> Errors { get; } = [];
	}

	private static PlayerPageRemoval Removal(VideoPlayerViewModel vm, PageStandIn page, Func<bool, bool, Task<bool>>? leave = null, bool realClock = false) => new(
		() => page.NavigatedFrom,
		() => page.Loaded,
		() => vm.IsMpv,
		leave ?? vm.OnPageRemoved,
		work =>
		{
			if (page.QueueWorks)
			{
				page.Queued.Add(work);
			}
			return page.QueueWorks;
		},
		page.Errors.Add,
		realClock ? null : (Func<TimeSpan, Task>)(wait =>
		{
			page.Waits.Add(wait);
			return page.Settled.Task;
		}));

	[Fact]
	public async Task Unloaded_WithoutANavigation_IsLeft_OnceLayoutHasSettled()
	{
		var vm = await PagePlaying();
		var page = new PageStandIn();

		Assert.True(Removal(vm, page).OnUnloaded());

		// queued, not run: the Unloaded itself stops nothing
		var work = Assert.Single(page.Queued);
		Assert.Equal(["set_fullscreen", "play"], _backend.Commands);
		var looking = work();
		Assert.Equal([PlayerPageRemoval.SettleDelay], page.Waits);
		Assert.Equal(TimeSpan.FromMilliseconds(250), PlayerPageRemoval.SettleDelay);
		Assert.False(looking.IsCompleted);
		Assert.Equal(["set_fullscreen", "play"], _backend.Commands);
		page.Settled.SetResult();
		await looking.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(["set_fullscreen", "play", "stop"], _backend.Commands);
		Assert.False(vm.IsMpvVideoVisible);
		Assert.Empty(page.Errors);
		VerifyNoStopFromFluentFin();
	}

	[Fact]
	public async Task Unloaded_ThenLoadedAgainBeforeItSettles_IsNotALeave()
	{
		// a move within the window: unloaded, then loaded again
		var vm = await PagePlaying();
		var page = new PageStandIn();
		Removal(vm, page).OnUnloaded();
		var looking = page.Queued[0]();

		page.Loaded = true;
		page.Settled.SetResult();
		await looking.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(["set_fullscreen", "play"], _backend.Commands);
		Assert.True(vm.IsMpvVideoVisible);
	}

	[Fact]
	public async Task Unloaded_ThenNavigatedFromBeforeItSettles_IsLeftByTheNavigation_Once()
	{
		// the frame unloads the page before it tells it it was left
		var vm = await PagePlaying();
		var page = new PageStandIn();
		Removal(vm, page).OnUnloaded();
		var looking = page.Queued[0]();

		page.NavigatedFrom = true;
		await vm.OnNavigatedFrom();
		page.Settled.SetResult();
		await looking.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(["set_fullscreen", "play", "stop"], _backend.Commands);
	}

	[Fact]
	public async Task Unloaded_AfterItsNavigation_QueuesNothing()
	{
		var vm = await PagePlaying();
		var page = new PageStandIn { NavigatedFrom = true };

		Assert.False(Removal(vm, page).OnUnloaded());

		Assert.Empty(page.Queued);
	}

	[Fact]
	public async Task Unloaded_OnAPageOfAnotherPlayer_QueuesNothing()
	{
		_settings.SetupGet(x => x.MediaPlayer).Returns(MediaPlayerType.Vlc);
		var movie = Movie();
		_client.Setup(x => x.GetItem(movie.Id!.Value)).ReturnsAsync(movie);
		var vm = CreateViewModel();
		await vm.OnNavigatedTo(movie);
		var page = new PageStandIn();

		Assert.False(Removal(vm, page).OnUnloaded());

		Assert.Empty(page.Queued);
		Assert.Empty(_backend.Commands);
	}

	[Fact]
	public async Task Unloaded_WhenTheQueueIsGone_SaysSo()
	{
		var vm = await PagePlaying();

		Assert.False(Removal(vm, new PageStandIn { QueueWorks = false }).OnUnloaded());
	}

	[Fact]
	public async Task AFailedLeave_IsReported_AndDoesNotEscapeTheQueue()
	{
		var vm = await PagePlaying();
		var page = new PageStandIn();
		var failure = new InvalidOperationException("the leave failed");
		Removal(vm, page, leave: (_, _) => Task.FromException<bool>(failure)).OnUnloaded();
		page.Settled.SetResult();

		var error = await Record.ExceptionAsync(() => page.Queued[0]());

		Assert.Null(error);
		Assert.Same(failure, Assert.Single(page.Errors));
	}

	[Fact]
	public async Task WithTheRealClock_NothingIsLeftBeforeAQuarterSecond()
	{
		var vm = await PagePlaying();
		var page = new PageStandIn();
		Removal(vm, page, realClock: true).OnUnloaded();

		var watch = Stopwatch.StartNew();
		var looking = page.Queued[0]();
		await BridgeClientTests.WaitUntil(() => _backend.Commands.Contains("stop"));
		await looking.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(200), $"left after {watch.ElapsedMilliseconds} ms");
		Assert.Equal(["set_fullscreen", "play", "stop"], _backend.Commands);
	}
}
