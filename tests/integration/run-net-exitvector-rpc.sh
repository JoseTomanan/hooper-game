#!/usr/bin/env bash
# Dual-instance network harness orchestrator — issue #210 (exit-vector RPC
# divergence fix).
#
# Launches a server and a client of NetExitVectorRpcTest.tscn for a given
# scenario. The server boots the SHIPPED NetworkManager.HostGame listen-server
# path; the client joins via JoinGame and becomes player "2" — a REMOTE player
# from the server's perspective, which is exactly the role TickServerRemotePlayer
# drives. The client presses REAL right-stick input to commit a genuine
# Crossover through the unmodified production dispatch (SampleMoveInput), then
# holds a real, distinct left-stick direction as its exit vector — no harness
# seam stands in for either the move-begin or the exit-vector RPC send.
#
# Deliberate deviation from this repo's other dual-instance scripts: the
# SERVER's exit code is the verdict here, not the client's. The fact under
# test — did the SERVER's own authoritative composition use the CORRECT exit
# vector — is server-side ground truth; the client's own player immediately
# re-predicts its OWN burst from its OWN live input on its OWN
# JustEnteredActive tick, which would mask whatever the server actually used.
# See NetExitVectorRpcTest.cs's class doc for the full reasoning.
#
# Usage:  tests/integration/run-net-exitvector-rpc.sh [godot-binary] [scenario]
#   scenario defaults to "poisoned"; pass "steady" for the paired control.
# Exit: 0 = the server's composed velocity matched the TRUE-exit-vector
# oracle, non-zero = diverged or timed out.

set -uo pipefail

GODOT="${1:-godot}"
SCENARIO="${2:-poisoned}"
SCENE="res://tests/integration/NetExitVectorRpcTest.tscn"
PORT="${HARNESS_PORT:-23461}"   # 23456-23460 belong to the handshake/state-sync/
                                 # node-replication/behindtheback/defensive-telegraph harnesses
SERVER_BIND_WAIT="${SERVER_BIND_WAIT:-6}"

log() { echo "[run-net-exitvector-rpc:$SCENARIO] $*"; }

log "godot=$GODOT scene=$SCENE port=$PORT"

"$GODOT" --headless --path . "$SCENE" -- --harness-role=server --harness-port="$PORT" --harness-scenario="$SCENARIO" &
SERVER_PID=$!
log "server launched (pid $SERVER_PID); waiting ${SERVER_BIND_WAIT}s to bind…"

CLIENT_PID=""
cleanup() {
  if [ -n "$CLIENT_PID" ] && kill -0 "$CLIENT_PID" 2>/dev/null; then
    kill "$CLIENT_PID" 2>/dev/null
    wait "$CLIENT_PID" 2>/dev/null
  fi
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

log "launching client (backgrounded — server carries the verdict)…"
"$GODOT" --headless --path . "$SCENE" -- --harness-role=client --harness-port="$PORT" --harness-scenario="$SCENARIO" &
CLIENT_PID=$!

wait "$SERVER_PID"
SERVER_RC=$?

log "server exit code: $SERVER_RC"

if [ "$SERVER_RC" -eq 0 ]; then
  log "PASS: scenario=$SCENARIO proven — server composed the TRUE exit vector"
  exit 0
fi

log "FAIL: scenario=$SCENARIO — server never proved the TRUE exit vector composition (rc=$SERVER_RC)"
exit 1
