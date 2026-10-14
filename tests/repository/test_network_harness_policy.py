"""Scenario policy preserves the proof owner and bounded #264 retry contract."""

import contextlib
import io
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
import run_network_harness as adapter
from multiprocess_harness import PairResult, PeerEvidence


class NetworkHarnessPolicyTests(unittest.TestCase):
    def setUp(self):
        (ROOT / '.godot').mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=ROOT / '.godot')
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)

    def failure(self, server_lines, *, phase='proof', code=1, client_lines='', client_code=None):
        server_log, client_log = self.directory / 'server.log', self.directory / 'client.log'
        server_log.write_text(server_lines, encoding='utf-8')
        client_log.write_text(client_lines, encoding='utf-8')
        return PairResult(role='server', phase=phase, reason=f'exited {code}', peers={
            'server': PeerEvidence(server_log, self.directory / 'server-native.log', code, code),
            'client': PeerEvidence(client_log, self.directory / 'client-native.log', client_code, 1),
        })

    def run_adapter(self, results, attempts='3'):
        with patch.dict(os.environ, {'MAX_ATTEMPTS': attempts}), \
             patch.object(adapter, 'run_pair', side_effect=results) as run, \
             contextlib.redirect_stdout(io.StringIO()):
            code = adapter.main(['exitvector-rpc', 'godot', 'poisoned'])
        return code, run.call_args_list

    def test_deterministic_divergence_exhausts_fresh_bounded_attempts(self):
        failure = self.failure(adapter.EXIT_VECTOR_DIVERGENCE + ' diff=18.0000\n'
                               '[net-exitvec] server RESULT: FAIL (exit 1)\n')
        code, calls = self.run_adapter([failure] * 3)
        self.assertEqual(1, code)
        self.assertEqual(3, len(calls))
        self.assertEqual(3, len({call.kwargs['artifact_prefix'] for call in calls}))
        for call in calls:
            self.assertTrue(call.args[0].required)
            self.assertFalse(call.args[1].required)

    def test_documented_transient_divergence_can_retry_to_success(self):
        failure = self.failure(adapter.EXIT_VECTOR_DIVERGENCE + ' diff=18.0000\n'
                               '[net-exitvec] server RESULT: FAIL (exit 1)\n')
        code, calls = self.run_adapter([failure, PairResult(passed=True)])
        self.assertEqual(0, code)
        self.assertEqual(2, len(calls))

    def test_readiness_crash_missing_proof_and_auxiliary_failure_never_retry(self):
        divergence = adapter.EXIT_VECTOR_DIVERGENCE + ' diff=18.0000\n[net-exitvec] server RESULT: FAIL (exit 1)\n'
        controls = (
            dict(server_lines=divergence, phase='readiness'),
            dict(server_lines=divergence, code=9),
            dict(server_lines='[net-exitvec] server RESULT: FAIL (exit 1)'),
            dict(server_lines=divergence, client_code=9),
            dict(server_lines=divergence, client_lines='FAIL client'),
            dict(server_lines=divergence, client_code=0),
            dict(server_lines=divergence + 'FAIL unrelated assertion\n'),
        )
        for control in controls:
            with self.subTest(control=control):
                code, calls = self.run_adapter([self.failure(**control)])
                self.assertEqual(1, code)
                self.assertEqual(1, len(calls))

    def test_retired_bind_wait_cannot_delay_launch_and_roles_keep_scenario_input(self):
        families = (
            ('handshake', None, ('client',)), ('state-sync', None, ('client',)),
            ('node-replication', None, ('client',)), ('behindtheback-sweep', None, ('client',)),
            ('defensive-telegraph', 'control', ('client',)),
            ('rig-presentation', 'right', ('server', 'client')),
        )
        for family, scenario, owners in families:
            with self.subTest(family=family), \
                 patch.dict(os.environ, {'SERVER_BIND_WAIT': '99999', 'HARNESS_PORT': '23499'}), \
                 patch.object(adapter, 'run_pair', return_value=PairResult(passed=True)) as run, \
                 contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(0, adapter.main([family, 'godot', *([scenario] if scenario else [])]))
                server, client = run.call_args.args
                self.assertEqual(owners, tuple(p.role for p in (server, client) if p.required))
                for peer in (server, client):
                    command = peer.command(self.directory / 'native.log')
                    self.assertIn('--harness-port=23499', command)
                    self.assertIn(f'--harness-role={peer.role}', command)
                    if scenario:
                        self.assertIn(f'--harness-scenario={scenario}', command)
                self.assertEqual(15, run.call_args.kwargs['ready_timeout'])


if __name__ == '__main__':
    unittest.main()
