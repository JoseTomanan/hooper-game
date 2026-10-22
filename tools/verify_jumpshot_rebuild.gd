extends SceneTree
# Exact track/key comparison, excluding only #418's three normal shot clips.
# https://docs.godotengine.org/en/4.7/classes/class_animation.html
func fingerprint(anim: Animation) -> String:
	var content := [anim.length, anim.loop_mode, anim.step]
	for track in anim.get_track_count():
		var keys := []
		for key in anim.track_get_key_count(track):
			keys.append([anim.track_get_key_time(track, key), anim.track_get_key_value(track, key), anim.track_get_key_transition(track, key)])
		content.append([anim.track_get_path(track), anim.track_get_type(track), anim.track_get_interpolation_type(track), anim.track_is_enabled(track), keys])
	return var_to_str(content).sha256_text()

func _initialize() -> void:
	var args := OS.get_cmdline_user_args()
	if args.size() != 2:
		push_error("usage: -s tools/verify_jumpshot_rebuild.gd -- <before.res> <after.res>")
		quit(1)
		return
	var before: AnimationLibrary = load(args[0])
	var after: AnimationLibrary = load(args[1])
	if before == null or after == null or before.get_animation_list() != after.get_animation_list():
		push_error("animation inventory changed or resources missing")
		quit(1)
		return
	var checked := 0
	for name in before.get_animation_list():
		if name in [&"jumpshotstartup", &"jumpshotactive", &"jumpshotrecovery"]:
			continue
		if fingerprint(before.get_animation(name)) != fingerprint(after.get_animation(name)):
			push_error("unrelated animation changed: %s" % name)
			quit(1)
			return
		checked += 1
	print("[jumpshot-rebuild] PASS %d unrelated animations unchanged" % checked)
	quit(0)
