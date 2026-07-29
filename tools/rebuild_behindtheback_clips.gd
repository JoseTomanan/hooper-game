extends SceneTree
# Asset build tool (#281) — drafts the behind-the-back clip family into
# assets/locomotion.res by SLICING assets/behindtheback_authored.fbx.
#
# Run:  godot --headless --path . -s tools/rebuild_behindtheback_clips.gd
# Idempotent: re-running re-derives all six clips from the pristine FBX rather
# than stacking edits (the previous build is removed before the new one lands).
#
# Produces six LOOP_NONE one-shots — three phases x two hand-side polarities:
#   behindthebackstartupleft   /  ...right    6 ticks / 0.1000 s
#   behindthebackactiveleft    /  ...right    3 ticks / 0.0500 s
#   behindthebackrecoveryleft  /  ...right   10 ticks / 0.1667 s
#
# The LEFT/RIGHT suffix names the hand the ball STARTED in (BehindTheBack's
# origin hand), matching scenes/Player.tscn's state names and
# MoveAnimResolver's suffix convention (the same convention rebuild_crossover_
# clips.gd documents at length).
#
# ── Why this is a SLICE, not a compose (unlike rebuild_crossover_clips.gd) ───
# Crossover's source (Dribble.fbx) is a plain dribble cycle with no cross-body
# motion at all, so that tool has to COMPOSE a swing onto it. Behind-the-back's
# source is different: tools/author_behindtheback.py (headless Blender, #315's
# blender_anim_lib machinery) already authored the FULL two-polarity
# Startup/Active/Recovery arc as hand-keyed IK poses, baked at 60 Hz, on ONE
# timeline holding both polarities back to back (see that script's module
# docstring for the frame table). This tool's job is therefore only to
# resample ("slice") the six named windows out of that timeline — exactly
# rebuild_jumpshot_clips.gd's `_slice()` primitive, copied verbatim — and then
# PROVE geometrically that what got sliced is what the issue asked for. There
# is no swing to compose and no sign to derive; author_behindtheback.py already
# resolved and proved its own signs (see its `_ball_side_shoulder_moved_back`
# and `left_vs_right_active` proofs) before ever exporting the FBX.
#
# ── The Mixamo bone-name-prefix trap (read before touching bone_of/_resolve) ─
# In Blender the bones are named `mixamorig:Hips` (colon) — see
# blender_anim_lib.py's HIPS/SPINE constants. Godot 4.6+'s `ufbx` importer
# imports Mixamo-prefixed bones as `mixamorig_Hips` (UNDERSCORE) instead. Both
# the source clip (imported from behindtheback_authored.fbx) and the target
# skeleton (Y Bot.fbx) go through that same importer, so in practice both
# sides should already agree on the underscore form — but "should" is exactly
# the kind of claim this repo's convention says to prove, not assume (a prior
# session's #278 bug was a name-matching gate that silently no-op'd because it
# checked only one spelling). `_resolve_bone()` below therefore tries BOTH
# forms and `_initialize()` prints which form actually matched and how many
# tracks needed it, so a silent zero-match can never hide behind a green run.
#
# ── The a45bd1d full-body-coverage trap ──────────────────────────────────────
# A single-clip AnimationTree state plays at FULL WEIGHT, and Godot's
# AnimationMixer writes every bone the active clip does NOT track to the
# skeleton's rest transform. author_behindtheback.py's own
# `verify_all_bones_keyed(expected_count=52)` gate already proves the SOURCE
# carries full-body coverage (52 rotation tracks + 1 Hips position track, 53
# total — same shape as Dribble.fbx / Goalkeeper Catch Stationary.fbx).
# _assert_complete() below re-proves that every SLICE inherits that coverage
# verbatim rather than trusting the source's own proof to survive slicing.
#
# ── Where the six windows come from ──────────────────────────────────────────
# Hardcoded, not derived: unlike the jumpshot/crossover sources (arbitrary
# stock Mixamo clips whose arcs have to be found by curve analysis),
# author_behindtheback.py's frame layout is DETERMINISTIC BY CONSTRUCTION — it
# keys its four keyposes at exact times computed from BehindTheBack's own
# DefaultFrameData (6/3/10 ticks @ 60 Hz) and the import sets `trimming=false`,
# so those source times land exactly where the docstring says. Re-deriving them
# here with a landmark search would just be re-measuring a number the producer
# already guarantees; instead this tool ASSERTS the guarantee (the source
# clip's total length) so a silently-retrimmed or wrong-fps import fails loudly
# instead of slicing garbage silently.
#
#   source seconds        segment                  ticks
#   0.00000 -> 0.10000     LEFT-origin  Startup      6
#   0.10000 -> 0.15000     LEFT-origin  Active       3
#   0.15000 -> 0.31667     LEFT-origin  Recovery     10
#   (0.31667 -> 0.50000 gap -- never sliced)
#   0.50000 -> 0.60000     RIGHT-origin Startup      6
#   0.60000 -> 0.65000     RIGHT-origin Active       3
#   0.65000 -> 0.81667     RIGHT-origin Recovery     10

const LIB_PATH := "res://assets/locomotion.res"
const SRC_FBX := "res://assets/behindtheback_authored.fbx"
# Matches author_behindtheback.py's ACTION_NAME -- export_fbx() renames both
# the Blender action AND the scene to this so Godot's importer names the
# resulting AnimationPlayer take after it (see that helper's docstring in
# blender_anim_lib.py for why both renames are needed).
const SRC_CLIP := "behindtheback"

# Physics ticks per second (project.godot physics/common/physics_ticks_per_second).
const TPS := 60.0

# The node-path prefix Blender's Armature object wrapper adds; see _rebase_path.
const ARMATURE_PREFIX := "Armature/"

# BehindTheBack's frame data (scripts/Input/BehindTheBack.cs DefaultFrameData).
# Duplicated here because GDScript cannot read the C# constant -- so the
# duplication is made SAFE rather than avoided: LocomotionClipTest asserts each
# clip's length equals BehindTheBack.DefaultFrameData's own tick count / 60,
# reading the C# side directly. Retune the move without re-running this tool
# and that harness goes red and names this file.
const STARTUP_TICKS := 6
const ACTIVE_TICKS := 3
const RECOVERY_TICKS := 10

# Source-time windows, matching author_behindtheback.py's frame table exactly
# (frame numbers there ARE physics ticks at 60 Hz: 0/6/9/19 and 30/36/39/49).
const LEFT_STARTUP := [0.0 / 60.0, 6.0 / 60.0]
const LEFT_ACTIVE := [6.0 / 60.0, 9.0 / 60.0]
const LEFT_RECOVERY := [9.0 / 60.0, 19.0 / 60.0]
const RIGHT_STARTUP := [30.0 / 60.0, 36.0 / 60.0]
const RIGHT_ACTIVE := [36.0 / 60.0, 39.0 / 60.0]
const RIGHT_RECOVERY := [39.0 / 60.0, 49.0 / 60.0]

# The producer exports frame_start=0, frame_end=55 (EXPORT_FRAME_END in
# author_behindtheback.py), so the imported clip's length must be ~55/60 s.
# A silently-retrimmed or wrong-fps import would shift every window above out
# from under the actual keyed poses -- this is what makes that failure loud
# instead of quietly slicing garbage.
const EXPECTED_SRC_LENGTH_S := 55.0 / 60.0
const SRC_LENGTH_TOL_S := 0.02

const NAMES := {
	"left": ["behindthebackstartupleft", "behindthebackactiveleft", "behindthebackrecoveryleft"],
	"right": ["behindthebackstartupright", "behindthebackactiveright", "behindthebackrecoveryright"],
}

# G3/G4 legibility floors (#296's actual complaint -- Startup and Recovery must
# read as visibly different poses, and the two Active polarities must be a
# distinct silhouette from one another). Matches author_behindtheback.py's own
# POSE_DISTINCT_MIN_DEG=15.0 / left_vs_right_active's 20.0 gates -- this tool
# re-proves them on the SLICED clips rather than trusting the source's own
# Blender-side proof to survive the slice untouched.
const STARTUP_VS_RECOVERY_MIN_DEG := 15.0
const LEFT_VS_RIGHT_ACTIVE_MIN_DEG := 20.0

var _skel: Skeleton3D = null
var _right := Vector3.ZERO
var _forward := Vector3.ZERO
var _up := Vector3.UP


func bone_of(np: NodePath) -> String:
	return "" if np.get_subname_count() == 0 else String(np.get_subname(0))


# The node path an AnimationPlayer under scenes/Player.tscn can actually
# resolve. Blender's FBX export wraps the skeleton in an Armature object, so a
# track imported from behindtheback_authored.fbx reads
# "Armature/Skeleton3D:mixamorig_Hips" -- one level deeper than the rig, whose
# skeleton is at "Skeleton3D". Every stock-Mixamo clip already in
# locomotion.res (crossoverstartupleft, idle, ...) uses the short form, so this
# rebases onto that shape rather than inventing a third convention.
#
# This is NOT cosmetic. An unresolvable track binds to nothing, so the clip
# plays as a no-op: the state machine still enters the right state, the clip
# still reports the right duration, and the mesh never moves. Every harness
# assertion about reachability, duration and state->clip mapping passes anyway,
# which is exactly how 2376 "couldn't resolve track" warnings coexisted with
# seven green scenarios before this was found.
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
		push_error("[rebuild-behindtheback] failed to load AnimationLibrary at %s" % LIB_PATH)
		quit(1)
		return

	_skel = _find(load("res://assets/Y Bot.fbx").instantiate(), "Skeleton3D")
	if _skel == null:
		push_error("[rebuild-behindtheback] could not find a Skeleton3D in Y Bot.fbx.")
		quit(1)
		return

	if not _derive_body_axes():
		quit(1)
		return

	var packed = load(SRC_FBX)
	if packed == null:
		push_error("[rebuild-behindtheback] failed to load %s" % SRC_FBX)
		quit(1)
		return
	var ap: AnimationPlayer = _find(packed.instantiate(), "AnimationPlayer")
	if ap == null:
		push_error("[rebuild-behindtheback] %s has no AnimationPlayer." % SRC_FBX)
		quit(1)
		return
	if not ap.has_animation(SRC_CLIP):
		push_error("[rebuild-behindtheback] %s has no AnimationPlayer clip '%s' (has: %s)"
			% [SRC_FBX, SRC_CLIP, str(ap.get_animation_list())])
		quit(1)
		return
	var src: Animation = ap.get_animation(SRC_CLIP)
	print("[rebuild-behindtheback] source '%s': len=%.5f tracks=%d"
		% [SRC_CLIP, src.length, src.get_track_count()])

	# ── Sanity: the source must be the full 0..55-frame export, not a
	# silently-retrimmed or wrong-fps import. This is what makes the hardcoded
	# windows above safe to trust (see header).
	if absf(src.length - EXPECTED_SRC_LENGTH_S) > SRC_LENGTH_TOL_S:
		push_error("[rebuild-behindtheback] source length %.5f s is not within %.3f s of the expected "
			% [src.length, SRC_LENGTH_TOL_S] + "%.5f s (55/60 s @ 60 fps) -- the import may have been "
			% EXPECTED_SRC_LENGTH_S + "retrimmed or baked at the wrong fps, which would silently shift "
			+ "every hardcoded slice window in this file off the authored poses.")
		quit(1)
		return

	# What a SLICE is expected to carry, derived from the source by applying the
	# same two filters _slice() applies -- never the source's raw counts. The
	# source holds full TRS for every bone plus the Armature object node
	# (65*3 + 3 = 198); a slice keeps rotation+position for bone tracks only
	# (65*2 = 130). Deriving the expectation rather than hardcoding 130 keeps
	# this honest if the rig's bone count ever changes, while still failing
	# loudly if _slice starts dropping bone coverage it should have kept -- the
	# a45bd1d rest-pose trap this gate exists for.
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
	print("[rebuild-behindtheback] source has %d tracks; %d expected per slice after dropping "
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
		print("[rebuild-behindtheback] G1 '%s': length=%.5f (expect %.5f) loop_mode=%d %s"
			% [name, anim.length, expected_len, anim.loop_mode, "OK" if (len_ok and loop_ok) else "FAIL"])
		if not (len_ok and loop_ok):
			g1_ok = false
	if not g1_ok:
		push_error("[rebuild-behindtheback] G1 FAILED -- one or more clips has the wrong length or loop_mode.")
		quit(1)
		return

	# ── G2: track-count parity + full bone resolution (both name forms) ─────
	var g2_ok := true
	var form_counts := {"as-given": 0, "alt": 0, "unresolved": 0}
	for name in built:
		if not _assert_complete(built[name], name, src_rot, src_total, form_counts):
			g2_ok = false
	print("[rebuild-behindtheback] G2 bone-name resolution across all six clips: as-given=%d alt=%d unresolved=%d"
		% [form_counts["as-given"], form_counts["alt"], form_counts["unresolved"]])
	if not g2_ok:
		quit(1)
		return
	if form_counts["unresolved"] > 0:
		push_error("[rebuild-behindtheback] G2 FAILED -- %d bone tracks did not resolve on Y Bot in EITHER "
			% form_counts["unresolved"] + "name form.")
		quit(1)
		return
	var resolved_total: int = form_counts["as-given"] + form_counts["alt"]
	if resolved_total <= 0:
		push_error("[rebuild-behindtheback] G2 FAILED -- resolved count is %d; a name-matching gate that "
			% resolved_total + "matches nothing passes vacuously.")
		quit(1)
		return

	# ── G3: per polarity, Startup's END pose vs Recovery's END pose ─────────
	var g3_ok := true
	for polarity in ["left", "right"]:
		var names: Array = NAMES[polarity]
		var startup: Animation = built[names[0]]
		var recovery: Animation = built[names[2]]
		var delta := _pose_delta_at(startup, startup.length, recovery, recovery.length)
		print("[rebuild-behindtheback] G3 %s-origin startup-end vs recovery-end max bone delta = %.1f deg (want >= %.1f)"
			% [polarity, delta, STARTUP_VS_RECOVERY_MIN_DEG])
		if delta < STARTUP_VS_RECOVERY_MIN_DEG:
			push_error("[rebuild-behindtheback] G3 FAILED for %s-origin: only %.1f deg (< %.1f) -- Startup's end "
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
	print("[rebuild-behindtheback] G4 activeleft-vs-activeright max bone delta = %.1f deg (want >= %.1f)"
		% [g4_delta, LEFT_VS_RIGHT_ACTIVE_MIN_DEG])
	if g4_delta < LEFT_VS_RIGHT_ACTIVE_MIN_DEG:
		push_error("[rebuild-behindtheback] G4 FAILED: only %.1f deg (< %.1f) -- the two Active polarities do "
			% [g4_delta, LEFT_VS_RIGHT_ACTIVE_MIN_DEG] + "not read as distinct silhouettes.")
		quit(1)
		return

	# ── G5: the load-bearing silhouette gate -- both wrists BEHIND the hips ──
	var g5_ok := true
	for polarity in ["left", "right"]:
		var active: Animation = built[NAMES[polarity][1]]
		var hips_fwd := _pose_origin(active, 0.0, "mixamorig_Hips").dot(_forward)
		var lh_fwd := _pose_origin(active, 0.0, "mixamorig_LeftHand").dot(_forward)
		var rh_fwd := _pose_origin(active, 0.0, "mixamorig_RightHand").dot(_forward)
		var lh_off := lh_fwd - hips_fwd
		var rh_off := rh_fwd - hips_fwd
		print("[rebuild-behindtheback] G5 %s-origin active: LeftHand forward-offset=%+.4f m RightHand forward-offset=%+.4f m (both want < 0)"
			% [polarity, lh_off, rh_off])
		if not (lh_off < 0.0 and rh_off < 0.0):
			push_error("[rebuild-behindtheback] G5 FAILED for %s-origin: LeftHand offset %+.4f m, RightHand "
				% [polarity, lh_off] + "offset %+.4f m -- both wrists must sit BEHIND the hips' forward "
				% rh_off + "coordinate, or this is not distinguishable from a crossover.")
			g5_ok = false
	if not g5_ok:
		quit(1)
		return

	# ── G6: origin sanity -- the ORIGIN hand travels rearward more than the
	# other hand does, during that polarity's own Startup ─────────────────────
	var g6_ok := true
	for pair in [["left", "mixamorig_LeftHand", "mixamorig_RightHand"], ["right", "mixamorig_RightHand", "mixamorig_LeftHand"]]:
		var polarity: String = pair[0]
		var ball_hand: String = pair[1]
		var other_hand: String = pair[2]
		var startup: Animation = built[NAMES[polarity][0]]
		var ball_travel := _rearward_travel(startup, ball_hand)
		var other_travel := _rearward_travel(startup, other_hand)
		print("[rebuild-behindtheback] G6 %s-origin startup: ball-hand(%s) rearward travel=%+.4f m other-hand(%s) rearward travel=%+.4f m (want ball > other)"
			% [polarity, ball_hand, ball_travel, other_hand, other_travel])
		if not (ball_travel > other_travel):
			push_error("[rebuild-behindtheback] G6 FAILED for %s-origin: ball hand rearward travel %+.4f m does "
				% [polarity, ball_travel] + "not exceed the other hand's %+.4f m -- the wind-up does not read "
				% other_travel + "as originating from the correct hand.")
			g6_ok = false
	if not g6_ok:
		quit(1)
		return

	# ── G7: print-only contrast against the existing crossover clip ─────────
	if lib.has_animation("crossoveractiveleft"):
		var cross: Animation = lib.get_animation("crossoveractiveleft")
		var chips_fwd := _pose_origin(cross, 0.0, "mixamorig_Hips").dot(_forward)
		var clh_fwd := _pose_origin(cross, 0.0, "mixamorig_LeftHand").dot(_forward)
		var crh_fwd := _pose_origin(cross, 0.0, "mixamorig_RightHand").dot(_forward)
		print("[rebuild-behindtheback] G7 (print-only) crossoveractiveleft: LeftHand forward-offset=%+.4f m RightHand forward-offset=%+.4f m (contrast vs G5's negative offsets)"
			% [clh_fwd - chips_fwd, crh_fwd - chips_fwd])
	else:
		print("[rebuild-behindtheback] G7 (print-only) SKIPPED -- 'crossoveractiveleft' not found in %s" % LIB_PATH)

	# ── Save ─────────────────────────────────────────────────────────────────
	# Idempotency: drop any previous build first, so re-running re-derives from
	# the pristine FBX rather than stacking edits.
	for name in built:
		if lib.has_animation(name):
			lib.remove_animation(name)
		lib.add_animation(name, built[name])

	var err := ResourceSaver.save(lib, LIB_PATH)
	if err != OK:
		push_error("[rebuild-behindtheback] ResourceSaver.save failed with error %d" % err)
		quit(1)
		return

	print("[rebuild-behindtheback] saved %s; clips now: %s" % [LIB_PATH, str(lib.get_animation_list())])
	quit(0)


func _name_tick_index(name: StringName) -> int:
	var s := String(name)
	if s.contains("startup"):
		return 0
	if s.contains("active"):
		return 1
	return 2


# ── Body axes (verbatim rebuild_crossover_clips.gd approach) ─────────────────
# Derived from Y Bot's own REST pose, never from scenes/Player.tscn --
# BlendRestAnchor.cs re-anchors the UpLeg rests at runtime, and every
# foot/toe global rest downstream inherits that mutation (#298's 119.6 deg
# error / 2.17x stride mismeasurement). Checked, not assumed: up.cross(forward)
# points to this rig's LEFT (the #255 lesson), so body-right is
# forward.cross(up), verified below against the rest hand positions.
func _derive_body_axes() -> bool:
	var foot := _skel.find_bone("mixamorig_LeftFoot")
	var toe := _skel.find_bone("mixamorig_LeftToeBase")
	if foot < 0 or toe < 0:
		push_error("[rebuild-behindtheback] Y Bot has no LeftFoot/LeftToeBase to derive facing from.")
		return false
	var forward: Vector3 = _skel.get_bone_global_rest(toe).origin - _skel.get_bone_global_rest(foot).origin
	forward.y = 0.0
	if forward.length() < 0.001:
		push_error("[rebuild-behindtheback] Y Bot's foot->toe vector is vertical; cannot derive facing.")
		return false
	_forward = forward.normalized()
	_right = _forward.cross(_up).normalized()

	var lh := _skel.find_bone("mixamorig_LeftHand")
	var rh := _skel.find_bone("mixamorig_RightHand")
	if lh < 0 or rh < 0:
		push_error("[rebuild-behindtheback] Y Bot has no LeftHand/RightHand to verify the lateral axis.")
		return false
	var lh_lat: float = _skel.get_bone_global_rest(lh).origin.dot(_right)
	var rh_lat: float = _skel.get_bone_global_rest(rh).origin.dot(_right)
	print("[rebuild-behindtheback] axes: forward=%s right=%s | rest hand lateral L=%+.4f R=%+.4f"
		% [_forward, _right, lh_lat, rh_lat])
	if not (rh_lat > 0.0 and lh_lat < 0.0):
		push_error("[rebuild-behindtheback] the derived right axis puts the RIGHT hand at %+.4f and the LEFT "
			% rh_lat + "at %+.4f -- it is inverted (or the rig's bone names lie)." % lh_lat)
		return false
	return true


# ── Slicing (verbatim rebuild_jumpshot_clips.gd / rebuild_crossover_clips.gd
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
			# frame the clip plays. PlayerRigScaler's own class doc names this
			# exact hazard ("if a clip animates a scale track, it would fight
			# this node"). Dropping identity tracks costs nothing and keeps
			# these clips the same rotation+position shape as every existing
			# one in locomotion.res.
			continue

		var path := src.track_get_path(i)
		if bone_of(path) == "":
			# The bare "Armature" object-node tracks. Blender's FBX export
			# wraps the skeleton in an Armature object, and Godot imports it as
			# a real node, so the source holds position/rotation/scale tracks
			# for the object ITSELF. Player.tscn's rig has no such node -- its
			# skeleton sits directly at "Skeleton3D" -- so these resolve
			# against nothing. Measured identity ((0,0,0) / (0,0,0,1) /
			# (1,1,1)), so there is no unit scale hiding in them to preserve.
			continue

		var t := out.add_track(type)
		out.track_set_path(t, _rebase_path(path))
		for k in ticks + 1:
			var u := float(k) / float(ticks)
			var st: float = lerpf(t0, t1, u)
			var dt := float(k) / TPS
			# Only the two types the filter above lets through. A TYPE_SCALE_3D
			# arm here would be unreachable, and worse, would read as though
			# scale tracks were still supported when the whole point of the
			# filter is that they must not be (they fight PlayerRigScaler).
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
			# NOT a `continue`. The original gate skipped every subname-less
			# path, which silently exempted precisely the tracks that were
			# broken -- the bare "Armature" object-node tracks -- and let the
			# gate report "unresolved=[]" while every track in the clip failed
			# to bind at runtime. A track with no bone subname has no business
			# in a skeletal clip; say so instead of looking away.
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
	print("[rebuild-behindtheback]   '%s': len=%.4f tracks=%d rot=%d loop=%d unresolved=%s"
		% [name, anim.length, anim.get_track_count(), rot, anim.loop_mode, str(unresolved)])
	if rot != expected_rot or anim.get_track_count() != expected_total:
		push_error("[rebuild-behindtheback] '%s' has %d/%d tracks, source has %d/%d -- a slice must inherit "
			% [name, rot, anim.get_track_count(), expected_rot, expected_total]
			+ "the source's FULL body coverage or the untracked bones rest-pose at full weight.")
		return false
	if unresolved.size() > 0:
		push_error("[rebuild-behindtheback] '%s' has tracks that do not resolve on Y Bot in EITHER name form: %s"
			% [name, str(unresolved)])
		return false
	if bad_shape.size() > 0:
		push_error("[rebuild-behindtheback] '%s' has %d track(s) whose NODE PATH cannot bind on "
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
# fine there since Active is a held pose, start==end by construction).
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


# How far `bone` travels REARWARD (i.e. its forward-axis coordinate DECREASES)
# from `anim`'s first frame to its last. Positive = moved backward. Used by G6
# to check the origin hand's wind-up reads as originating from that hand.
func _rearward_travel(anim: Animation, bone: String) -> float:
	var start_fwd := _pose_origin(anim, 0.0, bone).dot(_forward)
	var end_fwd := _pose_origin(anim, anim.length, bone).dot(_forward)
	return start_fwd - end_fwd


# Global origin of `bone` with `anim` applied at time `t`, by manual forward
# kinematics.
#
# Deliberately NOT get_bone_global_pose(): a Skeleton3D that was never added to
# the SceneTree does not recompute its global poses, so that call returns the
# unchanged rest transform and every geometric proof built on it passes
# vacuously at exactly 0.0000 (measured, #285). Manual FK depends on nothing
# but the rest pose and the clip's own keys.
#
# Only ROTATION_3D tracks are walked (matching rebuild_jumpshot_clips.gd's and
# rebuild_crossover_clips.gd's own _pose_origin): the one POSITION_3D track
# (Hips) carries only a purely-vertical hip-drop delta in this source (see
# author_behindtheback.py's `drop_hips` call), so every gate here that reads a
# FORWARD-axis coordinate is unaffected by omitting it -- the same
# simplification those two files already rely on.
#
# Bone lookups go through `_resolve_bone()` so a track authored under either
# the colon or underscore Mixamo prefix form still walks the correct chain
# (see the header trap).
func _pose_origin(anim: Animation, t: float, bone: String) -> Vector3:
	var res := _resolve_bone(bone)
	var idx: int = res[0]
	if idx < 0:
		return Vector3.ZERO

	var track_of := {}
	for i in anim.get_track_count():
		if anim.track_get_type(i) != Animation.TYPE_ROTATION_3D:
			continue
		var b_res := _resolve_bone(bone_of(anim.track_get_path(i)))
		var b: int = b_res[0]
		if b >= 0:
			track_of[b] = i

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
		if track_of.has(b):
			var q: Quaternion = anim.rotation_track_interpolate(track_of[b], t)
			local = Transform3D(Basis(q).scaled(rest.basis.get_scale()), rest.origin)
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
