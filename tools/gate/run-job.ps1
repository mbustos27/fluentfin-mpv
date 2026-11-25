<#
  Nixie 1.7.5 (the basics gate): one job of tools\gate\run-gate.ps1 (one check in one theme), in its own hidden
  PowerShell. The runner writes the job's file (JSON: the script to call, its arguments, the environment to set); this
  sets the environment in this process only, calls the script (run-offscreen.ps1 for a 'then' check, the check's own
  script for a 'script' one), records everything it prints in the job's console file, and exits with its exit code.
  A script that throws (no slot came free, a bad argument) leaves its message in <console>.error.txt and exits 3.
#>
param([Parameter(Mandatory)][string]$JobFile)

$ErrorActionPreference = 'Stop'
$job = Get-Content -LiteralPath $JobFile -Raw -Encoding utf8 | ConvertFrom-Json
$errorFile = "$($job.console).error.txt"
[void](Start-Transcript -LiteralPath $job.console -Force)
$code = 3
try {
    foreach ($p in @($job.env.PSObject.Properties)) { Set-Item -LiteralPath "Env:$($p.Name)" -Value ([string]$p.Value) }
    $splat = @{}
    foreach ($p in @($job.args.PSObject.Properties)) {
        $v = $p.Value
        # a JSON array arrives as object[]: the scripts take [string[]]
        if ($v -is [object[]]) { $v = [string[]]@($v | ForEach-Object { [string]$_ }) }
        $splat[$p.Name] = $v
    }
    $global:LASTEXITCODE = 0
    & $job.script @splat
    $code = $LASTEXITCODE
    if ($null -eq $code) { $code = 0 }
}
catch {
    $message = $_.Exception.Message
    Write-Host "gate job: $message"
    [IO.File]::WriteAllText($errorFile, $message, [Text.UTF8Encoding]::new($false))
    $code = 3
}
finally {
    try { Stop-Transcript | Out-Null } catch {}
}
exit $code
