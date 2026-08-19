"""Author `drivegather` as a single-polarity keypose clip in headless Blender (#311).

Run:
    "$BLENDER" --background --python-exit-code 1 \
        --python tools/author_drivegather.py -- <src.fbx> <out.fbx>

`--python-exit-code 1` is MANDATORY. Blender's `--background --python` exits 0
even on an uncaught traceback, so without it a crashed authoring run reports
success to the pipeline.

===============================================================================
THIS CLIP CARRIES A RULES SIGNAL, NOT A LOOK
===============================================================================
The gather is the frame after which the dribble is DEAD. ADR-0022 builds the
whole rim-finishing vertical on it, and `MoveAnimState`'s own doc names the
failure mode: showing a live-dribble loop once the dribble is dead

    "would advertise a drive the holder can no longer legally make -- an
     actively FALSE read, which is worse than no signal."

So "both hands arrive on the ball" is not styling. It is the moment the
holder's legal options change, and it is the one thing in this clip that MUST
be unambiguous. `_verify_hand_convergence` below asserts it -- together with
its own control (the hands are FAR APART at Startup's end), because a
convergence floor with no separation premise passes on a clip whose hands were
never apart in the first place.

===============================================================================
STEP-BACK'S TEMPLATE, RUN FORWARD (read author_stepback.py first)
===============================================================================
Same source (`assets/Dribble.fbx`), same dribble-family edges, same
"burst on JustEnteredActive" gameplay class, same in-place discipline. What
differs:

  - The burst points FORWARD, so every sign in the foot/torso tables flips.
  - Startup LOADS BACK before it goes. A gather sinks the weight over the rear
    leg while the chest drives forward; that is what gives this move's
    Startup/Active contrast an honest OPPOSITE SIGN rather than a difference
    of magnitude (see `_verify_hips_travel_forward`).
  - The arms are NOT mirrored. Startup is a ONE-HANDED dribble by definition
    and Active is a two-handed gather, so each hand gets its own three
    channels. Step-back could mirror one set because it had no hand-side
    content to sell; this clip's entire rules signal lives in the asymmetry
    collapsing.
  - Recovery hands off to `layupstartup`, not `jumpshotstartup`.

===============================================================================
AUTHORED IN PLACE -- THE HIPS NEVER TRANSLATE HORIZONTALLY
===============================================================================
Handoff 11's Active line reads "hips travel forward ~0.35 m". That number does
NOT become a hip translation channel, for the reason
author_retreatdribble.py/author_stepback.py both document at length:
PlayerController already applies the real displacement via
`DriveGatherMath.ComposeActiveVelocity` on JustEnteredActive, so a clip that
ALSO translates its root plays the burst twice and slides the mesh off its own
collider.

And the real gameplay figure is much bigger than the spec's, which is the
second reason not to key it. Read off the code:
`DriveGatherBurstSpeed = 9.0 m/s` (PlayerController.cs:486) x 10 Active ticks
/ 60 Hz = **1.50 m** of world displacement -- 4.3x the spec's 0.35 m. Neither
number reaches a translation channel. The clip depicts SOME clear forward
travel of the body over its own base, spent as the TRAIL foot drifting
backward relative to a vertically-anchored Hips; #238's tuning pass owns the
actual gameplay/visual match, and the 1.50 m figure is cited here and in the
PR so it has the number.

`_verify_hips_stay_in_place` is retreat dribble's gate reused verbatim over
the WHOLE clip -- one rule covering both the Startup load and the Active
burst.

===============================================================================
"~0.70 m STEP" IS A STEP LENGTH, NOT A HIPS-TO-ANKLE REACH
===============================================================================
Handoff 11: "The long first step plants far forward (~0.70 m) -- the gather
step is the biggest stride in the game." Read as an ankle-forward-of-the-hips
offset that number is not merely large, it is UNREACHABLE on this rig, and
that is a measurement rather than an opinion: femur 0.4060 + tibia 0.4210 =
0.8270 m of total leg reach (probed on `assets/Dribble.fbx`, and rig-intrinsic
so it is independent of the source clip). An ankle 0.70 m forward, 0.12 m
lateral and even 0.44 m below the hips needs 0.8354 m -- past the budget, so
`plant_foot` would clamp it and `report_ankle_ik` would refuse the run.

Read as a STEP LENGTH -- the fore/aft separation between the two ankles at the
moment of the plant, which is what "step length" means in gait -- 0.70 m is
both the natural reading and comfortably reachable: the table below lands the
lead ankle 0.40 m ahead of the hips and the trail ankle 0.30 m behind them,
each at ~81% and ~74% of the leg budget. `_verify_step_length` asserts the
separation, not a single ankle's offset.

Cross-check that this really is "the biggest stride in the game", using a
number this repo already measured rather than re-deriving one:
`LocomotionClipTest`'s #298 non-vacuity control records `locomotion/run` -- the
sprint -- as a KNOWN-GOOD real stride at **0.6418 m** peak-to-peak per foot. A
0.70 m gather step clears it. That is the whole use handoff 11 has for
`assets/run.fbx`: a reference figure to size against, never a source to
transplant from.

===============================================================================
#298'S TRANSPLANT IS A MEASURED DEAD END -- DO NOT RE-DERIVE IT
===============================================================================
The leg motion here is authored with `plant_foot`, the way
author_dribble_move.py does. It is NOT built by scaling a world-frame delta out
of `run.fbx`. That was #298's approach and the human REJECTED it in #300: a
world-frame delta from a sprint clip "inherits its motion CHARACTER wholesale
from a sprint clip. Amplitude scaling changes how big the sprint is, never that
it is a sprint." See `tools/rebuild_dribble_clips.gd`'s #298 section --
particularly its closing paragraph -- before deciding otherwise.

(`assets/run.fbx` is in any case not the thing it looks like: probed
2026-08-20 it is a 58-bone Kenney CONTROL rig -- `HipsCtrl`, `LeftFootIK`,
`LeftFootRollCtrl` -- carrying two frames, with none of the `mixamorig:`
names this library's constants assume. The usable run stride lives in the
retargeted `locomotion/run` clip, already measured above.)

===============================================================================
GROUNDED THROUGHOUT -- THIS MOVE NEVER JUMPS
===============================================================================
Unlike step-back (both feet leave the floor) this is a running move: one foot
is always in contact. The trail (LEFT) foot bears weight through Startup and
Active while the lead (RIGHT) foot swings and plants; then the lead foot bears
weight through Recovery while the trail foot comes through. So
`lib.verify_grounded` -- which takes the LOWER of the two toes per frame -- is
the right gate and is applied over the WHOLE clip, and `verify_airborne` does
not apply anywhere.

That grounding is not incidental. ADR-0003's primary anti-goal is arcade
decoupling of action from physical commitment; a gather that floats is exactly
that defect in the display layer.

===============================================================================
THE TORSO IS THE DEEPEST LEAN IN THE BATCH, AND IT IS READ AS ABSOLUTE
===============================================================================
`assets/Dribble.fbx`'s crouch already sits ~30 deg forward of vertical
(measured by author_retreatdribble.py on this same source and re-reported by
this script's own run). Handoff 11's "torso pitches forward hard (~40 deg, the
deepest lean in the batch)" is read as an ABSOLUTE target, matching the reading
author_stepback.py and author_retreatdribble.py both settled on -- so
`torso_fore_deg` below is an offset OFF that ~30 deg baseline, not a pitch
stacked on top of it (which would land past 70 deg and read as a stumble).

`TORSO_PITCH_SIGN` is NOT assumed inherited from a sibling script -- this move
pitches FORWARD where step-back counter-rotates BACKWARD, so the sign is the
opposite one and a copied constant would be silently wrong.
`_torso_pitch_sign_is_forward` re-derives it numerically on this clip's own
axes.

===============================================================================
THE RECOVERY -> LAYUP HAND-OFF
===============================================================================
`PlayerController` treats the finish as a SEPARATE `"layup"` request begun from
the displaced position (see the comment at PlayerController.cs:2332 and
EuroStep's class doc), so `DriveGatherRecovery -> LayupStartup` genuinely
occurs at runtime -- and every AnimationTree transition is a hard cut
(`grep -c xfade_time scenes/Player.tscn` == 0), so a large pose discontinuity
there SNAPS.

Handoff 11 says whichever of #311/#313 lands second owns the assertion. #313
landed first and did not take it, so this clip owns it. This script cannot
measure it -- `layupstartup` lives in a different source pipeline this script
never loads -- so the measurement lives downstream in
`tools/rebuild_drivegather_clips.gd`'s G6 and in `DriveGatherAnimTest`'s
`drivegather-recovery-hands-off-to-layup`, exactly as step-back's does.

What this script CAN do is aim at it, and the final Recovery row below is
authored against `author_layup.py`'s own frame-0 channel values (hips at
neutral, LEFT ankle 0.05 m fore / 0.05 m risen, RIGHT ankle square, both
wrists forward and just BELOW hip height) rather than picked in isolation. The
"ball raised to chest height" the handoff asks for therefore lands on the
INTERMEDIATE Recovery keypose (frame 23) and settles out of it by frame 30 --
Recovery has to read on its own AND land on the next clip's opening pose, and
those are not the same pose.

===============================================================================
WHAT THIS PRODUCES
===============================================================================
ONE Blender action, baked at 60 Hz, frame numbers ARE physics ticks:

    frames     seconds              segment
    0  -> 6    0.00000 -> 0.10000   Startup   (6 ticks  -- the last dribble)
    6  -> 16   0.10000 -> 0.26667   Active    (10 ticks -- the gather + plant)
    16 -> 30   0.26667 -> 0.50000   Recovery  (14 ticks -- through to the layup)

===============================================================================
UNHANDED
===============================================================================
DriveGather never swaps the ball hand -- it ENDS the dribble. So this clip
commits to ONE fixed polarity (right-handed last dribble) and must NOT be added
to `MoveAnimResolver.HandedMoves`; README trap 4 does not apply because there
is no second polarity to mistime.

===============================================================================
COSMETIC-ONLY
===============================================================================
This script is an ASSET BUILD TOOL. It does not import, read, or feed
`DriveGatherBurstSpeed`, `DriveGatherDecel`, `BallState`, `HasDribbled`, or any
PlayerController move-begin gate -- the 9.0 m/s figure above is quoted from a
code comment, not read. `DriveGatherTest`'s `dead-dribble-gate` scenario asserts
behaviour this file cannot reach, and stays green throughout.
"""

import math
import os
import sys

import bpy
from mathutils import Matrix

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import blender_anim_lib as lib  # noqa: E402  (must follow the sys.path fix)

log = lib.log

# ── clip contract, 60 Hz (DriveGather.DefaultFrameData) ──────────────────────
FPS = 60
STARTUP_TICKS = 6
ACTIVE_TICKS = 10
RECOVERY_TICKS = 14
TOTAL_TICKS = STARTUP_TICKS + ACTIVE_TICKS + RECOVERY_TICKS  # 30

F0 = 0
F1 = TOTAL_TICKS  # inclusive -- frame numbers ARE physics ticks

# Segment boundaries as frame numbers, named once so the gates below and
# rebuild_drivegather_clips.gd's slice windows cannot drift apart silently.
STARTUP_END = STARTUP_TICKS                # 6
ACTIVE_END = STARTUP_TICKS + ACTIVE_TICKS  # 16

ACTION_NAME = "drivegather"

# ── limb roles ───────────────────────────────────────────────────────────────
# RIGHT = LEAD (the foot that swings through and plants far forward; also the
# dribbling hand). LEFT = TRAIL (weight-bearing through Startup/Active, comes
# through in Recovery). Same limb assignment author_stepback.py /
# author_retreatdribble.py / author_jabstep.py use, so the cross-move contrast
# lives in what the body DOES, never in which limb is which -- and it agrees
# with author_layup.py's PLANT_FOOT_SIDE="R" / DRIVE_KNEE_SIDE="L", which is
# what lets the Recovery row below land on `layupstartup`'s opening stance
# without swapping legs across the cut.
LEAD_SIDE = "R"
TRAIL_SIDE = "L"

# ── stance geometry, metre-denominated ───────────────────────────────────────
# Reused verbatim from author_stepback.py / author_retreatdribble.py's
# measurement on this SAME rig (Y Bot: femur/tibia/foot are rig-intrinsic,
# independent of the source clip). Re-reported by geom.log_summary() every run.
NEUTRAL_HIP_TO_ANKLE_M = 0.62
STANCE_HALF_WIDTH_M = 0.12
STANCE_HALF_DEPTH_M = 0.10

# MEASURED HEADROOM against the two-link leg reach budget (femur 0.4060 +
# tibia 0.4210 = 0.8270 m). Worst target in the table below is the LEAD ankle
# at Active's end: 0.40 m fore (0.10 stance depth + 0.30 drift), 0.12 m
# lateral, 0.52 m down (0.62 neutral - 0.10 hip drop) -- 0.667 m of reach,
# 80.7% of budget, 0.160 m of headroom. Second worst is the LEAD ankle at
# frame 23 (0.675 m, 81.6%). `plant_foot`'s own `report_ankle_ik` catches
# anything this comment gets wrong.

# Torso pitch sign. OPPOSITE to author_stepback.py's, because this move pitches
# FORWARD where step-back counter-rotates BACKWARD -- so the constant is NOT
# inherited and `_torso_pitch_sign_is_forward` re-derives it below on this
# clip's own body_right/forward axes. author_contest.py's docstring records that
# its own initial sign guess was wrong on this same rig; a wrong sign here would
# ship a gather that rocks BACK at the instant it is supposed to attack the rim,
# which is the ADR-0003 false read this whole campaign exists to close.
TORSO_PITCH_SIGN = -1.0

# ── keypose channel table ────────────────────────────────────────────────────
# Columns:
#   time_s, label,
#   hip_offset_m    (+up, VERTICAL delta off the fixed hips_base anchor --
#                    there is no fore/aft channel anywhere in this table; see
#                    the module docstring's "AUTHORED IN PLACE" section)
#   torso_fore_deg  (FORWARD pitch off the source's own ~30 deg crouch;
#                    TORSO_PITCH_SIGN supplies the sign. NEGATIVE values pitch
#                    back toward vertical -- Recovery rises out of the gather)
#   lead_fore_m, lead_up_m    (RIGHT foot, off its own base spot)
#   trail_fore_m, trail_up_m  (LEFT foot, off its own base spot)
#   rh_fore_m, rh_lat_m, rh_up_m   (RIGHT wrist, hips-relative)
#   lh_fore_m, lh_lat_m, lh_up_m   (LEFT wrist, hips-relative)
#
# All `*_lat_m` channels are DIRECTLY SIGNED along body_right (positive =
# anatomical right), the same convention author_layup.py uses -- no mirror-sign
# indirection, because this clip's hands are genuinely asymmetric for half its
# length and a mirrored table could not express that at all.
_KEYPOSES_RAW = [
    # t_s,              label,      hip_off, torso, lead_fore, lead_up, trail_fore, trail_up, rh_fore, rh_lat, rh_up, lh_fore, lh_lat, lh_up
    # Frame 0 -- entry, hard-cut from the dribble stance (no xfade on any edge).
    # Already driving: hips a little low, chest a little past the crouch, the
    # LEAD foot just off the floor mid-swing, the RIGHT hand out on the ball and
    # the LEFT hand clearly away from it. This last cue is the CONTROL for the
    # whole convergence claim -- see `_verify_hand_convergence`.
    [0.00000,           "startup",  -0.10,    6.0,   -0.04,     0.06,    0.00,       0.00,     0.28,    0.24,   -0.06, 0.02,    -0.24,  0.00],
    # Frame 6 -- the Startup/Active SLICE BOUNDARY: simultaneously the last
    # frame of `drivegatherstartup` and the first of `drivegatheractive`. THE
    # LAST DRIBBLE, fully sold. Hips at their LOWEST (-0.18) and loaded BACK
    # over the trail foot (trail_fore +0.10 puts that foot forward relative to
    # the sinking hips), chest at its DEEPEST forward pitch (+12 off the ~30 deg
    # baseline, i.e. ~42 deg absolute -- handoff 11's "~40 deg, the deepest lean
    # in the batch"), the ball pushed out ahead and low in ONE hand, the LEFT
    # hand still out of it.
    [STARTUP_END / FPS, "active",   -0.18,    12.0,  0.16,      0.10,    0.10,       0.00,     0.34,    0.20,   -0.06, 0.00,    -0.26,  0.02],
    # Frame 11 -- mid-Active. Handoff 11: "Animate it properly; this is not a
    # held pose." Ten ticks is the longest Active in the dribble family and the
    # one segment in this clip that genuinely has room for an arc, so it gets an
    # interior keypose: the LEFT hand is travelling ACROSS to the ball (lh_fore
    # 0.00 -> 0.14, lh_lat -0.26 -> -0.18) while the lead foot is at the top of
    # its reach. The convergence is a MOTION here, not a cut.
    [11 / FPS,          "active",   -0.15,    6.0,   0.26,      0.06,    -0.04,      0.00,     0.30,    0.15,   -0.04, 0.14,    -0.18,  0.00],
    # Frame 16 -- the Active/Recovery boundary: THE GATHER. Both wrists have
    # arrived symmetrically on the ball at hip height (rh_lat +0.11 / lh_lat
    # -0.11, ~0.22 m apart -- a two-handed grip on a ball, not two hands near
    # each other). The lead foot has PLANTED (lead_up back to 0) 0.40 m ahead of
    # the hips while the trail foot sits 0.30 m behind them: a 0.70 m step
    # length, the module docstring's headline number. Hips have risen 0.08 m off
    # Startup's low point (-0.18 -> -0.10, handoff 11's "rise ~0.08 m") and
    # travelled forward over the base, and the chest has come UP out of the
    # crouch (-8 off the baseline, ~22 deg absolute) as the weight arrives.
    [ACTIVE_END / FPS,  "recovery", -0.10,    -8.0,  0.30,      0.00,    -0.20,      0.00,     0.26,    0.11,   0.00,  0.26,    -0.11,  0.00],
    # Frame 23 -- mid-Recovery. "The second foot comes through" (trail_up 0.14,
    # trail_fore -0.20 -> +0.05, swinging past the plant), "hips continue
    # forward" (the PLANTED lead foot drifts back 0.06 m relative to the hips --
    # the in-place expression, never a root translation), and "the ball is
    # raised to chest height": both wrists at +0.25 hips-relative, which is
    # roughly chest level given the measured shoulder at +0.438.
    [23 / FPS,          "recovery", -0.05,    -4.0,  0.24,      0.00,    0.05,       0.14,     0.22,    0.09,   0.25,  0.22,    -0.09,  0.25],
    # Frame 30 -- the end, authored AT `layupstartup`'s own opening pose (see
    # the module docstring's hand-off section): hips back to neutral, LEFT ankle
    # 0.05 m fore and 0.05 m risen, RIGHT ankle square under the hips, both
    # wrists forward and just BELOW hip height. The lead foot's 0.40 m of
    # backward drift across Recovery IS "the hips continued forward"; the trail
    # foot's 0.35 m forward swing IS "the second foot came through".
    # Deliberately NOT the chest-height gather of frame 23: Recovery has to read
    # on its own AND land on the next clip's first frame, and a residual gap is
    # accepted rather than chased to zero (author_stepback.py's Recovery row
    # records the same trade).
    #
    # `torso_fore_deg = +4` is the one cell here that is MEASURED rather than
    # copied, and the reason is worth stating because it is a trap the rest of
    # this row walked into. Matching author_layup.py's frame-0 CHANNEL VALUES is
    # only equivalent to matching its POSE where the two scripts share a source
    # FBX -- and they do not: layup is authored off `Goalkeeper Catch
    # Stationary.fbx`, this clip off `Dribble.fbx`. Channels measured relative
    # to the hips (the feet, the wrists) transfer exactly and land within 0.6 mm.
    # `torso_pitch_deg`, which is an offset off each source's OWN baseline
    # posture, does not. Copying layup's 0.0 left this clip bolt upright against
    # layupstartup's genuinely forward-leaning opening: G6 read the Head jumping
    # 0.3570 m. +4 is the value that puts the head where `layupstartup` actually
    # holds it (measured off the sliced resource: 0.330 m fore / 0.492 m up of
    # the hips), and it reads correctly on its own terms too -- a driver loading
    # for a finish leans INTO the rim, they do not come upright and stop.
    [TOTAL_TICKS / FPS, "recovery", 0.00,     4.0,   -0.10,     0.00,    0.15,       0.05,     0.05,    0.15,   -0.05, 0.05,    -0.10,  -0.05],
]

_CHANNEL_NAMES = (
    "hip_offset_m", "torso_fore_deg",
    "lead_fore_m", "lead_up_m", "trail_fore_m", "trail_up_m",
    "rh_fore_m", "rh_lat_m", "rh_up_m",
    "lh_fore_m", "lh_lat_m", "lh_up_m",
)

# Elbow bend-plane hints, per side: outward and slightly DOWN. A gather tucks
# the elbows under the ball rather than winging them up, which is the opposite
# of step-back's up+outward hint. Only has to avoid being parallel to the reach
# direction (which is mostly forward/down here), and a mostly-lateral hint
# safely is; `aim_arm` refuses outright if it ever is not.
ELBOW_HINT_UP = -0.3
ELBOW_HINT_LAT = 0.7

# ── proof thresholds ─────────────────────────────────────────────────────────
# Startup-end(f6)-vs-Recovery-end(f30) legibility floor (#296). Matches the
# other scripts' 15.0 deg floor.
STARTUP_END_VS_RECOVERY_END_MIN_DEG = 15.0

# THE RULES SIGNAL. Wrist-to-wrist distance at Active's end must be AT MOST
# this, and at Startup's end AT LEAST the second figure. Authored: ~0.22 m
# converged (a two-handed grip on a ~0.24 m ball) against ~0.58 m apart, so
# both floors sit roughly a third of the way clear of their authored readings
# and the two bands are separated by 0.10 m of no-man's-land. The ceiling is
# NOT widened to make a retune pass: "the off-hand comes clearly onto the ball,
# not near it" is handoff 11's explicit instruction, and a 0.30 m ceiling is
# already the loosest reading of it that still excludes "near".
HANDS_CONVERGED_MAX_M = 0.30
HANDS_APART_MIN_M = 0.40

# The step length (ankle-to-ankle fore separation) at Active's end -- see the
# module docstring for why this is a separation and not an ankle offset.
# Authored 0.70 m; the floor is set well under it because this gate exists to
# catch a sign error or a dead clip, not to pin a specific stride.
STEP_LENGTH_MIN_M = 0.55

# Hips travel forward over their own base during Active, measured against the
# TRAIL ankle (the base the exploding body leaves behind). Authored +0.30 m
# (trail_fore +0.10 -> -0.20).
HIPS_TRAVEL_FORWARD_MIN_M = 0.10

# ...and travel BACKWARD over the same measure during Startup: the gather LOADS
# before it goes. Authored -0.10 m (trail_fore 0.00 -> +0.10). This is the
# opposite-sign control that makes the figure above mean something rather than
# being one of two floors that both happen to pass -- the same structure
# author_stepback.py's Startup/Active pair uses, mirrored.
HIPS_LOAD_BACK_MIN_M = 0.04

# The torso is DEEPEST at Startup's end and has come up by Active's end.
# Measured as the spine->head vector's forward projection (the same quantity
# every sibling script uses). Authored: ~+0.23 m at f6 against ~+0.13 m at f16.
TORSO_FORE_AT_STARTUP_END_MIN_M = 0.15
TORSO_RISE_ACROSS_ACTIVE_MIN_M = 0.04

# One foot is on the floor at every frame -- this move never jumps (see the
# module docstring). Same tolerance the other grounded scripts use.
GROUND_BAND_MAX_M = 0.02


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

    Identical helper to author_stepback.py's / author_retreatdribble.py's --
    see those files for why this one quantity is measured three independent
    times across the pipeline (Blender-side here, resource-side in
    rebuild_drivegather_clips.gd, live-rig in DriveGatherAnimTest).
    """
    spine_head = arm.pose.bones[lib.SPINE].head.copy()
    head_head = arm.pose.bones["mixamorig:Head"].head.copy()
    return geom.to_m((head_head - spine_head).dot(forward))


def _torso_pitch_sign_is_forward(arm, geom, body_right, forward):
    """A positive `torso_fore_deg` must tip the torso FORWARD.

    Same technique as author_stepback.py's oracle -- rotate the spine->head
    vector by the signed pitch at a single frame (no baking, no two-frame
    comparison, so the source clip's own drift cannot contaminate the reading)
    -- but the ASSERTION is inverted, because this move's sign convention is the
    opposite one. A copied constant would be silently wrong here, which is
    exactly why this re-derives rather than inherits.
    """
    spine_head = arm.pose.bones[lib.SPINE].head.copy()
    head_head = arm.pose.bones["mixamorig:Head"].head.copy()
    vec = head_head - spine_head
    rot = Matrix.Rotation(math.radians(TORSO_PITCH_SIGN * 10.0), 4, body_right)
    delta_fore = geom.to_m(((rot @ vec) - vec).dot(forward))
    lib.report("torso_pitch_sign_fore_delta_m", f"{delta_fore:+.4f}")
    if not (delta_fore > 0.0):
        raise SystemExit(
            f"FATAL: a positive torso_fore_deg under TORSO_PITCH_SIGN="
            f"{TORSO_PITCH_SIGN} moves the spine->head vector {delta_fore:+.4f} m "
            f"along forward, i.e. NOT forward. A drive-gather's chest must pitch "
            f"FORWARD off the dribble crouch to reach handoff 11's '~40 deg, the "
            f"deepest lean in the batch'. Flip TORSO_PITCH_SIGN. (Note the "
            f"comparison is inverted -- `not >` rather than `<=` -- so a NaN "
            f"reading fails CLOSED instead of sailing through.)")


def _verify_hips_stay_in_place(arm, geom, hips_base, up, forward, body_right, frames):
    """The Hips translate along `up` ONLY -- never fore/aft, never laterally.

    Retreat dribble's / step-back's gate reused verbatim across the WHOLE clip
    -- see the module docstring's "AUTHORED IN PLACE" section for why one rule
    covers both the Startup load and the Active burst. Zero by construction
    today (`apply()` builds the Hips target as `hips_base + up * hip_offset_m`,
    no fore/aft term exists to be nonzero) -- the gate's job is refusing a
    FUTURE edit that adds one.
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
            f"This clip must be authored IN PLACE: PlayerController already "
            f"applies DriveGatherBurstSpeed via DriveGatherMath.ComposeActiveVelocity "
            f"on JustEnteredActive, so root translation here double-counts the burst "
            f"(1.50 m of it) and slides the mesh off its collider. Express both the "
            f"Startup load and the Active drive as the FEET moving relative to the "
            f"hips, never as hip translation.")


def _wrist_gap_m(arm, geom, frame):
    """Wrist-to-wrist distance in METRES at `frame`."""
    with lib.preserve_frame():
        bpy.context.scene.frame_set(frame)
        l = arm.pose.bones[lib.ARM_CHAIN["L"][2]].head.copy()
        r = arm.pose.bones[lib.ARM_CHAIN["R"][2]].head.copy()
    return geom.to_m((l - r).length)


def _verify_hand_convergence(arm, geom):
    """THE RULES SIGNAL: one hand at Startup's end, two hands on the ball at Active's end.

    Both halves live in ONE gate on purpose. A convergence ceiling on its own is
    satisfied by a clip whose hands were never apart -- which is precisely the
    generic-fallback defect #296 reports, since `locomotion/idle` holds both arms
    in a fixed relationship for every phase. The separation floor is the premise
    that makes the ceiling mean "they CAME together", and keeping the two in one
    function is what stops a future edit from deleting the premise and leaving a
    green gate that asserts nothing.

    Note this measures WRIST-TO-WRIST, not wrist-to-ball. The ball's position is
    BallController's business (it is attached by gameplay state, not by this
    clip), so "the hands are on the ball" is not a claim a cosmetic clip can
    make or a clip harness can check. What the clip owns -- and what an opponent
    actually reads at a glance -- is the two hands arriving together in front of
    the body.
    """
    apart = _wrist_gap_m(arm, geom, STARTUP_END)
    together = _wrist_gap_m(arm, geom, ACTIVE_END)
    lib.report("wrist_gap_at_startup_end_m", f"{apart:.4f}")
    lib.report("wrist_gap_at_active_end_m", f"{together:.4f}")

    # Inverted comparisons throughout: a NaN gap must fail CLOSED. (#310 needed
    # three separate NaN guards and the last was found only by mutation.)
    if not (apart >= HANDS_APART_MIN_M):
        raise SystemExit(
            f"FATAL: at Startup's end (frame {STARTUP_END}) the wrists are only "
            f"{apart:.4f} m apart (floor {HANDS_APART_MIN_M}). Startup is a "
            f"ONE-HANDED dribble -- without a real separation here the Active "
            f"convergence below proves nothing, because hands that were never "
            f"apart cannot come together.")
    if not (together <= HANDS_CONVERGED_MAX_M):
        raise SystemExit(
            f"FATAL: at Active's end (frame {ACTIVE_END}) the wrists are "
            f"{together:.4f} m apart (ceiling {HANDS_CONVERGED_MAX_M}). The gather "
            f"is the frame after which the DRIBBLE IS DEAD, and handoff 11 is "
            f"explicit that the off-hand must come clearly ONTO the ball, not near "
            f"it. Retune lh_lat_m/lh_fore_m/lh_up_m at the Active row; do NOT widen "
            f"this ceiling -- an ambiguous gather is the actively-false read "
            f"MoveAnimState's own doc names.")


def _verify_step_length(arm, geom, forward):
    """The lead and trail ankles straddle a ~0.70 m step at Active's end.

    A SEPARATION between the two ankles, not one ankle's offset from the hips --
    see the module docstring for the measurement that rules the latter reading
    out on this rig.
    """
    with lib.preserve_frame():
        bpy.context.scene.frame_set(ACTIVE_END)
        lead = arm.pose.bones[lib.LEG_CHAIN[LEAD_SIDE][2]].head.copy()
        trail = arm.pose.bones[lib.LEG_CHAIN[TRAIL_SIDE][2]].head.copy()
    step_m = geom.to_m((lead - trail).dot(forward))
    lib.report("step_length_at_active_end_m", f"{step_m:+.4f}")
    if not (step_m >= STEP_LENGTH_MIN_M):
        raise SystemExit(
            f"FATAL: at Active's end (frame {ACTIVE_END}) the lead ankle is only "
            f"{step_m:+.4f} m ahead of the trail ankle (floor {STEP_LENGTH_MIN_M}). "
            f"Handoff 11: the gather step is the biggest stride in the game -- it "
            f"has to clear locomotion/run's own measured 0.6418 m. A NEGATIVE "
            f"reading means the leg roles are swapped.")


def _hips_over_trail_m(arm, geom, forward, frame):
    """(Hips - trail ankle) . forward, in METRES, at `frame`.

    POSITIVE = the hips sit ahead of the base they are driving off. The live-rig
    counterpart in DriveGatherAnimTest measures the SAME relationship, for the
    same reason author_stepback.py's does: the Hips bone itself is pinned
    horizontally, so "the body travelled" can only be read against the foot it
    left behind.
    """
    with lib.preserve_frame():
        bpy.context.scene.frame_set(frame)
        hips = arm.pose.bones[lib.HIPS].head.copy()
        trail = arm.pose.bones[lib.LEG_CHAIN[TRAIL_SIDE][2]].head.copy()
    return geom.to_m((hips - trail).dot(forward))


def _verify_hips_travel_forward(arm, geom, forward):
    """Startup LOADS BACK over the trail foot; Active then DRIVES FORWARD off it.

    One gate, two opposite signs, for the same reason `_verify_hand_convergence`
    keeps its premise inline: a forward-travel floor alone would pass on a clip
    that simply started further back, and the thing that makes this move legible
    is the REVERSAL -- the gather sinks the weight before it goes.
    """
    at_entry = _hips_over_trail_m(arm, geom, forward, F0)
    at_startup_end = _hips_over_trail_m(arm, geom, forward, STARTUP_END)
    at_active_end = _hips_over_trail_m(arm, geom, forward, ACTIVE_END)
    load = at_startup_end - at_entry
    drive = at_active_end - at_startup_end
    lib.report("hips_over_trail_at_entry_m", f"{at_entry:+.4f}")
    lib.report("hips_over_trail_at_startup_end_m", f"{at_startup_end:+.4f}")
    lib.report("hips_over_trail_at_active_end_m", f"{at_active_end:+.4f}")
    lib.report("hips_load_back_over_startup_m", f"{load:+.4f}")
    lib.report("hips_drive_forward_over_active_m", f"{drive:+.4f}")

    if not (load <= -HIPS_LOAD_BACK_MIN_M):
        raise SystemExit(
            f"FATAL: over Startup the hips moved {load:+.4f} m relative to the trail "
            f"ankle (need <= {-HIPS_LOAD_BACK_MIN_M:+.4f}). A gather LOADS BACK over "
            f"the rear leg before it explodes; without that reversal the Active claim "
            f"below is just 'the clip moved forward', which an inert forward drift "
            f"would also satisfy.")
    if not (drive >= HIPS_TRAVEL_FORWARD_MIN_M):
        raise SystemExit(
            f"FATAL: over Active the hips moved {drive:+.4f} m relative to the trail "
            f"ankle (floor {HIPS_TRAVEL_FORWARD_MIN_M}). Either the trail foot's "
            f"Active-row drift regressed or the clip is inert. Remember the real "
            f"1.50 m of world displacement is PlayerController's, not this clip's -- "
            f"this figure is the in-place depiction only.")


def _verify_torso_deepest_at_startup_end(arm, geom, forward):
    """The chest is at its deepest forward pitch at Startup's end, and rises through Active.

    Handoff 11 puts the ~40 deg lean on Startup ("a gather converts horizontal
    speed into a launch") and brings the body up as the weight arrives over the
    plant. Asserting BOTH the absolute depth and the rise keeps the two phases
    visually separate -- a clip that leaned hard and STAYED there would satisfy
    a depth floor alone while reading as a stumble rather than a gather.
    """
    with lib.preserve_frame():
        bpy.context.scene.frame_set(STARTUP_END)
        at_startup_end = _spine_head_forward_m(arm, geom, forward)
        bpy.context.scene.frame_set(ACTIVE_END)
        at_active_end = _spine_head_forward_m(arm, geom, forward)
    rise = at_startup_end - at_active_end
    lib.report("torso_forward_at_startup_end_m", f"{at_startup_end:+.4f}")
    lib.report("torso_forward_at_active_end_m", f"{at_active_end:+.4f}")
    lib.report("torso_rise_across_active_m", f"{rise:+.4f}")

    if not (at_startup_end >= TORSO_FORE_AT_STARTUP_END_MIN_M):
        raise SystemExit(
            f"FATAL: at Startup's end the spine->head vector projects only "
            f"{at_startup_end:+.4f} m forward (floor {TORSO_FORE_AT_STARTUP_END_MIN_M}). "
            f"Handoff 11 wants the DEEPEST lean in the batch here. If this went "
            f"NEGATIVE the torso is leaning back and TORSO_PITCH_SIGN is inverted -- "
            f"but `_torso_pitch_sign_is_forward` should have caught that first.")
    if not (rise >= TORSO_RISE_ACROSS_ACTIVE_MIN_M):
        raise SystemExit(
            f"FATAL: the chest rose only {rise:+.4f} m between Startup's end and "
            f"Active's end (floor {TORSO_RISE_ACROSS_ACTIVE_MIN_M}). The weight has to "
            f"visibly come UP over the plant, or Startup and Active read as the same "
            f"pose held for sixteen ticks -- #296's actual complaint.")


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

    # Anchor Hips position, captured ONCE before any of this script's own
    # posing. Every frame's Hips target is built from this, so the move authors
    # its own (purely vertical) trajectory rather than inheriting the source's
    # root motion -- and so `_verify_hips_stay_in_place` has a fixed reference.
    hips_base = arm.pose.bones[lib.HIPS].head.copy()

    # Base spots for the two feet, staggered fore/aft: the LEAD (right) foot
    # starts marginally ahead, the TRAIL (left) marginally behind. Anchored to
    # `hips_base`, never to the moving hips -- author_contest.py's lesson was
    # that anchoring to the live hips made a crouch lift the feet by exactly the
    # crouch depth.
    lead_ankle_base = (hips_base
                       + body_right * geom.m(STANCE_HALF_WIDTH_M)
                       + forward * geom.m(STANCE_HALF_DEPTH_M)
                       - up * geom.m(NEUTRAL_HIP_TO_ANKLE_M))
    trail_ankle_base = (hips_base
                        - body_right * geom.m(STANCE_HALF_WIDTH_M)
                        - forward * geom.m(STANCE_HALF_DEPTH_M)
                        - up * geom.m(NEUTRAL_HIP_TO_ANKLE_M))

    keyposes = _keyposes_for_lib()

    worst_wrist_err = 0.0
    worst_ankle_err = 0.0

    def apply(frame, _t_s, ch):
        nonlocal worst_wrist_err, worst_ankle_err

        # ---- clavicles: pinned to REST, not inherited from the source -------
        # Dribble.fbx's own Shoulder(clavicle) bones carry a large, uncontrolled
        # asymmetry across this frame range (probed: the LEFT shoulder sits
        # 0.23 m forward of the hips against the right's 0.03 m, because the
        # source is mid-dribble on that side). ARM_CHAIN deliberately excludes
        # the clavicle from the two-link solve, so nothing else here controls
        # it, and leaving it would bake the source's own dribble hand into a
        # clip that dribbles on the other side.
        for side in ("L", "R"):
            sh = arm.pose.bones[f"mixamorig:{'Left' if side == 'L' else 'Right'}Shoulder"]
            sh.matrix_basis = Matrix.Identity(4)
            bpy.context.view_layer.update()
            sh.keyframe_insert("rotation_quaternion", frame=frame)

        # ---- hips: VERTICAL delta off the fixed anchor, and nothing else ----
        pb = arm.pose.bones[lib.HIPS]
        mh = pb.matrix.copy()
        mh.translation = hips_base + up * geom.m(ch["hip_offset_m"])
        pb.matrix = mh
        bpy.context.view_layer.update()
        pb.keyframe_insert("location", frame=frame)
        hips_now = pb.head.copy()

        # ---- torso pitch about the LATERAL axis (a pitch, not a twist) ------
        pitch_rad = math.radians(TORSO_PITCH_SIGN * ch["torso_fore_deg"])
        lib.rotate_bone_about_head(
            arm, lib.SPINE, (Matrix.Rotation(pitch_rad, 4, body_right),), frame=frame)

        # ---- legs: both feet channelled, both anchored to the FLOOR ---------
        toe_dir = (forward * 0.90 - up * 0.44).normalized()

        lead_ankle = (lead_ankle_base
                      + forward * geom.m(ch["lead_fore_m"])
                      + up * geom.m(ch["lead_up_m"]))
        _solved, lead_err = lib.plant_foot(arm, LEAD_SIDE, lead_ankle, toe_dir, geom, frame=frame)
        worst_ankle_err = max(worst_ankle_err, lead_err)

        trail_ankle = (trail_ankle_base
                       + forward * geom.m(ch["trail_fore_m"])
                       + up * geom.m(ch["trail_up_m"]))
        _solved, trail_err = lib.plant_foot(arm, TRAIL_SIDE, trail_ankle, toe_dir, geom, frame=frame)
        worst_ankle_err = max(worst_ankle_err, trail_err)

        # ---- arms: SEPARATE channels per hand -------------------------------
        # Not mirrored, unlike step-back/retreat-dribble/jab-step. Those moves
        # have no hand-side content to sell, so one set of channels reflected
        # across the body is a fair simplification. Here the asymmetry IS the
        # content: Startup is a one-handed dribble and Active is a two-handed
        # gather, and a mirrored table cannot express the first at all.
        for side, prefix, lat_sign in (("R", "rh", 1.0), ("L", "lh", -1.0)):
            target = (hips_now
                      + forward * geom.m(ch[f"{prefix}_fore_m"])
                      + body_right * geom.m(ch[f"{prefix}_lat_m"])
                      + up * geom.m(ch[f"{prefix}_up_m"]))
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

    # ── the move itself, proved from independent angles ──────────────────────
    _torso_pitch_sign_is_forward(arm, geom, body_right, forward)   # right way
    _verify_torso_deepest_at_startup_end(arm, geom, forward)       # deep, then up
    _verify_hips_stay_in_place(arm, geom, hips_base, up, forward, body_right, all_frames)
    _verify_hips_travel_forward(arm, geom, forward)                # load, then drive
    _verify_step_length(arm, geom, forward)                        # the biggest stride
    _verify_hand_convergence(arm, geom)                            # the RULES SIGNAL

    # One foot down at every frame -- verify_grounded takes the LOWER of the two
    # toes, so this is the whole-clip statement that the body never floats. See
    # the module docstring for why verify_airborne does not apply to any window
    # of this move.
    lib.verify_grounded(arm, all_frames, GROUND_BAND_MAX_M, geom)

    # ── #296 legibility ──────────────────────────────────────────────────────
    lib.verify_pose_distinct(
        lib.snapshot_pose(arm, STARTUP_END), lib.snapshot_pose(arm, F1),
        STARTUP_END_VS_RECOVERY_END_MIN_DEG, label="startup_end_vs_recovery_end")

    lib.export_fbx(arm, dst, ACTION_NAME)
    log(f"wrote {dst}")


if __name__ == "__main__":
    main()
