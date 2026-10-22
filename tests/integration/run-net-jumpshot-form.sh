#!/usr/bin/env bash
# Both server occurrence and genuinely remote final-pose display must be observed.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PYTHON="${PYTHON:-python3}"
command -v "$PYTHON" >/dev/null 2>&1 || PYTHON=python
exec "$PYTHON" "$ROOT/tools/run_jumpshot_proof.py" --godot "${1:-godot}"
