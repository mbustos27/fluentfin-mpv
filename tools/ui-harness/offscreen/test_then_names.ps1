<#
  Tests that the scripts drive.ps1 dot-sources (-Then and -Before scripts: then-*.ps1, before-*.ps1, and states-common.ps1,
  which those dot-source in turn) never assign one of drive.ps1's parameters at their top level. Dot-sourced, their top level
  is drive.ps1's own scope, so `$before = ...` writes drive.ps1's [string]$Before: an array of names became one string, and
  a number one that `+ 0.5` appended to (1.2.0 merge: the states stream's -Before parameter and the library's
  then-continue.ps1). Variables inside a function or a { } script block are their own and are not checked. Parses only,
  starts nothing.

    powershell -NoProfile -ExecutionPolicy Bypass -File tools\ui-harness\offscreen\test_then_names.ps1

  Prints PASS/FAIL per script; exit code 1 when any failed. -Dir <folder> checks another copy of offscreen\.
#>
param([string]$Dir)
$ErrorActionPreference = 'Stop'
if (-not $Dir) { $Dir = $PSScriptRoot }
$Language = [System.Management.Automation.Language.Parser]

function Parse([string]$Path) {
    $tokens = $null; $errors = $null
    $ast = $Language::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if ($errors) { throw "${Path}: does not parse: $($errors[0].Message)" }
    $ast
}

# the variables an assignment (or a foreach) binds in the scope it runs in
function Bound([System.Management.Automation.Language.Ast]$Node) {
    $targets = switch ($Node) {
        { $_ -is [System.Management.Automation.Language.AssignmentStatementAst] } { @($_.Left) }
        { $_ -is [System.Management.Automation.Language.ForEachStatementAst] } { @($_.Variable) }
        default { @() }
    }
    foreach ($t in $targets) {
        if ($t -is [System.Management.Automation.Language.ConvertExpressionAst]) { $t = $t.Child }
        $vars = if ($t -is [System.Management.Automation.Language.ArrayLiteralAst]) { $t.Elements } else { @($t) }
        foreach ($v in $vars) {
            if ($v -is [System.Management.Automation.Language.ConvertExpressionAst]) { $v = $v.Child }
            if ($v -isnot [System.Management.Automation.Language.VariableExpressionAst]) { continue }   # $x.Prop, $x[0]: no new binding
            $path = $v.VariablePath
            if ($path.IsGlobal -or $path.DriveName -and $path.DriveName -ne 'script') { continue }
            $path.UserPath -replace '^script:', ''
        }
    }
}

# true when the node runs in the file's own scope: not inside a function or a script block (those get a scope of their own;
# the file's own top-level script block is the only one allowed on the way up)
function TopLevel([System.Management.Automation.Language.Ast]$Node, [System.Management.Automation.Language.Ast]$Root) {
    for ($p = $Node.Parent; $p -and $p -ne $Root; $p = $p.Parent) {
        if ($p -is [System.Management.Automation.Language.FunctionDefinitionAst]) { return $false }
        if ($p -is [System.Management.Automation.Language.ScriptBlockExpressionAst]) { return $false }
    }
    $true
}

$drive = Parse (Join-Path $Dir 'drive.ps1')
$params = @($drive.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
$failed = 0
$scripts = @(Get-ChildItem -LiteralPath $Dir -Filter '*.ps1' | Where-Object { $_.Name -like 'then-*.ps1' -or $_.Name -like 'before-*.ps1' -or $_.Name -eq 'states-common.ps1' } | Sort-Object Name)
if ($scripts.Count -eq 0 -or $params.Count -eq 0) { Write-Host "FAIL nothing to check: $($scripts.Count) scripts, $($params.Count) parameters"; exit 1 }
foreach ($file in $scripts) {
    $ast = Parse $file.FullName
    $hits = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] -or $n -is [System.Management.Automation.Language.ForEachStatementAst] }, $true) |
        Where-Object { TopLevel $_ $ast } |
        ForEach-Object { $n = $_; Bound $n | Where-Object { $params -contains $_ } | ForEach-Object { "`$$_ (line $($n.Extent.StartLineNumber))" } })
    if ($hits) { $failed++ }
    Write-Host ("{0} {1}{2}" -f $(if ($hits) { 'FAIL' } else { 'PASS' }), $file.Name, $(if ($hits) { ": assigns drive.ps1's " + ($hits -join ', ') }))
}
Write-Host ("{0} scripts, {1} parameters of drive.ps1, {2} failed" -f $scripts.Count, $params.Count, $failed)
if ($failed) { exit 1 }
