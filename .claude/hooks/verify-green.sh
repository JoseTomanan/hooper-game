#!/usr/bin/env bash
# verify-green.sh — Stop / SubagentStop hook (ADR-0015 green gate).
#
# Why this exists
# ---------------
# ADR-0015 makes the AFK lane auto-merge on green and states the rule plainly:
# "no agent reports done on red." This hook is the LOCAL mirror of that rule —
# it runs the same two gates CI runs (game-project build + full test suite) and,
# while either is red, blocks the agent from finishing (exit 2) and feeds the
# failure tail back so the agent fixes the root cause instead of reporting done.
#
# CI remains the authoritative gate the orchestrator merges on; this is cheap
# belt-and-suspenders so a worker can't *claim* done over a broken tree.
#
# Loop bound: a hook that blocks on red can spin forever if the agent genuinely
# cannot fix the failure. We respect Claude Code's `stop_hook_active` re-entry
# signal and a small consecutive-failure counter: after MAX_ATTEMPTS red stops
# we SURFACE loudly and allow the stop (exit 0) rather than hang. Forward
# progress + a visible failure beats an infinite loop.

set -uo pipefail

MAX_ATTEMPTS=3

PROJECT_DIR="${CLAUDE_PROJECT_DIR:-$(pwd)}"
cd "$PROJECT_DIR" 2>/dev/null || { echo "[green-gate] cannot cd to project dir; skipping" >&2; exit 0; }

# Resolve dotnet: PATH first, then the known Windows install location.
if command -v dotnet >/dev/null 2>&1; then
  DOTNET="dotnet"
elif [ -x "/c/Program Files/dotnet/dotnet.exe" ]; then
  DOTNET="/c/Program Files/dotnet/dotnet.exe"
else
  echo "[green-gate] dotnet not found on PATH or at /c/Program Files/dotnet; skipping gate" >&2
  exit 0
fi

# Read the hook's stdin JSON (best-effort; no jq dependency).
STDIN_JSON="$(cat 2>/dev/null || true)"
STOP_HOOK_ACTIVE=0
case "$STDIN_JSON" in
  *'"stop_hook_active":true'*|*'"stop_hook_active": true'*) STOP_HOOK_ACTIVE=1 ;;
esac

# Consecutive-failure counter (in an ignored path; survives across re-entries).
COUNTER_FILE="$PROJECT_DIR/.claude/.greengate-attempts"
read_count() { [ -f "$COUNTER_FILE" ] && cat "$COUNTER_FILE" 2>/dev/null || echo 0; }
reset_count() { rm -f "$COUNTER_FILE" 2>/dev/null || true; }

LOG_DIR="${TMPDIR:-/tmp}"
BUILD_LOG="$LOG_DIR/hooper-greengate-build.log"
TEST_LOG="$LOG_DIR/hooper-greengate-test.log"

fail() {
  # $1 = human label, $2 = log file to tail
  local count
  count=$(( $(read_count) + 1 ))
  echo "$count" > "$COUNTER_FILE" 2>/dev/null || true

  if [ "$count" -ge "$MAX_ATTEMPTS" ]; then
    reset_count
    echo "[green-gate] STILL RED after ${count} attempts ($1). Surfacing instead of looping — DO NOT treat this as done; the tree is broken:" >&2
    tail -n 25 "$2" 2>/dev/null >&2
    exit 0   # allow stop to avoid an infinite loop; failure is loud on stderr
  fi

  echo "[green-gate] BLOCKED ($1, attempt ${count}/${MAX_ATTEMPTS}). Fix the root cause before finishing — do not skip tests or weaken assertions:" >&2
  tail -n 25 "$2" 2>/dev/null >&2
  exit 2     # block the stop; stderr is fed back to the agent
}

# Gate 1 — the game project must build on its own compile surface.
if ! "$DOTNET" build "HOOPER GAME.csproj" --configuration Debug -nologo -v quiet > "$BUILD_LOG" 2>&1; then
  fail "game build failed" "$BUILD_LOG"
fi

# Gate 2 — the full test suite must pass.
if ! "$DOTNET" test tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj --configuration Debug -nologo -v quiet > "$TEST_LOG" 2>&1; then
  fail "tests are red" "$TEST_LOG"
fi

# Green: clear the counter and allow the stop.
reset_count
exit 0
