extends SceneTree
# Asset build tool (#309) — slices the between-the-legs clip family into
# assets/locomotion.res from assets/betweenthelegs_authored.fbx.
#
# Run:  godot --headless --path . -s tools/rebuild_betweenthelegs_clips.gd
# Idempotent: re-running re-derives all six clips from the pristine FBX rather
# than stacking edits (the previous build is removed before the new one lands).
#
# Produces six LOOP_NONE one-shots — three phases x two hand-side polarities:
#   betweenthelegsstartupleft   /  ...right    6 ticks / 0.1000 s
#   betweenthelegsactiveleft    /  ...right    3 ticks / 0.0500 s
#   betweenthelegsrecoveryleft  /  ...right   11 ticks / 0.1833 s
#
# The LEFT/RIGHT suffix names the hand the ball STARTED in (BetweenTheLegs's
# origin hand), matching scenes/Player.tscn's state names and
# MoveAnimResolver's suffix convention.
#
# ── This is a SLICE, not a compose ───────────────────────────────────────────
# tools/author_betweenthelegs.py (headless Blender, #315's blender_anim_lib
# machinery) already authored the FULL two-polarity Startup/Active/Recovery arc
# as hand-keyed IK poses, baked at 60 Hz, on ONE timeline holding both
# polarities back to back. This tool's job is to resample ("slice") the six
# named windows out of that timeline — rebuild_jumpshot_clips.gd's `_slice()`
# primitive, copied verbatim — and then PROVE geometrically that what got
# sliced is what the issue asked for. There is no swing to compose and no sign
# to derive; the authoring script already resolved and proved its own signs
# before ever exporting the FBX.
#
# ── Why re-prove what Blender already proved ─────────────────────────────────
# Because the slice is a real transformation with its own failure modes:
# resampling can land a window off the authored poses (a retrimmed or wrong-fps
# import shifts every hardcoded source time), and the track filtering can drop
# coverage. The Blender-side gates prove the SOURCE; these prove the ARTEFACT
# that actually ships. The two measure the same claims through different code,
# which is the point — see G5/G6 below, whose live-rig third measurement is
# BetweenTheLegsAnimTest.
#
# ── The Mixamo bone-name-prefix trap ─────────────────────────────────────────
# In Blender the bones are `mixamorig:Hips` (colon); Godot's ufbx importer
# writes `mixamorig_Hips` (underscore). Both the source clip and the target
# skeleton go through that same importer so in practice both agree — but
# "should" is exactly the kind of claim this repo proves rather than assumes (a
# prior session's #278 bug was a name-matching gate that silently no-op'd
# because it checked one spelling). `_resolve_bone()` tries BOTH forms and
# `_initialize()` prints which form matched and how many tracks needed it, so a
# silent zero-match cannot hide behind a green run.
#
# ── The a45bd1d full-body-coverage trap ──────────────────────────────────────
# A single-clip AnimationTree state plays at FULL WEIGHT, and Godot's
# AnimationMixer writes every bone the active clip does NOT track to the
# skeleton's rest transform. author_betweenthelegs.py's own
# `verify_all_bones_keyed(expected_count=52)` proves the SOURCE carries
# full-body coverage; `_assert_complete()` re-proves that every SLICE inherits
# it rather than trusting the source's proof to survive slicing.
#
# ── Where the six windows come from ──────────────────────────────────────────
# Hardcoded, not derived: author_betweenthelegs.py's frame layout is
# DETERMINISTIC BY CONSTRUCTION (keyposes at exact times computed from
# BetweenTheLegs.DefaultFrameData, 6/3/11 ticks @ 60 Hz) and the import sets
# `trimming=false`, so those source times land exactly where its docstring
# says. Rather than re-deriving them with a landmark search, this tool ASSERTS
# the guarantee (the source clip's total length) so a silently-retrimmed or
# wrong-fps import fails loudly instead of slicing garbage.
#
#   source seconds        segment                  ticks
#   0.00000 -> 0.10000     LEFT-origin  Startup      6
#   0.10000 -> 0.15000     LEFT-origin  Active       3
#   0.15000 -> 0.33333     LEFT-origin  Recovery    11
#   (0.33333 -> 0.50000 gap -- never sliced)
#   0.50000 -> 0.60000     RIGHT-origin Startup      6
#   0.60000 -> 0.65000     RIGHT-origin Active       3
#   0.65000 -> 0.83333     RIGHT-origin Recovery    11

const LIB_PATH := "res://assets/locomotion.res"
const SRC_FBX := "res://assets/betweenthelegs_authored.fbx"
# Matches author_betweenthelegs.py's ACTION_NAME — export_fbx() renames both the
# Blender action AND the scene to this so Godot's importer names the resulting
# AnimationPlayer take after it.
const SRC_CLIP := "betweenthelegs"

# Physics ticks per second (project.godot physics/common/physics_ticks_per_second).
const TPS := 60.0

# The node-path prefix Blender's Armature object wrapper adds; see _rebase_path.
const ARMATURE_PREFIX := "Armature/"

# BetweenTheLegs's frame data (scripts/Input/BetweenTheLegs.cs DefaultFrameData).
# Duplicated here because GDScript cannot read the C# constant — so the
# duplication is made SAFE rather than avoided: BetweenTheLegsAnimTest's
# `betweenthelegs-segment-lengths` scenario asserts each clip's length equals
# BetweenTheLegs.DefaultFrameData's own tick count / 60, reading the C# side
# directly. Retune the move without re-running this tool and that harness goes
# red and names this file.
const STARTUP_TICKS := 6
const ACTIVE_TICKS := 3
const RECOVERY_TICKS := 11

# Source-time windows, matching author_betweenthelegs.py's frame table exactly
# (frame numbers there ARE physics ticks at 60 Hz: 0/6/9/20 and 30/36/39/50).
const LEFT_STARTUP := [0.0 / 60.0, 6.0 / 60.0]
const LEFT_ACTIVE := [6.0 / 60.0, 9.0 / 60.0]
const LEFT_RECOVERY := [9.0 / 60.0, 20.0 / 60.0]
const RIGHT_STARTUP := [30.0 / 60.0, 36.0 / 60.0]
const RIGHT_ACTIVE := [36.0 / 60.0, 39.0 / 60.0]
const RIGHT_RECOVERY := [39.0 / 60.0, 50.0 / 60.0]

# The producer exports frame_start=0, frame_end=55 (EXPORT_FRAME_END in
# author_betweenthelegs.py), so the imported clip's length must be ~55/60 s.
const EXPECTED_SRC_LENGTH_S := 55.0 / 60.0
const SRC_LENGTH_TOL_S := 0.02

const NAMES := {
	"left": ["betweenthelegsstartupleft", "betweenthelegsactiveleft", "betweenthelegsrecoveryleft"],
	"right": ["betweenthelegsstartupright", "betweenthelegsactiveright", "betweenthelegsrecoveryright"],
}

# G3/G4 legibility floors (#296's actual complaint — Startup and Recovery must
# read as visibly different poses, and the two Active polarities must be
# distinct silhouettes). Match author_betweenthelegs.py's own
# POSE_DISTINCT_MIN_DEG=15.0 / LEFT_VS_RIGHT_ACTIVE_MIN_DEG=20.0 gates; this
# tool re-proves them on the SLICED clips.
const STARTUP_VS_RECOVERY_MIN_DEG := 15.0
const LEFT_VS_RIGHT_ACTIVE_MIN_DEG := 20.0

# G5, the silhouette. Both floors match the authoring script's own
# (STANCE_WIDEN_MIN_M / HANDS_INSIDE_KNEES_MIN_M); the measurement is
# re-implemented here against the sliced Animation resources rather than the
# Blender pose, so a bug in one measurement path is unlikely to be replicated
# in the other.
const STANCE_WIDEN_MIN_M := 0.08
const HANDS_INSIDE_KNEES_MIN_M := 0.05

# G6, the handedness oracle — see that gate for why it is the only NON-SYMMETRIC
# claim this clip can make.
const RECV_HAND_HIGHER_MIN_M := 0.06

var _skel: Skeleton3D = null
var _right := Vector3.ZERO
var _forward := Vector3.ZERO
var _up := Vector3.UP


func bone_of(np: NodePath) -> String:
	return "" if np.get_subname_count() == 0 else String(np.get_subname(0))


# The node path an AnimationPlayer under scenes/Player.tscn can actually
# resolve. Blender's FBX export wraps the skeleton in an Armature object, so a
# track imported from betweenthelegs_authored.fbx reads
# "Armature/Skeleton3D:mixamorig_Hips" — one level deeper than the rig, whose
# skeleton is at "Skeleton3D".
#
# This is NOT cosmetic. An unresolvable track binds to nothing, so the clip
# plays as a no-op: the state machine still enters the right state, the clip
# still reports the right duration, and the mesh never moves (#281, where 2376
# "couldn't resolve track" warnings coexisted with seven green scenarios).
func _rebase_path(np: NodePath) -> NodePath:
	var s := String(np)
	if s.begins_with(ARMATURE_PREFIX):
		return NodePath(s.substr(len(ARMATURE_PREFIX)))
	return np


func _alt_bone_name(name: String) -> String:
	if name.begins_with("mixamorig:"):
		return "mixamorig_" + name.substr(len("mixamorig:"))
	if name.begins_with("mixamorig_"):
		return "mixamorig:" + name.substr(len("mixamorig_"))
	return name


# Returns [bone_index, form_used] where form_used is "as-given", "alt", or
# "unresolved".
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
		push_error("[rebuild-betweenthelegs] failed to load AnimationLibrary at %s" % LIB_PATH)
		quit(1)
		return

	_skel = _find(load("res://assets/Y Bot.fbx").instantiate(), "Skeleton3D")
	if _skel == null:
		push_error("[rebuild-betweenthelegs] could not find a Skeleton3D in Y Bot.fbx.")
		quit(1)
		return

	if not _derive_body_axes():
		quit(1)
		return

	var packed = load(SRC_FBX)
	if packed == null:
		push_error("[rebuild-betweenthelegs] failed to load %s" % SRC_FBX)
		quit(1)
		return
	var ap: AnimationPlayer = _find(packed.instantiate(), "AnimationPlayer")
	if ap == null:
		push_error("[rebuild-betweenthelegs] %s has no AnimationPlayer." % SRC_FBX)
		quit(1)
		return
	if not ap.has_animation(SRC_CLIP):
		push_error("[rebuild-betweenthelegs] %s has no AnimationPlayer clip '%s' (has: %s)"
			% [SRC_FBX, SRC_CLIP, str(ap.get_animation_list())])
		quit(1)
		return
	var src: Animation = ap.get_animation(SRC_CLIP)
	print("[rebuild-betweenthelegs] source '%s': len=%.5f tracks=%d"
		% [SRC_CLIP, src.length, src.get_track_count()])

	# ── Sanity: the source must be the full 0..55-frame export. This is what
	# makes the hardcoded windows above safe to trust (see header).
	if absf(src.length - EXPECTED_SRC_LENGTH_S) > SRC_LENGTH_TOL_S:
		push_error("[rebuild-betweenthelegs] source length %.5f s is not within %.3f s of the expected "
			% [src.length, SRC_LENGTH_TOL_S] + "%.5f s (55/60 s @ 60 fps) -- the import may have been "
			% EXPECTED_SRC_LENGTH_S + "retrimmed or baked at the wrong fps, which would silently shift "
			+ "every hardcoded slice window in this file off the authored poses.")
		quit(1)
		return

	# What a SLICE is expected to carry, DERIVED from the source by applying the
	# same two filters _slice() applies — never the source's raw counts.
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
	print("[rebuild-betweenthelegs] source has %d tracks; %d expected per slice after dropping "
		% [src.get_track_count(), src_total]
		+ "SCALE (fights PlayerRigScaler) and the Armature object node (unbindable on Player.tscn).")

	# ── Slice the six windows ────────────────────────────────────────────────
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
		print("[rebuild-betweenthelegs] G1 '%s': length=%.5f (expect %.5f) loop_mode=%d %s"
			% [name, anim.length, expected_len, anim.loop_mode, "OK" if (len_ok and loop_ok) else "FAIL"])
		if not (len_ok and loop_ok):
			g1_ok = false
	if not g1_ok:
		push_error("[rebuild-betweenthelegs] G1 FAILED -- one or more clips has the wrong length or loop_mode.")
		quit(1)
		return

	# ── G2: track-count parity + full bone resolution (both name forms) ─────
	var g2_ok := true
	var form_counts := {"as-given": 0, "alt": 0, "unresolved": 0}
	for name in built:
		if not _assert_complete(built[name], name, src_rot, src_total, form_counts):
			g2_ok = false
	print("[rebuild-betweenthelegs] G2 bone-name resolution across all six clips: as-given=%d alt=%d unresolved=%d"
		% [form_counts["as-given"], form_counts["alt"], form_counts["unresolved"]])
	if not g2_ok:
		quit(1)
		return
	if form_counts["unresolved"] > 0:
		push_error("[rebuild-betweenthelegs] G2 FAILED -- %d bone tracks did not resolve on Y Bot in EITHER "
			% form_counts["unresolved"] + "name form.")
		quit(1)
		return
	var resolved_total: int = form_counts["as-given"] + form_counts["alt"]
	if resolved_total <= 0:
		push_error("[rebuild-betweenthelegs] G2 FAILED -- resolved count is %d; a name-matching gate that "
			% resolved_total + "matches nothing passes vacuously.")
		quit(1)
		return

	# ── G3: per polarity, Startup's END pose vs Recovery's END pose (#296) ──
	var g3_ok := true
	for polarity in ["left", "right"]:
		var names: Array = NAMES[polarity]
		var startup: Animation = built[names[0]]
		var recovery: Animation = built[names[2]]
		var delta := _pose_delta_at(startup, startup.length, recovery, recovery.length)
		print("[rebuild-betweenthelegs] G3 %s-origin startup-end vs recovery-end max bone delta = %.1f deg (want >= %.1f)"
			% [polarity, delta, STARTUP_VS_RECOVERY_MIN_DEG])
		if delta < STARTUP_VS_RECOVERY_MIN_DEG:
			push_error("[rebuild-betweenthelegs] G3 FAILED for %s-origin: only %.1f deg (< %.1f) -- Startup's end "
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
	print("[rebuild-betweenthelegs] G4 activeleft-vs-activeright max bone delta = %.1f deg (want >= %.1f)"
		% [g4_delta, LEFT_VS_RIGHT_ACTIVE_MIN_DEG])
	if g4_delta < LEFT_VS_RIGHT_ACTIVE_MIN_DEG:
		push_error("[rebuild-betweenthelegs] G4 FAILED: only %.1f deg (< %.1f) -- the two Active polarities do "
			% [g4_delta, LEFT_VS_RIGHT_ACTIVE_MIN_DEG] + "not read as distinct silhouettes.")
		quit(1)
		return

	# ── G5: the load-bearing silhouette gate — stance WIDTH and ball HEIGHT ──
	# Handoff 09: "Stance width and ball height are this move's signature."
	# Two claims, both reduced with `min` over the limb pair (README trap 17 --
	# "knees APART" and "both hands between them" are both-limbs claims, and a
	# one-legged step or one flung hand must fail rather than be averaged away):
	#
	#   (a) both ankles sit further from the midline at Active than at Startup;
	#   (b) both wrists sit INSIDE the narrower knee at Active.
	#
	# (b) is what makes the clip agree with BallSweepPath.ThroughLegs. That path
	# leaves the ball's forward offset at DribbleForwardOffset (IN FRONT) and
	# sweeps its lateral factor through the MIDLINE at t=0.5 under a deep
	# vertical dip — so the ball transits low and down the centreline. A hand
	# outside the knee line while the ball mesh routes down the middle makes the
	# hands and the ball visibly disagree, which is handoff 09's named hazard.
	# Read the path; never modify it.
	#
	# Both wrists are bounded by the NARROWER of the two knees rather than each
	# by its own-side knee, because at Active the origin hand has followed the
	# ball PAST the midline and is physically on the other side of the body.
	var g5_ok := true
	for polarity in ["left", "right"]:
		var startup: Animation = built[NAMES[polarity][0]]
		var active: Animation = built[NAMES[polarity][1]]

		var widen := {}
		for side in ["Left", "Right"]:
			var at_startup := _lateral_from_midline(startup, 0.0, "mixamorig_%sFoot" % side)
			var at_active := _lateral_from_midline(active, 0.0, "mixamorig_%sFoot" % side)
			widen[side] = at_active - at_startup
		var widen_min: float = minf(widen["Left"], widen["Right"])
		print("[rebuild-betweenthelegs] G5a %s-origin stance widen: L=%+.4f m R=%+.4f m min=%+.4f (want >= %.2f)"
			% [polarity, widen["Left"], widen["Right"], widen_min, STANCE_WIDEN_MIN_M])
		if widen_min < STANCE_WIDEN_MIN_M:
			push_error("[rebuild-betweenthelegs] G5a FAILED for %s-origin: the narrower ankle widened only "
				% polarity + "%+.4f m (< %.2f) -- stance WIDTH is half this move's signature."
				% [widen_min, STANCE_WIDEN_MIN_M])
			g5_ok = false

		var knee_min := minf(
			_lateral_from_midline(active, 0.0, "mixamorig_LeftLeg"),
			_lateral_from_midline(active, 0.0, "mixamorig_RightLeg"))
		var margins := {}
		for side in ["Left", "Right"]:
			margins[side] = knee_min - _lateral_from_midline(active, 0.0, "mixamorig_%sHand" % side)
		var margin_min: float = minf(margins["Left"], margins["Right"])
		print("[rebuild-betweenthelegs] G5b %s-origin hands inside knees: narrowestKnee=%.4f m L margin=%+.4f R margin=%+.4f min=%+.4f (want >= %.2f)"
			% [polarity, knee_min, margins["Left"], margins["Right"], margin_min, HANDS_INSIDE_KNEES_MIN_M])
		if margin_min < HANDS_INSIDE_KNEES_MIN_M:
			push_error("[rebuild-betweenthelegs] G5b FAILED for %s-origin: a wrist sits only %+.4f m inside the "
				% [polarity, margin_min] + "narrower knee (< %.2f) -- both hands must be BETWEEN the knees, or "
				% HANDS_INSIDE_KNEES_MIN_M + "they disagree with BallSweepPath.ThroughLegs routing the ball "
				+ "down the midline.")
			g5_ok = false
	if not g5_ok:
		quit(1)
		return

	# ── G6: the handedness oracle (the ONLY non-symmetric claim here) ────────
	# README trap 5: the Y Bot rig is mirror-symmetric to 0.17 mm across X=0, so
	# a symmetric assertion proves NOTHING about handedness — a clip whose
	# polarity silently inverted would sail through G1-G5.
	#
	# This move's stance is symmetric BY DESIGN ("knees APART" is the read), so
	# the polarity lives entirely in the arms: at Recovery's end the RECEIVING
	# hand has risen to dribble height on the NEW side while the hand that
	# pushed the ball through trails low. Naming which physical hand that is,
	# per polarity, is a claim that INVERTS when the polarity does — which is
	# exactly what #255's mirror bug needed and did not have.
	var g6_ok := true
	for pair in [["left", "mixamorig_LeftHand", "mixamorig_RightHand"],
			["right", "mixamorig_RightHand", "mixamorig_LeftHand"]]:
		var polarity: String = pair[0]
		var origin_hand: String = pair[1]
		var recv_hand: String = pair[2]
		var recovery: Animation = built[NAMES[polarity][2]]
		var origin_h := _pose_origin(recovery, recovery.length, origin_hand).dot(_up)
		var recv_h := _pose_origin(recovery, recovery.length, recv_hand).dot(_up)
		var gap := recv_h - origin_h
		print("[rebuild-betweenthelegs] G6 %s-origin recovery-end: recv(%s) height - origin(%s) height = %+.4f m (want >= %.2f)"
			% [polarity, recv_hand, origin_hand, gap, RECV_HAND_HIGHER_MIN_M])
		if gap < RECV_HAND_HIGHER_MIN_M:
			push_error("[rebuild-betweenthelegs] G6 FAILED for %s-origin: the receiving hand (%s) sits only "
				% [polarity, recv_hand] + "%+.4f m above the origin hand (%s) (< %.2f). The polarity is "
				% [gap, origin_hand, RECV_HAND_HIGHER_MIN_M] + "inverted or absent -- and this is the only "
				+ "gate here that can tell, since the rig is mirror-symmetric (README trap 5 / #255).")
			g6_ok = false
	if not g6_ok:
		quit(1)
		return

	# ── G7: print-only three-way contrast against the sibling clips ─────────
	# Handoff 09 states the read as a three-way contrast: crossover = both hands
	# in FRONT at knee height; behind-the-back = both wrists BEHIND the hip
	# line; between-the-legs = knees APART with both hands BETWEEN them, ball
	# below the hips. G5 asserts this clip's own half; these lines put the other
	# two on the same page of the log so a reviewer can see the contrast in
	# measured numbers rather than take it on faith. Print-only on purpose —
	# this issue does not own those clips, and asserting against them would make
	# an unrelated retune of either one redden this build.
	for other in ["crossoveractiveleft", "behindthebackactiveleft"]:
		if not lib.has_animation(other):
			print("[rebuild-betweenthelegs] G7 (print-only) SKIPPED -- '%s' not in %s" % [other, LIB_PATH])
			continue
		var clip: Animation = lib.get_animation(other)
		var hips_fwd := _pose_origin(clip, 0.0, "mixamorig_Hips").dot(_forward)
		var lh := _pose_origin(clip, 0.0, "mixamorig_LeftHand")
		var rh := _pose_origin(clip, 0.0, "mixamorig_RightHand")
		print("[rebuild-betweenthelegs] G7 (print-only) %s: LeftHand fwd-offset=%+.4f lat=%.4f | RightHand fwd-offset=%+.4f lat=%.4f"
			% [other, lh.dot(_forward) - hips_fwd, _lateral_from_midline(clip, 0.0, "mixamorig_LeftHand"),
				rh.dot(_forward) - hips_fwd, _lateral_from_midline(clip, 0.0, "mixamorig_RightHand")])
	var own: Animation = built[NAMES["left"][1]]
	var own_hips_fwd := _pose_origin(own, 0.0, "mixamorig_Hips").dot(_forward)
	print("[rebuild-betweenthelegs] G7 (print-only) betweenthelegsactiveleft: LeftHand fwd-offset=%+.4f lat=%.4f | RightHand fwd-offset=%+.4f lat=%.4f"
		% [_pose_origin(own, 0.0, "mixamorig_LeftHand").dot(_forward) - own_hips_fwd,
			_lateral_from_midline(own, 0.0, "mixamorig_LeftHand"),
			_pose_origin(own, 0.0, "mixamorig_RightHand").dot(_forward) - own_hips_fwd,
			_lateral_from_midline(own, 0.0, "mixamorig_RightHand")])

	# ── Save ─────────────────────────────────────────────────────────────────
	# Idempotency: drop any previous build first, so re-running re-derives from
	# the pristine FBX rather than stacking edits.
	for name in built:
		if lib.has_animation(name):
			lib.remove_animation(name)
		lib.add_animation(name, built[name])

	var err := ResourceSaver.save(lib, LIB_PATH)
	if err != OK:
		push_error("[rebuild-betweenthelegs] ResourceSaver.save failed with error %d" % err)
		quit(1)
		return

	print("[rebuild-betweenthelegs] saved %s; clips now: %s" % [LIB_PATH, str(lib.get_animation_list())])
	quit(0)


func _name_tick_index(name: StringName) -> int:
	var s := String(name)
	if s.contains("startup"):
		return 0
	if s.contains("active"):
		return 1
	return 2


# ── Body axes ────────────────────────────────────────────────────────────────
# Derived from Y Bot's own REST pose, never from scenes/Player.tscn —
# BlendRestAnchor.cs re-anchors the UpLeg rests at runtime and every foot/toe
# global rest downstream inherits that mutation (#298's 119.6 deg error / 2.17x
# stride mismeasurement). Checked, not assumed: up.cross(forward) points to this
# rig's LEFT (the #255 lesson), so body-right is forward.cross(up), verified
# below against the rest hand positions.
func _derive_body_axes() -> bool:
	var foot := _skel.find_bone("mixamorig_LeftFoot")
	var toe := _skel.find_bone("mixamorig_LeftToeBase")
	if foot < 0 or toe < 0:
		push_error("[rebuild-betweenthelegs] Y Bot has no LeftFoot/LeftToeBase to derive facing from.")
		return false
	var forward: Vector3 = _skel.get_bone_global_rest(toe).origin - _skel.get_bone_global_rest(foot).origin
	forward.y = 0.0
	if forward.length() < 0.001:
		push_error("[rebuild-betweenthelegs] Y Bot's foot->toe vector is vertical; cannot derive facing.")
		return false
	_forward = forward.normalized()
	_right = _forward.cross(_up).normalized()

	var lh := _skel.find_bone("mixamorig_LeftHand")
	var rh := _skel.find_bone("mixamorig_RightHand")
	if lh < 0 or rh < 0:
		push_error("[rebuild-betweenthelegs] Y Bot has no LeftHand/RightHand to verify the lateral axis.")
		return false
	var lh_lat: float = _skel.get_bone_global_rest(lh).origin.dot(_right)
	var rh_lat: float = _skel.get_bone_global_rest(rh).origin.dot(_right)
	print("[rebuild-betweenthelegs] axes: forward=%s right=%s | rest hand lateral L=%+.4f R=%+.4f"
		% [_forward, _right, lh_lat, rh_lat])
	if not (rh_lat > 0.0 and lh_lat < 0.0):
		push_error("[rebuild-betweenthelegs] the derived right axis puts the RIGHT hand at %+.4f and the LEFT "
			% rh_lat + "at %+.4f -- it is inverted (or the rig's bone names lie)." % lh_lat)
		return false
	return true


# ── Slicing (verbatim rebuild_jumpshot_clips.gd primitive) ───────────────────
# Resamples source range [t0, t1] into a clip of exactly `ticks` ticks at 60 tps,
# one key per gameplay tick (ticks + 1 keys, the last landing exactly on
# `length`). Keying at the tick rate rather than copying the source's own key
# times is what ties the clip to the move's frame data.
func _slice(src: Animation, t0: float, t1: float, ticks: int) -> Animation:
	var out := Animation.new()
	# Explicit, not inherited: the FBX import default happens to agree (these ARE
	# one-shots) — which is exactly why it must not be inherited silently.
	out.loop_mode = Animation.LOOP_NONE
	out.length = float(ticks) / TPS

	for i in src.get_track_count():
		var type := src.track_get_type(i)
		if type != Animation.TYPE_ROTATION_3D and type != Animation.TYPE_POSITION_3D:
			# SCALE tracks are dropped deliberately, not overlooked. Blender's
			# exporter bakes full TRS, so the source carries 65 scale tracks --
			# all identity (the authoring script's own verify_pose_unscaled
			# measured worst deviation 4.8e-7). Keeping them would be worse than
			# useless: PlayerRigScaler applies the height/wingspan chains via
			# SetBonePoseScale, which writes the ANIMATED pose, so a per-bone
			# scale track overwrites it every frame the clip plays --
			# PlayerRigScaler's own class doc names this exact hazard.
			continue

		var path := src.track_get_path(i)
		if bone_of(path) == "":
			# The bare "Armature" object-node tracks. Blender's FBX export wraps
			# the skeleton in an Armature object and Godot imports it as a real
			# node, so the source holds position/rotation/scale tracks for the
			# object ITSELF. Player.tscn's rig has no such node -- its skeleton
			# sits directly at "Skeleton3D" -- so these resolve against nothing.
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
			# NOT a `continue`. The original #281 gate skipped every subname-less
			# path, which silently exempted precisely the tracks that were broken
			# -- the bare "Armature" object-node tracks -- and let the gate report
			# "unresolved=[]" while every track in the clip failed to bind.
			bad_shape.append(String(path))
			continue
		if String(path).begins_with(ARMATURE_PREFIX):
			# Resolves as a BONE NAME but not as a NODE PATH: Player.tscn's
			# skeleton is at "Skeleton3D", not "Armature/Skeleton3D". Checking
			# only the bone name is what made the original gate blind to this.
			bad_shape.append(String(path))
			continue
		var res := _resolve_bone(b)
		var form: String = res[1]
		form_counts[form] = form_counts[form] + 1
		if res[0] < 0:
			unresolved.append(b)
	print("[rebuild-betweenthelegs]   '%s': len=%.4f tracks=%d rot=%d loop=%d unresolved=%s"
		% [name, anim.length, anim.get_track_count(), rot, anim.loop_mode, str(unresolved)])
	if rot != expected_rot or anim.get_track_count() != expected_total:
		push_error("[rebuild-betweenthelegs] '%s' has %d/%d tracks, source has %d/%d -- a slice must inherit "
			% [name, rot, anim.get_track_count(), expected_rot, expected_total]
			+ "the source's FULL body coverage or the untracked bones rest-pose at full weight.")
		return false
	if unresolved.size() > 0:
		push_error("[rebuild-betweenthelegs] '%s' has tracks that do not resolve on Y Bot in EITHER name form: %s"
			% [name, str(unresolved)])
		return false
	if bad_shape.size() > 0:
		push_error("[rebuild-betweenthelegs] '%s' has %d track(s) whose NODE PATH cannot bind on "
			% [name, bad_shape.size()]
			+ "scenes/Player.tscn (skeleton at 'Skeleton3D', no 'Armature' wrapper): %s. "
			% str(bad_shape)
			+ "Such a track binds to nothing and the clip plays as a silent no-op -- the state machine "
			+ "still enters, the duration still checks out, and the mesh never moves.")
		return false
	return true


func _rotation_track_count(anim: Animation) -> int:
	var n := 0
	for i in anim.get_track_count():
		if anim.track_get_type(i) == Animation.TYPE_ROTATION_3D:
			n += 1
	return n


# Largest per-bone angular difference between two clips sampled across their own
# timelines at matched phase (u in [0,1]).
func _max_pose_delta(a: Animation, b: Animation) -> float:
	var worst := 0.0
	for i in a.get_track_count():
		if a.track_get_type(i) != Animation.TYPE_ROTATION_3D:
			continue
		var bone := bone_of(a.track_get_path(i))
		var j := _matching_rotation_track(b, bone)
		if j < 0:
			continue
		for s in 24:
			var u := float(s) / 24.0
			var qa: Quaternion = a.rotation_track_interpolate(i, u * a.length)
			var qb: Quaternion = b.rotation_track_interpolate(j, u * b.length)
			var d: float = clampf(absf(qa.normalized().dot(qb.normalized())), -1.0, 1.0)
			worst = maxf(worst, rad_to_deg(2.0 * acos(d)))
	return worst


# Largest per-bone angular difference between a SINGLE named instant in clip `a`
# and a single named instant in clip `b` — for G3's "Startup's END pose vs
# Recovery's END pose", two fixed poses rather than two trajectories.
func _pose_delta_at(a: Animation, ta: float, b: Animation, tb: float) -> float:
	var worst := 0.0
	for i in a.get_track_count():
		if a.track_get_type(i) != Animation.TYPE_ROTATION_3D:
			continue
		var bone := bone_of(a.track_get_path(i))
		var j := _matching_rotation_track(b, bone)
		if j < 0:
			continue
		var qa: Quaternion = a.rotation_track_interpolate(i, ta)
		var qb: Quaternion = b.rotation_track_interpolate(j, tb)
		var d: float = clampf(absf(qa.normalized().dot(qb.normalized())), -1.0, 1.0)
		worst = maxf(worst, rad_to_deg(2.0 * acos(d)))
	return worst


func _matching_rotation_track(anim: Animation, bone: String) -> int:
	for t in anim.get_track_count():
		if anim.track_get_type(t) == Animation.TYPE_ROTATION_3D and bone_of(anim.track_get_path(t)) == bone:
			return t
	return -1


# |bone - Hips| projected on the body-right axis, in METRES. Always
# non-negative: it is a DISTANCE FROM THE MIDLINE, deliberately side-agnostic,
# because at Active the origin hand has crossed past the midline onto the other
# side of the body and an unsigned reading stays correct however far it crosses.
#
# (An unsigned form is the wrong choice for a "travelled in then out" claim and
# the right one for a "how far from the midline" claim -- the #308/#339 lesson
# is that the form must match the CLAIM, not be swept one way globally.)
func _lateral_from_midline(anim: Animation, t: float, bone: String) -> float:
	var hips := _pose_origin(anim, t, "mixamorig_Hips")
	var p := _pose_origin(anim, t, bone)
	return absf((p - hips).dot(_right))


# Global origin of `bone` with `anim` applied at time `t`, by manual forward
# kinematics.
#
# Deliberately NOT get_bone_global_pose(): a Skeleton3D that was never added to
# the SceneTree does not recompute its global poses, so that call returns the
# unchanged rest transform and every geometric proof built on it passes
# vacuously at exactly 0.0000 (measured, #285). Manual FK depends on nothing but
# the rest pose and the clip's own keys.
#
# Walks POSITION_3D tracks as well as ROTATION_3D, unlike the equivalent helper
# in rebuild_behindtheback_clips.gd. That file could omit the single Hips
# position track because every gate it fed read a FORWARD-axis coordinate and
# the drop is purely vertical. G6 here is a HEIGHT comparison, so the exclusion
# would no longer be self-evidently harmless — it happens to cancel (both hands
# inherit the same Hips offset), but relying on a cancellation is a worse
# foundation than simply applying the track.
func _pose_origin(anim: Animation, t: float, bone: String) -> Vector3:
	var res := _resolve_bone(bone)
	var idx: int = res[0]
	if idx < 0:
		# Poisoned rather than Vector3.ZERO: a Zero fallback makes an
		# unresolvable bone read as "at the origin" and lets a gate print a
		# plausible number while measuring nothing (#305's lesson). NAN
		# propagates through every comparison below as false, so the gate fails.
		return Vector3(NAN, NAN, NAN)

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
		# REPLACES the rest basis' rotation; scale and origin carry over.
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
