"""Author `stepback` as a single-polarity keypose clip in headless Blender (#306).

Run:
    "$BLENDER" --background --python-exit-code 1 \
        --python tools/author_stepback.py -- <src.fbx> <out.fbx>

`--python-exit-code 1` is MANDATORY. Blender's `--background --python` exits 0
even on an uncaught traceback, so without it a crashed authoring run reports
success to the pipeline.

===============================================================================
RETREAT DRIBBLE'S TEMPLATE, NOT ITS TWIN
===============================================================================
StepBack.DefaultFrameData is Startup=7 / Active=4 / Recovery=8 ticks @ 60 Hz --
19 ticks, 0.317 s total, off the SAME source (assets/Dribble.fbx) as
RetreatDribble (#305), the SAME dribble-family edges, and the SAME
"backward-burst on JustEnteredActive" gameplay class -- so the whole
clip-space-vs-world-translation discipline that script's docstring documents
in ~40 lines of measured prose applies here VERBATIM. Read that file before
retuning this one.

But the two moves are NOT structural twins the way jab step and retreat
dribble are. Retreat dribble is a 9-tick "quick" bait with one weight-bearing
foot throughout; step-back is a 19-tick "hold" -- the biggest separation move
in the taxonomy (StepBack.cs's own class doc), and its motion spec (handoff
06) has a genuinely different shape:

  - Startup is 7 ticks, not 3 -- long enough to animate a real weight
    transfer, not just hold a pose (handoff 06: "7 ticks is enough to animate
    the weight transfer rather than hold a pose -- do").
  - BOTH feet leave the ground during Active (handoff 06: "both feet leave
    the ground"). Retreat dribble's rear foot never lifts -- this move has no
    such invariant, so BOTH ankles get a vertical (up) channel, and
    `lib.verify_grounded` (built for a support foot that never leaves the
    floor) does not apply to the Active window at all. See "AIRBORNE, NOT
    GROUNDED" below.
  - The torso claim at Active's end is a BAND, not a one-sided floor.
    Retreat dribble only had to clear "at or past vertical" (leaning INTO the
    defender was the one failure mode). Step-back must land NEAR vertical on
    BOTH sides: leaning forward is the same "driving in" false read, but
    leaning BACK is `FadeawayActive`'s territory (handoff 06 / #318), and the
    display layer already splits on it (`FadeawayTriggerResolver`). Landing
    on the wrong side of THAT line reads as a shot payload came, which did
    not.

===============================================================================
TWO HANDOFF CORRECTIONS (docs/handoffs/306-step-back.md, verified against the
code 2026-08-12) -- READ BOTH BEFORE RETUNING A NUMBER BELOW
===============================================================================
Correction 1: the spec doc's Active-phase "hips travel back ~0.50 m" is STALE.
The real gameplay figure, read off the code: StepBackBurstSpeed=10.0 m/s
(PlayerController.cs:330) x 4 Active ticks / 60 Hz = 0.6667 m -- 33% more than
the spec's number.

Correction 2: that 0.6667 m gameplay figure must NOT reach a translation
channel, for the identical reason RetreatDribble's docstring gives --
PlayerController's StepBack branch already sets Velocity via
StepBackBurstMath.ComposeActiveVelocity on JustEnteredActive, so a clip that
ALSO translates its root plays the burst twice and slides the mesh off its
own collider.

JUDGMENT CALL (ADR-0014 tier 1/2, recorded here and in the PR): the number
that reaches the FEET below is neither of the above. It is the ORIGINAL
(pre-correction) spec figure, ~0.50 m, spent as clip-space foot drift -- the
same choice author_retreatdribble.py made for its own move: that script's
spec said "0.25 m" against a REAL gameplay burst of only 0.133 m and did not
reconcile the two, because 0.25 m is what reads at 0.033 s. This clip follows
that precedent rather than deriving a new ratio: legibility, not arithmetic,
picks the authored number. The corrected 0.6667 m figure is cited here and in
the PR for #238's tuning pass, which owns the actual gameplay/visual match --
this script's job is only to depict SOME clear backward extension, not to
scale it to the burst speed.

===============================================================================
AUTHORED IN PLACE -- THE HIPS NEVER TRANSLATE, ANYWHERE IN THE CLIP
===============================================================================
Handoff 06's Startup line reads "hips drop ~0.12 m AND SHIFT FORWARD ~0.15 m
over that foot." That forward-shift number does NOT become a hip translation
channel either, and this is a second, narrower judgment call worth stating
explicitly: jab step (#304) sells an ALMOST IDENTICAL read -- "leaning
forward over the extended front foot" -- using ONLY a vertical hip channel,
the planted foot's own forward reach, and torso pitch. No script in this
batch has ever given Hips a fore/aft channel; `_verify_hips_stay_in_place`
below is retreat dribble's gate reused VERBATIM (not narrowed to the Active
window) specifically because the same one-channel discipline covers both the
Startup "sell it" lie and the Active "don't double the burst" requirement
with one rule instead of two. The forward WEIGHT SHIFT reads through the
front foot's own forward offset (front_fore_m) under a forward-pitched torso
-- exactly jab step's technique -- never through translating the root.

===============================================================================
AIRBORNE, NOT GROUNDED -- Active gets `verify_airborne`, not `verify_grounded`
===============================================================================
`blender_anim_lib.verify_grounded`'s own docstring: "Skip only for genuinely
airborne moves (jump shot, block, layup) and assert the INTENDED flight arc
instead; do not simply widen tol_m until it passes." Handoff 06 puts
step-back in that category explicitly ("both feet leave the ground"), so this
script follows the same precedent rather than stretching a support-foot
tolerance to cover an actual jump.

The lift check in `main()` (below the timeline bake, using the channel
values captured at `frame == ACTIVE_END` during `apply()`) is the
move-specific positive claim -- mirrors `_verify_both_feet_drift_forward`,
but for the VERTICAL axis instead of the horizontal one: both ankles must
clear a minimum height above their base by Active's end, reduced with MIN,
never MAX (README trap 17 -- "both feet leave the ground" is a both-limbs
claim, and a one-legged hop would satisfy a max-reduced gate while failing
the read). It reads the channel table directly rather than re-solving IK
from the posed bones, since `apply()` already has the authored values in
hand. `lib.verify_airborne` is layered on top as the same generic proof
jumpshot/block/layup already carry (the Hips genuinely rose above a
known-grounded reference), so a defect that only fooled one of the two
measures still reddens the authoring run.

===============================================================================
THE TORSO BAND, AND WHY IT IS A BAND
===============================================================================
`assets/Dribble.fbx`'s crouch sits ~29.8-30.7 deg / +0.245..+0.252 m forward
of vertical (measured across frames 1..12, see author_retreatdribble.py's own
measurement on the same source -- re-verified below via this script's own
report rather than trusted). Handoff 06 reads step-back's Startup number
("torso pitches forward ~25 deg") the same way jab step's spec phrases its
own forward pitch -- a bare magnitude with no "from the dribble stance"
qualifier is read here as an ABSOLUTE target (matching retreat dribble's
"vertical to 5 deg back," the one other absolute framing in this batch),
which lands Startup's end pose SLIGHTLY straighter than the raw dribble
crouch -- an athletic "leaning in aggressively but not buried in the crouch"
stance, not a forward pitch stacked on top of an already-forward baseline
(which would land past 50 deg and read as a stumble, not a plant).

At Active's end the claim is symmetric: "upright and squares to the rim,"
explicitly NOT past vertical (that is the fadeaway, #318). So
`_verify_torso_band_at_active_end` checks BOTH directions around 0 -- unlike
retreat dribble's one-sided "at or past vertical" gate, which only had to
rule out leaning IN.

===============================================================================
BOTH FEET GET CHANNELS, ONE PLANTED HARDER THAN THE OTHER
===============================================================================
Like retreat dribble (and unlike jab step's single fixed plant), both legs
are channelled: the spec distinguishes a "lead" foot (plants hard in Startup,
explodes off it in Active) from a "trail" foot (light through Startup, kicks
through in Active), and BOTH re-plant wide in Recovery. Leg roles follow the
established convention (RIGHT = lead/front, LEFT = trail/rear) so the
cross-move contrast with retreat dribble and jab step lives in what the body
DOES, never in which limb is which.

===============================================================================
WHAT THIS PRODUCES
===============================================================================
ONE Blender action, baked at 60 Hz, frame numbers ARE physics ticks:

    frames    seconds              segment
    0  -> 7   0.00000 -> 0.11667   Startup   (7 ticks -- sell the drive)
    7  -> 11  0.11667 -> 0.18333   Active    (4 ticks -- the explosive jump)
    11 -> 19  0.18333 -> 0.31667   Recovery  (8 ticks -- land wide, rise to
                                               the jumpshot hand-off)

===============================================================================
UNHANDED
===============================================================================
Handoff 06 / StepBack.cs's own class doc: "No hand swap: there is no ball
transit (unlike Crossover/BehindTheBack)." So this clip commits to ONE fixed
polarity and must NOT be added to `MoveAnimResolver.HandedMoves` -- README
trap 4 does not apply because there is no second polarity to mistime. Arm
channels are mirrored per side, held close to the body (the ball gathered
and protected), the same simplification retreat dribble and jab step both
make for an unhanded move with no encoded hand-side polarity to sell.

===============================================================================
THE RECOVERY -> JUMPSHOT HAND-OFF
===============================================================================
Handoff 06's other load-bearing point: #253's cradle-race fix exists BECAUSE
StepBack cradles the ball at Active-entry for a shot that follows, so
Recovery's own end pose is authored to land close to `jumpshotstartup`'s
opening pose (ball at chest/gather height, feet set, hips beginning to rise)
-- every transition in scenes/Player.tscn is a hard cut (`xfade_time` 0
everywhere), so a large discontinuity here SNAPS at the most-watched moment
in the game. This script cannot measure that directly -- `jumpshotstartup`
lives in a different source pipeline this script never loads -- so the
measurement and the threshold live downstream, in
`tools/rebuild_stepback_clips.gd`'s G6 (which loads the SAME
`assets/locomotion.res` this rebuild is about to update, where
`jumpshotstartup` already exists from an earlier build) and in
`StepBackAnimTest`'s `stepback-recovery-hands-off-to-jumpshot` scenario (the
live-rig third measurement). See both for the threshold and its reasoning.

===============================================================================
COSMETIC-ONLY
===============================================================================
This script is an ASSET BUILD TOOL. It does not import, read, or feed
StepBackBurstSpeed, StepBackExitConeDegrees, BallState, HasDribbled, or any
PlayerController move-begin gate. It VISUALISES the step-back; the burst
above and the Active-entry cradle (`GetBall()?.CradleForShotStartup`) remain
the only things that actually move the player and the ball. StepBackTest's
`step-back-gathers` scenario asserts behaviour this file cannot reach.
"""

import math
import os
import sys

import bpy
from mathutils import Matrix

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import blender_anim_lib as lib  # noqa: E402  (must follow the sys.path fix)

log = lib.log

# ── clip contract, 60 Hz (StepBack.DefaultFrameData) ─────────────────────────
FPS = 60
STARTUP_TICKS = 7
ACTIVE_TICKS = 4
RECOVERY_TICKS = 8
TOTAL_TICKS = STARTUP_TICKS + ACTIVE_TICKS + RECOVERY_TICKS  # 19

F0 = 0
F1 = TOTAL_TICKS  # inclusive -- frame numbers ARE physics ticks

# Segment boundaries as frame numbers, named once so the gates below and
# rebuild_stepback_clips.gd's slice windows cannot drift apart silently.
STARTUP_END = STARTUP_TICKS                # 7
ACTIVE_END = STARTUP_TICKS + ACTIVE_TICKS  # 11

ACTION_NAME = "stepback"

# ── stance geometry, metre-denominated ────────────────────────────────────────
# NEUTRAL_HIP_TO_ANKLE_M / STANCE_HALF_WIDTH_M / STANCE_HALF_DEPTH_M reused
# verbatim from author_retreatdribble.py's measurement on this SAME rig (Y
# Bot: femur/tibia/foot are rig-intrinsic, independent of the source clip).
NEUTRAL_HIP_TO_ANKLE_M = 0.62
STANCE_HALF_WIDTH_M = 0.12
STANCE_HALF_DEPTH_M = 0.10

# MEASURED HEADROOM against the two-link leg reach budget (femur 0.4060 +
# tibia 0.4210 = 0.8270 m, probed on Dribble.fbx -- rig-intrinsic, reused from
# author_retreatdribble.py's own measurement). The worst target in the table
# below is the LEAD (front) ankle at Active's end: 0.40 m fore (0.10 stance
# depth + 0.30 drift), 0.12 m lateral, 0.53 m down (0.62 neutral drop - 0.09
# up-clearance) -- 0.6748 m of reach, 81.6% of budget, 0.152 m of headroom.
# `plant_foot`'s own `report_ankle_ik` catches anything this comment gets
# wrong.

# Torso pitch sign, SAME CONVENTION as author_retreatdribble.py: a positive
# rotation is BACKWARD (counter-rotating off the source's own forward crouch).
# NOT assumed inherited -- `_torso_pitch_sign_is_backward` below re-derives it
# independently on THIS clip's own body_right/forward axes, because
# author_contest.py's docstring records that its own initial sign guess was
# wrong on this same rig, and a wrong sign here would ship a step-back that
# leans further INTO the defender at the exact instant it is supposed to
# explode away -- the ADR-0003 false read this whole campaign exists to close.
TORSO_PITCH_SIGN = 1.0

# ── keypose channel table ─────────────────────────────────────────────────────
# Columns:
#   time_s, label,
#   hip_offset_m      (+up, VERTICAL delta off the fixed hips_base anchor --
#                      no fore/aft channel anywhere in this table; see the
#                      module docstring's "AUTHORED IN PLACE" section)
#   torso_back_deg    (magnitude of the BACKWARD counter-rotation off the
#                      source's own ~30 deg forward crouch; TORSO_PITCH_SIGN
#                      supplies the sign)
#   front_fore_m, front_up_m   (LEAD (right) foot: forward offset / vertical
#                               clearance off its base spot)
#   rear_fore_m, rear_up_m     (TRAIL (left) foot: same, off its own base spot
#                               -- UNLIKE retreat dribble, this foot DOES get
#                               a vertical channel: it leaves the ground too)
#   arm_fore_m, arm_lat_m, arm_height_m  (BOTH hands -- mirrored)
_KEYPOSES_RAW = [
    # t_s,               label,      hip_off, back, fr_fore, fr_up, re_fore, re_up, arm_fore, arm_lat, arm_h
    # Frame 0 -- entry, hard-cut from the dribble stance (no xfade on any
    # edge). Barely adjusted yet -- the sell has not started.
    [0.00000,             "startup",  -0.02,   3.0,  0.03,    0.00,  -0.01,   0.00,  0.06,     0.14,    0.02],
    # Frame 7 -- the Startup/Active SLICE BOUNDARY: simultaneously the last
    # frame of `stepbackstartup` and the first of `stepbackactive`. THE LIE,
    # fully sold: hips dropped into a hard plant (-0.12, matches the spec's
    # "~0.12 m"), torso pitched in to ~28 deg absolute forward (back=2 off the
    # ~30 deg baseline -- close to the spec's "~25 deg," and deliberately kept
    # a couple degrees short of that target rather than exact, so Startup's
    # own end pose stays clearly separated from Recovery's settled ~12 deg
    # forward lean; see G3 in rebuild_stepback_clips.gd), the LEAD foot
    # planted hard forward (reads as "hips shifted forward over that foot" --
    # see the docstring, this is jab step's technique, not a hip translation),
    # the TRAIL foot light and starting to unweight. Every cue points
    # forward, per the spec.
    [STARTUP_END / FPS,   "active",   -0.12,   2.0,  0.16,    0.00,  -0.06,   0.02,  0.05,     0.15,    0.00],
    # Frame 11 -- the Active/Recovery boundary: the apex of the explosive
    # jump backward. Hips risen well past the spec's "~0.10 m" (net +0.17 m
    # off Startup's own end), torso countered to square-to-the-rim (back=30
    # off the ~30 deg baseline lands close to 0, i.e. near vertical -- see
    # `_verify_torso_band_at_active_end`, NOT past vertical, which would read
    # as the fadeaway). BOTH feet drifted forward relative to the hips AND
    # cleared the ground -- the in-place depiction of "hips travel back" (see
    # the docstring's Correction 2) plus the genuine vertical liftoff the
    # spec calls for. The LEAD foot (which had all the weight) drifts and
    # clears the most; the TRAIL foot, lighter throughout, clears higher
    # (kicking through) but drifts less.
    [ACTIVE_END / FPS,    "recovery", 0.05,    30.0, 0.30,    0.09,  0.18,    0.11,  -0.04,    0.12,    0.05],
    # Frame 19 -- landed WIDE (front and rear straddle their base spots in
    # OPPOSITE directions, a genuinely widened stance, not a re-squared one),
    # hips low, torso settled toward square but not fully vertical (back=18,
    # ~12 deg residual forward -- see the module docstring's "RECOVERY ->
    # JUMPSHOT HAND-OFF" section for why this number is tuned against
    # jumpshotstartup rather than picked in isolation: jumpshotstartup's own
    # opening pose measures a genuinely forward-leaning torso and low, forward
    # hands, i.e. a shooter already settling into the load, not standing bolt
    # upright), ball drawn up to gather height for the shot that follows.
    # front_fore/rear_fore are DELIBERATELY more modest than an
    # early draft (0.20/-0.16) -- rebuild_stepback_clips.gd's G6 measured that
    # draft's RightFoot landmark jumping 0.63 m into jumpshotstartup's opening
    # stance (a visible foot-teleport at the hand-off); this narrower spread
    # still reads as "wider than the Startup plant" while landing close enough
    # to jumpshotstartup's own foot placement not to snap. arm_fore/arm_height
    # are likewise nudged toward jumpshotstartup's own opening hand position
    # (measured hands-relative-to-hips: forward and BELOW hip height, not
    # tucked high against the chest) -- see the "RECOVERY -> JUMPSHOT
    # HAND-OFF" section; a small residual gap is expected and accepted rather
    # than chased to zero, since Recovery also has to read correctly on its
    # OWN (e.g. the RetreatDribbleRecovery-style exit back to Locomotion/
    # Dribble), not solely as a mirror of jumpshotstartup's first frame.
    [TOTAL_TICKS / FPS,   "recovery", -0.09,   18.0, 0.11,    0.00,  -0.08,   0.00,  0.08,     0.10,   -0.02],
]

_CHANNEL_NAMES = (
    "hip_offset_m", "torso_back_deg", "front_fore_m", "front_up_m",
    "rear_fore_m", "rear_up_m", "arm_fore_m", "arm_lat_m", "arm_height_m",
)

# Elbow bend-plane hints, mirrored per side (up + outward). Same pattern as
# author_retreatdribble.py/author_jabstep.py.
ELBOW_HINT_UP = 0.3
ELBOW_HINT_LAT = 0.6

# ── proof thresholds ──────────────────────────────────────────────────────────
# Startup-end(f7)-vs-Recovery-end(f19) legibility floor (#296). Matches the
# other scripts' 15.0 deg floor.
STARTUP_END_VS_RECOVERY_END_MIN_DEG = 15.0

# The torso BAND at Active's end (see the docstring's "THE TORSO BAND"
# section) -- symmetric around true vertical, unlike retreat dribble's
# one-sided "at or past vertical" gate. 0.05 m is a modest allowance (the
# authored target counter-rotates 30 deg off a ~30 deg baseline, so the
# expected reading is within a centimetre or two of zero); if a retune lands
# outside this band on EITHER side, adjust torso_back_deg at Active's row --
# do not widen this band, because which side of (near-)vertical the chest
# sits on is exactly the distinction that keeps this move visually separate
# from the fadeaway (#318).
TORSO_BAND_AT_ACTIVE_M = 0.05

# Both feet must end Active measurably FORWARD of where they started
# Active (frame STARTUP_END, not F0 -- the interesting drift is Active's
# own event, per the docstring), relative to the hips. Reduced with MIN,
# never MAX (README trap 17): the claim is BOTH feet were left behind.
# Floor well under the table's authored front=0.14 m / rear=0.24 m deltas.
FEET_DRIFT_MIN_M = 0.06

# Both feet must ALSO clear a minimum HEIGHT above their own base spot by
# Active's end -- the positive statement of "both feet leave the ground"
# (see the docstring's "AIRBORNE, NOT GROUNDED" section). Reduced with MIN,
# never MAX, for the identical README trap 17 reason. Floor well under the
# table's authored front=0.09 m / rear=0.11 m.
FEET_LIFT_MIN_M = 0.04

# verify_airborne's companion floor: the Hips must rise measurably above
# their OWN Startup-end (grounded) height. Table delta is -0.12 -> +0.05 =
# 0.17 m; floor is under half that.
HIP_RISE_MIN_M = 0.06


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

    Identical helper to author_retreatdribble.py's own -- see that file's
    docstring for why this one quantity is measured three independent times
    across the pipeline (Blender-side here, resource-side in
    rebuild_stepback_clips.gd, live-rig in StepBackAnimTest).
    """
    spine_head = arm.pose.bones[lib.SPINE].head.copy()
    head_head = arm.pose.bones["mixamorig:Head"].head.copy()
    return geom.to_m((head_head - spine_head).dot(forward))


def _torso_pitch_sign_is_backward(arm, geom, body_right, forward):
    """A positive `TORSO_PITCH_SIGN` rotation must tip the torso BACKWARD.

    Verbatim technique from author_retreatdribble.py's own oracle: rotate the
    spine->head vector by the signed pitch at a single frame (no baking, no
    two-frame comparison, so the source clip's own drift cannot contaminate
    the reading) and check the forward component SHRANK.
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
            f"FORWARD. A step-back's torso must counter-rotate BACKWARD off the "
            f"dribble crouch to reach 'upright and squares to the rim' by Active's "
            f"end (handoff 06). Flip TORSO_PITCH_SIGN.")


def _verify_torso_band_at_active_end(arm, geom, forward):
    """At Active's end the chest sits WITHIN a band of true vertical.

    The two-sided version of author_retreatdribble.py's one-sided
    `_verify_torso_at_or_past_vertical`. Retreat dribble only had to rule out
    leaning IN (past vertical was fine -- more separation, still readable).
    Step-back must rule out BOTH sides: leaning forward is the same "driving
    in" false read, and leaning back is FadeawayActive's own territory
    (handoff 06 / #318) -- landing there would make a step-back visually
    indistinguishable from a shot that has not happened yet.
    """
    with lib.preserve_frame():
        bpy.context.scene.frame_set(ACTIVE_END)
        fore_m = _spine_head_forward_m(arm, geom, forward)
    lib.report("torso_forward_at_active_end_m", f"{fore_m:+.4f}")
    if abs(fore_m) > TORSO_BAND_AT_ACTIVE_M:
        side = "FORWARD (driving in)" if fore_m > 0 else "BACKWARD (reads as a fadeaway)"
        raise SystemExit(
            f"FATAL: at Active's end (frame {ACTIVE_END}) the spine->head vector "
            f"projects {fore_m:+.4f} m off vertical (band +/-{TORSO_BAND_AT_ACTIVE_M:.2f}), "
            f"i.e. leaning {side}. Handoff 06: 'torso comes upright and squares to "
            f"the rim -- deliberately NOT leaning back' -- retune torso_back_deg at "
            f"the Active row (currently landing on the wrong side of vertical, or "
            f"too far off it either way). Do NOT widen this band; which side of "
            f"vertical the chest sits on is what keeps this move visually distinct "
            f"from the fadeaway (#318).")


def _verify_hips_stay_in_place(arm, geom, hips_base, up, forward, body_right, frames):
    """The Hips translate along `up` ONLY -- never fore/aft, never laterally.

    Verbatim from author_retreatdribble.py, reused across the WHOLE clip
    (not narrowed to Active) -- see the module docstring's "AUTHORED IN
    PLACE" section for why one rule covers both the Startup "sell it" lie
    and the Active "don't double the burst" requirement. Zero by construction
    today (`apply()` builds the Hips target as `hips_base + up * hip_offset_m`,
    no fore/aft term exists to be nonzero) -- the gate's job is refusing a
    FUTURE edit that adds one, exactly as it does in the sibling script.
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
            f"applies StepBackBurstSpeed via StepBackBurstMath on JustEnteredActive, "
            f"so root translation here double-counts the burst and slides the mesh "
            f"off its collider. Express both the Startup weight-shift and the Active "
            f"burst as the FEET moving relative to the hips, never as hip translation.")


def _verify_both_feet_drift_forward(arm, geom, forward, frame_ref, frame_active_end):
    """BOTH ankles end Active further forward, relative to the hips, than at Active's start.

    Horizontal counterpart to `_verify_both_feet_leave_ground` below --
    together the two prove the in-place depiction of "hips travel back ~0.5 m
    and up ~0.1 m" (handoff 06's Active line): the base is left behind AND
    the body clears the floor. `frame_ref` is STARTUP_END, not F0 -- unlike
    retreat dribble (where the whole clip IS the retreat), here the
    interesting drift is Active's own event, so the reference is Active's
    own start pose, not the pre-move dribble stance.

    REDUCED WITH `min`, NEVER `max` (README trap 17) -- see
    author_retreatdribble.py's identical gate for the full reasoning.
    """
    scene = bpy.context.scene

    def ankles_rel_hips(frame):
        scene.frame_set(frame)
        hips = arm.pose.bones[lib.HIPS].head.copy()
        out = {}
        for side in ("L", "R"):
            ankle = arm.pose.bones[lib.LEG_CHAIN[side][2]].head.copy()
            out[side] = geom.to_m((ankle - hips).dot(forward))
        return out

    with lib.preserve_frame():
        at_ref = ankles_rel_hips(frame_ref)
        at_active = ankles_rel_hips(frame_active_end)

    drift = {s: at_active[s] - at_ref[s] for s in ("L", "R")}
    lib.report("foot_drift_trail_L_m", f"{drift['L']:+.4f}")
    lib.report("foot_drift_lead_R_m", f"{drift['R']:+.4f}")
    worst = min(drift["L"], drift["R"])
    lib.report("foot_drift_min_m", f"{worst:+.4f}")
    if worst < FEET_DRIFT_MIN_M:
        loser = "trail (L)" if drift["L"] < drift["R"] else "lead (R)"
        raise SystemExit(
            f"FATAL: the {loser} ankle drifted only {worst:+.4f} m forward "
            f"relative to the hips between frames {frame_ref} and {frame_active_end} "
            f"(floor {FEET_DRIFT_MIN_M}). BOTH feet must be left behind by the "
            f"exploding-backward body (L={drift['L']:+.4f} R={drift['R']:+.4f}; "
            f"reduced with min, never max -- README trap 17).")


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
    # author_retreatdribble.py / author_jabstep.py use, so the cross-move
    # contrast lives in what the body DOES, never in which leg is which.
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

    # Latched at Active's end for _verify_both_feet_leave_ground's caller
    # below -- captured inside apply() because that is the only place the
    # per-frame ankle targets (base + channel offsets) are assembled; reading
    # them back out of the posed bones after baking would re-derive the same
    # numbers through an extra IK round-trip for no benefit.
    lift_at_active_end = {}

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

        # ---- legs: BOTH feet channelled, BOTH get a vertical channel --------
        # Anchored to `hips_base`, not `hips_now` -- a planted move keeps the
        # FLOOR fixed and lets the hips move relative to it (author_contest.py's
        # lesson: anchoring to `hips_now` made a crouch lift the feet by
        # exactly the crouch depth).
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

        if frame == ACTIVE_END:
            lift_at_active_end["R"] = ch["front_up_m"]
            lift_at_active_end["L"] = ch["rear_up_m"]

        # ---- arms: ONE set of channels, mirrored per side --------------------
        # Ball gathered and protected close to the body -- this move is
        # unhanded (no shooting-side polarity encoded, see the docstring), so
        # the channels stay symmetric across both hands, the same
        # simplification retreat dribble and jab step both make.
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

    # ── the burst itself, proved from independent angles ─────────────────────
    _torso_pitch_sign_is_backward(arm, geom, body_right, forward)     # right way
    _verify_torso_band_at_active_end(arm, geom, forward)              # far enough, not too far
    _verify_hips_stay_in_place(arm, geom, hips_base, up, forward, body_right, all_frames)
    _verify_both_feet_drift_forward(arm, geom, forward, STARTUP_END, ACTIVE_END)

    # "Both feet leave the ground" -- the move-specific positive claim,
    # reduced with min (README trap 17), plus the generic verify_airborne
    # proof jumpshot/block/layup already carry (see the docstring's
    # "AIRBORNE, NOT GROUNDED" section for why verify_grounded does not apply
    # here at all).
    worst_lift = min(lift_at_active_end["L"], lift_at_active_end["R"])
    lib.report("foot_lift_trail_L_m", f"{lift_at_active_end['L']:+.4f}")
    lib.report("foot_lift_lead_R_m", f"{lift_at_active_end['R']:+.4f}")
    lib.report("foot_lift_min_m", f"{worst_lift:+.4f}")
    if worst_lift < FEET_LIFT_MIN_M:
        loser = "trail (L)" if lift_at_active_end["L"] < lift_at_active_end["R"] else "lead (R)"
        raise SystemExit(
            f"FATAL: the {loser} ankle cleared only {worst_lift:+.4f} m above its "
            f"base spot at Active's end (floor {FEET_LIFT_MIN_M}). Handoff 06: 'both "
            f"feet leave the ground' -- reduced with min, never max (README trap 17).")

    with lib.preserve_frame():
        scene.frame_set(STARTUP_END)
        ref_hip_height = arm.pose.bones[lib.HIPS].head.dot(up)
    lib.verify_airborne(arm, list(range(STARTUP_END, ACTIVE_END + 1)), HIP_RISE_MIN_M, geom,
                        ref_height=ref_hip_height)

    # ── #296 legibility ───────────────────────────────────────────────────────
    lib.verify_pose_distinct(
        lib.snapshot_pose(arm, STARTUP_END), lib.snapshot_pose(arm, F1),
        STARTUP_END_VS_RECOVERY_END_MIN_DEG, label="startup_end_vs_recovery_end")

    lib.export_fbx(arm, dst, ACTION_NAME)
    log(f"wrote {dst}")


if __name__ == "__main__":
    main()
