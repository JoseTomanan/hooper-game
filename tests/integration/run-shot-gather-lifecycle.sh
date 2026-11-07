#!/usr/bin/env bash
# Real client role; deterministic lifecycle packets are injected by the fixture.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PYTHON="${PYTHON:-python3}"
command -v "$PYTHON" >/dev/null 2>&1 || PYTHON=python
exec "$PYTHON" - "$ROOT" "${1:-godot}" <<'PY'
import os
from pathlib import Path
import sys

root = Path(sys.argv[1]).resolve()
sys.path.insert(0, str(root / 'tools'))
from multiprocess_harness import Peer, report, run_pair

port = int(os.environ.get('HARNESS_PORT', '28442'))
if not 0 < port < 65536:
    raise SystemExit('HARNESS_PORT must be in 1..65535')

def peer(role):
    def command(native):
        return [sys.argv[2], '--headless', '--path', str(root), '--log-file', str(native),
                'res://tests/integration/ShotGatherLifecycleTest.tscn', '--',
                '--harness-role=' + role, '--harness-port=' + str(port)]
    return Peer(role, command, '[shot-gather-lifecycle] ' + role + ' RESULT: PASS (exit 0)')

result = run_pair(peer('server'), peer('client'),
    ready_marker='[shot-gather-lifecycle] READY server',
    artifact_prefix=root / '.godot/harness-logs/shot-gather-lifecycle',
    ready_timeout=float(os.environ.get('HARNESS_READY_TIMEOUT', '15')),
    proof_timeout=40, cwd=root)
report(result)
raise SystemExit(0 if result.passed else 1)
PY
