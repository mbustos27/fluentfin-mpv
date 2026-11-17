namespace FluentFin.Core.Mpv;

/// <summary>
/// 1.2.0 (the interaction audit, item 1): what Escape does on the player page. Full screen: leave it. In a window: show the
/// controls, nothing else. Until 1.1.3 Escape in a window went Back, which stopped the video and reported it (0.4 s, measured
/// live); a second "make sure" press out of full screen did the same. Every desktop video player (Netflix, YouTube, the
/// Windows Media Player app, VLC, mpv) uses Escape for full screen only, and Microsoft's keyboard guidance has Escape cancel
/// transient UI, never navigate back. Leaving stays on the player's Back button, Alt+Left, the Back key and Mouse 4.
/// </summary>
public static class PlayerEscape
{
	public enum Action
	{
		/// <summary>Out of full screen; playback goes on.</summary>
		LeaveFullScreen,

		/// <summary>In a window: the controls show; playback goes on.</summary>
		ShowControls,
	}

	/// <summary>Escape's action: there is no "leave" among them.</summary>
	public static Action For(bool fullScreen) => fullScreen ? Action.LeaveFullScreen : Action.ShowControls;
}
