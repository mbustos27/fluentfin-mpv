<#
.SYNOPSIS
  Opt-in live check (Nixie 1.7, stream K): TMDb's daily export files and Wikidata's query service, asked once with Nixie's
  real request shapes through the real outside layer (ui\FluentFin.Core\Outside), to prove the parsers and the refusals the
  strict fakes model (tools\ui-harness\fakeserver\outside_*.py).

.DESCRIPTION
  Skipped unless asked: it runs only with -Run. It may reach only:
  - files.tmdb.org: yesterday's collection and keyword export files (one request each);
  - Wikidata's query endpoints, query.wikidata.org and query-next.wikidata.org: each list's fixed query, one at a time, one
    label query for the items the queries name, one request without a User-Agent (it must be refused with 403), and one
    query to query-next (refused with "Unauthorized User-Agent" until it opens).
  Every request carries Nixie's User-Agent, Nixie/<version> (<repository URL>), but the one without. Any other host fails
  the run (the recorder refuses it). Nothing about any library, server or user is sent: the requests are constants.

  Writes <Out>\live-outside-summary.json (status codes, sizes, row counts, timings; never a key, a path or a user's data).
  With -Fixtures, writes the trimmed real answers the parser tests read into ui\FluentFin.Core.Tests\Outside\Fixtures: the
  export files' first lines and the lines the tests name (never a whole file), and the Wikidata answers cut to 40 rows (the
  Best Picture answer whole).

.EXAMPLE
  .\live_outside_sources.ps1 -Run -Out <scratch>\live
  .\live_outside_sources.ps1 -Run -Out <scratch>\live -Fixtures
#>
[CmdletBinding(PositionalBinding = $false)]
param([switch]$Run, [Parameter(Mandatory)][string]$Out, [switch]$Fixtures)
$ErrorActionPreference = 'Stop'
if (-not $Run) {
    Write-Host 'Skipped: the live check runs only with -Run (it contacts files.tmdb.org and Wikidata once).'
    exit 0
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
New-Item -ItemType Directory -Force $Out | Out-Null
$env:NIXIE_LIVE_OUTSIDE = '1'
$env:NIXIE_LIVE_OUTSIDE_OUT = (Resolve-Path $Out).Path
if ($Fixtures) { $env:NIXIE_LIVE_OUTSIDE_FIXTURES = Join-Path $root 'ui\FluentFin.Core.Tests\Outside\Fixtures' }
else { Remove-Item Env:NIXIE_LIVE_OUTSIDE_FIXTURES -ErrorAction SilentlyContinue }
try {
    dotnet test (Join-Path $root 'ui\FluentFin.Core.Tests') -nodeReuse:false --filter 'Category=LiveOutside' --logger 'console;verbosity=normal'
    $code = $LASTEXITCODE
}
finally {
    Remove-Item Env:NIXIE_LIVE_OUTSIDE, Env:NIXIE_LIVE_OUTSIDE_OUT, Env:NIXIE_LIVE_OUTSIDE_FIXTURES -ErrorAction SilentlyContinue
}

$summary = Join-Path $Out 'live-outside-summary.json'
if (Test-Path $summary) {
    (Get-Content $summary -Raw | ConvertFrom-Json).requests |
        Format-Table what, status, outcome, bytes, ms, @{ n = 'rows'; e = { if ($null -ne $_.items) { $_.items } elseif ($null -ne $_.lines) { $_.lines } } } -AutoSize
}
exit $code
