"""Measure the layup drive knee's position, and its sensitivity to the drive
ankle's LATERAL target, in headless Blender (#335).

Run:
    "$BLENDER" --background --python-exit-code 1 \
        --python tools/measure_layup_knee.py -- "assets/Goalkeeper Catch Stationary.fbx"

This is a MEASUREMENT INSTRUMENT, not part of the authoring pipeline. It writes
no FBX and mutates nothing on disk. It exists because #335's central question --
"is `layup`'s 65 deg / 0.437 m left-femur drift #321's correction, or evidence of
a spec tuned against the old broken solver?" -- is answerable only with numbers,
and those numbers have to be reproducible by the next reader rather than quoted
from a session log.

===============================================================================
WHY IT SPIES ON THE REAL SCRIPT INSTEAD OF REIMPLEMENTING THE SPEC
===============================================================================
The obvious approach -- copy `author_layup.py`'s apex ankle target arithmetic
into this file and measure that -- silently answers a DIFFERENT question the
moment either file is edited. The keypose table, the hips anchor, the
interpolation, and the neutral hip-to-ankle constant are all spec, and a
measurement of a stale copy of the spec is worse than no measurement: it looks
authoritative.

So this script `exec`s `author_layup.py` verbatim with two things swapped out:

    lib.plant_foot   wrapped, so every leg solve the real script performs is
                     recorded (hip head, ankle target, resulting knee/ankle)
    lib.export_fbx   no-op, so the run proves the pose without shipping it

`author_layup.py` looks its helpers up as `lib.<name>` at CALL time, so patching
the module object is enough -- no edit to the authoring script, and no second
copy of the spec to drift.

===============================================================================
THE TWO QUESTIONS, AND WHY THE SECOND ONE EXISTS
===============================================================================
1. WHERE IS THE KNEE? Reported per frame, relative to the DRIVE HIP (not the
   hips centre -- the hip is where the femur actually hangs from, and the whole
   finding is that the two differ by enough to matter).

2. HOW FAST DOES IT MOVE? `plant_foot` controls the ANKLE. Nothing in the layup
   spec controls the KNEE: it is emergent from the bend plane, whose normal is
   `dir_ankle.cross(geom.forward)`. When the leg is deeply folded -- and at the
   layup apex `|to_ankle|` is a small fraction of full leg reach -- `dir_ankle`
   is short and steeply inclined, so a small lateral change tilts that plane a
   lot and the femur sweeps a large arc. The sweep in phase 2 measures that
   response curve directly instead of asserting it.

===============================================================================
SIGN CONVENTION -- REPORTED ALONG body_right, POSITIVE = ANATOMICAL RIGHT
===============================================================================
Every lateral figure below is `dot(body_right)`, so POSITIVE means the
character's anatomical RIGHT, matching `author_layup.py`'s own keypose channels
(`drive_lat_m` et al are direct-signed along `body_right`). `geom.lateral` is
NOT used for any placement measurement here -- on this rig it points at the
character's LEFT, and #320 removed exactly that trap from the library.

Note this is the OPPOSITE convention to the table recorded in issue #335's
2026-08-05 comment, which reported "+lat = the character's LEFT". Same geometry,
flipped sign. The `drive_lat_m` figures printed by phase 3 are directly
substitutable into `author_layup.py`'s keypose table with no sign juggling,
which is the point of choosing this convention over that one.
"""
import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import blender_anim_lib as lib  # noqa: E402  (must follow the sys.path fix)

log = lib.log

# The drive leg, mirroring author_layup.DRIVE_KNEE_SIDE. Asserted against the
# authoring script after the exec below rather than trusted, so this file cannot
# silently measure the wrong leg if the spec's handedness ever changes.
DRIVE_SIDE = "L"

# Frames worth printing in full. 8/10 are the Active keyposes; 11 is the
# interpolated frame where #335 recorded the worst pose delta against the
# committed asset; 12 is the Active/Recovery slice boundary.
FOCUS_FRAMES = (8, 10, 11, 12)

# Lateral sweep, in metres along body_right, applied to the recorded apex ankle
# target. Wide enough to bracket both the current realised offset and zero.
SWEEP_M = [-0.20, -0.15, -0.12, -0.10, -0.097, -0.05, -0.025,
           0.0, 0.025, 0.05, 0.10, 0.15, 0.20]


class Record:
    """One `plant_foot` call, with the geometry it produced."""

    def __init__(self, frame, side, hip, target, toe_dir, knee, ankle, geom):
        self.frame = frame
        self.side = side
        self.hip = hip
        self.target = target
        self.toe_dir = toe_dir
        self.knee = knee
        self.ankle = ankle
        self.geom = geom


def _components(geom, vec):
    """`vec` decomposed onto (forward, up, body_right), in METRES."""
    return (geom.to_m(vec.dot(geom.forward)),
            geom.to_m(vec.dot(geom.up)),
            geom.to_m(vec.dot(geom.body_right)))


def _knee_for_lateral(record, lat_offset_m):
    """Re-solve the drive leg with the apex ankle target shifted laterally.

    Returns `(knee_components, ankle_components, hips_centre, fold_ratio)` --
    components relative to the DRIVE HIP, in metres, except the ratio.

    THE `frame_set` IS LOAD-BEARING, and omitting it silently measures a
    different pose. `plant_foot` reads the femur's head LIVE off the rig, and
    the femur head is owned by Hips -- so re-solving a frame-10 ankle target
    while the scene sits whereever the authoring run happened to stop poses the
    frame-10 foot off the WRONG hip. Measured while writing this: the frame-10
    fold ratio read 0.443 that way against 0.312 for the real baked pose, and
    the whole sensitivity curve flattened into a different, reassuring, and
    entirely fictional shape. Setting the frame first re-applies the baked
    action, which restores the correct Hips location; `plant_foot` then
    overrides the leg on top of it.

    Repeated calls at one frame do not compound: the femur's head is its parent's
    business, so every call re-solves from the identical hip position.
    """
    geom = record.geom
    arm = geom.arm
    bpy.context.scene.frame_set(record.frame)

    target = record.target + geom.body_right * geom.m(lat_offset_m)
    lib.plant_foot(arm, record.side, target, record.toe_dir, geom, frame=None)

    up_leg, leg, foot_b, _toe = lib.LEG_CHAIN[record.side]
    hip = arm.pose.bones[up_leg].head.copy()
    knee = arm.pose.bones[leg].head.copy()
    ankle = arm.pose.bones[foot_b].head.copy()
    hips_centre = arm.pose.bones[lib.HIPS].head.copy()
    fold = (target - hip).length / geom.leg_reach
    return (_components(geom, knee - hip), _components(geom, ankle - hip),
            hips_centre, fold)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    src = argv[0]

    records = []
    real_plant_foot = lib.plant_foot

    def spy_plant_foot(arm, side, ankle_target, toe_dir, geom, frame=None):
        # Read the hip BEFORE the solve: it is the femur's head, so it is the
        # frame of reference the solve works in.
        hip = arm.pose.bones[lib.LEG_CHAIN[side][0]].head.copy()
        result = real_plant_foot(arm, side, ankle_target, toe_dir, geom, frame=frame)
        records.append(Record(
            frame=frame, side=side, hip=hip,
            target=ankle_target.copy(), toe_dir=toe_dir.copy(),
            knee=arm.pose.bones[lib.LEG_CHAIN[side][1]].head.copy(),
            ankle=arm.pose.bones[lib.LEG_CHAIN[side][2]].head.copy(),
            geom=geom))
        return result

    lib.plant_foot = spy_plant_foot
    lib.export_fbx = lambda *a, **k: log("export suppressed -- measurement run")

    # ── phase 0: run the real authoring script ────────────────────────────────
    # sys.argv is rewritten because author_layup.py parses it the same way this
    # file does. The destination is never written (export_fbx is stubbed above),
    # but it must be present for the arg parse to succeed.
    author_path = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                               "author_layup.py")
    sys.argv = ["blender", "--", src, os.devnull]
    log(f"=== phase 0: running {author_path} with plant_foot instrumented ===")
    author_globals = {"__name__": "__author_layup__", "__file__": author_path}
    with open(author_path, encoding="utf-8") as fh:
        exec(compile(fh.read(), author_path, "exec"), author_globals)  # noqa: S102

    # The spec owns the handedness; this file must not assume it. If the layup
    # is ever re-authored off the other foot, fail loudly rather than silently
    # reporting the plant leg's knee as if it were the drive leg's.
    spec_side = author_globals["DRIVE_KNEE_SIDE"]
    if spec_side != DRIVE_SIDE:
        raise SystemExit(
            f"FATAL: author_layup.py drives off the {spec_side} leg but this "
            f"measurement is written for {DRIVE_SIDE}. Update DRIVE_SIDE.")

    geom = records[0].geom
    drive = {r.frame: r for r in records if r.side == DRIVE_SIDE}

    # ── phase 1: rest-pose hip offsets ────────────────────────────────────────
    # The claim under test is that the drive ankle's realised lateral position
    # differs from its SPECIFIED one because the spec measures from the hips
    # CENTRE while the femur hangs from an OFFSET hip. That is only true if the
    # hips are actually offset, so measure it rather than assuming it.
    log("=== phase 1: rest hip lateral offsets (m along body_right) ===")
    hips_rest = geom.arm.pose.bones[lib.HIPS].bone.head_local
    for side in ("L", "R"):
        head = geom.arm.pose.bones[lib.LEG_CHAIN[side][0]].bone.head_local
        _f, _u, lat = _components(geom, head - hips_rest)
        lib.report(f"rest_{side}_hip_lat_m", f"{lat:+.4f}")

    # ── phase 2: realised drive-leg geometry, per frame ───────────────────────
    # `spec_lat` re-derives what the keypose table asked for, from the ankle
    # target the run actually built, so the printed table can be compared
    # against `_KEYPOSES_RAW`'s `drive_lat_m` column directly. It is measured
    # from the HIPS CENTRE because that is what the spec's channel means; the
    # gap between it and `ankle_lat` (measured from the drive HIP) is the entire
    # subject of this script.
    log("=== phase 2: drive-leg geometry, per frame (m) ===")
    log("  knee_*/ankle_* are vs the DRIVE HIP; spec_lat is vs the HIPS CENTRE")
    log(f"{'frame':>5} {'spec_lat':>9} {'ankle_fwd':>10} {'ankle_up':>9} "
        f"{'ankle_lat':>10} {'knee_fwd':>9} {'knee_up':>9} {'knee_lat':>9} "
        f"{'fold':>6}")
    scene = bpy.context.scene
    for frame in sorted(drive):
        r = drive[frame]
        scene.frame_set(frame)
        hips_centre = geom.arm.pose.bones[lib.HIPS].head.copy()
        _f, _u, spec_lat = _components(geom, r.target - hips_centre)
        knee = _components(geom, r.knee - r.hip)
        ankle = _components(geom, r.ankle - r.hip)
        fold = (r.target - r.hip).length / geom.leg_reach
        mark = " <=" if frame in FOCUS_FRAMES else ""
        log(f"{frame:5d} {spec_lat:+9.4f} {ankle[0]:+10.4f} {ankle[1]:+9.4f} "
            f"{ankle[2]:+10.4f} {knee[0]:+9.4f} {knee[1]:+9.4f} "
            f"{knee[2]:+9.4f} {fold:6.3f}{mark}")

    # ── phase 3: lateral sensitivity sweep ────────────────────────────────────
    # `drive_lat_m` in the keypose table is measured from the hips CENTRE, so the
    # value to write back into the spec is the sweep offset PLUS whatever the
    # table already carries at that frame. Reported as `spec_drive_lat_m` so the
    # answer is read off the table, not re-derived by the reader.
    for apex in FOCUS_FRAMES:
        if apex not in drive:
            continue
        r = drive[apex]
        log(f"=== phase 3: lateral sweep at frame {apex} ===")
        log(f"{'d_lat':>7} {'spec_drive_lat_m':>17} {'ankle_lat':>10} "
            f"{'knee_fwd':>9} {'knee_up':>9} {'knee_lat':>9} {'fold':>6}")
        for d in SWEEP_M:
            knee, ankle, hips_centre, fold = _knee_for_lateral(r, d)
            target = r.target + geom.body_right * geom.m(d)
            _f, _u, spec_lat = _components(geom, target - hips_centre)
            log(f"{d:+7.3f} {spec_lat:+17.4f} {ankle[2]:+10.4f} "
                f"{knee[0]:+9.4f} {knee[1]:+9.4f} {knee[2]:+9.4f} {fold:6.3f}")

    print("MEASURE_OK")


main()
