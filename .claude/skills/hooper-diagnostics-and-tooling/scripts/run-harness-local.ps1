# run-harness-local.ps1 — Windows PowerShell 5.1-compatible equivalent of
# run-harness-local.sh: run the full SINGLE-INSTANCE headless-harness scenario
# matrix locally, in CI order, with a per-scenario PASS/FAIL summary table.
#
# The scenario list is parsed LIVE from .github/workflows/ci.yml, so it cannot
# drift from CI. Dual-instance scenarios (run-net-*.sh) are bash orchestrators —
# run those via Git Bash, not this script.
#
# Usage:
#   powershell -File run-harness-local.ps1 -Godot "C:\...\Godot_v4.6.3-stable_mono_win64_console.exe"
#   powershell -File run-harness-local.ps1 -Godot $env:GODOT -StopOnFail
#   powershell -File run-harness-local.ps1 -List          # print matrix, no binary needed
#
#   -Godot       path to the Godot 4.6.3 .NET binary (falls back to $env:GODOT).
#                Use the *_console.exe variant so [harness] output is visible.
#   -StopOnFail  abort at the first failing scenario (default: run all).
#   -List        print the parsed scenario matrix and exit.
#
# Exit codes: 0 = all passed, 1 = at least one failed, 2 = usage/env error.
#
# PowerShell 5.1 constraints honoured: no && / || chaining, no ternary.
# As of 2026-07-15 the parsed matrix is 30 scenario invocations across 10 scenes.

param(
    [string]$Godot = $env:GODOT,
    [switch]$StopOnFail,
    [switch]$List
)

$ErrorActionPreference = 'Continue'

# Repo root = four levels above this script (.claude/skills/<skill>/scripts/).
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = (Resolve-Path (Join-Path $ScriptDir '..\..\..\..')).Path
$CiYml     = Join-Path $RepoRoot '.github\workflows\ci.yml'

if (-not (Test-Path $CiYml)) {
    Write-Error "cannot find ci.yml at $CiYml"
    exit 2
}

# Parse the single-instance matrix out of ci.yml, preserving CI order.
$pattern = 'godot --headless --path \. res://tests/integration/'
$invocations = @(
    Select-String -Path $CiYml -Pattern $pattern | ForEach-Object {
        $_.Line.Trim() -replace '^run:\s*', ''
    }
)

if ($invocations.Count -eq 0) {
    Write-Error "parsed 0 scenario invocations from ci.yml - the workflow's invocation shape changed; update this script's pattern."
    exit 2
}

if ($List) {
    Write-Output ("Parsed {0} single-instance scenario invocations (CI order):" -f $invocations.Count)
    $invocations | ForEach-Object { Write-Output "  $_" }
    exit 0
}

if ([string]::IsNullOrWhiteSpace($Godot)) {
    Write-Error "no Godot binary given. Pass -Godot <path> or set `$env:GODOT."
    exit 2
}
if (-not (Test-Path $Godot)) {
    Write-Error "Godot binary not found: $Godot"
    exit 2
}

Set-Location $RepoRoot

Write-Output "Godot binary : $Godot"
Write-Output "Repo root    : $RepoRoot"
Write-Output ("Scenarios    : {0} (parsed from ci.yml, CI order)" -f $invocations.Count)
Write-Output ""

$results = @()
$anyFailed = $false

foreach ($inv in $invocations) {
    # Label = scene name + scenario suffix, e.g. "StealTurnoverTest / success".
    $scene = $inv -replace '.*res://tests/integration/([A-Za-z0-9]+)\.tscn.*', '$1'
    $scenario = ''
    if ($inv -match '--harness-scenario=([a-z0-9-]+)') { $scenario = $Matches[1] }
    if ($scenario -ne '') { $label = "$scene / $scenario" } else { $label = $scene }

    # Rebuild the argument vector with OUR binary instead of the literal `godot`.
    $argString = $inv.Substring('godot '.Length)
    $argv = $argString -split ' '

    Write-Output "=== RUN  $label"
    & $Godot @argv
    $rc = $LASTEXITCODE

    if ($rc -eq 0) {
        $verdict = 'PASS'
        Write-Output "=== PASS $label"
    } else {
        $verdict = 'FAIL'
        $anyFailed = $true
        Write-Output "=== FAIL $label (exit $rc - 1=assertion fail, anything else=harness crash)"
    }
    $results += New-Object PSObject -Property @{ Result = $verdict; Exit = $rc; Scenario = $label }

    if ($anyFailed -and $StopOnFail) {
        Write-Output ""
        Write-Output "-StopOnFail: aborting after first failure."
        break
    }
    Write-Output ""
}

Write-Output "==================== SUMMARY ===================="
$results | Format-Table -Property Result, Exit, Scenario -AutoSize | Out-String | Write-Output
Write-Output "=================================================="
if (-not $anyFailed -and $results.Count -eq $invocations.Count) {
    Write-Output ("ALL {0} SCENARIOS PASSED" -f $results.Count)
    exit 0
}
Write-Output ("FAILURES PRESENT (ran {0}/{1})" -f $results.Count, $invocations.Count)
exit 1
