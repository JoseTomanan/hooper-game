"""Measure #427 final displayed poses and apply the frozen rebound proof gates."""
import argparse
import copy
import json
import math
from pathlib import Path

CONTACT_LIMIT = .025
BOUNDS = {"Head": .24}
BOUNDS.update({side + part: .30 for side in ("Left", "Right")
               for part in ("Hand", "HandMiddle1", "HandIndex1", "HandPinky1")})
BOUNDS.update({side + part: .24 for side in ("Left", "Right") for part in ("Foot", "ToeBase")})


def distance(a, b):
    return math.sqrt(sum((x - y) ** 2 for x, y in zip(a, b)))


def vector(value):
    return isinstance(value, list) and len(value) == 3 and all(
        isinstance(x, (float, int)) and math.isfinite(x) for x in value)


def is_owner(manifest):
    return manifest["role"] in ("client", "owner") and manifest.get("actor") == "client"


def source_tick(row, manifest):
    if manifest["role"] in ("client", "owner") and not is_owner(manifest):
        return row.get("posePresentationTick", -1)
    return row.get("physicsFrame", -1)


def catching(row):
    return row.get("pose", {}).get("animation", row.get("animation")) == "ReboundGrab"


def intervals(rows):
    groups, current = [], []
    for row in rows:
        if catching(row):
            current.append(row)
        elif current:
            groups.append(current)
            current = []
    if current:
        groups.append(current)
    return groups


def body_point(row, name):
    if "bodyBones" in row["pose"]:
        return row["pose"]["bodyBones"][name]
    point = row["pose"]["bones"][name]
    root = row["displayRoot"]
    x, y, z = [a - b for a, b in zip(point, root)]
    heading = row["heading"]
    # Display heading is radians around world Y. Remove it before comparing
    # articulated motion, so whole-body translation/turning is not an arm pop.
    c, s = math.cos(heading), math.sin(heading)
    return [c * x - s * z, y, s * x + c * z]


def palm_error(row):
    hand = row["displayHand"]
    bones = row["pose"]["bones"]
    palm = [(a + b) / 2 for a, b in zip(bones[hand + "HandIndex1"], bones[hand + "HandPinky1"])]
    return abs(distance(palm, row["ball"]) - row["ballRadius"])


def analyze(directory, mutate_disconnected=False, mutate_delayed=False):
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8"))
    rows = [json.loads(line) for line in (directory / "trace.jsonl").read_text(encoding="utf-8").splitlines() if line.strip()]
    failures, invalid = [], []
    if manifest.get("result") != "captured" or manifest.get("failure"):
        invalid.append("capture did not finish successfully")
    if not rows:
        invalid.append("trace is empty")
    if manifest.get("role") not in ("local", "server", "client", "owner"):
        invalid.append("invalid capture role")
    if invalid:
        return dict(case=str(directory), result="failed", validCapture=False,
                    metrics={}, failures=invalid, invalid=invalid)

    # Select whole observations, never a ball from one row and bones from another.
    # Repeated held presentation ticks are one source observation, not coverage.
    unique, previous = {}, -1
    for row in rows:
        tick = source_tick(row, manifest)
        if not isinstance(tick, int) or tick < 0:
            continue
        if tick < previous:
            failures.append("source clock moved backwards")
        previous = tick
        unique[tick] = row
    ordered = [unique[tick] for tick in sorted(unique)]

    # Controls mutate the measured observations and then use the ordinary gates.
    # They do not set a failure flag or alter the detector's thresholds.
    if mutate_disconnected or mutate_delayed:
        ordered = copy.deepcopy(ordered)
        for group in intervals(ordered):
            if mutate_disconnected:
                for row in group:
                    bones = row.get("pose", {}).get("bones", {})
                    for finger in ("HandIndex1", "HandPinky1"):
                        name = row.get("displayHand", "Right") + finger
                        if name in bones:
                            bones[name][0] += 1
            if mutate_delayed:
                for row in group[:6]:
                    row["animation"] = "HeldRight"
                    row.setdefault("pose", {})["animation"] = "HeldRight"
        unique = {source_tick(row, manifest): row for row in ordered}

    groups = intervals(ordered)
    events = manifest.get("events", [])
    if is_owner(manifest):
        recoveries = [e for e in events if e.get("kind") == "confirmed-recovery-received"]
        recovery_ticks = [e["physicsFrame"] for e in recoveries]
        onset_clock = "owning-client confirmed receipt physicsFrame"
    elif manifest["role"] in ("client", "owner"):
        recoveries = [e for e in events if e.get("kind") == "authoritative-recovery-received"]
        recovery_ticks = [e["detail"]["sourceTick"] for e in recoveries]
        onset_clock = "authoritative recovery source tick / display source tick"
    else:
        recoveries = [e for e in events if e.get("kind") == "live-holder-edge"]
        recovery_ticks = [e["physicsFrame"] for e in recoveries]
        onset_clock = "authority physicsFrame"

    metrics = dict(rawSamples=len(rows), uniqueSourceSamples=len(ordered), recoveryCount=len(recoveries),
                   catchIntervals=len(groups), recoveryTicks=recovery_ticks, onsetClock=onset_clock,
                   contactLimitM=CONTACT_LIMIT, continuityBoundsM=BOUNDS,
                   ownershipBallBypass=is_owner(manifest), intervals=[],
                   bodyCoordinateMode="sampled model rigid frame; world metres" if manifest.get("schema", "").endswith("/v2")
                       else "legacy signal-time world pose / sampled heading; moving-transform comparisons are diagnostic")
    if len(recoveries) < 2:
        failures.append("fewer than two observed real recoveries")
    if len(groups) != 2:
        failures.append("expected exactly two separated ReboundGrab intervals")
    moving = manifest.get("moving", False)

    for index, group in enumerate(groups):
        start, end = source_tick(group[0], manifest), source_tick(group[-1], manifest)
        recovery = recovery_ticks[index] if index < len(recovery_ticks) else None
        onset = start - recovery if recovery is not None else None
        # Include entry and exit in one continuous observed window. An omitted
        # intermediate source tick cannot hide a transient discontinuity.
        # Include the complete six-tick exit bridge and its first unmodified tick.
        expected_ticks = list(range(start - 1, end + 8))
        window = [unique[t] for t in expected_ticks if t in unique]
        missing = [t for t in expected_ticks if t not in unique]
        bad_pose, incoherent, missing_bones, missing_heading = [], [], [], []
        for row in window:
            tick = source_tick(row, manifest)
            pose = row.get("pose", {})
            if not row.get("poseMatchesPhysics") or pose.get("physicsFrame") != row.get("physicsFrame"):
                bad_pose.append(tick)
            if manifest["role"] in ("client", "owner"):
                bypass = is_owner(manifest) and row.get("isLocalSubject") is True
                if not bypass and not (row.get("posePresentationTick") == row.get("selectedPresentationTick") == row.get("ballPresentationTick")):
                    incoherent.append(tick)
            bones = pose.get("bones", {})
            if not all(vector(bones.get(name)) for name in BOUNDS):
                missing_bones.append(tick)
            if "bodyBones" in pose and not all(vector(pose["bodyBones"].get(name)) for name in BOUNDS):
                missing_bones.append(tick)
            if manifest.get("schema", "").endswith("/v2"):
                transform = pose.get("skeletonTransform", [])
                if "bodyBones" not in pose or not isinstance(transform, list) or len(transform) != 12 or not all(
                        isinstance(x, (float, int)) and math.isfinite(x) for x in transform):
                    missing_bones.append(tick)
            if not vector(row.get("displayRoot")) or not isinstance(row.get("heading"), (int, float)) or not math.isfinite(row["heading"]):
                missing_heading.append(tick)
        contact, missing_contact = [], []
        for row in group:
            age = source_tick(row, manifest) - start
            if not 6 <= age <= 29:
                continue
            # Both the ball and fingers come from this final displayed sample.
            # A wrist proxy cannot prove surface contact of the visible palm.
            hand = row.get("displayHand")
            bones = row.get("pose", {}).get("bones", {})
            state = row.get("displayBallState") if manifest["role"] == "client" and not is_owner(manifest) else row.get("ballState")
            holder = row.get("displayHolder") if manifest["role"] == "client" and not is_owner(manifest) else row.get("holder")
            eligible = hand in ("Left", "Right") and state == "Held" and holder == row.get("subject")
            if eligible:
                radius = row.get("ballRadius")
                complete = vector(row.get("ball")) and isinstance(radius, (int, float)) and math.isfinite(radius) and radius > 0
                complete = complete and all(vector(bones.get(hand + finger)) for finger in ("HandIndex1", "HandPinky1"))
                if complete:
                    contact.append(dict(tick=source_tick(row, manifest), age=age, errorM=palm_error(row)))
                else:
                    missing_contact.append(source_tick(row, manifest))
        edges = []
        for a, b in zip(window, window[1:]):
            ta, tb = source_tick(a, manifest), source_tick(b, manifest)
            if ta in missing_bones + missing_heading or tb in missing_bones + missing_heading:
                continue
            displacements = {name: distance(body_point(a, name), body_point(b, name)) for name in BOUNDS}
            edges.append(dict(fromTick=ta, toTick=tb, displacementM=displacements))
        maxima = {name: max((edge["displacementM"][name] for edge in edges), default=None) for name in BOUNDS}
        measure = dict(ordinal=index + 1, firstTick=start, lastTick=end, recoveryTick=recovery,
                       onsetDelayTicks=onset, catchSamples=len(group), missingSourceTicks=missing,
                       posePhysicsMismatchTicks=bad_pose, incoherentPresentationTicks=incoherent,
                       missingFinalBonesTicks=missing_bones, missingDisplayHeadingTicks=missing_heading,
                       contactSamples=len(contact), missingPalmContactTicks=missing_contact,
                       maxContactErrorM=max((c["errorM"] for c in contact), default=None),
                       contact=contact, maxBodyDisplacementM=maxima, edges=edges)
        dribbling = [r for r in group if (r.get("displayBallState") if manifest["role"] == "client"
                     and not is_owner(manifest) else r.get("ballState")) == "Dribbling"]
        speeds = [distance(r["velocity"], [0, 0, 0]) for r in dribbling if vector(r.get("velocity"))]
        wrist_y = [body_point(r, r["displayHand"] + "Hand")[1] for r in dribbling
                   if r["displayHand"] + "Hand" in r.get("pose", {}).get("bones", {})
                   and vector(r.get("displayRoot")) and isinstance(r.get("heading"), (float, int))]
        measure["movingDribble"] = dict(samples=len(dribbling), maxSpeedMps=max(speeds, default=0),
                                       wristTravelM=max(wrist_y)-min(wrist_y) if wrist_y else 0,
                                       contactClaim="Held surface contact" if not moving else
                                           "continuity and live dribble motion; palm contact is not required while the ball bounces")
        metrics["intervals"].append(measure)
        prefix = f"catch {index + 1}: "
        if len(group) != 30:
            failures.append(prefix + "catch did not retain its 30-source-tick duration")
        if onset is None or not 0 <= onset <= 2:
            failures.append(prefix + "onset exceeds two source ticks after observed recovery")
        if missing:
            failures.append(prefix + "missing source tick coverage including entry/exit")
        if bad_pose:
            failures.append(prefix + "pose does not match sampled physics frame")
        if incoherent:
            failures.append(prefix + "incoherent remote pose/ball source sample")
        if missing_bones:
            failures.append(prefix + "missing final bones")
        if missing_heading:
            failures.append(prefix + "missing display root/heading")
        if moving and (len(dribbling) < 15 or max(speeds, default=0) < 1 or not wrist_y or max(wrist_y)-min(wrist_y) < .1):
            failures.append(prefix + "moving recovery lacks live travel and a moving dribble hand")
        if not moving and len(contact) < 15:
            failures.append(prefix + "fewer than 15 Held palm contact samples at ages 6..29")
        if missing_contact:
            failures.append(prefix + "missing Held palm/ball observation")
        if any(c["errorM"] > CONTACT_LIMIT for c in contact):
            failures.append(prefix + "palm surface contact exceeds 0.025m")
        if len(edges) != len(expected_ticks) - 1 or any(e["toTick"] - e["fromTick"] != 1 for e in edges):
            failures.append(prefix + "incomplete adjacent-source-tick continuity")
        for name, bound in BOUNDS.items():
            if maxima[name] is not None and maxima[name] > bound:
                failures.append(prefix + name + " displacement exceeds frozen continuity bound")

    return dict(case=str(directory), role=manifest["role"], actor=manifest.get("actor"),
                resourceSha256=manifest.get("animationResourceSha256"), validCapture=True,
                metrics=metrics, result="failed" if failures else "passed", failures=failures, invalid=[])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--output", type=Path)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--measure-only", action="store_true")
    mode.add_argument("--verify", action="store_true", help="apply proof gates (default)")
    parser.add_argument("--mutate-disconnected", action="store_true")
    parser.add_argument("--mutate-delayed", action="store_true")
    args = parser.parse_args()
    paths = [args.directory] if (args.directory / "manifest.json").is_file() else sorted(
        p.parent for p in args.directory.rglob("manifest.json"))
    results = []
    for path in paths:
        try:
            results.append(analyze(path, args.mutate_disconnected, args.mutate_delayed))
        except (OSError, ValueError, KeyError, TypeError) as error:
            results.append(dict(case=str(path), validCapture=False, result="failed", metrics={},
                                failures=[str(error)], invalid=[str(error)]))
    valid = bool(results) and all(r["validCapture"] for r in results)
    passed = valid and all(r["result"] == "passed" for r in results)
    report = dict(result="measured" if args.measure_only and valid else "passed" if passed else "failed",
                  proofPassed=passed, measureOnly=args.measure_only,
                  mutation="disconnected" if args.mutate_disconnected else "delayed" if args.mutate_delayed else "none",
                  cases=results)
    # Metrics precede gates/failures in every case, including failing captures.
    payload = json.dumps(report, indent=2)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(payload, encoding="utf-8")
    else:
        print(payload)
    print(f"[rebound-proof] {report['result'].upper()} cases={len(results)} report={args.output}")
    raise SystemExit(0 if valid and (args.measure_only or passed) else 1)


if __name__ == "__main__":
    main()
