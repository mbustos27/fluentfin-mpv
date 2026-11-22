# 1.6.1: a -Before script of tools\ui-harness\run-offscreen.ps1: the run's backend logs at DEBUG (the shim's own
# mpv_log_level in the run's mpv-backend\conf.json). then-160-player.ps1 counts the Playback stats' get_stats questions in
# the backend's log, and since 1.6.1 a polled command's "<-" line is DEBUG (bridge.py, Bridge.POLLED). The backend is
# started at sign-in (MpvPrewarm), after this runs, so it reads the file. Only the run's own -DataDir is written.
# Dot-sourced by drive.ps1 on the server page; it leaves the page as it found it.

$bdConf = Join-Path $DataDir 'mpv-backend\conf.json'
New-Item -ItemType Directory -Force (Split-Path $bdConf) | Out-Null
$bdJson = if (Test-Path -LiteralPath $bdConf) { Get-Content -LiteralPath $bdConf -Raw | ConvertFrom-Json } else { $null }
if ($null -eq $bdJson) { $bdJson = [pscustomobject]@{} }
$bdJson | Add-Member -NotePropertyName mpv_log_level -NotePropertyValue 'debug' -Force
[IO.File]::WriteAllText($bdConf, ($bdJson | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
Say "before-backend-debug: the run's backend logs at DEBUG (mpv_log_level in $bdConf)"
