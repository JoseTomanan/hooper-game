"""Check #416 owner-history isolation and protection handoff against paired captures."""
import argparse
import json
import math
from pathlib import Path

POSITION_TOLERANCE = 1e-5
ROTATION_TOLERANCE_DEGREES = 0.1
BONE_COUNT = 65


def _load(directory, label, failures):
    directory = Path(directory)
    try:
        manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8-sig"))
        raw = [json.loads(line) for line in (directory / "trace.jsonl").read_text(encoding="utf-8-sig").splitlines()]
        # AnimationTree.Advance(0) can evaluate twice; retain the final rendered pose.
        rows = list({row["physicsFrame"]: row for row in raw}.values())
        measured = {}
        for row in rows:
            if not row.get("measuring"):
                continue
            key = (row["cycle"], row["stage"], row["stageAge"])
            if key in measured:
                failures.append(f"{label}: duplicate measured row key {key}")
            measured[key] = row
        if manifest.get("result") != "captured" or manifest.get("requestedCycles") != 3 or manifest.get("completedCycles") != 3:
            failures.append(f"{label}: capture must complete exactly three cycles")
        if manifest.get("role") != "local" or manifest.get("gesture") != "held":
            failures.append(f"{label}: expected local held-gesture capture")
        if manifest.get("diagnosticProbeApplied") or manifest.get("diagnosticPivotXfade", -1) != -1:
            failures.append(f"{label}: blend probe invalidates paired regression")
        for key, row in measured.items():
            if len(row.get("bones", {})) != BONE_COUNT:
                failures.append(f"{label}: row {key} must contain {BONE_COUNT} bones")
        return manifest, measured
    except (OSError, ValueError, KeyError, TypeError) as error:
        failures.append(f"{label}: invalid capture: {error}")
        return {}, {}


def _compare(first, second, label, failures):
    if first.keys() != second.keys():
        failures.append(f"{label}: measured row keys differ ({len(first)} versus {len(second)})")
    maximum_position = maximum_angle = maximum_component = 0.0
    bone_pairs = 0
    for key in first.keys() & second.keys():
        a, b = first[key].get("bones", {}), second[key].get("bones", {})
        if a.keys() != b.keys():
            failures.append(f"{label}: bone names differ at {key}")
        for bone in a.keys() & b.keys():
            try:
                pa, pb = a[bone]["world"], b[bone]["world"]
                qa, qb = a[bone]["localRotation"], b[bone]["localRotation"]
                if len(pa) != 3 or len(pb) != 3 or len(qa) != 4 or len(qb) != 4 or not all(math.isfinite(x) for x in pa + pb + qa + qb):
                    raise ValueError("invalid vector/quaternion")
                na = math.sqrt(sum(x * x for x in qa))
                nb = math.sqrt(sum(x * x for x in qb))
                if na == 0 or nb == 0:
                    raise ValueError("zero quaternion")
                dot = sum(x * y for x, y in zip(qa, qb)) / (na * nb)
                qa, qb = [x / na for x in qa], [x / nb for x in qb]
                # q and -q express the same rotation; compare the nearest hemisphere.
                sign = -1 if dot < 0 else 1
                maximum_position = max(maximum_position, math.dist(pa, pb))
                maximum_angle = max(maximum_angle, math.degrees(2 * math.acos(min(1.0, abs(dot)))))
                maximum_component = max(maximum_component, max(abs(x - sign * y) for x, y in zip(qa, qb)))
                bone_pairs += 1
            except (KeyError, TypeError, ValueError) as error:
                failures.append(f"{label}: invalid bone {bone} at {key}: {error}")
    if maximum_position > POSITION_TOLERANCE:
        failures.append(f"{label}: world position error {maximum_position:.9g} m exceeds {POSITION_TOLERANCE}")
    if maximum_angle > ROTATION_TOLERANCE_DEGREES:
        failures.append(f"{label}: local rotation error {maximum_angle:.9g} degrees exceeds {ROTATION_TOLERANCE_DEGREES}")
    return {"rowsFirst": len(first), "rowsSecond": len(second), "bonePairs": bone_pairs,
            "maxWorldPositionErrorMetres": maximum_position, "maxLocalRotationAngleDegrees": maximum_angle,
            "maxHemisphereQuaternionComponentError": maximum_component}


def verify_context(normal_dir, reset_dir, protection_fixed_dir, protection_control_dir):
    failures = []
    normal_manifest, normal = _load(normal_dir, "normal owner", failures)
    reset_manifest, reset = _load(reset_dir, "reset owner", failures)
    fixed_manifest, fixed = _load(protection_fixed_dir, "protection fixed", failures)
    control_manifest, control = _load(protection_control_dir, "protection control", failures)
    for label, manifest, case, protection, reset_flag, mutation in (
        ("normal owner", normal_manifest, "run", False, False, False),
        ("reset owner", reset_manifest, "run", False, True, False),
        ("protection fixed", fixed_manifest, "right", True, False, False),
        ("protection control", control_manifest, "right", True, False, True),
    ):
        if manifest.get("case") != case or manifest.get("protection") is not protection or manifest.get("resetOwnerPresentation") is not reset_flag:
            failures.append(f"{label}: incorrect case/protection/owner-reset flags")
        if manifest.get("mutationApplied") is not mutation or manifest.get("mutation") != ("disable-pivot-pose" if mutation else "none"):
            failures.append(f"{label}: incorrect modifier mutation")
    for label, rows in (("normal owner", normal), ("reset owner", reset)):
        if len(rows) < 300 or {key[0] for key in rows} != {1, 2, 3}:
            failures.append(f"{label}: need at least 300 measured rows spanning all three cycles")
    reset_events = [event for event in reset_manifest.get("events", []) if event.get("kind") == "owner-remote-history-reset"]
    if len(reset_events) != 3:
        failures.append("reset owner: expected exactly three reset events")
    for cycle in (1, 2, 3):
        row = reset.get((cycle, "measured", 4))
        events = [event for event in reset_events if f"cycle={cycle};" in event.get("detail", "")]
        if row is None or len(events) != 1 or events[0].get("tick") != row.get("tick") or not row.get("isPivotingInPlace"):
            failures.append(f"reset owner: cycle {cycle} needs one reset at measured age4 during owning pivot")
    owner_result = _compare(normal, reset, "owner reset", failures)
    owner_result["resetEvents"] = len(reset_events)
    # Epoch may change because this fixture resets unrelated history. Body and
    # display inputs, ordinary playback and final pose must remain unchanged.
    ignored = {"sample", "tick", "physicsFrame", "renderFrame", "commit", "presentationEpoch", "bones"}
    different_fields = set()
    for key in normal.keys() & reset.keys():
        for field in normal[key].keys() | reset[key].keys():
            if field not in ignored and normal[key].get(field) != reset[key].get(field):
                different_fields.add(field)
    if different_fields:
        failures.append("owner reset: non-epoch state changed: " + ", ".join(sorted(different_fields)))
    owner_result["differentStateFields"] = sorted(different_fields)
    fixed = {key: row for key, row in fixed.items() if key[1] == "measured" and 20 <= key[2] <= 24}
    control = {key: row for key, row in control.items() if key[1] == "measured" and 20 <= key[2] <= 24}
    for label, rows in (("protection fixed", fixed), ("protection control", control)):
        for cycle in (1, 2, 3):
            if sum(key[0] == cycle for key in rows) != 5:
                failures.append(f"{label}: cycle {cycle} needs exactly five age20..24 rows")
        if len(rows) != 15 or not all(row.get("displayProtecting") is True for row in rows.values()):
            failures.append(f"{label}: need fifteen actual protection rows")
    if not all(row.get("pivotPoseApplied") is False for row in fixed.values()):
        failures.append("protection fixed: pivot modifier still owns neutral handoff")
    if not all(row.get("mutationApplied") is True for row in control.values()):
        failures.append("protection control: modifier mutation not applied to measured rows")
    protection_result = _compare(fixed, control, "protection handoff", failures)
    return {"failures": failures, "ownerReset": owner_result, "protectionHandoff": protection_result,
            "positionToleranceMetres": POSITION_TOLERANCE, "rotationToleranceDegrees": ROTATION_TOLERANCE_DEGREES}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("normal_dir", type=Path)
    parser.add_argument("reset_dir", type=Path)
    parser.add_argument("protection_fixed_dir", type=Path)
    parser.add_argument("protection_control_dir", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = verify_context(args.normal_dir, args.reset_dir, args.protection_fixed_dir, args.protection_control_dir)
    rendered = json.dumps(result, indent=2)
    if args.output:
        args.output.write_text(rendered + "\n", encoding="utf-8")
    print(rendered)
    return bool(result["failures"])


if __name__ == "__main__":
    raise SystemExit(main())
