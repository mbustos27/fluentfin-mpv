# Nixie 1.7.5, the basics gate, class 8: the -Before script of 8-lifecycle.ps1. On the server page, before the sign-in: starts
# g4_forward.py (a TCP forwarder on 127.0.0.1 in front of the fake server, in this run's job) and has the sign-in type its
# address, so the server Nixie saves is the forwarder and the lifecycle check can take it away and bring it back.
# Dot-sourced by drive.ps1 (its helpers; no assignment of its parameters): names start with g4f.

$g4fDir = Join-Path $Out 'forward'
New-Item -ItemType Directory -Force $g4fDir | Out-Null
$g4fTarget = ([Uri]$script:serverUrl).Authority
$g4fProc = Start-Process -FilePath $Python -ArgumentList @('-I', '-B', ('"' + (Join-Path $PSScriptRoot 'g4_forward.py') + '"'), '--target', $g4fTarget, '--dir', ('"' + $g4fDir + '"')) `
    -PassThru -WindowStyle Hidden -RedirectStandardError (Join-Path $g4fDir 'stderr.txt') -RedirectStandardOutput (Join-Path $g4fDir 'stdout.txt')
Add-Tracked -Process $g4fProc
$g4fPort = Wait-Until { $f = Join-Path $g4fDir 'port.txt'; if (Test-Path $f) { (Get-Content -LiteralPath $f -Raw).Trim() } } -Seconds 20 -PollMs 100
if (-not $g4fPort) { throw "the forwarder did not start (see $g4fDir\stderr.txt)" }
$script:g4Forward = $g4fDir
$script:signInAddress = "http://127.0.0.1:$g4fPort"
Say "lifecycle: the forwarder on $($script:signInAddress) in front of $($script:serverUrl); the sign-in uses its address"
