using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Contracts.Services
{
	public interface IDeviceProfileFactory
	{
		DeviceProfile GetDeviceProfile();

		/// <summary>
		/// fluentfin-mpv (startup-perf, docs/research/perf-ui.md O13): makes <see cref="GetDeviceProfile"/>'s answer ready for
		/// the selected player: the Windows player's codec probe runs the first time its profile is needed, not at every
		/// launch. Awaited before the profile is read (the capabilities posted at sign-in, PlaybackInfo).
		/// </summary>
		Task PrepareAsync() => Task.CompletedTask;
	}
}
