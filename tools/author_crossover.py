"""Author the two-polarity crossover action in headless Blender (#317).

Run:
    blender --background --python-exit-code 1 --python tools/author_crossover.py \
        -- assets/Dribble.fbx assets/crossover_authored.fbx

The action deliberately contains two independent 21-tick windows.  `Left` and
`Right` name the hand the ball starts in: a left-origin cross finishes toward
the anatomical right, and a right-origin cross finishes toward the anatomical
left.  The explicit windows are consumed by rebuild_crossover_clips.gd; they
replace that tool's legacy Dribble.fbx landmark/signed-synthesis route without
deleting it.
"""
import math
import os
import sys

import bpy
from mathutils import Matrix

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import blender_anim_lib as lib  # noqa: E402

FPS = 60
STARTUP_TICKS, ACTIVE_TICKS, RECOVERY_TICKS = 6, 3, 12
TOTAL_TICKS = STARTUP_TICKS + ACTIVE_TICKS + RECOVERY_TICKS
LEFT_F0, RIGHT_F0, EXPORT_FRAME_END = 0, 30, 55
ACTION_NAME = "crossover"
EXPECTED_SOURCE = "Dribble.fbx"

# Each row is: time, phase label, hips-drop metres, torso twist degrees,
# ball-hand lateral metres, receiving-hand lateral metres, hand height metres,
# ball-hand-side multiplier. The final -1 puts both hands on the destination
# side, so the whole carriage visibly crosses rather than merely exchanging
# two symmetric wrist targets at the body's midline.
# The active interval is a held silhouette: two hands forward at knee height,
# with the receiving hand arriving to take the ball.  That is deliberately
# unlike behind-the-back (hands behind), in-and-out (off hand stays out), and
# between-the-legs (hands stay inside the knees).
KEYPOSES = (
    (0.0, "startup", 0.02, -10.0, 0.20, 0.20, -0.02, 1.0),
    (STARTUP_TICKS / FPS, "active", 0.08, 12.0, 0.05, 0.06, -0.10, 1.0),
    ((STARTUP_TICKS + ACTIVE_TICKS) / FPS, "recovery", 0.08, 12.0, 0.05, 0.06, -0.10, 1.0),
    (TOTAL_TICKS / FPS, "recovery", 0.03, 15.0, 0.08, 0.08, -0.04, -1.0),
)

NEUTRAL_HIP_TO_ANKLE_M = 0.62
STANCE_HALF_WIDTH_M = 0.12
ELBOW_HINT_UP, ELBOW_HINT_LAT = -0.35, 0.55
GROUND_BAND_TOL_M = 0.08
MIN_CROSS_TRAVEL_M = 0.18
POSE_DISTINCT_MIN_DEG = 15.0


def keyposes():
    return [lib.Keypose(t, label, drop=drop, twist=twist, ball_lat=ball_lat,
                        recv_lat=recv_lat, hand_height=height, ball_side=ball_side)
            for t, label, drop, twist, ball_lat, recv_lat, height, ball_side in KEYPOSES]


def author_polarity(arm, geom, body_right, origin, frame_offset):
    """Bake one complete origin-hand arc and return its proof measurements."""
    recv = "R" if origin == "L" else "L"
    sign = -1.0 if origin == "L" else 1.0
    scene = bpy.context.scene
    f0, f1 = frame_offset, frame_offset + TOTAL_TICKS
    hips_base = arm.pose.bones[lib.HIPS].head.copy()
    ankle_base = {
        "L": hips_base - body_right * geom.m(STANCE_HALF_WIDTH_M) - geom.up * geom.m(NEUTRAL_HIP_TO_ANKLE_M),
        "R": hips_base + body_right * geom.m(STANCE_HALF_WIDTH_M) - geom.up * geom.m(NEUTRAL_HIP_TO_ANKLE_M),
    }

    def apply(frame, _time, ch):
        # Pin clavicles rather than letting the dribble source's idle sway
        # decide the active silhouette.
        for side in ("L", "R"):
            clavicle = arm.pose.bones[f"mixamorig:{'Left' if side == 'L' else 'Right'}Shoulder"]
            clavicle.matrix_basis = Matrix.Identity(4)
            bpy.context.view_layer.update()
            clavicle.keyframe_insert("rotation_quaternion", frame=frame)

        lib.drop_hips(arm, -geom.up * geom.m(ch["drop"]), geom, frame=frame)
        hips = arm.pose.bones[lib.HIPS].head.copy()

        # The measured body-right convention is the polarity oracle.  A positive
        # destination is anatomical right, so Left-origin gets a positive finish.
        lib.rotate_bone_about_head(
            arm, lib.SPINE,
            (Matrix.Rotation(math.radians(-sign * ch["twist"]), 4, geom.up),),
            frame=frame)

        toe_dir = (geom.forward * 0.90 - geom.up * 0.44).normalized()
        # A grounded, widening base makes the one-shot commitment readable.
        for side, side_sign in (("L", -1.0), ("R", 1.0)):
            ankle = ankle_base[side] + geom.forward * geom.m(0.02 * sign)
            lib.plant_foot(arm, side, ankle, toe_dir, geom, frame=frame)

        # Both wrists move in front of the hips at Active.  The origin hand
        # carries across; the receiving hand arrives from the other side.
        for side, lateral, side_sign in ((origin, ch["ball_lat"], sign * ch["ball_side"]),
                                         (recv, ch["recv_lat"], -sign)):
            target = (hips + geom.forward * geom.m(0.08)
                      + body_right * geom.m(side_sign * lateral)
                      + geom.up * geom.m(ch["hand_height"]))
            hint = (geom.up * ELBOW_HINT_UP + body_right * (side_sign * ELBOW_HINT_LAT)).normalized()
            lib.aim_arm(arm, side, target, hint, geom, frame=frame)

    lib.bake_timeline(arm, keyposes(), apply, f0, f1, FPS)

    with lib.preserve_frame():
        scene.frame_set(f0)
        start = arm.pose.bones[lib.ARM_CHAIN[origin][2]].head.copy()
        scene.frame_set(f1)
        end = arm.pose.bones[lib.ARM_CHAIN[recv][2]].head.copy()
    travel = geom.to_m((end - start).dot(body_right))
    lib.report(f"{origin}_origin_cross_travel_m", f"{travel:+.4f}")
    if not (travel * -sign >= MIN_CROSS_TRAVEL_M):
        raise SystemExit(
            f"FATAL: {origin}-origin crossover travel {travel:+.4f} m does not reach its "
            f"anatomical destination by {MIN_CROSS_TRAVEL_M} m.")
    return f0, f1, travel


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    if len(argv) != 2:
        raise SystemExit("usage: author_crossover.py <Dribble.fbx> <crossover_authored.fbx>")
    src, dst = argv
    arm, _source_f0, _source_f1 = lib.load_source(src, FPS, expected=EXPECTED_SOURCE)
    geom = lib.RigGeometry(arm)
    geom.log_summary()
    lib.enter_pose_mode(arm)

    result = {}
    for origin, frame in (("L", LEFT_F0), ("R", RIGHT_F0)):
        result[origin] = author_polarity(arm, geom, geom.body_right, origin, frame)

    # A duplicated polarity is not merely an aesthetic defect: it creates the
    # wrong read for one origin.  Require the two measured directions to differ.
    if not (result["L"][2] > 0.0 and result["R"][2] < 0.0):
        raise SystemExit("FATAL: authored crossover polarities do not travel in opposite directions.")

    lib.verify_all_bones_keyed(arm, expected_count=52)
    for origin, (f0, f1, _travel) in result.items():
        frames = list(range(f0, f1 + 1))
        lib.verify_pose_unscaled(arm, frames)
        lib.verify_grounded(arm, frames, GROUND_BAND_TOL_M, geom)
        lib.verify_pose_distinct(lib.snapshot_pose(arm, f0), lib.snapshot_pose(arm, f1),
                                 POSE_DISTINCT_MIN_DEG, f"{origin}_startup_vs_recovery")

    bpy.ops.object.mode_set(mode="OBJECT")
    bpy.context.scene.frame_start, bpy.context.scene.frame_end = 0, EXPORT_FRAME_END
    lib.export_fbx(arm, dst, ACTION_NAME)
    print("AUTHOR_OK")


if __name__ == "__main__":
    main()
