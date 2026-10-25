"""Author the four-tick fadeaway silhouette over the normal release (#429).

Run:
    "$BLENDER" --background --python-exit-code 1 \
        --python tools/author_fadeaway.py -- \
        "assets/Goalkeeper Catch Stationary.fbx" "assets/fadeaway_authored.fbx"

The shared JumpShot family owns the gather and landing. The Active-only envelope
starts and ends on #418's normal release, with bent legs in front of the leaned
torso at its peak. These dimensions interpret the maintainer's #429 direction;
they are authored pose values, not measured basketball or feel acceptance.
All bones remain keyed to avoid full-weight AnimationTree rest-pose fallbacks.
"""
import math
import os
import sys

import bpy
from mathutils import Matrix

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import blender_anim_lib as lib  # noqa: E402
import author_jumpshot as normal  # noqa: E402

FPS = 60
ACTIVE_TICKS = 4
ACTION_NAME = "fadeaway"
EXPECTED_SOURCE = "Goalkeeper Catch Stationary.fbx"

HIP_BACK_M = 0.50
TORSO_BACK_DEG = 55.0
ANKLE_FORWARD_M = 0.35
ANKLE_DOWN_M = 0.70
ENVELOPE = (0.0, 0.8, 1.0, 0.5, 0.0)
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
    if os.path.basename(src) != EXPECTED_SOURCE:
        raise SystemExit(f"expected owned source {EXPECTED_SOURCE!r}, got {src!r}")
    # Import TimeMode is 30 Hz. Changing output FPS does not rescale source keys:
    # https://docs.blender.org/api/current/bpy.types.RenderSettings.html#bpy.types.RenderSettings.fps
    # https://docs.blender.org/api/current/bpy.ops.import_scene.html#bpy.ops.import_scene.fbx
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=src)
    arm = next(obj for obj in bpy.data.objects if obj.type == "ARMATURE")
    scene = bpy.context.scene
    source_fps = scene.render.fps / scene.render.fps_base
    source_start = arm.animation_data.action.frame_range[0]
    scene.render.fps = FPS
    scene.render.fps_base = 1.0
    print(f"SOURCE_RATE {source_fps:g} OUTPUT_RATE {FPS}")
    geom = lib.RigGeometry(arm)
    geom.log_summary()

    # Evaluate all source frames before replacing its Action. Parent-relative
    # matrices preserve the jump while changing parents during target authoring.
    # https://docs.blender.org/api/current/bpy.types.Scene.html#bpy.types.Scene.frame_set
    # https://docs.blender.org/api/current/bpy.types.PoseBone.html#bpy.types.PoseBone.matrix_basis
    source = []
    for frame in range(ACTIVE_TICKS + 1):
        sample = source_start + normal.source_time(normal.STARTUP_TICKS + frame) * source_fps
        integer = int(sample)
        scene.frame_set(integer, subframe=sample - integer)
        source.append({pb.name: pb.matrix_basis.copy() for pb in arm.pose.bones})

    arm.animation_data.action = bpy.data.actions.new(ACTION_NAME)
    lib.enter_pose_mode(arm)
    peak = None
    for frame, matrices in enumerate(source):
        scene.frame_set(frame)
        for pb in arm.pose.bones:
            pb.rotation_mode = "QUATERNION"
            pb.matrix_basis = matrices[pb.name]
        bpy.context.view_layer.update()
        normal.author_arms(arm, geom, normal.STARTUP_TICKS + frame)
        base_rotations = {pb.name: pb.rotation_quaternion.copy() for pb in arm.pose.bones}
        hips = arm.pose.bones[lib.HIPS]
        base_hip_location = hips.location.copy()
        base_hip_head = hips.head.copy()
        base_torso = _torso_forward_m(arm, geom)

        # PoseBone.matrix is armature-object space, including parent pose:
        # https://docs.blender.org/api/current/bpy.types.PoseBone.html#bpy.types.PoseBone.matrix
        hips_matrix = hips.matrix.copy()
        hips_matrix.translation -= geom.forward * geom.m(HIP_BACK_M)
        hips.matrix = hips_matrix
        bpy.context.view_layer.update()

        # Preserve the established pitch/axis sign pairing; production evaluated
        # anatomy, not this producer-side magnitude gate, proves its direction.
        lib.rotate_bone_about_head(
            arm, lib.SPINE,
            (Matrix.Rotation(math.radians(TORSO_BACK_DEG), 4, geom.body_right),))
        hip_head = hips.head.copy()
        for side in ("L", "R"):
            upper = arm.pose.bones[lib.LEG_CHAIN[side][0]]
            lateral = (upper.head - hip_head).dot(geom.body_right)
            target = (hip_head + geom.body_right * lateral
                      + geom.forward * geom.m(ANKLE_FORWARD_M)
                      - geom.up * geom.m(ANKLE_DOWN_M))
            lib.plant_foot(arm, side, target, geom.forward, geom)

        target_hip_location = hips.location.copy()
        weight = ENVELOPE[frame]
        for pb in arm.pose.bones:
            pb.rotation_quaternion = base_rotations[pb.name].slerp(pb.rotation_quaternion, weight)
        hips.location = base_hip_location.lerp(target_hip_location, weight)
        bpy.context.view_layer.update()
        if frame == 2:
            peak = (_torso_forward_m(arm, geom) - base_torso,
                    geom.to_m((base_hip_head - hips.head).dot(geom.forward)))
            for name in (lib.HIPS, lib.SPINE, "mixamorig:Neck",
                         "mixamorig:LeftLeg", "mixamorig:RightLeg",
                         "mixamorig:LeftFoot", "mixamorig:RightFoot"):
                projection = geom.to_m((arm.pose.bones[name].head - hips.head).dot(geom.forward))
                lib.report("fadeaway_peak_forward_from_hips_m_" + name, f"{projection:.6f}")
        # https://docs.blender.org/api/current/bpy.types.bpy_struct.html#bpy.types.bpy_struct.keyframe_insert
        for pb in arm.pose.bones:
            pb.keyframe_insert("rotation_quaternion", frame=frame)
        hips.keyframe_insert("location", frame=frame)

    lib.leave_pose_mode()
    scene.frame_start, scene.frame_end = 0, ACTIVE_TICKS

    # The five authored release keys include both endpoints and all leaves;
    # that is intentionally stricter than the source's 52 non-terminal tracks.
    lib.verify_all_bones_keyed(arm, expected_count=65, allow_leaf_ends=False)
    lib.verify_pose_unscaled(arm, range(ACTIVE_TICKS + 1))

    torso_back, hip_back = peak
    # These Blender-side gates protect the authored source; the paired
    # JumpshotAnimTest gates re-measure the same read on Player.tscn's live rig.
    lib.report("fadeaway_torso_delta_from_source_m", f"{torso_back:.4f}")
    lib.report("fadeaway_hips_back_from_source_m", f"{hip_back:.4f}")
    lib.report_ankle_ik(ACTION_NAME, geom)
    # This source-space delta is a magnitude-only producer-side tripwire;
    # JumpshotAnimTest and rebuild_jumpshot_clips.gd independently verify the
    # Player.tscn-space direction after Godot's FBX import basis conversion.
    if not abs(torso_back) >= MIN_TORSO_TRAVEL_M:
        raise SystemExit(f"FATAL: torso moved only {torso_back:.4f} m from the source release; "
                         f"need magnitude >= {MIN_TORSO_TRAVEL_M:.2f} m.")
    if not hip_back >= MIN_HIP_BACK_M:
        raise SystemExit(f"FATAL: hips move back only {hip_back:.4f} m, need {MIN_HIP_BACK_M:.2f} m.")

    # Full-frame baking with reduction disabled preserves the five authored keys:
    # https://docs.blender.org/api/current/bpy.ops.export_scene.html#bpy.ops.export_scene.fbx
    lib.export_fbx(arm, dst, ACTION_NAME)
    print("AUTHOR_OK")


if __name__ == "__main__":
    main()
