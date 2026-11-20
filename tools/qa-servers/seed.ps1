<#
.SYNOPSIS
  Rebuilds a QA Jellyfin server's seeded state from nothing: test media, the startup wizard, the two test users,
  Quick Connect, the libraries, a box set and a playlist; then snapshots it for qa.ps1 reset. Idempotent.

.DESCRIPTION
  1. Credentials: %USERPROFILE%\Dev\jellyfin-qa\qa-credentials.json (outside the repo), generated once (qa-admin, an
     administrator; qa-user, an ordinary user) and reused after that. Never printed, never committed.
  2. Media: media.py with the server's bundled jellyfin-ffmpeg, off-screen (hidden_run.py), into <root>\media (shared by
     both servers, read-only to them: no metadata or images are saved into it). Only missing or changed files are made.
  3. Per server: stopped, its data, config and cache wiped, network.xml written (127.0.0.1 only, before it starts),
     started hidden (qa.ps1's way), the startup wizard through the API (en-US, metadata language en, country US, the
     admin), remote access off, Quick Connect on, server name "Nixie QA <version>", qa-user created, five libraries
     (Movies, Shows, Anime, Home Videos, Music Videos; no chapter or trickplay images, nothing written next to the media),
     a full scan (the server's own TMDb lookup: public data, no account), the box set "QA Box Set" (admin) and the
     playlist "QA Playlist" (qa-user's own), then stopped and copied to <version>\seeded (data, config, seed.json).
  The server is left stopped unless -LeaveRunning.

.EXAMPLE
  .\seed.ps1                 # both servers
  .\seed.ps1 -Version 12 -LeaveRunning
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [ValidateSet('10.11', '12', 'all')][string]$Version = 'all',
    [string]$Python,
    [switch]$LeaveRunning,
    [int]$ScanMinutes = 20
)
. (Join-Path $PSScriptRoot 'qa-common.ps1')
$versions = Resolve-QaVersions $Version
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$pyw = Resolve-QaPython $Python
$py = $pyw -replace 'pythonw\.exe$', 'python.exe'
function Say([string]$Text) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $Text) }
function Need($Response, [string]$What, [int[]]$Ok = @(200, 204)) {
    if ($Ok -notcontains $Response.Status) { throw "$What failed: HTTP $($Response.Status) $(if ($Response.Text) { $Response.Text.Substring(0, [Math]::Min(300, $Response.Text.Length)) })" }
    $Response
}

# --- 1. credentials -----------------------------------------------------------------------------------------------------
$creds = Get-QaCredentials -Create
Say "credentials: $script:QaCredentialsFile (qa-admin, qa-user; not shown)"

# --- 2. media -----------------------------------------------------------------------------------------------------------
$media = Join-Path $script:QaRoot 'media'
$ffmpeg = $null
foreach ($v in @($script:QaPorts.Keys)) { $f = Join-Path (Get-QaDir $v) 'server\ffmpeg.exe'; if (Test-Path -LiteralPath $f) { $ffmpeg = $f } }
if (-not $ffmpeg) { throw 'no jellyfin-ffmpeg: install a server first (docs\QA-SERVERS.md)' }
$mediaLog = Join-Path $script:QaRoot 'media-build.log'
& $py -B -I (Join-Path $repo 'tools\backend-bench\hidden_run.py') --log $mediaLog --timeout 1800 --desktop "nixie-qa-media-$PID" -- `
    $py -B -I (Join-Path $PSScriptRoot 'media.py') --ffmpeg $ffmpeg --out $media | Out-Null
if ($LASTEXITCODE -ne 0) { throw "media.py failed (see $mediaLog)" }
Say ((Get-Content -LiteralPath $mediaLog -Encoding utf8 | Where-Object { $_ -like 'media:*' } | Select-Object -Last 1))

$libraries = @(
    @{ Name = 'Movies'; Type = 'movies'; Folder = 'Movies' },
    @{ Name = 'Shows'; Type = 'tvshows'; Folder = 'Shows' },
    @{ Name = 'Anime'; Type = 'tvshows'; Folder = 'Anime' },
    @{ Name = 'Home Videos'; Type = 'homevideos'; Folder = 'Home Videos' },
    @{ Name = 'Music Videos'; Type = 'musicvideos'; Folder = 'Music Videos' }
)

function Wait-QaIdle([string]$V, [string]$Token, [double]$Since, [int]$Minutes) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalMinutes -lt $Minutes) {
        Start-Sleep -Seconds 3
        $tasks = (Invoke-Qa $V '/ScheduledTasks?isHidden=false' -Token $Token).Json
        $scan = @($tasks | Where-Object { $_.Key -eq 'RefreshLibrary' }) | Select-Object -First 1
        $running = @($tasks | Where-Object { $_.State -ne 'Idle' })
        $ended = $false
        if ($scan -and $scan.LastExecutionResult -and $scan.LastExecutionResult.EndTimeUtc) {
            $ended = ([DateTimeOffset]::Parse($scan.LastExecutionResult.EndTimeUtc).ToUnixTimeSeconds() -ge $Since)
        }
        if ($ended -and -not $running) { return $true }
    }
    $false
}

foreach ($v in $versions) {
    $dir = Get-QaDir $v
    Say "${v}: rebuilding from nothing"
    [void](Stop-QaServer $v)
    foreach ($d in 'data', 'config', 'cache', 'seeded') {
        $p = Join-Path $dir $d
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
    }
    New-Item -ItemType Directory -Force (Join-Path $dir 'data'), (Join-Path $dir 'cache') | Out-Null
    Write-QaNetworkXml $v
    [void](Start-QaServer $v $Python)
    $info = (Invoke-Qa $v '/System/Info/Public').Json
    Say "${v}: Jellyfin $($info.Version) up on $(Get-QaUrl $v), wizard $(if ($info.StartupWizardCompleted) { 'done?!' } else { 'pending' })"
    if ($info.StartupWizardCompleted) { throw "${v} has a finished wizard on a wiped data folder" }

    # --- the startup wizard ---
    [void](Need (Invoke-Qa $v '/Startup/Configuration' 'POST' @{ UICulture = 'en-US'; MetadataCountryCode = 'US'; PreferredMetadataLanguage = 'en' }) 'Startup/Configuration')
    [void](Need (Invoke-Qa $v '/Startup/User') 'GET Startup/User')
    [void](Need (Invoke-Qa $v '/Startup/User' 'POST' @{ Name = $creds.admin.username; Password = $creds.admin.password }) 'POST Startup/User')
    [void](Need (Invoke-Qa $v '/Startup/RemoteAccess' 'POST' @{ EnableRemoteAccess = $false; EnableAutomaticPortMapping = $false }) 'Startup/RemoteAccess')
    [void](Need (Invoke-Qa $v '/Startup/Complete' 'POST') 'Startup/Complete')
    $admin = Connect-Qa $v 'admin' 'qa-seed'
    if (-not $admin.IsAdmin) { throw "${v}: qa-admin is not an administrator" }
    Say "${v}: wizard done (qa-admin)"

    # --- server settings: its name, Quick Connect, nothing slow or written next to the media ---
    $cfg = (Need (Invoke-Qa $v '/System/Configuration' -Token $admin.Token) 'GET System/Configuration').Json
    $set = [ordered]@{ ServerName = "Nixie QA $v"; QuickConnectAvailable = $true; EnableMetrics = $false
        PreferredMetadataLanguage = 'en'; MetadataCountryCode = 'US'; UICulture = 'en-US'; SaveMetadataHidden = $false }
    foreach ($k in $set.Keys) {
        if ($cfg.PSObject.Properties[$k]) { $cfg.$k = $set[$k] }
    }
    [void](Need (Invoke-Qa $v '/System/Configuration' 'POST' $cfg -Token $admin.Token) 'POST System/Configuration')
    $qc = Invoke-Qa $v '/QuickConnect/Enabled'
    if ($qc.Text -ne 'true') { throw "${v}: Quick Connect is not on (answered '$($qc.Text)')" }

    # --- the ordinary user ---
    $u = (Need (Invoke-Qa $v '/Users/New' 'POST' @{ Name = $creds.user.username; Password = $creds.user.password } -Token $admin.Token) 'Users/New').Json
    if ($u.Policy.IsAdministrator) { throw "${v}: qa-user came out an administrator" }
    $user = Connect-Qa $v 'user' 'qa-seed'
    Say "${v}: qa-user created (not an administrator)"

    # --- libraries, then one full scan ---
    foreach ($lib in $libraries) {
        $opts = [ordered]@{
            PathInfos = @(@{ Path = (Join-Path $media $lib.Folder) })
            EnableRealtimeMonitor = $false; EnablePhotos = $false; EnableInternetProviders = $true
            EnableChapterImageExtraction = $false; ExtractChapterImagesDuringLibraryScan = $false
            EnableTrickplayImageExtraction = $false; ExtractTrickplayImagesDuringLibraryScan = $false
            SaveLocalMetadata = $false; SaveSubtitlesWithMedia = $false; SaveLyricsWithMedia = $false; SaveTrickplayWithMedia = $false
            MetadataSavers = @(); AutomaticallyAddToCollection = $false; EnableEmbeddedTitles = $false
            PreferredMetadataLanguage = 'en'; MetadataCountryCode = 'US'; SeasonZeroDisplayName = 'Specials'
        }
        $q = '/Library/VirtualFolders?name={0}&collectionType={1}&refreshLibrary=false' -f [Uri]::EscapeDataString($lib.Name), $lib.Type
        [void](Need (Invoke-Qa $v $q 'POST' @{ LibraryOptions = $opts } -Token $admin.Token) "add library $($lib.Name)")
    }
    $since = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    [void](Need (Invoke-Qa $v '/Library/Refresh' 'POST' -Token $admin.Token) 'Library/Refresh')
    Say "${v}: $($libraries.Count) libraries added; scanning (TMDb lookups by the server)"
    if (-not (Wait-QaIdle $v $admin.Token $since $ScanMinutes)) { throw "${v}: the scan did not finish within $ScanMinutes min" }

    $items = (Invoke-Qa $v ("/Items?userId={0}&recursive=true&includeItemTypes=Movie,Series,Episode,Video,MusicVideo&fields=ProviderIds,Tags,Path" -f $user.UserId) -Token $user.Token).Json.Items
    $byType = $items | Group-Object Type | ForEach-Object { "$($_.Count) $($_.Name)" }
    Say "${v}: scanned: $($byType -join ', ')"
    $movies = @($items | Where-Object { $_.Type -eq 'Movie' })
    $id = @{}
    foreach ($m in $movies) { $id[$m.Name] = $m.Id }
    $find = { param($pattern) @($movies | Where-Object { $_.Name -like $pattern }) | Select-Object -First 1 }
    $bbb = & $find 'Big Buck Bunny*'; $sintel = & $find 'Sintel*'
    $ep = @($items | Where-Object { $_.Type -eq 'Episode' -and $_.Path -like '*S01E01.mkv' -and $_.Path -like '*Twilight*' }) | Select-Object -First 1
    $mv = @($items | Where-Object { $_.Type -eq 'MusicVideo' }) | Select-Object -First 1
    if (-not $bbb -or -not $sintel -or -not $ep -or -not $mv) { throw "${v}: the scan is missing items (Big Buck Bunny, Sintel, S01E01, a music video)" }

    # --- the box set (the admin's) and the playlist (qa-user's own) ---
    $box = (Need (Invoke-Qa $v ('/Collections?name={0}&ids={1},{2}' -f [Uri]::EscapeDataString('QA Box Set'), $bbb.Id, $sintel.Id) 'POST' -Token $admin.Token) 'create box set').Json
    # 12.4 (2026-11-19): the first box set on a fresh server came back empty from POST /Collections?ids=, a second one
    # did not; 10.11.11 kept both. Checked, said, and filled through POST /Collections/{id}/Items.
    Start-Sleep -Seconds 1
    $boxCount = (Invoke-Qa $v "/Items?userId=$($admin.UserId)&parentId=$($box.Id)" -Token $admin.Token).Json.TotalRecordCount
    if ($boxCount -ne 2) {
        Say "${v}: NOTE POST /Collections?ids= made a box set with $boxCount of 2 films; adding them with POST /Collections/{id}/Items"
        [void](Need (Invoke-Qa $v "/Collections/$($box.Id)/Items?ids=$($bbb.Id),$($sintel.Id)" 'POST' -Token $admin.Token) 'add to box set')
        Start-Sleep -Seconds 1
        $boxCount = (Invoke-Qa $v "/Items?userId=$($admin.UserId)&parentId=$($box.Id)" -Token $admin.Token).Json.TotalRecordCount
        if ($boxCount -ne 2) { throw "${v}: the box set has $boxCount of 2 films" }
    }
    $pl = (Need (Invoke-Qa $v '/Playlists' 'POST' @{ Name = 'QA Playlist'; Ids = @($ep.Id, $bbb.Id, $mv.Id); UserId = $user.UserId; MediaType = 'Video' } -Token $user.Token) 'create playlist').Json
    Say "${v}: box set 'QA Box Set' (2 films) and playlist 'QA Playlist' (3 items) made"
    Start-Sleep -Seconds 3
    [void](Wait-QaIdle $v $admin.Token 0 5)

    # --- what the smoke checks rely on (no secrets) ---
    $tmdb = @($movies | ForEach-Object { [ordered]@{ name = $_.Name; tmdb = $_.ProviderIds.Tmdb; collection = $_.ProviderIds.TmdbCollection; tags = @($_.Tags).Count } })
    $seed = [ordered]@{ seeded = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'); version = $info.Version; url = (Get-QaUrl $v)
        counts = [ordered]@{}; movies = $tmdb; box_set = $box.Id; playlist = $pl.Id; playlist_items = 3
        libraries = @($libraries | ForEach-Object { $_.Name }) }
    foreach ($g in ($items | Group-Object Type)) { $seed.counts[$g.Name] = $g.Count }
    $missing = @($tmdb | Where-Object { -not $_.tmdb } | ForEach-Object { $_.name })
    if ($missing) { Say "${v}: WARNING no TMDb id for: $($missing -join ', ')" }

    # --- snapshot for qa.ps1 reset, server left stopped (or running with -LeaveRunning) ---
    [void](Stop-QaServer $v)
    $snap = Join-Path $dir 'seeded'
    New-Item -ItemType Directory -Force $snap | Out-Null
    foreach ($d in 'data', 'config') { Copy-Item -LiteralPath (Join-Path $dir $d) -Destination (Join-Path $snap $d) -Recurse }
    [IO.File]::WriteAllText((Join-Path $snap 'seed.json'), ($seed | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    Say "${v}: seeded and snapshotted to $snap ($('{0:N0}' -f ((Get-ChildItem -LiteralPath $snap -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)) MB)"
    if ($LeaveRunning) { [void](Start-QaServer $v $Python); Say "${v}: left running on $(Get-QaUrl $v)" }
}
