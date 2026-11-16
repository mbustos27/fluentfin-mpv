# Bridge mode

The shim as FluentFin's playback backend: a child process FluentFin starts
and drives over stdin/stdout. The **contract** is `BRIDGE.md` at the
repository root (`../docs/BRIDGE.md` from `backend/`), protocol 1, and is
not repeated here; this file is how the shim implements it, and what it
switches off to do so.

    <venv>\Scripts\python.exe -I -m jellyfin_mpv_shim.bridge --config-dir DIR [--log-file PATH] [--dev]

`--config-dir` defaults to `%LOCALAPPDATA%\FluentFinMpv\mpv-backend\`
(`~/.config/FluentFinMpv/mpv-backend` elsewhere): its own `conf.json`,
`mpv.conf`, `input.conf`, scripts and shader packs, never shared with a
standalone shim or an upstream FluentFin install. `--log-file` is written
fresh each start, in addition to stderr. `--dev` adds `play_file`.

Code: `jellyfin_mpv_shim/bridge.py` (process, threads, commands),
`bridge_protocol.py` (framing, validation, redaction, translations -- no shim
imports), `bridge_state.py` (observations -> events), `bridge_http.py`
(the guard on each client's requests: the method on a retry, retiring a
switched-away client -- section 3), `window_embed.py` (embedded-window
state, read by `mpv_options` and `player_window`).

## 1. Startup order, and why

`bridge.main` does three things before anything else, in this order:

1. **stdout.** `bridge_protocol.isolate_stdout` duplicates fd 1 for the
   protocol and points fd 1, `sys.stdout` and the Win32 `STD_OUTPUT_HANDLE`
   at stderr. A stray `print`, a C library, `WriteFile` on the standard
   handle and a child process that inherits it all land in stderr. The
   duplicate is non-inheritable, so mpv never holds the protocol pipe.
2. **argv.** The bridge parses its own flags, then pre-seeds the shim's
   parser (`bridge.preseed_shim_args`, i.e. `args._args`) with
   `--config DIR --no-gui`. `args.get_args()` is reached at import time all
   over the tree (docs/testing.md section 2); left alone it would parse
   `--config-dir` and die with the app's usage line.
3. **No player until `connect`.** Importing `player` builds mpv
   (docs/mpv-backends.md, docs/testing.md section 1), and mpv has to know
   whether it renders into FluentFin's window before it exists. The first
   `connect` fixes the window and only then imports `player`.

`hello` is written before the stdin reader starts, so it is always the
first line; anything the UI sends early waits in the pipe.

Threads: the **reader** answers `ping`, `get_state` and `shutdown` itself
and queues everything else; the **control worker** runs queued commands in
order (so `connect` then `play` cannot race) and answers each; the **play
worker** runs whatever blocks for a playback start (`play`, `next`,
`previous`, `set_audio`/`set_subtitle` -- a transcode restart -- and
`play_file`); the **monitor** samples the player twice a second, or when
poked, and emits events; the **reported writer** writes a `reported` the
monitor has not written within a second (section 5). Responses therefore
carry ids but not request order: `unknown_cmd` for a later line can
overtake a queued `play`.

No single message may end a thread. The reader handles each line inside
its own `try`, the control worker answers every command even when the
answer will not encode (`internal`), and the monitor sends each event on
its own. A lone UTF-16 surrogate -- the server's JSON can carry one in a
badly decoded name -- is replaced with U+FFFD by `encode_message`; an
escaped one would be refused by System.Text.Json on the UI side.

**Shutdown** (`Bridge.stop`, after stdin EOF, `shutdown` or a signal)
runs under **one deadline**, `SHUTDOWN_BUDGET` (8 s) from the request
(BRIDGE.md section 3). Every wait below takes what is left of it; none has
a clock of its own, because the shim's own bounds -- the reporter's 5 s
drain, a thread's 15 s join, the old 5 s control join plus 8 s on the lock
-- added up to ~18 s, and the UI kills the backend at 10 s.

1. Queued starts are dropped and one in flight cancelled. A `connect`
   checking its new server's `Users/Me` has that client retired at once
   (`bridge_http`), so the check gives up instead of spending the budget.
2. The control worker is joined, because a first `connect` it is running
   may be starting mpv, and a shutdown that ran first would leave that mpv
   behind (`ShimRuntime._publish`: whichever of the start and the shutdown
   comes second ends it). Commands still queued are answered `internal`,
   not run. A switch waiting out `STOP_WAIT` gives way to the shutdown.
3. `ShimRuntime.shutdown` waits for the player's lock (a start, next,
   previous or track restart holds it across PlaybackInfo), then for the
   report lock `_tl_lock` (a progress report holds it across its request,
   and the reporter's `session_playing` across its own), holds both across
   `stop()` -- so no progress report can slip in -- and then drains the
   reporter until the final Stopped report is delivered.
4. All of that by the deadline: the ordinary `terminate()` (which by then
   finds nothing to stop or drain) and the shim's threads joined with what
   is left. Otherwise -- a lock still held, or the report not delivered --
   the external mpv is ended without the lock, the shim's threads are told
   to halt rather than joined, the final report is not sent or not waited
   for (it could only go to that same server), and the exit status is 1.

mpv itself can stop answering too. Once the orderly stop has both locks,
mpv's replies get what is left of the deadline (`IPC_FLOOR`, 0.25 s, at
least), not the teardown's 5 s each (`_final_report`): a ``next`` that gave
the player's lock back late in the budget (a slow PlaybackInfo), then an
mpv that had stopped answering, kept ``stop()`` 5 s on one reply, past the
exit watchdog -- measured (``hung_mpv_next``): no answer, exit 1 at 9.7 s,
and that mpv still running. A reply that times out is mpv lost, and the
shim ends it (`_handle_mpv_disconnect`). As a backstop, `main` registers
`ShimRuntime.end_mpv_process` as `exit_watchdog`'s final action, which runs
on both ways out -- the orderly finish and the watchdog's forced exit --
and ends an external mpv still running by its process handle
(TerminateProcess), never its IPC. Inside the UI's job object the job
would end it; outside one (a probe, a dev run) nothing else does. One hung
thing at a time never needed it: an mpv suspended while playing (the
monitor and timeline threads then wait on it) gets its first reply timeout
or the deadline path, and is gone with the backend (``hung_mpv:*``).

The answer goes out when that is done, so by ~8 s. `main` then cuts the
exit watchdog's straggler grace (`GRACE_SECONDS`, 3 s) to what is left of
`EXIT_SLACK` (1 s) past the deadline, and arms the watchdog at 9.5 s
rather than the app's 20 s: the process is gone by ~9 s whatever the
server is doing. A degraded shutdown leaves the timeline and action
threads parked, which is what that grace is for. (`update`, the action
thread's pump in `player.py`, is not `@synchronous` itself, but every pass
calls `_pump_trickplay`, which is: so the action thread waits on the
player's lock on **every** pass for as long as a start -- or a next,
previous or track restart -- holds it, not only inside a queued task that
takes it, such as a deferred stop or a finished callback. A task queued
behind a start runs no sooner than that start lets go of the lock,
whatever its place in the queue.)

## 2. What bridge mode switches off

Set for the run with `settings.apply_cli_override`, so none of it reaches
`conf.json` (`ShimRuntime.HEADLESS_OVERRIDES`):

| Off | How |
|---|---|
| library browser, tray, playback HUD | `enable_gui=False`; `mpvtk_browser` is never imported. `osc_style` resolves `mpvtk` -> `mpv`, and the bridge loads the shim's OSC fork with FluentFin's look (section 4a) |
| update check | `check_updates=False` (it also runs inside a playback start, under the player lock) |
| idle quit | `mpv_idle_quit=False`: mpv stays up between plays. A re-created mpv costs ~240 ms to the first frame against ~42 ms, and an embedded one would take the owner's video surface with it |
| Discord presence, MPRIS | `discord_presence=False`, `mpris=False` |
| gamepad navigation | `input_gamepad=False` |
| health check / reconnect loop | `health_check_interval=None`; there is no websocket to reconnect |
| downloads | `auto_download_enable=False`; `syncManager` is never started |
| the shim's own log file | `write_logs=False`; `--log-file` instead |

Never imported or started at all: `mpv_shim.main`, `single_instance` (the
loopback listener), `tray`, `cli_mgr`, `clientManager`'s connect paths.
**No network port is bound.** The external backend talks to mpv over a named
pipe on Windows (a unix socket elsewhere).

mpv: libmpv when python-mpv can load it, else the external binary
(python-mpv-jsonipc) -- `player.py`'s own choice, untouched. For the
external one `mpv_ext_path` is used if set, else `mpv.exe` on PATH, else a
WinGet install under `%LOCALAPPDATA%\Microsoft\WinGet` (`bridge.find_mpv_binary`).
The default shader pack (`gen_pkg.sh`, not in git) is optional: without one
the bridge turns `shader_pack_enable` off for the run and says so in one
INFO line, instead of the menu's "Could not load profile manager"
traceback. The result is the same either way -- no shader profiles.

## 3. Auth: FluentFin's identity, REST only

`connect` builds a `JellyfinClient` from what FluentFin sends
(`ShimRuntime.make_client`): `config.app(client.name, client.version,
device.name, device.id)` and `config.auth(server.url, user.id, token)`, then
`start(websocket=False)`. So every request -- PlaybackInfo, the stream
header mpv gets (docs/auth-headers.md), the progress reports -- carries
`Authorization: MediaBrowser Client="FluentFin", Device=..., DeviceId=...,
Token=...`, and the server attributes playback to FluentFin's own session.
No password, no login, no websocket, no capabilities post: those are
FluentFin's. One bounded authenticated request, `GET /Users/Me` on the
same client that will play (`ShimRuntime.verify_client`), checks the token
at `connect` rather than at the first `play`, and warms that client's
connection for the first PlaybackInfo. `Users/Me` answers with the user the
token belongs to, so its `Id` is compared (case- and dash-insensitively)
with `connect.user.id`:

| Answer | Code |
|---|---|
| a user whose `Id` is `user.id` | connected |
| a user with another `Id` | `auth_failed` |
| 401, 403 | `auth_failed` |
| no connection, a timeout, a 5xx | `server_unreachable` |
| a body that is not a user object | `server_unreachable` |

The last row matters: **the apiclient returns None for a 500** (and for a
body that is not JSON) instead of raising, so "no exception" is not
success. `tests/test_bridge.py:VerifyAgainstTheApiclientTest` drives the
apiclient's own request path with the session answering each of these.

The same None from **PlaybackInfo** reached `Video.get_playback_url`, which
stored it and dereferenced it: a TypeError, reported as ``internal``
("'NoneType' object is not subscriptable") for a restart or a next whose
server answered 500. `make_client` puts `bridge_http.check_play_info` round
each client's ``get_play_info``: an answer that is not a dict with a
``MediaSources`` list -- a 5xx, an empty or non-JSON body -- raises the
apiclient's own ``HTTPException("ServerUnreachable")`` where it comes back,
with a sentence saying which (``the server answered PlaybackInfo with HTTP
500``), so it is ``server_unreachable`` (BRIDGE.md section 5) and nothing
stores it. ``MediaSources: []`` is an answer (nothing to play) and passes.
`describe_exception` shows such a sentence; the apiclient's own
HTTPExceptions carry the requests error, which names the url, and stay
"status (type)". `PlaybackInfoAnswerTest`.

A new `connect` with a server **retires** the old client, stops playback,
drops the old client, and is "not connected" until the new one verifies.

The player has one report lock (`_tl_lock`) and one report worker
(`SessionReporter`) for every client, and holds its own lock across
PlaybackInfo. So a hung old server held everything behind it: a progress
report holding the report lock made the switch's inline stop wait 30 s
(`tl_switch`), and the new server's first `Playing` queued 31 s behind the
old server's `Stopped` on the shared worker (`tl_switch_long`). Hence:

- **Retired first** (`bridge_http.ClientGuard`). Every request of a bridge
  client runs on a helper thread while its caller waits; once the client
  is retired, a caller waits at most `RETIRED_WAIT` (2 s) more and gets
  `HTTPException("ServerUnreachable")`, the request's own retries are
  refused, and new requests get one attempt with a `RETIRED_TIMEOUT` (5 s)
  socket timeout. A healthy old server still gets its Stopped report and
  stream release; **a hung one loses them** -- accepted: the alternative is
  the new server's session waiting on the old one. The same release frees
  a `next` holding the player's lock across a hung PlaybackInfo, so a
  `play` after the switch no longer waits for that request to time out.
  The grace counts from the later of the retirement and the request's own
  start: the stop's Stopped report goes out after the retirement -- once a
  hung progress report lets go of the report lock, up to the grace later --
  and counted from the retirement alone it was refused before it was sent.
- **The stop is always deferred** to the action thread (`put_task`) and
  waited for at most `STOP_WAIT` (5 s), then the switch goes ahead. Inline,
  a free player lock was no bound at all: `stop()` then waits on the report
  lock. When the deferred stop finally runs it stops only what it was
  meant for, never a newer start -- and it decides that **under the
  player's lock**, in one step with the stop: decided before taking it, a
  newer ``play`` holding the lock across its load (``_video`` still the
  old item) passed the check, and the stop, waiting on the lock, then
  ended the new item (`DeferredStopUnderTheLockTest`). The ``stop``
  command's stop is deferred
  the same way, and answered at once: inline, it held the control worker
  -- and a ``connect`` to another server queued behind it -- on the report
  lock for as long as a hung progress report took, while ``state`` already
  said ``idle`` (``stop_hung``: neither answered in 20 s). Its Stopped
  report goes out when the stop runs, or from the shutdown's own stop if
  the shutdown comes first; a stop still waiting on the report lock holds
  the player's lock, so the shutdown waits for it inside its one deadline.
- **An answered stop's item is gone for transport** (`Bridge._stop_for`,
  read through `_playing`) until its deferred stop has run: ``_video``
  outlives the answer, and a ``next`` sent right behind the ``stop`` read
  it as playing, started the next item -- Playing to the server -- and the
  deferred stop then ended that one (``stop_then:next``: 4 runs in 7).
  ``next``, ``previous``, ``set_audio``, ``set_subtitle``, ``pause``,
  ``unpause``, ``toggle_pause``, ``seek`` and ``seek_relative`` are
  ``no_playback`` for it, and ``set_volume`` sets the next item's volume.
  A start the stop cancelled no longer counts as opening either: it is only
  unwinding (`_starting`).

Two costs of retirement and of the POST fix below, accepted:

- **Retirement drops the retry for a healthy old server too.** The old
  server's Stopped (and stream release) gets one attempt, so a single stale
  keep-alive reset on that attempt loses it, where a client not retired
  would have retried a second later. Retiring only after the Stopped's
  first normal attempt is not cheap: the stop cannot report while a hung
  progress report holds the report lock, and it is the retirement that
  frees it, so the switch would wait out `STOP_WAIT` (5 s) instead of
  answering in ~2 s.
- **A Stopped can reach an overloaded server twice.** A retry stays a POST,
  so a report whose first attempt timed out on read -- the server had taken
  it and was slow to answer -- is sent again, and the server sees the same
  Stopped twice. The same holds for a progress report.

**The apiclient's retry turns a POST into a GET.** In
`jellyfin_apiclient_python` 1.21.0, `HTTP.request` does
`data.pop('type', "GET")` inside its retry loop (`http.py:149`): the first
attempt takes the method, every retry goes out as a `GET` with the body
still attached. A progress or Stopped report that timed out once was
retried as `GET /Sessions/Playing/Progress` -- a 405 on a real server, so
the report was lost; against the probes' fake server it "succeeded" as a
GET. In bridge mode `ClientGuard` remembers each call's method and hands
it to every attempt (a thread-local per call, read in its wrapper of
`HTTP._requests`); `site-packages` is not edited. It belongs upstream;
until then, note that fixing it makes a retried report to a hung server
take the full 30 s × 6 attempts, which is why the retirement above does
not rely on the retry failing fast.

**Redaction.** The token is registered with `bridge_protocol.add_secret` in
`Bridge.handle_line`, before the request is even logged. Every log handler
uses `RedactingFormatter`, which formats the whole record (arguments and
traceback included) and then removes registered secrets and any
`accessToken`, `api_key=`/`ApiKey=`, `X-Emby-Token`/`X-MediaBrowser-Token`,
`Authorization`/`X-Emby-Authorization` value and `Token="..."`. Error
messages sent to the UI go through the same function. Commands are logged
by name and id, never by args. `configure_logging` also replaces
`sys.excepthook` and `threading.excepthook`, so an exception nothing
caught is logged through the same formatter (and into `--log-file`)
instead of printed raw to stderr.

`play_file` goes round `_apply_auth_headers`, so it calls
`_revoke_auth_header` before handing mpv its URL: `http-header-fields` is
global, and the last server item's token would otherwise go to the file's
host (docs/auth-headers.md).

## 4. The window

`connect.window` is fixed for the process (`window_locked` otherwise).

**Embedded** (`{"mode": "embedded", "hwnd": N}`): the handle must satisfy
`0 < hwnd <= 4294967295` (`bridge_protocol.normalize_hwnd`; mpv reads a
negative or wider value as "no window" and opens its own) and be a live
window (`IsWindow`), else `bad_args`. `window_embed.apply_to_options` then rewrites
the construction options: `wid`, `force-window` and `idle` (the surface
exists before the first file), `window-dragging=no`, a black
`background-color`; `geometry`, `window-maximized`, `border` and `title` are
dropped. At runtime every `player_window` path that would write geometry,
save it, maximize, minimize, drag, raise or drop the surface returns early
when embedded, and the bridge also forces `fullscreen`, `raise_mpv` and
`remember_window_size` off (idle quit is off in every bridge, section 2).

Fullscreen becomes a request (`fullscreen_request`):

- the shim's own paths -- the claimed `f` key, a remote `ToggleFullscreen`
  -- reach `set_fullscreen`, which asks the owner instead of writing mpv;
- mpv's own -- the `MBTN_LEFT_DBL` binding, the OSC button, ESC -- flip
  mpv's `fullscreen` property directly, so `_on_embedded_fullscreen`
  observes it, asks, and puts the property back to the owner's state.

The UI answers with `set_fullscreen`, which `set_embedded_fullscreen`
records and mirrors into mpv's property so the OSC draws the right button.
Measured on mpv 0.41/Windows: with `--wid`, writing `fullscreen`,
`geometry` or `ontop` moves, resizes and retitles nothing.

**Standalone**: today's behaviour -- mpv's own top-level window, the shim's
fullscreen and geometry handling, and `set_fullscreen` toggles mpv.

**Attached** (`{"mode": "attached", "ipc": "<pipe>"}`, FluentFin MPV's
player controls, docs\research\player-controls.md in the repository root):
mpv runs inside FluentFin (libmpv, composition mode, no window) and answers
JSON IPC on that pipe. `ShimRuntime.attached_overrides` makes the run the
shim's external backend with `mpv_ext_start=False` on that pipe (and
`mpv_ext_no_ovr` off), for the run only, plus the embedded overrides; it
connects and never starts or quits mpv (`terminate()` only closes the pipe,
idle quit is off, `end_mpv_process` finds no process of its own).
`window_embed.configure(None, attach=True)` makes the window the UI's in
every runtime path (`active()`), and `apply_to_options` adds nothing: no
construction option reaches an mpv the shim did not start (python-mpv-jsonipc
drops them when it only connects). So FluentFin creates its mpv with what
matters (`vo`, `gpu-context`, `d3d11-output-mode=composition`, `idle`,
`force-window`, `osc=no`, no input bindings, `input-ipc-server`, this config
dir for `mpv.conf`); runtime writes (title, hwdec per file, tracks, the OSC
script it loads) work as on any external mpv. Not there in attached mode:
the scripts the shim passes on the command line (thumbfast, mouse; FluentFin
draws its own controls) and `tls_*` client certificates. Measured with
`tools\ui-harness\composition-spike` (scenario `bridge`) and
`tools\ui-harness\fluentfin-controls`.

## 4a. The on-screen controller

`fluentfin_osc` and `fluentfin_bar` (FluentFin MPV's own modules),
switched on by `start_player` before the player is imported, like
`window_embed`:

- **Which bar** (`fluentfin_bar.choose`, from the *configured* `osc_style`;
  bridge mode resolves the shim's default "mpvtk" to "mpv", since the HUD
  needs the library browser): **uosc** (vendored under `fluentfin/mpv`,
  `fluentfin/VENDORED.md`) for "mpvtk"/"jellyfin", the default; **the fork**
  for "mpv"/"default", and as the fallback wherever uosc cannot run: its
  files missing, an `attached` window (FluentFin's own mpv gets no
  construction option, so no `osd-fonts-dir` for uosc's icon font), a user
  `mpv.conf` that sets `osd-fonts-dir`, or a uosc that will not load.
  "none" and "custom" are the shim's, as without the bridge. Either way the
  shim builds mpv with style "mpv", and `_load_classic_osc` asks
  `fluentfin_osc.load_bar` first: it `load-script`s uosc's folder and
  `fluentfin.lua` (after the `script-opts` appends, so both read their
  options at their start), or only `fluentfin.lua`, and the fork then loads
  as before. With a bar of ours, a `kb_kill_shader` still at its default `k`
  moves to `K` for the run (the shim's binding is forced, and `k` is pause
  in FluentFin's key map).
- **uosc's look** is `fluentfin_bar.uosc_script_opts`: FluentFin's controls
  left to right (play/pause, previous and next in the shim's queue, -10 s,
  +30 s, Skip when a segment is on offer, audio, subtitles, editions,
  chapters, full screen), the title on top with no window buttons, no
  progress line while hidden, the seek line in the accent with no cache
  ranges or buffered readout, the wheel over it not a seek
  (`timeline_step=0`), the volume slider in steps of 5, FluentFin's scrim
  colour at 75 %, Windows' text scale, `languages=en` (no translations are
  vendored), no idle or audio indicator. Construction options:
  `osd-fonts-dir` (uosc's two fonts) and `volume-max=100` unless `mpv.conf`
  sets it. The fork's options go with uosc's, for the fallback. A user
  `script-opts/uosc.conf` keeps uosc's look theirs.
- **`fluentfin.lua`** goes with either bar: the keys of FluentFin's own
  controls (k pause, j/l -/+10 s, Up/Down volume +-5 held to 0..100, s skip,
  N next, P previous; non-forced, so the user's `input.conf` wins; s, N
  and P are `fluentfin-*` script messages that `PlayerManager.
  _on_client_message` hands to `fluentfin_osc.on_message`, which queues the
  shim's own `skip_intro`/`play_next`/`play_prev` on the action thread),
  and the Skip prompt (an OSD message from `user-data/fluentfin/skip`,
  which the bridge writes, section 5). Once uosc has published
  `user-data/fluentfin/bar` (its one local patch) it also makes the wheel
  the volume (horizontal: nothing), raises the subtitles above the bar
  (restores deferred 150 ms, so a `sub-pos` the bridge writes with a hide
  is kept), fills uosc's managed buttons (`fluentfin_prev`,
  `fluentfin_next` from `user-data/fluentfin/next`, `fluentfin_skip`) and
  turns the shim's `osc-visibility never`/`auto` (its `enable_osc`, which
  `set_controls` uses) into uosc's `disable-elements`. User-data strings
  are observed as `native`: observed as `string` they arrive JSON-quoted.
- **The fork** is `trickplay-osc.lua`, whatever the mpv version. The winget
  mpv-CI build on the development machine reports `v0.41.0-dev`, which
  `osc_preview_api_works` reads as 0.41, and its stock OSC has no
  `user-data/osc/draw-preview` (none in the binary): the stock OSC drew no
  trickplay preview at all. The fork asks `thumbfast.lua` itself, on any
  mpv; uosc asks it through thumbfast's own messages, which the shim's
  `thumbfast.lua` answers with Jellyfin's tiles.
- **FluentFin's patches live in the fork**, each between `-- BEGIN patch
  FluentFin MPV` and `-- END patch`, each behind an option whose default is
  upstream's look, so a standalone shim is unchanged. `script_opts` turns
  them on through `script-opts` (appended after `osc-idlescreen`, by
  `mpv_options.osc_script_opts`): no cache readout, no inert playlist
  buttons (mpv's playlist holds one item; the queue is the shim's), track
  names on the track buttons instead of "1/2" (language, or a title that
  names it; never a sidecar's file name, which can carry `ApiKey=`),
  subtitles moved above the bar while it is shown (`sub-pos`, put back on
  hide, and a value the shim writes meanwhile is the one put back), the
  bar in FluentFin's `PlayerControlsScrimBrush`, a thin seek rail with the
  Windows accent (`AccentPalette`, Light2, as WinUI fills a slider), the
  OSC scaled by Windows' text size, and the wheel as volume +-5 held to
  0..100 that never seeks (non-forced bindings: the user's `input.conf`
  wins). A user `script-opts/osc.conf` in the config folder turns all of it
  off, since `script-opts` would override every line of it.
- `apply_to_options` sets `osd-font` to Segoe UI Variable Text unless the
  user's `mpv.conf` sets one (for the external backend a construction
  option is a command-line argument, which `mpv.conf` cannot override).
- The title (`force-media-title`, all modes) is `media_title`:
  "Series — S6E4 · Episode", without the episode's name when it only
  repeats the series (a library that prefers embedded titles stores the
  show there), and a film's "Name (Year)". A sidecar is `sub-add`ed with
  its stream's title and language (`sidecar_labels`), which mpv cannot know.

Tests: `test_fluentfin_osc` and `test_fluentfin_bar` (pure),
`test_fluentfin_osc_player` (imports the player: off-screen),
`test_fluentfin_osc_lua` (the fork's raise, wheel and labels against a real
mpv, `--vo=null`, no window), `test_fluentfin_bar_lua` (`fluentfin.lua`
against a real mpv with no window and a stand-in uosc that records its
messages; the vendored uosc itself needs a video output, so those cases run
only on a private desktop, i.e. through `run_suite.py`, and skip on the
user's). Those start `mpv.exe`, not the `mpv.com` console wrapper, which
starts mpv as a child a kill of its own handle leaves running.

## 5. Commands and events

Item ids: `play` takes them with or without dashes and normalises them to
Jellyfin's N format (32 lowercase hex digits); anything else is `bad_args`.
Every id the backend emits -- `state.itemId`, `queue.itemIds`, `item`,
`ended` -- is N format (`bridge_protocol.item_id_out`). `play_file`
playback has `itemId: null`.

`play` goes down the server's remote-Play path,
`event_handler.start_playback`, on the play worker: transcode decisions,
remembered tracks, media segments, next-episode and progress reporting are
the shim's own. `audioStreamIndex`/`subtitleStreamIndex` are passed as the
remote Play passes them, and marked `explicit_tracks` only when **both** are
given (with one, the shim would read the other as "none"). A newer `play`,
`play_file`, `stop` or server `connect` cancels a start in flight
(`cancel_load`) and drops queued ones.

**Replacing playback.** The shim's remote-Play path treats a start over
playback as a hand-over and reports nothing for the outgoing item, so the
server kept that play session open (resume point, played state, transcode)
until its idle timeout. BRIDGE.md section 5 asks for what `next`/`previous`
do: the `play()` hook, on the one start a `play` command makes, takes the
player lock and calls `send_timeline_stopped(True)` and
`release_stream(outgoing)` before the new item goes on
(`Bridge._report_outgoing`). In the hook rather than before
`start_playback`, so an item fetch that fails leaves the old item playing
*and* reported; under the player's lock, so the item reported is the one
playing then, not one a `next` holding the lock is about to report itself.

**Once, whatever happens next.** The report waits on the player's lock and
the report lock, which a progress report holds for as long as its server
takes; the start is not flagged in the player yet, so a `stop`, `play` or
`connect` in that time had nothing to cancel -- it was answered `ok`, the
start went ahead, and after a switch it played and reported on the old
server (`stop_window:*`). The hook checks the generation again after the
report and drops the start; a command landing in the few bytecodes between
that check and the player's own start flag is held there briefly
(`_committed`, `COMMIT_WAIT`) so its cancel lands.

Every Stopped report the player makes goes through the hook's wrapper of
`send_timeline_stopped`, which records it (`_reported`) and skips one
already recorded; the wrapper of `release_stream` does the same with a
record of its own (`_released`) -- separate, because an end of file
reports the item and *then* releases its stream (`_unload_ended`). Both
are keyed on the video object and its PlaySessionId (`_SessionLedger`),
held weakly, so they are bounded by the videos still alive, and a video's
entry is dropped when it starts a new play session -- a restart, the same
video played again. Keyed on the id alone and never pruned, an item played
again under an id a server reused was never reported (``replay:fixed``: X,
Y, X, stop -- 3 Playing, 2 Stopped). A report built from bookkeeping after
mpv went away (`_report_stopped_offline`, on a thread of its own) is
checked and recorded by its options' session **alone** (`_stop_subject`,
`_SessionLedger.ORPHANS`), whatever is loaded by the time it runs: matched
against `_video`, a new item already loaded made it send a reported item a
second time, and its record overwrote the new item's, which was then
reported twice too. A new play session under the same id leaves that
record. When the replacement does not take, the
old item is ended (`_end_reported`: `_unload_ended`, no report):

| The replacement | The old item |
|---|---|
| superseded while the report waited | ended by the hook, reported once |
| cancelled while opening (`ppp:*`) | ended by the hook (the finished callback mpv's abort queued, if it gets the lock first, is held: below), reported once, never shown `paused` in between; `ended` is `stopped`/`replaced`, never `eof` |
| mpv failing the load | ended by the load's `stop()` or the hook, reported once |
| failing before mpv (PlaybackInfo, no URL) | **plays on** -- mpv was never touched -- with no progress reports and no second Stopped: the server keeps the resume point of the replacement. A transcode's stream was released with the report, so a transcoded old item stops soon after. If it plays to its end, `ended` is `eof` -- and if it reached its end while the replacement negotiated, that end runs now (below) |

**A ``play`` owns what comes next.** The replacement's report and its
PlaybackInfo run without the player's lock -- the only start that does --
so the old item can reach its own end meanwhile. Its finished callback then
had the queue advance: the next item started, Playing to the server, and
the replacement loaded over it with no Stopped (``play_vs_advance``: queue
[X, Y], X in its last seconds, ``play Z`` with a slow PlaybackInfo -- Y
played, no Stopped). The callback's wrapper now holds the end back
(`_defer_finish`, under the player's lock) while a ``play`` is pending (not
yet in play()) or opening another video, keeping the `_finished_lock` it
took; when the ``play`` command's start is done, `_finish_deferred` runs
it after all (`put_task`, the same callback and epoch) if the item is still
current -- the replacement failed before mpv, or its item fetch did -- so
the queue advances, or the item ends, as it would have. A replacement that
loads moves the epoch on (the held end is dropped, and `_play_media`
releases the lock); a stop, a switch or a failed or cancelled load ends the
item; a newer ``play`` takes the held end over. While held, the old item is
neither marked ``eof`` nor reported: the replacement reported it, at its
end (``finished``), and it ends ``replaced``. Holding it turns the progress
reports off, as an end does: `update`'s end-of-file poll rescue, gated on
them, queued the held end again on every pass, each queueing woke the action
thread for the next, and it spun for as long as the play's item fetch took
(``spin:20,*``: ~40,000 holds, logged each time, and 14 s of CPU in 19 s);
it is logged once per item and epoch. A stop (the ``stop`` command, a
switch) or the shutdown that finds the end held for an item still current
-- its ``stop()`` returned early, mpv having dropped the file: the last item
at its end -- ends it (`_end_held`): its Stopped (``finished``), then the
unload (not during the shutdown, whose own stop ends what is loaded), never
the queue's advance. Otherwise the shutdown refused the held end and its
stop() reported nothing (``held_last:shutdown``, ``held_last:eof``: Playing,
no Stopped, ``reported failed``), and a ``play`` job a stop dropped before it
started never ran it. The shutdown's runs inside its orderly stop, under both
locks, right after its stop() (`ShimRuntime.before_final_stop`), as the
``stop`` command's does. Run before it, with mpv not answering, the held
end's report waited out one reply -- `get_timeline_options` swallows the
timeout and leaves mpv for alive -- and stop() then another on
``playback_abort``: 10 s of the 8 s budget, no answer, exit 1, no
``reported`` (``held_hung``). stop()'s one timeout marks mpv lost and
reports the item from bookkeeping, and the held end then finds it gone. A
``play`` job superseded while its item fetch was finishing leaves a held end
alone (`_finish_deferred`): whatever superseded it ends the item. Run by the
job in the window between a stop's `_supersede` and its `put_task`, the end
was queued ahead of the deferred stop: the queue advanced, the next item got
Playing, and the stop then stopped that one. (``play_file``, ``--dev`` only,
is the exception: its stop() returns early for an item at its end and does
not end a held one.) The shim's
``force_set_played`` mark (off by default) is not sent for an item a
``play`` replaced at its end; the Stopped's position already says it was
finished.

One Stopped per play session, whoever makes it. `play_next`/`play_prev`
(and a finished callback's advance) report the outgoing item *before* they
call `play()`. When that start then did not take, the item was reported
twice: dropped by the hook as superseded -- a stop, play or connect while
the next waited on the report lock (``next_window:*``) -- or cancelled
while it negotiated (``next_cancel:*``, and the extra Stopped of
``hung_switch:PlaybackInfo``), the item stayed loaded and whatever ended it
next reported it again; a load that failed took it down with its own
`stop()`, which reported it again. The record skips all of them, and a
``play`` replacing such an item releases its stream (the next did not).
What can still reach a server twice is a retried POST (section 3).

**The item before a next, previous or advance that does not take** does
not stay current (BRIDGE.md section 5). A failed load's own `stop()`
returns early once mpv has dropped the file (``playback_abort``), so the
old item stayed `_video`: "paused" on a session its Stopped had closed, the
load's error state dropped by the tracker as "something is playing", and a
``stop`` doing nothing (``next_load_error``, ``next_load_timeout_stop``,
``eof_advance_fail``; ``prev_load_error`` went ``idle`` with ``ended:
eof`` instead, when the abort's finished callback -- queued for a last
item only -- got to it). A target failing *before* mpv (PlaybackInfo, no
URL; ``next_pbi_fail``) left it playing on its closed session. So the hook
ends it (`_end_reported`, no second report, its stream released once) for
any start of this kind that did not take -- failed in mpv or before it,
or cancelled -- under the player's lock the next holds, before `opening`
goes: the state is then ``error`` (or ``idle`` after a cancelling
``stop``), and ``ended`` says ``replaced`` (``eof`` for an advance). A
start dropped as superseded before it began is not this: the stop, play or
connect that superseded it ends the item, which mpv still has. Unlike a
``play`` command's replacement failing before mpv (the table above), which
plays on: BRIDGE.md section 6 accepts that one. Not during the shutdown
either (for any `_end_reported`): the shutdown's own stop ends what is
loaded, with mpv's replies bounded to its deadline, while the hook's unload
talks to mpv unbounded -- a next cancelled by the shutdown, mpv suspended,
held the player's lock past the deadline (``hung_mpv_next``: exit 1 where
it was 0).

The error of a start that failed before mpv is set before `opening` goes,
not after: with the item before it gone, a sample in between -- the play
session's settling pokes the monitor -- read ``idle``, and a UI leaves the
player page on that.

**Done with a play session: ``reported``** (BRIDGE.md sections 5 and 6;
the UI keeps a session open until it gets one). Where each outcome comes
from:

| Outcome | From |
|---|---|
| ``sent`` / ``failed`` | `bridge_http.watch_stops`, which `make_client` puts round each client's ``session_stop``: ``sent`` only on a 2xx from the request's last attempt (`ClientGuard.last_status`, per calling thread -- the apiclient answers None for a 204 and a 500 alike), ``failed`` on any other status, an error, a timeout, or a retired client giving up after its grace. On the report worker, or on the caller once the worker is stopped (`SessionReporter.submit` after `stop()`) |
| ``none`` | the ``play()`` hook (`_settle_sessions`), for a start that did not end up playing -- cancelled, failed to load, failed after PlaybackInfo, superseded -- when the session it negotiated never sent Playing; and (`_restart_not_taken`) the session a restart that found no stream was handed. "Sent Playing" is recorded by the bridge's wrapper of `send_timeline_initial`, under the player's lock and before it submits, so a stop taking the item between `_play_media` and the hook cannot make it look never opened |
| ``failed``, a restart | the ``play()`` hook: `restart_playback` (a track change on a transcode, a quality change) renegotiates on the same video and upstream sends no Stopped for the session it leaves. When the restart's start has finished -- which can be up to the playback timeout after the new PlaybackInfo -- and it leaves the item on a new id, whether the new session then opened, was cancelled or failed to load, the old one is abandoned (``failed``), so a UI still tracking it (no ``state`` showed the new one yet, or the restart did not take) closes it. Not on the new PlaybackInfo's arrival: a restart that never got a usable stream keeps the old session, below |
| ``failed`` / ``none``, at the shutdown | `Bridge.stop`, after `ShimRuntime.shutdown` and before the answer (`Tracker.sweep_sessions`): a session whose Playing or Stopped was submitted and has no outcome -- the final report not delivered by the deadline, or never made -- is ``failed``; one a ``state`` showed opening is ``none`` |

`bridge_state.PlaySessions` keeps each PlaySessionId's phase -- ``seen``
(the tracker noted a ``state`` showing it), ``opened``, ``stopping`` (its
Stopped submitted, noted by the Stopped wrapper) -- and lets **one outcome
per id** through for the process: a report worker finishing after the
shutdown's sweep is dropped. A server that hands one id to two plays gets
one outcome for both (Jellyfin makes a new id per PlaybackInfo).

**A restart that does not take** (`Bridge._restart_not_taken`, in the hook,
the video still current):

- *It never got a usable stream*: its PlaybackInfo request failed (or
  anything before it raised), or the answer had no media source or nothing
  this player can play (play()'s "no URL" return). mpv was never touched:
  the video plays on, on the stream and session it had, which goes on
  reporting and gets its normal Stopped (BRIDGE.md section 5). The
  negotiation had overwritten the video first -- the PlaybackInfo and its
  session, the media source, the stream maps, the play method -- and
  `Video.set_streams` the tracks before it, and play() turned the progress
  reports off. Left so, ``restart_pbi_fail`` reported the session
  ``failed`` while its stream played on, with no Stopped ever;
  ``restart_no_url`` showed a subtitle that was not on screen, reported
  nothing more, and the ``stop`` sent a Stopped for a session that never
  sent Playing; ``restart_no_source`` (``MediaSources: []``) left no media
  source, so `get_timeline_options` raised inside `stop()` -- answered ok,
  still ``playing``, no Stopped, and the shutdown's stop raised too. So the
  hook takes a shallow copy of the video's attributes before a restart
  (`_video_state`; the negotiation binds new objects to those names, it does
  not edit the old ones), the bridge's wrapper of `set_streams` the tracks
  it changes from, and `_put_back` restores both; then the quality override
  (``trs_ovr``) the stream was negotiated with, recorded when that start
  took (`_quality`) -- a quality change writes the new one before its
  restart is even queued, so the snapshot has it, and kept, the next
  renegotiation asked for the quality that had failed -- and mpv's auth
  header, which is global (play()'s no-URL return revokes it; a start that
  raised after installing it leaves it on where the old stream had it off).
  Then:
  progress reports back on unless a stop, play or switch interrupted the
  start; the session the failed negotiation was handed, never Playing,
  ``none``; and an ``error`` event naming the item -- not the state, it
  plays on -- with the code a new item gets for the same failure
  (``mpv_failed`` for no stream, the request's own, e.g.
  ``server_unreachable``, for a failed one; not when interrupted). Its
  message is the user's, shown as it is by FluentFin's notice
  (fluentfin-mpv): what was asked (`_change_asked`: audio, subtitles or
  quality, read before `_put_back` restores them), the item by its bar
  title (`item_title`) and the cause in words (`failure_cause`;
  `check_play_info` marks an answer that came, with its HTTP status, so a
  500 reads "the server answered with an error", not "could not be
  reached"). The raw cause, with the item id, is a warning in the log. Not
  undone: the old transcode, which `get_playback_url` ends before it asks,
  so a transcoded item's stream runs out soon after, as with a
  replacement's. `get_timeline_options` also builds a report with no media
  source now (``MediaSourceId`` null, an upstream diff of one line), so a
  video in that state can never make ``stop`` a no-op. Done in the bridge,
  not in `media.py`. Until mpv has the restart's load, ``state`` keeps
  showing the session mpv is playing (`_restart_shown`,
  `Observation.restart_session`), not the id the negotiation wrote on the
  video: the UI tracks the latest session it is shown (BRIDGE.md section
  6) and never reopens one it has seen (`MpvBackendHost.TrackPlaySession`),
  so a new id sampled mid-negotiation took the old session off its crash
  rule, and a restart that then found no stream left that session playing
  with no Stopped owed by anyone (``restart_no_url`` showed it).
- *mpv was handed its load* (``on_load_start``, which only the browser UI
  sets otherwise, `_on_load_start` -- for the start recorded on the calling
  thread: `_play_media` runs on its play()'s thread, and ``opening`` is the
  latest start on any thread, so a ``play`` entering its hook while the
  OSC's restart negotiated took the restart's load, and its load error,
  for its own) and the start then failed or was
  cancelled: mpv dropped the new file and the old one with it
  (`_abort_load`), the load's `stop()` returned early, and the video stayed
  current with nothing loaded -- "paused", a ``stop`` doing nothing, with a
  next item (``restart_load_error``, ``restart_cancel_queue``; with none,
  the abort's finished callback ended it ``eof``). The hook ends it, with
  no Stopped: mpv has no position to report (0 would wipe the resume
  point), the session it played was abandoned when the new PlaybackInfo
  replaced it (``failed``) and the new one never sent Playing (``none``).
  A server that gave the restart the same id gets ``failed`` for it.
- *Cancelled before mpv* with a stream in hand (the profile lookup): its
  session replaced the old one (``failed``), and the stop, play or switch
  that cancelled it ends the video and reports that session. Without one,
  as above, quietly. A ``play`` marks the item it replaces ``replaced`` when
  it is accepted: a restart it cancels once mpv has the load ends the item
  itself, before that start's hook would mark it (``restart_cancel_play``
  ended ``stopped``).

Nothing that finishes a report writes the event: the outcome is queued on
the tracker, and the monitor writes it after the events of its tick, under
`_emit_lock` (`_emit_tick`); the shutdown flushes what is left before its
answer (`_flush_reported`), and nothing is written after that flush. The
report worker never touches the pipe -- `ProtocolWriter.send` holds its
lock across a blocking write, and a UI that stopped reading would
otherwise stall report delivery. The monitor reads mpv before each tick,
outside `_emit_lock`, and an mpv that stops answering holds that read for
as long as an IPC reply is waited for (120 s on the external backend):
every outcome queued meanwhile waited with it, until the shutdown
(``hung_mpv_stop``). So a thread of its own, the **reported writer**
(`_reported_loop`), wakes when an outcome is queued and, if the monitor
has not written it within `REPORTED_WAIT` (1 s), writes it directly, under
`_emit_lock`; each is drained once, so it goes out once, whoever writes
it. With a monitor that answers, a ``reported`` is never written ahead of
a ``state`` sampled before its outcome was decided; written directly it
can be, which BRIDGE.md section 5 allows. The other way round is possible
and expected anyway: a replacement that fails before mpv leaves the old
item playing, its Stopped already sent and ``reported``, and later
``state`` events still show its session. A UI must not reopen a session
it has had ``reported`` for.

Not covered, by design:

- **The exit watchdog's forced exit** (`os._exit` at 9.5 s, a step that
  never returned): no sweep, no answer. It only runs after a shutdown was
  asked for, and after ``shutdown``/EOF the UI never reports (BRIDGE.md
  section 6). A crash is the UI's crash rule.
- **A Stopped the record skips** (`_reported`): the first attempt for that
  session emitted, so nothing.
- **``play_file``**: no play session, no request, nothing.
- **SyncPlay's starts** (``no_initial_timeline``, not in v1): they send a
  progress report instead of Playing; the hook marks the session opened when
  the start has taken.

**Transport while opening.** `pause`, `unpause`, `toggle_pause`, `seek`
and `seek_relative` sent while an item is opening (a `play` not yet handed
over, a start in progress, a `play_file` mpv does not have yet) are queued
on the play worker behind the start (`Bridge._transport`). Run at once, a
pause was a no-op or was undone by the start's final unpause, and a seek
lost to the resume offset. A `stop` or a newer `play` drops them with the
other queued jobs. While one is queued the state stays `opening`, so the UI
sees `opening` -> `paused`, not a `playing` in between. Each is for the
item that was opening -- the video in the hook, or the `play` whose video
the hook records once it exists -- and is dropped where it applies, under
the player's lock after the start, unless that item is playing: a
replacement that failed before mpv left the old item playing, and the seek
landed on it (`seek_failed_replace`).

**The sticky error** (`state: "error"`, an item that never opened) is
cleared by `play`, `play_file`, `stop` and a successful `connect` that
includes a server -- not by a window-only or failed `connect`
(`ErrorClearingTest`). The tracker also drops it when something is playing
and nothing is starting: then it was a replacement that failed before
reaching mpv, the state never showed `error`, and it must not surface when
the item that kept playing ends. A play-worker job superseded since it was
queued -- by a stop, a newer play, a switch -- reports no failure at all
(the play worker's handler, the ``play`` job's own, the hook's): a switch
retires the old client, which is what makes a slow old server's request
fail 2 s *after* the switch cleared the error, and the ``error`` event and
``state: "error"`` landed on the new server's session (``stale_error:connect``,
``hung_switch:item``).

Transport goes through `PlayerManager.run_action`, which never blocks the
caller while a start holds the player lock. `set_volume` with nothing
playing sets both of the shim's per-kind volumes (`video_volume`,
`music_volume`), since the shim applies one of those at every start.
`no_playback` is returned by `pause`/`unpause`/`toggle_pause`/`seek`/
`seek_relative` (unless something is opening), and by `next`/`previous`/
`set_audio`/`set_subtitle`/`skip_segment` when nothing is playing; `stop`, volume, mute and
fullscreen always succeed once connected.

Events come from `bridge_state.Tracker`, fed by the monitor's samples and
by observation-only wrappers on the player (`play()`, the queue's
`finished_callback`, `on_load_error`). An item's `ended` is emitted when the
player's current video changes away from it, with the reason marked on the
way (`eof` from the finished callback, `error` from an interrupted stream or
a failed restart) or else `replaced`/`stopped`. The finished callback's
mark is made under the player's lock, as the callback itself reads the
epoch: read before it, a start holding the lock had not yet bumped the
epoch, and every replaced item ended `eof`. Track indices are Jellyfin's:
mpv's `aid`/`sid` are mapped back through the shim's own tables
(`audio_uid`, `subtitle_uid`, `external_subtitles`); a transcode reports
the negotiated track, a burned-in or still-loading subtitle its selection.
`state` is emitted on any change except position and duration, which travel
in `position` (at most every 0.45 s). The monitor *polls* mpv
(`playback-time`, `duration`, `pause`, volume, mute, track ids) once per
sample and observes nothing: an observer on `playback-time` would be about
30 messages a second over JSON IPC.

**Segments and the queue's neighbours** (fluentfin-mpv, BRIDGE.md sections
4 and 5). `segments` is the shim's own list, `media.Video.intros` (fetched
inside the start, for the types not set to off), with each type's action
read from the settings at every sample (`ShimRuntime.segments_of`); `next`
is `state.queue`'s neighbours plus `settings.auto_play`. Both follow
`tracks`, for a new item always and later only when they change. The offer
(`current`) is worked out from the monitor's own position sample with the
rule in `bridge_protocol.offered_segment` (an `ask` segment, end exclusive,
so the skip's seek to the end closes it), and only while the item plays:
during a start or a restart mpv's position can still be the outgoing
item's. `skip_segment` checks the offer last sent (`Tracker.offer`, the one
the UI saw) and then does what the shim's own Skip button does,
`put_task(skip_intro)`. The monitor also writes the offer's button text to
`user-data/fluentfin/skip` after each tick (`Bridge._push_offer`, "" with
none or with `set_controls` `"ui"`), for the Skip button and prompt of the
bar mpv draws (section 4a); written on change and again for a new mpv.

## 6. Testing

Unit tests, no mpv (stdlib unittest, select with `-k`):

    python -m unittest discover tests -k test_bridge -k test_window_embed

None of these modules imports `player` except `test_window_embed_start`
(a start's auto-fullscreen decision, which lives in `_play_media`), which
builds a real mpv on import as `test_window_geometry` does. Discovery still imports every test
module to apply `-k`, and on Windows -- no xvfb -- the ones that import
`player` either start a real mpv or, with no mpv to find, fail at import
and are reported as errors whatever `-k` says. Point `APPDATA` and
`LOCALAPPDATA` at a scratch folder for such a run: those imports create the
shim's config tree under the real ones otherwise.

`tests/test_bridge_protocol.py` (framing, stdout isolation in a real child
process, validation, redaction, translations), `tests/test_bridge.py`
(dispatch and error codes, connect and `window_locked`, the play worker,
the hooks, `Tracker` over several steps, stdin EOF and `shutdown` through
the real entry point, the replace report and everything that can come after it, transport
while opening, a shutdown during the first `connect`, the shutdown's one
deadline against each server endpoint that can hang, a switch against a
hung progress report, a ``stop`` against one, a next whose start does not
take, a failure of a superseded job, the play-session record, mpv's
replies and process at the exit, the excepthooks, `connect`'s user check
and the client guard through the real apiclient, transport behind an
answered ``stop``, the deferred stop deciding under the player's lock,
``reported`` on every way a play session ends -- and its outcome from the
real apiclient's status (`StopOutcomeTest`), and written without the
monitor's tick (`ReportedWithoutATickTest`) -- the item before a next,
previous or advance that does not take (`NextTargetNeverOpenedTest`), a
restart that does not take (`RestartNotTakenTest`), the end of an item a
``play`` is replacing (`PlayOwnsWhatComesNextTest`, whose fake models the
end-of-file poll rescue; and `HeldEndAtTheStopTest`: a stop or the
shutdown finding it held -- the shutdown with mpv not answering, whose fake
reads wait out the reply bound, and a stop as the play's item fetch
returns), the load hooks on
their start's own thread (`LoadHooksAreTheirStartsTest`), a report with no
media source (`TimelineOptionsWithoutASourceTest`), PlaybackInfo answers
through the real apiclient (`PlaybackInfoAnswerTest`), and `ReportPathsTest`,
which reads the player's source for the one place each report request is
made and checks every report goes through ``self``, where the wrappers
are), `tests/test_mpv_error_catch.py` (an mpv error caught where
`player.py` says it is: an ``except`` tuple holding `_mpv_errors` itself
raises TypeError; imports ``player``, so off-screen),
`tests/test_window_embed.py` (the embedded window's options and
`player_window` paths, each with a standalone control, and the fullscreen
put-back racing the owner's answer -- one `window_embed.fullscreen_lock`
around both). The fakes there model what the tests are named after -- mpv's
global `http_header_fields`, the stop reports, `run_action` deferring when
the lock is held, the report lock the stop report and a progress report
take, the start flagged only after `play()` has reset its cancel flag, a
`stop()` that returns early once mpv has dropped the file, the Playing a
successful start sends and the Stopped request on a report worker that
can run it at once or hold it (a report still pending), answered by a fake
API as a server would (a status, an error, a wait) -- and
`PlayerNamesTest` pins the player methods they stand in for against the
real classes' source.

The smoke test runs the real bridge, with mpv, and needs no server:

    backend\.venv\Scripts\python.exe backend\tools\bridge_smoke.py [--out DIR] [--only basic|eof|standalone|embedded]

It checks the server-less protocol, stdin EOF, `play_file` of
`av://lavfi:testsrc` standalone (state, advancing `position`, pause, volume,
`window_locked`, `stop`, a dead-server `connect` whose token appears in no
output), and -- on Windows -- the same inside a ctypes window of its own
(not topmost), with a `PrintWindow` screenshot of that window drawn by the
window itself, whatever lies over it (printed path; look at it), a
double-click and an `f` key each producing
`fullscreen_request`, and the window left unmoved and untitled; neither
`play_file` session emits a ``reported`` event (it has no play session).
Exit status 0 when every check passed. ``reported`` against a server is
the probes' (`tools/backend-harness/rr/rr4probe.py` in the repository
root: `exits`, which counts it per session against the fake server's
Stopped requests; ``rr5probe.py`` for the item left behind by a start that
does not take, and a ``reported`` with mpv not answering; ``rr6probe.py``
for a ``play`` racing the queue's own advance, a restart that finds no
stream, and a PlaybackInfo 500's error code; ``rr7probe.py`` for an end
held through a slow item fetch -- its CPU and log bounded -- and ended by a
stop, a second play, a switch or the shutdown; ``rr8probe.py`` for that
held end at the shutdown or stdin EOF with mpv suspended: answered, exit 0,
inside the budget).

## 7. Not covered here

- Playback against a real Jellyfin server (PlaybackInfo, the auth header to
  mpv, progress reports showing on FluentFin's session) -- verified only by
  the code path being the shim's own; first real check is through the UI.
- libmpv: nothing in the bridge assumes the external backend, but only the
  external one exists on the development machine.
- A frozen, windowless (GUI-subsystem) build: `isolate_stdout` sets the
  Win32 standard handle explicitly for it, which a console process does not
  need.
