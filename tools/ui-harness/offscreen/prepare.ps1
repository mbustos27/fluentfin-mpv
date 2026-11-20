<#
  Dot-sourced by run-offscreen.ps1 (and offscreen\test_prepare.ps1): the checks on an off-screen run's folders and the
  preparation of its data folder, apart from the run so they can be tested without starting anything.
#>

function Test-Under([string]$Path, [string]$Root) {
    $p = $Path.TrimEnd('\') + '\'; $r = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $p.StartsWith($r, [StringComparison]::OrdinalIgnoreCase)
}

# The run's data folder as a full path without a trailing backslash: quoted, "C:\x\" reaches FluentFin as C:\x" (a
# folder it cannot make). Refused: a drive root, the user's FluentFin folders (or a folder holding them) and anything
# in the repository.
function Resolve-HarnessDataDir([string]$DataDir, [string]$Repo, [string]$LocalAppData = [Environment]::GetFolderPath('LocalApplicationData')) {
    if (-not $DataDir.Trim()) { throw "-DataDir is empty; use a scratch folder" }
    if ($DataDir.Trim() -match '^[A-Za-z]:([^\\/]|$)') { throw "-DataDir $DataDir is relative to a drive's current folder (or a drive root); give a full path to a scratch folder" }
    $full = [IO.Path]::GetFullPath($DataDir)
    $root = [IO.Path]::GetPathRoot($full)
    $trimmed = $full.TrimEnd('\', '/')
    if (-not $trimmed -or $trimmed -eq $root.TrimEnd('\', '/')) { throw "-DataDir $DataDir is a drive root; use a scratch folder" }
    foreach ($forbidden in (Join-Path $LocalAppData 'Nixie'), (Join-Path $LocalAppData 'Seaglass'), (Join-Path $LocalAppData 'FluentFinMpv'), (Join-Path $LocalAppData 'FluentFin')) {
        if ((Test-Under $trimmed $forbidden) -or (Test-Under $forbidden $trimmed)) { throw "-DataDir $DataDir would touch $forbidden (a real FluentFin data folder); use a scratch folder" }
    }
    if (Test-Under $trimmed $Repo) { throw "-DataDir must be outside the repository ($Repo)" }
    $trimmed
}

# The settings the harness owns or that would leave its rules: the backend command (the harness's), the saved servers (a
# sign-in's), the player (Mpv only: Flyleaf opens the real audio device).
$HarnessOwnedSettings = @('MpvBackendCommand', 'Servers', 'MediaPlayerType')

# A saved server the run may keep: every address it has is on this machine (loopback), and it has one.
function Test-LoopbackServer($Server) {
    $urls = @(foreach ($name in 'PublicUrl', 'LocalUrl', 'Url', 'Address') { if ($Server.PSObject.Properties[$name] -and "$($Server.$name)".Trim()) { "$($Server.$name)".Trim() } })
    if (-not $urls) { return $false }
    foreach ($u in $urls) {
        $uri = $null
        if (-not [Uri]::TryCreate($u, [UriKind]::Absolute, [ref]$uri) -or -not $uri.IsLoopback) { return $false }
    }
    $true
}

# Writes the run's LocalSettings.json: the backend command, the mpv player, the -Setting values (Name=Value, a JSON value
# else a string), and of the saved servers only loopback ones (a reused folder must not keep a real server to sign in
# to). Returns how many servers it removed and who draws the player controls (Mpv or FluentFin): FluentFin's are the
# default (SettingKeys.MpvPlayerControls) unless the setting says mpv, and need $LibmpvThere (libmpv\libmpv-2.dll next to the
# exe; without it FluentFin falls back to mpv's and says so).
function Initialize-HarnessSettings([string]$DataDir, [string]$BackendCommand, [string[]]$Setting = @(), [bool]$LibmpvThere = $true) {
    $parsedSettings = [ordered]@{}
    foreach ($pair in $Setting) {
        $name, $value = $pair -split '=', 2
        if (-not $name -or $null -eq $value) { throw "-Setting '$pair': use Name=Value" }
        if ($name -in $HarnessOwnedSettings) { throw "-Setting $($name): the harness sets it (the backend command, the mpv player; servers come from signing in)" }
        $parsed = try { $value | ConvertFrom-Json } catch { $value }
        if ($null -eq $parsed) { $parsed = $value }
        # a JSON array: a plain array again (Windows PowerShell wrote ConvertFrom-Json's as {"value": [...], "Count": n})
        if ($parsed -is [array]) { $parsed = [object[]]@($parsed | ForEach-Object { $_ }) }
        $parsedSettings[$name] = $parsed
    }
    $file = Join-Path $DataDir 'LocalSettings.json'
    $settings = if (Test-Path $file) { Get-Content $file -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
    if ($null -eq $settings) { $settings = [pscustomobject]@{} }
    $removed = 0
    if ($settings.PSObject.Properties['Servers']) {
        $all = @($settings.Servers | Where-Object { $null -ne $_ })
        $kept = @($all | Where-Object { Test-LoopbackServer $_ })
        $removed = $all.Count - $kept.Count
        $settings.Servers = $kept
    }
    $settings | Add-Member -NotePropertyName MpvBackendCommand -NotePropertyValue $BackendCommand -Force
    $settings | Add-Member -NotePropertyName MediaPlayerType -NotePropertyValue 'Mpv' -Force
    # Nixie 1.3.0: the one-time "Choose a look" dialog would stop every script after the sign-in; the runs that look at it
    # say -Setting ChooseLookOffered=false
    if (-not $settings.PSObject.Properties['ChooseLookOffered']) { $settings | Add-Member -NotePropertyName ChooseLookOffered -NotePropertyValue $true -Force }
    # Nixie 1.3.1: update checks are on by default; a test run asks nothing of GitHub: off, and the default's one-time switch
    # marked done (UpdateDefaults), unless the run says -Setting CheckForUpdates=true (a dev build then asks the fake server:
    # drive.ps1's NIXIE_UPDATE_API; a release package would ask GitHub, so verify-package never says it)
    $settings | Add-Member -NotePropertyName CheckForUpdates -NotePropertyValue $false -Force
    $settings | Add-Member -NotePropertyName CheckForUpdatesDefaultOn -NotePropertyValue $true -Force
    foreach ($name in $parsedSettings.Keys) { $settings | Add-Member -NotePropertyName $name -NotePropertyValue $parsedSettings[$name] -Force }
    [IO.File]::WriteAllText($file, ($settings | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
    $chosen = if ($settings.PSObject.Properties['MpvPlayerControls'] -and "$($settings.MpvPlayerControls)" -in @('Mpv', '0')) { 'Mpv' } else { 'FluentFin' }
    $controls = if ($chosen -eq 'FluentFin' -and $LibmpvThere) { 'FluentFin' } else { 'Mpv' }
    [pscustomobject]@{ RemovedServers = $removed; PlayerControls = $controls }
}

# The run's mpv-backend\mpv.conf: silent (ao=null, muted) on top of whatever it had, and never a log file: a log file
# makes mpv log verbosely, and that log holds the request headers, the access token among them.
function Initialize-HarnessMpvConf([string]$DataDir) {
    $dir = Join-Path $DataDir 'mpv-backend'
    New-Item -ItemType Directory -Force $dir | Out-Null
    $conf = Join-Path $dir 'mpv.conf'
    $kept = if (Test-Path $conf) { @(Get-Content $conf | Where-Object { $_ -notmatch '^\s*-{0,2}(ao|mute|volume|audio-device|log-file)\s*=' -and $_ -notmatch '^# fluentfin-mpv harness' }) } else { @() }
    [IO.File]::WriteAllLines($conf, @('# fluentfin-mpv harness: test runs are silent (no audio device)', 'ao=null', 'mute=yes', 'volume=0') + $kept, [Text.UTF8Encoding]::new($false))
    $conf
}

# Nixie.exe for the run: -Exe, else the one -Build made in the build folder. Never builds by itself: a missing
# exe is an error that says to pass -Build or -Exe.
function Resolve-HarnessExe([string]$Exe, [string]$BuildDir, [bool]$Build) {
    if ($Exe) {
        $Exe = [IO.Path]::GetFullPath($Exe)
        if (-not (Test-Path $Exe)) { throw "-Exe $Exe does not exist" }
        return $Exe
    }
    if ($Build) { return $null }   # the caller builds, then calls Find-HarnessBuiltExe
    $built = Find-HarnessBuiltExe $BuildDir
    if (-not $built) { throw "no Nixie.exe in ${BuildDir}: pass -Build (builds it there) or -Exe <Nixie.exe>" }
    $built
}

function Find-HarnessBuiltExe([string]$BuildDir) {
    $bin = Join-Path $BuildDir 'bin\FluentFin'
    if (-not (Test-Path $bin)) { return $null }
    Get-ChildItem $bin -Recurse -Filter 'Nixie.exe' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}

# The build of FluentFin for a run: its own artifacts folder (bin and obj outside the checkout), no MSBuild node left
# waiting for reuse and no shared compiler server (nothing of the build keeps running after it).
function Get-HarnessBuildArgs([string]$Repo, [string]$BuildDir) {
    @('build', (Join-Path $Repo 'ui\FluentFin\FluentFin.csproj'), '-c', 'Debug', '-p:Platform=x64', '--artifacts-path', $BuildDir,
        '-nodeReuse:false', '-p:UseSharedCompilation=false', '-nologo', '-v:q')
}

# --- Nixie 1.4.x (sg-parallel): off-screen runs at once on this machine ---------------------------------------------------
# Bug hunt m10's "three runs at once all ended minimized" was never the parallel runs: the primary monitor dropped off the
# bus in that second (drive.ps1's Resolve-WindowsMinimize). Runs go N at a time (NIXIE_HARNESS_PARALLEL, default 3: about
# 1-1.5 GB each), and a run starts only while the machine has the memory for it (Wait-HarnessMemory).
# The lock is N named mutexes, one per slot: slot 1 is Local\NixieHarnessOffscreenRun (Nixie 1.4.0's one-at-a-time lock, so a
# run of an older checkout holds one of the N), slot n is Local\NixieHarnessOffscreenRun.<n>. Mutexes, not a semaphore: a
# run that dies (killed, its terminal closed) gives its slot back as an abandoned mutex; a semaphore's count would stay taken.
$HarnessRunLockName = 'Local\NixieHarnessOffscreenRun'

# how many runs go at once: NIXIE_HARNESS_PARALLEL (1 to 8), else 3
function Get-HarnessRunSlots([string]$Value = $env:NIXIE_HARNESS_PARALLEL) {
    if (-not "$Value".Trim()) { return 3 }
    $n = 0
    if (-not [int]::TryParse("$Value".Trim(), [ref]$n) -or $n -lt 1 -or $n -gt 8) { throw "NIXIE_HARNESS_PARALLEL=${Value}: give 1 to 8 (off-screen runs at once on this machine)" }
    $n
}

function Get-HarnessRunSlotName([int]$Slot, [string]$Name = $HarnessRunLockName) { if ($Slot -le 1) { $Name } else { "$Name.$Slot" } }

# Takes a free slot of $Slots, waiting up to $Wait for one: { Slot, Mutex } (give it back with Exit-HarnessRunSlot, on the same
# thread), or $null when none came free. A slot a dead run left (an abandoned mutex) is taken.
function Enter-HarnessRunSlot([int]$Slots, [TimeSpan]$Wait, [string]$Name = $HarnessRunLockName) {
    $mutexes = @(for ($i = 1; $i -le $Slots; $i++) { New-Object System.Threading.Mutex($false, (Get-HarnessRunSlotName $i $Name)) })
    $index = [Threading.WaitHandle]::WaitTimeout
    try { $index = [Threading.WaitHandle]::WaitAny([Threading.WaitHandle[]]$mutexes, $Wait) }
    catch {
        $abandoned = @($_.Exception, $_.Exception.InnerException) | Where-Object { $_ -is [Threading.AbandonedMutexException] } | Select-Object -First 1
        if (-not $abandoned) { foreach ($m in $mutexes) { $m.Dispose() }; throw }
        $index = $abandoned.MutexIndex
    }
    for ($i = 0; $i -lt $mutexes.Count; $i++) { if ($i -ne $index) { $mutexes[$i].Dispose() } }
    if ($index -eq [Threading.WaitHandle]::WaitTimeout -or $index -lt 0 -or $index -ge $mutexes.Count) { return $null }
    [pscustomobject]@{ Slot = $index + 1; Slots = $Slots; Mutex = $mutexes[$index] }
}

function Exit-HarnessRunSlot($Lock) {
    if (-not $Lock -or -not $Lock.Mutex) { return }
    try { $Lock.Mutex.ReleaseMutex() } finally { $Lock.Mutex.Dispose(); $Lock.Mutex = $null }
}

# the machine's available memory, GB: Ram (physical: free and standby) and Commit (what can still be committed before the
# commit limit; a run that cannot commit fails, whatever the RAM)
function Get-HarnessMemory {
    if (-not ('HarnessMemoryStatus' -as [type])) {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class HarnessMemoryStatus {
  [StructLayout(LayoutKind.Sequential)] struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
  [DllImport("kernel32.dll", SetLastError = true)] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
  public static double[] Get() {
    var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
    if (!GlobalMemoryStatusEx(ref m)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    return new[] { m.ullAvailPhys / 1073741824.0, m.ullAvailPageFile / 1073741824.0 };
  }
}
"@
    }
    $v = [HarnessMemoryStatus]::Get()
    [pscustomobject]@{ Ram = [Math]::Round($v[0], 1); Commit = [Math]::Round($v[1], 1) }
}

# how much memory a run waits for: NIXIE_HARNESS_MIN_FREE_GB (a number, 0 to turn the wait off), else 3
function Get-HarnessMinFreeGB([string]$Value = $env:NIXIE_HARNESS_MIN_FREE_GB) {
    if (-not "$Value".Trim()) { return 3.0 }
    $n = 0.0
    if (-not [double]::TryParse("$Value".Trim(), [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$n) -or $n -lt 0) { throw "NIXIE_HARNESS_MIN_FREE_GB=${Value}: give a number of GB (0 turns the wait off)" }
    $n
}

# Waits while the available RAM or commit is under $MinGB, until $Until: the last reading, with Ok. $OnWait runs once, with
# the reading, when it has to wait. $Probe and $Sleep stand in for the machine in the tests.
function Wait-HarnessMemory([double]$MinGB, [DateTime]$Until, [scriptblock]$OnWait = {}, [scriptblock]$Probe = { Get-HarnessMemory },
    [scriptblock]$Sleep = { Start-Sleep -Seconds 5 }) {
    $said = $false
    while ($true) {
        $m = & $Probe
        $ok = $m.Ram -ge $MinGB -and $m.Commit -ge $MinGB
        if ($ok -or [DateTime]::UtcNow -ge $Until.ToUniversalTime()) { return [pscustomobject]@{ Ok = $ok; Ram = $m.Ram; Commit = $m.Commit } }
        if (-not $said) { & $OnWait $m; $said = $true }
        & $Sleep
    }
}

# One build or test-video build at a time in a folder (the default -Cache is shared by every checkout): runs $Body holding the
# folder's own named mutex, waiting up to $Wait for it.
function Get-HarnessFolderLockName([string]$Folder) {
    $sha = [Security.Cryptography.SHA1]::Create()
    try { $hash = -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($Folder).TrimEnd('\').ToLowerInvariant())) | Select-Object -First 8 | ForEach-Object { $_.ToString('x2') }) }
    finally { $sha.Dispose() }
    "Local\NixieHarnessFolder-$hash"
}
function Use-HarnessFolderLock([string]$Folder, [TimeSpan]$Wait, [scriptblock]$Body) {
    $m = New-Object System.Threading.Mutex($false, (Get-HarnessFolderLockName $Folder))
    $held = $false
    try {
        try { $held = $m.WaitOne($Wait) }
        catch { if (@($_.Exception, $_.Exception.InnerException) | Where-Object { $_ -is [Threading.AbandonedMutexException] }) { $held = $true } else { throw } }
        if (-not $held) { throw "another off-screen run kept $Folder busy for $([int]$Wait.TotalMinutes) min" }
        & $Body
    }
    finally { if ($held) { $m.ReleaseMutex() }; $m.Dispose() }
}
