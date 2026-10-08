#!/usr/bin/env bash
# #367: two production Main/Player.tscn peers, default rig parity and actual
# remote defensive AnimationTree nodes. Both peers must exit successfully.
set -uo pipefail

GODOT="${1:-godot}"
SCENARIO="${2:-left}"
SCENE="res://tests/integration/NetRigPresentationTest.tscn"
PORT="${HARNESS_PORT:-23462}"
SERVER_BIND_WAIT="${SERVER_BIND_WAIT:-6}"
LOG_DIR=".godot/harness-logs"
case "$SCENARIO" in left|right) ;; *) echo "invalid scenario: $SCENARIO" >&2; exit 2 ;; esac
mkdir -p "$LOG_DIR" || { echo "[run-net-rig:$SCENARIO] cannot create $LOG_DIR" >&2; exit 2; }
case "$(uname -s)" in
  MINGW*|MSYS*) GODOT_LOG_DIR="$(pwd -W)/$LOG_DIR" ;;
  *) GODOT_LOG_DIR="$PWD/$LOG_DIR" ;;
esac
log() { echo "[run-net-rig:$SCENARIO] $*"; }

SERVER_PID=""
CLIENT_PID=""
cleanup() {
  for task_pid in "$CLIENT_PID" "$SERVER_PID"; do
    if [ -n "$task_pid" ] && kill -0 "$task_pid" 2>/dev/null; then
      kill "$task_pid" 2>/dev/null || true
      wait "$task_pid" 2>/dev/null || true
    fi
  done
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

log "godot=$GODOT scene=$SCENE port=$PORT"
# Separate, workspace-local native logs prevent restricted user:// writes and
# preserve both peers' evidence. PID suffix avoids collisions between runs.
"$GODOT" --log-file "$GODOT_LOG_DIR/net-rig-presentation-$SCENARIO-$$-server.log" --headless --path . "$SCENE" -- --harness-role=server --harness-port="$PORT" --harness-scenario="$SCENARIO" &
SERVER_PID=$!
sleep "$SERVER_BIND_WAIT"
if ! kill -0 "$SERVER_PID" 2>/dev/null; then
  log "FAIL: server exited during bind wait"
  exit 1
fi
"$GODOT" --log-file "$GODOT_LOG_DIR/net-rig-presentation-$SCENARIO-$$-client.log" --headless --path . "$SCENE" -- --harness-role=client --harness-port="$PORT" --harness-scenario="$SCENARIO" &
CLIENT_PID=$!
wait "$CLIENT_PID"
CLIENT_RC=$?
log "client exit code: $CLIENT_RC"
if [ "$CLIENT_RC" -eq 0 ]; then
  # The server exits after its final proof/reliable reply flush, or its bounded
  # engine timeout. A late server assertion failure must invalidate client green.
  wait "$SERVER_PID"
  SERVER_RC=$?
  log "server exit code: $SERVER_RC"
  if [ "$SERVER_RC" -eq 0 ]; then
    log "PASS: both production peers proved rig scenario=$SCENARIO"
    exit 0
  fi
  log "FAIL: server proof scenario=$SCENARIO (rc=$SERVER_RC)"
  exit 1
fi
log "FAIL: production remote rig scenario=$SCENARIO (rc=$CLIENT_RC)"
exit 1
