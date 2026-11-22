<#
.SYNOPSIS
  Opt-in live check (Nixie 1.6, smart collections phase 1): what an ordinary user's library index gets from each running
  QA server, per title, so a kind of Nixie collection ships only where its data is there.

.DESCRIPTION
  Per running QA server (tools\qa-servers, 10.11.11 and 12.4.0), signed in as qa-user (the tools' generated account, read
  by qa-common.ps1 and never printed), read-only:
  - the index query Nixie's LibraryIndex sends (recursive, typed, Fields=ProviderIds,Tags,Genres,Studios,
    ProductionLocations,SortName,DateCreated, ImageTypeLimit=1, Primary only) per movie and show library, and per title:
    TmdbCollection, Tags, Genres, Studios, ProductionLocations, CommunityRating, RunTimeTicks, ProductionYear;
  - the same query with a bogus Fields name: the contrast (the server drops a name it doesn't know and answers 200, so
    only a difference proves the fields were applied);
  - series Studios (networks only on 10.11; networks and companies on 12);
  - the box sets: their Tmdb id against the films' TmdbCollection;
  - whether minCriticRating filters a recursive query (minCriticRating=101 must leave nothing if it is applied);
  - how long Fields=People costs next to the same query without it (also on the QA Search libraries: cost only).
  Libraries whose names start "QA Search" are left out of every coverage figure (NFO-only titles, no TMDb data).
  Prints a table; -Out writes the per-title facts as JSON (names, ids and the facts only; no token, no path).
  Starts and stops nothing; the session is signed out again.

.EXAMPLE
  .\live-server-index.ps1
  .\live-server-index.ps1 -Version 12 -Out <scratch>\index
#>
[CmdletBinding(PositionalBinding = $false)]
param([ValidateSet('10.11', '12', 'all')][string]$Version = 'all', [string]$Out)
. (Join-Path $PSScriptRoot '..\qa-servers\qa-common.ps1')
$device = 'qa-live-index'
$indexFields = 'ProviderIds,Tags,Genres,Studios,ProductionLocations,SortName,DateCreated'
$rows = New-Object System.Collections.ArrayList
function Add-Row([string]$Server, [string]$Check, [string]$Value, [string]$Result) {
    [void]$rows.Add([pscustomobject]@{ Server = $Server; Check = $Check; Value = $Value; Result = $Result })
}
function Get-Timed([string]$V, [string]$Path, [string]$Token) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = Invoke-Qa $V $Path -Token $Token -Device $device -TimeoutSec 120
    [pscustomobject]@{ R = $r; Ms = [int]$sw.Elapsed.TotalMilliseconds }
}
function Get-Index([string]$V, [string]$UserId, [string]$Token, $Library, [string]$Type, [string]$Fields) {
    $q = "/Items?userId=$UserId&parentId=$($Library.Id)&recursive=true&includeItemTypes=$Type&fields=$Fields" +
        '&imageTypeLimit=1&enableImageTypes=Primary&sortBy=SortName,ProductionYear&enableTotalRecordCount=true'
    Get-Timed $V $q $Token
}
$report = [ordered]@{}
foreach ($v in (Resolve-QaVersions $Version)) {
    $st = Get-QaState $v
    if (-not ($st -and $st.Running)) { Add-Row $v 'server' 'not running' 'SKIP'; continue }
    $token = $null
    try {
        $info = (Invoke-Qa $v '/System/Info/Public').Json
        $name = "$v ($($info.Version))"
        $user = Connect-Qa $v 'user' $device
        $token = $user.Token
        $uid = $user.UserId
        $views = @((Invoke-Qa $v "/UserViews?userId=$uid" -Token $token -Device $device).Json.Items)
        $media = @($views | Where-Object { $_.CollectionType -in 'movies', 'tvshows' })
        $covered = @($media | Where-Object { $_.Name -notlike 'QA Search*' })
        $titles = New-Object System.Collections.ArrayList
        foreach ($lib in $covered) {
            $type = if ($lib.CollectionType -eq 'movies') { 'Movie' } else { 'Series' }
            $idx = Get-Index $v $uid $token $lib $type $indexFields
            $bogus = Get-Index $v $uid $token $lib $type 'NixieNoSuchField'
            $items = @($idx.R.Json.Items)
            Add-Row $name "index '$($lib.Name)' ($type)" "$($items.Count) titles, HTTP $($idx.R.Status), $($idx.Ms) ms" $(if ($idx.R.Status -eq 200) { 'PASS' } else { 'FAIL' })
            # the contrast: the bogus name answers 200 without the fields (so a test that only checks success proves nothing)
            $withData = @($items | Where-Object { $_.ProviderIds -or $_.Tags -or $_.Genres }).Count
            $bogusItems = @($bogus.R.Json.Items)
            $bogusData = @($bogusItems | Where-Object { ($_.ProviderIds -and @($_.ProviderIds.PSObject.Properties).Count) -or ($_.Tags -and @($_.Tags).Count) -or ($_.Genres -and @($_.Genres).Count) }).Count
            Add-Row $name "contrast '$($lib.Name)': bogus Fields" "HTTP $($bogus.R.Status), $($bogusItems.Count) titles, $bogusData with ProviderIds/Tags/Genres (asked: $withData)" $(if ($bogus.R.Status -eq 200 -and $bogusData -eq 0 -and $withData -gt 0) { 'PASS' } else { 'FAIL' })
            foreach ($it in $items) {
                $ids = $it.ProviderIds
                [void]$titles.Add([pscustomobject]@{
                        Library = $lib.Name; Type = $it.Type; Name = $it.Name; Year = $it.ProductionYear
                        Tmdb = $ids.Tmdb; TmdbCollection = $ids.TmdbCollection; Imdb = $ids.Imdb
                        Tags = @($it.Tags); Genres = @($it.Genres); Studios = @($it.Studios | ForEach-Object { $_.Name })
                        Countries = @($it.ProductionLocations | Where-Object { $_ }); Rating = $it.CommunityRating; Critic = $it.CriticRating
                        Minutes = $(if ($it.RunTimeTicks) { [Math]::Round($it.RunTimeTicks / 600000000, 1) } else { $null })
                    })
            }
        }
        $movies = @($titles | Where-Object { $_.Type -eq 'Movie' })
        $series = @($titles | Where-Object { $_.Type -eq 'Series' })
        function Coverage($List, [scriptblock]$Has) { $n = @($List | Where-Object $Has).Count; "$n of $(@($List).Count)" }
        Add-Row $name 'films with a TMDb id' (Coverage $movies { $_.Tmdb }) 'INFO'
        Add-Row $name 'films with TmdbCollection' ((Coverage $movies { $_.TmdbCollection }) + ': ' + ((@($movies | Where-Object { $_.TmdbCollection } | ForEach-Object { "$($_.Name)=$($_.TmdbCollection)" })) -join ', ')) 'INFO'
        Add-Row $name 'films with Tags' ((Coverage $movies { @($_.Tags).Count }) + '; e.g. ' + ((@($movies | Where-Object { @($_.Tags).Count } | Select-Object -First 2 | ForEach-Object { "$($_.Name): $((@($_.Tags) | Select-Object -First 6) -join '|')" })) -join '; ')) 'INFO'
        Add-Row $name 'shows with Tags' (Coverage $series { @($_.Tags).Count }) 'INFO'
        Add-Row $name 'films with Genres' (Coverage $movies { @($_.Genres).Count }) 'INFO'
        Add-Row $name 'films with Studios' (Coverage $movies { @($_.Studios).Count }) 'INFO'
        Add-Row $name 'show Studios' (((@($series | ForEach-Object { "$($_.Name): $((@($_.Studios)) -join '|')" })) -join '; ')) 'INFO'
        Add-Row $name 'films with ProductionLocations' ((Coverage $movies { @($_.Countries).Count }) + '; ' + ((@($movies | Where-Object { @($_.Countries).Count } | Select-Object -First 3 | ForEach-Object { "$($_.Name): $((@($_.Countries)) -join '|')" })) -join '; ')) 'INFO'
        Add-Row $name 'shows with ProductionLocations' (Coverage $series { @($_.Countries).Count }) 'INFO'
        Add-Row $name 'films with CommunityRating' (Coverage $movies { $_.Rating }) 'INFO'
        Add-Row $name 'films with CriticRating' (Coverage $movies { $_.Critic }) 'INFO'
        Add-Row $name 'films with RunTimeTicks' (Coverage $movies { $_.Minutes }) 'INFO'
        Add-Row $name 'films with ProductionYear' (Coverage $movies { $_.Year }) 'INFO'
        # unfinished series: a show's own UserData has no LastPlayedDate (and 10.11 leaves PlayedPercentage out), so the
        # index reads the played episodes (SeriesId and LastPlayedDate) in a pass of its own
        $showUd = @($series | ForEach-Object { $_.Name })
        $played = (Invoke-Qa $v "/Items?userId=$uid&recursive=true&includeItemTypes=Episode&filters=IsPlayed&sortBy=DatePlayed&sortOrder=Descending&enableTotalRecordCount=true&enableImageTypes=Primary&imageTypeLimit=0" -Token $token -Device $device).Json
        $withDate = @($played.Items | Where-Object { $_.UserData.LastPlayedDate -and $_.SeriesId }).Count
        $rawShows = @((Invoke-Qa $v "/Items?userId=$uid&recursive=true&includeItemTypes=Series" -Token $token -Device $device).Json.Items | Where-Object { $_.Name -in $showUd })
        $ud = (@($rawShows | ForEach-Object { "$($_.Name): unplayed $($_.UserData.UnplayedItemCount), played% $(if ($null -ne $_.UserData.PlayedPercentage) { [Math]::Round($_.UserData.PlayedPercentage) } else { '(none)' }), last $(if ($_.UserData.LastPlayedDate) { 'yes' } else { '(none)' })" })) -join '; '
        Add-Row $name 'show UserData' $ud 'INFO'
        Add-Row $name 'played episodes (DatePlayed)' "$($played.TotalRecordCount) played; $withDate with SeriesId and LastPlayedDate" 'INFO'
        # box sets: the admin's, with their Tmdb id against the films' TmdbCollection
        $boxes = @((Invoke-Qa $v "/Items?userId=$uid&recursive=true&includeItemTypes=BoxSet&fields=ProviderIds,ChildCount" -Token $token -Device $device).Json.Items)
        Add-Row $name 'box sets' (((@($boxes | ForEach-Object { "$($_.Name) (Tmdb $(if ($_.ProviderIds.Tmdb) { $_.ProviderIds.Tmdb } else { '-' }), $($_.ChildCount) items)" })) -join '; ')) 'INFO'
        # minCriticRating on a recursive query: applied would leave nothing at 101
        $movieLib = @($covered | Where-Object { $_.CollectionType -eq 'movies' })[0]
        if ($movieLib) {
            $all = (Invoke-Qa $v "/Items?userId=$uid&parentId=$($movieLib.Id)&recursive=true&includeItemTypes=Movie&limit=0&enableTotalRecordCount=true" -Token $token -Device $device).Json.TotalRecordCount
            $crit = (Invoke-Qa $v "/Items?userId=$uid&parentId=$($movieLib.Id)&recursive=true&includeItemTypes=Movie&minCriticRating=101&limit=0&enableTotalRecordCount=true" -Token $token -Device $device).Json.TotalRecordCount
            $comm = (Invoke-Qa $v "/Items?userId=$uid&parentId=$($movieLib.Id)&recursive=true&includeItemTypes=Movie&minCommunityRating=10.1&limit=0&enableTotalRecordCount=true" -Token $token -Device $device).Json.TotalRecordCount
            Add-Row $name 'minCriticRating=101 (recursive)' "$crit of $all films left ($(if ($crit -eq $all) { 'ignored' } else { 'applied' })); minCommunityRating=10.1 left $comm" 'INFO'
        }
        # People: the cost, per library (QA Search too, cost only), with and without
        foreach ($lib in $media) {
            $type = if ($lib.CollectionType -eq 'movies') { 'Movie' } else { 'Series' }
            $without = Get-Index $v $uid $token $lib $type $indexFields
            $with = Get-Index $v $uid $token $lib $type "$indexFields,People"
            $people = @($with.R.Json.Items | ForEach-Object { @($_.People) }).Count
            $kinds = (@($with.R.Json.Items | ForEach-Object { @($_.People) } | Group-Object Type | ForEach-Object { "$($_.Name) $($_.Count)" })) -join ', '
            $tag = if ($lib.Name -like 'QA Search*') { ' (cost only)' } else { '' }
            Add-Row $name "People cost '$($lib.Name)'$tag" "$(@($with.R.Json.Items).Count) titles: $($without.Ms) ms without, $($with.Ms) ms with; $people people ($kinds)" 'INFO'
        }
        $report[$v] = [ordered]@{ Version = $info.Version; Titles = $titles; Boxes = @($boxes | ForEach-Object { [ordered]@{ Name = $_.Name; Tmdb = $_.ProviderIds.Tmdb; Items = $_.ChildCount } }) }
    }
    catch { Add-Row $v 'sign-in or read' $_.Exception.Message 'FAIL' }
    finally { if ($token) { try { [void](Invoke-Qa $v '/Sessions/Logout' 'POST' -Token $token -Device $device) } catch {} } }
}
$rows | Format-Table -AutoSize -Wrap | Out-String -Width 260 | Write-Host
if ($Out) {
    New-Item -ItemType Directory -Force $Out | Out-Null
    [IO.File]::WriteAllText((Join-Path $Out 'live-server-index.json'), ($report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}
if (@($rows | Where-Object { $_.Result -eq 'FAIL' })) { exit 1 }
