extends SceneTree

# Slices #296's three separately-authored generic fallback poses into the
# runtime library. Each is a short, self-contained six-tick clip: generic
# phases have incompatible move windows, so LOOP_NONE must hold a terminal
# load/commit/settle pose rather than loop an arbitrary fraction of a stride.

const TPS := 60.0
const TICKS := 6
const LENGTH := TICKS / TPS
const LIB_PATH := "res://assets/locomotion.res"
const ARMATURE_PREFIX := "Armature/"
const SPECS := [
	["res://assets/genericstartup_authored.fbx", &"genericstartup"],
	["res://assets/genericactive_authored.fbx", &"genericactive"],
	["res://assets/genericrecovery_authored.fbx", &"genericrecovery"],
]


func _initialize() -> void:
	var library := load(LIB_PATH) as AnimationLibrary
	if library == null:
		push_error("[generic-fallback] cannot load %s" % LIB_PATH)
		quit(1)
		return

	for spec in SPECS:
		var source := _source(spec[0], spec[1])
		if source == null:
			quit(1)
			return
		var rebuilt := _resample(source)
		if not _assert_contract(rebuilt, spec[1]):
			quit(1)
			return
		if library.has_animation(spec[1]):
			library.remove_animation(spec[1])
		library.add_animation(spec[1], rebuilt)

	var names := {}
	for spec in SPECS:
		names[spec[1]] = true
	if names.size() != SPECS.size():
		push_error("[generic-fallback] duplicate output clip name; refusing to collapse phases.")
		quit(1)
		return

	var err := ResourceSaver.save(library, LIB_PATH)
	if err != OK:
		push_error("[generic-fallback] save failed: %d" % err)
		quit(1)
		return
	print("[generic-fallback] saved three distinct six-tick LOOP_NONE fallback clips.")
	quit(0)


func _source(path: String, clip_name: StringName) -> Animation:
	var packed := load(path) as PackedScene
	if packed == null:
		push_error("[generic-fallback] cannot load %s" % path)
		return null
	var player := _find_animation_player(packed.instantiate())
	if player == null or not player.has_animation(clip_name):
		push_error("[generic-fallback] %s has no '%s' take." % [path, clip_name])
		return null
	var source := player.get_animation(clip_name)
	if not is_equal_approx(source.length, LENGTH):
		push_error("[generic-fallback] %s length %.9f, expected %.9f (six 60-Hz ticks)." %
			[path, source.length, LENGTH])
		return null
	return source


func _resample(source: Animation) -> Animation:
	var output := Animation.new()
	output.length = LENGTH
	output.loop_mode = Animation.LOOP_NONE
	for i in source.get_track_count():
		var kind := source.track_get_type(i)
		if kind != Animation.TYPE_ROTATION_3D and kind != Animation.TYPE_POSITION_3D:
			continue
		var path := _rebase_path(source.track_get_path(i))
		if path.get_subname_count() != 1:
			continue
		var track := output.add_track(kind)
		output.track_set_path(track, path)
		for frame in TICKS + 1:
			var time := float(frame) / TPS
			if kind == Animation.TYPE_ROTATION_3D:
				output.rotation_track_insert_key(track, time, source.rotation_track_interpolate(i, time))
			else:
				output.position_track_insert_key(track, time, source.position_track_interpolate(i, time))
	return output


func _assert_contract(clip: Animation, clip_name: StringName) -> bool:
	if clip.loop_mode != Animation.LOOP_NONE or not is_equal_approx(clip.length, LENGTH):
		push_error("[generic-fallback] %s is not a six-tick LOOP_NONE clip." % clip_name)
		return false
	var rotations := 0
	var bones := {}
	for i in clip.get_track_count():
		if clip.track_get_type(i) != Animation.TYPE_ROTATION_3D:
			continue
		rotations += 1
		var path := clip.track_get_path(i)
		if path.get_subname_count() != 1 or not String(path).begins_with("Skeleton3D:"):
			push_error("[generic-fallback] %s contains an unbindable rotation path %s." % [clip_name, path])
			return false
		bones[String(path.get_subname(0))] = true
		if clip.track_get_key_count(i) != TICKS + 1:
			push_error("[generic-fallback] %s track %d has no terminal hold key." % [clip_name, i])
			return false
	if rotations < 52 or bones.size() < 52:
		push_error("[generic-fallback] %s covers only %d rotation tracks / %d bones; full body was lost." %
			[clip_name, rotations, bones.size()])
		return false
	return true


func _rebase_path(path: NodePath) -> NodePath:
	var value := String(path)
	if value.begins_with(ARMATURE_PREFIX):
		value = value.substr(ARMATURE_PREFIX.length())
	var bone := "" if path.get_subname_count() == 0 else String(path.get_subname(0))
	if bone.begins_with("mixamorig:"):
		value = value.replace(":" + bone, ":mixamorig_" + bone.substr(len("mixamorig:")))
	return NodePath(value)


func _find_animation_player(node: Node) -> AnimationPlayer:
	if node is AnimationPlayer:
		return node
	for child in node.get_children():
		var found := _find_animation_player(child)
		if found != null:
			return found
	return null
