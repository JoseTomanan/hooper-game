#!/usr/bin/env bash
# Dual-instance network harness orchestrator — issue #102 (defensive move
# telegraph + whiff-punish "beaten" cue, remote-phase display sync).
#
# Launches a server and a client of NetDefensiveTelegraphTest.tscn for a given
# scenario. The server boots the SHIPPED NetworkManager.HostGame listen-server
# path (so it owns player node "1" — the node under test); the client joins
# via JoinGame. For scenario "telegraph" the server begins a real StealMove on
# its own player 1 via the DefensiveMoveHarnessSeam (the same BeginCommittedMove
# path production input reaches); for "control" it never begins a move. The
# client passes once it has gathered the evidence its own scenario requires
# (see NetDefensiveTelegraphTest.cs's Verdict) purely from broadcast state on
# player 1's REMOTE copy. The client's exit code is the verdict.
#
# Usage:  tests/integration/run-net-defensive-telegraph.sh [godot-binary] [scenario]
#   scenario defaults to "telegraph"; pass "control" for the counterfactual.
# Exit: 0 = scenario proven on a true remote client, non-zero = failed.

set -uo pipefail

GODOT="${1:-godot}"
SCENARIO="${2:-telegraph}"
SCENE="res://tests/integration/NetDefensiveTelegraphTest.tscn"
PORT="${HARNESS_PORT:-23460}"   # 23456-23459 belong to the handshake/state-sync/node-replication/behindtheback harnesses
SERVER_BIND_WAIT="${SERVER_BIND_WAIT:-6}"

log() { echo "[run-net-defensive-telegraph:$SCENARIO] $*"; }

log "godot=$GODOT scene=$SCENE port=$PORT"

"$GODOT" --headless --path . "$SCENE" -- --harness-role=server --harness-port="$PORT" --harness-scenario="$SCENARIO" &
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
"$GODOT" --headless --path . "$SCENE" -- --harness-role=client --harness-port="$PORT" --harness-scenario="$SCENARIO"
CLIENT_RC=$?

log "client exit code: $CLIENT_RC"

if [ "$CLIENT_RC" -eq 0 ]; then
  log "PASS: scenario=$SCENARIO proven on a true remote client"
  exit 0
fi

log "FAIL: client never proved scenario=$SCENARIO (rc=$CLIENT_RC)"
exit 1
