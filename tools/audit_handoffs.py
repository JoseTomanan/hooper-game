"""Report scratch handoff issue states; never modify or prune handoffs."""

from pathlib import Path
import json
import re
import stat
import itertools
import argparse
import subprocess
import time
import os

REPOSITORY = 'JoseTomanan/hooper-game'


def is_redirected(info):
    return (stat.S_ISLNK(info.st_mode) or
            bool(getattr(info, 'st_file_attributes', 0) & getattr(stat, 'FILE_ATTRIBUTE_REPARSE_POINT', 0x400)))


class GitHubLookup:
    """Bounded GET-only adapter; cached failures avoid repeated remote calls."""

    def __init__(self, *, max_calls=64, budget_seconds=20):
        self.max_calls = min(max_calls, 64)
        self.deadline = time.monotonic() + min(budget_seconds, 20)
        self.calls = 0
        self.cache = {}

    def __call__(self, repository, number):
        key = repository, number
        if key in self.cache:
            return self.cache[key]
        remaining = self.deadline - time.monotonic()
        if self.calls >= self.max_calls:
            result = {'error': 'lookup call budget exhausted'}
        elif remaining <= 0:
            result = {'error': 'lookup time budget exhausted'}
        else:
            self.calls += 1
            try:
                # argv + shell=False avoids shell interpretation; timeout kills/waits.
                # https://docs.python.org/3.10/library/subprocess.html#subprocess.run
                # GET and --jq projection: https://cli.github.com/manual/gh_api
                # Issues endpoint also returns PRs; retain its discriminator.
                # https://docs.github.com/en/rest/issues/issues#get-an-issue
                response = subprocess.run([
                    'gh', 'api', f'repos/{repository}/issues/{number}', '--method', 'GET',
                    '--jq', '{number: .number, state: .state, pull_request: has("pull_request")}',
                ], shell=False, capture_output=True, text=True, timeout=min(5, remaining))
                if response.returncode != 0:
                    result = {'error': 'GitHub lookup failed'}
                elif len(response.stdout) > 4096:
                    result = {'error': 'invalid GitHub response'}
                else:
                    result = json.loads(response.stdout)
                if not isinstance(result, dict):
                    result = {'error': 'invalid GitHub response'}
            except subprocess.TimeoutExpired:
                result = {'error': 'GitHub lookup timed out'}
            except (OSError, ValueError, RecursionError):
                result = {'error': 'GitHub lookup unavailable or invalid response'}
        self.cache[key] = result
        return result


def identity(path, *, allow_legacy=True):
    """Read only bounded front matter, never the handoff body."""
    fields = {}
    with path.open('rb', buffering=0) as stream:
        first = stream.readline(4096)
        if first.rstrip(b'\r\n') != b'---':
            if first.lstrip(b'\xef\xbb\xbf').strip() == b'---':
                return None, 'invalid metadata delimiter'
            if not allow_legacy:
                return None, 'nested legacy filename is ambiguous; metadata required'
            match = re.fullmatch(r'([1-9][0-9]*)-([A-Za-z].*)\.md', path.name, re.IGNORECASE)
            if not match or re.search(r'(?:^|[-_])[0-9]+(?:[-_]|$)', match[2]):
                return None, 'no unambiguous issue identity'
            return [int(match[1])], None
        size = len(first)
        for _ in range(31):
            line = stream.readline(4096 - size)
            size += len(line)
            if size > 4096 or not line:
                return None, 'invalid or oversized metadata'
            if line.rstrip(b'\r\n') == b'---':
                break
            try:
                key, value = line.decode('utf-8').rstrip('\r\n').split(':', 1)
            except (UnicodeError, ValueError):
                return None, 'invalid metadata'
            if key not in ('handoff_version', 'repo', 'issues', 'baseline') or key in fields:
                return None, 'invalid metadata'
            fields[key] = value.strip()
        else:
            return None, 'metadata line limit exceeded'
    if fields.get('handoff_version') != '1' or fields.get('repo') != REPOSITORY:
        return None, 'invalid metadata identity'
    if 'baseline' in fields and not re.fullmatch(r'[0-9a-fA-F]{40}', fields['baseline']):
        return None, 'invalid metadata baseline'
    try:
        issues = json.loads(fields.get('issues', 'null'))
    except (ValueError, RecursionError):
        return None, 'invalid metadata issues'
    if not isinstance(issues, list) or not 1 <= len(issues) <= 16 or any(type(n) is not int or n <= 0 for n in issues):
        return None, 'invalid metadata issues'
    return list(dict.fromkeys(issues)), None


def audit(repo_root, *, lookup=None, offline=False, max_entries=1000, max_depth=8):
    report = {'active': [], 'stale': [], 'unknown': []}
    cache = {}
    if lookup is None and not offline:
        lookup = GitHubLookup()
    # resolve follows links; is_relative_to alone is only lexical containment.
    # https://docs.python.org/3.10/library/pathlib.html#pathlib.Path.resolve
    try:
        root = Path(repo_root).resolve()
        docs = root / 'docs'
        directory = docs / 'handoffs'
        if not docs.resolve().is_relative_to(root) or not directory.resolve().is_relative_to(docs):
            raise ValueError('redirected directory')
        for parent in (docs, directory):
            if is_redirected(parent.lstat()):
                raise ValueError('redirected directory')
        if not directory.exists():
            return report
    except FileNotFoundError:
        return report
    except (OSError, ValueError, RuntimeError):
        report['unknown'].append({'filename': 'docs/handoffs', 'issues': [], 'reason': 'handoff directory unavailable or redirected'})
        return report
    paths = []
    remaining = max_entries
    def unknown(path, reason):
        report['unknown'].append({'filename': path.relative_to(directory).as_posix() or '.', 'issues': [], 'reason': reason})

    def discover(parent, depth):
        nonlocal remaining
        try:
            # scandir is lazy and context-managed; Path.iterdir eagerly lists
            # directories on supported Python versions, defeating this bound.
            # https://docs.python.org/3.10/library/os.html#os.scandir
            with os.scandir(parent) as entries:
                candidates = [Path(entry.path) for entry in itertools.islice(entries, remaining + 1)]
            if len(candidates) > remaining:
                unknown(parent, 'entry limit; subtree omitted')
                remaining = 0
                return
            remaining -= len(candidates)
            for path in sorted(candidates, key=lambda p: p.name):
                if path.name.lower() == 'readme.md':
                    continue
                try:
                    info = path.lstat()
                    redirected = is_redirected(info) or not path.resolve().is_relative_to(directory)
                except (OSError, ValueError, RuntimeError):
                    unknown(path, 'unreadable entry')
                    continue
                if redirected:
                    unknown(path, 'unsupported or redirected entry')
                elif stat.S_ISDIR(info.st_mode):
                    if depth >= max_depth:
                        unknown(path, 'depth limit; subtree omitted')
                    else:
                        discover(path, depth + 1)
                else:
                    paths.append(path)
        except (OSError, ValueError, RuntimeError):
            unknown(parent, 'unreadable directory or entry')

    discover(directory, 0)
    for path in sorted(paths, key=lambda p: p.relative_to(directory).as_posix()):
        if path.name.lower() == 'readme.md':
            continue
        try:
            info = path.lstat()
            if (not stat.S_ISREG(info.st_mode) or
                    getattr(info, 'st_file_attributes', 0) & getattr(stat, 'FILE_ATTRIBUTE_REPARSE_POINT', 0x400) or
                    not path.resolve().is_relative_to(directory.resolve()) or path.suffix.lower() != '.md'):
                issues, reason = None, 'unsupported or redirected entry'
            else:
                issues, reason = identity(path, allow_legacy=path.parent == directory)
        except (OSError, ValueError, RuntimeError):
            issues, reason = None, 'unreadable entry'
        if reason:
            unknown(path, reason)
            continue
        evidence = []
        for number in issues:
            if number not in cache:
                try:
                    cache[number] = None if offline else lookup(REPOSITORY, number)
                except Exception:
                    cache[number] = None
            value = cache[number]
            valid = (isinstance(value, dict) and type(value.get('number')) is int and
                     value['number'] == number and value.get('pull_request', False) is False and
                     value.get('state') in ('open', 'closed'))
            allowed_errors = {'lookup call budget exhausted', 'lookup time budget exhausted',
                              'GitHub lookup failed', 'invalid GitHub response', 'GitHub lookup timed out',
                              'GitHub lookup unavailable or invalid response'}
            error = value.get('error') if isinstance(value, dict) else None
            reason = ('issue state verified' if valid else 'offline' if offline else
                      error if isinstance(error, str) and error in allowed_errors else 'lookup unavailable or invalid issue response')
            evidence.append({'number': number, 'state': value['state'] if valid else 'unknown', 'reason': reason})
        states = [e['state'] for e in evidence]
        group = 'unknown' if 'unknown' in states else 'active' if 'open' in states else 'stale'
        report[group].append({'filename': path.relative_to(directory).as_posix(), 'issues': evidence, 'reason': group})
    for entries in report.values():
        entries.sort(key=lambda entry: entry['filename'])
    return report


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo-root', type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument('--offline', action='store_true', help='Disable GitHub lookups; known identities remain unknown.')
    parser.add_argument('--json', action='store_true', help='Print the structured report.')
    args = parser.parse_args(argv)
    report = audit(args.repo_root, offline=args.offline)
    if args.json:
        print(json.dumps(report, ensure_ascii=True, indent=2))
    else:
        for group, entries in report.items():
            print(f'{group}: {len(entries)}')
            for entry in entries:
                print(f'  {json.dumps(entry["filename"], ensure_ascii=True)}: {entry["reason"]}')
                for issue in entry['issues']:
                    print(f'    #{issue["number"]}: {issue["state"]} ({issue["reason"]})')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
