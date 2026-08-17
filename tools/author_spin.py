"""Author `spin` as a single-polarity keypose clip in headless Blender (#310).

Run:
    "$BLENDER" --background --python-exit-code 1 \
        --python tools/author_spin.py -- <src.fbx> <out.fbx>

`--python-exit-code 1` is MANDATORY. Blender's `--background --python` exits 0
even on an uncaught traceback, so without it a crashed authoring run reports
success to the pipeline.

===============================================================================
TRAP A -- THE CLIP MUST NOT ROTATE THE ROOT. This is the whole issue.
===============================================================================
A spin is 180 deg of body rotation, so the instinct is to key that rotation into
the clip. Do NOT. Player heading is SERVER-AUTHORITATIVE (ADR-0010) and
`Hooper.Player.SpinHeadingMath` drives the arc as gameplay state, integrated
into `Move()`. A clip that ALSO rotated the root would compose
engine-rotation x clip-rotation on every role -- and the client's remote copy
composes a DIFFERENT engine rotation (its broadcast phase/heading is ~1 RTT
stale), so the two would visibly disagree only under network conditions. That
is a defect you cannot see in a single-instance visual check, which is exactly
why it is gated mechanically here and again in
`tools/rebuild_spin_clips.gd` and `tests/integration/SpinAnimTest.cs`.

This file goes one step further than handoff 10's "leave Hips rotation
untouched": it PINS the Hips rotation to its frame-`F0` value and KEYS it on
every frame, so "the clip does not rotate the root" is true BY CONSTRUCTION
rather than true to within `assets/Dribble.fbx`'s own uncontrolled idle sway.
Two consequences, both deliberate:

  - The exported Hips rotation track is CONSTANT, which is precisely what
    `animation/remove_immutable_tracks` drops. `assets/spin_authored.fbx.import`
    sets it to `false`, and `rebuild_spin_clips.gd`'s G4 re-checks the track
    actually survived rather than trusting the flag (the #297 lesson).
  - Nothing else in this file may touch the Hips basis. `lib.drop_hips` is
    deliberately not used; `apply()` writes the Hips matrix itself.

The VISUAL read handoff 10 asks for -- "the shoulders lead, the hips follow,
the body shields the ball" -- is shoulder-relative-to-hip twist, which is
exactly what can be keyed safely. It lives on Spine/Spine1/Spine2 (a third
each, so no single vertebra carries 30 deg of yaw), and
`_verify_hips_do_not_yaw` proves the hip span itself never turns.

===============================================================================
TRAP B -- UNHANDED, AND FOR A REASON THAT IS NOT "IT HAS NO DIRECTION"
===============================================================================
`MoveAnimResolver.HandedMoves`' own docstring: "Crossover, BehindTheBack and
BetweenTheLegs [swap at Active-entry]; SPIN SWAPS ON THE LAST ACTIVE TICK
instead". So `OriginHand`'s phase-conditioned formula
(`Startup -> ballHand`, else `Opposite(ballHand)`) is WRONG for spin's Active
phase, where the ball is still in the ORIGINAL hand for 5 of its 6 ticks.
Adding `spin` to `HandedMoves` would ship a clip that is correct in Startup and
INVERTED afterwards -- a state that exists, plays cleanly, and telegraphs the
wrong side. `SpinAnimTest`'s `spin-stays-unsuffixed` scenario is the standing
regression guard against exactly that edit.

CONSEQUENCE FOR THIS FILE, and it shapes the whole arm spec: the clip may
encode NO hand-side claim at all. Both arms are therefore posed from ONE set of
mirrored channels -- the same simplification hesitation / step-back / retreat
dribble / jab step / in-and-out all make. There is no "off-arm across the
chest" here even though handoff 10's prose describes one: an asymmetric arm
pose IS a hand-side claim, and this clip is not entitled to make one.

The shield read survives that intact, because BOTH arms clamped in tight and
low against the hips is a body-shield silhouette, and it AGREES with the ball's
actual authoritative path: `BallController` gives spin
`BallSweepPath.BodyShield`, whose `CrossoverBallSweep.ForwardOffset` pulls the
in-hand ball from `DribbleForwardOffset` (0.5 m) to 0.05 m at the sweep peak --
tucked tight, still marginally in front, never extended. `arm_fore_m` below
follows that curve down to and through zero for the same window. A clip that
put the hands out front while the ball tucked in would be an actively false
read about whether the ball is stealable.

===============================================================================
WHAT *IS* ENCODED DIRECTIONALLY, stated rather than hidden
===============================================================================
The SPINE TWIST and the PIVOT/TRAIL foot assignment are tied to the ROTATION
direction (`Spin.SpinDirection`, +/-1), which this single clip does not carry.
Handoff 10 weighed this and ruled to ship unhanded anyway (ADR-0014, on the
record): "a spin's legibility comes from the ROTATION DIRECTION, which the
engine's authoritative heading already shows". So for one of the two polarities
the shoulders counter-rotate the 'wrong' way relative to a turn the player can
already see the body performing.

That is a real, accepted residual -- and it is a DIFFERENT and smaller class
than the hand-side false read #255/#282 shipped, because the authoritative
heading is rendering the turn either way. If it is ever revisited, the correct
fix is a NEW resolver axis keyed on `Spin.SpinDirection` (constant across the
move, so no `OriginHand`-style phase correction), NOT `HandedMoves`.

===============================================================================
FEET: THE PIVOT SKIDS IN WORLD SPACE, AND THAT IS NOT THIS CLIP'S BUG
===============================================================================
RIGHT is the PIVOT (lead, weighted) foot and LEFT the TRAIL foot that leaves
the floor and swings through -- the same "RIGHT is the weighted foot" limb
assignment every dribble-family script in this batch uses, so cross-move
contrast lives in what the body DOES, never in which limb is which.

"Planted" here means planted in CLIP space: the pivot ankle holds its local
position and its height. In WORLD space it cannot stay put, because the engine
rotates the whole node ~180 deg about its own origin and every local offset
sweeps with it. Counter-animating that in the clip would be re-introducing
trap A through the feet, so it is not done; the world-space pivot skid is an
accepted artefact of a rotation carried authoritatively without foot IK
(ADR-0020's fidelity ceiling; feel is #173's, per ADR-0021).

`lib.verify_grounded` is still run across the WHOLE clip and needs no
sub-windowing: it measures the LOWER of the two toes per frame, which IS the
pivot foot for every frame the trail foot is airborne. `_verify_pivot_stays_down`
adds the claim `verify_grounded` cannot make on its own (that ONE NAMED foot is
the one holding the floor, rather than the two alternating), and
`_verify_trail_foot_lifts` is the POSITIVE check handoff 10 asks for in place of
merely exempting the airborne foot.

===============================================================================
WHAT THIS PRODUCES
===============================================================================
ONE Blender action, baked at 60 Hz, frame numbers ARE physics ticks:

    frames    seconds              segment
    0  -> 8   0.00000 -> 0.13333   Startup   (8 ticks -- the plant and load)
    8  -> 14  0.13333 -> 0.23333   Active    (6 ticks -- the turn)
    14 -> 24  0.23333 -> 0.40000   Recovery  (10 ticks -- unwind, punish window)

Eight ticks of Startup is the longest telegraph in the dribble family and the
wind-up should say so; ten of Recovery is a real punish window.

===============================================================================
COSMETIC-ONLY
===============================================================================
This script is an ASSET BUILD TOOL. It does not import, read, or feed
`Spin.DefaultFrameData`, `SpinHeadingMath`, `BallState`, `HasDribbled`, or any
PlayerController move-begin gate. It VISUALISES the spin; nothing here changes
gameplay. The tick counts below are DUPLICATED from `Spin.DefaultFrameData` for
slicing and are never read back into it -- `SpinAnimTest`'s
`spin-segment-lengths` scenario is what makes that duplication safe, by
asserting each clip's length against the C# frame data directly.
"""

import math
import os
import sys

import bpy
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import blender_anim_lib as lib  # noqa: E402  (must follow the sys.path fix)

log = lib.log

# ── clip contract, 60 Hz (Spin.DefaultFrameData) ─────────────────────────────
FPS = 60
STARTUP_TICKS = 8
ACTIVE_TICKS = 6
RECOVERY_TICKS = 10
TOTAL_TICKS = STARTUP_TICKS + ACTIVE_TICKS + RECOVERY_TICKS  # 24

F0 = 0
F1 = TOTAL_TICKS  # inclusive -- frame numbers ARE physics ticks

# Segment boundaries as frame numbers, named once so the gates below and
# rebuild_spin_clips.gd's slice windows cannot drift apart silently.
STARTUP_END = STARTUP_TICKS                # 8
ACTIVE_END = STARTUP_TICKS + ACTIVE_TICKS  # 14

ACTION_NAME = "spin"

# ── stance geometry, metre-denominated ────────────────────────────────────────
# Reused from author_stepback.py / author_hesitation.py's measurement on this
# SAME rig off this SAME source clip (Y Bot: femur/tibia/foot are rig-intrinsic;
# the stance geometry is source-intrinsic to Dribble.fbx's own neutral pose).
NEUTRAL_HIP_TO_ANKLE_M = 0.62
STANCE_HALF_WIDTH_M = 0.12
PIVOT_FORE_M = 0.06    # the pivot (right) foot sits slightly ahead...
TRAIL_FORE_M = -0.10   # ...and the trail (left) foot slightly behind.

# Torso pitch sign, SAME CONVENTION as author_stepback.py / author_hesitation.py:
# a POSITIVE rotation is BACKWARD (counter-rotating off the source's own ~30 deg
# forward crouch), so a NEGATIVE `torso_back_deg` below digs DEEPER into it.
# NOT assumed inherited -- `_verify_torso_pitch_sign_is_backward` re-derives it
# on THIS clip's own body_right/forward axes.
TORSO_PITCH_SIGN = 1.0

# The twist is distributed over three vertebrae rather than loaded onto one, so
# no single joint carries the full 30 deg of yaw. They compound (each is the
# next one's parent), so the SHOULDER-relative-to-HIP twist the gates measure is
# the full authored `twist_deg`.
TWIST_BONES = ("mixamorig:Spine", "mixamorig:Spine1", "mixamorig:Spine2")

# ── keypose channel table ─────────────────────────────────────────────────────
# Columns:
#   time_s, label,
#   hip_drop_m     (+up, VERTICAL delta off the fixed hips_base anchor. NO
#                   fore/aft or lateral channel exists anywhere in this table --
#                   the exit burst is the engine's (CrossoverBurstMath), so any
#                   horizontal hip travel here would be double-counted; see
#                   `_verify_hips_stay_in_place`)
#   twist_deg      (SIGNED shoulder-span-relative-to-hip-span rotation about
#                   `up`, right-hand rule. Positive at the load, negative once
#                   the hips have passed the shoulders)
#   torso_back_deg (magnitude of the BACKWARD counter-rotation off the source's
#                   own ~30 deg forward crouch; NEGATIVE = deeper crouch.
#                   TORSO_PITCH_SIGN supplies the sign)
#   pivot_fore_m   (RIGHT foot: forward offset off its base spot. Held at 0.0
#                   EVERYWHERE -- this foot is the pivot and does not travel)
#   trail_fore_m, trail_up_m
#                  (LEFT foot: forward offset / vertical clearance off its own
#                   base spot -- this is the foot that leaves the floor and
#                   swings through)
#   arm_fore_m, arm_lat_m, arm_height_m
#                  (BOTH hands -- mirrored. Unhanded, see the module docstring's
#                   TRAP B section; `arm_fore_m` tracks BodyShield's own tuck)
_KEYPOSES_RAW = [
    # t_s,             label,      hip_drop, twist, back, pv_fore, tr_fore, tr_up, arm_fore, arm_lat, arm_h
    # Frame 0 -- entry, hard-cut from the dribble stance (no xfade on any
    # edge). The live drive: shallow crouch, feet staggered, both hands out at
    # ordinary dribble width and height, shoulders very nearly square.
    #
    # `twist` starts at +4 rather than 0 ON PURPOSE. The Startup control gate
    # (`_verify_startup_twist_does_not_reverse`, and the harness's
    # `control-spin-startup-twist-does-not-reverse`) asserts Startup is
    # SINGLE-SIGNED; a table starting at exactly 0.0 would make the sign of the
    # first sample float noise and the control unreliable for reasons that have
    # nothing to do with the clip.
    [0.00000,           "startup",   -0.02,   4.0,   2.0,   0.00,   -0.00,   0.00,   0.05,    0.16,   0.00],
    # Frame 8 -- the Startup/Active SLICE BOUNDARY: simultaneously the LAST
    # pose of `spinstartup` and the FIRST of `spinactive`. THE PLANT AND LOAD,
    # fully coiled: hips at their lowest (-0.16), both shoulders wound to
    # +30 deg off the hips, both hands pulled in tight and slightly behind the
    # hip line (arm_fore -0.02 -- the shield), trail foot still DOWN (a load is
    # not a lift). Eight ticks of this is the telegraph.
    [STARTUP_END / FPS, "active",    -0.16,  23.0,  -6.0,   0.00,   -0.12,   0.00,  -0.02,    0.10,   0.06],
    # Frame 14 -- the Active/Recovery boundary: the turn is through. The hips
    # have PASSED the shoulders, so the twist has reversed to -30 deg; the trail
    # foot is at the top of its swing (+0.11 up, and already carried forward to
    # +0.06); the ball is still tucked at its tightest (arm_fore -0.04). Hips
    # have begun to rise out of the load (-0.13).
    [ACTIVE_END / FPS,  "recovery",  -0.13, -39.0,  -4.0,   0.00,    0.06,   0.11,  -0.04,    0.09,   0.08],
    # Frame 24 -- unwound and slightly off balance. Shoulders settle to very
    # nearly square (-5, not 0: a hair of the unwind is retained, and it keeps
    # Recovery single-signed for the same float-noise reason frame 0 does); the
    # NEW lead foot plants AHEAD of the pivot (trail_fore +0.14 off a base that
    # started at -0.10, so the left ankle finishes in front of the right); the
    # ball emerges back to ordinary dribble width and height; hips rise +0.08
    # off the load, still below the entry stance -- ten ticks of punish window,
    # not a clean landing.
    [TOTAL_TICKS / FPS, "recovery",  -0.08,  -5.0,   6.0,   0.00,    0.14,   0.00,   0.06,    0.16,   0.00],
]

_CHANNEL_NAMES = (
    "hip_drop_m", "twist_deg", "torso_back_deg", "pivot_fore_m",
    "trail_fore_m", "trail_up_m", "arm_fore_m", "arm_lat_m", "arm_height_m",
)

# Elbow bend-plane hints, mirrored per side. Weighted OUTWARD much harder than
# up, because a tucked shield's elbows flare out to the sides rather than
# tucking under -- and because a nearly-vertical hint on an arm reaching almost
# straight down would sit close to `aim_arm`'s parallel-to-reach refusal.
ELBOW_HINT_UP = 0.2
ELBOW_HINT_LAT = 0.8

# ── proof thresholds ──────────────────────────────────────────────────────────
# Startup-end(f8)-vs-Recovery-end(f24) legibility floor (#296). Matches the
# other scripts' 15.0 deg floor.
STARTUP_END_VS_RECOVERY_END_MIN_DEG = 15.0

# The pivot foot holds the floor throughout. Matches author_hesitation.py /
# author_retreatdribble.py / author_contest.py's tolerance -- appropriate here
# because `pivot_fore_m` and the pivot's vertical are both fixed by
# construction, so any measured deviation is IK slack, not intent.
GROUND_BAND_TOL_M = 0.02

# The TRAIL foot's positive check -- handoff 10: "assert the trailing foot DOES
# leave the floor as a positive check rather than just exempting it". Authored
# peak is +0.11 m at frame ACTIVE_END; floor set well under that so a retune has
# room without silently regressing to a two-footed shuffle.
TRAIL_LIFT_MIN_M = 0.06

# The twist trajectory, in DEGREES of shoulder-span-relative-to-hip-span
# rotation about `up`. Authored table reaches +30 at STARTUP_END and -30 at
# ACTIVE_END; floors sit at 2/3 of that so a retune has room.
TWIST_LOADED_MIN_DEG = 20.0    # at STARTUP_END, shoulders LEAD (positive)
TWIST_PASSED_MIN_DEG = 20.0    # at ACTIVE_END, hips have PASSED (negative)

# TRAP A's own gate, in DEGREES: the largest excursion of the HIP SPAN's yaw
# about `up` across the whole clip, measured against frame F0. This is the
# GEOMETRIC form of "the clip does not rotate the root" and is independent of
# whether a Hips rotation TRACK exists at all -- so it survives an importer that
# drops the (constant) track, which is the one thing a track-level check cannot.
#
# Zero by construction (`apply()` pins the Hips basis), so the tolerance is pure
# float/IK noise headroom, not a budget. If this ever reads a real number,
# something started rotating the root and the whole point of the issue is lost.
HIPS_YAW_TOL_DEG = 0.5

# Degeneracy floor for the signed-angle helper, as a length in ARMATURE UNITS
# after projecting out the `up` component. The real spans read ~37 units (a
# shoulder span on this rig), so honest input clears this by ~4 decades. Set
# from the same reasoning as blender_anim_lib's LANDMARK_MIN_COS: a guard placed
# at exact zero fires decades after the answer it protects stopped meaning
# anything (#338).
SPAN_MIN_LEN = 1e-2


def _keyposes_for_lib():
    """`_KEYPOSES_RAW` translated into `blender_anim_lib.Keypose` objects."""
    out = []
    for row in _KEYPOSES_RAW:
        t_s, label = row[0], row[1]
        channels = dict(zip(_CHANNEL_NAMES, row[2:]))
        out.append(lib.Keypose(t_s, label, **channels))
    return out


def _signed_angle_deg(a, b, axis):
    """Signed angle from `a` to `b` about `axis`, right-hand rule, in DEGREES.

    Both vectors are projected onto the plane perpendicular to `axis` first, so
    this measures pure YAW and is blind to how much either span pitches.

    RAISES rather than returning 0.0 on a degenerate projection
    (measurement-helpers-must-poison-on-failure, #305). A helper that degraded
    to 0.0 here would make `_verify_hips_do_not_yaw` print a confident PASS
    while measuring nothing -- and 0.0 is that gate's PASSING value, so the
    degradation would be silent in exactly the direction that hides a defect.
    """
    n = axis.normalized()
    pa = a - n * a.dot(n)
    pb = b - n * b.dot(n)
    if pa.length < SPAN_MIN_LEN or pb.length < SPAN_MIN_LEN:
        raise SystemExit(
            f"FATAL: a span projected to {pa.length:.6f} / {pb.length:.6f} "
            f"armature units in the plane perpendicular to `up` (need >= "
            f"{SPAN_MIN_LEN}) -- it is essentially vertical, so it names no yaw. "
            f"Refusing rather than reporting 0.0 deg, which is this gate's "
            f"PASSING value.")
    pa.normalize()
    pb.normalize()
    return math.degrees(math.atan2(pa.cross(pb).dot(n), pa.dot(pb)))


def _hip_span(arm):
    """Left-to-right hip span in ARMATURE space, from the POSED rig."""
    b = arm.pose.bones
    return b["mixamorig:RightUpLeg"].head - b["mixamorig:LeftUpLeg"].head


def _shoulder_span(arm):
    """Left-to-right shoulder span in ARMATURE space, from the POSED rig.

    Uses the `<side>Arm` heads (the humerus roots), NOT `<side>Shoulder` (the
    clavicles): `apply()` pins the clavicles to rest identity, so the humerus
    heads are driven purely by the spine chain this file twists, which is the
    quantity the gates are actually claiming something about.
    """
    b = arm.pose.bones
    return b["mixamorig:RightArm"].head - b["mixamorig:LeftArm"].head


def _twist_deg(arm, up):
    """Signed shoulder-relative-to-hip yaw, in DEGREES. The move's whole read.

    This is measured on the POSED rig rather than read back off the channel
    table, so it proves what the spine composition ACTUALLY achieved -- a twist
    applied about the wrong axis, or cancelled by the torso pitch, reads here as
    a number the gates reject rather than as a table that looks right.
    """
    return _signed_angle_deg(_hip_span(arm), _shoulder_span(arm), up)


def _verify_sign_convention(up):
    """A POSITIVE rotation about `up` must INCREASE `_signed_angle_deg`.

    Five lines, and it pins the one thing the rest of this file's twist gates
    silently assume: that `Matrix.Rotation(+theta, 4, up)` and
    `_signed_angle_deg(..., up)` agree on handedness. If they disagreed, every
    twist threshold below would be satisfied by a clip twisting the OTHER way
    and no other gate here would notice -- the abs()-blind-to-sign failure this
    repo has already shipped once (#339).
    """
    probe = Vector((1.0, 0.0, 0.0))
    probe = probe - up.normalized() * probe.dot(up.normalized())
    if probe.length < SPAN_MIN_LEN:
        probe = Vector((0.0, 0.0, 1.0))
        probe = probe - up.normalized() * probe.dot(up.normalized())
    rotated = Matrix.Rotation(math.radians(10.0), 4, up) @ probe
    measured = _signed_angle_deg(probe, rotated, up)
    lib.report("sign_convention_probe_deg", f"{measured:+.4f}")
    if abs(measured - 10.0) > 1e-3:
        raise SystemExit(
            f"FATAL: a +10 deg rotation about `up` measures {measured:+.4f} deg "
            f"through `_signed_angle_deg`. The rotation and the measurement "
            f"disagree on handedness, so every twist threshold in this file "
            f"would be satisfied by a clip twisting the WRONG WAY.")


def _spine_head_forward_m(arm, geom, forward):
    """The spine->head vector's projection along `forward`, in METRES."""
    spine_head = arm.pose.bones[lib.SPINE].head.copy()
    head_head = arm.pose.bones["mixamorig:Head"].head.copy()
    return geom.to_m((head_head - spine_head).dot(forward))


def _verify_torso_pitch_sign_is_backward(arm, geom, body_right, forward):
    """A positive `TORSO_PITCH_SIGN` rotation must tip the torso BACKWARD.

    Verbatim technique from author_hesitation.py / author_stepback.py: rotate
    the spine->head vector by the signed pitch at a single frame (no baking, no
    two-frame comparison, so the source clip's own drift cannot contaminate the
    reading) and check the forward component SHRANK. This file authors NEGATIVE
    `torso_back_deg` through the load, so a flipped sign here would make the
    spin STAND UP into its plant instead of digging into it.
    """
    spine_head = arm.pose.bones[lib.SPINE].head.copy()
    head_head = arm.pose.bones["mixamorig:Head"].head.copy()
    vec = head_head - spine_head
    rot = Matrix.Rotation(math.radians(TORSO_PITCH_SIGN * 10.0), 4, body_right)
    delta_fore = geom.to_m(((rot @ vec) - vec).dot(forward))
    lib.report("torso_pitch_sign_fore_delta_m", f"{delta_fore:+.4f}")
    if delta_fore >= 0.0:
        raise SystemExit(
            f"FATAL: a positive TORSO_PITCH_SIGN ({TORSO_PITCH_SIGN}) rotation "
            f"moves the spine->head vector {delta_fore:+.4f} m ALONG forward, "
            f"i.e. FORWARD. This file's convention is that positive is "
            f"BACKWARD (it authors negative values to dig INTO the plant). "
            f"Flip TORSO_PITCH_SIGN.")


def _verify_hips_stay_in_place(arm, geom, hips_base, forward, body_right, frames):
    """The Hips translate along `up` ONLY -- never fore/aft, never laterally.

    Verbatim from author_hesitation.py / author_stepback.py. Zero by
    construction today (`apply()` builds the Hips target as
    `hips_base + up * hip_drop_m`, and no horizontal term exists to be nonzero)
    -- the gate's job is refusing a FUTURE edit that adds one.

    It matters more here than in most of the batch: spin's exit burst is real
    and is applied by the ENGINE (PlayerController's Spin branch composes
    CrossoverBurstMath against the exit vector snapshotted at Active-entry). A
    clip that ALSO translated the hips forward would double-count that burst,
    the display-layer twin of trap A.
    """
    tol_m = 1e-4
    scene = bpy.context.scene
    worst_fore = 0.0
    worst_lat = 0.0
    worst_frame = None
    with lib.preserve_frame():
        for f in frames:
            scene.frame_set(f)
            d = arm.pose.bones[lib.HIPS].head.copy() - hips_base
            fore = abs(geom.to_m(d.dot(forward)))
            lat = abs(geom.to_m(d.dot(body_right)))
            if max(fore, lat) > max(worst_fore, worst_lat):
                worst_frame = f
            worst_fore = max(worst_fore, fore)
            worst_lat = max(worst_lat, lat)
    lib.report("hips_horizontal_travel_fore_m", f"{worst_fore:.6f}")
    lib.report("hips_horizontal_travel_lat_m", f"{worst_lat:.6f}")
    if max(worst_fore, worst_lat) > tol_m:
        raise SystemExit(
            f"FATAL: the Hips travelled {worst_fore:.6f} m fore/aft and "
            f"{worst_lat:.6f} m laterally (frame {worst_frame}, tol {tol_m}). "
            f"This clip must be authored IN PLACE -- the spin's exit burst is "
            f"applied by the ENGINE against a snapshot taken at Active-entry, "
            f"so hip translation here would be double-counted.")


def _verify_hips_do_not_yaw(arm, up, frames):
    """TRAP A, GEOMETRICALLY: the HIP SPAN's yaw never moves off frame F0's.

    This is the gate the whole issue exists for, and it is deliberately NOT a
    check on the Hips ROTATION TRACK. A track-level check ("the track is
    constant") is blind to two things this is not: a Hips basis pinned but then
    perturbed downstream, and an importer that DROPS the (constant) track
    entirely and rest-falls the pelvis. Measuring the posed hip span catches a
    root rotation however it arrived.

    Zero by construction -- `apply()` writes the same basis on every frame -- so
    HIPS_YAW_TOL_DEG is float/IK noise headroom, not a budget.
    """
    scene = bpy.context.scene
    with lib.preserve_frame():
        scene.frame_set(frames[0])
        ref = _hip_span(arm).copy()
        worst = 0.0
        worst_frame = frames[0]
        for f in frames:
            scene.frame_set(f)
            yaw = abs(_signed_angle_deg(ref, _hip_span(arm), up))
            if yaw > worst:
                worst, worst_frame = yaw, f
    lib.report("hips_yaw_excursion_deg", f"{worst:.4f}")
    if worst > HIPS_YAW_TOL_DEG:
        raise SystemExit(
            f"FATAL: the HIP SPAN yawed {worst:.4f} deg off its frame-{frames[0]} "
            f"orientation at frame {worst_frame} (tol {HIPS_YAW_TOL_DEG}). THIS "
            f"CLIP MUST NOT ROTATE THE ROOT -- player heading is "
            f"server-authoritative (ADR-0010, SpinHeadingMath), so a clip that "
            f"also turns the body double-rotates on the authoritative roles and "
            f"fights reconciliation on the client's remote copy. Express the "
            f"turn as SHOULDER-relative-to-HIP twist (the `twist_deg` channel), "
            f"never as hip rotation.")


def _verify_twist_trajectory(arm, up):
    """Shoulders LEAD at the load, and the hips have PASSED them by Active's end.

    The move's defining read, measured on the POSED rig at the two slice
    boundaries the harness can also see. Reported at all four named instants so
    a CI log carries the whole arc rather than just the two gated values.
    """
    scene = bpy.context.scene
    with lib.preserve_frame():
        scene.frame_set(F0)
        t_f0 = _twist_deg(arm, up)
        scene.frame_set(STARTUP_END)
        t_su = _twist_deg(arm, up)
        scene.frame_set(ACTIVE_END)
        t_ac = _twist_deg(arm, up)
        scene.frame_set(F1)
        t_re = _twist_deg(arm, up)

    lib.report("twist_f0_deg", f"{t_f0:+.3f}")
    lib.report("twist_startup_end_deg", f"{t_su:+.3f}")
    lib.report("twist_active_end_deg", f"{t_ac:+.3f}")
    lib.report("twist_recovery_end_deg", f"{t_re:+.3f}")

    if t_su < TWIST_LOADED_MIN_DEG:
        raise SystemExit(
            f"FATAL: at Startup's end (frame {STARTUP_END}) the shoulders lead "
            f"the hips by only {t_su:+.3f} deg, need >= "
            f"+{TWIST_LOADED_MIN_DEG}. Handoff 10: 'both shoulders begin "
            f"rotating away from the defender (twist to ~30 deg relative to the "
            f"hips)' -- that wind-up IS the telegraph.")
    if t_ac > -TWIST_PASSED_MIN_DEG:
        raise SystemExit(
            f"FATAL: at Active's end (frame {ACTIVE_END}) the twist is "
            f"{t_ac:+.3f} deg, need <= -{TWIST_PASSED_MIN_DEG}. Handoff 10: "
            f"'shoulder twist carries through from ~+30 to ~-30 relative to the "
            f"hips'. Without the REVERSAL the clip reads as a lean, not a turn "
            f"-- and the reversal is the only thing in this clip that says the "
            f"hips came around, since the hips themselves must not rotate "
            f"(trap A).")


def _verify_startup_twist_does_not_reverse(arm, up):
    """THE CONTROL for `_verify_twist_trajectory`: Startup is SINGLE-SIGNED.

    Without it, "the twist reverses" could be satisfied by a clip that
    oscillates -- or, more realistically, by a spine composition whose sign is
    noise-dominated near zero. Sampling every Startup frame and requiring them
    all strictly positive is what makes the reversal at Active a claim about
    the MOVE rather than about where the samples happened to land.

    Also the reason the frame-0 row authors +4.0 and not 0.0: the sign of a
    sample at exactly zero is float noise.
    """
    scene = bpy.context.scene
    worst = None
    with lib.preserve_frame():
        for f in range(F0, STARTUP_END + 1):
            scene.frame_set(f)
            t = _twist_deg(arm, up)
            if worst is None or t < worst[0]:
                worst = (t, f)
    lib.report("twist_startup_min_deg", f"{worst[0]:+.3f}")
    if worst[0] <= 0.0:
        raise SystemExit(
            f"FATAL: the twist reached {worst[0]:+.3f} deg at frame {worst[1]}, "
            f"inside STARTUP. Startup must wind UP in ONE direction -- it is the "
            f"telegraph. A Startup that already reverses makes "
            f"`_verify_twist_trajectory`'s reversal claim vacuous, because the "
            f"clip would be oscillating rather than turning.")


def _verify_trail_foot_lifts(arm, geom, up, frames):
    """The POSITIVE check handoff 10 asks for: the trail foot LEAVES the floor.

    `lib.verify_grounded` takes the LOWER toe per frame, so it is deliberately
    blind to one foot lifting -- which is correct for it (the pivot is what
    holds the floor) but means nothing in this file would otherwise assert the
    swing happened at all. A spin whose trail foot never leaves the ground is a
    pivot-in-place, not a spin.
    """
    scene = bpy.context.scene
    trail_toe = lib.LEG_CHAIN["L"][3]
    with lib.preserve_frame():
        scene.frame_set(F0)
        base = arm.pose.bones[trail_toe].head.dot(up)
        peak = 0.0
        peak_frame = F0
        for f in frames:
            scene.frame_set(f)
            h = geom.to_m(arm.pose.bones[trail_toe].head.dot(up) - base)
            if h > peak:
                peak, peak_frame = h, f
    lib.report("trail_toe_peak_lift_m", f"{peak:.4f}")
    lib.report("trail_toe_peak_frame", peak_frame)
    if peak < TRAIL_LIFT_MIN_M:
        raise SystemExit(
            f"FATAL: the TRAIL (left) toe rose only {peak:.4f} m above its "
            f"frame-{F0} height (peak at frame {peak_frame}), need >= "
            f"{TRAIL_LIFT_MIN_M}. Handoff 10: 'the trailing foot comes off the "
            f"floor and swings through'. A spin with both feet planted reads as "
            f"a pivot, not a turn.")


def _verify_pivot_stays_down(arm, geom, up, frames):
    """...and the OTHER half: the PIVOT (right) foot holds the floor throughout.

    `lib.verify_grounded` proves SOME foot is down every frame; this proves it
    is the SAME, NAMED foot every frame. Without it a clip whose two feet
    alternated -- a shuffle, not a pivot -- would satisfy both
    `verify_grounded` and `_verify_trail_foot_lifts` together.
    """
    scene = bpy.context.scene
    pivot_toe = lib.LEG_CHAIN["R"][3]
    with lib.preserve_frame():
        scene.frame_set(F0)
        base = arm.pose.bones[pivot_toe].head.dot(up)
        worst = 0.0
        worst_frame = F0
        for f in frames:
            scene.frame_set(f)
            d = abs(geom.to_m(arm.pose.bones[pivot_toe].head.dot(up) - base))
            if d > worst:
                worst, worst_frame = d, f
    lib.report("pivot_toe_height_excursion_m", f"{worst:.4f}")
    if worst > GROUND_BAND_TOL_M:
        raise SystemExit(
            f"FATAL: the PIVOT (right) toe moved {worst:.4f} m vertically off "
            f"its frame-{F0} height at frame {worst_frame} (tol "
            f"{GROUND_BAND_TOL_M}). The pivot foot is what the body turns "
            f"AROUND -- if it leaves the floor the move has no pivot and "
            f"`verify_grounded` alone cannot see it, since that measures "
            f"whichever foot is lower.")


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    src, dst = argv[0], argv[1]

    arm, _src_f0, _src_f1 = lib.load_source(src, FPS)
    scene = bpy.context.scene

    geom = lib.RigGeometry(arm)
    geom.log_summary()
    # Anatomical right, derived + verified in the lib (#320). `geom.lateral` is
    # a BASIS vector that points at the character's LEFT on this rig and must
    # not be used for placement.
    body_right = geom.body_right
    up, forward = geom.up, geom.forward
    lib.report("body_right", tuple(round(v, 4) for v in body_right))

    _verify_sign_convention(up)

    lib.enter_pose_mode(arm)

    # Anchor Hips POSITION and BASIS, both captured ONCE before any of this
    # script's own posing.
    #
    # The BASIS is the trap-A pin (see the module docstring). Capturing it here,
    # from the source's frame-F0 pose, and re-writing it on every frame is what
    # makes "the clip does not rotate the root" true by construction. Note the
    # scene is still sitting on the source's own first frame at this point --
    # `load_source` set frame_start/frame_end and never stepped away from it --
    # so this IS frame F0's pose.
    scene.frame_set(F0)
    hips_base = arm.pose.bones[lib.HIPS].head.copy()
    hips_base_basis = arm.pose.bones[lib.HIPS].matrix.to_3x3().copy()

    # Base spots for the two feet, staggered fore/aft. RIGHT is the PIVOT
    # (lead, weighted) foot, LEFT the TRAIL foot that lifts and swings through
    # -- see the module docstring's FEET section.
    pivot_ankle_base = (hips_base
                        + body_right * geom.m(STANCE_HALF_WIDTH_M)
                        + forward * geom.m(PIVOT_FORE_M)
                        - up * geom.m(NEUTRAL_HIP_TO_ANKLE_M))
    trail_ankle_base = (hips_base
                        + body_right * geom.m(-STANCE_HALF_WIDTH_M)
                        + forward * geom.m(TRAIL_FORE_M)
                        - up * geom.m(NEUTRAL_HIP_TO_ANKLE_M))

    keyposes = _keyposes_for_lib()

    worst_wrist_err = 0.0
    worst_ankle_err = 0.0

    def apply(frame, _t_s, ch):
        nonlocal worst_wrist_err, worst_ankle_err

        # ---- clavicles: pinned to REST, not inherited from the source -------
        # Dribble.fbx's own Shoulder(clavicle) bones carry uncontrolled idle
        # sway; ARM_CHAIN deliberately excludes the clavicle from the two-link
        # solve, so nothing else here controls it. Pinning them also makes
        # `_shoulder_span` a pure readout of the spine twist, which is what the
        # twist gates claim to measure.
        for side in ("L", "R"):
            sh = arm.pose.bones[f"mixamorig:{'Left' if side == 'L' else 'Right'}Shoulder"]
            sh.matrix_basis = Matrix.Identity(4)
            bpy.context.view_layer.update()
            sh.keyframe_insert("rotation_quaternion", frame=frame)

        # ---- hips: VERTICAL delta off the anchor, and a PINNED basis ---------
        # TRAP A. The basis is re-written from `hips_base_basis` (frame F0's)
        # every frame and KEYED, so the exported Hips rotation track is
        # constant. `lib.drop_hips` is deliberately not used: it composes onto
        # the source's per-frame basis, which would leave Dribble.fbx's own idle
        # sway on the root of a move whose entire contract is "does not rotate
        # the root". See the module docstring.
        pb = arm.pose.bones[lib.HIPS]
        mh = hips_base_basis.to_4x4()
        mh.translation = hips_base + up * geom.m(ch["hip_drop_m"])
        pb.matrix = mh
        bpy.context.view_layer.update()
        pb.keyframe_insert("location", frame=frame)
        pb.keyframe_insert("rotation_quaternion", frame=frame)
        hips_now = pb.head.copy()

        # ---- torso pitch about the LATERAL axis (a pitch, not a twist) ------
        pitch_rad = math.radians(TORSO_PITCH_SIGN * ch["torso_back_deg"])
        lib.rotate_bone_about_head(
            arm, lib.SPINE, (Matrix.Rotation(pitch_rad, 4, body_right),), frame=frame)

        # ---- the twist: a THIRD each on Spine / Spine1 / Spine2 --------------
        # About `up`, so it is a pure yaw and does not fight the pitch above.
        # Distributed rather than loaded onto one vertebra, and they COMPOUND
        # (each is the next one's parent), so the shoulder-relative-to-hip twist
        # the gates measure is the full authored `twist_deg`. This -- not the
        # Hips -- is where a spin's rotation is allowed to live.
        twist_rad = math.radians(ch["twist_deg"] / len(TWIST_BONES))
        for bone in TWIST_BONES:
            lib.rotate_bone_about_head(
                arm, bone, (Matrix.Rotation(twist_rad, 4, up),), frame=frame)

        # ---- legs: the pivot holds, the trail lifts and swings ---------------
        # Anchored to the FIXED bases, not to `hips_now` -- a planted move keeps
        # the FLOOR fixed and lets the hips move relative to it
        # (author_contest.py's lesson: anchoring to the live hips made a crouch
        # lift the feet by exactly the crouch depth).
        toe_dir = (forward * 0.90 - up * 0.44).normalized()

        pivot_ankle = pivot_ankle_base + forward * geom.m(ch["pivot_fore_m"])
        _solved, pivot_err = lib.plant_foot(arm, "R", pivot_ankle, toe_dir, geom, frame=frame)
        worst_ankle_err = max(worst_ankle_err, pivot_err)

        trail_ankle = (trail_ankle_base
                       + forward * geom.m(ch["trail_fore_m"])
                       + up * geom.m(ch["trail_up_m"]))
        _solved, trail_err = lib.plant_foot(arm, "L", trail_ankle, toe_dir, geom, frame=frame)
        worst_ankle_err = max(worst_ankle_err, trail_err)

        # ---- arms: ONE set of channels, MIRRORED per side --------------------
        # Unhanded by contract (module docstring, TRAP B): an asymmetric arm
        # pose would be a hand-side claim this clip is not entitled to make.
        # `arm_fore_m` goes NEGATIVE through the shield window, matching
        # BallSweepPath.BodyShield pulling the in-hand ball in tight.
        for side, lat_sign in (("R", 1.0), ("L", -1.0)):
            target = (hips_now
                      + forward * geom.m(ch["arm_fore_m"])
                      + body_right * geom.m(lat_sign * ch["arm_lat_m"])
                      + up * geom.m(ch["arm_height_m"]))
            hint = (up * ELBOW_HINT_UP + body_right * (lat_sign * ELBOW_HINT_LAT)).normalized()
            err_u = lib.aim_arm(arm, side, target, hint, geom, frame=frame)
            worst_wrist_err = max(worst_wrist_err, err_u)

    lib.bake_timeline(arm, keyposes, apply, F0, F1, FPS)

    bpy.ops.object.mode_set(mode="OBJECT")
    scene.frame_start, scene.frame_end = F0, F1

    lib.report_ankle_ik("worst_ankle_ik_err_m", geom.to_m(worst_ankle_err))
    lib.report("worst_wrist_ik_err_m", f"{geom.to_m(worst_wrist_err):.6f}")

    all_frames = list(range(F0, F1 + 1))
    lib.verify_all_bones_keyed(arm, expected_count=52)
    lib.verify_pose_unscaled(arm, all_frames)

    # ── trap A, from the only angle that survives an importer change ─────────
    _verify_hips_do_not_yaw(arm, up, all_frames)
    _verify_hips_stay_in_place(arm, geom, hips_base, forward, body_right, all_frames)

    # ── the turn itself: the claim, then its control ────────────────────────
    _verify_torso_pitch_sign_is_backward(arm, geom, body_right, forward)
    _verify_twist_trajectory(arm, up)
    _verify_startup_twist_does_not_reverse(arm, up)

    # ── the footwork: one foot holds, the other swings ──────────────────────
    lib.verify_grounded(arm, all_frames, GROUND_BAND_TOL_M, geom)
    _verify_pivot_stays_down(arm, geom, up, all_frames)
    _verify_trail_foot_lifts(arm, geom, up, list(range(STARTUP_END, ACTIVE_END + 1)))

    # ── #296 legibility ──────────────────────────────────────────────────────
    lib.verify_pose_distinct(
        lib.snapshot_pose(arm, STARTUP_END), lib.snapshot_pose(arm, F1),
        STARTUP_END_VS_RECOVERY_END_MIN_DEG, label="startup_end_vs_recovery_end")

    lib.export_fbx(arm, dst, ACTION_NAME)
    log(f"wrote {dst}")


if __name__ == "__main__":
    main()
