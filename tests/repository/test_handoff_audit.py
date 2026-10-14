"""Read-only handoff classification through the report's public interface."""

import sys
from pathlib import Path
import shutil
import uuid
import contextlib
import io
import subprocess
from unittest.mock import Mock, patch
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools'))
import audit_handoffs as audit


class HandoffAuditTests(unittest.TestCase):
    def setUp(self):
        (ROOT / '.godot').mkdir(exist_ok=True)
        self.repo = ROOT / '.godot' / ('handoff-audit-' + uuid.uuid4().hex)
        self.repo.mkdir()
        self.addCleanup(shutil.rmtree, self.repo)
        self.handoffs = self.repo / 'docs' / 'handoffs'
        self.handoffs.mkdir(parents=True)

    def write(self, name, issues):
        (self.handoffs / name).write_text(
            '---\nhandoff_version: 1\nrepo: JoseTomanan/hooper-game\n'
            f'issues: {issues}\n---\nSECRET body #999\n', encoding='utf-8')

    def test_all_issue_states_required_and_cached(self):
        self.write('a.md', '[1, 2]')
        self.write('b.md', '[2]')
        self.write('c.md', '[1, 3]')
        calls = []
        def lookup(repo, number):
            calls.append((repo, number))
            return {'number': number, 'state': {1: 'open', 2: 'closed', 3: None}[number]}
        report = audit.audit(self.repo, lookup=lookup)
        self.assertEqual(['a.md'], [e['filename'] for e in report['active']])
        self.assertEqual(['b.md'], [e['filename'] for e in report['stale']])
        self.assertEqual(['c.md'], [e['filename'] for e in report['unknown']])
        self.assertEqual(3, len(calls))

    def test_strict_metadata_and_legacy_names_do_not_read_body(self):
        for name, body in {
            '410-topic.md': 'ordinary body #1\n',
            '411-bad.md': '---\nhandoff_version: 1\nissues: [411]\n---\n',
            '321-320-conflict.md': 'body #410',
            'notes.md': 'body Closes #410',
            '412-good.md': '---\nhandoff_version: 1\nrepo: JoseTomanan/hooper-game\nissues: [412]\n---\n' + 'SECRET' * 10000,
            'README.MD': 'ignored',
        }.items():
            (self.handoffs / name).write_text(body, encoding='utf-8')
        report = audit.audit(self.repo, lookup=lambda repo, n: {'number': n, 'state': 'closed'})
        self.assertEqual(['410-topic.md', '412-good.md'], [e['filename'] for e in report['stale']])
        self.assertEqual(['321-320-conflict.md', '411-bad.md', 'notes.md'], [e['filename'] for e in report['unknown']])
        self.assertNotIn('SECRET', str(report))

    def test_unsupported_entries_unreadable_and_offline_are_unknown(self):
        self.write('valid.md', '[410]')
        (self.handoffs / 'binary.txt').write_bytes(b'ignored')
        lookup = Mock(return_value={'number': 410, 'state': 'closed'})
        report = audit.audit(self.repo, offline=True, lookup=lookup)
        lookup.assert_not_called()
        self.assertEqual(2, len(report['unknown']))
        with patch.object(Path, 'open', side_effect=PermissionError('SECRET')):
            report = audit.audit(self.repo, offline=True)
        self.assertNotIn('SECRET', str(report))

    def test_invalid_states_prs_and_lookup_failures_are_unknown(self):
        self.write('valid.md', '[1, 2]')
        for value in ({'number': 2, 'state': 'open'}, {'number': True, 'state': 'closed'},
                      {'number': 1, 'state': 'closed', 'pull_request': {}},
                      {'number': 1}, {'error': []}, None):
            report = audit.audit(self.repo, lookup=lambda repo, n: value)
            self.assertEqual(1, len(report['unknown']))
        def failed(*args):
            raise RuntimeError('SECRET')
        self.assertNotIn('SECRET', str(audit.audit(self.repo, lookup=failed)))

    def test_missing_handoff_directory_is_empty(self):
        shutil.rmtree(self.handoffs)
        self.assertEqual({'active': [], 'stale': [], 'unknown': []}, audit.audit(self.repo, offline=True))

    def test_recursive_inventory_is_sorted_and_bounds_omit_subtrees(self):
        nested = self.handoffs / 'nested'
        nested.mkdir()
        (nested / '410-topic.md').write_text(
            '---\nhandoff_version: 1\nrepo: JoseTomanan/hooper-game\nissues: [410]\n---\nSECRET', encoding='utf-8')
        (nested / 'README.md').write_text('ignored', encoding='utf-8')
        report = audit.audit(self.repo, lookup=lambda repo, n: {'number': n, 'state': 'closed'})
        self.assertEqual(['nested/410-topic.md'], [e['filename'] for e in report['stale']])
        self.assertIn('depth', str(audit.audit(self.repo, offline=True, max_depth=0)))
        self.assertIn('entry limit', str(audit.audit(self.repo, offline=True, max_entries=1)))

    def test_nested_chapter_numbers_do_not_infer_issue_identity(self):
        nested = self.handoffs / 'anim-clips'
        nested.mkdir()
        (nested / '10-spin.md').write_text('Chapter ten SECRET', encoding='utf-8')
        (self.handoffs / '410-topic.md').write_text('ordinary body', encoding='utf-8')
        calls = []
        def lookup(repo, n):
            calls.append(n)
            return {'number': n, 'state': 'closed'}
        report = audit.audit(self.repo, lookup=lookup)
        self.assertEqual(['410-topic.md'], [e['filename'] for e in report['stale']])
        self.assertEqual(['anim-clips/10-spin.md'], [e['filename'] for e in report['unknown']])
        self.assertEqual([410], calls)

    def test_cli_is_read_only_and_escapes_names(self):
        self.write('valid.md', '[410]')
        before = {p.relative_to(self.repo): p.read_bytes() for p in self.repo.rglob('*') if p.is_file()}
        for flags in ([], ['--offline'], ['--json'], ['--offline', '--json']):
            out = io.StringIO()
            with contextlib.redirect_stdout(out), patch.object(audit, 'GitHubLookup') as lookup:
                lookup.return_value.return_value = {'number': 410, 'state': 'closed'}
                self.assertEqual(0, audit.main(['--repo-root', str(self.repo)] + flags))
            self.assertNotIn('SECRET', out.getvalue())
        self.assertEqual(before, {p.relative_to(self.repo): p.read_bytes() for p in self.repo.rglob('*') if p.is_file()})

    def test_github_adapter_bounds_time_calls_and_sanitizes_failures(self):
        completed = subprocess.CompletedProcess([], 0, '{"number":410,"state":"closed","pull_request":false}', '')
        with patch.object(audit.subprocess, 'run', return_value=completed) as run:
            lookup = audit.GitHubLookup(max_calls=1)
            self.assertEqual('closed', lookup(audit.REPOSITORY, 410)['state'])
            self.assertEqual('closed', lookup(audit.REPOSITORY, 410)['state'])
            self.assertEqual('lookup call budget exhausted', lookup(audit.REPOSITORY, 411)['error'])
            self.assertEqual(1, run.call_count)
            self.assertFalse(run.call_args.kwargs['shell'])
            self.assertLessEqual(run.call_args.kwargs['timeout'], 5)
        with patch.object(audit.subprocess, 'run', side_effect=OSError('SECRET')):
            self.assertNotIn('SECRET', str(audit.GitHubLookup()(audit.REPOSITORY, 410)))

    def test_malformed_metadata_is_unknown_without_filename_fallback(self):
        for fields in ('issues: [true]', 'issues: [0]', 'issues: ["410"]', 'issues: []',
                       'issues: [410]\nissues: [411]', 'extra: SECRET\nissues: [410]',
                       'issues: [410]\nbaseline: nope', 'issues: [' + ','.join(['410'] * 17) + ']',
                       'issues: [410]\n' + 'x' * 5000):
            (self.handoffs / '410-topic.md').write_text(
                '---\nhandoff_version: 1\nrepo: JoseTomanan/hooper-game\n' + fields + '\n---\nSECRET', encoding='utf-8')
            lookup = Mock(side_effect=lambda repo, n: {'number': n, 'state': 'closed'})
            report = audit.audit(self.repo, lookup=lookup)
            lookup.assert_not_called()
            self.assertEqual(1, len(report['unknown']))
            self.assertNotIn('SECRET', str(report))
        for delimiter in (' --- ', '\ufeff---'):
            (self.handoffs / '410-topic.md').write_text(delimiter + '\nSECRET', encoding='utf-8')
            lookup = Mock(side_effect=lambda repo, n: {'number': n, 'state': 'closed'})
            report = audit.audit(self.repo, lookup=lookup)
            lookup.assert_not_called()
            self.assertEqual(1, len(report['unknown']))

    def test_required_metadata_and_valid_baseline_deduplication(self):
        prefix = '---\nhandoff_version: 1\nrepo: JoseTomanan/hooper-game\nissues: [410]\n'
        for text in (prefix.replace('version: 1', 'version: 2') + '---\n',
                     prefix.replace('JoseTomanan/hooper-game', 'other/repo') + '---\n',
                     prefix.replace('issues: [410]\n', '') + '---\n', prefix,
                     prefix + '\n' * 40 + '---\n'):
            (self.handoffs / '410-topic.md').write_text(text, encoding='utf-8')
            lookup = Mock(side_effect=lambda repo, n: {'number': n, 'state': 'closed'})
            self.assertEqual(1, len(audit.audit(self.repo, lookup=lookup)['unknown']))
            lookup.assert_not_called()
        (self.handoffs / '410-topic.md').write_text(
            prefix.replace('[410]', '[410, 410]') + 'baseline: ' + 'a' * 40 + '\n---\nSECRET', encoding='utf-8')
        calls = []
        def lookup(repo, n):
            calls.append(n)
            return {'number': n, 'state': 'closed'}
        report = audit.audit(self.repo, lookup=lookup)
        self.assertEqual([410], calls)
        self.assertEqual(1, len(report['stale'][0]['issues']))

    def test_discovery_bounds_scanner_consumption_and_closes_on_overflow(self):
        class Scanner:
            count = 0
            closed = False
            def __enter__(self):
                return self
            def __exit__(self, *args):
                self.closed = True
            def __iter__(self):
                return self
            def __next__(self):
                self.count += 1
                return type('Entry', (), {'path': str(self.handoffs / f'{self.count}.md')})()
        scanner = Scanner()
        scanner.handoffs = self.handoffs
        with patch.object(audit.os, 'scandir', return_value=scanner), \
             patch.object(Path, 'iterdir', side_effect=AssertionError('eager enumeration')), \
             patch.object(Path, 'open', side_effect=AssertionError('overflow contents read')):
            report = audit.audit(self.repo, offline=True, max_entries=2)
        self.assertIn('entry limit', str(report))
        self.assertEqual(3, scanner.count)
        self.assertTrue(scanner.closed)

    def test_metadata_read_stops_at_delimiter_and_at_byte_limit(self):
        class Observed(io.BytesIO):
            def close(self):
                self.position = self.tell()
        header = b'---\nhandoff_version: 1\nrepo: JoseTomanan/hooper-game\nissues: [410]\n---\n'
        stream = Observed(header + b'SECRET' * 1000)
        with patch.object(Path, 'open', return_value=stream):
            self.assertEqual([410], audit.identity(Path('notes.md'))[0])
        self.assertEqual(len(header), stream.position)
        stream = Observed(b'---\n' + b'x' * 5000)
        with patch.object(Path, 'open', return_value=stream):
            self.assertIsNone(audit.identity(Path('410-topic.md'))[0])
        self.assertLessEqual(stream.position, 4096)

    def test_redirected_entries_and_roots_are_not_followed(self):
        self.write('valid.md', '[410]')
        original = Path.lstat
        class Reparse:
            st_mode = 0o100644
            st_file_attributes = 0x400
        with patch.object(Path, 'lstat', autospec=True, side_effect=lambda p: Reparse() if p.name == 'valid.md' else original(p)):
            self.assertIn('redirected', str(audit.audit(self.repo, offline=True)))
        with patch.object(Path, 'lstat', autospec=True, side_effect=lambda p: Reparse() if p.name == 'handoffs' else original(p)):
            self.assertIn('redirected', str(audit.audit(self.repo, offline=True)))

    def test_real_links_are_not_followed(self):
        outside = self.repo / 'outside'
        outside.mkdir()
        secret = outside / '410-secret.md'
        secret.write_text('SECRET', encoding='utf-8')
        try:
            (self.handoffs / '410-link.md').symlink_to(secret)
            (self.handoffs / 'nested-link').symlink_to(outside, target_is_directory=True)
        except OSError:
            self.skipTest('symbolic links unavailable for this account')
        with patch.object(Path, 'open', side_effect=AssertionError('must not read linked contents')):
            report = audit.audit(self.repo, offline=True)
        self.assertEqual(2, len(report['unknown']))
        self.assertNotIn('SECRET', str(report))
        for path in self.handoffs.iterdir():
            path.unlink()
        self.handoffs.rmdir()
        self.handoffs.symlink_to(outside, target_is_directory=True)
        with patch.object(Path, 'open', side_effect=AssertionError('must not read redirected root')):
            self.assertIn('redirected', str(audit.audit(self.repo, offline=True)))

    def test_adapter_deadline_failures_are_cached_and_do_not_leak_output(self):
        with patch.object(audit.time, 'monotonic', side_effect=[0, 21]), patch.object(audit.subprocess, 'run') as run:
            self.assertEqual('lookup time budget exhausted', audit.GitHubLookup()(audit.REPOSITORY, 410)['error'])
            run.assert_not_called()
        failures = [OSError('SECRET'), subprocess.TimeoutExpired('gh SECRET', 1),
                    subprocess.CompletedProcess([], 1, 'SECRET', 'SECRET'),
                    subprocess.CompletedProcess([], 0, '{SECRET', 'SECRET'),
                    subprocess.CompletedProcess([], 0, 'null', ''),
                    subprocess.CompletedProcess([], 0, '{"number":410,"state":"closed","padding":"' + 'x' * 5000 + '"}', '')]
        for failure in failures:
            kwargs = {'side_effect': failure} if isinstance(failure, Exception) else {'return_value': failure}
            with patch.object(audit.subprocess, 'run', **kwargs) as run:
                lookup = audit.GitHubLookup()
                result = lookup(audit.REPOSITORY, 410)
                self.assertEqual(result, lookup(audit.REPOSITORY, 410))
                self.assertEqual(1, run.call_count)
                self.assertNotIn('SECRET', str(result))
                self.assertIn('error', result)


if __name__ == '__main__':
    unittest.main()
