extends SceneTree
# Immutable mutation input for #418, saved before replacing production clips.
# https://docs.godotengine.org/en/4.7/classes/class_resourcesaver.html#class-resourcesaver-method-save
func _initialize() -> void:
	# This historical fixture must never be refreshed from replacement assets.
	if FileAccess.file_exists("res://tests/fixtures/jumpshot_goalkeeper.res"):
		push_error("immutable goalkeeper fixture already exists; refusing to overwrite")
		quit(1)
		return
	var source: AnimationLibrary = load("res://assets/locomotion.res")
	var baseline := AnimationLibrary.new()
	for name in [&"jumpshotstartup", &"jumpshotactive", &"jumpshotrecovery", &"fadeawayactive"]:
		baseline.add_animation(name, source.get_animation(name).duplicate(true))
	var error := ResourceSaver.save(baseline, "res://tests/fixtures/jumpshot_goalkeeper.res")
	if error != OK:
		push_error("baseline save failed: %s" % error)
	quit(0 if error == OK else 1)
