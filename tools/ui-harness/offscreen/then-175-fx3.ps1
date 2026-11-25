# Nixie 1.7.5, stream fx3: what the user sees of its UI fixes, in the current NIXIE_LAYOUT theme. Run with:
#   $env:FAKESERVER_COLLECTIONS='1'; $env:FAKESERVER_COLLIDE='1'; $env:NIXIE_LAYOUT='<Classic|DriveIn|Cinematic|Editorial>'
#   [$env:FX3_PARTS='shelf,board,trail']
#   run-offscreen.ps1 ... -Script home -NoA11y -Then offscreen\then-175-fx3.ps1
# Parts:
#   shelf: the keyboard on a Collections page tile stays on it while its row leaves the view by the wheel and comes back,
#          and through a resize to 1100 and back; the arrows then go on along the row; UI Automation's SetFocus (Narrator's)
#          to the page's first stop moves the keyboard there. A UI Automation scroll of the page (Narrator's scroll) moves
#          the keyboard to the page's first stop: said, not checked (KNOWN-ISSUES, the 1.7.5 review's call);
#   board: Drive-in's item board at 1100 x 900 and 1920 x 1080: its top edge (the row of lights 12 px into it) below the
#          48 px top bar (a shot of each, fx3-board-<w>.png; the board's top read through UI Automation's kicker);
#   update: (with -Setting CheckForUpdates=true) Settings' Check now after the release notes, 20 times (FX3_UPDATE_TIMES): open
#          the notes, Esc, resize to 1100 x 900 and back as then-update.ps1's shots do, then Check now: a check is sent each time.
#          Says how often Check now was left out of view, which then-update.ps1's old click needed (1.6's "no check for about
#          two minutes" after the notes: that click never happened);
#   trail: the log names the crash trail's listener, and a first-chance line says the last things done in the window.
# Checks are named '<id>: <what the user sees>' (tools\gate\README.md): shelf-keyboard, drivein-board, update-check-now,
# crash-trail. Dot-sourced by drive.ps1: every name here starts with fx.

$fxParts = @($(if ($env:FX3_PARTS) { $env:FX3_PARTS } else { 'shelf,board,trail' }) -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$fxTag = if ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT.ToLowerInvariant() } else { 'classic' }
$fxBegan = [DateTimeOffset]::Now

if (-not ('FxWin' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class FxWin {
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
  // the client area's top on the screen (the window's rectangle has its frame)
  public static int ClientTop(IntPtr h) { var p = new POINT(); ClientToScreen(h, ref p); return p.Y; }
}
"@
}

function Fx-Focus { $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); if ($f) { $f | ConvertFrom-Json } else { $null } }
function Fx-Say($F) { if ($F) { "$($F.type) '$($F.name)' at $($F.rect -join ',')" } else { 'nothing' } }
function Fx-Size([int]$W, [int]$H) { [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $W, $H); Start-Sleep -Milliseconds 1500 }

# ---- shelf: the keyboard on a tile while its row leaves the view --------------------------------------------------------
if ($fxParts -contains 'shelf') {
    Fx-Size 1920 1080
    Open-Nav 'Libraries'
    $fxMade = Wait-Until { Find-Ui -Name 'Made by Nixie, ' -Contains -Type 'ListItem' } -Seconds 30 -PollMs 300
    Check "shelf-keyboard: ($fxTag) the Libraries page has Made by Nixie" ([bool]$fxMade)
    if ($fxMade) {
        [void](Invoke-Element $fxMade)
        $fxTitle = Wait-Ui -Id 'TitleText' -Name 'Collections' -OffscreenToo -Seconds 20
        Check "shelf-keyboard: ($fxTag) the Collections page opened" ([bool]$fxTitle)
        [void](Wait-Until { Find-Ui -Name 'Picks' -Contains } -Seconds 20 -PollMs 200)
        Start-Sleep -Milliseconds 1500
        # the keyboard along the row (Right twice): a tile with the keyboard's focus, not the arrival's
        Key 0x27; Start-Sleep -Milliseconds 300; Key 0x27; Start-Sleep -Milliseconds 500
        $fx0 = Fx-Focus
        Say "fx3 shelf: the keyboard on $(Fx-Say $fx0)"
        Check "shelf-keyboard: ($fxTag) the keyboard is on a Nixie tile" ([bool]($fx0 -and $fx0.name -like '*made by Nixie*')) (Fx-Say $fx0)
        if ($fx0) {
            # the keyboard back on the tile when it left (UI Automation's SetFocus brings the tile into view: no page scroll)
            function Fx-BackOnTile {
                $now = Fx-Focus
                if ($now -and $now.name -eq $fx0.name) { return $true }
                $tile = Find-Ui -Name $fx0.name -OffscreenToo
                if ($tile) { [void][OffscreenUi]::SetFocus($tile); Start-Sleep -Milliseconds 500 }
                $now = Fx-Focus
                $back = [bool]($now -and $now.name -eq $fx0.name)
                if (-not $back) { Say "fx3 shelf: couldn't put the keyboard back on the tile ($(Fx-Say $now))" }
                $back
            }
            $fxSteps = @(
                @{ What = 'the wheel up 12 notches'; Do = {
                        $w = [OffscreenUi]::RectOf([OffscreenUi]::Root($script:hwnd, $script:ui.Id))
                        for ($i = 0; $i -lt 12; $i++) { [OffscreenUi]::Wheel($script:hwnd, $script:ui.Id, [int]($w.X + $w.Width / 2), [int]($w.Y + $w.Height * 0.6), 120, $false, $false) } } },
                @{ What = 'the wheel down 12 notches'; Do = {
                        $w = [OffscreenUi]::RectOf([OffscreenUi]::Root($script:hwnd, $script:ui.Id))
                        for ($i = 0; $i -lt 12; $i++) { [OffscreenUi]::Wheel($script:hwnd, $script:ui.Id, [int]($w.X + $w.Width / 2), [int]($w.Y + $w.Height * 0.6), -120, $false, $false) } } },
                @{ What = 'a resize to 1100 x 900 and back'; Do = { Fx-Size 1100 900; Fx-Size 1920 1080 } }
            )
            foreach ($s in $fxSteps) {
                [void](Fx-BackOnTile)
                & $s.Do
                Start-Sleep -Milliseconds 900
                $fxNow = Fx-Focus
                Check "shelf-keyboard: ($fxTag) the keyboard stays on its tile after $($s.What)" ([bool]($fxNow -and $fxNow.name -eq $fx0.name -and $fxNow.type -eq $fx0.type)) "was $(Fx-Say $fx0); now $(Fx-Say $fxNow)"
            }
            Shot "fx3-shelf-$fxTag"
            # and the keyboard goes on from there along the same row: Left (Right when the tile is the row's first)
            if (Fx-BackOnTile) {
                Key 0x25; Start-Sleep -Milliseconds 500
                $fxNext = Fx-Focus
                if ($fxNext -and $fxNext.name -eq $fx0.name) { Key 0x27; Start-Sleep -Milliseconds 500; $fxNext = Fx-Focus }
                Check "shelf-keyboard: ($fxTag) the arrows then move along the row from that tile" ([bool]($fxNext -and $fxNext.name -ne $fx0.name -and $fxNext.name -like '*made by Nixie*')) (Fx-Say $fxNext)
            }
            # Narrator's case (the 1.7.5 review): UI Automation's SetFocus to the page's very first stop from a card goes as asked
            if (Fx-BackOnTile) {
                Start-Sleep -Milliseconds 1500
                $fxFirst = Find-Ui -Name "See all The server's collections" -OffscreenToo
                if ($fxFirst) {
                    [void][OffscreenUi]::SetFocus($fxFirst)
                    Start-Sleep -Milliseconds 700
                    $fxThere = Fx-Focus
                    Check "shelf-keyboard: ($fxTag) UI Automation's SetFocus to the page's first stop moves the keyboard there" ([bool]($fxThere -and $fxThere.name -eq "See all The server's collections")) (Fx-Say $fxThere)
                }
                else { Say "shelf-keyboard: ($fxTag) no 'See all The server's collections' to focus (the theme names the first stop otherwise)" }
            }
            # KNOWN-ISSUES (1.7.5): a UI Automation scroll of the page (Narrator's scroll, not the wheel) moves the keyboard to the
            # page's first stop; said, not checked
            if (Fx-BackOnTile) {
                [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0)
                Start-Sleep -Milliseconds 900
                Say "shelf-keyboard: ($fxTag) after a UI Automation scroll to the top the keyboard is on $(Fx-Say (Fx-Focus)) (known: the page's first stop)"
            }
        }
    }
}

# ---- board: Drive-in's item board below the top bar -------------------------------------------------------------------------
if ($fxParts -contains 'board' -and $env:NIXIE_LAYOUT -eq 'DriveIn') {
    Fx-Size 1920 1080
    Open-Library $fixture.movie.library
    $fxCard = Wait-Until { Find-MediaCard $fixture.movie.name } -Seconds 30 -PollMs 200
    if ($fxCard) { [void](Invoke-Element $fxCard) }
    $fxKicker = Wait-Until { Find-Ui -Name 'NOW SHOWING' -Type 'Text' } -Seconds 20 -PollMs 200
    Check "drivein-board: the film's page opened (its board's kicker)" ([bool]$fxKicker)
    foreach ($fxSize in @(@(1100, 900), @(1280, 720), @(1399, 900), @(1920, 1080))) {
        Fx-Size $fxSize[0] $fxSize[1]
        [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0)
        Start-Sleep -Milliseconds 600
        $fxTop = [FxWin]::ClientTop($script:hwnd)
        $fxK = Find-Ui -Name 'NOW SHOWING' -Type 'Text'
        $fxKr = if ($fxK) { [OffscreenUi]::RectOf($fxK) } else { $null }
        # the board's padding above its kicker: 32 compact, 40 wide (ItemBoard.ApplySize); its lights 12 px below its top edge
        $fxPad = if ($fxSize[0] -lt 1400) { 32 } else { 40 }
        $fxScale = [FxWin]::GetDpiForWindow($script:hwnd) / 96.0
        $fxKy = if ($fxKr) { ($fxKr.Y - $fxTop) / $fxScale } else { -1 }
        $fxLights = if ($fxKr) { $fxKy - $fxPad + 12 - 3 } else { -1 }
        Check "drivein-board: at $($fxSize[0]) x $($fxSize[1]) the board's top lights are below the 48 px top bar" ($fxLights -ge 48) ("lights from {0:0.0} px (the kicker's top at {1:0.0}; scale {2})" -f $fxLights, $fxKy, $fxScale)
        Shot "fx3-board-$($fxSize[0])"
    }
    Fx-Size 1920 1080
}

# ---- update: Check now after the release notes, 20 times ---------------------------------------------------------------------
if ($fxParts -contains 'update') {
    $fxTimes = if ($env:FX3_UPDATE_TIMES) { [int]$env:FX3_UPDATE_TIMES } else { 20 }
    $fxBase = [version](Get-Item -LiteralPath $Exe).VersionInfo.FileVersion
    $fxV = '{0}.{1}.{2}' -f $fxBase.Major, $fxBase.Minor, ($fxBase.Build + 1)
    [void](Invoke-Server '/__harness/release' 'POST' @{ version = $fxV; mode = 'ok'; notes = "## Nixie $fxV`n`n- one`n- two`n"; mismatch = $false; digest = 'ok' })
    # a button found in view or out of it, brought into view, then pressed; whether it was in view first
    # FX3_UPDATE_OLDCLICK=1: then-update.ps1's click before 1.7.5 (only a button in view, waited for 10 s): the revert check
    function Fx-Press([string]$Name) {
        $inView = [bool](Find-Ui -Name $Name -Type 'Button')
        $b = if ($env:FX3_UPDATE_OLDCLICK -eq '1' -and $Name -eq 'Check now') { Wait-Ui -Name $Name -Seconds 10 }
        else { Wait-Until { $x = Find-Ui -Name $Name -Type 'Button'; if (-not $x) { $x = Find-Ui -Name $Name -OffscreenToo }; $x } -Seconds 10 -PollMs 150 }
        if ($b) {
            $p = $null
            try { if ($b.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$p)) { $p.ScrollIntoView(); Start-Sleep -Milliseconds 300 } } catch {}
            [void](Invoke-Element $b)
        }
        @{ Found = [bool]$b; InView = $inView }
    }
    Fx-Size 1920 1080
    Open-Nav 'Settings'
    $fxCheck = Wait-Ui -Name 'Check now' -Type 'Button' -OffscreenToo -Seconds 15
    Check "update-check-now: Settings has Check now" ([bool]$fxCheck)
    $fxLast = 0.0
    if ($fxCheck) {
        $fxLast = Get-Epoch
        [void](Fx-Press 'Check now')
        $fxSaid = Wait-Until { Find-Ui -Name "Nixie $fxV is available" -Contains -Type 'Text' -OffscreenToo } -Seconds 20 -PollMs 300
        Check "update-check-now: Check now finds Nixie $fxV" ([bool]$fxSaid)
        $fxAsked = @(Get-Requests $fxLast | Where-Object { $_.path -eq '/__harness/release/latest' }) | Select-Object -Last 1
        if ($fxAsked) { $fxLast = [double]$fxAsked.t }
        $fxSent = 0; $fxInView = 0; $fxNotes = 0
        for ($i = 1; $i -le $fxTimes; $i++) {
            $n = Fx-Press 'Release notes'
            if ($n.Found -and (Wait-Until { Find-Ui -Name "What's new in Nixie $fxV" } -Seconds 8 -PollMs 150)) {
                $fxNotes++
                Key 0x09; Start-Sleep -Milliseconds 200    # the keyboard off Close, as then-update's notes step leaves it
                Key 0x1B; Start-Sleep -Milliseconds 800
            }
            Fx-Size 1100 900; Fx-Size 1920 1080
            $wait = 16 - ((Get-Epoch) - $fxLast)
            if ($wait -gt 0) { Start-Sleep -Milliseconds ([int]($wait * 1000)) }
            $fxLast = Get-Epoch
            $c = Fx-Press 'Check now'
            if ($c.InView) { $fxInView++ }
            $asked = Wait-Until { @(Get-Requests $fxLast | Where-Object { $_.path -eq '/__harness/release/latest' }) | Select-Object -First 1 } -Seconds 10 -PollMs 200
            # the next round counts its 16 s from this request, not from the press: Check now says an answer under 15 s old
            # again without asking (UpdateCoordinator.CheckNowReuse, from when the app began the check), and under load the
            # press reached the app a second late (the first run's round 17: no request, by that rule)
            if ($asked) { $fxSent++; $fxLast = [double]$asked.t } else { Say "fx3 update: round $i, Check now (found $($c.Found), in view $($c.InView)) sent no check"; Shot "fx3-update-nocheck-$i" }
        }
        Say "fx3 update: Check now was in view when pressed $fxInView of $fxTimes times; the notes opened $fxNotes of $fxTimes"
        Check "update-check-now: the release notes opened and closed each time" ($fxNotes -eq $fxTimes) "$fxNotes of $fxTimes"
        Check "update-check-now: after the release notes, Check now sent a check each time" ($fxSent -eq $fxTimes) "$fxSent of $fxTimes"
    }
}

# ---- trail: the crash trail's lines -------------------------------------------------------------------------------------------
if ($fxParts -contains 'trail') {
    $fxLog = @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | ForEach-Object { Get-Content -LiteralPath $_.FullName -Encoding utf8 })
    $fxListen = @($fxLog | Where-Object { $_ -match '\[crash-trail\] (listening for unhandled errors|UnhandledErrorDetected isn.t available)' }) | Select-Object -First 1
    Check "crash-trail: the log says whether the crash trail listens for unhandled errors" ([bool]$fxListen) "$fxListen"
    $fxFirst = @($fxLog | Where-Object { $_ -match 'last in the window: ' }) | Select-Object -First 1
    Say "fx3 trail: $(if ($fxFirst) { $fxFirst } else { 'no line with the trail (no first-chance exception this run)' })"
}
