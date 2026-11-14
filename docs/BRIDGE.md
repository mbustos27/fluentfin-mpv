# Bridge protocol (UI ⇄ backend), version 1

FluentFin (C#, `ui/`) is the face of the app: login, servers, libraries, item pages,
settings, the Jellyfin dashboard. jellyfin-mpv-shim (Python, `backend/`) is the
playback engine: PlaybackInfo and the transcode decision, track selection, mpv and its
options/shaders/SVP, the shim's OSC (trickplay, skip intro), progress reporting to the
server, next-episode handling.

The UI starts the backend as a **child process** and talks to it over **stdio**. This
file is the contract both sides build against. Change it here first, then on both sides.

## 1. Process and framing

- Started by the UI as
  `<python> -I -m jellyfin_mpv_shim.bridge --config-dir <dir> [--log-file <path>]`
  (dev, from `backend\.venv`), or later as a frozen `fluentfin-mpv-backend.exe` with the
  same arguments. **argv and the environment carry no secrets.**
- Default config dir: `%LOCALAPPDATA%\FluentFin\mpv-backend\` — the shim's `conf.json`,
  `mpv.conf`, `input.conf`, shader packs live there. It is separate from a standalone
  jellyfin-mpv-shim install so the two never share a lock, credentials or settings.
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
- **stdin EOF = shutdown** (final progress report, then exit 0).

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
- A response means the command was **accepted**, not that its effect finished
  (`play` returns once the shim has the request, not when the file is open). Effects are
  observed through events.

Error codes: `not_connected`, `bad_args`, `unknown_cmd`, `auth_failed`,
`server_unreachable`, `mpv_failed`, `no_playback`, `window_locked`, `internal`.

## 3. Lifecycle

1. Backend starts, emits `{"event":"hello","data":{"protocol":1,"backend":"jellyfin-mpv-shim","version":"<CLIENT_VERSION>","bridge":"<bridge version>"}}`
   once it is reading stdin.
2. UI sends `connect` (§4). Until it succeeds, every other command except `ping`,
   `get_state` and `shutdown` fails with `not_connected`.
3. UI sends `play`, transport commands, etc. Backend emits events.
4. UI sends `shutdown` (or closes stdin). Backend stops playback, sends the final
   progress report, replies, exits 0.

`connect` may be sent again (server or user switch); it replaces the server client and
stops any playback first. Its `window` part is fixed for the process lifetime: a
different `window` on a later `connect` fails with `window_locked`, and the UI restarts
the backend to change it.

## 4. Commands

| cmd | args | notes |
|---|---|---|
| `ping` | — | result `{}` |
| `connect` | see below | result `{"mpv": "<mpv version>", "mpvBackend": "libmpv" \| "external"}` |
| `play` | `itemIds: string[]` (required, ≥1), `startIndex: int=0`, `startPositionTicks: int=0`, `mediaSourceId: string?`, `audioStreamIndex: int?`, `subtitleStreamIndex: int?` | Mirrors the server's remote **Play/PlayNow** command and goes down the same path in the shim (`event_handler.start_playback`), so transcode decisions, remembered tracks, next-episode and progress reporting are the shim's own. Replaces anything playing. |
| `pause` / `unpause` / `toggle_pause` | — | |
| `stop` | — | Stops and reports stop to the server. State → `idle`. |
| `next` / `previous` | — | Within the queue. |
| `seek` | `positionTicks: int` | Absolute. |
| `seek_relative` | `seconds: number` | Negative = back. |
| `set_audio` | `index: int` | Jellyfin stream index. |
| `set_subtitle` | `index: int` | `-1` = off. |
| `set_volume` | `volume: int` 0–100 | |
| `set_mute` | `muted: bool` | |
| `set_fullscreen` | `fullscreen: bool` | Embedded mode: tells the backend the UI's window state (for the OSC button); standalone mode: mpv goes fullscreen. |
| `get_state` | — | result = the `state` snapshot (§5). |
| `shutdown` | — | result `{}`, then the process exits 0. |

`connect` args:

```jsonc
{
  "protocol": 1,
  "server": {"url": "https://jf.example:8920", "id": "<ServerId>", "name": "Home"},
  "user":   {"id": "<UserId, N format>", "name": "alice"},
  "accessToken": "<token>",
  "device": {"id": "<DeviceId FluentFin uses>", "name": "<device name>"},
  "client": {"name": "FluentFin", "version": "1.0.0"},
  "window": {"mode": "embedded", "hwnd": 1311242}   // or {"mode": "standalone"}
}
```

- The backend reuses **FluentFin's identity** (token, device id/name, client name), so the
  server sees one session — FluentFin's — playing. It talks to the server over **REST
  only and does not open the server websocket**: FluentFin owns the session websocket and
  forwards remote-control commands (Playstate, seek, volume) to the backend as bridge
  commands. Remote **Play** commands (casting to FluentFin) become a bridge `play`.
- `embedded`: mpv renders into `hwnd` (`wid`), a child window FluentFin owns and positions
  over the player page. mpv never resizes, moves, retitles or fullscreens that window;
  the shim's fullscreen key/double-click becomes a `fullscreen_request` event and the UI
  does it. `standalone`: mpv has its own top-level window, as the shim does today.
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
| `tracks` | `{"audio": [Track], "subtitle": [Track]}` when an item opens; Track = `{"index", "language"?, "title"?, "codec"?, "channels"?, "isDefault", "isForced"?, "isExternal"}` |
| `ended` | `{"itemId", "reason": "eof" \| "stopped" \| "error" \| "replaced"}` per item; after the last item, state → `idle` |
| `fullscreen_request` | `{"fullscreen": bool}` (embedded mode only) |
| `error` | `{"code", "message", "fatal": bool}`; `fatal` = the backend is about to exit |

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
  "window": "embedded" | "standalone" | null
}
```

## 6. Responsibilities when the mpv player is selected

| Concern | Owner |
|---|---|
| Login, servers, users, browsing, item pages, settings, dashboard | UI |
| Session websocket, remote-control intake | UI (forwards to backend) |
| PlaybackInfo, device profile, direct play vs transcode, stream URL | backend |
| Track defaults and remembered tracks | backend (UI may pass explicit indices) |
| Progress / start / stop reports to the server | **backend only** — the UI must not also report |
| Media segments (skip intro/credits), trickplay preview | backend (shim OSC) |
| Video surface | backend's mpv inside the UI's child window |
| Window, fullscreen, back navigation | UI |
| SyncPlay | not supported with the mpv player in v1; UI disables it in that mode |
