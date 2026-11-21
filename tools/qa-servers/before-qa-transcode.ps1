# 1.5 qacheck: a -Before script of tools\ui-harness\run-offscreen.ps1: the run's backend transcodes everything (the shim's
# own always_transcode setting in the run's mpv-backend\conf.json), so then-qa-playlist.ps1's audio part changes the track
# of a transcode (the backend's restart: a new PlaybackInfo and a new stream for the new track). The backend is started at
# sign-in (MpvPrewarm), after this runs, so it reads the file. Only the run's own -DataDir is written. Dot-sourced by
# drive.ps1 on the server page; it leaves the page as it found it.

$btConf = Join-Path $DataDir 'mpv-backend\conf.json'
New-Item -ItemType Directory -Force (Split-Path $btConf) | Out-Null
$btJson = if (Test-Path -LiteralPath $btConf) { Get-Content -LiteralPath $btConf -Raw | ConvertFrom-Json } else { $null }
if ($null -eq $btJson) { $btJson = [pscustomobject]@{} }
$btJson | Add-Member -NotePropertyName always_transcode -NotePropertyValue $true -Force
[IO.File]::WriteAllText($btConf, ($btJson | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
Say "before-qa-transcode: the run's backend transcodes everything (always_transcode in $btConf)"
