## anim_audit.gd
## Headless audit of EVERY AnimationTree state in Player.tscn.
##
## Reads the state machine straight out of the PackedScene's SceneState (so no
## C# instantiation is needed), resolves each state to its clip(s) in
## assets/locomotion.res, then reports, per clip:
##
##   • existence        — does the referenced clip resolve at all?
##   • length/loop      — nonzero length; loop mode (FBX import defaults to
##                        LOOP_NONE, which silently freezes a looping state)
##   • bone coverage    — which skeleton bones the clip does NOT track. An
##                        untracked bone falls back to the skeleton REST pose
##                        (not "hold last"), so a clip missing arm tracks
##                        renders a T-posed upper body.
##   • rest distance    — mean/max geodesic angle between the clip's t=0 bone
##                        rotations and the skeleton rest rotations. Kenney-
##                        retargeted clips sit near rest; stock-Mixamo clips sit
##                        155-180 deg away. Blending ACROSS those two families at
##                        partial weight interpolates through garbage.
##
## Then it cross-checks each BlendSpace1D's endpoints for a family mismatch.
##
## Run: godot --headless --path . --script res://tools/anim_audit.gd
extends SceneTree

const LIB_PATH := "res://assets/locomotion.res"
const RIG_PATH := "res://assets/Y Bot.fbx"
const PLAYER_PATH := "res://scenes/Player.tscn"

# A clip whose mean rest-distance exceeds this is treated as the "far" (stock
# Mixamo) rotation family; below it, the "near" (Kenney-retarget) family.
const FAMILY_SPLIT_DEG := 90.0

var _rest_rot := {}      # bone name -> Quaternion rest rotation
var _bone_names := []    # all skeleton bone names
var _clip_report := {}   # clip name -> dict of measurements
var _findings := []      # human-readable defect lines


func _init() -> void:
	print("")
	print("=== Animation audit: every AnimationTree state in Player.tscn ===")
	print("")

	var lib: AnimationLibrary = load(LIB_PATH)
	if lib == null:
		print("FATAL: could not load ", LIB_PATH)
		quit(1)
		return

	if not _load_rest_pose():
		quit(1)
		return

	var sm := _load_state_machine()
	if sm == null:
		print("FATAL: could not read AnimationTree.tree_root from ", PLAYER_PATH)
		quit(1)
		return

	# ---- map every state to the clip(s) it plays -------------------------
	var state_clips := {}      # state -> Array of {clip, weightable}
	var blend_pairs := []      # {state, clips:[...]}
	for state_name in sm.get_node_list():
		var node := sm.get_node(state_name)
		if node == null:
			continue  # Start/End are virtual
		if node is AnimationNodeAnimation:
			state_clips[state_name] = [str(node.animation)]
		elif node is AnimationNodeBlendSpace1D:
			var bs := node as AnimationNodeBlendSpace1D
			var clips := []
			for i in range(bs.get_blend_point_count()):
				var bn := bs.get_blend_point_node(i)
				if bn is AnimationNodeAnimation:
					clips.append(str(bn.animation))
			state_clips[state_name] = clips
			blend_pairs.append({"state": state_name, "clips": clips})
		else:
			state_clips[state_name] = []
			_findings.append("state '%s' uses unhandled node type %s" % [state_name, node.get_class()])

	# ---- measure every distinct clip -------------------------------------
	var distinct := {}
	for s in state_clips:
		for c in state_clips[s]:
			distinct[c] = true

	for clip_name in distinct:
		_measure_clip(lib, clip_name)

	# ---- report ----------------------------------------------------------
	_print_state_table(state_clips)
	_print_clip_table()
	_check_blend_families(blend_pairs)
	_check_loop_modes(state_clips)

	print("")
	print("=== FINDINGS (%d) ===" % _findings.size())
	for f in _findings:
		print("  * ", f)
	print("")
	quit(0)


# ---------------------------------------------------------------------------
# Rest pose
# ---------------------------------------------------------------------------
func _load_rest_pose() -> bool:
	var rig_scene: PackedScene = load(RIG_PATH)
	if rig_scene == null:
		print("FATAL: could not load ", RIG_PATH)
		return false
	var rig := rig_scene.instantiate()
	var skel := _find_skeleton(rig)
	if skel == null:
		print("FATAL: no Skeleton3D inside ", RIG_PATH)
		rig.free()
		return false
	for i in range(skel.get_bone_count()):
		var bname := skel.get_bone_name(i)
		_bone_names.append(bname)
		_rest_rot[bname] = skel.get_bone_rest(i).basis.get_rotation_quaternion()
	print("rig: %d bones from %s" % [_bone_names.size(), RIG_PATH])
	rig.free()
	return true


func _find_skeleton(n: Node) -> Skeleton3D:
	if n is Skeleton3D:
		return n
	for c in n.get_children():
		var r := _find_skeleton(c)
		if r != null:
			return r
	return null


# ---------------------------------------------------------------------------
# State machine, read without instantiating the C# scene
# ---------------------------------------------------------------------------
func _load_state_machine() -> AnimationNodeStateMachine:
	var ps: PackedScene = load(PLAYER_PATH)
	if ps == null:
		return null
	var st := ps.get_state()
	for i in range(st.get_node_count()):
		if str(st.get_node_name(i)) != "AnimationTree":
			continue
		for p in range(st.get_node_property_count(i)):
			if str(st.get_node_property_name(i, p)) == "tree_root":
				var v = st.get_node_property_value(i, p)
				if v is AnimationNodeStateMachine:
					return v
	return null


# ---------------------------------------------------------------------------
# Per-clip measurement
# ---------------------------------------------------------------------------
func _measure_clip(lib: AnimationLibrary, clip_name: String) -> void:
	# clip_name is library-qualified, e.g. "locomotion/idle"
	var short := clip_name.get_slice("/", 1) if clip_name.contains("/") else clip_name
	if not lib.has_animation(short):
		_clip_report[clip_name] = {"missing": true}
		_findings.append("MISSING CLIP: '%s' is referenced by the state machine but is not in %s" % [clip_name, LIB_PATH])
		return

	var anim: Animation = lib.get_animation(short)
	var tracked := {}
	var rot_track_count := 0
	var deg_sum := 0.0
	var deg_max := 0.0
	var deg_n := 0

	for t in range(anim.get_track_count()):
		var path := str(anim.track_get_path(t))
		var bone := path.get_slice(":", 1) if path.contains(":") else ""
		if bone == "":
			continue
		tracked[bone] = true
		if anim.track_get_type(t) != Animation.TYPE_ROTATION_3D:
			continue
		rot_track_count += 1
		if anim.track_get_key_count(t) == 0:
			continue
		if not _rest_rot.has(bone):
			continue
		var q0: Quaternion = anim.rotation_track_interpolate(t, 0.0)
		var rest: Quaternion = _rest_rot[bone]
		var d := rad_to_deg(_quat_angle(q0, rest))
		deg_sum += d
		deg_max = maxf(deg_max, d)
		deg_n += 1

	var untracked := []
	for b in _bone_names:
		if not tracked.has(b):
			untracked.append(b)

	_clip_report[clip_name] = {
		"missing": false,
		"length": anim.length,
		"loop": anim.loop_mode,
		"tracks": anim.get_track_count(),
		"rot_tracks": rot_track_count,
		"tracked_bones": tracked.size(),
		"untracked": untracked,
		"mean_deg": (deg_sum / float(deg_n)) if deg_n > 0 else -1.0,
		"max_deg": deg_max,
	}

	if anim.length <= 0.0:
		_findings.append("ZERO LENGTH: clip '%s' has length %s" % [clip_name, str(anim.length)])
	if untracked.size() > 0:
		_findings.append("UNTRACKED BONES: clip '%s' animates %d/%d bones; %d fall back to REST pose -> %s" % [
			clip_name, tracked.size(), _bone_names.size(), untracked.size(),
			str(untracked.slice(0, min(8, untracked.size())))])


func _quat_angle(a: Quaternion, b: Quaternion) -> float:
	var dot := absf(a.normalized().dot(b.normalized()))
	return 2.0 * acos(clampf(dot, -1.0, 1.0))


# ---------------------------------------------------------------------------
# Reports
# ---------------------------------------------------------------------------
func _print_state_table(state_clips: Dictionary) -> void:
	var names := state_clips.keys()
	names.sort()
	print("")
	print("--- STATE -> CLIP(S) (%d states) ---" % names.size())
	for s in names:
		var clips: Array = state_clips[s]
		print("  %-26s %s" % [s, str(clips)])


func _print_clip_table() -> void:
	var names := _clip_report.keys()
	names.sort()
	print("")
	print("--- CLIP MEASUREMENTS (%d distinct clips) ---" % names.size())
	print("  %-34s %8s %5s %6s %7s %9s %9s" % ["clip", "len", "loop", "tracks", "bones", "meanDeg", "maxDeg"])
	for c in names:
		var r: Dictionary = _clip_report[c]
		if r.get("missing", false):
			print("  %-34s   <<< NOT PRESENT IN LIBRARY >>>" % c)
			continue
		print("  %-34s %8.3f %5d %6d %7d %9.1f %9.1f" % [
			c, r["length"], r["loop"], r["tracks"], r["tracked_bones"],
			r["mean_deg"], r["max_deg"]])


func _check_blend_families(blend_pairs: Array) -> void:
	print("")
	print("--- BLENDSPACE FAMILY CHECK ---")
	for bp in blend_pairs:
		var fams := {}
		var detail := []
		for c in bp["clips"]:
			var r: Dictionary = _clip_report.get(c, {})
			if r.is_empty() or r.get("missing", false):
				continue
			var fam := "far" if r["mean_deg"] > FAMILY_SPLIT_DEG else "near"
			fams[fam] = true
			detail.append("%s=%.1fdeg(%s)" % [c, r["mean_deg"], fam])
		print("  %-26s %s" % [bp["state"], str(detail)])
		if fams.size() > 1:
			_findings.append("CROSS-FAMILY BLEND: state '%s' blends clips from two rotation families -> partial weights mangle the rig: %s" % [
				bp["state"], str(detail)])


func _check_loop_modes(state_clips: Dictionary) -> void:
	# States that are sustained stances and MUST loop.
	var looping_states := ["Locomotion", "Dribble"]
	print("")
	print("--- LOOP MODE CHECK (sustained stances must loop) ---")
	for s in looping_states:
		if not state_clips.has(s):
			continue
		for c in state_clips[s]:
			var r: Dictionary = _clip_report.get(c, {})
			if r.is_empty() or r.get("missing", false):
				continue
			var ok: bool = int(r["loop"]) != int(Animation.LOOP_NONE)
			print("  %-26s %-34s loop=%d %s" % [s, c, r["loop"], "OK" if ok else "<<< LOOP_NONE"])
			if not ok:
				_findings.append("NO LOOP: sustained state '%s' plays clip '%s' with LOOP_NONE -> freezes on last frame" % [s, c])
