#!/usr/bin/env bash
# Dual-instance network harness orchestrator — issue #212 (BehindTheBack
# broadcast-driven shielded sweep).
#
# Launches a server and a client of NetBehindTheBackSweepTest.tscn. The server
# boots the SHIPPED NetworkManager.HostGame listen-server path (so it owns a
# player node named "1" — the holder); the client joins via JoinGame. The
# server drives a real BehindTheBack on its own player; the client passes only
# once its LOCAL ball render flags a live behind-body sweep with a negative
# forward offset AND the remote holder's DisplayMoveId() returns
# "behindtheback" — i.e. the _serverMoveId broadcast branch, unreachable in
# any single process. The client's exit code is the verdict.
#
# Usage:  tests/integration/run-net-behindtheback-sweep.sh [godot-binary]
# Exit: 0 = shielded sweep proven to render from broadcast state on a true
# remote client, non-zero = failed.

set -uo pipefail

GODOT="${1:-godot}"
SCENE="res://tests/integration/NetBehindTheBackSweepTest.tscn"
PORT="${HARNESS_PORT:-23459}"   # 23456/23457/23458 belong to the handshake/state-sync/node-replication harnesses
SERVER_BIND_WAIT="${SERVER_BIND_WAIT:-6}"

log() { echo "[run-net-behindtheback-sweep] $*"; }

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
  log "PASS: BehindTheBack shielded sweep rendered from broadcast state on a true remote client"
  exit 0
fi

log "FAIL: client never proved the broadcast-driven behind-body sweep (rc=$CLIENT_RC)"
exit 1
