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
  (dev, from `backend\.venv`), or in a release as the frozen
  `<app>\backend\fluentfin-mpv-backend.exe` with the same arguments (PyInstaller, one folder: the
  interpreter runs in that process, so the job holds it from its first instruction;
  `tools\release`). **argv and the environment carry no secrets.**
- Config dir: `%LOCALAPPDATA%\Nixie\mpv-backend\` (the UI passes it as `--config-dir`) — the shim's `conf.json`,
  `mpv.conf`, `input.conf`, shader packs live there. It is separate from a standalone
  jellyfin-mpv-shim install so the two never share a lock, credentials or settings.
  The UI's own data folder is `%LOCALAPPDATA%\Nixie\` (not upstream FluentFin's
  `%LOCALAPPDATA%\FluentFin\`), so this fork never shares saved servers, passwords or
  settings with an upstream FluentFin install. (It was `%LOCALAPPDATA%\Seaglass\` in 1.1.x and
  `%LOCALAPPDATA%\FluentFinMpv\` before 1.1.0; the UI copies the newest of them to `Nixie\` once at start-up,
  never moving or deleting the old one.) A dev or test run started with
  `Nixie.exe --data-dir <folder>` (or `FLUENTFIN_MPV_DATA_DIR=<folder>`) keeps both under
  that folder instead (`<folder>\mpv-backend\`, `<folder>\Logs\`; `tools/README.md`).
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
  connection fails `not_connected` before its args are checked. <!-- 1.6 player --> `get_stats` (1.6) is
  read beside them, never in that order: it never delays another command (§4). <!-- end 1.6 player -->

Error codes: `not_connected`, `bad_args`, `unknown_cmd`, `auth_failed`,
`server_unreachable`, `mpv_failed`, `mpv_not_found`, `no_playback`, `window_locked`, `internal`,
`not_allowed` (1.6 player).
`mpv_not_found` (added in protocol 1 without a version bump) is `connect`'s answer when the mpv the backend
would start itself (`embedded`/`standalone`; never `attached`) cannot be found: no `mpv_ext_path` naming a
file, none on PATH, none in a WinGet install. Any other failure to start mpv stays `mpv_failed`. A UI that does not
know the code treats it as any failed `connect`; FluentFin says what to do (use its own controls, or install mpv).
<!-- 1.6 player -->
`not_allowed` (1.6, added in protocol 1 without a version bump) is the code of a non-fatal `error` event: the server will
not convert the video for this account, so a lower quality (`set_quality`) is not applied and the item plays on as it was.
Its message is written to be shown as it is. A UI that does not know the code shows it as any non-fatal `error`.
<!-- end 1.6 player -->

## 3. Lifecycle

1. Backend starts, emits `{"event":"hello","data":{"protocol":1,"backend":"jellyfin-mpv-shim","version":"<CLIENT_VERSION>","bridge":"<bridge version>"}}`
   once it is reading stdin.
2. UI sends `connect` (§4). Until it succeeds, every other command except `ping`,
   `get_state` and `shutdown` fails with `not_connected`. The UI may start the backend and
   send it right after sign-in, with `prewarm` (§4), and only `play` when the user does (with an
   `attached` window it starts the backend then and connects at the first play); it
   shuts such a backend down when the user signs out. A backend that dies while idle
   leaves nothing to report (§6) and is started again by the next `play`.
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
| `next` / `previous` | — | Within the queue. One at a time (1.2.0): while a `next`, a `previous`, a `play` or the backend's own advance is still opening its item, another `next`/`previous` is `no_playback` (until then the item being left still counted as playing, and a second move went on from it: an extra episode). A `next` from inside the item's end credits (an `Outro` segment starting in its second half) marks the item played, after its Stopped report (Jellyfin marks played only from 90 %, and credits often start earlier). |
| `seek` | `positionTicks: int` | Absolute. |
| `seek_relative` | `seconds: number` | Negative = back. |
| `set_audio` | `index: int` | Jellyfin stream index. |
| `set_subtitle` | `index: int` | `-1` = off. |
| `set_volume` | `volume: int` 0–100 | |
| `set_mute` | `muted: bool` | |
| `set_subtitle_size` | `percent: int` 50-200 | fluentfin-mpv: the subtitle text size in percent. Sets mpv's `sub-scale` to `percent / 100` now (text and ASS subtitles) and keeps it as the shim's own `subtitle_size` setting, so every later item starts with it and mpv's own controls use the same value. Always succeeds once connected, also with nothing playing. The UI sends its remembered value when it takes over the controls (§4 `set_controls`). Added in protocol 1 without a version bump. |
| `set_speed` | `speed: number` 0.5-2 | 1.2.0: the playback speed (mpv's `speed`; 1 is normal). Written to mpv now; mpv keeps it for every later item (it is not re-created between queue items) until the next `set_speed`, so the UI decides when it goes back to 1: FluentFin's controls keep it for the queue they play and send `1` when they take over a new playback (§4 `set_controls`). Not in `state`, and not held by the backend: an mpv the backend restarts starts at 1. Always succeeds once connected, also with nothing playing; `bad_args` outside 0.5-2 or not a number. Added in protocol 1 without a version bump: an older backend answers `unknown_cmd` and the speed stays 1. |
| `set_subtitle_delay` | `seconds: number` -30 to 30 | 1.2.0: the subtitle delay in seconds (mpv's `sub-delay`; positive shows them later, negative earlier). Written to mpv now and kept by mpv for later items like `set_speed`: FluentFin's controls send `0` when they take over and when another item starts, so a delay is per item. Not in `state`. Always succeeds once connected; `bad_args` outside -30 to 30 or not a number. Added in protocol 1 without a version bump. |
| `set_fullscreen` | `fullscreen: bool` | Embedded and attached mode: tells the backend the UI's window state (for the OSC button); standalone mode: mpv goes fullscreen. |
| `set_controls` | `controls: "ui" \| "mpv"` (required), `subtitleInset: number = 0` (0 to 0.5) | Who draws the player controls. `"mpv"` (what a backend starts with): the shim's OSC, subtitles where mpv puts them. `"ui"`: the UI draws its own over the video, mpv's OSC is hidden and stays hidden (across items, and after mpv is restarted), and `subtitleInset` is the share of the video area's height the UI's bottom controls cover **now**: mpv's subtitles move up by that share of the window's height (`sub-pos`, from the value mpv had), and back with `0`. The UI sends it again whenever the controls or their height change. With `"mpv"`, `subtitleInset` is ignored. |
| `set_skip_options` | `onlineLookup: bool?`, `learnIntros: bool?`; 1.6 skip: `learnSkips: bool?`, `segmentActions: object?`, `ruleAction: string?` (each optional: left out or null = unchanged) | fluentfin-mpv: the Skip intro settings, held for every later item (a backend starts with `onlineLookup` false, `learnIntros` true). `onlineLookup`: when the server, the file's chapters gave no segment of a type, look the episode up online in the background (never delays playback; the answer is added to `segments` when it arrives). Sources: TheIntroDB and, for anime, AniSkip (MyAnimeList id, mapped through arm.haglund.dev when the server has none). What is sent: the show's public provider id, the season, the episode and the runtime, and a `Nixie/<version>` User-Agent (`Seaglass/<version>` before 1.2.0); never the server address, user, token, file path or a title. Only the episode being played is asked for, TheIntroDB answers are cached in memory for the session only, and a 429 pauses that service for its Retry-After. `learnIntros`: remember a forward seek of 30 to 150 s that starts in the first 10 minutes of an episode with no intro from any other source, per series and season, and offer it on later episodes of that season; 1.5.1: a season whose skips by hand of one part (the first of each episode, lengths within 15 s) are 10 s or more apart has an intro that moves, and is offered no learned spot (kept per server, user, show and season; from 1.6 with the season's learned intro in `skip-rules.json`, where 1.5.1's marks are migrated). Until 1.5 the learned ranges were in `learned-intros.json` in the backend's `--config-dir`, and the UI forgot them by deleting that file; from 1.6 they are in `skip-rules.json` (below) and forgotten with `forget_skip_rules`. Needs `connect` first (`not_connected`); `bad_args` for a non-bool. Added in protocol 1 without a version bump. **1.6 skip** (added in protocol 1 without a version bump; an older backend ignores the fields, §2): `learnSkips: bool?` is `learnIntros` renamed (Settings' "Learn skips from my seeks"; it wins when both are sent) and also turns on the suggestions (`skip_suggestion`, §5); the learned ranges and the skip rules now live in `skip-rules.json` (below), and `learned-intros.json` is migrated into it once, at the first `connect` that has an account, and renamed `learned-intros.json.migrated`. `segmentActions: {type: "off"\|"ask"\|"always"}?` sets what the backend does with some of the segment types `Intro`, `Outro`, `Commercial`, `Preview`, `Recap` (others unchanged; a backend starts with `Commercial`, `Preview` and `Recap` off); held in memory for every later item, never written over the user's own settings. `ruleAction: "ask"\|"always"?`: what a skip rule with no action of its own does (a backend starts with `"ask"`, a Skip button). `bad_args` for an unknown type, action or a non-bool. |
| `set_auto_play` | `autoPlay: bool` | 1.2.0: whether the backend plays the next queue item by itself when an item ends (the shim's `auto_play` setting, true in a new config). Held for every later item, in memory (not written to `conf.json`); the `next` event's `autoPlay` follows it. The UI sends the user's choice (Jellyfin's "play the next episode automatically", `EnableNextEpisodeAutoPlay`) before every `play`, in either controls mode, and `false` while the user has cancelled the next item's countdown (FluentFin's controls), its own choice again when another item starts. With it off, an item's end is the end of playback (state `idle`). Needs `connect` first (`not_connected`); `bad_args` for a non-bool. Added in protocol 1 without a version bump: an older backend answers `unknown_cmd` and keeps its own setting. |
| `skip_segment` | — | fluentfin-mpv: skips the segment the backend offers now (`segments.current`, §5): seeks to that segment's end (not another one the position may also be in). Result `{"skipped": bool}`: `false`, and nothing done, when none is on offer or the backend's last sample of the position (about every 0.5 s) is outside it; `true` is the seek accepted, and it is left out if the position has left the segment by the time it would run. The backend's S key and its bar's Skip button do the same. Added in protocol 1 without a version bump. |
| `skip_rules` | `serverId: string?`, `userId: string?` (both or neither) | **1.6 skip.** The skip rules of the account connected (or, before `connect`, of `serverId` and `userId`: the ids Jellyfin shows any client, no secret, so Settings can list them without a playback). Result `{"shows": [{"seriesId", "name"?, "rules": [{"id", "anchor", "startS", "endS", "action", "origin", "created"}], "learnedIntros": [{"id", "season", "startS", "endS", "moves"}], "refused": bool}]}`, sorted by name. A rule's `anchor` is `"start"` (`startS` and `endS` are seconds from the episode's start) or `"end"` (seconds before the episode's end, `startS > endS >= 0`); `action` is `"ask"`, `"always"` or null (follows `set_skip_options.ruleAction`); `origin` is `"marked"` (the viewer marked it) or `"learned"` (a yes to a `skip_suggestion`); `created` is Unix seconds. `learnedIntros` are the one-seek intros per season (`set_skip_options.learnIntros`); `moves`: skips in that season were 10 s or more apart, so its spot is no longer offered. `refused`: the viewer answered "Never for this show". The rules live on this PC in `skip-rules.json` in the backend's `--config-dir`, keyed per server, user and show; the backend is its only writer (the UI asks it). Without `connect` and without both ids: `not_connected`. Added in protocol 1 without a version bump. |
| `add_skip_rule` | `anchor: "start"\|"end"`, `startS: number`, `endS: number`, `action: "ask"\|"always"?`, `seriesId: string?`, `seriesName: string?` | **1.6 skip.** A part to skip in every episode of a show (the player's "Skip a part every episode"): `seriesId` (Jellyfin id) or, left out, the show playing (`no_playback` when no episode of a show plays). Times as in `skip_rules`, 0 to 4 h, at most 600 s long, `startS` before `endS` for `"start"` and farther from the end than `endS` for `"end"`; else `bad_args`. A rule within 2 s of one already there (same anchor) replaces it. Applies to the episode playing at once (a new `segments` follows). Result `{"ruleId"}`. Needs `connect` (`not_connected`). Added in protocol 1 without a version bump. |
| `forget_skip_rules` | `seriesId: string?`, `ruleId: string?`, and `serverId`/`userId` as `skip_rules` | **1.6 skip.** Forgets one rule or learned intro (`ruleId`, from `skip_rules`), one show's rules, learned intros, candidates and refusal (`seriesId`), or with neither all of the account's. Result `{"forgotten": <shows changed>}`; the episode playing loses a forgotten rule's segment at once. Connection as `skip_rules`. Added in protocol 1 without a version bump. |
| `answer_skip_suggestion` | `id: string` (the `skip_suggestion`'s), `answer: "yes"\|"not_now"\|"never"`, `action: "ask"\|"always"?` | **1.6 skip.** The viewer's answer to a `skip_suggestion` (§5): `yes` makes it a rule (`origin` `"learned"`, the two skips' average span; it applies to the episode playing at once), result `{"ruleId"}`; `not_now` drops it (the same skip in two more episodes asks again), `never` stops all suggestions for that show (until it is forgotten); result `{}` for both. `bad_args` for an id the backend does not know (any more): each is answered once. Needs `connect` (`not_connected`). Added in protocol 1 without a version bump. |
<!-- 1.6 player -->
| `set_quality` | `bitrate: int?` (bits per second, 100000 to 1000000000; null or left out = no cap), `original: bool = false` | 1.6: the quality of the item playing, for the rest of that item (not a setting: the next item starts with its default, `set_quality_defaults`). **Auto** (`bitrate` null, `original` false): what the backend does without a choice, the shim's own settings (today's behaviour). **Original** (`original` true): no cap at all, so the server sends the file as it is wherever this player can open it. **A cap** (`bitrate`): the profile's maximum bitrate, compared by the server with the file as jellyfin-web's quality menu does: a file under the cap still plays directly, one over it is converted down (Transcode). `original` true with a bitrate is `bad_args`. Answered at once; on the play worker the item then restarts at its position **as the same viewing**, exactly as a track change that needs a new stream (§5 `reported`: no second Playing, progress under the new `playSessionId`); a choice that is already in effect does nothing. Refused, with an `error` event `not_allowed` (non-fatal; message: `This server doesn't allow a lower quality for your account. Playing at the original quality.`) and the item playing on untouched: a cap under the file's bitrate for an account whose policy says it may not have video converted (`EnableVideoPlaybackTranscoding` false, read from `connect`'s `Users/Me` answer, no request of its own; an unknown policy does not refuse); and, after the restart, a cap the server ignored (the file still sent as it is although it is over the cap), whose choice then reads Original. A change that finds no stream is the §5 rule for a track or quality change that cannot be applied (`error` naming "the quality", the item plays on). Needs `connect` first (`not_connected`, before the args are checked), then `bad_args`, then `no_playback` when nothing is playing. The choice in effect is in the `stream` event (§5). Added in protocol 1 without a version bump: an older backend answers `unknown_cmd`. |
| `set_quality_defaults` | `home: Quality?`, `away: Quality?` (each optional: left out or null = unchanged); Quality = `{"bitrate": int?, "original": bool}` as `set_quality`'s args | 1.6: the default quality at home and away, held in memory for every later item (a backend starts with Auto for both). Each item's **first** start takes the one for where its server is, by the shim's local-or-remote rule (the address it reaches the server at, `utils.is_local_domain`); a restart, a retry of the same item and the item playing now keep theirs. Auto is the shim's own settings, as without the command; the backend never changes a default by itself. The UI sends its Settings before every `play`. Needs `connect` first (`not_connected`); `bad_args` for anything else in either. Added in protocol 1 without a version bump. |
| `get_stats` | — | 1.6: the Playback stats, read from mpv **when asked and only then** (the UI asks about once a second while its panel is open, never while it is closed): result `{"itemId", "playMethod", "transcodeReasons": [string], "container", "streamFormat", "videoCodec", "videoProfile", "streamVideoCodec", "sourceWidth", "sourceHeight", "width", "height", "frameRate", "hdr", "hardwareDecoding", "droppedFrames", "delayedFrames", "audioCodec", "audioChannels", "audioSampleRate", "audioTrack", "subtitleFormat", "subtitlesOff": bool, "streamBitrate", "measuredBitrate", "bufferSeconds", "bufferBytes", "networkSpeed", "isLocal": bool, "mpvVersion"}`, every field but the two bools nullable. From the server's negotiation: the play method and its reasons, the file's container, the video stream's codec, profile and size (`source…`) and its range type (`hdr`: the server's `VideoRangeType`, e.g. `SDR`, `HDR10`, `DOVI`), the audio track's title, the subtitle's codec, and `streamBitrate` (what the server sends: a transcode's target, else the file's own; bits per second). From mpv: `streamFormat` (`file-format`), `streamVideoCodec` (`video-format`: what mpv decodes, a transcode's own codec), `width`/`height` (the decoded picture), `frameRate`, `hardwareDecoding` (the decoder in use, null for software), `droppedFrames` (output and decoder drops), `delayedFrames`, the audio's codec, channels and sample rate as decoded, `measuredBitrate` (video plus audio, bits per second), `bufferSeconds` and `bufferBytes` (the demuxer's cache ahead), `networkSpeed` (bytes per second) and `mpvVersion`. No ids but the item's, no URL, path or header. Takes no lock (a quality restart may hold the player's). **It never delays another command:** it is read beside the command queue, one read at a time (a `get_stats` sent while one is still being read is answered at once with `mpv_failed`), and answered within 1.5 s: with what mpv has answered by then, or `mpv_failed` when mpv has not answered (no property is asked after that; a property read already sent to an mpv that does not answer can hold the next `get_stats` back, answered `mpv_failed` at once, until it returns). A `stop`, `play` or `connect` ends a read in progress: its `get_stats` is answered `no_playback` at once and mpv is asked nothing more for it; the shutdown answers it `internal` before its own answer. Needs `connect` (`not_connected`), then `no_playback` when nothing is playing (a `play_file` playback included). Added in protocol 1 without a version bump: an older backend answers `unknown_cmd`, and the UI shows that the stats are not available. |
<!-- end 1.6 player -->
| `get_state` | — | result = the `state` snapshot (§5). |
| `shutdown` | — | result `{}`, then the process exits 0 (1 when the 8 s deadline in §3 ran out). |
| `play_file` | `url: string`, `title: string?` | **Dev only**: exists only when the bridge was started with `--dev`, otherwise `unknown_cmd`. Plays a local file or an mpv URL such as `av://lavfi:testsrc=size=1280x720:rate=30` in the same mpv/window with no server and no reporting, and still emits `state`/`position`/`ended`. Works before `connect` (but after the window is known — with `--dev`, `connect` may be sent with only `protocol` and `window`). Used to verify embedding without a Jellyfin server. |

Command details:
- `pause`, `unpause`, `toggle_pause`, `seek`, `seek_relative` → `no_playback` when nothing is
  playing or opening; `next`, `previous`, `set_audio`, `set_subtitle`, `skip_segment` →
  `no_playback` when nothing is playing. <!-- 1.6 player --> So are `set_quality` and `get_stats` (1.6). <!-- end 1.6 player --> `stop`, volume, mute, fullscreen, `set_subtitle_size`, `set_speed`, `set_subtitle_delay` and `set_controls` always succeed once
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
  "window": {"mode": "embedded", "hwnd": 1311242},  // or {"mode": "attached", "ipc": "fluentfin-mpv-4242-0011223344556677"}, or {"mode": "standalone"}
  "prewarm": true                                    // optional, default false
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
  long as the backend process: when that process ends for any reason (its `shutdown`, stdin EOF, a
  crash, a kill) the UI ends that mpv too, off its UI thread, so nothing plays on after the backend that
  played and reported it (on a `shutdown` whose final report ran out of time the backend only closed
  its pipe; the UI's end of the mpv is what stops it). The next `connect` gets a new mpv on a new pipe.
  An mpv that ended by itself is replaced on a new pipe as well, so the next `connect` has a different
  `window` and the UI restarts the backend (`window_locked`). A pipe that does not answer fails the
  first `connect` with `mpv_failed`. The UI sends `set_controls` `"ui"` for its controls.
- `prewarm` (true or false; absent or null = false; anything else is `bad_args`): the UI connects **ahead of
  playback** (after sign-in, so the first `play` skips the backend start and `connect`). With
  an `embedded` window, mpv is started **without its video output**: no window of mpv's
  exists inside `hwnd` (no input queues attached across the processes, no GPU surface) until
  the first `play` or `play_file` is accepted, which has mpv build it alongside that start
  (a start that then never reaches mpv leaves an idle, black one); from then on the surface
  stays for the process, exactly as without `prewarm`. Everything else
  is the ordinary `connect`: the same result, the same `Users/Me` check (the only request a
  pre-warmed backend makes while idle: it reports nothing), the window fixed for the
  process. Only the `connect` that starts mpv (the first one with a `window`) reads it; later
  ones, `standalone` and `attached` ignore it (an attached mpv is the UI's, and lives only as long as
  the backend it was offered to: whether one exists before the first play is the UI's choice.
  FluentFin then only starts the backend ahead, and sends `connect` at the first play, which
  makes a new mpv for that backend). A UI that never sends it
  gets today's behaviour, and a backend that does not know it ignores it (unknown fields, §2):
  the surface then exists from `connect` on.
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
| `segments` | fluentfin-mpv: `{"itemId", "segments": [Segment], "current": int?}`; Segment = `{"type", "startTicks", "endTicks", "action", "source"}`. `source` is where it came from, by precedence per type: `"server"` (media segments), `"chapters"` (the file's chapters named Intro, Opening, OP, Recap, Previously, Credits, Ending, ED, End Credits and the like), `"online"` (`set_skip_options`) or `"learned"` (a range the viewer skipped by hand earlier in the season); the UI may say so quietly, as a tooltip. **1.6 skip:** chapters named Commercial, Ad break, Advertisement, Promo or Sponsor (whole words, any case) are `Commercial` segments with `source` `"chapters"`, each at most 5 minutes, any number per item. And `"rule"`: a show's skip rule (`skip_rules`, §4), which also carries `ruleId`, `origin` (`"marked"`/`"learned"`) and `anchor`; its `action` is the rule's own (or `set_skip_options.ruleAction`), whatever its type is set to, so it is listed with every type off. Its `type` is `Commercial` (the UI's "Skip extra"), or `Intro`/`Outro` when it covers at least half of an intro or credits segment from another source, which it then replaces (never two offers for one part; a learned range under a rule is dropped and the rule stays `Commercial`). An `"ask"` start rule is offered from 5 s before its start (a bumper is not the same length twice), an `"always"` one skips exactly its span. A rule's edge (the end of a start rule, the start of an end rule) may move once while the item plays (a new `segments`): it snaps to the nearest black frame or silence within 3 s (1.5 s once corrected), found in what mpv has already cached (`dump-cache` of those few seconds, analysed by a windowless, silent mpv, deleted at once; kept under the backend's own folder in the system's temp folder, whose leftovers (a crash) it removes as it next starts, and whose analysers it ends on a stop and at its exit; nothing more is read from the server); a seek of the viewer's within 15 s after the backend skipped a start rule's part moves that rule's end to where it lands. Items with no rule get exactly the segments they got before. `type` is Jellyfin's `MediaSegmentType` (`Intro`, `Outro`, `Commercial`, `Preview`, `Recap`); `action` is what the backend's settings do with it: `"ask"` (offer a skip) or `"always"` (the backend skips it by itself, in either controls mode). Types set to off are neither fetched nor listed. `current` is the index in `segments` of the one on offer now: an `ask` segment the position is in (start inclusive, end exclusive; 1.6 skip: not its last 0.1 s, as mpv's exact seek to a segment's end can land a frame short of it), else null; with `set_controls` `"mpv"` the backend's bar offers it too (a Skip button, a prompt, the S key), with `"ui"` only the UI does. Sent after `tracks` for every Jellyfin item that starts (an empty list included), and whenever the list or `current` changes; `current` follows the position at the backend's sampling (about every 0.5 s). Never for `play_file`. Added in protocol 1 without a version bump |
<!-- 1.6 player -->
| `stream` | 1.6: how the item is served and the quality chosen for it: `{"itemId", "playSessionId", "playMethod", "transcodeReasons": [string], "bitrate", "sourceBitrate", "sourceWidth", "sourceHeight", "quality": {"bitrate": int?, "original": bool}, "isLocal": bool}`. `playMethod` as in `state`; `transcodeReasons` the server's own words (`TranscodeReasons`, e.g. `ContainerBitrateExceedsLimit`), empty when it plays directly; `bitrate` what the server sends in bits per second (a transcode's target, else the file's own, null when unknown); `sourceBitrate`, `sourceWidth` and `sourceHeight` the file's (its first video stream), null when unknown; `quality` the choice in effect for this item (`set_quality`: Auto is `{"bitrate": null, "original": false}`); `isLocal` whether its server is at home (the rule `set_quality_defaults` uses). Sent after `segments` and `next` for every Jellyfin item that starts, and whenever it changes once no start or restart is negotiating (a quality or track change that took, a refused cap reading Original). Never for `play_file`. Added in protocol 1 without a version bump. |
<!-- end 1.6 player -->
| `next` | fluentfin-mpv: `{"itemId", "nextItemId"?, "previousItemId"?, "autoPlay": bool}` after `segments` for every Jellyfin item that starts, and whenever one of them changes: `state.queue`'s neighbours, which `next` and `previous` play (absent at an end of the queue). 1.2.0: an episode at the queue's start (one started from Next Up or Continue Watching) gets the series' earlier episodes put into the queue as it starts, in the background, so a later `next` (and `state.queue`) names a `previousItemId`; `previous` also still fills the queue itself when pressed before that, and `autoPlay`, whether the backend plays `nextItemId` by itself when this item ends (its `auto_play` setting; false without a next item). Ids only: the UI resolves names and artwork itself. Never for `play_file`. Added in protocol 1 without a version bump |
| `ended` | `{"itemId", "reason": "eof" \| "stopped" \| "error" \| "replaced"}` per item; after the last item, state → `idle` |
| `reported` | `{"itemId", "playSessionId", "outcome": "sent" \| "failed" \| "none"}` once the backend is **done with its Stopped report** for that play session: `sent` = the server accepted it (a 2xx status, not merely "no exception"), `failed` = it errored, timed out or was abandoned (old client retired, shutdown deadline), `none` = the session never reached the server (no Playing was sent, e.g. it failed while opening), so there is nothing to stop. At most one per `playSessionId` for the life of the process (a server that reuses ids gets one in all). Written on the backend's next state tick after the attempt ends, after that tick's `state` events, or directly within about 1 s if no tick completes (an mpv that stops answering); so a `state` sampled earlier can still show the session after its `reported` (the UI keeps it closed, §6). Everything pending is written before the `shutdown` answer or the EOF exit, and a session whose Stopped is still pending then gets `failed`. A restart (a track or quality change that renegotiates the stream) goes on as the same viewing: the server gets progress reports under the new `playSessionId`, no second Playing, and no Stopped for the old session (1.5; upstream sent Playing again, two starts on the server for one viewing). A restart that leaves the item on a new `playSessionId` gets `failed` for the old session when the restart's start has finished (which can be up to the playback timeout after the new PlaybackInfo), whether the new session then opened, was cancelled or failed to load. A restart that never got a usable stream (its PlaybackInfo request failed, or it returned no playable source or URL) keeps the old session, which goes on reporting and gets its normal Stopped. While a restart negotiates, `state` keeps showing the old `playSessionId`; the new one appears only once mpv has started loading the new stream (so a UI tracking sessions never drops the old one for a restart that then fails). After a server switch the old server's outcome may arrive after the `connect` answer (`failed` within the 2 s grace if it hangs). It can name a session the UI never saw in a `state` (e.g. `none` for one that failed before any snapshot); a UI ignores ids it is not tracking. A UI closes the session on **any** outcome value, including ones it does not know. Not tied to `ended` or `idle`, which can come earlier (a deferred `stop`) or later. Never for `play_file`. Added in protocol 1 without a version bump: a UI that ignores it loses nothing but the §6 crash rule's precision. |
| `skip_suggestion` | **1.6 skip:** `{"id", "itemId", "seriesId", "seriesName"?, "anchor": "start"\|"end", "startTicks", "endTicks"}` once per candidate: the viewer made the same forward seek of 10 s to 5 min near the start (first 10 minutes) or the end (last 10 minutes) of two episodes of a show (starts within 10 s, lengths within 10 % or 5 s), with `set_skip_options.learnSkips` on and the show not refused; seeks one after another (each leaving within 3 s of where the last landed, within 4 s of it) are one skip, judged 4 s after the last, or at once when it lands within 4 s of the item's end (so an end skip is asked while its episode still plays). Not asked again while it waits for an answer, also through the next item (a question about the promos after an episode stays up as the next one starts; the UI shows a question about the item playing or the one just before it); one still unanswered after that may be asked again at the next matching skip. App-made jumps never count (`skip_segment`, a segment skipped by itself, a seek to a chapter's start). `startTicks`/`endTicks` are the span in the item playing. The UI asks "Skip this every episode?" and answers with `answer_skip_suggestion`. Candidates are dropped after 30 days or 10 other episodes of the show. Never for `play_file`. Added in protocol 1 without a version bump. |
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
