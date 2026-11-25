<#
  Nixie 1.7.5 (fx3, the review's Blocker): the crash trail never logs what was typed. -Before script of run-offscreen.ps1:
  on the sign-in page, the fake server's user name, then a wrong password typed with posted key presses (letters, a digit
  and the numpad's 7, never the real one) and Enter: the server refuses it (a 401, rethrown at sign-in, so a first-chance
  line with "last in the window:" is written). Then back to the server page for the driver's sign-in. The check reads the
  app's log: no line names a typed key ("key W", "pressed Number7", ...), the wrong password, the user name or the server's
  address, and the trail said "typing" for the password box (so it was really there). Run with:
    run-offscreen.ps1 ... -Before offscreen\before-175-trailkeys.ps1 -Script home -NoA11y
  Dot-sourced by drive.ps1: every name here starts with tk.
#>
. (Join-Path $PSScriptRoot 'states-common.ps1')

$tkWrong = 'WRONG77'   # what the posted keys type: W R O N G 7 and the numpad's 7
$tkBegan = [DateTimeOffset]::Now

Submit-Address $script:serverUrl
$tkQuick = Wait-Until { $q = [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Use Quick Connect'); if (-not $q) { $q = [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Quick Connect') }; $q } -Seconds 20 -PollMs 150
Check 'trail-keys: the sign-in page opened' ([bool]$tkQuick)
if ($tkQuick) {
    Start-Sleep -Milliseconds 600
    $tkEdits = @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.ControlType.ProgrammaticName -eq 'ControlType.Edit' -and -not $_.Cached.IsOffscreen } catch { $false } })
    $tkUser = $tkEdits | Where-Object { $_.Cached.ClassName -ne 'PasswordBox' } | Select-Object -First 1
    $tkPassword = $tkEdits | Where-Object { $_.Cached.ClassName -eq 'PasswordBox' } | Select-Object -First 1
    Check 'trail-keys: the page has the user name and password boxes' ($tkUser -and $tkPassword)
    if ($tkUser -and $tkPassword) {
        Set-ElementValue $tkUser $fixture.username
        [void][OffscreenUi]::SetFocus($tkPassword)
        Start-Sleep -Milliseconds 400
        # the wrong password, key by key (posted key presses reach WinUI's KeyDown, as a keyboard's do)
        foreach ($tkVk in @(0x57, 0x52, 0x4F, 0x4E, 0x47, 0x37, 0x67)) { Key $tkVk; Start-Sleep -Milliseconds 60 }
        Key 0x0D   # Enter: sign in
        Start-Sleep -Milliseconds 2500
        $tkRefused = Wait-Until { Find-Ui -Name 'password' -Contains -Type 'Text' } -Seconds 8 -PollMs 200
        Say "trail-keys: after the wrong password: $(if ($tkRefused) { $tkRefused.Current.Name } else { 'no message found' })"
        foreach ($tkId in @('CloseButton', 'PrimaryButton')) { $b = Find-Ui -Id $tkId -Type 'Button'; if ($b) { [void](Invoke-Element $b); Start-Sleep -Milliseconds 700; break } }
    }

    # what the log says (the app writes each line as it happens)
    $tkLines = [System.Collections.Generic.List[string]]::new()
    foreach ($tkFile in @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue)) {
        foreach ($l in @(Get-Content -LiteralPath $tkFile.FullName -Encoding utf8)) { $tkLines.Add($l) }
    }
    $tkTrails = @($tkLines | Where-Object { $_ -match 'last in the window: ' })
    $tkTyped = @($tkLines | Where-Object { $_ -cmatch 'key [A-Z0-9]|pressed (Number|NumberPad|[A-Z]\b)' })
    $tkHost = ([Uri]$script:serverUrl).Authority
    $tkPrivate = @($tkTrails | Where-Object { $_.Contains($tkWrong) -or $_.Contains($fixture.username) -or $_.Contains($tkHost) })
    Say "trail-keys: $($tkTrails.Count) trail line(s); the newest: $(if ($tkTrails) { ($tkTrails[-1] -replace '.*last in the window: ', '') } else { 'none' })"
    Check 'trail-keys: a trail line was written after the wrong password (the check is not empty)' ($tkTrails.Count -ge 1) "$($tkTrails.Count)"
    Check 'trail-keys: the trail saw the password box only as "typing"' ([bool]($tkTrails | Where-Object { $_ -match 'last in the window: .*\btyping\b' })) ''
    Check 'trail-keys: no log line names a typed key' ($tkTyped.Count -eq 0) $(if ($tkTyped) { ($tkTyped | Select-Object -First 2) -join ' | ' } else { 'none' })
    Check 'trail-keys: no trail holds the password, the user name or the server address' ($tkPrivate.Count -eq 0) "$($tkPrivate.Count)"
    Check 'trail-keys: no log line at all holds the password typed' (-not ($tkLines | Where-Object { $_.Contains($tkWrong) })) ''
}

# back to the server page for the driver's sign-in (as before-states.ps1 leaves it)
$tkChange = Wait-Until { $c = Find-Ui -Name 'Change server'; if (-not $c) { $c = [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Switch server') }; $c } -Seconds 10
if ($tkChange) { [void](Invoke-Element $tkChange); [void](Wait-Ui -Id 'TxtUrl' -Type 'Edit' -Seconds 20); Start-Sleep -Milliseconds 600 }
