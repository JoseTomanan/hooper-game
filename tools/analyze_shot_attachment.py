"""Strict #442 anatomical contact and release proof over production final poses.

The calibrated skin vertex is independent of the ball. Source-tick coverage and
production fake timing additionally reuse #440's existing transition analyzer.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path

import analyze_jumpshot_transitions as transitions
from analyze_jumpshot_capture import distance
from generate_shot_gather_profile import transform

ROOT = Path(__file__).resolve().parents[1]
CALIBRATION = ROOT / "assets/shot_gather_calibration.json"
RADIUS_M = .12
CONTACT_TOLERANCE_M = .02
NUMERICAL_TOLERANCE_M = 1e-4
# A held world target is recomposed through a moving float32 parent transform.
# Permit one micrometre of numerical noise, far below contact/model tolerances;
# a real changed duplicate still fails instead of being selected away.
DUPLICATE_TOLERANCE_M = 1e-6
DT = 1 / 60
GRAVITY = (0, -9.8, 0)


def vector(value, label, length=3):
    if (not isinstance(value, (list, tuple)) or len(value) != length or
            any(not isinstance(x, (int, float)) or not math.isfinite(x) for x in value)):
        raise ValueError("missing/nonfinite " + label)
    return value


def required(row, name):
    return vector(row.get(name), name)


def step_telemetry(row):
    step, correction = row.get("flightStep"), row.get("ballCorrection")
    if not isinstance(step, dict) or type(step.get("advanced")) is not bool:
        raise ValueError("missing/invalid flightStep telemetry")
    if not isinstance(correction, dict) or type(correction.get("received")) is not bool:
        raise ValueError("missing/invalid ballCorrection telemetry")
    for name in ("position", "velocity"):
        vector(step.get(name), "flightStep." + name)
    for name in ("before", "authoritative", "velocity"):
        vector(correction.get(name), "ballCorrection." + name)
    for name in ("beforeState", "state"):
        if correction["received"] and not isinstance(correction.get(name), str):
            raise ValueError("missing/invalid ballCorrection." + name)
    return step, correction


def load_calibration(path=CALIBRATION):
    calibration = transitions.load_json(Path(path))
    if calibration.get("radius") != RADIUS_M:
        raise ValueError("calibration radius differs from fixed .12 m proof radius")
    for side, vertex_id in (("Right", 11216), ("Left", 10324)):
        palm = calibration["palms"][side]
        influence = palm["influences"]
        if (palm["vertex"] != vertex_id or palm["mesh"] != "Skeleton3D/Alpha_Surface" or
                len(influence) != 1 or influence[0]["bone_name"] != "mixamorig_" + side + "Hand" or
                influence[0]["weight"] != 1):
            raise ValueError("calibration is not the independently selected pure wrist skin vertex")
        local = palm["wrist_local"][side]
        vector(local["position"], side + " anatomical palm")
        normal = vector(local["normal"], side + " palm normal")
        if abs(sum(x*x for x in normal) - 1) > 1e-4:
            raise ValueError("calibrated palm normal is not unit length")
    asset_hash = hashlib.sha256((ROOT / "assets/Y Bot.fbx").read_bytes()).hexdigest()
    if calibration["rigSha256"] != asset_hash:
        raise ValueError("calibration rig SHA256 differs from actual skin asset")
    return calibration


def source_tick(row, remote):
    # Owning prediction and server physics counters are independent clocks.
    # Observer tick comes from the evaluated pose, after coherence validation.
    return row["pose"]["presentationTick"] if remote else row["physicsFrame"]


def _window(rows, remote):
    shots = [r for r in rows if r.get("pose", {}).get("move") == "jumpshot" and
             r["pose"].get("phase") != "Inactive"]
    if not shots:
        return [], ["no evaluated final shot poses"], 0
    lo = min(source_tick(r, remote) for r in shots) - 8
    hi = max(source_tick(r, remote) for r in shots) + 8
    # The row clock locates even a missing/stale pose inside the window. Every
    # admitted observer pose must then agree with that clock in observations().
    raw = [r for r in rows if lo <= transitions.clock(r, remote) <= hi]
    unique, failures, duplicates = transitions.observations(raw, remote)
    if any(r.get("poseMatchesPhysics") is not True for r in raw):
        failures.append("poseMatchesPhysics is absent/false in proof window")
    if [source_tick(r, remote) for r in unique] != list(range(lo, hi + 1)):
        failures.append("missing adjacent source tick in eight-neutral attachment window")
    if len({r.get("presentationResyncCount", 0) for r in raw}) != 1:
        failures.append("presentation reset spans attachment window")
    previous = None
    for row in raw:
        if previous is not None and transitions.clock(previous, remote) == transitions.clock(row, remote):
            # Ball appearance is also part of the selected source pose. Do not
            # compare observer liveBall, which legitimately follows latest state.
            if distance(required(previous, "ball"), required(row, "ball")) > DUPLICATE_TOLERANCE_M:
                failures.append("changed rendered ball on duplicate source tick")
        previous = row
    return unique, failures, duplicates


def analyze_rows(manifest, rows, calibration):
    """Attachment operator; full-file analyze also gates existing transition proof."""
    role = manifest["role"]
    remote = role in transitions.OBSERVER_ROLES
    unique, failures, duplicates = _window(rows, remote)
    require = lambda condition, message: None if condition else failures.append(message)
    feint = manifest["variant"] in ("early", "late")
    shots = [r for r in unique if r["pose"].get("move") == "jumpshot" and r["pose"].get("phase") != "Inactive"]
    # HandSide is retained authoritative possession state, not the anatomical
    # shooting wrist. Left entries still gather to the right shooting palm.
    require(all(r["pose"].get("hand") == manifest["hand"] for r in shots),
            "shot changed retained authoritative HandSide")
    by_phase = {}
    for row in shots:
        key = row["pose"]["phase"], row["pose"]["frame"]
        require(key not in by_phase, "repeated evaluated phase frame " + str(key))
        by_phase[key] = row
    required_keys = [("Startup", 1)]
    if not feint:
        required_keys += [("Startup", 8), ("Startup", 17), ("Active", 0), ("Active", 1), ("Active", 2)]
    for key in required_keys:
        require(key in by_phase, "missing evaluated boundary " + str(key))
    contact = []
    measured = [r for r in shots if (r["pose"]["phase"] == "Startup" and 8 <= r["pose"]["frame"] <= 17)
                or (r["pose"]["phase"], r["pose"]["frame"]) == ("Active", 0)]
    if not feint:
        require({r["pose"]["frame"] for r in measured if r["pose"]["phase"] == "Startup"} == set(range(8, 18)),
                "missing contact frame in Startup8..17")
    for row in measured:
        pose = row["pose"]
        try:
            wrist = vector(pose["bones"]["mixamorig_RightHand"]["world"], "final right wrist transform", 12)
            palm = transform(wrist, calibration["palms"]["Right"]["wrist_local"]["Right"]["position"])
            ball = required(row, "ball")
            gap = distance(palm, ball) - RADIUS_M
            sample = {"sourceTick": source_tick(row, remote), "physicsFrame": row["physicsFrame"],
                      "phase": pose["phase"], "frame": pose["frame"], "palmWorld": palm,
                      "ballWorld": ball, "palmSurfaceGapM": gap, "contactErrorM": abs(gap),
                      "wristCenterDistanceM": distance(wrist[:3], ball)}
            contact.append(sample)
            # A release-pose sample still displays the retained release origin;
            # Active1/2 are flight and intentionally do not receive contact gates.
            require(abs(gap) <= CONTACT_TOLERANCE_M, "anatomical right palm detached at " + str((pose["phase"], pose["frame"])))
            if not remote:
                live = required(row, "liveBall")
                offset = required(row, "shotVisualOffset")
                sample.update({"physicalBallWorld": live, "visualOffsetWorld": offset,
                               "physicalVisibleDistanceM": distance(live, ball),
                               "visualCorrectionResidualM": distance([a+b for a, b in zip(live, offset)], ball),
                               "attachmentModelWorld": required(row, "shotAttachmentModel")})
                if pose["phase"] == "Startup":
                    sample["physicalModelErrorM"] = distance(live, sample["attachmentModelWorld"])
                    require(sample["physicalModelErrorM"] <= NUMERICAL_TOLERANCE_M,
                            "Held physical ball differs from attachment model endpoint")
                require(sample["visualCorrectionResidualM"] <= NUMERICAL_TOLERANCE_M,
                        "physical-visible ball difference is not explained by holder visual correction")
            else:
                sample["physicalComparison"] = "not joined: observer live state is not selected source tick"
        except (KeyError, ValueError, TypeError) as error:
            failures.append("invalid/missing contact evidence: " + str(error))
    flight, release, phase_samples, stalls, corrections = [], {}, [], [], []
    if not feint and not remote and all(key in by_phase for key in (("Startup", 17), ("Active", 0), ("Active", 1), ("Active", 2))):
        held, at = [by_phase[key] for key in (("Startup", 17), ("Active", 0))]
        for frame in (1, 2):
            row = by_phase[("Active", frame)]
            phase_samples.append({"sourceTick": source_tick(row, False), "phase": "Active", "frame": frame,
                                  "ballState": row.get("ballState"), "liveBall": row.get("liveBall"),
                                  "flightStep": row.get("flightStep"), "ballCorrection": row.get("ballCorrection")})
        try:
            origin = required(at, "releasePosition")  # independently latched production release origin
            endpoint = required(held, "shotAttachmentModel")
            release_model = required(at, "shotAttachmentModel")
            held_root, release_root = required(held, "gatherRoot"), required(at, "gatherRoot")
            endpoint_delta = [a - b for a, b in zip(origin, endpoint)]
            root_delta = [a - b for a, b in zip(release_root, held_root)]
            release = {"clock": "owner-local physics" if role == "owner" else "local authority physics",
                       "releaseSourceTick": source_tick(at, False), "releasePosition": origin,
                       "lastHeldModelEndpoint": endpoint, "lastHeldEndpointErrorM": distance(origin, endpoint),
                       "releaseModelEndpoint": release_model, "releaseModelErrorM": distance(origin, release_model),
                       "lastHeldGatherRoot": held_root, "releaseGatherRoot": release_root,
                       "lastHeldToReleaseDelta": endpoint_delta, "ownGatherRootDelta": root_delta,
                       "endpointRootDeltaErrorM": distance(endpoint_delta, root_delta),
                       "releaseLiveBallErrorM": distance(origin, required(at, "liveBall"))}
            require(held.get("ballState") == "Held", "last Startup endpoint was not Held")
            require(at.get("ballState") == "InFlight", "release-pose ball was not InFlight")
            if manifest["entry"] != "moving":
                require(release["lastHeldEndpointErrorM"] <= NUMERICAL_TOLERANCE_M,
                        "release origin differs from last Held model endpoint")
            require(release["releaseModelErrorM"] <= NUMERICAL_TOLERANCE_M,
                    "release origin differs from independent first-Active model endpoint")
            require(release["endpointRootDeltaErrorM"] <= NUMERICAL_TOLERANCE_M,
                    "last Held to release model displacement differs from own gatherRoot delta")
            require(release["releaseLiveBallErrorM"] <= NUMERICAL_TOLERANCE_M,
                    "release-pose physical ball differs from latched origin")
            require(at.get("releaseCount") == 1 and held.get("releaseCount") == 0,
                    "local release latch does not rise exactly at own first Active")
            release_step, _ = step_telemetry(at)
            require(not release_step["advanced"], "release-pose unexpectedly advanced the arc")
            previous = at
            # The first TWO real Step() calls prove flight. Keep inspecting
            # later Held stalls through the bounded window: an authoritative
            # Held packet may arrive only after two locally predicted steps.
            for current in unique:
                if source_tick(current, False) <= source_tick(at, False):
                    continue
                step, correction = step_telemetry(current)
                require(source_tick(current, False) - source_tick(previous, False) == 1,
                        "postrelease evidence is not adjacent own physics ticks")
                require(required(current, "releasePosition") == origin, "release latch changed during first flight")
                gravity = current.get("arcGravity")
                require(isinstance(gravity, (int, float)) and math.isfinite(gravity) and
                        abs(gravity - 9.8) <= NUMERICAL_TOLERANCE_M,
                        "missing/changed arcGravity for flight proof")
                if correction["received"]:
                    event = {"sourceTick": source_tick(current, False), **correction,
                             "magnitudeM": distance(correction["before"], correction["authoritative"]),
                             "beforePreviousLiveErrorM": distance(correction["before"], required(previous, "liveBall"))}
                    corrections.append(event)
                    require(role == "owner", "local authority unexpectedly received a ball correction")
                    require(event["beforePreviousLiveErrorM"] <= NUMERICAL_TOLERANCE_M,
                            "ball correction before position differs from previous own sample")
                if len(flight) >= 2 and (step["advanced"] or current.get("ballState") != "Held"):
                    previous = current
                    continue
                if not step["advanced"]:
                    require(role == "owner" and current.get("ballState") == "Held",
                            "postrelease nonflight sample is not an owning Held correction stall")
                    require(previous.get("ballState") == "Held" or
                            (correction["received"] and correction["state"] == "Held"),
                            "Held stall lacks an observed authoritative correction")
                    model = required(current, "shotAttachmentModel")
                    stall = {"sourceTick": source_tick(current, False), "phase": current["pose"]["phase"],
                             "frame": current["pose"]["frame"], "modelWorld": model,
                             "liveBall": required(current, "liveBall"),
                             "retainedOriginErrorM": distance(model, origin),
                             "physicalModelErrorM": distance(required(current, "liveBall"), model),
                             "correctionReceived": correction["received"]}
                    stalls.append(stall)
                    require(stall["retainedOriginErrorM"] <= NUMERICAL_TOLERANCE_M and
                            stall["physicalModelErrorM"] <= NUMERICAL_TOLERANCE_M,
                            "postrelease Held stall snapped away from retained absolute release origin")
                    if correction["received"]:
                        require(correction["state"] == "Held", "Held stall correction metadata has wrong state")
                else:
                    before, velocity = step["position"], step["velocity"]
                    expected_velocity = [v + g * DT for v, g in zip(velocity, GRAVITY)]
                    expected_position = [p + v * DT + .5 * g * DT ** 2 for p, v, g in zip(before, velocity, GRAVITY)]
                    frame = {"sourceTick": source_tick(current, False), "phase": current["pose"]["phase"],
                             "frame": current["pose"]["frame"], "inputPosition": before, "inputVelocity": velocity,
                             "expectedPosition": expected_position, "actualPosition": required(current, "liveBall"),
                             "positionErrorM": distance(expected_position, required(current, "liveBall")),
                             "velocityErrorMPerS": distance(expected_velocity, required(current, "flightVelocity")),
                             "correctionReceived": correction["received"]}
                    require(current.get("ballState") == "InFlight", "actual first flight step not InFlight")
                    if correction["received"]:
                        require(correction["state"] == "InFlight", "flight input correction metadata has wrong state")
                        frame["authoritativeInputPositionErrorM"] = distance(before, correction["authoritative"])
                        frame["authoritativeInputVelocityErrorMPerS"] = distance(velocity, correction["velocity"])
                        require(frame["authoritativeInputPositionErrorM"] <= NUMERICAL_TOLERANCE_M and
                                frame["authoritativeInputVelocityErrorMPerS"] <= NUMERICAL_TOLERANCE_M,
                                "flightStep input differs from fresh authoritative correction")
                    else:
                        frame["previousOwnPositionErrorM"] = distance(before, required(previous, "liveBall"))
                        frame["previousOwnVelocityErrorMPerS"] = distance(velocity, required(previous, "flightVelocity"))
                        require(previous.get("ballState") == "InFlight",
                                "flight resumed from Held without fresh authoritative correction")
                        require(frame["previousOwnPositionErrorM"] <= NUMERICAL_TOLERANCE_M and
                                frame["previousOwnVelocityErrorMPerS"] <= NUMERICAL_TOLERANCE_M,
                                "uncorrected flightStep input differs from previous own sample")
                        if not flight:
                            frame["uncorrectedOriginErrorM"] = distance(before, origin)
                            require(frame["uncorrectedOriginErrorM"] <= NUMERICAL_TOLERANCE_M,
                                    "first uncorrected flightStep input differs from latched release origin")
                    require(frame["positionErrorM"] <= NUMERICAL_TOLERANCE_M and
                            frame["velocityErrorMPerS"] <= NUMERICAL_TOLERANCE_M,
                            "actual flight step violates gravity trapezoidal integrator")
                    if flight:
                        frame["interveningHeldStallTicks"] = [s["sourceTick"] for s in stalls
                            if flight[-1]["sourceTick"] < s["sourceTick"] < frame["sourceTick"]]
                        require(frame["sourceTick"] - flight[-1]["sourceTick"] == 1 or
                                len(frame["interveningHeldStallTicks"]) == frame["sourceTick"] - flight[-1]["sourceTick"] - 1,
                                "first two actual flight steps lack consecutive ticks or explicit Held stalls")
                    flight.append(frame)
                previous = current
            require(len(flight) == 2, "missing first two actual flightStep advances inside bounded proof window")
        except (KeyError, ValueError, TypeError) as error:
            failures.append("invalid/missing release/flight evidence: " + str(error))
    if feint:
        require(not any(r["pose"]["phase"] == "Active" for r in shots), "fake entered Active")
        # Observer release/possession semantics are proven by source authority in
        # transitions.analyze; its row counters may belong to latest state.
        if not remote:
            require(all(r.get("releaseCount") == 0 for r in shots), "fake released or lacks zero release evidence")
            recovery = [r for r in shots if r["pose"]["phase"] == "Recovery"]
            require(bool(recovery), "fake lacks recovery poses")
            if recovery:
                startup = [r for r in shots if r["pose"]["phase"] == "Startup"]
                expected_hand = startup[-1]["pose"].get("hand") if startup else None
                expected_dead = manifest["entry"] != "held"
                tail = [r for r in unique if source_tick(shots[-1], remote) < source_tick(r, remote)
                        <= source_tick(shots[-1], remote) + 8]
                require(all(r["pose"].get("hand") == expected_hand and r.get("hand") == expected_hand
                            and r.get("hasDribbled") is expected_dead for r in recovery + tail),
                        "fake lost retained HandSide/dead-dribble state")
    return {"case": manifest.get("case", ""), "role": role, "entry": manifest["entry"],
            "hand": manifest["hand"], "variant": manifest["variant"], "result": "failed" if failures else "passed",
            "failures": list(dict.fromkeys(failures)), "contactToleranceM": CONTACT_TOLERANCE_M,
            "radiusM": RADIUS_M, "numericalToleranceM": NUMERICAL_TOLERANCE_M,
            "duplicateHoldSamples": duplicates, "contactFrames": contact, "release": release, "firstFlightFrames": flight,
            "firstTwoPhaseFrames": phase_samples, "heldCorrectionStalls": stalls, "ballCorrections": corrections,
            "flightEvidenceBound": "contiguous evaluated shot plus eight neutral source ticks",
            "observerReleaseMechanics": "not joined to latest physical state" if remote else None}


def feint_resolution_failures(variant, authority_events, driver_events):
    """Check each event clock separately; receipt is not inferred from input."""
    failures = []
    expected = 1 if variant in ("early", "late") else 0
    evidence = {}
    for role, events in (("authority", authority_events), ("driver", driver_events)):
        resolutions = [e for e in events if e.get("kind") == "feint-resolution"]
        evidence[role] = resolutions
        if len(resolutions) != expected:
            failures.append(role + " missing/unexpected actual feint resolution")
        for event in resolutions:
            if not (event.get("accepted") is True and event.get("phase") == "Startup"
                    and isinstance(event.get("frame"), int) and 3 <= event["frame"] < 12
                    and isinstance(event.get("physicsFrame"), int) and event["physicsFrame"] >= 0):
                failures.append(role + " actual feint was rejected or outside legal Startup window")
    return failures, evidence


def limb_dimensions(rows):
    """Compare actual evaluated segment lengths with the incoming neutral rig."""
    measurements, failures = {}, []
    if not rows:
        return measurements, ["missing rig dimension samples"]
    for side in ("Right", "Left"):
        for start, end in (("Arm", "ForeArm"), ("ForeArm", "Hand")):
            lengths = [distance(r["pose"]["bones"]["mixamorig_" + side + start]["world"][:3],
                                r["pose"]["bones"]["mixamorig_" + side + end]["world"][:3]) for r in rows]
            incoming = lengths[0]
            error = max(abs(length - incoming) for length in lengths)
            measurements[side + start] = {"incomingLengthM": incoming, "minLengthM": min(lengths),
                                         "maxLengthM": max(lengths), "maxChangeM": error}
            if incoming <= 0 or error > NUMERICAL_TOLERANCE_M:
                failures.append("evaluated rig segment stretched: " + side + start)
    return measurements, failures


def analyze(directory, calibration=None):
    directory = Path(directory)
    manifest, rows = transitions.load_json(directory / "manifest.json"), transitions.load_rows(directory)
    report = analyze_rows(manifest, rows, calibration or load_calibration())
    window, _, _ = _window(rows, manifest["role"] in transitions.OBSERVER_ROLES)
    report["limbDimensions"], dimension_failures = limb_dimensions(window)
    report["failures"] += dimension_failures
    prior = transitions.analyze(directory)
    feint_failures, report["feintResolutions"] = feint_resolution_failures(report["variant"],
        prior["authoritativeEvents"], prior["driverEvents"])
    report["failures"] += feint_failures
    report["case"] = directory.name
    report["transitionFailures"] = prior["failures"]
    report["consumedRecoveryTicks"] = prior["consumedRecoveryTicks"]
    report["boundaries"] = [{"name": b["name"], "sourceTicks": b["sourceTicks"], "stable": b["stable"]} for b in prior["boundaries"]]
    report["failures"] += ["transition proof: " + f for f in prior["failures"]]
    report["result"] = "failed" if report["failures"] else "passed"
    return report


def safe_analyze(directory, calibration=None):
    try:
        return analyze(directory, calibration)
    except (KeyError, ValueError, OSError, StopIteration, TypeError, IndexError) as error:
        return {"case": Path(directory).name, "result": "failed", "failures": ["invalid/missing capture evidence: " + str(error)]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--calibration", type=Path, default=CALIBRATION)
    parser.add_argument("--allow-failure", action="store_true", help="diagnostic exit zero; report still records failure")
    args = parser.parse_args()
    calibration = load_calibration(args.calibration)
    directories = [args.directory] if (args.directory / "manifest.json").exists() else sorted(
        p.parent for p in args.directory.glob("*/manifest.json") if not p.parent.name.endswith("-server"))
    cases = [safe_analyze(p, calibration) for p in directories]
    passed = bool(cases) and all(c["result"] == "passed" for c in cases)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps({"schema": "hooper-shot-attachment/v1", "result": "passed" if passed else "failed",
        "diagnosticAllowFailure": args.allow_failure, "cases": cases}, indent=2) + "\n", encoding="utf-8")
    print(f"[shot-attachment] {'PASS' if passed else 'FAIL'} cases={len(cases)} report={args.output}")
    raise SystemExit(0 if passed or args.allow_failure else 1)


if __name__ == "__main__":
    main()
