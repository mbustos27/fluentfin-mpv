<#
.SYNOPSIS
  Opt-in, by hand: Nixie 1.7's trending sources against the REAL services, read-only: Trakt (api.trakt.tv) with the
  Client ID in NIXIE_LIVE_TRAKT_CLIENT_ID, and TMDb (api.themoviedb.org) with the token in NIXIE_LIVE_TMDB_TOKEN, else
  NIXIE_TMDB_TOKEN (Nixie's app token, as a release builds it in). A source whose variable is missing is skipped. Nothing
  runs without -Live.

.DESCRIPTION
  It asks exactly what the app asks (ui\FluentFin.Core\Outside\TraktLists.cs and TmdbLists.cs: the same addresses, the
  same headers, Nixie's User-Agent "Nixie/<version> (<repository>)", gzip), plus the contrast requests that prove each
  strict fake's refusals are real (tools\ui-harness\fakeserver\trakt.py, tmdb_trending.py):
    Trakt: the six lists' first page at limit=250 and trending's second, the box office, limit=300 (the clamp), one
           without a User-Agent (403 expected) and one with a made-up Client ID (403 expected). 11 requests, far under
           Trakt's 500 GETs per 5 minutes.
    TMDb:  trending films and shows this week, page 1, and one without the token (401 expected). 3 requests.
  It contacts no other host. It prints and writes each answer's status code, size and paging headers, never a Client ID
  or a token: the values are only ever sent in a request's headers. The answers' first 12 titles go to -Out as the
  parsers' recorded fixtures (ui\FluentFin.Core.Tests\Fixtures\Outside\ takes them; trimmed, never a whole list).

  Trakt deletes an app registration unused for 30 days, and a live check alone doesn't count as use (plan Appendix A).

.EXAMPLE
  .\tools\live\live_trending_sources.ps1 -Live -Out C:\scratch\live-trending
#>
[CmdletBinding()]
param(
    [switch]$Live,          # without it nothing is contacted
    [string]$Out,           # where the trimmed answers and the summary go
    [int]$Keep = 12         # titles kept of each answer
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

if (-not $Live) { 'live_trending_sources: skipped (pass -Live to contact api.trakt.tv and api.themoviedb.org)'; exit 0 }
if (-not $Out) { throw 'pass -Out <folder> for the recorded answers' }
New-Item -ItemType Directory -Force $Out | Out-Null

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$csproj = [xml](Get-Content -Raw (Join-Path $repo 'ui\FluentFin\FluentFin.csproj'))
$version = @($csproj.SelectNodes('//PropertyGroup/Version') | Where-Object { $_.GetAttribute('Condition') -match "== ''" } | ForEach-Object { $_.InnerText.Trim() })[0]
$userAgent = "Nixie/$version (https://github.com/mbustos27/nixie)"

function Get-Secret([string]$name) {
    foreach ($scope in 'Process', 'User') {
        $v = [Environment]::GetEnvironmentVariable($name, $scope)
        if ($v -and $v.Trim()) { return $v.Trim() }
    }
    $null
}

$summary = [Collections.Generic.List[string]]::new()
function Note([string]$line) { $line; $summary.Add($line) }

# One GET: its status, size in bytes, the response headers worth keeping (names of all of them; the values of paging,
# rate-limit and Retry-After headers only), and the body text. Never throws on an HTTP error.
function Invoke-Live([string]$url, [hashtable]$headers, [string]$agent, [string]$contentType) {
    $request = [Net.HttpWebRequest]::Create($url)
    $request.Method = 'GET'
    $request.Timeout = 30000
    $request.AutomaticDecompression = [Net.DecompressionMethods]::GZip -bor [Net.DecompressionMethods]::Deflate
    if ($agent) { $request.UserAgent = $agent }
    if ($contentType) { $request.ContentType = $contentType }
    foreach ($k in $headers.Keys) {
        if ($k -eq 'Accept') { $request.Accept = $headers[$k] } else { $request.Headers.Add($k, $headers[$k]) }
    }
    $response = $null
    try { $response = $request.GetResponse() }
    catch [Net.WebException] { $response = $_.Exception.Response }
    if (-not $response) { return [pscustomobject]@{ Status = -1; Bytes = 0; Headers = @{}; Names = @(); Body = '' } }
    try {
        $reader = [IO.StreamReader]::new($response.GetResponseStream(), [Text.Encoding]::UTF8)
        $body = $reader.ReadToEnd()
        $reader.Dispose()
        $kept = [ordered]@{}
        foreach ($name in $response.Headers.AllKeys) {
            if ($name -match '^(X-Pagination-|X-Ratelimit|Retry-After$|Content-Type$)') { $kept[$name] = $response.Headers[$name] }
        }
        [pscustomobject]@{ Status = [int]$response.StatusCode; Bytes = [Text.Encoding]::UTF8.GetByteCount($body); Headers = $kept
            Names = @($response.Headers.AllKeys | Sort-Object); Body = $body }
    }
    finally { $response.Close() }
}

function Save-Answer([string]$name, $answer, [string]$listProperty) {
    $file = Join-Path $Out ($name.Replace('/', '-') + '.json')
    $trimmed = $answer.Body
    try {
        $parsed = $answer.Body | ConvertFrom-Json
        if ($listProperty) {
            $parsed.$listProperty = @($parsed.$listProperty | Select-Object -First $Keep)
            $trimmed = $parsed | ConvertTo-Json -Depth 12 -Compress
        }
        elseif ($parsed -is [array]) {
            $trimmed = ConvertTo-Json -InputObject @($parsed | Select-Object -First $Keep) -Depth 12 -Compress
        }
    }
    catch { }
    [IO.File]::WriteAllText($file, $trimmed, [Text.UTF8Encoding]::new($false))
    $meta = [ordered]@{ status = $answer.Status; bytes = $answer.Bytes; headers = $answer.Headers; header_names = $answer.Names; kept = $Keep }
    [IO.File]::WriteAllText(($file -replace '\.json$', '.headers.json'), ($meta | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
}

function Format-Paging($answer) {
    $p = @($answer.Headers.Keys | Where-Object { $_ -like 'X-Pagination-*' } | ForEach-Object { "$($_ -replace '^X-Pagination-', '')=$($answer.Headers[$_])" })
    if ($p) { ' (' + ($p -join ', ') + ')' } else { '' }
}

Note "live_trending_sources $(Get-Date -Format s) UA '$userAgent'"
$failed = $false

# --- Trakt ----------------------------------------------------------------------------------------------------------
$clientId = Get-Secret 'NIXIE_LIVE_TRAKT_CLIENT_ID'
if (-not $clientId) { Note 'Trakt: skipped (NIXIE_LIVE_TRAKT_CLIENT_ID is not set)' }
else {
    # TraktLists.Headers: Content-Type (sent on the GET, as Trakt's guide asks), the version, the Client ID
    $trakt = @{ 'trakt-api-version' = '2'; 'trakt-api-key' = $clientId }
    $asks = @(
        @('movies/trending', '?page=1&limit=250'), @('movies/trending', '?page=2&limit=250'), @('shows/trending', '?page=1&limit=250'),
        @('movies/popular', '?page=1&limit=250'), @('shows/popular', '?page=1&limit=250'),
        @('movies/anticipated', '?page=1&limit=250'), @('shows/anticipated', '?page=1&limit=250'), @('movies/boxoffice', ''),
        @('movies/popular', '?page=1&limit=300'))
    foreach ($ask in $asks) {
        $answer = Invoke-Live "https://api.trakt.tv/$($ask[0])$($ask[1])" $trakt $userAgent 'application/json'
        $label = "$($ask[0])$($ask[1])"
        Note ("Trakt {0}: {1}, {2:N0} bytes{3}" -f $label, $answer.Status, $answer.Bytes, (Format-Paging $answer))
        if ($answer.Status -eq 200) {
            $suffix = if ($ask[1] -match 'page=2') { '-page2' } elseif ($ask[1] -match 'limit=300') { '-limit300' } else { '' }
            Save-Answer ("trakt-$($ask[0])$suffix") $answer $null
        }
        else { $failed = $true }
    }
    $noAgent = Invoke-Live 'https://api.trakt.tv/movies/trending?page=1&limit=1' $trakt '' 'application/json'
    Note "Trakt without a User-Agent: $($noAgent.Status) (403 expected: the fake's refusal)"
    $made = Invoke-Live 'https://api.trakt.tv/movies/trending?page=1&limit=1' @{ 'trakt-api-version' = '2'; 'trakt-api-key' = ('0' * 64) } $userAgent 'application/json'
    Note "Trakt with a made-up Client ID: $($made.Status) (403 expected: the fake's refusal)"
}

# --- TMDb -----------------------------------------------------------------------------------------------------------
# the live check's own token, else Nixie's app token (the one a release builds in, NIXIE_TMDB_TOKEN)
$token = Get-Secret 'NIXIE_LIVE_TMDB_TOKEN'
if (-not $token) { $token = Get-Secret 'NIXIE_TMDB_TOKEN' }
if (-not $token) { Note 'TMDb: skipped (neither NIXIE_LIVE_TMDB_TOKEN nor NIXIE_TMDB_TOKEN is set)' }
else {
    foreach ($media in 'movie', 'tv') {
        $answer = Invoke-Live "https://api.themoviedb.org/3/trending/$media/week?page=1" @{ 'Authorization' = "Bearer $token"; 'Accept' = 'application/json' } $userAgent ''
        Note ("TMDb trending/{0}/week page 1: {1}, {2:N0} bytes" -f $media, $answer.Status, $answer.Bytes)
        if ($answer.Status -eq 200) { Save-Answer "tmdb-trending-$media-week" $answer 'results' } else { $failed = $true }
    }
    $none = Invoke-Live 'https://api.themoviedb.org/3/trending/movie/week?page=1' @{ 'Accept' = 'application/json' } $userAgent ''
    Note "TMDb without a token: $($none.Status) (401 expected: the fake's refusal)"
}

[IO.File]::WriteAllLines((Join-Path $Out 'live-trending.txt'), $summary, [Text.UTF8Encoding]::new($false))
if ($failed) { 'FAILED (a list answered other than 200)'; exit 1 }
'DONE'
