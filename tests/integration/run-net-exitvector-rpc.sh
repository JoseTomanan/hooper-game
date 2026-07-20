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
# ── Bounded retry (issue #264) ──────────────────────────────────────────────
# This proof has a SUB-TICK timing margin and so is retried up to MAX_ATTEMPTS.
# The client fires the RequestExitVector RPC at its OWN JustEnteredActive tick;
# the server composes the burst ONE-SHOT at its OWN JustEnteredActive tick,
# only ~0.5 RTT later. On a jittery CI runner the RPC is occasionally
# delivered/polled a full physics tick late, so the server composes from the
# streamed _pendingRawStick fallback instead — which the "poisoned" scenario
# has stuffed with the exact-opposite decoy, turning that documented-and-
# accepted #210 fallback (PlayerController.cs:1620; real-play error is only a
# few degrees) into a hard fail. Retrying is SOUND here precisely because the
# two outcomes are separable: a genuine #210 regression (RPC preference
# removed) composes the decoy on EVERY attempt (a deterministic diff=18) and
# still fails all retries, whereas only the intermittent sub-tick RPC-arrival
# race is retried away. Every attempt is logged — nothing is masked silently.
#
# Usage:  tests/integration/run-net-exitvector-rpc.sh [godot-binary] [scenario]
#   scenario defaults to "poisoned"; pass "steady" for the paired control.
# Exit: 0 = the server's composed velocity matched the TRUE-exit-vector
# oracle, non-zero = diverged or timed out on every attempt.

set -uo pipefail

GODOT="${1:-godot}"
SCENARIO="${2:-poisoned}"
SCENE="res://tests/integration/NetExitVectorRpcTest.tscn"
PORT="${HARNESS_PORT:-23461}"   # 23456-23460 belong to the handshake/state-sync/
                                 # node-replication/behindtheback/defensive-telegraph harnesses
SERVER_BIND_WAIT="${SERVER_BIND_WAIT:-6}"
MAX_ATTEMPTS="${MAX_ATTEMPTS:-3}"  # see the "Bounded retry" header block

log() { echo "[run-net-exitvector-rpc:$SCENARIO] $*"; }

log "godot=$GODOT scene=$SCENE port=$PORT max_attempts=$MAX_ATTEMPTS"

# Launch one server+client pair and echo the SERVER's exit code (the verdict).
# Self-contained: it reaps both child processes before returning so the next
# attempt starts from a clean slate (ENet is UDP, so the fixed port rebinds
# immediately once the prior server process is gone — no TCP TIME_WAIT).
run_once() {
  local attempt="$1"
  local server_pid client_pid server_rc

  "$GODOT" --headless --path . "$SCENE" -- --harness-role=server --harness-port="$PORT" --harness-scenario="$SCENARIO" &
  server_pid=$!
  log "attempt $attempt/$MAX_ATTEMPTS: server launched (pid $server_pid); waiting ${SERVER_BIND_WAIT}s to bind…"

  sleep "$SERVER_BIND_WAIT"

  if ! kill -0 "$server_pid" 2>/dev/null; then
    log "attempt $attempt: server process exited during bind wait"
    return 1
  fi

  log "attempt $attempt: launching client (backgrounded — server carries the verdict)…"
  "$GODOT" --headless --path . "$SCENE" -- --harness-role=client --harness-port="$PORT" --harness-scenario="$SCENARIO" &
  client_pid=$!

  wait "$server_pid"
  server_rc=$?

  # Server has rendered its verdict; tear the client down before returning.
  if kill -0 "$client_pid" 2>/dev/null; then
    kill "$client_pid" 2>/dev/null
    wait "$client_pid" 2>/dev/null
  fi

  log "attempt $attempt: server exit code: $server_rc"
  return "$server_rc"
}

# Best-effort cleanup of any stragglers if we are interrupted mid-attempt.
cleanup() { pkill -P $$ 2>/dev/null || true; }
trap cleanup EXIT

attempt=1
while [ "$attempt" -le "$MAX_ATTEMPTS" ]; do
  if run_once "$attempt"; then
    log "PASS: scenario=$SCENARIO proven on attempt $attempt/$MAX_ATTEMPTS — server composed the TRUE exit vector"
    exit 0
  fi
  log "attempt $attempt/$MAX_ATTEMPTS did not prove the composition; $([ "$attempt" -lt "$MAX_ATTEMPTS" ] && echo "retrying (sub-tick RPC-arrival race, #264)" || echo "no attempts left")"
  attempt=$((attempt + 1))
  [ "$attempt" -le "$MAX_ATTEMPTS" ] && sleep 1  # let the prior server fully exit before rebinding
done

log "FAIL: scenario=$SCENARIO — server never proved the TRUE exit vector composition across $MAX_ATTEMPTS attempts (a genuine #210 regression would fail every attempt deterministically; see this script's retry header)"
exit 1
