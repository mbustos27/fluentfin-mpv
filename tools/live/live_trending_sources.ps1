<#
.SYNOPSIS
  Opt-in, by hand: Nixie's trending sources against the REAL services, read-only, through the app's own requests: Trakt
  (api.trakt.tv) and TMDb (api.themoviedb.org). Nothing runs without -Live.

.DESCRIPTION
  Nixie 1.7.5 (BH-170-q2): the Trakt and TMDb legs are the Core test category LiveTrending
  (ui\FluentFin.Core.Tests\Outside\LiveTrendingSourcesTests.cs): the app's TrendingSources on the app's outside layer
  (OutsideHub: Nixie's User-Agent "Nixie/<version> (<repository>)", gzip only, redirects by hand, Trakt's Content-Type on an
  empty body). 1.7.0's script built its own HttpWebRequest, so the app's own GET had never met real Trakt.

  The keys are this Windows user's variables, read by the test itself (never an argument, never printed):
    Trakt: NIXIE_LIVE_TRAKT_CLIENT_ID, else NIXIE_TRAKT_CLIENT_ID (a release's).
    TMDb:  NIXIE_LIVE_TMDB_TOKEN, else NIXIE_TMDB_TOKEN (Nixie's app token, as a release builds it in).
  A source without one is skipped. This script says only whether each is set, and its length.

  It asks what the app asks (every list's pages: Trakt's seven lists, TMDb's two weeks), then the contrast requests that
  prove each strict fake's refusals are real (tools\ui-harness\fakeserver\outside_trakt.py, outside_tmdb_trending.py):
  Trakt with a made-up Client ID and without a User-Agent (403 expected), TMDb without the token (401 expected). About 20
  requests, far under Trakt's 500 GETs per 5 minutes. It contacts no other host (the test's recorder refuses any other).

  Writes <Out>\live-trending-summary.json (status codes, sizes, paging headers, list counts, a refusal's first words: never
  a key) and each list's first page cut to 12 titles (the parsers' recorded fixtures; ui\FluentFin.Core.Tests\Fixtures\
  Outside\ takes them by hand).

  Trakt deletes an app registration unused for 30 days, and a live check alone doesn't count as use (plan Appendix A).

.EXAMPLE
  .\tools\live\live_trending_sources.ps1 -Live -Out C:\scratch\live-trending
#>
[CmdletBinding()]
param(
    [switch]$Live,          # without it nothing is contacted
    [string]$Out            # where the summary and the trimmed answers go
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

if (-not $Live) { 'live_trending_sources: skipped (pass -Live to contact api.trakt.tv and api.themoviedb.org)'; exit 0 }
if (-not $Out) { throw 'pass -Out <folder> for the summary and the recorded answers' }
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

# whether each key is there, by name and length only
function Get-KeyNote([string[]]$names) {
    foreach ($name in $names) {
        foreach ($scope in 'Process', 'User') {
            $v = [Environment]::GetEnvironmentVariable($name, $scope)
            if ($v -and $v.Trim()) { return "$name ($($v.Trim().Length) characters)" }
        }
    }
    'not set: skipped'
}
"Trakt: $(Get-KeyNote 'NIXIE_LIVE_TRAKT_CLIENT_ID', 'NIXIE_TRAKT_CLIENT_ID')"
"TMDb:  $(Get-KeyNote 'NIXIE_LIVE_TMDB_TOKEN', 'NIXIE_TMDB_TOKEN')"

$env:NIXIE_LIVE_TRENDING = '1'
$env:NIXIE_LIVE_TRENDING_OUT = $Out
try {
    $ErrorActionPreference = 'Continue'
    dotnet test (Join-Path $repo 'ui\FluentFin.Core.Tests') -nodeReuse:false --filter 'Category=LiveTrending' --logger 'console;verbosity=normal' 2>&1 |
        Out-File -Encoding utf8 (Join-Path $Out 'test.log')
    $code = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = 'Stop'
    Remove-Item Env:NIXIE_LIVE_TRENDING, Env:NIXIE_LIVE_TRENDING_OUT -ErrorAction SilentlyContinue
}

$summary = Join-Path $Out 'live-trending-summary.json'
if (Test-Path $summary) {
    $s = Get-Content $summary -Raw | ConvertFrom-Json
    "User-Agent: $($s.userAgent)"
    $s.requests | Format-Table what, status, outcome, bytes, ms, pagination, sentBody -AutoSize | Out-String -Width 220
    if ($s.PSObject.Properties['lists']) { $s.lists | Format-Table source, list, kind, titles -AutoSize | Out-String }
    if ($s.PSObject.Properties['contrast']) { $s.contrast | Format-Table what, status -AutoSize | Out-String }
}
if ($code -ne 0) { "FAILED (see $(Join-Path $Out 'test.log'))"; exit 1 }
'DONE'
