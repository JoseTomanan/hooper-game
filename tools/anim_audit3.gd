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
	# --- harness validity gate ------------------------------------------------
	var void_run := false
	for c in CONTROL:
		var mx := 0.0
		for b in BLENDS:
			mx = maxf(mx, _rows[b][c])
		if mx < 0.05:
			void_run = true
	print("  %-24s %10s %10s %10s   %s" % ["bone", "blend=0", "blend=3", "blend=6", "note"])
	for b in SUSPECT:
		print("  %-24s %10.2f %10.2f %10.2f   %s" % [b, _rows[0.0][b], _rows[3.0][b], _rows[6.0][b], "SUSPECT"])
	for b in CONTROL:
		print("  %-24s %10.2f %10.2f %10.2f   %s" % [b, _rows[0.0][b], _rows[3.0][b], _rows[6.0][b], "control"])
	print("")
	if void_run:
		print("  RESULT: VOID — a control bone never left rest, so the harness is not")
		print("          applying poses. No conclusion can be drawn about the suspects.")
		quit(1)
		return
	print("  controls moved, so the harness is applying poses. Verdicts:")
	for b in SUSPECT:
		var d0: float = _rows[0.0][b]
		var d6: float = _rows[6.0][b]
		var v := ""
		if d6 < 0.05 and d0 > 1.0:
			v = "COLLAPSES TO REST at full run (idle poses it, run does not)"
		elif d6 < 0.05 and d0 < 0.05:
			v = "at rest at BOTH ends"
		else:
			v = "posed at both ends — OK"
		print("    %-24s %s" % [b, v])

	print("")
	print("=== Per-state pose (deg from rest) ===")
	print("  %-14s %12s %10s %10s" % ["state", "RightUpLeg", "Spine1", "LeftArm"])
	for s in _phase_rows:
		var r: Dictionary = _phase_rows[s]
		print("  %-14s %12.2f %10.2f %10.2f" % [s, r["RightUpLeg"], r["Spine1"], r["LeftArm"]])
	print("")
	quit(0)


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
