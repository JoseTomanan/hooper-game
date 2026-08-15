"""Author `betweenthelegs` as a two-polarity keypose clip in headless Blender (#309).

Run:
    "$BLENDER" --background --python-exit-code 1 \
        --python tools/author_betweenthelegs.py -- <src.fbx> <out.fbx>

`--python-exit-code 1` is MANDATORY. Blender's `--background --python` exits 0
even on an uncaught traceback, so without it a crashed authoring run reports
success to the pipeline.

===============================================================================
BEHIND-THE-BACK'S FRAME LAYOUT, STEP-BACK'S MACHINERY
===============================================================================
This clip is HANDED (two polarities on one timeline), so its frame table is
`author_behindtheback.py`'s shape. But it is authored with `bake_timeline` +
a channel dict, the way `author_stepback.py` / `author_retreatdribble.py` do,
rather than behind-the-back's hand-rolled per-frame interpolation: the shared
library gained that machinery in #315 and it brings the per-segment
`PHASE_EASING` resolution (and its run-log line) for free. `bake_timeline` is
simply called TWICE, once per polarity, over that polarity's own frame range --
it derives `t_s` from its own `f0`, so both calls see an identical 0.000..0.333 s
timeline and only the sign handed to `apply` differs.

    frames   seconds            segment
    0  -> 6  0.00000 -> 0.10000  LEFT-origin  Startup   (6 ticks)
    6  -> 9  0.10000 -> 0.15000  LEFT-origin  Active    (3 ticks)
    9  -> 20 0.15000 -> 0.33333  LEFT-origin  Recovery  (11 ticks)
    20 -> 30 (never sampled)     hold gap -- neither slice window in
                                 tools/rebuild_betweenthelegs_clips.gd reads it
    30 -> 36 0.50000 -> 0.60000  RIGHT-origin Startup
    36 -> 39 0.60000 -> 0.65000  RIGHT-origin Active
    39 -> 50 0.65000 -> 0.83333  RIGHT-origin Recovery
    50 -> 55 tail hold           exported so anything reading past 50 has
                                 somewhere to interpolate from; nothing does.

"LEFT-origin" means the ball STARTS in the LEFT hand. 6/3/11 ticks is
`BetweenTheLegs.DefaultFrameData` read off `scripts/Input/BetweenTheLegs.cs`,
not re-derived here.

===============================================================================
HANDEDNESS IS SAFE HERE -- VERIFIED FROM CONTROL FLOW, NOT FROM A COMMENT
===============================================================================
README trap 4 (`docs/handoffs/anim-clips/README.md`): `OriginHand`'s
phase-conditioned formula is valid ONLY for a move that swaps the ball hand
exactly at Active-entry. Re-verified against the branch itself rather than the
prose describing it: `PlayerController.cs`'s burst branch fires on
`_machine.JustEnteredActive && CurrentMove is Crossover or BehindTheBack or
BetweenTheLegs or InAndOut`, and inside it `if (CurrentMove is not InAndOut)
HandSide = HandStateResolver.Opposite(HandSide)`. So BetweenTheLegs swaps on the
FIRST Active tick -- the timing OriginHand assumes. It therefore joins
`MoveAnimResolver.HandedMoves`, and this script authors six clips.

Contrast its neighbour `author_inandout.py`, which carries the SAME
`BurstDirection` parameter and must NOT be handed: same param, opposite answer,
because the discriminator is the swap TIMING and never the param.

===============================================================================
THE SILHOUETTE: STANCE WIDTH AND BALL HEIGHT
===============================================================================
Three-way contrast, all off the same source clip:

    Crossover        both hands in FRONT of the torso, ball at knee height.
    Behind-the-back  both wrists BEHIND the hip line.
    Between-the-legs knees APART, both hands BETWEEN them, ball below the hips.

Stance WIDTH and ball HEIGHT are this move's signature, so those are the two
quantities the gates below measure (`_verify_stance_widens`,
`_verify_hands_inside_knees`) and the two that `rebuild_betweenthelegs_clips.gd`
re-measures on the sliced clips.

===============================================================================
WHERE THE HANDS GO IS DICTATED BY BallSweepPath.ThroughLegs, AND IT IS NOT
WHERE THE NAME SUGGESTS
===============================================================================
Handoff 09 requires the clip to visually AGREE with the ball's own transit path,
and reading that path changes the answer. `BallController` gives this move
`BallSweepPath.ThroughLegs`, and `CrossoverBallSweep.ForwardOffset`'s own
docstring is explicit that ThroughLegs "stays in front like InFront (same
baseline, forward axis untouched); its distinguishing depth is a deeper VERTICAL
dip". Measured off the shipped tunables (no scenes/*.tscn overrides exist for
any of them, so the C# defaults ARE the live values -- the #217 trap checked,
not assumed):

    forward offset  DribbleForwardOffset = 0.5 m, IN FRONT, constant through
                    the transit -- ThroughLegs never pulls back the way
                    BehindBody does.
    lateral         smoothstep from +/-HandOffset (0.4429 m) to the mirror, so
                    it crosses the MIDLINE at t=0.5.
    height          dip = sin(pi*t) * BetweenTheLegsDipDepth (0.85 m, the
                    deepest in the family), clamped to >= BallRadius.

So the ball is at the MIDLINE, LOW, and IN FRONT during transit. The hands
therefore belong low and forward at the midline -- NOT tucked behind the crotch,
which is what "between the legs" reads as if you never open the sweep code, and
which would put the hands behind the hips while the ball mesh renders half a
metre in front of them.

Timing lines up too: the sweep starts on the HandSide flip (the first Active
tick) and runs `CrossoverSweepDuration` 0.12 s ~= 7 ticks, so its midpoint --
ball exactly at the midline, dip at maximum -- lands at the Active/Recovery
boundary. Hence the Active channel table below puts the ball hand just PAST the
midline (it has pushed the ball through and is following through) and the
receiving hand arriving at the midline from its own side, with the ball's own
rendered position between the two. Cosmetic-only: this script READS that path,
and nothing here writes to it.

===============================================================================
THE DEEP CROUCH PUSHES THE LEG IK -- HENCE AN EXPLICIT KNEE FLOOR
===============================================================================
Handoff 09's per-move hazard. At 0.18 m of hip drop plus a 0.30 m lateral step
the hip-to-ankle distance is far inside the 0.8270 m reach, so
`report_ankle_ik` -- which only ever catches OVER-reach -- has nothing to say.
The risk is the opposite end: `solve_two_link`'s law-of-cosines solve will
happily return a hyperflexed knee that reads as broken, and no gate in
`blender_anim_lib` looks at that angle.

It does, however, already RETURN it: `plant_foot` returns
`(solve_two_link triple, ankle_err)` and the triple's third element is the
interior knee angle in radians (pi = straight leg, small = folded). So the gate
is a floor on a number the library already hands back -- see
`KNEE_INTERIOR_MIN_DEG`, which is set from this clip's own measured worst case
with headroom, and reported every run so a retune that eats the headroom is
visible before it is fatal.

===============================================================================
AUTHORED IN PLACE -- THE HIPS NEVER TRANSLATE HORIZONTALLY
===============================================================================
`PlayerController` already applies the real lateral burst on JustEnteredActive
(`BetweenTheLegsBurstSpeed` via `CrossoverBurstMath.ComposeActiveVelocity`), so
a clip that ALSO translates its root plays the burst twice and slides the mesh
off its own collider. `_verify_hips_stay_in_place` is retreat dribble's /
step-back's gate reused verbatim: the Hips move along `up` only. Zero by
construction today (`apply()` builds the Hips target as
`hips_base + up * -hip_drop_m`, with no horizontal term to be nonzero) -- the
gate exists to refuse a FUTURE edit that adds one.

The feet are anchored to `hips_base`, NOT to `hips_now`, which is
`author_contest.py`'s lesson: anchoring the ankle targets to the dropped hips
makes a crouch LIFT the feet by exactly the crouch depth, so the "deepest crouch
in the batch" would have been a whole-body descent with no knee bend at all --
and the knee floor above would have had nothing to catch.

===============================================================================
THE POLARITY LIVES IN THE ARMS, NOT THE STANCE
===============================================================================
Both feet step out symmetrically -- that IS the read ("knees APART"), so the
stance cannot also carry the handedness. The hands carry it: the ball hand
starts on its own side and follows through past the midline, while the receiving
hand arrives from the other side and, by Recovery's end, has risen to dribble
height on the new side while the origin hand trails low.

That gives the non-symmetric control README trap 5 demands
(`_verify_recv_hand_ends_higher`): the Y Bot rig is mirror-symmetric to 0.17 mm,
so a symmetric assertion proves nothing about handedness. "The RECEIVING hand is
the higher one at Recovery's end" is a claim that names the sides and inverts
when the polarity does. `left_vs_right_active` (pose-distinctness across the two
Active poses) backs it up from a second angle, exactly as behind-the-back does.

===============================================================================
COSMETIC-ONLY
===============================================================================
This script is an ASSET BUILD TOOL. It does not import, read, or feed
`BetweenTheLegsBurstSpeed`, `BetweenTheLegsDipDepth`, `BallState`, or any
`PlayerController` move-begin gate. It VISUALISES the between-the-legs;
`BetweenTheLegsTest`'s scenarios assert the behaviour this file cannot reach.
"""

import math
import os
import sys

import bpy
from mathutils import Matrix

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import blender_anim_lib as lib  # noqa: E402  (must follow the sys.path fix)

log = lib.log

# ── clip contract, 60 Hz (BetweenTheLegs.DefaultFrameData) ───────────────────
FPS = 60
STARTUP_TICKS = 6
ACTIVE_TICKS = 3
RECOVERY_TICKS = 11
TOTAL_TICKS = STARTUP_TICKS + ACTIVE_TICKS + RECOVERY_TICKS  # 20

T_ACTIVE_START = STARTUP_TICKS / FPS                    # 0.10000
T_ACTIVE_END = (STARTUP_TICKS + ACTIVE_TICKS) / FPS     # 0.15000
T_RECOVERY_END = TOTAL_TICKS / FPS                      # 0.33333

# Absolute Blender frame numbers, chosen so frame numbers ARE physics ticks
# within each polarity (the same readability choice author_behindtheback.py
# makes). The 20..30 gap and the 50..55 tail are never sliced by
# rebuild_betweenthelegs_clips.gd.
LEFT_F0 = 0
RIGHT_F0 = 30
EXPORT_FRAME_END = 55

ACTION_NAME = "betweenthelegs"

# ── stance geometry, metre-denominated ────────────────────────────────────────
# Rig-intrinsic (Y Bot femur/tibia/foot), so reused verbatim from
# author_stepback.py / author_retreatdribble.py's own measurement rather than
# re-derived. STANCE_HALF_WIDTH_M is the NEUTRAL half-width this move widens
# away from; the widened numbers live in the channel table.
NEUTRAL_HIP_TO_ANKLE_M = 0.62
STANCE_HALF_WIDTH_M = 0.12

# Torso pitch sign: a positive rotation about `body_right` must tip the torso
# FORWARD (over the ball). This is the OPPOSITE claim from author_stepback.py's
# `_torso_pitch_sign_is_backward`, so the constant is NOT inherited from that
# file -- `_torso_pitch_sign_is_forward` below re-derives it independently on
# this clip's own axes. author_contest.py's docstring records that its own
# initial sign guess was wrong on this same rig; a wrong sign here would lean
# the player AWAY from a ball being pushed toward the floor between their feet.
TORSO_PITCH_SIGN = -1.0

# ── keypose channel table ─────────────────────────────────────────────────────
# Every lateral channel is in OWN-SIDE units: positive = toward that limb's own
# side of the body, multiplied by `body_right`'s sign for the limb in question
# (see `_side_signs`). So a NEGATIVE hand lateral means that hand has crossed
# PAST the midline onto the other side.
#
# Heights are relative to `hips_now` (i.e. after that frame's hip drop);
# `hip_drop_m` is a positive DOWNWARD magnitude off the fixed `hips_base`
# anchor. Foot laterals/fores are offsets off `hips_base`'s horizontal position,
# with the ankle held at a FIXED floor level (see the docstring).
#
# The Active row is duplicated at t=0.10000 (label "active") and t=0.15000
# (label "recovery") with IDENTICAL values: 3 ticks is a HELD impact pose, not a
# movement (README-blender.md "Segments of <=3 ticks are single poses"). The
# second row's "recovery" label is what makes the FOLLOWING segment resolve to
# `ease_in_out` via PHASE_EASING.
_KEYPOSES_RAW = [
    # t_s,            label,      hip_drop, pitch, bf_lat, bf_fore, rf_lat, rf_fore, bh_fore, bh_lat, bh_h, rh_fore, rh_lat, rh_h
    #
    # Frame 0 -- entry, a hard cut from the dribble stance (no xfade on any
    # edge). Barely wider than neutral yet; the ball hand is dribble-ready on
    # its own side and the receiving hand hangs passive. The tell has started
    # but not committed.
    [0.0,             "startup",  0.04,     2.0,   0.14,   0.02,    0.14,   -0.02,   0.16,    0.20,   -0.02, 0.10,   0.22,   0.06],
    #
    # Frame 6 -- the Startup/Active SLICE BOUNDARY, simultaneously the last
    # frame of `betweenthelegs*startup` and the first of `betweenthelegs*active`.
    # MAXIMUM SPLIT: the widest stance in the batch (0.30 m per foot off the
    # midline, against the 0.12 m neutral) because the legs must open far enough
    # for the ball to pass, and the deepest crouch in the batch (0.18 m of hip
    # drop). Both hands are low and INSIDE the knee line, at/near the midline,
    # and slightly forward -- matching where BallSweepPath.ThroughLegs actually
    # renders the ball (see the docstring; NOT behind the hips). The ball hand
    # has followed the ball THROUGH and sits just past the midline (negative
    # own-side lateral) while the receiving hand arrives from its own side, so
    # the ball's own midline position at this instant sits between the two.
    [T_ACTIVE_START,  "active",   0.18,     26.0,  0.30,   0.00,    0.30,   0.00,    0.18,    -0.08,  -0.05, 0.16,   0.07,   -0.01],
    [T_ACTIVE_END,    "recovery", 0.18,     26.0,  0.30,   0.00,    0.30,   0.00,    0.18,    -0.08,  -0.05, 0.16,   0.07,   -0.01],
    #
    # Frame 20 -- Recovery's end. The stance narrows back toward neutral as the
    # trailing foot comes in, the hips rise 0.10 m off the Active low (0.18 ->
    # 0.08, handoff 09's "hips rise ~0.10 m"), and the RECEIVING hand has risen
    # to dribble height on the new side while the origin hand trails low --
    # which is this clip's non-symmetric handedness oracle, see
    # `_verify_recv_hand_ends_higher`. Eleven ticks animates that transition
    # cleanly; handoff 09 is explicit that it must not be held.
    [T_RECOVERY_END,  "recovery", 0.08,     5.0,   0.16,   -0.04,   0.16,   0.04,    0.06,    0.16,   -0.06, 0.16,   0.24,   0.10],
]

_CHANNEL_NAMES = (
    "hip_drop_m", "torso_pitch_deg",
    "ball_foot_lat_m", "ball_foot_fore_m",
    "recv_foot_lat_m", "recv_foot_fore_m",
    "ball_hand_fore_m", "ball_hand_lat_m", "ball_hand_height_m",
    "recv_hand_fore_m", "recv_hand_lat_m", "recv_hand_height_m",
)

# Elbow bend-plane hints (own-side units, NOT normalized -- aim_arm normalizes
# the resulting axis). Down-and-outward: both arms reach DOWN toward the floor
# for most of this clip, which is where a real elbow goes for a low reach.
ELBOW_HINT_UP = -0.6
ELBOW_HINT_LAT = 0.5

# ── proof thresholds ──────────────────────────────────────────────────────────
# Startup-end-vs-Recovery-end legibility floor (#296). Matches the other
# scripts' 15.0 deg floor and rebuild_betweenthelegs_clips.gd's own G3.
POSE_DISTINCT_MIN_DEG = 15.0

# Cross-polarity Active distinctness -- the two Active silhouettes must read as
# different moves, not as one pose plus float noise. Matches
# author_behindtheback.py's own left_vs_right_active floor.
LEFT_VS_RIGHT_ACTIVE_MIN_DEG = 20.0

# Support-level band. The ankles are anchored to a FIXED floor with no vertical
# channel anywhere in the table, so this reads essentially zero; its job is to
# catch a future edit that gives a foot an `up` channel without saying so.
# MEASURED on this clip: ground_band_m = 0.0000 for both polarities, so this
# tolerance is ~100x the print resolution and still catches a 5 mm float.
GROUND_BAND_TOL_M = 0.005

# The interior knee angle floor, in DEGREES (180 = a straight leg, small = a
# folded one). Handoff 09's named per-move hazard: nothing in blender_anim_lib
# guards hyperflexion, and `solve_two_link` will return one happily.
#
# Set from THIS clip's MEASURED worst case with headroom, not from taste, and
# the measurement is worth stating because a hand-calculation off the Hips is
# wrong by ~17 deg: `plant_foot` solves from the UpLeg (hip JOINT) head, which
# sits ~0.09 m out from the midline and below the Hips bone, so the femur-root-
# to-ankle span is materially shorter than a Hips-to-ankle estimate suggests.
# Predicted ~80 deg that way; the real figure is 63.16 deg (L leg, frame 40).
#
# 63 deg is a deep athletic squat, which is the intended pose. A genuinely
# folded, broken-reading knee is well under 40 deg. 50 sits between them with
# ~13 deg of headroom, and catches the realistic regression: pushing hip_drop_m
# from the authored 0.18 to 0.28 lands at ~48 deg and reddens. The measured
# worst is reported every run, so a retune that eats the headroom is visible
# before it is fatal.
KNEE_INTERIOR_MIN_DEG = 50.0

# "Knees APART": each foot's own distance from the midline must GROW by at least
# this much from Startup's entry to Active. Reduced with `min`, never `max`
# (README trap 17) -- "both knees went out" is a both-limbs claim, and a
# one-legged step would satisfy a max-reduced gate while failing the read.
# Floor well under the table's authored 0.14 -> 0.30 m growth.
STANCE_WIDEN_MIN_M = 0.08

# "Both hands BETWEEN the knees": at Active, each wrist's distance from the
# midline must be inside its own side's knee by at least this margin. Reduced
# with `min` for the same trap-17 reason. The authored gap is large (hands
# within ~0.12 m of the midline against knees at ~0.30 m), so this floor is
# a fraction of it.
HANDS_INSIDE_KNEES_MIN_M = 0.05

# The receiving hand must end Recovery at least this far ABOVE the origin hand
# (the non-symmetric handedness oracle). Authored gap is 0.10 - (-0.06) =
# 0.16 m of channel; the realized world gap is measured and reported.
RECV_HAND_HIGHER_MIN_M = 0.06

# Any per-frame reach demand above this fraction of the arm's budget is logged
# as it happens. `aim_arm` raises on over-reach with no frame context, so
# without this the first failure names a number but not the keypose that caused
# it -- and on a TWO-POLARITY clip the offender is very often only one of them.
# The two polarities are NOT mirror images: they sit on different SOURCE frames
# (0..20 vs 30..50 of a 2.1 s dribble cycle) and therefore compose onto
# different baseline poses. Measured here: the first draft of the Active row
# read 0.9812 on the L arm and 1.0909 on the R -- the L polarity baked fine and
# only the R one died, which is exactly the case a single fatal line cannot
# explain on its own.
#
# This move sits high by nature: it is a low reach toward the floor, so the arm
# is genuinely near-extended and a low warn threshold would fire every run. The
# authored worst is 0.9039 (R arm, frame 39, i.e. Active's end); `aim_arm`'s own
# hard failure is at 0.999, so the threshold below sits between the two and
# fires only on real drift toward the cliff.
REACH_WARN_RATIO = 0.95

# Diagnostic escape hatch, `author_behindtheback.py`'s precedent: skip the arm
# solve so a single run reports the reach ratio at EVERY frame instead of dying
# at the first over-reach.
#
# TIGHTENED beyond that precedent, because the hatch has two failure modes the
# original leaves open and this clip walks straight into both:
#
#   1. Every ARM-dependent gate becomes meaningless, not merely weaker. With
#      `aim_arm` skipped the arms sit wherever the source dribble left them, so
#      `_verify_hands_inside_knees` measured the SOURCE's hanging arms and
#      failed at -0.4134 m -- a real number about nothing. Those gates are
#      therefore SKIPPED under the hatch, with a loud NOTE, rather than left to
#      report a confusing geometric failure that hides the reach data the run
#      was invoked to collect.
#   2. The exported FBX would carry NO arm keys, which is the a45bd1d
#      rest-fallback T-pose trap authored by hand. So the hatch refuses to
#      export at all; a diagnostic run cannot leave a shippable-looking asset
#      behind.
#
# The reach ratio itself IS valid under the hatch: `aim_arm` poses the humerus/
# ulna/hand, never the shoulder, and the shoulder is what the ratio measures
# from (the clavicle is pinned to rest and the spine pitch is applied either
# way). That is the whole reason the hatch is worth having.
_MEASURE_ONLY = os.environ.get("BTL_MEASURE_ONLY") == "1"


def _side_signs(ball_side):
    """`(ball_sign, recv_side)` for `ball_side` in {"L","R"}.

    `ball_sign` multiplies every own-side lateral channel to place it on the
    ANATOMICALLY correct side, via `geom.body_right` -- never `geom.lateral`,
    which is a BASIS vector pointing at the character's LEFT on this rig (#320).
    -1 for L / +1 for R matches `body_right`'s sign.
    """
    return (-1.0 if ball_side == "L" else 1.0), ("R" if ball_side == "L" else "L")


def _keyposes_for_lib():
    """`_KEYPOSES_RAW` translated into `blender_anim_lib.Keypose` objects."""
    out = []
    for row in _KEYPOSES_RAW:
        t_s, label = row[0], row[1]
        channels = dict(zip(_CHANNEL_NAMES, row[2:]))
        out.append(lib.Keypose(t_s, label, **channels))
    return out


def _spine_head_forward_m(arm, geom, forward):
    """The spine->head vector's projection along `forward`, in METRES.

    The same one-quantity torso probe author_retreatdribble.py / author_stepback.py
    use; a LARGER value means the chest is pitched further forward.
    """
    spine_head = arm.pose.bones[lib.SPINE].head.copy()
    head_head = arm.pose.bones["mixamorig:Head"].head.copy()
    return geom.to_m((head_head - spine_head).dot(forward))


def _torso_pitch_sign_is_forward(arm, geom, body_right, forward):
    """A positive `TORSO_PITCH_SIGN` rotation must tip the torso FORWARD.

    The MIRROR of author_stepback.py's `_torso_pitch_sign_is_backward`, and
    deliberately re-derived rather than inherited: rotation handedness about an
    axis is not reliably derivable by eye from the axis convention (the reason
    author_behindtheback.py's own twist sign was measured wrong the first time),
    and this move needs the OPPOSITE sign from its sibling.

    Technique is that file's verbatim: rotate the spine->head vector by the
    signed pitch at a SINGLE frame -- no baking, no two-frame comparison, so the
    source clip's own drift cannot contaminate the reading -- and check the
    forward component GREW.
    """
    spine_head = arm.pose.bones[lib.SPINE].head.copy()
    head_head = arm.pose.bones["mixamorig:Head"].head.copy()
    vec = head_head - spine_head
    rot = Matrix.Rotation(math.radians(TORSO_PITCH_SIGN * 10.0), 4, body_right)
    delta_fore = geom.to_m(((rot @ vec) - vec).dot(forward))
    lib.report("torso_pitch_sign_fore_delta_m", f"{delta_fore:+.4f}")
    if delta_fore <= 0.0:
        raise SystemExit(
            f"FATAL: a positive TORSO_PITCH_SIGN ({TORSO_PITCH_SIGN}) rotation moves "
            f"the spine->head vector {delta_fore:+.4f} m along forward, i.e. BACKWARD "
            f"or not at all. A between-the-legs pitches FORWARD over a ball being "
            f"pushed toward the floor between the feet (handoff 09). Flip "
            f"TORSO_PITCH_SIGN.")


def _verify_hips_stay_in_place(arm, geom, hips_base, forward, body_right, frames):
    """The Hips translate along `up` ONLY -- never fore/aft, never laterally.

    Verbatim from author_retreatdribble.py / author_stepback.py. Zero by
    construction today (`apply()` builds the Hips target with no horizontal
    term); the gate's job is refusing a FUTURE edit that adds one, because
    PlayerController already applies this move's real lateral burst via
    CrossoverBurstMath on JustEnteredActive.
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
            f"{worst_lat:.6f} m laterally (frame {worst_frame}, tol {tol_m}). This "
            f"clip must be authored IN PLACE: PlayerController already applies "
            f"BetweenTheLegsBurstSpeed via CrossoverBurstMath.ComposeActiveVelocity "
            f"on JustEnteredActive, so root translation here double-counts the burst "
            f"and slides the mesh off its collider. Express the split as the FEET "
            f"moving relative to the hips, never as hip translation.")


def _lateral_from_midline(arm, geom, body_right, bone, hips):
    """|bone - hips| projected on `body_right`, in METRES. Always non-negative."""
    p = arm.pose.bones[bone].head.copy()
    return abs(geom.to_m((p - hips).dot(body_right)))


def _verify_stance_widens(arm, geom, body_right, f_start, f_active, label):
    """BOTH ankles sit further from the midline at Active than at Startup's entry.

    "Knees APART" is this move's headline read and a BOTH-limbs claim, so it is
    reduced with `min`, never `max` (README trap 17): a one-legged step out would
    satisfy a max-reduced gate while failing the silhouette entirely. Both
    per-side numbers are printed (the `LocomotionClipTest` #298 shape the README
    names as preferred) so a one-sided regression is legible in the log rather
    than just failing anonymously.
    """
    scene = bpy.context.scene
    with lib.preserve_frame():
        scene.frame_set(f_start)
        hips_s = arm.pose.bones[lib.HIPS].head.copy()
        start = {s: _lateral_from_midline(arm, geom, body_right, lib.LEG_CHAIN[s][2], hips_s)
                 for s in ("L", "R")}
        scene.frame_set(f_active)
        hips_a = arm.pose.bones[lib.HIPS].head.copy()
        active = {s: _lateral_from_midline(arm, geom, body_right, lib.LEG_CHAIN[s][2], hips_a)
                  for s in ("L", "R")}

    grow = {s: active[s] - start[s] for s in ("L", "R")}
    lib.report(f"{label}_stance_widen_L_m", f"{grow['L']:+.4f}")
    lib.report(f"{label}_stance_widen_R_m", f"{grow['R']:+.4f}")
    worst = min(grow["L"], grow["R"])
    lib.report(f"{label}_stance_widen_min_m", f"{worst:+.4f}")
    if worst < STANCE_WIDEN_MIN_M:
        loser = "L" if grow["L"] < grow["R"] else "R"
        raise SystemExit(
            f"FATAL: the {loser} ankle moved only {worst:+.4f} m further from the "
            f"midline between frames {f_start} and {f_active} (floor "
            f"{STANCE_WIDEN_MIN_M}). Stance WIDTH is half this move's signature "
            f"(L={grow['L']:+.4f} R={grow['R']:+.4f}; reduced with min, never max -- "
            f"README trap 17).")


def _verify_hands_inside_knees(arm, geom, body_right, frame, label):
    """At Active, BOTH wrists sit laterally INSIDE the knee line.

    The other half of the silhouette, and the claim that makes the clip agree
    with `BallSweepPath.ThroughLegs` -- if a hand sits outside the knee while
    the ball mesh routes down the midline, the hands and the ball visibly
    disagree on screen (handoff 09's named hazard).

    Both wrists are bounded by the NARROWER of the two knees, not each by the
    knee on its own side. That is deliberate: at Active the origin hand has
    followed the ball PAST the midline, so it is physically sitting on the
    OTHER side of the body and pairing it with its own-side knee compares two
    things that are no longer on the same side. Measuring every wrist against
    the tighter bound is both side-agnostic and strictly stronger, and it stays
    correct however far a hand crosses. (The two knees are NOT symmetric even
    with symmetric foot targets -- measured 0.1976 L vs 0.1596 R -- because the
    source dribble's own femur orientation is baked in underneath.)

    The two margins are reduced with `min` (README trap 17): "both hands are
    between the knees" is a both-limbs claim, and one hand flung wide must fail
    it. Both per-side numbers are printed so a one-sided regression is legible.
    """
    scene = bpy.context.scene
    with lib.preserve_frame():
        scene.frame_set(frame)
        hips = arm.pose.bones[lib.HIPS].head.copy()
        knees = {s: _lateral_from_midline(arm, geom, body_right, lib.LEG_CHAIN[s][1], hips)
                 for s in ("L", "R")}
        wrists = {s: _lateral_from_midline(arm, geom, body_right, lib.ARM_CHAIN[s][2], hips)
                  for s in ("L", "R")}

    narrowest_knee = min(knees["L"], knees["R"])
    margins = {s: narrowest_knee - wrists[s] for s in ("L", "R")}
    for side in ("L", "R"):
        lib.report(f"{label}_{side}_knee_lat_m", f"{knees[side]:.4f}")
        lib.report(f"{label}_{side}_wrist_lat_m", f"{wrists[side]:.4f}")

    worst = min(margins["L"], margins["R"])
    lib.report(f"{label}_hand_inside_knee_min_m", f"{worst:+.4f}")
    if worst < HANDS_INSIDE_KNEES_MIN_M:
        loser = "L" if margins["L"] < margins["R"] else "R"
        raise SystemExit(
            f"FATAL: at frame {frame} the {loser} wrist sits only {worst:+.4f} m "
            f"inside the narrower knee (floor {HANDS_INSIDE_KNEES_MIN_M}; L="
            f"{margins['L']:+.4f} R={margins['R']:+.4f}, reduced with min). Both hands "
            f"must be BETWEEN the knees -- a hand outside the knee line while "
            f"BallSweepPath.ThroughLegs routes the ball down the midline makes the "
            f"hands and the ball visibly disagree (handoff 09).")


def _verify_recv_hand_ends_higher(arm, geom, up, ball_side, recv_side, f_end, label):
    """At Recovery's end the RECEIVING hand sits above the ORIGIN hand.

    The non-symmetric control README trap 5 demands. The Y Bot rig is
    mirror-symmetric to 0.17 mm across X=0, so every side-agnostic check is blind
    to a polarity that is silently inverted; this one NAMES the sides and flips
    its answer when the polarity does. It is the authoring-time half of a claim
    re-measured twice downstream (rebuild_betweenthelegs_clips.gd's G6 on the
    sliced clips, and BetweenTheLegsAnimTest on the live rig).

    Physically it is the move finishing: the receiving hand has come up to
    dribble height on the NEW side while the hand that pushed the ball through
    trails low.
    """
    scene = bpy.context.scene
    with lib.preserve_frame():
        scene.frame_set(f_end)
        ball_h = arm.pose.bones[lib.ARM_CHAIN[ball_side][2]].head.dot(up)
        recv_h = arm.pose.bones[lib.ARM_CHAIN[recv_side][2]].head.dot(up)
    gap = geom.to_m(recv_h - ball_h)
    lib.report(f"{label}_recv_minus_origin_hand_height_m", f"{gap:+.4f}")
    if gap < RECV_HAND_HIGHER_MIN_M:
        raise SystemExit(
            f"FATAL: at Recovery's end (frame {f_end}) the RECEIVING hand "
            f"({recv_side}) sits only {gap:+.4f} m above the ORIGIN hand ({ball_side}) "
            f"(floor {RECV_HAND_HIGHER_MIN_M}). The receiving hand must rise to "
            f"dribble height on the new side while the origin hand trails -- this is "
            f"the clip's only NON-SYMMETRIC handedness claim, and a polarity that "
            f"silently inverted would pass every other gate here (README trap 5 / the "
            f"#255 mirror bug).")


def _author_polarity(arm, geom, body_right, hips_base, ball_side, frame_offset, state):
    """Bake one polarity's Startup/Active/Recovery arc onto `arm`'s action.

    `ball_side`: "L" or "R" -- the physical hand the move ORIGINATES in.
    `frame_offset`: the absolute Blender frame number for this polarity's t=0.
    `hips_base`: the ONE anchor both polarities are built from, captured by the
        caller before any posing. Deliberately NOT re-read here per polarity:
        by the time the second call runs, the armature is sitting on the first
        polarity's last baked frame, so a local re-read would anchor the R arc
        to an already-posed crouch and silently give the two polarities
        different floors -- while every per-polarity gate below, being relative
        to its own anchor, stayed green.
    `state`: a dict the caller uses to accumulate cross-polarity worst cases.

    Returns a dict of frame landmarks for the caller's proofs.
    """
    ball_sign, recv_side = _side_signs(ball_side)
    recv_sign = -ball_sign
    up, forward = geom.up, geom.forward

    f0 = frame_offset
    f1 = frame_offset + TOTAL_TICKS  # inclusive
    startup_end = f0 + STARTUP_TICKS
    active_end = f0 + STARTUP_TICKS + ACTIVE_TICKS

    # Ankle base spots: a FIXED floor level, symmetric about the midline. Both
    # feet share one base because this move's stance is symmetric -- the
    # handedness lives in the arms (see the module docstring).
    def ankle_base(side_sign, lat_m, fore_m):
        return (hips_base
                + body_right * (side_sign * geom.m(lat_m))
                + forward * geom.m(fore_m)
                - up * geom.m(NEUTRAL_HIP_TO_ANKLE_M))

    def apply(frame, _t_s, ch):
        # ---- clavicles: pinned to REST, not inherited from the source --------
        # Dribble.fbx's own Shoulder(clavicle) bones carry uncontrolled idle
        # sway; ARM_CHAIN deliberately excludes the clavicle from the two-link
        # solve, so nothing else here controls it.
        for side in ("L", "R"):
            sh = arm.pose.bones[f"mixamorig:{'Left' if side == 'L' else 'Right'}Shoulder"]
            sh.matrix_basis = Matrix.Identity(4)
            bpy.context.view_layer.update()
            sh.keyframe_insert("rotation_quaternion", frame=frame)

        # ---- hips: a VERTICAL drop off the fixed anchor, and nothing else ----
        pb = arm.pose.bones[lib.HIPS]
        mh = pb.matrix.copy()
        mh.translation = hips_base - up * geom.m(ch["hip_drop_m"])
        pb.matrix = mh
        bpy.context.view_layer.update()
        pb.keyframe_insert("location", frame=frame)
        hips_now = pb.head.copy()

        # ---- torso pitch about body_right (a pitch, not a twist) -------------
        pitch_rad = math.radians(TORSO_PITCH_SIGN * ch["torso_pitch_deg"])
        lib.rotate_bone_about_head(
            arm, lib.SPINE, (Matrix.Rotation(pitch_rad, 4, body_right),), frame=frame)

        # ---- legs: anchored to hips_base, so the crouch is REAL knee bend ----
        toe_dir = (forward * 0.90 - up * 0.44).normalized()
        for side, side_sign, lat_key, fore_key in (
            (ball_side, ball_sign, "ball_foot_lat_m", "ball_foot_fore_m"),
            (recv_side, recv_sign, "recv_foot_lat_m", "recv_foot_fore_m"),
        ):
            ankle = ankle_base(side_sign, ch[lat_key], ch[fore_key])
            solved, ankle_err = lib.plant_foot(arm, side, ankle, toe_dir, geom, frame=frame)
            state["worst_ankle_err"] = max(state["worst_ankle_err"], ankle_err)
            # solve_two_link's triple is (distance, hip_offset, interior_angle);
            # the third element is the knee angle this move's deep crouch puts
            # at risk. See KNEE_INTERIOR_MIN_DEG.
            knee_deg = math.degrees(solved[2])
            if knee_deg < state["worst_knee_deg"][0]:
                state["worst_knee_deg"] = (knee_deg, side, frame)

        # ---- arms: own-side channels, mirrored by the polarity's signs -------
        for side, side_sign, fore_key, lat_key, h_key in (
            (ball_side, ball_sign, "ball_hand_fore_m", "ball_hand_lat_m", "ball_hand_height_m"),
            (recv_side, recv_sign, "recv_hand_fore_m", "recv_hand_lat_m", "recv_hand_height_m"),
        ):
            target = (hips_now
                      + forward * geom.m(ch[fore_key])
                      + body_right * (side_sign * geom.m(ch[lat_key]))
                      + up * geom.m(ch[h_key]))
            hint = (up * ELBOW_HINT_UP + body_right * (side_sign * ELBOW_HINT_LAT)).normalized()

            # Measure the reach demand BEFORE handing the target to aim_arm,
            # which raises on over-reach with no frame context (it hardcodes
            # on_overreach="fail" and cannot know what we were aiming at).
            # Reporting the ratio here turns "IK target 55.77 exceeds reach
            # 55.02" into an actionable "the L ball hand at frame 6 wants 101.4%
            # of its reach" -- author_behindtheback.py's technique, which is how
            # that clip's Active rows were tuned into budget.
            humerus_u, ulna_u = lib.arm_lengths(arm, side)
            sh_head = arm.pose.bones[lib.ARM_CHAIN[side][0]].head.copy()
            ratio = (target - sh_head).length / (humerus_u + ulna_u)
            if ratio > state["worst_reach"][0]:
                state["worst_reach"] = (ratio, side, frame)
            if ratio > REACH_WARN_RATIO:
                log(f"REACH {ratio:.4f} ({side} arm, frame {frame}, "
                    f"{'ball' if side == ball_side else 'recv'} hand)")
            if _MEASURE_ONLY:
                continue

            err_u = lib.aim_arm(arm, side, target, hint, geom, frame=frame)
            state["worst_wrist_err"] = max(state["worst_wrist_err"], err_u)

    lib.bake_timeline(arm, _keyposes_for_lib(), apply, f0, f1, FPS)

    return {
        "f0": f0, "f1": f1,
        "startup_end": startup_end, "active_end": active_end,
        "ball_side": ball_side, "recv_side": recv_side,
    }


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

    lib.enter_pose_mode(arm)

    # Sign oracle FIRST, on the untouched source pose: a wrong pitch sign would
    # otherwise be baked into every frame before anything noticed.
    _torso_pitch_sign_is_forward(arm, geom, body_right, forward)

    state = {
        "worst_ankle_err": 0.0,
        "worst_wrist_err": 0.0,
        "worst_reach": (0.0, "", 0),
        "worst_knee_deg": (180.0, "", 0),
    }

    # The ONE anchor both polarities are built from, captured before any posing.
    # See _author_polarity's docstring for why it is not re-read per polarity.
    hips_base = arm.pose.bones[lib.HIPS].head.copy()

    results = {}
    for ball_side, f0 in (("L", LEFT_F0), ("R", RIGHT_F0)):
        log(f"── authoring {ball_side}-origin polarity from frame {f0} ──")
        results[ball_side] = _author_polarity(
            arm, geom, body_right, hips_base, ball_side, f0, state)

    bpy.ops.object.mode_set(mode="OBJECT")

    # Export range covers both polarities plus the documented tail hold; the
    # 20..30 gap is contiguous with both windows but never read downstream.
    scene.frame_start, scene.frame_end = 0, EXPORT_FRAME_END

    lib.report_ankle_ik("worst_ankle_ik_err_m", geom.to_m(state["worst_ankle_err"]))
    lib.report("worst_wrist_ik_err_m", f"{geom.to_m(state['worst_wrist_err']):.6f}")
    _ratio, _rside, _rframe = state["worst_reach"]
    lib.report("worst_reach_ratio", f"{_ratio:.4f} ({_rside} arm, frame {_rframe})")

    # ── the knee floor (handoff 09's named per-move hazard) ──────────────────
    knee_deg, knee_side, knee_frame = state["worst_knee_deg"]
    lib.report("worst_knee_interior_deg", f"{knee_deg:.2f} ({knee_side} leg, frame {knee_frame})")
    # Inverted comparison so this fails CLOSED on NaN, matching
    # `report_ankle_ik`'s own reasoning: a degenerate solve is precisely the case
    # that yields NaN, and NaN is false against `<`.
    if not (knee_deg >= KNEE_INTERIOR_MIN_DEG):
        raise SystemExit(
            f"FATAL: the {knee_side} knee's interior angle reached {knee_deg:.2f} deg at "
            f"frame {knee_frame}, below the {KNEE_INTERIOR_MIN_DEG} deg floor -- a "
            f"hyperflexed knee that reads as broken. `solve_two_link` does NOT guard "
            f"this (it only ever refuses OVER-reach), so nothing else in the pipeline "
            f"would have caught it. Reduce hip_drop_m or the foot lateral at the "
            f"offending keypose; do not lower this floor.")

    lib.enter_pose_mode(arm)
    # 52 = Y Bot's 65 bones minus the 13 leaf terminators.
    lib.verify_all_bones_keyed(arm, expected_count=52)

    all_frames = list(range(LEFT_F0, results["L"]["f1"] + 1)) + \
        list(range(RIGHT_F0, results["R"]["f1"] + 1))
    _verify_hips_stay_in_place(arm, geom, hips_base, forward, body_right, all_frames)

    for ball_side, res in results.items():
        frames = list(range(res["f0"], res["f1"] + 1))
        label = f"{ball_side}origin"
        lib.verify_pose_unscaled(arm, frames)
        lib.verify_grounded(arm, frames, GROUND_BAND_TOL_M, geom)

        # #296: Startup's own end pose and Recovery's end pose must not be the
        # same picture. Anchored on Startup's END frame (the slice boundary),
        # not f0 -- f0 is the hard-cut entry from the dribble stance and belongs
        # to neither phase's authored arc.
        lib.verify_pose_distinct(
            lib.snapshot_pose(arm, res["startup_end"]),
            lib.snapshot_pose(arm, res["f1"]),
            POSE_DISTINCT_MIN_DEG,
            label=f"{label}_startup_vs_recovery")

        # The silhouette's leg half. Valid under the measure-only hatch (the
        # legs are solved either way), so it is never skipped.
        _verify_stance_widens(arm, geom, body_right, res["f0"], res["active_end"], label)

        if _MEASURE_ONLY:
            log(f"NOTE: BTL_MEASURE_ONLY -- skipping {label}'s arm-dependent gates "
                f"(hands-inside-knees, recv-hand-higher). The arms were never posed, "
                f"so those gates would measure the SOURCE clip's hanging arms.")
            continue

        _verify_hands_inside_knees(arm, geom, body_right, res["active_end"], label)

        # The handedness oracle.
        _verify_recv_hand_ends_higher(
            arm, geom, up, res["ball_side"], res["recv_side"], res["f1"], label)

    if _MEASURE_ONLY:
        log("NOTE: BTL_MEASURE_ONLY -- refusing to export. A diagnostic run's arms "
            "carry no keys, so the FBX would be the a45bd1d rest-fallback T-pose trap "
            "wearing a shippable filename. Re-run without the env var.")
        return

    # Cross-polarity Active distinctness -- the second non-symmetric guard
    # (README trap 5 / #255): a polarity loop that silently ignored its sign
    # argument would still pass every per-polarity check above.
    left_active = lib.snapshot_pose(arm, results["L"]["active_end"])
    right_active = lib.snapshot_pose(arm, results["R"]["active_end"])
    lib.verify_pose_distinct(
        left_active, right_active, LEFT_VS_RIGHT_ACTIVE_MIN_DEG, label="left_vs_right_active")

    lib.export_fbx(arm, dst, ACTION_NAME)
    log(f"wrote {dst}")


if __name__ == "__main__":
    main()
