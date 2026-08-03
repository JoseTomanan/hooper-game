extends SceneTree
# Measurement instrument (#316) — reports, for each named clip in
# assets/locomotion.res, how much of the Y Bot rig it actually animates.
#
# Run:  godot --headless --path . -s tools/measure_clip_completeness.gd
#
# Why this exists: #316 proposes re-authoring the jumpshot clips in Blender on
# the stated grounds that the GDScript-authored ones "leave track gaps by
# construction", every gap being a bone pinned to REST for the clip's duration
# (the a45bd1d T-pose trap). That is a quantitative claim about the SHIPPED
# binary resource, and the handoff makes measuring it the gate on doing the work
# at all. Reading the build tool's source is not the same evidence: locomotion.res
# is committed as a binary and could have drifted from the tool that allegedly
# built it.
#
# Reports against the rig's real bone list rather than a hardcoded 65, so a rig
# change makes the number move instead of making the number lie.
#
# Reports TWO coverage numbers per clip, and the second is the one that matters:
#   bones     — every rig bone the clip animates. Headline figure, easy to
#               misread: the jumpshot family scores 52/65 = 80%, which LOOKS
#               like a defect and is not.
#   non-leaf  — bones that have CHILDREN. An untracked bone pins its whole
#               SUBTREE to the rig's rest (a Mixamo T-pose) at full weight, which
#               is the a45bd1d trap. A leaf has no subtree and its rest is
#               relative to a parent the clip does animate, so it follows
#               correctly. Only a non-leaf gap can produce a false read.
#
# BOTH numbers count ROTATION_3D tracks ONLY (#330). The first cut of this
# instrument counted a bone as animated if it carried a track of ANY type, and
# that is a false negative: Godot's AnimationMixer drives translation, rotation
# and scale as independent channels, so a bone whose only track is SCALE_3D
# still has its ROTATION written from skeleton rest. The a45bd1d trap is
# specifically about rotation rest-fallback. `idle` is exactly that shape — it
# carries a 1-key SCALE track and no rotation track for mixamorig_LeftToeBase —
# and scored a clean 30/52 here until this filter landed.

const LIB_PATH := "res://assets/locomotion.res"
const RIG_FBX := "res://assets/Y Bot.fbx"

# Empty = every clip in the library, sorted. Name clips explicitly to narrow.
const CLIPS := []


func bone_of(np: NodePath) -> String:
	return "" if np.get_subname_count() == 0 else String(np.get_subname(0))


func _initialize() -> void:
	var lib = load(LIB_PATH)
	if lib == null or not (lib is AnimationLibrary):
		push_error("[measure] failed to load AnimationLibrary at %s" % LIB_PATH)
		quit(1)
		return

	var skel: Skeleton3D = _find(load(RIG_FBX).instantiate(), "Skeleton3D")
	if skel == null:
		push_error("[measure] no Skeleton3D in %s" % RIG_FBX)
		quit(1)
		return

	var total := skel.get_bone_count()
	var non_leaf := []
	for i in total:
		if skel.get_bone_children(i).size() > 0:
			non_leaf.append(skel.get_bone_name(i))
	print("[measure] rig '%s': %d bones, %d of them non-leaf" % [RIG_FBX, total, non_leaf.size()])
	print("[measure] library holds %d clips" % lib.get_animation_list().size())
	print("")
	print("[measure] coverage columns count ROTATION_3D tracks only (#330)")
	print("%-26s %8s %6s %6s %6s %8s %10s" % ["clip", "len", "trk", "rot", "pos", "rotbone", "non-leaf"])

	var names := CLIPS
	if names.is_empty():
		names = lib.get_animation_list()
		names.sort()

	for name in names:
		if not lib.has_animation(name):
			print("%-24s  (absent)" % name)
			continue
		var a: Animation = lib.get_animation(name)
		var rot := 0
		var pos := 0
		var scl := 0
		var bones := {}
		var unresolved := []
		for i in a.get_track_count():
			match a.track_get_type(i):
				Animation.TYPE_ROTATION_3D: rot += 1
				Animation.TYPE_POSITION_3D: pos += 1
				Animation.TYPE_SCALE_3D: scl += 1
			var b := bone_of(a.track_get_path(i))
			if b == "":
				continue
			if skel.find_bone(b) < 0:
				# Unresolved is a whole-clip health check, so it stays type-blind:
				# a SCALE track aimed at a bone the rig does not have is still a
				# defect worth surfacing, even though it cannot cause a T-pose.
				if not unresolved.has(b):
					unresolved.append(b)
			elif a.track_get_type(i) == Animation.TYPE_ROTATION_3D:
				bones[b] = true
		var n := bones.size()
		var nl_missing := []
		for b in non_leaf:
			if not bones.has(b):
				nl_missing.append(b)
		var nl_have := non_leaf.size() - nl_missing.size()
		var flag := "" if nl_missing.is_empty() else "  <-- NON-LEAF GAP"
		print("%-26s %8.4f %6d %6d %6d %5d/%d %6d/%d%s"
			% [name, a.length, a.get_track_count(), rot, pos, n, total,
			   nl_have, non_leaf.size(), flag])
		if unresolved.size() > 0:
			print("    UNRESOLVED on the rig: %s" % str(unresolved))
		if not nl_missing.is_empty():
			print("    non-leaf bones left at REST: %s" % str(nl_missing))

	# The gap list is the actual subject of #316's claim: which bones sit at REST
	# for the whole clip. Print it in full for the jumpshot family, since a count
	# alone cannot distinguish "13 finger bones nobody sees" from "13 spine bones".
	print("")
	for name in ["jumpshotstartup"]:
		if not lib.has_animation(name):
			continue
		var a: Animation = lib.get_animation(name)
		var tracked := {}
		for i in a.get_track_count():
			if a.track_get_type(i) != Animation.TYPE_ROTATION_3D:
				continue
			var b := bone_of(a.track_get_path(i))
			if b != "" and skel.find_bone(b) >= 0:
				tracked[b] = true
		var missing := []
		for i in total:
			var bn := skel.get_bone_name(i)
			if not tracked.has(bn):
				missing.append(bn)
		print("[measure] '%s' leaves %d/%d bones at REST:" % [name, missing.size(), total])
		print("    %s" % str(missing))

	quit(0)


func _find(n: Node, cls: String) -> Node:
	if n.get_class() == cls:
		return n
	for c in n.get_children():
		var r := _find(c, cls)
		if r != null:
			return r
	return null
