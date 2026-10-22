"""Regression contracts for the jump-shot capture proof's source-tick coverage."""

import gzip
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
EVIDENCE = ROOT / "docs/analysis/evidence/0418"


class JumpshotCaptureCoverageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.manifest = gzip.decompress(
            (EVIDENCE / "local-right-early-after-manifest.json.gz").read_bytes()
        ).decode("utf-8")
        cls.rows = [json.loads(line) for line in gzip.decompress(
            (EVIDENCE / "local-right-early-after-trace.jsonl.gz").read_bytes()
        ).decode("utf-8").splitlines()]

    def analyze(self, rows):
        with tempfile.TemporaryDirectory() as temporary:
            capture = Path(temporary) / "right-early-1"
            capture.mkdir()
            (capture / "manifest.json").write_text(self.manifest, encoding="utf-8")
            (capture / "trace.jsonl").write_text(
                "\n".join(json.dumps(row) for row in rows), encoding="utf-8")
            report = Path(temporary) / "proof.json"
            completed = subprocess.run(
                [sys.executable, str(ROOT / "tools/analyze_jumpshot_capture.py"),
                 str(capture), "--baseline", str(ROOT / "tests/fixtures/jumpshot_boundary_baseline.json"),
                 "--output", str(report)],
                cwd=ROOT, capture_output=True, text=True, check=False)
            self.assertTrue(report.exists(), completed.stdout + completed.stderr)
            return completed.returncode, json.loads(report.read_text(encoding="utf-8"))

    def test_missing_last_recovery_poses_cannot_prove_neutral_boundary(self):
        # The original capture is a positive control for this exact frozen
        # motion bound. Remove only the observed final Recovery pose: its two
        # neighbors still fit that bound but cannot prove the missing tick.
        status, report = self.analyze(self.rows)
        self.assertEqual(0, status, report)
        for frame in (18, 19):
            with self.subTest(missing_recovery_frame=frame):
                removed = [row for row in self.rows
                           if row.get("pose", {}).get("phase") == "Recovery"
                           and row["pose"].get("frame") == frame]
                self.assertEqual(1, len(removed))
                missing_tick = removed[0]["physicsFrame"]
                status, report = self.analyze(
                    [row for row in self.rows if row["physicsFrame"] != missing_tick])
                self.assertEqual(1, status, report)
                self.assertIn("incomplete one-tick phase boundary Recovery->Inactive",
                              report["cases"][0]["failures"])

    def test_truncated_neutral_neighborhood_cannot_prove_boundary(self):
        first_neutral = next(index for index in range(1, len(self.rows))
                             if self.rows[index - 1].get("pose", {}).get("phase") == "Recovery"
                             and self.rows[index].get("pose", {}).get("phase") == "Inactive")
        # Keep both sides of the transition but omit its following edge.
        # All retained source ticks are adjacent; exactly three edges are needed.
        status, report = self.analyze(self.rows[:first_neutral + 1])
        self.assertEqual(1, status, report)
        self.assertIn("incomplete one-tick phase boundary Recovery->Inactive",
                      report["cases"][0]["failures"])

    def test_remote_presentation_reset_never_forms_a_continuity_edge(self):
        path = ROOT / "tools/analyze_jumpshot_capture.py"
        spec = importlib.util.spec_from_file_location("analyze_jumpshot_capture", path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        rows = [json.loads(line) for line in gzip.decompress(
            (EVIDENCE / "remote-right-early-after-trace.jsonl.gz").read_bytes()
        ).decode("utf-8").splitlines()]
        # Real remote evidence supplies the positive control. Change only the
        # presentation reset counter at the source phase boundary; it must no
        # longer be considered an ordinary continuity observation.
        self.assertIn("Recovery->Inactive",
                      {seam["transition"] for seam in module.boundaries(rows, True)})
        rows = copy.deepcopy(rows)
        first_neutral = next(index for index in range(1, len(rows))
                             if rows[index - 1].get("pose", {}).get("phase") == "Recovery"
                             and rows[index].get("pose", {}).get("phase") == "Inactive")
        for row in rows[first_neutral:]:
            row["presentationResyncCount"] += 1
        self.assertNotIn("Recovery->Inactive",
                         {seam["transition"] for seam in module.boundaries(rows, True)})


if __name__ == "__main__":
    unittest.main()
