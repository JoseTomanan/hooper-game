@tool
extends SceneTree

func _init():
	# Load the Player scene and extract the AnimationTree's tree_root
	var scene = load("res://scenes/Player.tscn")
	if scene == null:
		print("ERROR: Failed to load Player.tscn")
		quit(1)
		return

	var player = scene.instantiate()
	if player == null:
		print("ERROR: Failed to instantiate Player.tscn")
		quit(1)
		return

	var anim_tree = player.get_node("AnimationTree")
	if anim_tree == null:
		print("ERROR: Could not find AnimationTree node")
		quit(1)
		return

	var tree_root = anim_tree.tree_root
	if tree_root == null:
		print("ERROR: AnimationTree.tree_root is null")
		quit(1)
		return

	print("tree_root type: ", tree_root.get_class())

	var result = ResourceSaver.save(tree_root, "res://scenes/spike/loco_baseline.tres")
	if result != OK:
		print("ERROR: ResourceSaver.save failed with code: ", result)
		quit(1)
		return

	print("SUCCESS: Saved loco_baseline.tres")
	quit(0)
