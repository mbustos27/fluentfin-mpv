# 1.5 qacheck: a -Then script of tools\ui-harness\run-offscreen.ps1 -QaServer 10.11|12 for the server address checks
# (subpath-probe.ps1 runs it): after the run's steps (home, library, movie: sign-in, browsing and images), the showcase movie
# plays for a few seconds (then-qa-play-movie.ps1: the backend's PlaybackInfo, mpv's stream, the reports) and then the
# tap's log is read:
#   - behind a sub-path (NIXIE_QA_BASE_PATH=/jellyfin), nothing of Nixie's, its backend's or mpv's went outside it (the
#     proxy's 404, "prefixed": false);
#   - no path with "//" in it (an address typed with a "/" at its end, joined to "/Items/..." by string);
#   - no answer of 500 or more, and no 4xx but the ones an ordinary user always gets (GET /Plugins 403, the playback
#     reporting plugin's 404, a new server's splash screen 404: docs\QA-SERVERS.md; an image the item does not have);
#   - the app's images, the backend's requests and the stream all went through it, and the address the app saved.
# Variables here start with sb.

if (-not $env:NIXIE_QA_PLAY_SECONDS) { $env:NIXIE_QA_PLAY_SECONDS = '6' }
. (Join-Path $PSScriptRoot 'then-qa-play-movie.ps1')

$sbBase = if ($env:NIXIE_QA_BASE_PATH) { '/' + $env:NIXIE_QA_BASE_PATH.Trim('/') } else { '' }
$sbAll = @(Get-Requests 0 | Where-Object { $_.route -ne 'harness' })
$sbOrigin = @{}
foreach ($sbR in $sbAll) { $sbO = Get-Origin $sbR; $sbOrigin[$sbO] = 1 + [int]$sbOrigin[$sbO] }
Say ("address: {0} request(s) through the tap ({1}); typed '{2}'" -f $sbAll.Count, (($sbOrigin.Keys | Sort-Object | ForEach-Object { "$_ $($sbOrigin[$_])" }) -join ', '),
    $(if ($script:signInAddress) { $script:signInAddress } else { $script:serverUrl }))
function Sb-List($Rows) { (@($Rows | Select-Object -First 8) | ForEach-Object { "{0} {1}{2} {3} ({4})" -f $_.method, $(if ($_.PSObject.Properties['prefixed'] -and $_.prefixed) { $sbBase } else { '' }), $_.path, $_.status, (Get-Origin $_) }) -join '; ' }

if ($sbBase) {
    $sbOutside = @($sbAll | Where-Object { $_.PSObject.Properties['prefixed'] -and -not $_.prefixed })
    Check "address: every request stayed under $sbBase (the app, its backend and mpv)" ($sbOutside.Count -eq 0) $(if ($sbOutside) { "$($sbOutside.Count) outside: $(Sb-List $sbOutside)" } else { "$($sbAll.Count) request(s), all under it" })
}
$sbDouble = @($sbAll | Where-Object { $_.PSObject.Properties['double_slash'] -and $_.double_slash })
Check 'address: no request path with "//" in it' ($sbDouble.Count -eq 0) $(if ($sbDouble) { "$($sbDouble.Count): $(Sb-List $sbDouble)" })
# (and an item's image the server does not have: a library or a folder without art, 404, drawn as a placeholder; and
# 10.11's 404 for /Items/{id}/Collections, the movie page's "Part of", a 12.x request Nixie leaves out quietly there)
$sbKnown = { param($r) ($r.status -eq 403 -and $r.path -match '^/Plugins/?$') -or ($r.status -eq 404 -and ($r.path -match '^/user_usage_stats/|^/Branding/Splashscreen|^/Items/[^/]+/Collections$' -or $r.route -eq 'image')) }
$sbBad = @($sbAll | Where-Object { $_.status -ge 500 -or ($_.status -ge 400 -and -not (& $sbKnown $_) -and -not ($_.PSObject.Properties['prefixed'] -and -not $_.prefixed)) })
Check 'address: no failed request but the ones an ordinary user always gets' ($sbBad.Count -eq 0) $(if ($sbBad) { "$($sbBad.Count): $(Sb-List $sbBad)" })
$sbImages = @($sbAll | Where-Object { $_.route -eq 'image' -and $_.status -eq 200 })
$sbBackend = @($sbAll | Where-Object { (Get-Origin $_) -eq 'backend' -and $_.status -lt 400 })
$sbStream = @($sbAll | Where-Object { $_.route -eq 'stream' -and $_.status -lt 400 })
Check 'address: images, the backend and the stream all came through it' ($sbImages.Count -gt 0 -and $sbBackend.Count -gt 0 -and $sbStream.Count -gt 0) `
    ("{0} image(s), {1} backend request(s), {2} stream request(s)" -f $sbImages.Count, $sbBackend.Count, $sbStream.Count)
# what the app saved for this server (the address a later start signs in with)
$sbSaved = try { @((Get-Content -LiteralPath (Join-Path $DataDir 'LocalSettings.json') -Raw | ConvertFrom-Json).Servers | ForEach-Object { $_.LocalUrl, $_.PublicUrl } | Where-Object { $_ }) } catch { @() }
Say "address: saved as $(if ($sbSaved) { ($sbSaved | Sort-Object -Unique) -join ', ' } else { 'nothing' })"
