"""Mirror the right-handed dribble clips into true left-handed twins (#294).

Run (once produces BOTH clips):
    "$BLENDER" --background --python-exit-code 1 \\
        --python tools/mirror_dribble_clips.py

`--python-exit-code 1` is MANDATORY. Blender's `--background --python` exits 0
even on an uncaught traceback, so without it a crashed mirroring run reports
SUCCESS to the pipeline. Every gate below raises `SystemExit` rather than
logging a warning specifically so this flag converts a failed proof into a
failed build.

Produces, from `assets/`:
    Dribble.fbx              -> dribble_idle_left.fbx  (+ .fbx.import)
    dribble_move_authored.fbx -> dribble_move_left.fbx  (+ .fbx.import)

Idempotent: each run re-derives both outputs from the pristine right-handed
sources; nothing here reads its own previous output.

═══════════════════════════════════════════════════════════════════════════════
THE ALGORITHM — rigid reflection of ARMATURE-SPACE pose matrices
═══════════════════════════════════════════════════════════════════════════════
Let S = diag(-1, 1, 1, 1) (armature-space sagittal reflection; `MIRROR` below).
Measured during #294 triage: the rig's lateral axis IS armature-space X — all
29 Left*/Right* bone heads mirror across X=0 to within 0.017 cm in the REST
pose, and the 7 unpaired bones (Hips, Spine, Spine1, Spine2, Neck, Head,
HeadTop_End) are exactly the midline chain. 29*2+7 = 65 = the full bone count.
That is treated as an established fact here (re-asserted as Gate 2, not
re-derived from a different axis).

For every frame, for every bone `b`:
    dst[partner(b)] = S @ src[b] @ S
where `partner(b)` swaps Left<->Right in the Mixamo name and is the identity
for the 7 midline bones. `S @ M @ S` is the correct conjugation — a point p
maps to Mp, so mirrored motion maps Sp to SMp = (SMS)(Sp) — and
det(S @ M @ S) = det(M) = 1, so it stays a proper rotation: no handedness
inversion, no flipped normals. Working in ARMATURE space (not bone-local
space) means bone roll and rest orientation fall out of Blender's own matrix
conversion for free — this is why no quaternion component is ever touched
directly. The Hips translation rides along automatically because full 4x4
matrices are mirrored, not just rotations.

Writes happen ROOT-FIRST (parent before child), with
`bpy.context.view_layer.update()` after every single assignment: setting
`pose_bone.matrix` decomposes the assignment against the bone's CURRENT
(live) parent matrix to produce `matrix_basis`, so a child written before its
parent has already been re-mirrored is silently wrong. Every destination bone
is keyframed (location + rotation_quaternion + scale) immediately after its
write, at an explicit `frame=` argument — this does NOT depend on the scene's
current frame, so, unlike the gait authors in `author_dribble_move.py`, this
script never needs to interleave `scene.frame_set()` with per-bone reads: the
entire source pose for every bone at every frame is captured into a plain
Python dict up front (`src_by_frame`), and every destination value is pure
matrix arithmetic from that dict. There is nothing left in the scene to read
mid-loop.

═══════════════════════════════════════════════════════════════════════════════
FORBIDDEN APPROACHES — all three were tried and rejected before this issue
═══════════════════════════════════════════════════════════════════════════════
- Negating quaternion components (`q.x = -q.x` etc.) is NOT a reflection.
  See `tools/rebuild_crossover_clips.gd:29-44`, which records this dead end.
- `bpy.ops.pose.copy()` / `bpy.ops.pose.paste(flipped=True)` do not work here:
  Mixamo bones are named `mixamorig:LeftHand` — "Left" in the MIDDLE — so
  Blender's `flip_side_name` does not recognise them. The matrix route is
  deterministic and self-verifiable; the operator route is not.
- Mirroring on the Godot side is rejected (#280) and stays rejected.

═══════════════════════════════════════════════════════════════════════════════
BONE NAMES — colon in Blender, underscore in Godot
═══════════════════════════════════════════════════════════════════════════════
After FBX import, Blender's bones are `mixamorig:LeftHand` (colon). Godot's
importer rewrites these to `mixamorig_LeftHand` (underscore) on its own side —
see `tools/blender_anim_lib.py` around line 62. This script runs entirely in
Blender, so it uses the colon form throughout.

═══════════════════════════════════════════════════════════════════════════════
WHY THE `.fbx.import` SIDECARS ARE VERBATIM COPIES OF THEIR SOURCES
═══════════════════════════════════════════════════════════════════════════════
`assets/Dribble.fbx.import` and `assets/dribble_move_authored.fbx.import` both
carry `animation/fps=30`, `animation/trimming=true`,
`animation/remove_immutable_tracks=true`. Elsewhere in this repo those
settings are flagged as WRONG for an authored clip that gets sliced by
source-time windows (see the authored-FBX-import-defaults trap in project
memory / handoff notes). That note does not apply here: `dribble_idle_left`
and `dribble_move_left` are each loaded WHOLE by Godot, never sliced, and the
overriding requirement is that a left clip and its right counterpart receive
IDENTICAL import processing — otherwise the two BlendSpace/state-machine
endpoints for a hand-side pair would end up with different track sets after
import, and their polarities would no longer be comparable. Symmetry beats the
general "these settings are wrong for authored clips" rule here. So
`write_import_sidecar()` below copies each source's `.fbx.import` byte-for-byte
except for the filename (in `source_file=` and `dest_files=`/`path=`) and the
`uid=` line, which is deleted outright so Godot regenerates a fresh one on
first import.

═══════════════════════════════════════════════════════════════════════════════
THE FIVE PROOF GATES
═══════════════════════════════════════════════════════════════════════════════
Gate 1 — pairing is total (exactly 65 bones / 29 pairs / 7 named midline).
Gate 2 — rest-pose symmetry re-measured (< 0.05 cm per pair). Runs ONCE,
         before the jobs, against the stock rig only — not per job. Rest
         geometry is a skeleton property and a Blender re-export rewrites it;
         see the comment above `gate2_rest_symmetry`.
Gate 3 — the mirror actually mirrored: every written pose matches
         S @ source @ S to a tight tolerance (this is the in-memory
         write-fidelity check, not a design proof — see Gate 4 for the one
         that can catch a wrong-axis or no-op mirror).
Gate 4 — the dominance-flip discriminator. A symmetric measurement (Gate 2/3's
         kind) passes on a broken mirror too, because this rig is symmetric to
         0.17 mm — that is exactly how bug #255 shipped green. So Gate 4
         asserts something that must genuinely DIFFER between polarities: the
         source clip's right hand has a large vertical pump range and the left
         a tiny one; the mirrored clip must show the reverse.

         SCOPE — read this before trusting Gate 4 for more than it does.
         `dst_by_frame` is DEFINED as S @ src @ S, and the translation column
         of S·M·S is S_rot @ M_trans = (-x, y, z) — Y is preserved bit-exactly.
         So `gate4_magnitude_deviation_pct` is TAUTOLOGICALLY 0.000 and cannot
         fail; it is reported as a consistency read-out, not as proof. The
         load-bearing half is the RATIO pair, because `dst_ratio` is computed
         from the mirrored clip's OWN internal L/R balance, not against the
         source: a no-op mirror (dst == src) yields 1/38.67 = 0.026 and goes
         red on the 5x floor.
         What Gate 4 does NOT catch — MEASURED by mutation, not reasoned:
         setting `MIRROR` to the identity while `partner_name` keeps swapping
         names passes Gate 4 at the full 38.67x, and passed Gates 1/2/3/5 as
         well (exit 0). That mutation is a pure bone-name swap. Gate 4b exists
         to catch it; see its docstring for the per-gate autopsy.
Gate 4b— laterality: the dribbling hand must end up on the OTHER SIDE of the
         body. The one property a name-swap cannot fake, and the runtime
         cross-check that the `MIRROR` constant in force agrees with the axis
         Gate 2 measured.
Gate 5 — round-trip: re-import the exported FBX into a reset Blender session
         and re-run the structural + Gate-3-style checks against it, to catch
         exporter/importer damage the in-memory checks cannot see.

DECISION (not specified by the issue brief): "a fresh Blender session" for
Gate 5 is implemented as `bpy.ops.wm.read_factory_settings(use_empty=True)` +
re-import, run inside the SAME Blender process — not a second `blender.exe`
subprocess. This is the established idiom this repo already uses for
round-trip proofs (`tools/compare_fbx_anim.py`'s `collect()`, and the "Round-
trip verified" step `author_dribble_move.py`'s history describes) — it fully
resets Blender's data-blocks, which is what actually matters for catching
exporter/importer damage, and keeps this tool self-contained.
"""
import os
import sys

import bpy
from mathutils import Matrix, Vector

# Blender runs this file as a script, not as a package member, so `tools/` is
# not importable by default. `--python <path>` does not add the script's own
# directory to sys.path the way `python <path>` does.
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import blender_anim_lib as lib  # noqa: E402  (must follow the sys.path fix)

FPS = 30  # both sources' .fbx.import carry animation/fps=30

# Armature-space sagittal reflection. See module docstring for the
# S @ M @ S conjugation derivation.
MIRROR = Matrix.Diagonal((-1.0, 1.0, 1.0, 1.0))

# The 7 midline bones, established fact per #294 triage (Gate 1 re-asserts,
# does not re-derive, this set).
MIDLINE_NAMES = {
    lib.HIPS, "mixamorig:Spine", "mixamorig:Spine1", "mixamorig:Spine2",
    "mixamorig:Neck", "mixamorig:Head", "mixamorig:HeadTop_End",
}

# ── proof tolerances (documented, not tuned to make a red run pass) ─────────
GATE1_EXPECTED_BONES = 65
GATE1_EXPECTED_PAIRS = 29
GATE1_EXPECTED_MIDLINE = 7
GATE2_REST_SYMMETRY_TOL_M = 0.0005  # 0.05 cm, per the issue brief
GATE3_ROT_TOL_DEG = 0.01
GATE3_LOC_TOL_UNITS = 1e-4  # armature units, per the issue brief
# Gate 4 is a DISCRIMINATOR, not a precision measurement: it exists to fail on
# a NO-OP mirror (see the Gate 4 SCOPE note above for what it does not cover).
# Measured: 38.67x on the idle clip, 22.61x on the move clip. The floor has to
# clear the lower of those with room to spare while staying far above a no-op's
# 1/22.61 = 0.044, and 5x sits between them by an order of magnitude either way,
# so it can be satisfied neither by accident nor by a marginal clip.
GATE4_MIN_DOMINANT_RATIO = 5.0
# Tautologically 0.000 -- kept as a read-out, not a proof. See the SCOPE note.
GATE4_MAGNITUDE_TOL_PCT = 2.0
# Gate 4b -- laterality. Measured: the dribbling hand sits 0.20-0.25 m off the
# midline, so a 0.05 m floor is ~4x clear of the value while still refusing to
# read a sign off a hand that is essentially centred.
GATE4B_MIN_LATERAL_M = 0.05
GATE4B_MAGNITUDE_TOL_PCT = 2.0
GATE5_ROT_TOL_DEG = GATE3_ROT_TOL_DEG
# DECISION (not specified by the issue brief): wider than Gate 3's 1e-4.
# Gate 3 is a pure in-memory decompose/recompose with no FBX export/import in
# the loop; Gate 5 adds exactly that round trip. Measured on the first run of
# this script: 1.13e-4 armature units (1.13 um) on `RightHandIndex4`, a
# fingertip 4 levels deep in the hand chain -- rotation delta was exactly
# 0.0 deg, so this is pure translation float-quantization compounding down the
# kinematic chain, the same documented phenomenon `preserve_frame()`'s
# docstring in blender_anim_lib.py measures independently (up to 0.85 um,
# "four orders of magnitude below any tolerance in this project"). 5e-4 units
# (5 um) gives ~4x headroom over the measured value while staying multiple
# orders of magnitude below any real defect, which would show up as
# millimetre/degree-scale errors, not single-digit micrometres.
GATE5_LOC_TOL_UNITS = 5e-4

log = lib.log
report = lib.report


def partner_name(name):
    """The mirror partner of a bone name: swap Left<->Right, identity for midline.

    Returns None if `name` is neither a recognised Left/Right bone nor a known
    midline bone -- Gate 1 treats that as a hard failure rather than silently
    leaving the bone untouched (an untouched bone would fall back to skeleton
    REST in Godot, the a45bd1d T-pose trap wearing a new hat).
    """
    if name in MIDLINE_NAMES:
        return name
    if "Left" in name:
        return name.replace("Left", "Right", 1)
    if "Right" in name:
        return name.replace("Right", "Left", 1)
    return None


def topo_order(arm):
    """Bone names in root-first (parent-before-child) order.

    Required for the write pass: `pose_bone.matrix = target` decomposes
    against the bone's CURRENT parent matrix, so a child must never be
    written before its parent's mirrored value is already live.
    """
    bones = arm.data.bones
    order = []
    visited = set()

    def visit(b):
        if b.name in visited:
            return
        if b.parent is not None:
            visit(b.parent)
        visited.add(b.name)
        order.append(b.name)

    for b in bones:
        visit(b)
    return order


# ═════════════════════════════════════════════════════════════════════════════
# Gate 1 — pairing is total
# ═════════════════════════════════════════════════════════════════════════════
def gate1_pairing(all_names):
    names = set(all_names)
    midline_found = sorted(n for n in names if n in MIDLINE_NAMES)
    pairs = set()
    unknown = []
    for n in names:
        if n in MIDLINE_NAMES:
            continue
        p = partner_name(n)
        if p is None or p not in names:
            unknown.append(n)
            continue
        pairs.add(frozenset((n, p)))

    report("gate1_bone_count", len(names))
    report("gate1_pair_count", len(pairs))
    report("gate1_midline_count", len(midline_found))
    report("gate1_midline_set", midline_found)

    if unknown:
        raise SystemExit(
            f"FATAL gate1: {len(unknown)} bone(s) are neither paired nor "
            f"midline: {sorted(unknown)}")
    if (len(names), len(pairs), len(midline_found)) != (
            GATE1_EXPECTED_BONES, GATE1_EXPECTED_PAIRS, GATE1_EXPECTED_MIDLINE):
        raise SystemExit(
            f"FATAL gate1: expected {GATE1_EXPECTED_BONES} bones / "
            f"{GATE1_EXPECTED_PAIRS} pairs / {GATE1_EXPECTED_MIDLINE} midline, "
            f"got {len(names)} / {len(pairs)} / {len(midline_found)}")
    if set(midline_found) != MIDLINE_NAMES:
        raise SystemExit(
            f"FATAL gate1: midline set mismatch, got {sorted(midline_found)} "
            f"expected {sorted(MIDLINE_NAMES)}")
    return [tuple(sorted(p)) for p in pairs], midline_found


# ═════════════════════════════════════════════════════════════════════════════
# Gate 2 — rest symmetry
# ═════════════════════════════════════════════════════════════════════════════
# DECISION (not specified by the issue brief): Gate 2 is measured ONCE, from
# `assets/Dribble.fbx` -- proven stock/unmangled below (its rest axes measure
# ~lateral=(1,0,0) / up=(0,1,0) / forward=(0,0,1), the raw Mixamo convention;
# `lateral` is a BASIS vector and on this rig it points at the character's
# LEFT, so do not read that (1,0,0) as "right" -- #320, #335) --
# and reused for BOTH jobs, rather than re-measured from each job's own source.
#
# Reason: `assets/dribble_move_authored.fbx` is itself a Blender re-export
# (#300). Blender's FBX exporter rewrites bone roll to its own convention, so
# a Blender-authored FBX's REST pose does not match the original rig's, even
# though its POSES (what this script actually mirrors) survive faithfully --
# this is exactly what that clip's own commit message documents ("poses
# survive; rest geometry does not"), and it is CONFIRMED here: measuring Gate
# 2 straight off `dribble_move_authored.fbx`'s own rest bones the first time
# this script ran gave a 70.53 cm "asymmetry" on `LeftHandMiddle4<->
# RightHandMiddle4` -- three orders of magnitude past any real defect, and an
# axis-convention artifact, not a mirror bug (the actual mirror never reads
# rest data at all; it operates entirely on per-frame ARMATURE-SPACE POSE
# matrices, which this rest corruption does not touch).
#
# Rest geometry is a property of the shared skeleton, not of any one clip's
# baked animation, so sourcing Gate 2 from the one file proven clean is
# correct rather than a workaround -- it is the same principle
# `blender_anim_lib.derive_axes()` already states for facing axes ("Derived,
# never hardcoded... Read from the RAW imported FBX -- never from a
# Player.tscn rig").
def gate2_rest_symmetry(arm, pairs, geom):
    bones = arm.data.bones
    worst = (0.0, None)
    for l, r in pairs:
        hl = bones[l].head_local
        hr = bones[r].head_local
        mirrored_l = Vector((-hl.x, hl.y, hl.z))
        err = geom.to_m((mirrored_l - hr).length)
        if err > worst[0]:
            worst = (err, f"{l}<->{r}")

    report("gate2_worst_rest_symmetry_cm", f"{worst[0] * 100.0:.6f}")
    report("gate2_worst_rest_pair", worst[1])
    if worst[0] > GATE2_REST_SYMMETRY_TOL_M:
        raise SystemExit(
            f"FATAL gate2: rest-pose asymmetry {worst[0] * 100.0:.6f} cm on "
            f"{worst[1]}, exceeds {GATE2_REST_SYMMETRY_TOL_M * 100.0} cm")


# ═════════════════════════════════════════════════════════════════════════════
# Gate 4 helper — the non-symmetric discriminator
# ═════════════════════════════════════════════════════════════════════════════
def hand_vertical_range_m(frames_dict, geom):
    """Per-hand vertical (armature-space Y) excursion relative to Hips, in metres.

    Deliberately raw armature-space Y, not a derived "up" axis -- the issue
    brief specifies "world/armature Y", and this is what distinguishes
    polarities (see module docstring, Gate 4).
    """
    ys = {"L": [], "R": []}
    for pose in frames_dict.values():
        hips_y = pose[lib.HIPS].translation.y
        ys["L"].append(pose["mixamorig:LeftHand"].translation.y - hips_y)
        ys["R"].append(pose["mixamorig:RightHand"].translation.y - hips_y)
    return {side: geom.to_m(max(v) - min(v)) for side, v in ys.items()}


def hand_lateral_offset_m(frames_dict, geom, lateral):
    """Per-hand mean SIGNED lateral offset from the Hips, in metres.

    Projected on `lateral` — the lateral axis measured ONCE from the stock rig,
    threaded in from `gate2_once()` — so this reads as "which side of the body"
    without hardcoding an axis.

    The SIGN is not anatomical and no caller may read it as such (#320): this
    is `geom.lateral`, which points at the character's LEFT on these rigs. Only
    the RELATIVE sign between two clips is meaningful, which is all Gate 4b
    asserts. An earlier version of this docstring claimed "positive = the rig's
    right"; it was wrong, and that same confusion once produced a spurious
    27.256% magnitude reading (see the note above). The parameter was itself
    still NAMED `right` until #335 -- the retracted claim outlived its
    retraction by living on in the identifier.

    `lateral` MUST NOT be re-derived from the job's own armature. `derive_axes()`
    reads REST orientation, and `dribble_move_authored.fbx` is a Blender
    re-export whose rest roll is rewritten (the same corruption that gives
    Gate 2 its false 70.53 cm reading there). MEASURED during #294: projecting
    on that file's own `geom.lateral` made this gate report a 27.256% magnitude
    deviation on a mirror that is provably exact. The arithmetic reason is
    sharp — `dst = S @ src @ S` gives `dst.trans = S_rot @ src.trans`, so two
    projections are equal-and-opposite ONLY if the axis is an eigenvector of S
    (i.e. ±X). A rest-corrupted axis carries y/z components and the readings
    stop mirroring, so the gate measures the axis rather than the clip.
    Bone LENGTHS survive that re-export, so `geom.to_m()` stays trustworthy
    per-job; only the axis direction has to come from the stock rig.
    """
    out = {}
    lateral = Vector(lateral)
    for side, bone in (("L", "mixamorig:LeftHand"), ("R", "mixamorig:RightHand")):
        vals = [(pose[bone].translation - pose[lib.HIPS].translation).dot(lateral)
                for pose in frames_dict.values()]
        out[side] = geom.to_m(sum(vals) / len(vals))
    return out


def gate4b_lateral_side(src_by_frame, dst_by_frame, geom, lateral):
    """The mirrored clip's dribbling hand must be on the OTHER SIDE of the body.

    WHY THIS GATE EXISTS -- it closes a hole the other four leave wide open,
    demonstrated by mutation during #294. Setting `MIRROR` to the identity
    while leaving `partner_name` swapping names produces
    `dst[LeftHand] = src[RightHand]` VERBATIM: every bone is placed at the
    absolute armature-space transform of its opposite number, with no
    reflection. That is a pure bone-name swap -- forbidden approach #2 in the
    module docstring, the measured #280 dead end -- and it passed Gates 1-5
    with exit 0:
      - Gate 1 passes: names are still swapped, so pairing is still total.
      - Gate 2 passes: it only ever reads REST geometry, never the mirror.
      - Gates 3/5 pass VACUOUSLY: both compare the written pose against
        `MIRROR @ src @ MIRROR`, i.e. against whatever MIRROR currently is.
        They prove the formula was applied faithfully, never that the formula
        is right.
      - Gate 4 passes: a name swap hands the mirrored left hand the source
        right hand's full vertical pump, so the dominance ratio is preserved
        at 38.67x -- the very number that was supposed to prove a real mirror.
    The one thing a name-swap CANNOT fake is laterality: it leaves the
    dribbling hand pumping on the SAME side of the body it started on, because
    no coordinate was ever reflected. So this gate asserts the sign flips.

    A wrong-AXIS reflection (diag(1,1,-1)) lands here too, for the same reason
    -- it preserves the lateral coordinate. Gate 2 independently establishes
    that X is the sagittal axis; this is the runtime cross-check that the
    constant actually in force agrees with that measurement.
    """
    src = hand_lateral_offset_m(src_by_frame, geom, lateral)
    dst = hand_lateral_offset_m(dst_by_frame, geom, lateral)
    # The source dribbles right-handed, the mirror must dribble left-handed, so
    # these are the two DOMINANT hands -- the ones Gate 4 just measured.
    src_dom, dst_dom = src["R"], dst["L"]

    # Labelled by the AXIS, not by anatomy: the sign of `geom.lateral` says
    # nothing about left/right (#320), and only the flip between the two rows
    # below is the finding.
    print("[mirror] Gate 4b -- dribbling-hand lateral offset (Hips-relative, "
          "signed along geom.lateral; only the FLIP is meaningful)")
    print(f"[mirror]   SOURCE    right hand = {src_dom:+.4f} m")
    print(f"[mirror]   MIRRORED  left  hand = {dst_dom:+.4f} m")

    report("gate4b_source_right_hand_lateral_m", f"{src_dom:+.4f}")
    report("gate4b_mirrored_left_hand_lateral_m", f"{dst_dom:+.4f}")
    report("gate4b_magnitude_deviation_pct",
           f"{abs(abs(dst_dom) - abs(src_dom)) / abs(src_dom) * 100.0:.3f}"
           if src_dom else "inf")

    if abs(src_dom) < GATE4B_MIN_LATERAL_M:
        raise SystemExit(
            f"FATAL gate4b: the source's dribbling hand sits only "
            f"{abs(src_dom):.4f} m off the midline (floor "
            f"{GATE4B_MIN_LATERAL_M} m) -- too close to centre for its SIGN to "
            f"be a meaningful reading, so this gate cannot discriminate. The "
            f"source measurement itself is suspect.")
    if (src_dom > 0.0) == (dst_dom > 0.0):
        raise SystemExit(
            f"FATAL gate4b: the dribbling hand did NOT change sides -- source "
            f"right hand {src_dom:+.4f} m, mirrored left hand {dst_dom:+.4f} m, "
            f"both on the same side of the midline. No coordinate was "
            f"reflected: this is a bone-name swap or a wrong-axis MIRROR, not a "
            f"mirror. See this gate's docstring.")
    dev_pct = abs(abs(dst_dom) - abs(src_dom)) / abs(src_dom) * 100.0
    if dev_pct > GATE4B_MAGNITUDE_TOL_PCT:
        raise SystemExit(
            f"FATAL gate4b: the hand changed sides but its distance from the "
            f"midline moved {dev_pct:.3f}% ({abs(src_dom):.4f} m -> "
            f"{abs(dst_dom):.4f} m), exceeding {GATE4B_MAGNITUDE_TOL_PCT}% -- a "
            f"reflection preserves it, so the transform is not rigid.")


def gate4_discriminator(src_by_frame, dst_by_frame, geom):
    src = hand_vertical_range_m(src_by_frame, geom)
    dst = hand_vertical_range_m(dst_by_frame, geom)
    src_ratio = src["R"] / src["L"] if src["L"] else float("inf")
    dst_ratio = dst["L"] / dst["R"] if dst["R"] else float("inf")
    mag_dev_pct = abs(dst["L"] - src["R"]) / src["R"] * 100.0 if src["R"] else float("inf")

    print("[mirror] Gate 4 -- hand vertical excursion (relative to Hips)")
    print(f"[mirror]   SOURCE    right={src['R']:.4f} m  left={src['L']:.4f} m  "
          f"ratio(R/L)={src_ratio:.2f}x")
    print(f"[mirror]   MIRRORED  left={dst['L']:.4f} m  right={dst['R']:.4f} m  "
          f"ratio(L/R)={dst_ratio:.2f}x")
    print(f"[mirror]   magnitude deviation (mirrored-left vs source-right) = "
          f"{mag_dev_pct:.3f}%")

    report("gate4_source_right_hand_range_m", f"{src['R']:.4f}")
    report("gate4_source_left_hand_range_m", f"{src['L']:.4f}")
    report("gate4_source_ratio_R_over_L", f"{src_ratio:.2f}")
    report("gate4_mirrored_left_hand_range_m", f"{dst['L']:.4f}")
    report("gate4_mirrored_right_hand_range_m", f"{dst['R']:.4f}")
    report("gate4_mirrored_ratio_L_over_R", f"{dst_ratio:.2f}")
    report("gate4_magnitude_deviation_pct", f"{mag_dev_pct:.3f}")

    if src_ratio < GATE4_MIN_DOMINANT_RATIO:
        raise SystemExit(
            f"FATAL gate4: source right/left ratio {src_ratio:.2f}x does not "
            f"clearly favour the right hand (floor {GATE4_MIN_DOMINANT_RATIO}x) "
            f"-- the source measurement itself is suspect.")
    if dst_ratio < GATE4_MIN_DOMINANT_RATIO:
        raise SystemExit(
            f"FATAL gate4: mirrored left/right ratio {dst_ratio:.2f}x does not "
            f"clearly favour the left hand (floor {GATE4_MIN_DOMINANT_RATIO}x) "
            f"-- the mirror did not flip dominance. A no-op mirror lands here; "
            f"a wrong-axis one does NOT (Gate 2 owns the axis) -- see the "
            f"Gate 4 SCOPE note in the module docstring.")
    if mag_dev_pct > GATE4_MAGNITUDE_TOL_PCT:
        raise SystemExit(
            f"FATAL gate4: mirrored-left range {dst['L']:.4f} m deviates "
            f"{mag_dev_pct:.3f}% from source-right range {src['R']:.4f} m, "
            f"exceeds {GATE4_MAGNITUDE_TOL_PCT}%")


# ═════════════════════════════════════════════════════════════════════════════
# The .fbx.import sidecar
# ═════════════════════════════════════════════════════════════════════════════
def write_import_sidecar(src_import_path, dst_fbx_path):
    """Copy `src_import_path` onto `dst_fbx_path + ".import"` verbatim, except
    the filename (in `source_file=` / `dest_files=` / `path=`) and the `uid=`
    line, which is deleted so Godot regenerates it on first import.

    See the module docstring for why this must be a verbatim copy rather than
    a fresh set of import parameters: the left and right clips of a hand-side
    pair must receive IDENTICAL import processing.
    """
    # newline="" on BOTH the read and the write disables Python's universal-
    # newline translation, so the copy keeps the source's exact line endings.
    # Without it Windows Python rewrites every LF as CRLF, and the repo's
    # `.gitattributes` normalizes to `eol=lf` -- the sidecar would then differ
    # from its source on every single line, which defeats the whole point of
    # this being a verbatim copy a reviewer can `diff`.
    with open(src_import_path, "r", encoding="utf-8", newline="") as fh:
        text = fh.read()

    src_base = os.path.basename(src_import_path)
    if src_base.endswith(".import"):
        src_base = src_base[: -len(".import")]
    dst_base = os.path.basename(dst_fbx_path)

    text = text.replace(src_base, dst_base)
    lines = [ln for ln in text.splitlines(keepends=True)
             if not ln.startswith("uid=")]
    text = "".join(lines)

    dst_import_path = dst_fbx_path + ".import"
    with open(dst_import_path, "w", encoding="utf-8", newline="") as fh:
        fh.write(text)
    log(f"wrote {dst_import_path} (copied from {src_import_path}, "
        f"filename {src_base!r} -> {dst_base!r}, uid line stripped)")
    return dst_import_path


# ═════════════════════════════════════════════════════════════════════════════
# per-job pipeline
# ═════════════════════════════════════════════════════════════════════════════
def process_job(src_path, dst_path, action_name, trusted_lateral):
    log(f"===== job: {src_path} -> {dst_path} (action {action_name!r}) =====")
    arm, f0, f1 = lib.load_source(src_path, FPS)
    scene = bpy.context.scene
    geom = lib.RigGeometry(arm)
    geom.log_summary()

    # Gate 1 IS per-job: it asserts that THIS file's own bone set is completely
    # paired, which is a property of the file. Gate 2 is NOT per-job -- it reads
    # REST geometry, which a Blender re-export rewrites, so it runs once in
    # main() against the one source proven stock. See GATE2_REFERENCE_SRC.
    all_names = [pb.name for pb in arm.pose.bones]
    pairs, midline = gate1_pairing(all_names)

    # ---- capture every source pose BEFORE anything is mutated --------------
    # Read-only pass. This is what lets the write pass below be pure matrix
    # arithmetic with no scene-frame dependency at all (see module docstring).
    src_by_frame = {}
    with lib.preserve_frame():
        for f in range(f0, f1 + 1):
            scene.frame_set(f)
            src_by_frame[f] = {pb.name: pb.matrix.copy() for pb in arm.pose.bones}

    frame_count = f1 - f0 + 1
    duration_s = (f1 - f0) / FPS
    report("source_frame_count", frame_count)
    report("source_duration_s", f"{duration_s:.6f}")

    topo = topo_order(arm)
    dst_by_frame = {}
    worst_g3_rot = (0.0, None, None)
    worst_g3_loc = (0.0, None, None)

    lib.enter_pose_mode(arm)
    for f in range(f0, f1 + 1):
        scene.frame_set(f)
        src_pose = src_by_frame[f]
        dst_pose = {}
        for d in topo:
            src_name = partner_name(d)
            target = MIRROR @ src_pose[src_name] @ MIRROR
            dst_pose[d] = target

            pb = arm.pose.bones[d]
            pb.matrix = target
            bpy.context.view_layer.update()

            # ---- Gate 3, inline: does the LIVE pose equal the target? ------
            # Re-reading rather than trusting `target` is the point -- this
            # catches a wrong write order or a decompose/recompose surprise,
            # not just a math bug in `target` itself.
            actual = pb.matrix
            dloc = (actual.translation - target.translation).length
            qa, qt = actual.to_quaternion(), target.to_quaternion()
            drot = abs(qa.rotation_difference(qt).angle) * lib.RAD_TO_DEG
            if drot > 180.0:
                drot = 360.0 - drot
            if drot > worst_g3_rot[0]:
                worst_g3_rot = (drot, f, d)
            if dloc > worst_g3_loc[0]:
                worst_g3_loc = (dloc, f, d)

            pb.keyframe_insert("location", frame=f)
            pb.keyframe_insert("rotation_quaternion", frame=f)
            pb.keyframe_insert("scale", frame=f)
        dst_by_frame[f] = dst_pose
    lib.leave_pose_mode()

    report("gate3_worst_rotation_deg", f"{worst_g3_rot[0]:.8f}")
    report("gate3_worst_rotation_at", f"frame={worst_g3_rot[1]} bone={worst_g3_rot[2]}")
    report("gate3_worst_location_units", f"{worst_g3_loc[0]:.8f}")
    report("gate3_worst_location_at", f"frame={worst_g3_loc[1]} bone={worst_g3_loc[2]}")
    if worst_g3_rot[0] > GATE3_ROT_TOL_DEG:
        raise SystemExit(
            f"FATAL gate3: worst write-fidelity rotation error "
            f"{worst_g3_rot[0]:.6f} deg (frame {worst_g3_rot[1]}, bone "
            f"{worst_g3_rot[2]}) exceeds {GATE3_ROT_TOL_DEG} deg")
    if worst_g3_loc[0] > GATE3_LOC_TOL_UNITS:
        raise SystemExit(
            f"FATAL gate3: worst write-fidelity location error "
            f"{worst_g3_loc[0]:.8f} units (frame {worst_g3_loc[1]}, bone "
            f"{worst_g3_loc[2]}) exceeds {GATE3_LOC_TOL_UNITS} units")

    gate4_discriminator(src_by_frame, dst_by_frame, geom)
    gate4b_lateral_side(src_by_frame, dst_by_frame, geom, trusted_lateral)

    # Reused library gates, exercised for real here since every one of the 65
    # bones is now keyed at every frame (a full mirror touches everything,
    # unlike the partial-bone gait authors).
    frames = list(range(f0, f1 + 1))
    lib.verify_all_bones_keyed(arm, expected_count=65, allow_leaf_ends=False)
    lib.verify_pose_unscaled(arm, frames)

    lib.export_fbx(arm, dst_path, action_name)
    import_sidecar = write_import_sidecar(src_path + ".import", dst_path)

    fbx_size = os.path.getsize(dst_path)
    report("export_file_size_bytes", fbx_size)

    gate5_roundtrip(dst_path, src_by_frame, f0, f1, frame_count, duration_s)

    log(f"===== job OK: {dst_path} =====")
    return {
        "dst_path": dst_path,
        "import_path": import_sidecar,
        "frame_count": frame_count,
        "duration_s": duration_s,
        "file_size": fbx_size,
    }


# ═════════════════════════════════════════════════════════════════════════════
# Gate 5 — round-trip
# ═════════════════════════════════════════════════════════════════════════════
def gate5_roundtrip(dst_path, src_by_frame, f0, f1, expected_frame_count, expected_duration_s):
    """Re-import the just-exported FBX into a reset Blender session and
    re-check structure + Gate-3-style pose fidelity against the ORIGINAL
    source. Catches exporter/importer damage the in-memory checks cannot see.
    """
    arm2, g0, g1 = lib.load_source(dst_path, FPS)
    scene = bpy.context.scene
    names2 = sorted(pb.name for pb in arm2.pose.bones)
    names1 = sorted(src_by_frame[f0].keys())

    reimport_frame_count = g1 - g0 + 1
    reimport_duration_s = (g1 - g0) / FPS
    report("gate5_reimport_frame_count", reimport_frame_count)
    report("gate5_reimport_duration_s", f"{reimport_duration_s:.6f}")
    report("gate5_reimport_bone_count", len(names2))

    problems = []
    if reimport_frame_count != expected_frame_count:
        problems.append(
            f"frame count {reimport_frame_count} != {expected_frame_count}")
    if abs(reimport_duration_s - expected_duration_s) > 1e-6:
        problems.append(
            f"duration {reimport_duration_s:.6f}s != {expected_duration_s:.6f}s")
    if len(names2) != GATE1_EXPECTED_BONES:
        problems.append(f"bone count {len(names2)} != {GATE1_EXPECTED_BONES}")
    if names2 != names1:
        only_a = sorted(set(names1) - set(names2))
        only_b = sorted(set(names2) - set(names1))
        problems.append(f"bone names differ: only-source={only_a} only-reimport={only_b}")
    if problems:
        for p in problems:
            log(f"gate5 STRUCTURAL PROBLEM: {p}")
        raise SystemExit("FATAL gate5: structural mismatch after round-trip; "
                          "pose comparison would not be meaningful")

    worst_rot = (0.0, None, None)
    worst_loc = (0.0, None, None)
    for i in range(reimport_frame_count):
        orig_f = f0 + i
        reimport_f = g0 + i
        scene.frame_set(reimport_f)
        src_pose = src_by_frame[orig_f]
        for d in names2:
            src_name = partner_name(d)
            expected = MIRROR @ src_pose[src_name] @ MIRROR
            actual = arm2.pose.bones[d].matrix

            dloc = (actual.translation - expected.translation).length
            qa, qe = actual.to_quaternion(), expected.to_quaternion()
            drot = abs(qa.rotation_difference(qe).angle) * lib.RAD_TO_DEG
            if drot > 180.0:
                drot = 360.0 - drot
            if drot > worst_rot[0]:
                worst_rot = (drot, reimport_f, d)
            if dloc > worst_loc[0]:
                worst_loc = (dloc, reimport_f, d)

    report("gate5_worst_rotation_deg", f"{worst_rot[0]:.8f}")
    report("gate5_worst_rotation_at", f"frame={worst_rot[1]} bone={worst_rot[2]}")
    report("gate5_worst_location_units", f"{worst_loc[0]:.8f}")
    report("gate5_worst_location_at", f"frame={worst_loc[1]} bone={worst_loc[2]}")

    if worst_rot[0] > GATE5_ROT_TOL_DEG:
        raise SystemExit(
            f"FATAL gate5: worst round-trip rotation error {worst_rot[0]:.6f} "
            f"deg (frame {worst_rot[1]}, bone {worst_rot[2]}) exceeds "
            f"{GATE5_ROT_TOL_DEG} deg")
    if worst_loc[0] > GATE5_LOC_TOL_UNITS:
        raise SystemExit(
            f"FATAL gate5: worst round-trip location error {worst_loc[0]:.8f} "
            f"units (frame {worst_loc[1]}, bone {worst_loc[2]}) exceeds "
            f"{GATE5_LOC_TOL_UNITS} units")


# ═════════════════════════════════════════════════════════════════════════════
# main
# ═════════════════════════════════════════════════════════════════════════════
JOBS = [
    ("assets/Dribble.fbx", "assets/dribble_idle_left.fbx", "dribbleidleleft"),
    ("assets/dribble_move_authored.fbx", "assets/dribble_move_left.fbx", "dribblemoveleft"),
]

# The one source whose REST pose is trustworthy: a straight Mixamo export that
# has never been through Blender. Gate 2 reads rest geometry, so it must be
# measured here and only here -- see the long comment above gate2_rest_symmetry.
GATE2_REFERENCE_SRC = "assets/Dribble.fbx"


def gate2_once():
    """Run Gate 2 once against the stock rig, and return its trusted right axis.

    Rest geometry belongs to the shared skeleton, not to any one clip's baked
    animation, so one measurement covers both jobs. Loading is destructive
    (`load_source` factory-resets the scene), but nothing is carried forward
    except pass/fail and the axis, so each job re-loading its own source
    afterwards is fine.

    The returned axis is the ONLY trustworthy lateral direction in this run --
    Gate 4b depends on it, and re-deriving it per job silently breaks that gate
    on the Blender-re-exported source. See `hand_lateral_offset_m`.
    """
    log(f"===== gate 2 (once, from {GATE2_REFERENCE_SRC}) =====")
    arm, _f0, _f1 = lib.load_source(GATE2_REFERENCE_SRC, FPS)
    geom = lib.RigGeometry(arm)
    pairs, _midline = gate1_pairing([pb.name for pb in arm.pose.bones])
    gate2_rest_symmetry(arm, pairs, geom)

    # `lateral`, not `body_right` (#320): every consumer of this axis compares
    # the two clips' projections on it RELATIVELY (Gate 4b asserts the sign
    # FLIPS between source and mirror), so the axis's anatomical direction is
    # irrelevant -- only that both clips are read against the SAME axis, and
    # that it is an eigenvector of MIRROR (asserted just below).
    lateral = Vector(geom.lateral)
    report("trusted_lateral_axis", tuple(round(v, 6) for v in lateral))
    # Gate 4b's arithmetic only mirrors if this axis is an eigenvector of
    # MIRROR, so assert that here rather than letting a skewed axis surface as a
    # confusing magnitude-deviation failure two gates later.
    # Threshold reasoning: the stock rig measures 7.285e-05 off-axis, so a 1e-4
    # gate would sit a mere 1.37x above the real value and flake on any minor
    # re-export. A skew of e can only perturb Gate 4b's magnitude reading by
    # roughly 2*e*(vertical excursion / lateral offset) ~= 2*e*0.6, so 1e-3 caps
    # the induced error at ~0.12% -- an order of magnitude under Gate 4b's 2%
    # tolerance -- while still being ~14x clear of the measured value. A
    # genuinely wrong axis has an off-axis component near 1.0, three orders of
    # magnitude above this, so nothing real can slip through.
    off_axis = max(abs(lateral.y), abs(lateral.z))
    report("trusted_lateral_off_axis_component", f"{off_axis:.8f}")
    if off_axis > 1e-3:
        raise SystemExit(
            f"FATAL gate2: the stock rig's lateral axis {tuple(lateral)} is not "
            f"aligned to armature X (off-axis component {off_axis:.8f}); "
            f"MIRROR = diag(-1,1,1) reflects X, so Gate 4b could not read a "
            f"meaningful side from it.")
    return lateral


def main():
    trusted_lateral = gate2_once()
    results = []
    for src, dst, action_name in JOBS:
        results.append(process_job(src, dst, action_name, trusted_lateral))
    for r in results:
        log(f"SUMMARY: {r['dst_path']} -- {r['frame_count']} frames, "
            f"{r['duration_s']:.3f}s, {r['file_size']} bytes")
    print("MIRROR_OK")


main()
