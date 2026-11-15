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

	// fluentfin-mpv: Settings > Playback, kept on this PC (docs/research/ux-review.md T1). FluentFin's own players use them;
	// the MPV player does not yet: the bridge has no field for them (docs/BRIDGE.md v1).

	/// <summary>Show the Skip intro / Skip credits button during those segments.</summary>
	public static Key<bool> ShowSkipPrompt { get; } = new Key<bool>("ShowSkipPrompt", true);

	/// <summary>The mouse wheel over the video changes the volume.</summary>
	public static Key<bool> MouseWheelVolume { get; } = new Key<bool>("MouseWheelVolume", true);

	/// <summary>How much one wheel notch or arrow key changes the volume, in percent.</summary>
	public static Key<int> VolumeStep { get; } = new Key<int>("VolumeStep", 5);
}

