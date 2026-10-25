"""Body-forward lower/upper-body ordering on final modified production poses (#429)."""
import argparse
import json
import math
from pathlib import Path

from analyze_jumpshot_capture import analyze, clock, coordinate_resolution

TORSO = ("Spine2", "Neck")
LEGS = ("RightLeg", "RightFoot", "LeftLeg", "LeftFoot")


def projections(pose):
    # Capture transforms world bones by the inverse DISPLAY heading/root.
    # Therefore body +Z is forward independent of foot angles or torso bend.
    values = {name: pose["bones"]["mixamorig_" + name]["body"][2]
              for name in TORSO + LEGS}
    front = max(values[name] for name in TORSO)
    leads = {name: values[name] - front for name in LEGS}
    return dict(forwardM=values, upperTorsoFrontM=front, leadsM=leads,
                minimumLeadM=min(leads.values()))


def form(rows, remote):
    measured = {}
    failures = []
    for row in rows:
        pose = row.get("pose", {})
        if (pose.get("physicsFrame") != row["physicsFrame"] or
                (remote and not (pose.get("presentationTick") ==
                    row.get("selectedPresentationTick") == row.get("ballPresentationTick")))):
            continue
        if pose.get("move") != "jumpshot" or pose.get("phase") != "Active":
            continue
        values = projections(pose)
        if not all(math.isfinite(v) for v in values["forwardM"].values()):
            failures.append("nonfinite fadeaway projection")
            continue
        magnitude = max(abs(v) for bone in pose["bones"].values() for v in bone["body"][:3])
        measured[pose["frame"]] = dict(frame=pose["frame"], sourceTick=clock(row, remote),
            animation=row["animation"], coordinateResolutionM=coordinate_resolution(magnitude), **values)
    # The first two labelled poses are entry and AnimationTree's one-tick
    # reporting lag. Publish them; assert the two observed core release poses.
    for frame in (2, 3):
        if frame not in measured:
            failures.append("missing final Active pose " + str(frame))
        elif measured[frame]["minimumLeadM"] <= measured[frame]["coordinateResolutionM"]:
            failures.append("both legs must lead upper torso at Active:" + str(frame))
    return dict(failures=failures, samples=list(measured.values()), coreFrames=[2, 3])


def analyze_capture(directory, gate=True):
    result = analyze(directory)
    manifest = json.loads((directory / "manifest.json").read_text())
    rows = [json.loads(line) for line in (directory / "trace.jsonl").read_text().splitlines()]
    if manifest["variant"] == "fadeaway":
        result["fadeawayForm"] = form(rows, manifest["role"] == "client")
        if gate:
            result["failures"].extend(result["fadeawayForm"]["failures"])
    result["result"] = "failed" if result["failures"] else "passed"
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--measure-only", action="store_true")
    args = parser.parse_args()
    directories = [args.directory] if (args.directory / "manifest.json").exists() else sorted(
        p.parent for p in args.directory.glob("*/manifest.json") if not p.parent.name.endswith("-server"))
    results = [analyze_capture(p, not args.measure_only) for p in directories]
    passed = bool(results) and all(r["result"] == "passed" for r in results)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(result="passed" if passed else "failed", cases=results), indent=2))
    print(f"[fadeaway-proof] {'PASS' if passed else 'FAIL'} cases={len(results)} report={args.output}")
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
