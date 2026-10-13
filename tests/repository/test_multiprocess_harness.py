"""Real subprocess controls for the network harness lifecycle (#409)."""

import sys
import os
import subprocess
import tempfile
import time
import unittest
from unittest.mock import patch
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
from multiprocess_harness import Peer, run_pair


def python_peer(role, code, pass_marker=None):
    return Peer(role, lambda native: [sys.executable, '-u', '-c', code],
                pass_marker or f'{role} PASS', required=pass_marker is not None)


class MultiprocessHarnessTests(unittest.TestCase):
    def setUp(self):
        (ROOT / '.godot').mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=ROOT / '.godot')
        self.addCleanup(self.temp.cleanup)
        self.prefix = Path(self.temp.name) / 'attempt'

    def run_control(self, server_code, client_code="print('client PASS')", **kwargs):
        return run_pair(python_peer('server', server_code),
                        python_peer('client', client_code, 'client PASS'),
                        ready_marker='READY server', artifact_prefix=self.prefix,
                        ready_timeout=kwargs.get('ready_timeout', .3),
                        proof_timeout=kwargs.get('proof_timeout', .3), cleanup_grace=.2)

    def test_auxiliary_early_exit_zero_without_completion_fails(self):
        result = self.run_control("print('READY server')",
                                  "import time; time.sleep(.15); print('client PASS')")
        self.assertFalse(result.passed)
        self.assertEqual('server', result.role)

    def test_missing_ready_does_not_launch_client_and_retains_artifacts(self):
        client_started = Path(self.temp.name) / 'client-started'
        result = self.run_control("import time; print('boot', flush=True); time.sleep(30)",
            f"import pathlib; pathlib.Path({str(client_started)!r}).touch(); print('client PASS')")
        self.assertFalse(result.passed)
        self.assertEqual('readiness', result.phase)
        self.assertFalse(client_started.exists())
        self.assertLess(result.elapsed_seconds, 2)
        self.assertIn('boot', result.peers['server'].text())
        self.assertIsNotNone(result.peers['server'].exit_code)

    def test_server_death_before_ready_fails_without_waiting_deadline(self):
        result = self.run_control("raise SystemExit(7)", ready_timeout=10)
        self.assertFalse(result.passed)
        self.assertEqual(7, result.peers['server'].exit_code)
        self.assertLess(result.elapsed_seconds, 2)
        self.assertNotIn('client', result.peers)

    def test_zero_exit_missing_or_wrong_role_pass_cannot_prove(self):
        for client_code in ("pass", "print('server PASS')"):
            with self.subTest(client_code=client_code):
                result = self.run_control("import time; print('READY server',flush=True); time.sleep(30)", client_code)
                self.assertFalse(result.passed)
                self.assertEqual('client', result.role)
                self.assertIn('without required PASS', result.reason)

    def test_pass_then_crash_or_assertion_failure_cannot_prove(self):
        for client_code in ("print('client PASS'); raise SystemExit(9)",
                            "print('client PASS'); print('FAIL');"):
            with self.subTest(client_code=client_code):
                result = self.run_control("import time; print('READY server',flush=True); time.sleep(30)", client_code)
                self.assertFalse(result.passed)

    def test_fake_ready_with_stalled_bind_is_not_proof(self):
        result = self.run_control("import time; print('READY server',flush=True); time.sleep(30)",
                                 "import time; time.sleep(30)")
        self.assertFalse(result.passed)
        self.assertEqual('proof timeout', result.reason)
        self.assertLess(result.elapsed_seconds, 2)
        self.assertTrue(all(p.exit_code is not None for p in result.peers.values()))

    def test_fake_ready_with_failed_bind_cannot_pass(self):
        result = self.run_control("print('READY server',flush=True); print('FAIL bind'); raise SystemExit(1)")
        self.assertFalse(result.passed)
        self.assertEqual('server', result.role)

    def test_stale_ready_and_pass_do_not_cross_attempts(self):
        first = self.run_control("import time; print('READY server',flush=True); time.sleep(30)")
        second = self.run_control("import time; time.sleep(30)")
        self.assertTrue(first.passed)
        self.assertFalse(second.passed)
        self.assertNotEqual(first.peers['server'].stdout, second.peers['server'].stdout)
        self.assertTrue(first.peers['server'].stdout.exists())

    def test_partial_launch_failure_reaps_server(self):
        server = python_peer('server', "import time; print('READY server',flush=True); time.sleep(30)")
        client = Peer('client', lambda native: ['409-no-such-executable'], 'client PASS')
        result = run_pair(server, client, ready_marker='READY server',
                          artifact_prefix=self.prefix, cleanup_grace=.2)
        self.assertFalse(result.passed)
        self.assertEqual('client', result.role)
        self.assertEqual('launch', result.phase)
        self.assertIsNotNone(result.peers['server'].exit_code)

    def test_interrupt_reaps_started_peers(self):
        server = python_peer('server', "import time; print('boot',flush=True); time.sleep(30)")
        launched = []
        original = subprocess.Popen
        def record(*args, **kwargs):
            process = original(*args, **kwargs)
            launched.append(process)
            return process
        with patch('subprocess.Popen', side_effect=record), patch('multiprocess_harness.time.sleep', side_effect=KeyboardInterrupt):
            with self.assertRaises(KeyboardInterrupt):
                run_pair(server, python_peer('client', "print('client PASS')", 'client PASS'),
                         ready_marker='READY server', artifact_prefix=self.prefix, cleanup_grace=.2)
        self.assertEqual(1, len(launched))
        self.assertIsNotNone(launched[0].poll())

    def test_descendant_is_cleaned_even_after_leader_exits(self):
        pidfile = Path(self.temp.name) / 'descendant.pid'
        child_code = ("import os,signal,time,pathlib; signal.signal(signal.SIGTERM,signal.SIG_IGN); "
                      f"pathlib.Path({str(pidfile)!r}).write_text(str(os.getpid())); time.sleep(30)")
        server_code = ("import subprocess,sys,time,pathlib; "
                       f"subprocess.Popen([sys.executable,'-c',{child_code!r}]); "
                       f"p=pathlib.Path({str(pidfile)!r}); "
                       "\nwhile not p.exists(): time.sleep(.01)\n"
                       "print('READY server',flush=True); time.sleep(.15); print('server PASS',flush=True)")
        result = self.run_control(server_code, "import time; time.sleep(.25); print('client PASS')", ready_timeout=3, proof_timeout=3)
        # The exited server supplied its own normal completion marker; its
        # descendants still belong to this attempt and must not outlive cleanup.
        self.assertTrue(result.passed, result)
        child_pid = int(pidfile.read_text())
        from test_harness_catalog import HarnessCatalogRunnerTests
        deadline = time.monotonic() + 3
        while HarnessCatalogRunnerTests._pid_is_alive(child_pid) and time.monotonic() < deadline:
            time.sleep(.025)
        self.assertFalse(HarnessCatalogRunnerTests._pid_is_alive(child_pid))

    def test_auxiliary_crash_during_other_peer_cleanup_invalidates_proof(self):
        verdict = Path(self.temp.name) / 'client-verdict'
        server = ("import time,pathlib; print('READY server',flush=True); "
                  f"p=pathlib.Path({str(verdict)!r}); "
                  "\nwhile not p.exists(): time.sleep(.01)\n"
                  "time.sleep(.1); raise SystemExit(9)")
        client = f"import pathlib; pathlib.Path({str(verdict)!r}).touch(); print('client PASS')"
        if os.name == 'nt':
            from windows_process_job import WindowsProcessJob
            target, original = 'windows_process_job.WindowsProcessJob.close', WindowsProcessJob.close
        else:
            import multiprocess_harness
            target, original = 'multiprocess_harness.terminate_process_tree', multiprocess_harness.terminate_process_tree
        calls = []
        def slow_first_cleanup(*args, **kwargs):
            if not calls:
                time.sleep(.25)
            calls.append(True)
            return original(*args, **kwargs)
        with patch(target, slow_first_cleanup):
            result = self.run_control(server, client, ready_timeout=3, proof_timeout=3)
        self.assertFalse(result.passed, result)
        self.assertEqual(9, result.peers['server'].exit_before_cleanup)

    def test_readiness_follows_successful_socket_bind(self):
        address = Path(self.temp.name) / 'port'
        server = ("import socket,pathlib,time; time.sleep(.15); "
                  "s=socket.socket(); s.bind(('127.0.0.1',0)); s.listen(); "
                  f"pathlib.Path({str(address)!r}).write_text(str(s.getsockname()[1])); "
                  "print('READY server',flush=True); s.accept(); time.sleep(30)")
        client = ("import socket,pathlib; "
                  f"s=socket.create_connection(('127.0.0.1',int(pathlib.Path({str(address)!r}).read_text()))); "
                  "print('client PASS')")
        result = self.run_control(server, client, ready_timeout=3, proof_timeout=3)
        self.assertTrue(result.passed, result)
        self.assertGreaterEqual(result.ready_seconds, .15)

    def test_server_owned_verdict_and_two_peer_verdict_are_distinct(self):
        server = python_peer('server', "import time; print('READY server',flush=True); time.sleep(.2); print('server PASS')", 'server PASS')
        auxiliary_client = python_peer('client', "import time; time.sleep(30)")
        result = run_pair(server, auxiliary_client, ready_marker='READY server',
                          artifact_prefix=self.prefix, ready_timeout=3, proof_timeout=3, cleanup_grace=.2)
        self.assertTrue(result.passed, result)
        self.assertEqual(0, result.peers['server'].exit_before_cleanup)
        self.assertIsNone(result.peers['client'].exit_before_cleanup)
        required_client = python_peer('client', "print('server PASS')", 'client PASS')
        result = run_pair(server, required_client, ready_marker='READY server',
                          artifact_prefix=self.prefix, ready_timeout=3, proof_timeout=3, cleanup_grace=.2)
        self.assertFalse(result.passed)
        self.assertEqual('client', result.role)

    def test_client_launch_follows_delayed_readiness(self):
        (ROOT / '.godot').mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ROOT / '.godot') as temp:
            bound = Path(temp) / 'bound'
            server = python_peer('server',
                f"import time,pathlib; time.sleep(.2); pathlib.Path({str(bound)!r}).touch(); "
                "print('READY server', flush=True); time.sleep(30)")
            client = python_peer('client',
                f"import pathlib; assert pathlib.Path({str(bound)!r}).exists(); print('client PASS')",
                'client PASS')
            result = run_pair(server, client, ready_marker='READY server',
                              artifact_prefix=Path(temp) / 'run', ready_timeout=3,
                              proof_timeout=3, cleanup_grace=.2)
            self.assertTrue(result.passed, result)
            self.assertGreaterEqual(result.ready_seconds, .2)
            self.assertIsNone(result.peers['server'].exit_before_cleanup)
            self.assertIsNotNone(result.peers['server'].exit_code)
            self.assertEqual(0, result.peers['client'].exit_code)


if __name__ == '__main__':
    unittest.main()
