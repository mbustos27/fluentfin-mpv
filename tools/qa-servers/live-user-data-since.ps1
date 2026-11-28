<#
.SYNOPSIS
  Opt-in live check (Nixie 1.9, the library index's load): how each local QA server lets an ordinary user's played marks
  and favourites be brought up to date without reading every title.

.DESCRIPTION
  Per QA server (tools\qa-servers, 10.11.11 and 12.4.0), signed in as qa-user (the tools' generated account, read by
  qa-common.ps1 and never printed), a film marked played, another marked favourite and an episode marked played:
  - (info) MinDateLastSavedForUser: whether those titles are among the titles whose user data was saved since just
    before. 27 Nov 2026 on both: no (0 of 7, from a minute or two days back): it doesn't follow user data, so the index
    can't use it;
  - what the index uses instead (LibraryIndex's marks): the titles played, latest first (Filters=IsPlayed,
    SortBy=DatePlayed descending, lean), begin with the episode then the film, each with its time played; the
    favourites (Filters=IsFavorite) hold the film; the show's played episodes count one more.
  The marks are put back as they were. A server that isn't running is started (in one of the machine's off-screen slots,
  as run-offscreen.ps1 -QaServer does) and stopped after; one already running is left running. Prints a table (counts and
  yes/no only: no token, no title); -Out writes it as JSON. -SinceMinutes widens the time asked from (2880: two days).

.EXAMPLE
  .\live-user-data-since.ps1 -Python C:\...\python.exe
  .\live-user-data-since.ps1 -Version 12 -SinceMinutes 2880 -Out <scratch>\userdata.json
#>
[CmdletBinding(PositionalBinding = $false)]
param([ValidateSet('10.11', '12', 'all')][string]$Version = 'all', [string]$Python, [string]$Out, [int]$SinceMinutes = 1)
. (Join-Path $PSScriptRoot 'qa-common.ps1')
. (Join-Path $PSScriptRoot '..\ui-harness\offscreen\prepare.ps1')
$device = 'qa-live-userdata'
$rows = New-Object System.Collections.ArrayList
function Add-Row([string]$Server, [string]$Check, $Value, [string]$Expected) {
    [void]$rows.Add([pscustomobject]@{ Server = $Server; Check = $Check; Value = "$Value"; Expected = $Expected; Pass = ($Expected -eq 'info' -or "$Value" -eq $Expected) })
}
function Get-Items($Version, [string]$Path, [string]$Token) {
    $r = Invoke-Qa $Version $Path -Token $Token -Device $device -TimeoutSec 60
    if ($r.Status -ne 200) { throw "a request answered HTTP $($r.Status)" }
    [pscustomobject]@{ Items = @($r.Json.Items); Ids = @($r.Json.Items | ForEach-Object { $_.Id }); Total = $r.Json.TotalRecordCount }
}

$slots = if ($env:NIXIE_HARNESS_PARALLEL) { [int]$env:NIXIE_HARNESS_PARALLEL } else { 3 }
$lock = Enter-HarnessRunSlot $slots ([TimeSpan]::FromMinutes(30))
if (-not $lock) { throw "no off-screen slot in 30 min" }
try {
    foreach ($v in Resolve-QaVersions $Version) {
        $st = Get-QaState $v
        $started = -not ($st -and $st.Running)
        if ($started) { [void](Start-QaServer $v $Python) }
        $token = $null
        try {
            $user = Connect-Qa $v 'user' $device
            $token = $user.Token
            $uid = $user.UserId
            $views = (Invoke-Qa $v "/UserViews?userId=$uid" -Token $token -Device $device).Json.Items
            $movies = @($views | Where-Object { $_.CollectionType -eq 'movies' -and $_.Name -eq 'Movies' })[0].Id
            $shows = @($views | Where-Object { $_.CollectionType -eq 'tvshows' -and $_.Name -notlike 'QA Search*' })[0].Id
            if (-not $movies -or -not $shows) { throw "no Movies or Shows library for qa-user on $v" }
            $q = "/Items?ParentId=$movies&IncludeItemTypes=Movie&Recursive=true&EnableImages=false&EnableTotalRecordCount=true&Limit=500&userId=$uid"
            $films = @((Get-Items $v $q $token).Items | Where-Object { -not $_.UserData.Played -and -not $_.UserData.IsFavorite })
            $episode = @((Get-Items $v "/Items?ParentId=$shows&IncludeItemTypes=Episode&Recursive=true&EnableImages=false&Limit=200&userId=$uid" $token).Items | Where-Object { -not $_.UserData.Played -and $_.SeriesId })[0]
            if ($films.Count -lt 2 -or -not $episode) { throw "fewer than two films and an episode unmarked for qa-user on $v" }
            $played, $favourite = $films[0].Id, $films[1].Id
            $countQ = "/Items?ParentId=$($episode.SeriesId)&IncludeItemTypes=Episode&Recursive=true&Filters=IsPlayed&EnableImages=false&EnableTotalRecordCount=true&Limit=1&userId=$uid"
            $episodesBefore = (Get-Items $v $countQ $token).Total
            $since = [DateTimeOffset]::UtcNow.AddMinutes(-$SinceMinutes)
            $sinceQ = [Uri]::EscapeDataString($since.ToString('o'))

            $marks = @(
                (Invoke-Qa $v "/UserPlayedItems/$($played)?userId=$uid" 'POST' -Token $token -Device $device).Status
                (Invoke-Qa $v "/UserFavoriteItems/$($favourite)?userId=$uid" 'POST' -Token $token -Device $device).Status
            )
            Start-Sleep -Milliseconds 1100 # the episode played after the film, by its time
            $marks += (Invoke-Qa $v "/UserPlayedItems/$($episode.Id)?userId=$uid" 'POST' -Token $token -Device $device).Status
            try {
                Add-Row $v 'marked: film played, film favourite, episode played (HTTP)' ($marks -join ',') '200,200,200'
                $forUser = Get-Items $v "$q&MinDateLastSavedForUser=$sinceQ" $token
                Add-Row $v 'MinDateLastSavedForUser: the marked films among them' ("$($forUser.Ids -contains $played),$($forUser.Ids -contains $favourite)") 'info'
                Add-Row $v 'MinDateLastSavedForUser: count (of every title)' "$($forUser.Total) of $((Get-Items $v $q $token).Total)" 'info'
                # what the index uses: the titles played lately, latest first; the favourites; a show's played episodes
                $recent = Get-Items $v "/Items?IncludeItemTypes=Movie,Episode&Recursive=true&Filters=IsPlayed&SortBy=DatePlayed&SortOrder=Descending&EnableImages=false&EnableTotalRecordCount=false&Limit=5&userId=$uid" $token
                Add-Row $v 'played latest first: the episode, then the film' ("$($recent.Ids[0] -eq $episode.Id),$($recent.Ids[1] -eq $played)") 'True,True'
                $firstPlayed = $recent.Items[0].UserData.LastPlayedDate
                Add-Row $v 'played latest first: the first has its time played, after the time asked from' ([bool]($firstPlayed -and [DateTimeOffset]::Parse($firstPlayed) -ge $since)) 'True'
                $favs = Get-Items $v "/Items?IncludeItemTypes=Movie,Series&Recursive=true&Filters=IsFavorite&EnableImages=false&EnableTotalRecordCount=true&Limit=100&userId=$uid" $token
                Add-Row $v 'favourites: hold the film marked' ($favs.Ids -contains $favourite) 'True'
                Add-Row $v "the show's played episodes: one more" ((Get-Items $v $countQ $token).Total - $episodesBefore) '1'
            }
            finally {
                foreach ($id in $played, $episode.Id) { [void](Invoke-Qa $v "/UserPlayedItems/$($id)?userId=$uid" 'DELETE' -Token $token -Device $device) }
                [void](Invoke-Qa $v "/UserFavoriteItems/$($favourite)?userId=$uid" 'DELETE' -Token $token -Device $device)
            }
        }
        finally {
            if ($token) { try { [void](Invoke-Qa $v '/Sessions/Logout' 'POST' -Token $token -Device $device) } catch {} }
            if ($started) { Stop-QaServer $v }
        }
    }
}
finally {
    Exit-HarnessRunSlot $lock
}
$rows | Format-Table -AutoSize | Out-String -Width 200
if ($Out) { $rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $Out -Encoding utf8 }
if (@($rows | Where-Object { -not $_.Pass }).Count) { exit 1 }
