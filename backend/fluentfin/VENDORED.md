# Vendored: uosc 5.13.0

FluentFin MPV's playback bar in bridge mode (`docs/bridge.md` section 4a) is
[uosc](https://github.com/tomasklaen/uosc), pinned. It is never self-updated: upstream
changes options and behaviour between minor versions, so a new version is a deliberate
re-vendor with this file updated.

| | |
|---|---|
| Release | https://github.com/tomasklaen/uosc/releases/tag/5.13.0 (published 2026-08-03) |
| Archive | https://github.com/tomasklaen/uosc/releases/download/5.13.0/uosc.zip |
| Archive size | 8,163,987 bytes |
| Archive SHA-256 | `4be9da3289285300fa374496c3f1bfd7bb20ac08e890d25bd5a06b28eebe4882` (equals the digest GitHub publishes for the asset) |
| Licence | LGPL-2.1, `mpv/scripts/uosc/LICENSE.LGPL`, from https://raw.githubusercontent.com/tomasklaen/uosc/5.13.0/LICENSE.LGPL (26,530 bytes, SHA-256 `dc626520dcd53a22f727af3ee42c770e56c97a64fe3adb063799d8ab032fe551`); the release archive carries no licence file |
| Downloaded | 2026-11-15, into an empty scratch folder; nothing from the archive was executed |

## What is vendored

From the archive, unchanged except for the patches below. Every vendored file is listed in
`uosc-files.txt` beside this file: the bridge checks each is there before it picks uosc, and a
test keeps the list equal to the folder.

- `scripts/uosc/main.lua`, `scripts/uosc/elements/*.lua` (but `Updater.lua`, below),
  `scripts/uosc/lib/*.lua` -> `mpv/scripts/uosc/`
- `fonts/uosc_icons.otf`, `fonts/uosc_textures.ttf` -> `mpv/fonts/` (mpv's
  `osd-fonts-dir` points there in bridge mode). `uosc_icons.otf` is Google's Material
  Symbols (Apache-2.0); the release ships no licence for either font, so the licences are in
  `THIRD-PARTY.md` and `LICENSE.Apache-2.0` beside this file.

Two files carry code under other licences, as upstream ships them: `lib/fzy.lua` (MIT,
notice in the file) and a Windows FFI block in `lib/utils.lua` marked as taken from
thumbfast (MPL-2.0).

## What is not vendored, and why

| Not vendored | Why |
|---|---|
| `scripts/uosc/bin/ziggy-*` (darwin, linux, windows.exe) | Executables. uosc calls them only for its self-updater, subtitle downloads and clipboard paths, none of which FluentFin's bar exposes; without them those paths log an error |
| `scripts/uosc/intl/*.json` | Translations. The bar is configured with `languages=en`; a missing locale file is read as "no translation" |
| `scripts/uosc/char-conv/zh.json` | Chinese romanisation for menu search; loaded only for a `zh` language |
| `script-opts/uosc.conf` | Defaults for reference only; FluentFin's options are sent as `script-opts` (`jellyfin_mpv_shim/fluentfin_bar.py`, `uosc_script_opts`); a `script-opts/uosc.conf` of the user's in the config folder replaces them |
| `scripts/uosc/elements/Updater.lua` | uosc's self-updater: it ran PowerShell to fetch and run an installer (`irm .../installers/windows.ps1 \| iex`). uosc is pinned and re-vendored by hand; `main.lua`'s `update` binding and its menu entry are patched out (below) |
| upstream thumbfast | Never installed: the shim's own `thumbfast.lua` answers the same messages with Jellyfin's trickplay tiles. A second one would take the script name and decode the authenticated stream in a background mpv |

## Local patches

Each is marked `FluentFin MPV` in the file.

| File | Change | Why |
|---|---|---|
| `scripts/uosc/main.lua`, `bind_command('update', ...)` and the "Update uosc" menu entry | `update` logs that uosc is pinned and does nothing; the menu entry is gone | The self-updater is not vendored (above) |
| `scripts/uosc/main.lua`, `update_margins` | Publishes `user-data/fluentfin/bar`: the bottom bar's share of the window height as shown now (proximity reveals included; uosc's own margins count only a persistent bar), 0 when hidden | `fluentfin.lua` raises the subtitles above the bar from it, and takes its first value as "uosc is running" |

## Not uosc's

`mpv/scripts/fluentfin.lua` is FluentFin MPV's own (not part of the vendored copy): the key
map, the Skip prompt and the glue between the shim and uosc (`docs/bridge.md` §4a).
