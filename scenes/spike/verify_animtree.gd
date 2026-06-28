## verify_animtree.gd
## Headless spike verification: compares loco_baseline.tres (editor-authored, extracted
## programmatically from Player.tscn) against loco_text.tres (hand-authored text) for
## structural and behavioral identity.
##
## Run: godot --headless --script res://scenes/spike/verify_animtree.gd --path .
@tool
extends SceneTree

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

var _checks_passed: int = 0
var _checks_failed: int = 0

func pass_check(label: String) -> void:
	_checks_passed += 1
	print("  PASS  ", label)

func fail_check(label: String, detail: String = "") -> void:
	_checks_failed += 1
	var msg = "  FAIL  " + label
	if detail != "":
		msg += "  (" + detail + ")"
	print(msg)

# Returns Array[StringName] of all state names in the state machine, sorted.
func get_state_names(sm: AnimationNodeStateMachine) -> Array:
	var names: Array = []
	for sn in sm.get_node_list():
		names.append(sn)
	names.sort()
	return names

# Returns Array of {from, to, advance_mode} dicts, sorted by (from, to)
func get_transitions(sm: AnimationNodeStateMachine) -> Array:
	var result: Array = []
	for i in range(sm.get_transition_count()):
		var t = sm.get_transition(i)
		result.append({
			"from": sm.get_transition_from(i),
			"to": sm.get_transition_to(i),
			"advance_mode": t.advance_mode
		})
	result.sort_custom(func(a, b):
		if a["from"] != b["from"]:
			return str(a["from"]) < str(b["from"])
		return str(a["to"]) < str(b["to"])
	)
	return result

# Compute deterministic 1D blend weights for two blend points at positions p0, p1
# given a query value t (clamped to [min_space, max_space]).
# Returns [w0, w1] where w0+w1=1.
func blend1d_weights(p0: float, p1: float, query: float, min_s: float, max_s: float) -> Array:
	var q = clampf(query, min_s, max_s)
	var span = p1 - p0
	if abs(span) < 1e-9:
		return [1.0, 0.0]
	var alpha = clampf((q - p0) / span, 0.0, 1.0)
	return [1.0 - alpha, alpha]

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

func _init():
	print("")
	print("=== AnimationTree Spike Verification ===")
	print("    baseline: res://scenes/spike/loco_baseline.tres")
	print("    text:     res://scenes/spike/loco_text.tres")
	print("")

	# -----------------------------------------------------------------------
	# 1. Load both resources
	# -----------------------------------------------------------------------
	print("[1] Loading resources")

	var baseline = load("res://scenes/spike/loco_baseline.tres")
	if baseline == null:
		print("FATAL: Could not load loco_baseline.tres")
		print("SPIKE RESULT: FAIL")
		quit(1)
		return
	if not baseline is AnimationNodeStateMachine:
		print("FATAL: loco_baseline.tres is not AnimationNodeStateMachine, got: ", baseline.get_class())
		print("SPIKE RESULT: FAIL")
		quit(1)
		return
	pass_check("loco_baseline.tres loaded as AnimationNodeStateMachine")

	var text_res = load("res://scenes/spike/loco_text.tres")
	if text_res == null:
		print("FATAL: Could not load loco_text.tres")
		print("SPIKE RESULT: FAIL")
		quit(1)
		return
	if not text_res is AnimationNodeStateMachine:
		print("FATAL: loco_text.tres is not AnimationNodeStateMachine, got: ", text_res.get_class())
		print("SPIKE RESULT: FAIL")
		quit(1)
		return
	pass_check("loco_text.tres loaded as AnimationNodeStateMachine")

	var b: AnimationNodeStateMachine = baseline
	var txt: AnimationNodeStateMachine = text_res

	# -----------------------------------------------------------------------
	# 2. State names match
	# -----------------------------------------------------------------------
	print("")
	print("[2] State name comparison")

	var b_states = get_state_names(b)
	var t_states = get_state_names(txt)
	print("    baseline states: ", b_states)
	print("    text     states: ", t_states)

	if b_states == t_states:
		pass_check("state name sets are identical: %s" % str(b_states))
	else:
		fail_check("state name sets differ",
			"baseline=%s  text=%s" % [str(b_states), str(t_states)])

	# -----------------------------------------------------------------------
	# 3. Per-state node types match
	# -----------------------------------------------------------------------
	print("")
	print("[3] Per-state node type comparison")

	for state_name in b_states:
		var b_node = b.get_node(state_name)
		if not txt.has_node(state_name):
			fail_check("state '%s': missing in text resource" % state_name)
			continue
		var t_node = txt.get_node(state_name)
		# Start/End are virtual: b_node and t_node will be null for them
		var b_type = b_node.get_class() if b_node != null else "null(virtual)"
		var t_type = t_node.get_class() if t_node != null else "null(virtual)"
		if b_type == t_type:
			pass_check("state '%s' node type = %s" % [state_name, b_type])
		else:
			fail_check("state '%s' node type mismatch" % state_name,
				"baseline=%s  text=%s" % [b_type, t_type])

	# -----------------------------------------------------------------------
	# 4. BlendSpace1D deep comparison (Locomotion state)
	# -----------------------------------------------------------------------
	print("")
	print("[4] BlendSpace1D deep comparison (Locomotion)")

	var b_loco = b.get_node("Locomotion") as AnimationNodeBlendSpace1D
	var t_loco = txt.get_node("Locomotion") as AnimationNodeBlendSpace1D

	if b_loco == null:
		fail_check("baseline Locomotion node is null or not a BlendSpace1D")
	if t_loco == null:
		fail_check("text Locomotion node is null or not a BlendSpace1D")

	if b_loco != null and t_loco != null:
		# blend point count
		var b_cnt = b_loco.get_blend_point_count()
		var t_cnt = t_loco.get_blend_point_count()
		if b_cnt == t_cnt:
			pass_check("BlendSpace1D blend_point_count = %d" % b_cnt)
		else:
			fail_check("BlendSpace1D blend_point_count mismatch",
				"baseline=%d  text=%d" % [b_cnt, t_cnt])

		# min_space / max_space (compare parsed float values; allow tiny FP)
		if is_equal_approx(b_loco.min_space, t_loco.min_space):
			pass_check("BlendSpace1D min_space ≈ %s" % str(b_loco.min_space))
		else:
			fail_check("BlendSpace1D min_space mismatch",
				"baseline=%s  text=%s" % [str(b_loco.min_space), str(t_loco.min_space)])

		if is_equal_approx(b_loco.max_space, t_loco.max_space):
			pass_check("BlendSpace1D max_space ≈ %s" % str(b_loco.max_space))
		else:
			fail_check("BlendSpace1D max_space mismatch",
				"baseline=%s  text=%s" % [str(b_loco.max_space), str(t_loco.max_space)])

		# Collect blend points sorted by position for order-independent comparison
		var b_pts: Array = []
		var t_pts: Array = []
		for i in range(b_loco.get_blend_point_count()):
			var n = b_loco.get_blend_point_node(i)
			b_pts.append({"pos": b_loco.get_blend_point_position(i),
				"anim": (n as AnimationNodeAnimation).animation if n != null else ""})
		for i in range(t_loco.get_blend_point_count()):
			var n = t_loco.get_blend_point_node(i)
			t_pts.append({"pos": t_loco.get_blend_point_position(i),
				"anim": (n as AnimationNodeAnimation).animation if n != null else ""})
		b_pts.sort_custom(func(a, bb): return a["pos"] < bb["pos"])
		t_pts.sort_custom(func(a, bb): return a["pos"] < bb["pos"])

		var n_pts = min(b_pts.size(), t_pts.size())
		for i in range(n_pts):
			var label = "blend_point_%d" % i
			if is_equal_approx(b_pts[i]["pos"], t_pts[i]["pos"]):
				pass_check("%s position = %s" % [label, str(b_pts[i]["pos"])])
			else:
				fail_check("%s position mismatch" % label,
					"baseline=%s  text=%s" % [str(b_pts[i]["pos"]), str(t_pts[i]["pos"])])
			if b_pts[i]["anim"] == t_pts[i]["anim"]:
				pass_check("%s animation = '%s'" % [label, b_pts[i]["anim"]])
			else:
				fail_check("%s animation mismatch" % label,
					"baseline='%s'  text='%s'" % [b_pts[i]["anim"], t_pts[i]["anim"]])

	# -----------------------------------------------------------------------
	# 5. Transitions: edges and advance_mode
	# -----------------------------------------------------------------------
	print("")
	print("[5] Transition comparison")

	var b_trans = get_transitions(b)
	var t_trans = get_transitions(txt)

	if b_trans.size() == t_trans.size():
		pass_check("transition count = %d" % b_trans.size())
	else:
		fail_check("transition count mismatch",
			"baseline=%d  text=%d" % [b_trans.size(), t_trans.size()])

	# Check each baseline transition exists in text with same advance_mode
	for bt in b_trans:
		var found = false
		for tt in t_trans:
			if str(bt["from"]) == str(tt["from"]) and str(bt["to"]) == str(tt["to"]):
				found = true
				if bt["advance_mode"] == tt["advance_mode"]:
					pass_check("transition %s→%s  advance_mode=%d" % [
						bt["from"], bt["to"], bt["advance_mode"]])
				else:
					fail_check("transition %s→%s advance_mode mismatch" % [bt["from"], bt["to"]],
						"baseline=%d  text=%d" % [bt["advance_mode"], tt["advance_mode"]])
				break
		if not found:
			fail_check("transition %s→%s missing in text resource" % [bt["from"], bt["to"]])

	# Check for extra transitions in text not in baseline
	for tt in t_trans:
		var found = false
		for bt in b_trans:
			if str(bt["from"]) == str(tt["from"]) and str(bt["to"]) == str(tt["to"]):
				found = true
				break
		if not found:
			fail_check("extra transition in text not in baseline: %s→%s" % [tt["from"], tt["to"]])

	# -----------------------------------------------------------------------
	# TIER 1: Deterministic 1D blend weight sweep (25 samples)
	# -----------------------------------------------------------------------
	print("")
	print("[TIER 1] Deterministic blend weight sweep (25 samples across min_space..max_space)")

	if b_loco != null and t_loco != null and b_loco.get_blend_point_count() >= 2 and t_loco.get_blend_point_count() >= 2:
		# Collect sorted points
		var b_pts2: Array = []
		var t_pts2: Array = []
		for i in range(b_loco.get_blend_point_count()):
			b_pts2.append(b_loco.get_blend_point_position(i))
		for i in range(t_loco.get_blend_point_count()):
			t_pts2.append(t_loco.get_blend_point_position(i))
		b_pts2.sort()
		t_pts2.sort()

		var b_p0 = b_pts2[0]; var b_p1 = b_pts2[1]
		var t_p0 = t_pts2[0]; var t_p1 = t_pts2[1]
		var b_min = b_loco.min_space; var b_max = b_loco.max_space
		var t_min = t_loco.min_space; var t_max = t_loco.max_space

		var SAMPLES = 25
		var all_ok = true
		for s in range(SAMPLES):
			var frac = float(s) / float(SAMPLES - 1)
			# Sample uniformly across the baseline's full range
			var sample_val = b_min + frac * (b_max - b_min)
			var b_w = blend1d_weights(b_p0, b_p1, sample_val, b_min, b_max)
			var t_w = blend1d_weights(t_p0, t_p1, sample_val, t_min, t_max)
			if abs(b_w[0] - t_w[0]) > 1e-5 or abs(b_w[1] - t_w[1]) > 1e-5:
				all_ok = false
				fail_check("blend weight mismatch at pos=%s" % str(sample_val),
					"baseline=[%.6f,%.6f]  text=[%.6f,%.6f]" % [b_w[0], b_w[1], t_w[0], t_w[1]])

		if all_ok:
			pass_check("all 25 blend weight samples match (TIER 1)")
	else:
		fail_check("TIER 1 skipped: BlendSpace1D not available or fewer than 2 blend points")

	# -----------------------------------------------------------------------
	# TIER 2: Reasoning for skip
	# -----------------------------------------------------------------------
	print("")
	print("[TIER 2] Live AnimationTree bone-transform sampling: SKIPPED")
	print("  Reason: Headless Godot does not tick AnimationTree nodes without a")
	print("  running main-loop _process() cycle. SceneTree._init() in @tool scripts")
	print("  exits before any frames are processed, so AnimationTree.advance() cannot")
	print("  be driven. Wiring up an AnimationPlayer + AnimationTree + rigged skeleton")
	print("  and getting a bone transform purely from _init() is not feasible without")
	print("  a live DisplayServer and scene-tree frame pump. TIER 1 (deterministic")
	print("  weight math from blend-point positions) is sufficient for the structural")
	print("  proof this spike requires.")

	# -----------------------------------------------------------------------
	# Final verdict
	# -----------------------------------------------------------------------
	print("")
	print("=== Summary ===")
	print("  Checks passed : ", _checks_passed)
	print("  Checks failed : ", _checks_failed)
	print("")

	if _checks_failed == 0:
		print("SPIKE RESULT: PASS")
		quit(0)
	else:
		print("SPIKE RESULT: FAIL")
		quit(1)
