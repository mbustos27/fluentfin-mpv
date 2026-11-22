using FluentFin.Core.Mpv;

namespace FluentFin.Core.Shortcuts;

/// <summary>What a global key does, on every page (the player's own keys are <see cref="MpvControlsAction"/>).</summary>
public enum AppShortcut
{
	Back,
	Forward,
	FocusSearch,
	OpenSettings,
	GoHome,
	ShowHelp,

	/// <summary>Nixie 1.3.1: load the page again from the server (R, F5, Ctrl+R; the user: "make r refresh the jellyfin page").</summary>
	Refresh,
}

/// <summary>Where the user is: signed out (server list, login), browsing, or in the player.</summary>
public enum ShortcutContext { SignedOut, Browsing, Player }

/// <summary>
/// Seaglass: the global key map (docs/SHORTCUTS.md), pure: which <see cref="AppShortcut"/> a key and its modifiers mean.
/// The window's key handler asks it for every key that reaches the window; nothing fires while the focus is in a text
/// input, and the keys here never overlap the player's (<see cref="MpvControlsViewModel.ActionFor"/>), so the two maps can
/// both be asked about the same key.
/// </summary>
public static class AppShortcuts
{
	/// <summary>Win32 virtual-key codes (the values of Windows.System.VirtualKey).</summary>
	public static class Keys
	{
		public const int Left = 0x25, Right = 0x27, Home = 0x24, F1 = 0x70, F5 = 0x74;
		public const int BrowserBack = 0xA6, BrowserForward = 0xA7;
		public const int E = 0x45, F = 0x46, R = 0x52;
		/// <summary>The "/" and "?" key (VK_OEM_2), and the "," key (VK_OEM_COMMA).</summary>
		public const int Slash = 0xBF, Comma = 0xBC;
	}

	/// <summary>
	/// Whether <paramref name="shortcut"/> applies where the user is. Signed out: back, forward and the sheet (there is no
	/// search, Home or Settings yet, and no server page to refresh). In the player: back (it leaves) and the sheet only; going
	/// elsewhere would leave the video playing behind another page, Forward has nowhere to go, and a refresh would start it again.
	/// </summary>
	public static bool AllowedIn(AppShortcut shortcut, ShortcutContext context) => context switch
	{
		ShortcutContext.Browsing => true,
		ShortcutContext.SignedOut => shortcut is AppShortcut.Back or AppShortcut.Forward or AppShortcut.ShowHelp,
		_ => shortcut is AppShortcut.Back or AppShortcut.ShowHelp,
	};

	/// <summary>
	/// Nixie 1.4.0 (bug hunt M4: a held R reloaded the page on every auto-repeat, 16 reloads in a second): whether a held key's
	/// repeats do the shortcut again. Only Back and Forward step on (a held Alt+Left walks back, as in a browser); the rest act
	/// once per press.
	/// </summary>
	public static bool Repeats(AppShortcut shortcut) => shortcut is AppShortcut.Back or AppShortcut.Forward;

	/// <summary>Nixie 1.4.0: Shift, Ctrl, Alt, the Windows keys and Caps Lock alone (the start of a chord, not a key of its own).</summary>
	public static bool IsModifierKey(int virtualKey) => virtualKey is 0x10 or 0x11 or 0x12 or (>= 0xA0 and <= 0xA5) or 0x5B or 0x5C or 0x14;

	/// <summary>
	/// The key map: null for a key that is none of the global ones, or whenever <paramref name="inTextInput"/>. Nixie 1.4.0:
	/// null for a held key's <paramref name="repeat"/> (<see cref="Repeats"/>), and for a bare letter when the focused control
	/// types with its letters (<paramref name="ownsLetters"/>: a closed combo box's type-ahead, bug hunt m2; F5 and Ctrl+R still
	/// refresh there).
	/// </summary>
	public static AppShortcut? For(int virtualKey, bool control, bool shift, bool alt, bool inTextInput, bool repeat = false, bool ownsLetters = false)
	{
		if (inTextInput || (ownsLetters && !control && !alt && virtualKey is >= 0x41 and <= 0x5A))
		{
			return null;
		}

		return Map(virtualKey, control, shift, alt) is { } found && (!repeat || Repeats(found)) ? found : null;
	}

	private static AppShortcut? Map(int virtualKey, bool control, bool shift, bool alt)
	{
		if (alt)
		{
			return control || shift ? null : virtualKey switch
			{
				Keys.Left => AppShortcut.Back,
				Keys.Right => AppShortcut.Forward,
				Keys.Home => AppShortcut.GoHome,
				_ => null,
			};
		}

		return virtualKey switch
		{
			Keys.BrowserBack when !control && !shift => AppShortcut.Back,
			Keys.BrowserForward when !control && !shift => AppShortcut.Forward,
			Keys.F or Keys.E when control && !shift => AppShortcut.FocusSearch,
			Keys.Slash when !control && !shift => AppShortcut.FocusSearch,
			Keys.Slash when shift && !control => AppShortcut.ShowHelp,
			Keys.F1 when !control && !shift => AppShortcut.ShowHelp,
			Keys.Comma when control && !shift => AppShortcut.OpenSettings,
			Keys.R when !shift => AppShortcut.Refresh,
			Keys.F5 when !control && !shift => AppShortcut.Refresh,
			_ => null,
		};
	}

	/// <summary>
	/// Nixie 1.3.1: whether a key pressed while the shortcut sheet is open closes it: the keys that open it (F1, ? as Shift+/)
	/// toggle it. A held key's repeats do not (<paramref name="repeat"/>): holding F1 must not open and close it in turn.
	/// </summary>
	public static bool ClosesHelp(int virtualKey, bool control, bool shift, bool alt, bool repeat = false) =>
		For(virtualKey, control, shift, alt, inTextInput: false, repeat) == AppShortcut.ShowHelp;
}

/// <summary>The mouse's side buttons (Mouse 4 and Mouse 5).</summary>
public static class AppMouse
{
	/// <summary>XButton1 is Back, XButton2 is Forward, in every page and in the player; nothing for any other press.</summary>
	public static AppShortcut? For(bool xButton1, bool xButton2) => (xButton1, xButton2) switch
	{
		(true, false) => AppShortcut.Back,
		(false, true) => AppShortcut.Forward,
		_ => null,
	};
}

public enum ShortcutScope { Global, Page, Player }

/// <summary>One line of the sheet. <paramref name="Chords"/> are the keys it stands for, for the tests (none for page keys).</summary>
public sealed record ShortcutEntry(string Keys, string Description, ShortcutScope Scope, params ShortcutChord[] Chords);

/// <summary>A key and its modifiers, as <see cref="AppShortcuts.For"/> and <see cref="MpvControlsViewModel.ActionFor"/> take them.</summary>
public readonly record struct ShortcutChord(int Key, bool Control = false, bool Shift = false, bool Alt = false);

public sealed record ShortcutSection(string Title, IReadOnlyList<ShortcutEntry> Entries);

/// <summary>
/// Every key the sheet ("?") lists, by section. The tests check each entry against the two key maps, so the sheet, the maps
/// and docs/SHORTCUTS.md cannot drift apart without a test failing.
/// </summary>
public static class ShortcutCatalog
{
	private static ShortcutChord C(int key, bool Control = false, bool Shift = false, bool Alt = false) => new(key, Control, Shift, Alt);

	private const int Left = 0x25, Right = 0x27, Up = 0x26, Down = 0x28, Space = 0x20;

	public static IReadOnlyList<ShortcutSection> Sections { get; } =
	[
		new("Everywhere",
		[
			new("Alt+Left, Back key, Mouse 4", "Back (in the player: leave it)", ShortcutScope.Global,
				C(Left, Alt: true), C(AppShortcuts.Keys.BrowserBack)),
			new("Alt+Right, Forward key, Mouse 5", "Forward (never into the player)", ShortcutScope.Global,
				C(Right, Alt: true), C(AppShortcuts.Keys.BrowserForward)),
			new("Ctrl+F, Ctrl+E, /", "Search", ShortcutScope.Global,
				C(AppShortcuts.Keys.F, Control: true), C(AppShortcuts.Keys.E, Control: true), C(AppShortcuts.Keys.Slash)),
			new("Alt+Home", "Go to Home", ShortcutScope.Global, C(AppShortcuts.Keys.Home, Alt: true)),
			new("Ctrl+,", "Settings", ShortcutScope.Global, C(AppShortcuts.Keys.Comma, Control: true)),
			new("? (Shift+/), F1", "This list", ShortcutScope.Global, C(AppShortcuts.Keys.Slash, Shift: true), C(AppShortcuts.Keys.F1)),
			new("R, F5, Ctrl+R", "Refresh the page from the server", ShortcutScope.Global,
				C(AppShortcuts.Keys.R), C(AppShortcuts.Keys.F5), C(AppShortcuts.Keys.R, Control: true)),
		]),
		new("Home page",
		[
			new("N", "Go to the Next Up row (Enter plays)", ShortcutScope.Page),
			new("C", "Go to the Continue Watching row (Enter plays)", ShortcutScope.Page),
			// 1.5 (the button review's L3): it was only on the notice's tooltip
			new("Ctrl+Z", "Undo the last removal from Continue Watching, while its notice shows", ShortcutScope.Page),
		]),
		// Nixie 1.4.0: R refreshes; while you're typing a title, R is a letter; R titles: the A to Z bar
		new("Library grids",
		[
			new("Letters on a poster", "Go to the title they start; R alone refreshes, but within a second of another letter it is part of the title", ShortcutScope.Page),
			new("A letter on the A to Z", "Go to that letter's titles (R there for the titles that start with R)", ShortcutScope.Page),
		]),
		new("Playback",
		[
			new("Space, K", "Play or pause", ShortcutScope.Player, C(Space), C(MpvControlsViewModel.Keys.K)),
			new("Left, Right", "Back or forward 5 seconds", ShortcutScope.Player, C(Left), C(Right)),
			new("J, Ctrl+Left", "Back 10 seconds (the bar's button)", ShortcutScope.Player, C(MpvControlsViewModel.Keys.J), C(Left, Control: true)),
			new("L, Ctrl+Right", "Forward 10 seconds (the bar's button)", ShortcutScope.Player, C(MpvControlsViewModel.Keys.L), C(Right, Control: true)),
			new("Shift+Right", "Forward 30 seconds", ShortcutScope.Player, C(Right, Shift: true)),
			new("Shift+Left", "Back 30 seconds", ShortcutScope.Player, C(Left, Shift: true)),
			new("0 to 9", "Jump to that tenth of the video (0 is the start)", ShortcutScope.Player,
				C(MpvControlsViewModel.Keys.D0), C(MpvControlsViewModel.Keys.D9), C(MpvControlsViewModel.Keys.Numpad0)),
			new("Home", "Back to the start", ShortcutScope.Player, C(MpvControlsViewModel.Keys.Home)),
			new("S", "Skip intro or credits, when offered", ShortcutScope.Player, C(MpvControlsViewModel.Keys.S)),
			// 1.6 skip
			new("Shift+S", "Skip a part in every episode of the show (marks a skip rule)", ShortcutScope.Player,
				C(MpvControlsViewModel.Keys.S, Shift: true)),
			new("Shift+N, Shift+P", "Next or previous episode", ShortcutScope.Player,
				C(MpvControlsViewModel.Keys.N, Shift: true), C(MpvControlsViewModel.Keys.P, Shift: true)),
			new("[, ]", "Slower or faster (0.5× to 2×)", ShortcutScope.Player,
				C(MpvControlsViewModel.Keys.OpenBracket), C(MpvControlsViewModel.Keys.CloseBracket)),
			new("Backspace", "Normal speed", ShortcutScope.Player, C(MpvControlsViewModel.Keys.Back)),
		]),
		new("Sound, subtitles and the window",
		[
			new("Up, Down, mouse wheel", "Volume", ShortcutScope.Player, C(Up), C(Down)),
			new("M", "Mute", ShortcutScope.Player, C(MpvControlsViewModel.Keys.M)),
			new("C", "Subtitles on or off", ShortcutScope.Player, C(MpvControlsViewModel.Keys.C)),
			new("V", "Next subtitle track", ShortcutScope.Player, C(MpvControlsViewModel.Keys.V)),
			new("B", "Next audio track", ShortcutScope.Player, C(MpvControlsViewModel.Keys.B)),
			new("Z, X", "Subtitles 0.1 s earlier or later", ShortcutScope.Player, C(MpvControlsViewModel.Keys.Z), C(MpvControlsViewModel.Keys.X)),
			new("I", "Playback stats: show or hide (Esc hides them too)", ShortcutScope.Player, C(MpvControlsViewModel.Keys.I)), // 1.6 player
			new("F, F11, Alt+Enter", "Full screen", ShortcutScope.Player, C(MpvControlsViewModel.Keys.F), C(MpvControlsViewModel.Keys.F11), C(MpvControlsViewModel.Keys.Enter, Alt: true)),
			new("Esc", "Leave full screen, or cancel a start still loading (never stops the video)", ShortcutScope.Player, C(MpvControlsViewModel.Keys.Escape)),
		]),
	];
}
