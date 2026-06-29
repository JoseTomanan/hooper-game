#!/usr/bin/env bash
# Dual-instance network harness orchestrator — INCREMENT 3 (real node replication).
#
# Launches a server and a client of NetNodeReplicationTest.tscn. The server boots
# through the SHIPPED NetworkManager.StartDedicatedServer (the headless
# authoritative-host path, ADR-0007 / #32); the client joins through
# NetworkManager.JoinGame. On connect, the server's SpawnPlayer creates a node
# named by the client's peer id under Players, and the sibling MultiplayerSpawner
# replicates that node's existence to the client. The client's exit code is the
# verdict: it passes only once it observes Players/<its-own-peer-id> appear.
#
# Usage:  tests/integration/run-net-node-replication.sh [godot-binary]
# Exit: 0 = server-authored node proven to replicate to the client, non-zero = failed.

set -uo pipefail

GODOT="${1:-godot}"
SCENE="res://tests/integration/NetNodeReplicationTest.tscn"
PORT="${HARNESS_PORT:-23458}"   # distinct from the handshake (23456) and state-sync (23457) harnesses
SERVER_BIND_WAIT="${SERVER_BIND_WAIT:-6}"

log() { echo "[run-net-node-replication] $*"; }

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
  log "PASS: server-authored player node replicated to the client via MultiplayerSpawner"
  exit 0
fi

log "FAIL: client never observed its replicated node (rc=$CLIENT_RC)"
exit 1
