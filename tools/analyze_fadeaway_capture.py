"""Body-forward lower/upper-body ordering on final modified production poses (#429)."""
import argparse
import json
import math
from pathlib import Path

from analyze_jumpshot_capture import analyze, clock, coordinate_resolution, geometry

TORSO = ("Spine2", "Neck")
LEGS = ("RightLeg", "RightFoot", "LeftLeg", "LeftFoot")


def projections(pose):
    # Capture transforms world bones by the inverse DISPLAY heading/root.
    # Therefore body +Z is forward independent of foot angles or torso bend.
    values = {name: pose["bones"]["mixamorig_" + name]["body"][2]
              for name in TORSO + ("Hips",) + LEGS}
    front = max(values[name] for name in TORSO)
    leads = {name: values[name] - front for name in LEGS}
    hip_leads = {name: values[name] - values["Hips"] for name in LEGS}
    return dict(forwardM=values, upperTorsoFrontM=front, leadsM=leads,
                minimumLeadM=min(leads.values()), leadsFromHipsM=hip_leads,
                minimumHipLeadM=min(hip_leads.values()))


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
        if frame in measured and measured[frame]["minimumHipLeadM"] <= measured[frame]["coordinateResolutionM"]:
            failures.append("both legs must advance from hips at Active:" + str(frame))
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
        result["authoritativeReleases"] = releases
        if not releases or any(not e.get("fadeaway") or e.get("residualAngle", 0) < math.pi / 2 for e in releases):
            result["failures"].append("actual fadeaway release-angle premise missing")
        if any(s["animation"] != "FadeawayActive" for s in result["fadeawayForm"]["samples"]):
            result["failures"].append("production FadeawayActive selection missing")
        # Re-solving the arms after leaning the torso must still produce a
        # shooting reach in the final blended pose, not only in Blender's IK
        # target. Body +Y is world up after inverse yaw, independent of lean.
        result["fadeawayArms"] = []
        for row in rows:
            pose = row.get("pose", {})
            if (pose.get("physicsFrame") != row["physicsFrame"] or
                    pose.get("move") != "jumpshot" or pose.get("phase") != "Active" or
                    pose.get("frame") not in (2, 3) or
                    (manifest["role"] == "client" and not (pose.get("presentationTick") ==
                        row.get("selectedPresentationTick") == row.get("ballPresentationTick")))):
                continue
            bones = pose["bones"]
            wrist_above = bones["mixamorig_RightHand"]["body"][1] - bones["mixamorig_RightForeArm"]["body"][1]
            shape = geometry(pose)
            sample = dict(frame=pose["frame"], elbow=shape["elbow"], guideElbow=shape["guideElbow"],
                          wristAboveElbowM=wrist_above)
            result["fadeawayArms"].append(sample)
            if gate and (not math.isfinite(wrist_above) or wrist_above <= 0 or shape["asymmetry"] <= 0):
                result["failures"].append("fadeaway shooting reach missing at Active:" + str(pose["frame"]))
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


def compare_exaggeration(result, prior):
    """Compare every leg landmark; a stronger spine alone is insufficient."""
    if result.get("variant") != "fadeaway":
        return
    if prior.get("failures"):
        result["failures"].append("invalid exaggeration baseline")
    old = {s["frame"]: s for s in prior.get("fadeawayForm", {}).get("samples", [])}
    new = {s["frame"]: s for s in result.get("fadeawayForm", {}).get("samples", [])}
    for frame in (2, 3):
        if frame not in old or frame not in new:
            result["failures"].append("missing paired exaggeration pose " + str(frame))
            continue
        margin = old[frame]["coordinateResolutionM"] + new[frame]["coordinateResolutionM"]
        for field in ("leadsM", "leadsFromHipsM"):
            for bone in LEGS:
                delta = new[frame][field][bone] - old[frame][field][bone]
                if not math.isfinite(delta) or delta <= margin:
                    result["failures"].append(f"fadeaway exaggeration missing: Active:{frame} {field} {bone}")
    result["result"] = "failed" if result["failures"] else "passed"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--measure-only", action="store_true")
    parser.add_argument("--baseline", type=Path)
    parser.add_argument("--exaggeration-baseline", type=Path,
                        help="require every core knee/ankle lead to exceed the frozen pre-439 pose")
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
    if args.exaggeration_baseline:
        baseline = {c["case"]: c for c in json.loads(args.exaggeration_baseline.read_text())["cases"]}
        for result in results:
            if result.get("variant") != "fadeaway":
                continue
            if result["case"] not in baseline:
                result["failures"].append("exaggeration baseline case missing")
                result["result"] = "failed"
            else:
                compare(result, baseline[result["case"]])
                compare_exaggeration(result, baseline[result["case"]])
    passed = bool(results) and all(r["result"] == "passed" for r in results)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(result="passed" if passed else "failed", cases=results), indent=2))
    print(f"[fadeaway-proof] {'PASS' if passed else 'FAIL'} cases={len(results)} report={args.output}")
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
