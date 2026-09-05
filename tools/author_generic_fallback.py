"""Author the three short, phase-honest generic fallback clips for #296.

Run:
    "$BLENDER" --background --python-exit-code 1 \
        --python tools/author_generic_fallback.py -- assets/Dribble.fbx \
        assets/genericstartup_authored.fbx assets/genericactive_authored.fbx \
        assets/genericrecovery_authored.fbx

The three outputs deliberately are separate FBXs.  Each state has to begin at
the same Dribble stance, rather than inheriting the terminal pose authored for
the preceding state.  Reloading the source before each export makes that
contract structural and gives the rebuild step one named take per clip.

The generic state has no move-specific tick window.  Every clip is therefore
six frames at 60 Hz (0.100 s), non-cyclic once rebuilt into locomotion.res, and
holds its terminal pose for however long the state remains active.  The poses
are intentionally neutral: load, commit, settle -- never a directional move.
"""

import math
import os
import sys

import bpy
from mathutils import Matrix

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import blender_anim_lib as lib  # noqa: E402  (must follow the sys.path fix)


EXPECTED_SOURCE = "Dribble.fbx"
FPS = 60
F0 = 0
F1 = 6  # inclusive: 0.100 s at 60 Hz
POSE_DISTINCT_MIN_DEG = 15.0

# Rig-intrinsic measurements used by the other Dribble.fbx authorers.
NEUTRAL_HIP_TO_ANKLE_M = 0.62
NEUTRAL_HALF_WIDTH_M = 0.12
TORSO_PITCH_SIGN = -1.0  # verified as forward by the sibling authors.
ELBOW_HINT_UP = 0.3
ELBOW_HINT_LAT = 0.6


# Each table describes one self-contained clip, from its hard-cut entry pose
# to the pose LOOP_NONE will hold.  `hips_fore_m` is a finite visual weight
# shift, not a locomotion cycle; the active clip has only two keyed poses.
CLIPS = {
    "genericstartup": (
        "load",
        [
            lib.Keypose(0.0, "entry", hip_up_m=0.0, hips_fore_m=0.0,
                        torso_pitch_deg=0.0, half_width_m=NEUTRAL_HALF_WIDTH_M,
                        right_fore_m=0.0, arm_fore_m=0.16, arm_lat_m=0.20,
                        arm_height_m=0.00),
            lib.Keypose(F1 / FPS, "load", hip_up_m=-0.10, hips_fore_m=0.0,
                        torso_pitch_deg=18.0, half_width_m=NEUTRAL_HALF_WIDTH_M,
                        right_fore_m=0.0, arm_fore_m=0.08, arm_lat_m=0.13,
                        arm_height_m=-0.02),
        ],
    ),
    "genericactive": (
        "commit",
        [
            lib.Keypose(0.0, "entry", hip_up_m=0.0, hips_fore_m=0.0,
                        torso_pitch_deg=0.0, half_width_m=NEUTRAL_HALF_WIDTH_M,
                        right_fore_m=0.0, arm_fore_m=0.16, arm_lat_m=0.20,
                        arm_height_m=0.00),
            lib.Keypose(F1 / FPS, "commit", hip_up_m=0.02, hips_fore_m=0.12,
                        torso_pitch_deg=25.0, half_width_m=NEUTRAL_HALF_WIDTH_M,
                        right_fore_m=0.20, arm_fore_m=0.25, arm_lat_m=0.32,
                        arm_height_m=0.08),
        ],
    ),
    "genericrecovery": (
        "settle",
        [
            lib.Keypose(0.0, "entry", hip_up_m=0.0, hips_fore_m=0.0,
                        torso_pitch_deg=0.0, half_width_m=NEUTRAL_HALF_WIDTH_M,
                        right_fore_m=0.0, arm_fore_m=0.16, arm_lat_m=0.20,
                        arm_height_m=0.00),
            lib.Keypose(F1 / FPS, "settle", hip_up_m=0.03, hips_fore_m=0.0,
                        torso_pitch_deg=4.0, half_width_m=0.18,
                        right_fore_m=0.0, arm_fore_m=0.10, arm_lat_m=0.16,
                        arm_height_m=-0.03),
        ],
    ),
}


def _author_one(src, dst, action_name, keyposes):
    """Reload the source, author one state, prove it, and export one take."""
    arm, _src_f0, _src_f1 = lib.load_source(src, FPS, expected=EXPECTED_SOURCE)
    scene = bpy.context.scene
    geom = lib.RigGeometry(arm)
    body_right, up, forward = geom.body_right, geom.up, geom.forward
    lib.enter_pose_mode(arm)

    # Capture the shared stance before this clip keys anything.  Feet stay on
    # this floor while the hips load/commit/settle, avoiding a whole-body lift.
    hips_base = arm.pose.bones[lib.HIPS].head.copy()
    worst_wrist_err = 0.0
    geom.reset_ankle_ik()

    def apply(frame, _t_s, ch):
        nonlocal worst_wrist_err

        # Dribble's clavicle sway is source motion, not generic-state intent.
        # Pin it before solving arms so every clip begins from the same upper
        # body basis and all arm bones receive baked keys.
        for side in ("L", "R"):
            shoulder = arm.pose.bones[
                f"mixamorig:{'Left' if side == 'L' else 'Right'}Shoulder"]
            shoulder.matrix_basis = Matrix.Identity(4)
            bpy.context.view_layer.update()
            shoulder.keyframe_insert("rotation_quaternion", frame=frame)

        hips = arm.pose.bones[lib.HIPS]
        hips_matrix = hips.matrix.copy()
        hips_matrix.translation = (hips_base + up * geom.m(ch["hip_up_m"])
                                   + forward * geom.m(ch["hips_fore_m"]))
        hips.matrix = hips_matrix
        bpy.context.view_layer.update()
        hips.keyframe_insert("location", frame=frame)
        hips_now = hips.head.copy()

        lib.rotate_bone_about_head(
            arm, lib.SPINE,
            (Matrix.Rotation(math.radians(TORSO_PITCH_SIGN * ch["torso_pitch_deg"]),
                             4, body_right),),
            frame=frame)

        toe_dir = (forward * 0.90 - up * 0.44).normalized()
        for side, lateral_sign, fore_m in (
            ("L", -1.0, 0.0), ("R", 1.0, ch["right_fore_m"]),
        ):
            ankle = (hips_base + body_right * geom.m(lateral_sign * ch["half_width_m"])
                     + forward * geom.m(fore_m) - up * geom.m(NEUTRAL_HIP_TO_ANKLE_M))
            lib.plant_foot(arm, side, ankle, toe_dir, geom, frame=frame)

        for side, lateral_sign in (("L", -1.0), ("R", 1.0)):
            hand = (hips_now + forward * geom.m(ch["arm_fore_m"])
                    + body_right * geom.m(lateral_sign * ch["arm_lat_m"])
                    + up * geom.m(ch["arm_height_m"]))
            hint = (up * ELBOW_HINT_UP
                    + body_right * (lateral_sign * ELBOW_HINT_LAT)).normalized()
            worst_wrist_err = max(
                worst_wrist_err,
                lib.aim_arm(arm, side, hand, hint, geom, frame=frame))

    lib.bake_timeline(arm, keyposes, apply, F0, F1, FPS)
    bpy.ops.object.mode_set(mode="OBJECT")
    scene.frame_start, scene.frame_end = F0, F1

    frames = list(range(F0, F1 + 1))
    lib.report_ankle_ik("worst_ankle_ik_err_m", geom)
    lib.report("worst_wrist_ik_err_m", f"{geom.to_m(worst_wrist_err):.6f}")
    lib.verify_all_bones_keyed(arm, expected_count=52)
    lib.verify_pose_unscaled(arm, frames)
    terminal_pose = lib.snapshot_pose(arm, F1)
    lib.export_fbx(arm, dst, action_name)
    lib.log(f"wrote {dst}")
    return terminal_pose


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    if len(argv) != 4:
        raise SystemExit(
            "usage: author_generic_fallback.py <Dribble.fbx> <startup.fbx> "
            "<active.fbx> <recovery.fbx>")
    src, startup_dst, active_dst, recovery_dst = argv

    poses = {}
    for action_name, dst in (
        ("genericstartup", startup_dst),
        ("genericactive", active_dst),
        ("genericrecovery", recovery_dst),
    ):
        _meaning, keyposes = CLIPS[action_name]
        poses[action_name] = _author_one(src, dst, action_name, keyposes)

    # This is #296's load-bearing authoring proof: the held wind-up pose cannot
    # regress into the held cooldown pose without Blender failing before export.
    lib.verify_pose_distinct(
        poses["genericstartup"], poses["genericrecovery"],
        POSE_DISTINCT_MIN_DEG, label="generic_startup_vs_recovery")


if __name__ == "__main__":
    main()
