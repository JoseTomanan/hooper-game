extends SceneTree
# Frozen #417 diagnostic, not a runtime modifier. Writes only .godot outputs.
# Pose is skeleton-relative; multiply by skeleton world transform:
# https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-method-get-bone-global-pose
# Vertex joint indices address Skin bindings, whose poses map mesh into bone space:
# https://docs.godotengine.org/en/4.7/classes/class_skin.html#class-skin-method-get-bind-pose
# https://docs.godotengine.org/en/4.7/classes/class_mesh.html#enum-mesh-arraytype


func tf(a):
	return Transform3D(Basis(Vector3(a[3],a[4],a[5]),Vector3(a[6],a[7],a[8]),Vector3(a[9],a[10],a[11])),Vector3(a[0],a[1],a[2]))

func _initialize():
	call_deferred("run")

func run():
	var model=load("res://assets/Y Bot.fbx").instantiate()
	root.add_child(model)
	var sk=model.find_children("*","Skeleton3D",true,false)[0]
	var meshes=model.find_children("*","MeshInstance3D",true,false)
	var samples=JSON.parse_string(FileAccess.get_file_as_string("res://.godot/417-clearance-input.json"))
	var rows=[]
	for r in samples:
		model.global_transform=tf(r.model)
		sk.reset_bone_poses()
		for name in r.poseRotations:
			var i=sk.find_bone(name)
			var q=r.poseRotations[name]
			sk.set_bone_pose_rotation(i,Quaternion(q[0],q[1],q[2],q[3]))
		var hip=sk.find_bone("mixamorig_Hips")
		var target=r.bones.mixamorig_Hips
		var local=sk.to_local(Vector3(target[0],target[1],target[2]))
		sk.set_bone_pose_position(hip,local)
		var maxerr=0.0
		for name in r.bones:
			var a=r.bones[name]
			maxerr=max(maxerr,(sk.global_transform*sk.get_bone_global_pose(sk.find_bone(name)).origin).distance_to(Vector3(a[0],a[1],a[2])))
		var packed=[]
		for mi in meshes:
			if mi.skin==null: continue
			var transforms=[]
			for j in mi.skin.get_bind_count():
				var bone=mi.skin.get_bind_bone(j)
				if bone<0: bone=sk.find_bone(mi.skin.get_bind_name(j))
				transforms.append(sk.global_transform*sk.get_bone_global_pose(bone)*mi.skin.get_bind_pose(j))
			for s in mi.mesh.get_surface_count():
				var arrays=mi.mesh.surface_get_arrays(s)
				var v=arrays[Mesh.ARRAY_VERTEX]
				var bones=arrays[Mesh.ARRAY_BONES]
				var weights=arrays[Mesh.ARRAY_WEIGHTS]
				var count=bones.size()/v.size()
				var posed=[]
				for vi in v.size():
					var p=Vector3.ZERO
					for wi in count:
						p+=(transforms[bones[vi*count+wi]]*v[vi])*weights[vi*count+wi]
					posed.append(p)
				var idx=arrays[Mesh.ARRAY_INDEX]
				for j in range(0,idx.size(),3):
					var tri=[]
					for k in range(3):
						var p=posed[idx[j+k]]
						tri.append([p.x,p.y,p.z])
					packed.append(tri)
		rows.append({"origin":r.origin,"sample":r.sample,"maxBoneError":maxerr,"triangles":packed})
	var f=FileAccess.open("res://.godot/417-clearance-meshes.json",FileAccess.WRITE)
	f.store_string(JSON.stringify(rows))
	print("CLEARANCE export samples=",rows.size())
	quit()
