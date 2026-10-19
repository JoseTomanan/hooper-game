"""Measure #417 diagnostic captures; never infer a fix or human feel acceptance.

World translation and display yaw are removed from bone positions. A finite,
nonzero excursion establishes motion, not whether it reads as BehindTheBack.
"""
import argparse
import json
from pathlib import Path

from analyze_crossover_continuity import body_position, distance


def analyze(directory):
    manifest = json.loads((directory / 'manifest.json').read_text(encoding='utf-8-sig'))
    rows = [json.loads(line) for line in (directory / 'trace.jsonl').read_text(encoding='utf-8-sig').splitlines()]
    measured = [r for r in rows if r['measuring']]
    move = manifest['move']
    action = [r for r in measured if r['poseDisplayMove'] == move and r['poseDisplayPhase'] != 'Inactive']
    phases = {}
    for row in action:
        phase = row['poseDisplayPhase']
        if phase not in phases:
            phases[phase] = {'samples': 0, 'clips': set(), 'states': set(), 'playback': []}
        item = phases[phase]
        item['samples'] += 1
        item['clips'].add(row['resolvedClipAtPoseSnapshot'])
        item['states'].add(row['playbackNodeAtPoseSnapshot'])
        item['playback'].append(row['playbackTimeAtPoseSnapshot'])
    for item in phases.values():
        item['clips'] = sorted(item['clips'])
        item['states'] = sorted(item['states'])
        item['playbackRangeSeconds'] = [min(item['playback']), max(item.pop('playback'))]
    excursions = {}
    for bone in ('Hips', 'Spine', 'LeftHand', 'RightHand', 'LeftFoot', 'RightFoot'):
        points = [body_position(r, r['bones']['mixamorig_' + bone]) for r in action]
        excursions[bone] = max((distance(a, b) for a in points for b in points), default=0)
    sweep = [r for r in action if r['sweepActive']]
    return dict(directory=str(directory), move=move, role=manifest['role'], origin=manifest['origin'], motion=manifest['motion'],
                result=manifest['result'], completedCycles=manifest['completedCycles'], measuringSamples=len(measured),
                phases=phases, bodySpaceMaximumExcursionMetres=excursions,
                sweepSamples=len(sweep), behindBodySweepSamples=sum(body_position(r, r['ball'])[2] > 0 for r in sweep),
                poseFrameMismatches=sum(not r['poseFrameMatches'] for r in measured),
                playerBallTickMismatches=sum(r['posePresentationTick'] != r['ballPresentationTick'] for r in measured) if manifest['role'] == 'client' else None,
                observedMoves=sorted({r['poseDisplayMove'] for r in measured}),
                observedMaxPhysicsGap=manifest['observedMaxPhysicsGap'],
                rendered=manifest['displayDriver'] != 'headless',
                meaning='Motion and hookup measurements only; no static-standing reproduction, repair or feel verdict inferred')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directories', type=Path, nargs='+')
    args = parser.parse_args()
    print(json.dumps([analyze(path) for path in args.directories], indent=2))
