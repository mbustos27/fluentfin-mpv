<#
  1.7 trending (the owner, 2026-11-22): Nixie's own TMDb API Read Access Token (v4) for TMDb trending, dot-sourced by
  build-release.ps1 beside trakt-client-id.ps1, and handled the same way.

  The release build reads the environment variable NIXIE_TMDB_TOKEN (this process first, then the user's environment) and
  hands it to the publish in the environment variable FluentFinTmdbToken: FluentFin.csproj writes it into Nixie.dll as the
  assembly metadata NixieTmdbToken (Core\Outside\TmdbAppToken reads it back; a token the user enters in Settings wins). It
  never goes on a command line, into a file of the repository, the source archive or a generated tracked file, and nothing
  here prints it: only "TMDb: app token" or "TMDb: no app token". Without one the build still succeeds and TMDb trending
  needs the user's own token ("Not available in this build" until then); -RequireAppTmdbToken refuses that.
#>

$NixieTmdbTokenVariable = 'NIXIE_TMDB_TOKEN'
# the variable the publish reads (FluentFin.csproj); set for the publish alone
$NixieTmdbPublishVariable = 'FluentFinTmdbToken'
# the variable path_gate.py's --secret-env reads for the gate; set for the gate alone
$NixieTmdbGateVariable = 'NIXIE_GATE_TMDB_TOKEN'

# The token: Value (null when none) and Problem (why a set one is left out: not shaped like a Read Access Token, a JWT's
# three base64url parts, which is also what MSBuild can take as it is). -Scopes: where to look (the tests look in their own
# process only).
function Get-NixieTmdbToken([string[]]$Scopes = @('Process', 'User')) {
    foreach ($scope in $Scopes) {
        $raw = [Environment]::GetEnvironmentVariable($NixieTmdbTokenVariable, $scope)
        if (-not $raw -or -not $raw.Trim()) { continue }
        $value = $raw.Trim()
        if ($value -notmatch '^eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$') {
            return [pscustomobject]@{ Value = $null; Problem = "$NixieTmdbTokenVariable is not shaped like a TMDb API Read Access Token (the long one, not the 32-digit API key)" }
        }
        return [pscustomobject]@{ Value = $value; Problem = $null }
    }
    [pscustomobject]@{ Value = $null; Problem = $null }
}

# The one line the build prints about it. Never the value.
function Format-NixieTmdbTokenNote($found) {
    if ($found.Value) { return 'TMDb: app token' }
    if ($found.Problem) { return "TMDb: no app token ($($found.Problem); left out)" }
    "TMDb: no app token ($NixieTmdbTokenVariable is not set; TMDb trending needs the user's own token)"
}

# -RequireAppTmdbToken: the build stops unless the app token is there (the value is never in the message).
function Assert-NixieTmdbToken($found, [bool]$RequireAppToken) {
    if ($RequireAppToken -and -not $found.Value) {
        $why = if ($found.Problem) { $found.Problem } else { "$NixieTmdbTokenVariable is not set" }
        throw "-RequireAppTmdbToken: the release needs Nixie's own TMDb token ($why)"
    }
}

# Runs $block with the publish variable set to the token (when there is one), and always takes it away again.
function Invoke-WithNixieTmdbToken($found, [scriptblock]$block) {
    $saved = [Environment]::GetEnvironmentVariable($NixieTmdbPublishVariable, 'Process')
    try {
        [Environment]::SetEnvironmentVariable($NixieTmdbPublishVariable, $(if ($found.Value) { $found.Value } else { $null }), 'Process')
        & $block
    }
    finally { [Environment]::SetEnvironmentVariable($NixieTmdbPublishVariable, $saved, 'Process') }
}

# The publish's own copies of the value outside Nixie.dll: the generated AssemblyInfo source in obj\ (ignored by git, never
# shipped), deleted. Returns how many went.
function Remove-NixieTmdbTokenCopies([string]$artifacts, $found) {
    if (-not $found.Value -or -not (Test-Path $artifacts)) { return 0 }
    $gone = 0
    foreach ($file in Get-ChildItem $artifacts -Recurse -File -Filter '*.AssemblyInfo.cs' -ErrorAction SilentlyContinue) {
        if ([IO.File]::ReadAllText($file.FullName).Contains($found.Value)) {
            Remove-Item -Force $file.FullName
            $gone++
        }
    }
    $gone
}
