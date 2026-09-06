#!/usr/bin/env bash
# Bounded rendered-evidence experiment for #365.  A new mktemp root is the
# freshness boundary: the C# runner also rejects a non-empty run directory, so
# old frames can never make a later manifest appear complete.
set -euo pipefail

GODOT="${1:-godot}"
ROOT="${RENDERED_EVIDENCE_ROOT:-$(mktemp -d)}"
PORT="${HARNESS_PORT:-23461}"
COMMIT="$(git rev-parse HEAD)"
SCENE="res://tests/integration/RenderedEvidenceCapture.tscn"
LOG_DIR="$ROOT/logs"
mkdir -p "$LOG_DIR"

run_capture() {
  local role="$1" out="$2" log="$3" extra="${4:-}"
  mkdir "$out"
  # xvfb supplies a real display server on hosted Linux.  gl_compatibility plus
  # Mesa software rendering is a reversible command-line choice, never a
  # project renderer setting.  The runner itself catches black framebuffer
  # output and reports NO-GO rather than publishing an empty image.
  xvfb-run -a env LIBGL_ALWAYS_SOFTWARE=1 "$GODOT" --path . --rendering-method gl_compatibility \
    --rendering-driver opengl3 --write-movie "$out/capture.avi" --fixed-fps 60 --disable-vsync \
    --log-file "$log" "$SCENE" -- --capture-role="$role" --capture-output="$out" \
    --capture-commit="$COMMIT" --capture-port="$PORT" $extra
}

run_capture local "$ROOT/local-a" "$LOG_DIR/local-a.log"
run_capture local "$ROOT/local-b" "$LOG_DIR/local-b.log"

# The mutation is intentionally expected to fail: it asks the runner to find a
# nonexistent live AnimationTree state.  A zero exit here means the intended-
# state validator has become vacuous, so fail the whole experiment.
if run_capture local "$ROOT/mutation" "$LOG_DIR/mutation.log" "--capture-mutation=missing-intended-state"; then
  echo "[rendered-evidence] FAIL: deliberately broken scenario unexpectedly passed" >&2
  exit 1
fi

# A second, persisted-artifact mutation proves the external verifier is not
# merely trusting the in-memory C# Image.  Truncate the first PNG only after a
# successful write, require the verifier to reject it, then run a fresh clean
# baseline afterwards to prove the mutation did not poison later evidence.
run_capture local "$ROOT/image-mutation" "$LOG_DIR/image-mutation.log"
truncate -s 12 "$ROOT/image-mutation/01-stationary-to-moving-dribble.png"
if python3 tools/verify_rendered_evidence.py --local "$ROOT/image-mutation/manifest.json"; then
  echo "[rendered-evidence] FAIL: deliberately truncated PNG unexpectedly passed external validation" >&2
  exit 1
fi
run_capture local "$ROOT/local-c" "$LOG_DIR/local-c.log"
python3 tools/verify_rendered_evidence.py --local "$ROOT/local-c/manifest.json"

run_capture server "$ROOT/remote-server" "$LOG_DIR/remote-server.log" &
SERVER_PID=$!
cleanup() {
  if kill -0 "$SERVER_PID" 2>/dev/null; then kill "$SERVER_PID" 2>/dev/null || true; wait "$SERVER_PID" 2>/dev/null || true; fi
}
trap cleanup EXIT
sleep 5
run_capture client "$ROOT/remote-client" "$LOG_DIR/remote-client.log"

python3 tools/verify_rendered_evidence.py "$ROOT/local-a/manifest.json" "$ROOT/local-b/manifest.json" "$ROOT/remote-client/manifest.json"
echo "[rendered-evidence] PASS root=$ROOT"
