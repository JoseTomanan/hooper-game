#!/usr/bin/env bash
# Dual-instance network harness orchestrator — INCREMENT 2 (authoritative state sync).
#
# Launches a server and a client of NetStateSyncTest.tscn. The server (multiplayer
# authority) broadcasts a strictly-increasing authoritative tick to the client
# every physics frame via RPC — the same per-tick ReceiveState cadence the real
# sim uses (ADR-0002). The client's exit code is the verdict: it passes only after
# receiving a sustained, in-order run of those authoritative updates. A client that
# merely handshook but received no broadcast would time out.
#
# Usage:  tests/integration/run-net-state-sync.sh [godot-binary]
# Exit: 0 = authoritative state proven to reach the client, non-zero = failed.

set -uo pipefail

GODOT="${1:-godot}"
SCENE="res://tests/integration/NetStateSyncTest.tscn"
PORT="${HARNESS_PORT:-23457}"   # distinct from the handshake harness port
SERVER_BIND_WAIT="${SERVER_BIND_WAIT:-6}"

log() { echo "[run-net-state-sync] $*"; }

log "godot=$GODOT scene=$SCENE port=$PORT"

"$GODOT" --headless --path . "$SCENE" -- --harness-role=server --harness-port="$PORT" &
SERVER_PID=$!
log "server launched (pid $SERVER_PID); waiting ${SERVER_BIND_WAIT}s to bind…"

cleanup() {
  if kill -0 "$SERVER_PID" 2>/dev/null; then
    log "stopping server (pid $SERVER_PID)"
    kill "$SERVER_PID" 2>/dev/null
    wait "$SERVER_PID" 2>/dev/null
  fi
}
trap cleanup EXIT

sleep "$SERVER_BIND_WAIT"

if ! kill -0 "$SERVER_PID" 2>/dev/null; then
  log "FAIL: server process exited during bind wait"
  exit 1
fi

log "launching client…"
"$GODOT" --headless --path . "$SCENE" -- --harness-role=client --harness-port="$PORT"
CLIENT_RC=$?

log "client exit code: $CLIENT_RC"

if [ "$CLIENT_RC" -eq 0 ]; then
  log "PASS: client received the authoritative state stream from the headless server"
  exit 0
fi

log "FAIL: client did not receive the authoritative stream (rc=$CLIENT_RC)"
exit 1
