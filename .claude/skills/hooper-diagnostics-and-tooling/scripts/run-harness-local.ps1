# PowerShell 5.1-compatible thin entry point. The Python catalog owns all logic.
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir '..\..\..\..')).Path
$Catalog = Join-Path $RepoRoot 'tools\harness_catalog.py'
$Python = Get-Command python -ErrorAction SilentlyContinue
if ($null -eq $Python) { $Python = Get-Command python3 -ErrorAction SilentlyContinue }
if ($null -eq $Python) { Write-Error 'python3 or python is required'; exit 2 }
& $Python.Source $Catalog @args
exit $LASTEXITCODE
