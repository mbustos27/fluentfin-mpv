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

# --- sg-parallel: runs at once (the slot lock), the memory wait, one build per folder ------------------------------------
# Another holder of a lock is a thread of this process (a mutex is re-entrant on the thread that holds it). Every lock here
# has a name of this test's own, never a real run's.
if (-not ('SlotTestHolder' -as [type])) {
    Add-Type @"
using System;
using System.Threading;
public sealed class SlotTestHolder {
  readonly ManualResetEvent _taken = new ManualResetEvent(false), _go = new ManualResetEvent(false);
  readonly Thread _t;
  // takes the named mutex on a thread of its own and holds it until Let(), or for releaseAfterMs (when > 0); abandon: the
  // thread ends without releasing it (a run that died)
  public SlotTestHolder(string name, bool abandon, int releaseAfterMs) {
    _t = new Thread(() => {
      var m = new Mutex(false, name);
      try { m.WaitOne(); } catch (AbandonedMutexException) { }
      _taken.Set();
      if (releaseAfterMs > 0) _go.WaitOne(releaseAfterMs); else _go.WaitOne();
      if (!abandon) m.ReleaseMutex();
    }) { IsBackground = true };
    _t.Start();
    if (!_taken.WaitOne(5000)) throw new TimeoutException("the holder did not take " + name);
  }
  public void Let() { _go.Set(); _t.Join(5000); }
  // whether another thread can take the named mutex now (it gives it back at once)
  public static bool Free(string name) {
    bool free = false;
    var t = new Thread(() => { using (var m = new Mutex(false, name)) { try { free = m.WaitOne(0); } catch (AbandonedMutexException) { free = true; } if (free) m.ReleaseMutex(); } });
    t.Start(); t.Join(5000);
    return free;
  }
}
"@
}
$lockName = "Local\NixieHarnessTest-$([guid]::NewGuid().ToString('N'))"
function Slot([int]$N) { Get-HarnessRunSlotName $N $lockName }
$holders = New-Object System.Collections.ArrayList
function Hold([string]$Name, [switch]$Abandon, [int]$ReleaseAfterMs = 0) { $h = New-Object SlotTestHolder($Name, [bool]$Abandon, $ReleaseAfterMs); [void]$holders.Add($h); $h }
try {
    Check 'runs at once: 3 by default' ((Get-HarnessRunSlots '') -eq 3)
    Check 'NIXIE_HARNESS_PARALLEL=1 is one at a time, 8 the most' ((Get-HarnessRunSlots '1') -eq 1 -and (Get-HarnessRunSlots ' 8 ') -eq 8)
    foreach ($bad in '0', '9', '-1', 'two', '2.5') { Check "NIXIE_HARNESS_PARALLEL=$bad is refused" (Throws { Get-HarnessRunSlots $bad } '*1 to 8*') }
    Check 'slot 1 is Nixie 1.4.0''s one-at-a-time lock (a run of an older checkout holds one slot)' ((Get-HarnessRunSlotName 1) -eq 'Local\NixieHarnessOffscreenRun' -and (Get-HarnessRunSlotName 2) -eq 'Local\NixieHarnessOffscreenRun.2')

    $l = Enter-HarnessRunSlot 3 ([TimeSpan]::Zero) $lockName
    Check 'nothing held: slot 1' ($l -and $l.Slot -eq 1) "$($l.Slot)"
    Exit-HarnessRunSlot $l
    Check 'Exit-HarnessRunSlot gives the slot back (another thread takes it)' ([SlotTestHolder]::Free((Slot 1)))

    $old = Hold (Slot 1)
    Check 'an older checkout''s run (the 1.4.0 lock) and NIXIE_HARNESS_PARALLEL=1: none free' ($null -eq (Enter-HarnessRunSlot 1 ([TimeSpan]::Zero) $lockName))
    $l = Enter-HarnessRunSlot 3 ([TimeSpan]::Zero) $lockName
    Check 'an older checkout''s run and 3 slots: slot 2' ($l -and $l.Slot -eq 2) "$($l.Slot)"
    Exit-HarnessRunSlot $l

    $h2 = Hold (Slot 2); $h3 = Hold (Slot 3)
    Check 'three runs going and 3 slots: a fourth gets none at once' ($null -eq (Enter-HarnessRunSlot 3 ([TimeSpan]::Zero) $lockName))
    $h2.Let()
    $l = Enter-HarnessRunSlot 3 ([TimeSpan]::Zero) $lockName
    Check 'a slot given back is taken' ($l -and $l.Slot -eq 2) "$($l.Slot)"
    # (this thread already holds slot 2, and a mutex is re-entrant on its thread: another run holds it for the next check)
    Exit-HarnessRunSlot $l; [void](Hold (Slot 2))
    $h3.Let(); [void](Hold (Slot 3) -ReleaseAfterMs 600)    # slot 3 taken again, and given back after 0.6 s
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $l2 = Enter-HarnessRunSlot 3 ([TimeSpan]::FromSeconds(5)) $lockName
    Check 'a run waiting for a slot gets the first given back, and not before' ($l2 -and $l2.Slot -eq 3 -and $sw.ElapsedMilliseconds -ge 300) "slot $($l2.Slot) after $($sw.ElapsedMilliseconds) ms"
    Exit-HarnessRunSlot $l2
    foreach ($h in $holders) { $h.Let() }

    [void](Hold (Slot 1) -Abandon -ReleaseAfterMs 1)
    Start-Sleep -Milliseconds 300
    $l = Enter-HarnessRunSlot 1 ([TimeSpan]::Zero) $lockName
    Check 'the slot of a run that died (an abandoned mutex) is taken, not lost' ($l -and $l.Slot -eq 1) "$($l.Slot)"
    Exit-HarnessRunSlot $l
    Check 'and given back again' ([SlotTestHolder]::Free((Slot 1)))
}
finally { foreach ($h in $holders) { $h.Let() } }

# the memory wait, on readings made up here
Check 'memory: 3 GB by default; 0 turns the wait off; a number of GB' ((Get-HarnessMinFreeGB '') -eq 3 -and (Get-HarnessMinFreeGB '0') -eq 0 -and (Get-HarnessMinFreeGB '2.5') -eq 2.5)
foreach ($bad in '-1', 'lots') { Check "NIXIE_HARNESS_MIN_FREE_GB=$bad is refused" (Throws { Get-HarnessMinFreeGB $bad } '*number of GB*') }
$script:readings = @(@{ Ram = 1.0; Commit = 9.0 }, @{ Ram = 2.9; Commit = 9.0 }, @{ Ram = 6.0; Commit = 9.0 })
$script:read = 0; $script:slept = 0; $script:said = @()
$w = Wait-HarnessMemory 3 ([DateTime]::UtcNow.AddMinutes(5)) -OnWait { param($m) $script:said += "$($m.Ram)" } -Probe { $script:readings[[Math]::Min($script:read++, 2)] } -Sleep { $script:slept++ }
Check 'under 3 GB of RAM: it waits until there is, says so once' ($w.Ok -and $w.Ram -eq 6 -and $script:slept -eq 2 -and $script:said.Count -eq 1 -and $script:said[0] -eq '1') "ok $($w.Ok), $($script:slept) waits, said $($script:said -join ',')"
Check 'RAM enough but the commit under 3 GB: it waits too' ((Throws { Wait-HarnessMemory 3 ([DateTime]::UtcNow.AddMinutes(5)) -Probe { @{ Ram = 8.0; Commit = 2.0 } } -Sleep { throw 'waited' } -OnWait {} } 'waited'))
$script:slept = 0
$w = Wait-HarnessMemory 3 ([DateTime]::UtcNow.AddSeconds(-1)) -Probe { @{ Ram = 1.0; Commit = 1.0 } } -Sleep { $script:slept++ } -OnWait {}
Check 'still short when the queue time is up: not Ok, and no more waiting' (-not $w.Ok -and $script:slept -eq 0)
$w = Wait-HarnessMemory 0 ([DateTime]::UtcNow) -Probe { @{ Ram = 0.1; Commit = 0.1 } } -Sleep { throw 'waited' } -OnWait {}
Check 'NIXIE_HARNESS_MIN_FREE_GB=0: never waits' ($w.Ok)
$m = Get-HarnessMemory
Check 'this machine''s memory is read (GB of RAM and commit available)' ($m.Ram -gt 0 -and $m.Commit -gt 0) "RAM $($m.Ram) GB, commit $($m.Commit) GB"

# one build (or test-video build) at a time in a folder
$folder = Join-Path $Out 'folder-lock'
Check 'a folder''s lock returns what its body made' ((Use-HarnessFolderLock $folder ([TimeSpan]::Zero) { 'made' }) -eq 'made')
Check 'the same folder however it is written: one lock' ((Get-HarnessFolderLockName ($folder + '\')) -eq (Get-HarnessFolderLockName $folder.ToUpperInvariant()) -and (Get-HarnessFolderLockName $folder) -ne (Get-HarnessFolderLockName "$folder-2"))
$busy = New-Object SlotTestHolder((Get-HarnessFolderLockName $folder), $false, 0)
try { Check 'a folder another run is building in: it waits, then says so' (Throws { Use-HarnessFolderLock $folder ([TimeSpan]::FromMilliseconds(200)) { 'built' } } '*kept*busy*') }
finally { $busy.Let() }
Check 'and the lock is given back after the body (also when it throws)' ((Throws { Use-HarnessFolderLock $folder ([TimeSpan]::Zero) { throw 'boom' } } 'boom') -and [SlotTestHolder]::Free((Get-HarnessFolderLockName $folder)))

Write-Host ("{0} failed" -f $script:failed)
if ($script:failed) { exit 1 }
