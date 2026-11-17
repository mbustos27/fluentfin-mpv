# Seaglass keyboard and mouse shortcuts

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
| ? (Shift+/), F1 | The shortcut sheet |

Home page only: N next up, C continue watching (they ignore typing too).

## Player keys (only while the video is shown)

| Key | Does |
|---|---|
| Space, K | Play / pause |
| Left, Right | 5 s back / forward |
| J, Ctrl+Left | 10 s back (the bar's -10 s button) |
| L, Ctrl+Right | 10 s forward |
| Shift+Right | 30 s forward (the bar's +30 s button) |
| Shift+Left | 30 s back |
| 0 to 9 (also the number pad) | Jump to that tenth of the video; 0 is the start |
| Home | Start |
| S | Skip intro or credits, while offered |
| Shift+N, Shift+P, media keys | Next / previous item |
| Up, Down, mouse wheel | Volume (by Settings' volume step) |
| M | Mute |
| C | Subtitles off, or back on to the last track |
| V | Next subtitle track (off, each track, off ...) |
| B | Next audio track |
| F, F11 | Full screen |
| Esc | Leave full screen, then leave the player |

The mouse: a click on the video pauses, a double click is full screen, the wheel is volume, Mouse 4 / 5 are back / forward.

## Rules

- Every key does one thing; the global and player maps never share a key (tested).
- No shortcut fires while the focus is in a text box, password box, search box, number box or editable combo box. The
  mouse buttons are not keys and always work. A dialog or menu being open also stops them (Esc and the open control's own
  keys are theirs).
- No CapsLock or Insert chords (Narrator's keys); no Windows-reserved chords (Win, Alt+Tab, Alt+F4, Ctrl+Shift+Esc).
- Back and Forward use the page history of the window's frame. Back from the player drops the player from the forward
  history, a player page replacing another keeps one entry, and Forward never starts playback.

## Considered and left out

- Speed (`<` `>`, `[` `]`), frame step (`,` `.`), subtitle delay (z/x), End: the bridge has no command for them
  (`docs/BRIDGE.md` §4), so they would need a protocol change on both sides. Not done here.
- F5 refresh: no page has a refresh to call.
- Backspace as Back: too close to text editing and it would add nothing to Alt+Left and Mouse 4.
- F11 outside the player: a full-screen window without the title bar has no use on the browsing pages.
- The built-in player's own global key hook keeps its keys as they are (Space, arrows, S, F, Up/Down); it is not the player
  Seaglass ships with.
