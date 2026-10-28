#!/usr/bin/env bash
# Production input, authoritative occurrences and separate final-pose observations.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PYTHON="${PYTHON:-python3}"
command -v "$PYTHON" >/dev/null 2>&1 || PYTHON=python
exec "$PYTHON" "$ROOT/tools/run_jumpshot_transition_proof.py" --godot "${1:-godot}"
