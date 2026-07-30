extends SceneTree
# Asset build tool (#282) — drafts the steal clip family into
# assets/locomotion.res by SLICING assets/steal_authored.fbx.
#
# Run:  godot --headless --path . -s tools/rebuild_steal_clips.gd
# Idempotent: re-running re-derives all six clips from the pristine FBX rather
# than stacking edits (the previous build is removed before the new one lands).
#
# Produces six LOOP_NONE one-shots — three phases x two TARGET-hand polarities:
#   stealstartupleft   /  ...right    8 ticks / 0.1333 s
#   stealactiveleft     /  ...right    8 ticks / 0.1333 s
#   stealrecoveryleft   /  ...right   20 ticks / 0.3333 s
#
# ── The suffix names the TARGET hand, NOT an origin (steal's own rule) ───────
# Unlike crossover/behind-the-back/between-the-legs, steal has no ball to swap
# sides on. Per the #282 handoff and tools/author_steal.py's own module
# docstring: the suffix names where the SWIPING HAND ENDS UP, in the
# defender's own body space. The arm that actually does the work is the
# OPPOSITE shoulder (a "left"-target steal swipes with the RIGHT arm crossing
# over). This tool does not need to re-derive or re-decide that -- it inherits
# whichever polarity author_steal.py baked into which half of the timeline
# (LEFT-target at frames 0/8/16/36, RIGHT-target at 60/68/76/96) and only
# proves, geometrically, that what it sliced matches the contract.
#
# `MoveAnimResolver`'s own consumption of these six clips (the steal-specific
# "target hand, not OriginHand" resolver rule the #282 handoff calls out) is
# OUT OF SCOPE for this tool -- a separate lane owns scripts/**. This tool's
# job ends at producing and proving six correctly-named clips in
# locomotion.res.
#
# ── Why this is a SLICE, not a compose (verbatim rebuild_behindtheback_clips.gd
# precedent) ──────────────────────────────────────────────────────────────────
# tools/author_steal.py (headless Blender, #315's blender_anim_lib machinery)
# already authored the FULL two-polarity Startup/Active/Recovery arc as
# hand-keyed IK poses, baked at 60 Hz, on ONE timeline holding both polarities
# back to back. This tool's job is therefore only to resample ("slice") the
# six named windows out of that timeline — rebuild_jumpshot_clips.gd's
# `_slice()` primitive, copied verbatim — and then PROVE geometrically that
# what got sliced is what the issue asked for.
#
# ── The Mixamo bone-name-prefix trap (read before touching bone_of/_resolve) ─
# In Blender the bones are named `mixamorig:Hips` (colon) — see
# blender_anim_lib.py's HIPS/SPINE constants. Godot 4.6+'s `ufbx` importer
# imports Mixamo-prefixed bones as `mixamorig_Hips` (UNDERSCORE) instead. Both
# the source clip (imported from steal_authored.fbx) and the target skeleton
# (Y Bot.fbx) go through that same importer, so in practice both sides should
# already agree on the underscore form — but "should" is exactly the kind of
# claim this repo's convention says to prove, not assume. `_resolve_bone()`
# below therefore tries BOTH forms and reports which form actually matched and
# how many tracks needed it, so a silent zero-match can never hide behind a
# green run.
#
# ── The a45bd1d full-body-coverage trap ──────────────────────────────────────
# A single-clip AnimationTree state plays at FULL WEIGHT, and Godot's
# AnimationMixer writes every bone the active clip does NOT track to the
# skeleton's rest transform. author_steal.py's own
# `verify_all_bones_keyed(expected_count=52)` gate already proves the SOURCE
# carries full-body coverage (52 rotation tracks + 1 Hips position track, 53
# total). `_assert_complete()` below re-proves that every SLICE inherits that
# coverage verbatim rather than trusting the source's own proof to survive
# slicing.
#
# ── Where the six windows come from ──────────────────────────────────────────
# Hardcoded, not derived: author_steal.py's frame layout is DETERMINISTIC BY
# CONSTRUCTION — it keys its six keyposes at exact times computed from
# StealMove's own DefaultFrameData (8/8/20 ticks @ 60 Hz) and the import sets
# `trimming=false`, so those source times land exactly where the docstring
# says. This tool ASSERTS the guarantee (the source clip's total length) so a
# silently-retrimmed or wrong-fps import fails loudly instead of slicing
# garbage silently.
#
#   source seconds        segment                    ticks
#   0.00000 -> 0.13333     LEFT-target  Startup       8
#   0.13333 -> 0.26667     LEFT-target  Active        8
#   0.26667 -> 0.60000     LEFT-target  Recovery      20
#   (0.60000 -> 1.00000 gap -- never sliced)
#   1.00000 -> 1.13333     RIGHT-target Startup       8
#   1.13333 -> 1.26667     RIGHT-target Active        8
#   1.26667 -> 1.60000     RIGHT-target Recovery      20

const LIB_PATH := "res://assets/locomotion.res"
const SRC_FBX := "res://assets/steal_authored.fbx"
# Matches author_steal.py's ACTION_NAME -- export_fbx() renames both the
# Blender action AND the scene to this so Godot's importer names the
# resulting AnimationPlayer take after it.
const SRC_CLIP := "steal"

# Physics ticks per second (project.godot physics/common/physics_ticks_per_second).
const TPS := 60.0

# The node-path prefix Blender's Armature object wrapper adds; see _rebase_path.
const ARMATURE_PREFIX := "Armature/"

# StealMove's frame data (scripts/Input/StealMove.cs DefaultFrameData).
# Duplicated here because GDScript cannot read the C# constant -- so the
# duplication is made SAFE rather than avoided: a StealAnimTest-style harness
# scenario should assert each clip's length equals StealMove.DefaultFrameData's
# own tick count / 60, reading the C# side directly. Retune the move without
# re-running this tool and that harness should go red and name this file.
const STARTUP_TICKS := 8
const ACTIVE_TICKS := 8
const RECOVERY_TICKS := 20

# Source-time windows, matching author_steal.py's frame table exactly (frame
# numbers there ARE physics ticks at 60 Hz: 0/8/16/36 and 60/68/76/96).
const LEFT_STARTUP := [0.0 / 60.0, 8.0 / 60.0]
const LEFT_ACTIVE := [8.0 / 60.0, 16.0 / 60.0]
const LEFT_RECOVERY := [16.0 / 60.0, 36.0 / 60.0]
const RIGHT_STARTUP := [60.0 / 60.0, 68.0 / 60.0]
const RIGHT_ACTIVE := [68.0 / 60.0, 76.0 / 60.0]
const RIGHT_RECOVERY := [76.0 / 60.0, 96.0 / 60.0]

# The producer exports frame_start=0, frame_end=96 (EXPORT_FRAME_END in
# author_steal.py), so the imported clip's length must be ~96/60 s. A
# silently-retrimmed or wrong-fps import would shift every window above out
# from under the actual keyed poses -- this is what makes that failure loud
# instead of quietly slicing garbage.
const EXPECTED_SRC_LENGTH_S := 96.0 / 60.0
const SRC_LENGTH_TOL_S := 0.02

const NAMES := {
	"left": ["stealstartupleft", "stealactiveleft", "stealrecoveryleft"],
	"right": ["stealstartupright", "stealactiveright", "stealrecoveryright"],
}

# G3/G4 legibility floors (#296's actual complaint -- Startup and Recovery must
# read as visibly different poses, and the two Active polarities must be a
# distinct silhouette from one another). Matches author_steal.py's own
# POSE_DISTINCT_MIN_DEG=15.0 / LEFT_VS_RIGHT_ACTIVE_MIN_DEG=20.0 gates -- this
# tool re-proves them on the SLICED clips rather than trusting the source's own
# Blender-side proof to survive the slice untouched.
const STARTUP_VS_RECOVERY_MIN_DEG := 15.0
const LEFT_VS_RIGHT_ACTIVE_MIN_DEG := 20.0

# G5 ball-height band (the motion spec's 0.65-0.80 m above the floor). The
# floor is approximated as NEUTRAL_HIP_TO_ANKLE_M below the Hips, matching
# author_steal.py's own floor identity exactly (see that script's module
# docstring) so this gate reads the same geometry the authoring script itself
# used to place the target, not a re-derived approximation.
const NEUTRAL_HIP_TO_ANKLE_M := 0.62
const BALL_HEIGHT_MIN_M := 0.60
const BALL_HEIGHT_MAX_M := 0.85

var _skel: Skeleton3D = null
var _right := Vector3.ZERO
var _forward := Vector3.ZERO
var _up := Vector3.UP


func bone_of(np: NodePath) -> String:
	return "" if np.get_subname_count() == 0 else String(np.get_subname(0))


# The node path an AnimationPlayer under scenes/Player.tscn can actually
# resolve. Blender's FBX export wraps the skeleton in an Armature object, so a
# track imported from steal_authored.fbx reads
# "Armature/Skeleton3D:mixamorig_Hips" -- one level deeper than the rig, whose
# skeleton is at "Skeleton3D". Every stock-Mixamo clip already in
# locomotion.res uses the short form, so this rebases onto that shape rather
# than inventing a third convention.
#
# This is NOT cosmetic. An unresolvable track binds to nothing, so the clip
# plays as a no-op: the state machine still enters the right state, the clip
# still reports the right duration, and the mesh never moves.
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
		push_error("[rebuild-steal] failed to load AnimationLibrary at %s" % LIB_PATH)
		quit(1)
		return

	_skel = _find(load("res://assets/Y Bot.fbx").instantiate(), "Skeleton3D")
	if _skel == null:
		push_error("[rebuild-steal] could not find a Skeleton3D in Y Bot.fbx.")
		quit(1)
		return

	if not _derive_body_axes():
		quit(1)
		return

	var packed = load(SRC_FBX)
	if packed == null:
		push_error("[rebuild-steal] failed to load %s" % SRC_FBX)
		quit(1)
		return
	var ap: AnimationPlayer = _find(packed.instantiate(), "AnimationPlayer")
	if ap == null:
		push_error("[rebuild-steal] %s has no AnimationPlayer." % SRC_FBX)
		quit(1)
		return
	if not ap.has_animation(SRC_CLIP):
		push_error("[rebuild-steal] %s has no AnimationPlayer clip '%s' (has: %s)"
			% [SRC_FBX, SRC_CLIP, str(ap.get_animation_list())])
		quit(1)
		return
	var src: Animation = ap.get_animation(SRC_CLIP)
	print("[rebuild-steal] source '%s': len=%.5f tracks=%d"
		% [SRC_CLIP, src.length, src.get_track_count()])

	# ── Sanity: the source must be the full 0..96-frame export, not a
	# silently-retrimmed or wrong-fps import.
	if absf(src.length - EXPECTED_SRC_LENGTH_S) > SRC_LENGTH_TOL_S:
		push_error("[rebuild-steal] source length %.5f s is not within %.3f s of the expected "
			% [src.length, SRC_LENGTH_TOL_S] + "%.5f s (96/60 s @ 60 fps) -- the import may have been "
			% EXPECTED_SRC_LENGTH_S + "retrimmed or baked at the wrong fps, which would silently shift "
			+ "every hardcoded slice window in this file off the authored poses.")
		quit(1)
		return

	# What a SLICE is expected to carry, derived from the source by applying the
	# same two filters _slice() applies — never the source's raw counts. The
	# source holds full TRS for every bone plus the Armature object node
	# (65*3 + 3 = 198); a slice keeps rotation+position for bone tracks only
	# (65*2 = 130).
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
	print("[rebuild-steal] source has %d tracks; %d expected per slice after dropping "
		% [src.get_track_count(), src_total]
		+ "SCALE (fights PlayerRigScaler) and the Armature object node (unbindable on Player.tscn).")

	# ── Slice the six windows (verbatim rebuild_jumpshot_clips.gd primitive;
	# no swing composition -- the motion is already fully authored) ─────────
	var windows := {
		"left": [LEFT_STARTUP, LEFT_ACTIVE, LEFT_RECOVERY],
		"right": [RIGHT_STARTUP, RIGHT_ACTIVE, RIGHT_RECOVERY],
	}
	var ticks := [STARTUP_TICKS, ACTIVE_TICKS, RECOVERY_TICKS]

	var built := {}
	for polarity in ["left", "right"]:
		var names: Array = NAMES[polarity]
		var wins: Array = windows[polarity]
		for i in 3:
			var w: Array = wins[i]
			built[names[i]] = _slice(src, w[0], w[1], ticks[i])

	# ── G1: existence, loop mode, exact length ───────────────────────────────
	var g1_ok := true
	for name in built:
		var anim: Animation = built[name]
		var idx := _name_tick_index(name)
		var expected_len := float(ticks[idx]) / TPS
		var len_ok := absf(anim.length - expected_len) <= 1e-4
		var loop_ok := anim.loop_mode == Animation.LOOP_NONE
		print("[rebuild-steal] G1 '%s': length=%.5f (expect %.5f) loop_mode=%d %s"
			% [name, anim.length, expected_len, anim.loop_mode, "OK" if (len_ok and loop_ok) else "FAIL"])
		if not (len_ok and loop_ok):
			g1_ok = false
	if not g1_ok:
		push_error("[rebuild-steal] G1 FAILED -- one or more clips has the wrong length or loop_mode.")
		quit(1)
		return

	# ── G2: track-count parity + full bone resolution (both name forms) ─────
	var g2_ok := true
	var form_counts := {"as-given": 0, "alt": 0, "unresolved": 0}
	for name in built:
		if not _assert_complete(built[name], name, src_rot, src_total, form_counts):
			g2_ok = false
	print("[rebuild-steal] G2 bone-name resolution across all six clips: as-given=%d alt=%d unresolved=%d"
		% [form_counts["as-given"], form_counts["alt"], form_counts["unresolved"]])
	if not g2_ok:
		quit(1)
		return
	if form_counts["unresolved"] > 0:
		push_error("[rebuild-steal] G2 FAILED -- %d bone tracks did not resolve on Y Bot in EITHER "
			% form_counts["unresolved"] + "name form.")
		quit(1)
		return
	var resolved_total: int = form_counts["as-given"] + form_counts["alt"]
	if resolved_total <= 0:
		push_error("[rebuild-steal] G2 FAILED -- resolved count is %d; a name-matching gate that "
			% resolved_total + "matches nothing passes vacuously.")
		quit(1)
		return

	# ── G3: per polarity, Startup's END pose vs Recovery's END pose ─────────
	# NOT a whole-clip-start-vs-whole-clip-end comparison: per the brief, frame
	# 0 and frame 36 (and 60/96) are BOTH the same neutral defensive stance by
	# design, so that comparison would be vacuous here. Startup's own LAST
	# frame (the tell) vs Recovery's own LAST frame (the return to neutral) is
	# the comparison that actually tests #296.
	var g3_ok := true
	for polarity in ["left", "right"]:
		var names: Array = NAMES[polarity]
		var startup: Animation = built[names[0]]
		var recovery: Animation = built[names[2]]
		var delta := _pose_delta_at(startup, startup.length, recovery, recovery.length)
		print("[rebuild-steal] G3 %s-target startup-end vs recovery-end max bone delta = %.1f deg (want >= %.1f)"
			% [polarity, delta, STARTUP_VS_RECOVERY_MIN_DEG])
		if delta < STARTUP_VS_RECOVERY_MIN_DEG:
			push_error("[rebuild-steal] G3 FAILED for %s-target: only %.1f deg (< %.1f) -- Startup's end "
				% [polarity, delta, STARTUP_VS_RECOVERY_MIN_DEG] + "pose and Recovery's end pose do not read as "
				+ "distinct (#296).")
			g3_ok = false
	if not g3_ok:
		quit(1)
		return

	# ── G4: activeleft vs activeright ────────────────────────────────────────
	var active_left: Animation = built[NAMES["left"][1]]
	var active_right: Animation = built[NAMES["right"][1]]
	var g4_delta := _max_pose_delta(active_left, active_right)
	print("[rebuild-steal] G4 activeleft-vs-activeright max bone delta = %.1f deg (want >= %.1f)"
		% [g4_delta, LEFT_VS_RIGHT_ACTIVE_MIN_DEG])
	if g4_delta < LEFT_VS_RIGHT_ACTIVE_MIN_DEG:
		push_error("[rebuild-steal] G4 FAILED: only %.1f deg (< %.1f) -- the two Active polarities do "
			% [g4_delta, LEFT_VS_RIGHT_ACTIVE_MIN_DEG] + "not read as distinct silhouettes.")
		quit(1)
		return

	# ── G5: the load-bearing silhouette gate -- ball height + crossed midline,
	# ONE arm, at Active's final frame (the full-extension pose) ────────────
	# Contrast per the brief: steal reaches ACROSS the body at ball height with
	# ONE arm; contest raises BOTH arms vertically with feet planted. This gate
	# only has to prove STEAL's own half of that contrast (contest's own clip
	# does not exist yet -- see the G7-equivalent note below).
	var g5_ok := true
	for pair in [["left", "mixamorig_RightHand"], ["right", "mixamorig_LeftHand"]]:
		# Swipe arm is the OPPOSITE chain from the polarity name (module
		# docstring in author_steal.py: a "left"-target steal swipes with the
		# RIGHT arm crossing over).
		var polarity: String = pair[0]
		var swipe_hand: String = pair[1]
		var active: Animation = built[NAMES[polarity][1]]
		var hips_pos := _pose_origin(active, active.length, "mixamorig_Hips")
		var hand_pos := _pose_origin(active, active.length, swipe_hand)
		var height_above_floor := (hand_pos.y - hips_pos.y) + NEUTRAL_HIP_TO_ANKLE_M
		var lat_offset := (hand_pos - hips_pos).dot(_right)
		# "Crossed the midline" reads as a NONZERO signed lateral offset in the
		# direction away from the swipe arm's own natural side; sign convention
		# is handled by simply requiring a healthy magnitude here (>= 0.10 m),
		# since G4/G6 already prove the two polarities are genuine, opposite
		# mirrors -- this gate only needs "far enough across to read as
		# crossed", not the sign itself.
		print("[rebuild-steal] G5 %s-target active-end: %s height-above-floor=%.4f m (want %.2f-%.2f) lateral-offset=%+.4f m (want |.|>=0.10)"
			% [polarity, swipe_hand, height_above_floor, BALL_HEIGHT_MIN_M, BALL_HEIGHT_MAX_M, lat_offset])
		if not (height_above_floor >= BALL_HEIGHT_MIN_M and height_above_floor <= BALL_HEIGHT_MAX_M):
			push_error("[rebuild-steal] G5 FAILED for %s-target: swipe hand height-above-floor %.4f m is "
				% [polarity, height_above_floor] + "outside the %.2f-%.2f m ball-height band."
				% [BALL_HEIGHT_MIN_M, BALL_HEIGHT_MAX_M])
			g5_ok = false
		if absf(lat_offset) < 0.10:
			push_error("[rebuild-steal] G5 FAILED for %s-target: swipe hand lateral offset %+.4f m is too "
				% [polarity, lat_offset] + "small to read as having crossed the midline.")
			g5_ok = false
	if not g5_ok:
		quit(1)
		return

	# ── G6: target sanity -- the swipe hand's reach-direction lateral offset
	# ADVANCES from Startup's end (the tell) to Active's end (full extension),
	# re-proven on the SLICED clips rather than trusted from the source's own
	# Blender-side proof (author_steal.py's `_swipe_side_advances`) ─────────
	var g6_ok := true
	for pair in [["left", "mixamorig_RightHand", -1.0], ["right", "mixamorig_LeftHand", 1.0]]:
		var polarity: String = pair[0]
		var swipe_hand: String = pair[1]
		var reach_sign: float = pair[2]
		var startup: Animation = built[NAMES[polarity][0]]
		var active: Animation = built[NAMES[polarity][1]]
		var tell_hips := _pose_origin(startup, startup.length, "mixamorig_Hips")
		var tell_hand := _pose_origin(startup, startup.length, swipe_hand)
		var active_hips := _pose_origin(active, active.length, "mixamorig_Hips")
		var active_hand := _pose_origin(active, active.length, swipe_hand)
		var lat_tell: float = (tell_hand - tell_hips).dot(_right) * reach_sign
		var lat_active: float = (active_hand - active_hips).dot(_right) * reach_sign
		print("[rebuild-steal] G6 %s-target: swipe-hand reach-direction lateral tell=%+.4f m active-end=%+.4f m (want active > tell)"
			% [polarity, lat_tell, lat_active])
		if not (lat_active > lat_tell):
			push_error("[rebuild-steal] G6 FAILED for %s-target: reach-direction lateral offset went from "
				% polarity + "%+.4f m (tell) to %+.4f m (active-end) -- did not advance toward the reach side."
				% [lat_tell, lat_active])
			g6_ok = false
	if not g6_ok:
		quit(1)
		return

	# ── G7 (print-only): the #255 non-symmetric handedness control. Raw
	# (un-normalized by reach_sign) hips-relative lateral offset at Active's
	# end must have OPPOSITE signs between polarities -- a mirror bug that
	# silently ignored its own sign argument would still pass G1-G6 above
	# (they are all reach_sign-normalized and therefore symmetric-blind).
	var left_active_end: Animation = built[NAMES["left"][1]]
	var right_active_end: Animation = built[NAMES["right"][1]]
	var l_hips := _pose_origin(left_active_end, left_active_end.length, "mixamorig_Hips")
	var l_hand := _pose_origin(left_active_end, left_active_end.length, "mixamorig_RightHand")
	var r_hips := _pose_origin(right_active_end, right_active_end.length, "mixamorig_Hips")
	var r_hand := _pose_origin(right_active_end, right_active_end.length, "mixamorig_LeftHand")
	var l_raw: float = (l_hand - l_hips).dot(_right)
	var r_raw: float = (r_hand - r_hips).dot(_right)
	print("[rebuild-steal] G7 non-symmetric control: L-target raw hips-relative lateral=%+.4f m, R-target=%+.4f m (want opposite signs)"
		% [l_raw, r_raw])
	if not (l_raw * r_raw < 0.0):
		push_error("[rebuild-steal] G7 FAILED: L-target=%+.4f m and R-target=%+.4f m do not have opposite "
			% [l_raw, r_raw] + "signs -- the #255 mirror-bug class.")
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
		push_error("[rebuild-steal] ResourceSaver.save failed with error %d" % err)
		quit(1)
		return

	print("[rebuild-steal] saved %s; clips now: %s" % [LIB_PATH, str(lib.get_animation_list())])
	quit(0)


func _name_tick_index(name: StringName) -> int:
	var s := String(name)
	if s.contains("startup"):
		return 0
	if s.contains("active"):
		return 1
	return 2


# ── Body axes (verbatim rebuild_behindtheback_clips.gd / rebuild_crossover_
# clips.gd approach) ──────────────────────────────────────────────────────────
# Derived from Y Bot's own REST pose, never from scenes/Player.tscn --
# BlendRestAnchor.cs re-anchors the UpLeg rests at runtime, and every
# foot/toe global rest downstream inherits the error (119.6 deg / 2.17x stride
# mismeasurement in #298). Checked, not assumed: forward.cross(up) points to
# this rig's right (the #255 lesson), verified below against the rest hand
# positions.
func _derive_body_axes() -> bool:
	var foot := _skel.find_bone("mixamorig_LeftFoot")
	var toe := _skel.find_bone("mixamorig_LeftToeBase")
	if foot < 0 or toe < 0:
		push_error("[rebuild-steal] Y Bot has no LeftFoot/LeftToeBase to derive facing from.")
		return false
	var forward: Vector3 = _skel.get_bone_global_rest(toe).origin - _skel.get_bone_global_rest(foot).origin
	forward.y = 0.0
	if forward.length() < 0.001:
		push_error("[rebuild-steal] Y Bot's foot->toe vector is vertical; cannot derive facing.")
		return false
	_forward = forward.normalized()
	_right = _forward.cross(_up).normalized()

	var lh := _skel.find_bone("mixamorig_LeftHand")
	var rh := _skel.find_bone("mixamorig_RightHand")
	if lh < 0 or rh < 0:
		push_error("[rebuild-steal] Y Bot has no LeftHand/RightHand to verify the lateral axis.")
		return false
	var lh_lat: float = _skel.get_bone_global_rest(lh).origin.dot(_right)
	var rh_lat: float = _skel.get_bone_global_rest(rh).origin.dot(_right)
	print("[rebuild-steal] axes: forward=%s right=%s | rest hand lateral L=%+.4f R=%+.4f"
		% [_forward, _right, lh_lat, rh_lat])
	if not (rh_lat > 0.0 and lh_lat < 0.0):
		push_error("[rebuild-steal] the derived right axis puts the RIGHT hand at %+.4f and the LEFT "
			% rh_lat + "at %+.4f -- it is inverted (or the rig's bone names lie)." % lh_lat)
		return false
	return true


# ── Slicing (verbatim rebuild_jumpshot_clips.gd / rebuild_behindtheback_clips.gd
# primitive) ──────────────────────────────────────────────────────────────────
# Resamples source range [t0, t1] into a clip of exactly `ticks` ticks at
# 60 tps, one key per gameplay tick (ticks + 1 keys, the last landing exactly
# on `length`). Keying at the tick rate rather than copying the source's own
# key times is what ties the clip to the move's frame data.
func _slice(src: Animation, t0: float, t1: float, ticks: int) -> Animation:
	var out := Animation.new()
	# Explicit, not inherited: the FBX import default happens to agree (these
	# ARE one-shots) -- which is exactly why it must not be inherited silently.
	out.loop_mode = Animation.LOOP_NONE
	out.length = float(ticks) / TPS

	for i in src.get_track_count():
		var type := src.track_get_type(i)
		if type != Animation.TYPE_ROTATION_3D and type != Animation.TYPE_POSITION_3D:
			# SCALE tracks are dropped deliberately, not overlooked. Blender's
			# exporter bakes full TRS for every bone, so the source carries 65
			# scale tracks -- all identity (the authoring script's own
			# verify_pose_unscaled measured worst deviation 4.8e-7). Keeping
			# them would be worse than useless: PlayerRigScaler applies the
			# height/wingspan chains via SetBonePoseScale, which writes the
			# ANIMATED pose, so a per-bone scale track overwrites it every
			# frame the clip plays.
			continue

		var path := src.track_get_path(i)
		if bone_of(path) == "":
			# The bare "Armature" object-node tracks. Blender's FBX export
			# wraps the skeleton in an Armature object, and Godot imports it as
			# a real node, so the source holds position/rotation/scale tracks
			# for the object ITSELF. Player.tscn's rig has no such node -- its
			# skeleton sits directly at "Skeleton3D" -- so these resolve
			# against nothing.
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
# `form_counts` accumulates which bone-name spelling actually resolved, across
# all six clips, so the caller can print an honest cross-clip match report
# instead of a single clip's number that could get lucky.
func _assert_complete(anim: Animation, name: StringName, expected_rot: int, expected_total: int, form_counts: Dictionary) -> bool:
	var rot := _rotation_track_count(anim)
	var unresolved := []
	var bad_shape := []
	for i in anim.get_track_count():
		var path := anim.track_get_path(i)
		var b := bone_of(path)
		if b == "":
			# NOT a `continue`. A gate that skips every subname-less path
			# silently exempts precisely the tracks that were broken -- the
			# bare "Armature" object-node tracks -- and would report
			# "unresolved=[]" while every track in the clip failed to bind at
			# runtime (README trap 15, proven by #281). A track with no bone
			# subname has no business in a skeletal clip; say so instead of
			# looking away.
			bad_shape.append(String(path))
			continue
		if String(path).begins_with(ARMATURE_PREFIX):
			# Resolves as a BONE NAME but not as a NODE PATH: Player.tscn's
			# skeleton is at "Skeleton3D", not "Armature/Skeleton3D". Checking
			# only the bone name is what made the original #281 gate blind to
			# this.
			bad_shape.append(String(path))
			continue
		var res := _resolve_bone(b)
		var form: String = res[1]
		form_counts[form] = form_counts[form] + 1
		if res[0] < 0:
			unresolved.append(b)
	print("[rebuild-steal]   '%s': len=%.4f tracks=%d rot=%d loop=%d unresolved=%s"
		% [name, anim.length, anim.get_track_count(), rot, anim.loop_mode, str(unresolved)])
	if rot != expected_rot or anim.get_track_count() != expected_total:
		push_error("[rebuild-steal] '%s' has %d/%d tracks, source has %d/%d -- a slice must inherit "
			% [name, rot, anim.get_track_count(), expected_rot, expected_total]
			+ "the source's FULL body coverage or the untracked bones rest-pose at full weight.")
		return false
	if unresolved.size() > 0:
		push_error("[rebuild-steal] '%s' has tracks that do not resolve on Y Bot in EITHER name form: %s"
			% [name, str(unresolved)])
		return false
	if bad_shape.size() > 0:
		push_error("[rebuild-steal] '%s' has %d track(s) whose NODE PATH cannot bind on "
			% [name, bad_shape.size()]
			+ "scenes/Player.tscn (skeleton at 'Skeleton3D', no 'Armature' wrapper): %s. "
			% str(bad_shape)
			+ "Such a track binds to nothing and the clip plays as a silent no-op -- the state "
			+ "machine still enters, the duration still checks out, and the mesh never moves.")
		return false
	return true


func _rotation_track_count(anim: Animation) -> int:
	var n := 0
	for i in anim.get_track_count():
		if anim.track_get_type(i) == Animation.TYPE_ROTATION_3D:
			n += 1
	return n


# Largest per-bone angular difference between two clips sampled across their
# own timelines at matched phase (u in [0,1]) -- the honest measure of "are
# these silhouettes actually distinct". Used for G4 (whole-clip comparison is
# fine there since it scans the full arc).
func _max_pose_delta(a: Animation, b: Animation) -> float:
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
		for s in 24:
			var u := float(s) / 24.0
			var qa: Quaternion = a.rotation_track_interpolate(i, u * a.length)
			var qb: Quaternion = b.rotation_track_interpolate(j, u * b.length)
			var d: float = clampf(absf(qa.normalized().dot(qb.normalized())), -1.0, 1.0)
			worst = maxf(worst, rad_to_deg(2.0 * acos(d)))
	return worst


# Largest per-bone angular difference between a SINGLE named instant in clip
# `a` (time `ta`) and a single named instant in clip `b` (time `tb`). Unlike
# _max_pose_delta (which scans both whole curves at matched phase), this is
# for G3's specific "Startup's END pose vs Recovery's END pose" comparison --
# two fixed poses, not two trajectories.
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
# kinematics.
#
# Deliberately NOT get_bone_global_pose(): a Skeleton3D that was never added to
# the SceneTree does not recompute its global poses, so that call returns the
# unchanged rest transform and every geometric proof built on it passes
# vacuously at exactly 0.0000 (measured, #285). Manual FK depends on nothing
# but the rest pose and the clip's own keys.
#
# BOTH ROTATION_3D and POSITION_3D tracks are walked, unlike
# rebuild_jumpshot_clips.gd's / rebuild_behindtheback_clips.gd's own
# `_pose_origin`, which only walk rotation -- those tools' own gates never read
# a VERTICAL coordinate, so omitting the one POSITION_3D track (Hips) cost them
# nothing. G5 here explicitly needs the Hips' animated vertical position (the
# ball-height-above-floor reading), so this version applies it: a POSITION_3D
# key REPLACES `rest.origin` for that bone, the same way a ROTATION_3D key
# REPLACES the rest rotation (see the comment on the loop below) -- getting
# this wrong would silently measure every height against the RESTING Hips
# height instead of the authored crouch, which is exactly the kind of "looks
# plausible, is quietly wrong" defect this repo's convention says to catch by
# measurement, not by assumption.
#
# Bone lookups go through `_resolve_bone()` so a track authored under either
# the colon or underscore Mixamo prefix form still walks the correct chain
# (see the header trap).
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
		# ROTATION_3D keys are absolute LOCAL rotations, so an animated bone
		# REPLACES the rest basis' rotation; scale carries over. POSITION_3D
		# keys likewise REPLACE the rest origin (only Hips carries one here).
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
