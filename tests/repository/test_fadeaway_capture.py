"""The form oracle must reject either bad leg, even with a bent-back torso."""
import copy
import importlib.util
import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
spec = importlib.util.spec_from_file_location("fadeaway", ROOT / "tools/analyze_fadeaway_capture.py")
fadeaway = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fadeaway)


class FadeawayProjectionTests(unittest.TestCase):
    def pose(self):
        # Body +Z is supplied by the capture's heading inverse, not inferred
        # from the legs under test. This fixture is an oracle test, not footage.
        return {"bones": {"mixamorig_" + name: {"body": [0, 0, z]}
                          for name, z in {"Spine2": -.2, "Neck": -.1,
                                          "RightLeg": .2, "RightFoot": .1,
                                          "LeftLeg": .3, "LeftFoot": .2}.items()}}

    def test_both_knees_and_ankles_must_clear_the_whole_upper_torso(self):
        pose = self.pose()
        measured = fadeaway.projections(pose)
        self.assertAlmostEqual(.2, measured["minimumLeadM"])
        for bone in ("RightLeg", "RightFoot", "LeftLeg", "LeftFoot"):
            with self.subTest(bone=bone):
                broken = copy.deepcopy(pose)
                broken["bones"]["mixamorig_" + bone]["body"][2] = -.3
                self.assertLess(fadeaway.projections(broken)["minimumLeadM"], 0)

    def rows(self):
        return [dict(physicsFrame=100 + frame, poseMatchesPhysics=True,
                     animation="FadeawayActive", pose=dict(self.pose(), move="jumpshot",
                         phase="Active", frame=frame, physicsFrame=100 + frame))
                for frame in range(4)]

    def test_missing_or_stale_core_pose_cannot_prove_form(self):
        self.assertFalse(fadeaway.form(self.rows(), False)["failures"])
        for frame in (2, 3):
            rows = self.rows()
            rows[frame]["pose"]["physicsFrame"] -= 1
            self.assertIn("missing final Active pose " + str(frame),
                          fadeaway.form(rows, False)["failures"])

    def test_remote_pose_must_share_the_selected_ball_source_tick(self):
        rows = self.rows()
        for row in rows:
            tick = row["physicsFrame"]
            row.update(selectedPresentationTick=tick, ballPresentationTick=tick)
            row["pose"]["presentationTick"] = tick
        self.assertFalse(fadeaway.form(rows, True)["failures"])
        rows[2]["ballPresentationTick"] -= 1
        self.assertIn("missing final Active pose 2", fadeaway.form(rows, True)["failures"])


if __name__ == "__main__":
    unittest.main()
