"""Check the captured #414 failure across the complete crossover action.

Limits distinguish the measured ~0.6 m wrist/~0.34 m foot seams from the
unchanged Active-entry lean (<=0.165 m wrist/0.048 m foot in the baseline).
They are regression bounds, not human feel acceptance.
"""

import argparse
import json
from pathlib import Path

from analyze_crossover_continuity import body_position, distance, palm_distances

LIMITS = {"Hips": .18, "LeftHand": .20, "RightHand": .20, "LeftFoot": .12, "RightFoot": .12}


def verify(directory):
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8-sig"))
    rows = [json.loads(line) for line in (directory / "trace.jsonl").read_text(encoding="utf-8-sig").splitlines()]
    failures = []
    if manifest["result"] != "captured" or manifest["completedCycles"] != manifest["requestedCycles"]:
        failures.append("capture did not complete requested cycles")
    rows = [row for row in rows if row["measuring"]]
    action_indices = {index for index, row in enumerate(rows) if row["animation"].startswith("Crossover")}
    if not action_indices:
        failures.append("no actual crossover playback in measured window")
    window = {j for i in action_indices for j in range(max(1, i - 2), min(len(rows), i + 3))}
    maximums = {name: 0.0 for name in LIMITS}
    for index in sorted(window):
        previous, current = rows[index - 1:index + 1]
        if current["physicsFrame"] == previous["physicsFrame"]:
            continue
        if current["physicsFrame"] != previous["physicsFrame"] + 1:
            failures.append(f"tick {current['tick']}: missing physics frame")
        if not current["bones"] or not previous["bones"]:
            failures.append(f"tick {current['tick']}: missing skeleton pose")
            continue
        for name, limit in LIMITS.items():
            key = "mixamorig_" + name
            step = distance(body_position(previous, previous["bones"][key]), body_position(current, current["bones"][key]))
            maximums[name] = max(maximums[name], step)
            if step > limit:
                failures.append(f"tick {current['tick']} {previous['animation']} -> {current['animation']}: {name} step {step:.4f} m > {limit:.2f} m")
    return {"capture": str(directory), "result": "FAIL" if failures else "PASS", "maxStepsMeters": maximums, "failures": failures}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directories", type=Path, nargs="+")
    args = parser.parse_args()
    results = [verify(directory) for directory in args.directories]
    print(json.dumps(results, indent=2))
    raise SystemExit(1 if any(result["failures"] for result in results) else 0)
