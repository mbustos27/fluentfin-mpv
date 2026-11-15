<#
  Checks common.ps1's Start-FluentFin against a stand-in process of its own, never FluentFin: a windowless pythonw
  (the backend venv's base interpreter) sleeping in a script under -Out, so -Out is in its command line.

    check-common.ps1 -Out <scratch folder> [-Common <a copy of common.ps1>]

  1. Tracking forced to fail (the script's start moved a minute ahead, so the stand-in looks older than the script):
     Start-FluentFin must throw AND leave no stand-in running (round 8: it threw and left it running).
  2. Control: tracking intact, Start-FluentFin returns the stand-in tracked; it is ended through Stop-Tracked.

  Anything left over is ended only if it is provably this run's: -Out in its command line, created after this script
  began, and then only through a handle that checks that creation time (Add-Tracked -Created, Stop-Tracked).
  Prints PASS/FAIL lines; exit 0 when both checks pass.
#>
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Common = ''
)
$ErrorActionPreference = 'Stop'
if (-not $Common) { $Common = Join-Path $PSScriptRoot 'common.ps1' }   # not in the param block: empty there on 5.1
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))   # this script's repo, whichever common.ps1 is checked
. $Common

$began = $script:HarnessStartedFt
New-Item -ItemType Directory -Force $Out | Out-Null
$marker = [IO.Path]::GetFullPath((Join-Path $Out ("standin-" + [Guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Force $marker | Out-Null
$standin = Join-Path $marker 'standin.py'
Set-Content -Path $standin -Value 'import time; time.sleep(60)' -Encoding ascii
$cfg = Get-Content (Join-Path $repo 'backend\.venv\pyvenv.cfg') | Where-Object { $_ -match '^\s*home\s*=' } | Select-Object -First 1
$pythonw = Join-Path (($cfg -split '=', 2)[1].Trim()) 'pythonw.exe'
if (-not (Test-Path $pythonw)) { throw "no pythonw.exe at $pythonw" }
$arguments = @('-I', "`"$standin`"")

# This run's stand-ins still running: -Out's marker in the command line, created after the script began.
function Get-Standins {
    @(Get-CimInstance Win32_Process | Where-Object {
        $_.CommandLine -and $_.CommandLine.Contains($marker) -and $_.CreationDate -and $_.CreationDate.ToFileTime() -ge $began
    })
}

$failed = 0

# 1. tracking forced to fail
$script:HarnessStartedFt = [UiHarnessNative]::Now() + 600000000L   # a minute ahead: the stand-in looks older than us
$threw = $null
try { $null = Start-FluentFin -Exe $pythonw -Arguments $arguments } catch { $threw = $_.Exception.Message }
$script:HarnessStartedFt = $began
Start-Sleep -Milliseconds 500
$left = @(Get-Standins)
"failure path: threw=[$threw] stand-ins left running: $($left.Count)"
if ($threw -and $left.Count -eq 0) { 'PASS start-fluentfin-untracked-is-ended' } else { 'FAIL start-fluentfin-untracked-is-ended'; $failed++ }
foreach ($s in $left) {
    Add-Tracked -Id ([int]$s.ProcessId) -Created $s.CreationDate.ToFileTime()
    if ($script:Tracked.ContainsKey([int]$s.ProcessId)) { $null = Stop-Tracked ([int]$s.ProcessId); "  ended leftover pythonw#$($s.ProcessId) (this run's)" }
}

# 2. control: tracking intact
$p = $null
try { $p = Start-FluentFin -Exe $pythonw -Arguments $arguments } catch { "control threw: $($_.Exception.Message)" }
if ($p -and $script:Tracked.ContainsKey($p.Id) -and (Get-TrackedProcess $p.Id)) { 'PASS start-fluentfin-tracked' } else { 'FAIL start-fluentfin-tracked'; $failed++ }
if ($p -and $script:Tracked.ContainsKey($p.Id)) { $null = Stop-Tracked $p.Id }
Start-Sleep -Milliseconds 500
Reset-Tracked
$still = @(Get-Standins)
"stand-ins still running at the end: $($still.Count)"
if ($still.Count -ne 0) { $failed++ }
exit $failed
