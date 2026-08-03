extends SceneTree
# Measurement instrument (#316) — reports the VERTICAL trajectory of the shipped
# jumpshot clip family, i.e. whether the jump shot contains a jump.
#
# Run:  godot --headless --path . -s tools/measure_jumpshot_vertical.gd
#
# Why: #316's third candidate justification for re-authoring is grounding —
# "the startup ends with a jump", proposing verify_grounded() on Startup and
# verify_airborne() on Active. Its first two justifications (track completeness,
# hand-keyed pose-to-pose interpolation) are both measurably false: the clips are
# resampled MOCAP slices at one key per tick, and their only untracked bones are
# inert Mixamo leaf terminators. So grounding is the last argument standing and
# it deserves a number rather than an assumption.
#
# The clips were sliced from `Goalkeeper Catch Stationary` — a STATIONARY reach.
# If that source never leaves the floor, neither does the shot, and the toes
# never leave the ground across the whole three-clip family.
#
# Measures two independent quantities, because either alone is ambiguous:
#   * hips Y  — the root translation. A jump raises the whole body.
#   * lowest foot/toe Y relative to its own grounded minimum — a body can rise
#     by straightening the knees without leaving the floor, which is exactly the
#     difference between "extends up" and "jumps".
# Both by manual FK (see rebuild_jumpshot_clips.gd:_pose_origin for why a
# detached Skeleton3D cannot be asked for global poses).

const LIB_PATH := "res://assets/locomotion.res"
const RIG_FBX := "res://assets/Y Bot.fbx"

const SEGMENTS := ["jumpshotstartup", "jumpshotactive", "jumpshotrecovery"]
const SAMPLES := 12

var _skel: Skeleton3D = null


func bone_of(np: NodePath) -> String:
	return "" if np.get_subname_count() == 0 else String(np.get_subname(0))


func _initialize() -> void:
	var lib = load(LIB_PATH)
	_skel = _find(load(RIG_FBX).instantiate(), "Skeleton3D")
	if lib == null or _skel == null:
		push_error("[vert] could not load library or rig")
		quit(1)
		return

	# Rest-pose reference: where the feet and hips sit with no clip applied at
	# all. Every reading below is relative to this, so the numbers are "how far
	# from standing", not raw rig coordinates.
	var rest_hips := _rest_origin("mixamorig_Hips").y
	var rest_foot := minf(_rest_origin("mixamorig_LeftToeBase").y, _rest_origin("mixamorig_RightToeBase").y)
	print("[vert] rest reference: hips.y=%.4f lowest toe.y=%.4f" % [rest_hips, rest_foot])
	print("")
	print("%-20s %6s %10s %10s %10s" % ["clip", "u", "hips dY", "toe dY", "toe(min)"])

	var global_toe_min := 1e9
	var global_toe_max := -1e9
	var global_hip_min := 1e9
	var global_hip_max := -1e9

	for name in SEGMENTS:
		if not lib.has_animation(name):
			print("%-20s (absent)" % name)
			continue
		var a: Animation = lib.get_animation(name)
		for s in SAMPLES + 1:
			var u := float(s) / float(SAMPLES)
			var t := u * a.length
			var hips := _pose_origin(a, t, "mixamorig_Hips").y - rest_hips
			var lt := _pose_origin(a, t, "mixamorig_LeftToeBase").y
			var rt := _pose_origin(a, t, "mixamorig_RightToeBase").y
			var toe := minf(lt, rt) - rest_foot
			global_toe_min = minf(global_toe_min, toe)
			global_toe_max = maxf(global_toe_max, toe)
			global_hip_min = minf(global_hip_min, hips)
			global_hip_max = maxf(global_hip_max, hips)
			if s % 3 == 0:
				print("%-20s %6.2f %+10.4f %+10.4f" % [name, u, hips, toe])
		print("")

	print("[vert] ACROSS THE WHOLE FAMILY:")
	print("[vert]   hips  dY range: %+.4f .. %+.4f  (excursion %.4f m)"
		% [global_hip_min, global_hip_max, global_hip_max - global_hip_min])
	print("[vert]   toe   dY range: %+.4f .. %+.4f  (excursion %.4f m)"
		% [global_toe_min, global_toe_max, global_toe_max - global_toe_min])
	print("")
	# The verdict #316 turns on. A real jump lifts the lowest toe clear of the
	# floor by a margin that dwarfs mocap foot-roll noise.
	if global_toe_max < 0.05:
		print("[vert] VERDICT: the lowest toe never rises more than %.4f m above its standing"
			% global_toe_max)
		print("[vert]          height. THE JUMP SHOT CONTAINS NO JUMP -- the feet never leave")
		print("[vert]          the floor in any of the three phases.")
	else:
		print("[vert] VERDICT: the lowest toe reaches %+.4f m -- the clip family does leave the floor."
			% global_toe_max)
	quit(0)


func _rest_origin(bone: String) -> Vector3:
	var idx := _skel.find_bone(bone)
	if idx < 0:
		return Vector3.ZERO
	var acc := Transform3D.IDENTITY
	var chain := _chain(idx)
	for b in chain:
		acc = acc * _skel.get_bone_rest(b)
	return acc.origin


func _chain(idx: int) -> Array:
	var chain := []
	var walk := idx
	while walk >= 0:
		chain.push_front(walk)
		walk = _skel.get_bone_parent(walk)
	return chain


# Manual FK honouring BOTH rotation and position tracks — the position track is
# the Hips root translation, which is the whole subject of this measurement, so
# unlike rebuild_jumpshot_clips.gd's rotation-only variant it must not be skipped.
func _pose_origin(anim: Animation, t: float, bone: String) -> Vector3:
	var idx := _skel.find_bone(bone)
	if idx < 0:
		return Vector3.ZERO

	var rot_of := {}
	var pos_of := {}
	for i in anim.get_track_count():
		var b := _skel.find_bone(bone_of(anim.track_get_path(i)))
		if b < 0:
			continue
		match anim.track_get_type(i):
			Animation.TYPE_ROTATION_3D: rot_of[b] = i
			Animation.TYPE_POSITION_3D: pos_of[b] = i

	var acc := Transform3D.IDENTITY
	for b in _chain(idx):
		var rest: Transform3D = _skel.get_bone_rest(b)
		var basis := rest.basis
		if rot_of.has(b):
			basis = Basis(anim.rotation_track_interpolate(rot_of[b], t)).scaled(rest.basis.get_scale())
		var origin := rest.origin
		if pos_of.has(b):
			origin = anim.position_track_interpolate(pos_of[b], t)
		acc = acc * Transform3D(basis, origin)
	return acc.origin


func _find(n: Node, cls: String) -> Node:
	if n.get_class() == cls:
		return n
	for c in n.get_children():
		var r := _find(c, cls)
		if r != null:
			return r
	return null
