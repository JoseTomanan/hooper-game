"""Measure #430 through real production input and final SkeletonUpdated samples.

Default matrix: eight angles x four entry cases x two gestures, three cycles
each. Local fixed60Hz headless captures establish simulation behavior only;
the separate native #416 matrix remains the rendered/remote continuity proof.
"""
import argparse
import json
import math
import os
from pathlib import Path
import struct
import subprocess
import sys
import uuid

from verify_pivot_continuity import CONTACT_METRES, EFFECTORS, ROTATION_DEGREES, STEP_METRES

ROOT = Path(__file__).resolve().parents[1]
ANGLES = (45, 90, 91, 120, 135, 170, 179.9, 180)
CASES = ("idle", "run", "left", "right")
GESTURES = ("held", "flick")
DT = 1 / 60


def f32(value):
    return struct.unpack("f", struct.pack("f", value))[0]


PI = f32(math.pi)
TAU = f32(2 * PI)


def angle_delta(start, target):
    # Match the documented float angle convention, including the exact half-turn
    # sign. Double-precision modulo would reverse PI-float -> zero's turn.
    delta = f32(target - start)
    while delta > PI:
        delta = f32(delta - TAU)
    while delta < -PI:
        delta = f32(delta + TAU)
    return delta


def heading_step(heading, target):
    """Independent implementation of the locked900/.95 fixed-step schedule."""
    remaining = angle_delta(heading, target)
    if abs(remaining) < 1e-5:
        return target
    fraction = f32(abs(remaining) / PI)
    slow_rate = f32(900 * f32(.95))
    rate = f32(900 + f32(f32(slow_rate - 900) * fraction))
    maximum = f32(f32(rate * f32(DT)) * f32(PI / 180))
    value = f32(heading + math.copysign(min(abs(remaining), maximum), remaining))
    while value > PI:
        value = f32(value - TAU)
    while value < -PI:
        value = f32(value + TAU)
    return value


def horizontal(vector):
    return vector[0], vector[2]


def speed(row):
    return math.hypot(*horizontal(row["displayVelocity"]))


def point(row):
    return horizontal(row["displayRoot"])


def at(row, origin):
    if row is None:
        return None
    ticks = row["physicsFrame"] - origin["physicsFrame"]
    return {"physicsFrame": row["physicsFrame"], "tick": row["tick"], "stageAge": row["stageAge"],
            "intervalTicksFromPreEntry": ticks, "simulationSecondsFromPreEntry": ticks * DT,
            "speedMetresPerSecond": speed(row)}


def contact_checks(active, measured, failures, prefix):
    """Contact/continuity checks apply to genuine latched turns of any length."""
    if not active:
        return {"supportTransfers": 0, "maxSupportFootprintErrorMetres": 0,
                "maxToeTargetErrorMetres": 0, "maxEffectorStepMetres": 0}
    support = active[0]["pivotSupportToe"]
    anchor = active[0]["bones"][support]["world"]
    start = previous = active[0]
    transfers = 0
    maximum_contact = maximum_target = maximum_step = 0.0
    for row in active:
        if not row["pivotPoseApplied"]:
            failures.append(f"{prefix}: active turn lacked its production pose modifier")
        incoming = row["pivotSupportToe"]
        if incoming != support:
            transfers += 1
            first = start["bones"][incoming]["world"]
            landed = previous["bones"][incoming]["world"]
            foot_delta = [landed[i] - first[i] for i in (0, 2)]
            root_delta = [previous["displayRoot"][i] - start["displayRoot"][i] for i in (0, 2)]
            if math.hypot(*foot_delta) < .02:
                failures.append(f"{prefix}: support changed without a relocated swing footprint")
            if math.hypot(*root_delta) > .02 and sum(a * b for a, b in zip(foot_delta, root_delta)) <= 0:
                failures.append(f"{prefix}: completed swing opposed measured travel")
            if abs(landed[1]) > CONTACT_METRES:
                failures.append(f"{prefix}: support transferred to an airborne foot")
            support = incoming
            anchor = row["bones"][support]["world"]
            start = row
        position = row["bones"][support]["world"]
        error = math.dist(horizontal(position), horizontal(anchor))
        maximum_contact = max(maximum_contact, error)
        if error > CONTACT_METRES:
            failures.append(f"{prefix}: support toe slid {error:.6f}m during a stance")
        if row["pivotStanceAge"] >= 4 and abs(position[1]) > CONTACT_METRES:
            failures.append(f"{prefix}: support was not grounded after four landing intervals")
        for side in ("Left", "Right"):
            if row["bones"][f"mixamorig_{side}Toe_End"]["world"][1] < -CONTACT_METRES:
                failures.append(f"{prefix}: {side} toe penetrated the court")
        if row["pivotStepping"]:
            for toe, target in ((support, row["pivotSupportTarget"]),
                                (row["pivotSwingToe"], row["pivotSwingTarget"])):
                error = math.dist(row["bones"][toe]["world"], target)
                maximum_target = max(maximum_target, error)
                if error > CONTACT_METRES:
                    failures.append(f"{prefix}: evaluated {toe} missed its world target by {error:.6f}m")
        previous = row
    # A six-tick91-degree latch must not be forced to transfer before stance8.
    # Require a transfer only when that source interval actually exists in motion.
    if any(row["physicsFrame"] - active[0]["physicsFrame"] >= 8 and speed(row) > .05
           for row in active) and math.dist(point(active[0]), point(active[-1])) > .08 and transfers == 0:
        failures.append(f"{prefix}: long moving turn never transferred support")
    first_frame, last_frame = active[0]["physicsFrame"], active[-1]["physicsFrame"]
    seam = [row for row in measured if first_frame - 1 <= row["physicsFrame"] <= last_frame + 8]
    for before, after in zip(seam, seam[1:]):
        for name in EFFECTORS:
            bone = "mixamorig_" + name
            distance = math.dist(before["bones"][bone]["world"], after["bones"][bone]["world"])
            maximum_step = max(maximum_step, distance)
            if distance > STEP_METRES:
                failures.append(f"{prefix}: {name} exceeded the unchanged {STEP_METRES}m source-step bound")
        for bone, data in after["bones"].items():
            left, right = before["bones"][bone]["worldRotation"], data["worldRotation"]
            dot = abs(sum(a * b for a, b in zip(left, right)))
            if math.degrees(2 * math.acos(min(1, dot))) > ROTATION_DEGREES:
                failures.append(f"{prefix}: {bone} exceeded the unchanged rotation bound")
    return {"supportTransfers": transfers, "maxSupportFootprintErrorMetres": maximum_contact,
            "maxToeTargetErrorMetres": maximum_target, "maxEffectorStepMetres": maximum_step}


def measure(directory):
    directory = Path(directory)
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8-sig"))
    raw = [json.loads(line) for line in (directory / "trace.jsonl").read_text(encoding="utf-8-sig").splitlines()]
    # Capture retains every callback. Measurements use only the final evaluated
    # pose per real physics frame, exactly as the renderer observes it.
    rows = sorted({row["physicsFrame"]: row for row in raw}.values(), key=lambda row: row["physicsFrame"])
    angle, case, gesture = manifest["requestedAngleDegrees"], manifest["case"], manifest["gesture"]
    failures, cycles = [], []
    if manifest["result"] != "captured" or manifest["completedCycles"] != 3:
        failures.append("capture did not finish three real input cycles")
    if manifest["role"] != "local" or not manifest["fixedFps"] or manifest["paced"]:
        failures.append("measurement requires a local fixed60Hz diagnostic capture")
    expected_hand = "Left" if case == "left" else "Right" if case == "right" else None
    for cycle in (1, 2, 3):
        prefix = f"cycle{cycle}"
        measured = [row for row in rows if row["cycle"] == cycle and row["measuring"] and row["stage"] == "measured"]
        if not measured:
            failures.append(f"{prefix}: no final evaluated measurement rows")
            continue
        previous = [row for row in rows if row["cycle"] == cycle and row["physicsFrame"] < measured[0]["physicsFrame"]]
        if not previous:
            failures.append(f"{prefix}: missing real pre-entry evaluated sample")
            continue
        before = previous[-1]
        if measured[0]["stageAge"] != 0 or any(b["physicsFrame"] - a["physicsFrame"] != 1
                                               for a, b in zip([before] + measured, measured)):
            failures.append(f"{prefix}: capture omitted a physics interval")
        if any(row["displayPhase"] != "Inactive" or row["presentationTick"] >= 0 for row in measured):
            failures.append(f"{prefix}: committed/remote state contaminated local neutral measurement")
        if expected_hand and any(row["displayHandSide"] != expected_hand or not row["displayDribbling"] for row in measured):
            failures.append(f"{prefix}: requested {expected_hand} hand did not remain the active dribble hand")
        first = measured[0]
        if abs(angle_delta(before["liveHeading"], first["gestureEntryHeading"])) > 1e-5:
            failures.append(f"{prefix}: recorded entry heading differs from the pre-input sample")
        actual_angle = first["actualSignedAngleDegrees"]
        if abs(abs(actual_angle) - angle) > .001:
            failures.append(f"{prefix}: actual initial angle {actual_angle} differs from requested magnitude {angle}")
        if abs(first["maxTurnRateDeg"] - 900) > 1e-5 or abs(first["backTurnSlowFactor"] - .95) > 1e-6:
            failures.append(f"{prefix}: live heading exports changed from 900/.95")
        release_age = 25 if gesture == "held" else 1
        expected_heading = first["gestureEntryHeading"]
        target = first["gestureTargetHeading"]
        latched = False
        maximum_heading_error = 0.0
        for row in measured:
            held = row["stageAge"] < release_age
            if held:
                latched |= abs(angle_delta(expected_heading, target)) > f32(f32(90 * PI) / 180)
            if held or latched:
                expected_heading = heading_step(expected_heading, target)
                if abs(angle_delta(expected_heading, target)) < 1e-5:
                    latched = False
            error = abs(angle_delta(row["liveHeading"], expected_heading))
            maximum_heading_error = max(maximum_heading_error, error)
            if error > 2e-5 or row["isPivotingInPlace"] != latched:
                failures.append(f"{prefix}: heading/latch departed from 900/.95 schedule at age {row['stageAge']}")
        active = [row for row in measured if row["isPivotingInPlace"]]
        if bool(active) != (angle > 90):
            failures.append(f"{prefix}: strict90-degree latch classification was not preserved")
        completion = next((row for row in measured if active and row["physicsFrame"] > active[-1]["physicsFrame"]), None)
        if active and completion is None:
            failures.append(f"{prefix}: owed turn never completed")
        path = sum(math.dist(point(a), point(b)) for a, b in zip([before] + measured, measured)
                   if b["isPivotingInPlace"])
        maximum_speed = max((speed(row) for row in active), default=0)
        mode_speed = first["moveSpeed"]
        envelope = max(speed(before), .65 * mode_speed) if case == "run" else .65 * mode_speed
        if active and maximum_speed > envelope + 2e-5:
            failures.append(f"{prefix}: active speed {maximum_speed} exceeded mode/incoming-momentum envelope {envelope}")
        if case == "idle" and gesture == "held" and angle > 90 and path <= .02:
            failures.append(f"{prefix}: held large turn did not meaningfully travel")
        max_velocity_step = max(math.dist(horizontal(a["displayVelocity"]), horizontal(b["displayVelocity"]))
                                for a, b in zip([before] + measured, measured))
        if max_velocity_step > max(first["accel"], first["decel"]) * DT + 2e-5:
            failures.append(f"{prefix}: velocity changed faster than the finite per-tick rate envelope")
        release = next((row for row in measured if row["stageAge"] >= release_age), None)
        stopped = next((row for row in measured if row["stageAge"] >= release_age and speed(row) <= .03), None)
        if release is None or stopped is None:
            failures.append(f"{prefix}: release did not reach zero travel")
        drift = None
        if stopped:
            tail = [row for row in measured if row["physicsFrame"] >= stopped["physicsFrame"]]
            drift = max(math.dist(point(stopped), point(row)) for row in tail)
            if drift > CONTACT_METRES or any(speed(row) > .03 for row in tail):
                failures.append(f"{prefix}: sustained drift resumed after released velocity reached zero")
        fast = next((row for row in measured if row["stageAge"] < release_age and
                     (completion is None or row["physicsFrame"] >= completion["physicsFrame"]) and speed(row) >= 5.9), None)
        if gesture == "held" and fast is None:
            failures.append(f"{prefix}: held input never resumed 5.9m/s before release")
        neutral_animation = "Dribble" + expected_hand if expected_hand else "Locomotion"
        settled = next((row for row in measured if row["stageAge"] >= release_age and
                        not row["displayPivoting"] and not row["pivotPoseApplied"] and speed(row) <= .03
                        and row["animation"] == neutral_animation), None)
        if settled is None:
            failures.append(f"{prefix}: final neutral animation never settled")
        animation = [row for row in measured if row["animation"] == "Pivot"]
        contact = contact_checks(active, [before] + measured, failures, prefix)
        reached = next((row for row in measured if abs(angle_delta(row["liveHeading"], target)) < 1e-5), None)
        cycles.append({"cycle": cycle, "requestedAngleDegrees": angle, "actualSignedAngleDegrees": actual_angle,
                       "preEntry": {**at(before, before), "headingRadians": before["liveHeading"], "root": before["displayRoot"]},
                       "trueFlagSamples": len(active), "trueFlagSimulationSeconds": len(active) * DT,
                       "owedTurnCompletion": at(completion, before), "headingTargetReached": at(reached, before),
                       "activePathMetres": path, "activeMaxSpeedMetresPerSecond": maximum_speed,
                       "activeSpeedEnvelopeMetresPerSecond": envelope,
                       "completionSpeedMetresPerSecond": speed(completion) if completion else None,
                       "heldFirstSpeedAtLeast5Point9": at(fast, before) if gesture == "held" else None,
                       "release": at(release, before), "firstZeroAfterRelease": at(stopped, before),
                       "maxRootDriftAfterZeroMetres": drift, "maxVelocityDeltaPerTick": max_velocity_step,
                       "maxHeadingScheduleErrorRadians": maximum_heading_error,
                       "animationPivotSamples": len(animation),
                       "animationPivotIntervalSimulationSeconds": len(animation) * DT,
                       "animationPivotFirst": at(animation[0], before) if animation else None,
                       "animationPivotLast": at(animation[-1], before) if animation else None,
                       "animationPivotPlaybackStartSeconds": animation[0]["playbackTime"] if animation else None,
                       "animationPivotPlaybackLastSeconds": animation[-1]["playbackTime"] if animation else None,
                       "finalSettled": at(settled, before), "contact": contact})
    return {"directory": str(directory), "case": case, "gesture": gesture, "angleDegrees": angle,
            "expectedHand": expected_hand, "result": "FAIL" if failures else "PASS", "failures": failures, "cycles": cycles}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", default=os.environ.get("GODOT", "godot"))
    parser.add_argument("--output", type=Path)
    parser.add_argument("--commit")
    parser.add_argument("--angles", type=float, nargs="+", default=ANGLES, choices=ANGLES)
    parser.add_argument("--cases", nargs="+", default=CASES, choices=CASES)
    parser.add_argument("--gestures", nargs="+", default=GESTURES, choices=GESTURES)
    parser.add_argument("--port", type=int, default=23518)
    args = parser.parse_args()
    revision = args.commit or subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, check=True, capture_output=True, text=True).stdout.strip()
    output = (ROOT / (args.output or Path(f".godot/moving-turn-measurements/{uuid.uuid4().hex}"))).resolve()
    if not output.is_relative_to(ROOT / ".godot"):
        parser.error("output must stay below workspace .godot")
    output.mkdir(parents=True, exist_ok=False)
    summary = {"schema": "hooper-moving-turn-measurements/v1", "result": "FAIL", "commit": revision,
               "scene": "res://tests/integration/PivotContinuityCapture.tscn", "simulationHz": 60,
               "timing": "local fixed-step diagnostic; no wall-time, rendered or remote claim",
               "angles": list(args.angles), "cases": list(args.cases), "gestures": list(args.gestures),
               "expectedCaptures": len(args.angles) * len(args.cases) * len(args.gestures), "captures": []}
    try:
        for angle in args.angles:
            for case in args.cases:
                for gesture in args.gestures:
                    directory = output / f"angle-{angle:g}" / f"{case}-{gesture}"
                    command = [sys.executable, str(ROOT / "tools/run_pivot_capture.py"), "--godot", args.godot,
                               "--output", str(directory), "--commit", revision, "--case", case, "--gesture", gesture,
                               "--angle", str(angle), "--port", str(args.port), "--headless", "--fixed-fps"]
                    try:
                        subprocess.run(command, cwd=ROOT, check=True)
                        report = measure(directory / "local")
                    except (subprocess.CalledProcessError, OSError, ValueError, KeyError, TypeError) as error:
                        report = {"directory": str(directory), "angleDegrees": angle, "case": case, "gesture": gesture,
                                  "result": "FAIL", "failures": [str(error)], "cycles": []}
                    summary["captures"].append(report)
                    print(f"[moving-turn-measurements] {report['result']} angle={angle:g} case={case} gesture={gesture}", flush=True)
                    (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
        summary["result"] = "PASS" if all(report["result"] == "PASS" for report in summary["captures"]) else "FAIL"
    finally:
        (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(f"[moving-turn-measurements] {summary['result']} captures={len(summary['captures'])} output={output}")
    raise SystemExit(summary["result"] != "PASS")


if __name__ == "__main__":
    main()
