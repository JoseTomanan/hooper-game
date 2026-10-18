"""Describe #414's same-frame capture; thresholds belong to the measured proof.

This deliberately emits measurements, not a feel verdict. Positions are metres.
Root translation and authoritative yaw are removed before comparing bone steps.
"""

import argparse
import json
import math
from pathlib import Path


def body_position(sample, point):
    x, y, z = (point[i] - sample["root"][i] for i in range(3))
    c, s = math.cos(sample["heading"]), math.sin(sample["heading"])
    return (c * x - s * z, y, s * x + c * z)


def distance(a, b):
    return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))


def analyze(path):
    samples = [json.loads(line) for line in path.read_text(encoding="utf-8-sig").splitlines() if line]
    steps = []
    for previous, current in zip(samples, samples[1:]):
        if not (previous["measuring"] and current["measuring"]):
            continue
        changes = {
            bone: distance(body_position(previous, point), body_position(current, current["bones"][bone]))
            for bone, point in previous["bones"].items()
        }
        maximum_bone = max(changes, key=changes.get)
        steps.append({
            "tick": current["tick"], "from": previous["animation"], "to": current["animation"],
            "phase": current["displayPhase"], "hand": current["hand"],
            "tickGap": current["tick"] - previous["tick"],
            "sweepProgress": current["sweepProgress"], "time": current["playbackTime"],
            "maxBone": maximum_bone, "maxStep": changes[maximum_bone],
            "trackedSteps": {name: value for name, value in changes.items()
                             if any(part in name for part in ("Hips", "Foot", "Hand"))
                             and not any(part in name for part in ("Thumb", "Index", "Middle", "Ring", "Pinky"))},
            "ballPalm": palm_distances(current),
        })
    return {
        "trace": str(path), "samples": len(samples), "measuredSteps": len(steps),
        "boundaries": [step for step in steps if step["from"] != step["to"]],
        "largestSteps": sorted(steps, key=lambda step: step["maxStep"], reverse=True)[:12],
        "skippedTicks": [step["tick"] for step in steps if step["tickGap"] != 1],
    }


def palm_distances(sample):
    # Keep center distances explicit. Radius subtraction alone does not prove
    # contact: only genuine hand-contact intervals can carry that assertion.
    result = {}
    for side in ("Left", "Right"):
        wrist = sample["bones"].get("mixamorig_" + side + "Hand")
        middle = sample["bones"].get("mixamorig_" + side + "HandMiddle1")
        if wrist is not None and middle is not None:
            palm = [(a + b) / 2 for a, b in zip(wrist, middle)]
            result[side] = distance(palm, sample["ball"])
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("traces", type=Path, nargs="+")
    args = parser.parse_args()
    print(json.dumps([analyze(path) for path in args.traces], indent=2))
