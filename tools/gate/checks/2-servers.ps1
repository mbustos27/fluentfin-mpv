<#
  The basics gate, class 2 (servers; Nixie 1.7.5, g2). A -Then script of run-offscreen.ps1 (manifest\g2.psd1, id
  "servers"), after the driver signed in to the run's fake server A ("Harness Jellyfin (fake)"). A second fake server B
  ("Second Harness (fake)", checks\second-server.py) runs beside it. What the user sees, in order:
    1. add B: Sign out, B's address, Quick Connect on B: Home, the Account menu naming B; both saved;
    2. switch: Sign out, both on the list; A's row by a click: A's sign-in page, Home on A; B's row by Enter and by
       gamepad A: B's sign-in page each time (then Change server);
    3. remove B through its More options: a click, Enter, Space, gamepad A, and the row's menu key each open a menu with Remove B
       and nothing else, the server page staying (no sign-in page, no server asked); Escape closes it with the keyboard
       back; a live resize with it open leaves the page; Remove asks; Escape and Cancel keep B; Remove takes B's row and
       B out of the saved file, A stays, the keyboard on A's row;
    4. remove the server in use: Settings > Remove server A: the question says Nixie signs out first; Remove: the server
       page, A gone from the list and the file;
    5. re-add A: its address, sign in: Home on A, saved once;
    6. close and start again on the same data: signed in to A at once (the Quick Connect sign-in kept), never an empty
       server page.
  The flow runs at GATE_SIZES' first size, the More options part at its last (a live resize in it). 1.7.3 fails 3 (Remove after
  the server page had been left kept the row: 174-signin) and 4 (stayed signed in to the removed server: 174-sessions
  BS-m2). Every name here starts with g2/G2 (dot-sourced by drive.ps1).
#>
. (Join-Path $PSScriptRoot 'g2-common.ps1')
$g2Id = 'servers'
$g2A = 'Harness Jellyfin (fake)'
$g2B = 'Second Harness (fake)'
$g2Sizes = @(G2-Sizes)
$g2Theme = G2-Theme
Say "servers: theme $g2Theme, sizes $($g2Sizes -join ', ')"
$g2Size = G2-Size $g2Sizes[0]
$g2Second = Start-G2SecondServer
try {
    # --- 1. add B -------------------------------------------------------------------------------------------------------
    G2-SignOut
    Check "${g2Id}: Sign out shows the server page with A saved" ([bool](Wait-Until { G2-ServerMore $g2A } -Seconds 10)) "saved: $((G2-SavedNames) -join ', ')"
    $g2At = Get-Epoch
    Submit-Address $g2Second.Url
    $g2Page = Wait-Until { G2-SignInPageServer } -Seconds 20
    Check "${g2Id} [add]: B's address opens B's sign-in page" ($g2Page -eq $g2B) "sign-in page for '$g2Page'"
    G2-SignInQuick $g2Second.Url
    $g2As = G2-SignedInAs
    $g2Saved = @(G2-SavedNames)
    Check "${g2Id} [add]: Home, signed in to B (the Account menu names it); A and B saved" ($g2As.Server -eq $g2B -and $g2Saved -contains $g2A -and $g2Saved -contains $g2B -and $g2Saved.Count -eq 2) "account: $($g2As.User) on $($g2As.Server); saved: $($g2Saved -join ', ')"
    Shot "servers-$g2Theme-added"

    # --- 2. switch ------------------------------------------------------------------------------------------------------
    G2-SignOut
    $g2Two = (G2-ServerMore $g2A) -and (G2-ServerMore $g2B)
    Check "${g2Id} [switch]: the server page lists A and B" ([bool]$g2Two)
    # a click on the row: its own Invoke (the pointer hook clicks buttons, not an ItemsView's rows)
    $g2Row = G2-ServerRow $g2A
    if ($g2Row) { [void](Invoke-Element $g2Row) }
    $g2Page = Wait-Until { G2-SignInPageServer } -Seconds 20
    Check "${g2Id} [switch, A's row invoked (a click)]: A's sign-in page" ($g2Page -eq $g2A) "sign-in page for '$g2Page'"
    if ($g2Page -eq $g2A) {
        G2-SignInQuick $script:serverUrl
        $g2As = G2-SignedInAs
        Check "${g2Id} [switch]: Home, signed in to A" ($g2As.Server -eq $g2A) "account: $($g2As.User) on $($g2As.Server)"
        G2-SignOut
    }
    elseif ($g2Page) { G2-ChangeServer }
    foreach ($g2Press in @(@('Enter', $g2Vk.Enter), @('gamepad A', $g2Vk.GamepadA))) {
        $g2Row = Wait-Until { G2-ServerRow $g2B } -Seconds 10
        if (-not $g2Row) { Check "${g2Id} [switch, $($g2Press[0]) on B's row]: B's row" $false 'no row'; continue }
        [void][OffscreenUi]::SetFocus($g2Row); Start-Sleep -Milliseconds 400
        Key $g2Press[1]
        $g2Page = Wait-Until { G2-SignInPageServer } -Seconds 20
        Check "${g2Id} [switch, $($g2Press[0]) on B's row]: B's sign-in page" ($g2Page -eq $g2B) "sign-in page for '$g2Page'; focus was on $(G2-Focused)"
        if ($g2Page) { G2-ChangeServer }
    }

    # --- 3. remove B through its More options, at the last size --------------------------------------------------------------------
    $g2Size = G2-Size $g2Sizes[-1]
    [void](Wait-Until { G2-ServerMore $g2B } -Seconds 10)
    $g2Allowed = @("Remove $g2B")
    $g2Ways = @(
        @('a click on More options', { G2-Click (G2-ServerMore $g2B) }, $true),
        @('Enter on More options', { Key $g2Vk.Enter }, $false),
        @('Space on More options', { Key $g2Vk.Space }, $false),
        @('gamepad A on More options', { Key $g2Vk.GamepadA }, $false),
        @('the menu key on the row', { Key $g2Vk.Apps }, $false))
    foreach ($g2Way in $g2Ways) {
        $g2More = G2-ServerMore $g2B
        $g2Opener = if ($g2Way[0] -like '*row*') { G2-ServerRow $g2B } else { $g2More }
        if (-not $g2Way[2]) { [void][OffscreenUi]::SetFocus($g2Opener); Start-Sleep -Milliseconds 400 }
        $g2BAt = Get-Epoch
        [void](Test-G2Menu -Id $g2Id -Name "$($g2Way[0]), $g2Size" -Opener $g2Opener -Open $g2Way[1] -Allowed $g2Allowed -Required $g2Allowed)
        $g2Asked = @(G2-RequestsIn $g2Second.Folder $g2BAt | Where-Object { $_.path -match '^/System/Info/Public$' })
        Check "${g2Id} [$($g2Way[0]), $g2Size]: B not asked, no sign-in page" ($g2Asked.Count -eq 0 -and -not (G2-SignInPageServer) -and (G2-OnServerPage)) "B asked $($g2Asked.Count) times; sign-in page: '$(G2-SignInPageServer)'"
        if (G2-SignInPageServer) { G2-ChangeServer }
    }
    # a live resize with the menu open: the page stays (the menu may close with it)
    $g2More = G2-ServerMore $g2B
    G2-Click $g2More
    [void](Wait-Until { G2-ServerRemove $g2B } -Seconds 3)
    $g2BAt = Get-Epoch
    G2-LiveResize
    Start-Sleep -Milliseconds 800
    $g2Asked = @(G2-RequestsIn $g2Second.Folder $g2BAt | Where-Object { $_.path -match '^/System/Info/Public$' })
    Check "${g2Id} [a live resize with B's menu open]: the server page stays, B not asked" ((G2-OnServerPage) -and -not (G2-SignInPageServer) -and $g2Asked.Count -eq 0 -and (G2-ServerMore $g2B)) "menu open after: $(G2-MenuOpen)"
    G2-CloseFlyouts

    # Remove: the question; Escape and Cancel keep B
    function G2-AskRemoveB {
        G2-Click (G2-ServerMore $g2B)
        $item = Wait-Until { G2-ServerRemove $g2B } -Seconds 3 -PollMs 100
        if (-not $item) { return $null }
        [void](Invoke-Element $item)
        Wait-Until { Find-Ui -Id 'PrimaryButton' -Type 'Button' } -Seconds 5 -PollMs 100
    }
    foreach ($g2No in @('Escape', 'Cancel')) {
        $g2Ask = G2-AskRemoveB
        Check "${g2Id} [Remove B, then $g2No]: Remove asks first" ([bool]$g2Ask) $(if ($g2Ask) { "the question's button: '$($g2Ask.Current.Name)'" } else { 'no question' })
        if (-not $g2Ask) { continue }
        if ($g2No -eq 'Escape') { Key $g2Vk.Escape } else { $g2Cancel = Find-Ui -Id 'CloseButton' -Type 'Button'; if ($g2Cancel) { [void](Invoke-Element $g2Cancel) } }
        [void](Wait-Until { -not (Find-Ui -Id 'PrimaryButton' -Type 'Button') } -Seconds 5)
        Start-Sleep -Milliseconds 600
        Check "${g2Id} [Remove B, then $g2No]: B stays (its row and the saved file)" ((G2-ServerMore $g2B) -and (G2-SavedNames) -contains $g2B) "saved: $((G2-SavedNames) -join ', ')"
    }
    $g2Ask = G2-AskRemoveB
    if ($g2Ask) {
        $g2Words = @(Find-Ui -Name 'Nothing changes on the server' -Contains -Type 'Text')
        Check "${g2Id} [Remove B]: the question names B and says nothing changes on the server" ((Find-Ui -Name "Remove $g2B?" -Contains) -and $g2Words.Count -ge 1)
        [void](Invoke-Element $g2Ask)
        $g2Gone = Wait-Until { -not (G2-ServerMore $g2B) } -Seconds 5 -PollMs 100
        Start-Sleep -Milliseconds 800
        $g2Saved = @(G2-SavedNames)
        Check "${g2Id} [Remove B]: B's row and saved server gone, A stays" ($g2Gone -and (G2-ServerMore $g2A) -and $g2Saved -notcontains $g2B -and $g2Saved -contains $g2A) "saved: $($g2Saved -join ', ')"
        $g2RowA = G2-ServerRow $g2A
        Check "${g2Id} [Remove B]: the keyboard on A's row" ((G2-HasFocus $g2RowA) -or (G2-HasFocus (G2-ServerMore $g2A))) "focus on $(G2-Focused)"
    }
    else { Check "${g2Id} [Remove B]: Remove asks first" $false 'no question' }
    Shot "servers-$g2Theme-removed-b"

    # --- 4. remove the server in use ---------------------------------------------------------------------------------------
    $g2Size = G2-Size $g2Sizes[0]
    [void](Invoke-Element (G2-ServerRow $g2A))
    if ((Wait-Until { G2-SignInPageServer } -Seconds 20) -eq $g2A) { G2-SignInQuick $script:serverUrl }
    Open-Nav 'Settings'
    [void](Wait-Ui -Name 'Servers and accounts' -Type 'Text' -OffscreenToo -Seconds 15)
    Start-Sleep -Milliseconds 1500
    # the server's card in Servers and accounts, opened as a user opens it (its header), scrolled to as needed
    $g2Remove = $null
    for ($g2P = 0; $g2P -le 100 -and -not $g2Remove; $g2P += 5) {
        [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, $g2P); Start-Sleep -Milliseconds 300
        $g2Exp = @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.Name -eq $g2A -and $_.Cached.ClassName -match 'Expander$' -and $_.Cached.ControlType.ProgrammaticName -eq 'ControlType.Button' } catch { $false } })[0]
        if ($g2Exp) { $g2Pat = $null; if ($g2Exp.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$g2Pat)) { try { $g2Pat.Expand() } catch {} }; Start-Sleep -Milliseconds 800 }
        $g2Remove = Find-Ui -Name "Remove server $g2A" -Type 'Button' -OffscreenToo
    }
    if (-not $g2Remove) { Check "${g2Id} [remove the one in use]: Settings has Remove server for A" $false 'no button' }
    else {
        [void](Invoke-Element $g2Remove)
        $g2Ask = Wait-Until { Find-Ui -Id 'PrimaryButton' -Type 'Button' } -Seconds 5 -PollMs 100
        $g2SaysOut = Find-Ui -Name 'signs you out of it' -Contains -Type 'Text'
        Check "${g2Id} [remove the one in use]: the question says Nixie signs out of it first" ($g2Ask -and $g2SaysOut)
        if ($g2Ask) {
            [void](Invoke-Element $g2Ask)
            $g2Out = Wait-Until { G2-OnServerPage } -Seconds 20
            Start-Sleep -Milliseconds 800
            $g2Saved = @(G2-SavedNames)
            Check "${g2Id} [remove the one in use]: signed out to the server page, A gone from the list and the file" ($g2Out -and -not (G2-ServerMore $g2A) -and $g2Saved -notcontains $g2A) "server page: $([bool]$g2Out); saved: $(if ($g2Saved) { $g2Saved -join ', ' } else { 'none' }); signed in still: $(G2-OnHome)"
            Shot "servers-$g2Theme-removed-in-use"
        }
    }

    # --- 5. re-add A ------------------------------------------------------------------------------------------------------
    if (-not (G2-OnServerPage)) { try { G2-SignOut } catch { Say "servers: $($_.Exception.Message)" } }
    Submit-Address $script:serverUrl
    $g2Page = Wait-Until { G2-SignInPageServer } -Seconds 20
    Check "${g2Id} [re-add A]: its address opens A's sign-in page" ($g2Page -eq $g2A) "sign-in page for '$g2Page'"
    G2-SignInQuick $script:serverUrl -Remember
    $g2As = G2-SignedInAs
    $g2Saved = @(G2-SavedNames)
    Check "${g2Id} [re-add A]: Home on A, saved once" ($g2As.Server -eq $g2A -and @($g2Saved | Where-Object { $_ -eq $g2A }).Count -eq 1) "account: $($g2As.User) on $($g2As.Server); saved: $($g2Saved -join ', ')"

    # --- 6. close and start again: still A ---------------------------------------------------------------------------------
    Step-Close 'servers-restart'
    $script:navPane = $null
    Start-App 'servers_restart_window_ms'
    # Home once the start's own sign-in is done (the server page can show under its "Signing in" panel meanwhile)
    $g2Where = Wait-Until { if (G2-OnHome) { 'home' } } -Seconds 40 -PollMs 200
    if (-not $g2Where) { $g2Where = if (G2-SignInPageServer) { 'signin' } elseif (G2-OnServerPage) { 'server' } else { 'neither' } }
    $g2As = if ($g2Where -eq 'home') { Start-Sleep -Milliseconds 1200; G2-SignedInAs } else { $null }
    Check "${g2Id} [re-added, then a restart]: Home, signed in to A" ($g2Where -eq 'home' -and $g2As.Server -eq $g2A) "page: $g2Where; account: $($g2As.User) on $($g2As.Server)"
    Shot "servers-$g2Theme-restarted"
}
finally { Stop-G2SecondServer $g2Second }
