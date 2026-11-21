<#
.SYNOPSIS
  The upgrade test in Windows Sandbox (Nixie 1.5): installs and upgrades Nixie inside a throwaway Sandbox, never in this
  account. Generates a .wsb, starts the Sandbox, waits for its results. The coordinator runs it at release, after telling
  the owner (the Sandbox's window shows on the desktop). Agents only run it with -DryRun.

.DESCRIPTION
  The Sandbox gets, by mapped folders: the release's dist folder, the previous release's installer and the test build,
  READ-ONLY; the scripts (tools\release\sandbox), read-only; and one results folder, read-write. Networking is off (no
  step needs GitHub: the updater's download is replaced by a local installer). No GPU, microphone, camera, clipboard or
  printer. Its logon command runs tools\release\sandbox\upgrade-inside.ps1, which (as a standard user it makes inside the
  Sandbox) runs two legs and writes results.json, then shuts the Sandbox down:
    leg 1, the old flow: the previous MSI (1.4.1), then this release over it the way 1.4.1's updater does it (msiexec /i,
      Nixie closed, nothing reopened); the installed version, and the data folders (a Seaglass folder copied to Nixie by
      the older version, kept by this one, the Seaglass folder untouched);
    leg 2, the new flow: this release's "Restart to update" (driven by Nixie.exe --install-update <msi>, no clicks) to a
      test build numbered higher (this release's files in an MSI of version -TestVersion, built here, never published):
      Nixie closes, the launcher's helper installs quietly (msiexec /passive, logged) and opens Nixie again;
    and the proof that the per-user MSI needs no administrator: the tester is not an administrator and runs at medium
      integrity (whoami /groups), every install exits 0, and the msiexec logs' privilege lines are kept.
  Nothing is installed by this script on this PC, and the inside script refuses to run anywhere but in the Sandbox.
  Plays no media: Nixie never signs in to a server there.

  Exit status: 0 the Sandbox's checks passed (or -DryRun wrote the plan); 1 a check failed or no results came;
  2 bad inputs; 3 Windows Sandbox is missing or already running.

.EXAMPLE
  .\tools\release\upgrade-test-sandbox.ps1 -Out C:\scratch\sandbox-test -DryRun
  .\tools\release\upgrade-test-sandbox.ps1 -Out C:\scratch\sandbox-test        # the coordinator, at release
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Out,
    [string]$Dist,
    [string]$Version,
    [string]$PreviousMsi,
    [string]$PreviousVersion = '1.4.1',
    [string]$TestVersion,
    [string]$TestMsi,
    [switch]$DryRun,
    [int]$TimeoutMinutes = 45
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$Out = [IO.Path]::GetFullPath($Out)
if ($Out.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) { Write-Host "-Out must be outside the repository ($repo)"; exit 2 }
if (-not $Dist) { $Dist = Join-Path $repo 'dist' }
$Dist = [IO.Path]::GetFullPath($Dist)
if (-not $Version) {
    $csproj = Get-Content -Raw (Join-Path $repo 'ui\FluentFin\FluentFin.csproj')
    $Version = ([regex]::Matches($csproj, '>(\d+\.\d+\.\d+)</Version>') | Select-Object -First 1).Groups[1].Value
}
if (-not $TestVersion) { $v = [version]$Version; $TestVersion = '{0}.{1}.{2}' -f $v.Major, $v.Minor, ($v.Build + 90) }
$SandboxRoot = 'C:\nixie'

function Fail([int]$Code, [string]$Why) { Write-Host "upgrade-test-sandbox: $Why"; exit $Code }

# --- the inputs ----------------------------------------------------------------------------------------------------------
$newMsi = Join-Path $Dist "Nixie-$Version-win-x64.msi"
if (-not $PreviousMsi) { $PreviousMsi = Join-Path $Dist "Nixie-$PreviousVersion-win-x64.msi" }
$PreviousMsi = [IO.Path]::GetFullPath($PreviousMsi)
foreach ($pair in @(@('this release', $newMsi), @('the previous release', $PreviousMsi))) {
    if (-not (Test-Path -LiteralPath $pair[1])) { Fail 2 "no installer of $($pair[0]) at $($pair[1])" }
    if (-not (Test-Path -LiteralPath "$($pair[1]).sha256")) { Fail 2 "no .sha256 beside $($pair[1])" }
}
if ([version]$PreviousVersion -ge [version]$Version) { Fail 2 "the previous release ($PreviousVersion) is not older than this one ($Version)" }
if ([version]$TestVersion -le [version]$Version) { Fail 2 "the test build ($TestVersion) must be numbered higher than $Version" }
foreach ($folder in $Dist, (Split-Path $PreviousMsi)) {
    if ($folder.StartsWith($Out, [StringComparison]::OrdinalIgnoreCase)) { Fail 2 "-Out ($Out) must not hold an input folder ($folder)" }
}

$testBuild = Join-Path $Out 'test-build'
$results = Join-Path $Out 'results'
# review m9: only a folder this script made (it holds its marker) is emptied again; anything else of that name is refused
$marker = '.nixie-upgrade-test'
foreach ($d in $testBuild, $results) {
    if (Test-Path -LiteralPath $d) {
        if (-not (Test-Path -LiteralPath (Join-Path $d $marker))) { Fail 2 "$d exists and is not this script's (no $marker in it): choose another -Out" }
        Remove-Item -LiteralPath $d -Recurse -Force
    }
    New-Item -ItemType Directory -Force $d | Out-Null
    [IO.File]::WriteAllText((Join-Path $d $marker), "made by tools\release\upgrade-test-sandbox.ps1`n", [Text.UTF8Encoding]::new($false))
}
$testName = "Nixie-$TestVersion-win-x64.msi"
if ($TestMsi) {
    if (-not (Test-Path -LiteralPath $TestMsi) -or (Split-Path -Leaf $TestMsi) -ne $testName) { Fail 2 "-TestMsi must be an existing $testName" }
    Copy-Item -LiteralPath $TestMsi (Join-Path $testBuild $testName)
}
elseif (-not $DryRun) {
    # the test build: this release's own files in an MSI numbered -TestVersion (WiX as build-release.ps1 runs it)
    $package = Join-Path $Dist "Nixie-$Version-win-x64"
    if (-not (Test-Path $package)) { Fail 2 "no release folder $package to build the test installer from" }
    $work = Join-Path $Out 'test-build-work'
    New-Item -ItemType Directory -Force $work | Out-Null
    $art = Join-Path $work 'art'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'installer\make-installer-art.ps1') -Out $art | Out-Null
    $env:DOTNET_ROLL_FORWARD = 'Major'
    $env:WIX_EXTENSIONS = Join-Path $Dist '.work\wix-ext'
    $wixDir = Join-Path $PSScriptRoot 'installer'
    Push-Location $repo
    try {
        & dotnet tool run wix build (Join-Path $wixDir 'Nixie.wxs') (Join-Path $wixDir 'NixieUI.wxs') -arch x64 -ext 'WixToolset.UI.wixext/5.0.2' `
            -loc (Join-Path $wixDir 'Nixie.en-us.wxl') -d "Version=$TestVersion" -d "PackageDir=$package" `
            -d "IconFile=$(Join-Path $repo 'ui\FluentFin\Assets\Nixie.ico')" -d "ArtDir=$art" -intermediatefolder (Join-Path $work 'wix') -pdbtype none `
            -o (Join-Path $testBuild $testName)
        if ($LASTEXITCODE -ne 0) { Fail 2 'the test installer did not build (wix)' }
    }
    finally { Pop-Location; Remove-Item Env:DOTNET_ROLL_FORWARD, Env:WIX_EXTENSIONS -ErrorAction SilentlyContinue }
}
$testPath = Join-Path $testBuild $testName
if (Test-Path $testPath) {
    $hash = (Get-FileHash -Algorithm SHA256 $testPath).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$testPath.sha256", "$hash *$testName`n", [Text.UTF8Encoding]::new($false))
}

# --- the Sandbox's configuration -----------------------------------------------------------------------------------------
function New-SandboxConfig([object[]]$Mappings, [string]$LogonCommand) {
    $esc = { param($s) [Security.SecurityElement]::Escape($s) }
    $lines = @('<Configuration>', '  <VGpu>Disable</VGpu>', '  <Networking>Disable</Networking>', '  <AudioInput>Disable</AudioInput>',
        '  <VideoInput>Disable</VideoInput>', '  <ClipboardRedirection>Disable</ClipboardRedirection>', '  <PrinterRedirection>Disable</PrinterRedirection>',
        '  <ProtectedClient>Disable</ProtectedClient>', '  <MemoryInMB>4096</MemoryInMB>', '  <MappedFolders>')
    foreach ($m in $Mappings) {
        $lines += "    <MappedFolder><HostFolder>$(& $esc $m.Host)</HostFolder><SandboxFolder>$(& $esc $m.Sandbox)</SandboxFolder><ReadOnly>$(([string]$m.ReadOnly).ToLowerInvariant())</ReadOnly></MappedFolder>"
    }
    $lines += @('  </MappedFolders>', "  <LogonCommand><Command>$(& $esc $LogonCommand)</Command></LogonCommand>", '</Configuration>')
    $lines -join "`r`n"
}

$previousFolder = Split-Path $PreviousMsi
$mappings = @(
    [pscustomobject]@{ Host = $Dist; Sandbox = "$SandboxRoot\dist"; ReadOnly = $true },
    [pscustomobject]@{ Host = $testBuild; Sandbox = "$SandboxRoot\test-build"; ReadOnly = $true },
    [pscustomobject]@{ Host = (Join-Path $PSScriptRoot 'sandbox'); Sandbox = "$SandboxRoot\scripts"; ReadOnly = $true },
    [pscustomobject]@{ Host = $results; Sandbox = "$SandboxRoot\results"; ReadOnly = $false })
if (-not $previousFolder.Equals($Dist, [StringComparison]::OrdinalIgnoreCase)) {
    $mappings = @([pscustomobject]@{ Host = $previousFolder; Sandbox = "$SandboxRoot\previous"; ReadOnly = $true }) + $mappings
}
$previousInSandbox = if ($previousFolder.Equals($Dist, [StringComparison]::OrdinalIgnoreCase)) { 'dist' } else { 'previous' }
$logon = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File $SandboxRoot\scripts\upgrade-inside.ps1 -Root $SandboxRoot " +
    "-NewMsi dist\Nixie-$Version-win-x64.msi -PreviousMsi $previousInSandbox\$(Split-Path -Leaf $PreviousMsi) -TestMsi test-build\$testName"
$wsb = Join-Path $Out 'nixie-upgrade-test.wsb'
[IO.File]::WriteAllText($wsb, (New-SandboxConfig $mappings $logon), [Text.UTF8Encoding]::new($false))
$plan = [ordered]@{ version = $Version; previous = $PreviousVersion; test = $TestVersion; wsb = $wsb; networking = 'off'
    mapped = @($mappings | ForEach-Object { [ordered]@{ host = $_.Host; sandbox = $_.Sandbox; readOnly = $_.ReadOnly } }); logon = $logon
    legs = @('1: previous MSI, then this release over it by the old flow (closes, nothing reopens); version and data folders',
             '2: this release, Restart to update (--install-update) to the test build: quiet install, Nixie reopens',
             'no administrator: a standard user at medium integrity; msiexec exit codes and log lines kept') }
$plan | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 (Join-Path $Out 'plan.json')
Write-Host "wrote $wsb"
if ($DryRun) {
    Write-Host "dry run: the Sandbox was not started. Plan: $(Join-Path $Out 'plan.json')"
    if (-not (Test-Path $testPath)) { Write-Host "(dry run: the test installer $testName would be built from $Dist\Nixie-$Version-win-x64)" }
    exit 0
}

# --- run it ---------------------------------------------------------------------------------------------------------------
$sandboxExe = Join-Path $env:SystemRoot 'System32\WindowsSandbox.exe'
if (-not (Test-Path $sandboxExe)) { Fail 3 'Windows Sandbox is not turned on (Windows features: Windows Sandbox)' }
if (Get-Process -Name 'WindowsSandbox', 'WindowsSandboxClient', 'WindowsSandboxRemoteSession' -ErrorAction SilentlyContinue) { Fail 3 'a Windows Sandbox is already running: close it first' }
Write-Host 'starting Windows Sandbox (its window shows on the desktop; it closes itself when the test is done)'
Start-Process -FilePath $sandboxExe -ArgumentList "`"$wsb`""
$done = Join-Path $results 'done.json'
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while (-not (Test-Path $done) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }
if (-not (Test-Path $done)) { Fail 1 "no results within $TimeoutMinutes minutes (see $results)" }
$r = Get-Content -Raw (Join-Path $results 'results.json') | ConvertFrom-Json
foreach ($c in $r.checks) { Write-Host ('  {0,-5} {1} {2}' -f $(if ($c.ok) { 'ok' } else { 'FAIL' }), $c.name, $c.detail) }
if (@($r.checks | Where-Object { -not $_.ok }).Count) { Write-Host 'FAILED'; exit 1 }
Write-Host 'PASSED'
exit 0
