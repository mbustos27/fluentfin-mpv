# Nixie keyboard and mouse shortcuts

The map below is what the app does (`ui/FluentFin.Core/Shortcuts/AppShortcuts.cs` for the global keys,
`MpvControlsViewModel.ActionFor` for the player's); `ShortcutCatalog` is the list the "?" sheet shows, and
`ShortcutsTests` checks every line of it against the two maps. Change this file, the catalog and the maps together.

## Inventory before this change (Seaglass 1.1.0)

| Where | Keys |
|---|---|
| Player (FluentFin controls) | Space, K; Left/Right 5 s, Ctrl+Left/Right 10 s, J/L 10 s; Up/Down volume; M; F; S; Shift+N/Shift+P; media keys; Esc (leave full screen, then leave); mouse wheel volume; click pauses, double click is full screen |
| Shell | Alt+Left and the Back key: back (ShellPage accelerators; no forward at all) |
| Title bar | Ctrl+F: search |
| Home page | N next up, C continue (hidden accelerators) |
| Built-in player (not the MPV player) | Space, Left/Right 5 s, Ctrl+Left/Right 10 s, Up/Down, S, F (global hook, `KeyboardMediaPlayerController`) |

Gaps and conflicts found: the bar's buttons (-10 s, +30 s) had no matching key (J was -10 but nothing was +30); no
Forward anywhere, no mouse side buttons; no help; no key for subtitles or audio, percent seeks, Settings or Home; plain
letter accelerators on Home had no explicit text-input rule.

## Global keys (every page, not while typing)

| Key | Does |
|---|---|
| Alt+Left, Back key, Mouse 4 | Back (in the player: leaves it, as its Back button does) |
| Alt+Right, Forward key, Mouse 5 | Forward. The player and dialogs are never forward targets |
| Ctrl+F, Ctrl+E, / | Search |
| Alt+Home | Home |
| Ctrl+, | Settings |
| ? (Shift+/), F1 | The shortcut sheet; pressed while it is open, they close it (1.3.1), as Esc does, and the keyboard goes back where it was |
| R, F5, Ctrl+R | Refresh the page from the server (1.3.1): Home, a library, a See all grid, an item, series or season page, search results, the Diary. The page loads again in place (the item the keyboard was on and each row's first item kept) and Narrator says "Refreshed". Not while typing, in a dialog or menu, in the player or over a trailer; nothing on pages without server data (Settings) |

Home page only: N goes to the Next Up row, C to the Continue Watching row (they ignore typing too). Since 1.1.4 they no
longer play anything: a single letter must never start a video (Enter on the focused card does).

## Library grids (1.4.0)

| Key | Does |
|---|---|
| Arrows, Home, End, Page Up, Page Down | Move in the grid (as before) |
| Letters typed on a poster | Go to the first title they start (sort name or name); the same letter again goes on to the next. R is Refresh, not a letter (the A to Z has R) |
| Enter on a letter of the A to Z | That letter's row goes to the top, its first title gets the keyboard |
| Ctrl+Up on a Cinematic still | The stage's Play |

## Player keys (only while the video is shown)

| Key | Does |
|---|---|
| Space, K | Play / pause (also with a button of the bar focused: Enter presses that button) |
| Left, Right | 5 s back / forward |
| J, Ctrl+Left | 10 s back (the bar's -10 s button) |
| L, Ctrl+Right | 10 s forward (the bar's +10 s button; it was +30 s until 1.2.0) |
| Shift+Right | 30 s forward |
| Shift+Left | 30 s back |
| 0 to 9 (also the number pad) | Jump to that tenth of the video; 0 is the start |
| Home | Start |
| S | Skip intro or credits, while offered |
| Shift+N, Shift+P, media keys | Next / previous item (also the bar's Previous and Next buttons) |
| [, ] | Speed a step down / up: 0.5×, 0.75×, normal, 1.25×, 1.5×, 1.75×, 2× (the bar's speed button shows any speed but normal) |
| Backspace | Normal speed |
| Up, Down, mouse wheel | Volume (by Settings' volume step) |
| M | Mute |
| C | Subtitles off, or back on to the last track |
| V | Next subtitle track (off, each track, off ...) |
| B | Next audio track |
| Z, X | Subtitles 0.1 s earlier / later (the subtitles menu has the same, with a reset) |
| F, F11, Alt+Enter | Full screen (Alt+Enter since 1.2.0, the classic Windows players' key) |
| Esc | Close an open panel of the theme (Cinematic's tabs panel), else leave full screen. In a window it only shows the controls: it never stops the video or leaves the player (1.2.0; Back, Alt+Left, the Back key and Mouse 4 leave). On the loading screen it cancels the start, as its Cancel does. |

The mouse: a click on the video pauses or plays at once (1.2.0; 1.1.3 waited the double-click time), a double click is full
screen and puts the play state back to what it was before its first click, the wheel is volume, Mouse 4 / 5 are back /
forward. Every button of the player names its key in its tooltip.

## How the player's keys are routed (1.3.1)

One router takes every key that reaches the window while the video is shown, before the focused control can
(`PlayerKeyRouter` in FluentFin.Core decides, `MpvPlayerKeys` and the player page's root key handler apply it). It works the
same in every layout theme (Classic, Drive-in, Cinematic, Editorial), playing or paused, windowed or full screen: themes
restyle and re-lay out the controls and never change a key (`PlayerKeysContractTests`).

| The keyboard is on | Space | Arrows | Home | End, Page Up/Down | Enter | Every other player key |
|---|---|---|---|---|---|---|
| The video, nothing, the title bar, a control the mouse clicked | play / pause | seek (Left/Right), volume (Up/Down) | the start | not the player's | the focused control's | the player's |
| A button of the bars (Tab put it there, or a menu that closed gave it back) | play / pause | seek, volume | the start | not the player's | presses the button | the player's |
| The seek bar | play / pause | seek, volume | the start | nothing (the bar would move its thumb and seek nowhere) | | the player's |
| A list, tab strip or panel of a theme (Cinematic's player tabs and its panel) | its own (picks) | move within it | its own | its own | picks | the player's |
| An open menu, flyout or dialog (speed, volume, audio and subtitles, the "?" sheet) | its own | its own | its own | its own | its own | its own: Esc closes it, then the player's keys work again |
| A text box | the text's | | | | | |

- **The mouse never takes the keyboard from the player.** The bars' controls, the Skip and Next pills and Cinematic's player
  tabs take no focus from a click (Tab and Narrator still reach each one), and after any click, drag or wheel notch on the
  player the keyboard is the player's again if it was elsewhere (the title bar, nothing, a theme panel's button). Every key
  works at once after a click, while the controls are still showing.
- **A held key** repeats only the seeks, the volume, the speed steps and the subtitle delay; a held Space, K, M, F, F11,
  Alt+Enter, Shift+N, Shift+P, S, C, V, B, Home, a digit or Backspace does its job once.
- **Escape** stays the page's: an open theme panel closes first, then full screen ends; in a window it shows the controls.
- **A dialog over the player** (F1's sheet, a confirmation) has every key (1.4.0: the router sees the open dialog itself,
  `PlayerFocus.Modal`; in 1.3.1 F, M, the digits, K and Shift+N still acted under the sheet). Esc and F1 close the sheet.
- **Not in the key matrix, by design** (`tools\ui-harness\keys-matrix.ps1` checks every other context in all four themes):
  paused with the controls auto-hidden (a paused player always shows its controls; they hide only while playing), and the
  keyboard on Previous on a first episode (disabled there, so it cannot hold the focus: the matrix plays the second episode).
- Before 1.3.1 the player listened on its own grid and a focused button kept Space and Enter: after the volume, speed or
  tracks menu closed (by a click or Esc), Space opened it again; with the focus on Full screen Space left full screen; with
  the focus in the title bar no player key worked until the controls hid. While paused the controls never hide, so it stayed.

## Click and double click (1.3.1)

A click on the video plays or pauses at once. A double click on the video toggles full screen and leaves the play state as it
was before it: the second click undoes the first one's pause or play (the picture holds for the moment between the two clicks,
as on YouTube and in MPC-HC). The system's double-click time decides what is a double click. The bars' scrims, where they only
fade into the picture (the bottom bar's top edge, the top bar's bottom edge), are the video: in 1.3.0 a double click there did
nothing, and a double click whose first click showed the bars under the pointer neither paused nor went full screen. A theme's
solid console (Drive-in's) is a control. A double click on a control is two clicks on it (a button pressed twice, the seek bar
seeked twice) and never toggles full screen. The keys work straight after either.

## Rules

- Every key does one thing; the global and player maps never share a key (tested).
- No shortcut fires while the focus is in a text box, password box, search box, number box or editable combo box. The
  mouse buttons are not keys and always work. A dialog or menu being open also stops them (Esc and the open control's own
  keys are theirs).
- No CapsLock or Insert chords (Narrator's keys); no Windows-reserved chords (Win, Alt+Tab, Alt+F4, Ctrl+Shift+Esc).
- Back and Forward use the page history of the window's frame. Back from the player drops the player from the forward
  history, a player page replacing another keeps one entry, and Forward never starts playback.

## Considered and left out

- Frame step (`,` `.`) and End: the bridge has no command for them (`docs/BRIDGE.md` §4). Speed and subtitle delay got
  theirs in 1.2.0 (`set_speed`, `set_subtitle_delay`), with mpv's own keys (`[` `]` Backspace, `z` `x`): with mpv's
  controls mpv's default bindings answer the same keys (its `[` and `]` change the speed by 10 % rather than a step).
  `<` `>` (YouTube's) were the other candidate; they are Shift chords on most layouts, and `[` `]` match mpv.
  `[` and `]` are the keys of a US layout (VK_OEM_4, VK_OEM_6): on others the key in that place.
- Backspace as Back: too close to text editing and it would add nothing to Alt+Left and Mouse 4.
- F11 outside the player: a full-screen window without the title bar has no use on the browsing pages.
- The built-in player's own global key hook keeps its keys as they are (Space, arrows, S, F, Up/Down); it is not the player
  Nixie ships with.
