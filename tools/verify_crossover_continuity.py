"""Check the captured #414 failure across the complete crossover action.

Limits distinguish the measured ~0.6 m wrist/~0.34 m foot seams from the
unchanged Active-entry lean (<=0.165 m wrist/0.048 m foot in the baseline).
They are regression bounds, not human feel acceptance.
"""

import argparse
import json
import math
from pathlib import Path

from analyze_crossover_continuity import body_position, distance, palm_distances

LIMITS = {"Hips": .18, "LeftHand": .20, "RightHand": .20, "LeftFoot": .12, "RightFoot": .12}
# Sub-pixel measurement deadband (0.1 mm) around the centimetre-rounded bounds.
# This is far below the 0.3-0.6 m reproduced seams; retain raw maxima in output.
MEASUREMENT_EPSILON = .0001


def verify(directory):
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8-sig"))
    rows = [json.loads(line) for line in (directory / "trace.jsonl").read_text(encoding="utf-8-sig").splitlines()]
    failures = []
    if manifest["result"] != "captured" or manifest["completedCycles"] != manifest["requestedCycles"]:
        failures.append("capture did not complete requested cycles")
    rows = [dict(row,
                 displayMove=row.get("poseDisplayMove", row["displayMove"]),
                 displayPhase=row.get("poseDisplayPhase", row["displayPhase"]),
                 hand=row.get("poseDisplayHand", row["hand"]))
            for row in rows if row["measuring"]]
    for row in rows:
        if not row.get("poseFrameMatches", False):
            failures.append(f"tick {row['tick']}: final pose is from another physics frame")
        if any(not math.isfinite(value) for point in row["bones"].values() for value in point):
            failures.append(f"tick {row['tick']}: non-finite final bone")
        if row["displayMove"] == "crossover" and manifest.get("mutation", "none") == "none":
            if not row.get("crossoverPoseApplied", False):
                failures.append(f"tick {row['tick']}: crossover adapter did not contribute")
            if row.get("crossoverPoseMaxReachError", 1) > .0001 or row.get("crossoverPoseClampCount", 1):
                failures.append(f"tick {row['tick']}: unreachable interpolated limb target")
            sampled = row.get("crossoverPoseSampleFrame", -1)
            frames = {"Startup": 6, "Active": 3, "Recovery": 12}.get(row["displayPhase"], 0)
            if not 0 <= sampled < frames:
                failures.append(f"tick {row['tick']}: sampled pose counter is absent or outside its current phase")
            if row.get("poseDisplaySimulatedLocally") and sampled != row.get("poseDisplayFrame"):
                failures.append(f"tick {row['tick']}: locally simulated pose did not use its actual machine counter")
    for previous, current in zip(rows, rows[1:]):
        if (manifest.get("mutation", "none") == "none" and current["displayMove"] == "crossover"
                and not current.get("poseDisplaySimulatedLocally", False)
                and previous["displayMove"] == "crossover"
                and current["displayPhase"] == previous["displayPhase"] and current["hand"] == previous["hand"]
                and current.get("poseDisplayFrame", 0) >= previous.get("poseDisplayFrame", 0)):
            elapsed = current["physicsFrame"] - previous["physicsFrame"]
            if current.get("crossoverPoseSampleFrame", 0) - previous.get("crossoverPoseSampleFrame", 0) > elapsed:
                failures.append(f"tick {current['tick']}: pose counter compressed multiple server intervals into one display interval")
    # Count actual display entries, then verify each complete phase and hand family.
    cycles = []
    for row in rows:
        if row["displayMove"] != "crossover":
            continue
        if not cycles or row["displayPhase"] == "Startup" and cycles[-1][-1]["displayPhase"] != "Startup":
            cycles.append([])
        cycles[-1].append(row)
    if len(cycles) != manifest["requestedCycles"]:
        failures.append(f"observed {len(cycles)} display cycles, expected {manifest['requestedCycles']}")
    for index, cycle in enumerate(cycles):
        phases = list(dict.fromkeys(row["displayPhase"] for row in cycle))
        if phases != ["Startup", "Active", "Recovery"]:
            failures.append(f"cycle {index + 1}: incomplete/incorrect display phases {phases}")
        origin = manifest["origin"] if index % 2 == 0 else ("Left" if manifest["origin"] == "Right" else "Right")
        for row in cycle:
            expected = origin if row["displayPhase"] == "Startup" else ("Left" if origin == "Right" else "Right")
            if row["hand"] != expected:
                failures.append(f"tick {row['tick']}: phase/hand family mismatch")
    action_indices = {index for index, row in enumerate(rows) if row["animation"].startswith("Crossover")}
    if not action_indices:
        failures.append("no actual crossover playback in measured window")
    window = {j for i in action_indices for j in range(max(1, i - 2), min(len(rows), i + 3))}
    maximums = {name: 0.0 for name in LIMITS}
    for index in sorted(window):
        previous, current = rows[index - 1:index + 1]
        if current["physicsFrame"] == previous["physicsFrame"]:
            continue
        gap = current["physicsFrame"] - previous["physicsFrame"]
        if not 1 <= gap <= manifest.get("allowedMaxPhysicsGap", 1):
            failures.append(f"tick {current['tick']}: missing physics frame")
        if not current["bones"] or not previous["bones"]:
            failures.append(f"tick {current['tick']}: missing skeleton pose")
            continue
        for name, limit in LIMITS.items():
            key = "mixamorig_" + name
            step = distance(body_position(previous, previous["bones"][key]), body_position(current, current["bones"][key]))
            maximums[name] = max(maximums[name], step)
            if step > limit * max(1, gap) + MEASUREMENT_EPSILON:
                failures.append(f"tick {current['tick']} {previous['animation']} -> {current['animation']}: {name} step {step:.4f} m > {limit:.2f} m/tick across {gap} ticks")
    contacts = [{"tick": row["tick"], "hand": row["hand"],
                 "centerDistanceMeters": palm_distances(row).get(row["hand"]),
                 "ballRadiusMeters": row.get("ballRadius")}
                for row in rows if row.get("neutralApex", False)]
    return {"capture": str(directory), "result": "FAIL" if failures else "PASS", "maxStepsMeters": maximums,
            "neutralApexContactMeasurements": contacts, "contactMeaning": "3D palm-center distances, including height; reported independently, no tight-contact acceptance claimed", "failures": failures}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directories", type=Path, nargs="+")
    args = parser.parse_args()
    results = [verify(directory) for directory in args.directories]
    print(json.dumps(results, indent=2))
    raise SystemExit(1 if any(result["failures"] for result in results) else 0)
