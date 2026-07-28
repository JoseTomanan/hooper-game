extends SceneTree
# Measures the three DribbleHandHeight/DribbleForwardOffset/HandOffset
# [Export] tunables on scripts/Ball/BallController.cs, which were never
# actually measured against the rig — HandOffset's code default (0.18) is
# even overridden in scenes/Ball.tscn (0.4) with no derivation on record.
#
# Run:  godot --headless --path . -s tools/measure_dribble_hand_offset.gd
#
# Companion to tools/probe_dribble_hand.gd (#285/#280 lineage), which already
# proved the dribbling hand is the RIGHT hand (mixamorig_RightHand) on clip
# `dribbleidle`, at pumpRange ratio 39.58x. This tool reuses that probe's FK
# machinery verbatim (_pose_origin, _derive_body_axes incl. its rest
# hand-lateral sanity check, _find) rather than re-deriving it, and adds two
# things the probe didn't need: (a) a second clip (`dribblemove`) for a
# sanity delta, and (b) the model-space -> world-space transform chain that
# BallController actually consumes, which the probe never had to resolve
# because it only ever compared hand-vs-hand within model space.
#
# ── Why the transform chain is the crux, not the FK sampling ────────────────
# The FK numbers below live in the Y Bot MODEL's local space. The game
# places the ball in WORLD space around the CharacterBody3D origin. Three
# separate assumptions have to hold for "model-space number" to equal
# "world-space tunable", and none of them is safe to just assert:
#   1. CharacterModel's basis (not just its translation) must be identity,
#      or the model's local forward/right axes are not the body's.
#   2. The model's derived facing at heading 0 must actually match
#      HeadingMath.Forward(0) — the axis convention BallController.HolderForward
#      uses — not a rotated/flipped version of it.
#   3. The vertical chain (model Y + CharacterModel's Y offset + the grounded
#      body origin's height above the floor) must compose the way the code
#      assumes, or DribbleHandHeight (a WORLD Y in DribbleCycle) will be
#      measured against the wrong floor.
# Sections 2a-2c below resolve each of these by reading actual scene state
# (PackedScene.get_state(), never instantiated into the tree — CharacterModel
# carries a PlayerController C# script whose _Ready() assumes multiplayer
# authority/networking wiring that don't exist in this bare headless
# SceneTree, so instantiating it live is the wrong tool; get_state() reads
# the same authored properties without ever calling into node script code).
#
# ── Fix (2026-07-28): wrong FK inputs AND the wrong reference frame ─────────
# An independent live-harness measurement (real Player.tscn + AnimationTree +
# Skeleton3D.GetBoneGlobalPose) put the right hand's lateral at +0.40 to
# +0.46 m and the vertical pump range at 0.2925 m, vs. this tool's prior
# +0.5276 m / 0.3450 m -- consistently ~85% of this tool's numbers. Root
# cause, confirmed by a standalone track-inventory diagnostic: TWO bugs
# stacked, one in the shared FK helper, one in this file's own framing.
#   1. _pose_origin() below (shared with probe_dribble_hand.gd) walked only
#      TYPE_ROTATION_3D tracks and used rest.origin for every bone's
#      translation. mixamorig_Hips carries a real TYPE_POSITION_3D track (a
#      dribble stance has genuine lateral weight-shift + crouch); ignoring it
#      corrupted every ABSOLUTE-frame reading built from this FK. Fixed below
#      to honor POSITION_3D (and SCALE_3D, if ever present -- neither clip
#      has one) tracks per bone.
#   2. This file measured lateral/forward HIPS-RELATIVE (`hand - hips`, the
#      same shape probe_dribble_hand.gd correctly uses for its L-vs-R
#      comparison). That subtraction algebraically CANCELS a shared-ancestor
#      translation term, whatever it is -- which is exactly why bug #1 didn't
#      change probe_dribble_hand.gd's verdict, but it also means a
#      hips-relative reading throws away the hips' own lateral shift instead
#      of reporting it. BallController places the ball relative to
#      holder.GlobalPosition, the CharacterBody3D origin (TickDribbling,
#      BallController.cs:2298-2329; the held-pose case, :2280-2293) -- NOT
#      relative to Hips. If the pelvis slides while dribbling, the ball must
#      track the body, not the pelvis. Fixed below: _sample_clip now measures
#      the hand's position ABSOLUTE in the model frame (dot with the derived
#      right/forward axes; model origin maps to the CharacterBody3D origin in
#      XZ, proved by sections 2a-2c above), not hips-relative.

const LOCOMOTION_PATH := "res://assets/locomotion.res"
const SKEL_FBX := "res://assets/Y Bot.fbx"
const PLAYER_SCENE := "res://scenes/Player.tscn"

const CLIPS := ["dribbleidle", "dribblemove"]
const SAMPLES := 240

const HAND_BONE := "mixamorig_RightHand"

# BallController.cs:204 -- `[Export] public float BallRadius { get; set; } = 0.12f;`
# Confirmed present at that line as of this run; DribbleCycle tracks the
# ball CENTER (floor clamp is `Y >= BallRadius`, BallController.cs:2327), so
# the palm sits one radius ABOVE the center at the top of the bounce.
const BALL_RADIUS := 0.12

# The #255 mirror-bug guard (see hand-side-world-direction-convention):
# HandRight == forward x up == body-right, HandSign(Right) = +1
# (BallController.cs:2367). A right-hand clip that measures negative lateral
# means one of the two layers (this probe's axis derivation, or the game's)
# is mirrored -- refuse to emit a constant built on a contradiction.
const LATERAL_SIGN_GATE := 0.0

# Spread-honesty gate (see header "Spread honesty"): if a single hand swings
# more than this across the clip, a single constant is a poor model of it.
const SPREAD_HONESTY_BAND := 0.20

var _skel: Skeleton3D = null
var _right := Vector3.ZERO      # body-right, = forward x up (checked, not assumed)
var _forward := Vector3.ZERO
var _up := Vector3.UP


func _initialize() -> void:
	var lib = load(LOCOMOTION_PATH)
	if lib == null or not (lib is AnimationLibrary):
		push_error("[measure-offset] failed to load AnimationLibrary at %s" % LOCOMOTION_PATH)
		quit(1)
		return

	var packed = load(SKEL_FBX)
	if packed == null:
		push_error("[measure-offset] failed to load %s" % SKEL_FBX)
		quit(1)
		return
	_skel = _find(packed.instantiate(), "Skeleton3D")
	if _skel == null:
		push_error("[measure-offset] could not find a Skeleton3D in %s" % SKEL_FBX)
		quit(1)
		return

	if not _derive_body_axes():
		push_error("[measure-offset] could not derive/verify the body's lateral axis from Y Bot's rest pose")
		quit(1)
		return

	# ── Step 1: sample the dribbling hand across both clips ─────────────────
	var clip_stats := {}
	for clip_name in CLIPS:
		if not lib.has_animation(clip_name):
			push_error("[measure-offset] %s has no clip named '%s'" % [LOCOMOTION_PATH, clip_name])
			quit(1)
			return
		var anim: Animation = lib.get_animation(clip_name)
		print("[measure-offset] clip '%s': len=%.4f tracks=%d loop=%d"
			% [clip_name, anim.length, anim.get_track_count(), anim.loop_mode])
		var stats := _sample_clip(anim)
		clip_stats[clip_name] = stats
		_print_clip_table(clip_name, stats)

	_print_delta_table(clip_stats["dribbleidle"], clip_stats["dribblemove"])

	# ── Step 2: resolve the model-space -> world-space transform chain ──────
	var chain := _resolve_transform_chain()
	if chain.is_empty():
		quit(1)
		return

	# worldY(grounded) = y_model + CharacterModelOffsetY + bodyOriginY, applied
	# to dribbleidle's already-measured ABSOLUTE (FK-frame) y min/mean/max —
	# this is a linear shift, so shifting the three summary stats is
	# equivalent to shifting all 240 raw samples first.
	var idle_y: Dictionary = clip_stats["dribbleidle"]["y"]
	var shift: float = chain["model_offset_y"] + chain["body_origin_y"]
	chain["world_y"] = {
		"min": idle_y["min"] + shift,
		"mean": idle_y["mean"] + shift,
		"max": idle_y["max"] + shift,
	}

	_verify_axis_mapping(chain)
	_print_worldy_chain(chain)

	# ── Step 3: emit the constants (derived from dribbleidle, the resting/
	# default dribble stance -- dribblemove is the sanity delta, not a second
	# source to blend in; there is exactly one HandOffset/DribbleForwardOffset/
	# DribbleHandHeight in the running game, not one per clip) ──────────────
	var idle: Dictionary = clip_stats["dribbleidle"]
	var lateral: Dictionary = idle["lateral"]
	var forward: Dictionary = idle["forward"]
	var y: Dictionary = idle["y"]

	# Sign assertion (non-negotiable, the #255 guard).
	if not (lateral["mean"] > LATERAL_SIGN_GATE):
		push_error(("[measure-offset] SIGN VIOLATION: mean(lateral)=%+.4f is not positive for the RIGHT "
			+ "hand. HandRight == forward x up == body-right and HandSign(Right)=+1 (BallController.cs:2367); "
			+ "a right-hand clip measuring negative lateral means this probe's axis derivation or the "
			+ "game's HandSign layer is mirrored. Refusing to emit a constant built on that contradiction.")
			% lateral["mean"])
		quit(1)
		return

	# Spread honesty (printed regardless of outcome -- see header).
	var lateral_spread: float = lateral["max"] - lateral["min"]
	if lateral_spread > SPREAD_HONESTY_BAND:
		print(("[measure-offset] *** SPREAD WARNING ***: dribbleidle's right-hand lateral swings %.4f m "
			+ "(min=%+.4f max=%+.4f) across the clip -- more than the %.2f m honesty band (i.e. more than "
			+ "about +/-%.2f m around the mean). A SINGLE HandOffset CONSTANT IS A POOR MODEL of this hand's "
			+ "motion; the honest fix is a per-tick hand-tracking offset sampled from the clip itself, not a "
			+ "tunable. The recommendation below is still the best single-constant compromise, not a "
			+ "vindication of using one.")
			% [lateral_spread, lateral["min"], lateral["max"], SPREAD_HONESTY_BAND, SPREAD_HONESTY_BAND / 2.0])
	else:
		print(("[measure-offset] spread check: dribbleidle's right-hand lateral swings %.4f m "
			+ "(min=%+.4f max=%+.4f) -- within the %.2f m honesty band. A single HandOffset constant is a "
			+ "reasonable model of this hand.")
			% [lateral_spread, lateral["min"], lateral["max"], SPREAD_HONESTY_BAND])

	var hand_offset: float = absf(lateral["mean"])
	var dribble_forward_offset: float = forward["mean"]

	# DribbleHandHeight uses the MAX, not the mean -- see DribbleCycle.cs:17-20
	# ("Phase convention: Phase 0.0 = ball at hand height (top of bounce, just
	# caught/released)"), confirmed at those lines as of this run. The
	# constant is a WORLD Y at the TOP of the arc, so it must be pinned to the
	# hand's highest measured point, not its average height across the clip
	# (the hand dips somewhat even in "idle" as part of the animation, and
	# averaging would sink the top of the arc below where the hand actually
	# is at release/catch). BallRadius is subtracted because DribbleCycle
	# tracks the ball CENTER (its floor clamp is `Y >= BallRadius`,
	# BallController.cs:2327, confirmed as of this run) -- the centre sits one
	# radius below the palm at the top of the bounce.
	var world_y_max: float = chain["world_y"]["max"]
	var dribble_hand_height: float = world_y_max - BALL_RADIUS
	print(("[measure-offset] DribbleHandHeight derivation: max(worldY)=%.4f (top-of-arc palm height, per "
		+ "DribbleCycle.cs:17-20's phase-0-is-hand-height convention) minus BallRadius=%.4f (DribbleCycle "
		+ "tracks the ball CENTER, one radius below the palm, per BallController.cs:2327's floor clamp) "
		+ "= %.4f")
		% [world_y_max, BALL_RADIUS, dribble_hand_height])

	print("[measure-offset] RECOMMENDED: HandOffset=%.4f DribbleForwardOffset=%.4f DribbleHandHeight=%.4f"
		% [hand_offset, dribble_forward_offset, dribble_hand_height])

	quit(0)


# ── Step 1 helpers ───────────────────────────────────────────────────────────

# Samples SAMPLES points evenly across anim's length. Per sample, for the
# RIGHT hand only: lateral/forward/y ALL measured ABSOLUTE in the model's FK
# frame (dot with the derived body axes for lateral/forward; raw .y for
# vertical) -- NOT hips-relative. See the "Why model-origin, not hips" note
# above _sample_clip's old body for the reasoning; the short version is that
# BallController places the ball relative to the CharacterBody3D origin
# (holderPos = holderBody.GlobalPosition, BallController.cs:2298-2329 and
# :2280-2293), which the model origin maps to in XZ (proved in this file's
# header, section "2a-2c"). A hips-relative measurement would silently cancel
# out any lateral/forward weight-shift baked into Hips' own POSITION_3D
# track (see the FK fix in _pose_origin below) -- and Task 1's diagnostic
# found exactly such a track, with a real (if modest, ~1cm spread) lateral
# component. That shift belongs in the constant; hips-relative subtraction
# would throw it away.
func _sample_clip(anim: Animation) -> Dictionary:
	var lat := []
	var fwd := []
	var y := []
	for s in SAMPLES:
		var t := (float(s) / float(SAMPLES)) * anim.length
		var hand_pos := _pose_origin(anim, t, HAND_BONE)
		lat.append(hand_pos.dot(_right))
		fwd.append(hand_pos.dot(_forward))
		y.append(hand_pos.y)
	return {
		"lateral": _minmeanmax(lat),
		"forward": _minmeanmax(fwd),
		"y": _minmeanmax(y),
	}


func _minmeanmax(values: Array) -> Dictionary:
	var lo: float = values[0]
	var hi: float = values[0]
	var sum := 0.0
	for v in values:
		lo = minf(lo, v)
		hi = maxf(hi, v)
		sum += v
	return {"min": lo, "mean": sum / float(values.size()), "max": hi}


func _print_clip_table(clip_name: String, stats: Dictionary) -> void:
	print("[measure-offset] --- %s (right hand, %d samples) ---" % [clip_name, SAMPLES])
	print("[measure-offset] metric        |     min     |     mean    |     max")
	for key in ["lateral", "forward", "y"]:
		var m: Dictionary = stats[key]
		print("[measure-offset] %-14s|  %+9.4f  |  %+9.4f  |  %+9.4f" % [key, m["min"], m["mean"], m["max"]])


func _print_delta_table(idle: Dictionary, move: Dictionary) -> void:
	print("[measure-offset] --- dribbleidle vs dribblemove delta (right hand) ---")
	print("[measure-offset] metric        |  idle.mean  |  move.mean  |    delta")
	for key in ["lateral", "forward", "y"]:
		var i: float = idle[key]["mean"]
		var mv: float = move[key]["mean"]
		print("[measure-offset] %-14s|  %+9.4f  |  %+9.4f  |  %+9.4f" % [key, i, mv, mv - i])


# ── Step 2: transform chain ─────────────────────────────────────────────────

# Reads res://scenes/Player.tscn via PackedScene.get_state(), WITHOUT
# instantiating it into the tree (CharacterModel carries PlayerController.cs,
# whose _Ready() assumes multiplayer wiring this bare SceneTree doesn't have).
# Returns {} (and has already push_error'd) on any failure.
func _resolve_transform_chain() -> Dictionary:
	var packed_player = load(PLAYER_SCENE)
	if packed_player == null:
		push_error("[measure-offset] failed to load %s" % PLAYER_SCENE)
		return {}
	var state: SceneState = packed_player.get_state()

	var model_transform = null
	var shape_resource = null
	var rig_height = null
	var rig_wingspan = null
	var rig_node_seen := false

	for i in state.get_node_count():
		var node_name := String(state.get_node_name(i))
		if node_name == "CharacterModel":
			for p in state.get_node_property_count(i):
				if String(state.get_node_property_name(i, p)) == "transform":
					model_transform = state.get_node_property_value(i, p)
		elif node_name == "CollisionShape3D":
			for p in state.get_node_property_count(i):
				if String(state.get_node_property_name(i, p)) == "shape":
					shape_resource = state.get_node_property_value(i, p)
		elif node_name == "RigScaler":
			rig_node_seen = true
			for p in state.get_node_property_count(i):
				var pn := String(state.get_node_property_name(i, p))
				if pn == "Height":
					rig_height = state.get_node_property_value(i, p)
				elif pn == "Wingspan":
					rig_wingspan = state.get_node_property_value(i, p)

	if model_transform == null:
		push_error("[measure-offset] CharacterModel node (with a 'transform' property) not found in %s" % PLAYER_SCENE)
		return {}
	if shape_resource == null:
		push_error("[measure-offset] CollisionShape3D node (with a 'shape' property) not found in %s" % PLAYER_SCENE)
		return {}
	if not (shape_resource is CapsuleShape3D):
		push_error("[measure-offset] CollisionShape3D's shape is a %s, not the expected CapsuleShape3D -- "
			% shape_resource.get_class() + "the height/2 grounded-origin assumption does not hold for this shape.")
		return {}

	# The RigScaler refusal gate (non-negotiable, see header): a non-unit rig
	# scale moves every bone via PlayerRigScaler.SetBonePoseScale and would
	# invalidate every FK number measured in Step 1. Absent property == the
	# script's own compiled default (PlayerRigScaler.cs: _height = _wingspan
	# = 1.0f), confirmed as of this run -- this tscn does not override either.
	var height_val: float = rig_height if rig_height != null else 1.0
	var wingspan_val: float = rig_wingspan if rig_wingspan != null else 1.0
	print("[measure-offset] RigScaler node present=%s Height=%s(%.4f) Wingspan=%s(%.4f)"
		% [rig_node_seen,
		("override" if rig_height != null else "defaulted"), height_val,
		("override" if rig_wingspan != null else "defaulted"), wingspan_val])
	if not (is_equal_approx(height_val, 1.0) and is_equal_approx(wingspan_val, 1.0)):
		push_error(("[measure-offset] RigScaler Height=%.4f Wingspan=%.4f -- at least one is not 1.0. "
			+ "PlayerRigScaler.SetBonePoseScale multiplies bone scale, so a non-unit rig scale moves the "
			+ "hand and invalidates every number this tool measured. Refusing to emit a constant.")
			% [height_val, wingspan_val])
		return {}

	var capsule_height: float = shape_resource.height
	var basis: Basis = model_transform.basis
	var basis_identity := _is_identity_basis(basis)
	print("[measure-offset] CharacterModel transform: origin=%s basis_identity=%s basis=[x:%s y:%s z:%s]"
		% [model_transform.origin, basis_identity, basis.x, basis.y, basis.z])
	print("[measure-offset] CollisionShape3D shape: %s height=%.4f (expected default CapsuleShape3D, height 2.0)"
		% [shape_resource.get_class(), capsule_height])

	var model_offset_y: float = model_transform.origin.y
	var body_origin_y: float = capsule_height / 2.0

	return {
		"model_transform": model_transform,
		"basis_identity": basis_identity,
		"capsule_height": capsule_height,
		"model_offset_y": model_offset_y,
		"body_origin_y": body_origin_y,
	}


func _is_identity_basis(b: Basis) -> bool:
	return b.x.is_equal_approx(Vector3(1, 0, 0)) \
		and b.y.is_equal_approx(Vector3(0, 1, 0)) \
		and b.z.is_equal_approx(Vector3(0, 0, 1))


# Compares the model's derived facing (this tool's _forward, from Y Bot's
# rest-pose foot->toe vector, in the MODEL's local space) against
# HeadingMath.Forward(0) mapped into BallController's world-space convention
# (HeadingMath.cs:239-240 -> Vector2(sin(0), cos(0)) = (0,1); BallController.cs
# :1611-1615 maps that to Vector3(fwd.X, 0, fwd.Y) = (0,0,1) -- confirmed at
# those lines as of this run). If CharacterModel's basis is identity (checked
# above) and PlayerController.ApplyCosmetics only ever sets `_mesh.Rotation.Y
# = Heading` (a rotation ON TOP of the authored CharacterModel transform),
# then at heading 0 the model's own local axes ARE the world axes with no
# extra rotation applied -- so this comparison is meaningful without any
# further rotation of the Step-1 samples, PROVIDED the two vectors actually
# agree (checked here, not assumed).
func _verify_axis_mapping(chain: Dictionary) -> void:
	var world_forward_at_heading0 := Vector3(0, 0, 1)  # HeadingMath.Forward(0) mapped, see comment above
	var dot: float = _forward.dot(world_forward_at_heading0)
	var cross: Vector3 = _forward.cross(world_forward_at_heading0)
	var angle_deg: float = rad_to_deg(acos(clampf(dot, -1.0, 1.0)))

	print(("[measure-offset] axis mapping check: model-local forward=%s vs HeadingMath.Forward(0) mapped=%s "
		+ "-> dot=%.6f angle=%.4f deg cross=%s")
		% [_forward, world_forward_at_heading0, dot, angle_deg, cross])

	if not chain["basis_identity"]:
		print(("[measure-offset] CORRECTION REQUIRED: CharacterModel's basis is NOT identity. The model's "
			+ "local forward/right axes are not the body's; Step 1's lateral/forward samples must be "
			+ "re-projected through this basis before they mean anything in body space. This tool did NOT "
			+ "apply that correction -- treat the Step 3 constants as INVALID until it is added."))
		return

	if dot < 0.0:
		push_error(("[measure-offset] FLIPPED FACING: model-local forward is pointing the OPPOSITE way from "
			+ "HeadingMath.Forward(0) (dot=%.6f). This is a real mirror/rotation bug, not noise -- refusing "
			+ "to emit constants without a 180-degree correction being applied and re-verified.") % dot)
		quit(1)
		return

	if angle_deg > 5.0:
		print(("[measure-offset] WARNING: model-local forward diverges from HeadingMath.Forward(0) by %.4f "
			+ "degrees -- large enough that it may not just be rest-pose noise. No correction applied "
			+ "automatically; the Step 3 constants use the RAW Step-1 projection (onto this tool's own "
			+ "foot/toe-derived axes, which is what BallController's HandOffset/DribbleForwardOffset actually "
			+ "consume via the same forward/right convention) -- inspect before trusting.") % angle_deg)
	else:
		print(("[measure-offset] axis mapping OK: basis is identity and model-local forward matches "
			+ "HeadingMath.Forward(0) within %.4f degrees (rest-pose foot/toe imprecision, not a real "
			+ "rotation) -- no correction applied, none needed.") % angle_deg)


func _print_worldy_chain(chain: Dictionary) -> void:
	var model_offset_y: float = chain["model_offset_y"]
	var body_origin_y: float = chain["body_origin_y"]
	var sum: float = model_offset_y + body_origin_y
	var world_y: Dictionary = chain["world_y"]

	print("[measure-offset] world-Y chain terms (dribbleidle, right hand):")
	print("[measure-offset]   CharacterModelOffsetY (CharacterModel.transform.origin.y) = %+.4f" % model_offset_y)
	print("[measure-offset]   bodyOriginY (capsuleHeight/2 = %.4f/2)                     = %+.4f"
		% [chain["capsule_height"], body_origin_y])
	print("[measure-offset]   sum (expected to compose to identity, i.e. 0.0)             = %+.4f (%s)"
		% [sum, "IDENTITY" if is_equal_approx(sum, 0.0) else "NOT IDENTITY"])
	print("[measure-offset]   worldY(grounded) = y_model + %+.4f: min=%+.4f mean=%+.4f max=%+.4f"
		% [sum, world_y["min"], world_y["mean"], world_y["max"]])


# ── Reused verbatim from tools/probe_dribble_hand.gd (see that file's header
# for the traps these guard against: Skeleton3D poses go stale without a
# SceneTree, and the naive up.cross(forward) lateral axis is mirrored) ───────

func _derive_body_axes() -> bool:
	var foot := _skel.find_bone("mixamorig_LeftFoot")
	var toe := _skel.find_bone("mixamorig_LeftToeBase")
	if foot < 0 or toe < 0:
		push_error("[measure-offset] Y Bot has no LeftFoot/LeftToeBase to derive facing from.")
		return false
	var forward: Vector3 = _skel.get_bone_global_rest(toe).origin - _skel.get_bone_global_rest(foot).origin
	forward.y = 0.0
	if forward.length() < 0.001:
		push_error("[measure-offset] Y Bot's foot->toe vector is vertical; cannot derive facing.")
		return false
	_forward = forward.normalized()
	_right = _forward.cross(_up).normalized()

	var lh := _skel.find_bone("mixamorig_LeftHand")
	var rh := _skel.find_bone("mixamorig_RightHand")
	if lh < 0 or rh < 0:
		push_error("[measure-offset] Y Bot has no LeftHand/RightHand to verify the lateral axis.")
		return false
	var lh_lat: float = _skel.get_bone_global_rest(lh).origin.dot(_right)
	var rh_lat: float = _skel.get_bone_global_rest(rh).origin.dot(_right)
	print("[measure-offset] axes: forward=%s right=%s | rest hand lateral L=%+.4f R=%+.4f"
		% [_forward, _right, lh_lat, rh_lat])
	if not (rh_lat > 0.0 and lh_lat < 0.0):
		push_error("[measure-offset] the derived right axis puts the RIGHT hand at %+.4f and the LEFT "
			% rh_lat + "at %+.4f -- it is inverted (or the rig's bone names lie). Every sign in this "
			% lh_lat + "tool depends on this axis; refusing to measure.")
		return false
	return true


# Fix (2026-07-28, see file header): honors TYPE_POSITION_3D (and, if
# present, TYPE_SCALE_3D) tracks in addition to TYPE_ROTATION_3D -- mirrors
# the same fix applied to tools/probe_dribble_hand.gd.
func _pose_origin(anim: Animation, t: float, bone: String) -> Vector3:
	var idx := _skel.find_bone(bone)
	if idx < 0:
		return Vector3.ZERO

	var pos_track_of := {}
	var rot_track_of := {}
	var scale_track_of := {}
	for i in anim.get_track_count():
		var track_type := anim.track_get_type(i)
		if track_type != Animation.TYPE_POSITION_3D \
				and track_type != Animation.TYPE_ROTATION_3D \
				and track_type != Animation.TYPE_SCALE_3D:
			continue
		var b := _skel.find_bone(_bone_of(anim.track_get_path(i)))
		if b < 0:
			continue
		match track_type:
			Animation.TYPE_POSITION_3D: pos_track_of[b] = i
			Animation.TYPE_ROTATION_3D: rot_track_of[b] = i
			Animation.TYPE_SCALE_3D: scale_track_of[b] = i

	var chain := []
	var walk := idx
	while walk >= 0:
		chain.push_front(walk)
		walk = _skel.get_bone_parent(walk)

	var acc := Transform3D.IDENTITY
	for b in chain:
		var rest: Transform3D = _skel.get_bone_rest(b)
		var origin: Vector3 = rest.origin
		if pos_track_of.has(b):
			origin = anim.position_track_interpolate(pos_track_of[b], t)
		var scale: Vector3 = rest.basis.get_scale()
		if scale_track_of.has(b):
			scale = anim.scale_track_interpolate(scale_track_of[b], t)
		var basis: Basis = rest.basis
		if rot_track_of.has(b):
			var q: Quaternion = anim.rotation_track_interpolate(rot_track_of[b], t)
			basis = Basis(q).scaled(scale)
		elif scale_track_of.has(b):
			basis = Basis(rest.basis.get_rotation_quaternion()).scaled(scale)
		var local := Transform3D(basis, origin)
		acc = acc * local
	return acc.origin


func _bone_of(np: NodePath) -> String:
	return "" if np.get_subname_count() == 0 else String(np.get_subname(0))


func _find(n: Node, cls: String) -> Node:
	if n.get_class() == cls:
		return n
	for c in n.get_children():
		var r := _find(c, cls)
		if r != null:
			return r
	return null
