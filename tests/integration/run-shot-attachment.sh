#!/usr/bin/env bash
# Reuse production FormCapture; each local/owner/observer role is judged separately.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PYTHON="${PYTHON:-python3}"
command -v "$PYTHON" >/dev/null 2>&1 || PYTHON=python
GODOT="${1:-godot}"
if (( $# > 0 )); then shift; fi
exec "$PYTHON" "$ROOT/tools/run_shot_attachment_proof.py" --godot "$GODOT" "$@"
