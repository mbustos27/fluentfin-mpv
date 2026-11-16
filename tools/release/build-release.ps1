<#
.SYNOPSIS
  Builds the portable FluentFin MPV release: dist\FluentFinMPV-<version>-win-x64\, a .zip of it and a .sha256.

.DESCRIPTION
  Runs on a Windows x64 machine with the .NET 10 SDK, git, mpv on PATH (the backend tests) and network access to python.org,
  PyPI, nuget.org, GitHub and code.videolan.org. Everything it builds or downloads stays under this checkout's dist\
  (dist\.work\ for the intermediate files); nothing is installed. It stops unless the working tree is clean (no change,
  no untracked file) and the tests pass (both UI suites, the backend's bridge tests off-screen; -SkipTests only to try
  the packaging), and it ships no debug symbols: a path gate fails the build if any file of the package or of the source
  archive names the build machine's user, home, checkout or temp folders (tools\release\path_gate.py).

  1. Python: python.org's CPython $PythonVersion runtime zip (the one the Python install manager uses, listed in
     https://www.python.org/ftp/python/index-windows.json), checked against the SHA-256 pinned below and against its
     Authenticode signature (Python Software Foundation).
  2. A build venv from it, with tools\release\requirements-backend.txt and requirements-build.txt: wheels only, from
     PyPI only, every one checked against the sha256 pinned there (pip --require-hashes).
  3. The backend frozen with PyInstaller (tools\release\fluentfin-mpv-backend.spec), one folder:
     backend\fluentfin-mpv-backend.exe, which FluentFin finds before a dev checkout (MpvBackendCommand.Resolve).
  4. FluentFin: dotnet publish Release x64, self-contained, Windows App SDK self-contained, unpackaged
     (WindowsPackageType=None), with libmpv-2.dll from vendor\libmpv (its SHA-256 checked against VENDORED.md's), and
     without upstream's VLC and Flyleaf libraries (libvlc\, FFMpeg\): the release offers MPV and Windows Media Player.
     No MSBuild node reuse, no shared compiler, no build server; bin and obj under dist\.work\dotnet.
  5. The folder: the publish output, backend\, LICENSE, README.txt and THIRD-PARTY-NOTICES.txt (tools\release\notices.py),
     then the zip (one top folder, entries sorted) and its SHA-256 in sha256sum format.
  6. dist\sources\: the corresponding source of the GPL, LGPL and MPL code shipped, with SOURCES.txt
     (tools\release\sources.py, sources.json; official hosts only).

  The version must match ui\FluentFin\FluentFin.csproj's <Version>. docs\RELEASE.md has the whole release procedure.

.EXAMPLE
  .\tools\release\build-release.ps1
  .\tools\release\build-release.ps1 -Reuse      # keep dist\.work's Python runtime and venv from an earlier run
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [string]$SourceUrl,
    [switch]$Reuse,
    [switch]$SkipTests   # only to try the packaging itself; a release runs them
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version 3

# --- pins ---------------------------------------------------------------------------------------------------------
$PythonVersion = '3.14.10'
$PythonUrl = "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-amd64.zip"
$PythonSha256 = 'cc28026a650970cc1570bb127312f1f9634eb67abfe157ae6ca1acbbc627d091'   # python.org index-windows.json
$LibmpvBuild = '20261111-git-bcde6b93cc'
$LibmpvSha256 = '88504ee5560942a955405e99dfb5971368e4cf851321f36565bf2a667271db1b'  # vendor\libmpv\VENDORED.md

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$name = "FluentFinMPV-$Version-win-x64"
$dist = Join-Path $repo 'dist'
$work = Join-Path $dist '.work'
$package = Join-Path $dist $name
$zip = Join-Path $dist "$name.zip"
if (-not $SourceUrl) { $SourceUrl = "https://github.com/mbustos27/fluentfin-mpv/tree/v$Version" }

function Step([string]$text) { Write-Host "`n== $text" }

function Invoke-Checked([string]$what, [scriptblock]$block) {
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}

function Assert-Sha256([string]$path, [string]$expected) {
    $actual = (Get-FileHash -Algorithm SHA256 $path).Hash.ToLowerInvariant()
    if ($actual -ne $expected.ToLowerInvariant()) { throw "SHA-256 of $path is $actual, expected $expected" }
}

function Assert-PsfSigned([string]$path) {
    $sig = Get-AuthenticodeSignature $path
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'O=Python Software Foundation') {
        throw "$path is not validly signed by the Python Software Foundation ($($sig.Status), $($sig.SignerCertificate.Subject))"
    }
}

# --- checks before building -----------------------------------------------------------------------------------------
Step "FluentFin MPV $Version -> $package"
$csproj = [xml](Get-Content -Raw (Join-Path $repo 'ui\FluentFin\FluentFin.csproj'))
$csprojVersion = @($csproj.SelectNodes('//PropertyGroup/Version') | Where-Object { $_.GetAttribute('Condition') -match "== ''" } | ForEach-Object { $_.InnerText.Trim() })[0]
if ($csprojVersion -ne $Version) { throw "ui\FluentFin\FluentFin.csproj says version '$csprojVersion', not $Version (docs\RELEASE.md)" }
$libmpv = Join-Path $repo "vendor\libmpv\$LibmpvBuild\libmpv-2.dll"
if (-not (Test-Path $libmpv)) { throw "no ${libmpv}: download it as vendor\libmpv\VENDORED.md says" }
Assert-Sha256 $libmpv $LibmpvSha256

$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
$dotnet = (Get-Command dotnet.exe -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { throw 'dotnet.exe is not on PATH (winget install Microsoft.DotNet.SDK.10)' }
# no MSBuild node or build server stays behind, nothing goes to a shared compiler, no telemetry
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

# a release is built from a commit: nothing changed, nothing untracked (ignored output such as dist\ aside). The source
# archive is that commit (sources.py), and the package must be what it builds.
$dirty = @(& git -C $repo status --porcelain)
if ($LASTEXITCODE -ne 0) { throw "git status failed in $repo" }
if ($dirty) { throw "the working tree is not clean; commit or remove these first:`n$($dirty -join "`n")" }
New-Item -ItemType Directory -Force $work | Out-Null

# --- 1. Python runtime ----------------------------------------------------------------------------------------------
Step "Python $PythonVersion (python.org)"
$downloads = Join-Path $work 'downloads'
New-Item -ItemType Directory -Force $downloads | Out-Null
$pythonZip = Join-Path $downloads (Split-Path $PythonUrl -Leaf)
if (-not (Test-Path $pythonZip)) {
    Write-Host "downloading $PythonUrl"
    Invoke-WebRequest -UseBasicParsing -Uri $PythonUrl -OutFile "$pythonZip.part"
    Move-Item "$pythonZip.part" $pythonZip
}
Assert-Sha256 $pythonZip $PythonSha256
$pythonHome = Join-Path $work "python-$PythonVersion"
$python = Join-Path $pythonHome 'python.exe'
if (-not $Reuse -or -not (Test-Path $python)) {
    if (Test-Path $pythonHome) { Remove-Item -Recurse -Force $pythonHome }
    Expand-Archive -Path $pythonZip -DestinationPath $pythonHome
}
Assert-PsfSigned $python
Assert-PsfSigned (Join-Path $pythonHome 'python314.dll')
$reported = & $python -I -c "import platform; print(platform.python_version())"
if ($reported -ne $PythonVersion) { throw "$python reports $reported, not $PythonVersion" }

# --- 2. build venv --------------------------------------------------------------------------------------------------
Step 'build venv (PyPI wheels, hashes required)'
$venv = Join-Path $work 'venv'
$venvPython = Join-Path $venv 'Scripts\python.exe'
if (-not $Reuse -or -not (Test-Path $venvPython)) {
    if (Test-Path $venv) { Remove-Item -Recurse -Force $venv }
    Invoke-Checked 'venv' { & $python -I -m venv $venv }
}
$pycache = Join-Path $work 'pycache'
Invoke-Checked 'pip install' {
    & $venvPython -I -X "pycache_prefix=$pycache" -m pip install --isolated --disable-pip-version-check --no-cache-dir `
        --index-url https://pypi.org/simple --only-binary ':all:' --require-hashes `
        -r (Join-Path $PSScriptRoot 'requirements-backend.txt') -r (Join-Path $PSScriptRoot 'requirements-build.txt')
}
Invoke-Checked 'pip check' { & $venvPython -I -m pip check --disable-pip-version-check }

# --- 2b. the tests: a failure stops the release ---------------------------------------------------------------------
# Both UI suites, and the backend's bridge tests off-screen (a private desktop and a kill-on-close job through
# tools\backend-bench\hidden_run.py, APPDATA/LOCALAPPDATA in dist\.work; mpv on PATH, which test discovery needs). A
# test that fails is run once more on its own: the UI suites have timing tests that fail under load (KNOWN-ISSUES);
# one that fails again stops the build, and one that passes is named in the output.
function Invoke-UiTests([string]$project, [string[]]$extra) {
    # dotnet test writes failures to stderr; under 'Stop', PowerShell 5.1 would end the script at the first one
    $ErrorActionPreference = 'Continue'
    $out = @(& $dotnet test $project @extra -p:UseSharedCompilation=false -nodeReuse:false --disable-build-servers -v:q -nologo 2>&1 | ForEach-Object { "$_" })
    $summary = $out | Where-Object { $_ -match '^(Passed|Failed)!' }
    Write-Host "  $(Split-Path $project -Leaf): $summary"
    if ($LASTEXITCODE -eq 0) { return }
    $failed = @($out | ForEach-Object { if ($_ -match '\]\s+(\S+?)(?:\(.*\))? \[FAIL\]\s*$') { $Matches[1] } } | Sort-Object -Unique)
    if (-not $failed) { throw "$project failed, and no failed test was named:`n$(($out | Select-Object -Last 30) -join "`n")" }
    $filter = ($failed | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
    $again = @(& $dotnet test $project @extra -p:UseSharedCompilation=false -nodeReuse:false --disable-build-servers -v:q -nologo --filter $filter 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0) { throw "$project : these tests fail again on their own: $($failed -join ', ')`n$(($again | Select-Object -Last 30) -join "`n")" }
    Write-Warning "$(Split-Path $project -Leaf): failed in the full run and passed on their own (timing): $($failed -join ', ')"
}
if ($SkipTests) {
    Write-Warning 'the tests were skipped (-SkipTests): not a release build'
}
else {
    Step 'tests (UI suites, backend bridge tests)'
    Invoke-UiTests (Join-Path $repo 'ui\FluentFin.Core.Tests') @()
    Invoke-UiTests (Join-Path $repo 'ui\FluentFin.Tests') @('-p:Platform=x64')
    $mpvCmd = Get-Command mpv.exe -ErrorAction SilentlyContinue
    if (-not $mpvCmd) { throw 'the backend tests need mpv on PATH (winget install mpv-player.mpv-CI.MSVC)' }
    $mpvExe = $mpvCmd.Source
    $mpvItem = Get-Item $mpvExe
    if ($mpvItem.LinkType -and $mpvItem.Target) { $mpvExe = [IO.Path]::GetFullPath((Join-Path (Split-Path $mpvExe) @($mpvItem.Target)[0])) }
    $testEnv = Join-Path $work 'test-env'
    New-Item -ItemType Directory -Force (Join-Path $testEnv 'appdata'), (Join-Path $testEnv 'localappdata') | Out-Null
    $testLog = Join-Path $work 'backend-tests.log'
    if (Test-Path $testLog) { Remove-Item $testLog }
    Push-Location (Join-Path $repo 'backend')
    try {
        & $venvPython -B -I (Join-Path $repo 'tools\backend-bench\hidden_run.py') --log $testLog --timeout 1800 --desktop "ffr-release-tests-$PID" `
            --env "APPDATA=$testEnv\appdata" --env "LOCALAPPDATA=$testEnv\localappdata" `
            --env "PATH=$(Split-Path $mpvExe);$env:SystemRoot\System32;$env:SystemRoot" `
            -- $venvPython -B -m unittest discover -s tests -k test_bridge -k test_window_embed -k test_window_geometry | Out-Null
        $code = $LASTEXITCODE
    }
    finally { Pop-Location }
    $ran = Select-String -Path $testLog -Pattern '^Ran \d+ tests' | Select-Object -Last 1
    Write-Host "  backend bridge tests: $(if ($ran) { $ran.Line } else { 'no result' }) (log $testLog)"
    if ($code -ne 0 -or -not (Select-String -Path $testLog -Pattern '^OK' -Quiet)) { throw "the backend bridge tests failed (see $testLog)" }
}

# --- 3. the backend, frozen -----------------------------------------------------------------------------------------
Step 'backend (PyInstaller, one folder)'
$pyi = Join-Path $work 'pyinstaller'
if (Test-Path $pyi) { Remove-Item -Recurse -Force $pyi }
$savedPath = $env:Path
try {
    # nothing of PATH but Windows and this Python: no hook may pick up an mpv or another Python's DLL
    $env:Path = "$pythonHome;$env:SystemRoot\System32;$env:SystemRoot"
    $env:PYINSTALLER_CONFIG_DIR = Join-Path $pyi 'config'
    $env:FFM_RELEASE_VERSION = $Version
    Invoke-Checked 'PyInstaller' {
        & $venvPython -I -X "pycache_prefix=$pycache" -m PyInstaller --noconfirm --clean --log-level WARN `
            --distpath (Join-Path $pyi 'dist') --workpath (Join-Path $pyi 'build') (Join-Path $PSScriptRoot 'fluentfin-mpv-backend.spec')
    }
}
finally {
    $env:Path = $savedPath
    Remove-Item Env:\PYINSTALLER_CONFIG_DIR, Env:\FFM_RELEASE_VERSION -ErrorAction SilentlyContinue
}
$backendOut = Join-Path $pyi 'dist\backend'
if (-not (Test-Path (Join-Path $backendOut 'fluentfin-mpv-backend.exe'))) { throw "PyInstaller made no fluentfin-mpv-backend.exe in $backendOut" }
$strayMpv = @(Get-ChildItem $backendOut -Recurse -File -Include '*mpv*.dll', 'mpv.exe', 'vulkan-1.dll')
if ($strayMpv) { throw "the frozen backend picked up mpv files it must not ship: $($strayMpv.FullName -join ', ')" }
# every package folder that ships is one THIRD-PARTY-NOTICES.txt lists (a new one means a new dependency to list)
$knownFolders = 'certifi', 'charset_normalizer', 'jellyfin_mpv_shim', 'PIL', 'pywin32_system32', 'win32', 'fluentfin'
$unknown = @(Get-ChildItem (Join-Path $backendOut '_internal') -Directory | Where-Object { $_.Name -notin $knownFolders })
if ($unknown) { throw "the frozen backend ships folders the notices do not cover: $($unknown.Name -join ', ') (tools\release\requirements-backend.txt, notices.py)" }
# the mpv bar (uosc): every file its manifest lists, where fluentfin_bar looks for it frozen (_MEIPASS\fluentfin\mpv);
# one missing and the backend quietly falls back to the older bar
$uoscManifest = Join-Path $repo 'backend\fluentfin\uosc-files.txt'
if (Test-Path $uoscManifest) {
    $frozenAssets = Join-Path $backendOut '_internal\fluentfin'
    $listed = @(Get-Content $uoscManifest | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
    $absent = @($listed | Where-Object { -not (Test-Path (Join-Path $frozenAssets ('mpv\' + $_.Replace('/', '\')))) })
    if (-not (Test-Path (Join-Path $frozenAssets 'uosc-files.txt'))) { $absent += 'uosc-files.txt' }
    if ($absent) { throw "the frozen backend lacks uosc files: $($absent -join ', ')" }
    Write-Host "uosc: all $($listed.Count) files of backend\fluentfin\uosc-files.txt are in the frozen backend"
}

# --- 4. FluentFin ---------------------------------------------------------------------------------------------------
Step 'FluentFin (dotnet publish, self-contained, unpackaged)'
$publish = Join-Path $work 'publish'
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
$artifacts = Join-Path $work 'dotnet'
Invoke-Checked 'dotnet publish' {
    & $dotnet publish (Join-Path $repo 'ui\FluentFin\FluentFin.csproj') -c Release -r win-x64 -p:Platform=x64 `
        --self-contained true -p:WindowsPackageType=None -p:VlcWindowsX64Enabled=false -p:FluentFinMpvRelease=true `
        -p:DebugType=none -p:DebugSymbols=false `
        "-p:LibmpvDir=$(Split-Path $libmpv)" --artifacts-path $artifacts -o $publish `
        -nodeReuse:false -p:UseSharedCompilation=false --disable-build-servers -nologo -v:minimal
}
$exe = Join-Path $publish 'FluentFin.exe'
if (-not (Test-Path $exe)) { throw "dotnet publish made no FluentFin.exe in $publish" }
# the Windows App SDK self-contained (FluentFin.csproj sets WindowsAppSDKSelfContained; as a global property it would reach
# the class libraries, which refuse it): its runtime is in the folder, so nothing needs installing
foreach ($sdkFile in 'Microsoft.WindowsAppRuntime.dll', 'Microsoft.ui.xaml.dll', 'coreclr.dll') {
    if (-not (Test-Path (Join-Path $publish $sdkFile))) { throw "the publish is not self-contained: no $sdkFile in $publish" }
}
$productVersion = (Get-Item $exe).VersionInfo.ProductVersion
if ($productVersion -ne $Version) { throw "FluentFin.exe says product version $productVersion, not $Version" }
Assert-Sha256 (Join-Path $publish 'libmpv-2.dll') $LibmpvSha256
# no debug symbols (they name the build machine's folders), and the release marker (no dev-checkout lookup, AppVersion)
$pdbs = @(Get-ChildItem $publish -Recurse -Filter '*.pdb')
if ($pdbs) { throw "the publish has debug symbols: $($pdbs.Name -join ', ')" }
if (-not (Select-String -Path (Join-Path $publish 'FluentFin.dll') -SimpleMatch -Pattern 'FluentFinMpvRelease' -Quiet)) { throw 'FluentFin.dll has no FluentFinMpvRelease marker' }
# upstream's VLC and Flyleaf players are not in the release: no libvlc (VlcWindowsX64Enabled=false above) and no FFMpeg
# folder (FluentFin.csproj copies it for the dev build). FluentFin offers only the players whose libraries are there
# (Core\Settings\MediaPlayerAvailability), so the release offers MPV and Windows Media Player.
$flyleafFFmpeg = Join-Path $publish 'FFMpeg'
if (Test-Path $flyleafFFmpeg) { Remove-Item -Recurse -Force $flyleafFFmpeg }
foreach ($gone in 'libvlc', 'FFMpeg') { if (Test-Path (Join-Path $publish $gone)) { throw "the publish still has $gone\" } }

# --- 5. the corresponding source, for the GitHub release (dist\sources, SOURCES.txt); the notices read it ------------
Step 'sources (dist\sources)'
$sources = Join-Path $dist 'sources'
$libmpvStatus = Join-Path $work 'libmpv-status.json'
Invoke-Checked 'sources' { & $venvPython -I (Join-Path $PSScriptRoot 'sources.py') --manifest (Join-Path $PSScriptRoot 'sources.json') `
        --out $sources --cache (Join-Path $work 'sources-cache') --repo $repo --version $Version --status-out $libmpvStatus }

# --- 6. the folder, the notices, the zip ----------------------------------------------------------------------------
Step "package folder $package"
if (Test-Path $package) { Remove-Item -Recurse -Force $package }
Copy-Item -Recurse $publish $package
Copy-Item -Recurse $backendOut (Join-Path $package 'backend')
Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $package 'LICENSE')
$readme = (Get-Content -Raw (Join-Path $PSScriptRoot 'README.txt')).Replace('{{VERSION}}', $Version).Replace('{{SOURCE_URL}}', $SourceUrl)
[IO.File]::WriteAllText((Join-Path $package 'README.txt'), ($readme -replace "`r?`n", "`r`n"), [Text.UTF8Encoding]::new($false))

$assets = Join-Path $artifacts 'obj\FluentFin\project.assets.json'
if (-not (Test-Path $assets)) { throw "no $assets (the restore of FluentFin)" }
$components = @(
    "Microsoft Visual C++ runtime (backend\_internal\VCRUNTIME140.dll, VCRUNTIME140_1.dll)|as shipped with python.org's CPython $PythonVersion|Microsoft Distributable Code, under the conditions in Python's LICENSE.txt (section 5: 'Additional Conditions for this Windows binary build')|https://www.python.org/downloads/release/python-$($PythonVersion.Replace('.', ''))/",
    "Microsoft Visual C++ runtime for .NET (vcruntime140_cor3.dll)|as shipped in the .NET runtime pack Microsoft.NETCore.App.Runtime.win-x64|the .NET runtime pack's licence (section 4, its LICENSE.TXT in section 5)|https://github.com/dotnet/runtime",
    "libmpv (libmpv-2.dll)|mpv git-bcde6b93cc, shinchiro/mpv-winbuild-cmake release $LibmpvBuild (x86_64), with FFmpeg git-cd4e96eb3 and the other libraries its recipe builds in|GPL: mpv is GPL-2.0-or-later, and this build links FFmpeg and the other libraries it includes for GPL use|https://github.com/mpv-player/mpv/tree/bcde6b93cc ; build recipe: https://github.com/shinchiro/mpv-winbuild-cmake (release ${LibmpvBuild}: https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/$LibmpvBuild). The release's sources archive (SOURCES.txt) has what of it is known exactly|SHA-256 of libmpv-2.dll: $LibmpvSha256"
)
$fluentfinAssets = Join-Path $repo 'backend\fluentfin'
$noticeArgs = @('-I', (Join-Path $PSScriptRoot 'notices.py'), '--out', (Join-Path $package 'THIRD-PARTY-NOTICES.txt'), '--version', $Version,
    '--source-url', $SourceUrl, '--assets', $assets, '--requirements', (Join-Path $PSScriptRoot 'requirements-backend.txt'),
    '--build-requirements', (Join-Path $PSScriptRoot 'requirements-build.txt'), '--python-license', (Join-Path $pythonHome 'LICENSE.txt'),
    '--python-version', $PythonVersion)
if (Test-Path $fluentfinAssets) { $noticeArgs += @('--fluentfin-assets', $fluentfinAssets) }
foreach ($c in $components) { $noticeArgs += @('--component', $c) }
# restored for the build, not shipped (see the publish step)
$noticeArgs += @('--not-shipped', 'VideoLAN.LibVLC.Windows', '--package-dir', $package,
    '--sources-manifest', (Join-Path $PSScriptRoot 'sources.json'), '--sources-dir', $sources, '--libmpv-status', $libmpvStatus)
Invoke-Checked 'notices' { & $venvPython @noticeArgs }
# what the notices must carry (the release review): the LGPL-3.0 text (FlyleafLib ships), the VC++ runtimes, the .NET
# runtime pack, and no Windows App SDK package called build-only (its files ship)
$noticeText = [IO.File]::ReadAllText((Join-Path $package 'THIRD-PARTY-NOTICES.txt'), [Text.Encoding]::UTF8)
$noticeGaps = @()
if ($noticeText -notmatch 'GNU LESSER GENERAL PUBLIC LICENSE\s+Version 3') { $noticeGaps += 'the LGPL-3.0 text' }
foreach ($must in 'VCRUNTIME140.dll', 'VCRUNTIME140_1.dll', 'vcruntime140_cor3.dll', 'Microsoft.NETCore.App.Runtime.win-x64') {
    if (-not $noticeText.Contains($must)) { $noticeGaps += $must }
}
if ($noticeText -match '(?m)^Microsoft\.WindowsAppSDK[^\r\n]*\r?\n(?:  [^\r\n]*\r?\n)*?  \[build only\]') { $noticeGaps += 'a Windows App SDK package marked build only' }
if ($noticeGaps) { throw "THIRD-PARTY-NOTICES.txt lacks: $($noticeGaps -join '; ')" }

foreach ($needed in 'FluentFin.exe', 'libmpv-2.dll', 'backend\fluentfin-mpv-backend.exe', 'LICENSE', 'README.txt', 'THIRD-PARTY-NOTICES.txt') {
    if (-not (Test-Path (Join-Path $package $needed))) { throw "the package has no $needed" }
}
$devFiles = @(Get-ChildItem $package -Recurse -File -Include '*.runtimeconfig.dev.json', 'LocalSettings.json')
if ($devFiles) { throw "dev-only files in the package: $($devFiles.FullName -join ', ')" }

# --- 6b. nothing names the machine it was built on ------------------------------------------------------------------
# every file of the package (the frozen backend's archive and the zips in it included) and of the source archive,
# case-insensitively, as UTF-8 and UTF-16: the user name, the home, the checkout, the folder above it, the temp folder
Step 'path gate'
$needles = @($env:USERNAME, $env:USERPROFILE, $repo, (Split-Path $repo), $env:TEMP, [IO.Path]::GetTempPath().TrimEnd('\')) | Where-Object { $_ } | Select-Object -Unique
$gateArgs = @('-I', (Join-Path $PSScriptRoot 'path_gate.py'), '--dir', $package, '--archive', (Join-Path $sources "fluentfin-mpv-$Version-source.zip"))
foreach ($n in $needles) { $gateArgs += @('--needle', $n) }
Invoke-Checked 'path gate' { & $venvPython @gateArgs }

Step "zip $zip"
foreach ($f in $zip, "$zip.sha256") { if (Test-Path $f) { Remove-Item -Force $f } }
Invoke-Checked 'zip' { & $venvPython -I (Join-Path $PSScriptRoot 'zipdir.py') $package $zip }
$hash = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$hash *$name.zip`n", [Text.UTF8Encoding]::new($false))

$files = @(Get-ChildItem $package -Recurse -File)
Write-Host ("`n{0}`n  {1} files, {2:N1} MB unpacked`n{3}`n  {4:N1} MB, SHA-256 {5}" -f $package, $files.Count,
    (($files | Measure-Object Length -Sum).Sum / 1MB), $zip, ((Get-Item $zip).Length / 1MB), $hash)
$sourceFiles = @(Get-ChildItem $sources -File)
Write-Host ("{0}
  {1} files, {2:N1} MB (SOURCES.txt)" -f $sources, $sourceFiles.Count, (($sourceFiles | Measure-Object Length -Sum).Sum / 1MB))
