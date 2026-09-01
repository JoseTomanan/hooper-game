"""Author `dribblemove` as a keyframed drive-dribble cycle in headless Blender (#300).

Run:
    "$BLENDER" --background --python-exit-code 1 \
        --python tools/author_dribble_move.py -- <src.fbx> <out.fbx>

`--python-exit-code 1` is MANDATORY. Blender's `--background --python` exits 0
even on an uncaught traceback, so without it a crashed authoring run reports
success to the pipeline.

═══════════════════════════════════════════════════════════════════════════════
WHY THIS EXISTS — superseding #298's delta transplant
═══════════════════════════════════════════════════════════════════════════════
#298 fixed the frozen-legs defect (0.0047 m toe excursion) by transplanting
`run`'s leg motion onto the dribble stance as a world-frame delta. It passed
every automated gate, but it reads as a scaled-down sprint, because the delta
transplant inherits its motion CHARACTER wholesale from a sprint clip.
Amplitude scaling changes how big the sprint is, never that it is a sprint.

The human's direction (#300): author new keyframed motion in headless Blender,
NBA 2K's drive-dribble as the reference. NBA 2K is a human-directed reference
override for this clip -- ADR-0014 ranks real ball > Undisputed 3 > 2K, and the
human has explicitly named 2K here. Do not relitigate it; cite #300.

`dribbleidle` is NOT touched by this script. Only `dribblemove` routes through
Blender. That matters concretely: the measured Blender round-trip error is
0.396 deg, which is below LOOP_SEAM_TOLERANCE_DEG (0.5) but NOT zero, so a clip
required to be verbatim must never make this trip.

═══════════════════════════════════════════════════════════════════════════════
THE METHOD — specify the foot trajectory, then solve the leg
═══════════════════════════════════════════════════════════════════════════════
The one mechanism #298 proved sound is composing a WORLD-FRAME orientation onto
the dribble chain with `Hips` pinned: a world-frame construction carries no
information about which retarget family a clip's rest is expressed against, so
it cannot import `run`'s (or Kenney's) convention into the stock-Mixamo dribble
clip. locomotion.res holds two families ~155-180 deg apart and mixing them is
the #287 degeneracy. This script keeps that mechanism and changes only where
the orientation comes from: a gait function we specify, not a sprint clip.

Crucially it does NOT specify joint ANGLES. Measured rest geometry:

    femur 0.4060 m + tibia 0.4210 m  ->  hip-to-ankle reach 0.8270 m
    animated stance holds the toe    ->  ~0.70 m below the hips

A leg swinging fore/aft from a fixed hip is a pendulum: it LIFTS the foot at
the stride extremes unless the knee compensates. At +-0.35 m of stride the
hip-to-ankle distance is sqrt(0.70^2 + 0.35^2) = 0.783 m against a 0.827 m
limit -- near full extension. That is the analytic explanation of #298's
empirically-discovered grounding cliff (both feet ~0.5 m airborne above
amplitude ~0.51, caught only because PROOF 6 was added after the fact).

So we invert it: define the FOOT TRAJECTORY -- which is what a gait actually
is -- and solve hip + knee by two-link IK (law of cosines). Grounding then
holds BY CONSTRUCTION rather than being discovered by a proof gate.

A second benefit falls out for free. The source clip bakes in a +0.6881 m
STATIC fore/aft stagger (left toe permanently ahead of right; measured
independently in Blender, agreeing with #298's Godot-side figure). #298 had to
cancel it with `C_leg`, a per-leg rotation solved by bisection. Authoring
ABSOLUTE foot positions overwrites the stagger outright, so the whole
`_solve_stance_correction` / `CENTRE_STANCE_STAGGER` machinery becomes
unnecessary rather than merely retuned.

═══════════════════════════════════════════════════════════════════════════════
THE #287 CORRIDOR IS A BUDGET, NOT A CLIFF
═══════════════════════════════════════════════════════════════════════════════
`BlendRestAnchor` pins both UpLeg RESTS to `idle`'s frame-0 key, and there is
one rest per bone shared with the Dribble blendspace. So any UpLeg difference
between the two dribble endpoints risks #287 mixer degeneracy at partial blend
weights. Measured on #298's construction: amplitude 0.50 -> 0/90 frames
violated (69.1 deg endpoint gap); 0.70 -> 1/90 (85.2 deg).

Here the UpLeg excursion is a PARAMETER (STRIDE_LENGTH_M), so the budget is
spent deliberately. If the corridor goes red, reduce stride -- do NOT attempt
to fix it by making the endpoints geometrically more similar. That was measured
and falsified in #298: narrowing the gap made it strictly WORSE (1/90 -> 2/90,
excess 38.1 -> 77.6 deg), which is #287's documented signature.

═══════════════════════════════════════════════════════════════════════════════
CADENCE — locked to the ball, not chosen
═══════════════════════════════════════════════════════════════════════════════
The source clip's own right-hand vertical oscillation measures 3 dribble
bounces per 2.100 s loop, and the human confirmed the target as ~1 bounce per
2 steps. So one gait cycle = 0.700 s = one bounce, 3 cycles per loop. The ball's
bounce timing is driven separately in-engine; the stride MUST agree with it or
the footfalls visibly desync from the ball.

═══════════════════════════════════════════════════════════════════════════════
THE MACHINERY LIVES IN blender_anim_lib (#315)
═══════════════════════════════════════════════════════════════════════════════
The rig geometry, IK, posing primitives, proof helpers and export settings were
extracted to `tools/blender_anim_lib.py` so the twenty clip handoffs in
`docs/handoffs/anim-clips/` share them instead of copying them. What remains
here is this clip's SPEC: the gait function, its constants, and the cadence
proof. The extraction was verified by re-running this script and comparing poses
against the committed `assets/dribble_move_authored.fbx` -- exactly 0.000000 deg
and 0.0 m on every bone of every frame.
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

# ── clip contract (see #300 / rebuild_dribble_clips.gd) ──────────────────────
# The source clip this move is authored OVER, enforced by `lib.load_source`.
# Every threshold in this file was read off a run against this file; see that
# function's docstring for why the source is load-bearing rather than a
# formality, and for the misdiagnosis that motivated the check. This script is
# doubly dependent on it: `_verify_bounce_count` re-measures the SOURCE's own
# dribble bounces and refuses to author if they change.
EXPECTED_SOURCE = "Dribble.fbx"

FPS = 30
CLIP_LENGTH_S = 2.100
# 3 gait cycles x 0.700 s. Measured from the source clip's bounce count, not
# assumed -- _verify_cadence() below re-measures and refuses to continue if the
# source ever changes.
GAIT_CYCLES_PER_LOOP = 3
CYCLE_S = CLIP_LENGTH_S / GAIT_CYCLES_PER_LOOP

# ── the drive-dribble motion spec (NBA 2K reference, #300) ───────────────────
# Total fore/aft foot travel per cycle. A drive dribble is a controlled,
# lower, choppier gait than a sprint: `run` measures ~1.39 m ptp and #298's
# transplant landed 0.73/0.55. 0.60 m sits clearly above PROOF 1's 0.15 m gate
# and clearly below a sprint, and keeps hip-to-ankle at
# sqrt(0.70^2 + 0.30^2) = 0.762 m -- comfortably inside the 0.827 m reach with
# the knee still bent at the extremes.
STRIDE_LENGTH_M = 0.60

# Ankle height above the ground plane while the foot is planted. Small and
# non-zero: the toe, not the ankle, is what contacts.
ANKLE_GROUND_CLEARANCE_M = 0.075
# Peak ankle lift during swing. Drives the knee-lift read; too little looks
# like shuffling, too much looks like a sprint's high knee action.
SWING_FOOT_LIFT_M = 0.13
# Fraction of the cycle each foot spends planted. Running gaits are <50% (a
# flight phase, both feet airborne); walking is >50%. A drive dribble keeps
# ground contact -- 0.62 gives a double-support overlap, which is also what
# keeps PROOF 6's support band tight.
STANCE_FRACTION = 0.62

# How far below the hips the ankle sits at mid-stance, before the crouch. From
# the measured animated stance (toe ~0.70 m below hips, ankle a little above).
HIP_TO_ANKLE_NEUTRAL_M = 0.655
# Extra hip lowering for the drive posture (#286: a lean alone reads as
# "leaning", pairing it with a lower stance is what reads as "moving").
# Matches the 0.12 m Hips POSITION crouch #298 shipped.
CROUCH_DROP_M = 0.12
# Vertical hip oscillation, twice per gait cycle (once per step) -- the classic
# gait bob. Also does real work for grounding: dropping the hips during stance
# reduces how far the leg must extend to keep the foot down.
HIP_BOB_M = 0.025

# Lateral foot separation, per side, from the body midline. Slightly wider than
# the 0.1825 m rest hip width -- a drive stance is not a catwalk.
STANCE_HALF_WIDTH_M = 0.115

# Forward torso lean. INHERITED from #298/#286 at 38 deg, deliberately not
# retuned here: the human's complaint was scoped to the legs reading as a
# sprint, and 38 deg was itself a measured legibility fix (#286 raised it from
# 20, which read as a standing player).
LEAN_DEGREES = 38.0
LEAN_BONE = "mixamorig:Spine"
# Torso counter-rotation against the legs, peak degrees. Real gait swings the
# shoulders opposite the pelvis. Kept small: this bone carries the ball hand,
# and a large twist would move it far enough to need a big HandOffset change.
COUNTER_ROTATION_DEG = 5.0

# ── proof thresholds (measured, not guessed) ─────────────────────────────────
# Both are set from the MEASURED value of this construction plus margin, and are
# reported every run (`ground_band_m`, `pose_distinct_half_cycle_deg`) so drift
# is visible in the log rather than silent. Do not widen either to make a red run
# pass -- the point of a gate is that it can fail.
#
# Support-level band. MEASURED on this construction: 0.0315 m (reported as
# `ground_band_m`). 0.05 leaves ~1.6x headroom for float noise while staying an
# order of magnitude below the both-feet-airborne failure #298 measured at
# ~0.5 m, so the gate still sits in the gap between "grounded" and "floating".
GROUND_BAND_TOL_M = 0.05
# Half-cycle pose divergence. MEASURED: 67.594 deg (reported as
# `pose_distinct_half_cycle_deg`). The 20 deg floor sits far below that and far
# above the ~0 deg a frozen-leg regression would produce -- this is the
# anti-#298 gate, so it must land in the gap between "striding" and "frozen".
GAIT_DISTINCT_MIN_DEG = 20.0

# Left leg leads; right is half a cycle out of phase.
PHASE_OFFSET = {"L": 0.0, "R": 0.5}

# Godot names the imported clip after the Blender action. The rebuild tool
# looks the clip up by name, so this is a contract, not cosmetic.
ACTION_NAME = "dribblemove"

log = lib.log


# ═════════════════════════════════════════════════════════════════════════════
# the gait function
# ═════════════════════════════════════════════════════════════════════════════
def foot_target(phase, stride, ground_y, lift):
    """Ankle position for one foot at `phase` in [0,1), as (fore, up) offsets.

    Stance: the foot is PLANTED, so it tracks backward relative to the body at
    a constant rate -- the body is what moves forward. Linear, because a planted
    foot does not accelerate relative to the ground.

    Swing: the foot lifts and swings forward. A raised-cosine gives zero
    fore/aft velocity at both ends, so the foot is not sliding at the moment it
    touches down -- the visual tell of a skating character. The lift uses
    sin(pi*s), which is zero at both ends and peaks mid-swing.
    """
    half = stride * 0.5
    if phase < STANCE_FRACTION:
        s = phase / STANCE_FRACTION
        return half - stride * s, ground_y
    s = (phase - STANCE_FRACTION) / (1.0 - STANCE_FRACTION)
    fore = -half + stride * (0.5 - 0.5 * math.cos(math.pi * s))
    return fore, ground_y + lift * math.sin(math.pi * s)


def hip_bob_factor(phase):
    """Unitless [0,1] hip-lowering factor. Two dips per cycle -- one per step.

    Returned unitless so the caller scales it in whatever space it is working
    in; mixing a metre-denominated return into armature-space arithmetic is
    the exact error this script already paid for once.
    """
    return abs(math.sin(math.pi * phase * 2.0))


# ═════════════════════════════════════════════════════════════════════════════
# main
# ═════════════════════════════════════════════════════════════════════════════
def verify_cadence(arm, f0, f1, geom):
    """Re-measure the source clip's bounce count; refuse to author on a change.

    GAIT_CYCLES_PER_LOOP is derived from this. If someone swaps the source FBX
    for a clip with different cadence, silently keeping 3 would desync the
    stride from the ball -- so this fails loud instead.
    """
    # `preserve_frame` per README-blender.md trap 5: anything that samples poses
    # restores the frame. Harmless here today (this runs before the authoring
    # loop, which sets every frame it touches), but this is the one sampler in
    # the PR that was violating the convention the PR itself introduces, and the
    # trap is that the harm only appears when someone later moves the call.
    vals = []
    with lib.preserve_frame():
        for f in range(f0, f1 + 1):
            bpy.context.scene.frame_set(f)
            vals.append((arm.pose.bones["mixamorig:RightHand"].head
                         - arm.pose.bones[lib.HIPS].head).dot(geom.up))
    span = max(vals) - min(vals)
    lo, hi = min(vals) + 0.2 * span, max(vals) - 0.2 * span
    bounces, state = 0, ("high" if vals[0] > hi else "low")
    for v in vals:
        if state == "high" and v < lo:
            state, bounces = "low", bounces + 1
        elif state == "low" and v > hi:
            state = "high"
    lib.report("source_bounces_per_loop", bounces)
    # `vals` are armature-space dot products, so the span is in armature units.
    # Reported in metres via geom to keep every logged length in one unit -- the
    # pre-#315 version of this line printed the raw armature figure with an "m"
    # suffix, i.e. 100x, which is precisely the confusion trap 3 warns about.
    lib.report("source_hand_span_m", f"{geom.to_m(span):.4f}")
    if bounces != GAIT_CYCLES_PER_LOOP:
        raise SystemExit(
            f"FATAL: source clip has {bounces} bounces but GAIT_CYCLES_PER_LOOP "
            f"is {GAIT_CYCLES_PER_LOOP}. The stride would desync from the ball. "
            f"Re-derive the cadence before authoring.")


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    src, dst = argv[0], argv[1]

    arm, f0, f1 = lib.load_source(src, FPS, expected=EXPECTED_SOURCE)
    scene = bpy.context.scene

    geom = lib.RigGeometry(arm)
    geom.log_summary()
    # `lateral`, NOT `body_right` (#320): this basis is handed to `aim_matrix`
    # as its `side_axis`, a bone-ROLL reference where the axis SIGN is
    # load-bearing but its anatomy is irrelevant. Swapping in `body_right` here
    # would roll the posed bones 180 deg while changing nothing about which side
    # anything lands on.
    #
    # `right` ALSO supplies the `Matrix.Rotation` torso-lean axis below (~line
    # 302). That is a separate contract: there either axis works, but only
    # paired with a lean-sign constant derived against it -- `LEAN_DEGREES` was
    # derived against `lateral`. Unlike `aim_matrix`, the pairing is what
    # matters, not the axis. See `RigGeometry`'s docstring; four sibling
    # scripts legitimately pass `body_right` there instead.
    right, up, forward = geom.lateral, geom.up, geom.forward

    verify_cadence(arm, f0, f1, geom)

    # Spec constants are metre-denominated for readability; convert once, here,
    # so nothing downstream has to remember which space it is in. `geom.m()` IS
    # that conversion -- never hand-roll the factor (trap 3).
    stride_u = geom.m(STRIDE_LENGTH_M)
    lift_u = geom.m(SWING_FOOT_LIFT_M)
    neutral_u = geom.m(HIP_TO_ANKLE_NEUTRAL_M)
    crouch_u = geom.m(CROUCH_DROP_M)
    bob_u = geom.m(HIP_BOB_M)
    half_width_u = geom.m(STANCE_HALF_WIDTH_M)

    lib.enter_pose_mode(arm)
    lean_q = Matrix.Rotation(math.radians(LEAN_DEGREES), 4, right)
    geom.reset_ankle_ik()

    for i, f in enumerate(range(f0, f1 + 1)):
        scene.frame_set(f)
        t = i / FPS
        phase_base = (t / CYCLE_S) % 1.0

        # ---- Hips: crouch + gait bob, keyed as a POSITION offset ------------
        # `drop_hips` applies this as a DELTA on the clip's own root motion, not
        # an absolute position, so whatever the source clip does vertically is
        # preserved and merely lowered -- and it leaves the Hips ROTATION alone,
        # which matters because that is the one bone the two rotation families
        # disagree on catastrophically (~158 deg) and every leg solve hangs off
        # it.
        drop = crouch_u + bob_u * hip_bob_factor(phase_base)
        lib.drop_hips(arm, -(up * drop), geom, frame=f)
        hips_now = arm.pose.bones[lib.HIPS].head.copy()

        # ---- torso lean + counter-rotation ----------------------------------
        twist = math.radians(COUNTER_ROTATION_DEG) * math.sin(2.0 * math.pi * phase_base)
        lib.rotate_bone_about_head(
            arm, LEAN_BONE,
            (Matrix.Rotation(twist, 4, up), lean_q),
            frame=f)

        # ---- legs: foot trajectory -> two-link IK ---------------------------
        for side in lib.LEG_CHAIN:
            phase = (phase_base + PHASE_OFFSET[side]) % 1.0
            sign = -1.0 if side == "L" else 1.0

            fore, vert = foot_target(phase, stride_u, -neutral_u, lift_u)
            ankle = (hips_now
                     + forward * fore
                     + up * vert
                     + right * (sign * half_width_u))

            # Foot: keep the sole roughly parallel to the ground during stance,
            # and toe-down through swing so the step reads as a real footfall
            # rather than a flat-footed slide.
            if phase < STANCE_FRACTION:
                toe_dir = (forward * 0.90 - up * 0.44).normalized()
            else:
                s = (phase - STANCE_FRACTION) / (1.0 - STANCE_FRACTION)
                pitch = math.sin(math.pi * s)
                toe_dir = (forward * 0.90 - up * (0.44 - 0.34 * pitch)).normalized()

            lib.plant_foot(arm, side, ankle, toe_dir, geom, frame=f)

    bpy.ops.object.mode_set(mode="OBJECT")

    # Where the ankles actually landed vs where the IK was asked to put them.
    # Reads 0.000000 since #321 made `plant_foot`'s bend-plane axis
    # perpendicular by construction, so the solve is exact for ANY target
    # direction (it previously read 0.029890 here). A nonzero value now means an
    # over-reach clamp or a rig-geometry change, not solver error -- and #335
    # made that a hard failure rather than a number in the log.
    lib.report_ankle_ik("worst_ankle_ik_err_m", geom)

    # ---- proofs, before the export commits anything --------------------------
    # These are the library's shared gates (#315), run here both because this
    # clip should be held to them and because a gate nothing exercises is a gate
    # nobody trusts. Each raises SystemExit, which `--python-exit-code 1` turns
    # into a failed build.
    frames = list(range(f0, f1 + 1))
    # `expected_count` is what gives this gate teeth. Without it only the
    # "some bone is unkeyed" branch runs, and that passes by construction on any
    # healthy Mixamo source. 52 = the Y Bot's 65 bones minus the 13 leaf
    # terminators; if a source swap or a narrowed export changes that, this is
    # what says so instead of shipping a clip that T-poses in Godot.
    lib.verify_all_bones_keyed(arm, expected_count=52)
    # NOTE: a source-integrity tripwire, NOT an `aim_matrix` guard -- see its
    # docstring. `aim_matrix` asserts its own orthonormality at construction.
    lib.verify_pose_unscaled(arm, frames)
    # A drive gait keeps ground contact (STANCE_FRACTION 0.62 > 0.5 gives a
    # double-support overlap), so the LOWER toe should never leave the support
    # level by much. This is #298's PROOF 6 promoted to a shared helper -- it is
    # the gate that caught the both-feet-0.5 m-airborne cliff.
    lib.verify_grounded(arm, frames, GROUND_BAND_TOL_M, geom)
    # The whole reason this clip was re-authored (#300) is that #298's legs read
    # as frozen. Half a gait cycle apart the pose must genuinely differ, so
    # assert it rather than trusting the eye.
    half_cycle_frames = int(round(CYCLE_S * FPS * 0.5))
    lib.verify_pose_distinct(
        lib.snapshot_pose(arm, f0),
        lib.snapshot_pose(arm, f0 + half_cycle_frames),
        GAIT_DISTINCT_MIN_DEG,
        label="half_cycle")

    lib.export_fbx(arm, dst, ACTION_NAME)
    print("AUTHOR_OK")


main()
