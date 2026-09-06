#!/usr/bin/env bash
# Three-process dedicated-server/browser/full-game proof for #375.
#
# The test artifact is DedicatedGameJourneyTest.tscn. Each role instances the
# shipped Main.tscn; the server is launched with production --dedicated/--port
# arguments, and both clients activate production ServerBrowser rows. Detached
# scene injection changes only same-host socket addressing/ports and the named
# mutation seam; gameplay is driven through Input.ActionPress/Release.
#
# Scenarios:
#   healthy              — two browser clients play the production target of 5
#   discovery-disabled   — a positively-bound mismatched listener sees no row
#   score-rpc-disabled   — baseline score RPC works, then only that RPC is muted

set -uo pipefail

GODOT="${1:-godot}"
SCENARIO="${2:-healthy}"
SCENE="res://tests/integration/DedicatedGameJourneyTest.tscn"

case "$SCENARIO" in
  healthy)            PORT="${HARNESS_PORT:-23462}"; DISCOVERY_PORT="${HARNESS_DISCOVERY_PORT:-27780}" ;;
  discovery-disabled) PORT="${HARNESS_PORT:-23463}"; DISCOVERY_PORT="${HARNESS_DISCOVERY_PORT:-27781}" ;;
  score-rpc-disabled) PORT="${HARNESS_PORT:-23464}"; DISCOVERY_PORT="${HARNESS_DISCOVERY_PORT:-27783}" ;;
  *) echo "[dedicated-game:$SCENARIO] FAIL: unknown scenario" >&2; exit 2 ;;
esac

mkdir -p .godot/harness-logs || exit 2
LOG_ROOT="$(mktemp -d ".godot/harness-logs/dedicated-game-$SCENARIO-XXXXXX")" || exit 2
COORD_ROOT="$(mktemp -d ".godot/dedicated-game-$SCENARIO-XXXXXX")" || exit 2

case "$(uname -s)" in
  MINGW*|MSYS*)
    LOG_ARG="$(cd "$LOG_ROOT" && pwd -W)"
    COORD_ARG="$(cd "$COORD_ROOT" && pwd -W)"
    ;;
  *)
    LOG_ARG="$PWD/$LOG_ROOT"
    COORD_ARG="$PWD/$COORD_ROOT"
    ;;
esac

log() { echo "[dedicated-game:$SCENARIO] $*"; }

pids=()
cleanup() {
  local pid
  for pid in "${pids[@]}"; do
    if kill -0 "$pid" 2>/dev/null; then kill "$pid" 2>/dev/null || true; fi
  done
  for pid in "${pids[@]}"; do wait "$pid" 2>/dev/null || true; done
}
trap cleanup EXIT INT TERM

dump_failure() {
  log "failure artifacts preserved: logs=$LOG_ROOT coordination=$COORD_ROOT"
  find "$COORD_ROOT" -maxdepth 1 -type f -print -exec sed -n '1,3p' {} \; 2>/dev/null || true
  local file
  for file in "$LOG_ROOT"/*.log; do
    [ -f "$file" ] || continue
    echo "----- tail: $file -----"
    tail -n 30 "$file"
  done
}

wait_for_file() {
  local file="$1" timeout="$2" started=$SECONDS
  while [ ! -f "$COORD_ROOT/$file" ]; do
    if [ $((SECONDS - started)) -ge "$timeout" ]; then
      log "FAIL: timed out waiting for coordination phase '$file'"
      dump_failure
      return 1
    fi
    sleep 0.1
  done
}

wait_for_pid() {
  local pid="$1" role="$2" timeout="$3" started=$SECONDS state
  while kill -0 "$pid" 2>/dev/null; do
    state="$(ps -o stat= -p "$pid" 2>/dev/null || true)"
    [[ "$state" == Z* ]] && break
    if [ $((SECONDS - started)) -ge "$timeout" ]; then
      log "FAIL: $role process exceeded ${timeout}s shell watchdog"
      kill "$pid" 2>/dev/null || true
      wait "$pid" 2>/dev/null || true
      dump_failure
      return 124
    fi
    sleep 0.2
  done
  wait "$pid"
}

launch_role() {
  local role="$1"
  shift
  "$GODOT" --log-file "$LOG_ARG/$role.log" --headless --path . "$SCENE" -- \
    --harness-role="$role" \
    --harness-scenario="$SCENARIO" \
    --harness-port="$PORT" \
    --harness-discovery-port="$DISCOVERY_PORT" \
    --harness-coordination-dir="$COORD_ARG" \
    "$@" >"$LOG_ROOT/$role.console.log" 2>&1 &
  LAST_PID=$!
  pids+=("$LAST_PID")
  log "launched $role pid=$LAST_PID"
}

log "scene=$SCENE gamePort=$PORT discoveryPort=$DISCOVERY_PORT logs=$LOG_ROOT"
launch_role server --dedicated --port="$PORT"
SERVER_PID=$LAST_PID

if ! wait_for_file server-ready 20; then exit 1; fi

launch_role client-a
CLIENT_A_PID=$LAST_PID

if [ "$SCENARIO" = "discovery-disabled" ]; then
  wait_for_pid "$CLIENT_A_PID" client-a 20
  client_rc=$?
  server_rc=0
  wait_for_pid "$SERVER_PID" server 20 || server_rc=$?
  if [ "$client_rc" -ne 0 ] || [ "$server_rc" -ne 0 ]; then dump_failure; exit 1; fi
  log "PASS: positively-bound mismatched discovery listener produced no target row"
  exit 0
fi

# UDP discovery listening is exclusive. Client A's browser activation closes
# its listener before GameReady writes this file, so only then may B bind.
if ! wait_for_file client-a-joined 25; then exit 1; fi
launch_role client-b
CLIENT_B_PID=$LAST_PID

server_rc=0; client_a_rc=0; client_b_rc=0
wait_for_pid "$SERVER_PID" server 90 || server_rc=$?
wait_for_pid "$CLIENT_A_PID" client-a 10 || client_a_rc=$?
wait_for_pid "$CLIENT_B_PID" client-b 10 || client_b_rc=$?

if [ "$server_rc" -ne 0 ] || [ "$client_a_rc" -ne 0 ] || [ "$client_b_rc" -ne 0 ]; then
  log "FAIL: server=$server_rc client-a=$client_a_rc client-b=$client_b_rc"
  dump_failure
  exit 1
fi

log "PASS: server and both browser-joined clients completed scenario=$SCENARIO"
