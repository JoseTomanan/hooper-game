"""Discriminating mutations of real-shaped #442 final-pose attachment evidence."""
import copy
import gzip
import json
from pathlib import Path
import sys
import unittest
from types import SimpleNamespace
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import analyze_shot_attachment as proof
from generate_shot_gather_profile import transform
from run_shot_attachment_proof import capture, expected_cases


class ShotAttachmentAnalysisTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.calibration = proof.load_calibration()
        cls.original = cls.real_trace("normal")

    @staticmethod
    def real_trace(variant):
        path = ROOT / f"docs/analysis/evidence/0418/local-right-{variant}-after-trace.jsonl.gz"
        rows = [json.loads(line) for line in gzip.decompress(path.read_bytes()).decode().splitlines()]
        return [row for row in rows if row.get("pose", {}).get("bones")]

    def fixture(self):
        # Operator fixture, NOT a claim the historical production ball was
        # attached: retain actual captured bone transforms, and independently
        # synthesize a ball one radius along the calibrated skin normal. The
        # production proof exclusively consumes unmodified live captures.
        rows = copy.deepcopy(self.original)
        palm = self.calibration["palms"]["Right"]["wrist_local"]["Right"]
        offset = [p + proof.RADIUS_M * n for p, n in zip(palm["position"], palm["normal"])]
        held = next(r for r in rows if r["pose"]["phase"] == "Startup" and r["pose"]["frame"] == 17)
        wrist = held["pose"]["bones"]["mixamorig_RightHand"]
        origin = transform(wrist["world"], offset)
        release_tick = next(r["physicsFrame"] for r in rows if r["pose"]["phase"] == "Active" and r["pose"]["frame"] == 0)
        for row in rows:
            pose = row["pose"]
            row["shotVisualOffset"] = [0, 0, 0]
            row["shotAttachmentModel"] = origin[:]
            row["gatherRoot"] = [0, 0, 0]
            row["flightVelocity"] = [0, 5, -3]
            row["arcGravity"] = 9.8
            row["flightStep"] = {"advanced": False, "position": [0, 0, 0], "velocity": [0, 0, 0]}
            row["ballCorrection"] = {"received": False, "before": [0, 0, 0], "authoritative": [0, 0, 0],
                                     "velocity": [0, 0, 0], "beforeState": None, "state": None}
            if row["physicsFrame"] >= release_tick:
                row["releasePosition"] = origin[:]
            if pose["move"] == "jumpshot" and pose["phase"] == "Startup":
                center = transform(pose["bones"]["mixamorig_RightHand"]["world"], offset)
                row["ball"] = center[:]
                row["liveBall"] = center[:]
                row["shotAttachmentModel"] = center[:]
                row["releaseCount"] = 0
            if pose["move"] == "jumpshot" and pose["phase"] == "Active":
                if pose["frame"] == 0:
                    pose["bones"]["mixamorig_RightHand"] = copy.deepcopy(wrist)
                elapsed = pose["frame"] * proof.DT
                row["ball"] = [p + v * elapsed + .5 * g * elapsed ** 2
                               for p, v, g in zip(origin, (0, 5, -3), proof.GRAVITY)]
                row["liveBall"] = row["ball"][:]
                row["flightVelocity"] = [v + g * elapsed for v, g in zip((0, 5, -3), proof.GRAVITY)]
                row["releasePosition"] = origin[:]
                row["releaseCount"] = 1
                if pose["frame"] > 0:
                    previous_time = (pose["frame"] - 1) * proof.DT
                    row["flightStep"] = {"advanced": True,
                        "position": [p + v * previous_time + .5 * g * previous_time ** 2
                                     for p, v, g in zip(origin, (0, 5, -3), proof.GRAVITY)],
                        "velocity": [v + g * previous_time for v, g in zip((0, 5, -3), proof.GRAVITY)]}
        return rows

    def analyze(self, rows, role="local", variant="normal", entry="held", hand="Right"):
        return proof.analyze_rows(dict(role=role, variant=variant, entry=entry, hand=hand), rows, self.calibration)

    def owner_stall_fixture(self):
        rows = self.fixture()
        active = {r["pose"]["frame"]: r for r in rows if r["pose"]["phase"] == "Active"}
        origin = active[0]["releasePosition"][:]
        for frame in (1, 2):
            row = active[frame]
            row["ballState"] = "Held"
            row["liveBall"] = origin[:]
            row["ball"] = origin[:]
            row["shotAttachmentModel"] = origin[:]
            row["flightStep"]["advanced"] = False
        active[1]["ballCorrection"] = {"received": True, "before": origin[:],
            "authoritative": [origin[0], origin[1] - .8, origin[2]], "velocity": [0, 0, 0],
            "beforeState": "InFlight", "state": "Held"}
        resumed = [origin[0], origin[1] + .2, origin[2] - .1]
        velocity = [0, 4, -3]
        active[3]["ballCorrection"] = {"received": True, "before": origin[:],
            "authoritative": resumed[:], "velocity": velocity[:], "beforeState": "Held", "state": "InFlight"}
        recovery = next(r for r in rows if r["pose"]["phase"] == "Recovery" and r["pose"]["frame"] == 0)
        for row in (active[3], recovery):
            row["ballState"] = "InFlight"
            row["flightStep"] = {"advanced": True, "position": resumed[:], "velocity": velocity[:]}
            row["liveBall"] = [p + v * proof.DT + .5 * g * proof.DT ** 2
                               for p, v, g in zip(resumed, velocity, proof.GRAVITY)]
            row["ball"] = row["liveBall"][:]
            row["flightVelocity"] = [v + g * proof.DT for v, g in zip(velocity, proof.GRAVITY)]
            resumed, velocity = row["liveBall"][:], row["flightVelocity"][:]
        return rows

    def assert_failure(self, report, text):
        self.assertEqual("failed", report["result"])
        self.assertTrue(any(text in f for f in report["failures"]), report["failures"])

    def test_independent_skin_fixture_and_gravity_operator_pass(self):
        report = self.analyze(self.fixture())
        self.assertEqual([], report["failures"])
        self.assertEqual(11, len(report["contactFrames"]))
        self.assertEqual(2, len(report["firstFlightFrames"]))
        self.assertLess(max(p["contactErrorM"] for p in report["contactFrames"]), 1e-7)

    def test_left_entry_retains_left_handside_with_right_shooting_palm_contact(self):
        rows = self.fixture()
        for row in rows:
            row["hand"] = "Left"
            row["pose"]["hand"] = "Left"
        report = self.analyze(rows, hand="Left")
        self.assertEqual([], report["failures"])
        self.assertEqual(11, len(report["contactFrames"]))
        self.assertLess(max(p["contactErrorM"] for p in report["contactFrames"]), 1e-7)
        at = next(r for r in rows if r["pose"]["phase"] == "Startup" and r["pose"]["frame"] == 8)
        at["hand"] = "Right"
        at["pose"]["hand"] = "Right"
        self.assert_failure(self.analyze(rows, hand="Left"), "retained authoritative HandSide")

    def test_absent_correction_null_states_are_valid_but_received_null_state_is_not(self):
        rows = self.fixture()
        self.assertEqual([], self.analyze(rows)["failures"])
        at = next(r for r in rows if r["pose"]["phase"] == "Active" and r["pose"]["frame"] == 1)
        for field in ("beforeState", "state"):
            with self.subTest(field=field):
                mutated = copy.deepcopy(at)
                mutated["ballCorrection"].update(received=True, beforeState="InFlight", state="InFlight")
                mutated["ballCorrection"][field] = None
                with self.assertRaisesRegex(ValueError, "ballCorrection." + field):
                    proof.step_telemetry(mutated)

    def test_release_requires_independent_first_active_model_even_when_palm_contacts(self):
        rows = self.fixture()
        at = next(r for r in rows if r["pose"]["phase"] == "Active" and r["pose"]["frame"] == 0)
        at["shotAttachmentModel"][1] -= .8
        report = self.analyze(rows)
        self.assert_failure(report, "independent first-Active model endpoint")
        self.assertLess(max(p["contactErrorM"] for p in report["contactFrames"]), 1e-7)

    def test_moving_release_displacement_requires_own_gather_root_delta(self):
        rows = self.fixture()
        at = next(r for r in rows if r["pose"]["phase"] == "Active" and r["pose"]["frame"] == 0)
        delta = [.1, 0, 0]
        for row in rows:
            if row["physicsFrame"] < at["physicsFrame"]:
                continue
            row["gatherRoot"] = delta[:]
            for field in ("ball", "liveBall", "shotAttachmentModel", "releasePosition"):
                row[field] = [a+b for a, b in zip(row[field], delta)]
            if row["flightStep"]["advanced"]:
                row["flightStep"]["position"] = [a+b for a, b in zip(row["flightStep"]["position"], delta)]
        for i, value in enumerate(delta):
            at["pose"]["bones"]["mixamorig_RightHand"]["world"][i] += value
        report = self.analyze(rows, entry="moving")
        self.assertEqual([], report["failures"])
        self.assertAlmostEqual(.1, report["release"]["lastHeldEndpointErrorM"])
        self.assertLess(report["release"]["endpointRootDeltaErrorM"], 1e-7)
        at["gatherRoot"][0] += .03
        self.assert_failure(self.analyze(rows, entry="moving"), "own gatherRoot delta")

    def test_old_fixed_ball_is_detected_without_optional_new_fields(self):
        report = self.analyze(copy.deepcopy(self.original))
        self.assert_failure(report, "anatomical right palm detached")
        self.assertEqual(11, len(report["contactFrames"]))
        self.assertGreater(max(p["contactErrorM"] for p in report["contactFrames"]), .5)

    def test_every_gather_and_release_boundary_is_required(self):
        for phase, frame in (("Startup", 1), ("Startup", 8), ("Startup", 12), ("Startup", 17),
                             ("Active", 0), ("Active", 1), ("Active", 2)):
            with self.subTest(phase=phase, frame=frame):
                rows = [r for r in self.fixture() if not (r["pose"]["phase"] == phase and r["pose"]["frame"] == frame)]
                self.assert_failure(self.analyze(rows), "missing adjacent source tick")

    def test_detached_wrist_and_shifted_whole_mesh_are_rejected(self):
        for bones in (("mixamorig_RightHand",), tuple(self.original[0]["pose"]["bones"])):
            with self.subTest(bones=len(bones)):
                rows = self.fixture()
                for row in rows:
                    if row["pose"]["phase"] == "Startup" and row["pose"]["frame"] == 8:
                        for name in bones:
                            row["pose"]["bones"][name]["world"][1] += .3
                self.assert_failure(self.analyze(rows), "anatomical right palm detached")

    def test_release_origin_and_first_flight_mutations_are_detected(self):
        for field in ("releasePosition", "liveBall", "flightVelocity"):
            with self.subTest(field=field):
                rows = self.fixture()
                row = next(r for r in rows if r["pose"]["phase"] == "Active" and r["pose"]["frame"] == (0 if field == "releasePosition" else 1))
                row[field][0] += .1
                report = self.analyze(rows)
                self.assert_failure(report, "release origin" if field == "releasePosition" else "trapezoidal integrator")

    def test_missing_velocity_or_visual_correction_cannot_silently_pass(self):
        for field, phase, frame in (("flightVelocity", "Active", 1), ("shotVisualOffset", "Startup", 8),
                                     ("shotAttachmentModel", "Startup", 17), ("flightStep", "Active", 1),
                                     ("ballCorrection", "Active", 1)):
            with self.subTest(field=field):
                rows = self.fixture()
                del next(r for r in rows if r["pose"]["phase"] == phase and r["pose"]["frame"] == frame)[field]
                self.assert_failure(self.analyze(rows), field)

    def test_visual_offset_is_recorded_but_does_not_excuse_palm_detachment(self):
        rows = self.fixture()
        at = next(r for r in rows if r["pose"]["phase"] == "Startup" and r["pose"]["frame"] == 8)
        at["liveBall"][0] += .01
        at["shotAttachmentModel"][0] += .01
        at["shotVisualOffset"][0] = -.01
        report = self.analyze(rows)
        self.assertEqual([], report["failures"])
        self.assertAlmostEqual(.01, report["contactFrames"][0]["physicalVisibleDistanceM"])
        at["ball"][0] += .5
        at["shotVisualOffset"][0] += .5
        self.assert_failure(self.analyze(rows), "anatomical right palm detached")

    def test_duplicate_ball_allows_only_submicrometre_transform_roundoff(self):
        rows = self.fixture()
        at = next(i for i, r in enumerate(rows) if r["pose"]["phase"] == "Startup" and r["pose"]["frame"] == 8)
        rows.insert(at + 1, copy.deepcopy(rows[at]))
        rows[at + 1]["ball"][0] += .0000005
        self.assertEqual([], self.analyze(rows)["failures"])
        rows[at + 1]["ball"][0] += .0000015
        self.assert_failure(self.analyze(rows), "changed rendered ball on duplicate source tick")

    def test_stale_pose_changed_duplicate_ball_and_reset_are_rejected(self):
        rows = self.fixture()
        at = next(i for i, r in enumerate(rows) if r["pose"]["phase"] == "Startup" and r["pose"]["frame"] == 8)
        stale = copy.deepcopy(rows)
        stale[at]["poseMatchesPhysics"] = False
        self.assert_failure(self.analyze(stale), "poseMatchesPhysics")
        duplicate = copy.deepcopy(rows)
        duplicate.insert(at + 1, copy.deepcopy(duplicate[at]))
        self.assertEqual([], self.analyze(duplicate)["failures"])
        duplicate[at+1]["ball"][0] += .001
        self.assert_failure(self.analyze(duplicate), "changed rendered ball on duplicate source tick")
        changed_pose = copy.deepcopy(rows)
        changed_pose.insert(at + 1, copy.deepcopy(changed_pose[at]))
        changed_pose[at+1]["pose"]["bones"]["mixamorig_RightHand"]["world"][0] += .001
        self.assert_failure(self.analyze(changed_pose), "changed final pose on duplicate source tick")
        for row in rows[at:]:
            row["presentationResyncCount"] += 1
        self.assert_failure(self.analyze(rows), "presentation reset spans attachment window")

    def test_observer_uses_selected_pose_clock_and_never_joins_latest_physics(self):
        rows = self.fixture()
        for row in rows:
            source = row["physicsFrame"]
            row["selectedPresentationTick"] = source
            row["ballPresentationTick"] = source
            row["pose"]["presentationTick"] = source
            row["physicsFrame"] += 10000
            row["pose"]["physicsFrame"] += 10000
            row["liveBall"] = [999, 999, 999]
            del row["flightVelocity"]
            del row["shotAttachmentModel"]
        report = self.analyze(rows, role="observer")
        self.assertEqual([], report["failures"])
        self.assertEqual([], report["firstFlightFrames"])
        self.assertLess(report["contactFrames"][0]["sourceTick"], 10000)
        rows[next(i for i,r in enumerate(rows) if r["pose"]["phase"] == "Startup" and r["pose"]["frame"] == 8)]["ballPresentationTick"] += 1
        self.assert_failure(self.analyze(rows, role="observer"), "incoherent selected player/ball source tick")

    def test_owner_keeps_own_clock_and_requires_own_latched_release(self):
        report = self.analyze(self.fixture(), role="owner")
        self.assertEqual([], report["failures"])
        self.assertEqual("owner-local physics", report["release"]["clock"])

    def test_owner_held_stall_is_separate_from_actual_first_two_flight_steps(self):
        report = self.analyze(self.owner_stall_fixture(), role="owner")
        self.assertEqual([], report["failures"])
        self.assertEqual(["Held", "Held"], [p["ballState"] for p in report["firstTwoPhaseFrames"]])
        self.assertEqual(2, len(report["heldCorrectionStalls"]))
        self.assertEqual([("Active", 3), ("Recovery", 0)],
                         [(p["phase"], p["frame"]) for p in report["firstFlightFrames"]])
        self.assertAlmostEqual(.8, report["ballCorrections"][0]["magnitudeM"])
        self.assertLess(max(p["positionErrorM"] for p in report["firstFlightFrames"]), 1e-8)

    def test_stale_held_waist_snap_fails_even_when_model_and_live_ball_agree(self):
        rows = self.owner_stall_fixture()
        at = next(r for r in rows if r["pose"]["phase"] == "Active" and r["pose"]["frame"] == 1)
        at["liveBall"][1] -= .8
        at["shotAttachmentModel"][1] -= .8
        self.assert_failure(self.analyze(rows, role="owner"), "retained absolute release origin")

    def test_late_held_stall_is_still_checked_after_two_actual_flight_steps(self):
        rows = self.fixture()
        index = next(i for i, r in enumerate(rows) if r["pose"]["phase"] == "Recovery" and r["pose"]["frame"] == 3)
        at, previous = rows[index], rows[index - 1]
        origin = at["releasePosition"][:]
        at["ballState"] = "Held"
        at["liveBall"] = origin[:]
        at["shotAttachmentModel"] = origin[:]
        at["ballCorrection"] = {"received": True, "before": previous["liveBall"][:],
            "authoritative": [origin[0], origin[1] - .8, origin[2]], "velocity": [0, 0, 0],
            "beforeState": "InFlight", "state": "Held"}
        report = self.analyze(rows, role="owner")
        self.assertEqual([], report["failures"])
        self.assertEqual(1, len(report["heldCorrectionStalls"]))
        at["liveBall"][1] -= .8
        at["shotAttachmentModel"][1] -= .8
        self.assert_failure(self.analyze(rows, role="owner"), "retained absolute release origin")

    def test_actual_flight_step_must_match_received_correction_and_real_integrator(self):
        for field, message in (("authoritative", "fresh authoritative correction"),
                               ("velocity", "fresh authoritative correction"),
                               ("before", "previous own sample")):
            with self.subTest(field=field):
                rows = self.owner_stall_fixture()
                at = next(r for r in rows if r["pose"]["phase"] == "Active" and r["pose"]["frame"] == 3)
                at["ballCorrection"][field][0] += .1
                self.assert_failure(self.analyze(rows, role="owner"), message)
        rows = self.owner_stall_fixture()
        at = next(r for r in rows if r["pose"]["phase"] == "Active" and r["pose"]["frame"] == 3)
        at["liveBall"][0] += .1
        self.assert_failure(self.analyze(rows, role="owner"), "trapezoidal integrator")
        rows = self.owner_stall_fixture()
        at = next(r for r in rows if r["pose"]["phase"] == "Active" and r["pose"]["frame"] == 3)
        at["ballCorrection"]["received"] = False
        self.assert_failure(self.analyze(rows, role="owner"), "resumed from Held without fresh authoritative correction")

    def test_missing_stall_step_telemetry_cannot_be_excused_as_reconciliation(self):
        rows = self.owner_stall_fixture()
        at = next(r for r in rows if r["pose"]["phase"] == "Active" and r["pose"]["frame"] == 1)
        del at["flightStep"]
        self.assert_failure(self.analyze(rows, role="owner"), "flightStep telemetry")

    def test_fake_requires_zero_release_and_retained_hand_and_dead_dribble(self):
        rows = self.real_trace("early")
        startup = [r for r in rows if r["pose"]["phase"] == "Startup"]
        hand = startup[-1]["pose"]["hand"]
        for row in rows:
            row["hasDribbled"] = True
            row["hand"] = hand
            row["pose"]["hand"] = hand
        report = self.analyze(rows, variant="early", entry="dribble")
        self.assertEqual([], report["failures"])
        at = next(r for r in rows if r["pose"]["phase"] == "Recovery")
        at["releaseCount"] = 1
        self.assert_failure(self.analyze(rows, variant="early", entry="dribble"), "fake released")
        at["releaseCount"] = 0
        at["pose"]["hand"] = "Left" if hand == "Right" else "Right"
        self.assert_failure(self.analyze(rows, variant="early", entry="dribble"), "retained HandSide")
        at["pose"]["hand"] = hand
        at["hasDribbled"] = False
        self.assert_failure(self.analyze(rows, variant="early", entry="dribble"), "dead-dribble")

    def test_common_visual_shift_cannot_explain_itself_as_holder_correction(self):
        rows = self.fixture()
        for row in rows:
            row["ball"][0] += .15
            for bone in row["pose"]["bones"].values():
                bone["world"][0] += .15
        # Independent holder correction stays zero. Palm/ball contact survives
        # this mutation, while the common presentation-frame assertion must fail.
        report = self.analyze(rows)
        self.assert_failure(report, "not explained by holder visual correction")

    def test_actual_limb_lengths_reject_stretch(self):
        rows = self.real_trace("normal")
        dimensions, failures = proof.limb_dimensions(rows)
        self.assertEqual([], failures)
        self.assertEqual(4, len(dimensions))
        rows[10]["pose"]["bones"]["mixamorig_RightHand"]["world"][0] += .1
        self.assertIn("evaluated rig segment stretched: RightForeArm", proof.limb_dimensions(rows)[1])

    def test_matrix_requires_all_combinations_and_separate_owner_and_observer(self):
        self.assertEqual(36, len(expected_cases("local")))
        self.assertEqual(72, len(expected_cases("owner")))
        self.assertIn(("left-moving-late-o7-1-owner", "owner"), expected_cases("owner"))
        self.assertIn(("left-moving-late-o7-1", "observer"), expected_cases("owner"))
        zero, seven = expected_cases("owner", (0,)), expected_cases("owner", (7,))
        self.assertEqual(36, len(zero))
        self.assertEqual(36, len(seven))
        self.assertFalse(zero & seven)
        self.assertEqual(expected_cases("owner"), zero | seven)
        self.assertEqual(108, len(expected_cases("local")) + len(zero) + len(seven))

    def test_actual_authority_and_driver_feint_resolutions_are_required_independently(self):
        authority = [{"kind": "feint-resolution", "phase": "Startup", "frame": 5,
                      "accepted": True, "physicsFrame": 173}]
        driver = [{"kind": "feint-resolution", "phase": "Startup", "frame": 11,
                   "accepted": True, "physicsFrame": 156}]
        self.assertEqual([], proof.feint_resolution_failures("late", authority, driver)[0])
        self.assertTrue(proof.feint_resolution_failures("late", [], driver)[0])
        self.assertTrue(proof.feint_resolution_failures("late", authority, [])[0])
        for key, value in (("accepted", False), ("phase", "Active"), ("frame", 12), ("physicsFrame", None)):
            changed = copy.deepcopy(authority)
            changed[0][key] = value
            self.assertTrue(proof.feint_resolution_failures("late", changed, driver)[0])
        self.assertTrue(proof.feint_resolution_failures("normal", authority, [])[0])

    def test_owner_capture_is_sequential_while_local_cases_may_parallelize(self):
        args = SimpleNamespace(godot="pinned-godot", port=24750, offsets=[0, 7])
        for topology, jobs in (("local", "2"), ("owner", "1")):
            with self.subTest(topology=topology), patch("run_shot_attachment_proof.subprocess.run") as run:
                capture(args, ROOT / ".godot/440/unstarted-test", topology)
                command = run.call_args.args[0]
                self.assertEqual(jobs, command[command.index("--jobs") + 1])
                self.assertEqual(["0", "7"], command[command.index("--offsets") + 1:command.index("--offsets") + 3])
        args.offsets = [7]
        with patch("run_shot_attachment_proof.subprocess.run") as run:
            capture(args, ROOT / ".godot/440/unstarted-test", "owner")
            command = run.call_args.args[0]
            self.assertEqual(["7"], command[command.index("--offsets") + 1:command.index("--offsets") + 2])


if __name__ == "__main__":
    unittest.main()
