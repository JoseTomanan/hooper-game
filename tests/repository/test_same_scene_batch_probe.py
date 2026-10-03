import importlib.util
import json
import sys
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / 'tools' / 'same_scene_batch_probe.py'
A = 'steal-facing-mapping-test-face-to-face'
B = 'steal-facing-mapping-test-side-by-side'


def load_probe_module():
    spec = importlib.util.spec_from_file_location('same_scene_batch_probe', MODULE_PATH)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


class SameSceneBatchAccountingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.probe = load_probe_module()

    def test_exactly_one_passing_result_per_selected_stable_id_is_success(self):
        report = self.probe.account_events(
            (A, B),
            (
                {'event': 'start', 'case_id': A, 'invocation': 1},
                {'event': 'result', 'case_id': A, 'invocation': 1, 'status': 'pass'},
                {'event': 'start', 'case_id': B, 'invocation': 2},
                {'event': 'result', 'case_id': B, 'invocation': 2, 'status': 'pass'},
            ),
            returncode=0,
        )

        self.assertTrue(report.ok)
        self.assertEqual((A, B), report.passed_ids)
        self.assertEqual((), report.errors)

    def test_repeated_catalog_id_is_accounted_by_invocation_without_false_duplicate(self):
        report = self.probe.account_events(
            (A, A),
            (
                {'event': 'start', 'case_id': A, 'invocation': 1},
                {'event': 'result', 'case_id': A, 'invocation': 1, 'status': 'pass'},
                {'event': 'start', 'case_id': A, 'invocation': 2},
                {'event': 'result', 'case_id': A, 'invocation': 2, 'status': 'pass'},
            ),
            returncode=0,
        )

        self.assertTrue(report.ok)
        self.assertEqual((A, A), report.passed_ids)

    def test_results_require_an_ordered_matching_start_event(self):
        result_without_start = self.probe.account_events(
            (A,),
            ({'event': 'result', 'case_id': A, 'invocation': 1, 'status': 'pass'},),
            returncode=0,
        )
        self.assertFalse(result_without_start.ok)
        self.assertIn(f'result for {A} invocation 1 has no matching active start', result_without_start.errors)

        reordered = self.probe.account_events(
            (A, B),
            (
                {'event': 'start', 'case_id': B, 'invocation': 2},
                {'event': 'result', 'case_id': B, 'invocation': 2, 'status': 'pass'},
            ),
            returncode=0,
        )
        self.assertFalse(reordered.ok)
        self.assertIn(f'out-of-order start for {B} invocation 2; expected {A} invocation 1', reordered.errors)

    def test_assertion_failure_is_attributed_and_does_not_hide_later_results(self):
        report = self.probe.account_events(
            (A, B),
            (
                {'event': 'start', 'case_id': A, 'invocation': 1},
                {'event': 'result', 'case_id': A, 'invocation': 1, 'status': 'fail', 'message': 'static sentinel leaked'},
                {'event': 'start', 'case_id': B, 'invocation': 2},
                {'event': 'result', 'case_id': B, 'invocation': 2, 'status': 'pass'},
            ),
            returncode=1,
        )

        self.assertFalse(report.ok)
        self.assertEqual((B,), report.passed_ids)
        self.assertIn(f'{A} invocation 1: static sentinel leaked', report.errors)
        self.assertNotIn(B, ' '.join(report.errors))

    def test_missing_duplicate_and_unknown_results_each_fail_closed(self):
        report = self.probe.account_events(
            (A, B),
            (
                {'event': 'start', 'case_id': A, 'invocation': 1},
                {'event': 'result', 'case_id': A, 'invocation': 1, 'status': 'pass'},
                {'event': 'result', 'case_id': A, 'invocation': 1, 'status': 'pass'},
                {'event': 'result', 'case_id': B, 'invocation': 99, 'status': 'pass'},
            ),
            returncode=0,
        )

        self.assertFalse(report.ok)
        joined = '\n'.join(report.errors)
        self.assertIn(f'duplicate result for {A} invocation 1', joined)
        self.assertIn(f'unknown result for {B} invocation 99', joined)
        self.assertIn(f'missing result for {B} invocation 2', joined)

    def test_timeout_names_active_case_and_preserves_log_location(self):
        report = self.probe.account_events(
            (A, B),
            (
                {'event': 'start', 'case_id': A, 'invocation': 1},
                {'event': 'result', 'case_id': A, 'invocation': 1, 'status': 'pass'},
                {'event': 'start', 'case_id': B, 'invocation': 2},
            ),
            returncode=None,
            timed_out=True,
            log_path=Path('.godot/same-scene-probe.log'),
        )

        self.assertFalse(report.ok)
        summary = report.summary()
        self.assertIn(f'timeout while {B} invocation 2 was active', summary)
        self.assertIn('.godot/same-scene-probe.log', summary)

    def test_abrupt_exit_names_active_case_and_missing_following_cases(self):
        report = self.probe.account_events(
            (A, B),
            ({'event': 'start', 'case_id': A, 'invocation': 1},),
            returncode=23,
        )

        self.assertFalse(report.ok)
        joined = '\n'.join(report.errors)
        self.assertIn(f'process exited 23 while {A} invocation 1 was active', joined)
        self.assertIn(f'missing result for {A} invocation 1', joined)
        self.assertIn(f'missing result for {B} invocation 2', joined)

    def test_empty_invalid_or_uncharacterized_selected_ids_are_rejected_before_execution(self):
        for selected in (('Bad ID',), ('smoke-test',), ()):
            with self.subTest(selected=selected), self.assertRaises(ValueError):
                self.probe.account_events(selected, (), returncode=0)

    def test_jsonl_parser_rejects_malformed_or_non_object_events(self):
        line = json.dumps({'event': 'start', 'case_id': A, 'invocation': 1})
        self.assertEqual(
            ({'event': 'start', 'case_id': A, 'invocation': 1},),
            self.probe.parse_event_lines((line,)),
        )

        for content in ('not-json', '[]'):
            with self.subTest(content=content), self.assertRaises(ValueError):
                self.probe.parse_event_lines((content,))

    def test_command_builder_keeps_log_and_event_paths_explicit(self):
        command = self.probe.build_godot_command(
            Path('godot'),
            ROOT,
            Path('.godot/probe.log'),
            Path('.godot/probe.jsonl'),
            (A, B),
            leak='input',
            failure='none',
        )

        self.assertEqual('godot', command[0])
        self.assertIn('--headless', command)
        self.assertIn('--log-file', command)
        self.assertIn('.godot/probe.log', command)
        self.assertIn('res://tests/integration/SameSceneReuseProbe.tscn', command)
        self.assertIn('--probe-events', command)
        self.assertIn('.godot/probe.jsonl', command)
        self.assertIn(f'{A},{B}', command)
        self.assertEqual('input', command[-3])

    def test_output_directory_must_resolve_inside_the_workspace(self):
        self.assertEqual(
            (ROOT / '.godot/probe').resolve(),
            self.probe.resolve_output_dir(ROOT, Path('.godot/probe')),
        )
        for path in (Path('../escape'), ROOT.parent / 'absolute-escape'):
            with self.subTest(path=path), self.assertRaises(ValueError):
                self.probe.resolve_output_dir(ROOT, path)

    def test_parent_removes_stale_event_file_before_process_start(self):
        relative = Path('.godot/same-scene-probe-stale-test.jsonl')
        target = ROOT / relative
        target.write_text('{"stale": true}\n', encoding='utf-8')
        try:
            self.probe.prepare_event_path(ROOT, relative)
            self.assertFalse(target.exists())
        finally:
            target.unlink(missing_ok=True)


if __name__ == '__main__':
    unittest.main()
