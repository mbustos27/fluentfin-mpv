using System.Text.Json.Nodes;
using FluentFin.Core.Mpv;
using FluentFin.Core.Shortcuts;

namespace FluentFin.Core.Tests;

/// <summary>Seaglass: the key maps (docs/SHORTCUTS.md): the global keys, the player's, the text-input guard, the mouse.</summary>
public sealed class ShortcutsTests
{
	private const int Left = 0x25, Right = 0x27;

	[Theory]
	[InlineData(Left, false, false, true, AppShortcut.Back)]
	[InlineData(Right, false, false, true, AppShortcut.Forward)]
	[InlineData(AppShortcuts.Keys.Home, false, false, true, AppShortcut.GoHome)]
	[InlineData(AppShortcuts.Keys.BrowserBack, false, false, false, AppShortcut.Back)]
	[InlineData(AppShortcuts.Keys.BrowserForward, false, false, false, AppShortcut.Forward)]
	[InlineData(AppShortcuts.Keys.F, true, false, false, AppShortcut.FocusSearch)]
	[InlineData(AppShortcuts.Keys.E, true, false, false, AppShortcut.FocusSearch)]
	[InlineData(AppShortcuts.Keys.Slash, false, false, false, AppShortcut.FocusSearch)]
	[InlineData(AppShortcuts.Keys.Slash, false, true, false, AppShortcut.ShowHelp)]
	[InlineData(AppShortcuts.Keys.F1, false, false, false, AppShortcut.ShowHelp)]
	[InlineData(AppShortcuts.Keys.Comma, true, false, false, AppShortcut.OpenSettings)]
	[InlineData(AppShortcuts.Keys.R, false, false, false, AppShortcut.Refresh)]
	[InlineData(AppShortcuts.Keys.R, true, false, false, AppShortcut.Refresh)]
	[InlineData(AppShortcuts.Keys.F5, false, false, false, AppShortcut.Refresh)]
	public void GlobalKeys_MeanWhatTheMapSays(int key, bool control, bool shift, bool alt, AppShortcut expected) =>
		Assert.Equal(expected, AppShortcuts.For(key, control, shift, alt, inTextInput: false));

	// Nixie 1.3.1 (the user: "f1 should close the menu as well"): the keys that open the sheet close it; Esc stays the dialog's own
	[Theory]
	[InlineData(AppShortcuts.Keys.F1, false, false, false, false, true)]
	[InlineData(AppShortcuts.Keys.Slash, false, true, false, false, true)]
	[InlineData(AppShortcuts.Keys.F1, false, false, false, true, false)]
	[InlineData(AppShortcuts.Keys.Slash, false, true, false, true, false)]
	[InlineData(AppShortcuts.Keys.Slash, false, false, false, false, false)]
	[InlineData(AppShortcuts.Keys.F1, true, false, false, false, false)]
	[InlineData(AppShortcuts.Keys.Comma, true, false, false, false, false)]
	[InlineData(0x1B, false, false, false, false, false)]
	public void TheKeysThatOpenTheSheet_CloseIt_ButNotTheirRepeats(int key, bool control, bool shift, bool alt, bool repeat, bool closes) =>
		Assert.Equal(closes, AppShortcuts.ClosesHelp(key, control, shift, alt, repeat));

	// Nixie 1.3.1 (the user: "make r refresh the jellyfin page"): R, F5 and Ctrl+R, and no other chord of them; never while
	// typing (an R typed in Search or a Settings field is a letter), never in the player or signed out
	[Fact]
	public void Refresh_IsRF5AndCtrlR_NotWhileTyping_NotInThePlayer()
	{
		Assert.Null(AppShortcuts.For(AppShortcuts.Keys.R, control: false, shift: true, alt: false, inTextInput: false));
		Assert.Null(AppShortcuts.For(AppShortcuts.Keys.R, control: true, shift: true, alt: false, inTextInput: false));
		Assert.Null(AppShortcuts.For(AppShortcuts.Keys.F5, control: true, shift: false, alt: false, inTextInput: false));
		Assert.Null(AppShortcuts.For(AppShortcuts.Keys.R, control: false, shift: false, alt: true, inTextInput: false));
		Assert.Null(AppShortcuts.For(AppShortcuts.Keys.R, control: false, shift: false, alt: false, inTextInput: true));
		Assert.Null(AppShortcuts.For(AppShortcuts.Keys.F5, control: false, shift: false, alt: false, inTextInput: true));
		Assert.True(AppShortcuts.AllowedIn(AppShortcut.Refresh, ShortcutContext.Browsing));
		Assert.False(AppShortcuts.AllowedIn(AppShortcut.Refresh, ShortcutContext.Player));
		Assert.False(AppShortcuts.AllowedIn(AppShortcut.Refresh, ShortcutContext.SignedOut));
		Assert.Null(MpvControlsViewModel.ActionFor(AppShortcuts.Keys.R, false, false, false));
	}

	/// <summary>
	/// Nixie 1.4.0 (bug hunt M4: a held R reloaded the page on every auto-repeat, 16 reloads and 32 requests in a second; a
	/// held F5 11): a held key's repeats do nothing but Back and Forward, which step on as in a browser.
	/// </summary>
	[Fact]
	public void AHeldKey_ActsOnce_ButBackAndForwardStepOn()
	{
		Assert.Equal(AppShortcut.Refresh, AppShortcuts.For(AppShortcuts.Keys.R, false, false, false, inTextInput: false, repeat: false));
		Assert.Null(AppShortcuts.For(AppShortcuts.Keys.R, false, false, false, inTextInput: false, repeat: true));
		Assert.Null(AppShortcuts.For(AppShortcuts.Keys.F5, false, false, false, inTextInput: false, repeat: true));
		Assert.Null(AppShortcuts.For(AppShortcuts.Keys.R, true, false, false, inTextInput: false, repeat: true));
		Assert.Null(AppShortcuts.For(AppShortcuts.Keys.F, true, false, false, inTextInput: false, repeat: true));
		Assert.Null(AppShortcuts.For(AppShortcuts.Keys.Home, false, false, true, inTextInput: false, repeat: true));
		Assert.Null(AppShortcuts.For(AppShortcuts.Keys.Comma, true, false, false, inTextInput: false, repeat: true));
		Assert.Equal(AppShortcut.Back, AppShortcuts.For(AppShortcuts.Keys.Left, false, false, true, inTextInput: false, repeat: true));
		Assert.Equal(AppShortcut.Back, AppShortcuts.For(AppShortcuts.Keys.BrowserBack, false, false, false, inTextInput: false, repeat: true));
		Assert.Equal(AppShortcut.Forward, AppShortcuts.For(AppShortcuts.Keys.Right, false, false, true, inTextInput: false, repeat: true));
		foreach (var shortcut in Enum.GetValues<AppShortcut>())
		{
			Assert.Equal(shortcut is AppShortcut.Back or AppShortcut.Forward, AppShortcuts.Repeats(shortcut));
		}
	}

	/// <summary>
	/// Nixie 1.4.0 (bug hunt m2: R on a closed Audio box refreshed the page): a control that types with its letters (a combo
	/// box's type-ahead) keeps the bare letters; F5 and Ctrl+R still refresh there, and the other keys still work.
	/// </summary>
	[Fact]
	public void AControlThatTypesWithItsLetters_KeepsR_ButF5AndCtrlRStillRefresh()
	{
		Assert.Null(AppShortcuts.For(AppShortcuts.Keys.R, false, false, false, inTextInput: false, ownsLetters: true));
		Assert.Equal(AppShortcut.Refresh, AppShortcuts.For(AppShortcuts.Keys.F5, false, false, false, inTextInput: false, ownsLetters: true));
		Assert.Equal(AppShortcut.Refresh, AppShortcuts.For(AppShortcuts.Keys.R, true, false, false, inTextInput: false, ownsLetters: true));
		Assert.Equal(AppShortcut.FocusSearch, AppShortcuts.For(AppShortcuts.Keys.F, true, false, false, inTextInput: false, ownsLetters: true));
		Assert.Equal(AppShortcut.Back, AppShortcuts.For(AppShortcuts.Keys.Left, false, false, true, inTextInput: false, ownsLetters: true));
		Assert.Equal(AppShortcut.ShowHelp, AppShortcuts.For(AppShortcuts.Keys.F1, false, false, false, inTextInput: false, ownsLetters: true));
	}

	/// <summary>
	/// Nixie 1.4.0 (R in a grid, the coordinator's rule): R alone refreshes, grids included; a letter typed on a grid within
	/// the last second opens a word, and R then goes on with it ("m", "a", "r" to "Mar…"); a second later R refreshes again.
	/// F5 and Ctrl+R always refresh.
	/// </summary>
	[Theory]
	[InlineData(0L, 0L, false)]           // nothing typed yet
	[InlineData(10_000L, 10_400L, true)]  // "m", "a" and then R within the second
	[InlineData(10_000L, 11_000L, true)]  // the second's last moment
	[InlineData(10_000L, 11_001L, false)] // a moment later: the word is over, R refreshes
	[InlineData(10_000L, 9_000L, false)]  // a clock that went back closes it
	public void RInAGrid_RefreshesUnlessATitleIsBeingTyped(long lastTypedAt, long now, bool wordOpen)
	{
		Assert.Equal(wordOpen, TypeAheadWord.IsOpen(lastTypedAt, now));
		var r = AppShortcuts.For(AppShortcuts.Keys.R, false, false, false, inTextInput: false, ownsLetters: TypeAheadWord.IsOpen(lastTypedAt, now));
		Assert.Equal(wordOpen ? null : (AppShortcut?)AppShortcut.Refresh, r);
		Assert.Equal(AppShortcut.Refresh, AppShortcuts.For(AppShortcuts.Keys.F5, false, false, false, inTextInput: false, ownsLetters: TypeAheadWord.IsOpen(lastTypedAt, now)));
		Assert.Equal(AppShortcut.Refresh, AppShortcuts.For(AppShortcuts.Keys.R, true, false, false, inTextInput: false, ownsLetters: TypeAheadWord.IsOpen(lastTypedAt, now)));
		// R in the search box (or any text box) is always a letter
		Assert.Null(AppShortcuts.For(AppShortcuts.Keys.R, false, false, false, inTextInput: true));
	}

	[Fact]
	public void TheModifiersAlone_AreNoKeyOfTheirOwn()
	{
		foreach (var key in new[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C, 0x14 })
		{
			Assert.True(AppShortcuts.IsModifierKey(key), $"{key:X}");
		}

		foreach (var key in new[] { AppShortcuts.Keys.R, AppShortcuts.Keys.F5, 0x09, 0x1B, 0x20, 0x0D, 0x25 })
		{
			Assert.False(AppShortcuts.IsModifierKey(key), $"{key:X}");
		}
	}

	[Fact]
	public void NothingFires_WhileTyping()
	{
		foreach (var section in ShortcutCatalog.Sections)
		{
			foreach (var chord in section.Entries.Where(e => e.Scope == ShortcutScope.Global).SelectMany(e => e.Chords))
			{
				Assert.NotNull(AppShortcuts.For(chord.Key, chord.Control, chord.Shift, chord.Alt, inTextInput: false));
				if (chord.Key is AppShortcuts.Keys.BrowserBack or AppShortcuts.Keys.BrowserForward)
				{
					continue; // Nixie 1.7.5: these keys type nothing, so they step back and forward from a text box too (BackKeysInTextTests)
				}

				Assert.Null(AppShortcuts.For(chord.Key, chord.Control, chord.Shift, chord.Alt, inTextInput: true));
			}
		}
	}

	[Fact]
	public void ExtraModifiers_AreNotTheShortcut()
	{
		Assert.Null(AppShortcuts.For(Left, control: true, shift: false, alt: true, inTextInput: false));
		Assert.Null(AppShortcuts.For(Left, control: false, shift: true, alt: true, inTextInput: false));
		Assert.Null(AppShortcuts.For(AppShortcuts.Keys.F, control: true, shift: true, alt: false, inTextInput: false));
		Assert.Null(AppShortcuts.For(AppShortcuts.Keys.F, control: false, shift: false, alt: false, inTextInput: false));
		Assert.Null(AppShortcuts.For(AppShortcuts.Keys.Slash, control: true, shift: false, alt: false, inTextInput: false));
	}

	[Fact]
	public void WhereTheyApply_PlayerAndSignedOutKeepToBackAndHelp()
	{
		foreach (var shortcut in Enum.GetValues<AppShortcut>())
		{
			Assert.True(AppShortcuts.AllowedIn(shortcut, ShortcutContext.Browsing));
		}

		Assert.Equal([AppShortcut.Back, AppShortcut.ShowHelp], Enum.GetValues<AppShortcut>().Where(x => AppShortcuts.AllowedIn(x, ShortcutContext.Player)));
		Assert.Equal([AppShortcut.Back, AppShortcut.Forward, AppShortcut.ShowHelp], Enum.GetValues<AppShortcut>().Where(x => AppShortcuts.AllowedIn(x, ShortcutContext.SignedOut)));
	}

	[Fact]
	public void TheSideButtons_AreBackAndForward_AndNothingElseIs()
	{
		Assert.Equal(AppShortcut.Back, AppMouse.For(xButton1: true, xButton2: false));
		Assert.Equal(AppShortcut.Forward, AppMouse.For(xButton1: false, xButton2: true));
		Assert.Null(AppMouse.For(false, false));
		Assert.Null(AppMouse.For(true, true));
	}

	[Fact]
	public void EveryKeyInTheSheet_IsBoundToSomething_AndNoKeyIsBoundTwice()
	{
		var seen = new Dictionary<ShortcutChord, string>();
		foreach (var entry in ShortcutCatalog.Sections.SelectMany(s => s.Entries))
		{
			foreach (var chord in entry.Chords)
			{
				var global = AppShortcuts.For(chord.Key, chord.Control, chord.Shift, chord.Alt, inTextInput: false);
				var player = MpvControlsViewModel.ActionFor(chord.Key, chord.Control, chord.Shift, chord.Alt);
				Assert.True(entry.Scope == ShortcutScope.Global ? global is not null : player is not null, $"{entry.Keys}: {chord} is bound");
				Assert.False(global is not null && player is not null, $"{chord} is both a global and a player key");
				Assert.True(seen.TryAdd(chord, entry.Keys) || seen[chord] == entry.Keys, $"{chord} is listed under both {seen.GetValueOrDefault(chord)} and {entry.Keys}");
			}
		}
	}

	[Fact]
	public void TheBarsButtons_HaveKeys()
	{
		// -10 s: J and Ctrl+Left; +30 s: Shift+Right
		Assert.Equal(-MpvControlsViewModel.SkipBackSeconds, -MpvControlsViewModel.LargeSeekSeconds);
		Assert.Equal(MpvControlsAction.SeekBackLong, MpvControlsViewModel.ActionFor(MpvControlsViewModel.Keys.J, false, false, false));
		Assert.Equal(MpvControlsAction.SeekBackLong, MpvControlsViewModel.ActionFor(Left, true, false, false));
		Assert.Equal(MpvControlsAction.SeekForwardFar, MpvControlsViewModel.ActionFor(Right, false, true, false));
		Assert.Equal(MpvControlsAction.SeekBackFar, MpvControlsViewModel.ActionFor(Left, false, true, false));
		Assert.Null(MpvControlsViewModel.ActionFor(Left, true, true, false));
	}

	[Theory]
	[InlineData(0x30, 0)]
	[InlineData(0x35, 5)]
	[InlineData(0x39, 9)]
	[InlineData(0x60, 0)]
	[InlineData(0x67, 7)]
	public void Digits_AreTenthsOfTheVideo(int key, int tenths)
	{
		var action = MpvControlsViewModel.ActionFor(key, false, false, false);
		Assert.NotNull(action);
		Assert.Equal(tenths, MpvControlsViewModel.PercentOf(action.Value));
		Assert.Null(MpvControlsViewModel.ActionFor(key, false, true, false));
		Assert.Null(MpvControlsViewModel.ActionFor(key, true, false, false));
	}

	[Fact]
	public void NoPlayerKey_FiresWithAlt_ButAltEnter()
	{
		for (var key = 0; key < 256; key++)
		{
			if (key != MpvControlsViewModel.Keys.Enter)
			{
				Assert.Null(MpvControlsViewModel.ActionFor(key, false, false, true));
			}
		}

		// 1.2.0 (the interaction audit, item 17): Alt+Enter is full screen; Enter alone and Ctrl/Shift+Alt+Enter are not the player's
		Assert.Equal(MpvControlsAction.ToggleFullScreen, MpvControlsViewModel.ActionFor(MpvControlsViewModel.Keys.Enter, false, false, true));
		Assert.Null(MpvControlsViewModel.ActionFor(MpvControlsViewModel.Keys.Enter, false, false, false));
		Assert.Null(MpvControlsViewModel.ActionFor(MpvControlsViewModel.Keys.Enter, true, false, true));
		Assert.Null(MpvControlsViewModel.ActionFor(MpvControlsViewModel.Keys.Enter, false, true, true));
	}

	/// <summary>1.2.0 (the interaction audit, item 1): Escape leaves full screen and, in a window, only shows the controls: it never leaves the player.</summary>
	[Fact]
	public void Escape_LeavesFullScreenOnly()
	{
		Assert.Equal(PlayerEscape.Action.LeaveFullScreen, PlayerEscape.For(fullScreen: true));
		Assert.Equal(PlayerEscape.Action.ShowControls, PlayerEscape.For(fullScreen: false));
		// button review P27: on the loading screen (nothing plays yet) it is the Cancel again
		Assert.Equal(PlayerEscape.Action.CancelStart, PlayerEscape.For(fullScreen: false, starting: true));
		Assert.Equal(PlayerEscape.Action.CancelStart, PlayerEscape.For(fullScreen: true, starting: true));
		Assert.Equal([PlayerEscape.Action.LeaveFullScreen, PlayerEscape.Action.ShowControls, PlayerEscape.Action.CancelStart], Enum.GetValues<PlayerEscape.Action>());
		var sheet = ShortcutCatalog.Sections.SelectMany(s => s.Entries).Single(e => e.Keys == "Esc");
		Assert.DoesNotContain("leave the player", sheet.Description, StringComparison.OrdinalIgnoreCase);
	}

	// -- what the new player keys do ------------------------------------------------------------------------------------

	private readonly List<(string Command, JsonObject? Args)> _sent = [];

	private MpvControlsViewModel Vm(long durationSeconds = 600, bool tracks = true)
	{
		var vm = new MpvControlsViewModel((c, a) => { _sent.Add((c, a?.DeepClone() as JsonObject)); return Task.CompletedTask; }, () => new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
		vm.OnState(new BridgeState { State = Bridge.States.Playing, DurationTicks = TimeSpan.FromSeconds(durationSeconds).Ticks, AudioStreamIndex = 1, SubtitleStreamIndex = -1 });
		if (tracks)
		{
			vm.OnEvent(new BridgeEvent(Bridge.Events.Tracks, System.Text.Json.JsonSerializer.SerializeToNode(
				new BridgeTracks([new BridgeTrack(1), new BridgeTrack(2)], [new BridgeTrack(3, "eng"), new BridgeTrack(4, "spa")]), Bridge.JsonOptions)));
		}

		return vm;
	}

	private int[] Indices(string command) => [.. _sent.Where(x => x.Command == command).Select(x => (int)x.Args!["index"]!)];

	[Fact]
	public void Digits_SeekToATenth_AndHomeToTheStart()
	{
		var vm = Vm(600);
		Assert.True(vm.HandleKey(0x35));
		Assert.True(vm.HandleKey(MpvControlsViewModel.Keys.Home));

		var seeks = _sent.Where(x => x.Command == Bridge.Commands.Seek).Select(x => (long)x.Args!["positionTicks"]!).ToList();
		Assert.Equal([TimeSpan.FromSeconds(300).Ticks, 0], seeks);
	}

	[Fact]
	public void Digits_WaitForTheLength()
	{
		var vm = Vm(0);
		vm.HandleKey(0x35);
		Assert.Empty(_sent);
	}

	[Fact]
	public void ShiftRight_IsThirtySeconds_AndShiftLeftBack()
	{
		var vm = Vm();
		vm.HandleKey(Right, shift: true);
		vm.HandleKey(Left, shift: true);
		Assert.Equal([30.0, -30.0], _sent.Where(x => x.Command == Bridge.Commands.SeekRelative).Select(x => (double)x.Args!["seconds"]!));
	}

	[Fact]
	public void V_CyclesThroughOffAndEveryTrack_AndBBetweenAudio()
	{
		var vm = Vm();
		vm.HandleKey(MpvControlsViewModel.Keys.V);
		vm.HandleKey(MpvControlsViewModel.Keys.V);
		vm.HandleKey(MpvControlsViewModel.Keys.V);
		vm.HandleKey(MpvControlsViewModel.Keys.V);
		Assert.Equal([3, 4, -1, 3], Indices(Bridge.Commands.SetSubtitle));

		vm.HandleKey(MpvControlsViewModel.Keys.B);
		vm.HandleKey(MpvControlsViewModel.Keys.B);
		Assert.Equal([2, 1], Indices(Bridge.Commands.SetAudio));
	}

	[Fact]
	public void C_TurnsSubtitlesOff_AndBackOnToTheLastOne()
	{
		var vm = Vm();
		vm.HandleKey(MpvControlsViewModel.Keys.C); // off -> the first track
		vm.HandleKey(MpvControlsViewModel.Keys.V); // -> the second
		vm.HandleKey(MpvControlsViewModel.Keys.C); // off
		vm.HandleKey(MpvControlsViewModel.Keys.C); // on: the second again
		Assert.Equal([3, 4, -1, 4], Indices(Bridge.Commands.SetSubtitle));
	}

	[Fact]
	public void TrackKeys_DoNothing_WithoutTracks()
	{
		var vm = Vm(tracks: false);
		Assert.True(vm.HandleKey(MpvControlsViewModel.Keys.V));
		vm.HandleKey(MpvControlsViewModel.Keys.C);
		vm.HandleKey(MpvControlsViewModel.Keys.B);
		Assert.Empty(_sent);
	}

	[Fact]
	public void F11_IsFullScreenToo()
	{
		Assert.Equal(MpvControlsAction.ToggleFullScreen, MpvControlsViewModel.ActionFor(MpvControlsViewModel.Keys.F11, false, false, false));
	}
}
