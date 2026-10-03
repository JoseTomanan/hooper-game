import importlib.util
import json
import sys
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / 'tools' / 'same_scene_batch_probe.py'


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
            ('probe-a', 'probe-b'),
            (
                {'event': 'start', 'case_id': 'probe-a'},
                {'event': 'result', 'case_id': 'probe-a', 'status': 'pass'},
                {'event': 'start', 'case_id': 'probe-b'},
                {'event': 'result', 'case_id': 'probe-b', 'status': 'pass'},
            ),
            returncode=0,
        )

        self.assertTrue(report.ok)
        self.assertEqual(('probe-a', 'probe-b'), report.passed_ids)
        self.assertEqual((), report.errors)

    def test_assertion_failure_is_attributed_and_does_not_hide_later_results(self):
        report = self.probe.account_events(
            ('probe-a', 'probe-b'),
            (
                {'event': 'start', 'case_id': 'probe-a'},
                {'event': 'result', 'case_id': 'probe-a', 'status': 'fail', 'message': 'static sentinel leaked'},
                {'event': 'start', 'case_id': 'probe-b'},
                {'event': 'result', 'case_id': 'probe-b', 'status': 'pass'},
            ),
            returncode=1,
        )

        self.assertFalse(report.ok)
        self.assertEqual(('probe-b',), report.passed_ids)
        self.assertIn('probe-a: static sentinel leaked', report.errors)
        self.assertNotIn('probe-b', ' '.join(report.errors))

    def test_missing_duplicate_and_unknown_results_each_fail_closed(self):
        report = self.probe.account_events(
            ('probe-a', 'probe-b'),
            (
                {'event': 'start', 'case_id': 'probe-a'},
                {'event': 'result', 'case_id': 'probe-a', 'status': 'pass'},
                {'event': 'result', 'case_id': 'probe-a', 'status': 'pass'},
                {'event': 'result', 'case_id': 'not-selected', 'status': 'pass'},
            ),
            returncode=0,
        )

        self.assertFalse(report.ok)
        joined = '\n'.join(report.errors)
        self.assertIn('duplicate result for probe-a', joined)
        self.assertIn('unknown result for not-selected', joined)
        self.assertIn('missing result for probe-b', joined)

    def test_timeout_names_active_case_and_preserves_log_location(self):
        report = self.probe.account_events(
            ('probe-a', 'probe-b'),
            (
                {'event': 'start', 'case_id': 'probe-a'},
                {'event': 'result', 'case_id': 'probe-a', 'status': 'pass'},
                {'event': 'start', 'case_id': 'probe-b'},
            ),
            returncode=None,
            timed_out=True,
            log_path=Path('.godot/same-scene-probe.log'),
        )

        self.assertFalse(report.ok)
        summary = report.summary()
        self.assertIn('timeout while probe-b was active', summary)
        self.assertIn('.godot/same-scene-probe.log', summary)

    def test_abrupt_exit_names_active_case_and_missing_following_cases(self):
        report = self.probe.account_events(
            ('probe-a', 'probe-b'),
            ({'event': 'start', 'case_id': 'probe-a'},),
            returncode=23,
        )

        self.assertFalse(report.ok)
        joined = '\n'.join(report.errors)
        self.assertIn('process exited 23 while probe-a was active', joined)
        self.assertIn('missing result for probe-a', joined)
        self.assertIn('missing result for probe-b', joined)

    def test_duplicate_or_invalid_selected_ids_are_rejected_before_execution(self):
        for selected in (('probe-a', 'probe-a'), ('Bad ID',), ()):
            with self.subTest(selected=selected), self.assertRaises(ValueError):
                self.probe.account_events(selected, (), returncode=0)

    def test_jsonl_parser_rejects_malformed_or_non_object_events(self):
        line = json.dumps({'event': 'start', 'case_id': 'probe-a'})
        self.assertEqual(
            ({'event': 'start', 'case_id': 'probe-a'},),
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
            ('probe-a', 'probe-b'),
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
        self.assertIn('probe-a,probe-b', command)
        self.assertEqual('input', command[-3])

    def test_output_directory_must_resolve_inside_the_workspace(self):
        self.assertEqual(
            (ROOT / '.godot/probe').resolve(),
            self.probe.resolve_output_dir(ROOT, Path('.godot/probe')),
        )
        for path in (Path('../escape'), ROOT.parent / 'absolute-escape'):
            with self.subTest(path=path), self.assertRaises(ValueError):
                self.probe.resolve_output_dir(ROOT, path)


if __name__ == '__main__':
    unittest.main()
