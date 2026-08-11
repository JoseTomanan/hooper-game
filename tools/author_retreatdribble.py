"""Author `retreatdribble` as a single-polarity keypose clip in headless Blender (#305).

Run:
    "$BLENDER" --background --python-exit-code 1 \
        --python tools/author_retreatdribble.py -- <src.fbx> <out.fbx>

`--python-exit-code 1` is MANDATORY. Blender's `--background --python` exits 0
even on an uncaught traceback, so without it a crashed authoring run reports
success to the pipeline.

===============================================================================
JAB STEP'S TWIN -- AND THE ONLY READ THAT SURVIVES IS THE TORSO LEAN SIGN
===============================================================================
RetreatDribble.DefaultFrameData is Startup=3 / Active=2 / Recovery=4 ticks @
60 Hz -- 9 ticks, 0.150 s total, IDENTICAL to JabStep, off the IDENTICAL source
(assets/Dribble.fbx), with the identical three-held-poses structure (README's
"the <=3-tick segments are single poses" rule). Handoff 05 states the
consequence bluntly: author the two independently and the game ships two
indistinguishable moves.

    Jab step's torso pitches FORWARD over an extended front foot.
    Retreat dribble's torso stays upright-to-back over a base moving away.

So this script is deliberately `author_jabstep.py` with the lean inverted and
the leg roles rebalanced, and every constant below that differs from that file
differs ON PURPOSE. Read the two side by side before retuning either.

===============================================================================
THE SPEC'S TORSO NUMBER IS ABSOLUTE, JAB STEP'S IS RELATIVE -- MEASURED
===============================================================================
This is the one place the two specs are not symmetric, and reading it wrong
produces a clip that leans forward while claiming to lean back.

Handoff 04 says the jab's torso "pitches forward ~10 deg FROM THE DRIBBLE
STANCE" -- a relative adjustment, which is exactly what
`lib.rotate_bone_about_head` composes. Handoff 05 says this move's torso is
"vertical to 5 deg back" -- an ABSOLUTE claim about where the chest ends up.

`assets/Dribble.fbx` is a crouching dribble whose torso is ALREADY well forward.
MEASURED on the source over frames 1..12 (spine->head vector, 0.5047 m long,
projected on the rest-derived `forward`/`up` axes):

    frame  1   fore=+0.2447 m   up=+0.4272 m   tilt off vertical = +29.81 deg
    frame  6   fore=+0.2483 m   up=+0.4233 m                       +30.40 deg
    frame 12   fore=+0.2517 m   up=+0.4243 m                       +30.68 deg

So "vertical to 5 deg back" requires counter-rotating roughly 30-35 deg, NOT
the 5 deg the sentence names. `TORSO_BACK_DEG` in the keypose table below is
therefore a RELATIVE counter-rotation sized to land on an ABSOLUTE target, and
`_verify_torso_at_or_past_vertical` re-measures the absolute result rather than
trusting the arithmetic -- the gate is on the claim, not on the input.

The happy side effect is that this move's signal is large. Jab step measures
+0.1356 m of forward growth by Active's end; this clip travels roughly -0.29 m
from the same baseline. The "opposite sign" contrast is not a marginal
few-centimetre call at 0.15 s -- it is the biggest single pose difference
between any two clips in the batch.

===============================================================================
AUTHORED IN PLACE -- THE GAME ALREADY MOVES THE CHARACTER
===============================================================================
Handoff 05's motion spec says Active has "hips displaced back ~0.25 m". That
number must NOT reach a translation channel.

`PlayerController.cs:3919-3920` already sets, on `JustEnteredActive`:

    Vector2 backward = -HeadingMath.Forward(Heading);
    Velocity = new Vector3(backward.X, 0f, backward.Y) * RetreatDribbleBurstSpeed;

with `RetreatDribbleBurstSpeed` an [Export] defaulting to 4.0 m/s. The GAME
moves the body. A clip that ALSO translates the root backward plays the retreat
twice and slides the mesh off its own collider.

The in-place way to depict a retreat is to invert the frame of reference: hold
the hips as the fixed anchor (VERTICAL offset only, exactly as
`author_jabstep.py` holds its own `hip_offset_m`) and let the FEET drift
FORWARD relative to them during Active -- the body has left its base behind --
then swing back under the hips to re-plant during Recovery. Same channel
structure as the jab, opposite cause, and BOTH feet rather than one.

`_verify_hips_stay_in_place` asserts this structurally rather than leaving it to
a reader to notice: the Hips head may move along `up` and nowhere else. It is
zero by construction today (`apply()` builds the target as
`hips_base + up * hip_offset_m`), which is the point -- the gate exists so that
a future edit adding a fore/aft hip channel fails the authoring run instead of
shipping a double-counted retreat.

Memory `project_clip_space_vs_world_burst_translation` generalises this to
#306 step-back, #311 drive-gather and #312 euro-step, which burst on the same
`JustEnteredActive` branch.

===============================================================================
BOTH FEET GET CHANNELS -- UNLIKE THE JAB
===============================================================================
`author_jabstep.py` hard-codes its PLANT foot as a fixed ankle target with no
channel at all, precisely so "the rear foot stays planted" cannot be retuned
into a violation. That technique needs an invariant to protect, and this move
has no such invariant: handoff 05 has the front foot going toe-down and
unweighted while the rear foot receives the weight, and BOTH re-plant in
Recovery. So both legs are channelled here.

Leg ROLES are kept identical to the jab's on purpose -- RIGHT is the front foot,
LEFT is the rear -- so the contrast between the two clips lives entirely in what
the body does, never in which limb is which. That is what makes "opposite lean
sign" the honest discriminator rather than an artefact of mirrored staging.

SIMPLIFICATION, STATED RATHER THAN HIDDEN: the spec's "front foot toe-down" is
authored as the front ANKLE lifting slightly (`front_up_m`) over one shared
`toe_dir`, not as a per-foot toe rotation channel. A raised ankle over a
down-pointing toe reads as an unweighted foot at 0.033 s, and README's bar is
legibility, not fidelity. A genuine toe-down would need a second `toe_dir`
channel; it buys nothing at two rendered frames.

===============================================================================
THE RECOVERY POSE IS BALANCED, AND IT IS NOT A RETURN TO NEUTRAL
===============================================================================
Two deliberate asymmetries with the rest of the batch, both from handoff 05:

BALANCED, NOT PUNISHED. Most recovery poses in this batch are authored
off-balance because the recovery is a punish window. A retreat dribble is a
RESET that buys space, and its 4-tick recovery is barely a window at all, so
Recovery ends on both feet with the rear leg loaded and the chest square --
"ready to go again".

NOT A RETURN TO NEUTRAL. The jab's foot comes back to its base spot; this
character has genuinely retreated. Recovery is therefore authored as a NEW
stance -- markedly lower (hips -0.11 m vs Startup's -0.03 m) and stretched wider
fore-to-aft -- rather than as the Startup stance re-entered.

That second point is also what keeps the #296 legibility floor honest. An
earlier draft of this table brought Recovery's feet and hips back near Startup's
and left the torso as the only difference: 5 deg on one bone, which would have
squeaked past `verify_pose_distinct` (whole-clip endpoints) while FAILING
`rebuild_retreatdribble_clips.gd`'s G3, which compares the instants that
actually matter -- Startup's own END pose against Recovery's own END pose.
`_verify_startup_end_differs_from_recovery_end` below re-proves G3's exact
comparison at authoring time, where it is cheap, instead of discovering it two
tools downstream.

===============================================================================
WHAT THIS PRODUCES
===============================================================================
ONE Blender action, baked at 60 Hz, frame numbers ARE physics ticks:

    frames    seconds              segment
    0  -> 3   0.00000 -> 0.05000   Startup   (3 ticks -- the weight shift back)
    3  -> 5   0.05000 -> 0.08333   Active    (2 ticks -- the push-off)
    5  -> 9   0.08333 -> 0.15000   Recovery  (4 ticks -- the loaded reset)

===============================================================================
UNHANDED
===============================================================================
Handoff 05: handedness is No. The ball never changes hands (RetreatDribble.cs's
class doc: "No gather: the ball stays Dribbling throughout"), so this clip
commits to ONE fixed polarity and must NOT be added to
`MoveAnimResolver.HandedMoves` -- README trap 4 does not apply because there is
no second polarity to mistime.

===============================================================================
COSMETIC-ONLY
===============================================================================
This script is an ASSET BUILD TOOL. It does not import, read, or feed
`RetreatDribbleBurstSpeed`, `BallState`, `HasDribbled`, or any PlayerController
move-begin gate. It VISUALISES the retreat; the burst above remains the only
thing that moves the player. In particular `StepBackTest`'s
`retreat-dribble-no-gather` and `retreat-dribble-dead-dribble-gate` scenarios
assert behaviour this file cannot reach.
"""

import math
import os
import sys

import bpy
from mathutils import Matrix

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import blender_anim_lib as lib  # noqa: E402  (must follow the sys.path fix)

log = lib.log

# ── clip contract, 60 Hz (RetreatDribble.DefaultFrameData) ───────────────────
FPS = 60
STARTUP_TICKS = 3
ACTIVE_TICKS = 2
RECOVERY_TICKS = 4
TOTAL_TICKS = STARTUP_TICKS + ACTIVE_TICKS + RECOVERY_TICKS  # 9

F0 = 0
F1 = TOTAL_TICKS  # inclusive -- frame numbers ARE physics ticks

# Segment boundaries as frame numbers, named once so the gates below and
# rebuild_retreatdribble_clips.gd's slice windows cannot drift apart silently.
STARTUP_END = STARTUP_TICKS                # 3
ACTIVE_END = STARTUP_TICKS + ACTIVE_TICKS  # 5

ACTION_NAME = "retreatdribble"

# ── stance geometry, metre-denominated ────────────────────────────────────────
# NEUTRAL_HIP_TO_ANKLE_M / STANCE_HALF_WIDTH_M reused verbatim from
# author_contest.py / author_jabstep.py's measurement on this SAME rig (Y Bot:
# femur/tibia/foot are rig-intrinsic, independent of the source clip).
NEUTRAL_HIP_TO_ANKLE_M = 0.62
STANCE_HALF_WIDTH_M = 0.12

# Fore/aft split of the BASE stance. The jab needs no such constant -- it
# authors a square base and stabs one foot out of it -- but this move's spec
# distinguishes a "front foot" from a "rear foot" from the very first frame
# (the front unweights, the rear receives the weight), so the two need distinct
# base spots to be offset FROM.
STANCE_HALF_DEPTH_M = 0.10

# MEASURED HEADROOM, so the numbers in the keypose table can be read as safe
# rather than hoped-safe. `report_ankle_ik` FAILS the run above
# ANKLE_IK_TOL_M = 1e-4 m, and `plant_foot` CLAMPS an out-of-reach target rather
# than refusing it, so an over-ambitious foot offset surfaces there.
#
# Probed on Dribble.fbx: leg_reach_m = 0.8270 (femur 0.4060 + tibia 0.4210).
# The worst target in the table below is the front ankle at Active's end --
# 0.32 m fore, 0.12 m lateral, 0.52 m below the hips -- which is 0.622 m of
# reach, i.e. 75% of the budget. The handoff's estimate treated
# NEUTRAL_HIP_TO_ANKLE_M itself as the ceiling and warned that 0.25 m was
# "pushing it"; it is not. 0.62 is the vertical DROP, and the budget left over
# for horizontal offset at that drop is sqrt(0.827^2 - 0.62^2) = 0.547 m.
# The table stays well inside anyway, because the constraint that actually
# binds here is legibility (a base stretched too far reads as a stumble, not a
# retreat), not the solver.

# Torso pitch sign. A retreat dribble's chest goes UPRIGHT-TO-BACK -- handoff
# 05's defining contrast with the jab's forward pitch over an extended foot.
#
# NOT assumed to be the negation of author_jabstep.py's -1.0, even though it is.
# `_torso_pitch_sign_is_backward` below re-derives it independently on THIS
# clip's own body_right/forward axes, because author_contest.py's docstring
# records that its own initial sign guess was wrong, and a guessed sign here
# would ship a retreat dribble that leans INTO the defender -- the exact
# ADR-0003 false read the per-move clip campaign exists to close.
TORSO_PITCH_SIGN = 1.0

# ── keypose channel table ─────────────────────────────────────────────────────
# Columns:
#   time_s, label,
#   hip_offset_m      (+up, VERTICAL delta off the fixed hips_base anchor --
#                      there is deliberately no fore/aft hip channel; see the
#                      module docstring's "AUTHORED IN PLACE" section and
#                      `_verify_hips_stay_in_place`)
#   torso_back_deg    (magnitude of the BACKWARD counter-rotation off the
#                      source's own ~30 deg forward crouch; TORSO_PITCH_SIGN
#                      supplies the sign)
#   front_fore_m      (the FRONT (right) foot's forward offset off its base spot)
#   front_up_m        (the FRONT foot's vertical clearance -- the unweighting)
#   rear_fore_m       (the REAR (left) foot's forward offset off its base spot)
#   arm_fore_m, arm_lat_m, arm_height_m  (BOTH hands -- mirrored)
#
# The REAR foot has no vertical channel: it is the weight-bearing foot for the
# whole move (spec: "rear foot receiving the weight", "rear foot loaded"), so
# a value that cannot exist cannot be retuned into a violation -- the same
# structural technique author_jabstep.py uses for its planted rear foot, applied
# to the one axis this move genuinely does constrain. It is also what makes
# `lib.verify_grounded` meaningful below: the lower of the two toes is the rear
# one at every frame, so the band it measures is a real claim about this clip
# containing NO hop, rather than an artefact of both feet happening to be down.
_KEYPOSES_RAW = [
    # t_s,               label,      hip_off, back,  fr_fore, fr_up, re_fore, arm_fore, arm_lat, arm_h
    # Frame 0 -- entry, hard-cut from the dribble stance (no xfade on any edge).
    # Already rising out of the crouch: 12 deg back off ~30 deg leaves the chest
    # ~18 deg forward, which reads as "he is coming up", not as a new stance.
    [0.00000,            "startup",  -0.01,   12.0,  0.02,    0.00,  0.01,    0.08,     0.15,    0.02],
    # Frame 3 -- the Startup/Active SLICE BOUNDARY: simultaneously the last
    # frame of `retreatdribblestartup` and the first of `retreatdribbleactive`.
    # Three ticks is all the wind-up there is, so the tell has to be fully
    # readable here: chest essentially SQUARE AND VERTICAL (25 back off ~30
    # leaves ~5 deg forward), hips sitting down, front foot beginning to
    # unweight. Explicitly not the jab's forward pitch (handoff 05).
    [STARTUP_END / FPS,  "active",   -0.03,   25.0,  0.06,    0.02,  0.03,    0.02,     0.16,    0.01],
    # Frame 5 -- the Active/Recovery boundary. THE PUSH-OFF, and the only frame
    # where the retreat itself is visible: the chest is now PAST vertical
    # (35 back off ~30 leaves ~5 deg BACK, the spec's number), the front foot
    # has been left 0.22 m ahead of its base and lifted off, and the rear foot
    # has swung to +0.14 -- i.e. directly under the hips, receiving the weight.
    # In world space the game is simultaneously carrying the body backward at
    # RetreatDribbleBurstSpeed; this frame is that same event drawn in the
    # body's own frame of reference.
    [ACTIVE_END / FPS,   "recovery", -0.05,   35.0,  0.22,    0.05,  0.14,   -0.05,     0.17,   -0.01],
    # Frame 9 -- the loaded reset. NOT the Startup stance re-entered (see the
    # docstring): hips 0.08 m lower than Startup's own end, the base stretched
    # fore-to-aft (front +0.14, rear +0.02) rather than re-squared, chest back
    # to square. Both feet down, rear leg loaded, ready to go again -- BALANCED,
    # which is the deliberate asymmetry with the rest of the batch.
    [TOTAL_TICKS / FPS,  "recovery", -0.11,   30.0,  0.14,    0.00,  0.02,   -0.03,     0.19,   -0.03],
]

_CHANNEL_NAMES = (
    "hip_offset_m", "torso_back_deg", "front_fore_m", "front_up_m", "rear_fore_m",
    "arm_fore_m", "arm_lat_m", "arm_height_m",
)

# Elbow bend-plane hints, mirrored per side (up + outward). One fixed hint per
# arm across the whole timeline, same pattern as author_jabstep.py: it only has
# to avoid being exactly parallel to the reach direction, which a shallow
# up-and-outward hint safely is for a low, near-hip hand target.
ELBOW_HINT_UP = 0.3
ELBOW_HINT_LAT = 0.6

# ── proof thresholds ──────────────────────────────────────────────────────────
# Startup(f0)-vs-Recovery(f9) legibility floor (#296). Matches the other
# scripts' 15.0 deg floor.
POSE_DISTINCT_MIN_DEG = 15.0

# The SAME floor applied to the comparison rebuild_retreatdribble_clips.gd's G3
# actually makes -- Startup's own END pose (frame 3) against Recovery's own END
# pose (frame 9). See the docstring: this is the strictly harder comparison, and
# the one an earlier draft of the table failed while passing the endpoint one.
STARTUP_END_VS_RECOVERY_END_MIN_DEG = 15.0

# The character never leaves the ground IN THE CLIP. The hop is the game's
# (RetreatDribbleBurstSpeed), and it is horizontal; a vertical excursion here
# would be this clip inventing a jump the tick loop never applies.
GROUND_BAND_TOL_M = 0.02

# Absolute-torso gate: at Active's end the spine->head vector's projection along
# `forward` must be AT OR PAST vertical, i.e. <= 0. Stated as the spec states
# it ("torso vertical to 5 deg back") rather than as a slack band, because the
# whole content of this clip is which SIDE of vertical the chest is on. If a
# retune lands this slightly positive, raise `torso_back_deg` -- do not raise
# this number.
TORSO_FORWARD_MAX_AT_ACTIVE_M = 0.0

# Both feet must end Active measurably FORWARD of where they started, relative
# to the hips -- the in-place expression of "the body has retreated out from
# under its base" (see the docstring). Reduced with MIN across the pair, never
# max: the claim is that BOTH feet were left behind, and README trap 17 is the
# record of a both-limbs gate that a one-limbed clip satisfied because it
# reduced with max. Floor is well under the table's authored 0.20 m (front) and
# 0.13 m (rear) so it catches a collapse, not a retune.
FEET_DRIFT_MIN_M = 0.06


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

    The single quantity every torso gate in this pipeline measures --
    Blender-side here, resource-side in `rebuild_retreatdribble_clips.gd`'s G4,
    and live-rig in `RetreatDribbleAnimTest.MeasureSpineHeadForward`. Three
    independent re-measurements of one claim, which is the point: the FBX
    round-trip and the slice are exactly the machinery that has silently
    corrupted clips in this repo before (#281, #295, #297).

    Positive is FORWARD. On the untouched source this reads +0.2447 m (a
    +29.81 deg crouch); at absolute vertical it is 0.0.
    """
    spine_head = arm.pose.bones[lib.SPINE].head.copy()
    head_head = arm.pose.bones["mixamorig:Head"].head.copy()
    return geom.to_m((head_head - spine_head).dot(forward))


def _torso_pitch_sign_is_backward(arm, geom, body_right, forward):
    """A positive `TORSO_PITCH_SIGN` rotation must tip the torso BACKWARD.

    The mirror of `author_jabstep.py`'s `_torso_pitch_sign_is_forward`, and
    isolated the same way: take the spine->head vector at a single frame, rotate
    it by the signed pitch (no baking, no two-frame comparison), and check the
    forward component SHRANK. A two-frame comparison would be dominated by the
    source clip's own drift -- probed at +0.87 deg of forward creep across
    frames 1..12 of Dribble.fbx, which is a fifth of the 5 deg this move's spec
    turns on -- and testing the rotation itself at one frame sidesteps it
    entirely.

    Deliberately re-derived rather than inherited as "-1.0 negated". It does
    come out that way, and this function is still here because
    author_contest.py's docstring records that ITS initial sign guess was wrong
    on this same rig, and a wrong sign here is not a broken clip -- it is a
    clean, plausible clip that telegraphs a drive when the player retreated.
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
            f"FORWARD. A retreat dribble's chest goes upright-to-BACK over a base "
            f"moving away -- leaning it in is jab step's read, not this one "
            f"(handoff 05). Flip TORSO_PITCH_SIGN.")


def _verify_torso_at_or_past_vertical(arm, geom, forward):
    """At Active's end the chest is AT OR PAST vertical -- the absolute claim.

    `_torso_pitch_sign_is_backward` proves the rotation goes the right WAY;
    this proves it goes FAR ENOUGH. They are genuinely different failures: the
    source crouch is +29.81 deg forward, so a correctly-signed but undersized
    counter-rotation (say 10 deg) leaves the chest still 20 deg FORWARD -- a
    clip that leans into the defender while every sign gate passes.

    Measured at frame ACTIVE_END, the pose the two-tick Active window resolves
    to and the instant the whole read hangs on.
    """
    with lib.preserve_frame():
        bpy.context.scene.frame_set(ACTIVE_END)
        fore_m = _spine_head_forward_m(arm, geom, forward)
    lib.report("torso_forward_at_active_end_m", f"{fore_m:+.4f}")
    if fore_m > TORSO_FORWARD_MAX_AT_ACTIVE_M:
        raise SystemExit(
            f"FATAL: at Active's end (frame {ACTIVE_END}) the spine->head vector "
            f"still projects {fore_m:+.4f} m FORWARD (max "
            f"{TORSO_FORWARD_MAX_AT_ACTIVE_M:+.4f}). The chest is on the wrong "
            f"side of vertical, so this clip reads as leaning INTO the defender. "
            f"The source crouch is +0.2447 m / +29.81 deg forward, so "
            f"`torso_back_deg` has to counter-rotate roughly 30-35 deg to reach "
            f"the spec's 'vertical to 5 deg back' -- raise it in _KEYPOSES_RAW. "
            f"Do NOT relax this threshold; which side of vertical the chest sits "
            f"on IS the move.")


def _verify_hips_stay_in_place(arm, geom, hips_base, up, forward, body_right, frames):
    """The Hips translate along `up` ONLY -- never fore/aft, never laterally.

    THE trap with this issue's name on it. The game moves the body: on
    `JustEnteredActive`, `PlayerController` sets `Velocity` to 4.0 m/s straight
    back along `Heading` (`RetreatDribbleBurstSpeed`). A clip that also
    translates its root backward plays the retreat TWICE and slides the mesh off
    its collider -- and nothing else in this pipeline would notice, because a
    doubled retreat is a perfectly well-formed clip that binds, resolves, slices
    to length, and passes every pose gate.

    Zero by construction today: `apply()` builds the Hips target as
    `hips_base + up * hip_offset_m`, so there is no horizontal term to be
    nonzero. That is precisely why the gate is worth its lines -- it is not
    checking today's arithmetic, it is refusing the edit that adds a
    `hip_fore_m` channel because handoff 05's motion spec literally says "hips
    displaced back ~0.25 m".

    Tolerance is float noise, not a drift allowance: the quantity is an exact
    zero in exact arithmetic.
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
            f"applies RetreatDribbleBurstSpeed (4.0 m/s backward) on "
            f"JustEnteredActive, so root translation here double-counts the "
            f"retreat and slides the mesh off its collider. Express the retreat "
            f"as the FEET drifting forward relative to the hips "
            f"(front_fore_m / rear_fore_m), never as hip translation.")


def _verify_both_feet_drift_forward(arm, geom, forward, frames_ref, frame_active_end):
    """BOTH ankles end Active further forward, relative to the hips, than at f0.

    The positive half of the in-place retreat: `_verify_hips_stay_in_place`
    proves the root did NOT move, and on its own that is satisfied by a clip
    where nothing moves at all. This proves the retreat is actually depicted --
    the base is left behind the body.

    Measured RELATIVE TO THE HIPS, not in armature space, because the hips
    themselves drop 0.11 m over the move; an absolute ankle reading would mix
    the crouch into the answer.

    REDUCED WITH `min`, NEVER `max` (README trap 17). The claim is that BOTH
    feet were left behind. A `max` reduction is satisfied by the front foot
    alone, which would wave through exactly the defect this gate exists to
    catch -- a rear foot that stayed under the body, i.e. a clip that depicts a
    one-legged kick rather than a whole base being vacated. Both values are
    reported so a one-footed clip is legible in the log rather than failing
    anonymously (the `LocomotionClipTest` #298 stride shape).
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
        at_ref = ankles_rel_hips(frames_ref)
        at_active = ankles_rel_hips(frame_active_end)

    drift = {s: at_active[s] - at_ref[s] for s in ("L", "R")}
    lib.report("foot_drift_rear_L_m", f"{drift['L']:+.4f}")
    lib.report("foot_drift_front_R_m", f"{drift['R']:+.4f}")
    worst = min(drift["L"], drift["R"])
    lib.report("foot_drift_min_m", f"{worst:+.4f}")
    if worst < FEET_DRIFT_MIN_M:
        loser = "rear (L)" if drift["L"] < drift["R"] else "front (R)"
        raise SystemExit(
            f"FATAL: the {loser} ankle drifted only {worst:+.4f} m forward "
            f"relative to the hips between frames {frames_ref} and "
            f"{frame_active_end} (floor {FEET_DRIFT_MIN_M}). BOTH feet must be "
            f"left behind by the retreating body -- this is the in-place "
            f"depiction of the burst, and a single-foot version reads as a kick "
            f"rather than a retreat. (L={drift['L']:+.4f} R={drift['R']:+.4f}; "
            f"reduced with min, never max -- README trap 17.)")


def _verify_startup_end_differs_from_recovery_end(arm):
    """Frame 3 vs frame 9 -- the comparison the rebuild script's G3 makes.

    `lib.verify_pose_distinct` on the whole-clip endpoints (frames 0 and 9) is
    the batch standard, but it is the EASIER comparison and it is not the one
    the pipeline downstream actually enforces:
    `rebuild_retreatdribble_clips.gd`'s G3 compares Startup's own END pose
    against Recovery's own END pose, because those are the instants a defender
    reads (the fully-committed wind-up vs the settled punish window), and
    `RetreatDribbleAnimTest`'s live-rig scenario samples the same two.

    Re-proved here because the failure it catches is a SPEC failure, not a
    machinery failure, and authoring time is where a spec failure is cheap to
    fix. An earlier draft of `_KEYPOSES_RAW` brought Recovery's hips and feet
    back near Startup's and left a 5 deg torso difference as the only delta;
    that draft passes the endpoint comparison (frame 0 is much further from
    frame 9) and fails this one.
    """
    lib.verify_pose_distinct(
        lib.snapshot_pose(arm, STARTUP_END), lib.snapshot_pose(arm, F1),
        STARTUP_END_VS_RECOVERY_END_MIN_DEG, label="startup_end_vs_recovery_end")


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

    # Base spots for the two feet. Unlike author_jabstep.py's square stance,
    # these are staggered fore/aft: this move distinguishes a front foot from a
    # rear foot in every phase, so the roles need distinct anchors rather than
    # being expressed purely as offsets from a shared spot.
    #
    # RIGHT is the FRONT foot and LEFT the REAR -- the same limb assignment
    # author_jabstep.py uses (its RIGHT leg is the jab/front leg), so the two
    # twin clips differ in what the body DOES and never in which leg is which.
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
        # Same fix author_jabstep.py / author_contest.py apply, same reason.
        for side in ("L", "R"):
            sh = arm.pose.bones[f"mixamorig:{'Left' if side == 'L' else 'Right'}Shoulder"]
            sh.matrix_basis = Matrix.Identity(4)
            bpy.context.view_layer.update()
            sh.keyframe_insert("rotation_quaternion", frame=frame)

        # ---- hips: VERTICAL delta off the fixed anchor, and nothing else -----
        # No fore/aft term, deliberately -- see the module docstring and
        # `_verify_hips_stay_in_place`. The retreat lives in the FEET.
        pb = arm.pose.bones[lib.HIPS]
        mh = pb.matrix.copy()
        mh.translation = hips_base + up * geom.m(ch["hip_offset_m"])
        pb.matrix = mh
        bpy.context.view_layer.update()
        pb.keyframe_insert("location", frame=frame)
        hips_now = pb.head.copy()

        # ---- torso pitch about the LATERAL axis (a pitch, not a twist) ------
        # A BACKWARD counter-rotation off the source's own ~30 deg forward
        # crouch; `rotate_bone_about_head` composes onto the current pose, so
        # the source's motion is adjusted rather than replaced.
        pitch_rad = math.radians(TORSO_PITCH_SIGN * ch["torso_back_deg"])
        lib.rotate_bone_about_head(
            arm, lib.SPINE, (Matrix.Rotation(pitch_rad, 4, body_right),), frame=frame)

        # ---- legs: BOTH feet channelled (unlike the jab's fixed plant) ------
        # Both ankle targets are anchored to `hips_base`, not `hips_now` -- a
        # planted move keeps the FLOOR fixed and lets the hips move relative to
        # it (author_contest.py's lesson: anchoring to `hips_now` made a crouch
        # lift the feet by exactly the crouch depth). That is what makes the
        # 0.11 m Recovery hip drop read as a crouch instead of as levitation.
        toe_dir = (forward * 0.90 - up * 0.44).normalized()

        front_ankle = (front_ankle_base
                       + forward * geom.m(ch["front_fore_m"])
                       + up * geom.m(ch["front_up_m"]))
        _solved, front_err = lib.plant_foot(arm, "R", front_ankle, toe_dir, geom, frame=frame)
        worst_ankle_err = max(worst_ankle_err, front_err)

        # The rear foot has no vertical channel at all -- it carries the weight
        # for the whole move, so "the rear foot stays down" is made structurally
        # impossible to violate rather than left to the table.
        rear_ankle = rear_ankle_base + forward * geom.m(ch["rear_fore_m"])
        _solved, rear_err = lib.plant_foot(arm, "L", rear_ankle, toe_dir, geom, frame=frame)
        worst_ankle_err = max(worst_ankle_err, rear_err)

        # ---- arms: ONE set of channels, mirrored per side --------------------
        # Unlike the jab -- where the hands are held CONSTANT because "the ball
        # does not travel with the foot" is the content of that read -- these DO
        # travel: handoff 05 has the ball pulled back toward the retreating hip
        # in Startup, protected on the retreat side through Active, and settled
        # at dribble height in Recovery. The arc also carries real weight in the
        # Startup-vs-Recovery pose gate.
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

    # The clip contains NO hop -- the burst is the game's, and it is horizontal.
    lib.verify_grounded(arm, all_frames, GROUND_BAND_TOL_M, geom)

    # ── the retreat itself, proved from three independent angles ─────────────
    _torso_pitch_sign_is_backward(arm, geom, body_right, forward)   # right way
    _verify_torso_at_or_past_vertical(arm, geom, forward)           # far enough
    _verify_hips_stay_in_place(arm, geom, hips_base, up, forward, body_right, all_frames)
    _verify_both_feet_drift_forward(arm, geom, forward, F0, ACTIVE_END)

    # ── #296 legibility, both comparisons ────────────────────────────────────
    lib.verify_pose_distinct(
        lib.snapshot_pose(arm, F0), lib.snapshot_pose(arm, F1),
        POSE_DISTINCT_MIN_DEG, label="startup_vs_recovery")
    _verify_startup_end_differs_from_recovery_end(arm)

    lib.export_fbx(arm, dst, ACTION_NAME)
    log(f"wrote {dst}")


if __name__ == "__main__":
    main()
