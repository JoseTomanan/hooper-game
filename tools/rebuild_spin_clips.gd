extends SceneTree
# Asset build tool (#310) — drafts the spin clip family into
# assets/locomotion.res by SLICING assets/spin_authored.fbx.
#
# Run:  godot --headless --path . -s tools/rebuild_spin_clips.gd
# Idempotent: re-running re-derives all three clips from the pristine FBX
# rather than stacking edits (the previous build is removed before the new
# one lands).
#
# Produces THREE LOOP_NONE one-shots — spin is UNHANDED (see the "trap B"
# section below), so — like hesitation (#307), retreat dribble (#305),
# step-back (#306), jab step (#304), layup (#313) and contest (#314) — this
# tool slices THREE clips, not six:
#   spinstartup     8 ticks / 0.13333 s
#   spinactive      6 ticks / 0.10000 s
#   spinrecovery   10 ticks / 0.16667 s
#
# ── TRAP A: the clip must NOT rotate the root, and G4 is why this matters here ─
# Player heading is SERVER-AUTHORITATIVE (ADR-0010): Hooper.Player.
# SpinHeadingMath drives the ~180 deg arc as gameplay state, integrated into
# Move(). A clip that ALSO rotated the root would double-rotate on the
# authoritative roles and fight reconciliation on the client's remote copy,
# whose broadcast heading is ~1 RTT stale — a defect that shows up ONLY under
# network conditions and is expensive to debug from a visual report.
#
# tools/author_spin.py closes this at authoring time by PINNING the Hips basis
# and keying it every frame, so the exported Hips rotation track is CONSTANT.
# G4 below re-proves it on the SLICED clips, and it is not redundant with the
# Blender-side gate: a constant track is exactly what
# `animation/remove_immutable_tracks` DROPS, and a dropped Hips rotation track
# does not merely lose the pin — it rest-falls the pelvis at full weight
# (the a45bd1d trap). So G4 asserts BOTH that the track survived AND that it is
# still constant. assets/spin_authored.fbx.import sets the flag to false; G4 is
# what makes that a checked fact rather than a hopeful one (the #297 lesson —
# a silently-ignored import flag killed that issue's first fix attempt).
#
# ── TRAP B: unhanded, and NOT because the move has no direction ───────────────
# MoveAnimResolver.HandedMoves' own docstring: spin swaps the ball hand on the
# LAST Active tick, not at Active-entry, so OriginHand's phase-conditioned
# formula is WRONG for 5 of its 6 Active ticks. Hence three clips, and hence
# author_spin.py poses both arms from ONE mirrored channel set. SpinAnimTest's
# `spin-stays-unsuffixed` scenario is the standing regression guard.
#
# ── G5: the turn has to be VISIBLE somewhere, and the hips are not it ─────────
# With the root pinned, the ONLY thing in this clip that says "the body came
# around" is the shoulder-relative-to-hip twist reversing across Active. G5
# measures it on the sliced clips by forward kinematics and asserts the
# reversal — opposite signs at Startup's end and Active's end, both with real
# magnitude. Without G5 a clip that lost its spine twist entirely would still
# satisfy G1-G4 and every length/coverage check, and would ship as a man
# standing still while the engine spun him.
#
# G5 asserts OPPOSITE SIGNS rather than a specific sign per boundary, on
# purpose: this tool measures in Godot's SKELETON space while author_spin.py
# measures in Blender's ARMATURE space, and the FBX import applies a coordinate
# conversion between them, so the two are not obliged to agree on handedness.
# "The twist reversed, with magnitude at both ends" is the claim either way, and
# it is NOT the abs()-blind-to-sign form (#339) — a clip that never reverses
# fails it, which is the whole point. Both raw signed values are printed so a
# reviewer can see the arc rather than a verdict.
#
# ── Why this is a SLICE, not a compose ───────────────────────────────────────
# tools/author_spin.py (headless Blender, #315's blender_anim_lib machinery)
# already authored the full Startup/Active/Recovery arc as hand-keyed IK poses,
# baked at 60 Hz, on ONE timeline. This tool's job is therefore only to resample
# ("slice") the three named windows out of that timeline and then PROVE
# geometrically that what got sliced is what the issue asked for.
#
# The proofs are RE-RUN here rather than inherited from the Blender side on
# purpose: the FBX round-trip, the importer's fps/trimming/immutable-track
# settings, and `_slice`'s resampling are exactly the machinery that has
# silently corrupted clips in this repo before (#281, #295, #297).
#
# ── The Mixamo bone-name-prefix trap (read before touching bone_of/_resolve) ─
# In Blender the bones are named `mixamorig:Hips` (colon). Godot 4.6+'s `ufbx`
# importer imports Mixamo-prefixed bones as `mixamorig_Hips` (UNDERSCORE).
# `_resolve_bone()` tries BOTH forms and reports which form actually matched,
# so a silent zero-match can never hide behind a green run.
#
# ── The a45bd1d full-body-coverage trap ──────────────────────────────────────
# A single-clip AnimationTree state plays at FULL WEIGHT, and Godot's
# AnimationMixer writes every bone the active clip does NOT track to the
# skeleton's rest transform. author_spin.py's own
# `verify_all_bones_keyed(expected_count=52)` gate proves the SOURCE carries
# full-body coverage; `_assert_complete()` below re-proves that every SLICE
# inherits it verbatim.
#
# ── The `Armature/` prefix trap (README trap 13, #281) ───────────────────────
# Blender's FBX export wraps the skeleton in an Armature object, so a track
# imported from spin_authored.fbx reads "Armature/Skeleton3D:mixamorig_Hips" —
# one level deeper than scenes/Player.tscn's rig, whose skeleton sits directly
# at "Skeleton3D". An unresolvable track binds to nothing and the clip plays as
# a SILENT no-op. `_rebase_path()` strips the prefix on every track, and
# `_assert_complete()` REJECTS (not skips) any surviving `Armature/`-prefixed
# path or any path with no bone subname.
#
# ── Where the three windows come from ────────────────────────────────────────
# Hardcoded, not derived: author_spin.py's frame layout is DETERMINISTIC BY
# CONSTRUCTION — it keys its timeline at exact times computed from Spin's own
# frame data (8/6/10 ticks @ 60 Hz) and the import sets `trimming=false`, so
# those source times land exactly where the docstring says. This tool ASSERTS
# the guarantee (the source clip's total length) so a silently-retrimmed or
# wrong-fps import fails loudly instead of slicing garbage.
#
#   source seconds        segment      ticks
#   0.00000 -> 0.13333    Startup       8
#   0.13333 -> 0.23333    Active        6
#   0.23333 -> 0.40000    Recovery     10
#
# ── Cosmetic-only (issue #310's standing constraint) ─────────────────────────
# This tool writes ONE file: assets/locomotion.res. It reads no gameplay
# constant and changes no gameplay behaviour. In particular it never touches
# Spin.DefaultFrameData, SpinHeadingMath, BallState or HasDribbled. The tick
# counts below are DUPLICATED from Spin's frame data for slicing, never read
# back into it.

const LIB_PATH := "res://assets/locomotion.res"
const SRC_FBX := "res://assets/spin_authored.fbx"
# Matches author_spin.py's ACTION_NAME -- export_fbx() renames both the Blender
# action AND the scene to this so Godot's importer names the resulting
# AnimationPlayer take after it.
const SRC_CLIP := "spin"

# Physics ticks per second (project.godot physics/common/physics_ticks_per_second).
const TPS := 60.0

# The node-path prefix Blender's Armature object wrapper adds; see _rebase_path.
const ARMATURE_PREFIX := "Armature/"

# Spin's frame data (scripts/Input/Spin.cs DefaultFrameData). Duplicated here
# because GDScript cannot read the C# constant -- so the duplication is made
# SAFE rather than avoided: SpinAnimTest's `spin-segment-lengths` scenario
# asserts each clip's length equals Spin.DefaultFrameData's own tick count / 60,
# reading the C# side directly. Retune the move without re-running this tool and
# that harness scenario goes red and names this file.
const STARTUP_TICKS := 8
const ACTIVE_TICKS := 6
const RECOVERY_TICKS := 10

# Source-time windows, matching author_spin.py's frame table exactly (frame
# numbers there ARE physics ticks at 60 Hz: 0/8/14/24).
const STARTUP := [0.0 / 60.0, 8.0 / 60.0]
const ACTIVE := [8.0 / 60.0, 14.0 / 60.0]
const RECOVERY := [14.0 / 60.0, 24.0 / 60.0]

# The producer exports frame_start=0, frame_end=24 (TOTAL_TICKS in
# author_spin.py), so the imported clip's length must be ~24/60 s. A silently-
# retrimmed or wrong-fps import would shift every window above out from under
# the actual keyed poses.
const EXPECTED_SRC_LENGTH_S := 24.0 / 60.0
const SRC_LENGTH_TOL_S := 0.02

const NAMES := ["spinstartup", "spinactive", "spinrecovery"]

# G3 legibility floor (#296's actual complaint -- Startup and Recovery must read
# as visibly different poses). Matches author_spin.py's own
# STARTUP_END_VS_RECOVERY_END_MIN_DEG = 15.0 gate -- this tool re-proves it on
# the SLICED clips rather than trusting the source's Blender-side proof to
# survive the slice untouched. Blender-side measured 55.579 deg.
const STARTUP_VS_RECOVERY_MIN_DEG := 15.0

const HIPS_BONE := "mixamorig_Hips"
const HIP_L_BONE := "mixamorig_LeftUpLeg"
const HIP_R_BONE := "mixamorig_RightUpLeg"
const ARM_L_BONE := "mixamorig_LeftArm"
const ARM_R_BONE := "mixamorig_RightArm"

# G4 (TRAP A): the largest angular difference between any two keys on a slice's
# Hips ROTATION track. author_spin.py pins that basis, so this is ZERO by
# construction and the tolerance is float/resampling noise headroom, not a
# budget. If it ever reads a real number, the clip started rotating the root and
# the whole point of #310 is lost.
const HIPS_ROTATION_CONSTANT_TOL_DEG := 0.5

# G5: the shoulder-relative-to-hip yaw magnitude required at BOTH ends of the
# reversal. author_spin.py measures +30.4 / -30.4 deg Blender-side; this floor
# sits at two thirds of that so a retune has room.
const TWIST_MIN_DEG := 20.0

var _skel: Skeleton3D = null
var _right := Vector3.ZERO
var _forward := Vector3.ZERO
var _up := Vector3.UP


func bone_of(np: NodePath) -> String:
	return "" if np.get_subname_count() == 0 else String(np.get_subname(0))


# The node path an AnimationPlayer under scenes/Player.tscn can actually
# resolve. See header — Blender's FBX export wraps the skeleton in an Armature
# object, so a track imported from spin_authored.fbx reads
# "Armature/Skeleton3D:mixamorig_Hips" — one level deeper than the rig.
func _rebase_path(np: NodePath) -> NodePath:
	var s := String(np)
	if s.begins_with(ARMATURE_PREFIX):
		return NodePath(s.substr(len(ARMATURE_PREFIX)))
	return np


# The Mixamo bone-name-prefix trap (see header): try the name as given, then
# the opposite colon/underscore form. Returns -1 only if NEITHER form resolves.
func _alt_bone_name(name: String) -> String:
	if name.begins_with("mixamorig:"):
		return "mixamorig_" + name.substr(len("mixamorig:"))
	if name.begins_with("mixamorig_"):
		return "mixamorig:" + name.substr(len("mixamorig_"))
	return name


# Returns [bone_index, form_used] where form_used is "as-given", "alt", or
# "unresolved". Called once per track by _assert_complete, which is what lets
# the report print an honest match count instead of assuming one spelling.
func _resolve_bone(name: String) -> Array:
	var idx := _skel.find_bone(name)
	if idx >= 0:
		return [idx, "as-given"]
	var alt := _alt_bone_name(name)
	if alt != name:
		idx = _skel.find_bone(alt)
		if idx >= 0:
			return [idx, "alt"]
	return [-1, "unresolved"]


func _initialize() -> void:
	var lib = load(LIB_PATH)
	if lib == null or not (lib is AnimationLibrary):
		push_error("[rebuild-spin] failed to load AnimationLibrary at %s" % LIB_PATH)
		quit(1)
		return

	_skel = _find(load("res://assets/Y Bot.fbx").instantiate(), "Skeleton3D")
	if _skel == null:
		push_error("[rebuild-spin] could not find a Skeleton3D in Y Bot.fbx.")
		quit(1)
		return

	if not _derive_body_axes():
		quit(1)
		return

	var packed = load(SRC_FBX)
	if packed == null:
		push_error("[rebuild-spin] failed to load %s" % SRC_FBX)
		quit(1)
		return
	var ap: AnimationPlayer = _find(packed.instantiate(), "AnimationPlayer")
	if ap == null:
		push_error("[rebuild-spin] %s has no AnimationPlayer." % SRC_FBX)
		quit(1)
		return
	if not ap.has_animation(SRC_CLIP):
		push_error("[rebuild-spin] %s has no AnimationPlayer clip '%s' (has: %s)"
			% [SRC_FBX, SRC_CLIP, str(ap.get_animation_list())])
		quit(1)
		return
	var src: Animation = ap.get_animation(SRC_CLIP)
	print("[rebuild-spin] source '%s': len=%.5f tracks=%d"
		% [SRC_CLIP, src.length, src.get_track_count()])

	# ── Sanity: the source must be the full 0..24-frame export, not a silently
	# retrimmed or wrong-fps import.
	if absf(src.length - EXPECTED_SRC_LENGTH_S) > SRC_LENGTH_TOL_S:
		push_error("[rebuild-spin] source length %.5f s is not within %.3f s of the expected "
			% [src.length, SRC_LENGTH_TOL_S] + "%.5f s (24/60 s @ 60 fps) -- the import may have been "
			% EXPECTED_SRC_LENGTH_S + "retrimmed or baked at the wrong fps, which would silently shift "
			+ "every hardcoded slice window in this file off the authored poses.")
		quit(1)
		return

	# What a SLICE is expected to carry, derived from the source by applying the
	# same two filters _slice() applies -- never the source's raw counts.
	var src_rot := 0
	var src_total := 0
	for i in src.get_track_count():
		var ty := src.track_get_type(i)
		if ty != Animation.TYPE_ROTATION_3D and ty != Animation.TYPE_POSITION_3D:
			continue
		if bone_of(src.track_get_path(i)) == "":
			continue
		src_total += 1
		if ty == Animation.TYPE_ROTATION_3D:
			src_rot += 1
	print("[rebuild-spin] source has %d tracks; %d expected per slice after dropping "
		% [src.get_track_count(), src_total]
		+ "SCALE (fights PlayerRigScaler) and the Armature object node (unbindable on Player.tscn).")

	# ── Slice the three windows ──────────────────────────────────────────────
	var windows := [STARTUP, ACTIVE, RECOVERY]
	var ticks := [STARTUP_TICKS, ACTIVE_TICKS, RECOVERY_TICKS]

	var built := {}
	for i in 3:
		var w: Array = windows[i]
		built[NAMES[i]] = _slice(src, w[0], w[1], ticks[i])

	# ── G1: existence, loop mode, exact length ───────────────────────────────
	var g1_ok := true
	for name in built:
		var anim: Animation = built[name]
		var idx := _name_tick_index(name)
		var expected_len := float(ticks[idx]) / TPS
		var len_ok := absf(anim.length - expected_len) <= 1e-4
		var loop_ok := anim.loop_mode == Animation.LOOP_NONE
		print("[rebuild-spin] G1 '%s': length=%.5f (expect %.5f) loop_mode=%d %s"
			% [name, anim.length, expected_len, anim.loop_mode, "OK" if (len_ok and loop_ok) else "FAIL"])
		if not (len_ok and loop_ok):
			g1_ok = false
	if not g1_ok:
		push_error("[rebuild-spin] G1 FAILED -- one or more clips has the wrong length or loop_mode.")
		quit(1)
		return

	# ── G2: track-count parity + full bone resolution (both name forms) ─────
	var g2_ok := true
	var form_counts := {"as-given": 0, "alt": 0, "unresolved": 0}
	for name in built:
		if not _assert_complete(built[name], name, src_rot, src_total, form_counts):
			g2_ok = false
	print("[rebuild-spin] G2 bone-name resolution across all three clips: as-given=%d alt=%d unresolved=%d"
		% [form_counts["as-given"], form_counts["alt"], form_counts["unresolved"]])
	if not g2_ok:
		quit(1)
		return
	if form_counts["unresolved"] > 0:
		push_error("[rebuild-spin] G2 FAILED -- %d bone tracks did not resolve on Y Bot in EITHER "
			% form_counts["unresolved"] + "name form.")
		quit(1)
		return
	var resolved_total: int = form_counts["as-given"] + form_counts["alt"]
	if resolved_total <= 0:
		push_error("[rebuild-spin] G2 FAILED -- resolved count is %d; a name-matching gate that "
			% resolved_total + "matches nothing passes vacuously.")
		quit(1)
		return

	var startup: Animation = built[NAMES[0]]
	var active: Animation = built[NAMES[1]]
	var recovery: Animation = built[NAMES[2]]

	# ── G3: Startup's END pose vs Recovery's END pose ────────────────────────
	# NOT a whole-clip-start-vs-whole-clip-end comparison -- Startup's own LAST
	# frame (the fully-coiled plant) vs Recovery's own LAST frame (unwound, new
	# lead foot planted) is the comparison that actually tests #296.
	var g3_delta := _pose_delta_at(startup, startup.length, recovery, recovery.length)
	print("[rebuild-spin] G3 startup-end vs recovery-end max bone delta = %.1f deg (want >= %.1f)"
		% [g3_delta, STARTUP_VS_RECOVERY_MIN_DEG])
	if g3_delta < STARTUP_VS_RECOVERY_MIN_DEG:
		push_error("[rebuild-spin] G3 FAILED: only %.1f deg (< %.1f) -- Startup's end pose and "
			% [g3_delta, STARTUP_VS_RECOVERY_MIN_DEG] + "Recovery's end pose do not read as distinct (#296).")
		quit(1)
		return

	# ── G4 (TRAP A): every slice carries a Hips ROTATION track, and it is
	# CONSTANT. See the header for why both halves are load-bearing: a missing
	# track rest-falls the pelvis, a varying one double-rotates against
	# SpinHeadingMath.
	for name in built:
		if not _assert_hips_rotation_pinned(built[name], name):
			quit(1)
			return

	# ── G5: the twist REVERSES across Active ─────────────────────────────────
	# With the root pinned by G4, this is the only thing left in the clip that
	# says the body came around.
	var twist_su := _twist_deg(startup, startup.length)
	var twist_ac := _twist_deg(active, active.length)
	print("[rebuild-spin] G5 shoulder-vs-hip yaw: startup-end=%+.2f deg active-end=%+.2f deg "
		% [twist_su, twist_ac] + "(each needs magnitude >= %.1f, and OPPOSITE signs)" % TWIST_MIN_DEG)
	if absf(twist_su) < TWIST_MIN_DEG or absf(twist_ac) < TWIST_MIN_DEG:
		push_error("[rebuild-spin] G5 FAILED: shoulder-vs-hip yaw magnitudes are %.2f / %.2f deg "
			% [absf(twist_su), absf(twist_ac)] + "(want >= %.1f each). The spine twist is the ONLY "
			% TWIST_MIN_DEG + "rotation this clip is allowed to carry (the root is pinned, G4), so "
			+ "without it the clip reads as a man standing still while the engine spins him.")
		quit(1)
		return
	if twist_su * twist_ac >= 0.0:
		push_error("[rebuild-spin] G5 FAILED: the yaw did NOT reverse -- startup-end %+.2f and "
			% twist_su + "active-end %+.2f share a sign. Handoff 10: 'shoulder twist carries "
			% twist_ac + "through from ~+30 to ~-30 relative to the hips'. Same-signed means the "
			+ "shoulders leaned and stayed leaned; the hips never passed them.")
		quit(1)
		return

	# ── Save ─────────────────────────────────────────────────────────────────
	# Idempotency: drop any previous build first, so re-running re-derives from
	# the pristine FBX rather than stacking edits.
	for name in built:
		if lib.has_animation(name):
			lib.remove_animation(name)
		lib.add_animation(name, built[name])

	var err := ResourceSaver.save(lib, LIB_PATH)
	if err != OK:
		push_error("[rebuild-spin] ResourceSaver.save failed with error %d" % err)
		quit(1)
		return

	print("[rebuild-spin] saved %s; clips now: %s" % [LIB_PATH, str(lib.get_animation_list())])
	quit(0)


func _name_tick_index(name: StringName) -> int:
	var s := String(name)
	if s.contains("startup"):
		return 0
	if s.contains("active"):
		return 1
	return 2


# ── Body axes (verbatim rebuild_hesitation_clips.gd approach) ─────────────────
func _derive_body_axes() -> bool:
	var foot := _skel.find_bone("mixamorig_LeftFoot")
	var toe := _skel.find_bone("mixamorig_LeftToeBase")
	if foot < 0 or toe < 0:
		push_error("[rebuild-spin] Y Bot has no LeftFoot/LeftToeBase to derive facing from.")
		return false
	var forward: Vector3 = _skel.get_bone_global_rest(toe).origin - _skel.get_bone_global_rest(foot).origin
	forward.y = 0.0
	if forward.length() < 0.001:
		push_error("[rebuild-spin] Y Bot's foot->toe vector is vertical; cannot derive facing.")
		return false
	_forward = forward.normalized()
	_right = _forward.cross(_up).normalized()

	var lh := _skel.find_bone("mixamorig_LeftHand")
	var rh := _skel.find_bone("mixamorig_RightHand")
	if lh < 0 or rh < 0:
		push_error("[rebuild-spin] Y Bot has no LeftHand/RightHand to verify the lateral axis.")
		return false
	var lh_lat: float = _skel.get_bone_global_rest(lh).origin.dot(_right)
	var rh_lat: float = _skel.get_bone_global_rest(rh).origin.dot(_right)
	print("[rebuild-spin] axes: forward=%s right=%s | rest hand lateral L=%+.4f R=%+.4f"
		% [_forward, _right, lh_lat, rh_lat])
	if not (rh_lat > 0.0 and lh_lat < 0.0):
		push_error("[rebuild-spin] the derived right axis puts the RIGHT hand at %+.4f and the LEFT "
			% rh_lat + "at %+.4f -- it is inverted (or the rig's bone names lie)." % lh_lat)
		return false
	return true


# ── G4 (TRAP A) ───────────────────────────────────────────────────────────────
# The Hips ROTATION track must EXIST and be CONSTANT. Both halves matter, and
# they catch different defects — see the header.
func _assert_hips_rotation_pinned(anim: Animation, name: StringName) -> bool:
	var res := _resolve_bone(HIPS_BONE)
	if res[0] < 0:
		push_error("[rebuild-spin] G4 FAILED on '%s': bone '%s' does not resolve on Y Bot in either "
			% [name, HIPS_BONE] + "name form, so the root-rotation claim cannot be measured at all. "
			+ "Refusing rather than reporting a confident 0.00 deg, which is this gate's PASSING value.")
		return false

	var track := -1
	for i in anim.get_track_count():
		if anim.track_get_type(i) != Animation.TYPE_ROTATION_3D:
			continue
		var b := _resolve_bone(bone_of(anim.track_get_path(i)))
		if b[0] == res[0]:
			track = i
			break
	if track < 0:
		push_error("[rebuild-spin] G4 FAILED on '%s': there is NO Hips rotation track. A missing "
			% name + "track does not merely lose the pin -- Godot's AnimationMixer writes every "
			+ "untracked bone to skeleton REST at full weight, so the pelvis would snap to the "
			+ "T-pose orientation the instant this state is entered (the a45bd1d trap). The likely "
			+ "cause is animation/remove_immutable_tracks having been re-enabled in "
			+ "assets/spin_authored.fbx.import: author_spin.py pins the Hips basis, so that track is "
			+ "CONSTANT, which is exactly what that flag drops.")
		return false

	var keys := anim.track_get_key_count(track)
	if keys < 2:
		push_error("[rebuild-spin] G4 FAILED on '%s': the Hips rotation track has %d key(s). A "
			% [name, keys] + "constancy claim over fewer than two keys is vacuous.")
		return false
	var first: Quaternion = anim.rotation_track_interpolate(track, 0.0)
	var worst := 0.0
	for k in keys:
		var q: Quaternion = anim.track_get_key_value(track, k)
		var d: float = clampf(absf(first.normalized().dot(q.normalized())), -1.0, 1.0)
		worst = maxf(worst, rad_to_deg(2.0 * acos(d)))
	print("[rebuild-spin] G4 '%s': Hips rotation track present (%d keys), max deviation from key 0 = "
		% [name, keys] + "%.4f deg (tol %.2f)" % [worst, HIPS_ROTATION_CONSTANT_TOL_DEG])
	if worst > HIPS_ROTATION_CONSTANT_TOL_DEG:
		push_error("[rebuild-spin] G4 FAILED on '%s': the Hips rotation varies by %.4f deg (tol %.2f). "
			% [name, worst, HIPS_ROTATION_CONSTANT_TOL_DEG]
			+ "THIS CLIP MUST NOT ROTATE THE ROOT -- player heading is server-authoritative "
			+ "(ADR-0010, SpinHeadingMath), so a clip that also turns the body double-rotates on the "
			+ "authoritative roles and fights reconciliation on the client's remote copy. Express the "
			+ "turn as SHOULDER-relative-to-HIP twist, never as hip rotation.")
		return false
	return true


# ── G5 ────────────────────────────────────────────────────────────────────────
# Signed shoulder-span-relative-to-hip-span yaw at time `t` of `anim`, in
# DEGREES. The mirror of author_spin.py's own `_twist_deg`, re-derived on the
# SLICED resource by forward kinematics.
func _twist_deg(anim: Animation, t: float) -> float:
	var hip := _pose_origin(anim, t, HIP_R_BONE) - _pose_origin(anim, t, HIP_L_BONE)
	var sho := _pose_origin(anim, t, ARM_R_BONE) - _pose_origin(anim, t, ARM_L_BONE)
	return _signed_yaw_deg(hip, sho)


# Signed angle from `a` to `b` about `_up`, right-hand rule, in DEGREES. Both
# are projected onto the horizontal plane first, so this is pure yaw.
#
# Returns NAN — never 0.0 — on a degenerate projection. 0.0 would be a
# *passing* magnitude for nothing and a *failing* one for G5's magnitude check,
# so a silent degradation would be confusingly wrong in both directions; NAN
# propagates through the comparisons as false and fails the gate closed
# (measurement-helpers-must-poison-on-failure, #305).
func _signed_yaw_deg(a: Vector3, b: Vector3) -> float:
	var pa := a - _up * a.dot(_up)
	var pb := b - _up * b.dot(_up)
	if pa.length() < 1e-4 or pb.length() < 1e-4:
		return NAN
	pa = pa.normalized()
	pb = pb.normalized()
	return rad_to_deg(atan2(pa.cross(pb).dot(_up), pa.dot(pb)))


# ── Slicing (verbatim rebuild_jumpshot_clips.gd / rebuild_hesitation_clips.gd
# primitive) ───────────────────────────────────────────────────────────────────
func _slice(src: Animation, t0: float, t1: float, ticks: int) -> Animation:
	var out := Animation.new()
	out.loop_mode = Animation.LOOP_NONE
	out.length = float(ticks) / TPS

	for i in src.get_track_count():
		var type := src.track_get_type(i)
		if type != Animation.TYPE_ROTATION_3D and type != Animation.TYPE_POSITION_3D:
			# SCALE tracks are dropped deliberately -- Blender bakes full TRS,
			# and PlayerRigScaler's SetBonePoseScale would be overwritten every
			# frame by a per-bone scale track (README trap 13).
			continue

		var path := src.track_get_path(i)
		if bone_of(path) == "":
			# The bare "Armature" object-node tracks -- Player.tscn's rig has
			# no such node, so these resolve against nothing.
			continue

		var t := out.add_track(type)
		out.track_set_path(t, _rebase_path(path))
		for k in ticks + 1:
			var u := float(k) / float(ticks)
			var st: float = lerpf(t0, t1, u)
			var dt := float(k) / TPS
			match type:
				Animation.TYPE_ROTATION_3D:
					out.rotation_track_insert_key(t, dt, src.rotation_track_interpolate(i, st))
				Animation.TYPE_POSITION_3D:
					out.position_track_insert_key(t, dt, src.position_track_interpolate(i, st))
	return out


# The a45bd1d guard: a slice that lost bone tracks would rest-pose the missing
# bones the moment its state was entered, and it would do so silently.
func _assert_complete(anim: Animation, name: StringName, expected_rot: int, expected_total: int, form_counts: Dictionary) -> bool:
	var rot := _rotation_track_count(anim)
	var unresolved := []
	var bad_shape := []
	for i in anim.get_track_count():
		var path := anim.track_get_path(i)
		var b := bone_of(path)
		if b == "":
			bad_shape.append(String(path))
			continue
		if String(path).begins_with(ARMATURE_PREFIX):
			bad_shape.append(String(path))
			continue
		var res := _resolve_bone(b)
		var form: String = res[1]
		form_counts[form] = form_counts[form] + 1
		if res[0] < 0:
			unresolved.append(b)
	print("[rebuild-spin]   '%s': len=%.4f tracks=%d rot=%d loop=%d unresolved=%s"
		% [name, anim.length, anim.get_track_count(), rot, anim.loop_mode, str(unresolved)])
	if rot != expected_rot or anim.get_track_count() != expected_total:
		push_error("[rebuild-spin] '%s' has %d/%d tracks, source has %d/%d -- a slice must inherit "
			% [name, rot, anim.get_track_count(), expected_rot, expected_total]
			+ "the source's FULL body coverage or the untracked bones rest-pose at full weight.")
		return false
	if unresolved.size() > 0:
		push_error("[rebuild-spin] '%s' has tracks that do not resolve on Y Bot in EITHER name form: %s"
			% [name, str(unresolved)])
		return false
	if bad_shape.size() > 0:
		push_error("[rebuild-spin] '%s' has %d track(s) whose NODE PATH cannot bind on "
			% [name, bad_shape.size()]
			+ "scenes/Player.tscn (skeleton at 'Skeleton3D', no 'Armature' wrapper): %s."
			% str(bad_shape))
		return false
	return true


func _rotation_track_count(anim: Animation) -> int:
	var n := 0
	for i in anim.get_track_count():
		if anim.track_get_type(i) == Animation.TYPE_ROTATION_3D:
			n += 1
	return n


# Largest per-bone angular difference between a SINGLE named instant in clip
# `a` (time `ta`) and a single named instant in clip `b` (time `tb`).
func _pose_delta_at(a: Animation, ta: float, b: Animation, tb: float) -> float:
	var worst := 0.0
	for i in a.get_track_count():
		if a.track_get_type(i) != Animation.TYPE_ROTATION_3D:
			continue
		var bone := bone_of(a.track_get_path(i))
		var j := -1
		for t in b.get_track_count():
			if b.track_get_type(t) == Animation.TYPE_ROTATION_3D and bone_of(b.track_get_path(t)) == bone:
				j = t
				break
		if j < 0:
			continue
		var qa: Quaternion = a.rotation_track_interpolate(i, ta)
		var qb: Quaternion = b.rotation_track_interpolate(j, tb)
		var d: float = clampf(absf(qa.normalized().dot(qb.normalized())), -1.0, 1.0)
		worst = maxf(worst, rad_to_deg(2.0 * acos(d)))
	return worst


# Global origin of `bone` with `anim` applied at time `t`, by manual forward
# kinematics (verbatim rebuild_hesitation_clips.gd approach). Returns
# Vector3.ZERO only if the bone genuinely fails to resolve on Y Bot, which G2
# above already refuses to let through silently.
func _pose_origin(anim: Animation, t: float, bone: String) -> Vector3:
	var res := _resolve_bone(bone)
	var idx: int = res[0]
	if idx < 0:
		return Vector3.ZERO

	var rot_track_of := {}
	var pos_track_of := {}
	for i in anim.get_track_count():
		var ty := anim.track_get_type(i)
		if ty != Animation.TYPE_ROTATION_3D and ty != Animation.TYPE_POSITION_3D:
			continue
		var b_res := _resolve_bone(bone_of(anim.track_get_path(i)))
		var b: int = b_res[0]
		if b < 0:
			continue
		if ty == Animation.TYPE_ROTATION_3D:
			rot_track_of[b] = i
		else:
			pos_track_of[b] = i

	var chain := []
	var walk := idx
	while walk >= 0:
		chain.push_front(walk)
		walk = _skel.get_bone_parent(walk)

	var acc := Transform3D.IDENTITY
	for b in chain:
		var rest: Transform3D = _skel.get_bone_rest(b)
		var local := rest
		if rot_track_of.has(b):
			var q: Quaternion = anim.rotation_track_interpolate(rot_track_of[b], t)
			local = Transform3D(Basis(q).scaled(rest.basis.get_scale()), rest.origin)
		if pos_track_of.has(b):
			local.origin = anim.position_track_interpolate(pos_track_of[b], t)
		acc = acc * local
	return acc.origin


func _find(n: Node, cls: String) -> Node:
	if n.get_class() == cls:
		return n
	for c in n.get_children():
		var r := _find(c, cls)
		if r != null:
			return r
	return null
