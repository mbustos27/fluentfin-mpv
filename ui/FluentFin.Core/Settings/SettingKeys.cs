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

	/// <summary>
	/// Look intros up online when neither the server nor the file's chapters have them (TheIntroDB, and AniSkip for anime, with arm.haglund.dev mapping the show's ID to MyAnimeList). Off
	/// until the user turns it on: the show's public id, season, episode and length are sent, nothing else.
	/// </summary>
	public static Key<bool> LookUpIntrosOnline { get; } = new Key<bool>("LookUpIntrosOnline", false);

	/// <summary>Remember where the user skips the start of an episode and offer it on the season's later episodes.</summary>
	public static Key<bool> LearnIntros { get; } = new Key<bool>("LearnIntros", true);

	// 1.6 skip: Settings > Playback's skip rules (docs/BRIDGE.md §4 set_skip_options). "Learn skips from my seeks" is
	// LearnIntros above, renamed on screen only, so a 1.5 choice carries over.

	/// <summary>1.6 skip: the server's ad and preview segments: "off", "ask" (a Skip button, the default) or "always".</summary>
	public static Key<string> AdsAndPreviewsAction { get; } = new Key<string>("AdsAndPreviewsAction", "ask");

	/// <summary>1.6 skip: "When a show has a skip rule": "ask" (a Skip button, the default) or "always" (skip it by itself).</summary>
	public static Key<string> SkipRuleAction { get; } = new Key<string>("SkipRuleAction", "ask");
	// end 1.6 skip

	/// <summary>The mouse wheel over the video changes the volume.</summary>
	public static Key<bool> MouseWheelVolume { get; } = new Key<bool>("MouseWheelVolume", true);

	/// <summary>How much one wheel notch or arrow key changes the volume, in percent.</summary>
	public static Key<int> VolumeStep { get; } = new Key<int>("VolumeStep", 5);

	/// <summary>fluentfin-mpv: the subtitle text size in the mpv player's controls, in percent (50 to 200), kept on this PC.</summary>
	public static Key<int> SubtitleSize { get; } = new Key<int>("SubtitleSize", 100);

	/// <summary>
	/// Seaglass: Settings > Check for updates. Nixie 1.3.1: on by default (the user confirmed): Nixie asks GitHub for its latest
	/// release when it opens and once a day; nothing else is sent. A saved off from an older version is the old default unless
	/// the check ever ran (<see cref="Updates.UpdateDefaults"/>).
	/// </summary>
	public static Key<bool> CheckForUpdates { get; } = new Key<bool>("CheckForUpdates", true);

	/// <summary>Seaglass: what the update check remembers (when it last asked, the release it found, the one dismissed), as JSON: <see cref="Updates.UpdateState"/>.</summary>
	public static Key<string> UpdateState { get; } = new Key<string>("UpdateState", "");

	// 1.5 updater
	/// <summary>
	/// Nixie 1.5: Settings > Install when I close Nixie. Off by default (the owner's 1.5 choice): an update that is downloaded and
	/// checked installs only on "Restart to update"; with this on, also quietly as Nixie closes. Kept on this PC.
	/// </summary>
	public static Key<bool> InstallUpdatesOnClose { get; } = new Key<bool>("InstallUpdatesOnClose", false);
	// end 1.5 updater

	/// <summary>Seaglass: how much glass the app shows (Settings > Appearance), kept on this PC. Automatic by default.</summary>
	public static Key<GlassChoice> Glass { get; } = new Key<GlassChoice>("Glass", GlassChoice.Automatic);

	/// <summary>
	/// fluentfin-mpv: who draws the player controls with the mpv player (docs/research/player-controls.md). FluentFin's own
	/// by default (the user's choice, 2026-11-15); without a libmpv that does composition output next to FluentFin, the
	/// player falls back to mpv's controls and says so (MpvVideoSurfaceChoice).
	/// </summary>
	public static Key<MpvPlayerControls> MpvPlayerControls { get; } = new Key<MpvPlayerControls>("MpvPlayerControls", global::FluentFin.Core.Settings.MpvPlayerControls.FluentFin);

	// 1.5 fixes
	/// <summary>
	/// 1.5 (the owner's choice): Settings' "Include shows I'm rewatching" turned off. Up next and Continue Watching include
	/// shows being watched again (on since 1.4.1, sg-cw) unless this is true: then Next Up is asked without rewatching, and
	/// Continue Watching leaves out an episode that is watched and being watched again (Jellyfin 12 keeps it marked watched
	/// meanwhile). Kept on this PC: Jellyfin web keeps its own in the browser. Stored the other way round from the switch, so
	/// that a settings store that says nothing (a new install, a test's) keeps rewatching in.
	/// </summary>
	public static Key<bool> LeaveOutRewatching { get; } = new Key<bool>("UpNextLeaveOutRewatching", false);

	/// <summary>
	/// 1.5 (the button review's L1, L2; WCAG 2.1.4): Settings' "Letter keys on Home" turned off: N and C no longer take the
	/// keyboard to Up next and Continue watching. Stored the other way round from the switch (on by default).
	/// </summary>
	public static Key<bool> HomeLetterKeysOff { get; } = new Key<bool>("HomeLetterKeysOff", false);
	// END 1.5 fixes

	// 1.6 player
	/// <summary>
	/// 1.6: Settings &gt; Playback, the default quality when the server is at home (1.5's local-or-remote rule, the backend's):
	/// <see cref="Mpv.QualityChoice.ToSetting"/>'s "auto", "original" or a cap in bits per second. Auto by default, and Nixie
	/// never moves it by itself. Sent before every play (<c>set_quality_defaults</c>, docs/BRIDGE.md §4). Kept on this PC.
	/// </summary>
	public static Key<string> QualityAtHome { get; } = new Key<string>("QualityAtHome", "auto");

	/// <summary>1.6: the default quality when the server is away (remote), as <see cref="QualityAtHome"/>.</summary>
	public static Key<string> QualityAway { get; } = new Key<string>("QualityAway", "auto");

	/// <summary>1.6: Settings &gt; Playback, "Show the Playback stats button" in the player. Off by default; the I key works either way.</summary>
	public static Key<bool> ShowPlaybackStatsButton { get; } = new Key<bool>("ShowPlaybackStatsButton", false);
	// end 1.6 player
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
