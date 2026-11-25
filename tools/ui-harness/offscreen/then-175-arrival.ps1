# Nixie 1.7.5 (KNOWN-ISSUES, 1.6.1 and 1.7.3 Left): the Collections page opened from the Libraries page's "Made by Nixie"
# tile gives the keyboard to its first Nixie tile, every time. Under load it sometimes stayed on Back: the first try "could
# not take the keyboard" and no try came after it, with no line. Run per theme, a few at once for the load:
#   $env:FAKESERVER_COLLECTIONS='1'; $env:FAKESERVER_COLLIDE='1'; $env:NIXIE_LAYOUT='<Cinematic|DriveIn|...>'
#   [$env:AR_TRIALS='7'] [$env:AR_WIDTHS='1920,1100'] [$env:AR_WITHIN_MS='6000']
#   run-offscreen.ps1 ... -Script wait:1 -NoA11y -Then offscreen\then-175-arrival.ps1
# Each trial at each width: the window sized (1920 x 1080, 1100 x 900), the Libraries page opened from the top bar, its
# "Made by Nixie, N" tile invoked, then the keyboard watched for AR_WITHIN_MS: a row passes when the element with the keyboard
# is a Nixie tile ("..., made by Nixie, ..."), on screen. Then Home, so the next trial is a new arrival. The app's "[focus]
# CollectionsPage" lines of the trial go with its row (why a try could not take the keyboard, or why the tries stopped).
# Rows in <Out>\arrival175\arrival175.json (theme, width, trial, ms, focus, ok, log); each a Check.
# Dot-sourced by drive.ps1: every name here starts with ar / Ar- ($out is its -Out, $DataDir its data folder).

$arTheme = if ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT } else { 'Classic' }
$arTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } else { $arTheme.ToLowerInvariant() }
$arTrials = if ($env:AR_TRIALS) { [int]$env:AR_TRIALS } else { 7 }
$arWidths = @($(if ($env:AR_WIDTHS) { $env:AR_WIDTHS } else { '1920,1100' }) -split ',' | ForEach-Object { [int]$_.Trim() })
$arWithin = if ($env:AR_WITHIN_MS) { [int]$env:AR_WITHIN_MS } else { 6000 }
$arDir = Join-Path $Out 'arrival175'
New-Item -ItemType Directory -Force $arDir | Out-Null
$arRows = New-Object System.Collections.Generic.List[object]

function Ar-Focus { $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); if ($f) { $f | ConvertFrom-Json } else { $null } }
# the app's "[focus] CollectionsPage" lines since a line count of its newest log
function Ar-LogLines([int]$From) {
    $file = Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $file) { return @() }
    @(Get-Content -LiteralPath $file.FullName -Encoding utf8 | Select-Object -Skip $From | Where-Object { $_ -match '\[focus\] CollectionsPage' } |
        ForEach-Object { ($_ -replace '^\S+ \S+ \S+ \[\w+\] ', '').Trim() })
}
function Ar-LogCount {
    $file = Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($file) { @(Get-Content -LiteralPath $file.FullName -Encoding utf8).Count } else { 0 }
}

foreach ($arW in $arWidths) {
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $arW, $(if ($arW -ge 1920) { 1080 } else { 900 }))
    Start-Sleep -Milliseconds 1500
    for ($arI = 1; $arI -le $arTrials; $arI++) {
        $arName = "arrival175 ($arTag): ${arW} #$arI"
        try {
            Open-Nav 'Libraries'
            # (a first visit: Made by Nixie's count comes once the library index is in; at 1100 x 900 the group sits below the
            # fold and its tiles are made once it is scrolled to: the page goes to its foot while the tile is looked for)
            $arLook = 0
            $arTile = Wait-Until {
                $t = Find-Ui -Name 'Made by Nixie, ' -Contains -Type 'ListItem' -OffscreenToo
                if (-not $t -and ((++$script:arLook) % 10) -eq 0) { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 100) }
                $t
            } -Seconds 60 -PollMs 300
            if (-not $arTile) { throw "no 'Made by Nixie, N' tile on the Libraries page" }
            $arFrom = Ar-LogCount
            $arT0 = Get-Epoch
            [void](Invoke-Element $arTile)
            $arMs = $null; $arF = $null
            $arDeadline = $arT0 + ($arWithin / 1000.0) + 4   # the page itself first (its title), then the tile within AR_WITHIN_MS
            [void](Wait-Ui -Id 'TitleText' -Name 'Collections' -OffscreenToo -Seconds 20)
            $arTitleMs = [int](((Get-Epoch) - $arT0) * 1000)
            while ((Get-Epoch) -lt $arDeadline) {
                $arF = Ar-Focus
                if ($arF -and $arF.name -like '*made by Nixie*') { $arMs = [int](((Get-Epoch) - $arT0) * 1000); break }
                Start-Sleep -Milliseconds 100
            }
            Start-Sleep -Milliseconds 400
            $arLines = @(Ar-LogLines $arFrom)
            $arOk = [bool]($arMs -ne $null)
            $arDetail = "{0}; the page's title in {1} ms; the keyboard on {2} '{3}'; log: {4}" -f $(if ($arOk) { "a Nixie tile in $arMs ms" } else { "no Nixie tile within $arWithin ms" }),
                $arTitleMs, $arF.type, $arF.name, $(if ($arLines.Count) { $arLines -join ' | ' } else { 'none' })
            if (-not $arOk) { Shot (Safe-Name "arrival175-$arTag-$arW-$arI") }
        } catch {
            $arOk = $false; $arMs = $null; $arF = $null; $arLines = @(); $arDetail = "the trial failed: $($_.Exception.Message)"
        }
        $arRows.Add([pscustomobject][ordered]@{ theme = $arTheme; width = $arW; trial = $arI; ms = $arMs; focus = $(if ($arF) { $arF.name }); ok = $arOk; log = $arLines; detail = $arDetail })
        Check $arName $arOk $arDetail
        [IO.File]::WriteAllText((Join-Path $arDir 'arrival175.json'), (ConvertTo-Json -InputObject @($arRows.ToArray()) -Depth 4), [Text.UTF8Encoding]::new($false))
        try { Open-Nav 'Home'; Start-Sleep -Milliseconds 800 } catch { }
    }
}
[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080)
Say ("arrival175 ({0}): {1}/{2} arrivals on a Nixie tile -> {3}" -f $arTag, @($arRows | Where-Object { $_.ok }).Count, $arRows.Count, (Join-Path $arDir 'arrival175.json'))
