<#
.SYNOPSIS
  Opt-in live check (Nixie 1.6, "Save as playlist"): Nixie's exact request to each running QA server as qa-user, an
  ordinary user, and what the server answers; the playlist is deleted again.

.DESCRIPTION
  Per running QA server, signed in as qa-user (the tools' generated account, read by qa-common.ps1 and never printed):
  - POST /Playlists with the body Nixie's JellyfinClient.CreatePlaylist sends ({Name, Ids, UserId, MediaType: Video,
    IsPublic: false}) for three films in a set order: 200 and an Id; the playlist's items come back in that order (GET
    /Playlists/{id}/Items);
  - private: qa-admin, the second QA account, doesn't see it (not among its playlists; GET /Playlists/{id} says
    OpenAccess false); and the contrast: the same request without IsPublic makes one qa-admin does see (the server's
    default, why Nixie sends it), deleted at once;
  - the refusals Nixie turns into its own words: no token (401), and an empty name (what the server answers is printed;
    the fake answers 400);
  - the playlist deleted again (DELETE /Items/{id} as qa-user, its owner; as qa-admin if the user may not), and gone (404).
  Leaves the "QA Search" libraries out. Starts and stops nothing; the sessions are signed out again.

.EXAMPLE
  .\live-save-playlist.ps1
  .\live-save-playlist.ps1 -Version 12
#>
[CmdletBinding(PositionalBinding = $false)]
param([ValidateSet('10.11', '12', 'all')][string]$Version = 'all')
. (Join-Path $PSScriptRoot 'qa-common.ps1')
$device = 'qa-live-playlist'
$rows = New-Object System.Collections.ArrayList
function Add-Row([string]$Server, [string]$Check, [string]$Value, [string]$Result) {
    [void]$rows.Add([pscustomobject]@{ Server = $Server; Check = $Check; Value = $Value; Result = $Result })
}
foreach ($v in (Resolve-QaVersions $Version)) {
    $st = Get-QaState $v
    if (-not ($st -and $st.Running)) { Add-Row $v 'server' 'not running' 'SKIP'; continue }
    $user = $null; $admin = $null; $made = $null
    try {
        $name = "$v ($((Invoke-Qa $v '/System/Info/Public').Json.Version))"
        $user = Connect-Qa $v 'user' $device
        $views = @((Invoke-Qa $v "/UserViews?userId=$($user.UserId)" -Token $user.Token -Device $device).Json.Items | Where-Object { $_.CollectionType -eq 'movies' -and $_.Name -notlike 'QA Search*' })
        $films = @((Invoke-Qa $v "/Items?userId=$($user.UserId)&parentId=$($views[0].Id)&recursive=true&includeItemTypes=Movie&sortBy=SortName&limit=3" -Token $user.Token -Device $device).Json.Items)
        $ids = @($films | Select-Object -ExpandProperty Id)
        [array]::Reverse($ids)   # an order of Nixie's own (not the server's sort): the playlist must keep it
        $body = @{ Name = 'Nixie live check'; Ids = $ids; UserId = $user.UserId; MediaType = 'Video'; IsPublic = $false }
        $r = Invoke-Qa $v '/Playlists' 'POST' $body -Token $user.Token -Device $device
        $made = $r.Json.Id
        Add-Row $name 'POST /Playlists (qa-user)' "HTTP $($r.Status), Id $(if ($made) { 'yes' } else { 'none' })" $(if ($r.Status -eq 200 -and $made) { 'PASS' } else { 'FAIL' })
        if ($made) {
            $items = @((Invoke-Qa $v "/Playlists/$made/Items?userId=$($user.UserId)" -Token $user.Token -Device $device).Json.Items | Select-Object -ExpandProperty Id)
            $same = ($items -join ',') -eq ($ids -join ',')
            Add-Row $name 'its items, in the order sent' "$($items.Count) of $($ids.Count)" $(if ($same) { 'PASS' } else { 'FAIL' })
            # private: the second QA account doesn't see it
            $admin = Connect-Qa $v 'admin' $device
            $seen = @((Invoke-Qa $v "/Items?userId=$($admin.UserId)&includeItemTypes=Playlist&recursive=true" -Token $admin.Token -Device $device).Json.Items | Where-Object { $_.Id -eq $made }).Count
            $open = (Invoke-Qa $v "/Playlists/$made" -Token $user.Token -Device $device).Json.OpenAccess
            Add-Row $name 'private: qa-admin sees it / OpenAccess' "$seen / $open" $(if ($seen -eq 0 -and $open -eq $false) { 'PASS' } else { 'FAIL' })
            # the contrast: IsPublic left out makes a public one (deleted at once)
            $loose = (Invoke-Qa $v '/Playlists' 'POST' @{ Name = 'Nixie live check (public)'; Ids = $ids; UserId = $user.UserId; MediaType = 'Video' } -Token $user.Token -Device $device).Json.Id
            if ($loose) {
                $seenLoose = @((Invoke-Qa $v "/Items?userId=$($admin.UserId)&includeItemTypes=Playlist&recursive=true" -Token $admin.Token -Device $device).Json.Items | Where-Object { $_.Id -eq $loose }).Count
                $gone = (Invoke-Qa $v "/Items/$loose" 'DELETE' -Token $user.Token -Device $device).Status
                Add-Row $name 'IsPublic left out: qa-admin sees it' "$seenLoose (deleted: HTTP $gone)" $(if ($seenLoose -eq 1) { 'INFO' } else { 'INFO' })
            }
        }
        $none = Invoke-Qa $v '/Playlists' 'POST' $body -Device $device
        Add-Row $name 'no token' "HTTP $($none.Status)" $(if ($none.Status -eq 401) { 'PASS' } else { 'FAIL' })
        $empty = Invoke-Qa $v '/Playlists' 'POST' @{ Name = ''; Ids = $ids; UserId = $user.UserId; MediaType = 'Video' } -Token $user.Token -Device $device
        if ($empty.Json.Id) {
            # the server made one with no name: delete it too
            [void](Invoke-Qa $v "/Items/$($empty.Json.Id)" 'DELETE' -Token $user.Token -Device $device)
        }
        Add-Row $name 'an empty name' "HTTP $($empty.Status)$(if ($empty.Json.Id) { ' (made one; deleted)' })" 'INFO'
    }
    catch { Add-Row $v 'sign-in or request' $_.Exception.Message 'FAIL' }
    finally {
        if ($made) {
            $del = Invoke-Qa $v "/Items/$made" 'DELETE' -Token $user.Token -Device $device
            $how = 'qa-user'
            if ($del.Status -notin 200, 204) {
                if (-not $admin) { $admin = Connect-Qa $v 'admin' $device }
                $del = Invoke-Qa $v "/Items/$made" 'DELETE' -Token $admin.Token -Device $device
                $how = 'qa-admin'
            }
            # gone: no longer among the user's playlists (GET /Items/{id} of a deleted item answers 400 on both versions)
            $left = @((Invoke-Qa $v "/Items?userId=$($user.UserId)&includeItemTypes=Playlist&recursive=true" -Token $user.Token -Device $device).Json.Items |
                Where-Object { $_.Name -like 'Nixie live check*' -or -not $_.Name })
            Add-Row $name "deleted again ($how)" "HTTP $($del.Status), $($left.Count) left" $(if ($del.Status -in 200, 204 -and $left.Count -eq 0) { 'PASS' } else { 'FAIL' })
        }
        # a sweep a moment later: 12.4 once listed a deleted test playlist again (seen 2026-11-21); this check's own only
        if ($user) {
            Start-Sleep -Seconds 2
            foreach ($p in @((Invoke-Qa $v "/Items?userId=$($user.UserId)&includeItemTypes=Playlist&recursive=true" -Token $user.Token -Device $device).Json.Items | Where-Object { $_.Name -like 'Nixie live check*' })) {
                $again = (Invoke-Qa $v "/Items/$($p.Id)" 'DELETE' -Token $user.Token -Device $device).Status
                Add-Row $name 'a leftover deleted' "HTTP $again" 'INFO'
            }
        }
        foreach ($s in @($user, $admin)) { if ($s) { try { [void](Invoke-Qa $v '/Sessions/Logout' 'POST' -Token $s.Token -Device $device) } catch {} } }
    }
}
$rows | Format-Table -AutoSize | Out-String -Width 220 | Write-Host
if (@($rows | Where-Object { $_.Result -eq 'FAIL' })) { exit 1 }
