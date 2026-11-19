using FluentFin.Core.Mpv;
using FluentFin.Core.Shortcuts;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.3.1: the player's one key router (<see cref="PlayerKeyRouter"/>, docs/SHORTCUTS.md "How the player's keys are
/// routed"), as a matrix: every documented key with its modifiers, against every kind of focus, the player's state and an
/// open panel, from one table (<see cref="Table"/>) that mirrors the Player keys table of SHORTCUTS.md and is checked against
/// the shortcut sheet's catalog. The user's report (2026-11-18): after a click, or after unpausing, the keys did nothing
/// until the overlay hid; Space opened a menu again or left full screen while a button had the focus.
/// </summary>
public sealed class PlayerKeyRouterTests
{
	private const int Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28, Space = 0x20, Enter = 0x0D, Escape = 0x1B, Home = 0x24;
	private const int End = 0x23, PageUp = 0x21, PageDown = 0x22, Tab = 0x09, BrowserBack = 0xA6, A = 0x41;

	/// <summary>One documented key (or a key the router must leave alone): its label in SHORTCUTS.md, the chord, what it does.</summary>
	public sealed record Row(string Label, int Key, bool Control, bool Shift, bool Alt, MpvControlsAction? Action)
	{
		public override string ToString() => $"{(Control ? "Ctrl+" : "")}{(Shift ? "Shift+" : "")}{(Alt ? "Alt+" : "")}{Label}";
	}

	private static Row R(string label, int key, MpvControlsAction? action, bool control = false, bool shift = false, bool alt = false) =>
		new(label, key, control, shift, alt, action);

	/// <summary>
	/// The player keys of docs/SHORTCUTS.md, line by line, and what each does; then the keys that are not the player's (the
	/// router hands them to the focused element or to the window).
	/// </summary>
	public static readonly IReadOnlyList<Row> Table =
	[
		// Space, K: play / pause
		R("Space", Space, MpvControlsAction.TogglePause),
		R("K", MpvControlsViewModel.Keys.K, MpvControlsAction.TogglePause),
		// Left, Right: 5 s
		R("Left", Left, MpvControlsAction.SeekBack),
		R("Right", Right, MpvControlsAction.SeekForward),
		// J, Ctrl+Left: 10 s back; L, Ctrl+Right: 10 s forward
		R("J", MpvControlsViewModel.Keys.J, MpvControlsAction.SeekBackLong),
		R("Left", Left, MpvControlsAction.SeekBackLong, control: true),
		R("L", MpvControlsViewModel.Keys.L, MpvControlsAction.SeekForwardLong),
		R("Right", Right, MpvControlsAction.SeekForwardLong, control: true),
		// Shift+Right, Shift+Left: 30 s
		R("Right", Right, MpvControlsAction.SeekForwardFar, shift: true),
		R("Left", Left, MpvControlsAction.SeekBackFar, shift: true),
		// 0 to 9 (also the number pad): tenths
		R("0", 0x30, MpvControlsAction.Percent0),
		R("5", 0x35, MpvControlsAction.Percent5),
		R("9", 0x39, MpvControlsAction.Percent9),
		R("Numpad 3", 0x63, MpvControlsAction.Percent3),
		// Home: the start
		R("Home", Home, MpvControlsAction.SeekStart),
		// S: skip
		R("S", MpvControlsViewModel.Keys.S, MpvControlsAction.SkipSegment),
		// Shift+N, Shift+P, media keys
		R("N", MpvControlsViewModel.Keys.N, MpvControlsAction.Next, shift: true),
		R("P", MpvControlsViewModel.Keys.P, MpvControlsAction.Previous, shift: true),
		R("Media next", MpvControlsViewModel.Keys.MediaNext, MpvControlsAction.Next),
		R("Media previous", MpvControlsViewModel.Keys.MediaPrevious, MpvControlsAction.Previous),
		R("Media play/pause", MpvControlsViewModel.Keys.MediaPlayPause, MpvControlsAction.TogglePause),
		// [ ] Backspace: speed
		R("[", MpvControlsViewModel.Keys.OpenBracket, MpvControlsAction.SpeedDown),
		R("]", MpvControlsViewModel.Keys.CloseBracket, MpvControlsAction.SpeedUp),
		R("Backspace", MpvControlsViewModel.Keys.Back, MpvControlsAction.SpeedNormal),
		// Up, Down: volume; M: mute
		R("Up", Up, MpvControlsAction.VolumeUp),
		R("Down", Down, MpvControlsAction.VolumeDown),
		R("M", MpvControlsViewModel.Keys.M, MpvControlsAction.ToggleMute),
		// C, V, B: tracks; Z, X: subtitle delay
		R("C", MpvControlsViewModel.Keys.C, MpvControlsAction.ToggleSubtitles),
		R("V", MpvControlsViewModel.Keys.V, MpvControlsAction.CycleSubtitles),
		R("B", MpvControlsViewModel.Keys.B, MpvControlsAction.CycleAudio),
		R("Z", MpvControlsViewModel.Keys.Z, MpvControlsAction.SubtitleEarlier),
		R("X", MpvControlsViewModel.Keys.X, MpvControlsAction.SubtitleLater),
		// F, F11, Alt+Enter: full screen
		R("F", MpvControlsViewModel.Keys.F, MpvControlsAction.ToggleFullScreen),
		R("F11", MpvControlsViewModel.Keys.F11, MpvControlsAction.ToggleFullScreen),
		R("Enter", Enter, MpvControlsAction.ToggleFullScreen, alt: true),
		// not the player's: Escape (the page's accelerator), Enter (presses the focused button), Tab (moves the focus), the Back
		// key (a global key, the window's), End and Page Up/Down (no player key), a letter with no key, a player key with a
		// modifier it does not take
		R("Esc", Escape, null),
		R("Enter", Enter, null),
		R("Tab", Tab, null),
		R("Back key", BrowserBack, null),
		R("End", End, null),
		R("Page Up", PageUp, null),
		R("Page Down", PageDown, null),
		R("A", A, null),
		R("Space", Space, null, control: true),
		R("Left", Left, null, control: true, shift: true),
		R("Left", Left, null, alt: true),
		R("M", MpvControlsViewModel.Keys.M, null, control: true),
	];

	/// <summary>What has the keyboard, as the user would describe it; the router's kind for each (<see cref="KindOf"/>).</summary>
	public enum Focus
	{
		/// <summary>The video (the player itself): where the keyboard is after playback starts, the bars hide or a click.</summary>
		Video,
		/// <summary>Nothing at all (the focused element went away).</summary>
		Nothing,
		/// <summary>The title bar, outside the player (in a window).</summary>
		TitleBar,
		/// <summary>A bar button (play/pause, volume, tracks, Next, Previous, speed, full screen) the mouse clicked: no claim.</summary>
		ClickedButton,
		/// <summary>A bar button the keyboard is on (Tab, or a menu that closed gave it back).</summary>
		KeyboardButton,
		/// <summary>The seek bar, focused by Tab or by a click.</summary>
		SeekBar,
		/// <summary>A tab or an item of a theme's player panel (Cinematic's tabs, its lists), the panel open.</summary>
		PanelItem,
		/// <summary>An item of an open menu or flyout (the speed menu, the tracks panel's choices).</summary>
		MenuItem,
		/// <summary>A text box.</summary>
		TextBox,
	}

	public enum State { Playing, Paused, Loading }

	private static PlayerFocus KindOf(Focus focus) => focus switch
	{
		Focus.KeyboardButton => PlayerFocus.Button,
		Focus.SeekBar => PlayerFocus.SeekBar,
		Focus.PanelItem or Focus.MenuItem => PlayerFocus.List,
		Focus.TextBox => PlayerFocus.TextInput,
		_ => PlayerFocus.Player,
	};

	/// <summary>
	/// The rules, as SHORTCUTS.md states them: nothing is the player's before the video shows or in a text box; Escape is the
	/// page's; in a list or menu its own keys (the arrows, Home, End, Page Up/Down, Space, Enter) are its; on the seek bar End
	/// and Page Up/Down do nothing; everything else in the table does its action, whatever has the focus, playing or paused.
	/// </summary>
	private static PlayerKeyDecision Expected(Row row, Focus focus, State state)
	{
		var listKey = row.Key is Left or Right or Up or Down or Home or End or PageUp or PageDown or Space or Enter && !(row.Alt && row.Key == Enter);
		if (state == State.Loading || focus == Focus.TextBox || row.Key == Escape)
		{
			return PlayerKeyDecision.Element;
		}

		if (focus is Focus.PanelItem or Focus.MenuItem && listKey)
		{
			return PlayerKeyDecision.Element;
		}

		if (focus == Focus.SeekBar && row.Key is End or PageUp or PageDown && !row.Alt)
		{
			return PlayerKeyDecision.Swallow;
		}

		return row.Action is { } action ? new PlayerKeyDecision(PlayerKeyRoute.Player, action) : PlayerKeyDecision.Element;
	}

	public static TheoryData<Row, Focus, State, bool> Matrix()
	{
		var data = new TheoryData<Row, Focus, State, bool>();
		foreach (var row in Table)
		{
			foreach (var focus in Enum.GetValues<Focus>())
			{
				foreach (var state in Enum.GetValues<State>())
				{
					// a panel open over the video with the keyboard elsewhere changes nothing: the focus decides
					foreach (var panelOpen in new[] { false, true })
					{
						if (panelOpen && focus is Focus.PanelItem or Focus.MenuItem)
						{
							continue;
						}

						data.Add(row, focus, state, panelOpen || focus is Focus.PanelItem or Focus.MenuItem);
					}
				}
			}
		}

		return data;
	}

	[Theory]
	[MemberData(nameof(Matrix))]
	public void EveryKey_EveryFocus_EveryState(Row row, Focus focus, State state, bool panelOpen)
	{
		_ = panelOpen;
		var decision = PlayerKeyRouter.Route(row.Key, row.Control, row.Shift, row.Alt, repeat: false, videoShown: state != State.Loading, KindOf(focus));
		Assert.Equal(Expected(row, focus, state), decision);
	}

	// ---- the table is SHORTCUTS.md's and the sheet's ---------------------------------------------------------------------

	[Fact]
	public void TheTable_SaysWhatTheKeyMapSays()
	{
		foreach (var row in Table)
		{
			Assert.True(row.Action == MpvControlsViewModel.ActionFor(row.Key, row.Control, row.Shift, row.Alt) || row.Key == Escape,
				$"{row}: the table says {row.Action}, the map {MpvControlsViewModel.ActionFor(row.Key, row.Control, row.Shift, row.Alt)}");
		}
	}

	[Fact]
	public void EveryPlayerKeyOfTheSheet_IsInTheTable()
	{
		var chords = Table.Where(r => r.Action is not null).Select(r => new ShortcutChord(r.Key, r.Control, r.Shift, r.Alt)).ToHashSet();
		foreach (var entry in ShortcutCatalog.Sections.SelectMany(s => s.Entries).Where(e => e.Scope == ShortcutScope.Player))
		{
			foreach (var chord in entry.Chords.Where(c => c.Key != Escape))
			{
				// the digits: 0, 5 and 9 stand for the row (the sheet lists 0, 9 and the number pad's 0)
				var digit = chord.Key is >= 0x30 and <= 0x39 or >= 0x60 and <= 0x69;
				Assert.True(digit || chords.Contains(chord), $"{entry.Keys}: {chord} is in the matrix's table");
			}
		}
	}

	[Fact]
	public void TheTablesLabels_AreTheDocsPlayerKeys()
	{
		var docs = File.ReadAllText(Path.Combine(RepoRoot, "..", "docs", "SHORTCUTS.md"));
		var player = docs[docs.IndexOf("## Player keys", StringComparison.Ordinal)..docs.IndexOf("## Rules", StringComparison.Ordinal)];
		foreach (var row in Table.Where(r => r.Action is not null))
		{
			var label = row.Label switch
			{
				"Numpad 3" => "number pad",
				"5" => "0 to 9",
				"9" => "0 to 9",
				"0" => "0 to 9",
				"N" or "P" => $"Shift+{row.Label}",
				"Media next" or "Media previous" or "Media play/pause" => "media keys",
				_ => row.Label,
			};
			Assert.Contains(label, player);
		}

		// and the routing rules are written down
		Assert.Contains("## How the player's keys are routed", docs);
	}

	// ---- the user's report, one fact each -----------------------------------------------------------------------------------

	[Fact]
	public void Space_OnAButtonTheKeyboardIsOn_PlaysAndPauses()
	{
		// 1.3.0: a focused button kept Space, so after the volume menu closed (the focus back on its button) Space opened it
		// again, and with the focus on Full screen it left full screen; while paused the bars never hid, so it stayed so
		Assert.Equal(new PlayerKeyDecision(PlayerKeyRoute.Player, MpvControlsAction.TogglePause),
			PlayerKeyRouter.Route(Space, false, false, false, false, true, PlayerFocus.Button));
		// Enter still presses it (the keyboard's way to use a button)
		Assert.Equal(PlayerKeyDecision.Element, PlayerKeyRouter.Route(Enter, false, false, false, false, true, PlayerFocus.Button));
	}

	[Theory]
	[InlineData(Space, MpvControlsAction.TogglePause)]
	[InlineData(Left, MpvControlsAction.SeekBack)]
	[InlineData(Right, MpvControlsAction.SeekForward)]
	[InlineData(Up, MpvControlsAction.VolumeUp)]
	[InlineData(Down, MpvControlsAction.VolumeDown)]
	[InlineData(0x46, MpvControlsAction.ToggleFullScreen)]
	[InlineData(0x4D, MpvControlsAction.ToggleMute)]
	public void AfterAClick_TheKeysWork_WhereverTheFocusWas(int key, MpvControlsAction action)
	{
		foreach (var focus in new[] { PlayerFocus.Player, PlayerFocus.Button, PlayerFocus.SeekBar })
		{
			Assert.Equal(new PlayerKeyDecision(PlayerKeyRoute.Player, action), PlayerKeyRouter.Route(key, false, false, false, false, true, focus));
		}
	}

	[Theory]
	[InlineData(Left)]
	[InlineData(Right)]
	[InlineData(Up)]
	[InlineData(Down)]
	[InlineData(Home)]
	[InlineData(End)]
	[InlineData(Space)]
	[InlineData(Enter)]
	public void InAListOrMenu_ItsOwnKeysAreItsOwn(int key)
	{
		// Cinematic's tabs moved with Left/Right only once the page stopped taking the arrows first (1.3.0 seeked instead)
		Assert.Equal(PlayerKeyDecision.Element, PlayerKeyRouter.Route(key, false, false, false, false, true, PlayerFocus.List));
		// with a modifier too (Shift+Left in a list is the list's)
		Assert.Equal(PlayerKeyDecision.Element, PlayerKeyRouter.Route(key, false, true, false, false, true, PlayerFocus.List));
	}

	[Fact]
	public void InAList_TheOtherPlayerKeysStillWork()
	{
		Assert.Equal(MpvControlsAction.ToggleFullScreen, PlayerKeyRouter.Route(0x46, false, false, false, false, true, PlayerFocus.List).Action);
		Assert.Equal(MpvControlsAction.ToggleFullScreen, PlayerKeyRouter.Route(Enter, false, false, true, false, true, PlayerFocus.List).Action);
		Assert.Equal(MpvControlsAction.ToggleMute, PlayerKeyRouter.Route(0x4D, false, false, false, false, true, PlayerFocus.List).Action);
	}

	// ---- held keys --------------------------------------------------------------------------------------------------------

	[Theory]
	[InlineData(0x46, false, false, false)]   // F
	[InlineData(0x7A, false, false, false)]   // F11
	[InlineData(Enter, false, false, true)]   // Alt+Enter
	[InlineData(Space, false, false, false)]
	[InlineData(0x4B, false, false, false)]   // K
	[InlineData(0x4D, false, false, false)]   // M
	[InlineData(0x4E, false, true, false)]    // Shift+N
	[InlineData(0x50, false, true, false)]    // Shift+P
	[InlineData(0x53, false, false, false)]   // S
	[InlineData(0x43, false, false, false)]   // C
	[InlineData(0x35, false, false, false)]   // 5
	[InlineData(Home, false, false, false)]
	public void AHeldToggle_DoesItsJobOnce(int key, bool control, bool shift, bool alt)
	{
		Assert.Equal(PlayerKeyRoute.Player, PlayerKeyRouter.Route(key, control, shift, alt, repeat: false, true, PlayerFocus.Player).Route);
		Assert.Equal(PlayerKeyDecision.Swallow, PlayerKeyRouter.Route(key, control, shift, alt, repeat: true, true, PlayerFocus.Player));
	}

	[Theory]
	[InlineData(Left, false, false)]
	[InlineData(Right, true, false)]
	[InlineData(Right, false, true)]
	[InlineData(Up, false, false)]
	[InlineData(Down, false, false)]
	[InlineData(0x4A, false, false)]   // J
	[InlineData(0xDD, false, false)]   // ]
	[InlineData(0x58, false, false)]   // X
	public void AHeldSeekOrVolume_KeepsGoing(int key, bool control, bool shift)
	{
		var decision = PlayerKeyRouter.Route(key, control, shift, false, repeat: true, true, PlayerFocus.Player);
		Assert.Equal(PlayerKeyRoute.Player, decision.Route);
		Assert.True(PlayerKeyRouter.Repeats(decision.Action!.Value));
	}

	/// <summary>
	/// Nixie 1.4.0 (the bug hunt's m1): a dialog over the player (F1's shortcut sheet): every key is the dialog's, the player
	/// underneath changes nothing; in 1.3.1 F, M, 5, K and Shift+N acted under the sheet.
	/// </summary>
	[Fact]
	public void UnderADialog_NoKeyIsThePlayers()
	{
		foreach (var row in Table)
		{
			foreach (var repeat in new[] { false, true })
			{
				var decision = PlayerKeyRouter.Route(row.Key, row.Control, row.Shift, row.Alt, repeat, videoShown: true, PlayerFocus.Modal);
				Assert.True(decision.Route == PlayerKeyRoute.Element, $"{row}: {decision.Route}");
			}
		}
	}

	private static string RepoRoot { get; } = FindRepoRoot();

	private static string FindRepoRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FluentFin.sln")))
		{
			dir = dir.Parent;
		}

		return dir?.FullName ?? throw new DirectoryNotFoundException("FluentFin.sln not found above " + AppContext.BaseDirectory);
	}
}
