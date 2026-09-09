#!/usr/bin/env bash
# Production discovery-to-authoritative-movement proof for #374. The negative
# listener runs first; positive clients then share the exclusive localhost
# discovery port sequentially while remaining connected for the final duel.

set -uo pipefail

GODOT="${1:-godot}"
SCENE="res://tests/integration/DedicatedDiscoveryJoinTest.tscn"
GAME_PORT="${HARNESS_PORT:-23467}"
DISCOVERY_PORT="${HARNESS_DISCOVERY_PORT:-27786}"
ROOT_LOG_DIR=".godot/harness-logs"
ROOT_COORD_DIR=".godot/harness-coordination"
mkdir -p "$ROOT_LOG_DIR" "$ROOT_COORD_DIR" || exit 2

case "$(uname -s)" in
  MINGW*|MSYS*) REPO_NATIVE="$(pwd -W)" ;;
  *)            REPO_NATIVE="$PWD" ;;
esac

LOG_DIR="$(mktemp -d "$ROOT_LOG_DIR/dedicated-discovery-join-XXXXXX")" || exit 2
COORD_DIR="$(mktemp -d "$ROOT_COORD_DIR/dedicated-discovery-join-XXXXXX")" || exit 2
case "$(uname -s)" in
  MINGW*|MSYS*) COORD_ARG="$(cd "$COORD_DIR" && pwd -W)" ;;
  *)            COORD_ARG="$PWD/$COORD_DIR" ;;
esac

log() { echo "[dedicated-discovery] $*"; }

pids=()
process_alive() {
  local pid="$1" state
  kill -0 "$pid" 2>/dev/null || return 1
  state="$(ps -o stat= -p "$pid" 2>/dev/null || true)"
  [[ "$state" != Z* ]]
}

terminate_pid() {
  local pid="$1" started=$SECONDS
  process_alive "$pid" || { wait "$pid" 2>/dev/null || true; return; }
  kill "$pid" 2>/dev/null || true
  while process_alive "$pid"; do
    if [ $((SECONDS - started)) -ge 3 ]; then
      kill -KILL "$pid" 2>/dev/null || true
      break
    fi
    sleep 0.1
  done
  wait "$pid" 2>/dev/null || true
}

cleanup() {
  local pid
  for pid in "${pids[@]}"; do terminate_pid "$pid"; done
}
trap cleanup EXIT INT TERM

dump_failure() {
  local file
  log "failure artifacts preserved: logs=$LOG_DIR coordination=$COORD_DIR"
  find "$COORD_DIR" -maxdepth 1 -type f -print -exec sed -n '1,3p' {} \; 2>/dev/null || true
  for file in "$LOG_DIR"/*.log; do
    [ -f "$file" ] || continue
    echo "----- tail: $file -----"
    tail -n 45 "$file"
  done
}

wait_for_file() {
  local name="$1" timeout="$2" watched_pid="$3" started=$SECONDS
  while [ ! -f "$COORD_DIR/$name" ]; do
    if ! process_alive "$watched_pid"; then
      log "FAIL: process $watched_pid exited before '$name'"
      return 1
    fi
    if [ $((SECONDS - started)) -ge "$timeout" ]; then
      log "FAIL: timed out waiting for '$name'"
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
      log "FAIL: $role exceeded ${timeout}s watchdog"
      terminate_pid "$pid"
      return 124
    fi
    sleep 0.2
  done
  wait "$pid"
}

launch_role() {
  local role="$1"
  shift
  "$GODOT" --log-file "$REPO_NATIVE/$LOG_DIR/$role.log" --headless --path . "$SCENE" -- \
    --harness-role="$role" \
    --harness-port="$GAME_PORT" \
    --harness-discovery-port="$DISCOVERY_PORT" \
    --harness-coordination-dir="$COORD_ARG" \
    "$@" >"$LOG_DIR/$role.console.log" 2>&1 &
  LAST_PID=$!
  pids+=("$LAST_PID")
  log "launched $role pid=$LAST_PID"
}

server_rc=0 negative_rc=0 client_a_rc=0 client_b_rc=0

launch_role server --dedicated --port="$GAME_PORT"
server_pid=$LAST_PID
if ! wait_for_file server-ready 20 "$server_pid"; then dump_failure; exit 1; fi

# The missing-beacon control occupies only the mismatched port. It exits before
# the positive clients start, leaving the expected discovery port uncontended.
launch_role negative
negative_pid=$LAST_PID
wait_for_pid "$negative_pid" negative 20 || negative_rc=$?
if [ "$negative_rc" -ne 0 ]; then
  log "FAIL: negative control exit=$negative_rc"
  dump_failure
  exit 1
fi

launch_role client-a
client_a_pid=$LAST_PID
# A must have joined (not merely emitted activation) before B reuses the same
# exclusive listener port, fixing both spawn-seat and socket ordering.
if ! wait_for_file client-a-joined 20 "$client_a_pid" \
  || ! wait_for_file server-a-ready 20 "$server_pid"; then
  dump_failure
  exit 1
fi

launch_role client-b
client_b_pid=$LAST_PID

wait_for_pid "$client_a_pid" client-a 45 || client_a_rc=$?
wait_for_pid "$client_b_pid" client-b 20 || client_b_rc=$?
wait_for_pid "$server_pid" server 20 || server_rc=$?

if [ "$server_rc" -ne 0 ] || [ "$negative_rc" -ne 0 ] || [ "$client_a_rc" -ne 0 ] || [ "$client_b_rc" -ne 0 ]; then
  log "FAIL: exits server=$server_rc negative=$negative_rc client-a=$client_a_rc client-b=$client_b_rc"
  dump_failure
  exit 1
fi

log "RESULT: PASS — production beacon, exact row activation, dedicated roster, and authoritative separation"
