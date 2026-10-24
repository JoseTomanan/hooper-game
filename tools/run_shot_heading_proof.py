"""Run the isolated normal-shot production-scene angle/entry/hand matrix (#428)."""
import argparse
import json
import os
from pathlib import Path
import subprocess
from concurrent.futures import ThreadPoolExecutor

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--godot', default=os.environ.get('GODOT', 'godot'))
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--diagnostic', action='store_true')
    parser.add_argument('--jobs', type=int, default=2)
    parser.add_argument('--suite', choices=('matrix', 'edges', 'all'), default='matrix')
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    cases = []
    if args.suite != 'edges':
        cases += [('matrix', angle, entry, hand) for angle in (0, 45, 89, 90, 91, 135, 180)
                  for entry in ('stationary', 'moving') for hand in ('Left', 'Right')]
    if args.suite != 'matrix':
        cases += [(scenario, angle, 'stationary', 'Right') for scenario, angle in (
            ('unfinished', 180), ('feint-early', 180), ('feint-late', 180),
            ('illegal-early', 180), ('illegal-late', 180), ('loss', 180), ('oob', 180),
            ('missing', 180), ('degenerate', 180), ('retarget', 180), ('contact-retarget', 180), ('opposite', -135),
            ('release-below', 89.9), ('release-at', 90), ('release-above', 90.1))]

    def run(case):
        scenario, angle, entry, hand = case
        name = f'{angle}-{entry}-{hand}' if scenario == 'matrix' else scenario
        command = [args.godot, '--headless', '--path', str(ROOT), '--log-file', str(output/f'{name}.godot.log'),
                   'res://tests/integration/JumpShotHeadingTest.tscn', '--', f'--harness-scenario={scenario}',
                   f'--angle={angle}', f'--entry={entry}', f'--hand={hand}',
                   f'--diagnostic={str(args.diagnostic).lower()}', f'--proof-trace={output/name}.json']
        result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, timeout=40)
        (output/f'{name}.log').write_text(result.stdout+result.stderr)
        print(f'{name}: {result.returncode}', flush=True)
        return {'name': name, 'exit': result.returncode}

    with ThreadPoolExecutor(max_workers=args.jobs) as pool:
        results = list(pool.map(run, cases))
    (output/'summary.json').write_text(json.dumps(results, indent=2))
    return int(any(case['exit'] for case in results))


if __name__ == '__main__':
    raise SystemExit(main())
