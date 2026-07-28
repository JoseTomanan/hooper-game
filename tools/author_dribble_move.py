"""Author `dribblemove` as a keyframed drive-dribble cycle in headless Blender (#300).

Run:
    "$BLENDER" --background --python-exit-code 1 \
        --python tools/author_dribble_move.py -- <src.fbx> <out.fbx>

`--python-exit-code 1` is MANDATORY. Blender's `--background --python` exits 0
even on an uncaught traceback, so without it a crashed authoring run reports
success to the pipeline.

═══════════════════════════════════════════════════════════════════════════════
WHY THIS EXISTS — superseding #298's delta transplant
═══════════════════════════════════════════════════════════════════════════════
#298 fixed the frozen-legs defect (0.0047 m toe excursion) by transplanting
`run`'s leg motion onto the dribble stance as a world-frame delta. It passed
every automated gate, but it reads as a scaled-down sprint, because the delta
transplant inherits its motion CHARACTER wholesale from a sprint clip.
Amplitude scaling changes how big the sprint is, never that it is a sprint.

The human's direction (#300): author new keyframed motion in headless Blender,
NBA 2K's drive-dribble as the reference. NBA 2K is a human-directed reference
override for this clip -- ADR-0014 ranks real ball > Undisputed 3 > 2K, and the
human has explicitly named 2K here. Do not relitigate it; cite #300.

`dribbleidle` is NOT touched by this script. Only `dribblemove` routes through
Blender. That matters concretely: the measured Blender round-trip error is
0.396 deg, which is below LOOP_SEAM_TOLERANCE_DEG (0.5) but NOT zero, so a clip
required to be verbatim must never make this trip.

═══════════════════════════════════════════════════════════════════════════════
THE METHOD — specify the foot trajectory, then solve the leg
═══════════════════════════════════════════════════════════════════════════════
The one mechanism #298 proved sound is composing a WORLD-FRAME orientation onto
the dribble chain with `Hips` pinned: a world-frame construction carries no
information about which retarget family a clip's rest is expressed against, so
it cannot import `run`'s (or Kenney's) convention into the stock-Mixamo dribble
clip. locomotion.res holds two families ~155-180 deg apart and mixing them is
the #287 degeneracy. This script keeps that mechanism and changes only where
the orientation comes from: a gait function we specify, not a sprint clip.

Crucially it does NOT specify joint ANGLES. Measured rest geometry:

    femur 0.4060 m + tibia 0.4210 m  ->  hip-to-ankle reach 0.8270 m
    animated stance holds the toe    ->  ~0.70 m below the hips

A leg swinging fore/aft from a fixed hip is a pendulum: it LIFTS the foot at
the stride extremes unless the knee compensates. At +-0.35 m of stride the
hip-to-ankle distance is sqrt(0.70^2 + 0.35^2) = 0.783 m against a 0.827 m
limit -- near full extension. That is the analytic explanation of #298's
empirically-discovered grounding cliff (both feet ~0.5 m airborne above
amplitude ~0.51, caught only because PROOF 6 was added after the fact).

So we invert it: define the FOOT TRAJECTORY -- which is what a gait actually
is -- and solve hip + knee by two-link IK (law of cosines). Grounding then
holds BY CONSTRUCTION rather than being discovered by a proof gate.

A second benefit falls out for free. The source clip bakes in a +0.6881 m
STATIC fore/aft stagger (left toe permanently ahead of right; measured
independently in Blender, agreeing with #298's Godot-side figure). #298 had to
cancel it with `C_leg`, a per-leg rotation solved by bisection. Authoring
ABSOLUTE foot positions overwrites the stagger outright, so the whole
`_solve_stance_correction` / `CENTRE_STANCE_STAGGER` machinery becomes
unnecessary rather than merely retuned.

═══════════════════════════════════════════════════════════════════════════════
THE #287 CORRIDOR IS A BUDGET, NOT A CLIFF
═══════════════════════════════════════════════════════════════════════════════
`BlendRestAnchor` pins both UpLeg RESTS to `idle`'s frame-0 key, and there is
one rest per bone shared with the Dribble blendspace. So any UpLeg difference
between the two dribble endpoints risks #287 mixer degeneracy at partial blend
weights. Measured on #298's construction: amplitude 0.50 -> 0/90 frames
violated (69.1 deg endpoint gap); 0.70 -> 1/90 (85.2 deg).

Here the UpLeg excursion is a PARAMETER (STRIDE_LENGTH_M), so the budget is
spent deliberately. If the corridor goes red, reduce stride -- do NOT attempt
to fix it by making the endpoints geometrically more similar. That was measured
and falsified in #298: narrowing the gap made it strictly WORSE (1/90 -> 2/90,
excess 38.1 -> 77.6 deg), which is #287's documented signature.

═══════════════════════════════════════════════════════════════════════════════
CADENCE — locked to the ball, not chosen
═══════════════════════════════════════════════════════════════════════════════
The source clip's own right-hand vertical oscillation measures 3 dribble
bounces per 2.100 s loop, and the human confirmed the target as ~1 bounce per
2 steps. So one gait cycle = 0.700 s = one bounce, 3 cycles per loop. The ball's
bounce timing is driven separately in-engine; the stride MUST agree with it or
the footfalls visibly desync from the ball.
"""
import math
import sys

import bpy
from mathutils import Matrix, Vector

# ── clip contract (see #300 / rebuild_dribble_clips.gd) ──────────────────────
FPS = 30
CLIP_LENGTH_S = 2.100
# 3 gait cycles x 0.700 s. Measured from the source clip's bounce count, not
# assumed -- _verify_cadence() below re-measures and refuses to continue if the
# source ever changes.
GAIT_CYCLES_PER_LOOP = 3
CYCLE_S = CLIP_LENGTH_S / GAIT_CYCLES_PER_LOOP

# ── the drive-dribble motion spec (NBA 2K reference, #300) ───────────────────
# Total fore/aft foot travel per cycle. A drive dribble is a controlled,
# lower, choppier gait than a sprint: `run` measures ~1.39 m ptp and #298's
# transplant landed 0.73/0.55. 0.60 m sits clearly above PROOF 1's 0.15 m gate
# and clearly below a sprint, and keeps hip-to-ankle at
# sqrt(0.70^2 + 0.30^2) = 0.762 m -- comfortably inside the 0.827 m reach with
# the knee still bent at the extremes.
STRIDE_LENGTH_M = 0.60

# Ankle height above the ground plane while the foot is planted. Small and
# non-zero: the toe, not the ankle, is what contacts.
ANKLE_GROUND_CLEARANCE_M = 0.075
# Peak ankle lift during swing. Drives the knee-lift read; too little looks
# like shuffling, too much looks like a sprint's high knee action.
SWING_FOOT_LIFT_M = 0.13
# Fraction of the cycle each foot spends planted. Running gaits are <50% (a
# flight phase, both feet airborne); walking is >50%. A drive dribble keeps
# ground contact -- 0.62 gives a double-support overlap, which is also what
# keeps PROOF 6's support band tight.
STANCE_FRACTION = 0.62

# How far below the hips the ankle sits at mid-stance, before the crouch. From
# the measured animated stance (toe ~0.70 m below hips, ankle a little above).
HIP_TO_ANKLE_NEUTRAL_M = 0.655
# Extra hip lowering for the drive posture (#286: a lean alone reads as
# "leaning", pairing it with a lower stance is what reads as "moving").
# Matches the 0.12 m Hips POSITION crouch #298 shipped.
CROUCH_DROP_M = 0.12
# Vertical hip oscillation, twice per gait cycle (once per step) -- the classic
# gait bob. Also does real work for grounding: dropping the hips during stance
# reduces how far the leg must extend to keep the foot down.
HIP_BOB_M = 0.025

# Lateral foot separation, per side, from the body midline. Slightly wider than
# the 0.1825 m rest hip width -- a drive stance is not a catwalk.
STANCE_HALF_WIDTH_M = 0.115

# Forward torso lean. INHERITED from #298/#286 at 38 deg, deliberately not
# retuned here: the human's complaint was scoped to the legs reading as a
# sprint, and 38 deg was itself a measured legibility fix (#286 raised it from
# 20, which read as a standing player).
LEAN_DEGREES = 38.0
LEAN_BONE = "mixamorig:Spine"
# Torso counter-rotation against the legs, peak degrees. Real gait swings the
# shoulders opposite the pelvis. Kept small: this bone carries the ball hand,
# and a large twist would move it far enough to need a big HandOffset change.
COUNTER_ROTATION_DEG = 5.0

HIPS = "mixamorig:Hips"
LEG_CHAIN = {
    "L": ("mixamorig:LeftUpLeg", "mixamorig:LeftLeg",
          "mixamorig:LeftFoot", "mixamorig:LeftToeBase"),
    "R": ("mixamorig:RightUpLeg", "mixamorig:RightLeg",
          "mixamorig:RightFoot", "mixamorig:RightToeBase"),
}
# Left leg leads; right is half a cycle out of phase.
PHASE_OFFSET = {"L": 0.0, "R": 0.5}

# Godot names the imported clip after the Blender action. The rebuild tool
# looks the clip up by name, so this is a contract, not cosmetic.
ACTION_NAME = "dribblemove"


def log(msg):
    print(f"[author] {msg}")


# ═════════════════════════════════════════════════════════════════════════════
# rig geometry
# ═════════════════════════════════════════════════════════════════════════════
def derive_axes(arm):
    """Right/up/forward in ARMATURE space, from the REST pose.

    Everything in this script works in armature space, deliberately. Blender's
    `pose_bone.matrix` and `pose_bone.head` are armature-space, while
    `arm.matrix_world @ p` is world-space and carries Mixamo's 0.01 cm->m
    object scale. Straddling the two is a silent 100x error -- and an
    asymmetric one: a child bone's head is recomputed from its parent, so a
    bad translation is absorbed and only the rotation survives, whereas on the
    ROOT bone (Hips) the translation IS the edit and it vanishes without a
    trace. Measured that the hard way: the legs strode correctly while the
    crouch track came back with range exactly (0,0,0).

    Derived, never hardcoded: Mixamo rest rolls are arbitrary. Read from the
    RAW imported FBX -- never from a Player.tscn rig, where BlendRestAnchor
    rotates both UpLeg rests at _Ready and every foot/toe global rest inherits
    the error (119.6 deg; cost a 2.17x stride mismeasurement in #298).
    """
    rest = arm.data.bones
    l_hip = rest["mixamorig:LeftUpLeg"].head_local
    r_hip = rest["mixamorig:RightUpLeg"].head_local
    hips = rest[HIPS].head_local
    head = rest["mixamorig:Head"].head_local

    right = (r_hip - l_hip).normalized()
    up = (head - hips).normalized()
    forward = right.cross(up).normalized()
    right = up.cross(forward).normalized()

    # Sign check against anatomy rather than assumption: the toe is ahead of
    # the ankle on a human.
    toe = rest["mixamorig:LeftToeBase"].head_local
    ankle = rest["mixamorig:LeftFoot"].head_local
    if (toe - ankle).dot(forward) < 0:
        forward, right = -forward, -right
    return right, up, forward


def units_per_metre(arm):
    """Armature units per metre.

    A Mixamo FBX is centimetre-scale and Blender puts 0.01 on the object, so
    `bone.length` reads 40.5994 for a femur that is 0.4060 m. Since this script
    works in armature space, every metre-denominated constant in the spec is
    converted through this once, at the top of main().
    """
    return 1.0 / arm.matrix_world.to_scale().x


def bone_lengths(arm):
    """Femur / tibia / foot lengths in ARMATURE UNITS (not metres)."""
    b = arm.data.bones
    return (b["mixamorig:LeftUpLeg"].length,
            b["mixamorig:LeftLeg"].length,
            b["mixamorig:LeftFoot"].length)


# ═════════════════════════════════════════════════════════════════════════════
# the gait function
# ═════════════════════════════════════════════════════════════════════════════
def foot_target(phase, stride, ground_y, lift):
    """Ankle position for one foot at `phase` in [0,1), as (fore, up) offsets.

    Stance: the foot is PLANTED, so it tracks backward relative to the body at
    a constant rate -- the body is what moves forward. Linear, because a planted
    foot does not accelerate relative to the ground.

    Swing: the foot lifts and swings forward. A raised-cosine gives zero
    fore/aft velocity at both ends, so the foot is not sliding at the moment it
    touches down -- the visual tell of a skating character. The lift uses
    sin(pi*s), which is zero at both ends and peaks mid-swing.
    """
    half = stride * 0.5
    if phase < STANCE_FRACTION:
        s = phase / STANCE_FRACTION
        return half - stride * s, ground_y
    s = (phase - STANCE_FRACTION) / (1.0 - STANCE_FRACTION)
    fore = -half + stride * (0.5 - 0.5 * math.cos(math.pi * s))
    return fore, ground_y + lift * math.sin(math.pi * s)


def hip_bob_factor(phase):
    """Unitless [0,1] hip-lowering factor. Two dips per cycle -- one per step.

    Returned unitless so the caller scales it in whatever space it is working
    in; mixing a metre-denominated return into armature-space arithmetic is
    the exact error this script already paid for once.
    """
    return abs(math.sin(math.pi * phase * 2.0))


def solve_two_link(target, l1, l2):
    """Planar 2-link IK. Returns (knee_pos_factor_unused, hip_dir, knee_angle).

    Standard law-of-cosines solve. Returns the interior knee angle and the
    angle between the femur and the hip->ankle line, from which both bone
    directions follow. The knee is forced to bend FORWARD (a human knee has one
    hinge direction) by the caller's choice of bend axis.
    """
    d = target.length
    reach = l1 + l2
    if d > reach * 0.999:
        # Clamp rather than produce NaN from acos(>1). This should not fire
        # given the spec above; if it does, the stride/height combination is
        # geometrically impossible and that is worth knowing loudly.
        log(f"WARNING: IK target {d:.4f} m exceeds reach {reach:.4f} m -- clamping")
        d = reach * 0.999
    cos_knee = (l1 * l1 + l2 * l2 - d * d) / (2.0 * l1 * l2)
    knee_interior = math.acos(max(-1.0, min(1.0, cos_knee)))
    cos_hip = (l1 * l1 + d * d - l2 * l2) / (2.0 * l1 * d)
    hip_offset = math.acos(max(-1.0, min(1.0, cos_hip)))
    return d, hip_offset, knee_interior


def aim_matrix(head, tail_dir, side_axis):
    """Armature-space matrix aiming the bone's local +Y along `tail_dir`.

    Blender bones point along their local +Y. Building the basis by
    Gram-Schmidt against the rig's own left-right axis sidesteps Mixamo's
    arbitrary rest roll entirely -- we never need to know what the rest roll
    was, which is what makes this robust across bones.

    Unit basis, no scale: a scaled basis would stretch the bone rather than
    just orient it, and the FBX round-trip would carry that into Godot as a
    SCALE_3D track the clip contract does not expect.
    """
    y = tail_dir.normalized()
    x = (side_axis - y * side_axis.dot(y))
    if x.length < 1e-6:
        # Degenerate only if the bone points along the side axis, which no leg
        # bone does; fall back to any perpendicular rather than emit NaN.
        x = Vector((1.0, 0.0, 0.0)) - y * y.x
    x.normalize()
    z = x.cross(y).normalized()
    return Matrix((
        (x.x, y.x, z.x, head.x),
        (x.y, y.y, z.y, head.y),
        (x.z, y.z, z.z, head.z),
        (0.0, 0.0, 0.0, 1.0),
    ))


# ═════════════════════════════════════════════════════════════════════════════
# main
# ═════════════════════════════════════════════════════════════════════════════
def verify_cadence(arm, f0, f1, up):
    """Re-measure the source clip's bounce count; refuse to author on a change.

    GAIT_CYCLES_PER_LOOP is derived from this. If someone swaps the source FBX
    for a clip with different cadence, silently keeping 3 would desync the
    stride from the ball -- so this fails loud instead.
    """
    vals = []
    for f in range(f0, f1 + 1):
        bpy.context.scene.frame_set(f)
        vals.append((arm.pose.bones["mixamorig:RightHand"].head
                     - arm.pose.bones[HIPS].head).dot(up))
    span = max(vals) - min(vals)
    lo, hi = min(vals) + 0.2 * span, max(vals) - 0.2 * span
    bounces, state = 0, ("high" if vals[0] > hi else "low")
    for v in vals:
        if state == "high" and v < lo:
            state, bounces = "low", bounces + 1
        elif state == "low" and v > hi:
            state = "high"
    log(f"source cadence: {bounces} bounces / loop (hand span {span:.4f} m)")
    if bounces != GAIT_CYCLES_PER_LOOP:
        raise SystemExit(
            f"FATAL: source clip has {bounces} bounces but GAIT_CYCLES_PER_LOOP "
            f"is {GAIT_CYCLES_PER_LOOP}. The stride would desync from the ball. "
            f"Re-derive the cadence before authoring.")


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    src, dst = argv[0], argv[1]

    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=src)
    arm = next(o for o in bpy.data.objects if o.type == "ARMATURE")
    scene = bpy.context.scene
    scene.render.fps = FPS

    act = arm.animation_data.action
    f0, f1 = (int(v) for v in act.frame_range)
    scene.frame_start, scene.frame_end = f0, f1
    n_frames = f1 - f0 + 1
    log(f"source action {act.name!r} frames {f0}..{f1} ({n_frames} frames)")

    right, up, forward = derive_axes(arm)
    l1, l2, lfoot = bone_lengths(arm)
    U = units_per_metre(arm)
    log(f"axes right={tuple(round(v,4) for v in right)} "
        f"up={tuple(round(v,4) for v in up)} fwd={tuple(round(v,4) for v in forward)}")
    log(f"units/metre={U:.1f}  femur={l1/U:.4f} tibia={l2/U:.4f} "
        f"foot={lfoot/U:.4f} reach={(l1+l2)/U:.4f} m")

    verify_cadence(arm, f0, f1, up)

    # Spec constants are metre-denominated for readability; convert once, here,
    # so nothing downstream has to remember which space it is in.
    stride_u = STRIDE_LENGTH_M * U
    lift_u = SWING_FOOT_LIFT_M * U
    neutral_u = HIP_TO_ANKLE_NEUTRAL_M * U
    crouch_u = CROUCH_DROP_M * U
    bob_u = HIP_BOB_M * U
    half_width_u = STANCE_HALF_WIDTH_M * U

    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode="POSE")
    lean_q = Matrix.Rotation(math.radians(LEAN_DEGREES), 4, right)

    for i, f in enumerate(range(f0, f1 + 1)):
        scene.frame_set(f)
        t = i / FPS
        phase_base = (t / CYCLE_S) % 1.0

        # ---- Hips: crouch + gait bob, keyed as a POSITION offset ------------
        # Applied as a DELTA on the clip's own root motion, not an absolute
        # position, so whatever the source clip does vertically is preserved
        # and merely lowered.
        #
        # Keep the Hips ROTATION untouched. It is the one bone the two rotation
        # families disagree on catastrophically (~158 deg), and every leg
        # solve below hangs off it.
        pb_hips = arm.pose.bones[HIPS]
        drop = crouch_u + bob_u * hip_bob_factor(phase_base)
        mh = pb_hips.matrix.copy()
        mh.translation = mh.translation - up * drop
        pb_hips.matrix = mh
        bpy.context.view_layer.update()
        pb_hips.keyframe_insert("location", frame=f)

        hips_now = pb_hips.head.copy()

        # ---- torso lean + counter-rotation ----------------------------------
        pb_spine = arm.pose.bones[LEAN_BONE]
        twist = math.radians(COUNTER_ROTATION_DEG) * math.sin(2.0 * math.pi * phase_base)
        spine_head = pb_spine.head.copy()
        rot = (Matrix.Translation(spine_head)
               @ Matrix.Rotation(twist, 4, up)
               @ lean_q
               @ Matrix.Translation(-spine_head))
        pb_spine.matrix = rot @ pb_spine.matrix
        bpy.context.view_layer.update()
        pb_spine.keyframe_insert("rotation_quaternion", frame=f)

        # ---- legs: foot trajectory -> two-link IK ---------------------------
        for side, (up_leg, leg, foot_b, toe_b) in LEG_CHAIN.items():
            phase = (phase_base + PHASE_OFFSET[side]) % 1.0
            sign = -1.0 if side == "L" else 1.0

            hip_head = arm.pose.bones[up_leg].head.copy()
            fore, vert = foot_target(phase, stride_u, -neutral_u, lift_u)

            ankle = (hips_now
                     + forward * fore
                     + up * vert
                     + right * (sign * half_width_u))
            to_ankle = ankle - hip_head
            d, hip_offset, knee_interior = solve_two_link(to_ankle, l1, l2)

            # Rotate the hip->ankle direction by `hip_offset` about the rig's
            # right axis to get the femur direction. Positive sense puts the
            # knee AHEAD of the hip->ankle line, which is the only way a human
            # knee bends.
            dir_ankle = to_ankle.normalized()
            femur_dir = Matrix.Rotation(-hip_offset, 4, right) @ dir_ankle
            knee = hip_head + femur_dir * l1
            tibia_dir = (ankle - knee).normalized()

            arm.pose.bones[up_leg].matrix = aim_matrix(hip_head, femur_dir, right)
            bpy.context.view_layer.update()
            # Re-read the knee head AFTER the femur is posed: it is the femur's
            # tail, so reading it before would aim the tibia from a stale
            # position and quietly break the IK chain.
            knee_head = arm.pose.bones[leg].head.copy()
            arm.pose.bones[leg].matrix = aim_matrix(knee_head, (ankle - knee_head), right)
            bpy.context.view_layer.update()

            # Foot: keep the sole roughly parallel to the ground during stance,
            # and toe-down through swing so the step reads as a real footfall
            # rather than a flat-footed slide.
            ankle_head = arm.pose.bones[foot_b].head.copy()
            if phase < STANCE_FRACTION:
                toe_dir = (forward * 0.90 - up * 0.44).normalized()
            else:
                s = (phase - STANCE_FRACTION) / (1.0 - STANCE_FRACTION)
                pitch = math.sin(math.pi * s)
                toe_dir = (forward * 0.90 - up * (0.44 - 0.34 * pitch)).normalized()
            arm.pose.bones[foot_b].matrix = aim_matrix(ankle_head, toe_dir, right)
            bpy.context.view_layer.update()

            for bn in (up_leg, leg, foot_b):
                arm.pose.bones[bn].keyframe_insert("rotation_quaternion", frame=f)

    bpy.ops.object.mode_set(mode="OBJECT")

    # Godot names the imported clip after the FBX animation TAKE, and with
    # bake_anim_use_all_actions=False Blender names that take after the SCENE,
    # not the action -- measured: renaming only the action still imported as
    # "Scene". The rebuild tool looks the clip up by name, so rename both.
    arm.animation_data.action.name = ACTION_NAME
    scene.name = ACTION_NAME
    log(f"action + scene renamed -> {ACTION_NAME!r}")

    bpy.ops.export_scene.fbx(
        filepath=dst,
        use_selection=False,
        object_types={"ARMATURE"},
        # Leaf bones would arrive in Godot carrying no clip keys, which is the
        # a45bd1d rest-fallback T-pose trap wearing a new hat.
        add_leaf_bones=False,
        bake_anim=True,
        bake_anim_use_all_actions=False,
        bake_anim_use_nla_strips=False,
        # Any simplification would resample the 63-key / 2.100 s grid the
        # rebuild tool's loop-seam proof depends on.
        bake_anim_simplify_factor=0.0,
        bake_anim_step=1.0,
    )
    log(f"exported -> {dst}")
    print("AUTHOR_OK")


main()
