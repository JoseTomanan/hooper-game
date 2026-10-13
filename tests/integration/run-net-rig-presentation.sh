#!/usr/bin/env bash
# Production rig presentation proof; both peers must pass.
# Semantic readiness replaces SERVER_BIND_WAIT.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PYTHON="${PYTHON:-python3}"
command -v "$PYTHON" >/dev/null 2>&1 || PYTHON=python
exec "$PYTHON" "$ROOT/tools/run_network_harness.py" rig-presentation "${1:-godot}" "${2:-left}"
