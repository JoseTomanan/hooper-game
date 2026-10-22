"""Author a right-handed JumpShot over the owned jump/landing source (#418).

    blender --background --python-exit-code 1 --python tools/author_jumpshot.py -- \
        "assets/Goalkeeper Catch Stationary.fbx" "assets/jumpshot_authored.fbx"

Frames are 60 Hz physics ticks: Startup 0..18, Active 18..22, Recovery 22..42.
The source's complete lower body is retained, rather than replacing its real
jump with a hip translation. Only the shooting and guide arm chains change.
There is one conventional RIGHT-handed shot regardless of the dribble origin.

Reference authority: https://jr.nba.com/how-to-shoot-a-jump-shot/ — ball in
front of forehead, shooting hand under/guide hand beside it, extend the shooting
arm and snap the wrist, then finish the follow-through and land. The exact
shoulder-relative targets below are authored interpretations, not NBA metrics.
"""
import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import blender_anim_lib as lib  # noqa: E402

FPS = 60
STARTUP_TICKS = 18
ACTIVE_TICKS = 4
TOTAL_TICKS = 42
ACTION_NAME = "jumpshot"
EXPECTED_SOURCE = "Goalkeeper Catch Stationary.fbx"

# Same measured source landmarks as rebuild_jumpshot_clips.gd's original slice.
# They preserve the existing full-body jump cadence across the replacement.
SOURCE_LANDMARKS = ((0, 0.3143), (18, 0.8200), (22, 0.9293), (42, 1.3530))


def smoothstep(value):
    value = max(0.0, min(1.0, value))
    return value * value * (3.0 - 2.0 * value)


def source_time(tick):
    for (a_tick, a_time), (b_tick, b_time) in zip(
            SOURCE_LANDMARKS, SOURCE_LANDMARKS[1:]):
        if tick <= b_tick:
            return a_time + (b_time - a_time) * (tick - a_tick) / (b_tick - a_tick)
    return SOURCE_LANDMARKS[-1][1]


def author_arms(arm, geom, frame):
    # Arm-relative reach avoids coupling these poses to a guessed rig scale or
    # source hip height. The pocket bends the elbow; release nearly extends it.
    extension = smoothstep((frame - 5.0) / 13.0)
    snap = smoothstep((frame - 8.0) / 10.0)
    for side in ("R", "L"):
        upper, _lower, _hand = lib.ARM_CHAIN[side]
        shoulder = arm.pose.bones[upper].head.copy()
        reach = sum(lib.arm_lengths(arm, side))
        if side == "R":
            pocket = geom.up * 0.24 + geom.forward * 0.40 - geom.body_right * 0.07
            release = geom.up * 0.97 + geom.forward * 0.20 - geom.body_right * 0.03
            target = shoulder + pocket.lerp(release, extension) * reach
            # Before release, fingers point up behind the ball. At the first
            # Active tick the wrist has flexed down/forward in follow-through.
            # Spherical interpolation keeps this a unit direction throughout:
            # a linear interpolation near opposing directions concentrates the
            # wrist's turn in a tiny interval and whips the fingers at release.
            # https://docs.blender.org/api/current/mathutils.html#mathutils.Vector.slerp
            hand_dir = geom.up.slerp((geom.forward * 0.65 - geom.up).normalized(), snap)
            elbow_hint = -geom.up + geom.forward * 0.30
        else:
            # The guide hand reaches the side of the pocket, then separates:
            # it never copies the shooting arm's extended follow-through.
            pocket = geom.up * 0.26 + geom.forward * 0.43 + geom.body_right * 0.30
            release = geom.up * 0.53 + geom.forward * 0.33 - geom.body_right * 0.10
            target = shoulder + pocket.lerp(release, extension) * reach
            hand_dir = geom.up + geom.forward * 0.15
            elbow_hint = -geom.up - geom.body_right * 0.55
        # Shared IK uses armature-space PoseBone.matrix, including parent pose:
        # https://docs.blender.org/api/current/bpy.types.PoseBone.html#bpy.types.PoseBone.matrix
        lib.aim_arm(arm, side, target, elbow_hint, geom, hand_dir=hand_dir)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    if len(argv) != 2:
        raise SystemExit("usage: author_jumpshot.py <Goalkeeper Catch Stationary.fbx> <out.fbx>")
    src, dst = argv
    if os.path.basename(src) != EXPECTED_SOURCE:
        raise SystemExit(f"expected owned source {EXPECTED_SOURCE!r}, got {src!r}")
    # FBX import updates the scene rate to the source's TimeMode (30 Hz for
    # this owned asset). Capture it BEFORE switching the output to 60 Hz:
    # changing render.fps changes playback time, not existing key coordinates.
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

    # Evaluate the source BEFORE assigning a new Action. Local matrices retain
    # exact parent-relative jump/landing transforms; applying saved global
    # matrices while changing a parent can otherwise accidentally move children.
    # https://docs.blender.org/api/current/bpy.types.PoseBone.html#bpy.types.PoseBone.matrix_basis
    source = []
    for tick in range(TOTAL_TICKS + 1):
        sample = source_start + source_time(tick) * source_fps
        integer = int(sample)
        scene.frame_set(integer, subframe=sample - integer)
        source.append({pb.name: pb.matrix_basis.copy() for pb in arm.pose.bones})

    arm.animation_data.action = bpy.data.actions.new(ACTION_NAME)
    lib.enter_pose_mode(arm)
    arm_bones = [name for chain in lib.ARM_CHAIN.values() for name in chain]
    for frame, matrices in enumerate(source):
        scene.frame_set(frame)
        for pb in arm.pose.bones:
            pb.rotation_mode = "QUATERNION"
            pb.matrix_basis = matrices[pb.name]
        bpy.context.view_layer.update()
        source_rotations = {name: arm.pose.bones[name].rotation_quaternion.copy()
                            for name in arm_bones}
        author_arms(arm, geom, frame)
        # Keep original gather/landing endpoints and hold the flexed wrist and
        # asymmetric follow-through through early Recovery. PumpFake borrows
        # only eight Recovery ticks, so return to the original source arms by
        # frame 28, its last displayed pose before the neutral handoff (the
        # AnimationTree pose trails the phase label), rather than leaving a raised
        # follow-through to snap down when that shorter feint becomes inactive.
        weight = smoothstep(frame / 4.0) * (1.0 - smoothstep((frame - 25.0) / 3.0))
        for name in arm_bones:
            pb = arm.pose.bones[name]
            pb.rotation_quaternion = source_rotations[name].slerp(pb.rotation_quaternion, weight)
        bpy.context.view_layer.update()
        # Full-body keys avoid omitted tracks reverting limbs to the rest pose.
        # keyframe_insert creates animation data on the owning ID:
        # https://docs.blender.org/api/current/bpy.types.bpy_struct.html#bpy.types.bpy_struct.keyframe_insert
        for pb in arm.pose.bones:
            pb.keyframe_insert("rotation_quaternion", frame=frame)
        arm.pose.bones[lib.HIPS].keyframe_insert("location", frame=frame)

    lib.leave_pose_mode()
    scene.frame_start, scene.frame_end = 0, TOTAL_TICKS
    lib.verify_all_bones_keyed(arm, expected_count=65, allow_leaf_ends=False)
    lib.verify_pose_unscaled(arm, range(TOTAL_TICKS + 1))
    # Shared exporter bakes every frame, disables reduction and exports one take:
    # https://docs.blender.org/api/current/bpy.ops.export_scene.html#bpy.ops.export_scene.fbx
    lib.export_fbx(arm, dst, ACTION_NAME)
    print("AUTHOR_OK")


if __name__ == "__main__":
    main()
