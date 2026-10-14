#!/usr/bin/env bash
# Server-owned exit-vector composition proof; only documented #264 divergence retries.
# Semantic readiness replaces SERVER_BIND_WAIT.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PYTHON="${PYTHON:-python3}"
command -v "$PYTHON" >/dev/null 2>&1 || PYTHON=python
exec "$PYTHON" "$ROOT/tools/run_network_harness.py" exitvector-rpc "${1:-godot}" "${2:-poisoned}"
