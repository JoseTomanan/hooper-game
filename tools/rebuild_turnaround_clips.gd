extends SceneTree
# #348 asset tool. Turnaround's first 30 Startup ticks show a low planted gather;
# the final 18 ticks and release/landing preserve the established jump-shot arc.
# Heading turns on the authoritative mesh; these clips add no root displacement.
# Run with Godot 4.7.1 mono, --headless --path . --log-file .godot/<unique>.log -s this file.
# Animation track APIs: https://docs.godotengine.org/en/4.7/classes/class_animation.html

const SOURCE := "res://assets/locomotion.res"
const OUTPUT := "res://assets/turnaround.res"
const TURN_SECONDS := 30.0 / 60.0
const STARTUP_SECONDS := 48.0 / 60.0

func _initialize() -> void:
	quit(0 if rebuild() == OK else 1)


# The normal-shot builder calls this after saving its new source family, so
# Turnaround's established dependency cannot silently retain the old arms.
# https://docs.godotengine.org/en/4.7/tutorials/scripting/gdscript/gdscript_basics.html#static-functions
static func rebuild(source: AnimationLibrary = null) -> Error:
	if source == null:
		source = load(SOURCE)
	if source == null:
		push_error("[rebuild-turnaround] source library missing")
		return ERR_CANT_OPEN
	var shot: Animation = source.get_animation(&"jumpshotstartup")
	var out := AnimationLibrary.new()
	var startup := Animation.new()
	startup.length = STARTUP_SECONDS
	startup.loop_mode = Animation.LOOP_NONE
	# Preserve every material bone track, including toes and fingers. Otherwise
	# full-weight AnimationTree states reset missing limbs to the imported T-pose.
	for i in shot.get_track_count():
		var type := shot.track_get_type(i)
		assert(type == Animation.TYPE_ROTATION_3D or type == Animation.TYPE_POSITION_3D)
		var track := startup.add_track(type)
		var path := shot.track_get_path(i)
		startup.track_set_path(track, path)
		startup.track_set_interpolation_type(track, shot.track_get_interpolation_type(i))
		startup.track_set_interpolation_loop_wrap(track, false)
		var bone := String(path.get_subname(0))
		if type == Animation.TYPE_ROTATION_3D:
			var gather: Quaternion = shot.rotation_track_interpolate(i, 0.0)
			var low := gather
			if bone.ends_with("UpLeg"):
				low = Quaternion(Vector3.RIGHT, -.18) * gather
			elif bone.ends_with("Leg"):
				low = Quaternion(Vector3.RIGHT, .36) * gather
			elif bone.ends_with("Foot"):
				low = Quaternion(Vector3.RIGHT, -.18) * gather
			startup.rotation_track_insert_key(track, 0.0, low.normalized())
			startup.rotation_track_insert_key(track, 24.0 / 60.0, low.normalized())
			startup.rotation_track_insert_key(track, TURN_SECONDS, gather)
		else:
			var gather: Vector3 = shot.position_track_interpolate(i, 0.0)
			var low := gather + Vector3(0, -.06, 0) if bone == "mixamorig_Hips" else gather
			startup.position_track_insert_key(track, 0.0, low)
			startup.position_track_insert_key(track, 24.0 / 60.0, low)
			startup.position_track_insert_key(track, TURN_SECONDS, gather)
		for k in shot.track_get_key_count(i):
			startup.track_insert_key(track, TURN_SECONDS + shot.track_get_key_time(i, k), shot.track_get_key_value(i, k))
	out.add_animation(&"turnaroundstartup", startup)
	for phase in ["active", "recovery"]:
		var segment: Animation = source.get_animation(StringName("jumpshot" + phase)).duplicate(true)
		segment.loop_mode = Animation.LOOP_NONE
		out.add_animation(StringName("turnaround" + phase), segment)
	for name in out.get_animation_list():
		var clip := out.get_animation(name)
		assert(clip.get_track_count() == shot.get_track_count(), "material track coverage changed")
		for track in clip.get_track_count():
			assert(clip.track_get_key_count(track) > 0, "empty material track")
		print("[rebuild-turnaround] %s length=%.6f tracks=%d" % [name, clip.length, clip.get_track_count()])
	var error := ResourceSaver.save(out, OUTPUT)
	if error != OK:
		push_error("could not save turnaround library")
		return error
	print("[rebuild-turnaround] PASS")
	return OK
