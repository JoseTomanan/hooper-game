## anim_audit2.gd — follow-up detail pass for anim_audit.gd
## Dumps, for the sparse clips, the FULL tracked/untracked bone split (so leaf-tip
## noise can be separated from load-bearing torso/arm gaps), and dumps every
## state-machine transition's xfade_time (a nonzero xfade between clips from two
## different rotation families is a partial-weight blend, which mangles the rig).
##
## Run: godot --headless --path . --script res://tools/anim_audit2.gd
extends SceneTree

const LIB_PATH := "res://assets/locomotion.res"
const RIG_PATH := "res://assets/Y Bot.fbx"
const PLAYER_PATH := "res://scenes/Player.tscn"

# Bones whose rest-fallback is cosmetically invisible: finger/toe tips and head top.
func _is_leaf_tip(b: String) -> bool:
	return b.ends_with("4") or b.ends_with("_End")

var _bone_names := []
var _rest_rot := {}


func _init() -> void:
	var lib: AnimationLibrary = load(LIB_PATH)
	var rig := (load(RIG_PATH) as PackedScene).instantiate()
	var skel := _find_skel(rig)
	for i in range(skel.get_bone_count()):
		_bone_names.append(skel.get_bone_name(i))
		_rest_rot[skel.get_bone_name(i)] = skel.get_bone_rest(i).basis.get_rotation_quaternion()
	rig.free()

	print("")
	print("=== DETAIL: full bone split per clip ===")
	for short in ["run", "idle", "pivot", "dribbleidle", "catch", "jumpshotstartup"]:
		if not lib.has_animation(short):
			continue
		var anim: Animation = lib.get_animation(short)
		var tracked := {}
		for t in range(anim.get_track_count()):
			var p := str(anim.track_get_path(t))
			if p.contains(":"):
				tracked[p.get_slice(":", 1)] = true
		var miss_real := []
		var miss_leaf := []
		for b in _bone_names:
			if tracked.has(b):
				continue
			if _is_leaf_tip(b):
				miss_leaf.append(b)
			else:
				miss_real.append(b)
		print("")
		print("  clip 'locomotion/%s'  len=%.3f loop=%d  tracked=%d/%d" % [
			short, anim.length, anim.loop_mode, tracked.size(), _bone_names.size()])
		print("    MISSING (load-bearing, %d): %s" % [miss_real.size(), str(miss_real)])
		print("    MISSING (leaf tips, %d): %s" % [miss_leaf.size(), str(miss_leaf)])
		# per-bone rest distance, worst offenders
		var worst := []
		for t in range(anim.get_track_count()):
			if anim.track_get_type(t) != Animation.TYPE_ROTATION_3D:
				continue
			var p := str(anim.track_get_path(t))
			var bone := p.get_slice(":", 1) if p.contains(":") else ""
			if bone == "" or not _rest_rot.has(bone) or anim.track_get_key_count(t) == 0:
				continue
			var q: Quaternion = anim.rotation_track_interpolate(t, 0.0)
			var d := rad_to_deg(2.0 * acos(clampf(absf(q.normalized().dot(_rest_rot[bone])), -1.0, 1.0)))
			worst.append({"b": bone, "d": d})
		worst.sort_custom(func(a, b): return a["d"] > b["d"])
		var top := []
		for i in range(min(6, worst.size())):
			top.append("%s=%.0f" % [worst[i]["b"], worst[i]["d"]])
		print("    worst rest-distance: %s" % str(top))

	# ---- transitions -----------------------------------------------------
	var sm := _load_sm()
	print("")
	print("=== TRANSITIONS: xfade_time / advance_mode ===")
	print("  (xfade>0 between different rotation families = partial-weight blend)")
	var nonzero := 0
	for i in range(sm.get_transition_count()):
		var tr := sm.get_transition(i)
		var f := str(sm.get_transition_from(i))
		var to := str(sm.get_transition_to(i))
		if tr.xfade_time > 0.0:
			nonzero += 1
			print("  %-24s -> %-24s xfade=%.3f advance=%d" % [f, to, tr.xfade_time, tr.advance_mode])
	print("  transitions with xfade>0: %d / %d" % [nonzero, sm.get_transition_count()])

	# ---- reachability: can every state be reached from Start? -------------
	print("")
	print("=== REACHABILITY from Start ===")
	var adj := {}
	for i in range(sm.get_transition_count()):
		var f := str(sm.get_transition_from(i))
		var to := str(sm.get_transition_to(i))
		if not adj.has(f):
			adj[f] = []
		adj[f].append(to)
	var seen := {"Start": true}
	var queue := ["Start"]
	while not queue.is_empty():
		var cur = queue.pop_front()
		for nxt in adj.get(cur, []):
			if not seen.has(nxt):
				seen[nxt] = true
				queue.append(nxt)
	var unreachable := []
	for s in sm.get_node_list():
		if not seen.has(str(s)):
			unreachable.append(str(s))
	print("  reachable: %d / %d states" % [seen.size(), sm.get_node_list().size()])
	print("  UNREACHABLE: %s" % str(unreachable))

	# ---- dead ends: states with no outgoing transition --------------------
	var dead := []
	for s in sm.get_node_list():
		var name := str(s)
		if name == "End":
			continue
		if not adj.has(name) or adj[name].is_empty():
			dead.append(name)
	print("  DEAD-END (no outgoing transition): %s" % str(dead))
	print("")
	quit(0)


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
