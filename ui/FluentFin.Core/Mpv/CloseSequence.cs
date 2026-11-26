using FluentFin.Core.Contracts.Services;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.Mpv;

/// <summary>
/// fluentfin-mpv: what closing FluentFin waits for once its window is hidden, each part bounded so that a hung server or
/// backend never keeps a hidden FluentFin alive. Two things run side by side: upstream's players (VLC, Flyleaf, WMP)
/// report their stop, for at most <see cref="UpstreamStopTimeout"/> (the HTTP client alone would wait far longer on a hung
/// server); and the mpv backend shuts down (killed 10 s after <c>shutdown</c>, docs/BRIDGE.md §3), followed by up to
/// <see cref="InterruptedReportTimeout"/> for a Stopped report FluentFin is sending for a backend that died (§6).
/// So closing takes about 13 s at worst (3 s for a backend still starting or connecting, then 10 s for shutdown). Never throws.
/// Nixie 1.8.1 (BH-175-m4): this window's device slot is freed as the close begins, or, while a play's reports are owed, after them
/// (a window starting meanwhile waits for it, a few seconds at most: <see cref="InstallDeviceId.Closing"/>).
/// </summary>
public static class CloseSequence
{
	public static readonly TimeSpan UpstreamStopTimeout = TimeSpan.FromSeconds(5);
	public static readonly TimeSpan InterruptedReportTimeout = TimeSpan.FromSeconds(2);

	public static async Task RunAsync(IJellyfinClient jellyfinClient, MpvPlaybackService mpvPlayback, ILogger logger,
								TimeSpan? upstreamStopTimeout = null, TimeSpan? interruptedReportTimeout = null)
	{
		// 1.7.5: the window is gone for the user: its device slot is free for a window opened meanwhile (Restart to update, a
		// quick start), which is then the same device on the server, not a second window's.
		// 1.8.1 (BH-175-m4): unless a play's reports are still owed (a film playing, or a stop not reported yet): then the slot
		// is freed after them. A window opened meanwhile with a saved password signed in by name as the same device, which
		// ends that device's older sessions: this window's Stopped report was refused, and the place it would have kept (its
		// recovery, RecentPlays, lives in this process) was lost. That window waits for the slot instead (the review: a few
		// seconds at most, InstallDeviceId.ClosingWait), then is the same device; past that, the copy's second for its run.
		// The review: owed also when the page has left the film but the backend hasn't reported its stop yet (Back, then a close
		// at once: the stop is deferred, the backend idle before its `reported`), or FluentFin is sending the Stopped of a
		// backend that died.
		var owed = mpvPlayback.HasPlayback || mpvPlayback.Backend.OwesStopReport || mpvPlayback.ReportsPending;
		logger.LogInformation("[close] this window's device slot ({Slot}): {What} (a play shown {Playback}, a Stopped owed {Owes}, reports on their way {Pending})",
			InstallDeviceId.WindowSlot?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none", owed ? "kept until the play's reports are sent" : "free",
			mpvPlayback.HasPlayback, mpvPlayback.Backend.OwesStopReport, mpvPlayback.ReportsPending);
		if (!owed)
		{
			InstallDeviceId.ReleaseWindow();
		}
		else
		{
			// the review: a window opened meanwhile waits for the slot (bounded), rather than being the copy's second device
			InstallDeviceId.Closing();
		}

		try
		{
			await Task.WhenAll(
				StopUpstreamPlaybackAsync(jellyfinClient, logger, upstreamStopTimeout ?? UpstreamStopTimeout),
				StopMpvAsync(mpvPlayback, logger, interruptedReportTimeout ?? InterruptedReportTimeout));
		}
		finally
		{
			if (owed)
			{
				InstallDeviceId.ReleaseWindow(); // the reports are sent (or their bounds are over): the slot is free now
			}
		}
	}

	private static async Task StopUpstreamPlaybackAsync(IJellyfinClient jellyfinClient, ILogger logger, TimeSpan timeout)
	{
		try
		{
			await jellyfinClient.Stop().WaitAsync(timeout);
		}
		catch (TimeoutException)
		{
			logger.LogWarning("The server did not take the playback stop report within {Seconds} s; closing without it", timeout.TotalSeconds);
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Reporting the playback stop failed");
		}
	}

	private static async Task StopMpvAsync(MpvPlaybackService mpvPlayback, ILogger logger, TimeSpan interruptedReportTimeout)
	{
		try
		{
			// the backend stops playback, reports it and exits (killed if it does not); a backend the watchdog killed just
			// before has had its interruption raised when this returns
			await mpvPlayback.Backend.ShutdownAsync();
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Stopping the mpv backend failed");
		}

		await mpvPlayback.WaitForReportsAsync(interruptedReportTimeout);
	}
}
