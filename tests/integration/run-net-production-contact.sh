#!/usr/bin/env bash
set -euo pipefail
GODOT="${1:-godot}"
MOVE="${2:-neutral}"
CONTACT="${3:-contact}"
DELAY="${4:-0}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PYTHON="${PYTHON:-python3}"
command -v "$PYTHON" >/dev/null 2>&1 || PYTHON=python
exec "$PYTHON" "$ROOT/tools/run_production_contact.py" --godot "$GODOT" --move "$MOVE" --contact "$CONTACT" --delay-ms "$DELAY" --output "$ROOT/.godot/production-contact/catalog-$MOVE-$CONTACT-$DELAY-$$"
