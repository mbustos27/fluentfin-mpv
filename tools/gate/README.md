# The basics gate (Nixie 1.7.5, design rule 12)

One command runs every basic path at real shape, off-screen, and fails on any failure with a one-line reason each.
`tools\release\verify-package.ps1` runs it, so a failing gate fails the release build. What it covers: the basics
sweep's classes 1-8 (sign-in and sessions; servers; every menu; rows, the top bar and the way up; item pages; playback
and the player's controls; settings; lifecycle).

## The command

    tools\gate\run-gate.ps1 -Exe <Nixie.exe> -Out <scratch outside the repo> -Python <absolute venv python>
        [-DevExe <a Debug Nixie.exe of the same code> | -Build] [-Backend <backend dir>]
        [-Checks id,id] [-Classes 1,2] [-Owners g2] [-Themes Classic,Cinematic] [-Parallel 3] [-QueueMinutes 120] [-List]

- `-Exe`: the release package's `app\Nixie.exe` or a Debug build, alike. A release package is told by its `Nixie.dll`
  carrying `FluentFinMpvRelease` (as build-release.ps1 checks). It never listens to the harness's dev hooks (the harness
  pointer's clicks, `FLUENTFIN_MPV_GLASS_ALWAYS_ACTIVE`, the fake update feed, `NIXIE_PRESETS`), so a check marked
  `Dev = $true` runs on `-DevExe`, or on the gate's own build (`-Build`: this checkout's Debug x64 into
  `<Cache>\gate-build`, off-screen, as `run-offscreen.ps1 -Build` builds). A Dev check with a release `-Exe` and neither
  fails ("needs a dev build of the same code"): a check never passes by being skipped.
- `-List` parses every manifest and prints the checks (id, class, owner, kind, themes, dev, tier, its expected minutes);
  exit 0. Run it after adding an entry.
- **The budget (2 hours, 1.7.5).** The jobs run **longest first** on `-Parallel` slots (default `NIXIE_HARNESS_PARALLEL`, 3), by
  each job's own run time measured before: `tools\gate\timings.json` (`"<id>|<theme>": seconds`, committed) and any
  `-Timings <an earlier gate.json>`; a job never measured counts a quarter of its `Minutes`. Before the jobs start the
  runner prints the plan (`gate: plan: <slot-minutes>, about <N> min real time on <P> slots, the longest job ...; budget
  -Budget (120) min`, "OVER by" when it doesn't fit) and warns when fewer slots are free than it will use (other runs, the
  TV's emulator). The end line gives the **real time** beside the plan. `-SaveTimings` writes this run's own run times
  into `timings.json` (commit it after a full run on a quiet machine).
- `Tier = 'nightly'` in an entry keeps it out of a release run (`-Nightly`, or the entry named in `-Checks`, runs it);
  every entry is `release` by default. Use it only for a variant of a check (another theme or size) whose class stays
  covered on every release build.
- Exit **0** every job passed, **1** a job failed, **2** the gate couldn't run (bad input, a manifest error).
- `-Python` absolute (a relative one fails under another working folder); `-Backend` defaults to this checkout's
  `backend\`.
- A revert check: `-Exe` an older build, `-Backend` that build's own `backend\`, `-Checks <id> -Themes Classic`; record
  that it fails, and why.

## Files

    tools\gate\run-gate.ps1                the runner
    tools\gate\run-job.ps1                 one job in its own hidden PowerShell (the runner's; checks don't call it)
    tools\gate\gate-common.ps1             helpers a check may dot-source
    tools\gate\manifest\<owner>.psd1       each owner's checks (g1.psd1, g2.psd1, g3.psd1, g4.psd1, back.psd1, c180.psd1, m181.psd1): one file
                                           per owner, so no two streams write the same file; the runner reads them all
    tools\gate\checks\<class>-<name>.ps1   a check's own script, e.g. 1-signin-methods.ps1

## A manifest entry (a PowerShell data file, read with `Import-PowerShellDataFile`)

    @{ Checks = @(
        @{
            Id       = 'signin-sessions'     # unique, [a-z0-9-]; every Check name of the run starts with it
            Class    = 1                      # the basics sweep's class, 1-8
            Owner    = 'g1'
            What     = 'one line: what the user sees that the check asserts'
            Kind     = 'then'                 # 'then' or 'script' (below)
            Then     = 'tools\ui-harness\offscreen\then-174-sessions.ps1'   # repo-relative (Kind then)
            Before   = ''                     # optional, repo-relative: run-offscreen.ps1 -Before
            Steps    = 'wait:1'               # run-offscreen.ps1 -Script (the sign-in always comes first)
            SignIn   = 'quickconnect'         # or 'password' (the fake server's generated test user)
            Settings = @()                    # run-offscreen.ps1 -Setting Name=Value
            RunArgs  = @{ WithoutMpv = $true }  # run-offscreen.ps1's other parameters: Latency, LatencyRule, WithoutMpv,
                                              # GlassFollowsActivity, TabStops, QaServer
            Env      = @{ SS_PARTS = 'bs6' }  # environment for the run: the fake server's shapes (FAKESERVER_*), a script's parts
            Themes   = 'all'                  # 'all' (Classic, DriveIn, Cinematic, Editorial) or @('Cinematic')
            Sizes    = 'gate'                 # GATE_SIZES: 'gate' = 1280x720,max (the sizes that matter); 'none' (one
                                              # size: the window as it opens, maximised); or 'WxH,WxH'
            Tier     = 'release'              # (optional) 'nightly': a variant kept out of release runs (above)
            Dev      = $false                 # needs a dev build's hooks (above)
            Minutes  = 20                     # the run's time limit
            Fails173 = 'what fails on 1.7.3 (its revert check), or why it can''t'
        }
    ) }

- **Kind `then`**: the runner runs `tools\ui-harness\run-offscreen.ps1 -Exe <exe> -DataDir <Out>\data\<id>-<theme>
  -Out <Out>\runs\<id>\<theme> -Script <Steps> -Then <Then> -NoA11y` (and `RunArgs`) with `NIXIE_LAYOUT=<theme>`,
  `GATE_ID`, `GATE_THEME`, `GATE_SIZES`, `GATE_OUT` (the run's folder) and the entry's `Env` set. The `-Then` script is dot-sourced by `offscreen\drive.ps1`, with
  its helpers (`Check`, `Say`, `Find-Ui`, `Wait-Ui`, `Invoke-Element`, `Key`, `Open-Nav`, `Open-Card`, `Step-Close`,
  `Start-App`, `Shot`, `Capture-Page`, `Invoke-Server`, `Get-Requests`; `offscreen\states-common.ps1`'s `Set-Rules`,
  `Submit-Address`, `Focused-Element`, `Capture-State`). **It reports through drive.ps1's `Check`**, into result.json's
  `checks`. Existing then-scripts are registered as they are, not rewritten.
- **Kind `script`**: `Script = 'tools\...\x.ps1'`, `Args = @{ Name = 'value' }`. Called as `powershell -File <Script>
  -Out <dir> -Exe <exe> -Python <py> -Cache <dir> -Backend <dir> -Theme <theme> [-<Name> <value> ...]`. It takes its
  slots through `run-offscreen.ps1` (or `offscreen\prepare.ps1`'s `Enter-HarnessRunSlot` and `Exit-HarnessRunSlot`, for a
  run that isn't run-offscreen.ps1's), exits 0 when it passed, and writes `<Out>\result.json` with
  `{ "checks": { "<id>: <what>": { "ok": true, "detail": "..." } } }` (drive.ps1's shape). A wrapper such as
  rows-gate.ps1 or keys-matrix.ps1, which takes `-Themes`, gets a small `checks\<class>-<name>.ps1` that maps `-Theme`
  to it.

## What a check asserts, and how it names it

- What the user sees: focus, the page shown, the item reached and in view, opacity and Visibility together. Never an
  internal flag.
- Where rule 12 says: four themes (`Themes = 'all'`); the sizes (`Get-GateSizes`, `Set-GateSize`); a live resize
  (`Invoke-GateLiveResize`).
- It fails on the build where the bug lived (its revert check, in `Fails173`).
- Check names are `"<id>: <what the user sees>"`, with the case in brackets where a run loops:
  `"signin-methods [password, restart]: Home, signed in as the user"`.

## gate-common.ps1

Dot-source it from a `then` script in `tools\gate\checks\`: `. (Join-Path $PSScriptRoot '..\gate-common.ps1')`.

- `Get-GateSizes`: GATE_SIZES as a list (`max` = maximised: 2560 x 1392 on a 2560 x 1440 monitor); `gate` = 1280x720 and
  max. A page check runs at those two; everything else at one size (`Sizes = 'none'`).
- `Set-GateSize <'WxH'|'max'>`: the window restored and sized (or maximised) and the layout settled; returns
  `@{ W; H; Dpi; Label }` (its client size and DPI), which the check names in its detail.
- `Invoke-GateLiveResize [-Steps 6]`: the window's size dragged through several sizes in a row without waiting (a live
  resize), then back where it was.
- `Get-GateTheme`: the run's theme.
- `Format-GateSize $state`: "1280x720 (client 1264x681, 96 dpi)", for a check's detail.
- **DPI:** the private desktop has the monitors' own scale. 150% can't be set off-screen without changing the user's
  display, so the gate runs at the PC's scale and `Set-GateSize` reports it.

## Slots

The runner holds no slot itself. It starts at most `-Parallel` jobs at once (default `NIXIE_HARNESS_PARALLEL`, 3). Each
job is a `run-offscreen.ps1` in its own hidden PowerShell, which takes one of the machine's slots for its run and gives
it back when the run ends; a job waits up to `-QueueMinutes` (120) for one, since other runs share them. A job is one
check in one theme. A job that failed while Windows minimized its window (a monitor dropping off the bus: result.json's
`display.events`) runs once more.

## The report

- Console: `PASS|FAIL <id> [<theme>] (<passed>/<checks>, <seconds> s)` as each job ends; at the end `gate: <passed>/<jobs>
  jobs passed in <minutes> min`, and one line per failed job: `FAIL <id> [<theme>]: <the first failed check, without the
  id>: <its detail> (+N more)`.
- `<Out>\gate.json`: `{ exe, dev_exe, release_package, started, minutes, slots, jobs: [ { id, class, owner, theme, ok,
  reason, passed, checks, seconds, exit, out } ] }`.
- `<Out>\gate.md`: one row per check, one column per theme (PASS, FAIL or -), the failures under it.
- Each job's own folder: `<Out>\runs\<id>\<theme>\` (result.json, drive.log, shots), its console
  `<Out>\runs\<id>\<theme>.console.txt`, and the app's logs in `<Out>\data\<id>-<theme>\Logs`.

## Who registers which existing script

- g1: sign-in (`then-174-sessions`, `then-174-qc-remember`), closes (`then-173-close*`), the self-test (the off-screen
  `tools\release\selftest-offscreen.ps1`; `tools\ui-harness\selftest-gate.ps1` runs on the desktop, so it can't be here).
- g2: menus (`before-174-servermenu`).
- g3: rows (`rows-gate`), Collections (`then-160-collections`), stress (`then-174-scrollstress`); back's `back-gate.ps1`
  in `manifest\back.psd1` at its merge.
- g4: the player's controls (`then-174-hud`), `keys-matrix`.
- c180 (1.8, BH-180-m3): `then-180-week`, `then-180-tiles`, `then-180-reverse`, `then-180-focus` (BH-180-M1), `then-180-off`
  (BH-180-M2); nightly `then-180-census` and `then-180-first-row`. test_gate checks every `then-18x` script is in a manifest.
