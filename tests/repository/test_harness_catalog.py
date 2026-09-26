'''Behavioral contract for the executable headless-harness catalog.'''

import contextlib
import hashlib
import importlib.util
import io
import json
import math
import os
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from dataclasses import replace
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / 'tools' / 'harness_catalog.py'


def load_catalog_module():
    spec = importlib.util.spec_from_file_location('harness_catalog', MODULE_PATH)
    if spec is None or spec.loader is None:
        raise RuntimeError(f'cannot import {MODULE_PATH}')
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


class HarnessCatalogCliTests(unittest.TestCase):
    def test_list_prints_the_complete_catalog_in_order(self):
        catalog = load_catalog_module()
        stdout = io.StringIO()
        with contextlib.redirect_stdout(stdout):
            result = catalog.main(['list'], repo_root=ROOT)

        lines = stdout.getvalue().splitlines()
        self.assertEqual(0, result)
        self.assertEqual(len(catalog.CATALOG), len(lines))
        self.assertTrue(lines[0].startswith('smoke-test\t'))
        self.assertTrue(lines[-1].startswith('dedicated-game-journey-score-rpc-disabled\t'))

    def test_catalog_exactly_preserves_the_frozen_migration_fixture(self):
        catalog = load_catalog_module()
        fixture = json.loads((ROOT / 'tests/repository/fixtures/harness_catalog_6e66aa9.json').read_text(encoding='utf-8'))
        expanded = [{'topology': case.topology, 'argv': list(case.argv)} for case in catalog.MIGRATION_BASELINE_CATALOG]
        baseline = fixture['cases']
        canonical = json.dumps(baseline, ensure_ascii=False, separators=(',', ':')).encode()
        self.assertEqual('6e66aa9', fixture['baseline_commit'])
        self.assertEqual({'total': 273, 'single': 260, 'multiprocess': 13}, fixture['counts'])
        self.assertEqual('e18358f926c27df275ffbcb4dd22a45cc042b53eec16c165a6cb91d66462c81d', fixture['cases_sha256'])
        self.assertEqual(fixture['cases_sha256'], hashlib.sha256(canonical).hexdigest())
        self.assertEqual(baseline, expanded)

    def test_repeated_selectors_are_or_within_category_and_categories_intersect(self):
        catalog = load_catalog_module()
        selected = catalog.select_cases(
            catalog.CATALOG,
            ids=['steal-turnover-test-success', 'steal-turnover-test-whiff'],
            scenes=['StealTurnoverTest.tscn'],
            tags=['single', 'network'],
        )
        self.assertEqual(
            ['steal-turnover-test-success', 'steal-turnover-test-whiff'],
            [case.id for case in selected],
        )

    def test_focused_selection_expands_the_entire_paired_control_component(self):
        catalog = load_catalog_module()
        base = catalog.CATALOG[0]
        cases = (
            replace(base, id='headline', paired_control_ids=('control-a',)),
            replace(base, id='unrelated', argv=base.argv + ('unrelated',)),
            replace(base, id='control-a', argv=base.argv + ('a',), paired_control_ids=('headline', 'control-b')),
            replace(base, id='control-b', argv=base.argv + ('b',), paired_control_ids=('control-a',)),
        )
        selected = catalog.select_cases(cases, ids=['headline'])
        self.assertEqual(['headline', 'control-a', 'control-b'], [case.id for case in selected])

    def test_source_declared_cross_invocation_controls_are_connected(self):
        catalog = load_catalog_module()
        components = (
            ('held-steal-test-held-vulnerable', 'held-steal-test-held-immune-outside-window', 'held-steal-test-pumpfake-now-exposed'),
            ('held-steal-test-held-static-vulnerable', 'held-steal-test-held-static-immune-out-of-reach', 'held-steal-test-held-static-immune-shielded', 'held-steal-test-held-static-immune-wrong-side'),
            ('transit-steal-test-transit-steal', 'transit-steal-test-out-of-reach-recovery', 'transit-steal-test-normal-window-unchanged', 'transit-steal-test-transit-steal-behind-the-back', 'transit-steal-test-out-of-reach-recovery-behind-the-back', 'transit-steal-test-transit-steal-between-the-legs', 'transit-steal-test-out-of-reach-recovery-between-the-legs', 'transit-steal-test-transit-steal-spin', 'transit-steal-test-out-of-reach-recovery-spin'),
            ('steal-turnover-test-success', 'steal-turnover-test-whiff'),
            ('steal-facing-mapping-test-face-to-face', 'steal-facing-mapping-test-side-by-side'),
            ('block-turnover-test-success', 'block-turnover-test-success-lastactive', 'block-turnover-test-whiff', 'block-turnover-test-out-of-range', 'block-turnover-test-control-make', 'block-turnover-test-control-make-default-geometry'),
            ('contest-scatter-test-contest-active', 'contest-scatter-test-no-contest'),
            ('fadeaway-trigger-test-mid-pivot', 'fadeaway-trigger-test-squared-up'),
            ('cradle-race-test-out-of-order', 'cradle-race-test-in-order', 'cradle-race-test-later-legit-drive'),
            ('step-back-cradle-race-test-out-of-order', 'step-back-cradle-race-test-in-order', 'step-back-cradle-race-test-no-drive-ever'),
            ('layup-test-block-success', 'layup-test-block-whiff'),
            ('layup-test-range-gate-inside', 'layup-test-range-gate-tolerated', 'layup-test-range-gate-rejected'),
            ('euro-step-test-euro-step-beats-committed-defender', 'euro-step-test-euro-step-read-still-contests'),
            ('dribble-loop-test-dribble-entered', 'dribble-loop-test-no-ball-locomotion', 'dribble-loop-test-held-no-dribble'),
            ('jumpshot-anim-test-fadeaway-active', 'jumpshot-anim-test-no-fadeaway-when-squared-up'),
            ('step-back-test-step-back-gathers', 'step-back-test-retreat-dribble-no-gather'),
            ('move-kind-anim-test-clipped-reaches-permove', 'move-kind-anim-test-unclipped-stays-generic'),
            ('terminal-no-holder-test-terminal-rebound', 'terminal-no-holder-test-terminal-oob', 'terminal-no-holder-test-nonwinning-make', 'terminal-no-holder-test-live-rebound', 'terminal-no-holder-test-oob-award'),
        )
        for component in components:
            expected = set(component)
            for case_id in component:
                with self.subTest(case_id=case_id):
                    selected = catalog.select_cases(catalog.CATALOG, ids=[case_id])
                    self.assertEqual(expected, {case.id for case in selected})

    def test_unknown_or_empty_selection_is_usage_error(self):
        catalog = load_catalog_module()
        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr):
            result = catalog.main(['list', '--id', 'does-not-exist'], repo_root=ROOT)
        self.assertEqual(2, result)
        self.assertIn('empty', stderr.getvalue())

    def test_run_requires_all_or_a_filter_and_all_rejects_filters(self):
        catalog = load_catalog_module()
        for argv in (['run'], ['run', '--all', '--tag', 'single']):
            with self.subTest(argv=argv), contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(2, catalog.main(argv, repo_root=ROOT))


class HarnessCatalogValidationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.catalog = load_catalog_module()
        cls.base = cls.catalog.CATALOG[0]

    def test_live_catalog_is_valid_and_immutable(self):
        self.catalog.validate_catalog(self.catalog.CATALOG, ROOT)
        self.assertIsInstance(self.catalog.CATALOG, tuple)
        self.assertIsInstance(self.base.tags, tuple)
        for case in self.catalog.CATALOG:
            if 'control' in case.id:
                self.assertTrue(case.paired_control_ids, f'{case.id} has no paired-control metadata')
        with self.assertRaises(Exception):
            self.base.id = 'changed'

    def test_validation_rejects_collisions_duplicates_and_bad_metadata(self):
        mutations = {
            'id': (replace(self.base, id='Bad_ID'),),
            'casefold collision': (self.base, replace(self.base, id=self.base.id.upper(), argv=self.base.argv + ('x',))),
            'invocation duplicate': (self.base, replace(self.base, id='other')),
            'topology': (replace(self.base, topology='cluster'),),
            'scenes': (replace(self.base, scenes=()),),
            'timeout': (replace(self.base, timeout_seconds=0),),
            'non-finite timeout': (replace(self.base, timeout_seconds=math.nan),),
            'tags': (replace(self.base, tags=('Bad Tag',)),),
            'logs': (replace(self.base, log_policy=self.catalog.LogPolicy('', '')),),
            'wrong log policy': (replace(self.base, log_policy=self.catalog.LogPolicy('adapter-owned', 'somewhere')),),
            'artifacts': (replace(self.base, artifact_globs=()),),
            'missing scene': (replace(self.base, scenes=(self.catalog.SceneMetadata('res://tests/integration/Missing.tscn'),)),),
            'escaping scene': (replace(self.base, scenes=(self.catalog.SceneMetadata('res://tests/integration/../repository/test_harness_catalog.py'),)),),
            'missing control': (replace(self.base, paired_control_ids=('missing',)),),
            'self control': (replace(self.base, paired_control_ids=(self.base.id,)),),
        }
        for label, cases in mutations.items():
            with self.subTest(label=label), self.assertRaises(self.catalog.CatalogError):
                self.catalog.validate_catalog(cases, ROOT)


class FakeProcess:
    def __init__(self, returncode=0, wait_error=None):
        self.returncode = returncode
        self.wait_error = wait_error
        self.pid = 4242
        self.signals = []
        self.killed = False

    def wait(self, timeout=None):
        if self.wait_error:
            error, self.wait_error = self.wait_error, None
            raise error
        return self.returncode

    def poll(self):
        return None if self.wait_error else self.returncode

    def send_signal(self, value):
        self.signals.append(value)

    def terminate(self):
        self.signals.append('terminate')

    def kill(self):
        self.killed = True


class PopenScript:
    def __init__(self, outcomes):
        self.outcomes = list(outcomes)
        self.calls = []

    def __call__(self, command, **kwargs):
        self.calls.append((command, kwargs))
        outcome = self.outcomes.pop(0)
        if isinstance(outcome, BaseException):
            raise outcome
        return outcome


class HarnessCatalogRunnerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.catalog = load_catalog_module()

    def test_builds_exact_direct_command_with_separator_unique_log_and_repo_cwd(self):
        case = next(c for c in self.catalog.CATALOG if c.id == 'steal-turnover-test-success')
        popen = PopenScript([FakeProcess(0)])
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            code, results = self.catalog.run_cases((case,), root, 'godot-bin', 'bash-bin', popen_factory=popen, run_id='run-1')
        command, options = popen.calls[0]
        self.assertEqual(0, code)
        self.assertEqual(root, options['cwd'])
        self.assertEqual(
            ['godot-bin', '--log-file', str(root / '.godot/harness-runs/run-1/steal-turnover-test-success.log'), '--headless', '--path', '.', 'res://tests/integration/StealTurnoverTest.tscn', '--', '--harness-scenario=success'],
            command,
        )
        self.assertIn('.godot/harness-runs/**/steal-turnover-test-success.log', results[0].artifacts)

    def test_adapter_receives_exact_existing_arguments_and_reports_owned_artifacts(self):
        case = next(c for c in self.catalog.CATALOG if c.id == 'net-defensive-telegraph-control')
        popen = PopenScript([FakeProcess(0)])
        with tempfile.TemporaryDirectory() as temp:
            code, results = self.catalog.run_cases((case,), Path(temp), 'godot-bin', 'bash-bin', popen_factory=popen, run_id='run-2')
        self.assertEqual(0, code)
        self.assertEqual(['bash-bin', 'tests/integration/run-net-defensive-telegraph.sh', 'godot-bin', 'control'], popen.calls[0][0])
        self.assertEqual('adapter-owned', results[0].log_policy.kind)
        self.assertTrue(results[0].artifacts)

    def test_keep_going_runs_each_case_once_and_aggregates_scenario_failures(self):
        cases = (self.catalog.CATALOG[0], self.catalog.CATALOG[1])
        popen = PopenScript([FakeProcess(1), FakeProcess(7)])
        with tempfile.TemporaryDirectory() as temp:
            code, results = self.catalog.run_cases(cases, Path(temp), 'godot-bin', 'bash-bin', popen_factory=popen, run_id='run-3')
        self.assertEqual(1, code)
        self.assertEqual(2, len(popen.calls))
        self.assertEqual([1, 7], [result.exit_code for result in results])

    def test_spawn_or_setup_error_is_two_timeout_is_one_and_interrupt_is_130(self):
        case = self.catalog.CATALOG[0]
        timeout = subprocess.TimeoutExpired(['godot'], 1)
        for label, process, expected in (
            ('spawn', PopenScript([OSError('no')]), 2),
            ('timeout', PopenScript([FakeProcess(wait_error=timeout)]), 1),
            ('interrupt', PopenScript([FakeProcess(wait_error=KeyboardInterrupt())]), 130),
        ):
            with self.subTest(label=label), tempfile.TemporaryDirectory() as temp:
                code, _ = self.catalog.run_cases((case,), Path(temp), 'godot-bin', 'bash-bin', popen_factory=process, run_id='run-errors')
                self.assertEqual(expected, code)

    def test_missing_executable_and_unexpected_internal_spawn_error_are_two(self):
        with contextlib.redirect_stderr(io.StringIO()):
            missing = self.catalog.main(['run', '--id', 'smoke-test', '--godot', 'absent'], repo_root=ROOT, which=lambda value: None)
        internal = PopenScript([RuntimeError('boom')])
        with tempfile.TemporaryDirectory() as temp, contextlib.redirect_stderr(io.StringIO()):
            code, _ = self.catalog.run_cases((self.catalog.CATALOG[0],), Path(temp), 'godot-bin', 'bash-bin', popen_factory=internal, run_id='internal')
        self.assertEqual(2, missing)
        self.assertEqual(2, code)

    def test_platform_process_group_seam_selects_windows_and_posix_modes(self):
        self.assertIn('creationflags', self.catalog.process_group_options('nt'))
        self.assertEqual({'start_new_session': True}, self.catalog.process_group_options('posix'))

    def test_terminate_process_tree_reaps_a_real_descendant_on_this_os(self):
        parent_code = (
            "import pathlib,subprocess,sys,time; "
            "child=subprocess.Popen([sys.executable,'-c','import time; time.sleep(60)']); "
            "pathlib.Path(sys.argv[1]).write_text(str(child.pid)); time.sleep(60)"
        )
        with tempfile.TemporaryDirectory() as temp:
            pidfile = Path(temp) / 'child.pid'
            process = subprocess.Popen(
                [sys.executable, '-c', parent_code, str(pidfile)],
                **self.catalog.process_group_options(),
            )
            try:
                deadline = time.monotonic() + 5
                while not pidfile.exists() and time.monotonic() < deadline:
                    time.sleep(0.02)
                self.assertTrue(pidfile.exists(), 'parent never reported descendant pid')
                child_pid = int(pidfile.read_text())
                self.catalog.terminate_process_tree(process, grace_seconds=1)
                process.wait(timeout=3)
                deadline = time.monotonic() + 5
                while self._pid_is_alive(child_pid) and time.monotonic() < deadline:
                    time.sleep(0.05)
                self.assertFalse(self._pid_is_alive(child_pid), f'descendant {child_pid} survived cleanup')
            finally:
                if process.poll() is None:
                    self.catalog.terminate_process_tree(process, grace_seconds=1)

    @staticmethod
    def _pid_is_alive(pid):
        if os.name != 'nt':
            try:
                os.kill(pid, 0)
                return True
            except OSError:
                return False
        import ctypes
        handle = ctypes.windll.kernel32.OpenProcess(0x1000, False, pid)
        if not handle:
            return False
        code = ctypes.c_ulong()
        try:
            return bool(ctypes.windll.kernel32.GetExitCodeProcess(handle, ctypes.byref(code))) and code.value == 259
        finally:
            ctypes.windll.kernel32.CloseHandle(handle)

    def test_run_all_executes_all_catalog_entries_once_in_order(self):
        popen = PopenScript([FakeProcess(0) for _ in self.catalog.CATALOG])
        run_id = 'all-' + next(tempfile._get_candidate_names())
        with contextlib.redirect_stdout(io.StringIO()):
            code = self.catalog.main(['run', '--all', '--godot', 'godot-bin', '--bash', 'bash-bin'], repo_root=ROOT, cases=self.catalog.CATALOG, popen_factory=popen, which=lambda value: value, run_id_factory=lambda: run_id)
        self.assertEqual(0, code)
        self.assertEqual(len(self.catalog.CATALOG), len(popen.calls))
        self.assertIn('SmokeTest.tscn', ' '.join(popen.calls[0][0]))
        self.assertEqual('score-rpc-disabled', popen.calls[-1][0][-1])

    def test_main_resolves_bare_executables_before_launch(self):
        case = next(c for c in self.catalog.CATALOG if c.id == 'net-handshake')
        popen = PopenScript([FakeProcess(0)])
        resolved = {'godot': 'resolved-godot.exe', 'bash': 'resolved-bash.exe'}
        with contextlib.redirect_stdout(io.StringIO()):
            code = self.catalog.main(
                ['run', '--id', case.id, '--godot', 'godot', '--bash', 'bash'],
                repo_root=ROOT,
                cases=(case,),
                popen_factory=popen,
                which=lambda value: resolved.get(value),
                run_id_factory=lambda: 'resolved',
            )
        self.assertEqual(0, code)
        self.assertEqual('resolved-bash.exe', popen.calls[0][0][0])
        self.assertEqual('resolved-godot.exe', popen.calls[0][0][2])


class HarnessCatalogWrapperTests(unittest.TestCase):
    SCRIPTS = (
        '.agents/skills/hooper-diagnostics-and-tooling/scripts/run-harness-local.sh',
        '.claude/skills/hooper-diagnostics-and-tooling/scripts/run-harness-local.sh',
        '.agents/skills/hooper-diagnostics-and-tooling/scripts/run-harness-local.ps1',
        '.claude/skills/hooper-diagnostics-and-tooling/scripts/run-harness-local.ps1',
    )

    def test_mirrors_are_byte_identical_and_contain_no_inventory(self):
        agent_sh, claude_sh, agent_ps, claude_ps = (ROOT / path for path in self.SCRIPTS)
        self.assertEqual(agent_sh.read_bytes(), claude_sh.read_bytes())
        self.assertEqual(agent_ps.read_bytes(), claude_ps.read_bytes())
        for path in (agent_sh, claude_sh, agent_ps, claude_ps):
            source = path.read_text(encoding='utf-8')
            self.assertNotIn('.tscn', source)
            self.assertNotIn('ci.yml', source)
            self.assertNotIn('run-net-', source)

    def test_all_four_wrappers_forward_arguments_and_exit_codes(self):
        for relative in self.SCRIPTS:
            path = ROOT / relative
            if path.suffix == '.sh':
                shell = shutil.which('bash')
                command = [shell, str(path)] if shell else None
            else:
                shell = shutil.which('powershell') or shutil.which('pwsh')
                command = [shell, '-NoProfile', '-File', str(path)] if shell else None
            if command is None:
                continue
            with self.subTest(path=relative):
                ok = subprocess.run([*command, 'list', '--id', 'smoke-test'], cwd=ROOT, text=True, capture_output=True, check=False)
                bad = subprocess.run([*command, 'list', '--id', 'missing-case'], cwd=ROOT, text=True, capture_output=True, check=False)
                self.assertEqual(0, ok.returncode, ok.stderr)
                self.assertIn('smoke-test', ok.stdout)
                self.assertEqual(2, bad.returncode, bad.stderr)


class HarnessCatalogWorkflowTests(unittest.TestCase):
    def test_ci_uses_only_the_exhaustive_catalog_runner_and_uploads_broad_failures(self):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text(encoding='utf-8')
        self.assertIn('timeout-minutes: 15', workflow)
        self.assertEqual(1, workflow.count('python3 tools/harness_catalog.py run --all --godot godot'))
        self.assertNotIn('res://tests/integration/', workflow)
        for obsolete in ('run-net-handshake.sh', 'run-dedicated-game-journey.sh', '--harness-scenario='):
            self.assertNotIn(obsolete, workflow)
        self.assertIn("python3 -m unittest discover -s tests/repository -p 'test_*.py' -v", workflow)
        for artifact in ('.godot/harness-runs/**', '.godot/harness-logs/**', '.godot/harness-coordination/**', '.godot/dedicated-game-*/**'):
            self.assertIn(artifact, workflow)


if __name__ == '__main__':
    unittest.main()
