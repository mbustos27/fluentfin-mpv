namespace FluentFin.Core.Mpv;

/// <summary>
/// Nixie 1.3.1: what has the keyboard when a key comes to the player page, as the one key router sees it
/// (<see cref="PlayerKeyRouter"/>). The view decides the kind from the focused element; every theme's controls fall into the
/// same kinds, so a key does the same thing whatever the theme.
/// </summary>
public enum PlayerFocus
{
	/// <summary>
	/// Nothing in particular: the video, the player itself, nothing at all, a control the mouse clicked, or anything else in
	/// the window that is none of the kinds below (the title bar).
	/// </summary>
	Player,

	/// <summary>A button of the player that the keyboard is on (Tab, or a menu closing gave it back): Enter presses it, Space still plays and pauses.</summary>
	Button,

	/// <summary>The seek bar, whoever focused it.</summary>
	SeekBar,

	/// <summary>
	/// A list, tab strip, menu or set of choices whose own keys are the arrows, Home, End, Page Up/Down, Space and Enter:
	/// Cinematic's player tabs and panel, an open menu or flyout, radio buttons, a list item.
	/// </summary>
	List,

	/// <summary>A text box, password box, search box, number box or editable combo box: every key is the text's.</summary>
	TextInput,
}

/// <summary>Where a key goes.</summary>
public enum PlayerKeyRoute
{
	/// <summary>Not the player's: the focused element (and then the page's accelerators, Escape among them) has it.</summary>
	Element,

	/// <summary>The player's: <see cref="PlayerKeyDecision.Action"/> is done and the key is handled.</summary>
	Player,

	/// <summary>Handled, and nothing is done (a held F's auto-repeat; End or Page Up/Down on the seek bar).</summary>
	Swallow,
}

/// <summary>The router's answer for one key: where it goes and, for the player, what it does.</summary>
public readonly record struct PlayerKeyDecision(PlayerKeyRoute Route, MpvControlsAction? Action = null)
{
	public static PlayerKeyDecision Element { get; } = new(PlayerKeyRoute.Element);

	public static PlayerKeyDecision Swallow { get; } = new(PlayerKeyRoute.Swallow);
}

/// <summary>
/// Nixie 1.3.1: the player page's one key router (docs/SHORTCUTS.md, "How the player's keys are routed"). The page asks it
/// for every key that reaches the window while the video is shown, before any focused control sees the key, so a key does
/// the same thing whichever control has the keyboard, in every theme, playing or paused:
/// <list type="bullet">
/// <item>Space (and K) always play and pause, unless the focus is in a text box or in a list or menu, where Space selects.</item>
/// <item>The arrows seek (Left/Right) and change the volume (Up/Down) unless the focus is in a list, tab strip or menu: there
/// they move within it (with Home, End and Page Up/Down).</item>
/// <item>Enter presses the focused button; Alt+Enter is full screen.</item>
/// <item>Escape is the page's accelerator (an open panel first, then full screen; PlayerEscape).</item>
/// <item>A held key repeats only seeks, volume, speed steps and the subtitle delay (<see cref="Repeats"/>); a held F, Space or
/// Shift+N does nothing more than its first press.</item>
/// <item>Nothing is the player's while the focus is in a text input, nor before the video shows (the loading screen's Cancel).</item>
/// </list>
/// Pure: the matrix of keys, modifiers, focus kinds and states is tested without a window (PlayerKeyRouterTests).
/// </summary>
public static class PlayerKeyRouter
{
	private const int PageUp = 0x21, PageDown = 0x22, End = 0x23;

	/// <summary>The keys a list, tab strip or menu moves or chooses with (its own, ahead of the player's).</summary>
	public static bool IsListKey(int key) => key is MpvControlsViewModel.Keys.Left or MpvControlsViewModel.Keys.Right
		or MpvControlsViewModel.Keys.Up or MpvControlsViewModel.Keys.Down or MpvControlsViewModel.Keys.Home or End or PageUp or PageDown
		or MpvControlsViewModel.Keys.Space or MpvControlsViewModel.Keys.Enter;

	/// <summary>
	/// The decision for <paramref name="key"/> (a Win32 virtual-key code) with its modifiers. <paramref name="repeat"/>: the key
	/// was already down (auto-repeat). <paramref name="videoShown"/>: the video is on screen (not the loading or an error screen).
	/// </summary>
	public static PlayerKeyDecision Route(int key, bool control, bool shift, bool alt, bool repeat, bool videoShown, PlayerFocus focus)
	{
		if (!videoShown || focus == PlayerFocus.TextInput || key == MpvControlsViewModel.Keys.Escape)
		{
			return PlayerKeyDecision.Element;
		}

		if (focus == PlayerFocus.List && IsListKey(key) && !(alt && key == MpvControlsViewModel.Keys.Enter))
		{
			return PlayerKeyDecision.Element;
		}

		if (focus == PlayerFocus.SeekBar && !alt && key is End or PageUp or PageDown)
		{
			// the slider would move its thumb and seek nowhere (it seeks on a drag only): nothing at all instead
			return PlayerKeyDecision.Swallow;
		}

		if (MpvControlsViewModel.ActionFor(key, control, shift, alt) is not { } action)
		{
			return PlayerKeyDecision.Element;
		}

		if (repeat && !Repeats(action))
		{
			// Seaglass 1.1.3: held, F and F11 switched full screen about 15 times a second; 1.3.1: a held Space, M or Shift+N
			// likewise played and paused, muted and unmuted, or went through the queue
			return PlayerKeyDecision.Swallow;
		}

		return new PlayerKeyDecision(PlayerKeyRoute.Player, action);
	}

	/// <summary>
	/// The actions a held key repeats: the seeks, the volume, the speed steps and the subtitle delay go on while the key is
	/// held; everything else (a toggle, a jump, the next item) happens once per press.
	/// </summary>
	public static bool Repeats(MpvControlsAction action) => action is MpvControlsAction.SeekBack or MpvControlsAction.SeekForward
		or MpvControlsAction.SeekBackLong or MpvControlsAction.SeekForwardLong or MpvControlsAction.SeekBackFar or MpvControlsAction.SeekForwardFar
		or MpvControlsAction.VolumeUp or MpvControlsAction.VolumeDown or MpvControlsAction.SpeedDown or MpvControlsAction.SpeedUp
		or MpvControlsAction.SubtitleEarlier or MpvControlsAction.SubtitleLater;
}
