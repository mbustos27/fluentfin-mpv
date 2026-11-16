<#
.SYNOPSIS
  Builds FluentFin MPV's libdvdcss-2.dll stub (dvdcss-stub.c: every function fails) with WinLibs gcc, in a neutral
  folder (default C:\b\dvdcss-stub) so no user path ends up in the binary; no debug info, no timestamp. Checks that it
  exports exactly the seven functions libdvdread-8.dll imports and holds no C:\Users path. build-libmpv.sh --dvdcss-stub
  puts it into the libmpv folder in place of MSYS2's libdvdcss (vendor\libmpv\VENDORED.md).
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Work = 'C:\b\dvdcss-stub',
    [string]$Gcc = (Join-Path $env:LOCALAPPDATA 'Programs\winlibs\mingw64\bin\gcc.exe')
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Gcc)) { throw "no WinLibs gcc at $Gcc" }
New-Item -ItemType Directory -Force $Work | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'dvdcss-stub.c'), (Join-Path $PSScriptRoot 'libdvdcss-2.def') $Work -Force
Push-Location $Work
try {
    Remove-Item 'libdvdcss-2.dll' -ErrorAction SilentlyContinue
    # relative names only, in the neutral folder; -s strips symbols; no -g
    & $Gcc -O2 -s -shared -static-libgcc '-Wl,--no-insert-timestamp' '-Wl,--build-id=none' -o 'libdvdcss-2.dll' 'dvdcss-stub.c' 'libdvdcss-2.def'
    if ($LASTEXITCODE -ne 0) { throw "gcc failed ($LASTEXITCODE)" }
    $dll = Join-Path $Work 'libdvdcss-2.dll'
    $objdump = Join-Path (Split-Path $Gcc) 'objdump.exe'
    $dump = & $objdump -p $dll
    $exports = @($dump | Select-String -Pattern '^\s*\[\s*\d+\]\s+\+base\[\s*\d+\]\s+[0-9a-f]+\s+(\S+)\s*$' | ForEach-Object { $_.Matches[0].Groups[1].Value }) | Sort-Object
    $want = 'dvdcpxm_init', 'dvdcpxm_read', 'dvdcss_close', 'dvdcss_open', 'dvdcss_open_stream', 'dvdcss_read', 'dvdcss_seek'
    if (($exports -join ',') -ne (($want | Sort-Object) -join ',')) { throw "exports are '$($exports -join ', ')', not the seven libdvdread imports" }
    $text = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($dll))
    foreach ($bad in 'C:\Users', 'C:/Users', 'AppData') {
        if ($text.IndexOf($bad, [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw "the DLL holds '$bad'" }
    }
    $imports = @($dump | Select-String -Pattern 'DLL Name: (\S+)' | ForEach-Object { $_.Matches[0].Groups[1].Value })
    $hash = (Get-FileHash $dll -Algorithm SHA256).Hash.ToLowerInvariant()
    "$dll  $((Get-Item $dll).Length) bytes  sha256 $hash"
    "exports: $($exports -join ', ')"
    "imports: $($imports -join ', ')"
    "gcc: $(& $Gcc --version | Select-Object -First 1)"
}
finally {
    Pop-Location
}
