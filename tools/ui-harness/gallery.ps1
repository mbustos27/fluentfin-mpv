<#
.SYNOPSIS
  The README's screenshots (docs\screenshots): Home in the four themes, and in one theme the film page, the series page,
  the player with Playback stats, the skip question, Collections and Settings at "Look up lists online". Taken by the
  off-screen harness against the showcase library (fakeserver\showcase\) at 1920 x 1080, then packed small (WebP, with
  thumbnails) and laid out on a contact sheet to look at before anything is committed.

.DESCRIPTION
  - Every run goes through run-offscreen.ps1, so it waits for a harness slot (NIXIE_HARNESS_PARALLEL) like any other run,
    on a private desktop inside a kill-on-close job: nothing shows on the desktop and nothing makes a sound.
  - The fake server runs the showcase library (-Showcase; default fakeserver\showcase\) in place of the generated one.
    Its problems (a picture without a free licence or a Commons source, say) fail the run's checks.
  - -Video is the public-domain episode the player shots play (sources.tsv's
    episode-beverly-hillbillies-s01e01.ogv, 16 MB, kept outside the repository). The detail stage needs it: its picture,
    from -VideoStart seconds in, is encoded into this gallery's own cache (<Scratch>\cache\media; the shared harness
    cache is never touched), silent, with no subtitle text. The home stage plays nothing.
  - -Scratch holds the cache, the data folders, the runs and the packed images; it must be outside the repository and
    outside Nixie's own data folders (run-offscreen.ps1 refuses those too). Each run gets a fresh data folder.
  - Nothing is written to docs\screenshots unless -Publish is given. Look at <Scratch>\gallery\contact.png first: no
    server name, user name, path or token may show in any shot.
  - -Part runs one stage: home (the four Home shots), detail (the rest), collections (the Collections shot alone), pack
    (packs what the runs left), or all.

.EXAMPLE
  .\gallery.ps1 -Scratch C:\scratch\gallery -Exe C:\scratch\build\Nixie.exe -Video C:\scratch\episode-beverly-hillbillies-s01e01.ogv
  .\gallery.ps1 -Scratch C:\scratch\gallery -Part pack -Publish
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)][string]$Scratch,
    [string]$Showcase,
    [string]$Video,
    [int]$VideoStart = 200,
    [string]$Exe,
    [switch]$Build,
    [string]$Python,
    [ValidateSet('DriveIn', 'Cinematic', 'Editorial', 'Classic')][string[]]$Themes = @('DriveIn', 'Cinematic', 'Editorial', 'Classic'),
    [ValidateSet('DriveIn', 'Cinematic', 'Editorial', 'Classic')][string]$DetailTheme = 'DriveIn',
    [ValidateSet('all', 'home', 'detail', 'collections', 'pack')][string]$Part = 'all',
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'offscreen\prepare.ps1')
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
# the harness's own rule for a scratch folder: not a drive root, not in or holding Nixie's (or FluentFin's) data
# folders, and not the repository or anything in it
try { $Scratch = Resolve-HarnessDataDir $Scratch $repo }
catch { throw ($_.Exception.Message -replace '-DataDir', '-Scratch') }
if (-not $Showcase) { $Showcase = Join-Path $PSScriptRoot 'fakeserver\showcase' }
$Showcase = [IO.Path]::GetFullPath($Showcase)
if (-not (Test-Path $Showcase)) { throw "-Showcase: nothing at $Showcase" }
if (-not $Python) { $Python = Join-Path $repo 'backend\.venv\Scripts\python.exe' }
$Python = [IO.Path]::GetFullPath($Python)
# a worktree has no venv of its own: pass the main checkout's (absolute), as run-offscreen.ps1 asks
if (-not (Test-Path -LiteralPath $Python -PathType Leaf)) { throw "no python at $Python; pass -Python <a venv with the backend's dependencies>\Scripts\python.exe (absolute)" }
New-Item -ItemType Directory -Force $Scratch | Out-Null
$cache = Join-Path $Scratch 'cache'
$runner = Join-Path $PSScriptRoot 'run-offscreen.ps1'
$then = Join-Path $PSScriptRoot 'offscreen\then-gallery.ps1'
$hidden = Join-Path $repo 'tools\backend-bench\hidden_run.py'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$built = $false

# the player's video: the episode's picture in the gallery's own cache (run-offscreen.ps1 takes <Cache>\media as it is,
# since media.json says silent)
if ($Part -in 'all', 'detail') {
    if (-not $Video) { throw "-Video: the detail stage plays the public-domain episode (sources.tsv); give its path" }
    $Video = [IO.Path]::GetFullPath($Video)
    if (Test-Under $Video $repo) { throw "-Video must be outside the repository (video files are never committed)" }
    if (-not (Test-Path -LiteralPath $Video -PathType Leaf)) { throw "-Video: no file at $Video" }
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
    $mpv = (Get-Command mpv.exe -ErrorAction SilentlyContinue).Source
    if (-not $mpv) { throw "mpv.exe is not on PATH (winget install mpv-player.mpv-CI.MSVC)" }
    $media = Join-Path $cache 'media'
    New-Item -ItemType Directory -Force $media | Out-Null
    Write-Host "gallery: the player's video from $([IO.Path]::GetFileName($Video)) at $VideoStart s into $media (off-screen, silent)"
    & $Python -B -I $hidden --log (Join-Path $Scratch 'media-build.log') --timeout 900 --desktop "nixie-gallery-$PID" -- `
        $Python -B -I (Join-Path $PSScriptRoot 'fakeserver\media.py') --mpv $mpv --out $media --source $Video --start $VideoStart | Out-Host
    if (-not (Test-Path (Join-Path $media 'media.json'))) { throw "the player's video was not built (see $Scratch\media-build.log)" }
}

function Invoke-GalleryRun([string]$Theme, [string]$What) {
    $name = "$What-$Theme-$stamp"
    $env:FAKESERVER_SHOWCASE = $Showcase
    $env:NIXIE_LAYOUT = $Theme
    $env:GALLERY_PART = $What
    $run = @{
        DataDir = Join-Path $Scratch "data\$name"; Out = Join-Path $Scratch "runs\$name"; Script = @('wait:2'); NoA11y = $true
        Then = $then; Python = $Python; Cache = $cache
    }
    if ($What -eq 'detail') { $run.Setting = @('ShowPlaybackStatsButton=true') }
    if ($Exe) { $run.Exe = $Exe } elseif ($Build -and -not $script:built) { $run.Build = $true; $script:built = $true }
    Write-Host "gallery: $What in $Theme -> $($run.Out)"
    try { & $runner @run } finally { Remove-Item Env:FAKESERVER_SHOWCASE, Env:NIXIE_LAYOUT, Env:GALLERY_PART -ErrorAction SilentlyContinue }
    $result = Join-Path $run.Out 'result.json'
    if (Test-Path $result) {
        # result.json's checks: { "<name>": { "ok": ..., "detail": ... } }
        $checks = (Get-Content $result -Raw | ConvertFrom-Json).checks
        foreach ($c in @($checks.PSObject.Properties)) {
            if ($c.Name -like 'gallery*' -and -not $c.Value.ok) { Write-Warning "$($c.Name) ($($c.Value.detail))" }
        }
    }
}

if ($Part -in 'all', 'home') { foreach ($t in $Themes) { Invoke-GalleryRun $t 'home' } }
if ($Part -in 'all', 'detail') { Invoke-GalleryRun $DetailTheme 'detail' }
# 1.7.5: the Collections shot alone (no player, so no -Video)
if ($Part -eq 'collections') { Invoke-GalleryRun $DetailTheme 'collections' }

# pack: the newest shot of each name, as WebP under 300 KB with a 640 px thumbnail, and a contact sheet
$pack = @('-B', '-I', (Join-Path $PSScriptRoot 'gallery_pack.py'), '--runs', (Join-Path $Scratch 'runs'), '--out', (Join-Path $Scratch 'gallery'))
if ($Publish) { $pack += @('--publish', (Join-Path $repo 'docs\screenshots')) }
& $Python @pack
if ($LASTEXITCODE -ne 0) { throw "gallery_pack.py failed ($LASTEXITCODE)" }
Write-Host "gallery: look at $(Join-Path $Scratch 'gallery\contact.png') before committing: no server name, user name, path or token in any shot."
