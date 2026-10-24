"""Validate retained #428 production traces; report actual release premises.

This reads successful authority releases and coherent observer samples. It does
not infer visual quality or a ball release from an animation name.
"""
import argparse
import gzip
import json
import math
from pathlib import Path


def read_rows(folder):
    path = folder / "trace.jsonl"
    if path.exists():
        text = path.read_text()
    else:
        # Retained evidence is losslessly compressed, with the same JSON rows.
        # https://docs.python.org/3.14/library/gzip.html#gzip.open
        with gzip.open(path.with_suffix(".jsonl.gz"), "rt", encoding="utf-8") as stream:
            text = stream.read()
    return [json.loads(line) for line in text.splitlines()]


EXPECTED_COUNTERS = ([('Startup', frame) for frame in range(1, 18)]
                     + [('Active', frame) for frame in range(4)]
                     + [('Recovery', frame) for frame in range(20)])


def observed_counters(rows):
    counters = []
    previous_tick = None
    for row in rows:
        if row["selectedPresentationTick"] != previous_tick:
            counters.append((row["phase"], row["frame"]))
        previous_tick = row["selectedPresentationTick"]
    return counters


def wrap(angle):
    return (angle + math.pi) % math.tau - math.pi


def read_case(folder, before):
    manifest = json.loads((folder / "server/manifest.json").read_text())
    observer = json.loads((folder / "client/manifest.json").read_text())
    rows = read_rows(folder / "server")
    remote = read_rows(folder / "client")
    if manifest["result"] != "captured" or observer["result"] != "captured":
        raise ValueError(f"failed capture: {folder}")
    fixture = next(event for event in manifest["events"] if event["kind"] == "heading-fixture")
    shot = [row for row in rows if row["move"] == "jumpshot"]
    startup = [row for row in shot if row["phase"] == "Startup"]
    releases = [event for event in manifest["events"] if event["kind"] == "release"]
    if len(releases) != 1 or manifest["releases"] != 1 or not startup:
        raise ValueError(f"missing unique successful release/Startup: {folder}")
    event = releases[0]
    release = next(row for row in shot if row["tick"] == event["tick"])
    if event["phase"] != "Active" or event["frame"] != 0 or release["releaseCount"] != 1:
        raise ValueError(f"release not at first Active: {folder}")
    if event["tick"] - startup[0]["tick"] != 17:
        raise ValueError(f"normal shot timeline changed: {folder}")
    angle = abs(wrap(release["heading"] - fixture["fixedTarget"]))
    expected = abs(math.radians(manifest["initialAngle"])) if before else 0
    if abs(angle - expected) > 1e-5 or abs(release["facingFactor"] - (1 + .8 * angle / math.pi)) > 1e-5:
        raise ValueError(f"release-angle/facing premise changed: {folder}")
    # The exact 90-degree fixture is float32; use the measured angle/flag rather
    # than assuming decimal pi and the runtime's pi have identical rounding.
    if abs(angle - math.pi / 2) > 1e-6 and release["fadeaway"] != (angle >= math.pi / 2):
        raise ValueError(f"release classification mismatch: {folder}")
    speed = math.hypot(fixture["velocity"][0], fixture["velocity"][2])
    if manifest["headingEntry"] == "moving" and (speed < .1 or abs(release["initiationSpeed"] - speed) > 1e-5):
        raise ValueError(f"moving entry was planted or re-sampled: {folder}")
    if manifest["headingEntry"] == "stationary" and speed > .03:
        raise ValueError(f"stationary fixture was moving: {folder}")
    if not before:
        remaining = abs(math.radians(manifest["initialAngle"]))
        for row in startup:
            now = abs(wrap(fixture["fixedTarget"] - row["heading"]))
            if now > remaining + 1e-5:
                raise ValueError(f"Startup reversed/overshot: {folder}")
            remaining = now
    seen = [row for row in remote if row["move"] == "jumpshot"]
    phases = {phase: sorted({row["frame"] for row in seen if row["phase"] == phase})
              for phase in ("Startup", "Active", "Recovery")}
    if not all(phases.values()):
        raise ValueError(f"observer missing a whole phase: {folder}")
    for row in seen:
        if row["selectedPresentationTick"] != row["ballPresentationTick"] or row["selectedPresentationTick"] < 0:
            raise ValueError(f"incoherent observer heading/ball: {folder}")
    return {"beginObservedTick": startup[0]["tick"], "beginObservedFrame": startup[0]["frame"],
            "shootInputTick": next(event["tick"] for event in manifest["events"] if event["kind"] == "shoot-input"),
            "releaseTick": event["tick"], "releaseHeading": release["heading"], "fixedTarget": fixture["fixedTarget"],
            "releaseAngleRadians": angle, "facingFactor": release["facingFactor"], "fadeaway": release["fadeaway"],
            "releaseCount": manifest["releases"], "initiationSpeed": release["initiationSpeed"],
            "releasePosition": release["releasePosition"], "observerPhases": phases,
            "observerCompleteOrderedCounters": observed_counters(seen) == EXPECTED_COUNTERS,
            "observerHolds": sum(row["presentationHolding"] for row in seen),
            "observerResets": max(row["presentationResyncCount"] for row in remote),
            "commit": manifest["commit"], "source": str(folder)}


def rendered_metrics(folder):
    manifest = json.loads((folder / "client/manifest.json").read_text())
    if not manifest["images"] or manifest["result"] != "captured":
        raise ValueError(f"not rendered evidence: {folder}")
    rows = read_rows(folder / "client")
    shot = [row for row in rows if row["move"] == "jumpshot"]
    phases = {phase: sorted({row["frame"] for row in shot if row["phase"] == phase})
              for phase in ("Startup", "Active", "Recovery")}
    if not all(phases.values()) or 0 not in phases["Active"]:
        raise ValueError(f"rendered evidence missing release/phase coverage: {folder}")
    if observed_counters(shot) != EXPECTED_COUNTERS:
        raise ValueError(f"rendered evidence must contain the complete ordered shot counters: {folder}")
    distances = []
    for row in shot:
        if row["selectedPresentationTick"] != row["ballPresentationTick"]:
            raise ValueError(f"rendered heading and ball do not share a presentation tick: {folder}")
        if not row["poseMatchesPhysics"] or row["pose"]["presentationTick"] != row["selectedPresentationTick"]:
            raise ValueError(f"rendered final pose not on heading/ball tick: {folder}")
        if row["pose"]["phase"] != row["phase"] or row["pose"]["frame"] != row["frame"]:
            raise ValueError(f"rendered phase/final pose mismatch: {folder}")
        if row["phase"] == "Startup" or (row["phase"] == "Active" and row["frame"] == 0):
            bones = row["pose"]["bones"]
            left = math.dist(row["ball"], bones["mixamorig_LeftHand"]["world"][:3])
            right = math.dist(row["ball"], bones["mixamorig_RightHand"]["world"][:3])
            distances.append({"phase": row["phase"], "frame": row["frame"], "sourceTick": row["selectedPresentationTick"],
                              "leftWristMetres": left, "rightWristMetres": right, "nearestWristMetres": min(left, right)})
    return {"source": str(folder), "frames": len(shot), "phases": phases,
            "nearestWristGapMetres": {"min": min(row["nearestWristMetres"] for row in distances),
                                      "max": max(row["nearestWristMetres"] for row in distances)},
            "separationSamples": distances, "camera": shot[0]["camera"], "viewport": shot[0]["viewportSize"],
            "holds": sum(row["presentationHolding"] for row in shot),
            "resets": max(row["presentationResyncCount"] for row in shot)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--current", type=Path, required=True)
    parser.add_argument("--baseline-moving", type=Path, required=True)
    parser.add_argument("--current-moving", type=Path, required=True)
    parser.add_argument("--baseline-supplement", type=Path)
    parser.add_argument("--rendered", type=Path, action="append", default=[])
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = {"schema": "hooper-shot-heading-proof/v1", "cases": [], "rendered": [],
              "limitation": "Objective state/pose evidence only; no feel or ball-attachment acceptance."}
    for angle in (0, 45, 89, 90, 91, 135, 180):
        for entry in ("stationary", "moving"):
            for hand in ("Left", "Right"):
                name = f"{angle}-{entry}-{hand}"
                baseline = args.baseline if entry == "stationary" else args.baseline_moving
                current = args.current if entry == "stationary" else args.current_moving
                if args.baseline_supplement and (args.baseline_supplement / name).is_dir():
                    baseline = args.baseline_supplement
                report["cases"].append({"name": name, "before": read_case(baseline / name, True),
                                        "after": read_case(current / name, False)})
    for folder in args.rendered:
        report["rendered"].append(rendered_metrics(folder))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2))
    print(f"PASS {len(report['cases'])} before/after real-observer cases; {len(report['rendered'])} rendered cases")


if __name__ == "__main__":
    main()
