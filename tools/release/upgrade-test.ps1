<#
.SYNOPSIS
  The upgrade test: install the published Seaglass MSI, run the new Nixie MSI over it (per user, silent), check the result,
  uninstall. INSTALLS INTO THE ACCOUNT IT RUNS IN: run it only where that is wanted (a test account, a VM).

.DESCRIPTION
  Nixie (1.2.0) was Seaglass (1.1.x), and before that FluentFin MPV (1.0.x): the same upgrade code, another product name,
  install folder (%LOCALAPPDATA%\Programs\<name>) and Start menu shortcut (<name>.lnk). The old MSI's names are read from
  it (ProductName, Manufacturer), the new one's from it, so any old version can be the old one.
  Checks, in order:
    - before anything is installed: no Nixie, Seaglass or FluentFin MPV is installed for this user or the machine (Windows
      Installer COM API by upgrade code, and the Settings > Apps list by name). If one is, NOTHING is touched: the test is
      SKIPPED (exit 2) and says why;
    - the old MSI installs, per user, with /qn: one Apps entry, the old version, the old name;
    - the new MSI over it: still one Apps entry, now the new version and name; every file of the new package in place with
      its hash, every file only the old one had gone (with the rename: the whole old install folder); the old shortcut
      gone, the new one there and pointing at the new install;
    - the user's data folders (%LOCALAPPDATA%\Nixie, \Seaglass and \FluentFinMpv) are only read, never written: the listing
      of each (if it exists) is the same before and after; with -Launch the shortcut's program is started with
      --data-dir in the scratch folder, never the user's data, and ended through the process it started;
    - an older MSI over the newer one is refused (non-zero exit) and the new version stays;
    - uninstall: no Apps entry, no install folder, no shortcut, data untouched.
  Whatever it installed is uninstalled in a finally block (by the product code it saw installed, not before it was checked
  safe). Writes install logs and test.log under -Out.

  Exit code: 0 all checks passed, 1 a check failed, 2 skipped (something is installed already).

  -OldMsi  default: the published Seaglass 1.1.3 (dist\Seaglass-1.1.3-win-x64.msi here, else downloaded from the GitHub
           release and checked against its .sha256 into -Out). Any older one (Seaglass 1.1.0, FluentFin MPV) works too.
  -NewMsi  default: dist\Nixie-<-Version>-win-x64.msi, the one build-release.ps1 made.
  -SelfCheck  proves this script on a throwaway product instead: builds two tiny isolated MSIs from installer\Nixie.wxs
           named like a rename (SeaglassUpgradeCheck, then NixieUpgradeCheck: other names, upgrade code, folders,
           shortcuts and registry keys, so they can never touch an installed Nixie or Seaglass), runs the whole test on
           them, and does not need the published MSI. The safety check still runs, for those products.

  Usage: upgrade-test.ps1 -Out <scratch folder> [-Version 1.2.0] [-OldMsi <msi>] [-NewMsi <msi>] [-Launch] [-SelfCheck]
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Out,
    [string]$Version = '1.2.0',
    [string]$OldMsi,
    [string]$NewMsi,
    [switch]$Launch,
    [switch]$SelfCheck
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
. (Join-Path $PSScriptRoot 'msi-tools.ps1')
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$Out = [IO.Path]::GetFullPath($Out)
if ($Out.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) { throw "-Out must be outside the repository ($repo)" }
New-Item -ItemType Directory -Force $Out | Out-Null
$logFile = Join-Path $Out 'test.log'
Set-Content $logFile '' -Encoding utf8
function Say([string]$text) { Write-Host $text; Add-Content $logFile $text -Encoding utf8 }
$failures = New-Object System.Collections.Generic.List[string]
function Check([string]$what, [bool]$ok, [string]$detail = '') {
    if ($ok) { Say "  ok    $what" } else { Say "  FAIL  $what $detail"; $failures.Add($what) }
}

# --- the MSIs ----------------------------------------------------------------------------------------------------------
$dist = Join-Path $repo 'dist'
if ($SelfCheck) {
    $dotnet = (Get-Command dotnet.exe).Source
    $work = Join-Path $Out 'selfcheck'
    New-Item -ItemType Directory -Force $work | Out-Null
    $artDir = Join-Path $work 'art'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'installer\make-installer-art.ps1') -Out $artDir | Out-Null
    $env:DOTNET_ROLL_FORWARD = 'Major'
    $env:WIX_EXTENSIONS = Join-Path $dist '.work\wix-ext'
    $ext = 'WixToolset.UI.wixext/5.0.2'
    function New-FakePackage([string]$name, [string[]]$files) {
        $dir = Join-Path $work $name
        if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
        foreach ($f in $files) { $p = Join-Path $dir $f; New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null; Set-Content $p "$name $f" }
        return $dir
    }
    $upgradeCode = '{7B0F4F6E-3C6B-4F44-9B52-6D5B7A0E2A11}'.Trim('{}')
    $pkgOld = New-FakePackage 'pkg-old' @('Nixie.exe', 'app\Nixie.exe', 'app\old-only.dll', 'app\both.dll')
    $pkgNew = New-FakePackage 'pkg-new' @('Nixie.exe', 'app\Nixie.exe', 'app\both.dll', 'app\new-only.dll')
    Set-Content (Join-Path $pkgNew 'app\both.dll') 'changed in the new one'
    $wixDir = Join-Path $PSScriptRoot 'installer'
    Push-Location $repo
    try {
        # the old one under another name, as Seaglass was: the rename's paths (folder, shortcut, Apps name) are exercised
        foreach ($spec in @(@('old', '1.0.0', $pkgOld, 'SeaglassUpgradeCheck'), @('new', '1.0.1', $pkgNew, 'NixieUpgradeCheck'))) {
            $msi = Join-Path $work "check-$($spec[0]).msi"
            & $dotnet tool run wix build (Join-Path $wixDir 'Nixie.wxs') (Join-Path $wixDir 'NixieUI.wxs') -arch x64 -ext $ext `
                -loc (Join-Path $wixDir 'Nixie.en-us.wxl') -d "Version=$($spec[1])" -d "PackageDir=$($spec[2])" `
                -d "IconFile=$(Join-Path $repo 'ui\FluentFin\Assets\Nixie.ico')" -d "ArtDir=$artDir" -d "AppName=$($spec[3])" `
                -d "UpgradeCode=$upgradeCode" -pdbtype none -o $msi
            if ($LASTEXITCODE -ne 0) { throw "wix build failed for the self-check $($spec[0]) MSI" }
        }
    }
    finally { Pop-Location; Remove-Item Env:DOTNET_ROLL_FORWARD, Env:WIX_EXTENSIONS -ErrorAction SilentlyContinue }
    $OldMsi = Join-Path $work 'check-old.msi'
    $NewMsi = Join-Path $work 'check-new.msi'
}
if (-not $NewMsi) { $NewMsi = Join-Path $dist "Nixie-$Version-win-x64.msi" }
if (-not $OldMsi) {
    $published = 'Seaglass-1.1.3-win-x64.msi'
    $OldMsi = Join-Path $dist $published
    if (-not (Test-Path $OldMsi)) {
        $base = 'https://github.com/mbustos27/fluentfin-mpv/releases/download/v1.1.3'
        $OldMsi = Join-Path $Out $published
        Invoke-WebRequest "$base/$published" -OutFile $OldMsi -UseBasicParsing
        $shaFile = Join-Path $Out "$published.sha256"
        Invoke-WebRequest "$base/$published.sha256" -OutFile $shaFile -UseBasicParsing
        $want = ((Get-Content $shaFile -Raw).Trim() -split '\s+')[0]
        if ((Get-FileHash -Algorithm SHA256 $OldMsi).Hash -ne $want.ToUpperInvariant()) { throw "the downloaded $published does not match its .sha256" }
    }
}
foreach ($m in $OldMsi, $NewMsi) { if (-not (Test-Path $m)) { throw "no MSI at $m (build it with build-release.ps1, or pass -OldMsi / -NewMsi)" } }
$OldMsi = [IO.Path]::GetFullPath($OldMsi); $NewMsi = [IO.Path]::GetFullPath($NewMsi)

$upgradeCodeOld = Get-MsiProperty $OldMsi 'UpgradeCode'; $upgradeCodeNew = Get-MsiProperty $NewMsi 'UpgradeCode'
# the names: the new one's (Nixie), the old one's (Seaglass, FluentFin MPV, or the same when both are Nixie)
$name = Get-MsiProperty $NewMsi 'ProductName'
$nameOld = Get-MsiProperty $OldMsi 'ProductName'
$publisherOld = Get-MsiProperty $OldMsi 'Manufacturer'; $publisherNew = Get-MsiProperty $NewMsi 'Manufacturer'
$renamed = $nameOld -ne $name
$verOld = [version](Get-MsiProperty $OldMsi 'ProductVersion'); $verNew = [version](Get-MsiProperty $NewMsi 'ProductVersion')
$codeOld = Get-MsiProperty $OldMsi 'ProductCode'; $codeNew = Get-MsiProperty $NewMsi 'ProductCode'
Say "upgrade test: $nameOld $verOld -> $name $verNew$(if ($renamed) { ' (a rename)' })"
Say "  old $OldMsi`n  new $NewMsi"
if ($upgradeCodeOld -ne $upgradeCodeNew) { throw "the two MSIs have different upgrade codes ($upgradeCodeOld, $upgradeCodeNew)" }
if ($verNew -le $verOld) { throw "the new MSI ($verNew) is not newer than the old one ($verOld)" }

# --- safety: nothing of ours may be installed already -------------------------------------------------------------------
$reasons = $null
# the self-check's products are isolated from the real ones
$names = if ($SelfCheck) { @($nameOld, $name) | Select-Object -Unique } else { @('Nixie', 'Seaglass', 'FluentFin MPV', $nameOld, $name) | Select-Object -Unique }
if (-not (Test-NothingInstalled $upgradeCodeNew $names ([ref]$reasons))) {
    Say "SKIPPED: $($names -join ' or ') is already installed here, and this test must not touch an installed copy:"
    foreach ($r in $reasons) { Say "  $r" }
    exit 2
}
Say '  nothing of it is installed: safe to go on'

# --- the places the test looks at ----------------------------------------------------------------------------------------
$programs = Join-Path $env:LOCALAPPDATA "Programs\$name"
$shortcut = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$name.lnk"
$programsOld = Join-Path $env:LOCALAPPDATA "Programs\$nameOld"
$shortcutOld = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$nameOld.lnk"
$dataFolders = @((Join-Path $env:LOCALAPPDATA 'Nixie'), (Join-Path $env:LOCALAPPDATA 'Seaglass'), (Join-Path $env:LOCALAPPDATA 'FluentFinMpv'))
$dataBefore = @{}; foreach ($d in $dataFolders) { $dataBefore[$d] = Get-FolderSnapshot $d }
$scratchData = Join-Path $Out 'data'; New-Item -ItemType Directory -Force $scratchData | Out-Null
$startedAt = Get-Date

function Invoke-Msi([string]$what, [string[]]$arguments) {
    $log = Join-Path $Out ("msi-{0}.log" -f ($what -replace '[^\w]+', '-'))
    $p = Start-Process msiexec.exe -ArgumentList (@($arguments) + @('/qn', '/norestart', '/l*v', "`"$log`"")) -Wait -PassThru -WindowStyle Hidden
    Say "  msiexec $what -> exit $($p.ExitCode)"
    return $p.ExitCode
}
function Get-Entries { return @(Get-AppsEntries @(@($nameOld, $name) | Select-Object -Unique)) }

$installedCode = $null
try {
    # --- install the old one ---------------------------------------------------------------------------------------------
    Say "`n== install $nameOld $verOld"
    $installedCode = $codeOld
    $exit = Invoke-Msi "install-$verOld" @('/i', "`"$OldMsi`"")
    Check "the old MSI installs (exit 0)" ($exit -eq 0) "exit $exit"
    $entries = @(Get-Entries)
    Check "one Apps entry, $nameOld $verOld" ($entries.Count -eq 1 -and [version]$entries[0].Version -eq $verOld -and $entries[0].Name -eq $nameOld) "found $($entries | ForEach-Object { "$($_.Name) $($_.Version)" })"
    Check 'its publisher is set' ($entries.Count -eq 1 -and $publisherOld -and $entries[0].Publisher -eq $publisherOld) "publisher '$($entries | ForEach-Object { $_.Publisher })'"
    $oldHashes = Get-FileHashes $programsOld
    Check "files installed in $programsOld" ($oldHashes.Count -gt 0) 'none'
    Check 'the Start menu shortcut exists' (Test-Path $shortcutOld)

    # --- the new one over it -----------------------------------------------------------------------------------------------
    Say "`n== install $name $verNew over it"
    $installedCode = $codeNew
    $exit = Invoke-Msi "upgrade-$verNew" @('/i', "`"$NewMsi`"")
    Check 'the new MSI installs over it (exit 0)' ($exit -eq 0) "exit $exit"
    $entries = @(Get-Entries)
    Check "exactly one Apps entry, $name $verNew" ($entries.Count -eq 1 -and [version]$entries[0].Version -eq $verNew -and $entries[0].Name -eq $name) "found $($entries | ForEach-Object { "$($_.Name) $($_.Version)" })"
    Check "its publisher is $publisherNew" ($entries.Count -eq 1 -and $entries[0].Publisher -eq $publisherNew) "publisher '$($entries | ForEach-Object { $_.Publisher })'"
    if ($renamed) {
        # the rename: the old install folder and the old Start menu entry go with the old product
        Check "the old install folder $programsOld is gone" (-not (Test-Path $programsOld))
        Check "the old Start menu shortcut ($nameOld) is gone" (-not (Test-Path $shortcutOld))
    }
    $codes = @(Get-InstalledByUpgradeCode $upgradeCodeNew)
    Check 'one installed product of that upgrade code, the new one' ($codes.Count -eq 1 -and $codes[0] -eq $codeNew) "found $($codes -join ', ')"
    # what the new MSI holds: an administrative extract (installs nothing)
    $extract = Join-Path $Out 'new-extract'
    if (Test-Path $extract) { Remove-Item -Recurse -Force $extract }
    $adm = Start-Process msiexec.exe -ArgumentList @('/a', "`"$NewMsi`"", '/qn', "TARGETDIR=`"$extract`"") -Wait -PassThru -WindowStyle Hidden
    Check 'the new MSI unpacks for comparison' ($adm.ExitCode -eq 0) "exit $($adm.ExitCode)"
    $newPackage = Join-Path $extract "LocalApp\Programs\$name"
    $wanted = Get-FileHashes $newPackage
    $now = Get-FileHashes $programs
    $missing = @($wanted.Keys | Where-Object { -not $now.ContainsKey($_) })
    $different = @($wanted.Keys | Where-Object { $now.ContainsKey($_) -and $now[$_] -ne $wanted[$_] })
    $leftovers = @($now.Keys | Where-Object { -not $wanted.ContainsKey($_) })
    $oldOnly = @($oldHashes.Keys | Where-Object { -not $wanted.ContainsKey($_) })
    Check "all $($wanted.Count) files of the new package are in place, with the same hash" ($wanted.Count -gt 0 -and -not $missing -and -not $different) "missing: $($missing -join ', '); different: $($different -join ', ')"
    Check "the $($oldOnly.Count) files only the old version had are gone" (-not (@($oldOnly | Where-Object { $now.ContainsKey($_) }))) "still there: $(@($oldOnly | Where-Object { $now.ContainsKey($_) }) -join ', ')"
    Check 'nothing else is in the install folder' (-not $leftovers) "extra: $($leftovers -join ', ')"
    $changed = @($wanted.Keys | Where-Object { $oldHashes.ContainsKey($_) -and $oldHashes[$_] -ne $wanted[$_] })
    Say "  ($($oldOnly.Count) removed, $($changed.Count) replaced, $(@($wanted.Keys | Where-Object { -not $oldHashes.ContainsKey($_) }).Count) added)"
    # the shortcut
    Check 'the Start menu shortcut is there' (Test-Path $shortcut)
    if (Test-Path $shortcut) {
        $target = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut).TargetPath
        Check "the shortcut points into the new install and the file exists" ($target.StartsWith($programs, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path $target)) "target '$target'"
        if ($Launch -and (Test-Path $target)) {
            $p = Start-Process $target -ArgumentList @('--data-dir', "`"$scratchData`"") -PassThru
            Start-Sleep -Seconds 10
            $alive = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($programs, [StringComparison]::OrdinalIgnoreCase) -and $_.StartTime -ge $startedAt })
            Check 'the shortcut starts the program (a process of the install folder is running)' ($alive.Count -gt 0)
            foreach ($a in $alive) { try { [void]$a.CloseMainWindow(); if (-not $a.WaitForExit(8000)) { $a.Kill() } } catch { } }
        }
    }
    $dataNow = @{}; foreach ($d in $dataFolders) { $dataNow[$d] = Get-FolderSnapshot $d }
    foreach ($d in $dataFolders) { Check "$d is untouched" ((@($dataBefore[$d]) -join "`n") -eq (@($dataNow[$d]) -join "`n")) }

    # --- an older MSI over the newer one is refused ------------------------------------------------------------------------
    Say "`n== the older $verOld over $verNew"
    $exit = Invoke-Msi "downgrade-$verOld" @('/i', "`"$OldMsi`"")
    Check 'refused (a non-zero exit)' ($exit -ne 0) "exit $exit"
    $entries = @(Get-Entries)
    Check "still one Apps entry, still $verNew" ($entries.Count -eq 1 -and [version]$entries[0].Version -eq $verNew) "found $($entries | ForEach-Object { $_.Version })"
    $afterDowngrade = Get-FileHashes $programs
    Check 'the files are the new version''s' (-not (Compare-Object @($now.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) @($afterDowngrade.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })))

    # --- uninstall ----------------------------------------------------------------------------------------------------------
    Say "`n== uninstall"
    $exit = Invoke-Msi 'uninstall' @('/x', $codeNew)
    $installedCode = $null
    Check 'uninstalls (exit 0)' ($exit -eq 0) "exit $exit"
    Check 'no Apps entry left' (@(Get-Entries).Count -eq 0)
    Check 'the install folder is gone' (-not (Test-Path $programs))
    Check 'the shortcut is gone' (-not (Test-Path $shortcut))
    $dataNow = @{}; foreach ($d in $dataFolders) { $dataNow[$d] = Get-FolderSnapshot $d }
    foreach ($d in $dataFolders) { Check "$d is still untouched" ((@($dataBefore[$d]) -join "`n") -eq (@($dataNow[$d]) -join "`n")) }
}
finally {
    # whatever this run installed and did not get to uninstall: only the product it installed itself
    if ($installedCode -and (Get-ProductInfo $installedCode 'ProductName')) {
        Say "`n(cleanup) uninstalling $installedCode, which this run installed"
        [void](Invoke-Msi 'cleanup' @('/x', $installedCode))
    }
}
Say ''
if ($failures.Count) { Say "FAILED: $($failures.Count) checks: $($failures -join '; ')"; exit 1 }
Say 'PASSED: all checks'
exit 0
