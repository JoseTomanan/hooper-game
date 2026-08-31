extends SceneTree
# Rebuilds #312's three 60 Hz Euro-step slices.  The source is whole-armature
# baked by Blender; object/scale tracks are intentionally discarded so Player's
# skeleton paths and PlayerRigScaler remain authoritative.
const TPS := 60.0
const LIB_PATH := "res://assets/locomotion.res"
const SRC_PATH := "res://assets/eurostep_authored.fbx"
const WINDOWS := [[0.0, 6.0 / 60.0, 6], [6.0 / 60.0, 20.0 / 60.0, 14], [20.0 / 60.0, 36.0 / 60.0, 16]]
const NAMES := ["eurostepstartup", "eurostepactive", "eurosteprecovery"]

func _initialize() -> void:
	var library: AnimationLibrary = load(LIB_PATH)
	var packed: PackedScene = load(SRC_PATH)
	var player := _find(packed.instantiate(), "AnimationPlayer") as AnimationPlayer
	if library == null or player == null or not player.has_animation("eurostep"):
		push_error("[eurostep] missing locomotion library or imported Euro-step action")
		quit(1); return
	var source := player.get_animation("eurostep")
	if absf(source.length - 36.0 / TPS) > 0.02:
		push_error("[eurostep] source duration is not 36 ticks; check Blender fps/trimming")
		quit(1); return
	for i in NAMES.size():
		var w: Array = WINDOWS[i]
		var clip := _slice(source, w[0], w[1], w[2])
		if absf(clip.length - float(w[2]) / TPS) > 0.0001 or clip.get_track_count() < 100:
			push_error("[eurostep] invalid slice %s (length/tracks)" % NAMES[i])
			quit(1); return
		if library.has_animation(NAMES[i]): library.remove_animation(NAMES[i])
		library.add_animation(NAMES[i], clip)
	var err := ResourceSaver.save(library, LIB_PATH)
	if err != OK: push_error("[eurostep] save failed: %d" % err); quit(1); return
	print("[eurostep] rebuilt Startup=6, Active=14, Recovery=16 ticks")
	quit(0)

func _slice(source: Animation, t0: float, t1: float, ticks: int) -> Animation:
	var out := Animation.new()
	out.length = float(ticks) / TPS
	out.loop_mode = Animation.LOOP_NONE
	for i in source.get_track_count():
		var kind := source.track_get_type(i)
		if kind != Animation.TYPE_ROTATION_3D and kind != Animation.TYPE_POSITION_3D: continue
		var path := source.track_get_path(i)
		if path.get_subname_count() == 0: continue
		var name := String(path)
		if name.begins_with("Armature/"): path = NodePath(name.substr(len("Armature/")))
		var track := out.add_track(kind)
		out.track_set_path(track, path)
		for k in ticks + 1:
			var time := float(k) / TPS
			var source_time := lerpf(t0, t1, float(k) / float(ticks))
			if kind == Animation.TYPE_ROTATION_3D:
				out.rotation_track_insert_key(track, time, source.rotation_track_interpolate(i, source_time))
			else:
				out.position_track_insert_key(track, time, source.position_track_interpolate(i, source_time))
	return out

func _find(node: Node, type_name: String) -> Node:
	if node.is_class(type_name): return node
	for child in node.get_children():
		var found := _find(child, type_name)
		if found != null: return found
	return null
