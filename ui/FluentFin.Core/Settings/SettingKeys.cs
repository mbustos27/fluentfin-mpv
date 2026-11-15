using System.Collections.ObjectModel;
using FluentFin.Core.Contracts.Services;

namespace FluentFin.Core.Settings;

public static class SettingKeys
{
	public static Key<ObservableCollection<SavedServer>> Servers { get; } = new Key<ObservableCollection<SavedServer>>("Servers", []);
	// the default applies to new installs only: ReadSetting stores the value it returns, so an install that has a stored choice keeps it
	public static Key<MediaPlayerType> MediaPlayerType { get; } = new Key<MediaPlayerType>("MediaPlayerType", Contracts.Services.MediaPlayerType.Mpv);

	/// <summary>
	/// Optional override of the command that starts the mpv backend, everything before <c>--config-dir</c>
	/// (for example <c>C:\shim\.venv\Scripts\python.exe -I -m jellyfin_mpv_shim.bridge</c>). Empty = find it automatically.
	/// </summary>
	public static Key<string> MpvBackendCommand { get; } = new Key<string>("MpvBackendCommand", "");
}

