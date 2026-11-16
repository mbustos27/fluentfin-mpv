namespace FluentFin.Mpv;

/// <summary>
/// fluentfin-mpv: the player page's rule for its <c>Unloaded</c> (<see cref="Views.VideoPlayerPage"/>), kept apart from the
/// page so it can be tested without WinUI. Only the page's own frame tells it it was left (its OnNavigatedFrom); a
/// navigation of the window's root frame (Log out, Switch user) takes the shell away with every page on it and tells only
/// the shell. So an <c>Unloaded</c> of a page of the mpv player that its frame did not navigate from is looked at again
/// once layout has settled: queued at low priority, then <see cref="SettleDelay"/>, then judged by what is true at that
/// point, not at the <c>Unloaded</c>: navigated from by then, or back in the window (a move within the window unloads and
/// loads again), it was not left; otherwise <c>leave</c> leaves it (the view model's OnPageRemoved: its playback
/// stops, once, docs/BRIDGE.md §6).
/// </summary>
/// <param name="navigatedFrom">The page's frame has navigated away from it.</param>
/// <param name="isLoaded">The page is in the window (FrameworkElement.IsLoaded).</param>
/// <param name="isMpv">The page plays with the mpv player.</param>
/// <param name="leave">The view model's OnPageRemoved(navigatedFrom, loadedAgain); true when it took it as a leave.</param>
/// <param name="enqueueLowPriority">Queues work on the page's thread at low priority; false when it could not.</param>
/// <param name="onError">A failure of the queued work (it runs from the dispatcher queue: nothing may escape it).</param>
/// <param name="delay">Waits (tests); <see cref="Task.Delay(TimeSpan)"/> by default.</param>
public sealed class PlayerPageRemoval(
	Func<bool> navigatedFrom,
	Func<bool> isLoaded,
	Func<bool> isMpv,
	Func<bool, bool, Task<bool>> leave,
	Func<Func<Task>, bool> enqueueLowPriority,
	Action<Exception> onError,
	Func<TimeSpan, Task>? delay = null)
{
	/// <summary>How long after its <c>Unloaded</c> (and the queue) the page is looked at again.</summary>
	public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(250);

	private readonly Func<TimeSpan, Task> _delay = delay ?? (wait => Task.Delay(wait));

	/// <summary>The page's <c>Unloaded</c>. True when the page is to be looked at again (and that was queued).</summary>
	public bool OnUnloaded()
	{
		if (navigatedFrom() || !isMpv())
		{
			return false;
		}

		return enqueueLowPriority(LookAgainAsync);
	}

	/// <summary>Never throws.</summary>
	private async Task LookAgainAsync()
	{
		try
		{
			await _delay(SettleDelay);
			await leave(navigatedFrom(), isLoaded());
		}
		catch (Exception ex)
		{
			onError(ex);
		}
	}
}
