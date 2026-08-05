"""Author `steal` as a two-polarity keypose clip in headless Blender (#282).

Run:
    "$BLENDER" --background --python-exit-code 1 \
        --python tools/author_steal.py -- <src.fbx> <out.fbx>

`--python-exit-code 1` is MANDATORY. Blender's `--background --python` exits 0
even on an uncaught traceback, so without it a crashed authoring run reports
success to the pipeline.

===============================================================================
WHAT THIS PRODUCES
===============================================================================
ONE Blender action, baked at 60 Hz, holding BOTH hand-side polarities on a
single timeline (frame numbers ARE physics ticks -- deliberate, so a reader can
cross-reference this file against the issue's frame table directly):

    frames   seconds            segment
    0  -> 8  0.00000 -> 0.13333  LEFT-target  Startup   (8 ticks -- the tell)
    8  -> 16 0.13333 -> 0.26667  LEFT-target  Active    (8 ticks -- the sweep)
    16 -> 36 0.26667 -> 0.60000  LEFT-target  Recovery  (20 ticks)
    36 -> 60 (never sampled)     hold gap -- neither slice window in
                                 tools/rebuild_steal_clips.gd reads it. A held
                                 neutral defensive stance is keyed at BOTH ends
                                 of the gap (36 and 60) so nothing drifts across
                                 it.
    60 -> 68 1.00000 -> 1.13333  RIGHT-target Startup
    68 -> 76 1.13333 -> 1.26667  RIGHT-target Active
    76 -> 96 1.26667 -> 1.60000  RIGHT-target Recovery

`StealMove.DefaultFrameData` = startup 8 / active 8 / recovery 20 ticks at
60 Hz (`scripts/Input/StealMove.cs`) -- verified against that file, not
re-derived here.

===============================================================================
"LEFT"/"RIGHT" NAME THE TARGET HAND, NOT AN ORIGIN -- AND THE ARM IS THE
OPPOSITE ONE
===============================================================================
Steal has no ball to swap sides on, so unlike behind-the-back/crossover this
is NOT an origin-hand move. Per the #282 handoff and this issue's brief: the
suffix names **where the swiping hand ENDS UP, in the defender's own body
space** --

    "left"  variant -> the swiping hand finishes on the defender's own LEFT
                        side, at ball height.
    "right" variant -> the exact mirror.

Because the motion reaches ACROSS the body, the arm doing the work is the
OPPOSITE shoulder: the "left" variant swipes with the RIGHT arm crossing over
to the defender's left side. Get this backwards and the clip is a mirror image
of its label -- plays cleanly, passes every symmetric check, and telegraphs
the wrong hand (the #255 lesson). See `_side_signs` below and the non-symmetric
checks at the bottom of `main()`.

`MoveAnimResolver`'s consumption of these six clips is OUT OF SCOPE here (a
separate issue/lane owns `scripts/**`) -- this script and its Godot-side
sibling only have to produce and prove six correctly-named, correctly-mirrored
clips in `locomotion.res`.

===============================================================================
WHY A KEYPOSE TIMELINE WITH SIX POINTS, NOT FOUR
===============================================================================
`author_behindtheback.py` (#281) uses a 4-point Startup/Active(held)/Recovery
timeline because its Active segment is a single HELD pose (3 ticks). Steal's
Active segment is 8 ticks and the brief is explicit that it must be "a genuine
sweep, not a held pose" with "at least one intermediate frame so the arc is
real motion" -- so this script keys SIX points per polarity: a neutral
Startup entry, the Startup-end/Active-start "tell" pose, an Active-midpoint
arc point, the Active-end/Recovery-start full-extension pose, a
Recovery-midpoint (the off-balance point the brief calls out by frame number),
and the Recovery-end neutral pose. All six go through
`blender_anim_lib.Keypose` + `bake_timeline`, which resolves easing PER PHASE
LABEL automatically (see that module's `PHASE_EASING`) -- this script supplies
only the channel values and the label at each point.

===============================================================================
THE SILHOUETTE CONTRAST (the whole point of this clip, per the brief)
===============================================================================
Contest (a different move, also 6-8/8/20) raises BOTH arms vertically with the
feet planted square. Steal reaches ACROSS the body at ball height with ONE arm
and commits the weight laterally. Nothing here has to reproduce Contest's own
clip -- this script's job is only to make Steal's own silhouette read that way:
one hand low and across, not two hands high and square.

Ball height (0.65-0.80 m above the FLOOR, per the brief) is authored relative
to the FLOOR, not to the hips, because that is how the spec states it. The
floor sits `NEUTRAL_HIP_TO_ANKLE_M` below the hips by construction (every
ankle target in this script is `hips_now - up * m(NEUTRAL_HIP_TO_ANKLE_M)`,
exactly as `author_behindtheback.py` does), so a hips-relative swipe-hand
height of `BALL_HEIGHT_ABOVE_FLOOR_M - NEUTRAL_HIP_TO_ANKLE_M` lands the target
at the stated floor height regardless of how much the stance crouches that
tick. See `H_BALL` below.

===============================================================================
"THE FAR FOOT" / "THE NEAR FOOT" -- which physical foot each name is
===============================================================================
The brief's motion spec talks about "the far foot" (weight-bearing anchor
during Startup, away from the reach) and "the near foot" (steps toward the
ball during Active, on the reach side). Given the target/reach side is named
by the polarity itself:

    near_foot_side = polarity   (steps toward the ball; same side as the reach)
    far_foot_side  = opposite(polarity)   (the Startup weight-bearing anchor)

`arm_side` (the bone chain that actually does the swiping) is
`opposite(polarity)` -- see the module docstring above. So for the "left"
polarity: arm_side="R" (right arm swipes), near_foot_side="L" (left foot steps
in), far_foot_side="R" (right foot anchors the Startup weight shift). The
"rest" arm (the one NOT swiping) is therefore `polarity`'s own chain.

===============================================================================
SIGN CONVENTION -- ONE VARIABLE, `reach_sign`
===============================================================================
`_side_signs` returns `reach_sign` = -1.0 for polarity "L", +1.0 for "R" --
matching `geom.body_right`'s sign (positive = character's actual anatomical
right; derived from the shoulder span and cross-checked against the hip span
in `blender_anim_lib.derive_body_right`, reused verbatim from
`author_behindtheback.py`). `geom.lateral` is a basis vector only -- on this
rig it points at the character's LEFT and must NOT be used for hand/foot
placement. There is no longer a `geom.right`; the local `-geom.right`
workaround this script used to carry is gone, replaced by the shared
accessor (#320).

Every lateral channel in this file is authored in "reach-direction-relative"
terms, i.e. a POSITIVE table value always means "further toward the side the
hand is reaching to/ending up on". This holds for the swipe arm, the rest arm,
and the near/lead foot, each placed with `body_right * (reach_sign * value)`.
The FAR/ANCHOR foot is the one exception: it is placed with `-reach_sign`
instead, because its table values are authored "own-natural-side positive"
(a plain stance-half-width offset on its OWN side, i.e. AWAY from the reach)
rather than reach-direction-relative -- it is the Startup weight-bearing
anchor, not part of the reach. A first draft used `reach_sign` for the far
foot too, which collapsed both feet toward the SAME side instead of anchoring
one on each; it was caught only because `_torso_outside_base_first_half`
(which specifically needs the far foot on the correct side to mean anything)
went red, not by inspection -- a hips-vs-NEAR-foot version of that same check
would have passed vacuously regardless of the bug, which is why it compares
against the far foot instead (see that function's own comment). The lesson
generalizes: a mismatched sign between limbs is exactly the kind of bug a
symmetric check cannot catch (the `_ball_side_shoulder_moved_back` lesson from
#281) -- it takes a check that is asymmetric BY DESIGN to catch it, not merely
a check that happens to involve two different limbs.

The one thing this script does NOT assume by reasoning is the SIGN of the
torso-twist rotation direction (which way "rotate about `up`" reads as
"twist into the reach") -- that is measured and asserted numerically by
`_swipe_side_advances` below, exactly as #281 had to do for its own torso
twist.

===============================================================================
THE MACHINERY LIVES IN blender_anim_lib (#315)
===============================================================================
Rig geometry, IK, posing primitives, the keypose timeline, and the proof
helpers are all imported from `tools/blender_anim_lib.py`. This file is only
the spec: the per-keypose channel values, the polarity loop, and the
move-specific proofs (pose-distinct, grounded, midline-crossing, and the
off-balance/base-of-support check the brief calls out by name).
"""
import math
import os
import sys

import bpy
from mathutils import Matrix

# Blender runs this file as a script, not as a package member, so `tools/` is not
# importable by default. `--python <path>` does not add the script's own
# directory to sys.path the way `python <path>` does.
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import blender_anim_lib as lib  # noqa: E402  (must follow the sys.path fix)

log = lib.log

# ── clip contract (StealMove.DefaultFrameData, 60 Hz) ────────────────────────
FPS = 60
STARTUP_TICKS = 8
ACTIVE_TICKS = 8
RECOVERY_TICKS = 20
TOTAL_TICKS = STARTUP_TICKS + ACTIVE_TICKS + RECOVERY_TICKS  # 36

T_TELL = STARTUP_TICKS / FPS                              # 0.13333 (Startup end / Active start)
T_ACTIVE_MID = (STARTUP_TICKS + ACTIVE_TICKS / 2.0) / FPS  # 0.20000 (arc midpoint)
T_ACTIVE_END = (STARTUP_TICKS + ACTIVE_TICKS) / FPS        # 0.26667 (Active end / Recovery start)
T_RECOVERY_MID = (STARTUP_TICKS + ACTIVE_TICKS + RECOVERY_TICKS / 2.0) / FPS  # 0.43333
T_RECOVERY_END = TOTAL_TICKS / FPS                         # 0.60000

# Absolute Blender frame numbers -- chosen to equal the issue's own frame table
# verbatim (0/8/16/36 and 60/68/76/96), so a reader cross-checking the
# authoring log against the contract does not have to translate. The 36..60
# gap is never sliced by tools/rebuild_steal_clips.gd; its content is whatever
# Blender's own fcurve interpolation produces holding the neutral keys at both
# ends, and that is fine because it is never read.
LEFT_F0 = 0
RIGHT_F0 = 60
EXPORT_FRAME_END = 96

ACTION_NAME = "steal"

# ── stance geometry, metre-denominated ────────────────────────────────────────
# Reused verbatim from author_behindtheback.py's own measurement on this SAME
# rig (Y Bot: femur/tibia/foot are rig-intrinsic, independent of source clip).
# Not re-measured against Goalkeeper Catch Moving.fbx specifically because the
# foot/ankle placement in this script is fully ABSOLUTE (via plant_foot) and
# overrides whatever stance the source clip itself holds -- exactly like
# author_behindtheback.py's own construction, which is why that script's
# comment about "close to (not copied blindly from) author_dribble_move.py's"
# constants applies transitively here too: this is a defensive crouch depth
# choice for THIS rig, not a per-clip re-measurement.
NEUTRAL_HIP_TO_ANKLE_M = 0.62
STANCE_HALF_WIDTH_M = 0.12

# Ball height above the FLOOR, per the motion spec's 0.65-0.80 m band -- the
# HIGH end chosen deliberately (not the midpoint): a higher ball height sits
# CLOSER to the shoulder, buying reach headroom for a target that already has
# to cross the midline (measured need, not guessed -- see the reach-ratio
# iteration in this file's own authoring log). Still fully inside the spec's
# stated range. Converted to a HIPS-relative height (what aim_arm's target
# actually needs) via the floor identity documented in the module docstring:
# floor = hips_now - up * m(NEUTRAL_HIP_TO_ANKLE_M).
BALL_HEIGHT_ABOVE_FLOOR_M = 0.78
H_BALL = BALL_HEIGHT_ABOVE_FLOOR_M - NEUTRAL_HIP_TO_ANKLE_M  # +0.16 m, hips-relative

# ── keypose channel tables ────────────────────────────────────────────────────
# All channels are in the "reach-direction-relative" convention documented in
# the module docstring: a positive value is placed at
# `body_right * (reach_sign * value)` (lateral channels) or is a plain metre/
# degree magnitude (fore/height/drop/twist channels, sign folded in separately
# where the spec calls for a specific direction -- see comments per channel).
#
# hip_drop_m / hip_lat_m: `drop_hips` takes ONE combined delta vector, so both
# ride the same call. hip_lat_m is signed by reach_sign directly (a positive
# value commits weight TOWARD the reach side); the brief's "shift weight onto
# the FAR foot" during Startup is therefore a NEGATIVE hip_lat_m at the tell.
#
# near_*/far_* (fore, lat): near_foot_side = polarity (steps toward the ball)
# is placed with `reach_sign` -- a positive table value moves it TOWARD the
# reach side, matching every other reach-direction-relative channel here.
# far_foot_side = opposite(polarity) (the Startup weight-bearing ANCHOR) is
# placed with `-reach_sign` instead: its table values are "own-natural-side
# positive" (a plain stance-half-width offset on ITS OWN side), and its own
# natural chain sign is `-reach_sign`, not `reach_sign`. Using `reach_sign`
# for the far foot too was a first-draft bug -- it collapsed both feet toward
# the SAME side instead of anchoring one on each -- caught only because
# `_torso_outside_base_first_half` went red (a hips-vs-near-foot comparison
# would have passed vacuously here regardless; see that function's own
# comment for why it compares against the far foot specifically). The
# authority for the actual sign is `_author_polarity`'s foot loop, not this
# comment.
#
# swipe_* (fore, lat, height): the arm that does the work (arm_side =
# opposite(polarity)). lat is reach_sign-signed: negative/small = still on the
# arm's own side (not crossed yet), positive = across the midline (crossed).
# Swipe-arm targets were pulled IN from a first draft that measured 114%/111%
# of the arm's 0.5502 m reach at the tell pose (frame 8/68) -- see this file's
# authoring log. The tell pose is a low, bent-elbow guard (the ELBOW drops
# behind the hip, per the spec; the HAND does not need to be nearly as low as
# a first draft placed it), so `swipe_height_m` there was raised from -0.18 to
# -0.05 -- much closer to hip height -- rather than shortening `fore`/`lat`,
# which would have blunted the "cocked back" read the Startup tell exists to
# convey. Re-measured after the fix: see the report lines this script prints;
# still update this comment's numbers if the tables below change again.
_KEYPOSES = (
    # (time_s, label, hip_drop_m, hip_lat_m, torso_twist_deg,
    #  near_fore_m, near_lat_m, far_fore_m, far_lat_m,
    #  swipe_fore_m, swipe_lat_m, swipe_height_m)
    (0.0,            "startup",  0.00,  0.00,  0.0,
     0.00, STANCE_HALF_WIDTH_M, 0.00, STANCE_HALF_WIDTH_M,
     0.05, -0.10, -0.05),
    (T_TELL,         "active",   0.12, -0.05,  0.0,
     0.00, STANCE_HALF_WIDTH_M, -0.05, STANCE_HALF_WIDTH_M * 1.3,
     -0.06, -0.05, -0.05),
    (T_ACTIVE_MID,   "active",   0.12,  0.00, 14.0,
     0.15, STANCE_HALF_WIDTH_M * 0.8, -0.03, STANCE_HALF_WIDTH_M * 1.2,
     0.05, 0.12, 0.00),
    (T_ACTIVE_END,   "recovery", 0.10,  0.10, 28.0,
     0.30, STANCE_HALF_WIDTH_M * 0.6, -0.02, STANCE_HALF_WIDTH_M * 1.1,
     0.08, 0.20, H_BALL),
    (T_RECOVERY_MID, "recovery", 0.08,  0.12, 14.0,
     0.20, STANCE_HALF_WIDTH_M * 0.7, 0.00, STANCE_HALF_WIDTH_M,
     0.05, 0.12, 0.05),
    (T_RECOVERY_END, "recovery", 0.00,  0.00,  0.0,
     0.00, STANCE_HALF_WIDTH_M, 0.00, STANCE_HALF_WIDTH_M,
     0.05, -0.10, -0.05),
)

_CHANNEL_NAMES = (
    "hip_drop_m", "hip_lat_m", "torso_twist_deg",
    "near_fore_m", "near_lat_m", "far_fore_m", "far_lat_m",
    "swipe_fore_m", "swipe_lat_m", "swipe_height_m",
)

# The "rest" arm (not swiping) held at a constant, modest guard position on
# ITS OWN natural side (reach_sign convention -- see module docstring: the
# rest arm's chain is `polarity`, whose own natural side coincides with
# reach_sign). It rides with the crouch/lateral shift automatically because
# its target is computed from `hips_now`, which already carries the frame's
# hip delta -- no separate table needed.
REST_FORE_M, REST_LAT_M, REST_HEIGHT_M = 0.03, 0.11, 0.00

# Elbow bend-plane hints (own-side units, NOT normalized -- aim_arm normalizes
# the resulting axis). Down-and-toward-the-reach is where the elbow goes for
# this reach; pattern reused from author_behindtheback.py's own hint shape.
SWIPE_ELBOW_HINT = (-0.6, 0.5)   # (up_component, reach_sign-signed lateral)
REST_ELBOW_HINT = (-0.5, 0.5)

# ── proof thresholds ──────────────────────────────────────────────────────────
# Support-level band. Wider than author_behindtheback.py's 0.14 m: this
# script's HIP_DROP_M peaks at 0.12 m (vs that script's 0.10 m) via the same
# hips-relative ankle-target construction, so the same "not fully absorbed by
# knee bend" slack applies, scaled up slightly.
GROUND_BAND_TOL_M = 0.16
# Startup(tell)-vs-Recovery(neutral) legibility floor (#296). The brief's own
# check 5 -- NOT a whole-clip f0-vs-f1 comparison, which for this move would
# compare neutral against neutral by construction (frame 0 and frame 36 are
# BOTH the neutral stance, per the frame table). See `main()`.
POSE_DISTINCT_MIN_DEG = 15.0
# Cross-polarity Active-end distinctness floor (the #255-class non-symmetric
# control -- matches author_behindtheback.py's own 20.0 for the same purpose).
LEFT_VS_RIGHT_ACTIVE_MIN_DEG = 20.0

# Diagnostic escape hatch: skip the arm solve so a single run can report the
# reach ratio at EVERY keypose instead of dying at the first over-reach. Never
# set for a real authoring run -- the exported FBX would have no arm keys.
_MEASURE_ONLY = os.environ.get("STEAL_MEASURE_ONLY") == "1"


def _side_signs(polarity):
    """`(reach_sign, arm_side)` for `polarity` in {"L","R"}.

    `reach_sign` multiplies every reach-direction-relative channel in this
    file (see module docstring) -- -1 for "L", +1 for "R", matching
    `geom.body_right`'s sign. `arm_side` is the OPPOSITE chain: the hand
    reaching to the defender's left is the RIGHT arm crossing over (module
    docstring).
    """
    reach_sign = -1.0 if polarity == "L" else 1.0
    arm_side = "R" if polarity == "L" else "L"
    return reach_sign, arm_side


def _row_at(t_s):
    """Linearly locate the bracketing rows in `_KEYPOSES` for `t_s`.

    A tiny local lookup (mirrors author_behindtheback.py's `_interp_table`
    pattern) used only by the sanity checks in `main()` to read a channel's
    AUTHORED value at an exact keypose time, without re-deriving the timeline
    interpolation `bake_timeline`/`interp_channels` already own.
    """
    for row in _KEYPOSES:
        if abs(row[0] - t_s) < 1e-9:
            return row
    raise SystemExit(f"FATAL: no authored keypose at t_s={t_s:.5f}")


def _keyposes_for_lib():
    """`_KEYPOSES` translated into `blender_anim_lib.Keypose` objects."""
    out = []
    for row in _KEYPOSES:
        t_s, label = row[0], row[1]
        values = row[2:]
        channels = dict(zip(_CHANNEL_NAMES, values))
        out.append(lib.Keypose(t_s, label, **channels))
    return out


def _author_polarity(arm, geom, body_right, polarity, frame_offset):
    """Key one polarity's Startup/Active/Recovery arc onto `arm`'s action.

    `polarity`: "L" or "R" -- the TARGET hand, i.e. where the swiping hand
    ends up (module docstring -- this is NOT an origin-hand move).
    `frame_offset`: the absolute Blender frame number for this polarity's t=0.

    Returns a dict of measurements for the caller's proofs/report lines.
    """
    reach_sign, arm_side = _side_signs(polarity)
    near_side, far_side = polarity, ("R" if polarity == "L" else "L")
    # `lateral`, NOT `body_right` (#320): this basis is handed to `aim_matrix`
    # as its `side_axis`, a bone-ROLL reference where the axis SIGN is
    # load-bearing but its anatomy is irrelevant. Swapping in `body_right` here
    # would roll the posed bones 180 deg while changing nothing about which side
    # anything lands on. (This file's only `Matrix.Rotation` is a spine twist
    # about `up`, so no lateral-axis rotation is involved either way.)
    right, up, forward = geom.lateral, geom.up, geom.forward

    swipe_humerus_u, swipe_ulna_u = lib.arm_lengths(arm, arm_side)
    rest_humerus_u, rest_ulna_u = lib.arm_lengths(arm, near_side)
    log(f"[{polarity}-target] arm reach: swipe({arm_side})="
        f"{geom.to_m(swipe_humerus_u + swipe_ulna_u):.4f} m "
        f"rest({near_side})={geom.to_m(rest_humerus_u + rest_ulna_u):.4f} m")

    keyposes = _keyposes_for_lib()
    f0 = frame_offset
    f1 = frame_offset + TOTAL_TICKS  # inclusive

    # Captured ONCE, at this polarity's own neutral frame, BEFORE any of our
    # own posing runs. This is the anchor every frame's Hips target is built
    # from -- see the "why not drop_hips" note in `apply` below.
    scene = bpy.context.scene
    with lib.preserve_frame():
        scene.frame_set(f0)
        hips_base = arm.pose.bones[lib.HIPS].head.copy()

    worst_wrist_err = 0.0
    worst_ankle_err = 0.0
    worst_reach = (0.0, "", 0, 0.0)  # (ratio, side, frame, t_s)

    def apply(frame, _t_s_unused, ch):
        nonlocal worst_wrist_err, worst_ankle_err, worst_reach

        # ---- hips: crouch + lateral weight commit, as an ABSOLUTE target off
        # `hips_base`, NOT `lib.drop_hips`'s delta-on-the-source's-own-root-
        # motion. That delta convention is right for a clip whose source root
        # motion IS the thing being adjusted (author_behindtheback.py's
        # Dribble.fbx source, a stationary crouch with a small, controlled
        # bob). "Goalkeeper Catch Moving.fbx" is -- as its name says -- a
        # MOVING clip: measured, its own natural Hips height varies by more
        # than our whole authored crouch band across the frame range this
        # script spans, which blew the grounding proof (0.2247 m vs a 0.16 m
        # tolerance on the first authoring pass). A steal is a stationary
        # defensive commitment, not a moving one, so pinning Hips to a fixed
        # per-polarity anchor plus our OWN authored delta is the correct
        # choice here, not merely a workaround -- it is what "author the
        # trajectory, don't inherit uncontrolled source motion" (this
        # library's own stated method, see blender_anim_lib's module
        # docstring) means when the source's root motion is not the thing the
        # move wants.
        pb = arm.pose.bones[lib.HIPS]
        mh = pb.matrix.copy()
        mh.translation = (hips_base
                           - up * geom.m(ch["hip_drop_m"])
                           + body_right * (reach_sign * geom.m(ch["hip_lat_m"])))
        pb.matrix = mh
        bpy.context.view_layer.update()
        pb.keyframe_insert("location", frame=frame)
        hips_now = pb.head.copy()

        # ---- torso twist, composed onto the source's own spine pose. Sign is
        # `reach_sign` here as the AUTHORED guess; _swipe_side_advances() below
        # measures whether that guess actually rotates the swipe shoulder
        # toward the reach side and fails loudly if it does not (the #281
        # lesson: rotation handedness about `up` is not reliably derivable by
        # eye).
        twist_rad = math.radians(reach_sign * ch["torso_twist_deg"])
        lib.rotate_bone_about_head(
            arm, lib.SPINE, (Matrix.Rotation(twist_rad, 4, up),), frame=frame)

        # ---- legs: fixed toe direction (verbatim author_behindtheback.py),
        # ankle from the near/far foot channels ------------------------------
        # NEAR foot uses `reach_sign` directly (it steps TOWARD the reach
        # side, by construction). FAR foot uses `-reach_sign`: it is the
        # Startup weight-bearing ANCHOR on the side OPPOSITE the reach, and
        # its table values are authored "own-natural-side positive" (a stance
        # half-width offset away from centre on ITS OWN side). Using
        # `reach_sign` for both (a first-draft bug caught by
        # `_torso_outside_base_first_half` going red -- see that function's
        # own comment) would place BOTH feet toward the reach side, collapsing
        # the stance instead of anchoring it.
        toe_dir = (forward * 0.90 - up * 0.44).normalized()
        for side, fore_m, lat_m, side_sign in (
            (near_side, ch["near_fore_m"], ch["near_lat_m"], reach_sign),
            (far_side, ch["far_fore_m"], ch["far_lat_m"], -reach_sign),
        ):
            ankle = (hips_now
                     + forward * geom.m(fore_m)
                     + body_right * (side_sign * geom.m(lat_m))
                     - up * geom.m(NEUTRAL_HIP_TO_ANKLE_M))
            _solved, ankle_err = lib.plant_foot(arm, side, ankle, toe_dir, geom, frame=frame)
            worst_ankle_err = max(worst_ankle_err, ankle_err)

        # ---- arms: swipe arm from the timeline channels, rest arm held -----
        arm_specs = (
            (arm_side, ch["swipe_fore_m"], ch["swipe_lat_m"], ch["swipe_height_m"],
             reach_sign, SWIPE_ELBOW_HINT, swipe_humerus_u, swipe_ulna_u),
            (near_side, REST_FORE_M, REST_LAT_M, REST_HEIGHT_M,
             reach_sign, REST_ELBOW_HINT, rest_humerus_u, rest_ulna_u),
        )
        for side, fore_m, lat_m, height_m, side_sign, hint_spec, humerus_u, ulna_u in arm_specs:
            target = (hips_now
                      + forward * geom.m(fore_m)
                      + body_right * (side_sign * geom.m(lat_m))
                      + up * geom.m(height_m))
            hint_up, hint_lat = hint_spec
            hint = (up * hint_up + body_right * (side_sign * hint_lat)).normalized()

            sh_head = arm.pose.bones[lib.ARM_CHAIN[side][0]].head.copy()
            reach_u = humerus_u + ulna_u
            ratio = (target - sh_head).length / reach_u
            if ratio > worst_reach[0]:
                worst_reach = (ratio, side, frame, _t_s_unused)
            if _MEASURE_ONLY:
                continue

            err_u = lib.aim_arm(arm, side, target, hint, geom, frame=frame)
            worst_wrist_err = max(worst_wrist_err, err_u)

    lib.bake_timeline(arm, keyposes, apply, f0, f1, FPS)

    lib.report(f"{polarity}target_worst_ankle_ik_err_m", f"{geom.to_m(worst_ankle_err):.6f}")
    lib.report(f"{polarity}target_worst_wrist_err_m", f"{geom.to_m(worst_wrist_err):.6f}")
    _ratio, _rside, _rframe, _rt = worst_reach
    lib.report(f"{polarity}target_worst_reach_ratio",
               f"{_ratio:.4f} ({_rside} arm, frame {_rframe}, t={_rt:.4f}s)")

    return {"f0": f0, "f1": f1, "polarity": polarity, "arm_side": arm_side,
            "near_side": near_side, "far_side": far_side, "reach_sign": reach_sign}


def _swipe_side_advances(arm, geom, body_right, res):
    """The swipe hand's reach-direction lateral coordinate (relative to Hips)
    must INCREASE from the Startup tell (frame f0+8) to the Active end
    (frame f0+16) -- i.e. it visibly crosses toward the reach side rather than
    the torso twist sign having rotated it the wrong way.

    This is the numeric oracle for TORSO_TWIST_DEG's sign (see `apply`'s
    comment) and for the swipe arm's own lat channel sign -- both together are
    what make "crosses the midline" real rather than assumed by eye (the #281
    `_ball_side_shoulder_moved_back` lesson, applied to this move's own axis).
    """
    hand_bone = lib.ARM_CHAIN[res["arm_side"]][2]
    f_tell = res["f0"] + STARTUP_TICKS
    f_active_end = res["f0"] + STARTUP_TICKS + ACTIVE_TICKS
    scene = bpy.context.scene
    with lib.preserve_frame():
        scene.frame_set(f_tell)
        hips_tell = arm.pose.bones[lib.HIPS].head.copy()
        hand_tell = arm.pose.bones[hand_bone].head.copy()
        scene.frame_set(f_active_end)
        hips_active = arm.pose.bones[lib.HIPS].head.copy()
        hand_active = arm.pose.bones[hand_bone].head.copy()
    lat_tell = geom.to_m((hand_tell - hips_tell).dot(body_right)) * res["reach_sign"]
    lat_active = geom.to_m((hand_active - hips_active).dot(body_right)) * res["reach_sign"]
    lib.report(f"{res['polarity']}target_swipe_lat_reachdir_tell_m", f"{lat_tell:+.4f}")
    lib.report(f"{res['polarity']}target_swipe_lat_reachdir_active_end_m", f"{lat_active:+.4f}")
    if not (lat_active > lat_tell):
        raise SystemExit(
            f"FATAL: the {res['polarity']}-target swipe hand's reach-direction "
            f"lateral offset went from {lat_tell:+.4f} m (tell) to "
            f"{lat_active:+.4f} m (active-end) -- it did not advance toward the "
            f"reach side. Check TORSO_TWIST_DEG's sign and the swipe_lat_m "
            f"channel against reach_sign in _author_polarity/apply.")
    if not (lat_active > 0.0):
        raise SystemExit(
            f"FATAL: the {res['polarity']}-target swipe hand's reach-direction "
            f"lateral offset at active-end is {lat_active:+.4f} m -- it must be "
            f"POSITIVE (past the Hips' own centreline, i.e. actually crossed) "
            f"not merely advancing from a very negative start.")


def _torso_outside_base_first_half(arm, geom, body_right, res):
    """At the Recovery midpoint (frame f0+26, exactly half of the 20-tick
    Recovery -- the brief's own "roughly frames 16->26" window), the Hips'
    reach-direction lateral coordinate must exceed the FAR/ANCHOR foot's --
    i.e. the torso has leaned laterally PAST its own weight-bearing plant
    foot, which is the genuine "outside the base of support" condition (#100
    blow-by legibility): the far foot is "the plant foot" the brief's motion
    spec names (the Startup anchor), and by Recovery the torso is still
    committed further toward the reach side than that anchor can cover.

    Deliberately NOT compared against the NEAR foot: the near foot's own
    ankle target is defined ADDITIVELY on top of `hips_now` (see `apply`'s
    foot loop), so it is ALWAYS at least as far toward the reach side as the
    hips by construction whenever its own table lat value is positive --
    comparing against it would make this check structurally unfalsifiable
    (it passed vacuously for the wrong reason on a first draft, until the
    FAR-foot sign bug below was found and this comparison was moved off the
    near foot entirely).
    """
    f_mid = res["f0"] + STARTUP_TICKS + ACTIVE_TICKS + RECOVERY_TICKS // 2
    scene = bpy.context.scene
    with lib.preserve_frame():
        scene.frame_set(f_mid)
        hips = arm.pose.bones[lib.HIPS].head.copy()
        far_ankle = arm.pose.bones[lib.LEG_CHAIN[res["far_side"]][2]].head.copy()
    hips_lat = geom.to_m(hips.dot(body_right)) * res["reach_sign"]
    far_lat = geom.to_m(far_ankle.dot(body_right)) * res["reach_sign"]
    margin = hips_lat - far_lat
    lib.report(f"{res['polarity']}target_recovery_mid_outside_base_m", f"{margin:+.4f}")
    if margin <= 0.0:
        raise SystemExit(
            f"FATAL: at the {res['polarity']}-target Recovery midpoint the "
            f"Hips' reach-direction lateral coordinate ({hips_lat:+.4f} m) does "
            f"not exceed the far/anchor foot's ({far_lat:+.4f} m) -- the torso "
            f"is not visibly outside the base of support, so the #100 blow-by "
            f"punish window is not legible.")


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    src, dst = argv[0], argv[1]

    arm, _src_f0, _src_f1 = lib.load_source(src, FPS)
    scene = bpy.context.scene

    geom = lib.RigGeometry(arm)
    geom.log_summary()
    # Anatomical right, derived + verified in the lib (#320).
    body_right = geom.body_right
    lib.report("body_right", tuple(round(v, 4) for v in body_right))
    lib.report("ball_height_above_floor_target_m", f"{BALL_HEIGHT_ABOVE_FLOOR_M:.4f}")
    lib.report("h_ball_hips_relative_m", f"{H_BALL:.4f}")

    lib.enter_pose_mode(arm)

    results = {}
    for polarity, f0 in (("L", LEFT_F0), ("R", RIGHT_F0)):
        results[polarity] = _author_polarity(arm, geom, body_right, polarity, f0)

    bpy.ops.object.mode_set(mode="OBJECT")

    # Export range covers both polarities plus the documented hold gap; the
    # 36..60 gap is included (contiguous with both windows) but its content is
    # never read downstream. See module docstring.
    scene.frame_start, scene.frame_end = 0, EXPORT_FRAME_END

    # ── proofs, before the export commits anything ────────────────────────────
    lib.enter_pose_mode(arm)
    # 52 = Y Bot's 65 bones minus the 13 leaf terminators (matches
    # author_behindtheback.py's / author_dribble_move.py's own gate against
    # the same rig).
    lib.verify_all_bones_keyed(arm, expected_count=52)

    for polarity, res in results.items():
        frames = list(range(res["f0"], res["f1"] + 1))
        lib.verify_pose_unscaled(arm, frames)
        lib.verify_grounded(arm, frames, GROUND_BAND_TOL_M, geom)

        # #296 floor: Startup's TELL pose (its own last frame) vs Recovery's
        # NEUTRAL pose (its own last frame) -- NOT a whole-clip f0-vs-f1
        # comparison, which for this move would compare neutral against
        # neutral by construction (frame 0 and frame 36 are both the neutral
        # stance). This mirrors what rebuild_steal_clips.gd's own G3-equivalent
        # gate will assert on the SLICED clips.
        f_tell = res["f0"] + STARTUP_TICKS
        f_recovery_end = res["f1"]
        lib.verify_pose_distinct(
            lib.snapshot_pose(arm, f_tell),
            lib.snapshot_pose(arm, f_recovery_end),
            POSE_DISTINCT_MIN_DEG,
            label=f"{polarity}target_tell_vs_recoveryend")

        _swipe_side_advances(arm, geom, body_right, res)
        _torso_outside_base_first_half(arm, geom, body_right, res)

    # Active-end cross-polarity distinctness -- the non-symmetric control this
    # move needs (README trap 5 / #255 lesson): a swing that silently ignored
    # its sign argument would still pass every per-polarity check above.
    left_active_end = lib.snapshot_pose(arm, LEFT_F0 + STARTUP_TICKS + ACTIVE_TICKS)
    right_active_end = lib.snapshot_pose(arm, RIGHT_F0 + STARTUP_TICKS + ACTIVE_TICKS)
    lib.verify_pose_distinct(left_active_end, right_active_end,
                              LEFT_VS_RIGHT_ACTIVE_MIN_DEG, label="left_vs_right_active_end")

    # The brief's own check 6: "the signed X of the swiping hand's world
    # position at the Active segment's final frame" -- reported here exactly
    # as asked, in WORLD space (the Y Bot rig is mirror-symmetric to 0.17 mm,
    # so an armature-space symmetric metric proves nothing about handedness on
    # its own; see the follow-up paragraph for why raw world X specifically is
    # NOT the actual pass/fail gate for this clip's source).
    raw_lat = {}
    with lib.preserve_frame():
        for polarity, res in results.items():
            hand_bone = lib.ARM_CHAIN[res["arm_side"]][2]
            f_active_end = res["f0"] + STARTUP_TICKS + ACTIVE_TICKS
            scene.frame_set(f_active_end)
            hips_pos = arm.pose.bones[lib.HIPS].head.copy()
            hand_pos = arm.pose.bones[hand_bone].head.copy()
            world_pos = arm.matrix_world @ hand_pos
            lib.report(f"{polarity}target_swipe_hand_world_x_m", f"{world_pos.x:+.4f}")
            # Hips-relative, UN-normalized by reach_sign (deliberately, unlike
            # `_swipe_side_advances`'s own report lines): this is the same
            # (hand - hips).dot(body_right) quantity, but reach_sign is what
            # flips between polarities, so leaving it in is what makes the
            # SIGN itself the discriminator, matching what the brief's raw
            # world-X ask is actually trying to prove.
            raw_lat[polarity] = geom.to_m((hand_pos - hips_pos).dot(body_right))
            lib.report(f"{polarity}target_swipe_hand_hipsrelative_lat_m", f"{raw_lat[polarity]:+.4f}")

    # FINDING, not a brief error: raw world X is NOT a clean discriminator for
    # THIS source clip. "Goalkeeper Catch Moving.fbx" carries its own
    # uncontrolled horizontal root drift (the same drift that blew the
    # grounding proof before the Hips-pinning fix above), and `hips_base` is
    # captured fresh per polarity at that polarity's OWN f0 (frame 0 for L,
    # frame 60 for R) -- two different, unrelated points along the source's
    # own natural walk. Measured: Ltarget world X=+0.3000 m, Rtarget world
    # X=+0.0064 m -- both POSITIVE, nearly cancelling for R, because each
    # polarity's absolute position is dominated by wherever the source
    # character had drifted to by that polarity's own anchor frame, not by
    # the authored pose. The HIPS-RELATIVE lateral offset above is the metric
    # this script actually gates on, because it is computed against each
    # frame's own Hips position and so cancels that per-polarity drift
    # entirely -- it necessarily has OPPOSITE signs between polarities by
    # construction (reach_sign flips, the table value does not).
    if not (raw_lat["L"] * raw_lat["R"] < 0.0):
        raise SystemExit(
            f"FATAL: the hips-relative swipe-hand lateral offset is "
            f"{raw_lat['L']:+.4f} m for L-target and {raw_lat['R']:+.4f} m for "
            f"R-target -- these must have OPPOSITE signs (the #255 "
            f"non-symmetric handedness control). Raw world X is reported above "
            f"for visibility only; it is confounded by this source clip's own "
            f"root drift and is NOT the gate.")

    lib.export_fbx(arm, dst, ACTION_NAME)
    print("AUTHOR_OK")


main()
