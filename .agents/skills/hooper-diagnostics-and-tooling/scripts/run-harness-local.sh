#!/usr/bin/env bash
# Thin compatibility entry point; the validated catalog owns all inventory and execution.
set -u
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../../.." && pwd)"
PYTHON_BIN="${PYTHON:-python3}"
exec "$PYTHON_BIN" "$REPO_ROOT/tools/harness_catalog.py" "$@"
