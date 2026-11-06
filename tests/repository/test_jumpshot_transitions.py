"""Real-capture mutation contracts for #440's continuity evidence oracle."""
import copy
import gzip
import importlib.util
import json
from pathlib import Path
import sys
import shutil
import unittest
import uuid

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import analyze_jumpshot_transitions as proof
from run_jumpshot_transition_proof import baseline_cases


class JumpshotTransitionEvidenceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        # This is committed production evidence, not a manufactured collection
        # of coordinates shaped to the analyzer. It predates #440 and remains
        # the positive control for source-tick neighborhood coverage alone.
        evidence = ROOT / "docs/analysis/evidence/0418"
        cls.local = [json.loads(line) for line in gzip.decompress(
            (evidence / "local-right-early-after-trace.jsonl.gz").read_bytes()
        ).decode("utf-8").splitlines()]
        cls.remote = [json.loads(line) for line in gzip.decompress(
            (evidence / "remote-right-early-after-trace.jsonl.gz").read_bytes()
        ).decode("utf-8").splitlines()]
        cls.local_manifest = json.loads(gzip.decompress(
            (evidence / "local-right-early-after-manifest.json.gz").read_bytes()))
        cls.remote_manifest = json.loads(gzip.decompress(
            (evidence / "remote-right-early-after-manifest.json.gz").read_bytes()))

    def observed(self, rows, remote=False):
        return proof.observations(rows, remote)

    def test_original_real_capture_proves_complete_entry_fake_and_return(self):
        rows, failures, _ = self.observed(self.local)
        self.assertEqual([], failures)
        seams, failures = proof.measure_boundaries(rows, False)
        self.assertEqual([], failures)
        self.assertEqual({"entry", "fake-recovery", "return"}, {s["name"] for s in seams})
        for seam in seams:
            self.assertEqual(5, len(seam["sourceTicks"]))
            self.assertTrue(all(b-a == 1 for a, b in zip(seam["sourceTicks"], seam["sourceTicks"][1:])))
            self.assertEqual(set(proof.JOINTS), set(seam["joints"]))

    def test_missing_first_or_following_neutral_pose_rejects_return(self):
        original, _, _ = self.observed(self.local)
        first = next(i for i in range(1, len(original))
                     if original[i-1]["pose"]["phase"] == "Recovery" and original[i]["pose"]["phase"] == "Inactive")
        for offset in (0, 1, 2):
            with self.subTest(removed_neutral_offset=offset):
                missing = original[first + offset]["physicsFrame"]
                rows = [row for row in original if row["physicsFrame"] != missing]
                _, failures = proof.measure_boundaries(rows, False)
                self.assertIn("incomplete adjacent source ticks at return", failures)

    def test_identical_hold_is_reported_but_changed_duplicate_is_rejected(self):
        rows, _, _ = self.observed(self.local)
        at = next(i for i, row in enumerate(rows) if row["pose"]["phase"] == "Recovery")
        repeated = rows[:at+1] + [copy.deepcopy(rows[at])] + rows[at+1:]
        observed, failures, duplicates = self.observed(repeated)
        self.assertEqual([], failures)
        self.assertEqual(1, duplicates)
        self.assertEqual(len(rows), len(observed))
        repeated[at+1]["pose"]["bones"]["mixamorig_RightHand"]["body"][0] += .001
        _, failures, _ = self.observed(repeated)
        self.assertIn("changed final pose on duplicate source tick", failures)

    def test_reset_cannot_prove_return_continuity(self):
        rows, failures, _ = self.observed(self.remote, True)
        # The older native remote recording can already expose changed poses
        # on repeated ticks. Full-capture acceptance rejects those separately;
        # this mutation isolates whether the boundary operator rejects resets.
        seams, failures = proof.measure_boundaries(rows, True)
        self.assertEqual([], failures)
        self.assertIn("return", {s["name"] for s in seams})
        rows = copy.deepcopy(rows)
        first = next(i for i in range(1, len(rows))
                     if rows[i-1]["pose"]["phase"] == "Recovery" and rows[i]["pose"]["phase"] == "Inactive")
        for row in rows[first:]: row["presentationResyncCount"] += 1
        seams, failures = proof.measure_boundaries(rows, True)
        self.assertIn("presentation reset spans return", failures)
        self.assertFalse(next(s for s in seams if s["name"] == "return")["stable"])

    def test_report_rejects_cross_clock_baseline_and_requires_improvement(self):
        metadata = dict(role="local", entry="dribble", hand="Right", variant="normal", entryOffset=0,
                        view="gameplay", subjectRole="host", paced=False, pacingMs=0, engine="4.7.1", images=False)
        rows, _, _ = self.observed(self.local)
        seams, _ = proof.measure_boundaries(rows, False)
        baseline = {**metadata, "boundaries": seams}
        unchanged = {**metadata, "boundaries": copy.deepcopy(seams), "failures": []}
        proof.compare(unchanged, baseline)
        self.assertIn("reproduced discontinuity not improved: return maxStepM", unchanged["failures"])
        mismatch = {**metadata, "images": True, "boundaries": copy.deepcopy(seams), "failures": []}
        proof.compare(mismatch, baseline)
        self.assertIn("baseline has incomparable input/view/topology/clock metadata", mismatch["failures"])

    def test_failed_or_truncated_before_recording_cannot_authorize_after_proof(self):
        before = proof.load_json(ROOT / "docs/analysis/evidence/0440/baseline-local.json.gz")
        self.assertEqual(36, len(baseline_cases(before, "local")))
        missing = copy.deepcopy(before)
        missing["cases"].pop()
        with self.assertRaisesRegex(ValueError, "36 complete passing before measurements"):
            baseline_cases(missing, "local")
        incomplete = copy.deepcopy(before)
        incomplete["cases"][0]["result"] = "failed"
        with self.assertRaisesRegex(ValueError, "36 complete passing before measurements"):
            baseline_cases(incomplete, "local")

    def test_measured_before_defect_requires_raw_evidence_and_rejects_other_failures(self):
        before = proof.load_json(ROOT / "docs/analysis/evidence/0440/baseline-owner.json.gz")
        self.assertEqual("failed", before["result"])  # Old continuity never passed.
        self.assertTrue(before["measurementComplete"])
        self.assertEqual(72, len(baseline_cases(before, "owner")))
        affected = next(i for i, case in enumerate(before["cases"]) if case["changedDuplicateEdges"])
        absent_edge = copy.deepcopy(before)
        absent_edge["cases"][affected]["changedDuplicateEdges"].pop()
        with self.assertRaisesRegex(ValueError, "incomplete baseline evidence beyond the named playback defect"):
            baseline_cases(absent_edge, "owner")
        missing_source = copy.deepcopy(before)
        missing_source["cases"][affected]["failures"].append("missing adjacent source tick during shot")
        with self.assertRaisesRegex(ValueError, "incomplete baseline evidence beyond the named playback defect"):
            baseline_cases(missing_source, "owner")

    def test_observer_role_uses_the_same_source_clock_and_authority_as_client(self):
        # The three-peer matrix names this role "observer", while #418 used
        # "client". Replay the same real captured poses through the public
        # analyzer with each role. Authority events must come from the server
        # artifact and boundary time must use selected source ticks in both.
        # Older recordings lack some new matrix evidence, so their common
        # remaining failures are retained rather than pretending full acceptance.
        scratch = ROOT / ".godot" / "repository-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        # Python's mode0700 temporary-directory ACL is not usable by some
        # filesystem-sandboxed Windows runners. Use an ordinary workspace
        # directory with inherited permissions instead.
        root = (scratch / ("jumpshot-observer-" + uuid.uuid4().hex)).resolve()
        root.mkdir()
        try:
            case = root / "right-dribble-early-o0-1"
            server = root / (case.name + "-server")
            case.mkdir()
            server.mkdir()
            (case / "trace.jsonl").write_text("\n".join(json.dumps(row) for row in self.remote), encoding="utf-8")
            (server / "trace.jsonl").write_text("\n".join(json.dumps(row) for row in self.local), encoding="utf-8")
            metadata = dict(entry="dribble", entryOffset=0, subjectPeer=1, subjectRole="host")
            authority = {**self.local_manifest, **metadata, "role": "server"}
            (server / "manifest.json").write_text(json.dumps(authority), encoding="utf-8")
            reports = []
            for role in ("client", "observer"):
                manifest = {**self.remote_manifest, **metadata, "role": role}
                (case / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
                reports.append(proof.analyze(case, root_gate=False))
            client, observer = reports
            self.assertEqual(authority["events"], observer["authoritativeEvents"])
            self.assertEqual(client["boundaries"], observer["boundaries"])
            self.assertEqual(client["failures"], observer["failures"])
            rows, _, _ = self.observed(self.remote, True)
            seams, _ = proof.measure_boundaries(rows, True)
            self.assertEqual([seam["sourceTicks"] for seam in seams],
                             [seam["sourceTicks"] for seam in observer["boundaries"]])
        finally:
            if not root.is_relative_to(scratch.resolve()):
                raise RuntimeError("test cleanup target escaped its workspace scratch directory")
            shutil.rmtree(root)

    def test_extra_startup_or_active_tick_fails_even_with_all_frame_counters(self):
        trace = ROOT / "docs/analysis/evidence/0418/local-right-normal-after-trace.jsonl.gz"
        rows = [json.loads(line) for line in gzip.decompress(trace.read_bytes()).decode().splitlines()]
        durations, failures = proof.normal_phase_durations(rows)
        self.assertEqual([], failures)
        self.assertEqual({"Startup": 18, "Active": 4}, durations)
        for phase, frame in (("Startup", 10), ("Active", 2)):
            with self.subTest(phase=phase):
                mutated = copy.deepcopy(rows)
                index = next(i for i, row in enumerate(mutated)
                             if row.get("authoritativePhase") == phase and row["authoritativeFrame"] == frame)
                duplicate = copy.deepcopy(mutated[index])
                # Preserve every pose/frame counter and an uninterrupted source
                # clock, but consume one extra simulation tick in this phase.
                for row in mutated[index+1:]: row["physicsFrame"] += 1
                duplicate["physicsFrame"] += 1
                mutated.insert(index + 1, duplicate)
                durations, failures = proof.normal_phase_durations(mutated)
                self.assertIn("consumed " + phase + " duration changed", failures)
                self.assertEqual((18 if phase == "Startup" else 4) + 1, durations[phase])

    def test_entry_gate_keeps_the_boundary_but_separates_preexisting_neutral_motion(self):
        rows, _, _ = self.observed(self.local)
        seams, _ = proof.measure_boundaries(rows, False)
        entry = next(s for s in seams if s["name"] == "entry")
        index = next(i for i in range(2, len(rows)) if rows[i-1]["pose"]["phase"] == "Inactive"
                     and rows[i]["pose"]["phase"] == "Startup")
        before = proof.repair_metrics(entry)
        # Alter only the wholly neutral pre-edge, retaining the real handoff.
        neutral = copy.deepcopy(rows)
        neutral[index-2]["pose"]["bones"]["mixamorig_RightHand"]["body"][0] += 10
        altered, _ = proof.measure_boundaries(neutral, False)
        altered = next(s for s in altered if s["name"] == "entry")
        self.assertGreater(altered["maxStepM"], entry["maxStepM"])
        self.assertEqual(before, proof.repair_metrics(altered))
        # A restored jump on the FIRST evaluated shot pose must still fail.
        jump = copy.deepcopy(rows)
        jump[index]["pose"]["bones"]["mixamorig_RightHand"]["body"][0] += 10
        altered, _ = proof.measure_boundaries(jump, False)
        altered = next(s for s in altered if s["name"] == "entry")
        self.assertTrue(all(proof.repair_metrics(altered)[key] > value for key, value in before.items()))
        # Return/fake pre-edges are repair-owned and must never be excluded.
        for seam in seams:
            if seam["name"] != "entry":
                self.assertEqual(seam["maxStepM"], proof.repair_metrics(seam)["maxStepM"])


if __name__ == "__main__":
    unittest.main()
