"""Rejected owner footage must retain resets without claiming continuity."""
import copy
import gzip
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import package_jumpshot_owner_diagnostics as diagnostic


class JumpshotOwnerDiagnosticsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        evidence = ROOT / "docs/analysis/evidence/0418"
        cls.original = [json.loads(line) for line in gzip.decompress(
            (evidence / "local-right-normal-after-trace.jsonl.gz").read_bytes()).decode().splitlines()]
        cls.manifest = json.loads(gzip.decompress(
            (evidence / "local-right-normal-after-manifest.json.gz").read_bytes()))

    def capture(self):
        rows = copy.deepcopy(self.original)
        for row in rows:
            row.update(locallyPredicted=True, camera=[], cameraProvenance="observed",
                       viewportSize=[960, 540], presentationResyncCount=2)
        manifest = {**self.manifest, "role": "owner", "subjectRole": "owner"}
        return manifest, rows

    def read(self, manifest, rows, image_exists=True, before=False):
        with patch.object(diagnostic, "read_json", return_value=manifest), \
             patch.object(Path, "read_text", return_value="\n".join(json.dumps(row) for row in rows)), \
             patch.object(Path, "is_file", return_value=image_exists):
            return diagnostic.read_owner(Path("fixture-owner"), before=before)

    def test_non_owner_rendering_is_refused(self):
        manifest, rows = self.capture()
        manifest["role"] = "observer"
        with self.assertRaisesRegex(ValueError, "owning predicted"):
            self.read(manifest, rows)

    def test_missing_owner_physics_tick_is_refused(self):
        manifest, rows = self.capture()
        at = next(i for i, row in enumerate(rows) if row.get("pose", {}).get("phase") == "Startup"
                  and row["pose"].get("frame") == 8)
        rows.pop(at)
        with self.assertRaisesRegex(ValueError, "consecutive owner physics"):
            self.read(manifest, rows)

    def test_changed_duplicate_pose_is_refused(self):
        manifest, rows = self.capture()
        at = next(i for i, row in enumerate(rows) if row.get("pose", {}).get("phase") == "Recovery")
        duplicate = copy.deepcopy(rows[at])
        duplicate["pose"]["bones"]["mixamorig_RightHand"]["world"][0] += .1
        rows.insert(at + 1, duplicate)
        with self.assertRaisesRegex(ValueError, "changed duplicate owner pose"):
            self.read(manifest, rows)

    def test_resets_survive_with_exact_markers_and_rejected_status(self):
        manifest, rows = self.capture()
        reset_tick = next(row["physicsFrame"] for row in rows if row.get("pose", {}).get("phase") == "Recovery")
        for row in rows:
            if row["physicsFrame"] >= reset_tick:
                row["presentationResyncCount"] = 3
        _, selected, record = self.read(manifest, rows)
        self.assertEqual("rejected", record["status"])
        self.assertEqual("not established by this rendered diagnostic", record["ownerRenderedContinuityVerdict"])
        self.assertNotIn("passed", json.dumps(record))
        markers = [frame for frame in record["frames"] if frame["resetEvent"]]
        self.assertEqual([{"outputFrame": next(i for i, row in enumerate(selected) if row["physicsFrame"] == reset_tick),
                           "physicsFrame": reset_tick, "resetCount": 3, "previousResetCount": 2,
                           "resetEvent": True}], markers)
        self.assertEqual([row["presentationResyncCount"] for row in selected],
                         [frame["resetCount"] for frame in record["frames"]])
        self.assertEqual("ball reset count=3 | RESET 2->3", diagnostic.reset_label(markers[0]))

    def test_missing_original_images_are_refused(self):
        with self.assertRaisesRegex(ValueError, "missing original PNG"):
            self.read(*self.capture(), image_exists=False)

    def test_before_requires_exact_frozen_asset_and_scene_hashes(self):
        manifest, rows = self.capture()
        with self.assertRaisesRegex(ValueError, "frozen before"):
            self.read(manifest, rows, before=True)
        manifest.update(animationResourceSha256=diagnostic.BEFORE_ASSET_SHA256,
                        playerSceneSha256=diagnostic.BEFORE_SCENE_SHA256)
        self.read(manifest, rows, before=True)
        for key in ("animationResourceSha256", "playerSceneSha256"):
            altered = {**manifest, key: "wrong"}
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, "frozen before"):
                self.read(altered, rows, before=True)


if __name__ == "__main__":
    unittest.main()
