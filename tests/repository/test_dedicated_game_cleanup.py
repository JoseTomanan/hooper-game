"""Exercise the adapter's real cleanup against a role that ignores TERM."""

import shutil
import subprocess
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]


class DedicatedGameCleanupTests(unittest.TestCase):
    @unittest.skipUnless(shutil.which('bash'), 'requires Bash (as does the dedicated adapter)')
    def test_term_ignoring_role_is_killed_and_reaped_within_grace_bound(self):
        adapter = (ROOT / 'tests/integration/run-dedicated-game-journey.sh').read_text(encoding='utf-8')
        functions = adapter[adapter.index('pids=()'):adapter.index('trap cleanup EXIT')]
        script = functions + r'''
set -uo pipefail
( trap '' TERM; while true; do sleep 0.1; done ) &
role_pid=$!
pids=("$role_pid")
# Wait for the subshell to install its handler before sending TERM.
sleep 0.2
started=$SECONDS
cleanup
if kill -0 "$role_pid" 2>/dev/null; then
  kill -KILL "$role_pid" 2>/dev/null || true
  exit 1
fi
elapsed=$((SECONDS - started))
# A quick exit would not prove the TERM-ignoring premise was exercised.
[ "$elapsed" -ge 2 ] && [ "$elapsed" -le 5 ]
'''
        result = subprocess.run([shutil.which('bash'), '-c', script], cwd=ROOT,
                                capture_output=True, text=True, timeout=10)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)


if __name__ == '__main__':
    unittest.main()
