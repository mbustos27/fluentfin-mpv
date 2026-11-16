using FluentFin.Core;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Settings;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Services
{
	public class DeviceProfileFactory(ISettings settings) : IDeviceProfileFactory
	{
		private static readonly Lock s_lock = new();
		private static Task? s_windowsProbe;

		/// <summary>The Windows player's codec probe (7 codec queries and an audio graph); replaced by tests.</summary>
		internal static Func<Task> WindowsProbe { get; set; } = WmpDeviceProfileManager.InitializeAsync;

		/// <summary>
		/// fluentfin-mpv (startup-perf, docs/research/perf-ui.md O13): the probe runs once, the first time the Windows player's
		/// profile is needed, not after every launch whatever player is selected; one that failed runs again next time.
		/// </summary>
		public Task PrepareAsync() => settings.MediaPlayer == MediaPlayerType.WindowsMediaPlayer ? ProbeWindowsPlayerAsync() : Task.CompletedTask;

		private static Task ProbeWindowsPlayerAsync()
		{
			lock (s_lock)
			{
				if (s_windowsProbe is { IsFaulted: false, IsCanceled: false } probe)
				{
					return probe;
				}

				return s_windowsProbe = WindowsProbe();
			}
		}

		/// <summary>For tests: as if the process had just started.</summary>
		internal static void Reset()
		{
			lock (s_lock)
			{
				s_windowsProbe = null;
			}
		}

		public DeviceProfile GetDeviceProfile() => GetDeviceProfile(settings.MediaPlayer);

		private static DeviceProfile GetDeviceProfile(MediaPlayerType type)
		{
			return type switch
			{
				MediaPlayerType.Flyleaf => DeviceProfiles.Flyleaf,
				MediaPlayerType.WindowsMediaPlayer => WmpDeviceProfileManager.Profile,
				_ => DeviceProfiles.Flyleaf,
			};
		}
	}
}
