<#
.SYNOPSIS
  Opt-in, by hand: the updater against the REAL GitHub release (read-only). One request to
  api.github.com/repos/mbustos27/fluentfin-mpv/releases/latest with Nixie's User-Agent, through the updater's own code
  (UpdaterTests.Live_TheRealLatestRelease_PassesTheUpdatersRules), and one without a User-Agent to see GitHub still refuse
  it (403), as the fake release does. Nothing is downloaded. Run it before a release, and after any change to the
  release's assets or the repository's name (docs\RELEASE.md, step 5).
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

# GitHub refuses an API request without a User-Agent: the fake release must keep doing the same
$request = [Net.HttpWebRequest]::Create('https://api.github.com/repos/mbustos27/fluentfin-mpv/releases/latest')
$request.Accept = 'application/vnd.github+json'
try { $response = $request.GetResponse(); $status = [int]$response.StatusCode; $response.Close() }
catch [Net.WebException] { $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { -1 } }
"  without a User-Agent: $status (403 expected)"
if (-not $passed -or $status -ne 403) { 'FAILED'; exit 1 }
'PASSED'
