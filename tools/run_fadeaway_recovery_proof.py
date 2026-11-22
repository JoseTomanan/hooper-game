"""Prove a production-input fadeaway landing and discriminate ordinary recovery.

Uses production turn defaults and final evaluated skeleton samples. The ordinary
Recovery substitution leaves the new turn/classification path live. Headless
measurements establish geometry and routing, not visual or feel acceptance.
"""
import argparse
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import uuid

ROOT = Path(__file__).resolve().parents[1]


def measure(directory, kind):
    manifest = json.loads((directory / 'manifest.json').read_text())
    rows = [json.loads(line) for line in (directory / 'trace.jsonl').read_text().splitlines()]
    if manifest.get('result') != 'captured' or manifest.get('headingRateOverride'):
        raise ValueError(f'{directory}: capture failed or heading rate overridden')
    releases = [e for e in manifest['events'] if e['kind'] == 'release']
    if kind == 'fake':
        if releases or not manifest.get('sawAcceptedFeintRecovery'):
            raise ValueError(f'{directory}: fake premise missing')
    elif len(releases) != 1 or bool(releases[0]['fadeaway']) != (kind == 'fadeaway'):
        raise ValueError(f'{directory}: release/classification premise failed: {releases}')
    if kind == 'fadeaway' and releases[0]['residualAngle'] < math.pi / 2:
        raise ValueError(f'{directory}: release angle is below fadeaway threshold')
    expected = 'FadeawayRecovery' if kind == 'fadeaway' else 'JumpshotRecovery'
    frames = {}
    for row in rows:
        pose = row.get('pose') or {}
        if (pose.get('physicsFrame') != row.get('physicsFrame') or pose.get('move') != 'jumpshot'
                or pose.get('phase') != 'Recovery'):
            continue
        frame = pose['frame']
        if pose.get('animation') != expected:
            raise ValueError(f'{directory}: Recovery {frame} played {pose.get("animation")} not {expected}')
        bones = pose['bones']
        left = bones['mixamorig_LeftFoot']['world'][1]
        right = bones['mixamorig_RightFoot']['world'][1]
        if not math.isfinite(left) or not math.isfinite(right):
            raise ValueError(f'{directory}: nonfinite foot pose')
        frames[frame] = abs(left - right)
    if kind == 'fake':
        if len(frames) < 2:
            raise ValueError(f'{directory}: fake recovery pose missing')
    elif kind == 'fadeaway':
        for frame in (4, 6, 18):
            if frame not in frames:
                raise ValueError(f'{directory}: missing evaluated Recovery frame {frame}')
        # 0.10 m lies between measured authored catch (~0.34 m at frame 4)
        # and the ordinary recovery substitution (~0.03 m), not a feel rating.
        if min(frames[4], frames[6]) <= .10:
            raise ValueError(f'{directory}: unequal-foot catch missing ({frames})')
        if frames[18] >= .04:
            raise ValueError(f'{directory}: feet did not settle ({frames[18]})')
    elif len(frames) < 2:
        raise ValueError(f'{directory}: ordinary recovery pose missing')
    return {'path': str(directory), 'kind': kind, 'release': releases, 'footHeightGapM': frames}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--godot', default=os.environ.get('GODOT', 'godot'))
    parser.add_argument('--port', type=int, default=26300)
    args = parser.parse_args()
    output = ROOT / '.godot/440' / ('fadeaway-recovery-' + uuid.uuid4().hex)
    output.mkdir(parents=True)
    commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    cases = []
    specs = [(hand, angle, 'normal', 'fadeaway') for hand in ('Right', 'Left') for angle in (-170, 170)]
    specs += [(hand, 0, 'normal', 'normal') for hand in ('Right', 'Left')]
    specs += [(hand, 170, 'early', 'fake') for hand in ('Right', 'Left')]
    for index, (hand, angle, variant, kind) in enumerate(specs):
        dest = output / f'case-{index}'
        command = [sys.executable, str(ROOT / 'tools/run_jumpshot_capture.py'), '--godot', args.godot,
                   '--output', str(dest), '--commit', commit, '--headless', '--origin', hand,
                   '--variant', variant, '--heading-angle', str(angle), '--port', str(args.port + index)]
        subprocess.run(command, cwd=ROOT, check=True)
        cases.append(measure(dest / f'{hand.lower()}-{variant}-1', kind))
    negative = output / 'negative'
    subprocess.run([sys.executable, str(ROOT / 'tools/run_jumpshot_capture.py'), '--godot', args.godot,
                    '--output', str(negative), '--commit', commit, '--headless', '--origin', 'Right',
                    '--variant', 'normal', '--heading-angle', '170', '--port', str(args.port + len(specs)),
                    '--fadeaway-recovery-normal'], cwd=ROOT, check=True)
    negative = negative / 'right-normal-1'
    manifest = json.loads((negative / 'manifest.json').read_text())
    if not any(e.get('kind') == 'fadeaway-recovery-normal' for e in manifest.get('events', [])):
        raise RuntimeError('normal recovery substitution did not apply')
    try:
        measure(negative, 'fadeaway')
    except ValueError as error:
        if 'unequal-foot catch missing' not in str(error):
            raise
        negative_failure = str(error)
    else:
        raise RuntimeError('normal recovery substitution escaped catch detector')
    report = {'result': 'passed', 'cases': cases, 'negativeFailure': negative_failure}
    (output / 'proof.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(f'[fadeaway-recovery] PASS {len(cases)} cases and ordinary-recovery negative: {output / "proof.json"}')


if __name__ == '__main__':
    main()
