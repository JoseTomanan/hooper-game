"""Author `hesitation` as a single-polarity keypose clip in headless Blender (#307).

Run:
    "$BLENDER" --background --python-exit-code 1 \
        --python tools/author_hesitation.py -- <src.fbx> <out.fbx>

`--python-exit-code 1` is MANDATORY. Blender's `--background --python` exits 0
even on an uncaught traceback, so without it a crashed authoring run reports
success to the pipeline.

===============================================================================
STEP-BACK'S TEMPLATE (#306), NOT ITS TWIN
===============================================================================
Hesitation.DefaultFrameData is Startup=4 / Active=8 / Recovery=6 ticks @ 60 Hz --
18 ticks, 0.300 s total, off the SAME source (assets/Dribble.fbx) as StepBack
(#306) and RetreatDribble (#305), the SAME dribble-family edges, and the SAME
"authored in place, no burst channel" discipline -- so `_verify_hips_stay_in_place`
below is `author_stepback.py`'s own gate, reused VERBATIM.

But the READ is the opposite of everything else in this batch. Every other
move drops the hips (a crouch, a load, a plant). Hesitation.cs's own class doc:
"the freeze move ... pause their dribble rhythm to bait the defender" -- and
handoff 07 is explicit: "it is the only move that goes UP ... do not give
hesitation a crouch; you would be spending the one silhouette nobody else in
the batch is using." So the ONE channel every other script in this batch
treats as "how deep is the crouch" is inverted here into "how tall is the
stand-up" -- same channel vocabulary (`hip_offset_m`, +up off the fixed
`hips_base` anchor), opposite sign of travel.

===============================================================================
UNHANDED, AND WHY THAT MATTERS MORE THAN USUAL
===============================================================================
Hesitation.cs's own class doc, twice over: "No ball swap: the ball stays in
the same hand throughout" AND "applies NO lateral velocity impulse." So this
clip -- unlike Crossover/BehindTheBack/BetweenTheLegs -- carries no encoded
hand-side polarity at all (must NOT join `MoveAnimResolver.HandedMoves`), and
unlike StepBack/RetreatDribble/JabStep it also carries no encoded BURST
direction -- there is no world-space translation this clip needs to avoid
double-counting (the class doc's "no lateral velocity impulse" line means
PlayerController never calls a burst-math helper for this move at all). So
`_verify_hips_stay_in_place` below is not defending against a double-counted
burst (as it is in author_stepback.py) -- there is no burst to double-count.
It is defended anyway, for the same reason README's "AUTHORED IN PLACE"
discipline is universal across this batch: a Hips fore/aft channel is a
FUTURE-EDIT tripwire, not a today's-bug fix, and one rule that always applies
is cheaper to remember than "usually, except here."

===============================================================================
GROUNDED, NOT AIRBORNE -- the opposite of step-back's Active
===============================================================================
Handoff 07's motion spec keeps both feet on the floor throughout: "weight
suspended over the front foot with both feet nearly level (rear heel down)."
Unlike step-back (which explicitly leaves the ground during Active,
`verify_airborne`), this clip's hip rise is achieved ENTIRELY by leg extension
-- the ankles' vertical channel (`front_up_m` / `rear_up_m`) never leaves 0.0
anywhere in the table below, so `plant_foot`'s IK naturally straightens the
legs to keep the ankles on the floor while the Hips climb. `lib.verify_grounded`
runs across the WHOLE clip (all 19 frames), not scoped to a sub-window, because
there is no airborne window to exempt.

MEASURED HEADROOM against the two-link leg reach budget (femur 0.4060 + tibia
0.4210 = 0.8270 m, the SAME rig measurement author_stepback.py's own comment
records): the worst target is the LEAD (front) ankle at frame ACTIVE_END, where
`hip_offset_m` peaks at +0.17 -- vertical reach from the (now-elevated) hip to
the (floor-fixed) ankle is NEUTRAL_HIP_TO_ANKLE_M + 0.17 = 0.79 m alone, before
any lateral/forward component. Quadrature budget left for
sqrt(lateral^2+forward^2) is sqrt(0.827^2 - 0.79^2) = 0.245 m; the authored
front_fore_m (<=0.07) plus STANCE_HALF_DEPTH_M (0.10) is nowhere near that
ceiling. `lib.report_ankle_ik` is the hard backstop if a future retune pushes
past it.

===============================================================================
THE STAND-UP COMPLETES DURING STARTUP, NOT ACTIVE -- read this before retuning
===============================================================================
`_slice()`'s windows share their boundary frame: `hesitationstartup`'s LAST
pose IS `hesitationactive`'s FIRST pose. Handoff 07 is explicit that the rise
therefore has to live mostly in the STARTUP row (frame STARTUP_END, table row
2): by the time Active begins, the stand-up is nearly done (hip already at
+0.14 of the eventual +0.17), so Active's own 8-tick window is free to be the
HOLD the move's whole identity depends on -- across frames STARTUP_END..
ACTIVE_END every channel below moves only a few cm/degrees, never the ~0.16 m
/ ~23 deg swings Startup itself carries. If a retune wants a bigger stand-up,
grow it in the Startup row; growing the Active-END row's DELTA off Startup-END
instead reopens "Active moves," which is the one thing this move is not
allowed to do.

===============================================================================
THE TORSO -- verticality is the claim, not an absolute angle
===============================================================================
`assets/Dribble.fbx`'s crouch sits ~29.8-30.7 deg forward of vertical (measured
across frames 1..12 by author_retreatdribble.py and author_stepback.py on this
SAME source and SAME rig, re-verified here rather than trusted on faith --
see `_torso_pitch_sign_is_backward` below). `torso_back_deg` is the magnitude
of the BACKWARD counter-rotation off that baseline, so `back=30` lands close to
true vertical and `back=3` stays close to the raw crouch. Rather than gate an
absolute band (which drifts with exactly where in Dribble.fbx's own idle bounce
each sampled frame happens to sit -- author_stepback.py measured a 0.9 deg
spread across 12 frames), `_verify_torso_more_vertical_at_active_end` asserts
the RELATIVE claim handoff 07 actually makes: the chest is measurably closer to
vertical at Active's end than at the pre-move drive stance (frame 0). That
survives a frame-sampling wobble a tight absolute band would not.

===============================================================================
BOTH FEET GET CHANNELS, ONE WEIGHTED HARDER THAN THE OTHER
===============================================================================
Same convention as every other dribble-family script in this batch: RIGHT is
the LEAD (front, weighted) foot, LEFT the TRAIL (rear, "heel down but light")
foot -- so the cross-move contrast lives in what the body DOES, never in which
limb is which. Neither foot travels far; handoff 07's "both feet nearly level"
is a near-static stance, not a stride.

===============================================================================
WHAT THIS PRODUCES
===============================================================================
ONE Blender action, baked at 60 Hz, frame numbers ARE physics ticks:

    frames    seconds              segment
    0  -> 4   0.00000 -> 0.06667   Startup   (4 ticks -- the stand-up)
    4  -> 12  0.06667 -> 0.20000   Active    (8 ticks -- the hold, the read)
    12 -> 18  0.20000 -> 0.30000   Recovery  (6 ticks -- back down, re-load)

===============================================================================
COSMETIC-ONLY
===============================================================================
This script is an ASSET BUILD TOOL. It does not import, read, or feed
Hesitation.DefaultFrameData, BallState, HasDribbled, or any PlayerController
move-begin gate. It VISUALISES the freeze; nothing here changes gameplay.
"""

import math
import os
import sys

import bpy
from mathutils import Matrix

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import blender_anim_lib as lib  # noqa: E402  (must follow the sys.path fix)

log = lib.log

# ── clip contract, 60 Hz (Hesitation.DefaultFrameData) ───────────────────────
FPS = 60
STARTUP_TICKS = 4
ACTIVE_TICKS = 8
RECOVERY_TICKS = 6
TOTAL_TICKS = STARTUP_TICKS + ACTIVE_TICKS + RECOVERY_TICKS  # 18

F0 = 0
F1 = TOTAL_TICKS  # inclusive -- frame numbers ARE physics ticks

# Segment boundaries as frame numbers, named once so the gates below and
# rebuild_hesitation_clips.gd's slice windows cannot drift apart silently.
STARTUP_END = STARTUP_TICKS               # 4
ACTIVE_END = STARTUP_TICKS + ACTIVE_TICKS  # 12

ACTION_NAME = "hesitation"

# ── stance geometry, metre-denominated ────────────────────────────────────────
# Reused VERBATIM from author_stepback.py's measurement on this SAME rig off
# this SAME source clip (Y Bot: femur/tibia/foot are rig-intrinsic; the stance
# geometry is source-intrinsic to Dribble.fbx's own neutral pose).
NEUTRAL_HIP_TO_ANKLE_M = 0.62
STANCE_HALF_WIDTH_M = 0.12
STANCE_HALF_DEPTH_M = 0.10

# Torso pitch sign, SAME CONVENTION as author_stepback.py / author_retreatdribble.py:
# a positive rotation is BACKWARD (counter-rotating off the source's own
# forward crouch). NOT assumed inherited -- `_torso_pitch_sign_is_backward`
# below re-derives it independently on THIS clip's own body_right/forward
# axes. A wrong sign here would ship a hesitation that DIVES further into the
# crouch at the exact instant it is supposed to stand tall and freeze -- the
# ADR-0003 false read this whole campaign exists to close.
TORSO_PITCH_SIGN = 1.0

# ── keypose channel table ─────────────────────────────────────────────────────
# Columns:
#   time_s, label,
#   hip_offset_m      (+up, VERTICAL delta off the fixed hips_base anchor --
#                      no fore/aft channel anywhere in this table; see the
#                      module docstring's "UNHANDED" section)
#   torso_back_deg    (magnitude of the BACKWARD counter-rotation off the
#                      source's own ~30 deg forward crouch; TORSO_PITCH_SIGN
#                      supplies the sign)
#   front_fore_m, front_up_m   (LEAD (right) foot: forward offset / vertical
#                               clearance off its base spot -- up stays 0.0
#                               EVERYWHERE, this move never leaves the floor)
#   rear_fore_m, rear_up_m     (TRAIL (left) foot: same, off its own base spot)
#   arm_fore_m, arm_lat_m, arm_height_m  (BOTH hands -- mirrored; unhanded)
_KEYPOSES_RAW = [
    # t_s,               label,      hip_off, back, fr_fore, fr_up, re_fore, re_up, arm_fore, arm_lat, arm_h
    # Frame 0 -- entry, hard-cut from the dribble stance (no xfade on any
    # edge). The drive stance: low hips, still deep in the source crouch,
    # ball at ordinary dribble height.
    [0.00000,             "startup",  -0.02,   3.0,   0.02,    0.00,  -0.02,   0.00,  0.05,     0.14,    0.00],
    # Frame 4 -- the Startup/Active SLICE BOUNDARY: simultaneously the last
    # frame of `hesitationstartup` and the first of `hesitationactive`. THE
    # STAND-UP, nearly complete: hips risen to +0.14 (most of the eventual
    # +0.17), torso counter-rotated to back=26 (close to vertical already),
    # ball pushed forward/up toward waist height -- "the I'm going frame"
    # (handoff 07). See the module docstring's "THE STAND-UP COMPLETES
    # DURING STARTUP" section for why the rise is front-loaded here rather
    # than spread across Active.
    [STARTUP_END / FPS,   "active",    0.14,  26.0,   0.06,    0.00,  -0.03,   0.00,  0.10,     0.10,    0.18],
    # Frame 12 -- the Active/Recovery boundary: the APEX of the hold. Hips at
    # +0.17 (only +0.03 past Startup's own end -- the arrested read), torso at
    # back=30 (essentially vertical, "squares to the rim"), ball held high
    # and close to the body near waist height. Every channel here sits only a
    # few cm/degrees from the previous row -- that narrow gap IS the hold; see
    # `_verify_active_is_a_hold` below.
    [ACTIVE_END / FPS,    "recovery",  0.17,  30.0,   0.07,    0.00,  -0.03,   0.00,  0.08,     0.08,    0.20],
    # Frame 18 -- back down to the drive stance, ready to explode: hips low
    # again (-0.02, matching frame 0), torso back into the crouch (back=3),
    # ball returned to ordinary dribble height, front foot re-loading
    # slightly (fr_fore eased back toward 0).
    [TOTAL_TICKS / FPS,   "recovery", -0.02,   3.0,   0.02,    0.00,  -0.01,   0.00,  0.04,     0.14,    0.00],
]

_CHANNEL_NAMES = (
    "hip_offset_m", "torso_back_deg", "front_fore_m", "front_up_m",
    "rear_fore_m", "rear_up_m", "arm_fore_m", "arm_lat_m", "arm_height_m",
)

# Elbow bend-plane hints, mirrored per side (up + outward). Same pattern as
# author_stepback.py / author_retreatdribble.py / author_jabstep.py.
ELBOW_HINT_UP = 0.3
ELBOW_HINT_LAT = 0.6

# ── proof thresholds ──────────────────────────────────────────────────────────
# Startup-end(f4)-vs-Recovery-end(f18) legibility floor (#296). Matches the
# other scripts' 15.0 deg floor.
STARTUP_END_VS_RECOVERY_END_MIN_DEG = 15.0

# "Both feet stay on the floor" -- this move never leaves the ground (see the
# module docstring's "GROUNDED, NOT AIRBORNE" section). Matches
# author_retreatdribble.py / author_contest.py's tolerance, tighter than
# step-back's (which is genuinely airborne) or behind-the-back's (which pivots
# on one foot) -- appropriate here since front_up_m/rear_up_m never leave 0.0
# by construction, so any measured deviation is IK slack, not intent.
GROUND_BAND_TOL_M = 0.02

# The hip RISE from the drive stance (frame 0) to Startup's own end (frame
# STARTUP_END) -- handoff 07: "Hips rise ~0.08 m" at minimum; the authored
# table moves 0.16 m, well past that. Floor set well under the authored
# delta so a retune has room without silently regressing to "barely stands
# up".
HIP_RISE_STARTUP_MIN_M = 0.08

# The Active-window HOLD ceiling -- the authoring-time analogue of the
# harness's own `hesitation-active-is-held` scenario. Measures the Hips'
# OWN vertical delta between Startup's end (frame STARTUP_END) and Active's
# end (frame ACTIVE_END): the authored table moves only 0.03 m across that
# span. Ceiling is set comfortably above the authored delta but far below
# Startup's own 0.16 m rise, so a channel accidentally re-authored to keep
# climbing through Active (turning the hold into a second rise) reddens here
# before it ever reaches Blender/Godot's rotation-level proofs.
HIP_HOLD_MAX_DELTA_M = 0.08

# The Recovery DROP -- hips at F1 must come back down measurably below
# Active's own end (frame ACTIVE_END), matching the harness's
# `control-hesitation-recovery-lowers-hips` premise. Authored delta is
# 0.19 m; floor is well under that.
HIP_DROP_RECOVERY_MIN_M = 0.08

# The torso must be measurably MORE VERTICAL (smaller forward-lean magnitude)
# at Active's end than at the pre-move drive stance (frame 0) -- the relative
# claim, not an absolute band; see the module docstring's "THE TORSO" section
# for why relative survives a source-clip frame-sampling wobble that an
# absolute band would not.
TORSO_VERTICAL_MARGIN_M = 0.05

# ...and the OTHER side of that claim: the torso must not sail PAST vertical
# into a backward lean, which reads as a fadeaway rather than a hesitation.
# author_stepback.py's `_verify_torso_band_at_active_end` is two-sided for the
# same reason ("NOT past vertical, which would read as the fadeaway"); without
# a ceiling this gate gets MORE comfortable the worse the over-rotation gets,
# since raising `torso_back_deg` only grows `delta`.
#
# Expressed as a SIGN check with tolerance, not as a magnitude band, so it
# keeps the relative-over-absolute property the module docstring's "THE TORSO"
# section argues for: the source-clip frame-sampling wobble it warns about
# moves the lean's MAGNITUDE, and anything from -0.02 m upward passes here.
# Only a genuine sign flip -- the torso actually tipping backward -- trips it.
# MEASURED: the shipped clip reaches +0.006 m at Active's end (still a hair
# forward of vertical), so it clears this by the full tolerance.
TORSO_PAST_VERTICAL_TOL_M = 0.02


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

    Identical helper to author_stepback.py's own -- see that file's docstring
    for why this one quantity is measured three independent times across the
    pipeline (Blender-side here, resource-side in rebuild_hesitation_clips.gd
    if that tool chooses to re-derive it, live-rig in HesitationAnimTest).
    """
    spine_head = arm.pose.bones[lib.SPINE].head.copy()
    head_head = arm.pose.bones["mixamorig:Head"].head.copy()
    return geom.to_m((head_head - spine_head).dot(forward))


def _torso_pitch_sign_is_backward(arm, geom, body_right, forward):
    """A positive `TORSO_PITCH_SIGN` rotation must tip the torso BACKWARD.

    Verbatim technique from author_stepback.py's own oracle (itself from
    author_retreatdribble.py): rotate the spine->head vector by the signed
    pitch at a single frame (no baking, no two-frame comparison, so the
    source clip's own drift cannot contaminate the reading) and check the
    forward component SHRANK.
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
            f"moves the spine->head vector {delta_fore:+.4f} m ALONG forward, i.e. "
            f"FORWARD. A hesitation's torso must counter-rotate BACKWARD off the "
            f"dribble crouch to stand tall by Active's end (handoff 07). Flip "
            f"TORSO_PITCH_SIGN.")


def _verify_torso_more_vertical_at_active_end(arm, geom, forward):
    """Active's end reads measurably MORE VERTICAL than the pre-move stance.

    The relative claim, not an absolute band -- see the module docstring's
    "THE TORSO" section. `forward_at_f0` is the drive-stance baseline (still
    deep in the source crouch, back=3); `forward_at_active_end` must have
    shrunk by at least TORSO_VERTICAL_MARGIN_M.
    """
    with lib.preserve_frame():
        bpy.context.scene.frame_set(F0)
        fore_f0 = _spine_head_forward_m(arm, geom, forward)
        bpy.context.scene.frame_set(ACTIVE_END)
        fore_active_end = _spine_head_forward_m(arm, geom, forward)
    delta = fore_f0 - fore_active_end
    lib.report("torso_forward_f0_m", f"{fore_f0:+.4f}")
    lib.report("torso_forward_active_end_m", f"{fore_active_end:+.4f}")
    lib.report("torso_verticality_gain_m", f"{delta:+.4f}")
    if delta < TORSO_VERTICAL_MARGIN_M:
        raise SystemExit(
            f"FATAL: torso forward-lean only shrank {delta:+.4f} m from the drive "
            f"stance (frame {F0}) to Active's end (frame {ACTIVE_END}), need >= "
            f"{TORSO_VERTICAL_MARGIN_M}. Handoff 07: 'torso pitches toward vertical' "
            f"-- retune torso_back_deg at the Active row.")
    # TWO-SIDED, following author_stepback.py's _verify_torso_band_at_active_end
    # ("checks BOTH directions around 0") rather than retreat dribble's
    # one-sided form. A one-sided gate gets MORE comfortable the further the
    # torso over-rotates: raise torso_back_deg far enough and `delta` grows
    # without bound while the clip ships a hesitation leaning BACKWARD past
    # vertical -- which reads as a fadeaway, an ADR-0003 false read reaching
    # the rig through a green gate. Hesitation's target is "essentially
    # vertical, squares to the rim", so it has the most to lose from
    # over-rotation of any move in the batch, and needs the ceiling most.
    if fore_active_end < -TORSO_PAST_VERTICAL_TOL_M:
        raise SystemExit(
            f"FATAL: at Active's end (frame {ACTIVE_END}) the torso projects "
            f"{fore_active_end:+.4f} m -- a BACKWARD lean past vertical, beyond the "
            f"{TORSO_PAST_VERTICAL_TOL_M} m tolerance. That reads as a FADEAWAY, not a "
            f"hesitation (handoff 07: 'torso essentially vertical, squares to the "
            f"rim'). Lower torso_back_deg at the Active row.")


def _verify_hips_stay_in_place(arm, geom, hips_base, up, forward, body_right, frames):
    """The Hips translate along `up` ONLY -- never fore/aft, never laterally.

    Verbatim from author_stepback.py (itself from author_retreatdribble.py),
    reused across the WHOLE clip. Zero by construction today (`apply()`
    builds the Hips target as `hips_base + up * hip_offset_m`, no fore/aft
    term exists to be nonzero) -- the gate's job is refusing a FUTURE edit
    that adds one, exactly as it does in both sibling scripts. Hesitation has
    no burst to double-count (see the module docstring's "UNHANDED" section),
    but the discipline is uniform across the batch on purpose.
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
            f"This clip must be authored IN PLACE -- Hesitation.cs applies NO "
            f"velocity impulse of its own, so any horizontal Hips travel here "
            f"is pure authoring drift, not a depicted burst. Express the whole "
            f"read as the VERTICAL hip channel plus the feet/torso/arms, never "
            f"as hip translation.")


def _verify_hip_rise_hold_drop(arm, geom, up):
    """The three-act hip story: rises in Startup, holds in Active, drops in Recovery.

    Reads the Hips' OWN vertical channel at the four named instants -- the
    authoring-time analogue of the harness's own
    `hesitation-active-raises-hips` / `control-hesitation-recovery-lowers-hips`
    / `hesitation-active-is-held` trio, so a defect this obvious cannot reach
    Blender's export at all, let alone the Godot side.
    """
    scene = bpy.context.scene
    with lib.preserve_frame():
        scene.frame_set(F0)
        h_f0 = arm.pose.bones[lib.HIPS].head.dot(up)
        scene.frame_set(STARTUP_END)
        h_su = arm.pose.bones[lib.HIPS].head.dot(up)
        scene.frame_set(ACTIVE_END)
        h_ac = arm.pose.bones[lib.HIPS].head.dot(up)
        scene.frame_set(F1)
        h_re = arm.pose.bones[lib.HIPS].head.dot(up)

    rise_m = geom.to_m(h_su - h_f0)
    hold_delta_m = geom.to_m(h_ac - h_su)
    drop_m = geom.to_m(h_ac - h_re)
    lib.report("hip_rise_startup_m", f"{rise_m:+.4f}")
    lib.report("hip_hold_delta_m", f"{hold_delta_m:+.4f}")
    lib.report("hip_drop_recovery_m", f"{drop_m:+.4f}")

    if rise_m < HIP_RISE_STARTUP_MIN_M:
        raise SystemExit(
            f"FATAL: hips rose only {rise_m:+.4f} m from frame {F0} to Startup's "
            f"end (frame {STARTUP_END}), need >= {HIP_RISE_STARTUP_MIN_M}. "
            f"Handoff 07: 'Hips rise ~0.08 m' minimum during Startup.")
    if abs(hold_delta_m) > HIP_HOLD_MAX_DELTA_M:
        raise SystemExit(
            f"FATAL: hips moved {hold_delta_m:+.4f} m from Startup's end to "
            f"Active's end (frame {STARTUP_END} -> {ACTIVE_END}), exceeding the "
            f"{HIP_HOLD_MAX_DELTA_M} m hold ceiling. Active must read as arrested "
            f"-- see the module docstring's 'THE STAND-UP COMPLETES DURING "
            f"STARTUP' section. Move the rise into the Startup row instead.")
    if drop_m < HIP_DROP_RECOVERY_MIN_M:
        raise SystemExit(
            f"FATAL: hips dropped only {drop_m:+.4f} m from Active's end to the "
            f"clip's own end (frame {ACTIVE_END} -> {F1}), need >= "
            f"{HIP_DROP_RECOVERY_MIN_M}. Without a real drop, "
            f"'hesitation-active-raises-hips' could pass merely because the "
            f"whole clip was authored tall, not because Active specifically is.")


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    src, dst = argv[0], argv[1]

    arm, _src_f0, _src_f1 = lib.load_source(src, FPS)
    scene = bpy.context.scene

    geom = lib.RigGeometry(arm)
    geom.log_summary()
    # Anatomical right, derived + verified in the lib (#320). `geom.lateral`
    # is a BASIS vector that points at the character's LEFT on this rig and
    # must not be used for placement.
    body_right = geom.body_right
    up, forward = geom.up, geom.forward
    lib.report("body_right", tuple(round(v, 4) for v in body_right))

    lib.enter_pose_mode(arm)

    # Anchor Hips position, captured ONCE before any of this script's own
    # posing. Every frame's Hips target is built from this, so the move
    # authors its own (purely vertical) trajectory rather than inheriting the
    # source's root motion -- and so `_verify_hips_stay_in_place` has a fixed
    # reference.
    hips_base = arm.pose.bones[lib.HIPS].head.copy()

    # Base spots for the two feet, staggered fore/aft (the move distinguishes
    # a LEAD foot from a TRAIL foot from the very first frame). RIGHT is the
    # LEAD (front) foot and LEFT the TRAIL (rear) -- the same limb assignment
    # every other dribble-family script in this batch uses.
    front_ankle_base = (hips_base
                        + body_right * geom.m(STANCE_HALF_WIDTH_M)
                        + forward * geom.m(STANCE_HALF_DEPTH_M)
                        - up * geom.m(NEUTRAL_HIP_TO_ANKLE_M))
    rear_ankle_base = (hips_base
                       + body_right * geom.m(-STANCE_HALF_WIDTH_M)
                       - forward * geom.m(STANCE_HALF_DEPTH_M)
                       - up * geom.m(NEUTRAL_HIP_TO_ANKLE_M))

    keyposes = _keyposes_for_lib()

    worst_wrist_err = 0.0
    worst_ankle_err = 0.0

    def apply(frame, _t_s, ch):
        nonlocal worst_wrist_err, worst_ankle_err

        # ---- clavicles: pinned to REST, not inherited from the source -------
        # Dribble.fbx's own Shoulder(clavicle) bones carry uncontrolled idle
        # sway across this frame range; ARM_CHAIN deliberately excludes the
        # clavicle from the two-link solve, so nothing else here controls it.
        for side in ("L", "R"):
            sh = arm.pose.bones[f"mixamorig:{'Left' if side == 'L' else 'Right'}Shoulder"]
            sh.matrix_basis = Matrix.Identity(4)
            bpy.context.view_layer.update()
            sh.keyframe_insert("rotation_quaternion", frame=frame)

        # ---- hips: VERTICAL delta off the fixed anchor, and nothing else -----
        pb = arm.pose.bones[lib.HIPS]
        mh = pb.matrix.copy()
        mh.translation = hips_base + up * geom.m(ch["hip_offset_m"])
        pb.matrix = mh
        bpy.context.view_layer.update()
        pb.keyframe_insert("location", frame=frame)
        hips_now = pb.head.copy()

        # ---- torso pitch about the LATERAL axis (a pitch, not a twist) ------
        pitch_rad = math.radians(TORSO_PITCH_SIGN * ch["torso_back_deg"])
        lib.rotate_bone_about_head(
            arm, lib.SPINE, (Matrix.Rotation(pitch_rad, 4, body_right),), frame=frame)

        # ---- legs: BOTH feet channelled, NEITHER ever leaves the floor ------
        # Anchored to `hips_base`, not `hips_now` -- a planted move keeps the
        # FLOOR fixed and lets the hips move relative to it (author_contest.py's
        # lesson: anchoring to `hips_now` made a crouch lift the feet by
        # exactly the crouch depth -- here it would make a STAND-UP sink the
        # feet by the rise instead, the same bug mirrored).
        toe_dir = (forward * 0.90 - up * 0.44).normalized()

        front_ankle = (front_ankle_base
                       + forward * geom.m(ch["front_fore_m"])
                       + up * geom.m(ch["front_up_m"]))
        _solved, front_err = lib.plant_foot(arm, "R", front_ankle, toe_dir, geom, frame=frame)
        worst_ankle_err = max(worst_ankle_err, front_err)

        rear_ankle = (rear_ankle_base
                     + forward * geom.m(ch["rear_fore_m"])
                     + up * geom.m(ch["rear_up_m"]))
        _solved, rear_err = lib.plant_foot(arm, "L", rear_ankle, toe_dir, geom, frame=frame)
        worst_ankle_err = max(worst_ankle_err, rear_err)

        # ---- arms: ONE set of channels, mirrored per side --------------------
        # Ball pushed toward the body and held close -- this move is unhanded
        # (no shooting-side polarity encoded, see the module docstring), so
        # the channels stay symmetric across both hands, the same
        # simplification step-back/retreat-dribble/jab-step all make.
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

    # ── the freeze itself, proved from independent angles ────────────────────
    _torso_pitch_sign_is_backward(arm, geom, body_right, forward)     # right way
    _verify_torso_more_vertical_at_active_end(arm, geom, forward)     # tall enough
    _verify_hips_stay_in_place(arm, geom, hips_base, up, forward, body_right, all_frames)
    _verify_hip_rise_hold_drop(arm, geom, up)                         # rise/hold/drop

    # Never leaves the floor -- the opposite of step-back's Active. Whole clip,
    # not a sub-window; see the module docstring's "GROUNDED, NOT AIRBORNE".
    lib.verify_grounded(arm, all_frames, GROUND_BAND_TOL_M, geom)

    # ── #296 legibility ───────────────────────────────────────────────────────
    lib.verify_pose_distinct(
        lib.snapshot_pose(arm, STARTUP_END), lib.snapshot_pose(arm, F1),
        STARTUP_END_VS_RECOVERY_END_MIN_DEG, label="startup_end_vs_recovery_end")

    lib.export_fbx(arm, dst, ACTION_NAME)
    log(f"wrote {dst}")


if __name__ == "__main__":
    main()
