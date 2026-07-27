extends SceneTree
# Asset build tool (#280) — drafts the crossover clip family into
# assets/locomotion.res by slicing assets/Dribble.fbx and composing a signed
# cross-body swing onto each slice.
#
# Run:  godot --headless --path . -s tools/rebuild_crossover_clips.gd
# Idempotent: re-running re-derives all six clips from the pristine FBX rather
# than stacking edits (the previous build is removed before the new one lands).
#
# Produces six LOOP_NONE one-shots — three phases x two hand-side polarities:
#   crossoverstartupleft   /  ...right    6 ticks / 0.1000 s
#   crossoveractiveleft    /  ...right    3 ticks / 0.0500 s
#   crossoverrecoveryleft  /  ...right   12 ticks / 0.2000 s
#
# The LEFT/RIGHT suffix names the hand the ball STARTED in, which is also what
# scenes/Player.tscn's state names and MoveAnimResolver's suffix mean. "Left"
# therefore crosses the ball toward the body's RIGHT, and vice versa. Getting
# that backwards is the entire failure mode this file's geometric proofs exist
# to catch — see "The proofs" below.
#
# ── Why this is a temp-draft, not a retarget ─────────────────────────────────
# #280's original body said "retarget the sourced crossover FBX". There is no
# crossover FBX: the #278 Mixamo sitting found only 3 of 9 requested clips and
# #276's "Sourcing outcome (2026-07-25)" re-scoped this move to a temp-draft
# (re-confirmed in #280's own comment). So spike 0012's retarget block does not
# apply — no bone_map, no custom SkeletonProfile, no fix_silhouette, and no
# "match count > 0" check. #276's temp-draft verification variant replaces it.
#
# ── Why a COMPOSED SWING and not a mirrored clip ─────────────────────────────
# #280's brief specifies the mirror as an "L<->R bone-track swap + X negation".
# That recipe is underspecified and, taken literally, wrong:
#
#   * Negating a quaternion's x alone is not a reflection. Mirroring a rotation
#     about the sagittal plane is (x, -y, -z, w); which component set is correct
#     depends on each bone's local frame, because ROTATION_3D keys are
#     PARENT-RELATIVE local rotations, not world orientations.
#   * A bone-name swap assumes every L/R pair has a mirror-symmetric rest frame.
#     Measured here that is nearly true (rest hand lateral L=+0.7392 R=-0.7359,
#     0.4% asymmetry) but "nearly" is not a foundation for a silent transform.
#   * It omits POSITION_3D. Dribble.fbx carries exactly one (mixamorig_Hips, 39
#     keys). A mirror that negates rotations but not that track leaves BOTH
#     polarities drifting laterally the SAME way — a false telegraph that no
#     state-name assertion can see.
#
# So both polarities are generated from the same source by running one
# synthesis with the opposite SIGN, rather than reflecting a baked clip into
# its twin. That removes the quaternion-reflection question and the
# rest-symmetry assumption outright, and it follows #279's own precedent for
# the fadeaway: compose a delta onto a real full-body clip, then prove
# geometrically that the result moved the right way.
#
# ── Why slicing a full-body clip is mandatory (the a45bd1d trap) ─────────────
# A single-clip AnimationTree state plays at FULL WEIGHT, and Godot's
# AnimationMixer writes every bone the active clip does NOT track to the
# skeleton's rest transform. A hand-keyed pose touching only the ball arm would
# therefore reset everything else the instant the state was entered. That
# shipped once as the "turning T-pose" bug (a45bd1d), where `pivot` tracked only
# 4 plant bones and a turn snapped the arms horizontal.
#
# Slicing sidesteps it structurally: Dribble.fbx carries 53 tracks (52 rotation
# + 1 position) covering the whole body, every one resolving on Y Bot with zero
# unresolved bones (probed headlessly, Godot 4.7.1). Every slice inherits that
# coverage verbatim and the swing only REWRITES existing keys, never drops a
# track. _assert_complete() pins it anyway.
#
# One caveat worth knowing before trusting a visual check: scripts/Player/
# BlendRestAnchor.cs re-anchors mixamorig_{Left,Right}UpLeg's REST to `idle`'s
# first key (#287). So a clip that lost those two tracks would pose into idle's
# crouch rather than a T-pose — subtler than the bug the "you'd see a T-pose"
# intuition is tuned for. The track-count assertion below, not a visual check,
# is what actually guards this.
#
# ── Why the rotation-family mismatch is harmless here ────────────────────────
# locomotion.res holds clips from two rotation representations 155-180 deg apart
# (Kenney-retargeted idle/run/pivot vs stock-Mixamo catch/dribble) — see
# rebuild_dribble_clips.gd's header for the measured table. That gap only bites
# at PARTIAL blend weight, because ROTATION_3D tracks are ABSOLUTE local
# rotations and a full-weight single-clip state ignores rest entirely. All six
# clips here feed single-clip per-move states whose transitions are hard cuts
# (xfade_time unset), so they are in the safe column. They are stock-Mixamo
# family anyway, same as their `Dribble` source.
#
# ── Known temp-draft fidelity gap (deferred to #173, ADR-0021) ───────────────
# The source is a RIGHT-hand dribble, and both polarities are built from it. So
# during a LEFT-origin crossover's Startup the wind-up shows the right hand
# pumping. This is a fidelity gap, NOT the false-telegraph failure #280 exists
# to prevent: the DIRECTION of the cross is provably correct on both polarities
# (see the proofs below), and possession itself is read off the ball mesh, which
# BallController renders on the authoritative HandSide independently of any
# clip. Closing it needs a genuine left-hand dribble source or the mirror
# transform rejected above; #276's bar is "legible, not pretty" and visual
# quality is explicitly deferred.
#
# ── The proofs ───────────────────────────────────────────────────────────────
# Two independent geometric gates run at build time, both non-symmetric (the
# #255 mirror bug shipped because its test was symmetric and passed on a broken
# mirror):
#
#   1. Per polarity: the lateral displacement of the HANDS' MIDPOINT -- the ball
#      carriage -- from the first Startup frame to the last Recovery frame must
#      point at the DESTINATION side and exceed MIN_CROSS_TRAVEL_M.
#   2. Across polarities: those two displacements must have OPPOSITE signs. A
#      sign error that flipped both would pass gate 1's magnitude check on its
#      own; only gate 2 catches a swing that ignores its sign argument entirely.
#
# The lateral axis is derived from Y Bot's own rest and then CHECKED against the
# rest hand positions (_derive_body_axes), because `up.cross(forward)` points to
# the character's LEFT on this rig — rebuild_jumpshot_clips.gd calls that vector
# a "right axis", which is harmless there (it only uses it as a rotation axis
# for a forward pitch) but is exactly the confusion that produced #255.

const LIB_PATH := "res://assets/locomotion.res"
const SRC_FBX := "res://assets/Dribble.fbx"
const SRC_CLIP := "mixamo_com"

# Physics ticks per second (project.godot physics/common/physics_ticks_per_second).
const TPS := 60.0

# Crossover's frame data (scripts/Input/Crossover.cs DefaultFrameData).
# Duplicated here because GDScript cannot read the C# constant — so the
# duplication is made SAFE rather than avoided: LocomotionClipTest asserts each
# clip's length equals Crossover.DefaultFrameData's own tick count / 60, reading
# the C# side directly. Retune the move (the pending #238 tuning pass will) and
# that harness goes red and names this file.
const STARTUP_TICKS := 6
const ACTIVE_TICKS := 3
const RECOVERY_TICKS := 12

# Clip names, indexed by polarity. The suffix is the ORIGIN hand.
const NAMES := {
	"left": ["crossoverstartupleft", "crossoveractiveleft", "crossoverrecoveryleft"],
	"right": ["crossoverstartupright", "crossoveractiveright", "crossoverrecoveryright"],
}

# Peak swing of the ball arm at the top of the Active whip. Large enough that the
# hand clearly crosses the body's midline (ADR-0003 legibility / #276's "distinct
# silhouette" bar), small enough to stay a dribble rather than a windmill. The
# measured travel it produces is asserted below rather than assumed.
const SWING_DEG := 60.0
# Lateral torso lean toward the destination side, PER SPINE BONE (x3 = 15 deg of
# total bend at full swing). This is for SILHOUETTE ONLY — ADR-0003 wants a
# committed move to engage the whole body so its startup frames are legible, and
# the head and shoulders do lean correctly into the cross (measured: head lateral
# +0.270 m for a rightward cross, −0.408 m for a leftward one).
#
# It contributes essentially NOTHING to hand travel, and that is worth knowing
# before anyone scales it up to fix a marginal cross. Rotating about the forward
# axis moves a point at lateral offset r and height h relative to the pivot by
#
#     d_lateral = r*(cos phi - 1) + h*sin phi
#
# The head sits ABOVE the spine pivot (h > 0) so it tracks the lean's sign; the
# hands sit at roughly hip height, at or below the pivot, so their h*sin(phi)
# term vanishes or inverts and all that is left is r*(cos phi - 1) — which is
# negative regardless of sign, and therefore pulls BOTH hands toward the midline
# whichever way the torso leans. Measured directly: at 40 deg/bone, lean alone
# moved the hands the SAME direction for both polarities. A waist lean cannot
# carry hands laterally when the hands are at pivot height; only the arms can.
const SPINE_LEAN_DEG := 5.0
const LEAN_BONES := ["mixamorig_Spine", "mixamorig_Spine1", "mixamorig_Spine2"]
# Lateral weight shift of the hips toward the destination side, in metres. Small
# — the feet stay planted; this reads as weight transfer, not a step. It also
# makes the one POSITION_3D track polarity-dependent, so a polarity mix-up
# cannot hide in the hips path.
const HIP_SHIFT_M := 0.06

# The swing profile's control points, in units of SWING_DEG, sampled at the
# phase boundaries. The negative Startup value is the wind-up: the ball is
# carried AWAY from the cross before it whips back, which is what gives the
# 6-tick Startup something for a defender to read (ADR-0003 — the wind-up the
# opponent sees must fill the real Startup window).
const F_START := 0.0
const F_STARTUP_END := -0.30
const F_ACTIVE_END := 1.0
const F_RECOVERY_END := 0.65

# Bones the swing is composed onto, each mapped to [effector hand, scale]. The
# effector is the bone whose lateral travel that joint's rotation is aimed at —
# see _swing_axis for why the axis is derived per-joint rather than fixed.
#
# ForeArm is deliberately absent: ROTATION_3D keys are parent-relative, so a
# forearm key encodes only the elbow bend — limb elevation and lateral travel
# live in Shoulder/Arm (the same distinction #279's off-rest grading turns on).
# The clavicle carries a fraction of the arm's swing so the shoulder visibly
# turns into the cross instead of the arm moving alone (ADR-0003: committed moves
# engage the whole body, so startup frames are legible).
const SWING_BONES := {
	"mixamorig_LeftArm": ["mixamorig_LeftHand", 1.0],
	"mixamorig_RightArm": ["mixamorig_RightHand", 1.0],
	"mixamorig_LeftShoulder": ["mixamorig_LeftHand", 0.35],
	"mixamorig_RightShoulder": ["mixamorig_RightHand", 0.35],
}
const HIPS_BONE := "mixamorig_Hips"

# ── Measured negative result, recorded so #281-#283 do not retry it ──────────
# Straightening the elbows during the cross (slerping each ForeArm toward its
# T-pose rest) was tried to lengthen the bent dribbling arm's lever. It makes the
# problem WORSE, not better: extending pushes the hand outward ALONG the upper
# arm, and the dribbling arm points down-and-outward, so the extension carries
# the hand further onto its own side and cancels part of the swing. Measured
# right-polarity travel went 0.200 m -> 0.097 m. Removed.
#
# The lever is a hard ceiling anyway: a rotation moves its effector at most
# |lever| metres, and the dribbling hand sits ~0.28 m from its shoulder, so no
# arm-only rotation of any magnitude reaches the 0.30 m bar. The travel has to
# come from the torso — which is where a real crossover puts it.

# Resolution of the elevation curve used to find the dribble cycle's landmarks.
const CURVE_SAMPLES := 240
# The source must genuinely be a pumping dribble. If a re-export flattened it,
# every landmark below would be noise-fitting.
const MIN_PUMP_RANGE_M := 0.15
# How far the hands' midpoint must commit laterally for the clip to read as a
# cross. Set from the rig, not from whatever the build happens to produce: a
# quarter of the body's half-span (rest hand lateral 0.74 m) is a visible lateral
# commitment of the ball carriage. The current constants clear it with headroom
# (measured +-0.21 to +-0.24 m); if a retune ever leaves it marginal, raise
# SWING_DEG rather than lowering this.
const MIN_CROSS_TRAVEL_M := 0.18

var _skel: Skeleton3D = null
var _right := Vector3.ZERO     # body-right, = forward x up (checked, not assumed)
var _forward := Vector3.ZERO
var _up := Vector3.UP


func bone_of(np: NodePath) -> String:
	return "" if np.get_subname_count() == 0 else String(np.get_subname(0))


func _initialize() -> void:
	var lib = load(LIB_PATH)
	if lib == null or not (lib is AnimationLibrary):
		push_error("[rebuild-crossover] failed to load AnimationLibrary at %s" % LIB_PATH)
		quit(1)
		return

	_skel = _find(load("res://assets/Y Bot.fbx").instantiate(), "Skeleton3D")
	if _skel == null:
		push_error("[rebuild-crossover] could not find a Skeleton3D in Y Bot.fbx.")
		quit(1)
		return

	if not _derive_body_axes():
		quit(1)
		return

	var packed = load(SRC_FBX)
	if packed == null:
		push_error("[rebuild-crossover] failed to load %s" % SRC_FBX)
		quit(1)
		return
	var ap: AnimationPlayer = _find(packed.instantiate(), "AnimationPlayer")
	if ap == null or not ap.has_animation(SRC_CLIP):
		push_error("[rebuild-crossover] %s has no AnimationPlayer clip '%s'" % [SRC_FBX, SRC_CLIP])
		quit(1)
		return
	var src: Animation = ap.get_animation(SRC_CLIP)
	print("[rebuild-crossover] source '%s': len=%.4f tracks=%d" % [SRC_CLIP, src.length, src.get_track_count()])

	# ── Derive the base window from the dribble cycle ────────────────────────
	var marks := _derive_landmarks(src)
	if marks.is_empty():
		quit(1)
		return
	var t_load: float = marks["load"]
	var t_low: float = marks["low"]
	var t_rise: float = marks["rise"]
	print("[rebuild-crossover] landmarks: load=%.4f low=%.4f rise=%.4f" % [t_load, t_low, t_rise])

	# Active is the instant at the bottom of the pump — the ball is at its
	# lowest and closest to the floor, which is where a real crossover's ball
	# actually crosses. It is carved out of the middle of the trough so the
	# three slices remain a partition of one continuous motion: Startup ends
	# where Active begins and Active ends where Recovery begins, so the last
	# frame of one clip is the first frame of the next and the state change is
	# invisible.
	var active_half: float = minf(t_low - t_load, t_rise - t_low) * 0.20
	var t_active_start := t_low - active_half
	var t_active_end := t_low + active_half
	if not (t_load < t_active_start and t_active_start < t_active_end and t_active_end < t_rise):
		push_error("[rebuild-crossover] landmarks are not strictly ordered "
			+ "(load=%.4f active=%.4f..%.4f rise=%.4f) -- the source does not contain the "
			% [t_load, t_active_start, t_active_end, t_rise]
			+ "expected pump-down/pump-up cycle.")
		quit(1)
		return

	var src_rot := _rotation_track_count(src)
	var built := {}
	var travel := {}

	for polarity in ["left", "right"]:
		# cross_sign: +1 = the ball travels toward the body's RIGHT. A LEFT-origin
		# crossover moves the ball out of the left hand, so it travels right.
		var cross_sign := 1.0 if polarity == "left" else -1.0
		var names: Array = NAMES[polarity]

		# Global progress spans all 21 ticks so the swing profile is continuous
		# ACROSS the three clips, not restarted per clip — otherwise the arm
		# would snap back to neutral at every state change.
		var total := float(STARTUP_TICKS + ACTIVE_TICKS + RECOVERY_TICKS)
		var g0 := 0.0
		var g1 := float(STARTUP_TICKS) / total
		var g2 := float(STARTUP_TICKS + ACTIVE_TICKS) / total

		var startup := _slice(src, t_load, t_active_start, STARTUP_TICKS)
		var active := _slice(src, t_active_start, t_active_end, ACTIVE_TICKS)
		var recovery := _slice(src, t_active_end, t_rise, RECOVERY_TICKS)

		_apply_cross_swing(startup, cross_sign, g0, g1)
		_apply_cross_swing(active, cross_sign, g1, g2)
		_apply_cross_swing(recovery, cross_sign, g2, 1.0)

		built[names[0]] = startup
		built[names[1]] = active
		built[names[2]] = recovery

		# ── Proof 1: the ball carriage commits toward the destination ────────
		# Measured on the MIDPOINT of the two hands, not on one hand. That is
		# where the ball is carried as it crosses, so it is what an opponent
		# actually reads — and unlike a single hand it is symmetric between the
		# two polarities by construction, since both use the same two bones with
		# opposite sign.
		#
		# NOT a single hand, because the source's two arms have very different
		# levers: it is a right-hand dribble, so the right arm is bent (hand
		# ~0.28 m from its shoulder) while the left hangs relaxed (~0.55 m). The
		# identical swing therefore moves the relaxed hand roughly twice as far in
		# BOTH polarities (measured: left-origin LH +0.43 m vs RH +0.00 m). That
		# asymmetry is an artifact of the SOURCE, not of the cross direction, so a
		# per-hand threshold would be measuring the artifact rather than the move.
		#
		# Hips-relative so the composed hip weight-shift cannot flatter the number.
		var mid_start := _hand_midpoint_lateral(startup, 0.0)
		var mid_end := _hand_midpoint_lateral(recovery, recovery.length)
		var delta := mid_end - mid_start
		travel[polarity] = delta
		print("[rebuild-crossover] %-5s: hand midpoint lateral %+.4f -> %+.4f m (travel %+.4f, want %s and >= %.2f)"
			% [polarity, mid_start, mid_end, delta,
			   "positive" if cross_sign > 0.0 else "negative", MIN_CROSS_TRAVEL_M])
		if signf(delta) != cross_sign:
			push_error("[rebuild-crossover] %s-origin crossover moved the hands %+.4f m -- that is toward "
				% [polarity, delta] + "the WRONG side. Check the sign convention in _apply_cross_swing "
				+ "against _derive_body_axes()'s handedness.")
			quit(1)
			return
		if absf(delta) < MIN_CROSS_TRAVEL_M:
			push_error("[rebuild-crossover] %s-origin crossover only commits %.4f m (< %.2f) -- the hands "
				% [polarity, absf(delta), MIN_CROSS_TRAVEL_M] + "never clearly cross the body. Raise "
				+ "SWING_DEG; raising SPINE_LEAN_DEG will NOT help (see its doc).")
			quit(1)
			return

	# ── Proof 2: the two polarities are genuinely opposite ───────────────────
	# A swing that silently ignored its sign argument would produce two identical
	# clips, each passing proof 1's magnitude check. Only this comparison catches
	# that, and it is non-symmetric by construction (the #255 lesson).
	print("[rebuild-crossover] polarity travel: left=%+.4f right=%+.4f (want opposite signs)"
		% [travel["left"], travel["right"]])
	if signf(travel["left"]) == signf(travel["right"]):
		push_error("[rebuild-crossover] both polarities travel the same way (left=%+.4f right=%+.4f) -- "
			% [travel["left"], travel["right"]] + "the mirror is not a mirror.")
		quit(1)
		return

	var spread := _max_pose_delta(built[NAMES["left"][1]], built[NAMES["right"][1]])
	print("[rebuild-crossover] left-vs-right Active max pose delta = %.1f deg" % spread)
	if spread < 15.0:
		push_error("[rebuild-crossover] the two Active polarities differ by only %.1f deg -- not two "
			% spread + "distinct silhouettes.")
		quit(1)
		return

	# ── Completeness guard ───────────────────────────────────────────────────
	for name in built:
		if not _assert_complete(built[name], name, src_rot, src.get_track_count()):
			quit(1)
			return

	# ── Save ─────────────────────────────────────────────────────────────────
	# Idempotency: drop any previous build first, so re-running re-derives from
	# the pristine FBX rather than stacking edits.
	for name in built:
		if lib.has_animation(name):
			lib.remove_animation(name)
		lib.add_animation(name, built[name])

	var err := ResourceSaver.save(lib, LIB_PATH)
	if err != OK:
		push_error("[rebuild-crossover] ResourceSaver.save failed with error %d" % err)
		quit(1)
		return

	print("[rebuild-crossover] saved %s; clips now: %s" % [LIB_PATH, str(lib.get_animation_list())])
	quit(0)


# ── Body axes ────────────────────────────────────────────────────────────────
# Derived from Y Bot's own rest pose rather than hardcoded, because "which world
# axis is forward" depends on the FBX's import orientation — then CHECKED against
# the rest hand positions, because getting this backwards is precisely the #255
# mirror bug. Note that up.cross(forward) points to the character's LEFT on this
# rig; body-right is forward.cross(up), matching HandStateResolver's documented
# cross(forward, up) convention.
func _derive_body_axes() -> bool:
	var foot := _skel.find_bone("mixamorig_LeftFoot")
	var toe := _skel.find_bone("mixamorig_LeftToeBase")
	if foot < 0 or toe < 0:
		push_error("[rebuild-crossover] Y Bot has no LeftFoot/LeftToeBase to derive facing from.")
		return false
	var forward: Vector3 = _skel.get_bone_global_rest(toe).origin - _skel.get_bone_global_rest(foot).origin
	forward.y = 0.0
	if forward.length() < 0.001:
		push_error("[rebuild-crossover] Y Bot's foot->toe vector is vertical; cannot derive facing.")
		return false
	_forward = forward.normalized()
	_right = _forward.cross(_up).normalized()

	# The check: the RIGHT hand's rest origin must lie on the positive side of
	# the derived right axis, and the LEFT hand's on the negative side.
	var lh := _skel.find_bone("mixamorig_LeftHand")
	var rh := _skel.find_bone("mixamorig_RightHand")
	if lh < 0 or rh < 0:
		push_error("[rebuild-crossover] Y Bot has no LeftHand/RightHand to verify the lateral axis.")
		return false
	var lh_lat: float = _skel.get_bone_global_rest(lh).origin.dot(_right)
	var rh_lat: float = _skel.get_bone_global_rest(rh).origin.dot(_right)
	print("[rebuild-crossover] axes: forward=%s right=%s | rest hand lateral L=%+.4f R=%+.4f"
		% [_forward, _right, lh_lat, rh_lat])
	if not (rh_lat > 0.0 and lh_lat < 0.0):
		push_error("[rebuild-crossover] the derived right axis puts the RIGHT hand at %+.4f and the LEFT "
			% rh_lat + "at %+.4f -- it is inverted (or the rig's bone names lie). Every sign in this "
			% lh_lat + "file depends on this axis; refusing to build.")
		return false
	return true


# ── Landmark derivation ──────────────────────────────────────────────────────
# Reads the dribble cycle off the RIGHT hand's elevation above the hips (the
# source is a right-hand dribble; the left hand hangs still). Hips-relative so a
# source that crouches or rises does not shift the landmarks.
#
# Derived rather than hardcoded — matching rebuild_jumpshot_clips.gd — because
# hardcoded times would silently drift into nonsense if the source FBX were ever
# re-exported, whereas a derivation fails loudly. The measured cycle on the
# committed FBX is ~0.70 s with troughs near t=0.35 / 1.05 / 1.75.
#
# The window used is the DEEPEST trough that has a peak on both sides: the ball
# is nearest the floor there, which is both where a real crossover's ball crosses
# and where the two hands are closest together — which softens the known
# right-hand-source fidelity gap documented in the header.
func _derive_landmarks(src: Animation) -> Dictionary:
	var curve := []
	for s in CURVE_SAMPLES + 1:
		var t := (float(s) / float(CURVE_SAMPLES)) * src.length
		curve.append(_elevation_at(src, t, "mixamorig_RightHand"))

	var hi: float = curve[0]
	var lo: float = curve[0]
	for v in curve:
		hi = maxf(hi, v)
		lo = minf(lo, v)
	if hi - lo < MIN_PUMP_RANGE_M:
		push_error("[rebuild-crossover] the source's right hand only moves %.4f m vertically (< %.2f) -- "
			% [hi - lo, MIN_PUMP_RANGE_M] + "that is not a pumping dribble. Wrong source, or a "
			+ "re-export changed it.")
		return {}

	# Interior local minima, deepest first.
	var best := -1
	for i in range(1, curve.size() - 1):
		if curve[i] <= curve[i - 1] and curve[i] <= curve[i + 1]:
			if best < 0 or curve[i] < curve[best]:
				best = i
	if best < 0:
		push_error("[rebuild-crossover] the source has no interior pump trough.")
		return {}

	# Walk outward to the surrounding peaks.
	var load_i := best
	while load_i > 0 and curve[load_i - 1] >= curve[load_i]:
		load_i -= 1
	var rise_i := best
	while rise_i < curve.size() - 1 and curve[rise_i + 1] >= curve[rise_i]:
		rise_i += 1
	if load_i == best or rise_i == best:
		push_error("[rebuild-crossover] the deepest trough at sample %d has no peak on both sides -- "
			% best + "it sits at the clip boundary, so there is no complete cycle to slice.")
		return {}

	var step := src.length / float(CURVE_SAMPLES)
	return {
		"load": float(load_i) * step,
		"low": float(best) * step,
		"rise": float(rise_i) * step,
	}


# Height of `bone` above the hips at time `t`, by manual FK (see _pose_origin).
func _elevation_at(anim: Animation, t: float, bone: String) -> float:
	return _pose_origin(anim, t, bone).y - _pose_origin(anim, t, HIPS_BONE).y


# Lateral offset of `bone` from the hips along the body-right axis at time `t`.
# Hips-relative so the composed hip shift cannot inflate the crossing proof.
func _lateral(anim: Animation, t: float, bone: String) -> float:
	return (_pose_origin(anim, t, bone) - _pose_origin(anim, t, HIPS_BONE)).dot(_right)


# Lateral position of the midpoint between the two hands — the ball carriage.
# See proof 1 for why the crossing gate measures this rather than one hand.
func _hand_midpoint_lateral(anim: Animation, t: float) -> float:
	return (_lateral(anim, t, "mixamorig_LeftHand") + _lateral(anim, t, "mixamorig_RightHand")) * 0.5


# ── Slicing ──────────────────────────────────────────────────────────────────
# Resamples source range [t0, t1] into a clip of exactly `ticks` ticks at 60 tps,
# one key per gameplay tick (ticks + 1 keys, the last landing exactly on
# `length`). Keying at the tick rate rather than copying the source's own key
# times is what ties the clip to the move's frame data: the displayed motion and
# the authoritative move phase advance in lockstep, so the wind-up an opponent
# reads fills exactly the real Startup window and never a frame more (#276
# point 4 -- "no false reads").
#
# Every track type the source carries is resampled, not just rotations. Dropping
# the Hips position track would strip the clip's weight shift; dropping any bone
# track at all would re-open the full-weight rest-pose trap.
func _slice(src: Animation, t0: float, t1: float, ticks: int) -> Animation:
	var out := Animation.new()
	# Explicit, not inherited: these ARE one-shots so LOOP_NONE happens to be the
	# FBX import default -- which is exactly why it is set on purpose.
	# dribbleidle/dribblemove needed the opposite and the silent default was the
	# easiest thing to get wrong there (#285).
	out.loop_mode = Animation.LOOP_NONE
	out.length = float(ticks) / TPS

	for i in src.get_track_count():
		var type := src.track_get_type(i)
		if type != Animation.TYPE_ROTATION_3D \
			and type != Animation.TYPE_POSITION_3D \
			and type != Animation.TYPE_SCALE_3D:
			continue
		var t := out.add_track(type)
		out.track_set_path(t, src.track_get_path(i))
		for k in ticks + 1:
			var u := float(k) / float(ticks)
			var st: float = lerpf(t0, t1, u)
			var dt := float(k) / TPS
			match type:
				Animation.TYPE_ROTATION_3D:
					out.rotation_track_insert_key(t, dt, src.rotation_track_interpolate(i, st))
				Animation.TYPE_POSITION_3D:
					out.position_track_insert_key(t, dt, src.position_track_interpolate(i, st))
				Animation.TYPE_SCALE_3D:
					out.scale_track_insert_key(t, dt, src.scale_track_interpolate(i, st))
	return out


# ── The cross-body swing ─────────────────────────────────────────────────────
# Rewrites `anim`'s keys in place, composing a signed swing whose magnitude
# follows the global profile between global progress `g_start` and `g_end`.
#
# cross_sign: +1 = the ball travels toward the body's RIGHT.
#
# Rotation about +forward by a POSITIVE angle carries a point on the body's right
# down and across to the left (with forward=+Z, up=+Y: body-right = Z x Y = -X,
# and rotating -X about +Z by +90 deg lands on -Y, i.e. straight down in front of
# the body -- which is exactly the path a crossover's ball takes). So a cross
# toward the RIGHT needs the NEGATIVE angle, hence the leading minus.
#
# That derivation is stated for the reader, not relied upon: _initialize's
# proof 1 measures the resulting hand travel and fails the build if the sign is
# wrong, which is the same posture rebuild_jumpshot_clips.gd takes with its
# fadeaway lean.
func _apply_cross_swing(anim: Animation, cross_sign: float, g_start: float, g_end: float) -> void:
	# Parent poses are read from a PRISTINE copy so the conjugation below is not
	# order-dependent: rewriting the shoulder's keys must not change the frame the
	# arm's own delta is conjugated into, or the two contributions would compound
	# differently depending on track order in the file.
	var pristine := anim.duplicate(true)

	for i in anim.get_track_count():
		var bone := bone_of(anim.track_get_path(i))
		var type := anim.track_get_type(i)

		var is_swing := type == Animation.TYPE_ROTATION_3D and SWING_BONES.has(bone)
		var is_lean := type == Animation.TYPE_ROTATION_3D and bone in LEAN_BONES
		var is_hips := type == Animation.TYPE_POSITION_3D and bone == HIPS_BONE
		if not (is_swing or is_lean or is_hips):
			continue

		for k in anim.track_get_key_count(i):
			var kt: float = anim.track_get_key_time(i, k)
			var u: float = 0.0 if anim.length <= 0.0 else kt / anim.length
			var g: float = lerpf(g_start, g_end, u)
			var f := _swing_profile(g)

			if type == Animation.TYPE_POSITION_3D:
				# Weight shift toward the destination side.
				var p: Vector3 = anim.track_get_key_value(i, k)
				anim.track_set_key_value(i, k, p + _right * (cross_sign * HIP_SHIFT_M * f))
				continue

			var q: Quaternion = anim.track_get_key_value(i, k)

			var delta: Quaternion
			if is_lean:
				# Lateral lean toward the destination. Rotating the up axis about
				# +forward by +phi carries it toward +right (U*cos phi + R*sin phi),
				# so the sign is positive for a rightward cross.
				delta = Quaternion(_forward, deg_to_rad(cross_sign * SPINE_LEAN_DEG * f))
			else:
				var effector: String = SWING_BONES[bone][0]
				var scale: float = SWING_BONES[bone][1]
				var axis := _swing_axis(pristine, kt, bone, effector)
				if axis == Vector3.ZERO:
					continue
				delta = Quaternion(axis, deg_to_rad(cross_sign * SWING_DEG * f * scale))

			# Conjugate the SKELETON-frame delta into the bone's PARENT frame
			# before composing: a ROTATION_3D key is parent-relative, so the
			# bone's global orientation is P*q and applying a global delta D
			# means solving P*q_new = D*P*q, i.e. q_new = (P^-1*D*P)*q.
			#
			# rebuild_jumpshot_clips.gd's _apply_lean pre-multiplies D directly,
			# which is the P = identity special case. That holds there because the
			# only bone it leans is mixamorig_Spine, whose parent is Hips. It does
			# NOT hold for the arm chain: LeftShoulder/RightShoulder sit far from
			# identity in a T-pose rig, so composing without the conjugation swings
			# the arm about an arbitrary axis (measured: it moved the ball hand the
			# WRONG way, -0.1114 m, before this was fixed).
			var p := _parent_global_rotation(pristine, kt, bone)
			var local_delta := (p.inverse() * delta * p).normalized()
			anim.track_set_key_value(i, k, (local_delta * q).normalized())


# The swing magnitude as a function of global progress g in [0, 1] across all
# three phases. Piecewise-smoothstep through the four control points so the
# motion has no velocity discontinuity at a phase boundary -- a hard corner
# there would read as a hitch exactly where the state machine changes state,
# which is the one place a viewer is primed to notice one.
func _swing_profile(g: float) -> float:
	var total := float(STARTUP_TICKS + ACTIVE_TICKS + RECOVERY_TICKS)
	var g1 := float(STARTUP_TICKS) / total
	var g2 := float(STARTUP_TICKS + ACTIVE_TICKS) / total
	if g <= g1:
		return lerpf(F_START, F_STARTUP_END, smoothstep(0.0, 1.0, g / g1))
	if g <= g2:
		return lerpf(F_STARTUP_END, F_ACTIVE_END, smoothstep(0.0, 1.0, (g - g1) / (g2 - g1)))
	return lerpf(F_ACTIVE_END, F_RECOVERY_END, smoothstep(0.0, 1.0, (g - g2) / (1.0 - g2)))


# The a45bd1d guard, applied at build time as well as in the harness: a slice
# that lost bone tracks would rest-pose the missing bones the moment its state
# was entered, and it would do so silently.
func _assert_complete(anim: Animation, name: StringName, expected_rot: int, expected_total: int) -> bool:
	var rot := _rotation_track_count(anim)
	var unresolved := []
	for i in anim.get_track_count():
		var b := bone_of(anim.track_get_path(i))
		if b != "" and _skel.find_bone(b) < 0:
			unresolved.append(b)
	print("[rebuild-crossover]   '%s': len=%.4f tracks=%d rot=%d loop=%d unresolved=%s"
		% [name, anim.length, anim.get_track_count(), rot, anim.loop_mode, str(unresolved)])
	if rot != expected_rot or anim.get_track_count() != expected_total:
		push_error("[rebuild-crossover] '%s' has %d/%d tracks, source has %d/%d -- a slice must inherit "
			% [name, rot, anim.get_track_count(), expected_rot, expected_total]
			+ "the source's FULL body coverage or the untracked bones rest-pose at full weight.")
		return false
	if unresolved.size() > 0:
		push_error("[rebuild-crossover] '%s' has tracks that do not resolve on Y Bot: %s"
			% [name, str(unresolved)])
		return false
	return true


func _rotation_track_count(anim: Animation) -> int:
	var n := 0
	for i in anim.get_track_count():
		if anim.track_get_type(i) == Animation.TYPE_ROTATION_3D:
			n += 1
	return n


# Largest per-bone angular difference between two clips at matched phase — the
# honest measure of "are these silhouettes actually distinct".
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


# The skeleton-frame axis about which rotating `joint` carries `effector`
# directly along the body's RIGHT axis, at time `t`.
#
# Why this is derived per joint and per key instead of being a fixed axis: a
# rotation moves its effector in the direction (axis x lever), so an effector
# lying near the axis barely moves at all. Measured on this source, a fixed
# forward-axis swing carried the LEFT hand 0.375 m (it hangs at the side,
# perpendicular to forward) but the RIGHT hand only 0.114 m -- because the right
# hand is the dribbling hand, held out IN FRONT of its shoulder and therefore
# nearly ON the forward axis. No single fixed axis serves both.
#
# Taking axis = normalize(lever x right) makes (axis x lever) equal the component
# of `right` perpendicular to the lever -- i.e. the most lateral motion that
# joint can produce -- so travel scales with limb length rather than with which
# way the limb happens to be pointing, and a POSITIVE angle always moves the
# effector toward +right.
#
# Returns ZERO when the lever is degenerate (effector at the joint, or the limb
# already parallel to `right`); the caller skips that key rather than composing a
# garbage axis.
func _swing_axis(anim: Animation, t: float, joint: String, effector: String) -> Vector3:
	var lever := _pose_origin(anim, t, effector) - _pose_origin(anim, t, joint)
	if lever.length() < 0.01:
		return Vector3.ZERO
	var axis := lever.cross(_right)
	if axis.length() < 0.01:
		return Vector3.ZERO
	return axis.normalized()


# Accumulated global ROTATION of `bone`'s PARENT with `anim` applied at time `t`
# — the frame a skeleton-space delta must be conjugated into before it can be
# composed onto a parent-relative ROTATION_3D key (see _apply_cross_swing).
#
# Same manual-FK walk as _pose_origin and for the same reason: a Skeleton3D never
# added to the SceneTree does not recompute global poses, so get_bone_global_pose
# would return rest and every proof built on it would pass vacuously (#285).
# Orthonormalized before extracting the quaternion because rest bases carry scale.
func _parent_global_rotation(anim: Animation, t: float, bone: String) -> Quaternion:
	var idx := _skel.find_bone(bone)
	if idx < 0:
		return Quaternion.IDENTITY
	var parent := _skel.get_bone_parent(idx)
	if parent < 0:
		return Quaternion.IDENTITY

	var track_of := {}
	for i in anim.get_track_count():
		if anim.track_get_type(i) != Animation.TYPE_ROTATION_3D:
			continue
		var b := _skel.find_bone(bone_of(anim.track_get_path(i)))
		if b >= 0:
			track_of[b] = i

	var chain := []
	var walk := parent
	while walk >= 0:
		chain.push_front(walk)
		walk = _skel.get_bone_parent(walk)

	var acc := Basis.IDENTITY
	for b in chain:
		var local: Basis = _skel.get_bone_rest(b).basis.orthonormalized()
		if track_of.has(b):
			local = Basis(anim.rotation_track_interpolate(track_of[b], t))
		acc = acc * local
	return acc.orthonormalized().get_rotation_quaternion()


# Global origin of `bone` with `anim` applied at time `t`, by manual forward
# kinematics.
#
# Deliberately NOT get_bone_global_pose(): a Skeleton3D that was never added to
# the SceneTree does not recompute its global poses, so that call returns the
# unchanged rest transform and every geometric proof built on it passes
# vacuously at exactly 0.0000 (measured, #285). Manual FK depends on nothing but
# the rest pose and the clip's own keys.
func _pose_origin(anim: Animation, t: float, bone: String) -> Vector3:
	var idx := _skel.find_bone(bone)
	if idx < 0:
		return Vector3.ZERO

	var track_of := {}
	for i in anim.get_track_count():
		if anim.track_get_type(i) != Animation.TYPE_ROTATION_3D:
			continue
		var b := _skel.find_bone(bone_of(anim.track_get_path(i)))
		if b >= 0:
			track_of[b] = i

	var chain := []
	var walk := idx
	while walk >= 0:
		chain.push_front(walk)
		walk = _skel.get_bone_parent(walk)

	var acc := Transform3D.IDENTITY
	for b in chain:
		var rest: Transform3D = _skel.get_bone_rest(b)
		# ROTATION_3D keys are absolute LOCAL rotations, so an animated bone
		# REPLACES the rest basis' rotation; scale and origin carry over.
		var local := rest
		if track_of.has(b):
			var q: Quaternion = anim.rotation_track_interpolate(track_of[b], t)
			local = Transform3D(Basis(q).scaled(rest.basis.get_scale()), rest.origin)
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
