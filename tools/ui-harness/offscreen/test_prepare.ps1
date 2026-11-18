<#
  Tests of offscreen\prepare.ps1 (the off-screen run's folder checks and data-folder preparation). Starts nothing:
  it only writes files under -Out.

    powershell -NoProfile -ExecutionPolicy Bypass -File tools\ui-harness\offscreen\test_prepare.ps1 -Out <scratch>

  Prints PASS/FAIL per check; exit code 1 when any failed. -Prepare <copy of prepare.ps1> tests another copy.
#>
param(
    [Parameter(Mandatory)][string]$Out,
    [string]$Prepare
)
$ErrorActionPreference = 'Stop'
if (-not $Prepare) { $Prepare = Join-Path $PSScriptRoot 'prepare.ps1' }
. $Prepare
$Out = [IO.Path]::GetFullPath($Out)
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
New-Item -ItemType Directory -Force $Out | Out-Null
$script:failed = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if (-not $Ok) { $script:failed++ }
    Write-Host ("{0} {1}{2}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Name, $(if ($Detail) { ": $Detail" }))
}
function Throws([scriptblock]$Block, [string]$Like = '*') {
    try { & $Block | Out-Null; $false } catch { $_.Exception.Message -like $Like }
}
function New-Case([string]$Name) { $d = Join-Path $Out $Name; if (Test-Path $d) { Remove-Item -Recurse -Force $d }; New-Item -ItemType Directory $d | Out-Null; $d }

# --- Resolve-HarnessDataDir --------------------------------------------------------------------------------------------
$fakeLocal = Join-Path $Out 'localappdata'
$data = Join-Path $Out 'data'
Check 'a trailing backslash is dropped (quoted "X\" would reach FluentFin as X")' ((Resolve-HarnessDataDir ($data + '\') $repo $fakeLocal) -eq $data) (Resolve-HarnessDataDir ($data + '\') $repo $fakeLocal)
Check 'two trailing backslashes too' ((Resolve-HarnessDataDir ($data + '\\') $repo $fakeLocal) -eq $data)
$drive = [IO.Path]::GetPathRoot($Out)
Check "a drive root is refused ($drive)" (Throws { Resolve-HarnessDataDir $drive $repo $fakeLocal } '*drive root*')
Check "a drive root without its backslash too ($($drive.TrimEnd('\')))" (Throws { Resolve-HarnessDataDir $drive.TrimEnd('\') $repo $fakeLocal } '*drive root*')
Check 'the user''s Nixie folder is refused' (Throws { Resolve-HarnessDataDir (Join-Path $fakeLocal 'Nixie') $repo $fakeLocal } '*real FluentFin data folder*')
Check 'the user''s Seaglass folder is refused' (Throws { Resolve-HarnessDataDir (Join-Path $fakeLocal 'Seaglass') $repo $fakeLocal } '*real FluentFin data folder*')
Check 'the user''s old FluentFinMpv folder is refused' (Throws { Resolve-HarnessDataDir (Join-Path $fakeLocal 'FluentFinMpv\') $repo $fakeLocal } '*real FluentFin data folder*')
Check 'a folder holding it too' (Throws { Resolve-HarnessDataDir $fakeLocal $repo $fakeLocal } '*real FluentFin data folder*')
Check 'a folder in the repository is refused' (Throws { Resolve-HarnessDataDir (Join-Path $repo 'scratch') $repo $fakeLocal } '*outside the repository*')

# --- Initialize-HarnessSettings: a JSON array value is written as an array ------------------------------------------------
$case = New-Case 'settings-array'
[void](Initialize-HarnessSettings $case 'backend-cmd' @('_Items=[{"Name":"Anime","Persistent":true,"Commands":[]}]'))
$raw = Get-Content (Join-Path $case 'LocalSettings.json') -Raw
$items = ($raw | ConvertFrom-Json)._Items
Check 'a JSON array setting is written as an array (not {"value": ..., "Count": ...})' ($raw -notmatch '"Count"' -and $raw -match '"_Items":\s*\[' -and @($items).Count -eq 1 -and @($items)[0].Name -eq 'Anime') ($raw -replace '\s+', ' ')

# --- Initialize-HarnessSettings ----------------------------------------------------------------------------------------
$case = New-Case 'settings'
@'
{
  "MediaPlayerType": "Flyleaf",
  "Volume": 40,
  "Servers": [
    { "DisplayName": "fake", "PublicUrl": "http://127.0.0.1:60082", "LocalUrl": "http://127.0.0.1:60082", "Users": [] },
    { "DisplayName": "home", "PublicUrl": "https://jellyfin.example.com:8920", "LocalUrl": "http://192.168.1.20:8096", "Users": [ { "Username": "me" } ] },
    { "DisplayName": "mixed", "PublicUrl": "http://localhost:8096", "LocalUrl": "http://10.0.0.5:8096", "Users": [] },
    { "DisplayName": "v6", "PublicUrl": "http://[::1]:8096", "LocalUrl": "", "Users": [] },
    { "DisplayName": "no address", "PublicUrl": "", "LocalUrl": "", "Users": [] }
  ]
}
'@ | Set-Content -LiteralPath (Join-Path $case 'LocalSettings.json') -Encoding ascii
$r = Initialize-HarnessSettings $case 'backend-cmd' @('MpvPlayerControls=FluentFin', 'ShowSkipPrompt=false')
$s = Get-Content (Join-Path $case 'LocalSettings.json') -Raw | ConvertFrom-Json
$names = @($s.Servers | ForEach-Object { $_.DisplayName })
Check 'only loopback saved servers are kept' (($names -join ',') -eq 'fake,v6') ($names -join ',')
Check 'and the count removed is returned' ($r.RemovedServers -eq 3) "$($r.RemovedServers)"
Check 'no address of a real server is left in the file' (-not ((Get-Content (Join-Path $case 'LocalSettings.json') -Raw) -match 'example\.com|192\.168|10\.0\.0\.5'))
Check 'the player is mpv (Flyleaf replaced)' ($s.MediaPlayerType -eq 'Mpv') $s.MediaPlayerType
Check 'the backend command is the harness''s' ($s.MpvBackendCommand -eq 'backend-cmd')
Check 'other settings stay, -Setting values are written (JSON values parsed)' ($s.Volume -eq 40 -and $s.MpvPlayerControls -eq 'FluentFin' -and $s.ShowSkipPrompt -eq $false)
Check 'FluentFin''s controls are reported' ($r.PlayerControls -eq 'FluentFin')
$one = New-Case 'one-server'
'{ "Servers": [ { "PublicUrl": "http://127.0.0.1:1", "LocalUrl": "http://127.0.0.1:1" } ] }' | Set-Content -LiteralPath (Join-Path $one 'LocalSettings.json') -Encoding ascii
[void](Initialize-HarnessSettings $one 'cmd')
Check 'one kept server stays a list' ((Get-Content (Join-Path $one 'LocalSettings.json') -Raw) -match '"Servers":\s*\[')
foreach ($refused in 'MediaPlayerType=Flyleaf', 'MediaPlayerType=Mpv', 'MpvBackendCommand=x', 'Servers=[]') {
    $c = New-Case 'refused'
    Check "-Setting $refused is refused, and nothing is written" ((Throws { Initialize-HarnessSettings $c 'cmd' @($refused) } '*the harness sets it*') -and -not (Test-Path (Join-Path $c 'LocalSettings.json')))
}
Check '-Setting without a value is refused' (Throws { Initialize-HarnessSettings (New-Case 'novalue') 'cmd' @('Volume') } '*Name=Value*')
$fresh = New-Case 'fresh'
$r = Initialize-HarnessSettings $fresh 'cmd'
Check 'a new folder gets the mpv player and FluentFin''s controls (the default)' ((Get-Content (Join-Path $fresh 'LocalSettings.json') -Raw | ConvertFrom-Json).MediaPlayerType -eq 'Mpv' -and $r.PlayerControls -eq 'FluentFin')
Check 'without libmpv next to the exe, FluentFin falls back to mpv''s controls' ((Initialize-HarnessSettings (New-Case 'nolibmpv') 'cmd' -LibmpvThere $false).PlayerControls -eq 'Mpv')
Check 'mpv''s controls when the setting says so' ((Initialize-HarnessSettings (New-Case 'mpvchosen') 'cmd' @('MpvPlayerControls=Mpv')).PlayerControls -eq 'Mpv')

# --- Initialize-HarnessMpvConf -----------------------------------------------------------------------------------------
$case = New-Case 'mpvconf'
New-Item -ItemType Directory (Join-Path $case 'mpv-backend') | Out-Null
@('log-file=C:\somewhere\mpv.log', 'ao=wasapi', '--audio-device=wasapi/{x}', 'volume=100', 'sub-font=Arial', '# fluentfin-mpv harness: old header', '  log-file = x') |
    Set-Content -LiteralPath (Join-Path $case 'mpv-backend\mpv.conf') -Encoding ascii
$conf = Initialize-HarnessMpvConf $case
$lines = @(Get-Content $conf)
Check 'mpv.conf has no log-file (a log file logs the token in the request headers)' (-not @($lines | Where-Object { $_ -match 'log-file' })) ($lines -join ' | ')
Check 'mpv.conf is silent: ao=null, mute, volume 0, no other audio output or device' (($lines -contains 'ao=null') -and ($lines -contains 'mute=yes') -and ($lines -contains 'volume=0') -and
    -not @($lines | Where-Object { $_ -match 'wasapi|volume=100' }))
Check 'the user''s other mpv options stay' ($lines -contains 'sub-font=Arial')
Check 'one harness header' (@($lines | Where-Object { $_ -match '^# fluentfin-mpv harness' }).Count -eq 1)
$case = New-Case 'mpvconf-new'
$lines = @(Get-Content (Initialize-HarnessMpvConf $case))
Check 'a new folder gets a silent mpv.conf with no log-file' (($lines -contains 'ao=null') -and -not @($lines | Where-Object { $_ -match 'log-file' }))

# --- the exe and the build -----------------------------------------------------------------------------------------------
$build = New-Case 'build'
Check 'no -Exe, no -Build, nothing built: an error saying to pass -Build, nothing built' (Throws { Resolve-HarnessExe '' $build $false } '*pass -Build*')
Check 'a missing -Exe is an error' (Throws { Resolve-HarnessExe (Join-Path $build 'nope.exe') $build $false } '*does not exist*')
$exe = Join-Path $build 'bin\FluentFin\debug_x64\Nixie.exe'
New-Item -ItemType Directory -Force (Split-Path $exe) | Out-Null; Set-Content -LiteralPath $exe -Value 'x'
Check 'an earlier -Build''s exe in the build folder is used' ((Resolve-HarnessExe '' $build $false) -eq $exe)
Check '-Build leaves the build to the caller' ($null -eq (Resolve-HarnessExe '' $build $true))
$a = @(Get-HarnessBuildArgs $repo $build)
Check 'the build has its own artifacts folder (not the checkout''s bin/obj)' (($a -join ' ') -like "*--artifacts-path $build*")
Check 'the build leaves no MSBuild node or compiler server running' (($a -contains '-nodeReuse:false') -and ($a -contains '-p:UseSharedCompilation=false'))
Check 'the build is the x64 app project' (($a -contains '-p:Platform=x64') -and ($a[1] -like '*ui\FluentFin\FluentFin.csproj'))

Write-Host ("{0} failed" -f $script:failed)
if ($script:failed) { exit 1 }
