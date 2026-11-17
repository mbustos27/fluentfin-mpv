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
	// the MPV player does not yet: the bridge has no field for them (docs/BRIDGE.md v1). Except with FluentFin's player
	// controls over it (MpvPlayerControls below): those take the wheel and the volume step.

	/// <summary>Show the Skip intro / Skip credits button during those segments.</summary>
	public static Key<bool> ShowSkipPrompt { get; } = new Key<bool>("ShowSkipPrompt", true);

	/// <summary>The mouse wheel over the video changes the volume.</summary>
	public static Key<bool> MouseWheelVolume { get; } = new Key<bool>("MouseWheelVolume", true);

	/// <summary>How much one wheel notch or arrow key changes the volume, in percent.</summary>
	public static Key<int> VolumeStep { get; } = new Key<int>("VolumeStep", 5);

	/// <summary>fluentfin-mpv: the subtitle text size in the mpv player's controls, in percent (50 to 200), kept on this PC.</summary>
	public static Key<int> SubtitleSize { get; } = new Key<int>("SubtitleSize", 100);

	/// <summary>Seaglass: how much glass the app shows (Settings > Appearance), kept on this PC. Automatic by default.</summary>
	public static Key<GlassChoice> Glass { get; } = new Key<GlassChoice>("Glass", GlassChoice.Automatic);

	/// <summary>
	/// fluentfin-mpv: who draws the player controls with the mpv player (docs/research/player-controls.md). FluentFin's own
	/// by default (the user's choice, 2026-11-15); without a libmpv that does composition output next to FluentFin, the
	/// player falls back to mpv's controls and says so (MpvVideoSurfaceChoice).
	/// </summary>
	public static Key<MpvPlayerControls> MpvPlayerControls { get; } = new Key<MpvPlayerControls>("MpvPlayerControls", global::FluentFin.Core.Settings.MpvPlayerControls.FluentFin);
}

/// <summary>fluentfin-mpv: who draws the controls over the mpv player's video.</summary>
public enum MpvPlayerControls
{
	/// <summary>mpv's own on-screen controller (the shim's OSC with FluentFin's look), mpv in a window of the backend's.</summary>
	Mpv,

	/// <summary>FluentFin's controls over an mpv inside FluentFin (libmpv, composition mode); the backend attaches to it.</summary>
	FluentFin,
}


/// <summary>Seaglass: Settings > Appearance > Glass. Automatic follows Windows (transparency, animation, high contrast).</summary>
public enum GlassChoice
{
	Automatic,
	Full,
	Reduced,
	Solid,
}
