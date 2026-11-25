<#
.SYNOPSIS
  Opt-in live check (Nixie 1.7.5, stream fx2): the two server calls 1.7.5 adds, against each running QA server.

.DESCRIPTION
  Per running QA server, as the tools' generated accounts (read by qa-common.ps1, never printed):
  - Save as playlist's owner check (JellyfinClient.IsOwnPlaylist, GET /Playlists/{id}/Users/{userId}):
    - qa-user's own "QA Playlist", asked by qa-user: 200 (CanEdit);
    - a public playlist of qa-admin's, made for the check and deleted again, asked by qa-user: 404 (not the user's);
  - the place after a refused Stopped (BH-151-m8, JellyfinClient.SetPlace, POST /UserItems/{id}/UserData with Played,
    PlaybackPositionTicks and LastPlayedDate), on a film of qa-user's: the answer and the item read again show that place;
    the film's user data is put back as it was afterwards.
  Starts and stops nothing; the sessions are signed out again.

.EXAMPLE
  .\live-175-owner-and-place.ps1 -Version 12
#>
[CmdletBinding(PositionalBinding = $false)]
param([ValidateSet('10.11', '12', 'all')][string]$Version = 'all')
. (Join-Path $PSScriptRoot 'qa-common.ps1')
$device = 'qa-live-175'
$rows = New-Object System.Collections.ArrayList
function Add-Row([string]$Server, [string]$Check, [string]$Value, [string]$Result) {
    [void]$rows.Add([pscustomobject]@{ Server = $Server; Check = $Check; Value = $Value; Result = $Result })
}
foreach ($v in (Resolve-QaVersions $Version)) {
    $st = Get-QaState $v
    if (-not ($st -and $st.Running)) { Add-Row $v 'server' 'not running' 'SKIP'; continue }
    $user = $null; $admin = $null; $theirs = $null; $film = $null; $before = $null
    try {
        $name = "$v ($((Invoke-Qa $v '/System/Info/Public').Json.Version))"
        $user = Connect-Qa $v 'user' $device
        $admin = Connect-Qa $v 'admin' $device

        # --- the owner check
        $own = @((Invoke-Qa $v "/Items?userId=$($user.UserId)&includeItemTypes=Playlist&recursive=true" -Token $user.Token -Device $device).Json.Items |
            Where-Object { $_.Name -eq 'QA Playlist' })[0]
        $r = Invoke-Qa $v "/Playlists/$($own.Id)/Users/$($user.UserId)" -Token $user.Token -Device $device
        Add-Row $name "the user's own playlist: owner check" "HTTP $($r.Status), CanEdit $($r.Json.CanEdit)" $(if ($r.Status -eq 200) { 'PASS' } else { 'FAIL' })
        $views = @((Invoke-Qa $v "/UserViews?userId=$($admin.UserId)" -Token $admin.Token -Device $device).Json.Items | Where-Object { $_.CollectionType -eq 'movies' -and $_.Name -notlike 'QA Search*' })
        $ids = @((Invoke-Qa $v "/Items?userId=$($admin.UserId)&parentId=$($views[0].Id)&recursive=true&includeItemTypes=Movie&sortBy=SortName&limit=2" -Token $admin.Token -Device $device).Json.Items | Select-Object -ExpandProperty Id)
        $theirs = (Invoke-Qa $v '/Playlists' 'POST' @{ Name = 'Nixie live check 175'; Ids = $ids; UserId = $admin.UserId; MediaType = 'Video'; IsPublic = $true } -Token $admin.Token -Device $device).Json.Id
        $seen = @((Invoke-Qa $v "/Items?userId=$($user.UserId)&includeItemTypes=Playlist&recursive=true" -Token $user.Token -Device $device).Json.Items | Where-Object { $_.Id -eq $theirs }).Count
        $r = Invoke-Qa $v "/Playlists/$theirs/Users/$($user.UserId)" -Token $user.Token -Device $device
        Add-Row $name "another user's public playlist (listed: $seen): owner check" "HTTP $($r.Status)" $(if ($seen -eq 1 -and $r.Status -in 403, 404) { 'PASS' } else { 'FAIL' })
        # the review: shared with qa-user, to edit and to read; the owner's own playlist never lists the owner among its shares
        $mine = (Invoke-Qa $v "/Playlists/$($own.Id)" -Token $user.Token -Device $device).Json
        $ownerListed = @($mine.Shares | Where-Object { $_.UserId -replace '-', '' -eq ($user.UserId -replace '-', '') }).Count
        Add-Row $name "the user's own playlist: listed among its shares" "$ownerListed" $(if ($ownerListed -eq 0) { 'PASS' } else { 'FAIL' })
        foreach ($edit in $true, $false) {
            $s = Invoke-Qa $v "/Playlists/$theirs/Users/$($user.UserId)" 'POST' @{ CanEdit = $edit } -Token $admin.Token -Device $device
            $p = Invoke-Qa $v "/Playlists/$theirs/Users/$($user.UserId)" -Token $user.Token -Device $device
            $dto = (Invoke-Qa $v "/Playlists/$theirs" -Token $user.Token -Device $device).Json
            $listed = @($dto.Shares | Where-Object { $_.UserId -replace '-', '' -eq ($user.UserId -replace '-', '') }).Count
            $notOwn = $p.Status -in 403, 404 -or $p.Json.CanEdit -ne $true -or $listed -gt 0
            Add-Row $name "shared to $(if ($edit) { 'edit' } else { 'read' }) (HTTP $($s.Status)): permission / listed" "HTTP $($p.Status) CanEdit $($p.Json.CanEdit) / $listed" $(if ($s.Status -in 200, 204 -and $notOwn) { 'PASS' } else { 'FAIL' })
        }

        # --- the place after a refused Stopped
        $uviews = @((Invoke-Qa $v "/UserViews?userId=$($user.UserId)" -Token $user.Token -Device $device).Json.Items | Where-Object { $_.CollectionType -eq 'movies' -and $_.Name -notlike 'QA Search*' })
        $film = @((Invoke-Qa $v "/Items?userId=$($user.UserId)&parentId=$($uviews[0].Id)&recursive=true&includeItemTypes=Movie&sortBy=SortName&limit=1&fields=UserData" -Token $user.Token -Device $device).Json.Items)[0]
        $before = (Invoke-Qa $v "/Items/$($film.Id)?userId=$($user.UserId)" -Token $user.Token -Device $device).Json.UserData
        $at = [DateTimeOffset]::UtcNow.AddMinutes(-3).ToString('o')
        $ticks = [long]30 * 10000000
        $set = Invoke-Qa $v "/UserItems/$($film.Id)/UserData?userId=$($user.UserId)" 'POST' @{ Played = $false; PlaybackPositionTicks = $ticks; LastPlayedDate = $at } -Token $user.Token -Device $device
        $now = (Invoke-Qa $v "/Items/$($film.Id)?userId=$($user.UserId)" -Token $user.Token -Device $device).Json.UserData
        $lastOk = $now.LastPlayedDate -and ([DateTimeOffset]::Parse($now.LastPlayedDate) - [DateTimeOffset]::Parse($at)).Duration().TotalSeconds -lt 2
        Add-Row $name 'SetPlace: answer / position / last played' "HTTP $($set.Status) / $($now.PlaybackPositionTicks) / $(if ($lastOk) { 'as sent' } else { $now.LastPlayedDate })" $(if ($set.Status -eq 200 -and $now.PlaybackPositionTicks -eq $ticks -and $lastOk -and -not $now.Played) { 'PASS' } else { 'FAIL' })
    }
    catch { Add-Row $v 'sign-in or request' $_.Exception.Message 'FAIL' }
    finally {
        if ($theirs) {
            $del = Invoke-Qa $v "/Items/$theirs" 'DELETE' -Token $admin.Token -Device $device
            Add-Row $v "the check's public playlist deleted" "HTTP $($del.Status)" $(if ($del.Status -in 200, 204) { 'PASS' } else { 'FAIL' })
        }
        if ($film -and $before) {
            $back = @{ Played = [bool]$before.Played; PlaybackPositionTicks = [long]$before.PlaybackPositionTicks }
            if ($before.LastPlayedDate) { $back.LastPlayedDate = $before.LastPlayedDate }
            $r = Invoke-Qa $v "/UserItems/$($film.Id)/UserData?userId=$($user.UserId)" 'POST' $back -Token $user.Token -Device $device
            Add-Row $v 'the film put back as it was' "HTTP $($r.Status)" $(if ($r.Status -eq 200) { 'PASS' } else { 'FAIL' })
        }
        foreach ($s in @($user, $admin)) { if ($s) { try { [void](Invoke-Qa $v '/Sessions/Logout' 'POST' -Token $s.Token -Device $device) } catch {} } }
    }
}
$rows | Format-Table -AutoSize | Out-String -Width 220 | Write-Host
if (@($rows | Where-Object { $_.Result -eq 'FAIL' })) { exit 1 }
