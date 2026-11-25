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
/// </summary>
public static class CloseSequence
{
	public static readonly TimeSpan UpstreamStopTimeout = TimeSpan.FromSeconds(5);
	public static readonly TimeSpan InterruptedReportTimeout = TimeSpan.FromSeconds(2);

	public static Task RunAsync(IJellyfinClient jellyfinClient, MpvPlaybackService mpvPlayback, ILogger logger,
								TimeSpan? upstreamStopTimeout = null, TimeSpan? interruptedReportTimeout = null)
	{
		// 1.7.5: the window is gone for the user: its device slot is free for a window opened meanwhile (Restart to update, a
		// quick start), which is then the same device on the server, not a second window's
		InstallDeviceId.ReleaseWindow();
		return Task.WhenAll(
			StopUpstreamPlaybackAsync(jellyfinClient, logger, upstreamStopTimeout ?? UpstreamStopTimeout),
			StopMpvAsync(mpvPlayback, logger, interruptedReportTimeout ?? InterruptedReportTimeout));
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
