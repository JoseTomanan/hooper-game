extends SceneTree
# Asset build tool (#285) — extracts the two dribble-loop blendspace endpoints
# from assets/Dribble.fbx into assets/locomotion.res.
#
# Run:  godot --headless --path . -s tools/rebuild_dribble_clips.gd
# Idempotent: re-running overwrites both clips with freshly-derived ones.
#
# Produces:
#   dribbleidle — the REAL stock-Mixamo clip verbatim (#285a), LOOPED.
#   dribblemove — a temp-draft (#285b): the same clip with a forward torso lean
#                 composed onto the spine, LOOPED, SAME LENGTH.
#
# ── Why both endpoints are derived from `Dribble`, not `Dribble` + `run` ──────
# #276's table suggested deriving the moving endpoint by remixing `Dribble` with
# `run`. Measured headlessly (Godot 4.7.1) that is unsafe, and this is the
# single most important thing to know before editing this file.
#
# locomotion.res holds clips from TWO different rotation representations:
#
#   family          clips                 distance from Y Bot's RAW bone rest
#   ------------    ------------------    -----------------------------------
#   Kenney          idle, run, pivot      Hips 155-158, Spine 140-152,
#   (retargeted,                          UpLeg 122-180   <- antipode zone
#    fix_silhouette)
#
#   stock Mixamo    catch, Dribble        Hips 4-38, Spine 4-33,
#   (straight)                            UpLeg 7-88      <- near rest
#
# Both render correctly on their own, because ROTATION_3D tracks are ABSOLUTE
# local rotations and a single-clip state plays at full weight (see
# BlendRestAnchor's doc). But a PARTIAL-weight blend is rest-anchored, and
# that is where the families cannot mix: at matched phase `dribble` sits
# 155-180 deg from `idle`/`run` on Hips and Spine. Blending across that gap is
# precisely the #287 degeneracy (two contributions near rest's antipode along
# different great circles, cancelling into a pose on neither clip's arc).
#
# It is worse than merely "not better": scripts/Player/BlendRestAnchor.cs
# re-anchors mixamorig_{Left,Right}UpLeg's REST to `idle`'s first key to fix the
# Locomotion blend. That anchor is a Kenney-family pose, so it puts a
# Dribble-family contribution for those same two bones ~92-147 deg from its own
# rest. The fix that stabilises Locomotion actively destabilises a Dribble
# blend built from mixed families.
#
# So: BOTH endpoints come from `Dribble`. Safety then rests on an invariant that
# does not depend on the rest at all — the two endpoints stay within a small
# angle of EACH OTHER, so every intermediate blend weight interpolates a short
# arc and there is nothing to cancel. DribbleLoopTest's `dribble-corridor`
# scenario is the standing proof of that (the #287 corridor sweep, re-pointed at
# this blendspace); it is what must stay green if this file is ever changed.
#
# ── Why the lean goes on Spine, not Hips ─────────────────────────────────────
# mixamorig_Hips is the skeleton root, so leaning it tips the legs too and the
# figure pivots rigidly at the ankles (feet clip the floor). mixamorig_Spine's
# children are torso/arms/head only — the legs hang off Hips — so leaning there
# tilts the upper body over planted feet, which is what a drive posture is.
#
# ── Temp-draft status (#276 rules) ───────────────────────────────────────────
# `dribblemove` is explicitly TEMPORARY and its visual quality is deferred to
# #173 / ADR-0021 — it does not gate merge. It satisfies the #276 temp-draft
# bar: it is a COMPLETE 53-track clip (so the a45bd1d untracked-bones T-pose
# trap cannot bite), its tracks resolve to real Y Bot bones, and its silhouette
# is measurably distinct from the idle endpoint.

const LIB_PATH := "res://assets/locomotion.res"
const SRC_FBX := "res://assets/Dribble.fbx"
const SRC_CLIP := "mixamo_com"

const IDLE_NAME := &"dribbleidle"
const MOVE_NAME := &"dribblemove"

# Forward torso lean applied to the moving endpoint. Big enough to read as a
# drive posture at a glance (ADR-0003 legibility), small enough that the two
# blendspace endpoints stay a short arc apart — see the corridor argument above.
const LEAN_DEGREES := 20.0
const LEAN_BONE := "mixamorig_Spine"

func bone_of(np: NodePath) -> String:
	return "" if np.get_subname_count() == 0 else String(np.get_subname(0))

func _initialize() -> void:
	var lib = load(LIB_PATH)
	if lib == null or not (lib is AnimationLibrary):
		push_error("[rebuild-dribble] failed to load AnimationLibrary at %s" % LIB_PATH)
		quit(1)
		return

	var packed = load(SRC_FBX)
	if packed == null:
		push_error("[rebuild-dribble] failed to load %s" % SRC_FBX)
		quit(1)
		return
	var src_root: Node = packed.instantiate()
	var ap: AnimationPlayer = src_root.get_node_or_null("AnimationPlayer")
	if ap == null or not ap.has_animation(SRC_CLIP):
		push_error("[rebuild-dribble] %s has no AnimationPlayer clip '%s'" % [SRC_FBX, SRC_CLIP])
		quit(1)
		return
	var src: Animation = ap.get_animation(SRC_CLIP)
	print("[rebuild-dribble] source '%s': len=%.3f tracks=%d loop=%d"
		% [SRC_CLIP, src.length, src.get_track_count(), src.loop_mode])

	# ── #285a: the real clip, verbatim, LOOPED ───────────────────────────────
	# FBX import defaults to LOOP_NONE. `catch` (a one-shot grab) wanted that;
	# a dribble stance is a sustained loop and MUST be set explicitly here --
	# the single easiest thing to get silently wrong in this issue.
	var idle_clip: Animation = src.duplicate(true)
	idle_clip.loop_mode = Animation.LOOP_LINEAR

	# ── #285b: the temp-draft moving endpoint ────────────────────────────────
	var move_clip: Animation = src.duplicate(true)
	move_clip.loop_mode = Animation.LOOP_LINEAR
	# Same length as the idle endpoint, deliberately: equal-length blend points
	# advance by the same delta and so stay phase-locked, which means the two
	# contributions differ ONLY by the lean at every frame -- never by dribble
	# cycle phase. That is what keeps the blended arc short at all times.

	var lean_axis := _derive_body_right_axis()
	if lean_axis == Vector3.ZERO:
		push_error("[rebuild-dribble] could not derive the body's right axis from Y Bot's rest pose.")
		quit(1)
		return
	var lean := Quaternion(lean_axis, deg_to_rad(LEAN_DEGREES))

	var leaned := _apply_lean(move_clip, LEAN_BONE, lean)
	if leaned <= 0:
		push_error("[rebuild-dribble] no '%s' rotation track found to lean -- refusing to save a "
			% LEAN_BONE + "moving endpoint identical to the idle one.")
		quit(1)
		return

	# Prove the lean goes FORWARD, geometrically, instead of trusting the
	# cross-product order: pose a real skeleton with each clip and check the head
	# actually moved along the facing axis. A sign error here would draft a
	# lean-BACK -- which reads as a step-back/retreat posture, a different move
	# rather than merely a rough-looking one, so it is worth an assertion.
	var head_shift := _head_shift_along(idle_clip, move_clip, _facing)
	print("[rebuild-dribble] head displacement along facing axis = %+.4f m" % head_shift)
	if head_shift <= 0.0:
		push_error("[rebuild-dribble] the lean moved the head %.4f m along facing -- that is a lean BACK. "
			% head_shift + "Check the lean-axis handedness in _derive_body_right_axis().")
		quit(1)
		return

	# Guard the "distinct silhouette" bar with a real measurement rather than
	# trusting the edit landed (the repo's prove-match-count-> 0 convention).
	var spread := _max_pose_delta(idle_clip, move_clip)
	print("[rebuild-dribble] leaned %d key(s) on '%s' by %.0f deg about %s; "
		% [leaned, LEAN_BONE, LEAN_DEGREES, lean_axis]
		+ "max endpoint-to-endpoint pose delta = %.1f deg" % spread)
	if spread < 5.0:
		push_error("[rebuild-dribble] endpoints differ by only %.1f deg -- not a distinct silhouette." % spread)
		quit(1)
		return

	# Idempotency: drop any previous build of these two clips first, so re-running
	# re-derives them from the pristine FBX rather than stacking edits.
	if lib.has_animation(IDLE_NAME):
		lib.remove_animation(IDLE_NAME)
	if lib.has_animation(MOVE_NAME):
		lib.remove_animation(MOVE_NAME)
	lib.add_animation(IDLE_NAME, idle_clip)
	lib.add_animation(MOVE_NAME, move_clip)

	var err := ResourceSaver.save(lib, LIB_PATH)
	if err != OK:
		push_error("[rebuild-dribble] ResourceSaver.save failed with error %d" % err)
		quit(1)
		return

	print("[rebuild-dribble] saved %s; clips now: %s" % [LIB_PATH, str(lib.get_animation_list())])
	quit(0)

var _facing := Vector3.ZERO   # set by _derive_body_right_axis, reused by the proof

# The axis to lean ABOUT, so that a POSITIVE rotation tips the torso forward.
# Derived from Y Bot's own rest pose rather than hardcoded, because "which world
# axis is forward" depends on the FBX's import orientation -- guessing it
# backwards would draft a lean-BACK (a step-back posture), which reads as a
# different move rather than merely looking rough.
#
# Handedness (Godot is right-handed, +Y up): a forward lean tips the spine's own
# up (+Y) TOWARD facing, so the axis must satisfy rot(axis, +theta): up -> facing.
# That is `up x facing`, NOT `facing x up` -- the two differ by sign and the
# wrong one leans back. _head_shift_along() is the standing proof, so this
# comment can never silently drift from what the code does.
func _derive_body_right_axis() -> Vector3:
	var ybot: Node = load("res://assets/Y Bot.fbx").instantiate()
	var skel: Skeleton3D = _find(ybot, "Skeleton3D")
	if skel == null:
		return Vector3.ZERO
	# Toes point forward: the foot->toe vector in the XZ plane is the facing.
	var foot := skel.find_bone("mixamorig_LeftFoot")
	var toe := skel.find_bone("mixamorig_LeftToeBase")
	if foot < 0 or toe < 0:
		return Vector3.ZERO
	var p_foot: Vector3 = skel.get_bone_global_rest(foot).origin
	var p_toe: Vector3 = skel.get_bone_global_rest(toe).origin
	var forward := (p_toe - p_foot)
	forward.y = 0.0
	if forward.length() < 0.001:
		return Vector3.ZERO
	forward = forward.normalized()
	_facing = forward
	var axis := Vector3.UP.cross(forward).normalized()
	print("[rebuild-dribble] derived facing=%s -> lean axis (up x facing)=%s" % [forward, axis])
	return axis

# Pre-multiplies every key of `bone`'s rotation track by `lean`. Pre-multiplication
# (world-space delta * local rotation) tilts the bone in the skeleton's frame,
# which is what a torso lean is; post-multiplying would twist it about its own
# already-rotated local axis instead.
func _apply_lean(anim: Animation, bone: String, lean: Quaternion) -> int:
	var touched := 0
	for i in anim.get_track_count():
		if anim.track_get_type(i) != Animation.TYPE_ROTATION_3D:
			continue
		if bone_of(anim.track_get_path(i)) != bone:
			continue
		for k in anim.track_get_key_count(i):
			var q: Quaternion = anim.track_get_key_value(i, k)
			anim.track_set_key_value(i, k, (lean * q).normalized())
			touched += 1
	return touched

# Largest per-bone angular difference between the two endpoints at matched
# phase -- the honest measure of "are these silhouettes actually distinct".
func _max_pose_delta(a: Animation, b: Animation) -> float:
	var worst := 0.0
	for i in a.get_track_count():
		if a.track_get_type(i) != Animation.TYPE_ROTATION_3D:
			continue
		var bone := bone_of(a.track_get_path(i))
		var j := -1
		for t in b.get_track_count():
			if b.track_get_type(t) == Animation.TYPE_ROTATION_3D and bone_of(b.track_get_path(t)) == bone:
				j = t
				break
		if j < 0:
			continue
		for s in 24:
			var u := float(s) / 24.0
			var qa: Quaternion = a.rotation_track_interpolate(i, u * a.length)
			var qb: Quaternion = b.rotation_track_interpolate(j, u * b.length)
			var d: float = clampf(absf(qa.normalized().dot(qb.normalized())), -1.0, 1.0)
			worst = maxf(worst, rad_to_deg(2.0 * acos(d)))
	return worst

# Poses a real Y Bot skeleton with each clip's frame 0 and returns how far the
# head moved along `axis` from the unleaned pose to the leaned one. Positive =
# the torso tipped toward the facing direction, i.e. a genuine forward lean.
func _head_shift_along(unleaned: Animation, leaned: Animation, axis: Vector3) -> float:
	var skel: Skeleton3D = _find(load("res://assets/Y Bot.fbx").instantiate(), "Skeleton3D")
	if skel == null:
		return 0.0
	var head := skel.find_bone("mixamorig_Head")
	if head < 0:
		return 0.0
	var before := _pose_and_read(skel, unleaned, head)
	var after := _pose_and_read(skel, leaned, head)
	return (after - before).dot(axis)

# Walks the bone chain by hand rather than calling get_bone_global_pose(): a
# Skeleton3D that was never added to the SceneTree does not recompute its global
# poses, so that call returns an unchanged transform and this proof would pass
# vacuously (it measured exactly 0.0000 m before this was fixed). Manual FK
# depends on nothing but the rest pose and the clip's own keys.
func _pose_and_read(skel: Skeleton3D, anim: Animation, bone_idx: int) -> Vector3:
	var poses := {}
	for i in anim.get_track_count():
		if anim.track_get_type(i) != Animation.TYPE_ROTATION_3D:
			continue
		var b := skel.find_bone(bone_of(anim.track_get_path(i)))
		if b < 0 or anim.track_get_key_count(i) <= 0:
			continue
		poses[b] = anim.track_get_key_value(i, 0)

	# Root-down chain to the target bone.
	var chain := []
	var walk := bone_idx
	while walk >= 0:
		chain.push_front(walk)
		walk = skel.get_bone_parent(walk)

	var acc := Transform3D.IDENTITY
	for b in chain:
		var rest: Transform3D = skel.get_bone_rest(b)
		# ROTATION_3D keys are absolute LOCAL rotations (see BlendRestAnchor's
		# doc), so an animated bone REPLACES the rest basis' rotation; scale and
		# origin carry over from the rest.
		var local := rest
		if poses.has(b):
			local = Transform3D(Basis(poses[b] as Quaternion).scaled(rest.basis.get_scale()), rest.origin)
		acc = acc * local
	return acc.origin

func _find(n: Node, cls: String) -> Node:
	if n.get_class() == cls:
		return n
	for c in n.get_children():
		var r := _find(c, cls)
		if r != null:
			return r
	return null
