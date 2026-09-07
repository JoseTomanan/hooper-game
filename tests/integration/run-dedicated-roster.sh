#!/usr/bin/env bash
# Production topology proof for #372. Runs an honest three-process dedicated
# server plus two sequential remote clients, then an isolated listen-server
# control. Every role instances Main.tscn through DedicatedRosterTest.tscn.

set -uo pipefail

GODOT="${1:-godot}"
SCENE="res://tests/integration/DedicatedRosterTest.tscn"
BASE_PORT="${HARNESS_PORT:-23465}"
BASE_DISCOVERY_PORT="${HARNESS_DISCOVERY_PORT:-27784}"
ROOT_LOG_DIR=".godot/harness-logs"
ROOT_COORD_DIR=".godot/harness-coordination"
mkdir -p "$ROOT_LOG_DIR" "$ROOT_COORD_DIR" || exit 2

case "$(uname -s)" in
  MINGW*|MSYS*) REPO_NATIVE="$(pwd -W)" ;;
  *)            REPO_NATIVE="$PWD" ;;
esac

log() { echo "[dedicated-roster] $*"; }

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
  local log_dir="$1" coord_dir="$2" file
  log "failure artifacts preserved: logs=$log_dir coordination=$coord_dir"
  find "$coord_dir" -maxdepth 1 -type f -print -exec sed -n '1,2p' {} \; 2>/dev/null || true
  for file in "$log_dir"/*.log; do
    [ -f "$file" ] || continue
    echo "----- tail: $file -----"
    tail -n 35 "$file"
  done
}

process_alive() {
  local pid="$1" state
  kill -0 "$pid" 2>/dev/null || return 1
  state="$(ps -o stat= -p "$pid" 2>/dev/null || true)"
  [[ "$state" != Z* ]]
}

wait_for_file() {
  local coord_dir="$1" name="$2" timeout="$3" watched_pid="$4" started=$SECONDS
  while [ ! -f "$coord_dir/$name" ]; do
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
      kill "$pid" 2>/dev/null || true
      wait "$pid" 2>/dev/null || true
      return 124
    fi
    sleep 0.2
  done
  wait "$pid"
}

launch_role() {
  local scenario="$1" role="$2" port="$3" discovery_port="$4" log_dir="$5" coord_arg="$6"
  shift 6
  "$GODOT" --log-file "$REPO_NATIVE/$log_dir/$role.log" --headless --path . "$SCENE" -- \
    --harness-scenario="$scenario" \
    --harness-role="$role" \
    --harness-port="$port" \
    --harness-discovery-port="$discovery_port" \
    --harness-coordination-dir="$coord_arg" \
    "$@" >"$log_dir/$role.console.log" 2>&1 &
  LAST_PID=$!
  pids+=("$LAST_PID")
  log "launched $scenario/$role pid=$LAST_PID"
}

run_dedicated() {
  local port="$BASE_PORT" discovery_port="$BASE_DISCOVERY_PORT"
  local log_dir coord_dir coord_arg server_pid client_a_pid client_b_pid
  local server_rc=0 client_a_rc=0 client_b_rc=0
  log_dir="$(mktemp -d "$ROOT_LOG_DIR/dedicated-roster-dedicated-XXXXXX")" || return 2
  coord_dir="$(mktemp -d "$ROOT_COORD_DIR/dedicated-roster-dedicated-XXXXXX")" || return 2
  case "$(uname -s)" in
    MINGW*|MSYS*) coord_arg="$(cd "$coord_dir" && pwd -W)" ;;
    *)            coord_arg="$PWD/$coord_dir" ;;
  esac

  launch_role dedicated server "$port" "$discovery_port" "$log_dir" "$coord_arg" --dedicated --port="$port"
  server_pid=$LAST_PID
  if ! wait_for_file "$coord_dir" server-ready 20 "$server_pid"; then dump_failure "$log_dir" "$coord_dir"; return 1; fi

  launch_role dedicated client-a "$port" "$discovery_port" "$log_dir" "$coord_arg"
  client_a_pid=$LAST_PID
  if ! wait_for_file "$coord_dir" client-a-ready 45 "$client_a_pid" \
    || ! wait_for_file "$coord_dir" server-a-ready 10 "$server_pid"; then
    dump_failure "$log_dir" "$coord_dir"; return 1
  fi

  # B cannot race A for either the first spawn or the exclusive discovery port.
  launch_role dedicated client-b "$port" "$discovery_port" "$log_dir" "$coord_arg"
  client_b_pid=$LAST_PID

  wait_for_pid "$client_b_pid" dedicated/client-b 45 || client_b_rc=$?
  wait_for_pid "$server_pid" dedicated/server 20 || server_rc=$?
  wait_for_pid "$client_a_pid" dedicated/client-a 20 || client_a_rc=$?
  if [ "$server_rc" -ne 0 ] || [ "$client_a_rc" -ne 0 ] || [ "$client_b_rc" -ne 0 ]; then
    log "FAIL: dedicated exits server=$server_rc client-a=$client_a_rc client-b=$client_b_rc"
    dump_failure "$log_dir" "$coord_dir"
    return 1
  fi
  log "PASS: dedicated server and both sequential remote clients proved topology and disconnect shrink"
}

run_listen() {
  local port=$((BASE_PORT + 1)) discovery_port=$((BASE_DISCOVERY_PORT + 1))
  local log_dir coord_dir coord_arg host_pid client_pid host_rc=0 client_rc=0
  log_dir="$(mktemp -d "$ROOT_LOG_DIR/dedicated-roster-listen-XXXXXX")" || return 2
  coord_dir="$(mktemp -d "$ROOT_COORD_DIR/dedicated-roster-listen-XXXXXX")" || return 2
  case "$(uname -s)" in
    MINGW*|MSYS*) coord_arg="$(cd "$coord_dir" && pwd -W)" ;;
    *)            coord_arg="$PWD/$coord_dir" ;;
  esac

  launch_role listen host "$port" "$discovery_port" "$log_dir" "$coord_arg"
  host_pid=$LAST_PID
  if ! wait_for_file "$coord_dir" host-ready 20 "$host_pid"; then dump_failure "$log_dir" "$coord_dir"; return 1; fi

  launch_role listen client "$port" "$discovery_port" "$log_dir" "$coord_arg"
  client_pid=$LAST_PID
  wait_for_pid "$client_pid" listen/client 35 || client_rc=$?
  wait_for_pid "$host_pid" listen/host 15 || host_rc=$?
  if [ "$host_rc" -ne 0 ] || [ "$client_rc" -ne 0 ]; then
    log "FAIL: listen exits host=$host_rc client=$client_rc"
    dump_failure "$log_dir" "$coord_dir"
    return 1
  fi
  log "PASS: listen-server control proved listener premise, peer-1 roster, spawns, and HUD identity"
}

if ! run_dedicated; then exit 1; fi
pids=()
if ! run_listen; then exit 1; fi
log "RESULT: PASS (dedicated + listen topology)"
