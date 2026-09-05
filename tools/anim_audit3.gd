## anim_audit3.gd — PROVE the consequence of the run-clip track gap.
##
## anim_audit2 measured that locomotion/run omits rotation tracks for Spine1,
## Spine2, Neck, LeftHand and RightHand, while locomotion/idle has all five.
## This script proves what that does to the RENDERED pose by driving a real
## AnimationTree and reading actual Skeleton3D bone rotations at each end of the
## Locomotion BlendSpace.
##
## Harness notes (both learned the hard way):
##   • Setup must happen in _initialize() but sampling must happen in _process(),
##     because the AnimationTree does not build its track caches until it has
##     been in the tree for a frame. Sampling in _initialize() returns rest for
##     EVERY bone, which looks exactly like a confirmed finding.
##   • PROBE therefore includes CONTROL bones the run clip demonstrably does
##     track (RightUpLeg at 180deg, Spine at 140deg in the clip data). If a
##     control reads 0.0, the harness is broken and the run is void — not proof.
##
## Run: godot --headless --path . --script res://tools/anim_audit3.gd
extends SceneTree

const RIG_PATH := "res://assets/Y Bot.fbx"
const LIB_PATH := "res://assets/locomotion.res"
const PLAYER_PATH := "res://scenes/Player.tscn"

# Bones the run clip omits (the hypothesis) followed by controls it tracks.
const SUSPECT := ["mixamorig_Spine1", "mixamorig_Spine2", "mixamorig_Neck",
	"mixamorig_LeftHand", "mixamorig_RightHand"]
const CONTROL := ["mixamorig_RightUpLeg", "mixamorig_Spine", "mixamorig_LeftUpLeg"]
const MATERIAL_SPINE2_FLOOR_DEG := 10.0
const RIGHT_UP_LEG_CONTROL := [176.46, 23.07, 175.55]
const RIGHT_UP_LEG_TOLERANCE_DEG := 2.0

var _skel: Skeleton3D
var _tree: AnimationTree
var _pb: AnimationNodeStateMachinePlayback
var _rest := {}
var _bone_idx := {}
var _frame := 0
var _phase := 0
var _rows := {}
var _phase_rows := {}
const BLENDS := [0.0, 3.0, 6.0]


func _initialize() -> void:
	var rig := (load(RIG_PATH) as PackedScene).instantiate()
	root.add_child(rig)
	_skel = _find_skel(rig)
	for i in range(_skel.get_bone_count()):
		var n := _skel.get_bone_name(i)
		_bone_idx[n] = i
		_rest[n] = _skel.get_bone_rest(i).basis.get_rotation_quaternion()

	var ap := AnimationPlayer.new()
	root.add_child(ap)
	ap.add_animation_library("locomotion", load(LIB_PATH))
	ap.root_node = ap.get_path_to(rig)

	_tree = AnimationTree.new()
	root.add_child(_tree)
	_tree.tree_root = _load_sm()
	_tree.anim_player = _tree.get_path_to(ap)
	_tree.root_node = _tree.get_path_to(rig)
	_tree.callback_mode_process = AnimationMixer.ANIMATION_CALLBACK_MODE_PROCESS_MANUAL
	_tree.active = true

	print("")
	print("=== PROOF: live AnimationTree bone poses vs REST ===")
	print("  deg = geodesic angle of the LIVE bone pose from its REST rotation.")
	print("")


func _process(_delta: float) -> bool:
	_frame += 1
	# Give the tree a few frames to build caches before touching it.
	if _frame < 4:
		return false
	if _pb == null:
		_pb = _tree.get("parameters/playback")
		_pb.travel("Locomotion")
		_step()
		return false

	if _phase < BLENDS.size():
		var blend: float = BLENDS[_phase]
		_tree.set("parameters/Locomotion/blend_position", blend)
		_step()
		var m := {}
		for b in SUSPECT + CONTROL:
			m[b] = _dev(b)
		_rows[blend] = m
		_phase += 1
		return false

	var gen_states := ["Startup", "Active", "Recovery", "Pivot", "ReboundGrab"]
	var gi := _phase - BLENDS.size()
	if gi < gen_states.size():
		_pb.travel(gen_states[gi])
		_step()
		_phase_rows[gen_states[gi]] = {
			"RightUpLeg": _dev("mixamorig_RightUpLeg"),
			"Spine1": _dev("mixamorig_Spine1"),
			"LeftArm": _dev("mixamorig_LeftArm"),
		}
		_phase += 1
		return false

	_report()
	return true


func _step() -> void:
	# Advance real time; the first advance after a state activates only primes it.
	for i in range(4):
		_tree.advance(1.0 / 60.0)


func _report() -> void:
	var void_run := false
	for control_bone in CONTROL:
		var maximum := 0.0
		for blend in BLENDS:
			maximum = maxf(maximum, _rows[blend][control_bone])
		if maximum < 0.05:
			void_run = true
	var passed := not void_run
	if void_run:
		push_error("[locomotion-rest-frame] a control bone never left rest; live-pose results are void.")

	for i in BLENDS.size():
		var blend: float = BLENDS[i]
		var spine2: float = _rows[blend]["mixamorig_Spine2"]
		var up_leg: float = _rows[blend]["mixamorig_RightUpLeg"]
		print("[locomotion-rest-frame] blend=%.1f Spine2=%.2f RightUpLeg=%.2f" % [blend, spine2, up_leg])
		if spine2 < MATERIAL_SPINE2_FLOOR_DEG:
			push_error("[locomotion-rest-frame] Spine2 at blend %.1f is %.2f deg (< %.1f): the run endpoint collapsed to rest." % [blend, spine2, MATERIAL_SPINE2_FLOOR_DEG])
			passed = false
		if absf(up_leg - RIGHT_UP_LEG_CONTROL[i]) > RIGHT_UP_LEG_TOLERANCE_DEG:
			push_error("[locomotion-rest-frame] RightUpLeg at blend %.1f is %.2f, expected %.2f +/- %.2f." % [blend, up_leg, RIGHT_UP_LEG_CONTROL[i], RIGHT_UP_LEG_TOLERANCE_DEG])
			passed = false

	if not _assert_resource_contract():
		passed = false
	if passed:
		print("[locomotion-rest-frame] PASS — runtime posture and material endpoint coverage are stable.")
		quit(0)
	else:
		print("[locomotion-rest-frame] FAIL — see structural or live-pose assertion above.")
		quit(1)


func _assert_resource_contract() -> bool:
	var passed := true
	var import_text := FileAccess.get_file_as_string("res://assets/run.fbx.import")
	if not import_text.contains("animation/remove_immutable_tracks=false"):
		push_error("[locomotion-rest-frame] run.fbx.import must retain immutable tracks for clean imports.")
		passed = false

	var lib := load(LIB_PATH) as AnimationLibrary
	if lib == null or not lib.has_animation(&"idle") or not lib.has_animation(&"run"):
		push_error("[locomotion-rest-frame] locomotion.res must contain idle and run.")
		return false
	var required := {}
	for i in _skel.get_bone_count():
		if _skel.get_bone_children(i).is_empty():
			continue
		var bone := _skel.get_bone_name(i)
		if is_finger_joint(bone):
			continue
		required[bone] = true
	if required.is_empty() or not required.has("mixamorig_Spine2"):
		push_error("[locomotion-rest-frame] material requirement is empty or excludes Spine2; the gate is vacuous.")
		return false

	for clip_name in [&"idle", &"run"]:
		var clip := lib.get_animation(clip_name)
		var covered := {}
		for track in clip.get_track_count():
			if clip.track_get_type(track) != Animation.TYPE_ROTATION_3D:
				continue
			var path := clip.track_get_path(track)
			if path.get_subname_count() == 0:
				continue
			var bone := String(path.get_subname(0))
			if not required.has(bone):
				continue
			if String(path) != "Skeleton3D:%s" % bone:
				push_error("[locomotion-rest-frame] %s '%s' has non-runtime binding '%s'." % [clip_name, bone, path])
				passed = false
			if clip.track_get_key_count(track) == 0 or covered.has(bone):
				push_error("[locomotion-rest-frame] %s '%s' has an empty or duplicate rotation track." % [clip_name, bone])
				passed = false
			covered[bone] = true
		var missing := []
		for bone in required:
			if not covered.has(bone):
				missing.append(bone)
		if not missing.is_empty():
			push_error("[locomotion-rest-frame] %s omits material rotation tracks: %s." % [clip_name, missing])
			passed = false
		else:
			print("[locomotion-rest-frame] %s covers all %d material rotation bones with runtime bindings." % [clip_name, required.size()])
	return passed


func is_finger_joint(bone: String) -> bool:
	for side in ["mixamorig_LeftHand", "mixamorig_RightHand"]:
		for digit in ["Thumb", "Index", "Middle", "Ring", "Pinky"]:
			if bone.begins_with(side + digit):
				return true
	return false


func _dev(bone: String) -> float:
	if not _bone_idx.has(bone):
		return -1.0
	var cur := _skel.get_bone_pose_rotation(_bone_idx[bone])
	var rest: Quaternion = _rest[bone]
	return rad_to_deg(2.0 * acos(clampf(absf(cur.normalized().dot(rest)), -1.0, 1.0)))


func _find_skel(n: Node) -> Skeleton3D:
	if n is Skeleton3D:
		return n
	for c in n.get_children():
		var r := _find_skel(c)
		if r != null:
			return r
	return null


func _load_sm() -> AnimationNodeStateMachine:
	var st := (load(PLAYER_PATH) as PackedScene).get_state()
	for i in range(st.get_node_count()):
		if str(st.get_node_name(i)) != "AnimationTree":
			continue
		for p in range(st.get_node_property_count(i)):
			if str(st.get_node_property_name(i, p)) == "tree_root":
				return st.get_node_property_value(i, p)
	return null
