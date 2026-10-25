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
    result["poseClocks"] = {f'{r["pose"]["phase"]}:{r["pose"]["frame"]}': r["playbackTime"]
        for r in rows if r.get("pose", {}).get("move") == "jumpshot" and
        r["pose"].get("physicsFrame") == r["physicsFrame"] and
        (manifest["role"] != "client" or r["pose"].get("presentationTick") ==
            r.get("selectedPresentationTick") == r.get("ballPresentationTick"))}
    if manifest["variant"] == "fadeaway":
        result["fadeawayForm"] = form(rows, manifest["role"] == "client")
        authority = manifest if manifest["role"] != "client" else json.loads(
            (Path(str(directory) + "-server") / "manifest.json").read_text())
        releases = [e for e in authority.get("events", []) if e["kind"] == "release"]
        if not releases or any(not e.get("fadeaway") or e.get("residualAngle", 0) < math.pi / 2 for e in releases):
            result["failures"].append("actual fadeaway release-angle premise missing")
        if any(s["animation"] != "FadeawayActive" for s in result["fadeawayForm"]["samples"]):
            result["failures"].append("production FadeawayActive selection missing")
        if gate:
            result["failures"].extend(result["fadeawayForm"]["failures"])
    result["result"] = "failed" if result["failures"] else "passed"
    return result


def compare(result, prior):
    # Keep the old motion envelope, including its native coordinate precision.
    # A changed playback offset is incomparable evidence, never a relaxed bound.
    for key, time in result["poseClocks"].items():
        if key not in prior["poseClocks"] or abs(time - prior["poseClocks"][key]) > 1e-6:
            result["failures"].append("incomparable playback clock: " + key)
    limits = {b["transition"]: b for b in prior["boundaries"]}
    for seam in result["boundaries"]:
        old = limits.get(seam["transition"])
        if old is None or seam["maxDisplacement"] > old["maxDisplacement"] + old["coordinateResolutionM"]:
            result["failures"].append("boundary displacement exceeds baseline: " + seam["transition"])
        if old:
            seam["baselineMotionBoundM"] = old["maxDisplacement"]
    result["result"] = "failed" if result["failures"] else "passed"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--measure-only", action="store_true")
    parser.add_argument("--baseline", type=Path)
    args = parser.parse_args()
    directories = [args.directory] if (args.directory / "manifest.json").exists() else sorted(
        p.parent for p in args.directory.glob("*/manifest.json") if not p.parent.name.endswith("-server"))
    results = [analyze_capture(p, not args.measure_only) for p in directories]
    if args.baseline:
        baseline = {c["case"]: c for c in json.loads(args.baseline.read_text())["cases"]}
        for result in results:
            if result["case"] not in baseline:
                result["failures"].append("baseline case missing")
                result["result"] = "failed"
            else:
                compare(result, baseline[result["case"]])
    passed = bool(results) and all(r["result"] == "passed" for r in results)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(result="passed" if passed else "failed", cases=results), indent=2))
    print(f"[fadeaway-proof] {'PASS' if passed else 'FAIL'} cases={len(results)} report={args.output}")
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
