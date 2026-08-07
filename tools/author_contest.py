"""Author `contest` as a single-polarity keypose clip in headless Blender (#314).

Run:
    "$BLENDER" --background --python-exit-code 1 \
        --python tools/author_contest.py -- <src.fbx> <out.fbx>

`--python-exit-code 1` is MANDATORY. Blender's `--background --python` exits 0
even on an uncaught traceback, so without it a crashed authoring run reports
success to the pipeline.

===============================================================================
THE ONE MOVE THAT MUST NEVER LEAVE THE GROUND
===============================================================================
Contest is the GROUNDED on-ball contest (#99, PR #221) and its whole identity is
that it is *not* a block. `ContestMove.cs` states the design intent directly:

    less commitment than block's full swat wind-up (BlockMove Startup=10) or
    steal's swipe wind-up (StealMove Startup=8); still a real, committed move

That ordering -- contest 6 < steal 8 < block 10 -- is a deliberate commitment
ladder, and this clip is where a player perceives it.

    Block leaves the ground with both arms up. Contest raises both arms up and
    keeps the feet PLANTED. Same arms, different base. FEET ARE THE READ.

So `verify_grounded` runs on ALL THREE segments here, where every other
airborne-capable move in the batch exempts one. If this clip goes airborne it
has become a block and the ladder above collapses.

===============================================================================
GROUNDED IS NOT ENOUGH ON ITS OWN -- THE VACUITY TRAP
===============================================================================
`verify_grounded` passes trivially on a clip that never moves at all: a rig
frozen in its source pose for 34 frames is, technically, extremely grounded.
Proving "the feet stayed down" is therefore only half a proof, and the weaker
half.

So the grounded gates below are PAIRED with positive assertions that the move
genuinely happened:

    _verify_arms_rise_in_active   both wrists go ABOVE THE HEAD during Active,
                                  and do NOT during Startup. This is the "arms
                                  up" half of the read, and it is what makes
                                  the grounded half meaningful -- together they
                                  say "arms up, feet down", which is the whole
                                  move.
    verify_pose_distinct          Startup != Recovery (#296).

Same discipline the Godot-side harness applies: every "X did not happen" claim
is paired with a scenario asserting its own premise.

===============================================================================
UNHANDED -- ONE POLARITY, AND SYMMETRIC BY DESIGN
===============================================================================
Per the issue: handedness is **No**. A contest raises BOTH arms, so the two arms
are authored from ONE set of channels, mirrored laterally (`+lat` right, `-lat`
left).

This is the one place in the batch where a symmetric pose is CORRECT rather than
a red flag. README trap 5 ("a hand-side assertion needs a NON-symmetric control",
because the Y Bot rig is mirror-symmetric to 0.17 mm) governs moves that HAVE a
polarity to prove; contest has none, so there is no hand-side claim here to
control for. The controls this clip does need are the grounded/arms-up pair
above, not a mirror.

===============================================================================
WHAT THIS PRODUCES
===============================================================================
ONE Blender action, baked at 60 Hz, frame numbers ARE physics ticks:

    frames     seconds              segment
    0  -> 6    0.00000 -> 0.10000   Startup   (6 ticks -- minimal commitment)
    6  -> 14   0.10000 -> 0.23333   Active    (8 ticks -- arms up, feet planted)
    14 -> 34   0.23333 -> 0.56667   Recovery  (20 ticks -- balanced, not floored)

===============================================================================
MOTION, PHASE BY PHASE
===============================================================================
Startup (0->6): minimal commitment, and that IS the point. The stance widens
slightly (~0.05 m per foot); both arms begin to rise; the hips drop only
~0.05 m. Compare block's 0.20 m load and steal's weight-onto-the-far-foot: this
pose must look recoverable, because it is.

WEIGHT STAYS CENTRED is structural here, not a value in the table: the Hips are
authored as `hips_base + up * offset` -- a purely VERTICAL delta off a fixed
anchor -- so there is no lateral or forward hip channel that could shift the
weight even by accident. The feet move outward symmetrically (`stance_lat_m`
applies `+lat` right and `-lat` left), which widens the base without moving its
centre.

Active (6->14): both arms extended up and slightly forward; torso upright (the
pitch channel returns to 0); hips at or just below neutral; both feet flat,
heels down. Eight ticks -- the arms reach full extension and HOLD, with the base
unchanged. The hold is why f10 and f14 carry the same apex height.

Recovery (14->34): arms come down through the front; stance re-centres to
neutral; hips settle into the defensive crouch. Grounded the entire time.
Twenty ticks is long, but unlike block and steal the end pose is BALANCED -- the
punishment for a contest is being unable to react for a third of a second, not
being on the floor.

===============================================================================
WHY THE END POSE IS LOWER THAN THE START POSE
===============================================================================
Not a stylistic accident, and worth stating because the obvious reading of
"Recovery returns to the defensive crouch" is "Recovery returns to frame 0".

If frame 0 and frame 34 were the same stance, `verify_pose_distinct` would fail
by construction -- and it SHOULD, because that identity is exactly the #296
defect (a wind-up and a punish window an opponent cannot tell apart). So frame 0
is authored as the taller, more upright READY stance the defender contests
from, and frame 34 as the settled, gathered crouch they recover into: hips
0.10 m lower, hands drawn in (`arm_lat` 0.22 -> 0.12) rather than merely
dropped. Both are legitimate defensive stances; they are deliberately not the
SAME one.

===============================================================================
LATERAL SIGN CONVENTION
===============================================================================
Same as `author_layup.py`: lateral offsets go through `geom.body_right`, the
rig's anatomical right (derived from the shoulder span and cross-checked
against the hip span in `blender_anim_lib.derive_body_right`). `geom.lateral`
is a basis vector only -- on this rig it points at the character's LEFT and
must NOT be used for hand/foot placement. There is no longer a `geom.right`;
the local `-geom.right` workaround is gone, replaced by the shared accessor
(#320). Since this clip is symmetric there is no polarity indirection at all
-- the right arm takes `+lat`, the left `-lat`, and that is the whole
convention.

===============================================================================
COSMETIC-ONLY (the dense surface this move sits on)
===============================================================================
This script is an ASSET BUILD TOOL. It does not import, read, or feed
`DefensiveResolution.Succeeds`, `StealReachRadius`, the #214 block reach gate,
the on-ball contest scatter penalty, or any ADR-0018 timing window. The clip
VISUALISES the contest window; it never defines it.
"""

import math
import os
import sys

import bpy
from mathutils import Matrix

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import blender_anim_lib as lib  # noqa: E402  (must follow the sys.path fix)

log = lib.log

# ── clip contract, 60 Hz ──────────────────────────────────────────────────────
# The source clip this move is authored OVER, enforced by `lib.load_source`.
# Every threshold in this file was read off a run against this file; see that
# function's docstring for why the source is load-bearing rather than a
# formality, and for the misdiagnosis that motivated the check.
EXPECTED_SOURCE = "Goalkeeper Catch Stationary.fbx"

FPS = 60
STARTUP_TICKS = 6
ACTIVE_TICKS = 8
RECOVERY_TICKS = 20
TOTAL_TICKS = STARTUP_TICKS + ACTIVE_TICKS + RECOVERY_TICKS  # 34

F0 = 0
F1 = TOTAL_TICKS  # inclusive -- frame numbers ARE physics ticks

# Segment boundaries as frame numbers, named once so the gates below and the
# rebuild script's slice windows cannot drift apart silently.
STARTUP_END = STARTUP_TICKS                    # 6
ACTIVE_END = STARTUP_TICKS + ACTIVE_TICKS      # 14

ACTION_NAME = "contest"

# ── stance geometry, metre-denominated ────────────────────────────────────────
# Reused verbatim from author_behindtheback.py / author_layup.py's measurement on
# this SAME rig (Y Bot: femur/tibia/foot are rig-intrinsic, independent of the
# source clip).
NEUTRAL_HIP_TO_ANKLE_M = 0.62
STANCE_HALF_WIDTH_M = 0.12

# Torso pitch sign. A defensive crouch pitches slightly FORWARD, unlike the
# layup's backward lean.
#
# MEASURED, and the initial +1.0 guess was wrong: `_torso_pitch_sign_is_forward`
# reports a +1.0 rotation about `body_right` moves the spine->head vector
# -0.0810 m along `forward`, i.e. BACKWARD. Hence -1.0 here.
#
# This is consistent with author_layup.py rather than in tension with it: that
# script WANTS a backward lean (the speed-to-height conversion that makes a layup
# read as a layup) and correctly uses +1.0. Same rig, same axis, opposite intent.
TORSO_PITCH_SIGN = -1.0

# Overhead extension above the (measured) rest shoulder height for the Active
# apex hand target. `aim_arm` treats over-reach as FATAL, so this is sized
# against the rig's MEASURED budget rather than guessed: shoulder sits 0.3717 m
# above the hips and the arm reaches 0.5502 m, so an apex of
# 0.3717 + 0.45 = 0.8217 m puts the wrist 0.45 m above the shoulder and needs
# sqrt(0.45^2 + 0.10^2) = 0.461 m of reach -- ratio 0.84, inside the budget with
# room, while still clearing the head by enough to satisfy
# WRIST_ABOVE_HEAD_MIN_M (a smaller extension measured only ~0.07 m of head
# clearance and would have failed that gate).
CONTEST_APEX_EXTENSION_ABOVE_SHOULDER_M = 0.45

# ── keypose channel table ─────────────────────────────────────────────────────
# Columns:
#   time_s, label,
#   hip_offset_m     (+up, delta from neutral -- VERTICAL ONLY, see docstring)
#   torso_pitch_deg  (magnitude; TORSO_PITCH_SIGN supplies the sign)
#   stance_lat_m     (extra half-width per foot beyond STANCE_HALF_WIDTH_M)
#   foot_fore_m      (both feet together; kept ~0 -- weight stays centred)
#   arm_fore_m, arm_lat_m, arm_height_m   (BOTH arms; lat mirrored per side,
#                                          height for the apex rows is patched
#                                          in main() from the measured shoulder)
_APEX_HEIGHT_PLACEHOLDER = None  # patched onto the two "active" apex rows in main()

# REACH BUDGET -- measured per frame, because the naive model is wrong twice.
#
# The obvious estimate is "the arm spans sqrt((arm_h - shoulder_height)^2 +
# arm_fore^2)" using the 0.3717 m REST shoulder height. Both terms mislead.
# Instrumenting the solve (CONTEST_MEASURE_ONLY=1, which reports the components
# at every frame instead of dying at the first over-reach) measured, at f34:
#
#   up  = -0.5000   the shoulder sits 0.48 m above hips_now, not 0.3717 -- the
#                   rest figure is taken before any posing, and the source's own
#                   spine pose raises the girdle
#   fore = +0.2652  the shoulder sits ~0.109 m BEHIND the hips along `forward`,
#                   so an arm_fore of 0.15 is really 0.26 m of forward reach
#   lat  = -0.0241  small, and NOT the cause of the L/R asymmetry it looked like
#
# Two drafts died here: hands at arm_h -0.20 / arm_fore 0.24 needed 0.62 m
# (ratio 1.13), and arm_h -0.02 / arm_fore 0.15 still needed 0.564 m (1.03),
# against a 0.5502 m reach. `aim_arm` treats over-reach as FATAL by design -- a
# clamped arm locks straight and reads as a mannequin -- so the tail poses are
# sized against the MEASURED offsets above, not the rest-pose estimate.
#
# Consequence worth naming: the hands stay around hip height rather than hanging
# at the thighs. That is also the better defensive pose -- a recovering defender
# keeps their hands live -- so the constraint and the intent agree here.
_KEYPOSES_RAW = [
    # t_s,             label,       hip_off, pitch, stance_lat, foot_fore, arm_fore, arm_lat, arm_h
    # Frame 0 -- the upright READY stance the contest is thrown from, hands low.
    [0.00000,          "startup",    0.00,    2.0,   0.00,       0.00,      0.14,     0.22,     0.04],
    [3 / FPS,          "startup",   -0.03,    3.0,   0.03,       0.00,      0.13,     0.24,     0.18],
    # Frame 6 is the Startup/Active SLICE BOUNDARY -- the last frame of
    # `conteststartup` and the first of `contestactive` simultaneously. The arms
    # are already well up here: the wind-up is only six ticks, so the rise has to
    # be most of the way done by the time Active begins, or the "arms up" read
    # lands after the window it is supposed to telegraph.
    [STARTUP_END / FPS, "active",   -0.05,    2.0,   0.05,       0.00,      0.12,     0.25,     0.38],
    # f10 / f14 carry the SAME apex height: Active is eight ticks of reaching
    # full extension and HOLDING, per the issue's motion spec ("the arms reaching
    # full extension and holding, with the base unchanged").
    [10 / FPS,          "active",   -0.03,    0.0,   0.05,       0.00,      0.10,     0.22,    _APEX_HEIGHT_PLACEHOLDER],
    [ACTIVE_END / FPS,  "recovery", -0.03,    0.0,   0.05,       0.00,      0.10,     0.22,    _APEX_HEIGHT_PLACEHOLDER],
    # Arms come down THROUGH THE FRONT (arm_fore grows as arm_h falls) rather
    # than dropping straight to the sides.
    [20 / FPS,          "recovery", -0.05,    3.0,   0.04,       0.00,      0.12,     0.20,     0.28],
    [26 / FPS,          "recovery", -0.08,    5.0,   0.02,       0.00,      0.10,     0.16,     0.16],
    # Frame 34 -- the settled, gathered defensive crouch. Deliberately NOT frame
    # 0's stance; see the docstring's "WHY THE END POSE IS LOWER" section. The
    # distinctness is carried by the hips (0.00 -> -0.10), the pitch (2 -> 6 deg)
    # and the hands drawing IN (lat 0.22 -> 0.12), not by dropping the hands
    # lower than the reach budget allows.
    [TOTAL_TICKS / FPS, "recovery", -0.10,    6.0,   0.00,       0.00,      0.08,     0.12,     0.10],
]

_CHANNEL_NAMES = (
    "hip_offset_m", "torso_pitch_deg", "stance_lat_m", "foot_fore_m",
    "arm_fore_m", "arm_lat_m", "arm_height_m",
)

# Elbow bend-plane hints, mirrored per side (up + outward). One fixed hint per
# arm across the whole timeline, same pattern as author_layup.py: it only has to
# avoid being exactly parallel to the reach direction, which a shallow
# up-and-outward hint safely is at every keypose here.
ELBOW_HINT_UP = 0.3
ELBOW_HINT_LAT = 0.6

# ── proof thresholds ──────────────────────────────────────────────────────────
# Support-level band, applied to ALL THREE segments against ONE shared ground
# reference (see `main()`).
#
# Dramatically tighter than author_layup.py's 0.18 m Startup band, and that is
# the POINT rather than an accident: the ankles here are anchored to a fixed
# floor (see `apply`), so the feet do not move vertically at all and the only
# residual is the foot IK solver's own error. 0.02 m was chosen as roughly 5x a
# then-measured ~0.004 m residual -- loose enough not to be flaky, tight enough
# that ANY real vertical excursion fails.
#
# THAT RATIONALE IS NOW STALE (#321): the solver residual is 0, so the 5x
# headroom no longer describes anything. The VALUE is still fine -- it is a
# vertical-excursion gate, not a solver-noise gate -- but do not re-derive it
# from the old multiplier. Re-tuning it needs a fresh measurement of what
# excursion this clip should be allowed.
#
# This is the gate that distinguishes contest from block, so it is deliberately
# not sized for comfort: a clip that drifts even 3 cm off the floor is one that
# has started to become a block.
GROUND_BAND_TOL_M = 0.02
# Startup(f0)-vs-Recovery(f34) legibility floor (#296). Matches the other
# scripts' 15.0 deg floor.
POSE_DISTINCT_MIN_DEG = 15.0
# How far ABOVE THE HEAD each wrist must sit at the Active apex. This is the
# positive half of the "arms up, feet down" read -- without it, the grounded
# gates above would pass on a rig that never moved (see the docstring's vacuity
# section).
WRIST_ABOVE_HEAD_MIN_M = 0.10
# ... and the same measurement must NOT be satisfied during Startup, or "the
# arms rose during Active" is true of a clip holding them overhead throughout.
WRIST_ABOVE_HEAD_STARTUP_MAX_M = 0.0

# Diagnostic escape hatch: skip the arm solve so a single run can report the
# reach ratio at EVERY frame instead of dying at the first over-reach. Never set
# for a real authoring run -- the exported FBX would have no arm keys.
_MEASURE_ONLY = os.environ.get("CONTEST_MEASURE_ONLY") == "1"


def _keyposes_for_lib():
    """`_KEYPOSES_RAW` translated into `blender_anim_lib.Keypose` objects."""
    out = []
    for row in _KEYPOSES_RAW:
        t_s, label = row[0], row[1]
        channels = dict(zip(_CHANNEL_NAMES, row[2:]))
        out.append(lib.Keypose(t_s, label, **channels))
    return out


def _torso_pitch_sign_is_forward(arm, geom, body_right, forward):
    """A positive `TORSO_PITCH_SIGN` rotation must tip the torso FORWARD.

    Rotation handedness about a derived axis is not reliably guessable by eye
    (author_behindtheback.py's torso-twist sign needed the same treatment), so
    `TORSO_PITCH_SIGN` gets a numeric oracle rather than trust.

    ISOLATED ON PURPOSE. The obvious oracle -- compare the head's position at an
    upright frame against a pitched frame -- was written first and is INVALID
    here: it measured -0.4816 m, which is an order of magnitude more than a 6 deg
    pitch of a ~0.6 m spine can produce (~0.06 m). The excess is the SOURCE
    clip's own upper-body motion. "Goalkeeper Catch Stationary.fbx" is a catch,
    and this script authors the arms and legs by IK but composes only a pitch
    onto the spine, leaving the source's neck/head animation in place -- so any
    two-frame comparison is dominated by source drift, not by the channel under
    test.

    So this tests the ROTATION ITSELF, at a single frame, with no baking: take
    the spine->head vector, rotate it by the signed pitch, and check the forward
    component grew. Source drift cancels because both sides are the same frame.
    """
    spine_head = arm.pose.bones[lib.SPINE].head.copy()
    head_head = arm.pose.bones["mixamorig:Head"].head.copy()
    vec = head_head - spine_head
    rot = Matrix.Rotation(math.radians(TORSO_PITCH_SIGN * 10.0), 4, body_right)
    delta_fore = geom.to_m(((rot @ vec) - vec).dot(forward))
    lib.report("torso_pitch_sign_fore_delta_m", f"{delta_fore:+.4f}")
    if delta_fore <= 0.0:
        raise SystemExit(
            f"FATAL: a positive TORSO_PITCH_SIGN ({TORSO_PITCH_SIGN}) rotation "
            f"moves the spine->head vector {delta_fore:+.4f} m ALONG forward, i.e. "
            f"BACKWARD. A defensive crouch leans IN toward the handler, not away. "
            f"Flip TORSO_PITCH_SIGN.")


def _wrist_above_head_m(arm, frame, geom):
    """Lower of the two wrists, relative to the Head bone, at `frame` (metres).

    The LOWER of the two on purpose: this clip is symmetric and the claim is
    "BOTH arms went up". Taking the higher wrist would let a clip that raised one
    arm and left the other down satisfy an "arms up" gate -- which is a steal or
    a one-handed block silhouette, not a contest.
    """
    with lib.preserve_frame():
        bpy.context.scene.frame_set(frame)
        head_y = arm.pose.bones["mixamorig:Head"].head.dot(geom.up)
        wrists = [arm.pose.bones[lib.ARM_CHAIN[s][2]].head.dot(geom.up) for s in ("L", "R")]
    return geom.to_m(min(wrists) - head_y)


def _verify_arms_rise_in_active(arm, geom):
    """Both wrists go above the head during Active, and do NOT during Startup.

    The positive half of the read, and the reason the three `verify_grounded`
    calls are not a vacuous proof. Reported as a pair so a failure names which
    half broke.
    """
    active_frames = range(STARTUP_END, ACTIVE_END + 1)
    startup_frames = range(F0, STARTUP_END + 1)

    best_active = max(_wrist_above_head_m(arm, f, geom) for f in active_frames)
    best_startup = max(_wrist_above_head_m(arm, f, geom) for f in startup_frames)
    lib.report("wrist_above_head_active_m", f"{best_active:.4f}")
    lib.report("wrist_above_head_startup_m", f"{best_startup:.4f}")

    if best_active < WRIST_ABOVE_HEAD_MIN_M:
        raise SystemExit(
            f"FATAL: the lower wrist peaked only {best_active:.4f} m above the head "
            f"during Active (required >= {WRIST_ABOVE_HEAD_MIN_M} m) -- the arms "
            f"never went up, so this is not a contest. Note that the grounded "
            f"gates would still PASS on this clip, which is exactly why this "
            f"assertion exists.")
    if best_startup > WRIST_ABOVE_HEAD_STARTUP_MAX_M:
        raise SystemExit(
            f"FATAL: the arms were already {best_startup:.4f} m above the head "
            f"during Startup (ceiling {WRIST_ABOVE_HEAD_STARTUP_MAX_M} m). The "
            f"overhead extension has to be an EVENT the opponent can read, not a "
            f"pose the clip holds throughout -- otherwise the wind-up telegraphs "
            f"nothing (ADR-0003).")


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    src, dst = argv[0], argv[1]

    arm, _src_f0, _src_f1 = lib.load_source(src, FPS, expected=EXPECTED_SOURCE)
    scene = bpy.context.scene

    geom = lib.RigGeometry(arm)
    geom.log_summary()
    # Anatomical right, derived + verified in the lib (#320).
    body_right = geom.body_right
    up, forward = geom.up, geom.forward
    lib.report("body_right", tuple(round(v, 4) for v in body_right))

    lib.enter_pose_mode(arm)

    # Anchor Hips position, captured ONCE before any of this script's own posing.
    # Every frame's Hips target is built from this, so the move authors its own
    # (purely vertical) trajectory rather than inheriting the source's root
    # motion -- and, here, so "weight stays centred" is structural.
    hips_base = arm.pose.bones[lib.HIPS].head.copy()

    # Measure the REST shoulder height above Hips so the Active apex is a real
    # overhead extension rather than a guessed metre figure that could silently
    # demand more reach than the arm has.
    shoulder_head = arm.pose.bones[lib.ARM_CHAIN["R"][0]].head.copy()
    shoulder_height_above_hips_m = geom.to_m((shoulder_head - hips_base).dot(up))
    apex_arm_height_m = shoulder_height_above_hips_m + CONTEST_APEX_EXTENSION_ABOVE_SHOULDER_M
    lib.report("shoulder_height_above_hips_m", f"{shoulder_height_above_hips_m:.4f}")
    lib.report("contest_apex_arm_height_m", f"{apex_arm_height_m:.4f}")

    arm_height_idx = 2 + _CHANNEL_NAMES.index("arm_height_m")
    patched = 0
    for row in _KEYPOSES_RAW:
        if row[arm_height_idx] is _APEX_HEIGHT_PLACEHOLDER:
            row[arm_height_idx] = apex_arm_height_m
            patched += 1
    if patched != 2:
        raise SystemExit(
            f"FATAL: expected exactly 2 apex-height placeholder rows (the f10/f14 "
            f"hold), patched {patched}")

    humerus_u = {}
    ulna_u = {}
    for side in ("L", "R"):
        humerus_u[side], ulna_u[side] = lib.arm_lengths(arm, side)
    log(f"arm reach: L={geom.to_m(humerus_u['L'] + ulna_u['L']):.4f} m "
        f"R={geom.to_m(humerus_u['R'] + ulna_u['R']):.4f} m")

    keyposes = _keyposes_for_lib()

    worst_wrist_err = 0.0
    geom.reset_ankle_ik()
    worst_reach = (0.0, "", 0, 0.0)  # (ratio, side, frame, t_s)

    def apply(frame, _t_s, ch):
        nonlocal worst_wrist_err, worst_reach

        # ---- clavicles: pinned to REST, not inherited from the source --------
        # "Goalkeeper Catch Stationary.fbx" is a catch pose whose own
        # Shoulder(clavicle) bones carry uncontrolled idle sway across this frame
        # range. ARM_CHAIN deliberately excludes the clavicle from the two-link
        # solve, so nothing else here controls it -- left alone, the humerus ROOT
        # drifts frame to frame independently of our own authoring, which is the
        # "uncontrolled source motion" this library's method says to author over
        # rather than inherit. (author_layup.py hit this as reach-ratio blowouts.)
        for side in ("L", "R"):
            sh = arm.pose.bones[f"mixamorig:{'Left' if side == 'L' else 'Right'}Shoulder"]
            sh.matrix_basis = Matrix.Identity(4)
            bpy.context.view_layer.update()
            sh.keyframe_insert("rotation_quaternion", frame=frame)

        # ---- hips: VERTICAL delta off the fixed anchor ------------------------
        # There is deliberately no lateral or forward hip channel: "weight stays
        # centred between the feet" is the distinguishing feature of a contest's
        # wind-up (compare block's 0.20 m load), so it is made structurally
        # impossible to violate rather than left as a value someone could retune.
        pb = arm.pose.bones[lib.HIPS]
        mh = pb.matrix.copy()
        mh.translation = hips_base + up * geom.m(ch["hip_offset_m"])
        pb.matrix = mh
        bpy.context.view_layer.update()
        pb.keyframe_insert("location", frame=frame)
        hips_now = pb.head.copy()

        # ---- torso pitch about the LATERAL axis (a pitch, not a twist) -------
        pitch_rad = math.radians(TORSO_PITCH_SIGN * ch["torso_pitch_deg"])
        lib.rotate_bone_about_head(
            arm, lib.SPINE, (Matrix.Rotation(pitch_rad, 4, body_right),), frame=frame)

        # ---- legs: symmetric stance, both feet planted ON A FIXED FLOOR -------
        # The ankle height is anchored to `hips_base`, NOT to `hips_now`.
        #
        # author_layup.py builds its ankle targets off `hips_now`, which is right
        # for a JUMP: there the feet leave the ground WITH the hips, so the whole
        # body translates together. Inheriting that model here was a real bug --
        # it made the toes track every hip movement, so a 0.10 m crouch lifted
        # the feet 0.10 m and `verify_grounded` correctly reported the character
        # floating (measured: ground_band_m = 0.1000 at exactly the 0.10 tol).
        #
        # For a PLANTED move the floor is fixed and the hips move relative to it.
        # Anchoring to `hips_base` is both the physically correct model (the
        # knees flex, which is what a crouch IS) and the reason the tolerance
        # below can be tightened rather than widened -- verify_grounded's own
        # docstring warns against widening `tol_m` until it passes, and this is
        # the fix that makes widening unnecessary.
        toe_dir = (forward * 0.90 - up * 0.44).normalized()
        half_width_m = STANCE_HALF_WIDTH_M + ch["stance_lat_m"]
        for side, lat_sign in (("R", 1.0), ("L", -1.0)):
            ankle = (hips_base
                     + forward * geom.m(ch["foot_fore_m"])
                     + body_right * geom.m(lat_sign * half_width_m)
                     - up * geom.m(NEUTRAL_HIP_TO_ANKLE_M))
            lib.plant_foot(arm, side, ankle, toe_dir, geom, frame=frame)

        # ---- arms: ONE set of channels, mirrored per side ---------------------
        for side, lat_sign in (("R", 1.0), ("L", -1.0)):
            target = (hips_now
                      + forward * geom.m(ch["arm_fore_m"])
                      + body_right * geom.m(lat_sign * ch["arm_lat_m"])
                      + up * geom.m(ch["arm_height_m"]))
            hint = (up * ELBOW_HINT_UP + body_right * (lat_sign * ELBOW_HINT_LAT)).normalized()

            sh_head = arm.pose.bones[lib.ARM_CHAIN[side][0]].head.copy()
            reach_u = humerus_u[side] + ulna_u[side]
            ratio = (target - sh_head).length / reach_u
            if ratio > worst_reach[0]:
                worst_reach = (ratio, side, frame, _t_s)
            if ratio > 0.90:
                d = target - sh_head
                log(f"reach ratio {ratio:.4f} for {side} arm at frame {frame} "
                    f"(t={_t_s:.4f}s) span={geom.to_m(d.length):.4f}m "
                    f"fore={geom.to_m(d.dot(forward)):+.4f} "
                    f"lat={geom.to_m(d.dot(body_right)):+.4f} "
                    f"up={geom.to_m(d.dot(up)):+.4f} "
                    f"sh_lat={geom.to_m((sh_head - hips_now).dot(body_right)):+.4f}")
            if _MEASURE_ONLY:
                continue

            err_u = lib.aim_arm(arm, side, target, hint, geom, frame=frame)
            worst_wrist_err = max(worst_wrist_err, err_u)

    lib.bake_timeline(arm, keyposes, apply, F0, F1, FPS)

    bpy.ops.object.mode_set(mode="OBJECT")
    scene.frame_start, scene.frame_end = F0, F1

    lib.report_ankle_ik("worst_ankle_ik_err_m", geom)
    lib.report("worst_wrist_ik_err_m", f"{geom.to_m(worst_wrist_err):.6f}")
    lib.report("worst_reach_ratio",
               f"{worst_reach[0]:.4f} ({worst_reach[1]} arm, frame {worst_reach[2]})")

    if _MEASURE_ONLY:
        log("CONTEST_MEASURE_ONLY=1 -- arm solve skipped; NOT exporting.")
        return

    all_frames = list(range(F0, F1 + 1))
    lib.verify_all_bones_keyed(arm, expected_count=52)
    lib.verify_pose_unscaled(arm, all_frames)

    # ── grounded on ALL THREE segments, against ONE shared reference ──────────
    # The reference matters as much as the tolerance. verify_grounded defaults
    # `band_ref` to min(heights) OVER THE FRAMES IT IS GIVEN, so three
    # independent per-segment calls would each establish their own floor -- and a
    # segment that floated uniformly 0.3 m above the others would pass all three.
    # Measuring the floor once over the whole clip and passing it to each call
    # keeps the per-segment failure attribution while making the gate strictly
    # stronger than three separate checks.
    scene_toes = [lib.LEG_CHAIN["L"][3], lib.LEG_CHAIN["R"][3]]
    with lib.preserve_frame():
        lows = []
        for f in all_frames:
            scene.frame_set(f)
            lows.append(min(arm.pose.bones[t].head.dot(geom.up) for t in scene_toes))
    ground_ref = min(lows)
    lib.report("ground_ref_u", f"{ground_ref:.6f}")

    lib.verify_grounded(arm, list(range(F0, STARTUP_END + 1)),
                        GROUND_BAND_TOL_M, geom, band_ref=ground_ref)
    lib.verify_grounded(arm, list(range(STARTUP_END, ACTIVE_END + 1)),
                        GROUND_BAND_TOL_M, geom, band_ref=ground_ref)
    lib.verify_grounded(arm, list(range(ACTIVE_END, F1 + 1)),
                        GROUND_BAND_TOL_M, geom, band_ref=ground_ref)

    # The positive half of the read -- see the docstring's vacuity section.
    _verify_arms_rise_in_active(arm, geom)

    _torso_pitch_sign_is_forward(arm, geom, body_right, forward)

    lib.verify_pose_distinct(
        lib.snapshot_pose(arm, F0), lib.snapshot_pose(arm, F1),
        POSE_DISTINCT_MIN_DEG, label="startup_vs_recovery")

    lib.export_fbx(arm, dst, ACTION_NAME)
    log(f"wrote {dst}")


if __name__ == "__main__":
    main()
