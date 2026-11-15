<#
  Restores and runs the two test projects of a FluentFin tree (this repo's ui\ by default, or an upstream FluentFin
  checkout for a baseline), timing each step; console log and .trx results go to -Out.
  Usage: tests.ps1 -Out <dir> [-Ui <folder with FluentFin.sln>]
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
$Out = [IO.Path]::GetFullPath($Out)
$log = Join-Path $Out 'tests.log'
"=== tests of $Ui $(Get-Date -Format o)" | Out-File $log -Encoding utf8
$core = Join-Path $Ui 'FluentFin.Core.Tests\FluentFin.Core.Tests.csproj'
$app = Join-Path $Ui 'FluentFin.Tests\FluentFin.Tests.csproj'
$runs = @(
    @{ name = 'restore FluentFin.Core.Tests'; args = @('restore', $core) },
    @{ name = 'restore FluentFin.Tests'; args = @('restore', $app, '-p:Platform=x64') },
    @{ name = 'test FluentFin.Core.Tests'; args = @('test', $core, '-c', 'Debug', '--no-restore', '--logger', 'console;verbosity=normal', '--logger', "trx;LogFileName=$Out\core-tests.trx") },
    @{ name = 'test FluentFin.Tests'; args = @('test', $app, '-c', 'Debug', '-p:Platform=x64', '--no-restore', '--logger', 'console;verbosity=normal', '--logger', "trx;LogFileName=$Out\app-tests.trx") }
)
foreach ($t in $runs) {
    $t0 = Get-Date
    "=== RUN $($t.name): dotnet $($t.args -join ' ')" | Out-File $log -Append -Encoding utf8
    & dotnet @($t.args) 2>&1 | Out-File $log -Append -Encoding utf8
    $code = $LASTEXITCODE
    "=== END $($t.name) exit=$code wall=$([math]::Round(((Get-Date) - $t0).TotalSeconds, 1))s" | Tee-Object -FilePath $log -Append
}
