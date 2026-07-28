extends SceneTree
# Read-only discriminator (companion to #285/#280's asset-build tools) — answers
# a question nothing in this repo has ever actually measured: WHICH HAND does
# the `dribbleidle` clip dribble with?
#
# Run:  godot --headless --path . -s tools/probe_dribble_hand.gd
#
# ── Why this exists ──────────────────────────────────────────────────────────
# tools/rebuild_crossover_clips.gd's header (line 84) asserts in PROSE that
# "the source is a RIGHT-hand dribble", and its own landmark derivation
# (_derive_landmarks) only ever reads mixamorig_RightHand's elevation to find
# the pump cycle. Nothing in the repo has ever actually compared the two hands
# against each other to check that prose claim. This tool is that comparison —
# a standalone, read-only probe that writes nothing and changes no asset.
#
# ── Trap 1: get_bone_global_pose() lies here ─────────────────────────────────
# A Skeleton3D instantiated but never added to the SceneTree does not
# recompute its global poses, so Skeleton3D.get_bone_global_pose() returns the
# unchanged REST transform — every sample would read identical and the probe
# would "measure" a constant 0.0000 m pump range on both hands and pass
# vacuously. (This exact bug shipped once already, per rebuild_dribble_clips.gd
# and rebuild_crossover_clips.gd's own doc comments — #285.) So every position
# here comes from manual forward kinematics (_pose_origin below), walking the
# bone chain by hand from each bone's REST transform plus whatever the clip's
# own ROTATION_3D keys override at the sampled time.
#
# ── Trap 2: the naive lateral axis is mirrored ───────────────────────────────
# `Vector3.UP.cross(forward)` points to this rig's LEFT, not its right — the
# #255 mirror bug shipped from exactly that mistake. _derive_body_axes() below
# is lifted VERBATIM from tools/rebuild_crossover_clips.gd (its rest
# hand-lateral sanity check included): body-right = forward.cross(up), and the
# function refuses to proceed (returns false) unless the REST pose actually
# puts the right hand on the positive side and the left hand on the negative
# side of the axis it derived. That check is the only thing standing between
# this probe and a silently mirrored verdict.
#
# ── Method ────────────────────────────────────────────────────────────────────
# 240 samples evenly spaced across the clip's length. At each sample, for both
# mixamorig_LeftHand and mixamorig_RightHand, compute the hand's position
# HIPS-RELATIVE (hand - hips, both from the same FK walk at the same t) and
# derive four metrics per hand:
#
#   pumpRange   = max - min of the hips-relative Y across all samples.
#                 PRIMARY discriminator: dribbling is a pumping motion, an
#                 idle hanging hand barely moves vertically.
#   meanLateral = mean of (hand - hips) . right.  Names which side a hand
#                 sits on; not part of the verdict, printed for the record.
#   meanForward = mean of (hand - hips) . forward.  Corroborator: a ball
#                 carried for dribbling is held out in FRONT of the body.
#   leverLength = mean of ||hand - shoulder||, shoulder = mixamorig_{L,R}Arm.
#                 Corroborator: a dribbling arm is bent (short lever); an idle
#                 arm hangs extended (long lever).
#
# ── Verdict rule — this probe is allowed to say "I CANNOT TELL" ──────────────
# A test that always names a winner is not evidence. So the two hard gates
# below are FAILURES (push_error + quit(1)), not soft warnings:
#
#   1. pumpRange(winner) >= MIN_PUMP_RANGE_M — it must be a genuine pumping
#      motion, not noise.
#   2. pumpRange(winner) >= RATIO_GATE * pumpRange(loser) — the winner must
#      win by a wide margin, not a coin flip.
#
# The two corroborators (meanForward, leverLength) do NOT gate the verdict —
# they only print a loud WARNING if they disagree with the pumpRange winner,
# because pumpRange is the metric that actually measures pumping motion; the
# corroborators measure a plausible side-effect of it and could legitimately
# be muddied by whatever the idle hand happens to be doing.

const LIB_PATH := "res://assets/locomotion.res"
const SKEL_FBX := "res://assets/Y Bot.fbx"
const CLIP_NAME := &"dribbleidle"

# Resolution of the sweep. 240 samples evenly spaced across [0, length) — the
# upper bound is deliberately open: dribbleidle is LOOP_LINEAR, so sampling
# exactly at `length` would just re-measure frame 0 a second time.
const SAMPLES := 240

# See "Verdict rule" above.
const MIN_PUMP_RANGE_M := 0.15
const RATIO_GATE := 3.0

const HANDS := {"Left": "mixamorig_LeftHand", "Right": "mixamorig_RightHand"}
const UPPER_ARMS := {"Left": "mixamorig_LeftArm", "Right": "mixamorig_RightArm"}
const HIPS_BONE := "mixamorig_Hips"

var _skel: Skeleton3D = null
var _right := Vector3.ZERO      # body-right, = forward x up (checked, not assumed)
var _forward := Vector3.ZERO
var _up := Vector3.UP


func _initialize() -> void:
	var lib = load(LIB_PATH)
	if lib == null or not (lib is AnimationLibrary):
		push_error("[probe-dribble-hand] failed to load AnimationLibrary at %s" % LIB_PATH)
		quit(1)
		return
	if not lib.has_animation(CLIP_NAME):
		push_error("[probe-dribble-hand] %s has no clip named '%s'" % [LIB_PATH, CLIP_NAME])
		quit(1)
		return
	var anim: Animation = lib.get_animation(CLIP_NAME)
	print("[probe-dribble-hand] clip '%s': len=%.4f tracks=%d loop=%d"
		% [CLIP_NAME, anim.length, anim.get_track_count(), anim.loop_mode])

	var packed = load(SKEL_FBX)
	if packed == null:
		push_error("[probe-dribble-hand] failed to load %s" % SKEL_FBX)
		quit(1)
		return
	var skel: Skeleton3D = _find(packed.instantiate(), "Skeleton3D")
	if skel == null:
		push_error("[probe-dribble-hand] could not find a Skeleton3D in %s" % SKEL_FBX)
		quit(1)
		return

	var result := probe_hand(anim, skel)
	if not result.get("ok", false):
		push_error("[probe-dribble-hand] %s" % String(result.get("reason", "unknown failure")))
		quit(1)
		return

	quit(0)


# Reusable entry point: probes `anim` against `skel`'s rest pose and returns a
# Dictionary of every metric plus the verdict. Self-contained — takes both
# inputs as parameters and carries no state that only _initialize() sets up,
# so a later script can call this once per clip across several clips.
#
# Return shape:
#   {"ok": bool, "reason": String (only on failure),
#    "verdict": "LEFT"|"RIGHT" (only on success), "ratio": float,
#    "metrics": {"Left": {...}, "Right": {...}}}
func probe_hand(anim: Animation, skel: Skeleton3D) -> Dictionary:
	_skel = skel
	if not _derive_body_axes():
		return {"ok": false, "reason": "could not derive/verify the body's lateral axis from Y Bot's rest pose"}

	var samples := {"Left": [], "Right": []}

	for s in SAMPLES:
		var t := (float(s) / float(SAMPLES)) * anim.length
		var hips_pos := _pose_origin(anim, t, HIPS_BONE)
		for hand in HANDS.keys():
			var hand_pos := _pose_origin(anim, t, HANDS[hand])
			var shoulder_pos := _pose_origin(anim, t, UPPER_ARMS[hand])
			var rel := hand_pos - hips_pos
			samples[hand].append({
				"y": rel.y,
				"lateral": rel.dot(_right),
				"forward": rel.dot(_forward),
				"lever": (hand_pos - shoulder_pos).length(),
			})

	var metrics := {}
	for hand in HANDS.keys():
		metrics[hand] = _summarize(samples[hand])

	var winner := "Left" if metrics["Left"]["pumpRange"] >= metrics["Right"]["pumpRange"] else "Right"
	var loser := "Right" if winner == "Left" else "Left"
	var win_range: float = metrics[winner]["pumpRange"]
	var lose_range: float = metrics[loser]["pumpRange"]
	var ratio := (win_range / lose_range) if lose_range > 0.00001 else INF

	_print_table(metrics)
	print("[probe-dribble-hand] pumpRange ratio (winner %s / loser %s) = %.2fx" % [winner, loser, ratio])

	if win_range < MIN_PUMP_RANGE_M:
		return {"ok": false, "reason": (
			"neither hand clears a genuine pumping motion: winner %s pumpRange=%.4f m (< %.2f). "
			+ "Either dribbleidle is not a pumping dribble, or the source got flattened.")
			% [winner, win_range, MIN_PUMP_RANGE_M]}

	if win_range < RATIO_GATE * lose_range:
		return {"ok": false, "reason": (
			"the answer is a coin flip: winner %s pumpRange=%.4f m is not >= %.1fx loser %s's %.4f m "
			+ "(ratio %.2fx). Refusing to name a hand.")
			% [winner, win_range, RATIO_GATE, loser, lose_range, ratio]}

	# ── Corroborators — WARN, never fail (see header) ────────────────────────
	if metrics[winner]["meanForward"] <= metrics[loser]["meanForward"]:
		print(("[probe-dribble-hand] WARNING: meanForward corroborator points at the OTHER hand "
			+ "(winner %s meanForward=%+.4f m, loser %s meanForward=%+.4f m) -- the pumpRange verdict "
			+ "stands, but the dribbling hand is not the one carried further in front.")
			% [winner, metrics[winner]["meanForward"], loser, metrics[loser]["meanForward"]])
	if metrics[winner]["leverLength"] >= metrics[loser]["leverLength"]:
		print(("[probe-dribble-hand] WARNING: leverLength corroborator points at the OTHER hand "
			+ "(winner %s lever=%.4f m, loser %s lever=%.4f m) -- expected the dribbling arm's lever to "
			+ "be SHORTER (bent), not longer or equal.")
			% [winner, metrics[winner]["leverLength"], loser, metrics[loser]["leverLength"]])

	var verdict := winner.to_upper()
	print("[probe-dribble-hand] VERDICT: %s (pumpRange L=%.4f R=%.4f, ratio %.2fx)"
		% [verdict, metrics["Left"]["pumpRange"], metrics["Right"]["pumpRange"], ratio])

	return {"ok": true, "verdict": verdict, "ratio": ratio, "metrics": metrics}


func _summarize(entries: Array) -> Dictionary:
	var hi: float = entries[0]["y"]
	var lo: float = entries[0]["y"]
	var lat_sum := 0.0
	var fwd_sum := 0.0
	var lever_sum := 0.0
	for e in entries:
		hi = maxf(hi, e["y"])
		lo = minf(lo, e["y"])
		lat_sum += e["lateral"]
		fwd_sum += e["forward"]
		lever_sum += e["lever"]
	var n := float(entries.size())
	return {
		"pumpRange": hi - lo,
		"meanLateral": lat_sum / n,
		"meanForward": fwd_sum / n,
		"leverLength": lever_sum / n,
	}


func _print_table(metrics: Dictionary) -> void:
	print("[probe-dribble-hand] metric        |       Left       |       Right")
	print("[probe-dribble-hand] pumpRange (m) |    %9.4f    |    %9.4f"
		% [metrics["Left"]["pumpRange"], metrics["Right"]["pumpRange"]])
	print("[probe-dribble-hand] meanLateral(m)|    %+9.4f    |    %+9.4f"
		% [metrics["Left"]["meanLateral"], metrics["Right"]["meanLateral"]])
	print("[probe-dribble-hand] meanForward(m)|    %+9.4f    |    %+9.4f"
		% [metrics["Left"]["meanForward"], metrics["Right"]["meanForward"]])
	print("[probe-dribble-hand] leverLength(m)|    %9.4f    |    %9.4f"
		% [metrics["Left"]["leverLength"], metrics["Right"]["leverLength"]])


# ── Body axes — LIFTED VERBATIM from tools/rebuild_crossover_clips.gd ────────
# (lines ~406-436 there) including its rest-hand-lateral sanity check. Do not
# re-derive this: `up.cross(forward)` points to this rig's LEFT, and getting
# that backwards is exactly the #255 mirror bug. body-right = forward x up.
func _derive_body_axes() -> bool:
	var foot := _skel.find_bone("mixamorig_LeftFoot")
	var toe := _skel.find_bone("mixamorig_LeftToeBase")
	if foot < 0 or toe < 0:
		push_error("[probe-dribble-hand] Y Bot has no LeftFoot/LeftToeBase to derive facing from.")
		return false
	var forward: Vector3 = _skel.get_bone_global_rest(toe).origin - _skel.get_bone_global_rest(foot).origin
	forward.y = 0.0
	if forward.length() < 0.001:
		push_error("[probe-dribble-hand] Y Bot's foot->toe vector is vertical; cannot derive facing.")
		return false
	_forward = forward.normalized()
	_right = _forward.cross(_up).normalized()

	# The check: the RIGHT hand's rest origin must lie on the positive side of
	# the derived right axis, and the LEFT hand's on the negative side.
	var lh := _skel.find_bone("mixamorig_LeftHand")
	var rh := _skel.find_bone("mixamorig_RightHand")
	if lh < 0 or rh < 0:
		push_error("[probe-dribble-hand] Y Bot has no LeftHand/RightHand to verify the lateral axis.")
		return false
	var lh_lat: float = _skel.get_bone_global_rest(lh).origin.dot(_right)
	var rh_lat: float = _skel.get_bone_global_rest(rh).origin.dot(_right)
	print("[probe-dribble-hand] axes: forward=%s right=%s | rest hand lateral L=%+.4f R=%+.4f"
		% [_forward, _right, lh_lat, rh_lat])
	if not (rh_lat > 0.0 and lh_lat < 0.0):
		push_error("[probe-dribble-hand] the derived right axis puts the RIGHT hand at %+.4f and the LEFT "
			% rh_lat + "at %+.4f -- it is inverted (or the rig's bone names lie). Every sign in this "
			% lh_lat + "probe depends on this axis; refusing to measure.")
		return false
	return true


# ── Manual forward kinematics — same reasoning as rebuild_crossover_clips.gd's
# _pose_origin: Skeleton3D.get_bone_global_pose() returns the unchanged REST
# transform for a skeleton that was never added to the SceneTree, so this
# walks the bone chain by hand from rest + the clip's own keys instead
# (Trap 1). Samples at an arbitrary time `t` via rotation_track_interpolate,
# so it works for any sample point, not just a key index.
#
# ── Fix (2026-07-28): honor TYPE_POSITION_3D (and, if ever present,
# TYPE_SCALE_3D) tracks, not just TYPE_ROTATION_3D ─────────────────────────
# Mixamo clips key mixamorig_Hips' TRANSLATION, not just its rotation (a real
# dribble has a lateral weight shift / crouch on the root bone). The old
# version of this FK silently substituted rest.origin for EVERY bone's
# translation, including Hips, so that weight-shift was invisible to any
# ABSOLUTE-frame reading. It happens NOT to matter for this probe's own
# hips-relative verdict (`hand - hips` cancels a shared-ancestor translation
# term algebraically, whether that term is right or wrong) — confirmed by
# re-running after this fix and finding the verdict byte-for-byte unchanged
# (see the tools/measure_dribble_hand_offset.gd task that motivated this fix
# for the full derivation) — but leaving the bug in here too would be a trap
# for the next person who copies this FK for an absolute-frame use.
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
		# POSITION_3D keys are absolute LOCAL translations -- an animated bone
		# REPLACES rest.origin, same logic as the rotation case below.
		var origin: Vector3 = rest.origin
		if pos_track_of.has(b):
			origin = anim.position_track_interpolate(pos_track_of[b], t)
		# ROTATION_3D keys are absolute LOCAL rotations, so an animated bone
		# REPLACES the rest basis' rotation; scale carries over from rest
		# unless a SCALE_3D track overrides it too.
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
