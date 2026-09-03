extends SceneTree
# Asset rebuild tool (#295) — retimes the one-shot rebound-grab display clip
# into assets/locomotion.res.
#
# Run: godot --headless --path . -s tools/rebuild_rebound_grab_clip.gd
#
# The live latch is intentionally the authority for this asset's length:
#
#   PlayerController.ReboundGrabDisplayTicks / Engine.PhysicsTicksPerSecond
#
# `ReboundGrabDisplayTicks` is an exported C# default rather than a GDScript
# constant, so this tool reads it from PlayerController.cs. That makes a future
# default retune fail closed if its declaration changes shape, instead of quietly
# leaving the visual one-shot at a stale duration. Engine's live physics rate
# supplies the denominator (currently 60 Hz from project.godot). The rebuilt
# clip gets one pose per physics tick plus its terminal pose, so its clock and
# the cosmetic latch start/end on the same ticks.
#
# The source FBX is pristine and the existing resource is replaced by name, so
# repeated runs are idempotent. The source is stock Mixamo on the same skeleton
# as Y Bot; every source bone path is rebased to Player.tscn's Skeleton3D layout.

const LIB_PATH := "res://assets/locomotion.res"
const SRC_FBX := "res://assets/Goalkeeper Catch Stationary.fbx"
const SRC_CLIP := &"mixamo_com"
const OUTPUT_NAME := &"catch"
const PLAYER_CONTROLLER_PATH := "res://scripts/Player/PlayerController.cs"
const ARMATURE_PREFIX := "Armature/"


func _initialize() -> void:
	var display_ticks := _read_display_ticks()
	if display_ticks <= 0:
		quit(1)
		return

	# This is the same runtime Engine value PlayerController's physics callback
	# advances against, not a second 60-Hz constant in an asset tool.
	var physics_ticks_per_second: int = Engine.physics_ticks_per_second
	if physics_ticks_per_second <= 0:
		push_error("[rebuild-rebound-grab] Engine.physics_ticks_per_second must be positive; got %d."
			% physics_ticks_per_second)
		quit(1)
		return
	var target_length_s := float(display_ticks) / float(physics_ticks_per_second)
	print("[rebuild-rebound-grab] target = ReboundGrabDisplayTicks (%d) / Engine.PhysicsTicksPerSecond (%d) = %.9f s"
		% [display_ticks, physics_ticks_per_second, target_length_s])

	var lib := load(LIB_PATH) as AnimationLibrary
	if lib == null:
		push_error("[rebuild-rebound-grab] failed to load AnimationLibrary at %s." % LIB_PATH)
		quit(1)
		return

	var packed := load(SRC_FBX) as PackedScene
	if packed == null:
		push_error("[rebuild-rebound-grab] failed to load source FBX %s." % SRC_FBX)
		quit(1)
		return
	var ap := _find_animation_player(packed.instantiate())
	if ap == null or not ap.has_animation(SRC_CLIP):
		push_error("[rebuild-rebound-grab] source FBX lacks AnimationPlayer clip '%s'." % SRC_CLIP)
		quit(1)
		return
	var source := ap.get_animation(SRC_CLIP)
	if source.length <= 0.0 or source.get_track_count() <= 0:
		push_error("[rebuild-rebound-grab] source '%s' is empty (length=%.9f tracks=%d)."
			% [SRC_CLIP, source.length, source.get_track_count()])
		quit(1)
		return

	# The source clip contains the complete secure motion. Resampling its whole
	# authored interval, rather than truncating its tail, preserves the authored
	# return pose at the instant the runtime latch releases.
	var rebuilt := _resample(source, display_ticks, physics_ticks_per_second)
	if not _assert_contract(rebuilt, source, display_ticks, target_length_s):
		quit(1)
		return

	if lib.has_animation(OUTPUT_NAME):
		lib.remove_animation(OUTPUT_NAME)
	lib.add_animation(OUTPUT_NAME, rebuilt)
	var err := ResourceSaver.save(lib, LIB_PATH)
	if err != OK:
		push_error("[rebuild-rebound-grab] ResourceSaver.save failed with error %d." % err)
		quit(1)
		return

	# Reload the serialized result: construction-time checks cannot catch a
	# write/read regression in the generated .res.
	var saved_lib := load(LIB_PATH) as AnimationLibrary
	var saved := null if saved_lib == null else saved_lib.get_animation(OUTPUT_NAME)
	if saved == null or not _assert_contract(saved, source, display_ticks, target_length_s):
		push_error("[rebuild-rebound-grab] serialized '%s' did not reload with the requested contract." % OUTPUT_NAME)
		quit(1)
		return
	print("[rebuild-rebound-grab] saved and reload-verified %s/%s." % [LIB_PATH, OUTPUT_NAME])
	quit(0)


func _read_display_ticks() -> int:
	var file := FileAccess.open(PLAYER_CONTROLLER_PATH, FileAccess.READ)
	if file == null:
		push_error("[rebuild-rebound-grab] cannot read %s; ReboundGrabDisplayTicks is the duration authority."
			% PLAYER_CONTROLLER_PATH)
		return -1
	var source := file.get_as_text()
	var declaration := RegEx.new()
	# Match the exported property's initializer specifically. A broad `= 30`
	# search would make this regeneration silently bind to an unrelated setting.
	var compile_error := declaration.compile("(?m)^[\\t ]*\\[Export\\][\\t ]+public[\\t ]+int[\\t ]+ReboundGrabDisplayTicks[\\t ]*\\{[^\\n]*\\}[\\t ]*=[\\t ]*([0-9]+)[\\t ]*;")
	if compile_error != OK:
		push_error("[rebuild-rebound-grab] internal ReboundGrabDisplayTicks declaration regex failed to compile.")
		return -1
	var match := declaration.search(source)
	if match == null:
		push_error("[rebuild-rebound-grab] %s no longer contains the expected exported ReboundGrabDisplayTicks default. Refusing to guess its value."
			% PLAYER_CONTROLLER_PATH)
		return -1
	var ticks := match.get_string(1).to_int()
	if ticks <= 0:
		push_error("[rebuild-rebound-grab] ReboundGrabDisplayTicks must be positive; parsed %d." % ticks)
		return -1
	return ticks


func _find_animation_player(node: Node) -> AnimationPlayer:
	if node is AnimationPlayer:
		return node as AnimationPlayer
	for child in node.get_children():
		var found := _find_animation_player(child)
		if found != null:
			return found
	return null


func _rebase_path(path: NodePath) -> NodePath:
	var value := String(path)
	if value.begins_with(ARMATURE_PREFIX):
		value = value.substr(ARMATURE_PREFIX.length())
	var bone := _bone_of(NodePath(value))
	if bone.begins_with("mixamorig:"):
		value = value.replace(":" + bone, ":mixamorig_" + bone.substr("mixamorig:".length()))
	return NodePath(value)


func _bone_of(path: NodePath) -> String:
	return "" if path.get_subname_count() == 0 else String(path.get_subname(0))


func _resample(source: Animation, ticks: int, physics_ticks_per_second: int) -> Animation:
	var output := Animation.new()
	output.loop_mode = Animation.LOOP_NONE
	output.length = float(ticks) / float(physics_ticks_per_second)
	for i in source.get_track_count():
		var type := source.track_get_type(i)
		if type != Animation.TYPE_ROTATION_3D and type != Animation.TYPE_POSITION_3D:
			# The source has no material scale tracks. Leaving them out is
			# deliberate: pose-scale tracks would overwrite PlayerRigScaler.
			continue
		var path := source.track_get_path(i)
		if _bone_of(path).is_empty():
			# The Player skeleton has no Armature wrapper/node track target.
			continue
		var track := output.add_track(type)
		output.track_set_path(track, _rebase_path(path))
		for key in ticks + 1:
			var normalized := float(key) / float(ticks)
			var source_time := source.length * normalized
			var output_time := float(key) / float(physics_ticks_per_second)
			if type == Animation.TYPE_ROTATION_3D:
				output.rotation_track_insert_key(track, output_time,
					source.rotation_track_interpolate(i, source_time))
			else:
				output.position_track_insert_key(track, output_time,
					source.position_track_interpolate(i, source_time))
	return output


func _assert_contract(rebuilt: Animation, source: Animation, ticks: int, target_length_s: float) -> bool:
	var expected_tracks := 0
	for i in source.get_track_count():
		var type := source.track_get_type(i)
		if (type == Animation.TYPE_ROTATION_3D or type == Animation.TYPE_POSITION_3D) \
			and not _bone_of(source.track_get_path(i)).is_empty():
			expected_tracks += 1
	if rebuilt.loop_mode != Animation.LOOP_NONE:
		push_error("[rebuild-rebound-grab] '%s' loop_mode=%d; rebound grab must be LOOP_NONE."
			% [OUTPUT_NAME, rebuilt.loop_mode])
		return false
	if not is_equal_approx(rebuilt.length, target_length_s):
		push_error("[rebuild-rebound-grab] '%s' length %.9f s != latch duration %.9f s."
			% [OUTPUT_NAME, rebuilt.length, target_length_s])
		return false
	if rebuilt.get_track_count() != expected_tracks:
		push_error("[rebuild-rebound-grab] '%s' has %d body tracks; source has %d."
			% [OUTPUT_NAME, rebuilt.get_track_count(), expected_tracks])
		return false
	for i in rebuilt.get_track_count():
		if rebuilt.track_get_key_count(i) != ticks + 1:
			push_error("[rebuild-rebound-grab] '%s' track %d has %d keys; expected one per latch tick plus terminal key (%d)."
				% [OUTPUT_NAME, i, rebuilt.track_get_key_count(i), ticks + 1])
			return false
	print("[rebuild-rebound-grab] '%s': len=%.9f s (%d ticks), loop=%d, tracks=%d, keys/track=%d"
		% [OUTPUT_NAME, rebuilt.length, ticks, rebuilt.loop_mode, rebuilt.get_track_count(), ticks + 1])
	return true
