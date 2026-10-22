extends SceneTree
# Immutable mutation input for #418, saved before replacing production clips.
# https://docs.godotengine.org/en/4.7/classes/class_resourcesaver.html#class-resourcesaver-method-save
func _initialize() -> void:
	var source: AnimationLibrary = load("res://assets/locomotion.res")
	var baseline := AnimationLibrary.new()
	for name in [&"jumpshotstartup", &"jumpshotactive", &"jumpshotrecovery", &"fadeawayactive"]:
		baseline.add_animation(name, source.get_animation(name).duplicate(true))
	var error := ResourceSaver.save(baseline, "res://tests/fixtures/jumpshot_goalkeeper.res")
	if error != OK:
		push_error("baseline save failed: %s" % error)
	quit(0 if error == OK else 1)
