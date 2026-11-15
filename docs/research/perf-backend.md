# Backend (jellyfin-mpv-shim) performance scout — bridge mode

Scope: the Python half as bridge mode will use it — headless (no tray, no mpvtk
browser, no websocket, no Discord), a Jellyfin client built from a token, playback
through `event_handler.start_playback` → `playerManager`, external mpv through
python-mpv-jsonipc. Everything was measured on the **pristine upstream copy**
(a3a9a31, identical to `backend/` at 4dc492e), never in the repo. File:line
references are upstream paths; the same paths apply under `backend/`.

`S` below = `%TEMP%\claude\4cb39b71e0149222\e4cf1356-33e7-412e-b62a-03eff8dc99f0\scratchpad`
(this session's scratchpad). Raw outputs: `S\perf-backend\`. Benchmarks: `S\perf-backend\bench\`.

**Headline.** Steady-state playback costs the Python side ~0.2 % of one core and is
not worth touching. The user-visible costs are all on *starts*: a new HTTPS
connection costs **~186 ms of CPU** on this machine (urllib3 re-parses certifi per
connection; 2 ms with a preloaded context), the first play after an idle-quit costs
**~200 ms** more than a warm one, three API round trips run **in series** before mpv
gets the URL, and starts whose stream never reports a duration (Live TV, live HLS)
or that fail to load wait **~250 ms on average (up to 500 ms)** for a 0.5 s poll.

---

## 1. Baseline

### Environment

| | |
|---|---|
| OS / HW | Windows 11 Pro 10.0.26200; Ryzen 7 7800X3D (16 threads); RX 9070 XT |
| Python | 3.14 (`py -3.14 -m venv S\perf-backend\.venv`; `pip install -e "<upstream>[all]"`) |
| Packages | python-mpv-jsonipc 1.5.0, python-mpv 1.0.8, jellyfin-apiclient-python 1.21.0, requests 2.34.2, urllib3 2.x, Pillow 13.0.0; mypy 2.4.1 (added to the venv for the gate) |
| mpv | `mpv.exe` v0.41.0-dev-g41f6a6450 (winget mpv-CI MSVC, built Dec 21 2025). **No libmpv on this PC** → `player.py:76-100` falls back to the **external (jsonipc) backend**. Every number below is that backend. |
| Display | No xvfb on Windows. Every run that imports `player.py` (it opens a real mpv) ran on a **private off-screen desktop** (`CreateDesktopW` + a kill-on-close job object, `bench\hidden_run.py`): nothing reached the user's desktop, and every mpv a run started died with it. mpv's d3d11 VO initialises normally there (`vo/gpu-next/d3d11 ... AMD Radeon RX 9070 XT`). |
| Isolation | `APPDATA`/`LOCALAPPDATA` → scratch dirs for every run, so neither the shim's config (`%APPDATA%\jellyfin-mpv-shim`) nor mpv's (`%APPDATA%\mpv`) on this machine was read or written. The mpv dir is prepended to `PATH` (this session's PATH predates the winget install). |

### Unit suite — parallel runner (the fast gate)

```
S\perf-backend\.venv\Scripts\python.exe S\perf-backend\bench\run_suite.py ^
    --root S\upstream\jellyfin-mpv-shim --log S\perf-backend\baseline-unit.log
# = off-screen, sandboxed env:  python tools\run_tests_parallel.py   (cwd = shim root)
```

| | |
|---|---|
| Modules / tests | 281 / **7189** |
| Result | **2 modules fail, 8 tests** (5 FAIL + 3 ERROR) — all pre-existing on Windows, listed below |
| Workers / wall | 8 (default: half the CPUs) / **267.5 s** |
| Slowest | test_shell_playback 267.5 s (= the whole wall), test_shell_library 101.4 s, test_shell_books 100.2 s, test_dpi_matrix 85.0 s, test_playlist_edit 80.0 s |
| mypy gate | the runner skips it on Windows (`os.name == "nt"`); run separately below |
| Log / ids | `S\perf-backend\baseline-unit.log`, `S\perf-backend\baseline-failures.txt` |

### Unit suite — serial `discover` (the cross-module gate; docs/testing.md §9 says this is the one that must pass)

```
S\perf-backend\.venv\Scripts\python.exe S\perf-backend\bench\run_suite.py --serial ^
    --root S\upstream\jellyfin-mpv-shim --log S\perf-backend\baseline-unit-serial.log
# = off-screen, sandboxed env:  python -m unittest discover tests
```

| Tests | Passed | Failed | Errors | Skipped | Wall |
|---|---|---|---|---|---|
| **7189** | **7127** | **5** | **3** | **54** | **1267 s** (21 min; `Ran 7189 tests in 1267.381s`) |

The serial run fails on **exactly the same 8 tests** as the parallel one — no
cross-module interference on this box. The 54 skips' reasons were not collected
(§4, `list_skips.py`); compare the count. (Part of the serial run overlapped a first
benchmark pass; the wall time is the shape of the wait, not a benchmark.)

**Pre-existing on Windows — not regressions.** A future run is clean if it fails on
this list and nothing else:

| Test id | Kind | One-line reason |
|---|---|---|
| `test_mpvtk_pilfont.TestCjkCoverage.test_the_notdef_probes_agree_on_every_installed_candidate` | ERROR | Pillow `getmask()` raises `OSError: invalid reference` on an installed Windows font candidate (host fonts; Pillow wheel without raqm/FriBiDi) — environment |
| `test_mpvtk_pilfont.TestTheHostsOwnInventory.test_every_script_this_host_can_draw_is_drawn` | ERROR | same `OSError` from `getmask()` in `_covers()` — environment |
| `test_audio_settings.ApplyAudioSettingsTest.test_night_mode_in_auto_restores_the_users_mpv_conf` | ERROR | `KeyError: 'audio_channels'`: `player_audio._mpv_property` (`player_audio.py:156-166`) takes the `command("get_property")` branch because `player.is_using_ext_mpv` is True here; the test's `FakePlayer` models only the libmpv `_get_property` path — backend (no libmpv) |
| `test_audio_settings.ApplyAudioSettingsTest.test_default_still_works_on_the_run_after_one_was_chosen` | FAIL | same branch: `audio_device` not restored to `auto` |
| `test_audio_settings.ApplyAudioSettingsTest.test_going_back_to_default_restores_what_was_there` | FAIL | same branch |
| `test_audio_settings.ApplyAudioSettingsTest.test_no_pointless_af_remove_for_an_unattached_filter` | FAIL | same branch: the attached-filter read is None ("don't know"), so the removes are sent |
| `test_audio_settings.ApplyAudioSettingsTest.test_returning_to_auto_restores_rather_than_defaults` | FAIL | same branch |
| `test_audio_settings.ApplyAudioSettingsTest.test_returning_to_auto_undoes_what_we_applied` | FAIL | same branch |

The 6 audio failures will disappear on a machine (or a frozen build) that loads
libmpv — so a libmpv run is not comparable to this list for that module.

**Flake risk:** `test_shell_playback.py` took 267.5 s against the parallel runner's
300 s per-module `--timeout`. Under extra load it can be killed and reported as a
failure that is not one; re-run with `--timeout 600` before believing it.

**Not run:** the integration matrix (`tests/integration/run_integration.py`, ~10 min
on Linux, drives real mpv through the keyboard) and `tests/e2e/` (real server). Any
change on the player path (opportunities O4, O5, O9 below) should add the
integration matrix to its gate; it was not baselined here.

### mypy gate

`tools/mypy_gate.sh` **cannot pass on Windows as written**: mypy prints
`jellyfin_mpv_shim\x.py` and the baseline holds `jellyfin_mpv_shim/x.py`, so every
finding reads as new (exit 1). Run with the paths normalised (`\` → `/`, otherwise
the gate's own `normalise`):

```
cd S\upstream\jellyfin-mpv-shim
S\perf-backend\.venv\Scripts\mypy jellyfin_mpv_shim/      # then normalise and diff vs tools/mypy-baseline.txt
```

| | |
|---|---|
| mypy | 2.4.1 — `Found 98 errors in 25 files (checked 176 source files)` → 85 unique normalised findings; baseline holds 84 |
| New vs baseline | **2, both Windows-only branches**: `player.py: Incompatible import of "win_utils"` (the `sys.platform == "win32"` block is only analysed on Windows) and `win_utils.py: Library stubs not installed for "win32gui"` |
| Baseline not seen | 1: `conf.py: Argument 1 to "loads" has incompatible type "Any \| None"` (mypy version/platform difference) |
| Files | `S\perf-backend\baseline-mypy-raw.log`, `S\perf-backend\baseline-mypy.txt` |

So: on Windows the gate's baseline is "these 2 extra, that 1 missing"; anything
else new is a real finding.

---

## 2. Measurements

All from the clean pass `S\perf-backend\results\run2-idle\`, produced by
`bench\run_all.py`. Imports, mpv start and the three local player runs ran on an
idle machine; the last three pieces (`player-wan50`, `player-notrick`,
`wait_property`, 00:12-00:13) overlapped a unit-suite run another agent started in
the repo, and agree with the first pass anyway. The first pass, taken while my
serial suite was running (`results\run1-during-serial-suite\`), agrees within noise
and is the run-to-run variance reference. Medians unless noted.

### 2.1 Import cost of what bridge mode loads (`bench_import.py`, `-X importtime`, 5 runs)

| Import (fresh interpreter, `--config <scratch>`) | Wall ms | Notes |
|---|---|---|
| `jellyfin_mpv_shim.conf` | 44 | |
| `jellyfin_mpv_shim.media` | 156 | pulls `requests` (≈100 ms of it: requests + urllib3) |
| `jellyfin_mpv_shim.clients` | 181 | + jellyfin_apiclient_python, websocket-client |
| `jellyfin_mpv_shim.player` | 420 | **of which 159 ms is the module's own body = constructing `PlayerManager` (spawning mpv)**; imports ≈ 261 ms |
| **bridge set**: player + event_handler + timeline + action_thread | **424** | event_handler/timeline/action_thread add ~6 ms over player |
| `sync.manager` (imported lazily by the first progress report) | 182 standalone | almost all shared deps; its own share shows up as ~34 ms on the first play (2.5) |
| for comparison — `mpv_shim` module (full app entry) | 64 | |
| NOT needed — `mpvtk_browser.ui` | 204 | mostly shared deps (clients, requests) |
| NOT needed — `tray` | 160 | pystray itself is not imported at module load |

Pillow (`PIL.Image`, ~9-12 ms) comes in through `trickplay`/`bifdecode`. Nothing
heavy and unneeded is pulled by the bridge set: the mpvtk browser, pystray,
pypresence and tkinter are absent from `sys.modules` after it.

### 2.2 mpv.exe startup, external backend (`bench_mpv_start.py`, 9 runs, interleaved)

Variants are built by the shim's own `mpv_options.build_mpv_options` + `mpv_scripts`
and turned into argv exactly as jsonipc's `MPVProcess` does. Source:
`av://lavfi:testsrc=size=1280x720:rate=30` (plays; no disk/network).

| Variant | spawn → pipe | → first IPC reply | loadfile → file-loaded | loadfile → first frame | spawn → first frame |
|---|---|---|---|---|---|
| shim, mpvtk style (as built: mouse.lua + thumbfast.lua, `osc=no`, `force-window`) | 18.1 | 18.3 | 63.8 | 77.3 | 95.7 |
| shim, no scripts | 17.5 | 17.7 | 65.4 | 78.9 | 97.0 |
| shim, no force-window | 18.4 | 18.6 | 63.1 | 76.5 | 94.9 |
| shim, `mpv` style (+ `load-script @osc.lua`) | 18.7 | 19.0 | 65.3 | 78.5 | 97.2 |
| bare `--no-config` (reference only; the shim never runs this) | 17.5 | 17.8 | 64.1 | 151.2 | 167.9 |

**The shim's always-on options (lua scripts, thumbfast, OSC choice, force-window)
cost nothing measurable** (all within ±2 ms). mpv's own pipe is up in ~18 ms — but
python-mpv-jsonipc's `MPVProcess` takes **105 ms** to notice (2.4, O7).

### 2.3 IPC cost (`bench_mpv_start.py`, 300 reads each)

| | median | p95 |
|---|---|---|
| raw pipe `get_property` round trip | 0.022 ms | 0.046 ms |
| through python-mpv-jsonipc (`m.volume`) | 0.039 ms | 0.073 ms |
| jsonipc `set` | 0.037 ms | |
| jsonipc attach (`property-list` 942 names + `command-list`) | 2.2 ms | |
| `playback-time` **observer** notifications | **30 /s** at 30 fps (one per frame) | |

An idle-mpv property read is ~0.04 ms. Reads only get slow when mpv's core is busy
(2.5: up to 31 ms while it opens a file) — that is mpv's time, not the IPC's.

### 2.4 Player construction (`bench_player.py`, 3 runs)

| | |
|---|---|
| `import player` + `PlayerManager()` (conf already imported) | 340 ms |
| of which `MPVProcess` spawn → pipe | **105 ms** (mpv's pipe is ready at ~18 ms) |
| IPC during `_init_mpv` | 61 commands, 9.0 ms total (property/command lists, 16 `_snapshot_render_pristine` reads, ~15 keybinds, 11 observers, `input-bindings`) |
| event_handler + timeline + action_thread imports | 6.5 ms |
| client from token (`credential_client` pattern, `websocket=False`) | 0.5 ms |

### 2.5 The play path (`bench_player.py` against `fake_jellyfin.py` in its own process)

`start_playback(client, [item])` on a 3-min 720p H.264 direct stream from a local
stand-in server (every request logged with a timestamp, so counts and order are
measured, not inferred). Times from the call.

| Scenario | → loadfile | → file-loaded | → **first frame** (`playback-restart`) | → `start_playback` returns |
|---|---|---|---|---|
| first play after backend start (cold HTTP session, fresh mpv) | 30.6 | 37.1 | **82.5** | 125.1 |
| second play, warm (the common case) | 5.3 | 13.2 | **41.5** | 49.5 |
| play after an idle-quit (`mpv_idle_quit`, default on, 300 s) | — | — | **240.4** | 252.7 |
| first play, **+50 ms per API request** (WAN stand-in; the stream itself is not delayed) | ~182 | 191.9 | **232.6** | 373.7 |
| second play, +50 ms per API request | — | 178.3 | 207.1 | 314.6 |
| first play, trickplay off | — | — | 82.3 | 124.2 |

What is in those numbers:

- **HTTP before mpv gets the URL: 3 requests, strictly sequential** —
  `GET /Users/{u}/Items/{id}` (`media.py:262`), `POST /Items/{id}/PlaybackInfo`
  (`media.py:1014`), `GET /MediaSegments/{id}` (`media.py:1037` → `get_intro`).
  At +50 ms each, loadfile moves from 30 ms to ~182 ms; the item GET took 77 ms of
  it because it is also the session's first request (cold: 15-26 ms client-side
  locally vs 0.7 ms warm, 2.9).
- Then mpv's own stream GET, and — after the first frame — `POST /Sessions/Playing`
  (reporter thread), `POST /Sessions/Playing/Progress` (on the **calling** thread,
  `event_handler.py:121`) and 2 trickplay tile GETs (worker thread).
  **7 requests in total**, every run.
- **Not exercised here** (code reading): for a server whose name resolves to a
  **public IPv4** address, every `Media()` also does
  `requests.get("https://checkip.amazonaws.com/", timeout=(3, 10))`
  (`utils.py:135`, called at `media.py:1213`) — after the item GET and before
  PlaybackInfo, on every start and every queue advance. For IPv6 it is a
  `GET /System/Endpoint` instead. Not measured live because it contacts a third
  party; its local CPU part is measured in 2.9.
- **IPC during a start:** 71 commands cold / 82 warm (~0.1 ms each) — except three
  writes that wait for mpv while it opens the file: `force-media-title` 8 ms,
  `keep-open` 14-31 ms, `audio` 1.5 ms. That wait overlaps mpv's own decode/VO
  work (first frame lands right after), so it is not added latency.
- **The first play's tail** (first frame → return: 43 ms cold vs 8 ms warm): the
  trailing progress post (`event_handler.py:121`) waits on `_tl_lock` behind the
  session-open, then `_record_progress` (`player_reporting.py:507-521`) re-reads
  the timeline options and does `from .sync.manager import syncManager` — the
  ~33 ms gap with no IPC or HTTP after it is that first import. One-time,
  after the first frame. Trickplay is **not** the cause (trickplay-off: 124 vs 125 ms).
- After an idle-quit the start re-spawns and re-configures mpv: 137 IPC commands,
  **+199 ms** to first frame over a warm start.

### 2.6 Steady state while playing (`bench_player.py`, 60 s × 3 runs)

| | |
|---|---|
| Python process CPU | **0.18 / 0.23 / 0.21 % of one core** |
| mpv CPU (off-screen desktop; frames not composited, so a floor) | 3.1 / 1.6 / 1.7 % |
| IPC | **12.7 commands/s**, 99.9 % from the timeline and action threads: `playback-time` 3.4/s, `playback-abort` 3.4/s, `pause` 2.4/s, `eof-reached` 0.9/s, then volume/mute/duration/cache/fullscreen/chapter-list/demuxer-cache-state |
| HTTP | `POST /Sessions/Playing/Progress` **every 5 s** (12 per 60 s), nothing else once trickplay's window is loaded |
| `playerManager.update()` (action thread, every 1 s) | 0.88 ms median, max 1.7 ms |
| `send_timeline()` (timeline thread, every 5 s; 6 reads + 1 POST) | 1.9 ms median |
| `push_playstate()` (with a hook set, as the bridge will) | 0.77 ms, 0.23/s |
| mpv events delivered | ~0 property changes/s (no observer on a per-frame property) |

Per-tick work in detail: `action_thread.py:51` wakes every 1 s and runs
`update()` (`player.py:2669`): the queued-task drain, `_pump_trickplay` (lock +
early return after the first fire), the segment check (2 `playback_time` reads),
`is_paused` (2 reads), and the EOF poll rescue (`eof_reached`, plus the stall check's
`pause`/`playback_time`). `timeline.py:89` wakes every 5 s: `send_timeline`
(7 reads + 1 POST, `player_reporting.py:426-469`), `push_playstate` (~10 reads),
`_maybe_save_volume` (1 read; writes `conf.json` only when the volume changed).
`session_reporter.py:58` wakes every 0.5 s even when idle (a `Queue.get(timeout=0.5)`;
negligible). **No logging, regex compilation, deep copies or busy waits in these
loops**; regexes are module-level (`player.py:377-436`).

### 2.7 Seek and stop

| | |
|---|---|
| `seek(+60)` returns / → `playback-restart` | 18.6 / 17.7 ms (local file over HTTP; mpv-bound) |
| `stop()` returns | 20 ms; the stop report is queued on the reporter (FIFO, off the caller) |

### 2.8 Trickplay

Lazy and windowed already: nothing is fetched until playback is live
(`_pump_trickplay`, `player.py:2625-2667`); the window is capped at 25 MB
(`trickplay.WINDOW_BUDGET_BYTES`); for the 3-min item it was 2 tile GETs and one
`bifdecode.decompress_tiles` of **77 ms on the trickplay worker thread**. The item
DTO's `Trickplay` manifest is used as delivered (10.9+ servers include it on the
single-item endpoint); `get_bif`'s re-fetch with `fields=Trickplay`
(`media.py:908`) only fires on servers that omit it. The chapter-image fallback
(`trickplay.py:521-552`, no manifest) fetches one image per chapter **serially** —
after playback is live, background only.

### 2.9 HTTP client costs (`bench_player.py`, `first_request.py`, `bench_tls_connect.py`)

| | |
|---|---|
| warm request, local (`session_progress` POST / `get_item`) | 0.6 / 0.7 ms |
| first request on a fresh `JellyfinClient` session, local HTTP | 15-26 ms (20-24 ms when also first in the process; 3-12 ms for a second client) |
| **new HTTPS connection, `requests` default (`verify=True`, certifi)** | **186 ms median** (168-212), n=10 |
| new HTTPS connection, `verify=False` | 30 ms |
| **new HTTPS connection, Session with a pre-loaded `SSLContext`** | **2.1 ms** |
| `ssl.create_default_context(cafile=certifi.where())` alone | 190 ms (Windows store instead: 14 ms) |

The 186 ms is urllib3 2.x building an `SSLContext` and parsing the whole certifi
bundle **for every new connection** (`urllib3/util/ssl_.py` `ssl_wrap_socket`:
`create_urllib3_context` + `load_verify_locations(ca_certs)`, with `ca_certs` set by
`requests/adapters.py:307-339` `cert_verify`). With OpenSSL 3 on this box that parse
is ~180 ms of CPU. It applies to: the first API request after `connect`; the first
request after the server or proxy closes an idle keep-alive connection (nginx
default 75 s, Kestrel 130 s — i.e. the first play after browsing for a couple of
minutes); and **every** `is_local_domain` call to checkip.amazonaws.com
(module-level `requests.get`, a new connection each time). mpv's own HTTPS stream
is a separate TLS stack and is not affected.

### 2.10 `wait_property` notices `satisfied_by` / `abort` late (`bench_wait_property.py`, 20 trials each)

| Event set (what sets it in the shim) | median | mean | p90 | max |
|---|---|---|---|---|
| `satisfied_by` (`_on_file_loaded` → `_load_completed`) | 376 ms | 284 ms | 449 ms | 501 ms |
| `abort` (`_on_end_file` error → `_load_failed`) | 319 ms | 258 ms | 439 ms | 485 ms |

(First pass: 432/352 and 193/214 — the distribution is uniform on [0, 500 ms].)
`mpv_events.py:141-158`: both Events are only looked at by the poll thread between
`stop_poll.wait(POLL_INTERVAL_SECS)` (0.5 s, `mpv_events.py:23`). A normal file is
unaffected (its `duration` observer fires right after `file-loaded`; measured
0.6 ms from file-loaded to the start continuing). It bites every start whose stream
never reports a duration — Live TV, live HLS, a `.strm` to a live origin — and
every failed load (the error appears up to 0.5 s late). A local "live" TS stand-in
did not reproduce it end to end (mpv still estimated a duration), so the number is
from the function itself with a fake mpv.

---

## 3. Opportunities

Ranked by expected user-visible gain ÷ risk. "Bridge" items are choices for the
bridge author with zero change to upstream code; "Code" items change `backend/`.

### O1 — Code: pre-load one `SSLContext` for the API session (and for the checkip call)

- **Where:** the session is built by `jellyfin_apiclient_python/http.py:33-47`
  (`start_session` mounts `HTTPAdapter(max_retries=...)`); in the shim, the client is
  made in `clients.py:415-430` (`client_factory`) and started like
  `credential_client` (`clients.py:446-457`), which is the pattern bridge `connect`
  will follow. The checkip request: `utils.py:135`.
- **Change:** mount an `HTTPAdapter` subclass on `https://` that (a) passes a
  process-wide `ssl.create_default_context(cafile=certifi.where())` through
  `init_poolmanager(ssl_context=...)`, and (b) in `cert_verify`, after `super()`,
  clears `conn.ca_certs` **only when `verify is True`**. Both halves are needed:
  with (a) alone requests still sets `ca_certs` and urllib3 re-parses the bundle
  into the shared context (measured: still 198 ms). Leave every other TLS path on
  the stock code: `verify=False` (`ignore_ssl_cert`), a CA path (`tls_server_ca`),
  client certs (`tls_client_cert`). Give `is_local_domain` a module-level session
  with the same adapter.
- **Gain (measured):** 186 ms → 2.1 ms per new HTTPS connection: on the first play
  after connect, on the first play after any idle gap longer than the server's
  keep-alive, and on every start/advance against a public-IPv4 server (checkip).
  Remote HTTPS servers only; LAN `http://` servers gain nothing.
- **Risk:** this is the certificate-verification path. The policy must be
  bit-for-bit the same (CERT_REQUIRED, hostname check, certifi bundle); a mistake
  here is a security bug, not a slow path. An `SSLContext` shared across threads is
  supported. Belongs upstream in jellyfin-apiclient-python if possible (same
  maintainer as the shim); shim-side it is a post-`start()` re-mount that a later
  `start_session()` would silently drop (perf only).
- **Tests today:** `tests/test_credential_client.py` (session start), nothing on TLS.
- **Prove it:** new unit test with a local TLS server: a self-signed cert is still
  **rejected** with the adapter (verification on), a cert signed by a test CA passed
  via `verify=<path>` is accepted (CA path unchanged), `verify=False` unchanged,
  and the adapter is mounted only for `verify is True`. Benchmark:
  `bench\bench_tls_connect.py` (before/after column 1 vs 3).

### O2 — Bridge: don't idle-quit mpv in embedded mode (or make it much longer)

- **Where:** `conf.py:154-155` (`mpv_idle_quit: True`, `mpv_idle_quit_secs: 300`),
  `timeline.py:81-86`, `player.py:5281` (`idle_quit`).
- **Change:** bridge sets `mpv_idle_quit: false` in its `conf.json` (or a long
  timeout). FluentFin users browse for minutes between plays, so with the default
  most plays would be "after idle-quit" starts.
- **Gain (measured):** first frame 240 ms vs 41.5 ms warm — **+199 ms on every
  start after 5 idle minutes** (re-spawn 105 ms + re-configure, 137 IPC commands).
  It also removes a re-attach of mpv to FluentFin's `hwnd` from every such start.
- **Risk to functionality:** none to features; it is resource management. The cost
  is an idle mpv kept alive (~76-81 MB working set measured here, plus its GPU
  context). If memory matters, keep idle-quit but pre-warm (re-open) when the UI
  shows an item page — that needs a protocol hint.
- **Tests:** `tests/integration/test_mpv_lifecycle.py`, `_idle_reopen_child.py`
  cover idle-quit itself; nothing to change upstream. Benchmark: `bench_player.py`
  `play3_after_idle_quit_*` vs `play2_*`.

### O3 — Bridge: warm the API connection during `connect`

- **Change:** in `connect`, validate the token with one cheap GET through the same
  `JellyfinClient` (e.g. `Users/{id}` or `System/Info`) — this is also the honest
  way to return BRIDGE.md's `auth_failed` / `server_unreachable`. The first `play`
  then reuses the pooled connection.
- **Gain (measured):** 15-26 ms off the first play on a LAN (cold session, 2.9); on
  a remote HTTPS server, the TCP + TLS handshake (1-2 RTT) and — without O1 —
  186 ms of certificate parsing move from the first play to connect.
- **Risk:** none (one GET). Requires `client.start(..., keep_alive=True)` (the
  default): with `keep_alive=False` the apiclient closes the session after every
  request (`http.py:157`) and every request pays a new connection.

### O4 — Code: let `wait_property` see `satisfied_by` / `abort` promptly

- **Where:** `mpv_events.py:141-158` (poller), `:23` (`POLL_INTERVAL_SECS = 0.5`);
  caller `player.py:3341-3349`.
- **Change:** keep the 0.5 s *property* poll exactly as is, but wake the poll
  thread on a short tick (e.g. 20 ms) to check the two Events, reading the property
  only once per `POLL_INTERVAL_SECS`. (Alternative: a `threading.Condition` the
  two handlers notify.)
- **Gain (measured):** mean ~250-285 ms (max 500 ms) off every start of a stream
  that never reports a duration (Live TV, live HLS, live `.strm`), and the same off
  the time to show a load error. Zero for ordinary files.
- **Risk:** low. `docs/mpv-backends.md` §9 says the poll must not be simplified
  away — this keeps it and its cadence; it only stops the two Events from waiting
  behind it. The extra wakeups (50/s) exist only during a start, bounded by
  `playback_timeout`.
- **Tests today:** `tests/test_wait_property.py` (`WaitPropertyAbortTest`,
  `WaitPropertySatisfiedByTest`, `WaitPropertyPollFallbackTest`) — but they patch
  `POLL_INTERVAL_SECS` to 0.01, so **none of them can see this latency**.
  `tests/integration/test_playback_start.py:LoadOutcomeTest` for the real start.
- **Prove it:** a new test at the *real* interval: Event set after 50 ms → returns
  within ~100 ms, for both Events; and a fake counting property reads shows ≤ 3
  reads per second (poll cadence unchanged, loop of ≥ 3 waits per STARTHERE's
  discipline). Benchmark: `bench\bench_wait_property.py`.

### O5 — Code: take `MediaSegments` off the critical path

- **Where:** `media.py:1036-1037` (`get_intro` inside `get_playback_url`, before the
  URL exists), `media.py:826-849`; consumer `player.py:2688-2758` (`update()`).
- **Change:** start `get_intro(media_source["Id"])` on a short-lived thread once the
  media source is known and join it in `_play_media` **before** `self._video = video`
  (`player.py:3398`) — i.e. while mpv is opening the file. `update()` cannot see
  the video before that line, so segment behaviour (including an intro at 0 s with
  `always`) is unchanged.
- **Gain:** one API round trip per start and per auto-advance — the fetch measured
  51 ms of the 182 ms before loadfile at 50 ms per API request (2.5); ~1 ms on a
  LAN. Whether it is fully hidden depends on how long mpv takes from loadfile to
  `file-loaded` (the join point): ~7-10 ms on the local test (the stand-in adds no
  latency to the stream itself), but on a real WAN mpv's own stream open is at
  least one round trip (TCP/TLS + GET), so the segments fetch fits inside it.
- **Risk:** low-moderate: threading on the start path; `get_intro` already swallows
  its own errors; `intro_tried` makes it idempotent. Must not hold `_lock` in the
  thread.
- **Tests today:** segments are covered mostly by `tests/test_offline_media.py` and
  the integration start tests; nothing pins *when* intros are fetched.
- **Prove it:** a unit test with a slow fake `get_media_segments` (e.g. 200 ms):
  `loadfile` is issued before it returns, and `_video` is published only after
  `intros` is filled; repeat over ≥ 3 starts (auto-advance included).
  Benchmark: `bench_player.py --latency-ms 50`, `play_call_to_file_loaded`.

### O6 — Code: stop re-asking "is this server local" for every queue step

- **Where:** `utils.py:118-160` (`is_local_domain`), called from `media.py:1213` in
  every `Media.__init__` — including `get_next` (`media.py:1221`, every
  auto-advance), `get_prev`, `get_from_key`.
- **Change (two steps, take the first alone if in doubt):** (1) `get_next` /
  `get_prev` / `get_from_key` pass the parent's `is_local` into the new `Media`
  (same client, same queue, seconds later); (2) optionally cache the answer per
  `(auth.server, resolved IP)` for a few minutes.
- **Gain:** for a public-IPv4 server, removes one HTTPS request to
  checkip.amazonaws.com per advance (step 1) / per start (step 2): ~186 ms of CPU
  for the certificate parse alone (2.9, until O1) plus DNS + TCP + TLS + GET ≈ 3-4
  RTT to AWS, worst case the 3 s connect timeout — all **before PlaybackInfo**. For
  IPv6 servers: one `/System/Endpoint` round trip per start. Private-address
  servers: nothing (a `getaddrinfo` on an IP literal, 0.05 ms measured).
- **Risk:** moderate for step 2 — this decides the bitrate cap; a cached "local"
  across a network change would stream at LAN bitrate from outside. Step 1 is low
  risk (a queue does not change networks between episodes often; a new
  `start_playback` re-evaluates). **Do not** merge it with
  `resolved_host_is_private` / `components.is_local_server` — `do-not-fix.md` §1
  row "Three functions that answer 'is this server local'"; caching inside
  `is_local_domain` is not that.
- **Tests today:** only `tests/test_server_recovery.py` mentions it.
- **Prove it:** unit test that an auto-advance does not call `is_local_domain`
  (patched counter) and carries the parent's value; for step 2, a TTL test and a
  "resolved IP changed → re-evaluated" test.

### O7 — Upstream library: python-mpv-jsonipc waits 100 ms before its first pipe probe

- **Where:** `python_mpv_jsonipc.py:618-620` (`for _ in range(100): time.sleep(0.1)`,
  then probe) — the library, not the shim.
- **Gain (measured):** mpv's pipe is up at 17.5-18.7 ms; `MPVProcess` returns at
  105 ms → **~87 ms on every mpv spawn** (backend start, every idle-quit re-open).
  Poll at 5-10 ms (or probe before the first sleep).
- **Risk:** low; `_ipc_endpoint_ready` is a non-blocking `WaitNamedPipeW(…, 0)`.
  External backend only.
- **Tests:** the library's own; shim side `tests/test_mpv_options.py` touches the
  retry options. Benchmark: `bench_player.py` `of_which_MPVProcess_spawn_to_pipe`.

### O8 — Bridge: emit `position` by polling one property, not by observing it

- BRIDGE.md wants `position` ~every 500 ms, only when changed. Measured: an
  observer on `playback-time` delivers **30 notifications/s** at 30 fps (one per
  frame, each through the pipe and jsonipc's event thread); a 2 Hz read is 2 ×
  0.04 ms. Poll `playback-time` (and `duration` only when the item changes). Risk:
  none. Do not reuse `push_playstate` for it at 2 Hz — that is ~10 reads (0.77 ms)
  per call.

### O9 — Code (small) / Bridge: the trailing progress post in `start_playback`

- **Where:** `event_handler.py:121` — `timelineManager.send_timeline()` runs on the
  **caller's** thread after `play()` returns: it waits on `_tl_lock` behind the
  session-open, then POSTs. Measured: `start_playback` returns 141 ms after the
  first frame at +50 ms API latency (233 → 374 ms), 8 ms locally when warm.
- **Change:** bridge — run `start_playback` on a worker so the stdin command loop
  stays responsive (it holds `_lock` for the whole start anyway). Upstream option —
  replace the call with `playerManager.timeline_handle()` (wake the timeline thread).
- **Gain:** the caller is free ~1-2 RTT earlier; time-to-first-frame unchanged.
- **Risk:** low; ordering is guaranteed by `_session_ready` either way.
  Tests: `tests/test_playstate_payload.py`, `tests/integration/test_playback_start.py:StartOrderTest`.

### O10 — Bridge (tiny): pre-import `sync.manager` at startup

- The first progress report imports the download catalog code on the playing
  thread (`player_reporting.py:511`): ~33 ms once, after the first frame. Import it
  at bridge start (or ignore). Every 30 s `_record_progress` also re-reads the 6
  timeline properties for an offline record bridge mode never has — ~0.5 ms, not
  worth a change.

### O11 — Bridge: settings that keep work off the play path

- `check_updates: false` — BRIDGE.md forbids update checks anyway, and
  `update_check.check()` runs at the end of `_play_media` **under `_lock`**
  (`player.py:3508`): once a day a GitHub request (3 s connect / up to 10 s read)
  would hold the player lock — controls freeze for that long.
- Do not import `mpv_shim`, `mpvtk_browser`, `tray` — mostly about side effects;
  the import time saved is small (2.1: ~20-60 ms own share).

### Considered, low value — not recommended now

- **Prefetch the next item's DTO near the end of an episode** (saves the item GET +
  O6's check on auto-advance): stale-UserData risk and a new code path on the
  advance, which runs under `_lock` (`player.py:4260-4336`). Do O5/O6 first.
- **Select tracks via `loadfile` per-file options** instead of after the load
  (`configure_streams`, `player.py:4941`): the post-load `audio` write measured
  1.5 ms; not worth reopening the track-selection order (`docs/track-selection.md`).
- **Parallel trickplay/chapter-image fetch:** background-only, after playback is
  live; the serial fetch is deliberate so it does not compete with the stream.
- **SessionReporter's 0.5 s idle wake** (`session_reporter.py:58`): 2 wakeups/s,
  unmeasurable.

### Do not do (conflicts with `do-not-fix.md`, STARTHERE or `mpv-backends.md`)

| Tempting change | Why not |
|---|---|
| Drop or slow `wait_property`'s property poll, or the 1 s EOF poll rescue in `update()` | Both rescue lost observer notifications on this backend — mpv-backends §9 "Do not simplify this away". Measured cost: ~0.9 ms/s. O4 keeps the poll. |
| Skip/defer `_snapshot_render_pristine`'s 16 reads at construction | Order is load-bearing (mpv-backends §6: a lazy snapshot records the shader pack's values as the user's). Cost: ~2 ms once. |
| Stop holding the start paused until `_play_media` finishes, to show frames sooner | #785: subtitles and the resume seek used to run under a playing video. Measured hold: ~4-8 ms after the first frame. |
| Start the trickplay fetch earlier | Deferred on purpose (`player.py:3481-3499`): competing with the stream open caused TLS errors and slow opens. |
| Merge the three "is this server local" functions | `do-not-fix.md` §1 — DNS on the render path or HTTP on the connect path. (O6 caches inside one of them.) |
| Set `http-header-fields` only when it changed / drop the clear at the top of `_apply_auth_headers` | Load-bearing (mpv-backends §6, auth-headers.md; do-not-fix §3 on `_warm_shader_scope` and §4 F57). Cost: 2 writes, 0.2 ms. |
| Fold the per-item property writes (hwdec, deinterlace, presets, loop-file) into construction | Per-item on purpose (settings apply on the next item; options outlive a queue item — mpv-backends §6/§10). Cost: ~15 writes ≈ 1.5 ms. |
| Bound `python_mpv_jsonipc.TIMEOUT` globally to make a wedged mpv fail fast | mpv-backends §1: a global bound turned every later command into a disconnect. |
| Use `command("set", name, <number>)` anywhere new (e.g. per-file options) | Silently fails on jsonipc — pass `str(value)` (mpv-backends §1). |
| Narrow `configure_streams`' `not video.is_transcode` gate | do-not-fix §1, row 1. |

---

## 4. Benchmarks (repeatable)

All under `S\perf-backend\bench\` (stdlib + ctypes; Pillow for the fake server's
trickplay tile). Run with the scratch venv's python; they import the shim from
`--shim-root` explicitly (nothing pip-installed is used for the shim), so point
it at `backend\` in the repo or at any checkout.

| Script | What |
|---|---|
| `run_all.py` | Runs everything below off-screen and prints one comparable table; writes `summary.json` |
| `hidden_run.py` | Runs a command on a private off-screen desktop in a kill-on-close job (no windows on the user's desktop, no stray mpv) |
| `run_suite.py` | The unit suite (parallel, or `--serial`) off-screen with sandboxed `APPDATA`/`LOCALAPPDATA` and mpv on `PATH` |
| `bench_import.py` | `-X importtime` of the bridge set and of what bridge must not need |
| `bench_mpv_start.py` | mpv spawn → pipe → first frame for the shim's own option sets; raw vs jsonipc IPC; observer rate |
| `bench_player.py` + `fake_jellyfin.py` | The real player and `start_playback` against a logging stand-in server (own process): HTTP/IPC per phase, play timelines (IPC waits, mpv events, HTTP, spans), cold/warm/after-idle-quit starts, steady-state CPU/IPC/HTTP, seek, stop. `--latency-ms`, `--no-trickplay`, `--live` |
| `bench_wait_property.py` | How late `wait_property` notices `satisfied_by` / `abort` |
| `bench_tls_connect.py` | Cost of a new HTTPS connection: requests default vs `verify=False` vs a pre-loaded `SSLContext` (needs a self-signed `cert.pem`/`key.pem`; `S\perf-backend\tls\` has one: `openssl req -x509 -newkey rsa:2048 -nodes -keyout key.pem -out cert.pem -days 2 -subj /CN=localhost`) |
| `first_request.py` | First vs later requests on a fresh client session |
| `list_skips.py` | Skipped tests and their reasons (serial discover, ~21 min; run it through `hidden_run.py` with the `run_suite.py` env). **Its one run lost its output** (an unflushed `os._exit`, now fixed), so the reasons for the 54 skips are not in this report yet |

**Before/after, on an otherwise idle machine:**

```
cd S\perf-backend
.venv\Scripts\python.exe bench\run_all.py --shim-root <base checkout>   --out results\before
.venv\Scripts\python.exe bench\run_all.py --shim-root <changed checkout> --out results\after
:: ~9 min each. Test media: media\bench.mkv (3 min 720p H.264, made with
::   mpv av://lavfi:testsrc=size=1280x720:rate=24 --length=180
::       --audio-file=av://lavfi:sine=frequency=440 --o=bench.mkv --ovc=h264_mf --oac=aac)
.venv\Scripts\python.exe bench\bench_tls_connect.py --cert tls\cert.pem --key tls\key.pem
```

And the functional gate for any change: both suite commands from §1, compared to
`baseline-failures.txt` (same 8, nothing else), plus the mypy diff (same 2 extra,
same 1 missing), plus the integration matrix for anything on the player path.

Noise seen between the two passes: start timings ±10-20 ms on the cold play,
±5 ms warm; `wait_property` means move by ±70 ms at n=20 (use ≥ 50 trials to compare
O4 before/after, or just compare max: 500 ms vs ~tick); steady CPU ±0.05 points.
Differences smaller than that are not results.
