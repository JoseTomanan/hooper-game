"""Author `inandout` as a single-polarity keypose clip in headless Blender (#308).

Run:
    "$BLENDER" --background --python-exit-code 1 \
        --python tools/author_inandout.py -- <src.fbx> <out.fbx>

`--python-exit-code 1` is MANDATORY. Blender's `--background --python` exits 0
even on an uncaught traceback, so without it a crashed authoring run reports
success to the pipeline.

===============================================================================
THE MOVE: A CROSSOVER IMPERSONATION THAT REVEALS ITSELF
===============================================================================
InAndOut.DefaultFrameData is Startup=4 / Active=3 / Recovery=12 ticks @ 60 Hz --
19 ticks, 0.317 s total. Before #308 it fell through
MoveAnimResolver.ResolveStateName's default case onto the shared generic
Startup/Active/Recovery states (#296).

Per docs/handoffs/anim-clips/08-in-and-out.md: "the first half of an in-and-out
is a deliberate crossover impersonation... the clip has to sell the fake and
then reveal it."

    Crossover's Active pose has the off-hand coming IN to meet the ball at the
    midline. In-and-out's off-hand stays OUT and away, and the ball-hand palm
    is rotated outward. The lie is the ball position; the tell is the off-hand.

Per README's "the <=3-tick segments are single poses" rule, Active is 3 ticks
(two to three rendered frames at 60 Hz), so this clip is authored as FOUR HELD
KEYPOSES at the phase boundaries (f0/f4/f7/f19), not four little movies. The
read comes from pose CONTRAST between phases.

===============================================================================
UNHANDED -- ONE FIXED POLARITY, NEVER SWAPS (the trap the handoff names first)
===============================================================================
Per docs/handoffs/anim-clips/08-in-and-out.md's "handedness trap" section:
in-and-out carries a BurstDirection param and so LOOKS handed, but it is not.
It commits to ONE fixed ball-hand polarity for the whole clip -- no Left/Right
suffix, no HandedMoves entry, no OriginHand routing (that formula is only valid
for a move that flips the ball hand at Active-entry; in-and-out never flips).
This script authors exactly one, RIGHT-handed polarity. "Pristine" means the
named Dribble.fbx whose SHA-256 content contract passes below; its native
cadence is then measured as a pre-output contract, not selected at runtime. A
left-handed, tied, renamed, or content-drifted source must fail before output.

===============================================================================
WHICH HAND IS "THE BALL HAND" -- MEASURED, NOT ASSUMED
===============================================================================
`_verify_authored_native_ball_side()` samples both wrists' vertical oscillation
amplitude (relative to the hips) across the WHOLE hash-pinned pristine source
timeline and requires the authored RIGHT contract: a material right-wrist
excursion and a material lead over the left. It reports `left_hand_vertical_amplitude_m`,
`right_hand_vertical_amplitude_m`, and the compatible `measured_ball_side=R`.
It never silently selects Left: a mirrored or near-degenerate source is a
source-asset change that must be re-authored with its matching tool/harness
proof, not adapted accidentally at export time.

===============================================================================
WHAT THIS PRODUCES
===============================================================================
ONE Blender action, baked at 60 Hz, frame numbers ARE physics ticks:

    frames     seconds                segment
    0  -> 4    0.00000 -> 0.06667     Startup   (4 ticks -- the fake)
    4  -> 7    0.06667 -> 0.11667     Active    (3 ticks -- the reveal)
    7  -> 19   0.11667 -> 0.31667     Recovery  (12 ticks -- the commitment)

===============================================================================
MOTION, PHASE BY PHASE (docs/handoffs/anim-clips/08-in-and-out.md's spec)
===============================================================================
f0 (base): the source's natural low dribble stance, ball hand out beside the
hip. Baseline lateral offsets for everything else below.

f4 (Startup end -- THE FAKE, indistinguishable from a crossover on purpose):
ball hand pushes the ball across toward the midline (lateral offset drops to
roughly HALF its f0 value); ball-side shoulder rotates IN ~12 deg (a torso
twist toward the midline); the lead (ball-side) foot steps across ~0.15 m;
hips drop ~0.08 m. The off-hand does NOT move -- it is held at its f0 value,
because the whole point of the fake is that only half the body commits to it.

f7 (Active end -- THE REVEAL): ball at the midline at knee height (lateral
offset near zero, clearly BELOW the f4 value), but the ball-hand palm has
rotated OUTWARD ~40 deg (the ball is still owned by the original hand -- this
is a roll of the hand about the forearm axis, not a change in wrist aim); the
off-hand is OUT AND AWAY, its lateral offset >= its f0 value; the torso has
begun rotating BACK toward the ball-hand side (the twist sign reverses from
f4's inward +12 deg to a smaller outward -6 deg). Every element contradicts
the crossover it was imitating.

f19 (Recovery end -- THE COMMITMENT): the ball snaps back OUTSIDE the original
hip (lateral offset clearly GREATER than f0, target +0.10 to +0.15 m beyond
it); torso and lead foot commit hard to the ball-hand side (twist grows to
-20 deg, well past f7's -6); hips ~0.10 m below f0 in a low driving stance.
12 ticks is a long recovery for a 7-tick commit -- this is a real direction
change through the segment, not a hold.

===============================================================================
THE TORSO TWIST SIGN -- MEASURED, PER #315 REVIEW'S "no guessed signs" rule
===============================================================================
`_measure_twist_sign()` establishes, independently of any assumption, which
signed rotation about `up` at the Spine moves the BALL-SIDE shoulder toward
the midline. The keypose table's `torso_twist_deg` values are then always
`positive == inward (toward midline)`, regardless of which physical side the
measured ball hand turns out to be -- the same discipline
author_jabstep.py's `_torso_pitch_sign_is_forward` applies to torso pitch.

===============================================================================
THE GROUNDING CONTRACT
===============================================================================
This is a weaving, grounded move -- a "step across," not a hop. Both ankle
targets are anchored to `hips_base` (never `hips_now`), so the floor stays
fixed while the hips move relative to it -- the same technique
author_jabstep.py uses for its planted leg, applied here to BOTH legs since
both the lead and trail foot move across the clip.

===============================================================================
LATERAL SIGN CONVENTION
===============================================================================
Same as author_contest.py/author_jabstep.py/author_layup.py: lateral offsets
go through `geom.body_right`, the rig's anatomical right (derived from the
shoulder span, cross-checked against the hip span in
`blender_anim_lib.derive_body_right`). `geom.lateral` is a basis vector only
-- on this rig it points at the character's LEFT and must NOT be used for
hand/foot placement.

===============================================================================
COSMETIC-ONLY
===============================================================================
This script is an ASSET BUILD TOOL. It does not import, read, or feed
InAndOut.cs, BallState, or any PlayerController move-begin gate. The clip
VISUALISES the fake-and-reveal; it never decides whether one is legal.
Per docs/handoffs/anim-clips/08-in-and-out.md: `BallController` gives
`inandout` no special `BallSweepPath` -- the ball follows the default path
regardless of what this clip's hands do.
"""

import math
import os
import sys
import hashlib
from pathlib import Path

import bpy
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import blender_anim_lib as lib  # noqa: E402  (must follow the sys.path fix)

log = lib.log

# ── clip contract, 60 Hz (InAndOut.DefaultFrameData) ─────────────────────────
FPS = 60
STARTUP_TICKS = 4
ACTIVE_TICKS = 3
RECOVERY_TICKS = 12
TOTAL_TICKS = STARTUP_TICKS + ACTIVE_TICKS + RECOVERY_TICKS  # 19

F0 = 0
F1 = TOTAL_TICKS  # inclusive -- frame numbers ARE physics ticks

# Segment boundaries as frame numbers, named once so the gates below and the
# rebuild script's slice windows cannot drift apart silently.
STARTUP_END = STARTUP_TICKS               # 4
ACTIVE_END = STARTUP_TICKS + ACTIVE_TICKS  # 7

ACTION_NAME = "inandout"
EXPECTED_SOURCE = "Dribble.fbx"
# Content identity is deliberate: the same basename can carry a different
# animation, rest pose, or cadence. This digest pins the pristine source whose
# polarity and geometry thresholds below were measured.
EXPECTED_SOURCE_SHA256 = "55d12dad3e71d6e588c08739385ef4c2a97d272a0391c130da9effa2ae876ff6"

# Authored-source polarity contract. The recorded pristine Dribble.fbx
# baseline is R=0.3456 m / L=0.0088 m over the whole source timeline. A 0.05 m
# floor is over five times the quiet wrist's motion, while leaving almost 7x
# headroom under the intended right cadence; the same 0.05 m lead rejects a
# tied/noisy source without baking a barely-evidenced polarity into the FBX.
AUTHORED_NATIVE_BALL_SIDE = "R"
NATIVE_RIGHT_WRIST_MIN_AMPLITUDE_M = 0.05
NATIVE_RIGHT_WRIST_MIN_DOMINANCE_M = 0.05

# ── stance geometry, metre-denominated ────────────────────────────────────────
# Reused verbatim from author_jabstep.py's measurement on this SAME rig (Y Bot:
# femur/tibia/foot are rig-intrinsic, independent of the source clip).
NEUTRAL_HIP_TO_ANKLE_M = 0.62
STANCE_HALF_WIDTH_M = 0.12

# Elbow bend-plane hints, mirrored per side (up + outward). One fixed hint per
# arm across the whole timeline, same pattern as author_jabstep.py.
ELBOW_HINT_UP = 0.3
ELBOW_HINT_LAT = 0.6

GROUND_BAND_TOL_M = 0.05

# ── keypose channel table ─────────────────────────────────────────────────────
# Columns (all metre/degree-denominated; converted through geom.m() in apply()):
#   hip_offset_m        (+up, vertical delta off the fixed hips_base anchor)
#   torso_twist_deg      (magnitude; sign resolved by _measure_twist_sign() so
#                          POSITIVE always means "toward the midline")
#   lead_fore_m, lead_lat_delta_m   (the ball-side foot's offset off ITS OWN
#                                     base stance spot; lat_delta negative =
#                                     toward midline, positive = away from it)
#   trail_fore_m, trail_lat_delta_m (the off-side foot, same convention)
#   ball_fore_m, ball_lat_m, ball_height_m   (ball hand target, relative to
#                                               hips_now; lat_m is an absolute
#                                               magnitude from the midline)
#   ball_palm_roll_deg   (roll of the ball hand about the forearm axis --
#                          the "outward palm" tell at f7)
#   off_fore_m, off_lat_m, off_height_m      (off hand target, same convention
#                                               as the ball hand)
#
# The lead/trail SIGN and the ball/off SIDE ASSIGNMENT are resolved in main()
# from the measured ball_side -- this table is deliberately side-agnostic.
_KEYPOSES_RAW = [
    # t_s,               label,      hip_off, twist, lead_fore, lead_lat, trail_fore, trail_lat, ball_fore, ball_lat, ball_h, ball_roll, off_fore, off_lat, off_h
    # Frame 0 -- the base: natural low dribble stance, ball hand beside the hip.
    [0.00000,             "startup",  -0.03,   0.0,   0.00,      0.00,     0.00,       0.00,      0.08,      0.18,     -0.03,  0.0,       0.07,     0.15,    0.00],
    # Frame 4 -- Startup/Active boundary -- THE FAKE. Indistinguishable from a
    # crossover: ball crosses halfway to the midline, ball-side shoulder
    # rotates IN, lead foot steps across, hips drop. Off-hand UNCHANGED.
    [STARTUP_END / FPS,   "active",   -0.08,   12.0,  0.02,     -0.15,     0.00,       0.00,      0.09,      0.09,     -0.02,  0.0,       0.07,     0.15,    0.00],
    # Frame 7 -- Active/Recovery boundary -- THE REVEAL. Ball at the midline
    # at knee height, palm rotated outward, off-hand out and away, torso
    # beginning to rotate back toward the ball-hand side.
    [ACTIVE_END / FPS,    "recovery", -0.10,  -6.0,   0.03,     -0.12,     0.02,      -0.03,      0.04,      0.02,     -0.06,  40.0,      0.05,     0.24,    0.05],
    # Frame 19 -- THE COMMITMENT. Ball snaps back outside the original hip,
    # torso and lead foot commit hard to the ball-hand side, low driving
    # stance. This is a real direction change through Recovery, not a hold.
    [TOTAL_TICKS / FPS,   "recovery", -0.13, -20.0,   0.15,      0.15,    -0.05,       0.00,      0.10,      0.30,     -0.04,  0.0,       0.08,     0.20,    0.02],
]

_CHANNEL_NAMES = (
    "hip_offset_m", "torso_twist_deg",
    "lead_fore_m", "lead_lat_delta_m", "trail_fore_m", "trail_lat_delta_m",
    "ball_fore_m", "ball_lat_m", "ball_height_m", "ball_palm_roll_deg",
    "off_fore_m", "off_lat_m", "off_height_m",
)

# ── proof thresholds ──────────────────────────────────────────────────────────
POSE_DISTINCT_MIN_DEG = 15.0

# The harness gate this clip exists to satisfy (see the issue prompt): at the
# last Active tick, |off-hand lateral| - |ball-hand lateral| >= this.
SEPARATION_MIN_M = 0.15


def _keyposes_for_lib():
    """`_KEYPOSES_RAW` translated into `blender_anim_lib.Keypose` objects."""
    out = []
    for row in _KEYPOSES_RAW:
        t_s, label = row[0], row[1]
        channels = dict(zip(_CHANNEL_NAMES, row[2:]))
        out.append(lib.Keypose(t_s, label, **channels))
    return out


def _verify_expected_source_content(src):
    """Fail before import/output unless `src` is the hash-pinned source asset."""
    source_path = Path(src)
    if not source_path.is_file():
        raise SystemExit(
            f"FATAL: expected source file {source_path} for {EXPECTED_SOURCE!r} "
            "does not exist; no FBX was authored.")

    digest = hashlib.sha256()
    with source_path.open("rb") as source_file:
        for chunk in iter(lambda: source_file.read(1024 * 1024), b""):
            digest.update(chunk)
    actual_sha256 = digest.hexdigest()
    if actual_sha256 != EXPECTED_SOURCE_SHA256:
        raise SystemExit(
            "FATAL: inandout source content contract FAILED before import/output: "
            f"expected SHA-256 {EXPECTED_SOURCE_SHA256}, got {actual_sha256} "
            f"for {source_path}. A basename match is insufficient; re-measure "
            "the source and update this hash plus all dependent proofs together.")

    lib.report("source_sha256", actual_sha256)


def _verify_authored_native_ball_side(arm, geom, f0, f1):
    """Fail closed unless the pristine source proves this author's Right contract.

    The vertical-amplitude signal is measured across the whole source clip
    before any posing. This is an authored asset contract, not an argmax that
    can silently flip a fixed-polarity move to Left when its source changes.
    """
    scene = bpy.context.scene
    amps = {}
    with lib.preserve_frame():
        for side in ("L", "R"):
            hand = lib.ARM_CHAIN[side][2]
            vals = []
            for f in range(f0, f1 + 1):
                scene.frame_set(f)
                vals.append((arm.pose.bones[hand].head
                             - arm.pose.bones[lib.HIPS].head).dot(geom.up))
            amps[side] = max(vals) - min(vals)
    left_m = geom.to_m(amps["L"])
    right_m = geom.to_m(amps["R"])
    dominance_m = right_m - left_m
    lib.report("left_hand_vertical_amplitude_m", f"{left_m:.4f}")
    lib.report("right_hand_vertical_amplitude_m", f"{right_m:.4f}")
    lib.report("right_hand_vertical_dominance_m", f"{dominance_m:.4f}")
    if (right_m < NATIVE_RIGHT_WRIST_MIN_AMPLITUDE_M
            or dominance_m < NATIVE_RIGHT_WRIST_MIN_DOMINANCE_M):
        raise RuntimeError(
            "inandout authored-source polarity contract FAILED: expected "
            f"Right wrist >= {NATIVE_RIGHT_WRIST_MIN_AMPLITUDE_M:.4f} m and "
            f"Right-Left >= {NATIVE_RIGHT_WRIST_MIN_DOMINANCE_M:.4f} m, got "
            f"R={right_m:.4f} m L={left_m:.4f} m lead={dominance_m:.4f} m. "
            "Refuse to author a fixed-right in-and-out from a mirrored, tied, "
            "or weak source; re-author the polarity contract and matching "
            "rebuild/harness proof together.")
    lib.report("measured_ball_side", AUTHORED_NATIVE_BALL_SIDE)
    return AUTHORED_NATIVE_BALL_SIDE


def measure_twist_sign(arm, geom, body_right, up, ball_side, ball_sign):
    """Which signed rotation about `up` at the Spine moves the BALL-side
    shoulder TOWARD the midline ("rotates in").

    Isolated the same way author_jabstep.py's `_torso_pitch_sign_is_forward`
    oracle is: rotate the spine->shoulder vector by a small test angle (no
    baking, no two-frame comparison) and check which way the ball-side
    shoulder's body_right-projected coordinate moved. A confidently-guessed
    sign here would be exactly the kind of "confident but wrong" call this
    repo's convention exists to prevent (#320's own history).

    Returns a sign S such that `S * torso_twist_deg` (positive
    `torso_twist_deg`) rotates the ball-side shoulder toward the midline,
    REGARDLESS of which physical side the measured ball hand turned out to
    be -- so the keypose table above can read "positive == inward" without
    caring about the runtime-measured polarity.
    """
    humerus = lib.ARM_CHAIN[ball_side][0]
    spine_head = arm.pose.bones[lib.SPINE].head.copy()
    shoulder_head = arm.pose.bones[humerus].head.copy()
    vec = shoulder_head - spine_head
    rot = Matrix.Rotation(math.radians(10.0), 4, up)
    delta_lat = geom.to_m(((rot @ vec) - vec).dot(body_right))
    lib.report("torso_twist_probe_delta_lat_m", f"{delta_lat:+.4f}")
    # "Inward" means the ball-side shoulder's ball_sign-signed coordinate
    # DECREASES (moves toward the midline). If the +10 deg probe rotation
    # already decreases it, the sign we want is +1; otherwise -1.
    sign = 1.0 if (ball_sign * delta_lat) < 0.0 else -1.0
    lib.report("torso_twist_sign", sign)
    return sign


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    src, dst = argv[0], argv[1]

    _verify_expected_source_content(src)
    arm, f0, f1 = lib.load_source(src, FPS, expected=EXPECTED_SOURCE)
    scene = bpy.context.scene

    geom = lib.RigGeometry(arm)
    geom.log_summary()
    body_right = geom.body_right
    up, forward = geom.up, geom.forward
    lib.report("body_right", tuple(round(v, 4) for v in body_right))

    # ── measure which hand is "the ball hand" -- BEFORE any posing ───────────
    ball_side = _verify_authored_native_ball_side(arm, geom, f0, f1)
    off_side = "L" if ball_side == "R" else "R"
    ball_sign = 1.0 if ball_side == "R" else -1.0
    off_sign = -ball_sign
    log(f"ball_side={ball_side} off_side={off_side} ball_sign={ball_sign:+.1f}")

    # ── measure the torso-twist sign convention -- also before posing ────────
    twist_sign = measure_twist_sign(arm, geom, body_right, up, ball_side, ball_sign)

    lib.enter_pose_mode(arm)

    # Anchor Hips position, captured ONCE before any of this script's own
    # posing -- every frame's Hips target is built from this.
    hips_base = arm.pose.bones[lib.HIPS].head.copy()

    # Base ankle spot for EACH side (both feet move in this clip, unlike
    # jabstep's fixed plant leg) -- anchored to hips_base so the floor stays
    # fixed while the hips move relative to it.
    ankle_base = {}
    for side, sign in (("L", -1.0), ("R", 1.0)):
        ankle_base[side] = (hips_base
                             + body_right * geom.m(sign * STANCE_HALF_WIDTH_M)
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
        # Same fix author_jabstep.py/author_contest.py apply for the identical
        # reason.
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

        # ---- torso twist about `up` (a twist, not a pitch) -------------------
        twist_rad = math.radians(twist_sign * ch["torso_twist_deg"])
        lib.rotate_bone_about_head(
            arm, lib.SPINE, (Matrix.Rotation(twist_rad, 4, up),), frame=frame)

        # ---- legs: BOTH feet move -- lead (ball-side), trail (off-side) -----
        # Anchored to hips_base (not hips_now), like author_jabstep.py's
        # plant leg: the floor stays fixed while the hips move relative to it.
        toe_dir = (forward * 0.90 - up * 0.44).normalized()

        lead_ankle = (ankle_base[ball_side]
                      + forward * geom.m(ch["lead_fore_m"])
                      + body_right * geom.m(ball_sign * ch["lead_lat_delta_m"]))
        _solved, lead_err = lib.plant_foot(arm, ball_side, lead_ankle, toe_dir, geom, frame=frame)
        worst_ankle_err = max(worst_ankle_err, lead_err)

        trail_ankle = (ankle_base[off_side]
                       + forward * geom.m(ch["trail_fore_m"])
                       + body_right * geom.m(off_sign * ch["trail_lat_delta_m"]))
        _solved, trail_err = lib.plant_foot(arm, off_side, trail_ankle, toe_dir, geom, frame=frame)
        worst_ankle_err = max(worst_ankle_err, trail_err)

        # ---- ball hand ---------------------------------------------------------
        ball_target = (hips_now
                       + forward * geom.m(ch["ball_fore_m"])
                       + body_right * geom.m(ball_sign * ch["ball_lat_m"])
                       + up * geom.m(ch["ball_height_m"]))
        ball_hint = (up * ELBOW_HINT_UP + body_right * (ball_sign * ELBOW_HINT_LAT)).normalized()
        err_u = lib.aim_arm(arm, ball_side, ball_target, ball_hint, geom, frame=frame)
        worst_wrist_err = max(worst_wrist_err, err_u)

        # The "palm rotates outward" tell (f7): a ROLL of the ball hand about
        # its own (post-IK) forearm axis, composed on top of aim_arm's wrist
        # pose and re-keyed at the same frame. Not cumulative across frames --
        # aim_arm re-derives the hand's base orientation from scratch each
        # frame, so this always applies the CHANNEL's absolute roll amount,
        # not a running delta.
        hand_bone = lib.ARM_CHAIN[ball_side][2]
        hb = arm.pose.bones[hand_bone]
        roll_axis = (hb.matrix.to_3x3() @ Vector((0.0, 1.0, 0.0))).normalized()
        lib.rotate_bone_about_head(
            arm, hand_bone,
            (Matrix.Rotation(math.radians(ch["ball_palm_roll_deg"]), 4, roll_axis),),
            frame=frame)

        # ---- off hand ------------------------------------------------------
        off_target = (hips_now
                      + forward * geom.m(ch["off_fore_m"])
                      + body_right * geom.m(off_sign * ch["off_lat_m"])
                      + up * geom.m(ch["off_height_m"]))
        off_hint = (up * ELBOW_HINT_UP + body_right * (off_sign * ELBOW_HINT_LAT)).normalized()
        err_u2 = lib.aim_arm(arm, off_side, off_target, off_hint, geom, frame=frame)
        worst_wrist_err = max(worst_wrist_err, err_u2)

    lib.bake_timeline(arm, keyposes, apply, F0, F1, FPS)

    bpy.ops.object.mode_set(mode="OBJECT")
    scene.frame_start, scene.frame_end = F0, F1

    lib.report_ankle_ik("worst_ankle_ik_err_m", geom)
    lib.report("worst_wrist_ik_err_m", f"{geom.to_m(worst_wrist_err):.6f}")

    all_frames = list(range(F0, F1 + 1))
    lib.verify_all_bones_keyed(arm, expected_count=52)
    lib.verify_pose_unscaled(arm, all_frames)
    lib.verify_grounded(arm, all_frames, GROUND_BAND_TOL_M, geom)

    lib.verify_pose_distinct(
        lib.snapshot_pose(arm, STARTUP_END), lib.snapshot_pose(arm, F1),
        POSE_DISTINCT_MIN_DEG, label="startup_end_vs_recovery_end")

    # ── the eight lateral offsets, MEASURED from the posed rig (not restated
    # from the keypose table) -- ground truth for the PR report ──────────────
    ball_hand_bone = lib.ARM_CHAIN[ball_side][2]
    off_hand_bone = lib.ARM_CHAIN[off_side][2]

    def lateral_offset_m(bone, frame):
        with lib.preserve_frame():
            scene.frame_set(frame)
            hips_head = arm.pose.bones[lib.HIPS].head.copy()
            wrist_head = arm.pose.bones[bone].head.copy()
            return geom.to_m((wrist_head - hips_head).dot(body_right))

    frames_named = (("f0", F0), ("f4", STARTUP_END), ("f7", ACTIVE_END), ("f19", F1))
    ball_lat = {}
    off_lat = {}
    for name, f in frames_named:
        bl = lateral_offset_m(ball_hand_bone, f)
        ol = lateral_offset_m(off_hand_bone, f)
        ball_lat[name] = abs(bl)
        off_lat[name] = abs(ol)
        lib.report(f"ball_lateral_{name}_m", f"{bl:+.4f}")
        lib.report(f"off_lateral_{name}_m", f"{ol:+.4f}")

    separation_f7 = off_lat["f7"] - ball_lat["f7"]
    lib.report("separation_f7_m", f"{separation_f7:+.4f}")

    if not (ball_lat["f7"] < ball_lat["f4"] < ball_lat["f0"]):
        raise SystemExit(
            f"FATAL: ball-hand lateral offsets are not strictly decreasing "
            f"f0->f4->f7 ({ball_lat['f0']:.4f} / {ball_lat['f4']:.4f} / "
            f"{ball_lat['f7']:.4f}) -- the fake-then-reveal collapse is not "
            f"legible.")
    if not (ball_lat["f19"] > ball_lat["f0"]):
        raise SystemExit(
            f"FATAL: ball-hand lateral offset at f19 ({ball_lat['f19']:.4f}) "
            f"is not greater than f0 ({ball_lat['f0']:.4f}) -- the ball did "
            f"not snap back outside the original hip.")
    if not (off_lat["f7"] >= off_lat["f0"]):
        raise SystemExit(
            f"FATAL: off-hand lateral offset at f7 ({off_lat['f7']:.4f}) is "
            f"not >= f0 ({off_lat['f0']:.4f}) -- the off-hand did not stay "
            f"out and away.")
    if not (separation_f7 >= SEPARATION_MIN_M):
        raise SystemExit(
            f"FATAL: off-hand vs ball-hand separation at f7 is only "
            f"{separation_f7:.4f} m, below the {SEPARATION_MIN_M} m harness "
            f"floor.")

    lib.export_fbx(arm, dst, ACTION_NAME)
    log(f"wrote {dst}")


if __name__ == "__main__":
    main()
