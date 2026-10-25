#!/usr/bin/env bash
set -euo pipefail
GODOT="${1:-godot}"
DELAY="${2:-0}"
MUTATION="${3:-none}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PYTHON="${PYTHON:-python3}"
command -v "$PYTHON" >/dev/null 2>&1 || PYTHON=python
exec "$PYTHON" "$ROOT/tools/run_net_shot_heading.py" --godot "$GODOT" --delay-ms "$DELAY" --mutation "$MUTATION" --output "$ROOT/.godot/shot-net/catalog-$DELAY-$MUTATION-$$"
