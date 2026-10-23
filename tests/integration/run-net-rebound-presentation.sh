#!/usr/bin/env bash
# Production scenes, real ENet owner/observer, and discriminating catch controls.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PYTHON="${PYTHON:-python3}"
command -v "$PYTHON" >/dev/null 2>&1 || PYTHON=python
exec "$PYTHON" "$ROOT/tools/run_rebound_proof.py" --godot "${1:-godot}"
