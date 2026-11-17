<#
  Helpers for the scripts that run the installer (upgrade-test.ps1, installer\installer-shots.ps1): dot-source this file.
  Read-only against the machine: MSI tables through the Windows Installer COM API, the installed products by upgrade code,
  and the Apps list (the Uninstall registry keys). Nothing here installs or removes anything.
#>

function Get-MsiTable([string]$Msi, [string]$Query) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = $installer.OpenDatabase($Msi, 0)
    $view = $db.OpenView($Query); [void]$view.Execute()
    $rows = @()
    $row = $view.Fetch()
    while ($row) { $rows += , @($row.StringData(1), $row.StringData(2)); $row = $view.Fetch() }
    [void]$view.Close()
    return ,$rows
}

function Get-MsiProperty([string]$Msi, [string]$Name) {
    $rows = Get-MsiTable $Msi "SELECT ``Property``,``Value`` FROM ``Property`` WHERE ``Property``='$Name'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0][1]
}

# the product codes installed for this user (or the machine) that belong to an upgrade code
function Get-InstalledByUpgradeCode([string]$UpgradeCode) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $codes = @()
    foreach ($code in $installer.RelatedProducts($UpgradeCode)) { $codes += [string]$code }
    return $codes
}

function Get-ProductInfo([string]$ProductCode, [string]$Attribute) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    try { return [string]$installer.ProductInfo($ProductCode, $Attribute) } catch { return $null }
}

# the Settings > Apps entries (per-user and machine-wide, 64 and 32 bit) whose name is one of $Names
function Get-AppsEntries([string[]]$Names) {
    $roots = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    $found = @()
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        foreach ($key in Get-ChildItem $root -ErrorAction SilentlyContinue) {
            $p = Get-ItemProperty $key.PSPath -ErrorAction SilentlyContinue
            if ($p -and $p.PSObject.Properties['DisplayName'] -and $p.DisplayName -in $Names) {
                $found += [pscustomobject]@{ Key = $key.PSChildName; Name = $p.DisplayName; Version = $p.DisplayVersion; Publisher = $p.Publisher; Root = $root }
            }
        }
    }
    return $found
}

# $true when nothing of Nixie, Seaglass or FluentFin MPV is installed (by upgrade code, and by name in Apps): the condition before any
# test installs anything. $Reasons gets what was found.
function Test-NothingInstalled([string]$UpgradeCode, [string[]]$Names, [ref]$Reasons) {
    $found = @()
    foreach ($code in Get-InstalledByUpgradeCode $UpgradeCode) { $found += "product $code ($(Get-ProductInfo $code 'ProductName') $(Get-ProductInfo $code 'VersionString'))" }
    foreach ($e in Get-AppsEntries $Names) { $found += "Apps entry '$($e.Name)' $($e.Version) ($($e.Key))" }
    $Reasons.Value = $found
    return ($found.Count -eq 0)
}

# a listing of a folder (relative path, size, last write): to see that nothing in it changed. $null when it does not exist.
function Get-FolderSnapshot([string]$Folder) {
    if (-not (Test-Path $Folder)) { return $null }
    return @(Get-ChildItem $Folder -Recurse -Force -ErrorAction SilentlyContinue | Sort-Object FullName |
        ForEach-Object { '{0}|{1}|{2}' -f $_.FullName.Substring($Folder.Length), $(if ($_.PSIsContainer) { 'dir' } else { $_.Length }), $_.LastWriteTimeUtc.Ticks })
}

function Get-FileHashes([string]$Dir) {
    $root = [IO.Path]::GetFullPath($Dir).TrimEnd('\')
    $map = @{}
    if (Test-Path $root) {
        foreach ($f in Get-ChildItem $root -Recurse -File -Force) { $map[$f.FullName.Substring($root.Length + 1)] = (Get-FileHash -Algorithm SHA256 $f.FullName).Hash }
    }
    return $map
}
