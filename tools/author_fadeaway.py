"""Author the four-tick off-balance fadeaway release in headless Blender (#318).

Run:
    "$BLENDER" --background --python-exit-code 1 \
        --python tools/author_fadeaway.py -- \
        "assets/Goalkeeper Catch Stationary.fbx" "assets/fadeaway_authored.fbx"

The shared JumpShot family already owns the gather and landing. This action is
only the replacement Active pose: a 28-degree backward torso pitch, a body
shift behind the feet, and the source clip's overhead shooting-arm extension.
Every non-leaf bone is still keyed, because a single AnimationTree state plays
at full weight and an omitted track falls back to the Mixamo T-pose (#a45bd1d).
"""
import math
import os
import sys

import bpy
from mathutils import Matrix

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import blender_anim_lib as lib  # noqa: E402

FPS = 60
ACTIVE_TICKS = 4
SOURCE_FRAME = 49  # peak of Goalkeeper Catch Stationary's authored extension.
ACTION_NAME = "fadeaway"
EXPECTED_SOURCE = "Goalkeeper Catch Stationary.fbx"

# The source's peak is already ~0.17 m forward of the live idle stance. Moving
# it 0.50 m back therefore lands the rendered Hips at roughly 0.30 m backward,
# the issue's off-balance release target. The live harness is the authority.
HIP_BACK_M = 0.50
TORSO_BACK_DEG = 55.0
MIN_TORSO_TRAVEL_M = 0.12
MIN_HIP_BACK_M = 0.25


def _torso_forward_m(arm, geom):
    spine = arm.pose.bones[lib.SPINE].head
    head = arm.pose.bones["mixamorig:Head"].head
    return geom.to_m((head - spine).dot(geom.forward))


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    if len(argv) != 2:
        raise SystemExit("usage: author_fadeaway.py <Goalkeeper Catch Stationary.fbx> <fadeaway_authored.fbx>")
    src, dst = argv
    arm, _f0, _f1 = lib.load_source(src, FPS, expected=EXPECTED_SOURCE)
    scene = bpy.context.scene
    geom = lib.RigGeometry(arm)
    geom.log_summary()

    # Freeze the source at its fully extended release, then copy that COMPLETE
    # pose into a fresh action. We preserve the source's shooting arm and airborne
    # legs instead of inventing a second jump-shot family for a 67-ms state.
    scene.frame_set(SOURCE_FRAME)
    matrices = {pb.name: pb.matrix.copy() for pb in arm.pose.bones}
    hips_source = arm.pose.bones[lib.HIPS].head.copy()
    source_torso = _torso_forward_m(arm, geom)

    lib.enter_pose_mode(arm)
    for frame in range(ACTIVE_TICKS + 1):
        # `matrix` is the evaluated source pose. `rotation_quaternion` alone is
        # not: FBX actions can carry their motion through a parent transform,
        # and keying that raw property would export a two-track no-op.
        for pb in arm.pose.bones:
            pb.matrix = matrices[pb.name]
        bpy.context.view_layer.update()

        hips = arm.pose.bones[lib.HIPS]
        hips_matrix = hips.matrix.copy()
        hips_matrix.translation -= geom.forward * geom.m(HIP_BACK_M)
        hips.matrix = hips_matrix
        bpy.context.view_layer.update()
        hips.keyframe_insert("location", frame=frame)

        # A pitch about the anatomy-derived body-right axis, not Blender's
        # guessed X axis. Its direction is verified below against the actual
        # forward projection; the sign is never trusted just because the script says so.
        lib.rotate_bone_about_head(
            arm, lib.SPINE,
            (Matrix.Rotation(math.radians(TORSO_BACK_DEG), 4, geom.body_right),),
            frame=frame)

        for pb in arm.pose.bones:
            pb.keyframe_insert("rotation_quaternion", frame=frame)

    bpy.ops.object.mode_set(mode="OBJECT")
    scene.frame_start, scene.frame_end = 0, ACTIVE_TICKS

    # The four authored release frames key the complete pose, including leaves;
    # that is intentionally stricter than the source's 52 non-terminal tracks.
    lib.verify_all_bones_keyed(arm, expected_count=65, allow_leaf_ends=False)
    lib.verify_pose_unscaled(arm, range(ACTIVE_TICKS + 1))

    with lib.preserve_frame():
        scene.frame_set(0)
        torso_back = _torso_forward_m(arm, geom) - source_torso
        hip_back = geom.to_m((hips_source - arm.pose.bones[lib.HIPS].head).dot(geom.forward))
    # These Blender-side gates protect the authored source; the paired
    # JumpshotAnimTest gates re-measure the same read on Player.tscn's live rig.
    lib.report("fadeaway_torso_delta_from_source_m", f"{torso_back:.4f}")
    lib.report("fadeaway_hips_back_from_source_m", f"{hip_back:.4f}")
    # This source-space delta is a magnitude-only producer-side tripwire;
    # JumpshotAnimTest and rebuild_jumpshot_clips.gd independently verify the
    # Player.tscn-space direction after Godot's FBX import basis conversion.
    if abs(torso_back) < MIN_TORSO_TRAVEL_M:
        raise SystemExit(f"FATAL: torso moved only {torso_back:.4f} m from the source release; "
                         f"need magnitude >= {MIN_TORSO_TRAVEL_M:.2f} m.")
    if hip_back < MIN_HIP_BACK_M:
        raise SystemExit(f"FATAL: hips move back only {hip_back:.4f} m, need {MIN_HIP_BACK_M:.2f} m.")

    lib.export_fbx(arm, dst, ACTION_NAME)
    print("AUTHOR_OK")


if __name__ == "__main__":
    main()
