<#
.SYNOPSIS
  Opt-in, by hand: the updater against the REAL GitHub release (read-only). One request to
  api.github.com/repos/mbustos27/nixie/releases/latest with Nixie's User-Agent, through the updater's own code
  (UpdaterTests.Live_TheRealLatestRelease_PassesTheUpdatersRules, which also asserts GitHub used the API version Nixie
  pins, BH-150-m8), one to the repository's old name as copies up to 1.7.2 ask it (1.7.3: GitHub must still redirect
  it, and they must take the same release), one without a User-Agent to see GitHub still refuse it (403), and one with
  an API version GitHub doesn't know to see it refuse that (400), as the fake release does. Nothing is downloaded. Run it before a release,
  and after any change to the release's assets or the repository's name (docs\RELEASE.md, step 5).
#>
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$env:NIXIE_LIVE_GITHUB = '1'
try {
    & dotnet test (Join-Path $repo 'ui\FluentFin.Core.Tests') -nodeReuse:false --filter 'FullyQualifiedName~Live_TheRealLatestRelease_PassesTheUpdatersRules' --logger 'console;verbosity=minimal'
    $passed = $LASTEXITCODE -eq 0
}
finally { Remove-Item Env:NIXIE_LIVE_GITHUB -ErrorAction SilentlyContinue }
$record = Join-Path ([IO.Path]::GetTempPath()) 'nixie-live-release-check.txt'
if (Test-Path $record) { Get-Content $record | ForEach-Object { "  $_" } }

function Get-Status([string]$UserAgent, [string]$ApiVersion) {
    $request = [Net.HttpWebRequest]::Create('https://api.github.com/repos/mbustos27/nixie/releases/latest')
    $request.Accept = 'application/vnd.github+json'
    if ($UserAgent) { $request.UserAgent = $UserAgent }
    if ($ApiVersion) { $request.Headers.Add('X-GitHub-Api-Version', $ApiVersion) }
    try { $response = $request.GetResponse(); $status = [int]$response.StatusCode; $response.Close() }
    catch [Net.WebException] { $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { -1 } }
    $status
}

# GitHub refuses an API request without a User-Agent, and one with an API version it doesn't know (BH-150-m8, the
# contrast to the pinned one): the fake release must keep doing the same. 1.7.3: since 2026-11-22 GitHub's servers differ
# on an API version they don't know: some answer 400 as before, others 200 with a version of their choosing
# (x-github-api-version-selected), for the same request a minute apart, a date or not. Either is recorded; Nixie always
# sends the version it pins, and the fake release still refuses (the stricter of the two)
$noAgent = Get-Status '' ''
"  without a User-Agent: $noAgent (403 expected)"
$unknown = Get-Status 'Nixie/0.0.1' '1999-01-01'
"  with API version 1999-01-01: $unknown (400, or 200 from GitHub's lenient servers)"
if (-not $passed -or $noAgent -ne 403 -or $unknown -notin @(400, 200)) { 'FAILED'; exit 1 }
'PASSED'
