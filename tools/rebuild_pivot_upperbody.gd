extends SceneTree
# One-shot asset build tool — fixes the "turning T-poses the arms" bug.
#
# Root cause (confirmed headlessly, Godot 4.7.1): the `pivot` clip in
# assets/locomotion.res carries ONLY 4 rotation tracks (Hips, Spine,
# LeftUpLeg, RightUpLeg — the plant/turn's lower body). Every other bone has
# no track in that clip, so while the AnimationTree sits in its `Pivot` state
# (a single clip at full weight) the AnimationMixer authoritatively writes
# those un-tracked bones to the skeleton's REST transform — and Y Bot's rest
# is a Mixamo T-pose. Result: the arms snap horizontal the instant a turn
# begins. (idle=32 tracks / run=25 tracks both drive the arms, which is why
# the collapse is specific to turning.)
#
# Fix: make `pivot` a COMPLETE clip. For every ROTATION_3D bone track that
# `idle` drives but `pivot` lacks, add a matching rotation track to `pivot`
# holding idle's frame-0 value as a single constant key. During a plant-pivot
# the upper body simply holds the neutral idle stance while the hips/thighs do
# the authored turn — natural, and (critically) surgical: these new tracks are
# full-weight ABSOLUTE rotations in a single-clip state, so they are
# rest-independent and change NO other state's blend accumulation (unlike
# re-anchoring the arm rests globally, which would re-entangle the Locomotion
# idle<->run blend that #287's BlendRestAnchor just stabilized).
#
# Idempotent: bones already present on `pivot` are skipped, so re-running is a
# no-op. Run:
#   godot --headless --path . -s tools/rebuild_pivot_upperbody.gd

const LIB_PATH := "res://assets/locomotion.res"

func bone_from_path(np: NodePath) -> String:
	# Tracks are authored as "Skeleton3D:mixamorig_<Bone>"; the bone is the
	# single sub-name after the node path.
	if np.get_subname_count() == 0:
		return ""
	return np.get_subname(0)

func _initialize() -> void:
	var lib = load(LIB_PATH)
	if lib == null or not (lib is AnimationLibrary):
		push_error("[rebuild-pivot] failed to load AnimationLibrary at %s" % LIB_PATH)
		quit(1)
		return

	var idle: Animation = lib.get_animation(&"idle")
	var pivot: Animation = lib.get_animation(&"pivot")
	if idle == null or pivot == null:
		push_error("[rebuild-pivot] locomotion.res missing 'idle' or 'pivot' clip.")
		quit(1)
		return

	# Bones pivot already animates (its authored plant tracks) — never touch these.
	var pivot_bones := {}
	for i in range(pivot.get_track_count()):
		if pivot.track_get_type(i) != Animation.TYPE_ROTATION_3D:
			continue
		var b := bone_from_path(pivot.track_get_path(i))
		if b != "":
			pivot_bones[b] = true

	print("[rebuild-pivot] pivot before: %d tracks; existing bones: %s"
		% [pivot.get_track_count(), str(pivot_bones.keys())])

	var added := 0
	for i in range(idle.get_track_count()):
		if idle.track_get_type(i) != Animation.TYPE_ROTATION_3D:
			continue
		var path := idle.track_get_path(i)
		var bone := bone_from_path(path)
		if bone == "" or pivot_bones.has(bone):
			continue
		if idle.track_get_key_count(i) <= 0:
			continue
		# idle's neutral frame-0 orientation for this bone, held constant.
		var q: Quaternion = idle.track_get_key_value(i, 0)
		var t := pivot.add_track(Animation.TYPE_ROTATION_3D)
		pivot.track_set_path(t, path)
		pivot.rotation_track_insert_key(t, 0.0, q)
		pivot_bones[bone] = true
		added += 1

	print("[rebuild-pivot] added %d upper/limb rotation tracks; pivot now %d tracks."
		% [added, pivot.get_track_count()])

	if added == 0:
		print("[rebuild-pivot] nothing to add — pivot already complete (idempotent no-op). Not re-saving.")
		quit(0)
		return

	var err := ResourceSaver.save(lib, LIB_PATH)
	if err != OK:
		push_error("[rebuild-pivot] ResourceSaver.save failed with error %d" % err)
		quit(1)
		return

	print("[rebuild-pivot] saved %s" % LIB_PATH)
	quit(0)
