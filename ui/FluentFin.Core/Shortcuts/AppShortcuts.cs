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
		public const int Left = 0x25, Right = 0x27, Home = 0x24, F1 = 0x70;
		public const int BrowserBack = 0xA6, BrowserForward = 0xA7;
		public const int E = 0x45, F = 0x46;
		/// <summary>The "/" and "?" key (VK_OEM_2), and the "," key (VK_OEM_COMMA).</summary>
		public const int Slash = 0xBF, Comma = 0xBC;
	}

	/// <summary>
	/// Whether <paramref name="shortcut"/> applies where the user is. Signed out: back, forward and the sheet (there is no
	/// search, Home or Settings yet). In the player: back (it leaves) and the sheet only; going elsewhere would leave the
	/// video playing behind another page, and Forward has nowhere to go.
	/// </summary>
	public static bool AllowedIn(AppShortcut shortcut, ShortcutContext context) => context switch
	{
		ShortcutContext.Browsing => true,
		ShortcutContext.SignedOut => shortcut is AppShortcut.Back or AppShortcut.Forward or AppShortcut.ShowHelp,
		_ => shortcut is AppShortcut.Back or AppShortcut.ShowHelp,
	};

	/// <summary>The key map: null for a key that is none of the global ones, or whenever <paramref name="inTextInput"/>.</summary>
	public static AppShortcut? For(int virtualKey, bool control, bool shift, bool alt, bool inTextInput)
	{
		if (inTextInput)
		{
			return null;
		}

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
			_ => null,
		};
	}
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
		]),
		new("Home page",
		[
			new("N", "Next up", ShortcutScope.Page),
			new("C", "Continue watching", ShortcutScope.Page),
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
			new("F, F11, Alt+Enter", "Full screen", ShortcutScope.Player, C(MpvControlsViewModel.Keys.F), C(MpvControlsViewModel.Keys.F11), C(MpvControlsViewModel.Keys.Enter, Alt: true)),
			new("Esc", "Leave full screen, or cancel a start still loading (never stops the video)", ShortcutScope.Player, C(MpvControlsViewModel.Keys.Escape)),
		]),
	];
}
