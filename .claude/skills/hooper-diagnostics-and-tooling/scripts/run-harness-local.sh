#!/usr/bin/env bash
# run-harness-local.sh — run the full SINGLE-INSTANCE headless-harness scenario
# matrix locally, in the same order CI runs it, with a per-scenario PASS/FAIL
# summary table.
#
# The scenario list is parsed LIVE from .github/workflows/ci.yml (every
# `godot --headless --path . res://tests/integration/*.tscn` invocation in the
# integration-test job), so it cannot drift from CI. Dual-instance scenarios
# (the run-net-*.sh orchestrators) are NOT run here — run those individually:
#   bash tests/integration/run-net-handshake.sh "$GODOT"   # etc.
#
# Usage:
#   bash run-harness-local.sh <path-to-godot-binary> [--stop-on-fail] [--list]
#   GODOT=<path> bash run-harness-local.sh [--stop-on-fail] [--list]
#
#   $1 / $GODOT     Godot 4.6.3 .NET binary (on Windows use the *_console.exe
#                   variant so [harness] output reaches the terminal).
#   --stop-on-fail  abort at the first failing scenario (default: run all,
#                   report everything, exit non-zero if anything failed).
#   --list          print the parsed scenario matrix and exit (no binary needed).
#
# Exit codes: 0 = all scenarios passed, 1 = at least one failed (or aborted),
#             2 = usage / environment error.
#
# As of 2026-07-15 the parsed matrix is 30 scenario invocations across 10
# scenes. If the parse ever returns 0 rows, ci.yml's invocation shape changed —
# update the grep pattern below to match.

set -u

STOP_ON_FAIL=0
LIST_ONLY=0
GODOT_BIN="${GODOT:-}"

for arg in "$@"; do
  case "$arg" in
    --stop-on-fail) STOP_ON_FAIL=1 ;;
    --list)         LIST_ONLY=1 ;;
    -h|--help)      grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *)              GODOT_BIN="$arg" ;;
  esac
done

# Repo root = four levels above this script (.claude/skills/<skill>/scripts/).
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../../.." && pwd)"
CI_YML="$REPO_ROOT/.github/workflows/ci.yml"

if [ ! -f "$CI_YML" ]; then
  echo "ERROR: cannot find ci.yml at $CI_YML" >&2
  exit 2
fi

# Parse the single-instance matrix out of ci.yml, preserving CI order.
# Matches both `run: godot --headless ...` one-liners and bare lines inside
# multi-line `run: |` blocks. Strips leading whitespace and any `run: ` prefix.
mapfile -t INVOCATIONS < <(
  grep -E 'godot --headless --path \. res://tests/integration/' "$CI_YML" \
    | sed -e 's/^[[:space:]]*//' -e 's/^run:[[:space:]]*//'
)

if [ "${#INVOCATIONS[@]}" -eq 0 ]; then
  echo "ERROR: parsed 0 scenario invocations from ci.yml — the workflow's" >&2
  echo "invocation shape changed; update this script's grep pattern." >&2
  exit 2
fi

if [ "$LIST_ONLY" -eq 1 ]; then
  echo "Parsed ${#INVOCATIONS[@]} single-instance scenario invocations (CI order):"
  printf '  %s\n' "${INVOCATIONS[@]}"
  exit 0
fi

if [ -z "$GODOT_BIN" ]; then
  echo "ERROR: no Godot binary given. Pass it as \$1 or set \$GODOT." >&2
  echo "  e.g.  GODOT=/path/to/Godot_v4.6.3-stable_mono_win64_console.exe bash $0" >&2
  exit 2
fi
if ! command -v "$GODOT_BIN" >/dev/null 2>&1 && [ ! -x "$GODOT_BIN" ]; then
  echo "ERROR: Godot binary not found or not executable: $GODOT_BIN" >&2
  exit 2
fi

cd "$REPO_ROOT" || exit 2

echo "Godot binary : $GODOT_BIN"
echo "Repo root    : $REPO_ROOT"
echo "Scenarios    : ${#INVOCATIONS[@]} (parsed from ci.yml, CI order)"
echo

declare -a LABELS RESULTS CODES
FAILED=0

for inv in "${INVOCATIONS[@]}"; do
  # Label = scene name + scenario suffix, e.g. "StealTurnoverTest / success".
  scene="$(echo "$inv" | sed -E 's|.*res://tests/integration/([A-Za-z0-9]+)\.tscn.*|\1|')"
  scenario="$(echo "$inv" | sed -n 's/.*--harness-scenario=\([a-z0-9-]*\).*/\1/p')"
  label="$scene${scenario:+ / $scenario}"

  # Rebuild the command with OUR binary instead of the literal `godot`.
  args="${inv#godot }"
  echo "=== RUN  $label"
  # shellcheck disable=SC2086 — args are a trusted, parsed ci.yml fragment.
  "$GODOT_BIN" $args
  rc=$?
  LABELS+=("$label"); CODES+=("$rc")
  if [ "$rc" -eq 0 ]; then
    RESULTS+=("PASS")
    echo "=== PASS $label"
  else
    RESULTS+=("FAIL")
    FAILED=1
    echo "=== FAIL $label (exit $rc — 1=assertion fail, anything else=harness crash)"
    if [ "$STOP_ON_FAIL" -eq 1 ]; then
      echo; echo "--stop-on-fail: aborting after first failure."
      break
    fi
  fi
  echo
done

echo "==================== SUMMARY ===================="
printf '%-6s %-4s  %s\n' "RESULT" "EXIT" "SCENARIO"
for i in "${!LABELS[@]}"; do
  printf '%-6s %-4s  %s\n' "${RESULTS[$i]}" "${CODES[$i]}" "${LABELS[$i]}"
done
echo "=================================================="
if [ "$FAILED" -eq 0 ] && [ "${#LABELS[@]}" -eq "${#INVOCATIONS[@]}" ]; then
  echo "ALL ${#LABELS[@]} SCENARIOS PASSED"
  exit 0
fi
echo "FAILURES PRESENT (ran ${#LABELS[@]}/${#INVOCATIONS[@]})"
exit 1
