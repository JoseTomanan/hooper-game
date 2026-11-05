"""Independent projection checks for #442's diagnostic overlays."""
import copy
from collections import Counter
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
from analyze_shot_attachment import load_calibration
from generate_shot_gather_profile import transform
from package_shot_attachment_evidence import expected_keys, overlay_geometry, project_world


class ShotAttachmentProjectionTests(unittest.TestCase):
    def camera(self, projection="Perspective"):
        return {"camera": [0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1],
                "viewportSize": [960, 540], "cameraProjection": projection,
                "cameraNear": .05, "cameraFar": 100, "cameraFov": 90, "cameraSize": 4}

    def test_perspective_center_and_vertical_fov_scale(self):
        row = self.camera()
        self.assertEqual([480, 270], project_world(row, [0, 0, -2]))
        x, y = project_world(row, [1, 1, -2])
        self.assertAlmostEqual(615, x)
        self.assertAlmostEqual(135, y)

    def test_orthographic_extent_has_no_depth_scaling(self):
        row = self.camera("Orthogonal")
        self.assertEqual([615, 135], project_world(row, [1, 1, -2]))
        self.assertEqual([615, 135], project_world(row, [1, 1, -20]))

    def test_world_transform_is_inverted_and_metadata_not_changed(self):
        row = self.camera()
        row["camera"] = [2, 3, 4, 0, 0, -1, 0, 1, 0, 1, 0, 0]
        before = copy.deepcopy(row)
        self.assertEqual([480, 270], project_world(row, [0, 3, 4]))
        x, y = project_world(row, [0, 4, 3])
        self.assertAlmostEqual(615, x)
        self.assertAlmostEqual(135, y)
        self.assertEqual(before, row)

    def test_behind_clipped_offscreen_or_unrecorded_projection_cannot_draw(self):
        for point, reason in (([0, 0, 2], "behind"), ([0, 0, -.01], "clipping"),
                              ([100, 0, -2], "outside")):
            with self.subTest(point=point), self.assertRaisesRegex(ValueError, reason):
                project_world(self.camera(), point)
        row = self.camera()
        row["cameraProjection"] = "Frustum"
        with self.assertRaisesRegex(ValueError, "unsupported"):
            project_world(row, [0, 0, -2])

    def test_overlay_uses_actual_calibrated_vertex_not_model_or_wrist_center(self):
        calibration = load_calibration()
        row = self.camera()
        wrist = [0, 0, -3, 1, 0, 0, 0, 1, 0, 0, 0, 1]
        row["pose"] = {"bones": {"mixamorig_RightHand": {"world": wrist}}}
        local = calibration["palms"]["Right"]["wrist_local"]["Right"]
        palm = transform(wrist, local["position"])
        row["ball"] = [p + calibration["radius"] * n for p, n in zip(palm, local["normal"])]
        row["shotAttachmentModel"] = [999, 999, 999]
        geometry = overlay_geometry(row, calibration)
        self.assertEqual(palm, geometry["palmWorld"])
        self.assertEqual(11216, geometry["skinVertex"])
        self.assertLess(geometry["contactErrorM"], 1e-8)
        self.assertGreater(geometry["wristCenterDistanceM"], .12)
        row["ball"][0] += .3
        self.assertGreater(overlay_geometry(row, calibration)["contactErrorM"], .02)

    def test_selection_is_exact_thirty_local_offset_zero_cases(self):
        keys = expected_keys()
        self.assertEqual(30, len(keys))
        self.assertEqual({"gameplay": 18, "front": 6, "side": 6}, dict(Counter(k[1] for k in keys)))
        self.assertTrue(all(role == "local" and name.endswith("-o0-1") for role, _, name in keys))


if __name__ == "__main__":
    unittest.main()
