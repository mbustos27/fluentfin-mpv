namespace FluentFin.Core.Mpv;

/// <summary>
/// fluentfin-mpv (release 1.0): what the player page says when the backend's <c>connect</c> answers <c>mpv_not_found</c>
/// (docs/BRIDGE.md section 2): mpv's controls are chosen, or FluentFin fell back to them, and there is no mpv.exe to run.
/// A plain sentence with what to do instead of the backend's error.
/// </summary>
public static class MpvNotFoundMessage
{
	public const string Code = "mpv_not_found";

	public const string Text =
		"mpv isn't installed on this PC, and mpv's player controls need it. Switch to FluentFin's controls in " +
		"Settings > Playback > Player controls, or install mpv: winget install mpv-player.mpv-CI.MSVC";

	/// <summary><see cref="Text"/> for a refused <c>connect</c> with <see cref="Code"/>, else null.</summary>
	public static string? For(Exception exception) =>
		exception is BridgeCommandException { Command: Bridge.Commands.Connect, Code: Code } ? Text : null;
}
