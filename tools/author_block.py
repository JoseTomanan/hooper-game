"""Author `block` as a single-polarity keypose clip in headless Blender (#283).

Run:
    "$BLENDER" --background --python-exit-code 1 \
        --python tools/author_block.py -- <src.fbx> <out.fbx>

`--python-exit-code 1` is MANDATORY. Blender's `--background --python` exits 0
even on an uncaught traceback, so without it a crashed authoring run reports
success to the pipeline.

===============================================================================
THE ONE MOVE THAT MUST LEAVE THE GROUND
===============================================================================
This script is the deliberate INVERSE of `author_contest.py`, and the two should
be read together -- they author the same arms off the same source FBX and are
separated by exactly one thing:

    Block leaves the ground with both arms up. Contest raises both arms up and
    keeps the feet PLANTED. Same arms, different base. FEET ARE THE READ.

`ContestMove.cs`'s own doc comment prices a commitment ladder on that ordering --
contest 6 < steal 8 < block 10 startup ticks -- and block sits at the top of it.
Ten ticks is the LONGEST wind-up in the game, which per the issue is a design
statement rather than a number: block is the biggest commitment on defence, so it
gets the most exaggerated telegraph in the batch. Nothing here is subtle.

So `verify_grounded` runs on Startup and on the back of Recovery only, and the
Active segment gets `verify_airborne` INSTEAD -- never merely an omission. A
block that visually squats and un-squats without leaving the floor is
indistinguishable from a contest, the ladder collapses, and no other gate in this
library would notice: the phases still enter, the durations still check out, the
arms still go up.

===============================================================================
REPLACE THE PROOF, DO NOT DELETE IT -- AND MIND THE BASELINE
===============================================================================
`verify_airborne` is not vacuously satisfiable the way `verify_grounded` is, but
it does have a baseline, and the baseline is where the vacuity hides. Its own
docstring states the trap: comparing the airborne window's peak against that
window's own minimum proves nothing, because every sample in it is already
elevated. It therefore REQUIRES an explicit `ref_height`.

The reference passed below is the Hips height at frame 0 -- a frame independently
proven grounded by the Startup `verify_grounded` call. That pairing is what makes
the airborne claim mean "rose 0.30 m off the floor" rather than "rose 0.30 m off
wherever it already was".

The paired gates, then:

    verify_grounded(0..10)          the load: feet stay down through the wind-up.
                                    ALSO the control for the next line.
    verify_airborne(10..18, 0.20)   the leap: the hips genuinely left, measured
                                    against frame 0's proven-grounded height.
    verify_grounded(26..38)         the landing, from ~40% of Recovery onward.
    _verify_arms_rise_in_active     both wrists clear the head during Active and
                                    do NOT during Startup (verbatim
                                    author_contest.py -- block raises the same
                                    arms, and the MIN across the wrist pair is
                                    what makes "BOTH arms" the measured claim
                                    rather than an assumed one; README trap 17).
    verify_pose_distinct            Startup != Recovery (#296).

===============================================================================
UNHANDED -- ONE POLARITY, AND SYMMETRIC BY DESIGN
===============================================================================
Per the issue: handedness is **No** -- a block reads symmetrically (grill
decision, #276). Both arms go up together, authored from ONE set of channels
mirrored laterally (`+lat` right, `-lat` left).

README traps 4 (handedness swap timing) and 5 (hand-side predicate needs a
non-symmetric control) therefore do NOT apply here: there is no polarity to
mistime and no hand-side claim to control for. This is one of the few places in
the batch where a symmetric pose is CORRECT rather than a red flag.

===============================================================================
WHAT THIS PRODUCES
===============================================================================
ONE Blender action, baked at 60 Hz, frame numbers ARE physics ticks:

    frames     seconds              segment
    0  -> 10   0.00000 -> 0.16667   Startup   (10 ticks -- the longest in the game)
    10 -> 18   0.16667 -> 0.30000   Active    ( 8 ticks -- airborne)
    18 -> 38   0.30000 -> 0.63333   Recovery  (20 ticks -- the landing/punish)

===============================================================================
THE FEET GET THEIR OWN VERTICAL CHANNEL, AND THAT IS THE STRUCTURAL POINT
===============================================================================
`author_contest.py` anchors both ankles to `hips_base` (a FIXED floor -- the hips
move relative to the feet, which is what a crouch IS). `author_layup.py` anchors
them to `hips_now` (the feet leave the ground WITH the hips, which is what a jump
is). Block is BOTH, in sequence, and picking either model wholesale would author
the wrong move for two thirds of the clip.

So the ankle height is neither -- it is `hips_base + up*(foot_rise_m - NEUTRAL)`,
where `foot_rise_m` is an explicit authored channel. The feet have a trajectory of
their own:

    frames  0..10   foot_rise = 0.00     planted (verify_grounded's window)
    frame   14      foot_rise = 0.34     airborne
    frames 26..38   foot_rise = 0.00     landed (verify_grounded's window)

Two consequences worth stating, because both are load-bearing:

1. The vertical hip-to-ankle distance becomes `hip_offset - foot_rise + 0.62`,
   i.e. the KNEE FLEXION is a derived quantity rather than a channel. Reading it
   off the table is how the silhouette was checked: 0.42 m at the deepest load
   (f10), 0.58 m mildly tucked at the apex (f14), 0.48 m absorbing on landing
   (f26), against a 0.827 m leg reach.
2. The grounded windows are grounded BY CONSTRUCTION, not by tuning. `foot_rise`
   is exactly 0.0 across every frame either `verify_grounded` call inspects, so
   the measured excursion is the foot IK solver's own residual and nothing else.
   That is why the tolerance below can be contest's tight 0.02 m rather than
   layup's 0.18 m -- `verify_grounded`'s docstring warns against widening `tol_m`
   until it passes, and this is the authoring choice that makes widening
   unnecessary.

Frame 10 and frame 18 are SLICE BOUNDARIES -- each is simultaneously the last
frame of one clip and the first of the next -- so their values decide what BOTH
clips read as at the cut, and neither is a free parameter:

  f10  feet still planted at the deepest crouch. The leap belongs to Active; a
       player already airborne on Startup's last frame has no visible load, which
       is the un-telegraphed commitment ADR-0003 names as the primary anti-goal.
  f18  still airborne (hips +0.24, feet +0.26). The descent belongs to Recovery,
       which has twenty ticks to spend on it. This is layup's f12 lesson applied
       verbatim (measured there: hips had returned to +0.05 by the boundary, i.e.
       grounded at release).

===============================================================================
WHAT IS DELIBERATELY NOT AUTHORED: THE HEEL LIFT
===============================================================================
The issue's Startup spec says "heels lift". It is not authored, and that is a
choice rather than an oversight.

`plant_foot` places the ANKLE and aims the foot bone along a fixed `toe_dir`, so
the ToeBase head is `ankle + foot_length * toe_dir`. A heel lift means raising the
ankle while the toe stays put, which requires solving `toe_dir` against the ankle
rise as a coupled constraint. That coupling would put a derived quantity inside
the one measurement `verify_grounded` reads, on the single clip in this batch
whose entire legibility claim is vertical displacement of the feet.

The read does not need it: the issue itself says to author the vertical
displacement as the primary read and everything else as secondary, and a 0.20 m
hip drop over ten ticks is not a subtle tell. Trading a cosmetic detail for a
gate that measures exactly what it says is the right side of that trade at the
"legible, not pretty" bar (#276/#302).

===============================================================================
LATERAL SIGN CONVENTION
===============================================================================
Same as `author_contest.py` / `author_layup.py`: on this rig `geom.right` points
at the character's LEFT, so every lateral offset goes through
`BODY_RIGHT = -geom.right`. Since this clip is symmetric there is no polarity
indirection at all -- the right arm takes `+lat`, the left `-lat`.

===============================================================================
COSMETIC-ONLY (the dense surface this move sits on)
===============================================================================
This script is an ASSET BUILD TOOL. It does not import, read, or feed
`DefensiveResolution.Succeeds`, the #214 block reach gate,
`BlockMove.DefaultBlockGraceTicks`, or any ADR-0018 timing window. The clip
VISUALISES the block window; it never defines it.
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
FPS = 60
STARTUP_TICKS = 10
ACTIVE_TICKS = 8
RECOVERY_TICKS = 20
TOTAL_TICKS = STARTUP_TICKS + ACTIVE_TICKS + RECOVERY_TICKS  # 38

F0 = 0
F1 = TOTAL_TICKS  # inclusive -- frame numbers ARE physics ticks

# Segment boundaries as frame numbers, named once so the gates below and the
# rebuild script's slice windows cannot drift apart silently.
STARTUP_END = STARTUP_TICKS                    # 10
ACTIVE_END = STARTUP_TICKS + ACTIVE_TICKS      # 18

# Recovery is grounded from ~40% of the segment onward (the issue's own wording),
# i.e. frame 18 + 8 = 26. The eight ticks before it are the descent and the
# absorb, which are legitimately still in the air or arriving.
RECOVERY_GROUNDED_START = ACTIVE_END + int(RECOVERY_TICKS * 0.4)  # 26

ACTION_NAME = "block"

# ── stance geometry, metre-denominated ────────────────────────────────────────
# Reused verbatim from author_contest.py / author_layup.py's measurement on this
# SAME rig (Y Bot: femur/tibia/foot are rig-intrinsic, independent of the source
# clip).
NEUTRAL_HIP_TO_ANKLE_M = 0.62
STANCE_HALF_WIDTH_M = 0.12

# Torso pitch sign. Both a defensive load and a landing absorb pitch FORWARD.
#
# MEASURED by `_torso_pitch_sign_is_forward` below rather than guessed --
# author_contest.py's initial +1.0 guess was wrong on this same rig and axis (a
# +1.0 rotation about `body_right` moves the spine->head vector BACKWARD). Same
# rig, same axis, same intent as contest, hence the same -1.0.
TORSO_PITCH_SIGN = -1.0

# Overhead extension above the (measured) rest shoulder height for the Active
# apex hand target. `aim_arm` treats over-reach as FATAL, so this is sized
# against the rig's MEASURED budget: the shoulder sits ~0.37 m above the hips at
# rest but ~0.48 m above `hips_now` once the source's own spine pose is applied,
# and the arm reaches 0.5502 m. 0.50 puts the wrist ~0.39 m above the actual
# shoulder for ~0.86 of the reach budget -- straighter and higher than contest's
# 0.45 (a block extends; a contest merely raises), with margin left.
BLOCK_APEX_EXTENSION_ABOVE_SHOULDER_M = 0.50

# ── keypose channel table ─────────────────────────────────────────────────────
# Columns:
#   time_s, label,
#   hip_offset_m     (+up, delta from neutral -- VERTICAL ONLY; there is no
#                     lateral or forward hip channel, so the weight cannot drift
#                     sideways even by accident)
#   foot_rise_m      (+up, BOTH ankles, off the fixed floor -- see the docstring;
#                     this is the channel the whole legibility read rests on)
#   torso_pitch_deg  (magnitude; TORSO_PITCH_SIGN supplies the sign)
#   stance_lat_m     (extra half-width per foot beyond STANCE_HALF_WIDTH_M)
#   foot_fore_m      (both feet together)
#   arm_fore_m, arm_lat_m, arm_height_m   (BOTH arms; lat mirrored per side,
#                                          height for the apex rows is patched
#                                          in main() from the measured shoulder)
_APEX_HEIGHT_PLACEHOLDER = None  # patched onto the two "active" apex rows in main()

# REACH BUDGET -- the arms never hang at the thighs, and that is a constraint
# rather than a style choice. author_contest.py measured it on this exact rig and
# source: the shoulder sits ~0.48 m above `hips_now` (not the 0.3717 m REST
# figure -- the source's own spine pose raises the girdle) and ~0.109 m BEHIND
# the hips along `forward`. An arm reaching 0.5502 m therefore cannot put a wrist
# much below hip height at any real lateral offset, and `aim_arm` treats
# over-reach as FATAL by design -- a clamped arm locks straight and reads as a
# mannequin.
#
# So the issue's "both arms swing down and back past the hips" is authored as far
# down and as far BACK as the budget allows (`arm_fore_m` going negative is the
# "back" half and costs almost nothing, because the shoulder is already behind
# the hips) rather than as a literal below-the-hip target that would abort the
# run. The wind-up still reads: the contrast that carries it is arms-low-and-back
# at f10 against arms-fully-overhead at f14, which is the largest arm excursion
# in the batch.
_KEYPOSES_RAW = [
    # t_s,               label,      hip_off, foot_rise, pitch, stance_lat, foot_fore, arm_fore, arm_lat, arm_h
    # Frame 0 -- the upright READY stance the block is thrown from.
    [0.00000,            "startup",   0.00,    0.00,      4.0,   0.02,       0.00,      0.10,     0.24,    0.20],
    [5 / FPS,            "startup",  -0.12,    0.00,      10.0,  0.04,       0.00,     -0.04,     0.26,    0.14],
    # Frame 10 -- the Startup/Active SLICE BOUNDARY and the deepest crouch in the
    # move set (0.20 m, well past contest's 0.05 m load). Feet still planted: the
    # leap belongs to Active. Arms are at their lowest and furthest back here,
    # which is the wind-up's whole tell.
    [STARTUP_END / FPS,  "active",   -0.20,    0.00,      12.0,  0.04,       0.00,     -0.10,     0.26,    0.10],
    # Frame 14 -- the apex. Hips 0.30 m ABOVE neutral (a 0.50 m swing off f10 in
    # four ticks), feet 0.34 m off the floor, legs mildly tucked (hip->ankle
    # 0.58 m), torso vertical, both arms fully extended overhead.
    [14 / FPS,           "active",    0.30,    0.34,      0.0,  -0.02,       0.06,      0.06,     0.18,    _APEX_HEIGHT_PLACEHOLDER],
    # Frame 18 -- the Active/Recovery SLICE BOUNDARY. STILL AIRBORNE, on purpose
    # (see the docstring): the descent is Recovery's job.
    [ACTIVE_END / FPS,   "recovery",  0.24,    0.26,      2.0,   0.00,       0.04,      0.08,     0.20,    _APEX_HEIGHT_PLACEHOLDER],
    [22 / FPS,           "recovery",  0.02,    0.08,      4.0,   0.06,       0.02,      0.12,     0.24,    0.30],
    # Frame 26 -- landed, and the absorb trough. Feet are down (foot_rise 0.00,
    # where verify_grounded's Recovery window begins) and WIDER than neutral,
    # hips 0.14 m BELOW neutral as the knees take the load. A blocker who guessed
    # wrong is on the floor and the offence can see it.
    [RECOVERY_GROUNDED_START / FPS,
                         "recovery", -0.14,    0.00,      8.0,   0.10,       0.00,      0.12,     0.28,    0.16],
    # Frame 38 -- held low, wide and arms-down through the back half. Deliberately
    # NOT frame 0's stance: that identity is the #296 defect, and
    # verify_pose_distinct below makes it impossible rather than something a
    # reviewer has to catch by eye.
    [TOTAL_TICKS / FPS,  "recovery", -0.08,    0.00,      7.0,   0.08,       0.00,      0.08,     0.26,    0.12],
]

_CHANNEL_NAMES = (
    "hip_offset_m", "foot_rise_m", "torso_pitch_deg", "stance_lat_m",
    "foot_fore_m", "arm_fore_m", "arm_lat_m", "arm_height_m",
)

# Elbow bend-plane hints, mirrored per side (up + outward). One fixed hint per arm
# across the whole timeline, same pattern as author_contest.py: it only has to
# avoid being exactly parallel to the reach direction, which a shallow
# up-and-outward hint safely is at every keypose here.
ELBOW_HINT_UP = 0.3
ELBOW_HINT_LAT = 0.6

# ── proof thresholds ──────────────────────────────────────────────────────────
# Support-level band for the two GROUNDED windows, against ONE shared ground
# reference (see `main()` -- three per-segment calls would each establish their
# own floor, so a window that floated uniformly would pass).
#
# contest's tight 0.02 m rather than layup's 0.18 m, and the docstring's
# "structural point" section is why: `foot_rise_m` is exactly 0.0 across every
# frame either call inspects, so the only residual is the foot IK solver's own
# error. Widening this would be widening `tol_m` until it passes, which
# verify_grounded's docstring warns against by name.
GROUND_BAND_TOL_M = 0.02

# The load-bearing gate. 0.20 m is the issue's own floor; the table authors 0.30,
# i.e. the middle of the issue's 0.25-0.35 m band, so there is 50% headroom. The
# gap is deliberate -- if the authored rise ever has to be trimmed to within a
# centimetre of this floor, the move has stopped being a jump and the right
# response is to fix the table, not the threshold.
MIN_HIP_RISE_M = 0.20

# Startup(f0)-vs-Recovery(f38) legibility floor (#296). Matches every other
# script's 15.0 deg floor.
POSE_DISTINCT_MIN_DEG = 15.0

# How far ABOVE THE HEAD each wrist must sit at the Active apex. Verbatim
# author_contest.py: block raises the same arms, so it is held to the same bar.
WRIST_ABOVE_HEAD_MIN_M = 0.10
# ... and the same measurement must NOT be satisfied during Startup, or "the arms
# rose during Active" is true of a clip holding them overhead throughout.
WRIST_ABOVE_HEAD_STARTUP_MAX_M = 0.0

# Diagnostic escape hatch: skip the arm solve so a single run can report the
# reach ratio at EVERY frame instead of dying at the first over-reach. Never set
# for a real authoring run -- the exported FBX would have no arm keys.
_MEASURE_ONLY = os.environ.get("BLOCK_MEASURE_ONLY") == "1"


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

    Verbatim author_contest.py, including its reason for being isolated: the
    obvious oracle (compare the head's position at an upright frame against a
    pitched frame) is INVALID on this source. "Goalkeeper Catch Stationary.fbx"
    is a catch, and this script authors the arms and legs by IK but composes only
    a pitch onto the spine, leaving the source's own neck/head animation in place
    -- so any two-frame comparison is dominated by source drift rather than by
    the channel under test (contest measured -0.4816 m, an order of magnitude
    more than a 6 deg pitch of a ~0.6 m spine can produce).

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
            f"BACKWARD. A defensive load and a landing absorb both pitch IN, not "
            f"away. Flip TORSO_PITCH_SIGN.")


def _wrist_above_head_m(arm, frame, geom):
    """Lower of the two wrists, relative to the Head bone, at `frame` (metres).

    The LOWER of the two on purpose (README trap 17): this clip is symmetric and
    the claim is "BOTH arms went up". Taking the higher wrist would let a clip
    that raised one arm and left the other down satisfy an "arms up" gate -- and
    a one-armed overhead pose is a steal or a one-handed swat silhouette, not the
    two-handed block this authors. On a symmetric clip min == max, which is
    exactly why the wrong reduction is invisible in a green run.
    """
    with lib.preserve_frame():
        bpy.context.scene.frame_set(frame)
        head_y = arm.pose.bones["mixamorig:Head"].head.dot(geom.up)
        wrists = [arm.pose.bones[lib.ARM_CHAIN[s][2]].head.dot(geom.up) for s in ("L", "R")]
    return geom.to_m(min(wrists) - head_y)


def _verify_arms_rise_in_active(arm, geom):
    """Both wrists go above the head during Active, and do NOT during Startup.

    Block's read is vertical displacement FIRST (that is `verify_airborne`'s job)
    but a leap with the arms at the sides is a rebound box-out, not a block. This
    is the second half of the silhouette, and its Startup ceiling is what makes
    the overhead extension a readable EVENT rather than a pose the clip holds.
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
            f"never went up, so this is a leap with the hands down, not a block.")
    if best_startup > WRIST_ABOVE_HEAD_STARTUP_MAX_M:
        raise SystemExit(
            f"FATAL: the arms were already {best_startup:.4f} m above the head "
            f"during Startup (ceiling {WRIST_ABOVE_HEAD_STARTUP_MAX_M} m). The "
            f"overhead extension has to be an EVENT the opponent can read, not a "
            f"pose the clip holds throughout -- otherwise the longest wind-up in "
            f"the game telegraphs nothing (ADR-0003).")


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    src, dst = argv[0], argv[1]

    arm, _src_f0, _src_f1 = lib.load_source(src, FPS)
    scene = bpy.context.scene

    geom = lib.RigGeometry(arm)
    geom.log_summary()
    body_right = -geom.right  # see module docstring: geom.right points LEFT.
    up, forward = geom.up, geom.forward
    lib.report("body_right", tuple(round(v, 4) for v in body_right))

    lib.enter_pose_mode(arm)

    # Anchor Hips position, captured ONCE before any of this script's own posing.
    # Every frame's Hips target AND every frame's ankle target is built from this,
    # so the move authors its own trajectory rather than inheriting the source's
    # root motion -- and so the FLOOR is a fixed thing the feet depart from and
    # return to, which is what makes `foot_rise_m` mean what it says.
    hips_base = arm.pose.bones[lib.HIPS].head.copy()

    # Measure the REST shoulder height above Hips so the Active apex is a real
    # overhead extension rather than a guessed metre figure that could silently
    # demand more reach than the arm has.
    shoulder_head = arm.pose.bones[lib.ARM_CHAIN["R"][0]].head.copy()
    shoulder_height_above_hips_m = geom.to_m((shoulder_head - hips_base).dot(up))
    apex_arm_height_m = shoulder_height_above_hips_m + BLOCK_APEX_EXTENSION_ABOVE_SHOULDER_M
    lib.report("shoulder_height_above_hips_m", f"{shoulder_height_above_hips_m:.4f}")
    lib.report("block_apex_arm_height_m", f"{apex_arm_height_m:.4f}")

    arm_height_idx = 2 + _CHANNEL_NAMES.index("arm_height_m")
    patched = 0
    for row in _KEYPOSES_RAW:
        if row[arm_height_idx] is _APEX_HEIGHT_PLACEHOLDER:
            row[arm_height_idx] = apex_arm_height_m
            patched += 1
    if patched != 2:
        raise SystemExit(
            f"FATAL: expected exactly 2 apex-height placeholder rows (the f14/f18 "
            f"hold), patched {patched}")

    humerus_u = {}
    ulna_u = {}
    for side in ("L", "R"):
        humerus_u[side], ulna_u[side] = lib.arm_lengths(arm, side)
    log(f"arm reach: L={geom.to_m(humerus_u['L'] + ulna_u['L']):.4f} m "
        f"R={geom.to_m(humerus_u['R'] + ulna_u['R']):.4f} m")

    keyposes = _keyposes_for_lib()

    worst_wrist_err = 0.0
    worst_ankle_err = 0.0
    worst_reach = (0.0, "", 0, 0.0)  # (ratio, side, frame, t_s)
    worst_leg_span = (0.0, "", 0)    # (hip->ankle metres, side, frame)

    def apply(frame, _t_s, ch):
        nonlocal worst_wrist_err, worst_ankle_err, worst_reach, worst_leg_span

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

        # ---- legs: symmetric stance on a floor the feet DEPART from ----------
        # The ankle height is anchored to `hips_base` (the fixed floor) PLUS the
        # authored `foot_rise_m`. See the docstring: contest anchors to
        # `hips_base` alone (planted) and layup to `hips_now` (the feet leave with
        # the hips), and block is both in sequence -- so the departure is an
        # explicit channel rather than a consequence of which anchor was picked.
        toe_dir = (forward * 0.90 - up * 0.44).normalized()
        half_width_m = STANCE_HALF_WIDTH_M + ch["stance_lat_m"]
        for side, lat_sign in (("R", 1.0), ("L", -1.0)):
            ankle = (hips_base
                     + forward * geom.m(ch["foot_fore_m"])
                     + body_right * geom.m(lat_sign * half_width_m)
                     + up * geom.m(ch["foot_rise_m"] - NEUTRAL_HIP_TO_ANKLE_M))
            span_m = geom.to_m((ankle - arm.pose.bones[lib.LEG_CHAIN[side][0]].head).length)
            if span_m > worst_leg_span[0]:
                worst_leg_span = (span_m, side, frame)
            _solved, err = lib.plant_foot(arm, side, ankle, toe_dir, geom, frame=frame)
            worst_ankle_err = max(worst_ankle_err, err)

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

    lib.report("worst_ankle_ik_err_m", f"{geom.to_m(worst_ankle_err):.6f}")
    lib.report("worst_wrist_ik_err_m", f"{geom.to_m(worst_wrist_err):.6f}")
    lib.report("worst_reach_ratio",
               f"{worst_reach[0]:.4f} ({worst_reach[1]} arm, frame {worst_reach[2]})")
    # The leg equivalent, reported for the same reason: the hip->ankle span is a
    # DERIVED quantity here (hip_offset - foot_rise + NEUTRAL), so a table edit
    # can push it past the 0.827 m leg reach without anyone noticing until
    # solve_two_link silently clamps and the legs lock straight.
    lib.report("worst_hip_to_ankle_m",
               f"{worst_leg_span[0]:.4f} ({worst_leg_span[1]} leg, frame {worst_leg_span[2]}, "
               f"reach {geom.to_m(geom.leg_reach):.4f})")

    if _MEASURE_ONLY:
        log("BLOCK_MEASURE_ONLY=1 -- arm solve skipped; NOT exporting.")
        return

    lib.enter_pose_mode(arm)
    all_frames = list(range(F0, F1 + 1))
    lib.verify_all_bones_keyed(arm, expected_count=52)
    lib.verify_pose_unscaled(arm, all_frames)

    # ── grounded on the two PLANTED windows, against ONE shared reference ─────
    # The reference matters as much as the tolerance. verify_grounded defaults
    # `band_ref` to min(heights) OVER THE FRAMES IT IS GIVEN, so two independent
    # per-window calls would each establish their own floor -- and a Recovery that
    # landed uniformly 0.30 m above where Startup stood would pass both. Measuring
    # the floor once across the WHOLE clip and passing it to each call keeps the
    # per-window failure attribution while making the gate strictly stronger.
    #
    # Measuring across the whole clip (airborne frames included) is safe here
    # precisely because the minimum is what is taken: the airborne frames are all
    # HIGHER, so they cannot lower the floor, and they are excluded from the
    # windows either call actually inspects.
    scene_toes = [lib.LEG_CHAIN["L"][3], lib.LEG_CHAIN["R"][3]]
    with lib.preserve_frame():
        lows = []
        for f in all_frames:
            scene.frame_set(f)
            lows.append(min(arm.pose.bones[t].head.dot(geom.up) for t in scene_toes))
    ground_ref = min(lows)
    lib.report("ground_ref_u", f"{ground_ref:.6f}")

    # Startup: the load. Feet stay down through the whole wind-up. This is ALSO
    # the control that makes the airborne gate below non-vacuous -- it is what
    # establishes that frame 0's Hips height is a genuine ground reference.
    lib.verify_grounded(arm, list(range(F0, STARTUP_END + 1)),
                        GROUND_BAND_TOL_M, geom, band_ref=ground_ref)

    # Active: the leap HAPPENED. ref_height is the Hips height at frame 0 -- a
    # frame the call above independently proves grounded, never a value derived
    # from the airborne window itself (see verify_airborne's docstring for why
    # that would compare the peak against itself and pass vacuously).
    lib.verify_airborne(arm, list(range(STARTUP_END, ACTIVE_END + 1)),
                        MIN_HIP_RISE_M, geom, ref_height=hips_base.dot(up))

    # Recovery from ~40% of the segment onward: landed, and staying landed.
    lib.verify_grounded(arm, list(range(RECOVERY_GROUNDED_START, F1 + 1)),
                        GROUND_BAND_TOL_M, geom, band_ref=ground_ref)

    # The second half of the silhouette -- see the helper.
    _verify_arms_rise_in_active(arm, geom)

    _torso_pitch_sign_is_forward(arm, geom, body_right, forward)

    lib.verify_pose_distinct(
        lib.snapshot_pose(arm, F0), lib.snapshot_pose(arm, F1),
        POSE_DISTINCT_MIN_DEG, label="startup_vs_recovery")

    lib.export_fbx(arm, dst, ACTION_NAME)
    log(f"wrote {dst}")
    print("AUTHOR_OK")


if __name__ == "__main__":
    main()
