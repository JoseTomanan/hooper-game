"""Author `layup` as a single-polarity keypose clip in headless Blender (#313).

Run:
    "$BLENDER" --background --python-exit-code 1 \
        --python tools/author_layup.py -- <src.fbx> <out.fbx>

`--python-exit-code 1` is MANDATORY. Blender's `--background --python` exits 0
even on an uncaught traceback, so without it a crashed authoring run reports
success to the pipeline.

===============================================================================
UNHANDED -- ONE POLARITY, ONE TIMELINE (fixed, not an omission)
===============================================================================
Unlike the dribble-move family (crossover, behind-the-back, steal, ...), a
layup does not swap hands with the ball's own hand-side: it is authored once,
with a hard-coded finishing side. This script fixes:

    FINISH_ARM_SIDE = "R"   -- the finishing/release arm (goes overhead)
    DRIVE_KNEE_SIDE  = "L"  -- the leg that drives the knee up for lift

This is the standard right-handed layup shape (drive off the left foot, finish
with the right hand) and is fixed by spec, not derived -- there is no second
polarity to author or prove distinct from this one.

===============================================================================
WHAT THIS PRODUCES
===============================================================================
ONE Blender action, baked at 60 Hz, frame numbers ARE physics ticks (so a
reader can cross-reference this file against the issue's frame table
directly):

    frames    seconds              segment
    0  -> 8   0.00000 -> 0.13333   Startup   (8 ticks -- the last plant)
    8  -> 12  0.13333 -> 0.20000   Active    (4 ticks -- airborne, the release)
    12 -> 26  0.20000 -> 0.43333   Recovery  (14 ticks -- the landing/punish)

===============================================================================
MOTION, PHASE BY PHASE
===============================================================================
Startup (0->8): the last plant converts horizontal speed into vertical. The
drive knee (LEFT) comes up (thigh toward horizontal) while the plant foot
(RIGHT) stays down and loads, then extends. The torso pitches ~10 degrees BACK
from vertical (rotate SPINE about the lateral axis, `body_right`) -- this
backward pitch is the speed-to-height conversion and is what makes this read
as a layup rather than a vertical jump; it is never omitted and never made a
forward lean. The ball comes up to the RIGHT shoulder, LEFT hand supporting.
Hips stay at neutral height through Startup -- `verify_grounded` must pass
here.

Active (8->12): airborne, close to a single pose -- apex plus a hint of arm
follow-through. Hips rise +0.30 m above neutral (via a `drop_hips`-style
vertical delta). The drive knee stays high. The finishing (RIGHT) arm goes
fully extended overhead, wrist relaxed over the ball. The off (LEFT) arm
comes across the body for protection.

Recovery (12->26): land on both feet. Hips absorb to roughly -0.12 m below
neutral at the deepest point (frame ~18, 40% of the segment), then settle --
ending grounded and low, NOT re-set all the way back to neutral, because
fourteen ticks of recovery is a real punish window and has to read as one.
Arms come down, feet land slightly wider than neutral.

===============================================================================
LATERAL SIGN CONVENTION -- DIRECT, NOT reach_sign-RELATIVE
===============================================================================
The multi-polarity scripts (steal, behind-the-back) author lateral channels
"own-side positive" and multiply by a per-polarity `reach_sign`/`ball_sign`
because they must place the SAME table on either side depending on which hand
started with the ball. This clip has exactly one polarity, so that
indirection buys nothing here. Every lateral channel below is authored
DIRECTLY SIGNED along `geom.body_right` (positive = the character's actual
anatomical right, derived from the shoulder span and cross-checked against the
hip span in `blender_anim_lib.derive_body_right`; `geom.lateral` is a basis
vector only -- on this rig it points at the character's LEFT and must NOT be
used for hand/foot placement; there is no longer a `geom.right`, the local
`-geom.right` workaround is gone, replaced by the shared accessor, #320) and
applied as `body_right * value` with no extra sign multiplication in `apply`.

===============================================================================
TORSO PITCH -- ROTATION ABOUT THE LATERAL AXIS, NOT `up`
===============================================================================
Steal/behind-the-back twist the spine about `geom.up` (a turn). A layup's
backward lean is a PITCH -- rotation about the lateral axis (`body_right`),
which tilts the sagittal plane. `TORSO_PITCH_SIGN` below is the authored guess
for which signed rotation about `body_right` tilts the torso BACKWARD (i.e.
the head/spine-tip's `forward`-axis coordinate, relative to Hips, decreases);
`_torso_pitches_backward` measures it numerically and raises if the guess was
wrong, exactly as `author_behindtheback.py`'s own torso-twist sign was not
trusted by eye (rotation handedness about a derived axis is not reliably
guessable).

===============================================================================
WHY THE ARM/HAND HEIGHTS ARE DERIVED, NOT HARDCODED
===============================================================================
An overhead finish has to clear the shoulder by a real margin while staying
inside `aim_arm`'s reach budget (~0.55 m shoulder-to-wrist on this rig, and
`on_overreach="fail"` for arms -- see `blender_anim_lib.aim_arm`). Guessing a
hips-relative height risks demanding more reach than the arm has, the same
trap `author_behindtheback.py`'s own authoring log describes. So this script
measures the REST shoulder height above Hips once, in `main()`, and builds the
finishing arm's Active-apex target as that shoulder height plus a modest
extension -- never a bare hardcoded metre figure.

===============================================================================
THE MACHINERY LIVES IN blender_anim_lib (#315)
===============================================================================
Rig geometry, IK, posing primitives, the keypose timeline, and the proof
helpers are all imported from `tools/blender_anim_lib.py`. This file is only
the spec: the keypose channel table and the move-specific proofs (grounded,
airborne, pose-distinct, and the non-symmetric finishing-hand-height check).
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

# ── fixed hand-side convention (unhanded clip -- see module docstring) ───────
FINISH_ARM_SIDE = "R"   # the arm that releases the ball, overhead
DRIVE_KNEE_SIDE = "L"   # the leg that drives the knee up for lift
PLANT_FOOT_SIDE = "R"   # the leg that loads/extends off the floor in Startup

# ── clip contract, 60 Hz ──────────────────────────────────────────────────────
FPS = 60
STARTUP_TICKS = 8
ACTIVE_TICKS = 4
RECOVERY_TICKS = 14
TOTAL_TICKS = STARTUP_TICKS + ACTIVE_TICKS + RECOVERY_TICKS  # 26

F0 = 0
F1 = TOTAL_TICKS  # inclusive -- frame numbers ARE physics ticks

ACTION_NAME = "layup"

# ── stance geometry, metre-denominated ────────────────────────────────────────
# Reused verbatim from author_behindtheback.py's own measurement on this SAME
# rig (Y Bot: femur/tibia/foot are rig-intrinsic, independent of source clip).
NEUTRAL_HIP_TO_ANKLE_M = 0.62
STANCE_HALF_WIDTH_M = 0.12

# Torso pitch sign: positive TORSO_PITCH_SIGN * angle is the AUTHORED GUESS for
# "rotate backward". `_torso_pitches_backward` measures this numerically below
# and raises if the guess was wrong -- see module docstring.
TORSO_PITCH_SIGN = 1.0

# Overhead extension above the (measured) rest shoulder height, hips-relative,
# for the Active-apex finishing-hand target. Modest on purpose: `aim_arm`
# treats over-reach as FATAL for arms, so this is well inside the ~0.55 m
# shoulder-to-wrist budget once the shoulder's own rest height is added back
# in `main()`. See module docstring "WHY THE ARM/HEIGHTS ARE DERIVED".
FINISH_APEX_EXTENSION_ABOVE_SHOULDER_M = 0.40

# ── keypose channel table ─────────────────────────────────────────────────────
# All lateral channels (`*_lat_m`) are DIRECTLY SIGNED along `body_right`
# (positive = anatomical right) -- see module docstring. `right_height_m` /
# `left_height_m` are hips-relative; the Active-apex row for `right_height_m`
# is filled in dynamically in `main()` once the rest shoulder height is
# measured (see `_finish_apex_right_height_m`), NOT hardcoded here.
#
# Columns:
#   time_s, label,
#   hip_offset_m        (+up, delta from neutral)
#   torso_pitch_deg     (magnitude; TORSO_PITCH_SIGN supplies the sign)
#   drive_rise_m        (drive-ankle vertical rise above the grounded position)
#   drive_fore_m, drive_lat_m
#   plant_fore_m, plant_lat_m
#   right_fore_m, right_lat_m, right_height_m  (finishing arm; height for the
#                                               apex row is patched in main())
#   left_fore_m,  left_lat_m,  left_height_m   (off arm)
_APEX_HEIGHT_PLACEHOLDER = None  # patched onto the "active" apex row in main()

# Patched onto BOTH "active" rows' `drive_lat_m` in main(), with the drive hip's
# own measured lateral offset. A distinct sentinel object rather than a second
# `None`, so neither patch loop can ever claim the other's cell.
#
# WHY THIS IS DERIVED AND NOT A NUMBER (#335)
# ═══════════════════════════════════════════
# `drive_lat_m` is measured from the HIPS CENTRE, but the femur hangs off a hip
# that is itself 0.09 m to the side. So authoring `0.00` here does NOT put the
# drive ankle under the drive hip -- it puts it 0.09 m lateral of it, and at the
# apex this leg is folded to ~0.31 of full reach, where that offset is anything
# but cosmetic.
#
# `plant_foot` controls the ANKLE. Nothing controls the KNEE: it is emergent
# from the bend plane, whose normal is `dir_ankle.cross(geom.forward)`. A
# deeply-folded leg has a short, steeply-inclined `dir_ankle`, so a small
# lateral component tilts that plane hard and the femur sweeps a big arc.
# Measured by `tools/measure_layup_knee.py` at frame 10, knee position relative
# to the drive hip along `body_right`:
#
#     drive_lat_m   knee_up   knee_lat
#       -0.120       +0.305     +0.178
#       -0.100       +0.350     +0.082
#       -0.050       +0.296     -0.189
#        0.000       +0.175     -0.264     <- what this table used to say
#
# 5 cm of ankle swings the knee 27 cm, and the zero crossing sits at -0.092 --
# the drive hip's own offset, to within the sweep's resolution. Hence: derive
# it, do not tune it.
#
# #335 ASKS WHETHER THIS CELL WAS EYE-TUNED AGAINST THE OLD SOLVER. IT WAS.
# Three independent lines, because the column pattern alone only shows the cell
# is anomalous -- it cannot show how it got that way:
#
#   1. Shape. Read `drive_lat_m` down the column: -0.12, -0.05, 0.00, 0.00,
#      -0.10, -0.14, -0.17, -0.15. Every row tracks the hip's -0.09 offset
#      except the two apex rows, which spike to zero.
#   2. Chronology. The table landed in 48b6890 (2026-07-30) and this column was
#      NOT touched by df09950 (2026-08-02, the only later layup fix). The exact
#      solve is 95012c9 (2026-08-04). So these numbers were chosen while the
#      solver was still inexact -- a test the hypothesis could have failed, and
#      did not.
#   3. Method. 48b6890's own commit body specifies this pose qualitatively --
#      "reads as a layup rather than a jump", "the silhouette already reads
#      unmistakably" -- and gives no derivation for any lateral channel. The
#      numbers were nudged until the render looked right.
#
# THE TWO ANSWERS #335 OFFERS ARE NOT RIVALS; both hold. #321 is a correct fix,
# AND this cell was tuned against the defect it fixed. Before #321 the leg IK
# silently swallowed lateral targets (see `plant_foot`'s docstring for the
# shortfall formula), so an apex authored at 0.00 rendered fine -- the original
# author never saw a splayed knee and never chose one. #321 made the solve exact
# and the swallowed 0.09 m surfaced as a 26 cm knee splay across the whole Active
# window, plus a 25.6 cm one-frame snap back at frame 11.
#
# That is why the repair is a DERIVED cell and not a re-eyeballed one: the pose
# the author intended is recoverable by putting the ankle under its own hip,
# which is what they believed 0.00 already did. Regenerating blind would ship the
# splay; re-tuning by eye would replace their taste with mine. Neither is needed.
# `_drive_knee_stays_sagittal` below is the numeric oracle that holds this closed.
_APEX_DRIVE_LAT_PLACEHOLDER = object()

_KEYPOSES_RAW = [
    # t_s,      label,      hip_off, pitch, drive_rise, drive_fore, drive_lat, plant_fore, plant_lat, right_fore, right_lat, right_h,               left_fore, left_lat, left_h
    [0.00000,  "startup",   0.00,    0.0,   0.05,       0.05,       -0.12,     0.00,       0.12,      0.05,       0.15,      -0.05,                 0.05,      -0.10,     -0.05],
    [4 / FPS,  "startup",   0.00,    5.0,   0.30,       0.15,       -0.05,     -0.02,      0.12,      0.05,       0.20,      0.10,                  0.05,      -0.12,     0.08],
    [8 / FPS,  "active",    0.00,    10.0,  0.45,       0.20,       _APEX_DRIVE_LAT_PLACEHOLDER, 0.05, 0.10,      0.05,       0.22,      0.20,                  0.05,      -0.15,     0.18],
    [10 / FPS, "active",    0.30,    8.0,   0.50,       0.20,       _APEX_DRIVE_LAT_PLACEHOLDER, 0.05, 0.10,      0.10,       0.05,      _APEX_HEIGHT_PLACEHOLDER, -0.05,   0.05,      0.15],
    # Frame 12 is the Active/Recovery SLICE BOUNDARY -- it is the last frame of
    # `layupactive` and the first of `layuprecovery` simultaneously, so its hip
    # offset decides what BOTH clips read as at the cut. It must stay high: the
    # release happens on Active's last frame, and a player who is already back
    # on the floor there is the arcade "unplanted shot" decoupling that
    # ADR-0003 names as the primary anti-goal. The descent belongs to Recovery,
    # which has fourteen ticks to spend on it. (Measured before this was
    # corrected: hips returned to +0.05 by f12, i.e. grounded at release.)
    [12 / FPS, "recovery",  0.24,    4.0,   0.30,       0.15,       -0.10,     0.05,       0.12,      0.05,       0.10,      0.30,                  0.00,      0.05,      0.10],
    # f16 carries the fall that f12 no longer does: still descending, not yet
    # into the absorb trough at f18.
    [16 / FPS, "recovery",  0.02,    1.0,   0.10,       0.05,       -0.14,     0.02,       0.16,      0.03,       0.10,      0.10,                  0.00,      -0.06,     0.10],
    [18 / FPS, "recovery", -0.12,    0.0,   0.00,       0.00,       -0.17,     0.00,       0.17,      0.00,       0.12,      0.05,                  0.00,      -0.08,     0.10],
    [TOTAL_TICKS / FPS, "recovery", -0.05, 0.0, 0.00,   0.00,       -0.15,     0.00,       0.15,      0.00,       0.11,      0.02,                  0.00,      -0.08,     0.10],
]

_CHANNEL_NAMES = (
    "hip_offset_m", "torso_pitch_deg", "drive_rise_m",
    "drive_fore_m", "drive_lat_m", "plant_fore_m", "plant_lat_m",
    "right_fore_m", "right_lat_m", "right_height_m",
    "left_fore_m", "left_lat_m", "left_height_m",
)

# Elbow bend-plane hints (NOT normalized -- aim_arm normalizes the resulting
# axis). Direct-signed along body_right, same convention as the lateral
# channels above (no reach_sign indirection -- see module docstring). One
# fixed hint per arm across the whole timeline, same pattern as
# author_behindtheback.py: it only has to avoid being exactly parallel to the
# reach direction, which a shallow up+outward hint safely is at every keypose
# here.
FINISH_ELBOW_HINT = (0.3, 0.6)   # (up_component, body_right-signed lateral)
OFF_ELBOW_HINT = (-0.4, -0.5)

# ── proof thresholds ──────────────────────────────────────────────────────────
# Support-level band for the Startup segment (plant foot stays down). Wider
# than author_behindtheback.py's 0.14 m: this move's ankle targets are built
# the same "tracks hips_now" way (see that script's own comment on common-mode
# ankle travel), and this Startup segment additionally drives one ankle
# (DRIVE_KNEE_SIDE) up to 0.45 m -- verify_grounded takes the LOWER of the two
# toes per frame, so that alone does not widen the needed tolerance, but the
# plant leg's own load-then-extend motion (plant_fore_m swinging -0.02->0.05)
# adds a bit more common-mode travel than behind-the-back's fixed stance, so
# the band is set with modest headroom over that script's own figure rather
# than copied blindly.
GROUND_BAND_TOL_M = 0.18
# Recovery-segment support-level band (frames 18..26): hip_offset only moves
# -0.12 -> -0.05 there (0.07 m swing), well inside a tighter band than
# Startup's.
RECOVERY_GROUND_BAND_TOL_M = 0.12
# Startup(f0)-vs-Recovery(f25) legibility floor (#296). Matches the other
# scripts' 15.0 deg floor.
POSE_DISTINCT_MIN_DEG = 15.0
# Minimum hip rise (m) required during Active to prove the character actually
# left the ground -- see verify_airborne's docstring for why this exists.
MIN_HIP_RISE_M = 0.25
# Non-symmetric finishing-hand-height floor (m): how much higher the RIGHT
# (finishing) hand must sit than the LEFT (off) hand during Active. The rig
# is mirror-symmetric to 0.17 mm, so this specific, signed, per-side
# comparison is what actually pins the finishing side -- a check that merely
# confirmed "some hand went up" would pass even with the sides swapped.
FINISH_HAND_HEIGHT_MARGIN_MIN_M = 0.10
# Absolute overhead floor (m): how far ABOVE THE HEAD the finishing wrist must
# sit at the Active apex. Pairs with the relative margin above, which cannot
# tell "extended overhead" from "raised to chest" -- see the gate itself.
FINISH_HAND_ABOVE_HEAD_MIN_M = 0.12

# Drive-knee sagittal band (m), Active only: how far the drive KNEE may sit
# from its own hip's sagittal plane. This is the gate that #335 was missing --
# every existing proof in this file is about ANKLES (grounded/airborne) or
# HANDS, and `plant_foot` guarantees the ankle by construction, so a knee
# splayed a quarter-metre sideways passed all of them. See
# `_APEX_DRIVE_LAT_PLACEHOLDER` for the mechanism.
#
# Measured: 0.0293 m post-fix, against 0.2643 m for the pose a blind
# regeneration would have shipped. 0.12 sits between them, and the 4x apparent
# headroom over the current reading is NOT slack -- the knee's response to the
# apex `drive_lat_m` is roughly 6:1 in this fold band (27 cm of knee per 5 cm of
# ankle, measured), so 0.12 m of knee is only ~2 cm of drift in the spec channel
# anyone would actually be editing. Tightening it toward the reading would gate
# on solver float noise instead.
DRIVE_KNEE_LAT_MAX_M = 0.12
# Max per-frame lateral travel (m) of the drive knee, over the WHOLE clip. The
# band above is a static check and would happily pass a knee that crosses the
# body every other frame. The pre-fix pose snapped 0.256 m between frames 10 and
# 11 -- a full knee-width in 1/60 s, which reads as a glitch rather than a
# motion, and legibility is a competitive requirement here (CLAUDE.md section 1),
# not a polish item.
#
# Measured: 0.0258 m post-fix (frames 7->8, the drive knee's fastest genuine
# travel), against that 0.256 m snap. 0.08 is ~3x the real motion and ~1/3 of
# the glitch, which is the widest gap available -- the honest constraint is that
# a legitimate 60 Hz knee swing and a one-frame teleport differ by an order of
# magnitude here, so anywhere in the middle of that decade works.
DRIVE_KNEE_POP_MAX_M = 0.08

# Diagnostic escape hatch: skip the arm solve so a single run can report the
# reach ratio at EVERY frame instead of dying at the first over-reach. Never
# set for a real authoring run -- the exported FBX would have no arm keys.
_MEASURE_ONLY = os.environ.get("LAYUP_MEASURE_ONLY") == "1"


def _keyposes_for_lib():
    """`_KEYPOSES_RAW` translated into `blender_anim_lib.Keypose` objects."""
    out = []
    for row in _KEYPOSES_RAW:
        t_s, label = row[0], row[1]
        values = row[2:]
        channels = dict(zip(_CHANNEL_NAMES, values))
        out.append(lib.Keypose(t_s, label, **channels))
    return out


def _torso_pitches_backward(arm, geom, forward):
    """The torso's Startup pitch must move the head BACKWARD (module docstring
    "TORSO PITCH" section) -- i.e. the Head bone's `forward`-axis coordinate,
    relative to Hips, must DECREASE from frame 0 to frame 8 (the full-tell
    pose). `TORSO_PITCH_SIGN` is an authored guess; this is the numeric oracle
    for it, exactly the discipline `author_behindtheback.py`'s own torso-twist
    sign required (rotation handedness about a derived axis is not reliably
    guessable by eye).
    """
    scene = bpy.context.scene
    head_bone = "mixamorig:Head"
    with lib.preserve_frame():
        scene.frame_set(F0)
        hips0 = arm.pose.bones[lib.HIPS].head.copy()
        head0 = arm.pose.bones[head_bone].head.copy()
        scene.frame_set(STARTUP_TICKS)
        hips8 = arm.pose.bones[lib.HIPS].head.copy()
        head8 = arm.pose.bones[head_bone].head.copy()
    fore0 = (head0 - hips0).dot(forward)
    fore8 = (head8 - hips8).dot(forward)
    shift_m = geom.to_m(fore8 - fore0)
    lib.report("torso_head_fore_shift_m", f"{shift_m:+.4f}")
    if shift_m >= 0.0:
        raise SystemExit(
            f"FATAL: the Startup torso pitch moved the head {shift_m:+.4f} m "
            f"ALONG forward (frame 0 -> frame {STARTUP_TICKS}) -- expected "
            f"BACKWARD (negative). Flip TORSO_PITCH_SIGN.")


def _drive_knee_stays_sagittal(arm, geom, active_frames, all_frames):
    """The drive KNEE must stay near its own hip's sagittal plane, and must not
    jump laterally between frames (#335).

    WHY THIS GATE EXISTS, and why nothing already in this file covers it: every
    other proof here constrains an ANKLE (`verify_grounded`, `verify_airborne`)
    or a HAND (the two finishing-hand gates). `plant_foot` puts the ankle on its
    target by construction -- exactly, since #321 -- so an ankle-denominated
    proof cannot see the knee at all. The knee is emergent from the bend plane
    and, at the apex fold ratio (~0.31 of leg reach), hypersensitive to the
    ankle's lateral offset. That combination is how a 0.26 m splay across the
    whole Active window shipped under a fully green authoring run.

    Two separate claims, because one does not imply the other:

    - the BAND: |knee lateral offset from the drive hip| during Active. Catches
      a statically splayed, frog-legged drive knee.
    - the POP: per-frame change in that offset, across the whole clip. Catches a
      knee that is *in* band at both ends of a snap but teleports between them.
      The pre-fix pose passed neither, but a naive re-tune could fix the band and
      leave the snap.

    Measured relative to the DRIVE HIP, not the hips centre: the femur hangs off
    the hip, so the hip is the only frame of reference in which "sagittal" means
    anything.
    """
    scene = bpy.context.scene
    up_leg, leg, _foot, _toe = lib.LEG_CHAIN[DRIVE_KNEE_SIDE]
    body_right = geom.body_right

    lateral_by_frame = {}
    with lib.preserve_frame():
        for frame in all_frames:
            scene.frame_set(frame)
            hip = arm.pose.bones[up_leg].head
            knee = arm.pose.bones[leg].head
            lateral_by_frame[frame] = geom.to_m((knee - hip).dot(body_right))

    worst_frame = max(active_frames, key=lambda f: abs(lateral_by_frame[f]))
    worst_lat = lateral_by_frame[worst_frame]
    lib.report("drive_knee_worst_active_lat_m", f"{worst_lat:+.4f} (frame {worst_frame})")

    ordered = sorted(all_frames)
    pops = [(abs(lateral_by_frame[b] - lateral_by_frame[a]), a, b)
            for a, b in zip(ordered, ordered[1:])]
    worst_pop, pop_from, pop_to = max(pops)
    lib.report("drive_knee_worst_pop_m", f"{worst_pop:.4f} (frames {pop_from}->{pop_to})")

    if abs(worst_lat) > DRIVE_KNEE_LAT_MAX_M:
        raise SystemExit(
            f"FATAL: at frame {worst_frame} the drive ({DRIVE_KNEE_SIDE}) knee sits "
            f"{worst_lat:+.4f} m from its own hip's sagittal plane -- allowed "
            f"+/-{DRIVE_KNEE_LAT_MAX_M} m. A drive knee this far outboard reads as "
            f"frog-legged, not as a layup. The cause is almost certainly the apex "
            f"`drive_lat_m`: it is measured from the HIPS CENTRE, so it must carry "
            f"the drive hip's own offset to put the ankle under that hip. See "
            f"`_APEX_DRIVE_LAT_PLACEHOLDER`.")

    if worst_pop > DRIVE_KNEE_POP_MAX_M:
        raise SystemExit(
            f"FATAL: the drive ({DRIVE_KNEE_SIDE}) knee moved {worst_pop:.4f} m "
            f"laterally between frames {pop_from} and {pop_to} -- allowed "
            f"{DRIVE_KNEE_POP_MAX_M} m per frame (1/{FPS} s). That is a visible "
            f"snap, not a motion. Check whether `drive_lat_m` crosses the drive "
            f"hip's own lateral offset between those keyposes: the knee's bend "
            f"plane inverts there, and at this fold ratio the femur swings a long "
            f"way to do it.")


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    src, dst = argv[0], argv[1]

    arm, _src_f0, _src_f1 = lib.load_source(src, FPS)
    scene = bpy.context.scene

    geom = lib.RigGeometry(arm)
    geom.log_summary()
    # Anatomical right, derived + verified in the lib (#320).
    body_right = geom.body_right
    # `lateral`, NOT `body_right` (#320): this basis is handed to `aim_matrix`
    # as its `side_axis`, a bone-ROLL reference where the axis SIGN is
    # load-bearing but its anatomy is irrelevant. Swapping in `body_right` here
    # would roll the posed bones 180 deg while changing nothing about which side
    # anything lands on.
    #
    # This does NOT extend to the torso pitch below, which passes `body_right`
    # to `Matrix.Rotation` (line ~400) -- that is correct, because
    # `TORSO_PITCH_SIGN` was derived against `body_right` and is pinned by the
    # lean-direction oracle `_torso_pitches_backward`. Axis and sign constant
    # are a pair; see `RigGeometry`'s docstring.
    right, up, forward = geom.lateral, geom.up, geom.forward
    lib.report("body_right", tuple(round(v, 4) for v in body_right))

    lib.enter_pose_mode(arm)

    # Anchor Hips position, captured ONCE before any of this script's own
    # posing -- every frame's Hips target is built from this, same pattern as
    # author_steal.py's `hips_base` (a stationary commitment authors its own
    # trajectory rather than inheriting the source's root motion).
    hips_base = arm.pose.bones[lib.HIPS].head.copy()

    # Measure the REST shoulder height above Hips (armature units) so the
    # Active-apex finishing-hand target is a real overhead extension, not a
    # guessed metre figure -- see module docstring.
    finish_humerus = lib.ARM_CHAIN[FINISH_ARM_SIDE][0]
    shoulder_head = arm.pose.bones[finish_humerus].head.copy()
    shoulder_height_above_hips_u = (shoulder_head - hips_base).dot(up)
    shoulder_height_above_hips_m = geom.to_m(shoulder_height_above_hips_u)
    apex_right_height_m = shoulder_height_above_hips_m + FINISH_APEX_EXTENSION_ABOVE_SHOULDER_M
    lib.report("shoulder_height_above_hips_m", f"{shoulder_height_above_hips_m:.4f}")
    lib.report("finish_apex_right_height_m", f"{apex_right_height_m:.4f}")

    # Patch the Active-apex row's right_height_m placeholder now that it is
    # measured, rather than hardcoding a metre figure that could silently
    # demand more reach than the arm has (module docstring).
    right_height_idx = 2 + _CHANNEL_NAMES.index("right_height_m")
    patched = False
    for row in _KEYPOSES_RAW:
        if row[right_height_idx] is _APEX_HEIGHT_PLACEHOLDER:
            row[right_height_idx] = apex_right_height_m
            patched = True
    if not patched:
        raise SystemExit("FATAL: no keypose row carried the apex height placeholder")

    # Same treatment for the apex rows' `drive_lat_m` (#335). Measured from the
    # REST pose, not the posed rig: the hip's lateral offset is a property of
    # the Y Bot skeleton, so reading it off `bone.head_local` keeps it
    # independent of whatever pose the source clip happens to carry at frame 0.
    # See `_APEX_DRIVE_LAT_PLACEHOLDER` for why this is derived rather than
    # authored.
    hips_rest = arm.pose.bones[lib.HIPS].bone.head_local
    drive_hip_rest = arm.pose.bones[lib.LEG_CHAIN[DRIVE_KNEE_SIDE][0]].bone.head_local
    drive_hip_lat_m = geom.to_m((drive_hip_rest - hips_rest).dot(body_right))
    lib.report("drive_hip_lat_m", f"{drive_hip_lat_m:+.4f}")
    # The drive leg is the LEFT one, so its hip must sit on the character's
    # LEFT -- i.e. NEGATIVE along `body_right`. Asserted rather than assumed,
    # because a sign error here would place the apex ankle 0.18 m to the WRONG
    # side and still pass every side-agnostic gate in this file (the #255
    # lesson: the rig is mirror-symmetric to 0.17 mm, so only a signed check
    # catches a mirrored placement).
    expected_sign = -1.0 if DRIVE_KNEE_SIDE == "L" else 1.0
    if drive_hip_lat_m * expected_sign <= 0.0:
        raise SystemExit(
            f"FATAL: the {DRIVE_KNEE_SIDE} hip measured {drive_hip_lat_m:+.4f} m "
            f"along body_right, but a {DRIVE_KNEE_SIDE} hip must be "
            f"{'negative' if expected_sign < 0 else 'positive'}. Either "
            f"`body_right` is inverted or LEG_CHAIN is mismapped -- do not "
            f"'fix' this by flipping the sign here.")

    drive_lat_idx = 2 + _CHANNEL_NAMES.index("drive_lat_m")
    patched_lat = 0
    for row in _KEYPOSES_RAW:
        if row[drive_lat_idx] is _APEX_DRIVE_LAT_PLACEHOLDER:
            row[drive_lat_idx] = drive_hip_lat_m
            patched_lat += 1
    if patched_lat != 2:
        raise SystemExit(
            f"FATAL: expected exactly 2 keypose rows carrying the apex "
            f"drive-lateral placeholder, found {patched_lat}. Both Active rows "
            f"(frames {STARTUP_TICKS} and {STARTUP_TICKS + 2}) must carry it -- "
            f"patching only one reintroduces the frame-11 knee snap #335 fixed.")

    finish_humerus_u, finish_ulna_u = lib.arm_lengths(arm, FINISH_ARM_SIDE)
    off_side = "L" if FINISH_ARM_SIDE == "R" else "R"
    off_humerus_u, off_ulna_u = lib.arm_lengths(arm, off_side)
    log(f"arm reach: finish({FINISH_ARM_SIDE})={geom.to_m(finish_humerus_u + finish_ulna_u):.4f} m "
        f"off({off_side})={geom.to_m(off_humerus_u + off_ulna_u):.4f} m")

    keyposes = _keyposes_for_lib()

    worst_wrist_err = 0.0
    worst_ankle_err = 0.0
    worst_reach = (0.0, "", 0, 0.0)  # (ratio, side, frame, t_s)

    def apply(frame, _t_s, ch):
        nonlocal worst_wrist_err, worst_ankle_err, worst_reach

        # ---- clavicles: pinned to REST, not inherited from the source ------
        # "Goalkeeper Catch Stationary.fbx" is a catch pose -- its own
        # Shoulder(clavicle) bones carry uncontrolled idle sway across the
        # frame range this script spans (measured: this is what was blowing
        # the arm reach-ratio checks at frames 17-26 well past 100%, on
        # targets that are otherwise modest). `blender_anim_lib`'s ARM_CHAIN
        # deliberately excludes the clavicle from the two-link solve, so
        # nothing else in this file controls it -- left alone, the humerus
        # ROOT drifts frame to frame independent of our own hips/spine
        # authoring, which is exactly the "uncontrolled source motion" this
        # library's own stated method says to author over, not inherit (see
        # author_steal.py's hips_base rationale, applied here to the
        # clavicles instead of the root).
        for side in ("L", "R"):
            sh_bone = arm.pose.bones[f"mixamorig:{'Left' if side == 'L' else 'Right'}Shoulder"]
            sh_bone.matrix_basis = Matrix.Identity(4)
            bpy.context.view_layer.update()
            sh_bone.keyframe_insert("rotation_quaternion", frame=frame)

        # ---- hips: vertical delta off the fixed anchor (an absolute target,
        # not lib.drop_hips's delta-on-source's-own-root-motion -- same
        # reasoning as author_steal.py: this is an authored trajectory for a
        # planted/airborne move, not an adjustment to inherited root motion).
        pb = arm.pose.bones[lib.HIPS]
        mh = pb.matrix.copy()
        mh.translation = hips_base + up * geom.m(ch["hip_offset_m"])
        pb.matrix = mh
        bpy.context.view_layer.update()
        pb.keyframe_insert("location", frame=frame)
        hips_now = pb.head.copy()

        # ---- torso pitch: rotate SPINE about the LATERAL axis (body_right),
        # composed onto the source's own spine pose -- NOT about `up` (that
        # would be a twist, not a pitch). See module docstring + the numeric
        # oracle `_torso_pitches_backward` below.
        pitch_rad = math.radians(TORSO_PITCH_SIGN * ch["torso_pitch_deg"])
        lib.rotate_bone_about_head(
            arm, lib.SPINE, (Matrix.Rotation(pitch_rad, 4, body_right),), frame=frame)

        # ---- legs: fixed toe direction (verbatim author_behindtheback.py) --
        toe_dir = (forward * 0.90 - up * 0.44).normalized()

        drive_ankle = (hips_now
                        + forward * geom.m(ch["drive_fore_m"])
                        + body_right * geom.m(ch["drive_lat_m"])
                        + up * geom.m(ch["drive_rise_m"])
                        - up * geom.m(NEUTRAL_HIP_TO_ANKLE_M))
        _solved, drive_err = lib.plant_foot(arm, DRIVE_KNEE_SIDE, drive_ankle, toe_dir, geom, frame=frame)
        worst_ankle_err = max(worst_ankle_err, drive_err)

        plant_ankle = (hips_now
                       + forward * geom.m(ch["plant_fore_m"])
                       + body_right * geom.m(ch["plant_lat_m"])
                       - up * geom.m(NEUTRAL_HIP_TO_ANKLE_M))
        _solved, plant_err = lib.plant_foot(arm, PLANT_FOOT_SIDE, plant_ankle, toe_dir, geom, frame=frame)
        worst_ankle_err = max(worst_ankle_err, plant_err)

        # ---- arms: finishing arm + off arm from the timeline channels ------
        arm_specs = (
            (FINISH_ARM_SIDE, ch["right_fore_m"], ch["right_lat_m"], ch["right_height_m"],
             FINISH_ELBOW_HINT, finish_humerus_u, finish_ulna_u),
            (off_side, ch["left_fore_m"], ch["left_lat_m"], ch["left_height_m"],
             OFF_ELBOW_HINT, off_humerus_u, off_ulna_u),
        )
        for side, fore_m, lat_m, height_m, hint_spec, humerus_u, ulna_u in arm_specs:
            target = (hips_now
                      + forward * geom.m(fore_m)
                      + body_right * geom.m(lat_m)
                      + up * geom.m(height_m))
            hint_up, hint_lat = hint_spec
            hint = (up * hint_up + body_right * hint_lat).normalized()

            sh_head = arm.pose.bones[lib.ARM_CHAIN[side][0]].head.copy()
            reach_u = humerus_u + ulna_u
            ratio = (target - sh_head).length / reach_u
            if ratio > worst_reach[0]:
                worst_reach = (ratio, side, frame, _t_s)
            if ratio > 0.90:
                log(f"reach ratio {ratio:.4f} for {side} arm at frame {frame} (t={_t_s:.4f}s)")
            if _MEASURE_ONLY:
                continue

            err_u = lib.aim_arm(arm, side, target, hint, geom, frame=frame)
            worst_wrist_err = max(worst_wrist_err, err_u)

    lib.bake_timeline(arm, keyposes, apply, F0, F1, FPS)

    bpy.ops.object.mode_set(mode="OBJECT")
    scene.frame_start, scene.frame_end = F0, F1

    lib.report_ankle_ik("worst_ankle_ik_err_m", geom.to_m(worst_ankle_err))
    lib.report("worst_wrist_err_m", f"{geom.to_m(worst_wrist_err):.6f}")
    _ratio, _rside, _rframe, _rt = worst_reach
    lib.report("worst_reach_ratio", f"{_ratio:.4f} ({_rside} arm, frame {_rframe}, t={_rt:.4f}s)")

    # ── proofs, before the export commits anything ────────────────────────────
    lib.enter_pose_mode(arm)
    all_frames = list(range(F0, F1 + 1))
    # 52 = Y Bot's 65 bones minus the 13 leaf terminators (matches every other
    # authoring script against the same rig).
    lib.verify_all_bones_keyed(arm, expected_count=52)
    lib.verify_pose_unscaled(arm, all_frames)

    # Startup: the plant foot stays down (verify_grounded skips Active by
    # design -- it is genuinely airborne there, proven instead by
    # verify_airborne below).
    lib.verify_grounded(arm, range(0, STARTUP_TICKS + 1), GROUND_BAND_TOL_M, geom)

    # Active: the rise HAPPENED. ref_height is the Hips height measured at
    # frame 0 -- a frame independently known to be grounded (verify_grounded
    # above proves it), not derived from the airborne window itself (see
    # verify_airborne's docstring for why that would be vacuous).
    hips_frame0_u = hips_base.dot(up)
    lib.verify_airborne(arm, range(STARTUP_TICKS, STARTUP_TICKS + ACTIVE_TICKS + 1),
                        MIN_HIP_RISE_M, geom, ref_height=hips_frame0_u)

    # Recovery from ~40% of the segment (frame 18) onward: landed and grounded.
    recovery_grounded_start = STARTUP_TICKS + ACTIVE_TICKS + int(RECOVERY_TICKS * 0.4)
    lib.verify_grounded(arm, range(recovery_grounded_start, F1 + 1), RECOVERY_GROUND_BAND_TOL_M, geom)

    # #335: the drive knee stays sagittal, and does not snap. Placed with the
    # other geometric proofs and BEFORE the export, so a splayed knee fails the
    # authoring run rather than shipping.
    _drive_knee_stays_sagittal(
        arm, geom,
        active_frames=range(STARTUP_TICKS, STARTUP_TICKS + ACTIVE_TICKS + 1),
        all_frames=all_frames)

    # #296: Startup must not equal Recovery.
    lib.verify_pose_distinct(
        lib.snapshot_pose(arm, 0),
        lib.snapshot_pose(arm, 25),
        POSE_DISTINCT_MIN_DEG,
        label="startup_vs_recovery")

    _torso_pitches_backward(arm, geom, forward)

    # Non-symmetric handedness pin (#255 lesson): the RIGHT (finishing) hand
    # must sit meaningfully higher than the LEFT (off) hand during Active. A
    # symmetric check ("some hand rose") would pass even with the arms
    # swapped -- the rig is mirror-symmetric to 0.17 mm, so only a signed,
    # per-side comparison actually proves which arm finished.
    f_apex = STARTUP_TICKS + 2  # frame 10, the apex keypose
    with lib.preserve_frame():
        scene.frame_set(f_apex)
        right_hand_h = arm.pose.bones[lib.ARM_CHAIN[FINISH_ARM_SIDE][2]].head.dot(up)
        left_hand_h = arm.pose.bones[lib.ARM_CHAIN[off_side][2]].head.dot(up)
    margin_m = geom.to_m(right_hand_h - left_hand_h)
    lib.report("finish_vs_off_hand_height_margin_m", f"{margin_m:+.4f}")
    if margin_m < FINISH_HAND_HEIGHT_MARGIN_MIN_M:
        raise SystemExit(
            f"FATAL: at the Active apex (frame {f_apex}) the finishing "
            f"({FINISH_ARM_SIDE}) hand sits only {margin_m:+.4f} m above the off "
            f"({off_side}) hand -- required >= {FINISH_HAND_HEIGHT_MARGIN_MIN_M} m. "
            f"This is the non-symmetric handedness pin (#255); a purely "
            f"symmetric check would pass even with the arms swapped.")

    # ABSOLUTE overhead gate, and it is a separate gate on purpose. The
    # margin-vs-off-hand check above is RELATIVE: a finishing hand at chest
    # height passes it comfortably so long as the off hand is lower, which is
    # exactly what happened on the first authored pass (measured: hand 0.029 m
    # BELOW the head at apex, while the relative margin read a healthy
    # +0.40 m). "Fully extended overhead" is a claim about where the hand is
    # relative to the BODY, so it needs a body landmark -- the head -- not the
    # other hand.
    with lib.preserve_frame():
        scene.frame_set(f_apex)
        head_h = arm.pose.bones["mixamorig:Head"].head.dot(up)
        finish_hand_h = arm.pose.bones[lib.ARM_CHAIN[FINISH_ARM_SIDE][2]].head.dot(up)
    above_head_m = geom.to_m(finish_hand_h - head_h)
    lib.report("finish_hand_above_head_m", f"{above_head_m:+.4f}")
    if above_head_m < FINISH_HAND_ABOVE_HEAD_MIN_M:
        raise SystemExit(
            f"FATAL: at the Active apex (frame {f_apex}) the finishing "
            f"({FINISH_ARM_SIDE}) hand sits {above_head_m:+.4f} m relative to the "
            f"head -- required >= {FINISH_HAND_ABOVE_HEAD_MIN_M} m above it. The "
            f"arm is raised but not extended overhead, so the pose does not read "
            f"as a rim finish. Raise FINISH_APEX_EXTENSION_ABOVE_SHOULDER_M "
            f"(currently {FINISH_APEX_EXTENSION_ABOVE_SHOULDER_M}) -- but keep an "
            f"eye on worst_reach_ratio, since aim_arm treats over-reach as fatal.")

    lib.export_fbx(arm, dst, ACTION_NAME)
    print("AUTHOR_OK")


main()
