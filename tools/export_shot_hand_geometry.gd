extends SceneTree
## Offline, ball-independent anatomical evidence. Run with --headless --script
## res://tools/export_shot_hand_geometry.gd -- --output=res://.godot/442-hand-geometry.json
## No animations are played and no production controller is instantiated.

const ASSET := "res://assets/Y Bot.fbx"
const MAX_CANDIDATES := 20000
var failed := false

func _initialize() -> void:
	var output := "res://.godot/442-hand-geometry.json"
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--output="):
			output = arg.trim_prefix("--output=")
	var packed: PackedScene = load(ASSET)
	if packed == null:
		_fail("Cannot load production FBX")
		quit(1)
		return
	var model := packed.instantiate()
	var skeletons: Array = []
	var meshes: Array = []
	_collect(model, skeletons, meshes)
	var skeleton_rows: Array = []
	for skeleton: Skeleton3D in skeletons:
		var bones: Array = []
		# Global rest is relative to the Skeleton3D, not world space.
		# https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html
		for bone in skeleton.get_bone_count():
			bones.append({"index": bone, "name": skeleton.get_bone_name(bone),
				"parent": skeleton.get_bone_parent(bone),
				"rest_local": _transform(skeleton.get_bone_rest(bone)),
				"rest_global_skeleton": _transform(skeleton.get_bone_global_rest(bone))})
		skeleton_rows.append({"path": str(model.get_path_to(skeleton)),
			"global_transform_model": _transform(_model_transform(skeleton)), "bones": bones})
	var mesh_rows: Array = []
	var candidate_count := 0
	for instance: MeshInstance3D in meshes:
		if instance.skin == null or instance.mesh == null:
			continue
		# https://docs.godotengine.org/en/4.7/classes/class_meshinstance3d.html#class-meshinstance3d-property-skeleton
		var skeleton := instance.get_node_or_null(instance.skeleton) as Skeleton3D
		if skeleton == null:
			_fail("Skinned mesh has no resolved Skeleton3D: %s" % instance.name)
			continue
		var row := _export_mesh(instance, skeleton, model)
		mesh_rows.append(row)
		candidate_count += row.vertices.size()
	if candidate_count == 0 or candidate_count > MAX_CANDIDATES:
		_fail("Unexpected candidate count: %d (bound %d)" % [candidate_count, MAX_CANDIDATES])
	var document := {"schema": "shot-hand-geometry-v1", "engine": Engine.get_version_info().string,
		"source_asset": ASSET, "source_sha256": FileAccess.get_sha256(ASSET),
		# https://docs.godotengine.org/en/4.7/tutorials/3d/introduction_to_3d.html
		"units": "Imported Godot 3D units (1 unit = 1 meter). Raw asset only; no production runtime rig scaling or heading rotation applied.",
		"selection": "Every surface vertex with any strictly positive LeftHand/RightHand or descendant influence; no spatial or ball filter; no sampling.",
		"coordinate_rule": "wrist_local = wrist_skin_bind_pose * mesh_rest_vertex; normal uses inverse-transpose of that basis. Transform bases are column vectors x,y,z.",
		"candidate_count": candidate_count, "skeletons": skeleton_rows, "meshes": mesh_rows}
	if not failed:
		var file := FileAccess.open(output, FileAccess.WRITE)
		if file == null:
			_fail("Cannot write %s" % output)
		else:
			file.store_string(JSON.stringify(document, "\t", false, true) + "\n")
			print("[hand-geometry] %d candidates -> %s" % [candidate_count, output])
			var summary_path := output.get_basename() + "-summary.json"
			var summary_file := FileAccess.open(summary_path, FileAccess.WRITE)
			if summary_file == null:
				_fail("Cannot write %s" % summary_path)
			else:
				summary_file.store_string(JSON.stringify(_summary(document), "\t", false, true) + "\n")
				print("[hand-geometry] anatomical summary -> %s" % summary_path)
	model.free()
	quit(1 if failed else 0)

func _export_mesh(instance: MeshInstance3D, skeleton: Skeleton3D, model: Node) -> Dictionary:
	var skin := instance.skin
	var binds: Array = []
	var wrist_binds := {}
	var wrist_bones := {}
	for side in ["Left", "Right"]:
		wrist_bones[side] = skeleton.find_bone("mixamorig_%sHand" % side)
	# Array bone indices address the Skin bind table; named binds resolve by name.
	# APIs: https://docs.godotengine.org/en/4.7/classes/class_skin.html
	# Engine mapping/composition: https://github.com/godotengine/godot/blob/4.7-stable/scene/3d/skeleton_3d.cpp
	for bind in skin.get_bind_count():
		var name := str(skin.get_bind_name(bind))
		var bone := skeleton.find_bone(name) if not name.is_empty() else skin.get_bind_bone(bind)
		if bone < 0 or bone >= skeleton.get_bone_count():
			_fail("Unresolved skin bind %d (%s)" % [bind, name])
			continue
		name = skeleton.get_bone_name(bone)
		var side := ""
		for hand in wrist_bones:
			if bone == wrist_bones[hand]:
				wrist_binds[hand] = bind
			var ancestor := bone
			while ancestor >= 0:
				if ancestor == wrist_bones[hand]:
					side = hand
					break
				ancestor = skeleton.get_bone_parent(ancestor)
		binds.append({"bind_index": bind, "bind_name": str(skin.get_bind_name(bind)),
			"bone_index": bone, "bone_name": name, "hand": side,
			"pose": _transform(skin.get_bind_pose(bind)),
			"rest_composed_bind": _transform(skeleton.get_bone_global_rest(bone) * skin.get_bind_pose(bind))})
	for side in ["Left", "Right"]:
		if not wrist_binds.has(side):
			_fail("Missing %s wrist bind" % side)
	if failed:
		return {"vertices": []}
	var vertices: Array = []
	var surfaces: Array = []
	for surface in instance.mesh.get_surface_count():
		# Documented vertex/normal arrays and 4-or-8 influence stride.
		# https://docs.godotengine.org/en/4.7/classes/class_mesh.html#enum-mesh-arraytype
		var arrays := instance.mesh.surface_get_arrays(surface)
		var positions: PackedVector3Array = arrays[Mesh.ARRAY_VERTEX]
		var normals: PackedVector3Array = arrays[Mesh.ARRAY_NORMAL]
		var indices = arrays[Mesh.ARRAY_BONES]
		var weights = arrays[Mesh.ARRAY_WEIGHTS]
		if indices == null or weights == null:
			continue
		var stride := int(indices.size() / positions.size())
		if stride not in [4, 8] or weights.size() != indices.size() or normals.size() != positions.size():
			_fail("Malformed mesh arrays on surface %d" % surface)
			continue
		var count_before := vertices.size()
		for vertex in positions.size():
			var influences: Array = []
			var sides := {}
			for slot in stride:
				var weight: float = weights[vertex * stride + slot]
				if weight <= 0.0:
					continue
				var bind := int(indices[vertex * stride + slot])
				var info: Dictionary = binds[bind]
				influences.append({"bind_index": bind, "bone_index": info.bone_index,
					"bone_name": info.bone_name, "weight": weight})
				if not info.hand.is_empty():
					sides[info.hand] = true
			if sides.is_empty():
				continue
			var wrists := {}
			for side in sides:
				var bind_pose := skin.get_bind_pose(wrist_binds[side])
				wrists[side] = {"position": _vector(bind_pose * positions[vertex]),
					"normal": _vector((bind_pose.basis.inverse().transposed() * normals[vertex]).normalized())}
			vertices.append({"surface": surface, "vertex": vertex,
				"mesh_rest_position": _vector(positions[vertex]), "mesh_rest_normal": _vector(normals[vertex]),
				"wrist_local": wrists, "influences": influences})
		surfaces.append({"index": surface, "vertex_count": positions.size(),
			"influences_per_vertex": stride, "hand_candidate_count": vertices.size() - count_before})
	return {"path": str(model.get_path_to(instance)), "skeleton_path": str(model.get_path_to(skeleton)),
		"local_transform": _transform(instance.transform), "global_transform_model": _transform(_model_transform(instance)),
		"skin_resource": skin.resource_path, "binds": binds, "wrist_bind_indices": wrist_binds,
		"surfaces": surfaces, "vertices": vertices}

func _collect(node: Node, skeletons: Array, meshes: Array) -> void:
	if node is Skeleton3D:
		skeletons.append(node)
	if node is MeshInstance3D:
		meshes.append(node)
	for child in node.get_children():
		_collect(child, skeletons, meshes)

func _summary(document: Dictionary) -> Dictionary:
	var rows: Array = []
	for mesh: Dictionary in document.meshes:
		for side in ["Left", "Right"]:
			var points: Array = []
			var wrist_dominant: Array = []
			for vertex: Dictionary in mesh.vertices:
				if not vertex.wrist_local.has(side):
					continue
				points.append(vertex.wrist_local[side].position)
				# Convenience subset only; raw export retains all influences and fingers.
				for influence: Dictionary in vertex.influences:
					if influence.bone_name == "mixamorig_%sHand" % side and influence.weight > 0.95:
						wrist_dominant.append({"surface": vertex.surface, "vertex": vertex.vertex,
							"position": vertex.wrist_local[side].position, "normal": vertex.wrist_local[side].normal,
							"influences": vertex.influences})
			if points.is_empty():
				continue
			rows.append({"mesh": mesh.path, "side": side, "count": points.size(),
				"extents_wrist_local": _extents(points), "wrist_dominant_count": wrist_dominant.size(),
				"wrist_dominant_vertices": wrist_dominant,
				"wrist_skin_bind_pose": mesh.binds[mesh.wrist_bind_indices[side]].pose})
	return {"schema": "shot-hand-geometry-summary-v1", "raw_output_schema": document.schema,
		"source_asset": document.source_asset, "source_sha256": document.source_sha256, "units": document.units,
		"selection_note": "Convenience subset is wrist weight > 0.95 only, with no position/normal/ball filter. It includes both palm and dorsum; it does not choose a contact point.",
		"hands": rows}

func _extents(points: Array) -> Dictionary:
	var minimum := Vector3(INF, INF, INF)
	var maximum := Vector3(-INF, -INF, -INF)
	for point: Array in points:
		var position := Vector3(point[0], point[1], point[2])
		minimum = minimum.min(position)
		maximum = maximum.max(position)
	return {"min": _vector(minimum), "max": _vector(maximum), "size": _vector(maximum - minimum)}

func _model_transform(node: Node) -> Transform3D:
	var result := Transform3D.IDENTITY
	var cursor := node
	while cursor != null:
		if cursor is Node3D:
			result = cursor.transform * result
		cursor = cursor.get_parent()
	return result

func _vector(value: Vector3) -> Array:
	return [value.x, value.y, value.z]

func _transform(value: Transform3D) -> Dictionary:
	return {"origin": _vector(value.origin), "basis_columns": [_vector(value.basis.x), _vector(value.basis.y), _vector(value.basis.z)]}

func _fail(message: String) -> void:
	failed = true
	push_error(message)
