"""Author the unhanded Euro-step clip family for #312.

The Hips deliberately cross the body midline twice during the fourteen-tick
Active window.  This is an in-place visual read: gameplay owns world movement.
Run Blender with --python-exit-code 1; a Blender traceback otherwise exits zero.
"""
import math
import os
import sys

import bpy
from mathutils import Matrix

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import blender_anim_lib as lib

FPS = 60
STARTUP_TICKS, ACTIVE_TICKS, RECOVERY_TICKS = 6, 14, 16
F0, STARTUP_END = 0, STARTUP_TICKS
ACTIVE_MID, ACTIVE_END = 12, STARTUP_TICKS + ACTIVE_TICKS
F1 = ACTIVE_END + RECOVERY_TICKS
ACTION_NAME = "eurostep"

# The first Active pose remains on the entry side, then the hips cross to the
# plant side and back again.  The sign changes are the defining read, not hand.
KEYPOSES = [
    (F0,          0.00, -0.03,  8.0, 0.00),
    (STARTUP_END, -0.18, -0.10, 18.0, 0.00),
    (ACTIVE_MID,  0.34, -0.08, -8.0, 0.05),
    (ACTIVE_END, -0.38, -0.08,  8.0, 0.03),
    (F1,         -0.16, -0.04,  0.0, 0.00),
]

def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    src, dst = argv[0], argv[1]
    arm, _, _ = lib.load_source(src, FPS)
    geom = lib.RigGeometry(arm)
    right, up, forward = geom.body_right, geom.up, geom.forward
    lib.enter_pose_mode(arm)
    hips_base = arm.pose.bones[lib.HIPS].head.copy()
    ankle_base = {
        "R": hips_base + right * geom.m(.12) - up * geom.m(.62),
        "L": hips_base - right * geom.m(.12) - up * geom.m(.62),
    }
    poses = [lib.Keypose(frame / FPS, "euro", lateral=lat, crouch=crouch,
                         torso=torso, foot_lift=lift)
             for frame, lat, crouch, torso, lift in KEYPOSES]

    def apply(frame, _time, ch):
        # Bake every body bone; explicit Hips motion is lateral-relative only.
        hips = arm.pose.bones[lib.HIPS]
        matrix = hips.matrix.copy()
        matrix.translation = hips_base + right * geom.m(ch["lateral"]) + up * geom.m(ch["crouch"])
        hips.matrix = matrix
        bpy.context.view_layer.update()
        hips.keyframe_insert("location", frame=frame)
        lib.rotate_bone_about_head(arm, lib.SPINE,
            (Matrix.Rotation(math.radians(ch["torso"]), 4, right),), frame=frame)
        toe_dir = (forward * .9 - up * .44).normalized()
        # Alternating lift means grounding is intentionally assessed per foot.
        for side, sign in (("R", 1.0), ("L", -1.0)):
            lift = ch["foot_lift"] if (side == "R" and STARTUP_END <= frame <= ACTIVE_MID) else 0.0
            target = ankle_base[side] + right * geom.m(ch["lateral"] * .35) + up * geom.m(lift)
            lib.plant_foot(arm, side, target, toe_dir, geom, frame=frame)
        hips_now = arm.pose.bones[lib.HIPS].head.copy()
        for side, sign in (("R", 1.0), ("L", -1.0)):
            # Gathered ball: symmetric chest-high hands, never a hand-side signal.
            target = hips_now + forward * geom.m(.18) + right * geom.m(sign * .09) + up * geom.m(.20)
            hint = (up * .3 + right * sign * .6).normalized()
            lib.aim_arm(arm, side, target, hint, geom, frame=frame)

    lib.bake_timeline(arm, poses, apply, F0, F1, FPS)
    bpy.ops.object.mode_set(mode="OBJECT")
    bpy.context.scene.frame_start, bpy.context.scene.frame_end = F0, F1
    lib.verify_all_bones_keyed(arm, expected_count=52)
    lib.verify_pose_unscaled(arm, range(F0, F1 + 1))
    # A direct sign test refuses a normal one-cross side-step before export.
    values = []
    for frame in range(STARTUP_END, ACTIVE_END + 1):
        bpy.context.scene.frame_set(frame)
        values.append((arm.pose.bones[lib.HIPS].head - hips_base).dot(right))
    signs = [1 if v > geom.m(.01) else -1 if v < -geom.m(.01) else 0 for v in values]
    changes = sum(a != b for a, b in zip([s for s in signs if s], [s for s in signs if s][1:]))
    if changes < 2:
        raise SystemExit(f"FATAL: Euro-step Active crosses the midline {changes} time(s), need two.")
    lib.export_fbx(arm, dst, ACTION_NAME)

if __name__ == "__main__":
    main()
