using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core;

/// <summary>
/// fluentfin-mpv (startup-perf): timestamps of the start-up and of a play in the log, as lines that begin with
/// <c>[perf]</c>, so time-to-window and time-to-video can be read from any log (<c>grep "\[perf\]" log*.txt</c>).
/// Start-up: milliseconds since the process started (as Windows recorded it): window shown, shell ready, home requested.
/// Play: milliseconds since Play was pressed (<see cref="PlayPressed"/>), up to the first <c>playing</c> state.
/// </summary>
public static class PerfLog
{
	private static readonly Lazy<DateTime?> s_processStart = new(() =>
	{
		try
		{
			using var process = Process.GetCurrentProcess();
			return process.StartTime.ToUniversalTime();
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
		{
			return null;
		}
	});

	private static long s_playPressed;

	/// <summary>Milliseconds since the process started; null when Windows would not say.</summary>
	public static long? SinceProcessStartMs() =>
		s_processStart.Value is { } start ? (long)(DateTime.UtcNow - start).TotalMilliseconds : null;

	/// <summary>One start-up step: <c>[perf] startup: {step} at {ms} ms after the process started</c>.</summary>
	public static void Startup(ILogger logger, string step)
	{
		try
		{
			logger.LogInformation("[perf] startup: {Step} at {Ms} ms after the process started", step, SinceProcessStartMs()?.ToString() ?? "?");
		}
		catch (Exception)
		{
			// a timestamp never breaks the start-up
		}
	}

	/// <summary>Play was pressed (an item page's button, a card's play): the play timeline counts from here.</summary>
	public static void PlayPressed() => Interlocked.Exchange(ref s_playPressed, Stopwatch.GetTimestamp());

	/// <summary>
	/// The moment Play was pressed, if it was within <paramref name="window"/> of now (a player page opened by a remote
	/// play or a back navigation has none); then forgotten, so one press times one play.
	/// </summary>
	public static long? TakePlayPressed(TimeSpan window)
	{
		var pressed = Interlocked.Exchange(ref s_playPressed, 0);
		return pressed != 0 && Stopwatch.GetElapsedTime(pressed) <= window ? pressed : null;
	}
}
