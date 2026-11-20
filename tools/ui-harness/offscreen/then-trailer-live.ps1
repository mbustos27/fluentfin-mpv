# Nixie 1.4.0, trailers against REAL YouTube (opt-in: NIXIE_LIVE_YOUTUBE=1; never in a default suite). The one check of the
# theatre against YouTube itself: the 1.4.0 test build showed YouTube's "Error 153, Video player configuration error" on every
# trailer, which no harness run could see (they load the fake server's stand-in). Off-screen and silent: the app mutes its web
# view under the harness (CoreWebView2.IsMuted), and for an older build that does not, run it with
# WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--mute-audio (Chromium's own switch). Only public embed pages: no sign-in, the web
# view's cookies stay in the run's data folder, no Jellyfin server but the fake one. Nothing is clicked inside YouTube's
# player, and under the harness the app never launches the browser.
#   NIXIE_LIVE_YOUTUBE=1 [NIXIE_LIVE_PHASE=before|after] [NIXIE_LIVE_IDS=a,b] [NIXIE_LIVE_BLOCKED_IDS=c,d]
#   run-offscreen.ps1 -Script movie -Then offscreen\then-trailer-live.ps1 [-Exe <an older Nixie.exe>]
# For each id the fake server's showcase movie gets that trailer (POST /__harness/trailer {"url"}), R loads the page again,
# Enter on Trailer opens the theatre, and the app's [trailer] log says what the player did. "after": each playable id reaches
# playing (the IFrame Player API's state 1) with no error, and a blocked one shows Nixie's message. "before" (a build without
# the wrapper, no state to log): the theatre is captured and read with Windows' OCR; the shot is the evidence either way.
# Dot-sourced by drive.ps1: every name here starts with TL.

if ($env:NIXIE_LIVE_YOUTUBE -ne '1') { Say 'trailer live: NIXIE_LIVE_YOUTUBE is not 1, skipped (it asks real YouTube: opt-in only)'; return }
$tlPhase = if ($env:NIXIE_LIVE_PHASE -in @('before', 'after')) { $env:NIXIE_LIVE_PHASE } else { 'after' }
# official trailers and an open film that allow embedding; the blocked ones are tried until one says so
$tlPlays = @($(if ($env:NIXIE_LIVE_IDS) { $env:NIXIE_LIVE_IDS } else { 'YoHD9XEInc0,zSWdZVtXT7E,aqz-KE-bpKQ' }) -split ',' | Where-Object { $_ -match '^[A-Za-z0-9_-]{11}$' })
$tlBlocked = @($(if ($env:NIXIE_LIVE_BLOCKED_IDS) { $env:NIXIE_LIVE_BLOCKED_IDS } else { '' }) -split ',' | Where-Object { $_ -match '^[A-Za-z0-9_-]{11}$' })
Say "trailer live ($tlPhase): plays $($tlPlays -join ', '); blocked candidates $(if ($tlBlocked) { $tlBlocked -join ', ' } else { 'none' })"

function TL-Log([double]$Since) {
    @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Encoding utf8 | Where-Object { $_ -match '^(\S+ \S+ [+-]\d\d:\d\d) \[(INF|WRN)\] \[trailer\] (.*)$' } | ForEach-Object {
            $tlWhen = [DateTimeOffset]::MinValue
            if ([DateTimeOffset]::TryParse(($_ -split ' \[(INF|WRN)\] ')[0], [ref]$tlWhen) -and $tlWhen.ToUnixTimeMilliseconds() / 1000.0 -ge $Since) { ($_ -split '\[trailer\] ', 2)[1] }
        }
    })
}

# Windows' own OCR of a shot (Windows.Media.Ocr; best effort: a failure is said, not a check)
function TL-Ocr([string]$File) {
    try {
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        $null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
        $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
        $null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType = WindowsRuntime]
        $tlAsTask = @([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
        $tlAwait = { param($Op, [Type]$T) $t = $tlAsTask.MakeGenericMethod($T).Invoke($null, @($Op)); [void]$t.Wait(20000); $t.Result }
        $tlFile = & $tlAwait ([Windows.Storage.StorageFile]::GetFileFromPathAsync($File)) ([Windows.Storage.StorageFile])
        $tlStream = & $tlAwait ($tlFile.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
        $tlDecoder = & $tlAwait ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($tlStream)) ([Windows.Graphics.Imaging.BitmapDecoder])
        $tlBitmap = & $tlAwait ($tlDecoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
        $tlEngine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new('en-US'))
        if (-not $tlEngine) { $tlEngine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages() }
        $tlText = (& $tlAwait ($tlEngine.RecognizeAsync($tlBitmap)) ([Windows.Media.Ocr.OcrResult])).Text
        $tlStream.Dispose()
        return $tlText
    }
    catch { return "(OCR failed: $($_.Exception.Message))" }
}

function TL-Play([string]$Id, [string]$Kind) {
    [void](Invoke-Server '/__harness/trailer' 'POST' @{ url = "https://www.youtube.com/watch?v=$Id" })
    $tlAt = Get-Epoch
    Key 0x52   # R: the movie page asks the server again (the new trailer link)
    [void](Wait-Until { @(Get-Requests $tlAt | Where-Object { $_.auth -and $_.path -match [regex]::Escape($fixture.movie.id) }) | Select-Object -First 1 } -Seconds 10 -PollMs 200)
    Start-Sleep -Seconds 2
    $tlButton = Wait-Ui -Name 'Watch trailer' -Type 'Button' -Seconds 15
    if (-not $tlButton) { Check "trailer live ($tlPhase) ${Id}: the movie page has a Trailer button" $false; return }
    [void][OffscreenUi]::SetFocus($tlButton)
    Start-Sleep -Milliseconds 300
    $tlAt = Get-Epoch
    Key 0x0D
    $tlOpened = Wait-Until { @(TL-Log $tlAt | Where-Object { $_ -like 'theatre opened*' }) | Select-Object -First 1 } -Seconds 10 -PollMs 200
    Check "trailer live ($tlPhase) ${Id}: Enter on Trailer opens the theatre" ([bool]$tlOpened) "$tlOpened"
    if ($tlPhase -eq 'after') {
        $tlEnd = Wait-Until { @(TL-Log $tlAt | Where-Object { $_ -match 'the player: ready True, state 1, error 0|Nixie''s message' }) | Select-Object -First 1 } -Seconds 25 -PollMs 250
        Start-Sleep -Seconds 2
    }
    else { Start-Sleep -Seconds 12 }
    Shot "live-$tlPhase-$Kind-$Id"
    $tlText = TL-Ocr $shots["live-$tlPhase-$Kind-$Id"]
    $tlLines = @(TL-Log $tlAt)
    Say "trailer live ($tlPhase) ${Id}: the log: $($tlLines -join ' | ')"
    Say "trailer live ($tlPhase) ${Id}: OCR of the shot: $(($tlText -replace '\s+', ' ').Trim())"
    $tlRefused = @($tlLines | Where-Object { $_ -match 'Nixie''s message \(YouTube refused it: error (\d+)\)' } | ForEach-Object { [int]$Matches[1] }) | Select-Object -First 1
    $tlPlaying = [bool](@($tlLines | Where-Object { $_ -match 'the player: ready True, state 1, error 0' }).Count)
    $tlBrowser = @($tlLines | Where-Object { $_ -match 'the browser (opens|would open)' })
    $tl153 = $tlText -match '153|configuration error'
    if ($tlPhase -eq 'before') {
        Say "trailer live (before) ${Id}: YouTube's Error 153 on the shot (OCR): $tl153"
    }
    elseif ($Kind -eq 'plays') {
        Check "trailer live (after) ${Id}: the player reaches playing (state 1) with no error and no message" ($tlPlaying -and -not $tlRefused -and -not ($tlLines -match 'Nixie''s message')) ($tlLines -join ' | ')
        Check "trailer live (after) ${Id}: no Error 153 on the shot (OCR)" (-not $tl153) "$tlText"
    }
    Check "trailer live ($tlPhase) ${Id}: no browser launched" ($tlBrowser.Count -eq 0) ($tlBrowser -join ' | ')
    $tlAt2 = Get-Epoch
    Key 0x1B
    [void](Wait-Until { @(TL-Log $tlAt2 | Where-Object { $_ -eq 'theatre closed' }) | Select-Object -First 1 } -Seconds 5 -PollMs 150)
    Start-Sleep -Milliseconds 800
    return [pscustomobject]@{ id = $Id; kind = $Kind; playing = $tlPlaying; refused = $tlRefused; ocr153 = $tl153 }
}

[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080)
Start-Sleep -Milliseconds 1500
$tlResults = @()
foreach ($tlId in $tlPlays) { $tlResults += TL-Play $tlId 'plays' }
$tlFoundBlocked = $null
foreach ($tlId in $tlBlocked) {
    $tlR = TL-Play $tlId 'blocked'
    $tlResults += $tlR
    if ($tlR.refused -in @(101, 150)) { $tlFoundBlocked = $tlR; break }
}
if ($tlPhase -eq 'after' -and $tlBlocked) {
    Check 'trailer live (after): an id that disallows embedding shows Nixie''s message (error 101 or 150)' ([bool]$tlFoundBlocked) (($tlResults | Where-Object kind -eq 'blocked' | ForEach-Object { "$($_.id): refused $($_.refused), playing $($_.playing)" }) -join '; ')
}
[void](Invoke-Server '/__harness/trailer' 'POST' @{ url = '' })
[IO.File]::WriteAllText((Join-Path $Out "trailer-live-$tlPhase.json"), (ConvertTo-Json -InputObject @($tlResults) -Depth 4), [Text.UTF8Encoding]::new($false))
Say "trailer live ($tlPhase): $(($tlResults | ForEach-Object { "$($_.id) [$($_.kind)] playing=$($_.playing) refused=$($_.refused) ocr153=$($_.ocr153)" }) -join '; ')"
