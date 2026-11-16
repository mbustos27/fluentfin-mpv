# FluentFin MPV

FluentFin's Windows UI with jellyfin-mpv-shim's playback engine. Local project, GPLv3.

- `ui/` — [FluentFin](https://github.com/insomniachi/FluentFin) (C#, WinUI 3, .NET 10): login,
  servers, libraries, item pages, settings, the Jellyfin dashboard. Adds a player choice
  **"MPV (jellyfin-mpv-shim)"**.
- `backend/` — [jellyfin-mpv-shim](https://github.com/jellyfin/jellyfin-mpv-shim) (Python):
  run headless in **bridge mode**, it does PlaybackInfo and the transcode decision, track
  selection, mpv with the shim's OSC (trickplay, skip intro), and progress reporting.
- `docs/BRIDGE.md` — the stdio JSON-lines protocol between them (the contract).
- `docs/UPSTREAM.md` — the upstream commits this was built from, and how to pull updates.
- `docs/KNOWN-ISSUES.md` — what is open, accepted or never verified (a real Jellyfin server among them).
- `tools/README.md` — the test harnesses: off-screen backend suite, probes, mutation runners,
  the UI self-test gate and the end-to-end harness against the real backend.

When you sign in with the MPV player selected, FluentFin starts the backend in the
background as a child process (it dies with the app) and hands it the session it is
already logged in with, so Play does not wait for it; mpv draws inside FluentFin's player
page from the first play on. The server sees one session: FluentFin's. Signing out shuts
the backend down. Start-up and play timings are in the log as `[perf]` lines.

## Requirements on this PC
- .NET SDK 10 (`winget install Microsoft.DotNet.SDK.10`)
- Python 3.14 with the backend venv at `backend\.venv` (`pip install -e backend[all]`)
- mpv (`winget install mpv-player.mpv-CI.MSVC`), found on PATH

## Run
```powershell
.\run-dev.ps1            # build and launch
.\run-dev.ps1 -SelfTest  # play mpv's test pattern inside the player page, no server needed
```

## Agents
`fluentfin-mpv-dev` maintains both halves; `fluentfin-mpv-reviewer` is the read-only
reviewer to run after meaningful changes.
