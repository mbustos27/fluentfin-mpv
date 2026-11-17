namespace FluentFin.Core.Mpv;

/// <summary>
/// fluentfin-mpv (release 1.0): what the player page says when the backend's <c>connect</c> answers <c>mpv_not_found</c>
/// (docs/BRIDGE.md section 2): mpv's controls are chosen, or Seaglass fell back to them, and there is no mpv.exe to run.
/// A plain sentence with what to do instead of the backend's error. Seaglass 1.1.4: it says Seaglass, and when Seaglass's
/// own controls were chosen but cannot be had on this PC (the fallback) it no longer says to switch to them.
/// </summary>
public static class MpvNotFoundMessage
{
	public const string Code = "mpv_not_found";

	public const string Text =
		"mpv isn't installed on this PC, and mpv's player controls need it. Switch to Seaglass's controls in " +
		"Settings > Playback > Player controls, or install mpv: winget install mpv-player.mpv-CI.MSVC";

	/// <summary>Seaglass's controls were chosen but cannot be used on this PC, so mpv's were tried: switching back is no help.</summary>
	public const string FellBackText =
		"mpv isn't installed on this PC, and Seaglass's own player controls can't be used here either, so nothing can play. " +
		"Install mpv: winget install mpv-player.mpv-CI.MSVC";

	/// <summary>
	/// <see cref="Text"/> (or <see cref="FellBackText"/> when Seaglass's controls <paramref name="fellBack"/> to mpv's) for a
	/// refused <c>connect</c> with <see cref="Code"/>, else null.
	/// </summary>
	public static string? For(Exception exception, bool fellBack = false) =>
		exception is BridgeCommandException { Command: Bridge.Commands.Connect, Code: Code } ? fellBack ? FellBackText : Text : null;
}
