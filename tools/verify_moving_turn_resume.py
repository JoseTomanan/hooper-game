"""Verify a real #430 release/resume/release sequence and its final pose handoff."""
import argparse
import json
import math
from pathlib import Path

from verify_pivot_continuity import CONTACT_METRES, verify


def speed(row):
    return math.hypot(*row["displayVelocity"][::2])


def verify_resume(directory):
    directory = Path(directory)
    pose = verify(directory)
    failures = list(pose["failures"])
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8-sig"))
    if (manifest.get("resumeTravel") is not True or manifest["role"] != "local"
            or manifest["gesture"] != "held" or not manifest["fixedFps"]
            or manifest["protection"] or manifest["resetOwnerPresentation"]
            or manifest["requestedAngleDegrees"] != 180 or manifest["mutationApplied"]):
        failures.append("resume proof requires an unmutated local fixed60Hz held180 capture without posture/reset flags")
    entries = manifest["gestureEntries"]
    if len(entries) != 3 or {entry["cycle"] for entry in entries} != {1, 2, 3}:
        failures.append("resume changed the original three gesture-entry records")
    raw = [json.loads(line) for line in (directory / "trace.jsonl").read_text(encoding="utf-8-sig").splitlines()]
    rows = sorted({row["physicsFrame"]: row for row in raw}.values(), key=lambda row: row["physicsFrame"])
    cycles = []
    for cycle in (1, 2, 3):
        prefix = f"cycle{cycle}"
        measured = [row for row in rows if row["cycle"] == cycle and row["measuring"] and row["stage"] == "measured"]
        by_age = {row["stageAge"]: row for row in measured}
        if any(age not in by_age for age in range(51)):
            failures.append(f"{prefix}: missing source samples through the second release and stop")
            continue
        if any(b["physicsFrame"] - a["physicsFrame"] != 1 for a, b in zip(measured, measured[1:])):
            failures.append(f"{prefix}: missing evaluated physics intervals")
        if any(row.get("resumeTravel") is not True for row in measured):
            failures.append(f"{prefix}: trace did not exercise resumed travel")
        for kind, age in (("travel-resume", 28), ("travel-second-release", 40)):
            matching = [event for event in manifest["events"] if event["kind"] == kind
                        and event["tick"] == by_age[age]["tick"]]
            if len(matching) != 1:
                failures.append(f"{prefix}: missing unique {kind} event at actual stageAge{age}")
        release_events = [event for event in manifest["events"] if event["kind"] == "input-release"
                          and event["tick"] == by_age[25]["tick"]]
        if len(release_events) != 1:
            failures.append(f"{prefix}: first release did not occur at actual stageAge25")
        if not speed(by_age[24]) > speed(by_age[25]) > speed(by_age[26]) > speed(by_age[27]) > .03:
            failures.append(f"{prefix}: initial release did not brake while preserving travel before resume")
        if not (speed(by_age[28]) > speed(by_age[27]) and speed(by_age[29]) > speed(by_age[28])):
            failures.append(f"{prefix}: resumed production input did not reaccelerate before stopping")
        wish = by_age[0]["gestureWish"]
        velocity = by_age[29]["displayVelocity"][::2]
        if sum(a * b for a, b in zip(wish, velocity)) <= 0:
            failures.append(f"{prefix}: resumed travel opposed the original recorded wish")
        if any(row["gestureWish"] != wish for row in measured):
            failures.append(f"{prefix}: resume recalculated the recorded gesture wish")
        if speed(by_age[40]) >= speed(by_age[39]):
            failures.append(f"{prefix}: second release did not begin finite braking")
        stopped = next((row for row in measured if row["stageAge"] >= 40 and speed(row) <= .03), None)
        stop_bound = 40 + math.ceil(speed(by_age[39]) / (by_age[39]["decel"] / 60))
        if stopped is None or stopped["stageAge"] > stop_bound:
            failures.append(f"{prefix}: second release exceeded the documented finite-deceleration bound")
        if stopped:
            tail = [row for row in measured if row["stageAge"] >= stopped["stageAge"]]
            if any(speed(row) > .03 or math.dist(row["displayRoot"], stopped["displayRoot"]) > CONTACT_METRES for row in tail):
                failures.append(f"{prefix}: travel resumed after the second stop")
        pivot = [row for row in measured if row["displayPivoting"]]
        starts = sum(row["displayPivoting"] and (index == 0 or not measured[index - 1]["displayPivoting"])
                     for index, row in enumerate(measured))
        if starts != 1 or not pivot or pivot[-1]["stageAge"] >= 25:
            failures.append(f"{prefix}: resumed same-direction input introduced an extra turn")
        settled = next((row for row in measured if row["stageAge"] >= 40 and speed(row) <= .03
                        and not row["displayPivoting"] and not row["pivotPoseApplied"]), None)
        if settled is None or settled["stageAge"] > measured[-1]["stageAge"] - 2:
            failures.append(f"{prefix}: second release never relinquished the final neutral pose")
        cycles.append({"cycle": cycle, "firstReleaseSpeed": speed(by_age[25]), "beforeResumeSpeed": speed(by_age[27]),
                       "resumedSpeed": speed(by_age[29]), "secondReleaseSpeed": speed(by_age[40]),
                       "secondStopStageAge": stopped["stageAge"] if stopped else None,
                       "finalSettledStageAge": settled["stageAge"] if settled else None})
    return {"directory": str(directory), "result": "FAIL" if failures else "PASS", "failures": failures,
            "cycles": cycles, "continuity": pose}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    result = verify_resume(parser.parse_args().directory)
    print(json.dumps(result, indent=2))
    raise SystemExit(bool(result["failures"]))
