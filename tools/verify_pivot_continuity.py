"""Verify #416's evaluated scene poses; this does not accept visual feel."""
import argparse
import json
import math
import re
from pathlib import Path

# The unmodified forward-locomotion control reaches 0.38295 m/tick at the toe tip. Round
# upward to centimetres, rather than letting a repair define its own bound.
STEP_METRES = 0.39
ROTATION_DEGREES = 50.0  # Forward control 46.80 degrees; round up to five degrees.
CONTACT_METRES = 0.002  # Numerical tolerance for an exact fixed court contact.
EFFECTORS = ("Hips", "LeftHand", "RightHand", "LeftFoot", "RightFoot", "LeftToeBase", "RightToeBase", "LeftToe_End", "RightToe_End")


def verify(directory):
    directory = Path(directory)
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8-sig"))
    failures = []
    if manifest["result"] != "captured" or manifest["completedCycles"] != 3:
        failures.append("capture did not complete three cycles")
    rows = [json.loads(line) for line in (directory / "trace.jsonl").read_text(encoding="utf-8-sig").splitlines()]
    # Advance(0) may evaluate a skeleton twice. The final evaluation is what
    # reaches the renderer, so comparisons use that final pose per physics tick.
    final = {row["physicsFrame"]: row for row in rows}
    rows = list(final.values())
    image_rows = []
    if manifest["images"]:
        by_sample = {row["sample"]: row for row in rows}
        images = list(directory.glob("frame-*-pose-*.png"))
        if len(images) != manifest["images"]:
            failures.append("rendered image inventory does not match manifest")
        for file in images:
            match = re.fullmatch(r"frame-\d+-pose-(\d+)\.png", file.name)
            if match and int(match[1]) in by_sample:
                image_rows.append(by_sample[int(match[1])])
        image_rows.sort(key=lambda row: row["physicsFrame"])
    maximum_step = 0.0
    maximum_angle = 0.0
    maximum_contact = 0.0
    maximum_pelvis = 0.0
    boundaries = []
    for cycle in (1, 2, 3):
        measured = [row for row in rows if row["measuring"] and row["cycle"] == cycle]
        pivot = [row for row in measured if row["displayPivoting"]]
        if not measured:
            failures.append(f"cycle {cycle}: missing measurement")
            continue
        if manifest["gesture"] == "forward":
            if pivot:
                failures.append(f"cycle {cycle}: forward control pivoted")
            if not any(math.hypot(*row["displayVelocity"][::2]) > 5.9 for row in measured):
                failures.append(f"cycle {cycle}: forward control never reached running speed")
        elif len(pivot) < 3:
            failures.append(f"cycle {cycle}: no sustained pivot")
            continue
        if manifest["role"] == "client" and any(row["localPlayer"] or row["presentationTick"] < 0 for row in measured):
            failures.append(f"cycle {cycle}: observer was not a buffered remote view")
        # A history reset explicitly permits a visual discontinuity under ADR-0027;
        # reject that trial rather than quietly counting it as continuous proof.
        if manifest["role"] == "client" and len({(row["presentationEpoch"], row["presentationResyncCount"]) for row in measured}) != 1:
            failures.append(f"cycle {cycle}: presentation reset invalidates continuity trial")
        for a, b in zip(measured, measured[1:]):
            physics_gap = b["physicsFrame"] - a["physicsFrame"]
            source_gap = (b["presentationTick"] - a["presentationTick"]) if b["presentationTick"] >= 0 else physics_gap
            if physics_gap > 2 or source_gap < 0 or source_gap > 2:
                failures.append(f"cycle {cycle}: missing evaluated source ticks at {b['tick']}")
                continue
            crossing = a["displayPivoting"] != b["displayPivoting"]
            # Include every pivot sample and the full first neutral tail, not
            # only the requested state-change frame (which can hide a late snap).
            in_seam = manifest["gesture"] == "forward" or a["displayPivoting"] or b["displayPivoting"] or (
                pivot and pivot[-1]["physicsFrame"] < b["physicsFrame"] <= pivot[-1]["physicsFrame"] + 9)
            if not in_seam:
                continue
            for side in ("Left", "Right"):
                if b["bones"][f"mixamorig_{side}Toe_End"]["world"][1] < -CONTACT_METRES:
                    failures.append(f"cycle {cycle}: {side} toe penetrated the court during entry/exit")
            frame_max = 0.0
            for name in EFFECTORS:
                bone = "mixamorig_" + name
                pa, pb = a["bones"][bone]["world"], b["bones"][bone]["world"]
                if not all(math.isfinite(value) for value in pa + pb):
                    failures.append(f"cycle {cycle}: non-finite bone {name}")
                    continue
                distance = math.dist(pa, pb)
                frame_max = max(frame_max, distance)
                maximum_step = max(maximum_step, distance / max(1, source_gap))
                limit = STEP_METRES * source_gap if source_gap else CONTACT_METRES
                if distance > limit:
                    failures.append(f"cycle {cycle}: {name} step {distance:.6f} m > {limit:.6f} at tick {b['tick']}")
            for bone, data in b["bones"].items():
                qa, qb = a["bones"][bone]["worldRotation"], data["worldRotation"]
                dot = abs(sum(x * y for x, y in zip(qa, qb)))
                angle = math.degrees(2 * math.acos(min(1, dot)))
                maximum_angle = max(maximum_angle, angle / max(1, source_gap))
                angle_limit = ROTATION_DEGREES * source_gap if source_gap else .1
                if angle > angle_limit:
                    failures.append(f"cycle {cycle}: {bone} rotation {angle:.4f} degrees > {angle_limit:.4f} at tick {b['tick']}")
            if crossing:
                boundaries.append({"cycle": cycle, "tick": b["tick"], "entry": b["displayPivoting"], "maxStep": frame_max})
        if pivot:
            travel = math.dist(pivot[0]["displayRoot"], pivot[-1]["displayRoot"])
            # Moving footwork must transfer support rather than tow a frozen pose.
            # 8 cm is well above solver noise; released flicks retain their fixed plant.
            if manifest["gesture"] == "held" and travel > .08 and len({row.get("pivotSupportToe") for row in pivot}) < 2:
                failures.append(f"cycle {cycle}: moving turn never transferred support")
            if manifest["images"]:
                window = [row for row in image_rows if row["cycle"] == cycle and
                          pivot[0]["physicsFrame"] - 2 <= row["physicsFrame"] <= pivot[-1]["physicsFrame"] + 9]
                if len(window) < 6 or not any(row["physicsFrame"] < pivot[0]["physicsFrame"] for row in window) or not any(
                        row["physicsFrame"] > pivot[-1]["physicsFrame"] for row in window):
                    failures.append(f"cycle {cycle}: rendered frames do not bracket both boundaries")
                if any(b["physicsFrame"] - a["physicsFrame"] > 2 for a, b in zip(window, window[1:])):
                    failures.append(f"cycle {cycle}: rendered frame gaps exceed two physics ticks")
            support = pivot[0].get("pivotSupportToe", "")
            if not support or support not in pivot[0]["bones"]:
                failures.append(f"cycle {cycle}: no identified support toe")
                continue
            contact = pivot[0]["bones"][support]["world"]
            contact_row = pivot[0]
            grounded = 0
            stance_grounded = stance_samples = 0
            stance_start = pivot[0]
            for row in pivot:
                if row.get("pivotSwingError", 0) > CONTACT_METRES:
                    failures.append(f"cycle {cycle}: swing toe missed step target by {row['pivotSwingError']:.6f} m")
                if row.get("pivotSupportToe") != support:
                    incoming = row["pivotSupportToe"]
                    start = stance_start["bones"][incoming]["world"]
                    landed = contact_row["bones"][incoming]["world"]
                    foot_delta = [landed[i] - start[i] for i in (0, 2)]
                    root_delta = [contact_row["displayRoot"][i] - stance_start["displayRoot"][i] for i in (0, 2)]
                    # Evaluate the actual footprint, independently of solver targets.
                    # A support-ID alternation with two frozen feet is not stepping.
                    if math.hypot(*foot_delta) < .02:
                        failures.append(f"cycle {cycle}: completed swing did not relocate its footprint")
                    if math.hypot(*root_delta) > .02 and sum(a * b for a, b in zip(foot_delta, root_delta)) <= 0:
                        failures.append(f"cycle {cycle}: completed step opposed measured body travel")
                    if stance_samples >= 6 and stance_grounded < 2:
                        failures.append(f"cycle {cycle}: stance never established sustained ground contact")
                    if abs(contact_row["bones"][row["pivotSupportToe"]]["world"][1]) > CONTACT_METRES:
                        failures.append(f"cycle {cycle}: transferred onto an airborne toe")
                    support = row["pivotSupportToe"]
                    contact = row["bones"][support]["world"]
                    stance_grounded = stance_samples = 0
                    stance_start = row
                contact_row = row
                position = row["bones"][support]["world"]
                stance_samples += 1
                if abs(position[1]) <= CONTACT_METRES:
                    stance_grounded += 1
                if row.get("pivotStepping"):
                    for toe, target in ((support, row["pivotSupportTarget"]), (row["pivotSwingToe"], row["pivotSwingTarget"])):
                        error = math.dist(row["bones"][toe]["world"], target)
                        if error > CONTACT_METRES:
                            failures.append(f"cycle {cycle}: {toe} missed world foot target by {error:.6f} m")
                horizontal_error = math.hypot(position[0] - contact[0], position[2] - contact[2])
                maximum_contact = max(maximum_contact, horizontal_error)
                maximum_pelvis = max(maximum_pelvis, row.get("pivotPelvisShift", 0))
                if horizontal_error > CONTACT_METRES:
                    failures.append(f"cycle {cycle}: support toe slid {horizontal_error:.6f} m")
                if row.get("pivotStanceAge", row.get("pivotAge", -1)) >= 4:
                    grounded += 1
                    if abs(position[1]) > CONTACT_METRES:
                        failures.append(f"cycle {cycle}: support toe not grounded ({position[1]:.6f} m)")
                for side in ("Left", "Right"):
                    if row["bones"]["mixamorig_" + side + "Toe_End"]["world"][1] < -CONTACT_METRES:
                        failures.append(f"cycle {cycle}: {side} toe penetrated the court")
            if grounded < 2:
                failures.append(f"cycle {cycle}: no sustained grounded contact")
            if stance_samples >= 6 and stance_grounded < 2:
                failures.append(f"cycle {cycle}: final stance never established sustained ground contact")
    if manifest["gesture"] != "forward" and len(boundaries) != 6:
        failures.append(f"expected six evaluated pivot boundaries, got {len(boundaries)}")
    return {"directory": str(directory), "failures": failures, "boundaries": boundaries,
            "maxStepMetresPerTick": maximum_step, "maxWorldRotationDegreesPerTick": maximum_angle,
            "maxContactErrorMetres": maximum_contact, "maxPelvisAssistanceMetres": maximum_pelvis}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    result = verify(args.directory)
    print(json.dumps(result, indent=2))
    raise SystemExit(bool(result["failures"]))
