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

	/// <summary>
	/// fluentfin-mpv: who draws the player controls with the mpv player (docs/research/player-controls.md). mpv by default:
	/// FluentFin's own controls are the default only once every row of the phase 2a go/no-go table passed on this PC.
	/// </summary>
	public static Key<MpvPlayerControls> MpvPlayerControls { get; } = new Key<MpvPlayerControls>("MpvPlayerControls", global::FluentFin.Core.Settings.MpvPlayerControls.Mpv);
}

/// <summary>fluentfin-mpv: who draws the controls over the mpv player's video.</summary>
public enum MpvPlayerControls
{
	/// <summary>mpv's own on-screen controller (the shim's OSC with FluentFin's look), mpv in a window of the backend's.</summary>
	Mpv,

	/// <summary>FluentFin's controls over an mpv inside FluentFin (libmpv, composition mode); the backend attaches to it.</summary>
	FluentFin,
}

