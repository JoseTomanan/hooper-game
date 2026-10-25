extends SceneTree
# Freeze the pre-#429 production selection asset for comparable-clock controls.
# https://docs.godotengine.org/en/4.7/classes/class_animationlibrary.html
func _initialize() -> void:
	var args := OS.get_cmdline_user_args()
	if args.size() != 1 or FileAccess.file_exists(args[0]):
		push_error("usage: -- <fresh-output.res>; never overwrite the frozen baseline")
		quit(1)
		return
	var source: AnimationLibrary = load("res://assets/locomotion.res")
	var fixture := AnimationLibrary.new()
	fixture.add_animation(&"fadeawayactive", source.get_animation(&"fadeawayactive").duplicate(true))
	var error := ResourceSaver.save(fixture, args[0])
	quit(0 if error == OK else 1)
