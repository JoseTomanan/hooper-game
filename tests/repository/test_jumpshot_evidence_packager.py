"""Keep #440's frozen baseline defect visible without relaxing after evidence."""
import copy
import gzip
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import package_jumpshot_transition_evidence as package


class JumpshotEvidencePackagerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        evidence = ROOT / "docs/analysis/evidence/0418"
        cls.original = [json.loads(line) for line in gzip.decompress(
            (evidence / "local-right-early-after-trace.jsonl.gz").read_bytes()).decode().splitlines()]
        cls.manifest = json.loads(gzip.decompress(
            (evidence / "local-right-early-after-manifest.json.gz").read_bytes()))

    def capture(self):
        rows = copy.deepcopy(self.original)
        for row in rows:
            row.update(camera=[], cameraProvenance="observed", viewportSize=[960, 540])
        at = next(i for i, row in enumerate(rows) if row.get("pose", {}).get("phase") == "Recovery")
        # One source tick has three raw observations: original, changed, then
        # changed again. Both adjacent edges must survive the projection.
        first, second = copy.deepcopy(rows[at]), copy.deepcopy(rows[at])
        first["sample"] += 1000
        second["sample"] += 2000
        first["pose"]["bones"]["mixamorig_RightHand"]["world"][0] += .1
        second["pose"]["bones"]["mixamorig_RightHand"]["world"][0] += .2
        rows[at + 1:at + 1] = [first, second]
        manifest = {**self.manifest, "animationResourceSha256": package.BEFORE_ASSET_SHA256,
                    "playerSceneSha256": package.BEFORE_SCENE_SHA256}
        return manifest, rows

    def read(self, manifest, rows, before=False):
        with patch.object(package, "read_json", return_value=manifest), \
             patch.object(Path, "read_text", return_value="\n".join(json.dumps(row) for row in rows)), \
             patch.object(Path, "is_file", return_value=True):
            return package.read_capture(Path("fixture"), before=before)

    def test_frozen_before_retains_each_raw_adjacent_changed_edge(self):
        manifest, rows = self.capture()
        _, projection, duplicate_count, edges = self.read(manifest, rows, before=True)
        self.assertEqual(2, duplicate_count)
        self.assertEqual(2, len(edges))
        self.assertEqual(edges[0][1], edges[1][0])
        self.assertEqual([.1, .2], [round(edge[1]["pose"]["bones"]["mixamorig_RightHand"]["world"][0] -
                                         edges[0][0]["pose"]["bones"]["mixamorig_RightHand"]["world"][0], 5)
                                    for edge in edges])
        tick = edges[0][0]["physicsFrame"]
        self.assertEqual(1, sum(row["physicsFrame"] == tick for row in projection))
        self.assertEqual(edges[0][0], next(row for row in projection if row["physicsFrame"] == tick))

    def test_after_rejects_same_defect_even_with_frozen_hashes(self):
        with self.assertRaisesRegex(ValueError, "changed final pose"):
            self.read(*self.capture())

    def test_before_allowlist_requires_both_exact_hashes(self):
        for key in ("animationResourceSha256", "playerSceneSha256"):
            manifest, rows = self.capture()
            manifest[key] = "incorrect"
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, "changed final pose"):
                self.read(manifest, rows, before=True)

    def test_before_does_not_allow_missing_ticks_or_reset(self):
        manifest, rows = self.capture()
        tick = next(row["physicsFrame"] for row in rows if row.get("pose", {}).get("phase") == "Startup")
        missing = [row for row in rows if row["physicsFrame"] != tick]
        with self.assertRaisesRegex(ValueError, "rendered source ticks"):
            self.read(manifest, missing, before=True)
        for row in rows:
            if row["physicsFrame"] > tick: row["presentationResyncCount"] += 1
        with self.assertRaisesRegex(ValueError, "presentation reset"):
            self.read(manifest, rows, before=True)

    def test_observer_pacing_must_match(self):
        with self.assertRaisesRegex(ValueError, "observerPacingMs"):
            package.metadata_matches({"observerPacingMs": 16}, {"observerPacingMs": 0})


if __name__ == "__main__":
    unittest.main()
