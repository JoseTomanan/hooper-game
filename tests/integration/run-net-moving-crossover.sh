#!/usr/bin/env bash
# Production moving-crossover proof; both owner and observer must pass.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PYTHON="${PYTHON:-python3}"
command -v "$PYTHON" >/dev/null 2>&1 || PYTHON=python
exec "$PYTHON" "$ROOT/tools/run_network_harness.py" moving-crossover "${1:-godot}" "${2:-host}"
