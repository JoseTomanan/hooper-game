extends SceneTree
# Asset build tool (#305) — drafts the retreat-dribble clip family into
# assets/locomotion.res by SLICING assets/retreatdribble_authored.fbx.
#
# Run:  godot --headless --path . -s tools/rebuild_retreatdribble_clips.gd
# Idempotent: re-running re-derives all three clips from the pristine FBX
# rather than stacking edits (the previous build is removed before the new
# one lands).
#
# Produces THREE LOOP_NONE one-shots — retreat dribble is UNHANDED (the ball
# never changes hands; RetreatDribble.cs's class doc: "the ball stays Dribbling
# throughout"), so — like the jab step (#304), layup (#313) and contest (#314)
# families and unlike the dribble-move family — this tool slices THREE clips,
# not six:
#   retreatdribblestartup    3 ticks / 0.05000 s
#   retreatdribbleactive     2 ticks / 0.03333 s
#   retreatdribblerecovery   4 ticks / 0.06667 s
#
# ── Jab step's twin, and the reason G4 below is inverted ─────────────────────
# Retreat dribble is 3/2/4 ticks off assets/Dribble.fbx — IDENTICAL to jab step
# (#304) in tick shape, source and three-held-poses structure. At 0.150 s total
# the ONLY read that survives is the torso lean SIGN:
#
#   Jab step's torso pitches FORWARD over an extended front foot.
#   Retreat dribble's stays upright-to-BACK over a base moving away.
#
# So rebuild_jabstep_clips.gd's G4 (`growth >= +floor`) becomes this file's G4
# (`growth <= -floor`) plus a new G5 that checks the ABSOLUTE side of vertical.
# The two are different failures and both matter — see G5's own comment.
#
# ── Why this is a SLICE, not a compose ───────────────────────────────────────
# tools/author_retreatdribble.py (headless Blender, #315's blender_anim_lib
# machinery) already authored the full Startup/Active/Recovery arc as hand-keyed
# IK poses, baked at 60 Hz, on ONE timeline. This tool's job is therefore only
# to resample ("slice") the three named windows out of that timeline —
# rebuild_jumpshot_clips.gd's `_slice()` primitive, inherited via
# rebuild_jabstep_clips.gd — and then PROVE geometrically that what got sliced
# is what the issue asked for.
#
# The proofs are RE-RUN here rather than inherited from the Blender side on
# purpose: the FBX round-trip, the importer's fps/trimming/immutable-track
# settings, and `_slice`'s resampling are exactly the machinery that has
# silently corrupted clips in this repo before (#281, #295, #297).
#
# ── The Mixamo bone-name-prefix trap (read before touching bone_of/_resolve) ─
# In Blender the bones are named `mixamorig:Hips` (colon) — see
# blender_anim_lib.py's HIPS/SPINE constants. Godot 4.6+'s `ufbx` importer
# imports Mixamo-prefixed bones as `mixamorig_Hips` (UNDERSCORE) instead.
# `_resolve_bone()` tries BOTH forms and reports which form actually matched,
# so a silent zero-match can never hide behind a green run.
#
# ── The a45bd1d full-body-coverage trap ──────────────────────────────────────
# A single-clip AnimationTree state plays at FULL WEIGHT, and Godot's
# AnimationMixer writes every bone the active clip does NOT track to the
# skeleton's rest transform. author_retreatdribble.py's own
# `verify_all_bones_keyed(expected_count=52)` gate already proves the SOURCE
# carries full-body coverage; `_assert_complete()` below re-proves that every
# SLICE inherits that coverage verbatim rather than trusting the source's own
# proof to survive slicing.
#
# ── The `Armature/` prefix trap (README trap 13, #281) ───────────────────────
# Blender's FBX export wraps the skeleton in an Armature object, so a track
# imported from retreatdribble_authored.fbx reads
# "Armature/Skeleton3D:mixamorig_Hips" — one level deeper than
# scenes/Player.tscn's rig, whose skeleton sits directly at "Skeleton3D". An
# unresolvable track binds to nothing: the clip plays as a SILENT no-op —
# the state machine still enters the right state, the duration still checks
# out, and the mesh never moves. `_rebase_path()` strips the prefix on every
# track, and `_assert_complete()` rejects (not skips) any surviving
# `Armature/`-prefixed path or any path with no bone subname.
#
# ── Where the three windows come from ────────────────────────────────────────
# Hardcoded, not derived: author_retreatdribble.py's frame layout is
# DETERMINISTIC BY CONSTRUCTION — it keys its timeline at exact times computed
# from RetreatDribble's own frame data (3/2/4 ticks @ 60 Hz) and the import sets
# `trimming=false`, so those source times land exactly where the docstring says.
# This tool ASSERTS the guarantee (the source clip's total length) so a
# silently-retrimmed or wrong-fps import fails loudly instead of slicing garbage.
#
#   source seconds        segment      ticks
#   0.00000 -> 0.05000    Startup       3
#   0.05000 -> 0.08333    Active        2
#   0.08333 -> 0.15000    Recovery      4
#
# ── Cosmetic-only (issue #305's standing constraint) ─────────────────────────
# This tool writes ONE file: assets/locomotion.res. It reads no gameplay
# constant and changes no gameplay behaviour. In particular it never touches
# RetreatDribbleBurstSpeed, BallState or HasDribbled, so StepBackTest's
# `retreat-dribble-no-gather` and `retreat-dribble-dead-dribble-gate` scenarios
# are out of this file's reach by construction. The tick counts below are
# DUPLICATED from RetreatDribble's frame data for slicing, never read back
# into it.

const LIB_PATH := "res://assets/locomotion.res"
const SRC_FBX := "res://assets/retreatdribble_authored.fbx"
# Matches author_retreatdribble.py's ACTION_NAME -- export_fbx() renames both
# the Blender action AND the scene to this so Godot's importer names the
# resulting AnimationPlayer take after it.
const SRC_CLIP := "retreatdribble"

# Physics ticks per second (project.godot physics/common/physics_ticks_per_second).
const TPS := 60.0

# The node-path prefix Blender's Armature object wrapper adds; see _rebase_path.
const ARMATURE_PREFIX := "Armature/"

# RetreatDribble's frame data (scripts/Input/RetreatDribble.cs
# DefaultFrameData). Duplicated here because GDScript cannot read the C#
# constant -- so the duplication is made SAFE rather than avoided:
# RetreatDribbleAnimTest's `retreatdribble-segment-lengths` scenario asserts
# each clip's length equals RetreatDribble.DefaultFrameData's own tick count /
# 60, reading the C# side directly. Retune the move without re-running this tool
# and that harness scenario goes red and names this file.
const STARTUP_TICKS := 3
const ACTIVE_TICKS := 2
const RECOVERY_TICKS := 4

# Source-time windows, matching author_retreatdribble.py's frame table exactly
# (frame numbers there ARE physics ticks at 60 Hz: 0/3/5/9).
const STARTUP := [0.0 / 60.0, 3.0 / 60.0]
const ACTIVE := [3.0 / 60.0, 5.0 / 60.0]
const RECOVERY := [5.0 / 60.0, 9.0 / 60.0]

# The producer exports frame_start=0, frame_end=9 (TOTAL_TICKS in
# author_retreatdribble.py), so the imported clip's length must be ~9/60 s. A
# silently-retrimmed or wrong-fps import would shift every window above out
# from under the actual keyed poses.
const EXPECTED_SRC_LENGTH_S := 9.0 / 60.0
const SRC_LENGTH_TOL_S := 0.02

const NAMES := ["retreatdribblestartup", "retreatdribbleactive", "retreatdribblerecovery"]

# G3 legibility floor (#296's actual complaint -- Startup and Recovery must
# read as visibly different poses). Matches author_retreatdribble.py's own
# POSE_DISTINCT_MIN_DEG / STARTUP_END_VS_RECOVERY_END_MIN_DEG = 15.0 gates --
# this tool re-proves it on the SLICED clips rather than trusting the source's
# Blender-side proof to survive the slice untouched. Measured for this exact
# comparison: 30.118 deg Blender-side (all 65 bones' armature-space poses),
# 35.1 deg here on the sliced resource.
const STARTUP_VS_RECOVERY_MIN_DEG := 15.0

# G4/G5: the torso goes upright-to-BACK by Active -- the load-bearing contrast
# with jab step (#304) per the issue's motion spec. Measured against the
# spine->head vector's projection along `forward`, the same axis convention
# author_retreatdribble.py's own oracles use, re-proven here on the SLICED Godot
# resource by manual FK.
const SPINE_BONE := "mixamorig_Spine"
const HEAD_BONE := "mixamorig_Head"

# G4 -- minimum BACKWARD travel (Active-end vs Startup-end) of the spine->head
# vector's projection along `forward`, in metres. Sign-inverted from
# rebuild_jabstep_clips.gd's identically-shaped gate, which is the whole point.
# Small on purpose -- it exists to catch a SIGN error or a dead clip, not to
# demand a specific pitch magnitude.
const TORSO_BACKWARD_GROWTH_MIN_M := 0.01

# G5 -- the ABSOLUTE claim, and a genuinely different failure from G4.
# assets/Dribble.fbx is a crouching dribble whose torso already sits ~29.8 deg
# / +0.2447 m FORWARD of vertical (measured Blender-side). So a correctly-SIGNED
# but undersized counter-rotation still leaves the chest leaning forward: G4
# passes (it did move backward) while the clip reads as driving INTO the
# defender. G5 asserts which side of vertical the chest actually ends up on,
# which is the thing handoff 05's "torso vertical to 5 deg back" claims.
#
# The reference differs from the Blender side's: author_retreatdribble.py
# measures against the rig's own rest-derived `up`, while this rebuilds the pose
# by manual FK from Y Bot's rest chain. MEASURED on the shipped clip, the two
# agree closely on the quantity that matters --
#
#   Blender-side  torso_forward_at_active_end_m = -0.0402
#   here (G5)     torso-forward at Active-end   = -0.0460
#   here (G4)     Startup-end for contrast      = +0.0376
#
# -- so resource-side zero IS a meaningful "vertical", and the threshold is set
# at exactly the spec's own claim rather than at a slack band around the
# measurement. 4.6 cm of headroom. If a retune lands this positive, raise
# `torso_back_deg` in author_retreatdribble.py's keypose table; do NOT raise
# this number, because which side of vertical the chest sits on IS the move.
const TORSO_FORWARD_MAX_AT_ACTIVE_M := 0.0

var _skel: Skeleton3D = null
var _right := Vector3.ZERO
var _forward := Vector3.ZERO
var _up := Vector3.UP


func bone_of(np: NodePath) -> String:
	return "" if np.get_subname_count() == 0 else String(np.get_subname(0))


# The node path an AnimationPlayer under scenes/Player.tscn can actually
# resolve. See header — Blender's FBX export wraps the skeleton in an Armature
# object, so a track imported from retreatdribble_authored.fbx reads
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
		push_error("[rebuild-retreatdribble] failed to load AnimationLibrary at %s" % LIB_PATH)
		quit(1)
		return

	_skel = _find(load("res://assets/Y Bot.fbx").instantiate(), "Skeleton3D")
	if _skel == null:
		push_error("[rebuild-retreatdribble] could not find a Skeleton3D in Y Bot.fbx.")
		quit(1)
		return

	if not _derive_body_axes():
		quit(1)
		return

	var packed = load(SRC_FBX)
	if packed == null:
		push_error("[rebuild-retreatdribble] failed to load %s" % SRC_FBX)
		quit(1)
		return
	var ap: AnimationPlayer = _find(packed.instantiate(), "AnimationPlayer")
	if ap == null:
		push_error("[rebuild-retreatdribble] %s has no AnimationPlayer." % SRC_FBX)
		quit(1)
		return
	if not ap.has_animation(SRC_CLIP):
		push_error("[rebuild-retreatdribble] %s has no AnimationPlayer clip '%s' (has: %s)"
			% [SRC_FBX, SRC_CLIP, str(ap.get_animation_list())])
		quit(1)
		return
	var src: Animation = ap.get_animation(SRC_CLIP)
	print("[rebuild-retreatdribble] source '%s': len=%.5f tracks=%d"
		% [SRC_CLIP, src.length, src.get_track_count()])

	# ── Sanity: the source must be the full 0..9-frame export, not a
	# silently-retrimmed or wrong-fps import.
	if absf(src.length - EXPECTED_SRC_LENGTH_S) > SRC_LENGTH_TOL_S:
		push_error("[rebuild-retreatdribble] source length %.5f s is not within %.3f s of the expected "
			% [src.length, SRC_LENGTH_TOL_S] + "%.5f s (9/60 s @ 60 fps) -- the import may have been "
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
	print("[rebuild-retreatdribble] source has %d tracks; %d expected per slice after dropping "
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
		print("[rebuild-retreatdribble] G1 '%s': length=%.5f (expect %.5f) loop_mode=%d %s"
			% [name, anim.length, expected_len, anim.loop_mode, "OK" if (len_ok and loop_ok) else "FAIL"])
		if not (len_ok and loop_ok):
			g1_ok = false
	if not g1_ok:
		push_error("[rebuild-retreatdribble] G1 FAILED -- one or more clips has the wrong length or loop_mode.")
		quit(1)
		return

	# ── G2: track-count parity + full bone resolution (both name forms) ─────
	var g2_ok := true
	var form_counts := {"as-given": 0, "alt": 0, "unresolved": 0}
	for name in built:
		if not _assert_complete(built[name], name, src_rot, src_total, form_counts):
			g2_ok = false
	print("[rebuild-retreatdribble] G2 bone-name resolution across all three clips: as-given=%d alt=%d unresolved=%d"
		% [form_counts["as-given"], form_counts["alt"], form_counts["unresolved"]])
	if not g2_ok:
		quit(1)
		return
	if form_counts["unresolved"] > 0:
		push_error("[rebuild-retreatdribble] G2 FAILED -- %d bone tracks did not resolve on Y Bot in EITHER "
			% form_counts["unresolved"] + "name form.")
		quit(1)
		return
	var resolved_total: int = form_counts["as-given"] + form_counts["alt"]
	if resolved_total <= 0:
		push_error("[rebuild-retreatdribble] G2 FAILED -- resolved count is %d; a name-matching gate that "
			% resolved_total + "matches nothing passes vacuously.")
		quit(1)
		return

	var startup: Animation = built[NAMES[0]]
	var active: Animation = built[NAMES[1]]
	var recovery: Animation = built[NAMES[2]]

	# ── G3: Startup's END pose vs Recovery's END pose ────────────────────────
	# NOT a whole-clip-start-vs-whole-clip-end comparison -- Startup's own LAST
	# frame (the full tell) vs Recovery's own LAST frame (the settled stance)
	# is the comparison that actually tests #296. It is also the HARDER of the
	# two comparisons for this move specifically: the retreat dribble's Recovery
	# is a deliberate RESET ("balanced, not punished" -- handoff 05), so an
	# under-authored Recovery drifts back toward the Startup stance rather than
	# away from it. author_retreatdribble.py re-proves this exact pair
	# Blender-side for the same reason.
	var g3_delta := _pose_delta_at(startup, startup.length, recovery, recovery.length)
	print("[rebuild-retreatdribble] G3 startup-end vs recovery-end max bone delta = %.1f deg (want >= %.1f)"
		% [g3_delta, STARTUP_VS_RECOVERY_MIN_DEG])
	if g3_delta < STARTUP_VS_RECOVERY_MIN_DEG:
		push_error("[rebuild-retreatdribble] G3 FAILED: only %.1f deg (< %.1f) -- Startup's end pose and "
			% [g3_delta, STARTUP_VS_RECOVERY_MIN_DEG] + "Recovery's end pose do not read as distinct (#296).")
		quit(1)
		return

	# ── G4: torso travels BACKWARD by Active (the jab-step contrast) ────────
	# Startup's OWN end pose vs Active's END pose (the push-off instant), not vs
	# a rest reference -- consistent with G3's "compare named phase instants,
	# not whole-clip endpoints" discipline. Sign-inverted from
	# rebuild_jabstep_clips.gd's otherwise-identical G4: a POSITIVE growth here
	# would mean this clip leans into the defender exactly like the jab does,
	# which is the two-clips-converge failure #305 exists to prevent.
	var startup_lean := _spine_head_forward(startup, startup.length)
	var active_lean := _spine_head_forward(active, active.length)
	var growth := active_lean - startup_lean
	print("[rebuild-retreatdribble] G4 torso-forward growth Startup->Active = %.4f m (want <= %.4f); startup=%.4f active=%.4f"
		% [growth, -TORSO_BACKWARD_GROWTH_MIN_M, startup_lean, active_lean])
	if growth > -TORSO_BACKWARD_GROWTH_MIN_M:
		push_error("[rebuild-retreatdribble] G4 FAILED: torso-forward growth is %.4f m (want <= %.4f) -- the retreat "
			% [growth, -TORSO_BACKWARD_GROWTH_MIN_M] + "dribble is supposed to go upright-to-BACK over a base moving "
			+ "away (#305's defining contrast with jab step, #304). Check TORSO_PITCH_SIGN in "
			+ "author_retreatdribble.py.")
		quit(1)
		return

	# ── G5: and it ends up on the BACK side of vertical, not merely less
	# forward than it was ───────────────────────────────────────────────────
	# See TORSO_FORWARD_MAX_AT_ACTIVE_M's comment: the source crouch is ~30 deg
	# forward, so G4 alone is satisfied by a clip that merely straightens up a
	# little and still leans in. This is the absolute claim.
	print("[rebuild-retreatdribble] G5 torso-forward at Active-end = %.4f m (want <= %.4f)"
		% [active_lean, TORSO_FORWARD_MAX_AT_ACTIVE_M])
	if active_lean > TORSO_FORWARD_MAX_AT_ACTIVE_M:
		push_error("[rebuild-retreatdribble] G5 FAILED: at Active's end the torso still projects %.4f m FORWARD "
			% active_lean + "(max %.4f) -- it moved backward (G4 passed) but not far enough to clear the source's "
			% TORSO_FORWARD_MAX_AT_ACTIVE_M + "own ~30 deg dribble crouch, so the clip still reads as leaning INTO "
			+ "the defender. Raise torso_back_deg in author_retreatdribble.py's keypose table; do NOT raise this "
			+ "threshold.")
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
		push_error("[rebuild-retreatdribble] ResourceSaver.save failed with error %d" % err)
		quit(1)
		return

	print("[rebuild-retreatdribble] saved %s; clips now: %s" % [LIB_PATH, str(lib.get_animation_list())])
	quit(0)


func _name_tick_index(name: StringName) -> int:
	var s := String(name)
	if s.contains("startup"):
		return 0
	if s.contains("active"):
		return 1
	return 2


# ── Body axes (verbatim rebuild_jabstep_clips.gd approach) ───────────────────
func _derive_body_axes() -> bool:
	var foot := _skel.find_bone("mixamorig_LeftFoot")
	var toe := _skel.find_bone("mixamorig_LeftToeBase")
	if foot < 0 or toe < 0:
		push_error("[rebuild-retreatdribble] Y Bot has no LeftFoot/LeftToeBase to derive facing from.")
		return false
	var forward: Vector3 = _skel.get_bone_global_rest(toe).origin - _skel.get_bone_global_rest(foot).origin
	forward.y = 0.0
	if forward.length() < 0.001:
		push_error("[rebuild-retreatdribble] Y Bot's foot->toe vector is vertical; cannot derive facing.")
		return false
	_forward = forward.normalized()
	_right = _forward.cross(_up).normalized()

	var lh := _skel.find_bone("mixamorig_LeftHand")
	var rh := _skel.find_bone("mixamorig_RightHand")
	if lh < 0 or rh < 0:
		push_error("[rebuild-retreatdribble] Y Bot has no LeftHand/RightHand to verify the lateral axis.")
		return false
	var lh_lat: float = _skel.get_bone_global_rest(lh).origin.dot(_right)
	var rh_lat: float = _skel.get_bone_global_rest(rh).origin.dot(_right)
	print("[rebuild-retreatdribble] axes: forward=%s right=%s | rest hand lateral L=%+.4f R=%+.4f"
		% [_forward, _right, lh_lat, rh_lat])
	if not (rh_lat > 0.0 and lh_lat < 0.0):
		push_error("[rebuild-retreatdribble] the derived right axis puts the RIGHT hand at %+.4f and the LEFT "
			% rh_lat + "at %+.4f -- it is inverted (or the rig's bone names lie)." % lh_lat)
		return false
	return true


# Spine->Head vector's projection along `_forward`, at time `t` in `anim`, in
# metres. Used by G4/G5 -- the same quantity author_retreatdribble.py's own
# `_spine_head_forward_m` measures Blender-side, re-measured here on the SLICED
# Godot resource by manual FK.
func _spine_head_forward(anim: Animation, t: float) -> float:
	return (_pose_origin(anim, t, HEAD_BONE) - _pose_origin(anim, t, SPINE_BONE)).dot(_forward)


# ── Slicing (verbatim rebuild_jumpshot_clips.gd / rebuild_jabstep_clips.gd
# primitive) ──────────────────────────────────────────────────────────────────
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
	print("[rebuild-retreatdribble]   '%s': len=%.4f tracks=%d rot=%d loop=%d unresolved=%s"
		% [name, anim.length, anim.get_track_count(), rot, anim.loop_mode, str(unresolved)])
	if rot != expected_rot or anim.get_track_count() != expected_total:
		push_error("[rebuild-retreatdribble] '%s' has %d/%d tracks, source has %d/%d -- a slice must inherit "
			% [name, rot, anim.get_track_count(), expected_rot, expected_total]
			+ "the source's FULL body coverage or the untracked bones rest-pose at full weight.")
		return false
	if unresolved.size() > 0:
		push_error("[rebuild-retreatdribble] '%s' has tracks that do not resolve on Y Bot in EITHER name form: %s"
			% [name, str(unresolved)])
		return false
	if bad_shape.size() > 0:
		push_error("[rebuild-retreatdribble] '%s' has %d track(s) whose NODE PATH cannot bind on "
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
# kinematics (verbatim rebuild_jabstep_clips.gd / rebuild_contest_clips.gd
# approach — see their headers for why not get_bone_global_pose()).
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
