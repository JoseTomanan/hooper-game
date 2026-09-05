extends SceneTree
# One-shot asset build tool (#297). Completes run's five material ROTATION_3D
# bindings with idle's already-retargeted rest-frame values. Spine2's offset is
# constant; Neck/hand tracks are preserved for coverage even though their
# sub-degree motion is not a visible defect. The
# source run take supplies the binding set, but its corresponding values are
# identity/rest, so retaining its immutable tracks alone does NOT fix the live
# BlendSpace collapse. This is deliberately a splice, never a wholesale
# re-extract: locomotion.res carries post-import hemisphere/loop corrections
# which a fresh extraction would overwrite.

const LIB_PATH := "res://assets/locomotion.res"
const RUN_FBX := "res://assets/run.fbx"
const RIG_FBX := "res://assets/Y Bot.fbx"
const EXPECTED_TAKE := &"Root|Run"
const EXPECTED_RECOVERED := [
	"mixamorig_LeftHand",
	"mixamorig_Neck",
	"mixamorig_RightHand",
	"mixamorig_Spine1",
	"mixamorig_Spine2",
]


func bone_of(path: NodePath) -> String:
	return "" if path.get_subname_count() == 0 else String(path.get_subname(0))


func is_finger_joint(bone: String) -> bool:
	for side in ["mixamorig_LeftHand", "mixamorig_RightHand"]:
		for digit in ["Thumb", "Index", "Middle", "Ring", "Pinky"]:
			if bone.begins_with(side + digit):
				return true
	return false


func sorted_strings(values: Array) -> Array:
	values.sort()
	return values


func _initialize() -> void:
	var library = load(LIB_PATH) as AnimationLibrary
	if library == null or not library.has_animation(&"run"):
		push_error("[recover-run] missing locomotion/res run animation.")
		quit(1)
		return
	var run := library.get_animation(&"run")
	if not library.has_animation(&"idle"):
		push_error("[recover-run] missing locomotion/res idle animation.")
		quit(1)
		return
	var idle := library.get_animation(&"idle")
	var rig := _find(load(RIG_FBX).instantiate(), "Skeleton3D") as Skeleton3D
	if rig == null:
		push_error("[recover-run] no Skeleton3D in %s." % RIG_FBX)
		quit(1)
		return

	var prefix := ""
	var existing := {}
	for i in run.get_track_count():
		if run.track_get_type(i) != Animation.TYPE_ROTATION_3D:
			continue
		var path := run.track_get_path(i)
		var bone := bone_of(path)
		if bone == "" or existing.has(bone):
			push_error("[recover-run] run has an invalid or duplicate rotation binding: %s." % path)
			quit(1)
			return
		existing[bone] = i
		if prefix == "":
			prefix = String(path).get_slice(":", 0)
	if prefix != "Skeleton3D":
		push_error("[recover-run] expected runtime Skeleton3D binding prefix, got '%s'." % prefix)
		quit(1)
		return

	var player := _find(load(RUN_FBX).instantiate(), "AnimationPlayer") as AnimationPlayer
	if player == null:
		push_error("[recover-run] no AnimationPlayer under %s." % RUN_FBX)
		quit(1)
		return
	var source: Animation = null
	var matches := []
	var available_takes := []
	for library_name in player.get_animation_library_list():
		var source_library := player.get_animation_library(library_name)
		for take_name in source_library.get_animation_list():
			available_takes.append(String(take_name))
		if source_library.has_animation(EXPECTED_TAKE):
			matches.append(source_library.get_animation(EXPECTED_TAKE))
	if matches.size() != 1:
		push_error("[recover-run] expected exactly one imported '%s' take, found %d; available: %s." % [EXPECTED_TAKE, matches.size(), available_takes])
		quit(1)
		return
	source = matches[0]

	var source_material := {}
	for i in source.get_track_count():
		if source.track_get_type(i) != Animation.TYPE_ROTATION_3D:
			continue
		var source_path := source.track_get_path(i)
		var source_bone := bone_of(source_path)
		if source_bone == "" or rig.find_bone(source_bone) < 0:
			continue
		if rig.get_bone_children(rig.find_bone(source_bone)).is_empty() or is_finger_joint(source_bone):
			continue
		if source_material.has(source_bone):
			push_error("[recover-run] source has duplicate material rotation binding for '%s'." % source_bone)
			quit(1)
			return
		if source.track_get_key_count(i) <= 0:
			push_error("[recover-run] source rotation track '%s' has no keys." % source_bone)
			quit(1)
			return
		source_material[source_bone] = i

	for bone in EXPECTED_RECOVERED:
		if not source_material.has(bone):
			push_error("[recover-run] imported run take lacks expected material track '%s'; check remove_immutable_tracks." % bone)
			quit(1)
			return
	for bone in source_material:
		if not existing.has(bone) and not EXPECTED_RECOVERED.has(bone):
			push_error("[recover-run] unexpected missing material rotation track '%s'." % bone)
			quit(1)
			return

	var old_track_count := run.get_track_count()
	var old_loop_mode := run.loop_mode
	if idle.length <= 0.0:
		push_error("[recover-run] idle animation has invalid length %f." % idle.length)
		quit(1)
		return
	var added := 0
	for bone in EXPECTED_RECOVERED:
		var idle_track := _rotation_track(idle, bone)
		if idle_track < 0 or idle.track_get_key_count(idle_track) <= 0:
			push_error("[recover-run] idle lacks a keyed rotation track for '%s'." % bone)
			quit(1)
			return
		var existing_track := _rotation_track(run, bone)
		if existing_track >= 0:
			run.remove_track(existing_track)
		else:
			added += 1
		var target_track := run.add_track(Animation.TYPE_ROTATION_3D)
		run.track_set_path(target_track, NodePath("Skeleton3D:%s" % bone))
		# Keep the source animation's relative key timing while fitting run's
		# shorter loop. This preserves Spine2's constant rest-frame offset and
		# retains the tiny Neck/hand motions instead of fabricating a new pose.
		for key in idle.track_get_key_count(idle_track):
			run.rotation_track_insert_key(
				target_track,
				run.length * idle.track_get_key_time(idle_track, key) / idle.length,
				idle.track_get_key_value(idle_track, key))

	if run.loop_mode != old_loop_mode or run.get_track_count() != old_track_count + added:
		push_error("[recover-run] splice changed loop mode or an unexpected number of tracks.")
		quit(1)
		return
	var save_error := ResourceSaver.save(library, LIB_PATH)
	if save_error != OK:
		push_error("[recover-run] failed to save %s: %d." % [LIB_PATH, save_error])
		quit(1)
		return
	print("[recover-run] normalized %d material rotation tracks from idle offsets: %s" % [EXPECTED_RECOVERED.size(), EXPECTED_RECOVERED])
	quit(0)


func _find(node: Node, expected_class: String) -> Node:
	if node.get_class() == expected_class:
		return node
	for child in node.get_children():
		var found := _find(child, expected_class)
		if found != null:
			return found
	return null


func _rotation_track(animation: Animation, bone: String) -> int:
	for i in animation.get_track_count():
		if animation.track_get_type(i) != Animation.TYPE_ROTATION_3D:
			continue
		var path := animation.track_get_path(i)
		if bone_of(path) == bone:
			return i
	return -1
