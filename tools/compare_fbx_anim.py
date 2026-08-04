"""Compare the baked animation in two FBX files, bone by bone, frame by frame (#315).

    "$BLENDER" --background --python-exit-code 1 \
        --python tools/compare_fbx_anim.py -- <a.fbx> <b.fbx>

Exits nonzero if the two files differ beyond `--tol-deg` / `--tol-m` (default: 0,
i.e. exact). Prints `CMP_OK` when they match.

═══════════════════════════════════════════════════════════════════════════════
WHY THIS EXISTS: `cmp` AND `git diff` ARE USELESS ON AN FBX EXPORT
═══════════════════════════════════════════════════════════════════════════════
Handoff `docs/handoffs/anim-clips/00-blender-anim-lib.md` specified the
extraction's acceptance test as "re-run and prove byte-comparable output". That
test is not achievable, and the reason is worth knowing before anyone tries it
again.

MEASURED, 2026-07-29 (Blender 5.2.0): two runs of ONE UNCHANGED authoring script
produce FBX files that differ in 12598 bytes, while their poses are bit-identical
on all 4160 (frame,bone) pairs. Blender's exporter derives FBX object UUIDs from
`hash(key)` (`io_scene_fbx/fbx_utils.py:_key_to_uuid`, which carries its own
"TODO: Check this is robust enough for our needs!"), and those vary per process.
`PYTHONHASHSEED=0` does NOT fix it.

So byte equality reports a difference that does not exist. This tool measures the
thing the clip contract actually cares about -- the POSE -- in armature space,
and it is exact-zero for an unchanged script. That makes it a usable gate:

  refactor with no behaviour change   ->  0.000000 deg / 0.00000000 m
  STRIDE_LENGTH_M 0.60 -> 0.61 (1 cm)  ->  0.696609 deg / 0.00514797 m

i.e. it is sensitive to a 1 cm spec change at well below this project's own
0.5 deg LOOP_SEAM_TOLERANCE_DEG, and silent on encoding noise.

═══════════════════════════════════════════════════════════════════════════════
READING THE OUTPUT
═══════════════════════════════════════════════════════════════════════════════
It reports both the WORST delta and HOW MANY (frame,bone) pairs differ, because
those distinguish two very different situations that one number cannot:

  - rotations bit-identical, locations differing by <1 um and GROWING with depth
    down the kinematic chain (Hips smallest, fingertips largest) is float32
    quantization accumulating along the chain -- not a behaviour change;
  - a handful of pairs differing by a large amount is a real, localised change.
"""
import sys

import bpy


def collect(path):
    """Per-frame, per-bone armature-space pose matrices for the armature in `path`."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=path)
    arm = next(o for o in bpy.data.objects if o.type == "ARMATURE")
    scene = bpy.context.scene
    act = arm.animation_data.action
    f0, f1 = (int(v) for v in act.frame_range)

    # Armature space, deliberately: `pose_bone.matrix` is armature-space, and
    # `arm.matrix_world` carries Mixamo's 0.01 object scale. Only the reported
    # metre figures are converted, via this factor.
    upm = 1.0 / arm.matrix_world.to_scale().x
    frames = {}
    for f in range(f0, f1 + 1):
        scene.frame_set(f)
        frames[f] = {pb.name: pb.matrix.copy() for pb in arm.pose.bones}
    return {"f0": f0, "f1": f1, "names": sorted(frames[f0]), "frames": frames,
            "upm": upm, "action": act.name}


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    a_path, b_path = argv[0], argv[1]
    tol_deg = float(next((v.split("=", 1)[1] for v in argv
                          if v.startswith("--tol-deg=")), 0.0))
    tol_m = float(next((v.split("=", 1)[1] for v in argv
                        if v.startswith("--tol-m=")), 0.0))

    a, b = collect(a_path), collect(b_path)
    print(f"[cmp] A action={a['action']!r} frames {a['f0']}..{a['f1']} "
          f"bones={len(a['names'])}")
    print(f"[cmp] B action={b['action']!r} frames {b['f0']}..{b['f1']} "
          f"bones={len(b['names'])}")

    # Structural mismatch first: a pose comparison between different bone sets or
    # frame ranges is meaningless, so fail loudly rather than report a number.
    problems = []
    if a["names"] != b["names"]:
        problems.append(
            f"bone sets differ: only-A={sorted(set(a['names']) - set(b['names']))} "
            f"only-B={sorted(set(b['names']) - set(a['names']))}")
    if (a["f0"], a["f1"]) != (b["f0"], b["f1"]):
        problems.append(
            f"frame ranges differ: {a['f0']}..{a['f1']} vs {b['f0']}..{b['f1']}")
    if problems:
        for p in problems:
            print(f"[cmp] STRUCTURAL: {p}")
        raise SystemExit("FATAL: structural mismatch; pose comparison not meaningful")

    upm = a["upm"]
    worst_rot = (0.0, None, None)
    worst_loc = (0.0, None, None)
    n_pairs = n_rot = n_loc = 0
    per_bone = {}
    # Per-bone ROTATION spread, alongside the location one. Added in #321,
    # because ATTRIBUTING a non-zero diff -- not merely detecting one -- is what
    # this tool is actually used for, and the two channels attribute to different
    # causes. A location-only list conflates them: a rotated PARENT displaces
    # every descendant's head, so the leg chain shows location deltas all the way
    # down to `Toe_End` while only the three IK-posed bones per side actually
    # rotated. Reading "10 bones moved" without "which 4 rotated" invites
    # exactly the wrong conclusion (that the toes were re-posed, when they were
    # only carried).
    per_bone_rot = {}
    for f in range(a["f0"], a["f1"] + 1):
        for bone in a["names"]:
            ma, mb = a["frames"][f][bone], b["frames"][f][bone]
            n_pairs += 1

            qa, qb = ma.to_quaternion(), mb.to_quaternion()
            deg = abs(qa.rotation_difference(qb).angle) * 57.29577951308232
            if deg > 180.0:  # quaternion double cover: q and -q are one rotation
                deg = 360.0 - deg
            if deg > worst_rot[0]:
                worst_rot = (deg, f, bone)
            if deg != 0.0:
                n_rot += 1
                per_bone_rot[bone] = max(per_bone_rot.get(bone, 0.0), deg)

            dm = (ma.translation - mb.translation).length / upm
            if dm > worst_loc[0]:
                worst_loc = (dm, f, bone)
            if dm != 0.0:
                n_loc += 1
                per_bone[bone] = max(per_bone.get(bone, 0.0), dm)

    print(f"[cmp] compared {n_pairs} (frame,bone) pairs")
    print(f"[cmp] worst rotation delta = {worst_rot[0]:.6f} deg "
          f"(frame {worst_rot[1]}, {worst_rot[2]})")
    print(f"[cmp] worst location delta = {worst_loc[0]:.8f} m "
          f"(frame {worst_loc[1]}, {worst_loc[2]})")
    print(f"[cmp] pairs with nonzero rotation delta = {n_rot}")
    print(f"[cmp] pairs with nonzero location delta = {n_loc}")
    # Rotation first: it names the bones the authoring actually RE-POSED, which
    # is the set an attribution argument has to account for. The location list
    # below is the larger, derived set (re-posed bones plus everything hanging
    # off them).
    for bone, dg in sorted(per_bone_rot.items(), key=lambda kv: -kv[1])[:12]:
        print(f"[cmp]   rot-differing bone: {bone} max={dg:.6f} deg")
    for bone, dm in sorted(per_bone.items(), key=lambda kv: -kv[1])[:12]:
        print(f"[cmp]   loc-differing bone: {bone} max={dm:.10f} m")

    if worst_rot[0] > tol_deg or worst_loc[0] > tol_m:
        raise SystemExit(
            f"FATAL: clips differ beyond tolerance "
            f"(rot {worst_rot[0]:.6f} > {tol_deg} deg, or "
            f"loc {worst_loc[0]:.8f} > {tol_m} m)")
    print("CMP_OK")


main()
