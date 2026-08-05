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

# ── degenerate-geometry thresholds (#338) ────────────────────────────────────
# Both of the following guard a quantity that is ALREADY GARBAGE long before it
# reaches zero, so both are set from a measurement rather than from "close to
# zero looks about right". That was the #338 defect: guards written as exact- or
# near-zero checks fire two to five decades after the answer they protect has
# stopped meaning anything, and the caller gets a wrong-but-plausible pose with
# nothing said.

# Minimum |a x b| for two UNIT vectors to name a usable plane -- i.e. sin(angle),
# so this is an angle in radians for small values.
#
# MEASURED, in Blender's float32 `mathutils` vectors: the DIRECTION error of a
# Gram-Schmidt residual against an exact reference grows as roughly
# 1.1e-5/theta degrees --
#
#     theta   1e-3 -> 0.011 deg   1e-4 -> 0.109   1e-5 -> 1.12
#             1e-6 -> 10.31       1e-7 -> 62.02
#
# The old threshold of 1e-6 therefore admitted a plane that was already ~10 deg
# wrong. 1e-3 keeps it within ~0.01 deg, which is far below anything a pose can
# express.
#
# PROVEN INERT ON REAL WORK, so this is hardening and not a behaviour change:
# the smallest `|dir_ankle x forward|` over every `plant_foot` call made by all
# seven authoring scripts is 0.402019 (the layup), i.e. 400x this threshold, and
# the smallest Gram-Schmidt residual reaching `aim_matrix` is 0.759086. No
# committed clip changes because of it.
BEND_PLANE_MIN_SIN = 1e-3

# Minimum |span . axis| / |span| for a rest landmark pair to resolve which side
# of the body an axis points at. Relative to the span length, because the dot
# product scales with it and an absolute threshold would mean different angles
# on different rigs (and in different units -- this library works in armature
# units, where a shoulder span is ~37, not ~0.37).
#
# MEASURED on the Y Bot: the real landmarks read 0.99999998 (shoulders) and
# 1.00000000 (hips), so honest input clears this by a factor of 1000.
LANDMARK_MIN_COS = 1e-3


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
    """`(lateral, up, forward)` in ARMATURE space, from the REST pose.

    Derived, never hardcoded: Mixamo rest rolls are arbitrary. Read from the
    RAW imported FBX -- never from a Player.tscn rig, where BlendRestAnchor
    rotates both UpLeg rests at _Ready and every foot/toe global rest inherits
    the error (119.6 deg; cost a 2.17x stride mismeasurement in #298).

    THE FIRST RETURN VALUE IS `lateral`, NOT `right` (#320). It is a basis
    vector with a well-defined SIGN and NO anatomical meaning: on every Mixamo
    rig measured here it points at the character's LEFT. It used to be called
    `right`, which is a name that lies. For "which side of the body is this",
    use `RigGeometry.body_right`, which is derived anatomically and verified.

    `up` and `forward` ARE anatomical: `up` is hips->head, and `forward` is
    sign-checked against the toe-ahead-of-ankle test below.
    """
    rest = arm.data.bones
    l_hip = rest["mixamorig:LeftUpLeg"].head_local
    r_hip = rest["mixamorig:RightUpLeg"].head_local
    hips = rest[HIPS].head_local
    head = rest["mixamorig:Head"].head_local

    lateral = (r_hip - l_hip).normalized()
    up = (head - hips).normalized()
    forward = lateral.cross(up).normalized()
    lateral = up.cross(forward).normalized()

    # Sign check against anatomy rather than assumption: the toe is ahead of
    # the ankle on a human.
    #
    # THIS BRANCH FIRES ON EVERY RUN against the Mixamo rigs -- it is not a
    # defensive no-op, it is load-bearing. `forward = lateral x up` comes out
    # pointing BACKWARD for this rig's handedness, and the flip is what makes it
    # anatomically forward. Measured on `Dribble.fbx`: the post-flip axes are
    # lateral=(1,0,0), up=(0,1,0), forward=(0,0.006,1).
    #
    # A #315 review proposed raising here instead, on the theory that negating
    # the lateral axis mirrors the rig and that the branch never fires anyway.
    # Both halves were wrong: it fires every time, and raising broke every
    # authoring run. Negating BOTH preserves handedness.
    #
    # WHY BOTH ARE STILL NEGATED, given that this leaves `lateral` pointing at
    # the character's LEFT (#320): because this vector's SIGN is load-bearing in
    # two roles that have nothing to do with anatomy, and MEASURING the
    # alternative settled it. Negating only `forward` -- i.e. "correcting"
    # `lateral` to point anatomically right, with no other edit -- re-authors the
    # dribble into a BROKEN clip, not a mirrored one:
    #
    #   4096 of 4160 (frame,bone) pairs rotation-differing; every leg-chain bone
    #   rotated 179.99 deg (`aim_matrix`'s side reference flipped, so its x and z
    #   columns flip and each IK-posed bone rolls 180 deg about its own axis);
    #   the torso lean reversed, swinging everything hanging off the spine
    #   (HeadTop_End 0.8418 m, LeftHandMiddle4 0.6644 m).
    #
    # So the sign stays, and the ANATOMY question moved to a separate,
    # independently-derived accessor -- `RigGeometry.body_right`. That kills the
    # trap by making the misleading name unavailable rather than by flipping a
    # sign that three unrelated things depend on.
    toe = rest["mixamorig:LeftToeBase"].head_local
    ankle = rest["mixamorig:LeftFoot"].head_local
    if (toe - ankle).dot(forward) < 0:
        forward, lateral = -forward, -lateral
    return lateral, up, forward


def derive_body_right(arm, lateral):
    """The character's ANATOMICAL right, as +/-`lateral`. See #320.

    `lateral` is a basis vector whose sign says nothing about anatomy (on every
    Mixamo rig here it points at the character's LEFT). This resolves the
    anatomy question ONCE, so the six authoring scripts stop each carrying their
    own `BODY_RIGHT = -geom.right` workaround and a docstring explaining it.

    Returned as +/-`lateral` rather than re-derived as a fresh vector, and that
    is deliberate on two counts:

    - It stays EXACTLY antiparallel to the basis, bit-for-bit, so switching a
      call site from `-geom.right` to `geom.body_right` cannot perturb an
      exported clip. That is what let #320 land without re-authoring seven FBX
      assets and re-confirming seven equivalence gates.
    - A freshly re-derived vector would need its own orthogonalisation against
      `up`/`forward`, i.e. a second, subtly different basis to get wrong.

    The SIGN is measured against the SHOULDER pair, which is independent of the
    hip pair `derive_axes` built `lateral` from -- so this is a genuine second
    opinion, not a restatement. The hip pair is then cross-checked against it:
    a rig whose shoulders and hips disagree on handedness is malformed, and
    silently picking one would place every hand on a coin flip.
    """
    rest = arm.data.bones
    shoulder_span = (rest["mixamorig:RightArm"].head_local
                     - rest["mixamorig:LeftArm"].head_local)
    hip_span = (rest["mixamorig:RightUpLeg"].head_local
                - rest["mixamorig:LeftUpLeg"].head_local)
    by_shoulders = shoulder_span.dot(lateral)
    by_hips = hip_span.dot(lateral)
    # RELATIVE to the span length, not an exact-zero test (#338). This guard used
    # to read `== 0.0`, which for a float dot product essentially never holds --
    # so a span merely NEARLY perpendicular to `lateral` (dot = 1e-9) sailed
    # through and the anatomical side was then decided by float noise. That is
    # exactly the coin flip the message below says it refuses to make; the guard
    # was refusing only the one input that never arrives.
    cos_shoulders = abs(by_shoulders) / shoulder_span.length
    cos_hips = abs(by_hips) / hip_span.length
    if cos_shoulders < LANDMARK_MIN_COS or cos_hips < LANDMARK_MIN_COS:
        raise SystemExit(
            f"FATAL: cannot resolve anatomical right -- the shoulder or hip span "
            f"is perpendicular to `lateral` to within noise (shoulders "
            f"{by_shoulders:.6f}, |cos|={cos_shoulders:.3e}; hips {by_hips:.6f}, "
            f"|cos|={cos_hips:.3e}; need |cos| >= {LANDMARK_MIN_COS}). This rig's "
            f"landmarks do not define a side, so the sign of the dot product is "
            f"float noise rather than anatomy.")
    if (by_shoulders > 0.0) != (by_hips > 0.0):
        raise SystemExit(
            f"FATAL: this rig's shoulders and hips disagree on which way "
            f"`lateral` points (shoulders {by_shoulders:+.6f}, hips "
            f"{by_hips:+.6f}). One of the two bone pairs is mislabelled, so "
            f"anatomical side cannot be resolved -- refusing rather than "
            f"placing every hand on a coin flip.")
    return lateral if by_shoulders > 0.0 else -lateral


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

    TWO LATERAL AXES, AND THEY ARE NOT INTERCHANGEABLE (#320)
    --------------------------------------------------------
    `geom.lateral`     a BASIS vector. Sign is well-defined and load-bearing;
                       anatomy is NOT implied (it points at the character's LEFT
                       on every Mixamo rig measured here). Wherever a call site
                       passes a FIXED axis as `aim_matrix`'s `side_axis` -- a
                       bone-ROLL reference -- this is the one to pass: nothing
                       downstream re-derives that sign, so substituting
                       `body_right` there rolls the posed bones 180 deg.
                       NOT universal, though. `side_axis` may also be a COMPUTED
                       per-pose axis, and for the leg HINGE bones it must be:
                       #338 moved the femur and tibia onto the bend-plane normal
                       (`plant_foot`'s `hinge_axis`), leaving `lateral` to the
                       foot alone. See `aim_matrix` for which is which.
    `geom.body_right`  the character's ANATOMICAL right, derived and verified by
                       `derive_body_right`. Use it for every hand/foot PLACEMENT
                       that means "on the right side of the body".

    A `Matrix.Rotation` TORSO LEAN belongs to neither column, and the tree
    genuinely uses both: `author_dribble_move` passes `lateral`, while
    `author_block` / `author_contest` / `author_jabstep` / `author_layup` pass
    `body_right`. Both are correct, because `body_right` is exactly `+/-lateral`
    and each file's lean-sign constant (`LEAN_DEGREES`, `TORSO_PITCH_SIGN`) was
    co-derived against the axis that file passes. So the invariant here is a
    PAIRING, not an axis: change the axis and you MUST re-derive the sign
    constant with it. Do not "unify" these call sites on one axis without
    re-running each file's lean-direction oracle -- an unpaired swap tips every
    torso backwards while passing every side-agnostic gate.

    There is deliberately NO `geom.right`. It was removed rather than aliased,
    because it named the basis vector while reading as the anatomical one -- and
    the six authoring scripts each independently worked around it with a local
    `BODY_RIGHT = -geom.right`. An alias would preserve exactly the trap this
    split exists to remove; an AttributeError names the problem at the call site.
    """

    def __init__(self, arm):
        self.arm = arm
        self.lateral, self.up, self.forward = derive_axes(arm)
        self.body_right = derive_body_right(arm, self.lateral)
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
        report("axes_lateral", tuple(round(v, 4) for v in self.lateral))
        report("axes_up", tuple(round(v, 4) for v in self.up))
        report("axes_forward", tuple(round(v, 4) for v in self.forward))
        # Reported alongside `lateral` so the run log SHOWS the relationship
        # between the basis axis and anatomy rather than leaving a reader to
        # remember which way `lateral` happens to point on this rig.
        report("axes_body_right", tuple(round(v, 4) for v in self.body_right))
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

    Unit basis, no scale. The orthonormality assertion below is the guard --
    NOT `verify_pose_unscaled`, which is blind to this (see the note there).

    `side_axis` must not be parallel to `tail_dir`, and it sets the bone's ROLL:
    its SIGN matters (flipping it rolls the bone 180 deg), its ANATOMY does not.
    Never pass `geom.body_right` -- see `RigGeometry` (#320). The right axis
    depends on what the bone's roll MEANS, and there are two answers:

    A COMPUTED BEND-PLANE NORMAL, for any bone that acts as a HINGE. Both arm
    bones (`aim_arm`) and, since #338, both leg hinge bones (`plant_foot`'s
    `hinge_axis`). A hinge's roll is not free -- the kneecap/elbow must face the
    way the joint bends -- so the reference has to TRACK the pose. For arms this
    was also a necessity: an arm near the T-pose points straight down the lateral
    axis, so a fixed `geom.lateral` is degenerate there. For legs it is purely a
    correctness fix; `lateral` never went degenerate, it just left the femur's
    roll misaligned with the bend plane by up to 22 deg on shipped clips.

    A FIXED `geom.lateral`, for a bone whose roll is an ORIENTATION rather than a
    hinge. The FOOT is the live case: its roll is the tilt of the SOLE, which
    must stay flat on the floor regardless of how the knee bends, and `lateral`
    is horizontal. Handing the bend-plane normal to the foot as well -- #338's
    proposed one-line fix, applied uniformly -- stands the character on the edges
    of their feet (mutation-measured at 22.16 deg, dribble). Gated by
    `sole_stays_level_*` in the selftest.
    """
    y = tail_dir.normalized()
    x = (side_axis - y * side_axis.dot(y))
    if x.length < BEND_PLANE_MIN_SIN:
        # Degenerate: the bone points along the side axis. Fall back to whichever
        # world axis is LEAST aligned with y, rather than always X.
        #
        # The trip point moved from 1e-6 to `BEND_PLANE_MIN_SIN` in #338. Below
        # that the residual's DIRECTION is noise (see the constant's measured
        # table), so normalising it yields an arbitrary roll that merely LOOKS
        # like a computed one. Substituting a world axis is not better geometry,
        # but it is DETERMINISTIC and reproducible, which a noise direction is
        # not -- and the two are equally arbitrary in this regime, so the tie
        # goes to the one that exports the same clip twice.
        #
        # Always-X was itself degenerate in the case most likely to reach here:
        # `plant_foot` passes `geom.lateral`, which on this rig is essentially
        # exactly +X (it is derived from `r_hip - l_hip`), so `y` parallel to the
        # side axis means `y` parallel to X -- and `X - y*y.x` is then the ZERO
        # vector. The old fallback produced NaN in precisely the situation it
        # existed to prevent.
        ref = Vector((0.0, 0.0, 1.0)) if abs(y.x) > 0.9 else Vector((1.0, 0.0, 0.0))
        x = ref - y * ref.dot(y)
    x.normalize()
    z = x.cross(y).normalized()

    # Assert orthonormality HERE, at construction, rather than trusting a
    # downstream pose check. MEASURED (#315 review): `verify_pose_unscaled`
    # cannot see a non-unit basis at all -- the source action carries scale
    # fcurves for every posed bone, so `frame_set` re-drives pose scale from the
    # SOURCE before any measurement is taken, and the authored scale is
    # discarded. Mutation-proven: scaling this x column 2x left
    # `worst_pose_scale_dev` at 4.8e-7, bit-unchanged, and the selftest green.
    #
    # The damage is real but lands in the ROTATION, not the scale: a non-unit
    # basis displaces child bone heads, so the next chain step (re-reading the
    # knee after posing the femur) aims from a position the parent no longer
    # occupies. That same mutation moved the exported clip by 14.06 deg.
    # Re-authoring an existing clip catches that via `compare_fbx_anim.py`, but
    # a NEW clip has no committed reference to compare against -- which is every
    # one of the nineteen downstream handoffs. So the guard belongs here.
    errs = (abs(x.length - 1.0), abs(y.length - 1.0), abs(z.length - 1.0),
            abs(x.dot(y)), abs(y.dot(z)), abs(z.dot(x)))
    if max(errs) > 1e-5:
        raise SystemExit(
            f"FATAL: aim_matrix built a non-orthonormal basis "
            f"(|x|={x.length:.6f} |y|={y.length:.6f} |z|={z.length:.6f} "
            f"x.y={x.dot(y):.2e} y.z={y.dot(z):.2e} z.x={z.dot(x):.2e}). "
            f"This corrupts the child bone's head position and shows up as a "
            f"rotation error downstream, NOT as a scale track.")

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

    Returns `(solve_two_link triple, achieved_ankle_error_in_armature_units)`.
    Keys the three rotated bones when `frame` is given.

    THE SOLVE IS EXACT, INCLUDING FOR LATERAL TARGETS (#321). It did not used to
    be. The old femur direction was `Matrix.Rotation(-hip_offset, 4, right) @
    dir_ankle`, and a rotation about the rig's `right` only moves the component
    of `dir_ankle` PERPENDICULAR to `right`, so a laterally-offset target
    achieved strictly less than the requested hip angle:

        cos(theta_eff) = cos^2(alpha) + sin^2(alpha) * cos(hip_offset)

    with alpha the angle between `dir_ankle` and `right`. The knee landed off the
    IK circle, the tibia was then aimed at the true target from that wrong knee,
    and the ankle fell SHORT. Measured: 0.029890 m worst-case on the dribble --
    a NEARLY PLANAR gait, against a whole ground band of 0.0315 m -- and
    0.033680 m on the selftest's lateral cases.

    Note what that formula does at alpha = 90 deg: the shortfall vanishes
    exactly. Every clip authored before #321 is a near-sagittal gait, and every
    assertion written for them measured that one angle -- which is how a 3 cm
    error survived two milestones of green gates.

    The fix is `aim_arm`'s construction, applied to the leg: build the rotation
    axis PERPENDICULAR BY CONSTRUCTION as `dir_ankle.cross(forward)`, so
    rotating `dir_ankle` about it by a POSITIVE `hip_offset` carries it toward
    `forward` -- putting the knee ahead of the hip->ankle line, the only way a
    human knee bends -- and lands the knee exactly on the IK circle.

    Sign convention deliberately mirrors `aim_arm` rather than inventing a
    second one: `cross(reach, hint)` with a POSITIVE angle, where the old code
    used `right` with a NEGATIVE one. Do not "tidy" one to match the other.

    TWO ROLL REFERENCES, ONE PER JOINT ROLE (#338)
    ══════════════════════════════════════════════
    This function makes THREE `aim_matrix` calls -- femur, tibia, foot -- and
    `aim_matrix` uses its `side_axis` as a bone ROLL reference. Until #338 all
    three were handed `geom.lateral`. They now split, because the three bones ask
    different questions and only two of them share an answer:

    `hinge_axis`  = -(dir_ankle x forward), for the FEMUR and TIBIA.
                    A knee is a HINGE, so the femur's roll decides which way the
                    kneecap faces, and it should face along the direction the
                    knee actually bends -- i.e. the roll reference should be the
                    NORMAL of the hip-knee-ankle plane. That normal is exactly
                    this function's own bend-plane axis, already computed one
                    line above for the femur rotation.
    `sole_axis`   = geom.lateral, for the FOOT.
                    A foot's roll is not a hinge question, it is the orientation
                    of the SOLE, which should stay flat on the floor however
                    abducted the leg is. `geom.lateral` is HORIZONTAL, which is
                    the whole reason it works here.

    WHY `lateral` WAS WRONG FOR THE LEG, and it is a continuous error rather than
    a corner case: `geom.lateral` equals the bend-plane normal only for a purely
    SAGITTAL leg. As the leg abducts the two diverge by the abduction angle.
    Measured as the angle between them over every `plant_foot` call each
    authoring script makes:

        dribble 22.47 deg (mean 18.82)   layup 56.53 (mean 7.94)
        block   18.11        ( 9.64)     behindtheback 12.74 ( 5.93)
        contest  9.27        ( 6.69)     steal          7.47 ( 2.88)
        jabstep  6.24        ( 5.18)

    So every clip authored before #338 carries a kneecap pointing up to 22 deg
    (56 deg for the layup) away from where its knee bends.

    THE SIGN IS THE WHOLE BALLGAME, and #338 proposed it backwards. The issue
    suggests passing `axis` -- the vector this function already computes as
    `dir_ankle.cross(geom.forward)`. That vector is 180.000000 deg from
    `geom.lateral` for a sagittal leg, so passing it as written would roll every
    leg bone 180 deg (the same catastrophe #320 measured when it tried negating
    `lateral`). `forward x dir_ankle` -- the NEGATION, which is what
    `hinge_axis` is -- measures 0.000000 deg from `geom.lateral` for a sagittal
    leg. It therefore agrees with the old behaviour exactly where the old
    behaviour was right, and diverges only where it was wrong.

    WHY THE FOOT DID NOT COME ALONG, mutation-proven rather than argued: handing
    `hinge_axis` to the foot as well -- #338's one-line fix applied uniformly --
    rolls the planted sole out of horizontal by 22.16 deg on the dribble and
    46.35 deg on the layup, i.e. the character stands on the edges of their feet.
    Every other gate in the library stays green through that, `verify_grounded`
    included: it measures ankle and toe HEIGHTS, and a foot rolled about its own
    long axis keeps both. `selftest_anim_lib`'s `sole_stays_level_*` is the gate
    that now catches it.

    WHAT BOUNDS `sole_axis`, since it keeps a fixed axis rather than a derived
    one: its conditioning depends on `toe_dir`, NOT on how abducted the leg is.
    `aim_matrix` Gram-Schmidts it against `toe_dir`, so it degenerates only if
    the toe points along `geom.lateral` -- a foot aimed sideways, within
    `BEND_PLANE_MIN_SIN` of it. No authored clip does that (all pass a
    forward-ish `toe_dir`), and for any `toe_dir` in the sagittal plane the
    residual is exactly `lateral`, giving 0.0000 deg of sole tilt. A move that
    genuinely needs a sideways-pointing foot -- some defensive-slide shapes --
    would fall into `aim_matrix`'s deterministic world-axis fallback and should
    pass an explicit sole reference instead. That is the known limit; it is not
    reachable from anything in the tree today.

    A CONSEQUENCE WORTH KNOWING: the femur solve does not read the lateral axis
    at all, and since #338 neither does the femur or tibia ROLL. `geom.lateral`
    survives here only as the FOOT's roll reference, where its sign is still
    load-bearing (flipping it rolls the foot 180 deg) but its ANATOMY is
    irrelevant. That is exactly why this call site takes `geom.lateral` and NOT
    `geom.body_right`: swapping them would roll the feet inside out while
    changing nothing about which side the foot lands on.

    The returned ankle error stays reported even though it now reads as float
    noise. A future spec that pushes a target outside reach, or a rig whose bone
    tails stop coinciding with their children's heads, shows up here as a number
    instead of as a subtly wrong pose.

    BUT IT IS ONLY REPORTED, NEVER ASSERTED (#335). This call passes
    `on_overreach="warn"`, so an out-of-reach target is CLAMPED and logged, not
    refused -- and none of the seven authoring scripts compares the reported
    `worst_ankle_ik_err_m` against a threshold. So the number is a tripwire only
    for a human who reads the log. Now that the exact value is 0.000000 a hard
    gate is finally cheap (`selftest_anim_lib` already uses 1e-4); until #335
    adds one, do not treat a green authoring run as evidence that the feet
    reached their targets.
    """
    up_leg, leg, foot_b, _toe_b = LEG_CHAIN[side]
    # The FOOT's roll reference. Horizontal, so the sole stays flat -- see "TWO
    # ROLL REFERENCES" in the docstring. Roll only; NOT `body_right`.
    sole_axis = geom.lateral

    hip_head = arm.pose.bones[up_leg].head.copy()
    to_ankle = ankle_target - hip_head
    solved = solve_two_link(to_ankle, geom.femur, geom.tibia,
                            what=f"{side} leg")
    _d, hip_offset, _interior = solved

    dir_ankle = to_ankle.normalized()
    # Bend-plane normal, perpendicular to the reach BY CONSTRUCTION -- see the
    # docstring. `forward` is the knee hint: it is the axis this library verifies
    # anatomically (`derive_axes`' toe check), so "the knee goes forward" is a
    # grounded claim rather than a sign convention.
    axis = dir_ankle.cross(geom.forward)
    if axis.length < BEND_PLANE_MIN_SIN:
        # The ankle target is directly fore or aft of the hip AT HIP HEIGHT, so
        # `forward` names no bend plane. Refuse, exactly as `aim_arm` refuses a
        # hint parallel to its reach: normalizing a zero vector here would emit
        # NaN into the exported clip, and silently picking a plane would produce
        # a wrong-but-plausible pose, which is the failure mode this library
        # exists to prevent. Unreachable for a real foot plant -- an ankle level
        # with the hip and straight ahead -- so hitting it means the SPEC is
        # wrong, not this code.
        raise SystemExit(
            f"FATAL: the {side} ankle target sits within {BEND_PLANE_MIN_SIN} rad "
            f"of directly fore/aft of the hip at hip height, so `forward` names "
            f"no knee bend plane (|dir_ankle x forward| = {axis.length:.3e}). "
            f"Give the target a vertical component (a foot plant is always below "
            f"the hip).")
    axis.normalize()
    femur_dir = Matrix.Rotation(hip_offset, 4, axis) @ dir_ankle

    # The FEMUR/TIBIA roll reference: the bend-plane normal itself, NEGATED --
    # see "TWO ROLL REFERENCES" in the docstring for why this is not `axis` as
    # written, and why the sign is the whole ballgame.
    hinge_axis = -axis

    arm.pose.bones[up_leg].matrix = aim_matrix(hip_head, femur_dir, hinge_axis)
    bpy.context.view_layer.update()
    # Re-read the knee head AFTER the femur is posed: it is the femur's tail, so
    # reading it before would aim the tibia from a stale position and quietly
    # break the IK chain. The `view_layer.update()` calls in this function are
    # load-bearing for exactly this reason -- do not "clean them up".
    knee_head = arm.pose.bones[leg].head.copy()
    arm.pose.bones[leg].matrix = aim_matrix(
        knee_head, (ankle_target - knee_head), hinge_axis)
    bpy.context.view_layer.update()

    ankle_head = arm.pose.bones[foot_b].head.copy()
    # Measured AFTER the tibia is posed and updated, so this is where the ankle
    # actually landed -- not where the solve asked it to go. See the lateral
    # limitation in the docstring; without this the shortfall is unobservable,
    # because `solved` reports the REQUEST, not the RESULT.
    ankle_err = (ankle_head - ankle_target).length
    arm.pose.bones[foot_b].matrix = aim_matrix(ankle_head, toe_dir, sole_axis)
    bpy.context.view_layer.update()

    if frame is not None:
        for bn in (up_leg, leg, foot_b):
            arm.pose.bones[bn].keyframe_insert("rotation_quaternion", frame=frame)
    return solved, ankle_err


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
    # Both operands unit, so `|axis|` is sin(angle) and the guard below is an
    # ANGLE. The hint is normalised here rather than trusted: the signature does
    # not require a unit vector (every caller today happens to pass one), and an
    # un-normalised hint would scale `|axis|` -- making a short hint trip a guard
    # that an identical-direction long one passes.
    axis = dir_wrist.cross(elbow_hint_dir.normalized())
    if axis.length < BEND_PLANE_MIN_SIN:
        # `elbow_hint_dir` is parallel -- or merely NEARLY parallel (#338) -- to
        # the reach direction, so it names no usable plane. Refuse rather than
        # pick an arbitrary one: a silently-chosen elbow plane is exactly the
        # kind of wrong-but-plausible pose this library exists to prevent.
        raise SystemExit(
            f"FATAL: elbow_hint_dir is parallel to the {side} arm's reach "
            f"direction to within {BEND_PLANE_MIN_SIN} rad, so it does not "
            f"define a bend plane (|reach x hint| = {axis.length:.3e}). Pass a "
            f"hint that points across the reach (e.g. down/outward), not along "
            f"it.")
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
#:   startup  -> ease_in      the weight gathers, then goes: the tell, then snap
#:   active   -> ease_out     explode out of it, decelerate into the recovery
#:   recovery -> ease_in_out  a settle back toward neutral, if a later pose exists
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

#: Used for a keypose whose label is not a known phase. Deliberately smoothstep:
#: an unlabelled or cyclic timeline should glide, and that is what the timeline
#: did before `PHASE_EASING` existed.
DEFAULT_EASING = ease_in_out

# There is deliberately NO bare `ease` alias. It would be a footgun: passing
# `easing=lib.ease` to `bake_timeline` overrides the per-phase mapping with
# smoothstep across every segment, silently reinstating the glide-into-commitment
# defect this mapping exists to prevent. Name the curve you actually want.


def resolve_easing(keypose, override=None):
    """The easing for the segment starting at `keypose`.

    Precedence, most specific first: an explicit `override` (whole-timeline
    escape hatch) > the keypose's own `easing` > `PHASE_EASING` by label >
    `DEFAULT_EASING`.
    """
    chosen = override
    if chosen is None:
        chosen = getattr(keypose, "easing", None)
    if chosen is None:
        chosen = PHASE_EASING.get(str(keypose.label).strip().lower(), DEFAULT_EASING)
    if not callable(chosen):
        # `Keypose(**channels)` means a channel named `easing` is swallowed by
        # the kwarg instead of becoming a channel. Without this the failure is a
        # `TypeError: 'float' object is not callable` raised deep inside
        # interpolation, nowhere near the keypose that caused it.
        raise SystemExit(
            f"FATAL: easing for keypose {keypose.label!r} is {chosen!r}, which is "
            f"not callable. A channel may not be named 'easing', 'label' or "
            f"'time_s' -- those are Keypose's own parameters. Rename the channel.")
    return chosen


def interp_channels(keyposes, t_s, easing=None):
    """Channel values at `t_s`, for EVERY channel named anywhere in the timeline.

    Resolution is PER CHANNEL, against the keyposes that actually define that
    channel -- not against the bracketing segment. So a channel is interpolated
    between its own neighbouring definitions, and held flat before the first and
    after the last. It is never absent, and never lerped toward 0; silently
    lerping an absent channel toward 0 is a very easy way to author a limb that
    drifts to the origin.

    Per-channel resolution is what makes the returned dict TOTAL, and that
    matters more than it looks. `bake_timeline` evaluates t_s=0.0 on its first
    frame, which lands exactly on the opening keypose. Resolving against the
    segment would return only that keypose's channels, so a channel introduced
    later (a steal's `reach_extend_m`, first named on Active) would be missing
    on frame 0 alone -- a KeyError if the caller indexes, or, with the
    `.get(key, 0.0)` idiom this docstring's own warning invites, one frame of
    limb-at-origin followed by a jump. Every clip has a frame 0.

    It also removes a discontinuity: a channel defined on keyposes 1 and 4 but
    not 2 and 3 used to snap to keypose 4's value at the start of segment 3->4.
    Now it eases across the whole 1->4 span.

    Where every keypose defines every channel -- the common case -- this is
    identical to resolving against the segment.

    `easing=None` resolves per segment via `resolve_easing`; pass a callable to
    force one curve across the whole timeline.
    """
    if not keyposes:
        raise SystemExit("FATAL: empty keypose timeline")
    ordered = sorted(keyposes, key=lambda k: k.time_s)

    out = {}
    for key in {k for kp in ordered for k in kp.channels}:
        defs = [kp for kp in ordered if key in kp.channels]
        if t_s <= defs[0].time_s:
            out[key] = defs[0].channels[key]
            continue
        if t_s >= defs[-1].time_s:
            out[key] = defs[-1].channels[key]
            continue
        for a, b in zip(defs, defs[1:]):
            if a.time_s <= t_s <= b.time_s:
                span = b.time_s - a.time_s
                # The easing comes from the keypose the channel was last DEFINED
                # at, so each channel's motion is shaped by the phase it left --
                # a three-phase move gets load-and-snap into Active and a settle
                # out of it without the per-move spec restating that every time.
                shape = resolve_easing(a, easing)
                u = 0.0 if span <= 0.0 else shape((t_s - a.time_s) / span)
                out[key] = a.channels[key] + (b.channels[key] - a.channels[key]) * u
                break
    return out


def bake_timeline(arm, keyposes, apply, f0, f1, fps, easing=None):
    """Walk frames `f0..f1`, interpolate the timeline, and let `apply` pose+key.

    `apply(frame, t_s, channels)` is the move's own spec. It is called with the
    scene already on `frame`, so it can read the source clip's pose for that
    frame and compose onto it.

    Logs the easing resolved for each segment. That log line is what keeps the
    label-driven `PHASE_EASING` default honest: a wrong curve shows up as a
    readable line in the authoring run rather than as a clip that feels off.

    Raises if the timeline runs past the frame range. `f0`/`f1` come from the
    SOURCE FBX, while the keypose times come from the move's tick table, and the
    two are authored independently -- so a timeline that overruns is a realistic
    mistake. Untrapped it truncates silently: the clip just holds the last
    in-range interpolation, the Recovery pose never appears, and every gate in
    this library still passes, because none of them know what the timeline
    intended.
    """
    if not keyposes:
        raise SystemExit("FATAL: empty keypose timeline")
    ordered = sorted(keyposes, key=lambda k: k.time_s)

    span_s = (f1 - f0) / fps
    last_s = ordered[-1].time_s
    if last_s > span_s + 1e-9:
        raise SystemExit(
            f"FATAL: keypose timeline ends at {last_s:.3f}s but frames "
            f"{f0}..{f1} at {fps} fps only cover {span_s:.3f}s. The last "
            f"{last_s - span_s:.3f}s -- including keypose {ordered[-1].label!r} "
            f"-- would be silently truncated. Extend the frame range or shorten "
            f"the timeline.")
    if last_s < span_s - 1e-9:
        # Legitimate (the tail holds the final pose), but worth seeing.
        log(f"NOTE: timeline ends at {last_s:.3f}s, frames cover {span_s:.3f}s; "
            f"the final {span_s - last_s:.3f}s holds {ordered[-1].label!r}")

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
    that failure structurally absent.

    SCOPE: this inspects the ACTION, before export, so it does NOT verify export
    scope -- it cannot see `export_fbx`'s `object_types` or `add_leaf_bones`. An
    earlier version of this docstring claimed otherwise; it was wrong.

    Because these scripts key into the source action and the Mixamo sources
    already key all 52 non-terminator bones, the `missing` branch passes by
    construction on a healthy source. That is fine -- it is a source-swap and
    narrowing tripwire, not a check on what the authoring posed. To give it
    teeth, PASS `expected_count`: without it the rig/source-change branch below
    never runs, which is the difference between a live gate and a decoration.

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

    SCOPE -- read this before trusting it. This does NOT guard `aim_matrix`
    against a non-unit basis, despite the obvious appeal of that reading.
    MEASURED (#315 review): scaling `aim_matrix`'s x column 2x leaves the number
    this reports bit-unchanged at 4.8e-7, and the selftest fully green.

    The reason is that scale is never keyed by these scripts, while the source
    action carries scale CHANNELS for every posed bone -- measured 156 on
    `Dribble.fbx`, 52 bones x 3 axes. So the `frame_set` below re-drives pose
    scale from the SOURCE curves, discarding whatever the authoring wrote,
    before a single measurement is taken. `aim_matrix` asserts its own
    orthonormality at construction instead; that is the real guard.

    What this DOES prove is worth keeping: that the source clip being authored
    into carries no unexpected pose scale of its own, i.e. it is a source-
    integrity tripwire that fires on a bad or rescaled input FBX. Measured
    baseline on the untouched source: 2.4e-7 off unit.

    It checks the POSE rather than the fcurve list deliberately: "assert no
    scale fcurves exist" would fail on every authoring run (see the 156 above)
    while catching nothing.
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


def verify_airborne(arm, frames, min_hip_rise_m, geom, ref_height=None):
    """The character actually LEAVES the ground during `frames`.

    `verify_grounded` is deliberately skipped for the airborne segment of a
    jump shot, block, or layup -- there IS no support foot to check there. But
    skipping the check is not the same as having no check: without a positive
    assertion that the body actually rose, a layup that never leaves the floor
    (a floor-level reach with the same silhouette as the intended jump) would
    sail through authoring with NO proof at all for that segment, because
    every other gate in this module is blind to it (`verify_grounded` isn't
    run there by design, and nothing else measures vertical excursion). This
    is "replace the proof, do not delete it": the Startup/Recovery segments
    keep `verify_grounded`, and this fills the Active-segment gap it leaves.

    Definition: take the HIPS bone head height along `geom.up` at each frame in
    `frames`, and assert the peak exceeds `ref_height` by at least
    `min_hip_rise_m` (metres, converted through `geom.to_m`).

    `ref_height` is REQUIRED, not derived from `frames` itself. The obvious
    shortcut -- default to `min(heights)` when the caller passes nothing -- is
    wrong by construction: if `frames` covers only the airborne window, every
    sample in it is already airborne, so its minimum is itself elevated above
    the true ground level, and comparing the window's max against the
    window's own min proves nothing (a perfectly flat, floor-level reach would
    pass with a "rise" of 0.0 read against itself). The caller must instead
    pass the Hips height measured on a frame it KNOWS is grounded (e.g. the
    Startup frame the Active window departs from), so the rise is measured
    against a real, independently-established baseline.
    """
    if ref_height is None:
        raise SystemExit(
            "FATAL: verify_airborne requires an explicit ref_height (the Hips "
            "height measured on a known-grounded frame). Defaulting to "
            "min(heights) over an all-airborne window would compare the peak "
            "against itself and pass vacuously.")
    scene = bpy.context.scene
    heights = []
    with preserve_frame():
        for f in frames:
            scene.frame_set(f)
            heights.append(arm.pose.bones[HIPS].head.dot(geom.up))
    rise_m = geom.to_m(max(heights) - ref_height)
    report("hip_rise_m", f"{rise_m:.4f}")
    if rise_m < min_hip_rise_m:
        raise SystemExit(
            f"FATAL: hips rose only {rise_m:.4f} m above the {geom.to_m(ref_height):.4f} m "
            f"reference (required >= {min_hip_rise_m} m) -- the character never "
            f"left the ground; a layup/block that stays grounded is a floor-level "
            f"reach, not a jump.")


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
