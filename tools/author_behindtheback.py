"""Author `behindtheback` as a two-polarity keypose clip in headless Blender (#281).

Run:
    "$BLENDER" --background --python-exit-code 1 \
        --python tools/author_behindtheback.py -- <src.fbx> <out.fbx>

`--python-exit-code 1` is MANDATORY. Blender's `--background --python` exits 0
even on an uncaught traceback, so without it a crashed authoring run reports
success to the pipeline.

===============================================================================
WHAT THIS PRODUCES
===============================================================================
ONE Blender action, baked at 60 Hz, holding BOTH hand-side polarities on a
single timeline (frame numbers ARE physics ticks -- this is deliberate, so a
reader can cross-reference this file against the issue's frame table directly):

    frames   seconds            segment
    0  -> 6  0.00000 -> 0.10000  LEFT-origin  Startup   (6 ticks)
    6  -> 9  0.10000 -> 0.15000  LEFT-origin  Active    (3 ticks)
    9  -> 19 0.15000 -> 0.31667  LEFT-origin  Recovery  (10 ticks)
    19 -> 30 (never sampled)     hold gap -- neither _slice() window in
                                 tools/rebuild_behindtheback_clips.gd reads it
    30 -> 36 0.50000 -> 0.60000  RIGHT-origin Startup
    36 -> 39 0.60000 -> 0.65000  RIGHT-origin Active
    39 -> 49 0.65000 -> 0.81667  RIGHT-origin Recovery
    49 -> 55 tail hold           exported so the last RIGHT-Recovery frame has
                                 somewhere to interpolate FROM if anything ever
                                 reads past 49; nothing currently does.

"LEFT-origin" means the ball STARTS in the LEFT hand. `BehindTheBack.
DefaultFrameData` = startup 6 / active 3 / recovery 10 ticks at 60 Hz --
verified against `scripts/Input/BehindTheBack.cs`, not re-derived here.

===============================================================================
WHY A KEYPOSE TIMELINE, NOT A GAIT FUNCTION
===============================================================================
`author_dribble_move.py` is a CYCLIC gait authorer -- `phase = (t/CYCLE_S) % 1`.
Behind-the-back is not cyclic: it is a one-shot Startup/Active/Recovery arc, so
this script uses `blender_anim_lib.Keypose` + `bake_timeline` instead (the
machinery #315 built specifically because most of the twenty per-move clips are
this shape, not the dribble's). Four keyposes per polarity:

    t=0.00000  label="startup"   the pre-windup stance
    t=0.10000  label="active"    the wrap -- both wrists behind the hip line
    t=0.15000  label="recovery"  SAME channel values as t=0.10000: Active is a
                                 single HELD pose (3 ticks = 2 frames at 60 Hz),
                                 not a movement, so its start and end pose are
                                 identical by construction. Labelling this
                                 keypose "recovery" (not "active") is what makes
                                 the FOLLOWING segment (0.15->0.31667) resolve to
                                 `ease_in_out` via `PHASE_EASING` -- the segment
                                 BEFORE it (0.10->0.15) resolves off the *prior*
                                 keypose's "active" label to `ease_out`, and
                                 since the two active-phase values are equal
                                 that segment's easing is moot anyway.
    t=0.31667  label="recovery"  the punish-window end pose: off-balance, wide
                                 stance, shoulders past square.

Startup and Recovery MUST differ (#296) -- `verify_pose_distinct` gates it below
per polarity.

===============================================================================
THE SILHOUETTE CONTRAST (the whole point of this clip, per #276/#296)
===============================================================================
Crossover's Active pose puts both hands IN FRONT of the torso at knee height.
Behind-the-back's Active pose puts both hands BEHIND the hip line. Nothing else
needs to differ. `rebuild_behindtheback_clips.gd`'s gate G5 measures this
numerically (both wrists' forward coordinate behind the Hips') and gate G7
prints the same measurement on the shipped `crossoveractiveleft` clip for
contrast -- this script's job is only to make G5 true, not to reproduce G7's
comparison itself.

The reach behind the hip is close to the arm's two-link limit (measured on this
rig: shoulder->wrist reach 0.5502 m -- see `tools/README-blender.md` "Reach
budgets"). `aim_arm` treats over-reach as FATAL (`on_overreach="fail"`)
specifically so a clamp cannot silently ship a locked, mannequin-straight arm;
if it ever fires here, the fix is to pull the hand target IN, not to accept the
clamp.

===============================================================================
`geom.right` POINTS AT THE CHARACTER'S LEFT -- READ THIS BEFORE TOUCHING SIGNS
===============================================================================
Measured here (matches `tools/README-blender.md` and `selftest_anim_lib.py`
exactly): on this source, `LeftArm` sits at `+0.1343 m` along `geom.right` and
`RightArm` at `-0.1804 m`. `derive_axes` negates `right` alongside `forward` in
a branch that fires on every Mixamo rig, and nothing downstream re-checks the
sign anatomically -- `geom.right` is a internally-consistent basis vector, not
an anatomically-named one.

This script therefore NEVER uses `geom.right` directly for hand-side placement.
Every lateral offset goes through `BODY_RIGHT = -geom.right` instead, defined
once in `main()` and threaded through as `body_right` -- positive along it is
the character's actual anatomical right, matching `RightArm`'s sign. Getting
this backwards ships a clip that is a mirror image of its label: it plays
cleanly, passes every symmetric check, and telegraphs the wrong hand (the #255
lesson). The non-symmetric guards below (`_ball_side_shoulder_moved_back`, and
the caller's own G4/G6 in the Godot-side rebuild script) are what would actually
catch that.

===============================================================================
THE MACHINERY LIVES IN blender_anim_lib (#315)
===============================================================================
Rig geometry, IK, posing primitives, the keypose timeline, and the proof helpers
are all imported from `tools/blender_anim_lib.py`. This file is only the spec:
the per-keypose channel values, the polarity loop, and the move-specific proofs
(pose-distinct, grounded, and the shoulder-direction sanity check).
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

# ── clip contract (BehindTheBack.DefaultFrameData, 60 Hz) ────────────────────
FPS = 60
STARTUP_TICKS = 6
ACTIVE_TICKS = 3
RECOVERY_TICKS = 10
TOTAL_TICKS = STARTUP_TICKS + ACTIVE_TICKS + RECOVERY_TICKS  # 19

T_ACTIVE_START = STARTUP_TICKS / FPS          # 0.10000
T_ACTIVE_END = (STARTUP_TICKS + ACTIVE_TICKS) / FPS   # 0.15000
T_RECOVERY_END = TOTAL_TICKS / FPS            # 0.31667

# Absolute Blender frame numbers. Chosen to equal the issue's own frame table
# verbatim (0/6/9/19 and 30/36/39/49) rather than an arbitrary offset, so a
# reader cross-checking the authoring log against the contract does not have to
# translate. The 19..30 gap and the 49..55 tail are never sliced by
# rebuild_behindtheback_clips.gd -- their content is whatever Blender's default
# interpolation/extrapolation produces, and that is fine because it is never
# read.
LEFT_F0 = 0
RIGHT_F0 = 30
EXPORT_FRAME_END = 55

ACTION_NAME = "behindtheback"

# ── rig-derived constants filled in by main() ────────────────────────────────
# (kept as globals so the per-polarity authoring function can stay a plain
# function rather than a class; there is exactly one call site per polarity)

# Baseline crouch/stance geometry, metre-denominated. NEUTRAL_HIP_TO_ANKLE_M and
# STANCE_HALF_WIDTH_M are close to (not copied blindly from)
# `author_dribble_move.py`'s own constants for the same rig and the same source
# clip's crouch stance -- re-measured here directly (LeftFoot/RightFoot 'up'
# average 0.618 m, lateral average 0.130 m below) rather than assumed identical,
# since that script poses a different clip window.
NEUTRAL_HIP_TO_ANKLE_M = 0.62
STANCE_HALF_WIDTH_M = 0.12

# ── channel tables (own-side units; see `_side_signs` for how a polarity's
# physical L/R gets multiplied in) ────────────────────────────────────────────
# Every value is in metres (fore/lat/height, relative to the frame's hips
# position) or degrees (torso_twist). "ball" = the origin hand. "recv" = its
# opposite, which becomes the new ball hand at Active-entry (PlayerController's
# JustEnteredActive branch swaps HandSide there -- this script only has to draw
# the picture, not enforce the swap).
#
# Keys are `(t_s, label)`; `label` drives `PHASE_EASING` (README-blender.md
# "Easing is chosen for you, per phase"). The Active keypose is duplicated at
# t=0.10000 (label "active") and t=0.15000 (label "recovery") with IDENTICAL
# values, which is what makes the 3-tick Active segment a true held pose rather
# than an in-between interpolation -- see the module docstring.
_KEYPOSE_TIMES = (
    (0.0, "startup"),
    (T_ACTIVE_START, "active"),
    (T_ACTIVE_END, "recovery"),
    (T_RECOVERY_END, "recovery"),
)

# fore, lat (own-side, i.e. positive = toward THIS hand's/foot's own side),
# height (relative to hips_now, i.e. after that frame's hip drop is applied)
#
# The two Active rows are REACH-CONSTRAINED, not free choices. Measured: the
# ball hand's first draft (-0.10, 0.05, -0.03) demanded 101.67% of the arm's
# 0.5502 m reach at frame 9, which aim_arm refuses outright (a clamp yields a
# locked straight arm reading as a mannequin). Both Active rows were pulled in
# along the two axes that do NOT carry the silhouette: `height` (raised toward
# true hip height -- which is what the motion spec literally asks for anyway)
# and `lat` (nudged back toward the hand's own side, shortening the shoulder-
# to-wrist span). `fore` is left fully negative on both hands, because THAT is
# the read -- both wrists behind the coronal plane through the hips is the
# whole distinction from a crossover, and it is what the Godot-side G5 gate
# asserts. Trading depth for reach headroom here would have quietly bought
# margin by deleting the move's identity.
BALL_HAND_M = (
    (0.10, 0.22, -0.05),   # startup: relaxed dribble-ready
    (-0.10, 0.09, 0.02),   # active: fully behind the hip line
    (-0.10, 0.09, 0.02),   # (held)
    (0.08, 0.15, 0.08),    # recovery end: trailing/relaxed post-handoff
)
RECV_HAND_M = (
    (0.03, 0.18, 0.10),    # startup: passive, roughly where a still hand hangs
    (-0.07, 0.07, 0.00),   # active: across the small of the back, hip height
    (-0.07, 0.07, 0.00),   # (held)
    (0.18, 0.28, -0.06),   # recovery end: emerges to dribble on the new side
)
# LEAD = the foot planted on the ball-hand side. TRAIL = the opposite foot,
# which becomes the new lead by Recovery end ("the new lead foot plants wide").
LEAD_FOOT_M = (
    (0.00, STANCE_HALF_WIDTH_M),
    (0.08, 0.10),   # active: weight loaded onto the ball-side foot
    (0.08, 0.10),
    (-0.05, 0.17),  # recovery end: trailing, wide
)
TRAIL_FOOT_M = (
    (0.00, STANCE_HALF_WIDTH_M),
    (-0.06, 0.12),
    (-0.06, 0.12),
    (0.13, 0.19),   # recovery end: new lead, plants wide forward
)
# metres of extra hip drop below the source's own crouch, at each keypose.
HIP_DROP_M = (0.00, 0.10, 0.10, 0.05)
# degrees. Sign convention resolved in `_author_polarity`: positive here means
# "rotate the BALL-side shoulder backward" (the Startup/Active wind-up); the
# Recovery-end value is negative, i.e. the torso "unwinds and over-rotates past
# square the OTHER way", per the motion spec.
TORSO_TWIST_DEG = (0.0, 15.0, 15.0, -10.0)

# Elbow bend-plane hints (own-side units, NOT normalized -- aim_arm normalizes
# the resulting axis). Down-and-outward is where a real elbow goes for a reach
# behind and below the shoulder; see selftest_anim_lib.py's own hint pattern for
# the shape (down 0.7-0.8, out 0.4-0.6), reused here rather than re-derived.
BALL_ELBOW_HINT = (-0.7, 0.5)   # (up_component, own-side lateral_component)
RECV_ELBOW_HINT = (-0.6, 0.4)

# ── proof thresholds ──────────────────────────────────────────────────────────
# Support-level band. Wider than author_dribble_move.py's 0.05 m: this move's
# HIP_DROP_M peaks at 0.10 m (vs that script's 0.025 m HIP_BOB_M), and — matching
# that script's own construction — the ankle target tracks `hips_now` (i.e.
# post-drop), so part of the crouch shows up as common-mode ankle travel rather
# than being fully absorbed by extra knee bend. Set from the MEASURED value
# below plus headroom, not guessed.
GROUND_BAND_TOL_M = 0.14
# Startup-vs-Recovery legibility floor (#296). Matches the Godot-side gate G3.
POSE_DISTINCT_MIN_DEG = 15.0


# Diagnostic escape hatch: skip the arm solve so a single run can report the
# reach ratio at EVERY keypose instead of dying at the first over-reach. Never
# set for a real authoring run -- the exported FBX would have no arm keys.
_MEASURE_ONLY = os.environ.get("BTB_MEASURE_ONLY") == "1"


def _side_signs(ball_side):
    """(ball_sign, recv_side) for `ball_side` in {"L","R"}.

    `ball_sign` multiplies every own-side lateral/twist channel to place it on
    the ANATOMICALLY correct side, using `BODY_RIGHT` (see module docstring) --
    never `geom.right` directly. -1 for L, +1 for R matches `BODY_RIGHT`'s sign
    (positive = character's actual right, per the measured RightArm/LeftArm
    lateral figures).
    """
    return (-1.0 if ball_side == "L" else 1.0), ("R" if ball_side == "L" else "L")


def _lerp(a, b, u):
    return a + (b - a) * u


def _interp_table(table, t_s, easing_fn):
    """Interpolate a (fore, lat[, ...]) tuple table across `_KEYPOSE_TIMES`.

    A tiny local stand-in for `lib.interp_channels` scoped to the fixed 4-point
    timeline this script uses -- the shared helper works on named scalar
    channels via `Keypose` objects, which `_author_polarity` uses for the
    thing that actually needs `bake_timeline`'s per-segment easing lookup
    (see there). This one is for reading a plain constants table by phase.
    """
    times = [t for t, _label in _KEYPOSE_TIMES]
    if t_s <= times[0]:
        return table[0]
    if t_s >= times[-1]:
        return table[-1]
    for i in range(len(times) - 1):
        if times[i] <= t_s <= times[i + 1]:
            span = times[i + 1] - times[i]
            u = 0.0 if span <= 0.0 else easing_fn((t_s - times[i]) / span)
            a, b = table[i], table[i + 1]
            return tuple(_lerp(a[j], b[j], u) for j in range(len(a)))
    return table[-1]


def _easing_for(t_s):
    """The `PHASE_EASING` curve for the segment containing `t_s`."""
    times = [t for t, _label in _KEYPOSE_TIMES]
    labels = [lb for _t, lb in _KEYPOSE_TIMES]
    for i in range(len(times) - 1):
        if times[i] <= t_s <= times[i + 1]:
            return lib.PHASE_EASING.get(labels[i], lib.DEFAULT_EASING)
    return lib.DEFAULT_EASING


def _author_polarity(arm, geom, body_right, ball_side, frame_offset):
    """Key one polarity's Startup/Active/Recovery arc onto `arm`'s action.

    `ball_side`: "L" or "R" -- the physical hand the move ORIGINATES in.
    `frame_offset`: the absolute Blender frame number for this polarity's t=0.

    Returns a dict of measurements for the caller's proofs/report lines.
    """
    ball_sign, recv_side = _side_signs(ball_side)
    recv_sign = -ball_sign
    right, up, forward = geom.right, geom.up, geom.forward

    ball_humerus_u, ball_ulna_u = lib.arm_lengths(arm, ball_side)
    recv_humerus_u, recv_ulna_u = lib.arm_lengths(arm, recv_side)
    log(f"[{ball_side}-origin] arm reach: ball={geom.to_m(ball_humerus_u + ball_ulna_u):.4f} m "
        f"recv={geom.to_m(recv_humerus_u + recv_ulna_u):.4f} m")

    worst_wrist_err = 0.0
    worst_ankle_err = 0.0
    worst_reach = (0.0, "", 0, 0.0)  # (ratio, side, frame, t_s)
    scene = bpy.context.scene

    f0 = frame_offset
    f1 = frame_offset + TOTAL_TICKS  # inclusive

    for i, f in enumerate(range(f0, f1 + 1)):
        scene.frame_set(f)
        t_s = i / FPS
        easing = _easing_for(t_s)

        # ---- hips: crouch, keyed as a DELTA on the source's own root motion --
        drop_m = _lerp_scalar_table(HIP_DROP_M, t_s, easing)
        lib.drop_hips(arm, -(up * geom.m(drop_m)), geom, frame=f)
        hips_now = arm.pose.bones[lib.HIPS].head.copy()

        # ---- torso twist, composed onto whatever the source's own spine pose
        # already is at this frame (preserves the crouch lean baked into
        # Dribble.fbx; see module docstring for the sign derivation).
        twist_deg = _lerp_scalar_table(TORSO_TWIST_DEG, t_s, easing)
        # NEGATIVE ball_sign. The sign was originally derived from body_right's
        # own sign and was wrong: _ball_side_shoulder_moved_back measured the
        # L-origin ball shoulder travelling +0.0196 m ALONG forward between
        # Startup and Active, i.e. the wind-up rotated the wrong way. Rotation
        # handedness about `up` is not reliably derivable by eye from the axis
        # convention, so the measurement is the oracle here, not the reasoning
        # -- which is exactly why that gate is not optional.
        twist_rad = math.radians(-ball_sign * twist_deg)
        lib.rotate_bone_about_head(
            arm, lib.SPINE, (Matrix.Rotation(twist_rad, 4, up),), frame=f)

        # ---- legs: fixed toe direction, ankle from the (own-side) foot table -
        toe_dir = (forward * 0.90 - up * 0.44).normalized()
        for side, table, side_sign in (
            (ball_side, LEAD_FOOT_M, ball_sign),
            (recv_side, TRAIL_FOOT_M, recv_sign),
        ):
            fore_m, lat_m = _lerp2(table, t_s, easing)
            ankle = (hips_now
                     + forward * geom.m(fore_m)
                     + body_right * (side_sign * geom.m(lat_m))
                     - up * geom.m(NEUTRAL_HIP_TO_ANKLE_M))
            _solved, ankle_err = lib.plant_foot(arm, side, ankle, toe_dir, geom, frame=f)
            worst_ankle_err = max(worst_ankle_err, ankle_err)

        # ---- arms: aim_arm from the (own-side) hand table --------------------
        for side, table, side_sign, hint_spec, humerus_u, ulna_u in (
            (ball_side, BALL_HAND_M, ball_sign, BALL_ELBOW_HINT, ball_humerus_u, ball_ulna_u),
            (recv_side, RECV_HAND_M, recv_sign, RECV_ELBOW_HINT, recv_humerus_u, recv_ulna_u),
        ):
            fore_m, lat_m, height_m = _lerp3(table, t_s, easing)
            target = (hips_now
                      + forward * geom.m(fore_m)
                      + body_right * (side_sign * geom.m(lat_m))
                      + up * geom.m(height_m))
            hint_up, hint_lat = hint_spec
            hint = (up * hint_up + body_right * (side_sign * hint_lat)).normalized()

            # Measure the reach demand BEFORE handing the target to aim_arm,
            # which raises on over-reach with no frame/keypose context (it
            # hardcodes on_overreach="fail" and cannot know what we were
            # aiming at). Reporting the ratio here is what turns "IK target
            # 55.77 exceeds reach 55.02" into an actionable "the L ball hand
            # at frame 6 wants 101.4% of its reach".
            sh_head = arm.pose.bones[lib.ARM_CHAIN[side][0]].head.copy()
            reach_u = humerus_u + ulna_u
            ratio = (target - sh_head).length / reach_u
            if ratio > worst_reach[0]:
                worst_reach = (ratio, side, f, t_s)
            if _MEASURE_ONLY:
                continue

            err_u = lib.aim_arm(arm, side, target, hint, geom, frame=f)
            worst_wrist_err = max(worst_wrist_err, err_u)

    lib.report(f"{ball_side}origin_worst_ankle_ik_err_m", f"{geom.to_m(worst_ankle_err):.6f}")
    lib.report(f"{ball_side}origin_worst_wrist_err_m", f"{geom.to_m(worst_wrist_err):.6f}")
    _ratio, _rside, _rframe, _rt = worst_reach
    lib.report(f"{ball_side}origin_worst_reach_ratio",
               f"{_ratio:.4f} ({_rside} arm, frame {_rframe}, t={_rt:.4f}s)")

    return {"f0": f0, "f1": f1, "ball_side": ball_side, "recv_side": recv_side}


def _lerp_scalar_table(table, t_s, easing_fn):
    return _interp_table(tuple((v,) for v in table), t_s, easing_fn)[0]


def _lerp2(table, t_s, easing_fn):
    return _interp_table(table, t_s, easing_fn)


def _lerp3(table, t_s, easing_fn):
    return _interp_table(table, t_s, easing_fn)


def _ball_side_shoulder_moved_back(arm, geom, forward, ball_side, f_start, f_active):
    """The ball-side shoulder's forward coordinate must DECREASE from Startup's
    first frame to the Active pose -- i.e. the shoulder rotates backward, per
    the motion spec ("the ball-side shoulder rotates back ~15deg").

    A pure eyeball check on a sign that was DERIVED (not measured) earlier in
    this file's docstring; asserting it numerically is cheap and turns an
    assumption into a proven fact, per this repo's convention.
    """
    humerus = lib.ARM_CHAIN[ball_side][0]
    scene = bpy.context.scene
    with lib.preserve_frame():
        scene.frame_set(f_start)
        fore_start = arm.pose.bones[humerus].head.dot(forward)
        scene.frame_set(f_active)
        fore_active = arm.pose.bones[humerus].head.dot(forward)
    shift = geom.to_m(fore_active - fore_start)
    lib.report(f"{ball_side}origin_ball_shoulder_fore_shift_m", f"{shift:+.4f}")
    if shift >= 0.0:
        raise SystemExit(
            f"FATAL: the {ball_side}-origin ball-side shoulder moved {shift:+.4f} m "
            f"ALONG forward (Startup -> Active) -- expected backward (negative). "
            f"Check TORSO_TWIST_DEG's sign against ball_sign in _author_polarity.")


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    src, dst = argv[0], argv[1]

    arm, _src_f0, _src_f1 = lib.load_source(src, FPS)
    scene = bpy.context.scene

    geom = lib.RigGeometry(arm)
    geom.log_summary()
    body_right = -geom.right  # see module docstring: geom.right points LEFT.
    lib.report("body_right", tuple(round(v, 4) for v in body_right))

    lib.enter_pose_mode(arm)

    results = {}
    for ball_side, f0 in (("L", LEFT_F0), ("R", RIGHT_F0)):
        results[ball_side] = _author_polarity(arm, geom, body_right, ball_side, f0)

    bpy.ops.object.mode_set(mode="OBJECT")

    # Export range covers both polarities plus the documented tail hold; the
    # 19..30 gap is included (it is contiguous with both windows) but its
    # content is never read downstream. See module docstring.
    scene.frame_start, scene.frame_end = 0, EXPORT_FRAME_END

    # ── proofs, before the export commits anything ────────────────────────────
    lib.enter_pose_mode(arm)
    # 52 = Y Bot's 65 bones minus the 13 leaf terminators (matches
    # author_dribble_move.py's own gate against the same source).
    lib.verify_all_bones_keyed(arm, expected_count=52)

    for ball_side, res in results.items():
        frames = list(range(res["f0"], res["f1"] + 1))
        lib.verify_pose_unscaled(arm, frames)
        lib.verify_grounded(arm, frames, GROUND_BAND_TOL_M, geom)
        lib.verify_pose_distinct(
            lib.snapshot_pose(arm, res["f0"]),
            lib.snapshot_pose(arm, res["f1"]),
            POSE_DISTINCT_MIN_DEG,
            label=f"{ball_side}origin_startup_vs_recovery")
        _ball_side_shoulder_moved_back(
            arm, geom, geom.forward, ball_side, res["f0"],
            res["f0"] + STARTUP_TICKS)

    # Active-pose cross-polarity distinctness -- the non-symmetric control this
    # move needs (README trap 5 / #255 lesson): a swing that silently ignored
    # its sign argument would still pass every per-polarity check above.
    left_active = lib.snapshot_pose(arm, LEFT_F0 + STARTUP_TICKS)
    right_active = lib.snapshot_pose(arm, RIGHT_F0 + STARTUP_TICKS)
    lib.verify_pose_distinct(left_active, right_active, 20.0, label="left_vs_right_active")

    lib.export_fbx(arm, dst, ACTION_NAME)
    print("AUTHOR_OK")


main()
