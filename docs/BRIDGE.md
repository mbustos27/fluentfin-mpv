# Bridge protocol (UI ⇄ backend), version 1

FluentFin (C#, `ui/`) is the face of the app: login, servers, libraries, item pages,
settings, the Jellyfin dashboard. jellyfin-mpv-shim (Python, `backend/`) is the
playback engine: PlaybackInfo and the transcode decision, track selection, mpv and its
options/shaders/SVP, mpv's bar (uosc, or the shim's OSC: trickplay, skip intro), progress reporting to the
server, next-episode handling.

The UI starts the backend as a **child process** and talks to it over **stdio**. This
file is the contract both sides build against. Change it here first, then on both sides.

## 1. Process and framing

- Started by the UI as
  `<python> -I -m jellyfin_mpv_shim.bridge --config-dir <dir> [--log-file <path>]`
  (dev, from `backend\.venv`), or later as a frozen `fluentfin-mpv-backend.exe` with the
  same arguments. **argv and the environment carry no secrets.**
- Default config dir: `%LOCALAPPDATA%\FluentFinMpv\mpv-backend\` — the shim's `conf.json`,
  `mpv.conf`, `input.conf`, shader packs live there. It is separate from a standalone
  jellyfin-mpv-shim install so the two never share a lock, credentials or settings.
  The UI's own data folder is `%LOCALAPPDATA%\FluentFinMpv\` (not upstream FluentFin's
  `%LOCALAPPDATA%\FluentFin\`), so this fork never shares saved servers, passwords or
  settings with an upstream FluentFin install.
- Framing: UTF-8 JSON, **one object per line** (`\n`), no BOM.
  - UI → backend: the backend's **stdin**.
  - backend → UI: the backend's **stdout**, protocol only. At startup the bridge
    duplicates fd 1 for the protocol and points `sys.stdout` *and* fd 1 at stderr, so a
    stray `print` or a C library writing to stdout can never corrupt the stream.
  - **stderr** is free-form log text. The UI copies it to its own log file. Neither side
    ever logs an access token (redact `accessToken`, `api_key=`, `X-Emby-Token`,
    `Authorization` values).
- The UI puts the child in a Windows **Job Object with KILL_ON_JOB_CLOSE**, so the backend
  (and the mpv it may spawn) dies with the UI even on a crash.
- **stdin EOF = shutdown** (final progress report, then exit 0; same 8 s deadline as the
  `shutdown` command in §3, exit 1 when it runs out).

## 2. Message shapes

```jsonc
// request, UI -> backend
{"id": 7, "cmd": "seek", "args": {"positionTicks": 6000000000}}
// response, backend -> UI (exactly one per request, same id)
{"id": 7, "ok": true, "result": {}}
{"id": 7, "ok": false, "error": {"code": "no_playback", "message": "Nothing is playing"}}
// event, backend -> UI (no id)
{"event": "position", "data": {"positionTicks": 6001234567, "durationTicks": 72000000000}}
```

- `id` is a positive integer chosen by the UI, unique per process lifetime.
- Unknown `cmd` → `unknown_cmd`. Missing/ill-typed args → `bad_args`. Unknown fields in
  `args` are ignored (forward compatibility). Unknown events are ignored by the UI.
- Times are Jellyfin **ticks** (100 ns), integers. Track indices are Jellyfin
  **MediaStream indices**, not mpv track ids. Subtitle index `-1` = off.
- Item ids, in `play` args and in every event, are Jellyfin ids in **N format** (32 lowercase
  hex digits, no dashes). Receivers still compare ids case- and dash-insensitively.
- A response means the command was **accepted**, not that its effect finished
  (`play` returns once the shim has the request, not when the file is open). Effects are
  observed through events.

- A line that is not a JSON object with a positive integer `id` gets **no response**; the
  backend emits `error {"code":"bad_args","fatal":false}` and keeps reading. A valid `id`
  with a missing or non-string `cmd`, or non-object `args`, is answered `bad_args`.
- Responses are matched by `id` and need not follow request order. Commands other than
  `ping`, `get_state` and `shutdown` run in the order received. A command that needs a
  connection fails `not_connected` before its args are checked.

Error codes: `not_connected`, `bad_args`, `unknown_cmd`, `auth_failed`,
`server_unreachable`, `mpv_failed`, `no_playback`, `window_locked`, `internal`.

## 3. Lifecycle

1. Backend starts, emits `{"event":"hello","data":{"protocol":1,"backend":"jellyfin-mpv-shim","version":"<CLIENT_VERSION>","bridge":"<bridge version>"}}`
   once it is reading stdin.
2. UI sends `connect` (§4). Until it succeeds, every other command except `ping`,
   `get_state` and `shutdown` fails with `not_connected`.
3. UI sends `play`, transport commands, etc. Backend emits events.
4. UI sends `shutdown` (or closes stdin). Backend stops playback, sends the final
   progress report, replies, exits 0 (1 if the shutdown deadline below ran out).

Ordering: a `play` response may arrive before or after the first `state` event it causes,
and `connect` may emit `state` (`idle`). The UI must not rely on either order.

`connect` may be sent again (server or user switch); it replaces the server client and
stops any playback first. `window` is required on the first `connect` and may be omitted
later. `connect` verifies the token with one request on the client that will play:
`GET /Users/Me`, whose `Id` must equal `connect.user.id`. A mismatch, 401 or 403 →
`auth_failed`; no connection, timeout, 5xx, an empty or non-JSON body → `server_unreachable`.
Either way the backend is **not connected**.

`shutdown` runs under **one overall deadline of 8 s** in the backend (control worker, player
lock, report lock and the final Stopped report all share it): it answers and exits within
~9 s even on a hung server (then without the final report, exit 1). The UI hides its window
at once and sends `shutdown` (after waiting up to 3 s for a backend still starting or a `connect` still unanswered;
one that is still not done then is killed as part of the close; a pending `play` is not
waited for: the backend runs `shutdown` alongside it, under its own deadline), then waits
up to 10 s from sending it for the process to exit, then kills the job — so the backend's
budget always fits inside the UI's. Measured with each server endpoint hung in turn: answer
and exit by 8.04 s, no mpv left behind; likewise with mpv itself not answering, alone or
while a slow PlaybackInfo holds the player lock (by 8.05 s; the backend ends an external
mpv by its process handle as a last resort). A slow (3 s per request) server still gets
the normal shutdown with its Stopped report and exit 0.

A server switch (`connect` to another server while the old one hangs) retires the old
client first: its requests get at most 2 s more (counted from the switch, or from the
request's own start if later), no retries, so the old server may lose its Stopped report. Measured: `connect` answers within 2.1 s, and the new server's first
Playing report arrives within ~4 s of `play` (one more 2 s wait for a transcode's stream
release). With `--dev`, a window-only `connect` succeeds with
`connected: false`: transport commands and `play_file` work, `play` still fails
`not_connected`. Its `window` part is fixed for the process lifetime: a
different `window` on a later `connect` fails with `window_locked`, and the UI restarts
the backend to change it.

## 4. Commands

| cmd | args | notes |
|---|---|---|
| `ping` | — | result `{}` |
| `connect` | see below | result `{"mpv": "<mpv version>", "mpvBackend": "libmpv" \| "external"}` |
| `play` | `itemIds: string[]` (required, ≥1), `startIndex: int=0`, `startPositionTicks: int=0`, `mediaSourceId: string?`, `audioStreamIndex: int?`, `subtitleStreamIndex: int?` | Mirrors the server's remote **Play/PlayNow** command and goes down the same path in the shim (`event_handler.start_playback`), so transcode decisions, remembered tracks, next-episode and progress reporting are the shim's own. Replaces anything playing. |
| `pause` / `unpause` / `toggle_pause` | — | |
| `stop` | — | Stops and reports stop to the server. Answered at once; the stop and its report follow (a hung progress report never delays the answer or a following `connect`). State → `idle`. |
| `next` / `previous` | — | Within the queue. |
| `seek` | `positionTicks: int` | Absolute. |
| `seek_relative` | `seconds: number` | Negative = back. |
| `set_audio` | `index: int` | Jellyfin stream index. |
| `set_subtitle` | `index: int` | `-1` = off. |
| `set_volume` | `volume: int` 0–100 | |
| `set_mute` | `muted: bool` | |
| `set_fullscreen` | `fullscreen: bool` | Embedded and attached mode: tells the backend the UI's window state (for the OSC button); standalone mode: mpv goes fullscreen. |
| `set_controls` | `controls: "ui" \| "mpv"` (required), `subtitleInset: number = 0` (0 to 0.5) | Who draws the player controls. `"mpv"` (what a backend starts with): the shim's OSC, subtitles where mpv puts them. `"ui"`: the UI draws its own over the video, mpv's OSC is hidden and stays hidden (across items, and after mpv is restarted), and `subtitleInset` is the share of the video area's height the UI's bottom controls cover **now**: mpv's subtitles move up by that share of the window's height (`sub-pos`, from the value mpv had), and back with `0`. The UI sends it again whenever the controls or their height change. With `"mpv"`, `subtitleInset` is ignored. |
| `skip_segment` | — | fluentfin-mpv: skips the segment the backend offers now (`segments.current`, §5): seeks to its end, as the backend's own Skip button and S key do. Result `{"skipped": bool}`, `false` (and nothing done) when none is on offer. Added in protocol 1 without a version bump. |
| `get_state` | — | result = the `state` snapshot (§5). |
| `shutdown` | — | result `{}`, then the process exits 0 (1 when the 8 s deadline in §3 ran out). |
| `play_file` | `url: string`, `title: string?` | **Dev only**: exists only when the bridge was started with `--dev`, otherwise `unknown_cmd`. Plays a local file or an mpv URL such as `av://lavfi:testsrc=size=1280x720:rate=30` in the same mpv/window with no server and no reporting, and still emits `state`/`position`/`ended`. Works before `connect` (but after the window is known — with `--dev`, `connect` may be sent with only `protocol` and `window`). Used to verify embedding without a Jellyfin server. |

Command details:
- `pause`, `unpause`, `toggle_pause`, `seek`, `seek_relative` → `no_playback` when nothing is
  playing or opening; `next`, `previous`, `set_audio`, `set_subtitle`, `skip_segment` →
  `no_playback` when nothing is playing. `stop`, volume, mute, fullscreen and `set_controls` always succeed once
  connected (`set_controls` also on a `--dev` backend connected with only its window). With nothing
  playing, `set_volume` sets the volume the next item starts at, and `set_controls` holds for every
  item after it.
- `play` stream indices are a deliberate pick only when **both** `audioStreamIndex` and
  `subtitleStreamIndex` are given; one alone is treated like a remote Play's (the shim's
  own defaults/remembered tracks decide the other), so a missing subtitle index never
  silently means "subtitles off". The UI sends both when the user picked either.
- `connect.result.mpv` is mpv's `mpv-version` string verbatim. `Remux` is reported as
  `DirectStream`.

`connect` args:

```jsonc
{
  "protocol": 1,
  "server": {"url": "https://jf.example:8920", "id": "<ServerId>", "name": "Home"},
  "user":   {"id": "<UserId, N format>", "name": "alice"},
  "accessToken": "<token>",
  "device": {"id": "<DeviceId FluentFin uses>", "name": "<device name>"},
  "client": {"name": "FluentFin", "version": "1.0.0"},
  "window": {"mode": "embedded", "hwnd": 1311242}   // or {"mode": "attached", "ipc": "fluentfin-mpv-4242-0011223344556677"}, or {"mode": "standalone"}
}
```

- `server.name` and `user.name` are optional. `window.hwnd` is the HWND as an **unsigned
  32-bit integer** (0 < hwnd ≤ 4294967295), never negative; the backend rejects anything
  else with `bad_args` (a negative or truncated value makes mpv open its own window).
  `window.ipc` (attached mode) is the name of a Windows named pipe without `\\.\pipe\`: 1 to 100
  characters from `A-Z a-z 0-9 . _ -`, the first a letter or a digit; anything else is `bad_args`.
- v1 has **no enqueue**: remote PlayNext/PlayLast are not supported with the mpv player
  (the UI tells the user); remote PlayNow becomes a `play`.
- The backend reuses **FluentFin's identity** (token, device id/name, client name), so the
  server sees one session — FluentFin's — playing. It talks to the server over **REST
  only and does not open the server websocket**: FluentFin owns the session websocket and
  forwards remote-control commands (Playstate, seek, volume) to the backend as bridge
  commands. Remote **Play** commands (casting to FluentFin) become a bridge `play`.
- `embedded`: mpv renders into `hwnd` (`wid`), a child window FluentFin owns and positions
  over the player page. mpv never resizes, moves, retitles or fullscreens that window;
  the shim's fullscreen key/double-click becomes a `fullscreen_request` event and the UI
  does it. `standalone`: mpv has its own top-level window, as the shim does today.
- `attached` (fluentfin-mpv player controls, added in protocol 1 without a version bump): mpv already
  runs **in the UI's process** (libmpv in composition mode: no window, the UI shows its video in its
  own XAML and draws its controls over it) and answers mpv's JSON IPC on the pipe `ipc`. The backend
  attaches to it as an IPC client (the shim's external backend with `mpv_ext_start=False`) and never
  starts, quits, resizes, retitles or fullscreens it, nor ends it on `shutdown` (it only closes its
  pipe). No construction option reaches that mpv: the UI creates it with what the backend would
  have given (`vo=gpu-next`, `gpu-context=d3d11`, `d3d11-output-mode=composition`, `idle`,
  `force-window`, `osc=no`, no input bindings, `input-ipc-server`, the backend's config dir for
  `mpv.conf`); everything the shim sets at runtime (title, hwdec per file, tracks, OSC scripts) it
  still sets. Full screen is the UI's, as embedded. The UI's mpv listens before `connect` and lives as
  long as the backend process; one that ended is replaced on a new pipe, so the next `connect` has a
  different `window` and the UI restarts the backend (`window_locked`). A pipe that does not answer
  fails the first `connect` with `mpv_failed`. The UI sends `set_controls` `"ui"` for its controls.
- The backend must not bind any network port, start the tray, the in-player library
  browser, update checks, Discord presence, gamepad-to-browser navigation, or the
  single-instance listener of a standalone install.

## 5. Events

| event | data |
|---|---|
| `hello` | §3 |
| `state` | full snapshot, sent on any change of state, item, tracks chosen, volume, mute, fullscreen, queue position |
| `position` | `{"positionTicks", "durationTicks"}` ~every 500 ms while playing, only when changed |
| `item` | `{"itemId", "name", "type", "seriesName"?, "seasonNumber"?, "episodeNumber"?, "runTimeTicks"?}` when a queue item starts |
| `tracks` | `{"audio": [Track], "subtitle": [Track]}` when an item opens; Track = `{"index", "language"?, "title"?, "streamTitle"?, "codec"?, "channels"?, "isDefault", "isForced"?, "isHearingImpaired"?, "isExternal"}`. `title` is the server's `DisplayTitle` (or the stream's own title when there is none); `streamTitle` is only the stream's own title tag, absent when it has none ("SDH", "Commentary", "Signs & Songs"); `isHearingImpaired` is sent when the server says (Jellyfin 10.9+). `streamTitle` and `isHearingImpaired` were added in protocol 1 without a version bump: a UI that does not know them builds its labels from `title`. |
| `segments` | fluentfin-mpv: `{"itemId", "segments": [Segment], "current": int?}`; Segment = `{"type", "startTicks", "endTicks", "action"}`. `type` is Jellyfin's `MediaSegmentType` (`Intro`, `Outro`, `Commercial`, `Preview`, `Recap`); `action` is what the backend's settings do with it: `"ask"` (offer a skip) or `"always"` (the backend skips it by itself, in either controls mode). Types set to off are neither fetched nor listed. `current` is the index in `segments` of the one on offer now: an `ask` segment the position is in (start inclusive, end exclusive), else null; with `set_controls` `"mpv"` the backend's bar offers it too (a Skip button, a prompt, the S key), with `"ui"` only the UI does. Sent after `tracks` for every Jellyfin item that starts (an empty list included), and whenever the list or `current` changes; `current` follows the position at the backend's sampling (about every 0.5 s). Never for `play_file`. Added in protocol 1 without a version bump |
| `next` | fluentfin-mpv: `{"itemId", "nextItemId"?, "previousItemId"?, "autoPlay": bool}` after `segments` for every Jellyfin item that starts, and whenever one of them changes: the items `next` and `previous` would play now (`state.queue`'s neighbours; absent at an end of the queue), and `autoPlay`, whether the backend plays `nextItemId` by itself when this item ends (its `auto_play` setting; false without a next item). Ids only: the UI resolves names and artwork itself. Never for `play_file`. Added in protocol 1 without a version bump |
| `ended` | `{"itemId", "reason": "eof" \| "stopped" \| "error" \| "replaced"}` per item; after the last item, state → `idle` |
| `reported` | `{"itemId", "playSessionId", "outcome": "sent" \| "failed" \| "none"}` once the backend is **done with its Stopped report** for that play session: `sent` = the server accepted it (a 2xx status, not merely "no exception"), `failed` = it errored, timed out or was abandoned (old client retired, shutdown deadline), `none` = the session never reached the server (no Playing was sent, e.g. it failed while opening), so there is nothing to stop. At most one per `playSessionId` for the life of the process (a server that reuses ids gets one in all). Written on the backend's next state tick after the attempt ends, after that tick's `state` events, or directly within about 1 s if no tick completes (an mpv that stops answering); so a `state` sampled earlier can still show the session after its `reported` (the UI keeps it closed, §6). Everything pending is written before the `shutdown` answer or the EOF exit, and a session whose Stopped is still pending then gets `failed`. A restart (a track or quality change that renegotiates the stream; upstream never sends the old session's Stopped) that leaves the item on a new `playSessionId` gets `failed` for the old session when the restart's start has finished (which can be up to the playback timeout after the new PlaybackInfo), whether the new session then opened, was cancelled or failed to load. A restart that never got a usable stream (its PlaybackInfo request failed, or it returned no playable source or URL) keeps the old session, which goes on reporting and gets its normal Stopped. While a restart negotiates, `state` keeps showing the old `playSessionId`; the new one appears only once mpv has started loading the new stream (so a UI tracking sessions never drops the old one for a restart that then fails). After a server switch the old server's outcome may arrive after the `connect` answer (`failed` within the 2 s grace if it hangs). It can name a session the UI never saw in a `state` (e.g. `none` for one that failed before any snapshot); a UI ignores ids it is not tracking. A UI closes the session on **any** outcome value, including ones it does not know. Not tied to `ended` or `idle`, which can come earlier (a deferred `stop`) or later. Never for `play_file`. Added in protocol 1 without a version bump: a UI that ignores it loses nothing but the §6 crash rule's precision. |
| `fullscreen_request` | `{"fullscreen": bool}` (embedded mode only) |
| `error` | `{"code", "message", "fatal": bool}`; `fatal` = the backend is about to exit |

`ended.reason` is `replaced` when another item took over (`play`, `next`, `previous`). An
item that never opened gets no `item` or `ended`: only an `error` event and
`state: "error"` (also when it was the target of `next`, `previous` or an auto-advance: the
item before it, already reported stopped, does not stay current), which clears on the next `play`, `play_file`, `stop` or **successful**
`connect` that includes a server (a window-only or failed `connect` leaves it). A start
superseded by `stop`, `play` or `connect` reports no `error`, even if its request fails
later (so an old server's late failure never lands after a switch).
A track or quality change that cannot be applied (no usable stream for it) gets only an
`error` event (`mpv_failed`, or `server_unreachable` when the request itself failed) whose
message names the item. That message is written to be shown as it is (fluentfin-mpv, the event's
shape unchanged): what could not be changed (the audio track, the subtitles, the quality), the
item by the title the backend's bar shows, and the cause in plain words, e.g. `The audio track
of "Series — S6E4 · Episode" could not be changed: the server answered with an error. It goes
on playing as before.` No ids, URLs, status codes or exception text: those go to the backend's
log. The item plays on with the tracks it had and `state` keeps showing
the tracks actually playing. Known limit: for a **transcoded** item the server's old
transcode was already ended before the new one was negotiated (upstream behaviour), so it
plays on only as long as mpv's buffer lasts. A server answer that is not a usable PlaybackInfo (a 5xx, an empty
or non-JSON body) is `server_unreachable`, never `internal`.
`play_file` items have `itemId: null`.

Replacing playback: a `play` (or remote PlayNow) while an item is playing reports
**Stopped** for the outgoing item and releases its stream before the new one starts, exactly
as `next`/`previous` do, so the server closes that play session (resume point, played state,
transcode cleanup). Transport commands sent while `opening` are **queued** and applied once
the item has started (a `stop` in between drops them); they are not acknowledged and then lost.

`state` snapshot:

```jsonc
{
  "state": "idle" | "opening" | "playing" | "paused" | "error",
  "connected": true,
  "itemId": "…" | null, "mediaSourceId": "…" | null, "playSessionId": "…" | null,
  "playMethod": "DirectPlay" | "DirectStream" | "Transcode" | null,
  "positionTicks": 0, "durationTicks": null,
  "volume": 100, "muted": false,
  "audioStreamIndex": null, "subtitleStreamIndex": null,
  "queue": {"itemIds": [], "index": 0},
  "fullscreen": false,
  "window": "embedded" | "attached" | "standalone" | null
}
```

## 6. Responsibilities when the mpv player is selected

| Concern | Owner |
|---|---|
| Login, servers, users, browsing, item pages, settings, dashboard | UI |
| Session websocket, remote-control intake | UI (forwards to backend) |
| PlaybackInfo, device profile, direct play vs transcode, stream URL | backend |
| Track defaults and remembered tracks | backend (UI may pass explicit indices) |
| Progress / start / stop reports to the server | **backend only** — the UI must not also report. One exception: when the backend exits **unexpectedly** (crash or watchdog kill, not `shutdown`/EOF or a kill the UI's own close decided, whatever the exit code) while a play session is still **open**, the UI sends **one** Stopped report itself. A session is open from the first `state` that shows it (`opening`/`playing`/`paused` with a `playSessionId`) until a `reported` event for that `playSessionId` arrives — **not** until `idle` or `ended`, since a deferred `stop` answers and goes `idle` before its report is sent. The UI tracks only the latest session (a newer one replaces it; the backend reports the outgoing one itself). A session stays closed once its `reported` has arrived, even if a later `state` shows it again (a replacement that fails before mpv starts can leave the old item showing after its Stopped was sent), and a `reported` that arrives before any `state` of its session keeps that session from opening. Whether the exit was unexpected is decided by what happened **first**, the process exit or the UI's own shutdown, not by when the UI gets round to handling the exit. The report carries that session's `itemId`, `mediaSourceId`, `playSessionId` and the position from the latest `position` event **for that play session** (not the older `state` snapshot, and not a position of an earlier session of the same item), so the server closes the session and its transcode instead of waiting for its idle timeout. If no `position` event was seen for that session (e.g. it was still `opening`, whose snapshot always says 0, even when the same item played before), the report is sent with **`Failed = true`** and no position, so the server closes the session without overwriting the item's resume point. The report goes only to the server and user the session was played as: if the UI has since switched to another server or user, it is skipped and logged (the backend reports a switched-away session itself, §5). After `shutdown`/EOF, or a kill the UI's own close decided (a backend still starting, or with a `connect` unanswered, after 3 s), the UI never reports, even on exit 1 (the backend's report may still be in flight or its server hung); a session whose Stopped was still pending then gets none, an accepted cost of closing a hung backend. Once the close returns, any Stopped the UI owes has been attempted (it waits, bounded, for the exit it caused before deciding). Accepted cost: a backend that dies between sending its Stopped and emitting `reported` gets a second Stopped from the UI |
| Media segments (skip intro/credits), trickplay preview | backend: it fetches the segments and skips the types set to skip automatically, in either controls mode. With `set_controls` `"mpv"` its bar offers the others (a Skip button and an on-screen prompt) and draws the trickplay previews; with `"ui"` it draws neither, and the UI offers the skip from the `segments` event with `skip_segment` (so the two never disagree about which segments to offer) and asks the server for trickplay tiles itself |
| Player controls, and all input over the video | mpv's bar (`"mpv"`: FluentFin's key map, the wheel as volume ±5 held to 0–100 and never a seek, `backend/docs/bridge.md` §4a; a volume or mute change made there reaches the UI in `state`), or the UI (`"ui"`: mpv's window gets no input, the UI sends commands) |
| Video surface | backend's mpv inside the UI's child window (embedded), or the UI's own in-process mpv the backend attaches to (attached) |
| Window, fullscreen, back navigation | UI |
| SyncPlay | not supported with the mpv player in v1; UI disables it in that mode |
| End of playback | UI leaves the player page when `state` goes to `idle` after its playback; if the last `ended` reason was `error`, it stays and shows the error |

### Embedded input and hangs
mpv creates its `--wid` window **disabled** (`EnableWindow(FALSE)`), so the UI enables it on
the first mouse move and focuses it on click; the backend passes `--window-dragging=no`.
The two windows are in different processes and share an input queue, so a hung mpv can
freeze the UI's input: if mpv's window does not answer `SendMessageTimeout(SMTO_ABORTIFHUNG)`
for about 6–7 s (consecutive missed checks), the UI kills the backend's job and shows the
error. The kill path must never throw into the UI process. The backend must be **inside the
job from its first instruction** (created suspended or with `PROC_THREAD_ATTRIBUTE_JOB_LIST`,
then resumed): the venv `python.exe` is a launcher that starts the real interpreter at once,
and the interpreter puts mpv in a breakaway job of its own, so assigning the job after start
can leave the interpreter and mpv outside it. The host window also
hides while a flyout, dialog or notification is open over the video area (airspace).
