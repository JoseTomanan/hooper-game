extends SceneTree
# Exact track/key comparison; optionally allow only #429's fadeaway change.
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
	if args.size() not in [2, 3] or (args.size() == 3 and args[2] != "--fadeaway-only"):
		push_error("usage: -s tools/verify_jumpshot_rebuild.gd -- <before.res> <after.res> [--fadeaway-only]")
		quit(1)
		return
	var fadeaway_only := args.size() == 3
	var before: AnimationLibrary = load(args[0])
	var after: AnimationLibrary = load(args[1])
	if before == null or after == null or before.get_animation_list() != after.get_animation_list():
		push_error("animation inventory changed or resources missing")
		quit(1)
		return
	var checked := 0
	var allowed := [&"fadeawayactive"] if fadeaway_only else [&"jumpshotstartup", &"jumpshotactive", &"jumpshotrecovery"]
	for name in before.get_animation_list():
		if name in allowed:
			continue
		if fingerprint(before.get_animation(name)) != fingerprint(after.get_animation(name)):
			push_error("unrelated animation changed: %s" % name)
			quit(1)
			return
		checked += 1
	if fadeaway_only and not exact_fadeaway_endpoints(after):
		quit(1)
		return
	print("[jumpshot-rebuild] PASS %d unrelated animations unchanged" % checked)
	quit(0)


func exact_fadeaway_endpoints(library: AnimationLibrary) -> bool:
	if not library.has_animation(&"fadeawayactive") or not library.has_animation(&"jumpshotactive"):
		push_error("fadeaway/normal Active animation missing")
		return false
	var fadeaway := library.get_animation(&"fadeawayactive")
	var normal := library.get_animation(&"jumpshotactive")
	var duration := 4.0 / 60.0
	# Saved 4.7.1 animation track times round-trip through float32: the measured
	# final key is 0.06666667014360428, while Animation.length retains the double
	# 4/60 value 0.06666666666666667. Compare exact representations, never an
	# adjustable epsilon that could admit a genuinely moved endpoint.
	# https://docs.godotengine.org/en/4.7/classes/class_packedfloat32array.html
	var stored_duration: float = PackedFloat32Array([duration])[0]
	for animation in [fadeaway, normal]:
		if animation.length != duration and animation.length != stored_duration:
			push_error("fadeaway/normal length is not exactly four ticks in double or float32 representation: %.17f" % animation.length)
			return false
	var checked := 0
	# Require both track inventories: omitting a material normal channel cannot
	# turn endpoint verification into an empty intersection that passes.
	for item in [fadeaway, normal]:
		var source: Animation = item
		var other: Animation = normal if source == fadeaway else fadeaway
		for track in source.get_track_count():
			var type := source.track_get_type(track)
			if type not in [Animation.TYPE_POSITION_3D, Animation.TYPE_ROTATION_3D]:
				continue
			var path := source.track_get_path(track)
			var counterpart := other.find_track(path, type)
			if counterpart < 0 or source.track_get_key_count(track) < 2 or other.track_get_key_count(counterpart) < 2:
				push_error("fadeaway/normal endpoint counterpart missing: %s type=%d" % [path, type])
				return false
			var last := source.track_get_key_count(track) - 1
			var other_last := other.track_get_key_count(counterpart) - 1
			var end_time := source.track_get_key_time(track, last)
			var other_end_time := other.track_get_key_time(counterpart, other_last)
			if source.track_get_key_time(track, 0) != 0.0 or other.track_get_key_time(counterpart, 0) != 0.0 \
				or end_time != other_end_time or end_time != stored_duration:
				push_error("fadeaway/normal endpoint time mismatch: %s source-key=%.17f source-length=%.17f normal-key=%.17f normal-length=%.17f expected=%.17f"
					% [path, source.track_get_key_time(track, last), source.length, other.track_get_key_time(counterpart, other_last), other.length, 4.0 / 60.0])
				return false
			for pair in [[0, 0], [last, other_last]]:
				if var_to_bytes(source.track_get_key_value(track, pair[0])) != var_to_bytes(other.track_get_key_value(counterpart, pair[1])):
					push_error("fadeaway/normal endpoint value mismatch: %s key=%d" % [path, pair[0]])
					return false
			checked += 1
	if checked == 0:
		push_error("no fadeaway/normal position or rotation endpoints checked")
		return false
	print("[jumpshot-rebuild] PASS exact fadeaway/normal position and rotation endpoints")
	print("[jumpshot-rebuild] endpoint-time=%.17f duration=%.17f float32-duration=%.17f" % [stored_duration, duration, stored_duration])
	return true
