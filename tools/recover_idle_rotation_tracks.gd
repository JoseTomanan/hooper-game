extends SceneTree
# One-shot asset build tool (#330) — restores ROTATION_3D tracks that the FBX
# importer dropped from `idle`, for MATERIAL bones only.
#
# The bug it closes
# -----------------
# `assets/idle.fbx.import` shipped with `animation/remove_immutable_tracks=true`
# (README trap 2). Godot drops a track that is constant AND equal to the bone's
# rest, so a bone held at its neutral angle through the whole idle loop loses
# its rotation track entirely. `mixamorig_LeftToeBase` is exactly that case.
#
# That matters downstream because tools/rebuild_pivot_upperbody.gd completes the
# `pivot` clip by copying, for every bone `idle` drives that `pivot` lacks,
# idle's ROTATION_3D track. It found none for LeftToeBase, so `pivot` inherited
# the hole — 22 finger gaps plus one foot bone, which is what the #316/#329
# completeness sweep surfaced.
#
# What this does NOT claim (measured 2026-08-03, #330)
# ----------------------------------------------------
# The recovered value is `0.0000 deg` from Y Bot's own LeftToeBase rest, and the
# surviving `mixamorig_RightToeBase` track is likewise `0.0000 deg` from ITS
# rest. Both toes therefore render at rest either way, and adding this track
# changes the rendered pose by exactly nothing. The original #330 write-up
# claimed the feet were visibly asymmetric during a plant-and-pivot; that is
# FALSE and the measurement is in the issue.
#
# It is still worth doing, for two reasons that are about the future rather than
# the current frame:
#   1. It makes "every material non-leaf bone carries a rotation track" TRUE for
#      idle/pivot, so LocomotionClipTest family 9 becomes a live guard. While
#      the invariant is violated the gate can only report the known-benign gap,
#      and a REAL gap arriving later would be indistinguishable from it.
#   2. It removes a latent asymmetry. The two toes coincide today only because
#      the authored value happens to equal rest. `BlendRestAnchor` already
#      rewrites bone rests at runtime (LeftUpLeg/RightUpLeg); the day anyone
#      extends it down the leg chain, RightToeBase would keep its authored value
#      while LeftToeBase followed the new rest, and the asymmetry the write-up
#      described would become real.
#
# Why a splice and not a re-extract
# ---------------------------------
# There is no shipped tool that rebuilds `idle` from `idle.fbx` — the original
# extraction was a disposable one-off (see docs/spikes/0012). A wholesale
# re-extract would discard #275/#286's hemisphere normalization and #271's
# programmatic loop_mode, and would drag in the 31 per-bone SCALE tracks the
# reimport now carries, which overwrite PlayerRigScaler's SetBonePoseScale every
# frame (README trap 13). So: take the one track, leave everything else alone.
#
# Finger joints are deliberately excluded. They are 22 of the 23 gaps and a
# finger's subtree is one more finger joint — invisible at this rig's scale, and
# out of scope per the #330 write-up.
#
# Idempotent: bones already carrying a rotation track on `idle` are skipped, so
# re-running is a no-op. Run BEFORE tools/rebuild_pivot_upperbody.gd, which
# propagates the result into `pivot`:
#   godot --headless --path . -s tools/recover_idle_rotation_tracks.gd
#   godot --headless --path . -s tools/rebuild_pivot_upperbody.gd

const LIB_PATH := "res://assets/locomotion.res"
const IDLE_FBX := "res://assets/idle.fbx"
const RIG_FBX := "res://assets/Y Bot.fbx"


func bone_of(np: NodePath) -> String:
	return "" if np.get_subname_count() == 0 else String(np.get_subname(0))


# A finger JOINT, not the hand itself — mixamorig_LeftHand is a material limb
# bone. Kept in sync with LocomotionClipTest.IsFingerJoint.
func is_finger_joint(b: String) -> bool:
	for side in ["mixamorig_LeftHand", "mixamorig_RightHand"]:
		for digit in ["Thumb", "Index", "Middle", "Ring", "Pinky"]:
			if b.begins_with(side + digit):
				return true
	return false


func ang(a: Quaternion, b: Quaternion) -> float:
	return rad_to_deg(2.0 * acos(clampf(absf(a.normalized().dot(b.normalized())), -1.0, 1.0)))


func _initialize() -> void:
	var lib = load(LIB_PATH)
	if lib == null or not (lib is AnimationLibrary):
		push_error("[recover-idle] failed to load AnimationLibrary at %s" % LIB_PATH)
		quit(1)
		return
	var idle: Animation = lib.get_animation(&"idle")
	if idle == null:
		push_error("[recover-idle] locomotion.res has no 'idle' clip.")
		quit(1)
		return

	var rig: Skeleton3D = _find(load(RIG_FBX).instantiate(), "Skeleton3D")
	if rig == null:
		push_error("[recover-idle] no Skeleton3D in %s" % RIG_FBX)
		quit(1)
		return

	# Bones idle already drives, and the shipped node-path prefix. The prefix is
	# READ from an existing track rather than hardcoded: the retargeted source
	# scene names its skeleton `%GeneralSkeleton` while the shipped clips bind to
	# `Skeleton3D` (spike 0012), and a hardcoded guess that drifts would produce
	# a track that resolves to nothing while every count still looks right
	# (README trap 13 — a silent no-op that passes every duration assertion).
	var have := {}
	var prefix := ""
	for i in idle.get_track_count():
		if idle.track_get_type(i) != Animation.TYPE_ROTATION_3D:
			continue
		var np := idle.track_get_path(i)
		var b := bone_of(np)
		if b == "":
			continue
		have[b] = true
		if prefix == "":
			prefix = String(np).get_slice(":", 0)
	if prefix == "":
		push_error("[recover-idle] 'idle' has no bone rotation tracks to read a node prefix from.")
		quit(1)
		return
	print("[recover-idle] idle before: %d tracks, %d rotation bones, node prefix '%s'"
		% [idle.get_track_count(), have.size(), prefix])

	var ap: AnimationPlayer = _find(load(IDLE_FBX).instantiate(), "AnimationPlayer")
	if ap == null:
		push_error("[recover-idle] no AnimationPlayer under %s" % IDLE_FBX)
		quit(1)
		return

	var src: Animation = null
	for lib_name in ap.get_animation_library_list():
		var l: AnimationLibrary = ap.get_animation_library(lib_name)
		for an in l.get_animation_list():
			if String(an).contains("Idle"):
				src = l.get_animation(an)
	if src == null:
		push_error("[recover-idle] no 'Idle' take in %s — is remove_immutable_tracks still true?" % IDLE_FBX)
		quit(1)
		return

	var added := 0
	var skipped_fingers := []
	for i in src.get_track_count():
		if src.track_get_type(i) != Animation.TYPE_ROTATION_3D:
			continue
		var b := bone_of(src.track_get_path(i))
		if b == "" or have.has(b):
			continue
		if rig.find_bone(b) < 0:
			print("[recover-idle]   skip '%s' — not a bone on the Y Bot rig." % b)
			continue
		if is_finger_joint(b):
			skipped_fingers.append(b)
			continue
		if src.track_get_key_count(i) <= 0:
			continue

		var t := idle.add_track(Animation.TYPE_ROTATION_3D)
		idle.track_set_path(t, NodePath("%s:%s" % [prefix, b]))
		for k in src.track_get_key_count(i):
			idle.rotation_track_insert_key(
				t, src.track_get_key_time(i, k), src.track_get_key_value(i, k))

		# Provenance, printed rather than asserted: this number is the whole
		# reason the change is safe to make on a live BlendSpace1D endpoint.
		var d := ang(src.track_get_key_value(i, 0),
			rig.get_bone_rest(rig.find_bone(b)).basis.get_rotation_quaternion())
		print("[recover-idle]   + %-28s keys=%d  first_vs_ybot_rest=%.4f deg"
			% [b, src.track_get_key_count(i), d])
		have[b] = true
		added += 1

	if not skipped_fingers.is_empty():
		print("[recover-idle] skipped %d finger joint(s) by design: %s"
			% [skipped_fingers.size(), str(skipped_fingers)])

	if added == 0:
		print("[recover-idle] nothing to add — idle already complete (idempotent no-op). Not re-saving.")
		quit(0)
		return

	print("[recover-idle] added %d material rotation track(s); idle now %d tracks."
		% [added, idle.get_track_count()])
	var err := ResourceSaver.save(lib, LIB_PATH)
	if err != OK:
		push_error("[recover-idle] ResourceSaver.save failed with error %d" % err)
		quit(1)
		return
	print("[recover-idle] saved %s" % LIB_PATH)
	quit(0)


func _find(n: Node, cls: String) -> Node:
	if n.get_class() == cls:
		return n
	for c in n.get_children():
		var r := _find(c, cls)
		if r != null:
			return r
	return null
