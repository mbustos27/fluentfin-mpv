<#
.SYNOPSIS
  FluentFin MPV's libmpv: MSYS2's mpv package and every DLL libmpv-2.dll loads, collected into one folder, with the
  exact sources (vendor\libmpv\VENDORED.md). Runs build-libmpv.sh in MSYS2's UCRT64 environment, hidden.

.EXAMPLE
  .\build-libmpv.ps1 -Out ..\..\..\vendor\libmpv\msys2-20261116-mpv-0.42.0-2 -Sources ..\..\..\vendor\libmpv\sources -InstallPinned
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Sources,
    [switch]$InstallPinned,
    [string]$Msys2 = 'C:\msys64'
)
$ErrorActionPreference = 'Stop'
$bash = Join-Path $Msys2 'usr\bin\bash.exe'
if (-not (Test-Path $bash)) { throw "no MSYS2 at $Msys2 (VENDORED.md: install it first)" }
$script = (Join-Path $PSScriptRoot 'build-libmpv.sh') -replace '\\', '/'
$arguments = @('-l', $script, '--out', ([IO.Path]::GetFullPath($Out) -replace '\\', '/'))
if ($Sources) { $arguments += @('--sources', ([IO.Path]::GetFullPath($Sources) -replace '\\', '/')) }
if ($InstallPinned) { $arguments += '--install-pinned' }
$env:MSYSTEM = 'UCRT64'
$env:CHERE_INVOKING = '1'
& $bash @arguments
if ($LASTEXITCODE -ne 0) { throw "build-libmpv.sh failed ($LASTEXITCODE)" }
