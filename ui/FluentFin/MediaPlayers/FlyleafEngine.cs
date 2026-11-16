using System.Reflection;

namespace FluentFin.MediaPlayers;

/// <summary>
/// fluentfin-mpv (startup-perf, docs/research/perf-ui.md O5): Flyleaf's engine (FFmpeg's DLLs, its threads, a 250 ms UI
/// refresh) starts the first time a Flyleaf player is created, not at every launch whatever player is selected: measured
/// ~128 ms of the UI thread before the window (941 ms the first time after an install), +18.5 MB private, +2 threads,
/// +69 modules. <see cref="EnsureStarted"/> must come before the first <c>new FlyleafHost()</c>
/// (<c>MediaPlayerHost.CreateFlyleaf</c>, the only place a Flyleaf player is made).
/// </summary>
public static class FlyleafEngine
{
	private static readonly Lock s_lock = new();

	/// <summary>Starts the engine; replaced by tests.</summary>
	internal static Action Start { get; set; } = StartEngine;

	public static bool IsStarted { get; private set; }

	/// <summary>Starts the engine once per process (UI thread). A start that throws is tried again next time.</summary>
	public static void EnsureStarted()
	{
		lock (s_lock)
		{
			if (IsStarted)
			{
				return;
			}

			Start();
			IsStarted = true;
		}
	}

	/// <summary>For tests: as if the process had just started.</summary>
	internal static void Reset()
	{
		lock (s_lock)
		{
			IsStarted = false;
		}
	}

	private static void StartEngine()
	{
		var location = Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly()!.Location)!, "FFMpeg");
		FlyleafLib.Engine.Start(new FlyleafLib.EngineConfig()
		{
			FFmpegLoadProfile = Flyleaf.FFmpeg.LoadProfile.Main,    // Prevents loading avdevice/avfilter dll files
			FFmpegPath = location ?? "FFmpeg",
			FFmpegLogLevel = Flyleaf.FFmpeg.LogLevel.Quiet,
			LogLevel = FlyleafLib.LogLevel.Quiet,
			UIRefresh = true,    // Required for Activity, BufferedDuration, Stats in combination with Config.Player.Stats = true
			UIRefreshInterval = 250,      // How often (in ms) to notify the UI
		});
	}
}
