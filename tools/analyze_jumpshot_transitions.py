"""Measure #440 production final poses; missing source-tick evidence fails proof.

The root-centering invariant is structural (a stationary shot has no horizontal
skin-root travel), not a claim of human appearance/feel acceptance. Comparison
gates require an explicitly supplied, matched immutable baseline.
"""
from __future__ import annotations

import argparse
import gzip
import json
import math
from pathlib import Path

from analyze_jumpshot_capture import distance

JOINTS = ("RightHand", "LeftHand", "RightFoot", "LeftFoot", "RightToeBase",
          "LeftToeBase", "Hips", "Spine", "Spine2", "Neck")
ROOT_CENTER_FLOOR_M = 1e-4
OBSERVER_ROLES = ("client", "observer")
PAIRS = {("Inactive", "Startup"): "entry", ("Startup", "Active"): "startup-active",
         ("Active", "Recovery"): "active-recovery", ("Startup", "Recovery"): "fake-recovery",
         ("Recovery", "Inactive"): "return"}


def load_json(path):
    return json.loads(gzip.decompress(path.read_bytes()).decode("utf-8") if path.suffix == ".gz"
                      else path.read_text(encoding="utf-8"))


def load_rows(directory):
    return [json.loads(line) for line in (directory / "trace.jsonl").read_text(encoding="utf-8").splitlines()]


def clock(row, remote):
    return row["selectedPresentationTick"] if remote else row["physicsFrame"]


def pose_signature(row):
    pose = row["pose"]
    # Holds may generate a new local render/physics number while preserving the
    # selected server pose. Those local counters are not part of the skin pose.
    return {key: pose.get(key, row.get(key)) for key in
            ("move", "phase", "frame", "animation", "clip", "playbackTime", "root", "bones")}


def observations(rows, remote, duplicate_changes=None):
    """Keep one identical pose per source tick; never silently erase a change."""
    unique, failures = [], []
    duplicates = 0
    previous = None
    for row in rows:
        if not row.get("pose", {}).get("bones"):
            continue
        if row["pose"].get("physicsFrame") != row.get("physicsFrame"):
            # Initial setup can precede SkeletonUpdated. Shot windows below
            # reject the resulting hole, rather than accepting a stale pose.
            continue
        if remote and not (row["pose"].get("presentationTick") ==
                           row.get("selectedPresentationTick") == row.get("ballPresentationTick")):
            failures.append("incoherent selected player/ball source tick")
            continue
        if remote and clock(row, True) < 0:
            continue
        if unique and clock(row, remote) == clock(unique[-1], remote):
            duplicates += 1
            # Keep the earliest pose as the numeric source-tick sample, but
            # inspect every RAW adjacent hold edge. Repeated observations of
            # an already changed pose are not eleven separate physical jumps.
            if pose_signature(row) != pose_signature(previous):
                failures.append("changed final pose on duplicate source tick")
                if duplicate_changes is not None:
                    joints = {}
                    for name in JOINTS:
                        joints[name] = {space: {"from": joint(previous, name, space),
                                               "to": joint(row, name, space),
                                               "stepM": distance(joint(previous, name, space), joint(row, name, space))}
                                        for space in ("body", "world")}
                    duplicate_changes.append({"sourceTick": clock(row, remote),
                        "fromPhysicsFrame": previous["physicsFrame"], "toPhysicsFrame": row["physicsFrame"],
                        "fromPlayback": playback(previous), "toPlayback": playback(row), "joints": joints,
                        "maxWorldPositionStepM": max(distance(previous["pose"]["bones"][name]["world"][:3],
                                                             row["pose"]["bones"][name]["world"][:3])
                                                     for name in previous["pose"]["bones"])})
            previous = row
            continue
        if unique and clock(row, remote) < clock(unique[-1], remote):
            if row.get("presentationResyncCount", 0) == unique[-1].get("presentationResyncCount", 0):
                failures.append("source clock regressed without presentation reset")
        unique.append(row)
        previous = row
    return unique, failures, duplicates


def joint(row, name, space="body"):
    value = row["pose"]["bones"]["mixamorig_" + name][space][:3]
    if len(value) != 3 or not all(math.isfinite(x) for x in value):
        raise ValueError("missing/nonfinite final joint " + name)
    return value


def playback(row):
    pose = row["pose"]
    return {"tick": row.get("selectedPresentationTick", -1), "physicsFrame": row["physicsFrame"],
            **{key: pose.get(key, row.get(key)) for key in
               ("phase", "frame", "requestedState", "animation", "clip", "playbackTime", "recoveryBlend", "fadingFrom", "fadingTime", "callback")}}


def measure_boundaries(rows, remote):
    """Five poses around each seam include first evaluated pose and two after it."""
    result, failures = [], []
    for index in range(1, len(rows)):
        a, b = rows[index - 1], rows[index]
        old, new = a["pose"], b["pose"]
        pair = old.get("phase"), new.get("phase")
        if pair not in PAIRS or not (old.get("move") == "jumpshot" or new.get("move") == "jumpshot"):
            continue
        name = PAIRS[pair]
        neighborhood = rows[max(0, index - 2):index + 3]
        complete = len(neighborhood) == 5 and all(clock(y, remote) - clock(x, remote) == 1
                     for x, y in zip(neighborhood, neighborhood[1:]))
        stable = len({r.get("presentationResyncCount", 0) for r in neighborhood}) == 1
        if not complete:
            failures.append("incomplete adjacent source ticks at " + name)
        if not stable:
            failures.append("presentation reset spans " + name)
        per_joint = {}
        for bone in JOINTS:
            positions = [joint(row, bone) for row in neighborhood]
            steps = [[y - x for x, y in zip(a, b)] for a, b in zip(positions, positions[1:])]
            magnitudes = [math.sqrt(sum(x*x for x in step)) for step in steps]
            changes = [distance(a, b) for a, b in zip(steps, steps[1:])]
            per_joint[bone] = {"stepM": magnitudes, "velocityChangeMPerTick": changes,
                               "maxStepM": max(magnitudes, default=0),
                               "maxVelocityChangeMPerTick": max(changes, default=0)}
        result.append({"name": name, "transition": "->".join(pair), "complete": complete,
                       "stable": stable, "sourceTicks": [clock(r, remote) for r in neighborhood],
                       "firstNewPoseIndex": min(2, index), "playback": [playback(r) for r in neighborhood],
                       "joints": per_joint,
                       "maxStepM": max(j["maxStepM"] for j in per_joint.values()),
                       "maxVelocityChangeMPerTick": max(j["maxVelocityChangeMPerTick"] for j in per_joint.values())})
    return result, failures


def events(manifest, name):
    return [event for event in manifest.get("events", []) if event.get("kind") == name]


def normal_phase_durations(rows):
    """Prove consumed authority ticks, not merely the set of frame counters."""
    durations, failures = {}, []
    for phase, length in (("Startup", 18), ("Active", 4)):
        observed = {}
        for row in rows:
            if row.get("move") == "jumpshot" and row.get("authoritativePhase") == phase:
                observed.setdefault(row["physicsFrame"], row)
        phase_rows = list(observed.values())
        if not phase_rows:
            failures.append("missing authoritative " + phase + " duration evidence")
            continue
        first, last = phase_rows[0], phase_rows[-1]
        frame = first["authoritativeFrame"]
        counters = [row["authoritativeFrame"] for row in phase_rows]
        ticks = list(observed)
        if (frame not in ((0, 1) if phase == "Startup" else (0,)) or
                counters != list(range(frame, length)) or
                ticks != list(range(ticks[0], ticks[-1] + 1))):
            failures.append("authoritative " + phase + " tick/frame sequence changed")
        following = next((row for row in rows if row["physicsFrame"] > last["physicsFrame"]
                          and row.get("authoritativePhase") != phase), None)
        if following is None:
            failures.append("missing authoritative boundary after " + phase)
            continue
        durations[phase] = following["physicsFrame"] - first["physicsFrame"] + frame
        if durations[phase] != length:
            failures.append("consumed " + phase + " duration changed")
    return durations, failures


def analyze(directory, root_gate=True):
    directory = Path(directory)
    manifest, rows = load_json(directory / "manifest.json"), load_rows(directory)
    role = manifest["role"]
    remote = role in OBSERVER_ROLES
    primary = directory.name.removesuffix("-owner")
    server_dir = directory.parent / (primary + "-server")
    owner_dir = directory.parent / (primary + "-owner")
    authority = load_json(server_dir / "manifest.json") if remote or role == "owner" else manifest
    driver = load_json(owner_dir / "manifest.json") if manifest.get("subjectRole") == "owner" and remote else manifest
    if remote and manifest.get("subjectRole") != "owner":
        driver = authority
    authority_rows = load_rows(server_dir) if remote or role == "owner" else rows
    # Acceptance covers the shot and its immediate neutral neighbors. The
    # later explicit possession reset/resumed dribble is a separate fixture
    # episode, whose state events are checked below; its unrelated animation
    # settling must not be mistaken for this shot's entry/return continuity.
    # Preserve all-capture observation issues as diagnostics, never hide them.
    _, all_observation_failures, duplicates = observations(rows, remote)
    raw_shots = [row for row in rows if row.get("pose", {}).get("bones") and
                 row["pose"].get("move") == "jumpshot" and row["pose"].get("phase") != "Inactive"]
    proof_tick_range = None
    proof_rows = []
    if raw_shots:
        # Eight neutral poses on either side include the complete bounded
        # four-tick crossfade and its one-evaluation reporting lag.
        proof_tick_range = [min(clock(row, remote) for row in raw_shots) - 8,
                            max(clock(row, remote) for row in raw_shots) + 8]
        proof_rows = [row for row in rows if proof_tick_range[0] <= clock(row, remote) <= proof_tick_range[1]]
    duplicate_changes = []
    unique, failures, _ = observations(proof_rows, remote, duplicate_changes)
    require = lambda test, message: None if test else failures.append(message)
    for row in unique:
        for name in JOINTS:
            joint(row, name)
            joint(row, name, "world")
    if proof_tick_range is not None:
        require([clock(row, remote) for row in unique] ==
                list(range(proof_tick_range[0], proof_tick_range[1] + 1)),
                "missing adjacent source tick in eight-neutral shot window")
    require(manifest.get("result") == "captured", "capture did not complete")
    variant, entry, hand = manifest["variant"], manifest["entry"], manifest["hand"]
    feint = variant in ("early", "late")
    shoots = events(driver, "shoot-input")
    require(len(shoots) == 1, "missing/duplicate production shoot input")
    if shoots:
        e = shoots[0]
        require(e.get("entry") == entry and e.get("hand") == hand and
                e.get("holder") == manifest["subjectPeer"], "entry hand/state ownership premise")
        require(e.get("ballState") == ("Held" if entry == "held" else "Dribbling"), "wrong entry ball state")
        require(e.get("speed", -1) > .1 if entry == "moving" else 0 <= e.get("speed", -1) <= .03,
                "wrong entry movement premise")
        if entry == "held": require(e.get("hasDribbled") is False, "Held entry was not fresh")
    begins = events(authority, "accepted-begin-observed")
    require(len(begins) == 1, "missing/duplicate accepted authoritative Begin")
    if begins:
        require(begins[0].get("initiationSpeed", -1) > .1 if entry == "moving" else
                0 <= begins[0].get("initiationSpeed", -1) <= .03, "accepted Begin entry speed changed")
    releases = events(authority, "release")
    require(authority.get("releases") == (0 if feint else 1) and len(releases) == (0 if feint else 1),
            "authoritative release count changed")
    for event in releases:
        require(event.get("phase") == "Active" and event.get("frame") == 0, "release outside first Active")
        require(event.get("fadeaway") is False and abs(event.get("residualAngle", 99)) < 1e-4,
                "normal release heading/classification premise changed")
    requests = events(driver, "feint-input")
    require(len(requests) == (1 if feint else 0), "missing/unexpected feint input")
    if feint and requests:
        frame = requests[0].get("phaseFrameBeforeInput", -1)
        if manifest.get("subjectRole") == "owner":
            # Prediction and authority are independent live clocks. The local
            # matrix proves the exact legal edges; ENet records both clocks and
            # must prove an accepted request while prediction is still legal.
            require(3 <= frame < 12, "network feint input outside legal window")
            require(requests[0].get("receivedAuthorityPhase") == "Startup" and
                    3 <= requests[0].get("receivedAuthorityFrame", -1) < 12,
                    "network feint lacks observed authoritative legal window")
        else:
            require(frame == (3 if variant == "early" else 11), "wrong legal feint edge")
    resumes = events(driver, "resumed-movement-observed")
    require(len(resumes) == 1, "resumed movement was not exercised")
    if resumes and feint and entry != "held":
        require(resumes[0].get("ballState") == "Held" and resumes[0].get("hasDribbled") is True
                and resumes[0].get("speed", 99) <= .03, "spent Held fake resumed illegal dribbling")
    if resumes and (not feint or entry == "held"):
        require(resumes[0].get("speed", 0) > .1, "legal resumed movement did not occur")
    later = events(driver, "later-legal-dribble-observed")
    resets = [e for e in events(authority, "possession-reset") if e.get("ticket") == 2]
    require(len(resets) == 1 and resets[0].get("neutralInput") is True and resets[0].get("hasDribbled") is False,
            "later dribble lacks real fresh possession reset/fence")
    require(len(later) == 1 and later[0].get("ballState") == "Dribbling" and later[0].get("speed", 0) > .1,
            "later legal dribble was not observed")

    shot_indices = [i for i, r in enumerate(unique) if r["pose"].get("move") == "jumpshot"
                    and r["pose"].get("phase") != "Inactive"]
    shots = [unique[i] for i in shot_indices]
    require(bool(shots), "no coherent final shot poses")
    for phase, expected in (("Active", set() if feint else set(range(4))),
                            ("Recovery", set(range(13, 20)) if feint else set(range(20)))):
        actual = {r["pose"].get("frame") for r in shots if r["pose"].get("phase") == phase}
        require(actual == expected, "missing/extra final-pose phase frames " + phase)
    startup = {r["pose"].get("frame") for r in shots if r["pose"].get("phase") == "Startup"}
    require(bool(startup) and min(startup) <= 1, "missing first Startup final pose")
    if not feint: require(set(range(1, 18)).issubset(startup), "missing Startup source poses")
    require(all("animation" in r["pose"] and "clip" in r["pose"] and "playbackTime" in r["pose"] for r in shots),
            "actual playback not sampled at SkeletonUpdated")
    for row in shots:
        pose = row["pose"]
        phase = pose["phase"]
        require(pose.get("animation") == "Jumpshot" + phase and
                pose.get("clip") == "locomotion/jumpshot" + phase.lower(),
                "actual shot state/clip differs from displayed phase " + phase)
    if role == "owner": require(all(r.get("locallyPredicted") is True for r in shots), "owner is not predicted subject")
    if remote: require(all(r.get("locallyPredicted") is False for r in shots), "observer subject is local")
    seams, seam_failures = measure_boundaries(unique, remote)
    failures.extend(seam_failures)
    required = {"entry", "fake-recovery", "return"} if feint else {"entry", "startup-active", "active-recovery", "return"}
    require(required == {s["name"] for s in seams}, "missing/extra production phase boundaries")
    if shots:
        first, last = shot_indices[0], shot_indices[-1]
        require(first >= 8 and all(row["pose"].get("phase") == "Inactive" for row in unique[first-8:first]),
                "missing eight neutral poses before shot")
        require(len(unique[last+1:last+9]) == 8 and
                all(row["pose"].get("phase") == "Inactive" for row in unique[last+1:last+9]),
                "missing eight neutral poses after shot")
        window = unique[max(0, first-8):last+9]
        require(all(clock(b, remote)-clock(a, remote) == 1 for a, b in zip(window, window[1:])),
                "missing adjacent source tick during shot")
        require(len({r.get("presentationResyncCount", 0) for r in window}) == 1, "presentation reset during shot")
    max_hip = 0.0
    max_body_hip = 0.0
    for row in shots:
        for name in JOINTS: joint(row, name)
        pose = row["pose"]
        if pose["phase"] != "Startup" or pose["frame"] >= 6:
            body_hip = joint(row, "Hips")
            max_body_hip = max(max_body_hip, math.hypot(body_hip[0], body_hip[2]))
            # Owning prediction may translate the whole mesh by reconciliation's
            # smooth offset. That is presented motion, but is not authored hip
            # travel. Use the final production-scaled skeleton pose sampled at
            # the same SkeletonUpdated signal, before its world transform.
            h = pose.get("hipInSkeleton")
            if h is None:
                if root_gate and not feint:
                    require(False, "same-signal skeleton hip missing for authored centering proof")
                h = body_hip  # disclosed legacy baseline diagnostic only
            require(len(h) == 3 and all(math.isfinite(x) for x in h), "invalid skeleton-space hip")
            max_hip = max(max_hip, math.hypot(h[0], h[2]))
    if root_gate and not feint:
        require(max_hip <= ROOT_CENTER_FLOOR_M, "normal shot carries horizontal hip travel")

    support = []
    for row in shots:
        support.append({"sourceTick": clock(row, remote), "phase": row["pose"]["phase"],
                        "frame": row["pose"]["frame"], "hipHeightBodyM": joint(row, "Hips")[1],
                        "leftToeHeightWorldM": joint(row, "LeftToeBase", "world")[1],
                        "rightToeHeightWorldM": joint(row, "RightToeBase", "world")[1]})

    # Both independent event timing and observed pose timing are retained. Owner
    # physics ticks are a different clock, so never join them to server ticks.
    ball_windows = {"gather": [], "release": []}
    for row in shots:
        pose = row["pose"]
        gather = pose["phase"] == "Startup" and pose["frame"] in range(8, 12)
        release = (pose["phase"] == "Active" and pose["frame"] in range(0, 3)) if role == "owner" else (
            releases and abs(clock(row, remote) - releases[0]["physicsFrame"]) <= 2)
        for selected, key in ((gather, "gather"), (release, "release")):
            if selected:
                ball_windows[key].append({"sourceTick": clock(row, remote), "phase": pose["phase"], "frame": pose["frame"],
                    "playbackTime": pose.get("playbackTime"), "alignment": "predicted phase only" if role == "owner" else "authority source tick",
                    **{side + "WristVisibleBallDistanceM": distance(joint(row, side + "Hand", "world"), row["ball"])
                       for side in ("Right", "Left")}})
    if not feint:
        require(bool(ball_windows["gather"]) and bool(ball_windows["release"]), "missing gather/release visible-ball measurements")
    release_windows = []
    if role != "owner":
        # An observer's selected source tick belongs to the server clock. The
        # owning client's local physics counter does not, so its phase-only
        # ball measurements above deliberately stay separate from this join.
        by_tick = {clock(row, remote): row for row in unique}
        for event in releases:
            event_tick = event["physicsFrame"]
            window = [by_tick.get(event_tick + offset) for offset in range(-2, 3)]
            require(all(row is not None for row in window), "missing actual release source-tick window")
            if not all(row is not None for row in window):
                continue
            require(len({row.get("presentationResyncCount", 0) for row in window}) == 1,
                    "presentation reset spans actual release window")
            at_release = window[2]["pose"]
            require(at_release.get("move") == "jumpshot" and at_release.get("phase") == "Active"
                    and at_release.get("frame") == 0, "actual release source tick lacks first-Active pose")
            release_windows.append({"authorityTick": event_tick,
                                    "sourceTicks": [clock(row, remote) for row in window],
                                    "playback": [playback(row) for row in window]})
    recovery_rows = [r for r in authority_rows if r.get("authoritativePhase") == "Recovery" and r.get("move") == "jumpshot"]
    recovery_frames = sorted({r["authoritativeFrame"] for r in recovery_rows})
    first_recovery_frame = recovery_frames[0] if recovery_frames else -1
    if feint:
        # Local SampleMoveInput calls Feint then Tick, exposing13 first. The
        # remote player's reliable Feint RPC can run after its physics callback
        # and expose12 before the next Tick. Both are real authority orderings;
        # a missing later tail frame is still missing evidence and fails.
        require(first_recovery_frame in (12, 13) and
                recovery_frames == list(range(first_recovery_frame, 20)),
                "authoritative Recovery phase coverage changed")
    else:
        require(recovery_frames == list(range(20)), "authoritative Recovery phase coverage changed")
    consumed_recovery = None
    consumed_phases = {}
    if not feint:
        consumed_phases, timing_failures = normal_phase_durations(authority_rows)
        failures.extend(timing_failures)
    if recovery_rows:
        first, last = recovery_rows[0]["physicsFrame"], recovery_rows[-1]["physicsFrame"]
        neutral = next((r for r in authority_rows if r["physicsFrame"] > last and r.get("authoritativePhase") == "Inactive"), None)
        if neutral:
            consumed_recovery = neutral["physicsFrame"] - first + (first_recovery_frame - 12 if feint else 0)
            require(consumed_recovery == (8 if feint else 20), "consumed Recovery duration changed")
        else: require(False, "no authoritative neutral after Recovery")
    return {"case": directory.name, "role": role, "entry": entry, "hand": hand, "variant": variant,
            "entryOffset": manifest.get("entryOffset"), "view": manifest["view"], "subjectRole": manifest.get("subjectRole"),
            "paced": manifest.get("paced"), "pacingMs": manifest.get("pacingMs"), "engine": manifest["engine"],
            "observerPacingMs": manifest.get("observerPacingMs", 0),
            "before440": manifest.get("before440"), "images": manifest["images"], "commit": manifest["commit"],
            "resourceSha256": manifest.get("animationResourceSha256"), "rootGate": root_gate,
            "result": "failed" if failures else "passed", "failures": failures, "finalShotSamples": len(shots),
            "duplicateHoldSamples": duplicates, "holdSamples": sum(bool(r.get("presentationHolding")) for r in rows),
            "proofSourceTickRange": proof_tick_range,
            "allCaptureObservationDiagnostics": all_observation_failures,
            "changedDuplicateEdges": duplicate_changes,
            "numericSourceTickSelection": "earliest evaluated pose; raw changed duplicate edges retained separately",
            "resets": max((r.get("presentationResyncCount", 0) for r in rows), default=0),
            "maxHorizontalHipM": max_hip, "maxHorizontalHipBodyM": max_body_hip,
            "rootMeasurementSpace": "production-scaled Skeleton3D pose; excludes whole-mesh correction" if shots and
                all("hipInSkeleton" in r["pose"] for r in shots) else "legacy body-relative diagnostic; no centering acceptance",
            "rootNumericalFloorM": ROOT_CENTER_FLOOR_M,
            "authorityRecoveryFramesObserved": recovery_frames, "consumedRecoveryTicks": consumed_recovery,
            "consumedNormalPhaseTicks": consumed_phases,
            "authoritativeEvents": authority.get("events", []), "driverEvents": driver.get("events", []),
            "ballWristSeparation": ball_windows, "actualReleaseSourceWindows": release_windows,
            "supportTrajectory": support, "boundaries": seams}


def compare(case, baseline, require_improved=("entry", "fake-recovery", "return")):
    failures = case["failures"]
    keys = ("role", "entry", "hand", "variant", "entryOffset", "view", "subjectRole", "paced", "pacingMs", "engine", "images")
    if any(case.get(k) != baseline.get(k) for k in keys):
        failures.append("baseline has incomparable input/view/topology/clock metadata")
    elif case.get("observerPacingMs", 0) != baseline.get("observerPacingMs", 0):
        failures.append("baseline has incomparable receiver pacing")
    else:
        old_seams = {s["name"]: s for s in baseline["boundaries"]}
        for seam in case["boundaries"]:
            old = old_seams.get(seam["name"])
            if old is None:
                failures.append("baseline boundary missing " + seam["name"])
                continue
            seam["baselineMaxStepM"] = old["maxStepM"]
            seam["baselineMaxVelocityChangeMPerTick"] = old["maxVelocityChangeMPerTick"]
            seam["baselinePlayback"] = old["playback"]
            if seam["name"] in require_improved:
                # This is an explicit before/after repair claim, not merely a
                # no-regression bound. Retain both velocity and position changes.
                for metric in ("maxStepM", "maxVelocityChangeMPerTick"):
                    if not seam[metric] < old[metric]:
                        failures.append("reproduced discontinuity not improved: " + seam["name"] + " " + metric)
    case["result"] = "failed" if failures else "passed"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--baseline-measurement", action="store_true", help="measure immutable pre-fix assets without root-centering acceptance")
    parser.add_argument("--baseline", type=Path, help="matched immutable baseline JSON report")
    parser.add_argument("--require-improved", nargs="*", choices=("entry", "fake-recovery", "return"), default=["entry", "fake-recovery", "return"])
    args = parser.parse_args()
    directories = [args.directory] if (args.directory / "manifest.json").exists() else sorted(
        p.parent for p in args.directory.glob("*/manifest.json") if not p.parent.name.endswith("-server"))
    results = []
    for directory in directories:
        try:
            results.append(analyze(directory, not args.baseline_measurement))
        except (KeyError, ValueError, OSError, StopIteration) as error:
            results.append({"case": directory.name, "result": "failed", "failures": ["invalid/missing capture evidence: " + str(error)]})
    if args.baseline:
        baseline = {case["case"]: case for case in load_json(args.baseline)["cases"]}
        for case in results:
            if case["case"] not in baseline: case["failures"].append("baseline case missing"); case["result"] = "failed"
            elif "boundaries" in case: compare(case, baseline[case["case"]], args.require_improved)
    passed = bool(results) and all(case["result"] == "passed" for case in results)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps({"schema": "hooper-jumpshot-transitions/v1", "result": "passed" if passed else "failed",
        "baselineMeasurement": args.baseline_measurement, "cases": results}, indent=2), encoding="utf-8")
    print(f"[jumpshot-transitions] {'PASS' if passed else 'FAIL'} cases={len(results)} report={args.output}")
    raise SystemExit(0 if passed else 1)


if __name__ == "__main__":
    main()
