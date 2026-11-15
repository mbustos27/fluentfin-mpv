<#
  Times a restore + build of a FluentFin tree (this repo's ui\ by default, or an upstream FluentFin checkout to compare
  against) and writes the build log.
  Usage: build.ps1 -Out <dir> [-Ui <folder with FluentFin.sln>]
#>
param(
    [Parameter(Mandatory)][string]$Out,
    [string]$Ui = (Join-Path $PSScriptRoot '..\..\ui')
)

$ErrorActionPreference = 'Continue'
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$Ui = [IO.Path]::GetFullPath($Ui)
New-Item -ItemType Directory -Force $Out | Out-Null
$log = Join-Path $Out 'build.log'
$t0 = Get-Date
"=== RESTORE+BUILD $Ui start $t0" | Out-File $log -Encoding utf8
dotnet build (Join-Path $Ui 'FluentFin.sln') -c Debug -p:Platform=x64 -clp:Summary 2>&1 | Tee-Object -FilePath $log -Append | Out-Null
$code = $LASTEXITCODE
"=== BUILD exit=$code elapsed=$(((Get-Date) - $t0).TotalSeconds)s" | Tee-Object -FilePath $log -Append
