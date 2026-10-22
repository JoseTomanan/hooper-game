extends SceneTree
# Rebuild #418's basketball normal-shot family and the distinct #318 fadeaway.
# Run after headless Blender author_jumpshot.py and Godot import.
# Normal action windows are exact 60 Hz ticks, not elevation-curve guesses.
# The original source retains provenance and landmark diagnostics; the
# separately authored fadeaway is unchanged. Full body tracks are mandatory.
# API: https://docs.godotengine.org/en/4.7/classes/class_animation.html

const LIB_PATH := "res://assets/locomotion.res"
const SRC_FBX := "res://assets/Goalkeeper Catch Stationary.fbx"
const SRC_CLIP := "mixamo_com"
const AUTHORED_NORMAL_FBX := "res://assets/jumpshot_authored.fbx"
const AUTHORED_NORMAL_CLIP := "jumpshot"
# #318 default: use the single authored off-balance release while retaining
# the former source-slice-plus-lean route as a measurable recovery path.
const AUTHORED_FADEAWAY_FBX := "res://assets/fadeaway_authored.fbx"
const AUTHORED_FADEAWAY_CLIP := "fadeaway"
const USE_AUTHORED_FADEAWAY := true
const AUTHORED_FADEAWAY_END_S := 4.0 / 60.0
const ARMATURE_PREFIX := "Armature/"

# Physics ticks per second (project.godot physics/common/physics_ticks_per_second).
const TPS := 60.0

# JumpShot's frame data (scripts/Input/JumpShot.cs DefaultFrameData). Duplicated
# here because GDScript cannot read the C# constant — so the duplication is made
# SAFE rather than avoided: LocomotionClipTest asserts each clip's length equals
# JumpShot.DefaultFrameData's own tick count / 60, reading the C# side directly.
# Retune the move without re-running this tool and that harness goes red and
# names this file.
const STARTUP_TICKS := 18
const ACTIVE_TICKS := 4
const RECOVERY_TICKS := 20

const STARTUP_NAME := &"jumpshotstartup"
const ACTIVE_NAME := &"jumpshotactive"
const RECOVERY_NAME := &"jumpshotrecovery"
const FADEAWAY_NAME := &"fadeawayactive"

# How far the fadeaway tips the torso back off the release. Large enough to read
# as a distinct silhouette from the squared-up release at a glance (ADR-0003
# legibility, and #276's "distinct silhouette" bar), small enough to stay a
# plausible shooting posture rather than a fall.
const FADEAWAY_LEAN_DEGREES := 22.0
const LEAN_BONE := "mixamorig_Spine"

# Resolution of the elevation curve used to find the arc's landmarks. 200
# samples over ~2.73 s is ~14 ms per sample, far finer than the 16.7 ms tick the
# slices are quantised to, so landmark precision is not the limiting factor.
const CURVE_SAMPLES := 200

# Fraction of peak elevation marking the end of "full extension" — the Active
# window is the plateau at the top of the reach.
const ACTIVE_END_FRACTION := 0.95
# Fraction of peak elevation marking the end of the descent. Below this the
# source clip is just its static idle tail, which must not bleed into Recovery.
const SETTLE_END_FRACTION := 0.10
# The source must genuinely be an arms-overhead clip. If a re-export ever made
# the peak this shallow, every landmark below would be noise-fitting.
const MIN_PEAK_ELEVATION_M := 0.50

var _facing := Vector3.ZERO   # set by _derive_body_right_axis, reused by the lean proof
var _skel: Skeleton3D = null


func bone_of(np: NodePath) -> String:
	return "" if np.get_subname_count() == 0 else String(np.get_subname(0))


func _rebase_path(np: NodePath) -> NodePath:
	var path := String(np)
	if path.begins_with(ARMATURE_PREFIX):
		path = path.substr(len(ARMATURE_PREFIX))
	# Blender exports Mixamo's colon spelling, while Player.tscn's Skeleton3D
	# uses Godot's underscore spelling. Resolve the prefix at BUILD time rather
	# than accepting a resource that looks complete but binds some limbs to rest.
	var bone := bone_of(NodePath(path))
	if bone.begins_with("mixamorig:"):
		path = path.replace(":" + bone, ":mixamorig_" + bone.substr(len("mixamorig:")))
	return NodePath(path)


func _resolves_on_ybot(name: String) -> bool:
	if _skel.find_bone(name) >= 0:
		return true
	if name.begins_with("mixamorig:"):
		return _skel.find_bone("mixamorig_" + name.substr(len("mixamorig:"))) >= 0
	if name.begins_with("mixamorig_"):
		return _skel.find_bone("mixamorig:" + name.substr(len("mixamorig_"))) >= 0
	return false


func _initialize() -> void:
	var lib = load(LIB_PATH)
	if lib == null or not (lib is AnimationLibrary):
		push_error("[rebuild-jumpshot] failed to load AnimationLibrary at %s" % LIB_PATH)
		quit(1)
		return

	_skel = _find(load("res://assets/Y Bot.fbx").instantiate(), "Skeleton3D")
	if _skel == null:
		push_error("[rebuild-jumpshot] could not find a Skeleton3D in Y Bot.fbx.")
		quit(1)
		return

	var packed = load(SRC_FBX)
	if packed == null:
		push_error("[rebuild-jumpshot] failed to load %s" % SRC_FBX)
		quit(1)
		return
	var ap: AnimationPlayer = _find(packed.instantiate(), "AnimationPlayer")
	if ap == null or not ap.has_animation(SRC_CLIP):
		push_error("[rebuild-jumpshot] %s has no AnimationPlayer clip '%s'" % [SRC_FBX, SRC_CLIP])
		quit(1)
		return
	var src: Animation = ap.get_animation(SRC_CLIP)
	print("[rebuild-jumpshot] source '%s': len=%.4f tracks=%d" % [SRC_CLIP, src.length, src.get_track_count()])

	# ── Derive the arc's landmarks ───────────────────────────────────────────
	var marks := _derive_landmarks(src)
	if marks.is_empty():
		quit(1)
		return
	var t_gather: float = marks["gather"]
	var t_peak: float = marks["peak"]
	var t_active_end: float = marks["active_end"]
	var t_settle: float = marks["settle"]
	print("[rebuild-jumpshot] landmarks: gather=%.4f peak=%.4f active_end=%.4f settle=%.4f"
		% [t_gather, t_peak, t_active_end, t_settle])

	# Ordering is what makes the three slices a partition of one continuous
	# motion rather than three arbitrary windows; assert it rather than assume
	# the threshold scans happened to come back monotonic.
	if not (t_gather < t_peak and t_peak < t_active_end and t_active_end < t_settle):
		push_error("[rebuild-jumpshot] landmarks are not strictly ordered "
			+ "(gather=%.4f peak=%.4f active_end=%.4f settle=%.4f) -- the source clip does not "
			% [t_gather, t_peak, t_active_end, t_settle]
			+ "contain the expected gather/extend/descend arc.")
		quit(1)
		return

	# ── Slice ────────────────────────────────────────────────────────────────
	# Startup ends AT the peak and Active begins there, so the two clips meet
	# with no gap and no overlap: the last Startup frame is the first Active
	# frame's pose, which is what makes the state transition invisible.
	var startup := _slice(src, t_gather, t_peak, STARTUP_TICKS)
	var active := _slice(src, t_peak, t_active_end, ACTIVE_TICKS)
	var recovery := _slice(src, t_active_end, t_settle, RECOVERY_TICKS)
	# #318's authored fadeaway imports some immutable channels as omissions.
	# Its original complete fallback is part of that asset's established pose;
	# filling those channels from the new normal shot would silently reauthor it.
	var legacy_active := active
	var legacy_startup := startup
	var legacy_recovery := recovery

	# ── The fadeaway variant ─────────────────────────────────────────────────
	# One authored action supplies three phases with identical shared keys.
	var normal_packed = load(AUTHORED_NORMAL_FBX)
	if normal_packed == null:
		push_error("[rebuild-jumpshot] missing normal source; run author_jumpshot.py and import")
		quit(1)
		return
	var normal_ap: AnimationPlayer = _find(normal_packed.instantiate(), "AnimationPlayer")
	if normal_ap == null or not normal_ap.has_animation(AUTHORED_NORMAL_CLIP):
		push_error("[rebuild-jumpshot] authored source has no jumpshot action")
		quit(1)
		return
	var normal: Animation = normal_ap.get_animation(AUTHORED_NORMAL_CLIP)
	startup = _slice(normal, 0.0, STARTUP_TICKS / TPS, STARTUP_TICKS)
	active = _slice(normal, STARTUP_TICKS / TPS, (STARTUP_TICKS + ACTIVE_TICKS) / TPS, ACTIVE_TICKS)
	recovery = _slice(normal, (STARTUP_TICKS + ACTIVE_TICKS) / TPS, (STARTUP_TICKS + ACTIVE_TICKS + RECOVERY_TICKS) / TPS, RECOVERY_TICKS)
	# Blender changes the six arm-chain rotations. Preserve the owned source's
	# untouched channels exactly, including its grounding and landing endpoints.
	# A round-trip of those channels otherwise introduces import-basis noise at
	# the shared neutral and short pump-fake exits for no intended motion change.
	_restore_inherited_channels(startup, legacy_startup, true)
	_restore_inherited_channels(active, legacy_active, false)
	# The eight-tick feint first displays Startup's tail and hands off to
	# neutral with Recovery's sixth key still visible. Preserve that measured
	# final pose, not an unobserved theoretical eighth animation key.
	_restore_inherited_channels(recovery, legacy_recovery, false, 6.0 / TPS)

	var lean_axis := _derive_body_right_axis()
	if lean_axis == Vector3.ZERO:
		push_error("[rebuild-jumpshot] could not derive the body's right axis from Y Bot's rest pose.")
		quit(1)
		return
	# NEGATIVE angle: _derive_body_right_axis returns the axis about which a
	# POSITIVE rotation tips the torso FORWARD (rebuild_dribble_clips.gd's drive
	# lean uses it that way). A fadeaway is the opposite sign of the same axis.
	var fadeaway := active.duplicate(true)
	var fadeaway_expected_rot := _rotation_track_count(src)
	if USE_AUTHORED_FADEAWAY:
		var authored_packed = load(AUTHORED_FADEAWAY_FBX)
		if authored_packed == null:
			push_error("[rebuild-jumpshot] failed to load authored fadeaway %s" % AUTHORED_FADEAWAY_FBX)
			quit(1)
			return
		var authored_ap: AnimationPlayer = _find(authored_packed.instantiate(), "AnimationPlayer")
		if authored_ap == null or not authored_ap.has_animation(AUTHORED_FADEAWAY_CLIP):
			push_error("[rebuild-jumpshot] %s has no authored clip '%s'" % [AUTHORED_FADEAWAY_FBX, AUTHORED_FADEAWAY_CLIP])
			quit(1)
			return
		var authored: Animation = authored_ap.get_animation(AUTHORED_FADEAWAY_CLIP)
		if authored.length < AUTHORED_FADEAWAY_END_S:
			push_error("[rebuild-jumpshot] authored fadeaway length %.4f is shorter than its 4-tick source window %.4f"
				% [authored.length, AUTHORED_FADEAWAY_END_S])
			quit(1)
			return
		fadeaway = _slice(authored, 0.0, AUTHORED_FADEAWAY_END_S, ACTIVE_TICKS)
		# Blender may omit an unchanged, rest-valued rotation channel on export.
		# A single Godot AnimationTree state then resets that non-authored limb to
		# the T-pose. The squared-up active is the shared base pose for this one
		# release variant, so inherit only missing channels from it; authored tracks
		# always win and retain the lean, hips, arms, and airborne legs.
		_fill_missing_rotation_tracks(fadeaway, legacy_active)
		fadeaway_expected_rot = _rotation_track_count(fadeaway)
		print("[rebuild-jumpshot] authored fadeaway '%s': len=%.4f tracks=%d" % [AUTHORED_FADEAWAY_CLIP, authored.length, authored.get_track_count()])
	else:
		var lean := Quaternion(lean_axis, deg_to_rad(-FADEAWAY_LEAN_DEGREES))
		var leaned := _apply_lean(fadeaway, LEAN_BONE, lean)
		if leaned <= 0:
			push_error("[rebuild-jumpshot] no '%s' rotation track to lean -- refusing to save a fadeaway "
				% LEAN_BONE + "identical to the squared-up release.")
			quit(1)
			return

	# Prove the lean goes BACK, geometrically, rather than trusting the sign:
	# pose a real skeleton with each clip and check the head actually moved
	# AGAINST the facing axis. A sign error here drafts a lean-FORWARD, which
	# reads as a drive/floater — a different move, not merely a rough-looking
	# one. (Mirror of rebuild_dribble_clips.gd's forward-lean proof, inverted.)
	var head_shift := _head_shift_along(active, fadeaway, _facing)
	print("[rebuild-jumpshot] fadeaway head displacement along facing = %+.4f m (want negative)" % head_shift)
	# The legacy composition and its source share this tool's FK basis, so this
	# remains a useful sign gate there. An authored FBX crosses Blender's import
	# basis before it reaches Player.tscn; #318's live Skeleton3D harness is the
	# authoritative direction proof for that path.
	if not USE_AUTHORED_FADEAWAY and head_shift >= 0.0:
		push_error("[rebuild-jumpshot] the fadeaway lean moved the head %+.4f m ALONG facing -- that is a "
			% head_shift + "lean FORWARD. Check the sign against _derive_body_right_axis()'s handedness.")
		quit(1)
		return

	# The whole point of a separate FadeawayActive state is that it looks
	# different from the squared-up release; measure it instead of assuming the
	# edit landed (the repo's prove-it-numerically convention).
	var spread := _max_pose_delta(active, fadeaway)
	print("[rebuild-jumpshot] fadeaway-vs-standard max pose delta = %.1f deg" % spread)
	if spread < 5.0:
		push_error("[rebuild-jumpshot] fadeaway differs from the standard release by only %.1f deg -- "
			% spread + "not a distinct silhouette.")
		quit(1)
		return

	# ── Completeness + distinctness guards ───────────────────────────────────
	var built := {
		STARTUP_NAME: startup,
		ACTIVE_NAME: active,
		RECOVERY_NAME: recovery,
		FADEAWAY_NAME: fadeaway,
	}
	var src_rot := _rotation_track_count(normal)
	for name in built:
		var expected_rot := fadeaway_expected_rot if name == FADEAWAY_NAME else src_rot
		if not _assert_complete(built[name], name, expected_rot):
			quit(1)
			return

	# A jump shot whose startup does not visibly rise is not legible (ADR-0003).
	# This is the one assertion that would catch a slice landing on the wrong
	# part of the curve — e.g. if a re-exported source moved the landmarks.
	var rise := _elevation_at(startup, startup.length) - _elevation_at(startup, 0.0)
	print("[rebuild-jumpshot] startup hand rise = %+.4f m" % rise)
	if rise < 0.40:
		push_error("[rebuild-jumpshot] startup only raises the hands %+.4f m -- expected a clear "
			% rise + "overhead extension (>= 0.40 m). The slice is on the wrong part of the arc.")
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
		push_error("[rebuild-jumpshot] ResourceSaver.save failed with error %d" % err)
		quit(1)
		return

	print("[rebuild-jumpshot] saved %s; clips now: %s" % [LIB_PATH, str(lib.get_animation_list())])
	quit(0)


# ── Landmark derivation ──────────────────────────────────────────────────────
# Reads the shot arc off a mean-hand-elevation-above-hips curve. Hips-relative
# rather than absolute so a source clip that crouches or rises does not shift
# the landmarks; "hands relative to the body" is what an overhead reach IS.
func _derive_landmarks(src: Animation) -> Dictionary:
	var curve := []
	for s in CURVE_SAMPLES + 1:
		var t := (float(s) / float(CURVE_SAMPLES)) * src.length
		curve.append(_elevation_at(src, t))

	var peak_i := 0
	for i in curve.size():
		if curve[i] > curve[peak_i]:
			peak_i = i
	var peak: float = curve[peak_i]
	if peak < MIN_PEAK_ELEVATION_M:
		push_error("[rebuild-jumpshot] source peaks at only %.4f m above the hips -- expected an "
			% peak + "arms-overhead clip (>= %.2f m). Wrong source, or a re-export changed it."
			% MIN_PEAK_ELEVATION_M)
		return {}

	# The gather bottom: the lowest the hands get BEFORE the extension.
	var gather_i := 0
	for i in range(0, peak_i + 1):
		if curve[i] < curve[gather_i]:
			gather_i = i

	# Walk forward off the peak for the two descent thresholds.
	var active_end_i := -1
	var settle_i := -1
	for i in range(peak_i + 1, curve.size()):
		if active_end_i < 0 and curve[i] < peak * ACTIVE_END_FRACTION:
			active_end_i = i
		if settle_i < 0 and curve[i] < peak * SETTLE_END_FRACTION:
			settle_i = i
			break
	if active_end_i < 0 or settle_i < 0:
		push_error("[rebuild-jumpshot] the source never descends past the %.0f%%/%.0f%% thresholds after "
			% [ACTIVE_END_FRACTION * 100.0, SETTLE_END_FRACTION * 100.0]
			+ "its peak -- it does not contain a complete extend-and-lower arc.")
		return {}

	var step := src.length / float(CURVE_SAMPLES)
	return {
		"gather": float(gather_i) * step,
		"peak": float(peak_i) * step,
		"active_end": float(active_end_i) * step,
		"settle": float(settle_i) * step,
	}


# Mean hand height above the hips at time `t`, by manual FK (see _pose_origin).
func _elevation_at(anim: Animation, t: float) -> float:
	var hips := _pose_origin(anim, t, "mixamorig_Hips")
	var lh := _pose_origin(anim, t, "mixamorig_LeftHand")
	var rh := _pose_origin(anim, t, "mixamorig_RightHand")
	return ((lh.y - hips.y) + (rh.y - hips.y)) * 0.5


# ── Slicing ──────────────────────────────────────────────────────────────────
# Resamples source range [t0, t1] into a clip of exactly `ticks` ticks at 60 tps,
# one key per gameplay tick (ticks + 1 keys, the last landing exactly on
# `length`). Keying at the tick rate rather than copying the source's own key
# times is what ties the clip to the move's frame data: the displayed motion and
# the authoritative move phase advance in lockstep, so the wind-up an opponent
# reads fills exactly the real Startup window and never a frame more (#276
# point 4 -- "no false reads").
#
# Every track type the source carries is resampled, not just rotations. Dropping
# the position track would silently strip the clip's hip motion; dropping any
# bone track at all would re-open the full-weight rest-pose trap this whole file
# is arranged to avoid.
func _slice(src: Animation, t0: float, t1: float, ticks: int) -> Animation:
	var out := Animation.new()
	# Explicit, not inherited: FBX import defaults to LOOP_NONE and these ARE
	# one-shots, so the default happens to be right -- which is exactly why it
	# gets set on purpose. `dribbleidle`/`dribblemove` needed the opposite and
	# the silent default was the easiest thing to get wrong there (#285).
	out.loop_mode = Animation.LOOP_NONE
	out.length = float(ticks) / TPS

	for i in src.get_track_count():
		var type := src.track_get_type(i)
		if type != Animation.TYPE_ROTATION_3D \
			and type != Animation.TYPE_POSITION_3D \
			and type != Animation.TYPE_SCALE_3D:
			continue
		var t := out.add_track(type)
		out.track_set_path(t, _rebase_path(src.track_get_path(i)))
		for k in ticks + 1:
			var u := float(k) / float(ticks)
			var st: float = lerpf(t0, t1, u)
			var dt := float(k) / TPS
			match type:
				Animation.TYPE_ROTATION_3D:
					out.rotation_track_insert_key(t, dt, src.rotation_track_interpolate(i, st))
				Animation.TYPE_POSITION_3D:
					out.position_track_insert_key(t, dt, src.position_track_interpolate(i, st))
				Animation.TYPE_SCALE_3D:
					out.scale_track_insert_key(t, dt, src.scale_track_interpolate(i, st))
	return out


func _restore_inherited_channels(authored: Animation, original: Animation, startup: bool, arm_tail_s: float = INF) -> void:
	var arm_bones := ["mixamorig_LeftArm", "mixamorig_LeftForeArm", "mixamorig_LeftHand",
		"mixamorig_RightArm", "mixamorig_RightForeArm", "mixamorig_RightHand"]
	for track in authored.get_track_count():
		var type := authored.track_get_type(track)
		var path := authored.track_get_path(track)
		var old := original.find_track(path, type)
		if old < 0:
			continue
		var is_arm := bone_of(path) in arm_bones and type == Animation.TYPE_ROTATION_3D
		for key in authored.track_get_key_count(track):
			var time := authored.track_get_key_time(track, key)
			if is_arm and not (startup and key == 0) and time < arm_tail_s:
				continue
			match type:
				Animation.TYPE_ROTATION_3D:
					authored.track_set_key_value(track, key, original.rotation_track_interpolate(old, time))
				Animation.TYPE_POSITION_3D:
					authored.track_set_key_value(track, key, original.position_track_interpolate(old, time))
				Animation.TYPE_SCALE_3D:
					authored.track_set_key_value(track, key, original.scale_track_interpolate(old, time))


# The a45bd1d guard, applied at build time as well as in the harness: a slice
# that lost bone tracks would T-pose the missing bones the moment its state was
# entered, and it would do so silently.
func _assert_complete(anim: Animation, name: StringName, expected_rot: int) -> bool:
	var rot := _rotation_track_count(anim)
	var unresolved := []
	for i in anim.get_track_count():
		var b := bone_of(anim.track_get_path(i))
		if b != "" and not _resolves_on_ybot(b):
			unresolved.append(b)
	print("[rebuild-jumpshot]   '%s': len=%.4f tracks=%d rot=%d loop=%d unresolved=%s"
		% [name, anim.length, anim.get_track_count(), rot, anim.loop_mode, str(unresolved)])
	if rot != expected_rot:
		push_error("[rebuild-jumpshot] '%s' has %d rotation tracks, source has %d -- a slice must "
			% [name, rot, expected_rot] + "inherit the source's FULL body coverage or the untracked "
			+ "bones rest-pose (T-pose) at full weight.")
		return false
	if unresolved.size() > 0:
		push_error("[rebuild-jumpshot] '%s' has tracks that do not resolve on Y Bot: %s"
			% [name, str(unresolved)])
		return false
	return true


func _rotation_track_count(anim: Animation) -> int:
	var n := 0
	for i in anim.get_track_count():
		if anim.track_get_type(i) == Animation.TYPE_ROTATION_3D:
			n += 1
	return n


# ── Lean helpers (mirrors of rebuild_dribble_clips.gd's, kept local) ─────────
# The axis about which a POSITIVE rotation tips the torso FORWARD. Derived from
# Y Bot's own rest pose rather than hardcoded, because "which world axis is
# forward" depends on the FBX's import orientation.
func _derive_body_right_axis() -> Vector3:
	var foot := _skel.find_bone("mixamorig_LeftFoot")
	var toe := _skel.find_bone("mixamorig_LeftToeBase")
	if foot < 0 or toe < 0:
		return Vector3.ZERO
	var forward: Vector3 = _skel.get_bone_global_rest(toe).origin - _skel.get_bone_global_rest(foot).origin
	forward.y = 0.0
	if forward.length() < 0.001:
		return Vector3.ZERO
	forward = forward.normalized()
	_facing = forward
	var axis := Vector3.UP.cross(forward).normalized()
	print("[rebuild-jumpshot] derived facing=%s -> lean axis (up x facing)=%s" % [forward, axis])
	return axis


# Pre-multiplies every key of `bone`'s rotation track by `lean` (world-space
# delta * local rotation), tilting the bone in the skeleton's frame — which is
# what a torso lean is. Post-multiplying would twist it about its own
# already-rotated local axis instead.
func _apply_lean(anim: Animation, bone: String, lean: Quaternion) -> int:
	var touched := 0
	for i in anim.get_track_count():
		if anim.track_get_type(i) != Animation.TYPE_ROTATION_3D:
			continue
		if bone_of(anim.track_get_path(i)) != bone:
			continue
		for k in anim.track_get_key_count(i):
			var q: Quaternion = anim.track_get_key_value(i, k)
			anim.track_set_key_value(i, k, (lean * q).normalized())
			touched += 1
	return touched


func _fill_missing_rotation_tracks(authored: Animation, fallback: Animation) -> void:
	var authored_bones := {}
	for i in authored.get_track_count():
		if authored.track_get_type(i) == Animation.TYPE_ROTATION_3D:
			authored_bones[bone_of(authored.track_get_path(i))] = true
	for i in fallback.get_track_count():
		if fallback.track_get_type(i) != Animation.TYPE_ROTATION_3D:
			continue
		var path := fallback.track_get_path(i)
		if authored_bones.has(bone_of(path)):
			continue
		var out := authored.add_track(Animation.TYPE_ROTATION_3D)
		authored.track_set_path(out, path)
		for k in fallback.track_get_key_count(i):
			authored.rotation_track_insert_key(out, fallback.track_get_key_time(i, k), fallback.track_get_key_value(i, k))


# Largest per-bone angular difference between two clips at matched phase — the
# honest measure of "are these silhouettes actually distinct".
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


# How far the head moved along `axis` from `before`'s frame 0 to `after`'s.
func _head_shift_along(before: Animation, after: Animation, axis: Vector3) -> float:
	return (_pose_origin(after, 0.0, "mixamorig_Head") - _pose_origin(before, 0.0, "mixamorig_Head")).dot(axis)


# Global origin of `bone` with `anim` applied at time `t`, by manual forward
# kinematics.
#
# Deliberately NOT get_bone_global_pose(): a Skeleton3D that was never added to
# the SceneTree does not recompute its global poses, so that call returns the
# unchanged rest transform and every geometric proof built on it passes
# vacuously at exactly 0.0000 (measured, #285). Manual FK depends on nothing but
# the rest pose and the clip's own keys.
func _pose_origin(anim: Animation, t: float, bone: String) -> Vector3:
	var idx := _skel.find_bone(bone)
	if idx < 0:
		return Vector3.ZERO

	var track_of := {}
	for i in anim.get_track_count():
		if anim.track_get_type(i) != Animation.TYPE_ROTATION_3D:
			continue
		var b := _skel.find_bone(bone_of(anim.track_get_path(i)))
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
