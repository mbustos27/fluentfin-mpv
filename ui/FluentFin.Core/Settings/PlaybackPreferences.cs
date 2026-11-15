namespace FluentFin.Core.Settings;

/// <summary>
/// fluentfin-mpv: Settings > Playback as FluentFin's own players apply it (docs/research/ux-review.md T1). The MPV player
/// does not use these yet: the bridge carries no player settings (docs/BRIDGE.md v1).
/// </summary>
public static class PlaybackPreferences
{
	/// <summary>The volume after one wheel notch or arrow key (<paramref name="direction"/>: its sign), 0 to 100.</summary>
	public static double StepVolume(double volume, double direction, int step) =>
		direction == 0 ? volume : Math.Clamp(volume + Math.Sign(direction) * Math.Clamp(step, 1, 20), 0, 100);

	/// <summary>
	/// Whether the next item starts by itself at the end of one: the server's per-user "auto-play next episode", which
	/// jellyfin-web honours too. Unknown (no session, an old server) = yes, as before.
	/// </summary>
	public static bool PlaysNextItem() => SessionInfo.CurrentUser?.Configuration?.EnableNextEpisodeAutoPlay != false;
}
