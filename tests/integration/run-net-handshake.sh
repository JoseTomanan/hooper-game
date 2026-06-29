#!/usr/bin/env bash
# Dual-instance network harness orchestrator — INCREMENT 1 (ENet handshake).
#
# Launches two headless Godot processes of the SAME scene in different roles and
# reads the CLIENT's exit code as the verdict: a client cannot report a completed
# handshake (exit 0) unless the server process bound the port and finished the
# ENet exchange. This is the two-process capability a single-instance test
# (IntegrationSmokeTest) structurally cannot cover.
#
# Usage:  tests/integration/run-net-handshake.sh [godot-binary]
#   godot-binary defaults to "godot" (CI provides it via setup-godot).
#
# Exit: 0 = handshake proven, non-zero = failed/timed out.

set -uo pipefail

GODOT="${1:-godot}"
SCENE="res://tests/integration/NetHandshakeTest.tscn"
PORT="${HARNESS_PORT:-23456}"   # high, fixed, unlikely to clash on a CI runner
SERVER_BIND_WAIT="${SERVER_BIND_WAIT:-6}"  # seconds to let the server boot + bind

log() { echo "[run-net-handshake] $*"; }

log "godot=$GODOT scene=$SCENE port=$PORT"

# 1) Launch the server in the background.
"$GODOT" --headless --path . "$SCENE" -- --harness-role=server --harness-port="$PORT" &
SERVER_PID=$!
log "server launched (pid $SERVER_PID); waiting ${SERVER_BIND_WAIT}s for it to bind…"

# Ensure the server is cleaned up no matter how we exit.
cleanup() {
  if kill -0 "$SERVER_PID" 2>/dev/null; then
    log "stopping server (pid $SERVER_PID)"
    kill "$SERVER_PID" 2>/dev/null
    wait "$SERVER_PID" 2>/dev/null
  fi
}
trap cleanup EXIT

# Give Godot's .NET cold-boot time to come up and bind the port before the client
# attempts to connect (CreateClient fails fast if nothing is listening yet).
sleep "$SERVER_BIND_WAIT"

if ! kill -0 "$SERVER_PID" 2>/dev/null; then
  log "FAIL: server process exited during bind wait"
  exit 1
fi

# 2) Run the client in the foreground; its exit code is the verdict.
log "launching client…"
"$GODOT" --headless --path . "$SCENE" -- --harness-role=client --harness-port="$PORT"
CLIENT_RC=$?

log "client exit code: $CLIENT_RC"

# 3) The server is best-effort confirmation; the client's success is the gate.
#    (The server also self-exits 0 after its linger, but races with our kill, so
#    we do not depend on its code in increment 1.)
if [ "$CLIENT_RC" -eq 0 ]; then
  log "PASS: client completed the ENet handshake against the headless server"
  exit 0
fi

log "FAIL: client did not complete the handshake (rc=$CLIENT_RC)"
exit 1
