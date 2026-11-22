<#
  1.7 trending: the Trakt Client ID of a release build, dot-sourced by build-release.ps1.

  The release build reads the user environment variable NIXIE_TRAKT_CLIENT_ID, else NIXIE_LIVE_TRAKT_CLIENT_ID (the live
  check's, the same registration on the maintainer's PC), and hands it to the publish in the environment variable
  FluentFinTraktClientId: FluentFin.csproj writes it into Nixie.dll as the assembly metadata NixieTraktClientId
  (Core\Outside\TraktClientId reads it back). It never goes on a command line, into a file of the repository, the source
  archive or a generated tracked file, and nothing here prints it: only whether it was found, and from which variable.
  Without one the build still succeeds, and Trakt's switch reads "Not available in this build". Nixie never signs anyone in
  to Trakt, so there is no Client Secret, ever. Forks register their own Trakt app (docs\RELEASE.md).
#>

# the variables, in the order they are read; each from this process first, then from the user's environment. The first is
# the release's own (the Production app's Client ID, once Trakt grants it); the second, the live check's Development app,
# is only a fallback, and the build says so loudly (-RequireProductionTraktId refuses it)
$NixieTraktClientIdVariables = @('NIXIE_TRAKT_CLIENT_ID', 'NIXIE_LIVE_TRAKT_CLIENT_ID')
$NixieTraktProductionVariable = 'NIXIE_TRAKT_CLIENT_ID'
# the variable the publish reads (FluentFin.csproj); set for the publish alone
$NixieTraktPublishVariable = 'FluentFinTraktClientId'
# the variable path_gate.py's --secret-env reads for the gate; set for the gate alone
$NixieTraktGateVariable = 'NIXIE_GATE_TRAKT_CLIENT_ID'

# The Client ID and where it came from: Value (null when none), Variable (null when none was set) and Problem (why a set
# one is left out: not shaped like a Client ID, so it could not be passed safely to MSBuild). -Scopes: where to look (the
# tests look in their own process only).
function Get-NixieTraktClientId([string[]]$Scopes = @('Process', 'User')) {
    foreach ($name in $NixieTraktClientIdVariables) {
        foreach ($scope in $Scopes) {
            $raw = [Environment]::GetEnvironmentVariable($name, $scope)
            if (-not $raw -or -not $raw.Trim()) { continue }
            $value = $raw.Trim()
            if ($value -notmatch '^[A-Za-z0-9_-]{16,128}$') {
                return [pscustomobject]@{ Value = $null; Variable = $name; Problem = 'is not shaped like a Client ID (16 to 128 letters, digits, - or _)'; Production = $false }
            }
            return [pscustomobject]@{ Value = $value; Variable = $name; Problem = $null; Production = ($name -eq $NixieTraktProductionVariable) }
        }
    }
    [pscustomobject]@{ Value = $null; Variable = $null; Problem = $null; Production = $false }
}

# The one line the build prints about it: which ID (Production, or the Development fallback), or none, and the variable's
# name. Never the value.
function Format-NixieTraktClientIdNote($found) {
    if ($found.Value -and $found.Production) { return "Trakt: Production ID ($($found.Variable)); it goes into Nixie.dll only" }
    if ($found.Value) { return "Trakt: Development ID (fallback): $NixieTraktProductionVariable is not set, so the live check's Development app ($($found.Variable)) goes into Nixie.dll. A release should carry the Production app's ID: set $NixieTraktProductionVariable, or pass -RequireProductionTraktId to refuse the fallback" }
    if ($found.Problem) { return "Trakt Client ID: $($found.Variable) $($found.Problem); left out, so Trakt reads 'Not available in this build'" }
    "Trakt Client ID: not found ($($NixieTraktClientIdVariables -join ', ')); Trakt reads 'Not available in this build'"
}

# -RequireProductionTraktId: the build stops unless the Production ID is there (the value is never in the message).
function Assert-NixieTraktClientId($found, [bool]$RequireProduction) {
    if ($RequireProduction -and -not ($found.Value -and $found.Production)) {
        $why = if ($found.Value) { "only the Development fallback in $($found.Variable) was found" } elseif ($found.Problem) { "$($found.Variable) $($found.Problem)" } else { 'none was found' }
        throw "-RequireProductionTraktId: the release needs the Production Trakt Client ID in $NixieTraktProductionVariable ($why)"
    }
}

# Runs $block with the publish variable set to the Client ID (when there is one), and always takes it away again.
function Invoke-WithNixieTraktClientId($found, [scriptblock]$block) {
    $saved = [Environment]::GetEnvironmentVariable($NixieTraktPublishVariable, 'Process')
    try {
        [Environment]::SetEnvironmentVariable($NixieTraktPublishVariable, $(if ($found.Value) { $found.Value } else { $null }), 'Process')
        & $block
    }
    finally { [Environment]::SetEnvironmentVariable($NixieTraktPublishVariable, $saved, 'Process') }
}

# The publish's own copies of the value outside Nixie.dll: the generated AssemblyInfo source in obj\ (ignored by git, never
# shipped). They are deleted, so dist\.work holds it nowhere but the built assemblies. Returns how many went.
function Remove-NixieTraktClientIdCopies([string]$artifacts, $found) {
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
