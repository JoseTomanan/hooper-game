"""Author `jabstep` as a single-polarity keypose clip in headless Blender (#304).

Run:
    "$BLENDER" --background --python-exit-code 1 \
        --python tools/author_jabstep.py -- <src.fbx> <out.fbx>

`--python-exit-code 1` is MANDATORY. Blender's `--background --python` exits 0
even on an uncaught traceback, so without it a crashed authoring run reports
success to the pipeline.

===============================================================================
THE SMALLEST COMMITTED MOVE IN THE GAME
===============================================================================
JabStep.DefaultFrameData is Startup=3 / Active=2 / Recovery=4 ticks @ 60 Hz --
9 ticks, 0.150 s total. Before #304 it fell through
MoveAnimResolver.ResolveStateName's default case onto the shared generic
Startup/Active/Recovery states, which per #296 render as a looping idle for
Startup/Recovery (pixel-identical) and a 2-tick slice of a SPRINT STRIDE for
Active -- an actively false read (JabStepLegalityResolver.cs's own class doc:
"a quick, honest foot-stab that sells 'I might drive' without surrendering the
pivot"; a torn sprint fragment sells nothing).

Per README's "the <=3-tick segments are single poses" rule: Active is 2 ticks
(one to two rendered frames at 60 Hz) and Startup is 3, so this clip is
authored as THREE HELD POSES, not three little movies. The read comes from pose
CONTRAST between phases, exactly like author_contest.py's own segments.

===============================================================================
THE DEFINING CONTRAST -- torso lean SIGN vs. its twin, retreat dribble (#305)
===============================================================================
Jab step and retreat dribble share the identical 3/2/4 tick shape off the same
source (assets/Dribble.fbx). If the two clips look alike the game has two
indistinguishable moves. The issue states the discriminator directly:

    Jab step's torso pitches FORWARD over an extended front foot. Retreat
    dribble's torso stays upright-to-back over a retreating base.

So TORSO_PITCH_SIGN here must produce a FORWARD lean, numerically verified by
`_torso_pitch_sign_is_forward` -- the same oracle author_contest.py uses for
its own (also forward) defensive crouch, because "forward" is not reliably
guessable by eye from a signed rotation about a derived axis (that script's own
docstring: the initial +1.0 guess for contest was wrong).

===============================================================================
UNHANDED -- ONE FIXED POLARITY, NOT A LEFT/RIGHT PAIR
===============================================================================
Per the issue: handedness is No. Real ball's jab step is a foot GESTURE the
defender reads, independent of which hand the ball is in (JabStep.cs's own
class doc: "carries NO burst payload... a pure 'wait' the tick loop does not
intercept at all"). So this clip commits to ONE fixed jab-leg polarity (right
leg jabs) regardless of ball hand, the same shape LayupAnimTest/ContestAnimTest
already ship for their own unhanded moves -- no Left/Right suffix, no swap
timing to get wrong (README trap 4 does not apply because there is no second
polarity to track).

===============================================================================
WHAT THIS PRODUCES
===============================================================================
ONE Blender action, baked at 60 Hz, frame numbers ARE physics ticks:

    frames    seconds              segment
    0  -> 3   0.00000 -> 0.05000   Startup   (3 ticks -- the wind-up read)
    3  -> 5   0.05000 -> 0.08333   Active    (2 ticks -- the stab itself)
    5  -> 9   0.08333 -> 0.15000   Recovery  (4 ticks -- back to the stance)

===============================================================================
MOTION, PHASE BY PHASE (issue #304's motion spec)
===============================================================================
Startup (0->3): weight settles onto the PLANT leg (left); the JAB leg (right)
unweights with a slight knee lift (~0.05 m ankle clearance) but does NOT yet
travel forward -- it is a preparatory lift, not the stab. Torso pitches forward
~10 deg off the stance. Ball/hands pulled in toward the hip (low, close to the
body) -- this position does not change materially for the rest of the move,
which is the "held back and protected" half of the read.

Active (3->5): the jab foot STABS forward ~0.35 m and replants (no vertical
clearance at the apex -- a real jab drives the foot back down, it does not hop).
Torso reaches ~20 deg forward, over the extended front foot. The rear (plant)
leg stays fixed -- "this is a jab, not a step" (issue). Deliberately MODEST:
0.35 m reads as a foot-stab, not a crossing stride (a jab authored large reads
as a drive, the false read in the opposite direction).

Recovery (5->9): the jab foot retracts back to the base stance (fore offset
returns to ~0, matching the plant foot); hips settle lower than Startup by
~0.03 m (issue: "hips re-centred and slightly LOWER than Startup"); torso eases
back most of the way toward vertical (~5 deg residual -- a real recovery is not
a hard reset to neutral, it is a settle, matching author_contest.py's own
"Recovery is a real, lower stance" precedent).

===============================================================================
WHY THE BALL/HANDS NEVER MOVE FORWARD
===============================================================================
Issue #304: "the ball is held back and protected, deliberately NOT moving
forward with the foot." So arm_fore_m/arm_lat_m/arm_height_m are held
CONSTANT across all four keyposes -- the only channels that move are the legs
and the torso pitch. That constancy is not an oversight; it is the content of
the read (the foot commits, the ball does not).

===============================================================================
LATERAL SIGN CONVENTION
===============================================================================
Same as author_contest.py/author_layup.py: lateral offsets go through
`geom.body_right`, the rig's anatomical right (derived from the shoulder span
and cross-checked against the hip span in `blender_anim_lib.derive_body_right`).
`geom.lateral` is a basis vector only -- on this rig it points at the
character's LEFT and must NOT be used for hand/foot placement. There is no
longer a `geom.right`; the local `-geom.right` workaround is gone, replaced by
the shared accessor (#320).

===============================================================================
COSMETIC-ONLY (the dense surface this move sits on)
===============================================================================
This script is an ASSET BUILD TOOL. It does not import, read, or feed
JabStepLegalityResolver.IsLegal, BallState, or any PlayerController move-begin
gate. The clip VISUALISES the jab; it never decides whether one is legal.
"""

import math
import os
import sys

import bpy
from mathutils import Matrix

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import blender_anim_lib as lib  # noqa: E402  (must follow the sys.path fix)

log = lib.log

# ── clip contract, 60 Hz (JabStep.DefaultFrameData) ──────────────────────────
FPS = 60
STARTUP_TICKS = 3
ACTIVE_TICKS = 2
RECOVERY_TICKS = 4
TOTAL_TICKS = STARTUP_TICKS + ACTIVE_TICKS + RECOVERY_TICKS  # 9

F0 = 0
F1 = TOTAL_TICKS  # inclusive -- frame numbers ARE physics ticks

# Segment boundaries as frame numbers, named once so the gates below and the
# rebuild script's slice windows cannot drift apart silently.
STARTUP_END = STARTUP_TICKS               # 3
ACTIVE_END = STARTUP_TICKS + ACTIVE_TICKS  # 5

ACTION_NAME = "jabstep"

# ── stance geometry, metre-denominated ────────────────────────────────────────
# Reused verbatim from author_contest.py's measurement on this SAME rig (Y Bot:
# femur/tibia/foot are rig-intrinsic, independent of the source clip).
NEUTRAL_HIP_TO_ANKLE_M = 0.62
STANCE_HALF_WIDTH_M = 0.12

# Torso pitch sign. A jab pitches FORWARD over the extended front foot (issue
# #304's defining contrast with retreat dribble's backward-leaning base).
#
# MEASURED via author_contest.py's identical oracle applied to this rig's SAME
# derived axes: a +1.0 rotation about `body_right` moves the spine->head vector
# BACKWARD (away from `forward`), so -1.0 is what tips it FORWARD. This is not
# assumed from that script's result -- `_torso_pitch_sign_is_forward` below
# re-verifies it independently on THIS clip's own body_right/forward, because a
# guessed sign here would be exactly the kind of "confident but wrong" call
# this repo's convention exists to prevent.
TORSO_PITCH_SIGN = -1.0

# ── keypose channel table ─────────────────────────────────────────────────────
# Columns:
#   time_s, label,
#   hip_offset_m      (+up, vertical delta off the fixed hips_base anchor)
#   torso_pitch_deg   (magnitude; TORSO_PITCH_SIGN supplies the sign)
#   jab_fore_m        (the JAB (right) foot's forward offset off its base spot)
#   jab_up_m          (the JAB foot's vertical clearance above the shared floor)
#   arm_fore_m, arm_lat_m, arm_height_m  (BOTH hands -- mirrored, held near the
#                                          hip, constant across the whole move;
#                                          see the module docstring)
#
# The PLANT (left) foot is not a channel at all -- issue #304: "rear foot stays
# planted" -- so it is authored as a fixed ankle target in `apply()`, never
# varying with time. That is the structural version of "the plant leg does not
# move," the same technique author_contest.py uses for "weight stays centred"
# (a value with no channel cannot be retuned into a violation by accident).
_KEYPOSES_RAW = [
    # t_s,              label,      hip_off, pitch, jab_fore, jab_up, arm_fore, arm_lat, arm_h
    # Frame 0 -- triple-threat entry, unweighting onto the plant leg.
    [0.00000,            "startup",  -0.01,   6.0,   0.00,     0.03,   0.10,     0.15,    0.02],
    # Frame 3 -- the Startup/Active SLICE BOUNDARY -- the last frame of
    # `jabstepstartup` and the first of `jabstepactive` simultaneously. The
    # wind-up is only three ticks, so the unweighted lift has to be fully
    # readable by here: knee lift near its peak, torso already pitching in.
    [STARTUP_END / FPS,  "active",   -0.02,   10.0,  0.00,     0.05,   0.10,     0.15,    0.02],
    # Frame 5 -- the Active/Recovery boundary. This is the STAB itself: the jab
    # foot has travelled forward and replanted (jab_up back to 0 -- a real jab
    # drives the foot back DOWN, it does not hop), torso at its deepest pitch.
    [ACTIVE_END / FPS,   "recovery", -0.03,   20.0,  0.35,     0.00,   0.10,     0.15,    0.02],
    # Frame 9 -- retracted back to the base stance, hips lower than frame 0
    # (issue: "slightly lower than Startup"), torso settled most of the way
    # back toward vertical (a real recovery is a settle, not a hard reset --
    # matches author_contest.py's own "end pose is lower, not neutral").
    [TOTAL_TICKS / FPS,  "recovery", -0.05,   5.0,   0.00,     0.00,   0.10,     0.15,    0.02],
]

_CHANNEL_NAMES = (
    "hip_offset_m", "torso_pitch_deg", "jab_fore_m", "jab_up_m",
    "arm_fore_m", "arm_lat_m", "arm_height_m",
)

# Elbow bend-plane hints, mirrored per side (up + outward). One fixed hint per
# arm across the whole timeline, same pattern as author_contest.py: it only has
# to avoid being exactly parallel to the reach direction, which a shallow
# up-and-outward hint safely is for a low, near-hip hand target.
ELBOW_HINT_UP = 0.3
ELBOW_HINT_LAT = 0.6

# ── proof thresholds ──────────────────────────────────────────────────────────
# Startup(f0)-vs-Recovery(f9) legibility floor (#296). Matches the other
# scripts' 15.0 deg floor -- with three held poses this is the load-bearing
# gate: if Startup and Recovery coincide the move has no arc at all.
POSE_DISTINCT_MIN_DEG = 15.0


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

    Isolated the same way author_contest.py's oracle is: take the spine->head
    vector at a single frame, rotate it by the signed pitch (no baking, no
    two-frame comparison), and check the forward component GREW. A two-frame
    comparison would be dominated by the source clip's own drift (the same
    trap that script's docstring names for Goalkeeper Catch Stationary.fbx);
    testing the rotation itself at one frame sidesteps it entirely.
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
            f"BACKWARD. A jab step leans IN over the extended front foot, not "
            f"away from it. Flip TORSO_PITCH_SIGN.")


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    src, dst = argv[0], argv[1]

    arm, _src_f0, _src_f1 = lib.load_source(src, FPS)
    scene = bpy.context.scene

    geom = lib.RigGeometry(arm)
    geom.log_summary()
    # Anatomical right, derived + verified in the lib (#320).
    body_right = geom.body_right
    up, forward = geom.up, geom.forward
    lib.report("body_right", tuple(round(v, 4) for v in body_right))

    lib.enter_pose_mode(arm)

    # Anchor Hips position, captured ONCE before any of this script's own
    # posing. Every frame's Hips target is built from this, so the move authors
    # its own (purely vertical) trajectory rather than inheriting the source's
    # root motion.
    hips_base = arm.pose.bones[lib.HIPS].head.copy()

    # The PLANT (left) leg's fixed ankle target -- captured as a spec constant,
    # not a channel (see the keypose table's header comment: "rear foot stays
    # planted" is made structurally impossible to violate, the same technique
    # author_contest.py uses for "weight stays centred").
    plant_ankle = (hips_base
                   + body_right * geom.m(-STANCE_HALF_WIDTH_M)
                   - up * geom.m(NEUTRAL_HIP_TO_ANKLE_M))
    # The JAB (right) foot's BASE spot (jab_fore_m/jab_up_m are offsets off
    # this), i.e. where the foot sits when neither raised nor stabbed forward.
    jab_ankle_base = (hips_base
                      + body_right * geom.m(STANCE_HALF_WIDTH_M)
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
        # Same fix author_contest.py applies for the identical reason.
        for side in ("L", "R"):
            sh = arm.pose.bones[f"mixamorig:{'Left' if side == 'L' else 'Right'}Shoulder"]
            sh.matrix_basis = Matrix.Identity(4)
            bpy.context.view_layer.update()
            sh.keyframe_insert("rotation_quaternion", frame=frame)

        # ---- hips: VERTICAL delta off the fixed anchor -----------------------
        pb = arm.pose.bones[lib.HIPS]
        mh = pb.matrix.copy()
        mh.translation = hips_base + up * geom.m(ch["hip_offset_m"])
        pb.matrix = mh
        bpy.context.view_layer.update()
        pb.keyframe_insert("location", frame=frame)
        hips_now = pb.head.copy()

        # ---- torso pitch about the LATERAL axis (a pitch, not a twist) ------
        pitch_rad = math.radians(TORSO_PITCH_SIGN * ch["torso_pitch_deg"])
        lib.rotate_bone_about_head(
            arm, lib.SPINE, (Matrix.Rotation(pitch_rad, 4, body_right),), frame=frame)

        # ---- legs: PLANT fixed, JAB moves per its own channels --------------
        # Both ankle targets are anchored to `hips_base`, not `hips_now` -- a
        # planted move keeps the floor fixed and the hips move relative to it
        # (author_contest.py's own lesson: anchoring to `hips_now` made a crouch
        # lift the feet by exactly the crouch depth).
        toe_dir = (forward * 0.90 - up * 0.44).normalized()

        _solved, plant_err = lib.plant_foot(arm, "L", plant_ankle, toe_dir, geom, frame=frame)
        worst_ankle_err = max(worst_ankle_err, plant_err)

        jab_ankle = (jab_ankle_base
                     + forward * geom.m(ch["jab_fore_m"])
                     + up * geom.m(ch["jab_up_m"]))
        _solved, jab_err = lib.plant_foot(arm, "R", jab_ankle, toe_dir, geom, frame=frame)
        worst_ankle_err = max(worst_ankle_err, jab_err)

        # ---- arms: ONE set of channels, mirrored per side, CONSTANT ----------
        # See the module docstring: the ball does not travel with the foot, so
        # these channels do not vary across the keypose table at all.
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

    _torso_pitch_sign_is_forward(arm, geom, body_right, forward)

    lib.verify_pose_distinct(
        lib.snapshot_pose(arm, F0), lib.snapshot_pose(arm, F1),
        POSE_DISTINCT_MIN_DEG, label="startup_vs_recovery")

    lib.export_fbx(arm, dst, ACTION_NAME)
    log(f"wrote {dst}")


if __name__ == "__main__":
    main()
