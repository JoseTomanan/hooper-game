"""Shared headless-Blender authoring machinery for the per-move animation clips (#315).

This is an EXTRACTION, not a rewrite. `tools/author_dribble_move.py` (#300) is a
working, measured, proven authoring script -- it survived a 0.396 deg worst-case
round-trip measurement against the Godot side. Its machinery is lifted here so
the twenty clip handoffs in `docs/handoffs/anim-clips/` import it instead of
copying it. The per-move *spec* stays with the per-move script.

Human decision, 2026-07-29: shared module + thin per-move spec, not twenty
standalone copies. Rationale: the armature-vs-world-space 100x error and the
slotted-Action API break are MACHINERY traps -- paid once here, or twenty times
in copies.

═══════════════════════════════════════════════════════════════════════════════
INVOCATION -- and the one flag that is not optional
═══════════════════════════════════════════════════════════════════════════════
    "$BLENDER" --background --python-exit-code 1 \
        --python tools/author_<move>.py -- <src.fbx> <out.fbx>

`--python-exit-code 1` is MANDATORY. Blender's `--background --python` exits 0
even on an uncaught traceback, so without it a crashed authoring run reports
SUCCESS to the pipeline. Every `verify_*` helper below raises `SystemExit`
rather than logging a warning specifically so that this flag converts a failed
proof into a failed build.

═══════════════════════════════════════════════════════════════════════════════
EVERYTHING IS ARMATURE SPACE (the trap that costs 100x)
═══════════════════════════════════════════════════════════════════════════════
`pose_bone.matrix` and `pose_bone.head` are ARMATURE-space. `arm.matrix_world @ p`
is WORLD-space and carries Mixamo's 0.01 cm->m object scale. Straddling the two
is a silent 100x error, and an ASYMMETRIC one: a child bone's head is recomputed
from its parent, so a bad translation is absorbed and only the rotation survives
-- but on the ROOT bone (Hips) the translation IS the edit and it vanishes with
no trace. #300 measured exactly that: the legs strode correctly while the crouch
track came back with range (0,0,0).

The guard is `RigGeometry.m()`. Author your spec constants in metres, convert
through `geom.m(x)` once, and never hand-roll the factor.

═══════════════════════════════════════════════════════════════════════════════
BYTE-REPRODUCIBILITY: the FBX export is NOT byte-stable, and that is fine
═══════════════════════════════════════════════════════════════════════════════
Measured 2026-07-29: two runs of one UNCHANGED authoring script produce FBX
files that differ in 12598 bytes. This is not float noise -- the poses are
bit-identical. Blender's exporter derives FBX object UUIDs from `hash(key)`
(`io_scene_fbx/fbx_utils.py:_key_to_uuid`, which carries its own
"TODO: Check this is robust enough for our needs!"), and those vary per process.
`PYTHONHASHSEED=0` does not fix it.

So NEVER gate an authoring change on `cmp`/`git diff` of the FBX. Compare POSES
instead -- see `tools/compare_fbx_anim.py`, which measures per-frame per-bone
rotation and translation deltas and is exact-zero for an unchanged script.
"""
import contextlib
import math

import bpy
from mathutils import Matrix, Vector

RAD_TO_DEG = 57.29577951308232

# ── rig bone names (Mixamo/Y Bot, as they arrive from an FBX import) ──────────
# NOTE: colons here. Godot's importer rewrites these to `mixamorig_<Name>`
# (underscore) on its side -- do not use the underscore form in Blender.
HIPS = "mixamorig:Hips"
SPINE = "mixamorig:Spine"

LEG_CHAIN = {
    "L": ("mixamorig:LeftUpLeg", "mixamorig:LeftLeg",
          "mixamorig:LeftFoot", "mixamorig:LeftToeBase"),
    "R": ("mixamorig:RightUpLeg", "mixamorig:RightLeg",
          "mixamorig:RightFoot", "mixamorig:RightToeBase"),
}
# `<side>Arm` is the humerus and `<side>ForeArm` the ulna. `<side>Shoulder` is
# the CLAVICLE and is deliberately not in this chain: pose it only for a genuine
# shrug/protraction, and re-read every downstream head afterwards, because it
# moves the whole chain's root.
ARM_CHAIN = {
    "L": ("mixamorig:LeftArm", "mixamorig:LeftForeArm", "mixamorig:LeftHand"),
    "R": ("mixamorig:RightArm", "mixamorig:RightForeArm", "mixamorig:RightHand"),
}

# Terminator bones a Mixamo source clip legitimately leaves unkeyed. Measured on
# `assets/Dribble.fbx`: 52 of 65 bones are keyed, and the 13 that are not are
# exactly these. They are chain ends with nothing hanging off them, so the
# README trap-1 rest-fallback does not produce a visible T-pose through them --
# unlike, say, an unkeyed forearm. `verify_all_bones_keyed` exempts them by
# default; pass `allow_leaf_ends=False` if a move genuinely keys the fingers.
LEAF_END_BONES = frozenset({
    "mixamorig:HeadTop_End",
    "mixamorig:LeftToe_End", "mixamorig:RightToe_End",
    "mixamorig:LeftHandThumb4", "mixamorig:LeftHandIndex4",
    "mixamorig:LeftHandMiddle4", "mixamorig:LeftHandRing4",
    "mixamorig:LeftHandPinky4",
    "mixamorig:RightHandThumb4", "mixamorig:RightHandIndex4",
    "mixamorig:RightHandMiddle4", "mixamorig:RightHandRing4",
    "mixamorig:RightHandPinky4",
})


def log(msg):
    print(f"[author] {msg}")


def report(name, value):
    """One machine-greppable measurement line.

    The point is that a PR can paste REAL NUMBERS instead of claiming greenness.
    Keep the name stable across runs so a reviewer can diff two logs.
    """
    print(f"[author] {name}={value}")


# ═════════════════════════════════════════════════════════════════════════════
# rig geometry
# ═════════════════════════════════════════════════════════════════════════════
def derive_axes(arm):
    """Right/up/forward in ARMATURE space, from the REST pose.

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
    `bone.length` reads 40.5994 for a femur that is 0.4060 m. Since everything
    here works in armature space, every metre-denominated spec constant is
    converted through this once.
    """
    return 1.0 / arm.matrix_world.to_scale().x


def bone_lengths(arm):
    """Femur / tibia / foot lengths in ARMATURE UNITS (not metres)."""
    b = arm.data.bones
    return (b["mixamorig:LeftUpLeg"].length,
            b["mixamorig:LeftLeg"].length,
            b["mixamorig:LeftFoot"].length)


def arm_lengths(arm, side):
    """Humerus / ulna lengths in ARMATURE UNITS for `side` in {"L","R"}.

    Measured, not assumed symmetric with the leg: an arm's reach budget is much
    shorter than a leg's, so a hand_target sized by eye against the 0.827 m leg
    reach will silently hit `solve_two_link`'s clamp and produce a locked,
    straight-armed mannequin pose. `aim_arm` therefore treats over-reach as
    FATAL rather than as a warning.
    """
    humerus, ulna, _hand = ARM_CHAIN[side]
    b = arm.data.bones
    return b[humerus].length, b[ulna].length


class RigGeometry:
    """Rest-derived axes + lengths + the metre->armature-unit converter.

    Bundled into one object so a per-move script threads a single `geom` through
    its pose calls instead of six positional axis arguments -- and so `geom.m()`
    is always at hand, which is the standing guard against the 100x
    armature-vs-world space error.
    """

    def __init__(self, arm):
        self.arm = arm
        self.right, self.up, self.forward = derive_axes(arm)
        self.units_per_metre = units_per_metre(arm)
        self.femur, self.tibia, self.foot = bone_lengths(arm)

    def m(self, metres):
        """Metres -> armature units. Convert every spec constant through this."""
        return metres * self.units_per_metre

    def to_m(self, units):
        """Armature units -> metres. For reporting measurements only."""
        return units / self.units_per_metre

    @property
    def leg_reach(self):
        """Hip-to-ankle reach in ARMATURE UNITS."""
        return self.femur + self.tibia

    def log_summary(self):
        report("axes_right", tuple(round(v, 4) for v in self.right))
        report("axes_up", tuple(round(v, 4) for v in self.up))
        report("axes_forward", tuple(round(v, 4) for v in self.forward))
        report("units_per_metre", f"{self.units_per_metre:.1f}")
        report("femur_m", f"{self.to_m(self.femur):.4f}")
        report("tibia_m", f"{self.to_m(self.tibia):.4f}")
        report("foot_m", f"{self.to_m(self.foot):.4f}")
        report("leg_reach_m", f"{self.to_m(self.leg_reach):.4f}")
        for side in ("L", "R"):
            h, u = arm_lengths(self.arm, side)
            report(f"arm_{side}_humerus_m", f"{self.to_m(h):.4f}")
            report(f"arm_{side}_ulna_m", f"{self.to_m(u):.4f}")
            report(f"arm_{side}_reach_m", f"{self.to_m(h + u):.4f}")


# ═════════════════════════════════════════════════════════════════════════════
# IK primitives
# ═════════════════════════════════════════════════════════════════════════════
def solve_two_link(target, l1, l2, on_overreach="warn", what="chain"):
    """Planar 2-link IK by law of cosines.

    Returns `(distance, hip_offset, interior_angle)` -- all in the same units
    `target`/`l1`/`l2` came in (ARMATURE UNITS in every caller here):

    - `distance`      root-to-tip distance, after any over-reach clamp;
    - `hip_offset`    angle (rad) between the FIRST bone and the root->tip line,
                      from which the first bone's direction follows;
    - `interior_angle` interior angle (rad) at the middle joint.

    The joint is forced to bend in one direction (a human knee and elbow each
    have a single hinge sense) by the CALLER's choice of bend axis, not here.

    `on_overreach`:
      "warn"  clamp and log -- the legacy leg behaviour, where the spec is
              chosen so it cannot fire and a clamp means the stride/height
              combination is geometrically impossible.
      "fail"  raise SystemExit. Correct for ARMS, where a clamp yields a locked
              straight limb that reads as a mannequin rather than a reach.
    """
    d = target.length
    reach = l1 + l2
    if d > reach * 0.999:
        msg = (f"IK target {d:.4f} exceeds reach {reach:.4f} "
               f"(armature units) for {what}")
        if on_overreach == "fail":
            raise SystemExit(
                f"FATAL: {msg}. Pull `hand_target` in -- accepting the clamp "
                f"produces a locked straight arm that reads as a mannequin.")
        log(f"WARNING: {msg} -- clamping")
        d = reach * 0.999
    cos_knee = (l1 * l1 + l2 * l2 - d * d) / (2.0 * l1 * l2)
    knee_interior = math.acos(max(-1.0, min(1.0, cos_knee)))
    cos_hip = (l1 * l1 + d * d - l2 * l2) / (2.0 * l1 * d)
    hip_offset = math.acos(max(-1.0, min(1.0, cos_hip)))
    return d, hip_offset, knee_interior


def aim_matrix(head, tail_dir, side_axis):
    """Armature-space matrix aiming the bone's local +Y along `tail_dir`.

    Blender bones point along their local +Y. Building the basis by
    Gram-Schmidt against a reference axis sidesteps Mixamo's arbitrary rest roll
    entirely -- we never need to know what the rest roll was, which is what
    makes this robust across bones.

    Unit basis, no scale: a scaled basis would stretch the bone rather than just
    orient it, and the FBX round-trip would carry that into Godot as a SCALE_3D
    track the clip contract does not expect. `verify_pose_unscaled` is the guard.

    `side_axis` must not be parallel to `tail_dir`. For legs the rig's `right`
    is always safe. For ARMS it is not -- an arm near the T-pose points straight
    down `right` -- so `aim_arm` passes the bend-plane normal instead.
    """
    y = tail_dir.normalized()
    x = (side_axis - y * side_axis.dot(y))
    if x.length < 1e-6:
        # Degenerate only if the bone points along the side axis; fall back to
        # any perpendicular rather than emit NaN.
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
# posing primitives
# ═════════════════════════════════════════════════════════════════════════════
def plant_foot(arm, side, ankle_target, toe_dir, geom, frame=None):
    """Pose one leg so the ANKLE lands on `ankle_target` (ARMATURE space).

    Foot trajectory -> two-link IK -> `aim_matrix`, root to tip. This is the
    inverse of the obvious approach and the inversion is the point: specifying
    joint ANGLES makes a fore/aft-swinging leg a pendulum that LIFTS the foot at
    the stride extremes unless the knee compensates, which is the analytic
    account of #298's empirically-discovered both-feet-airborne cliff. Specifying
    the foot POSITION makes grounding hold by construction.

    Absolute ankle positions also overwrite whatever static stagger the source
    clip baked in (`Dribble.fbx` carries +0.6881 m of it), so #298's `C_leg`
    bisection machinery is unnecessary rather than merely retuned.

    Returns the `solve_two_link` triple for reporting. Keys the three rotated
    bones when `frame` is given.
    """
    up_leg, leg, foot_b, _toe_b = LEG_CHAIN[side]
    right = geom.right

    hip_head = arm.pose.bones[up_leg].head.copy()
    to_ankle = ankle_target - hip_head
    solved = solve_two_link(to_ankle, geom.femur, geom.tibia,
                            what=f"{side} leg")
    _d, hip_offset, _interior = solved

    # Rotate the hip->ankle direction by `hip_offset` about the rig's right axis
    # to get the femur direction. This sense puts the knee AHEAD of the
    # hip->ankle line, which is the only way a human knee bends.
    dir_ankle = to_ankle.normalized()
    femur_dir = Matrix.Rotation(-hip_offset, 4, right) @ dir_ankle

    arm.pose.bones[up_leg].matrix = aim_matrix(hip_head, femur_dir, right)
    bpy.context.view_layer.update()
    # Re-read the knee head AFTER the femur is posed: it is the femur's tail, so
    # reading it before would aim the tibia from a stale position and quietly
    # break the IK chain. The `view_layer.update()` calls in this function are
    # load-bearing for exactly this reason -- do not "clean them up".
    knee_head = arm.pose.bones[leg].head.copy()
    arm.pose.bones[leg].matrix = aim_matrix(
        knee_head, (ankle_target - knee_head), right)
    bpy.context.view_layer.update()

    ankle_head = arm.pose.bones[foot_b].head.copy()
    arm.pose.bones[foot_b].matrix = aim_matrix(ankle_head, toe_dir, right)
    bpy.context.view_layer.update()

    if frame is not None:
        for bn in (up_leg, leg, foot_b):
            arm.pose.bones[bn].keyframe_insert("rotation_quaternion", frame=frame)
    return solved


def aim_arm(arm, side, hand_target, elbow_hint_dir, geom, frame=None,
            hand_dir=None):
    """Pose <side>Arm / <side>ForeArm / <side>Hand so the WRIST reaches
    `hand_target` (ARMATURE space).

    Same two-link solve as the leg -- `solve_two_link` + `aim_matrix`, root to
    tip, re-reading the elbow head after posing the humerus. Two differences,
    both deliberate:

    1. THE BEND PLANE IS SUPPLIED, NOT ASSUMED. A knee hinges in the sagittal
       plane against the rig's `right` axis. An elbow's plane depends on
       shoulder rotation, so the caller passes `elbow_hint_dir` -- roughly where
       the elbow should point -- and the plane normal is derived from it.
    2. OVER-REACH IS FATAL. An arm's reach budget is roughly half a leg's, so an
       over-ambitious target is easy to write by eye. Clamping it silently
       yields a locked straight arm that reads as a mannequin, so this raises.

    `hand_dir` orients the palm/hand bone; the default continues the forearm
    line, which is a neutral wrist. Pass a direction for a move that needs a
    specific wrist (a cradle, a swat).

    Returns the achieved wrist error in ARMATURE UNITS -- 0 within float noise
    unless the solve was clamped.
    """
    humerus, ulna, hand = ARM_CHAIN[side]
    l1, l2 = arm_lengths(arm, side)

    sh_head = arm.pose.bones[humerus].head.copy()
    to_wrist = hand_target - sh_head
    _d, sh_offset, _interior = solve_two_link(
        to_wrist, l1, l2, on_overreach="fail", what=f"{side} arm")

    dir_wrist = to_wrist.normalized()
    # Bend-plane normal. Rotating `dir_wrist` about (dir_wrist x hint) by a
    # POSITIVE angle carries it toward `hint`, so the elbow ends up displaced
    # to the hinted side.
    axis = dir_wrist.cross(elbow_hint_dir)
    if axis.length < 1e-6:
        # `elbow_hint_dir` is parallel to the reach direction, so it names no
        # plane. Refuse rather than pick an arbitrary one: a silently-chosen
        # elbow plane is exactly the kind of wrong-but-plausible pose this
        # library exists to prevent.
        raise SystemExit(
            f"FATAL: elbow_hint_dir is parallel to the {side} arm's reach "
            f"direction, so it does not define a bend plane. Pass a hint that "
            f"points across the reach (e.g. down/outward), not along it.")
    axis.normalize()
    humerus_dir = Matrix.Rotation(sh_offset, 4, axis) @ dir_wrist

    # The bend-plane normal doubles as `aim_matrix`'s side reference. It is
    # perpendicular to the bone by construction, so it can never hit the
    # degenerate branch -- which the rig's `right` axis WOULD hit for an arm
    # near the T-pose -- and it puts the elbow's roll in the anatomical plane.
    arm.pose.bones[humerus].matrix = aim_matrix(sh_head, humerus_dir, axis)
    bpy.context.view_layer.update()

    elbow_head = arm.pose.bones[ulna].head.copy()
    forearm_dir = (hand_target - elbow_head)
    arm.pose.bones[ulna].matrix = aim_matrix(elbow_head, forearm_dir, axis)
    bpy.context.view_layer.update()

    wrist_head = arm.pose.bones[hand].head.copy()
    arm.pose.bones[hand].matrix = aim_matrix(
        wrist_head, hand_dir if hand_dir is not None else forearm_dir, axis)
    bpy.context.view_layer.update()

    if frame is not None:
        for bn in (humerus, ulna, hand):
            arm.pose.bones[bn].keyframe_insert("rotation_quaternion", frame=frame)

    return (arm.pose.bones[hand].head - hand_target).length


def drop_hips(arm, offset_vec, geom, frame=None):
    """Translate the Hips by `offset_vec` (ARMATURE space) as a DELTA.

    Applied as a delta on the clip's own root motion rather than an absolute
    position, so whatever the source clip does vertically is preserved and
    merely offset.

    The Hips ROTATION is deliberately left untouched. It is the one bone the two
    rotation families in `locomotion.res` disagree on catastrophically (~158
    deg), and every leg solve hangs off it.
    """
    pb = arm.pose.bones[HIPS]
    mh = pb.matrix.copy()
    mh.translation = mh.translation + offset_vec
    pb.matrix = mh
    bpy.context.view_layer.update()
    if frame is not None:
        pb.keyframe_insert("location", frame=frame)


def rotate_bone_about_head(arm, bone_name, rotations, frame=None):
    """Compose `rotations` onto `bone_name`'s CURRENT pose, pivoting on its head.

    `rotations` is applied left-to-right, i.e. `rotations[0]` is outermost.
    Composing onto the current pose (rather than replacing it) is what keeps the
    source clip's own motion on this bone -- a torso lean is an adjustment to
    the clip, not a substitute for it.

    The fold below accumulates strictly left-to-right, starting from the pivot
    translation, so it reproduces Python's `@` associativity for the equivalent
    inline expression EXACTLY. That is not pedantry: matrix multiplication is
    associative in mathematics but not bitwise in floating point, so regrouping
    the product perturbs the low bits and shows up as a spurious sub-degree
    delta when an extraction is checked against its pre-refactor output.
    """
    pb = arm.pose.bones[bone_name]
    head = pb.head.copy()
    m = Matrix.Translation(head)
    for r in rotations:
        m = m @ r
    m = m @ Matrix.Translation(-head)
    pb.matrix = m @ pb.matrix
    bpy.context.view_layer.update()
    if frame is not None:
        pb.keyframe_insert("rotation_quaternion", frame=frame)


# ═════════════════════════════════════════════════════════════════════════════
# the keypose timeline
# ═════════════════════════════════════════════════════════════════════════════
# `author_dribble_move.py` is a CYCLIC gait authorer: `phase = (t/CYCLE_S) % 1`.
# Almost none of the twenty moves are cyclic -- they are three-phase one-shots
# (Startup / Active / Recovery). So the gait clock is not the structure; it is
# one possible pose source. The structure is an explicit keypose timeline.
#
# A Keypose carries a dict of named SCALAR channels. The library interpolates
# those channels and hands them to the move's own `apply` callback, which knows
# what "lead_foot_fore_m" means for that move. That split is what "shared module
# + thin per-move spec" means: the timeline, easing, and baking are shared; the
# pose vocabulary stays per-move, because no two of these twenty moves pose the
# same set of things.


class Keypose:
    """One authored pose at `time_s`, as a dict of named scalar channels.

    Channels are metre- and degree-denominated for readability; convert through
    `geom.m()` inside `apply`, never here.

    `easing` shapes the segment from THIS keypose to the next one (Blender's
    fcurve convention). Leave it None to take the label-driven default from
    `PHASE_EASING` -- see the note above that mapping.
    """

    def __init__(self, time_s, label, easing=None, **channels):
        self.time_s = time_s
        self.label = label
        self.easing = easing
        self.channels = channels

    def __repr__(self):
        return f"Keypose({self.label!r} @ {self.time_s:.3f}s)"


# ── easing: the legibility lever ─────────────────────────────────────────────
# Each of these maps [0,1] -> [0,1] with f(0)=0 and f(1)=1. What differs is the
# VELOCITY at the endpoints, and that is the whole legibility decision:
#
#   curve        v(0)  v(1)   reads as
#   ease_in         0     2   a load, then a snap  -- weight gathers, then goes
#   ease_out        2     0   a release, then a settle
#   ease_in_out     0     0   a glide (smoothstep)
#   ease_linear     1     1   mechanical, no weight
#
# Smoothstep everywhere is WRONG for a committed move, and specifically wrong on
# the segment that carries the read. Arriving at the Active pose with zero
# velocity means the body glides into its commitment and stops there -- which is
# the visual signature of exactly what ADR-0003 names as the primary anti-goal
# (arcade decoupling of action from physical commitment). Athletic movement is
# asymmetric: load slow, release fast, decelerate through the recovery.
#
# Quadratic, not cubic, deliberately. These clips are 3-13 ticks long; at 30 fps
# a 6-tick startup is six frames, so a more dramatic curve buys almost nothing
# and risks reading as a stutter. Per README-blender.md the read comes from POSE
# CONTRAST between phases, not from motion inside a phase -- the easing only has
# to avoid fighting that, not carry it.
def ease_linear(t):
    """No shaping. For a channel that must move at constant rate."""
    return t


def ease_in(t):
    """Accelerate: leave the pose slowly, ARRIVE at peak velocity. Load-and-snap."""
    return t * t


def ease_out(t):
    """Decelerate: LEAVE at peak velocity, settle into the pose. The follow-through."""
    return t * (2.0 - t)


def ease_in_out(t):
    """Smoothstep: zero velocity at both ends. A glide -- right for cyclic motion."""
    return t * t * (3.0 - 2.0 * t)


#: Segment easing chosen by the label of the keypose the segment STARTS from.
#: This follows Blender's own graph-editor convention -- an fcurve keyframe's
#: interpolation governs the interval to the NEXT key -- so it is the least
#: surprising rule for anyone who has touched the fcurve editor.
#:
#: The three-phase Startup/Active/Recovery vocabulary is universal across the
#: twenty clip handoffs (it is the tick table), so this mapping is resolved ONCE
#: here rather than re-decided in twenty per-move specs -- the whole charter of
#: #315. `bake_timeline` logs the resolved choice per segment, so a default that
#: you can read in the run output is a default, not hidden magic.
#:
#:   startup  -> ease_in   the weight gathers, then goes: the tell, then the snap
#:   active   -> ease_out  explode out of the commitment, decelerate into recovery
#:   recovery -> smoothstep  a settle back toward neutral, if a later pose exists
#:
#: Unknown labels fall back to `DEFAULT_EASING`, so a non-three-phase timeline
#: (a cyclic gait, a held idle) behaves exactly as it did before this mapping
#: existed. Override per keypose with `Keypose(..., easing=...)`, or for the
#: whole timeline by passing `easing=` to `bake_timeline`.
PHASE_EASING = {
    "startup": ease_in,
    "active": ease_out,
    "recovery": ease_in_out,
}

DEFAULT_EASING = ease_in_out

#: Backwards-compatible alias: `ease` was the single smoothstep before the
#: per-phase mapping existed. Kept so nothing silently changes meaning.
ease = ease_in_out


def resolve_easing(keypose, override=None):
    """The easing for the segment starting at `keypose`.

    Precedence, most specific first: an explicit `override` (whole-timeline
    escape hatch) > the keypose's own `easing` > `PHASE_EASING` by label >
    `DEFAULT_EASING`.
    """
    if override is not None:
        return override
    if getattr(keypose, "easing", None) is not None:
        return keypose.easing
    return PHASE_EASING.get(str(keypose.label).strip().lower(), DEFAULT_EASING)


def interp_channels(keyposes, t_s, easing=None):
    """Channel values at `t_s`, interpolated between the bracketing keyposes.

    Holds the endpoints outside the timeline's range rather than extrapolating.
    A channel present in one keypose but absent from its neighbour is HELD at
    the value it has, not treated as zero -- silently lerping an absent channel
    toward 0 is a very easy way to author a limb that drifts to the origin.

    `easing=None` resolves per segment via `resolve_easing`; pass a callable to
    force one curve across the whole timeline.
    """
    if not keyposes:
        raise SystemExit("FATAL: empty keypose timeline")
    ordered = sorted(keyposes, key=lambda k: k.time_s)
    if t_s <= ordered[0].time_s:
        return dict(ordered[0].channels)
    if t_s >= ordered[-1].time_s:
        return dict(ordered[-1].channels)

    for a, b in zip(ordered, ordered[1:]):
        if a.time_s <= t_s <= b.time_s:
            span = b.time_s - a.time_s
            # The SEGMENT's easing comes from the keypose it starts at, so a
            # three-phase move gets load-and-snap into Active and a settle out
            # of it without the per-move spec restating that every time.
            shape = resolve_easing(a, easing)
            u = 0.0 if span <= 0.0 else shape((t_s - a.time_s) / span)
            out = {}
            for key in set(a.channels) | set(b.channels):
                if key in a.channels and key in b.channels:
                    out[key] = a.channels[key] + (b.channels[key] - a.channels[key]) * u
                else:
                    out[key] = a.channels.get(key, b.channels.get(key))
            return out
    raise SystemExit(f"FATAL: t={t_s} fell through the keypose timeline")


def bake_timeline(arm, keyposes, apply, f0, f1, fps, easing=None):
    """Walk frames `f0..f1`, interpolate the timeline, and let `apply` pose+key.

    `apply(frame, t_s, channels)` is the move's own spec. It is called with the
    scene already on `frame`, so it can read the source clip's pose for that
    frame and compose onto it.

    Logs the easing resolved for each segment. That log line is what keeps the
    label-driven `PHASE_EASING` default honest: a wrong curve shows up as a
    readable line in the authoring run rather than as a clip that feels off.
    """
    ordered = sorted(keyposes, key=lambda k: k.time_s)
    for a, b in zip(ordered, ordered[1:]):
        log(f"segment {a.label!r} -> {b.label!r} "
            f"({a.time_s:.3f}s..{b.time_s:.3f}s): "
            f"easing={resolve_easing(a, easing).__name__}")

    scene = bpy.context.scene
    for i, f in enumerate(range(f0, f1 + 1)):
        scene.frame_set(f)
        apply(f, i / fps, interp_channels(keyposes, i / fps, easing))


# ═════════════════════════════════════════════════════════════════════════════
# proof helpers -- every one raises, none of them warn
# ═════════════════════════════════════════════════════════════════════════════
# These run at AUTHORING time, which is much earlier and much cheaper than
# discovering the same defect as a T-posing arm in a Godot harness scenario.
@contextlib.contextmanager
def preserve_frame():
    """Restore the scene's current frame on exit -- MEASUREMENT MUST NOT PERTURB.

    Every proof helper here samples the pose by stepping frames, and Blender's
    FBX exporter turns out to be sensitive to which frame the scene is sitting on
    when it runs. Measured (#315): running the proofs without this shifted every
    exported bone position by up to 0.85 um, growing with depth down the
    kinematic chain, with rotations completely unaffected.

    That magnitude is irrelevant to the game -- four orders of magnitude below
    any tolerance in this project. What is NOT irrelevant is that it makes
    "re-run the authorer and you get the committed asset back" false, and that
    property is the only cheap way to review an authoring change. So the proofs
    are frame-neutral by construction.
    """
    scene = bpy.context.scene
    saved = scene.frame_current
    try:
        yield
    finally:
        scene.frame_set(saved)


def _action_fcurves(arm):
    """All fcurves on the armature's action.

    Blender 4.4+ removed `Action.fcurves` (slotted Actions), so the path is
    layers -> strips -> channelbags -> fcurves. Verified on 5.2: `Action` has no
    `fcurves` attribute at all, so this is not a compatibility nicety.
    """
    act = arm.animation_data.action if arm.animation_data else None
    if act is None:
        raise SystemExit("FATAL: armature has no action to inspect")
    out = []
    for layer in act.layers:
        for strip in layer.strips:
            for bag in getattr(strip, "channelbags", []):
                out.extend(bag.fcurves)
    return out


def keyed_bone_names(arm):
    """Names of bones carrying at least one keyed channel."""
    names = set()
    for fc in _action_fcurves(arm):
        if fc.data_path.startswith('pose.bones["'):
            names.add(fc.data_path.split('"')[1])
    return names


def verify_all_bones_keyed(arm, expected_count=None, allow_leaf_ends=True):
    """Every bone carries keys -- the README trap-1 guard, at authoring time.

    Trap 1: a single-clip AnimationTree state does NOT hold a bone's previous
    pose for bones the clip omits; it falls back to skeleton REST. A clip that
    touches only the gesturing limb makes the arms T-pose the moment the move
    plays. A Blender FBX export bakes the whole armature by default, which makes
    that failure structurally absent -- this asserts nobody has narrowed the
    export to defeat that.

    Leaf terminators (`LEAF_END_BONES`) are exempt by default: the Mixamo source
    clips leave all 13 unkeyed, and nothing hangs off them.
    """
    all_bones = {pb.name for pb in arm.pose.bones}
    keyed = keyed_bone_names(arm)
    required = all_bones - (LEAF_END_BONES if allow_leaf_ends else frozenset())
    missing = sorted(required - keyed)
    report("bones_total", len(all_bones))
    report("bones_keyed", len(keyed))
    if missing:
        raise SystemExit(
            f"FATAL: {len(missing)} bone(s) carry no keys and will fall back to "
            f"skeleton REST in Godot (README trap 1 / a45bd1d): {missing}")
    if expected_count is not None and len(keyed) != expected_count:
        raise SystemExit(
            f"FATAL: {len(keyed)} bones keyed but expected {expected_count}. "
            f"The rig or the export scope changed; re-derive before authoring.")


def verify_pose_unscaled(arm, frames, tol=1e-4):
    """No bone carries a non-unit pose SCALE at any frame in `frames`.

    Guards `aim_matrix` against a non-unit basis, which would stretch the bone
    and arrive in Godot as a SCALE_3D track the clip contract does not expect.

    This checks the POSE, deliberately, not the fcurve list. Every Mixamo source
    action already carries scale CHANNELS -- measured 156 of them on
    `Dribble.fbx`, 52 bones x 3 axes -- and these scripts key into the source
    action, so "assert no scale fcurves exist" would fail on every single
    authoring run while catching nothing. The pose is where a bad basis shows
    up. Measured baseline on the untouched source: 2.4e-7 off unit.
    """
    scene = bpy.context.scene
    worst = (0.0, None, None)
    with preserve_frame():
        for f in frames:
            scene.frame_set(f)
            for pb in arm.pose.bones:
                dev = max(abs(c - 1.0) for c in pb.matrix_basis.to_scale())
                if dev > worst[0]:
                    worst = (dev, f, pb.name)
    report("worst_pose_scale_dev", f"{worst[0]:.8f}")
    if worst[0] > tol:
        raise SystemExit(
            f"FATAL: bone {worst[2]!r} has pose scale {worst[0]:.6f} off unit at "
            f"frame {worst[1]} (tol {tol}). A non-unit aim_matrix basis stretches "
            f"the bone and emits a SCALE_3D track.")


def snapshot_pose(arm, frame):
    """Armature-space pose matrices at `frame`, for `verify_pose_distinct`."""
    with preserve_frame():
        bpy.context.scene.frame_set(frame)
        return {pb.name: pb.matrix.copy() for pb in arm.pose.bones}


def pose_delta_deg(pose_a, pose_b):
    """Largest per-bone rotation difference between two snapshots, in degrees."""
    worst = (0.0, None)
    for name, ma in pose_a.items():
        mb = pose_b.get(name)
        if mb is None:
            continue
        qa, qb = ma.to_quaternion(), mb.to_quaternion()
        deg = abs(qa.rotation_difference(qb).angle) * RAD_TO_DEG
        if deg > 180.0:  # quaternion double cover: q and -q are one rotation
            deg = 360.0 - deg
        if deg > worst[0]:
            worst = (deg, name)
    return worst


def verify_pose_distinct(pose_a, pose_b, min_deg, label="poses"):
    """Two keyposes differ by at least `min_deg` on some bone.

    Use it to enforce STARTUP != RECOVERY. That identity is the whole defect
    #296 reports: a move whose wind-up and punish-window look the same is
    unreadable, because the opponent cannot tell which phase they are watching.
    Asserting it here makes the defect structurally impossible rather than
    something a reviewer has to notice by eye.
    """
    deg, bone = pose_delta_deg(pose_a, pose_b)
    report(f"pose_distinct_{label}_deg", f"{deg:.3f}")
    if deg < min_deg:
        raise SystemExit(
            f"FATAL: {label} differ by only {deg:.3f} deg (worst bone "
            f"{bone!r}), below the {min_deg} deg legibility floor. Startup and "
            f"Recovery must not be the same pose (#296).")


def verify_grounded(arm, frames, tol_m, geom, band_ref=None):
    """The support foot stays on one level -- i.e. the character never floats.

    Definition: at each frame take the LOWER of the two toes and measure its
    height along the rig's `up`. The reference level is the minimum of those
    across all frames (or `band_ref` if the caller has an absolute ground). Then
    every frame's lower toe must sit within `tol_m` of it.

    Taking the lower toe per frame is what catches the real failure: a frame
    where BOTH feet leave the ground raises that frame's minimum, so it falls
    out of the band. #298 found a both-feet-0.5 m-airborne cliff only because
    this proof was added after the fact -- so it is standard now.

    Skip only for genuinely airborne moves (jump shot, block, layup) and assert
    the INTENDED flight arc instead; do not simply widen `tol_m` until it passes.
    """
    scene = bpy.context.scene
    toes = [LEG_CHAIN["L"][3], LEG_CHAIN["R"][3]]
    heights = []
    with preserve_frame():
        for f in frames:
            scene.frame_set(f)
            heights.append(min(arm.pose.bones[t].head.dot(geom.up) for t in toes))
    ref = band_ref if band_ref is not None else min(heights)
    excursions = [geom.to_m(h - ref) for h in heights]
    worst = max(excursions)
    report("ground_band_m", f"{worst:.4f}")
    if worst > tol_m:
        bad = frames[excursions.index(worst)]
        raise SystemExit(
            f"FATAL: lower toe rises {worst:.4f} m above the support level at "
            f"frame {bad} (tol {tol_m} m) -- the character floats.")


# ═════════════════════════════════════════════════════════════════════════════
# export
# ═════════════════════════════════════════════════════════════════════════════
def export_fbx(arm, dst, action_name):
    """Export the armature + baked action, named so Godot can find the clip.

    Godot names the imported clip after the FBX animation TAKE, and with
    `bake_anim_use_all_actions=False` Blender names that take after the SCENE,
    not the action -- measured in #300: renaming only the action still imported
    as "Scene". So rename BOTH. The rebuild scripts look the clip up by name, so
    this is a contract, not cosmetics.

    All four bake flags matter:
      add_leaf_bones=False       leaf bones would arrive in Godot carrying no
                                 clip keys -- the a45bd1d rest-fallback T-pose
                                 trap wearing a new hat.
      bake_anim_simplify_factor=0.0 and bake_anim_step=1.0
                                 any simplification resamples the exact frame
                                 grid the rebuild tools' loop-seam proofs
                                 depend on.
      bake_anim_use_all_actions=False
                                 keeps the take single, and is what makes the
                                 scene rename above necessary.
    """
    arm.animation_data.action.name = action_name
    bpy.context.scene.name = action_name
    log(f"action + scene renamed -> {action_name!r}")

    bpy.ops.export_scene.fbx(
        filepath=dst,
        use_selection=False,
        object_types={"ARMATURE"},
        add_leaf_bones=False,
        bake_anim=True,
        bake_anim_use_all_actions=False,
        bake_anim_use_nla_strips=False,
        bake_anim_simplify_factor=0.0,
        bake_anim_step=1.0,
    )
    log(f"exported -> {dst}")


def load_source(src, fps):
    """Factory-reset, import `src`, and return (armature, f0, f1).

    Factory reset with `use_empty=True` first: a stale scene is how a second
    armature or a leftover action silently ends up in the export.
    """
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=src)
    arm = next(o for o in bpy.data.objects if o.type == "ARMATURE")
    scene = bpy.context.scene
    scene.render.fps = fps

    act = arm.animation_data.action
    f0, f1 = (int(v) for v in act.frame_range)
    scene.frame_start, scene.frame_end = f0, f1
    log(f"source action {act.name!r} frames {f0}..{f1} ({f1 - f0 + 1} frames)")
    return arm, f0, f1


def enter_pose_mode(arm):
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode="POSE")


def leave_pose_mode():
    bpy.ops.object.mode_set(mode="OBJECT")
