extends SceneTree
# #415: display-only carry clips. Run with pinned Godot 4.7.1 mono:
# godot --headless --path . --log-file .godot/415-held-asset.log -s tools/rebuild_held_clips.gd
# Keep locomotion's root/legs verbatim; freeze the corresponding handed
# dribble's contact pose above the hips so neither arm pumps or swings.
# Dedicated rotation tracks / copy_track / rotation_track_interpolate:
# https://docs.godotengine.org/en/4.7/classes/class_animation.html
# Binary AnimationLibrary persistence:
# https://docs.godotengine.org/en/4.7/classes/class_resourcesaver.html#class-resourcesaver-method-save
# Scene endpoints are ordinary speed-weighted blend points, unchanged at 0/6:
# https://docs.godotengine.org/en/4.7/classes/class_animationnodeblendspace1d.html
# Offline pose FK and local rotation writes (global pose is skeleton-relative):
# https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-method-get-bone-global-pose
# https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-method-set-bone-pose-rotation
# Shortest arc between measured vectors, with no guessed bone axis:
# https://docs.godotengine.org/en/4.7/classes/class_quaternion.html#class-quaternion-constructor-quaternion

const SOURCE := "res://assets/locomotion.res"
const OUTPUT := "res://assets/held.res"
const HIPS := NodePath("Skeleton3D:mixamorig_Hips")
const SPINE := NodePath("Skeleton3D:mixamorig_Spine")

func _initialize() -> void:
	call_deferred("_rebuild")

func _rebuild() -> void:
	var source: AnimationLibrary = load(SOURCE)
	assert(source != null, "source animation library missing")
	# Spine compensation below assumes an immediate Hips -> Spine hierarchy.
	var rig := (load("res://assets/Y Bot.fbx") as PackedScene).instantiate()
	var skeleton := rig.find_child("Skeleton3D", true, false) as Skeleton3D
	assert(skeleton != null, "Y Bot skeleton missing")
	assert(skeleton.get_bone_parent(skeleton.find_bone("mixamorig_Spine")) == skeleton.find_bone("mixamorig_Hips"), "unexpected spine ancestry")
	var out := AnimationLibrary.new()
	for hand in ["left", "right"]:
		var carry := source.get_animation(StringName("dribbleidle" + hand))
		for endpoint in ["idle", "moving"]:
			var base := source.get_animation(&"idle" if endpoint == "idle" else &"run")
			var clip: Animation = base.duplicate(true)
			clip.resource_name = endpoint + hand
			clip.loop_mode = Animation.LOOP_LINEAR
			var hips_track := base.find_track(HIPS, Animation.TYPE_ROTATION_3D)
			assert(hips_track >= 0 and base.track_get_key_count(hips_track) == 1, "base hips must stay constant for a fixed carry frame")
			var base_hips := base.rotation_track_interpolate(hips_track, 0.0)
			var source_hips := carry.rotation_track_interpolate(carry.find_track(HIPS, Animation.TYPE_ROTATION_3D), 0.0)
			var frozen := 0
			for track in carry.get_track_count():
				if carry.track_get_type(track) != Animation.TYPE_ROTATION_3D:
					continue
				var path := carry.track_get_path(track)
				var bone := String(path.get_subname(0))
				if bone == "mixamorig_Hips" or bone.ends_with("UpLeg") or bone.ends_with("Leg") or bone.ends_with("Foot") or bone.ends_with("ToeBase"):
					# Missing material lower-body rotations use idle's authored
					# neutral key; existing root/leg tracks remain untouched.
					if clip.find_track(path, Animation.TYPE_ROTATION_3D) < 0:
						var idle := source.get_animation(&"idle")
						var missing := idle.find_track(path, Animation.TYPE_ROTATION_3D)
						assert(missing >= 0, "no neutral lower-body rotation")
						idle.copy_track(missing, clip)
					continue
				var existing := clip.find_track(path, Animation.TYPE_ROTATION_3D)
				if existing >= 0:
					clip.remove_track(existing)
				var rotation := carry.rotation_track_interpolate(track, 0.0)
				if path == SPINE:
					# idle/run and dribble use different imported local rotation
					# frames. Change coordinates at their shared hip boundary,
					# preserving carry's global torso orientation without moving
					# locomotion's hips or re-anchoring the rig's bone rests.
					rotation = base_hips.inverse() * source_hips * rotation
				var target := clip.add_track(Animation.TYPE_ROTATION_3D)
				clip.track_set_path(target, path)
				clip.rotation_track_insert_key(target, 0.0, rotation.normalized())
				frozen += 1
			assert(frozen == 43, "complete torso/head/arm/finger coverage changed")
			out.add_animation(StringName(endpoint + hand), clip)
			print("[rebuild-held] %s length=%.6f tracks=%d frozen=%d" % [endpoint + hand, clip.length, clip.get_track_count(), frozen])
	var error := ResourceSaver.save(out, OUTPUT)
	assert(error == OK, "could not save held animation library")
	rig.free()
	# Bootstrap the library before loading Player.tscn, which binds it. This
	# also works on a clean checkout with the generated output removed.
	_bake_contact(out)
	error = ResourceSaver.save(out, OUTPUT)
	assert(error == OK, "could not save contact-adjusted held library")
	print("[rebuild-held] PASS")
	quit(0)

func _world_pose(skeleton: Skeleton3D, bone: int) -> Transform3D:
	return skeleton.global_transform * skeleton.get_bone_global_pose(bone)

func _palm(skeleton: Skeleton3D, index: int, pinky: int) -> Vector3:
	return (_world_pose(skeleton, index).origin + _world_pose(skeleton, pinky).origin) * 0.5

func _set_world_rotation(skeleton: Skeleton3D, bone: int, rotation: Quaternion) -> void:
	var parent := skeleton.get_bone_parent(bone)
	var parent_basis := skeleton.global_basis if parent < 0 else _world_pose(skeleton, parent).basis
	var local_basis := parent_basis.inverse() * Basis(rotation)
	skeleton.set_bone_pose_rotation(bone, local_basis.orthonormalized().get_rotation_quaternion().normalized())
	skeleton.force_update_bone_child_transform(bone)

func _bake_contact(library: AnimationLibrary) -> void:
	# Use production scene transforms, rig scaling and exported Ball values.
	# No frame elapses: this is an offline asset bake, never a runtime IK path.
	var player: Node3D = (load("res://scenes/Player.tscn") as PackedScene).instantiate()
	player.name = "1"
	player.position.y = (player.get_node("CollisionShape3D").shape as CapsuleShape3D).height * 0.5
	root.add_child(player)
	player.set_physics_process(false)
	player.get_node("AnimationTree").active = false
	var animation_player := player.get_node("AnimationPlayer") as AnimationPlayer
	animation_player.add_animation_library(&"held_bake", library)
	var skeleton := player.find_child("Skeleton3D", true, false) as Skeleton3D
	var ball := (load("res://scenes/Ball.tscn") as PackedScene).instantiate()
	var radius: float = ball.get("BallRadius")
	var center := Vector3(0, ball.get("DribbleHandHeight"), ball.get("DribbleForwardOffset"))
	assert(radius > 0.06, "authored palm target exceeds ball radius")
	for hand in ["left", "right"]:
		center.x = float(ball.get("HandOffset")) * (1.0 if hand == "left" else -1.0)
		var surface := center + Vector3(0, 0.06, -sqrt(radius * radius - 0.06 * 0.06))
		var prefix: String = "mixamorig_" + hand.capitalize()
		var arm := skeleton.find_bone(prefix + "Arm")
		var forearm := skeleton.find_bone(prefix + "ForeArm")
		var wrist := skeleton.find_bone(prefix + "Hand")
		var index := skeleton.find_bone(prefix + "HandIndex1")
		var pinky := skeleton.find_bone(prefix + "HandPinky1")
		assert(mini(mini(arm, forearm), mini(wrist, mini(index, pinky))) >= 0, "required palm/arm bones missing")
		assert(skeleton.get_bone_parent(forearm) == arm and skeleton.get_bone_parent(wrist) == forearm, "unexpected arm ancestry")
		for endpoint in ["idle", "moving"]:
			var name: String = endpoint + hand
			var clip := library.get_animation(name)
			animation_player.play("held_bake/" + name)
			animation_player.seek(0.0, true)
			var shoulder := _world_pose(skeleton, arm).origin
			var elbow := _world_pose(skeleton, forearm).origin
			var old_wrist := _world_pose(skeleton, wrist).origin
			var hand_rotation := _world_pose(skeleton, wrist).basis.orthonormalized().get_rotation_quaternion()
			var target := old_wrist + surface - _palm(skeleton, index, pinky)
			var upper_length := shoulder.distance_to(elbow)
			var lower_length := elbow.distance_to(old_wrist)
			var distance := shoulder.distance_to(target)
			# Fail instead of silently clamping an unreachable carry pose.
			assert(distance < upper_length + lower_length and distance > absf(upper_length - lower_length), "palm target outside arm reach")
			var direction := (target - shoulder).normalized()
			var pole := elbow - shoulder
			pole = pole - direction * pole.dot(direction)
			assert(pole.length_squared() > 0.000001, "source elbow cannot define carry pole")
			var along := (upper_length * upper_length - lower_length * lower_length + distance * distance) / (2.0 * distance)
			var bend := sqrt(maxf(0, upper_length * upper_length - along * along))
			var target_elbow := shoulder + direction * along + pole.normalized() * bend
			var rotation := Quaternion((elbow - shoulder).normalized(), (target_elbow - shoulder).normalized()) * _world_pose(skeleton, arm).basis.orthonormalized().get_rotation_quaternion()
			_set_world_rotation(skeleton, arm, rotation)
			elbow = _world_pose(skeleton, forearm).origin
			var actual_wrist := _world_pose(skeleton, wrist).origin
			rotation = Quaternion((actual_wrist - elbow).normalized(), (target - elbow).normalized()) * _world_pose(skeleton, forearm).basis.orthonormalized().get_rotation_quaternion()
			_set_world_rotation(skeleton, forearm, rotation)
			_set_world_rotation(skeleton, wrist, hand_rotation)
			var error := _palm(skeleton, index, pinky).distance_to(surface)
			assert(error < 0.005, "offline carry palm did not reach surface")
			for bone in [arm, forearm, wrist]:
				var path := NodePath("Skeleton3D:" + skeleton.get_bone_name(bone))
				clip.track_set_key_value(clip.find_track(path, Animation.TYPE_ROTATION_3D), 0, skeleton.get_bone_pose_rotation(bone))
			var min_error := INF
			var max_error := 0.0
			var min_root_y := INF
			var max_root_y := -INF
			for sample in 121:
				animation_player.seek(clip.length * float(sample) / 120.0, true)
				var sample_error := _palm(skeleton, index, pinky).distance_to(surface)
				min_error = minf(min_error, sample_error)
				max_error = maxf(max_error, sample_error)
				var root_y := _world_pose(skeleton, skeleton.find_bone("mixamorig_Hips")).origin.y
				min_root_y = minf(min_root_y, root_y)
				max_root_y = maxf(max_root_y, root_y)
			print("[rebuild-held] %s arm=%.6f+%.6f target-reach=%.6f clamp=0 palm-error=%.6f..%.6f root-bob=%.6f" % [name, upper_length, lower_length, distance, min_error, max_error, max_root_y - min_root_y])
	ball.free()
	player.free()
