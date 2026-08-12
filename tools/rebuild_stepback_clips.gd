extends SceneTree
# Asset build tool (#306) — drafts the step-back clip family into
# assets/locomotion.res by SLICING assets/stepback_authored.fbx.
#
# Run:  godot --headless --path . -s tools/rebuild_stepback_clips.gd
# Idempotent: re-running re-derives all three clips from the pristine FBX
# rather than stacking edits (the previous build is removed before the new
# one lands).
#
# Produces THREE LOOP_NONE one-shots — step-back is UNHANDED (StepBack.cs's
# own class doc: "No hand swap: there is no ball transit"), so — like retreat
# dribble (#305), jab step (#304), layup (#313) and contest (#314) — this
# tool slices THREE clips, not six:
#   stepbackstartup    7 ticks / 0.11667 s
#   stepbackactive     4 ticks / 0.06667 s
#   stepbackrecovery   8 ticks / 0.13333 s
#
# ── Retreat dribble's template, not its twin ─────────────────────────────────
# tools/author_stepback.py's docstring covers the full contrast (7/4/8 ticks
# vs 3/2/4, both feet airborne during Active vs one weight-bearing foot
# throughout, a torso BAND at Active's end vs a one-sided floor). This file
# inherits the SAME slicing/proof machinery rebuild_retreatdribble_clips.gd
# established (itself inherited from rebuild_jabstep_clips.gd /
# rebuild_jumpshot_clips.gd) and adapts only what the motion actually differs
# on: G5 below is a BAND, not a floor, and G6 is new — the recovery-to-
# jumpshot hand-off measurement handoff 06 calls out as load-bearing.
#
# ── Why this is a SLICE, not a compose ───────────────────────────────────────
# tools/author_stepback.py (headless Blender, #315's blender_anim_lib
# machinery) already authored the full Startup/Active/Recovery arc as
# hand-keyed IK poses, baked at 60 Hz, on ONE timeline. This tool's job is
# therefore only to resample ("slice") the three named windows out of that
# timeline and then PROVE geometrically that what got sliced is what the
# issue asked for.
#
# The proofs are RE-RUN here rather than inherited from the Blender side on
# purpose: the FBX round-trip, the importer's fps/trimming/immutable-track
# settings, and `_slice`'s resampling are exactly the machinery that has
# silently corrupted clips in this repo before (#281, #295, #297).
#
# ── The Mixamo bone-name-prefix trap (read before touching bone_of/_resolve) ─
# In Blender the bones are named `mixamorig:Hips` (colon) — see
# blender_anim_lib.py's HIPS/SPINE constants. Godot 4.6+'s `ufbx` importer
# imports Mixamo-prefixed bones as `mixamorig_Hips` (UNDERSCORE) instead.
# `_resolve_bone()` tries BOTH forms and reports which form actually matched,
# so a silent zero-match can never hide behind a green run.
#
# ── The a45bd1d full-body-coverage trap ──────────────────────────────────────
# A single-clip AnimationTree state plays at FULL WEIGHT, and Godot's
# AnimationMixer writes every bone the active clip does NOT track to the
# skeleton's rest transform. author_stepback.py's own
# `verify_all_bones_keyed(expected_count=52)` gate already proves the SOURCE
# carries full-body coverage; `_assert_complete()` below re-proves that every
# SLICE inherits that coverage verbatim rather than trusting the source's own
# proof to survive slicing.
#
# ── The `Armature/` prefix trap (README trap 13, #281) ───────────────────────
# Blender's FBX export wraps the skeleton in an Armature object, so a track
# imported from stepback_authored.fbx reads
# "Armature/Skeleton3D:mixamorig_Hips" — one level deeper than
# scenes/Player.tscn's rig, whose skeleton sits directly at "Skeleton3D". An
# unresolvable track binds to nothing: the clip plays as a SILENT no-op —
# the state machine still enters the right state, the duration still checks
# out, and the mesh never moves. `_rebase_path()` strips the prefix on every
# track, and `_assert_complete()` rejects (not skips) any surviving
# `Armature/`-prefixed path or any path with no bone subname.
#
# ── Where the three windows come from ────────────────────────────────────────
# Hardcoded, not derived: author_stepback.py's frame layout is DETERMINISTIC
# BY CONSTRUCTION — it keys its timeline at exact times computed from
# StepBack's own frame data (7/4/8 ticks @ 60 Hz) and the import sets
# `trimming=false`, so those source times land exactly where the docstring
# says. This tool ASSERTS the guarantee (the source clip's total length) so a
# silently-retrimmed or wrong-fps import fails loudly instead of slicing
# garbage.
#
#   source seconds        segment      ticks
#   0.00000 -> 0.11667    Startup       7
#   0.11667 -> 0.18333    Active        4
#   0.18333 -> 0.31667    Recovery      8
#
# ── Cosmetic-only (issue #306's standing constraint) ─────────────────────────
# This tool writes ONE file: assets/locomotion.res. It reads no gameplay
# constant and changes no gameplay behaviour. In particular it never touches
# StepBackBurstSpeed, StepBackExitConeDegrees, BallState or HasDribbled, so
# StepBackTest's `step-back-gathers` scenario is out of this file's reach by
# construction. The tick counts below are DUPLICATED from StepBack's frame
# data for slicing, never read back into it.

const LIB_PATH := "res://assets/locomotion.res"
const SRC_FBX := "res://assets/stepback_authored.fbx"
# Matches author_stepback.py's ACTION_NAME -- export_fbx() renames both the
# Blender action AND the scene to this so Godot's importer names the
# resulting AnimationPlayer take after it.
const SRC_CLIP := "stepback"

# Physics ticks per second (project.godot physics/common/physics_ticks_per_second).
const TPS := 60.0

# The node-path prefix Blender's Armature object wrapper adds; see _rebase_path.
const ARMATURE_PREFIX := "Armature/"

# StepBack's frame data (scripts/Input/StepBack.cs DefaultFrameData).
# Duplicated here because GDScript cannot read the C# constant -- so the
# duplication is made SAFE rather than avoided: StepBackAnimTest's
# `stepback-segment-lengths` scenario asserts each clip's length equals
# StepBack.DefaultFrameData's own tick count / 60, reading the C# side
# directly. Retune the move without re-running this tool and that harness
# scenario goes red and names this file.
const STARTUP_TICKS := 7
const ACTIVE_TICKS := 4
const RECOVERY_TICKS := 8

# Source-time windows, matching author_stepback.py's frame table exactly
# (frame numbers there ARE physics ticks at 60 Hz: 0/7/11/19).
const STARTUP := [0.0 / 60.0, 7.0 / 60.0]
const ACTIVE := [7.0 / 60.0, 11.0 / 60.0]
const RECOVERY := [11.0 / 60.0, 19.0 / 60.0]

# The producer exports frame_start=0, frame_end=19 (TOTAL_TICKS in
# author_stepback.py), so the imported clip's length must be ~19/60 s. A
# silently-retrimmed or wrong-fps import would shift every window above out
# from under the actual keyed poses.
const EXPECTED_SRC_LENGTH_S := 19.0 / 60.0
const SRC_LENGTH_TOL_S := 0.02

const NAMES := ["stepbackstartup", "stepbackactive", "stepbackrecovery"]

# G3 legibility floor (#296's actual complaint -- Startup and Recovery must
# read as visibly different poses). Matches author_stepback.py's own
# STARTUP_END_VS_RECOVERY_END_MIN_DEG = 15.0 gate -- this tool re-proves it
# on the SLICED clips rather than trusting the source's Blender-side proof to
# survive the slice untouched. Blender-side measured 26.304 deg; this
# resource-side re-measurement (a different space -- FBX round-trip + slice,
# not the Blender pose graph) reads 16.6 deg. The margin over the 15.0 floor
# is real but thin (1.6 deg) -- both Startup's and Recovery's final poses are
# independently constrained by other gates (Startup's torso lean by the
# "sell the drive" read, Recovery's by the jumpshot hand-off in G6 below), so
# there is limited room to widen this gap further without fighting one of
# those. Flagged rather than hidden; see the PR.
const STARTUP_VS_RECOVERY_MIN_DEG := 15.0

# G5: the torso BAND at Active's end -- see author_stepback.py's
# `_verify_torso_band_at_active_end` for the full reasoning (this is the
# two-sided version of rebuild_retreatdribble_clips.gd's one-sided G5:
# step-back must land NEAR vertical on BOTH sides, since leaning back reads
# as the fadeaway, #318). Same 0.05 m band, re-measured here by manual FK on
# the SLICED Godot resource rather than trusted from the Blender side.
# Blender-side measured torso_forward_at_active_end_m = +0.0044.
const SPINE_BONE := "mixamorig_Spine"
const HEAD_BONE := "mixamorig_Head"
const TORSO_BAND_AT_ACTIVE_M := 0.05

# G6: the recovery -> jumpshot hand-off (handoff 06's load-bearing cross-clip
# point; #253's cradle-race fix exists BECAUSE StepBack cradles the ball for
# a shot that follows). Every AnimationTree transition is a hard cut
# (`grep -c xfade_time scenes/Player.tscn` == 0), so a large pose
# discontinuity between StepBackRecovery's own end and JumpshotStartup's own
# start SNAPS at the most-watched moment in the game.
#
# METRIC, and why it is NOT a raw per-bone rotation delta (measured, not
# assumed): the first version of this gate compared raw ROTATION_3D track
# quaternions per bone and read 179.6 deg worst-case (mixamorig_RightUpLeg).
# That number turned out to be near-meaningless: a control comparison against
# `idle` -- a long-shipped, visibly-correct clip with no relation to this
# move at all -- showed the SAME 150-180 deg order of magnitude on the SAME
# limb bones (idle -> recovery: Hips 155.6, RightArm 162.2, LeftUpLeg 177.4
# deg). README's own "Rotation-family clearance" section documents exactly
# this: raw per-bone rotation distance on round, twist-dominated limb
# segments (UpLeg/Leg/Arm) is swamped by BONE-ROLL CONVENTION differences
# between authoring pipelines (the #338 story is the same defect shape) and
# is largely invisible on a cylindrical mesh, whereas the SAME comparison on
# orientation-sensitive extremities (hands, fingers) stayed far smaller
# (RightHand 59.8, fingers 5-28 deg) -- consistent with "this is roll noise
# on limbs, not a real pose difference."
#
# So this gate instead measures LANDMARK POSITION RELATIVE TO HIPS, via the
# same FK helper (`_pose_origin`) G5 already uses for the torso: how far each
# of a handful of VISUALLY SALIENT points (both hands -- where the ball is
# held, the head, both feet) sits relative to the body's own root, and how
# much THAT relationship changes across the cut. That is much closer to what
# a viewer perceives as "the pose jumped" than either a local-space rotation
# number (dominated by roll-convention noise, above) or an ABSOLUTE
# FK position (a first attempt at this: it read RightFoot jumping 0.55-0.63 m,
# which turned out to be a coordinate-ANCHOR artifact -- Hips is the root
# bone, so its position track carries author_stepback.py's own `hips_base`,
# a point captured from Dribble.fbx's own armature, with no guaranteed
# relationship to whatever origin jumpshotstartup's own source FBX,
# "Goalkeeper Catch Stationary.fbx", used. Measuring relative to each clip's
# own Hips cancels that anchor mismatch and leaves only genuine body-relative
# shape, mirroring how author_stepback.py's own `_verify_both_feet_drift_forward`
# already measures ankle position relative to Hips rather than absolute).
# Hips is deliberately excluded from this list -- it is the reference the
# others are measured relative to, so comparing it to itself would be
# trivially zero; its own vertical settle is checked separately in world Y.
const HANDOFF_LANDMARK_BONES := ["mixamorig_LeftHand", "mixamorig_RightHand",
	"mixamorig_Head", "mixamorig_LeftFoot", "mixamorig_RightFoot"]

# No established threshold existed before this issue; this is the ADR-0014
# legibility call, recorded here AND in the PR. MEASURED on the shipped
# clips (worst landmark, LeftFoot, relative-to-hips jump = 0.348 m -- printed
# per-landmark below on every run). 0.45 m gives ~30% headroom over that
# reading: enough to absorb float noise and small future retunes without
# masking a real regression -- the EARLIER (rejected) wide-stance draft
# measured 0.55-0.63 m absolute, well clear of this floor even accounting for
# the switch to a relative metric. Do NOT raise this number to make a bad
# hand-off pass; retune stepbackrecovery's Recovery-row channels in
# author_stepback.py instead.
const RECOVERY_JUMPSHOT_HANDOFF_MAX_M := 0.45

var _skel: Skeleton3D = null
var _right := Vector3.ZERO
var _forward := Vector3.ZERO
var _up := Vector3.UP


func bone_of(np: NodePath) -> String:
	return "" if np.get_subname_count() == 0 else String(np.get_subname(0))


# The node path an AnimationPlayer under scenes/Player.tscn can actually
# resolve. See header — Blender's FBX export wraps the skeleton in an Armature
# object, so a track imported from stepback_authored.fbx reads
# "Armature/Skeleton3D:mixamorig_Hips" — one level deeper than the rig.
func _rebase_path(np: NodePath) -> NodePath:
	var s := String(np)
	if s.begins_with(ARMATURE_PREFIX):
		return NodePath(s.substr(len(ARMATURE_PREFIX)))
	return np


# The Mixamo bone-name-prefix trap (see header): try the name as given, then
# the opposite colon/underscore form. Returns -1 only if NEITHER form resolves.
func _alt_bone_name(name: String) -> String:
	if name.begins_with("mixamorig:"):
		return "mixamorig_" + name.substr(len("mixamorig:"))
	if name.begins_with("mixamorig_"):
		return "mixamorig:" + name.substr(len("mixamorig_"))
	return name


# Returns [bone_index, form_used] where form_used is "as-given", "alt", or
# "unresolved". Called once per track by _assert_complete, which is what lets
# the report print an honest match count instead of assuming one spelling.
func _resolve_bone(name: String) -> Array:
	var idx := _skel.find_bone(name)
	if idx >= 0:
		return [idx, "as-given"]
	var alt := _alt_bone_name(name)
	if alt != name:
		idx = _skel.find_bone(alt)
		if idx >= 0:
			return [idx, "alt"]
	return [-1, "unresolved"]


func _initialize() -> void:
	var lib = load(LIB_PATH)
	if lib == null or not (lib is AnimationLibrary):
		push_error("[rebuild-stepback] failed to load AnimationLibrary at %s" % LIB_PATH)
		quit(1)
		return

	_skel = _find(load("res://assets/Y Bot.fbx").instantiate(), "Skeleton3D")
	if _skel == null:
		push_error("[rebuild-stepback] could not find a Skeleton3D in Y Bot.fbx.")
		quit(1)
		return

	if not _derive_body_axes():
		quit(1)
		return

	var packed = load(SRC_FBX)
	if packed == null:
		push_error("[rebuild-stepback] failed to load %s" % SRC_FBX)
		quit(1)
		return
	var ap: AnimationPlayer = _find(packed.instantiate(), "AnimationPlayer")
	if ap == null:
		push_error("[rebuild-stepback] %s has no AnimationPlayer." % SRC_FBX)
		quit(1)
		return
	if not ap.has_animation(SRC_CLIP):
		push_error("[rebuild-stepback] %s has no AnimationPlayer clip '%s' (has: %s)"
			% [SRC_FBX, SRC_CLIP, str(ap.get_animation_list())])
		quit(1)
		return
	var src: Animation = ap.get_animation(SRC_CLIP)
	print("[rebuild-stepback] source '%s': len=%.5f tracks=%d"
		% [SRC_CLIP, src.length, src.get_track_count()])

	# ── Sanity: the source must be the full 0..19-frame export, not a
	# silently-retrimmed or wrong-fps import.
	if absf(src.length - EXPECTED_SRC_LENGTH_S) > SRC_LENGTH_TOL_S:
		push_error("[rebuild-stepback] source length %.5f s is not within %.3f s of the expected "
			% [src.length, SRC_LENGTH_TOL_S] + "%.5f s (19/60 s @ 60 fps) -- the import may have been "
			% EXPECTED_SRC_LENGTH_S + "retrimmed or baked at the wrong fps, which would silently shift "
			+ "every hardcoded slice window in this file off the authored poses.")
		quit(1)
		return

	# What a SLICE is expected to carry, derived from the source by applying the
	# same two filters _slice() applies -- never the source's raw counts.
	var src_rot := 0
	var src_total := 0
	for i in src.get_track_count():
		var ty := src.track_get_type(i)
		if ty != Animation.TYPE_ROTATION_3D and ty != Animation.TYPE_POSITION_3D:
			continue
		if bone_of(src.track_get_path(i)) == "":
			continue
		src_total += 1
		if ty == Animation.TYPE_ROTATION_3D:
			src_rot += 1
	print("[rebuild-stepback] source has %d tracks; %d expected per slice after dropping "
		% [src.get_track_count(), src_total]
		+ "SCALE (fights PlayerRigScaler) and the Armature object node (unbindable on Player.tscn).")

	# ── Slice the three windows ──────────────────────────────────────────────
	var windows := [STARTUP, ACTIVE, RECOVERY]
	var ticks := [STARTUP_TICKS, ACTIVE_TICKS, RECOVERY_TICKS]

	var built := {}
	for i in 3:
		var w: Array = windows[i]
		built[NAMES[i]] = _slice(src, w[0], w[1], ticks[i])

	# ── G1: existence, loop mode, exact length ───────────────────────────────
	var g1_ok := true
	for name in built:
		var anim: Animation = built[name]
		var idx := _name_tick_index(name)
		var expected_len := float(ticks[idx]) / TPS
		var len_ok := absf(anim.length - expected_len) <= 1e-4
		var loop_ok := anim.loop_mode == Animation.LOOP_NONE
		print("[rebuild-stepback] G1 '%s': length=%.5f (expect %.5f) loop_mode=%d %s"
			% [name, anim.length, expected_len, anim.loop_mode, "OK" if (len_ok and loop_ok) else "FAIL"])
		if not (len_ok and loop_ok):
			g1_ok = false
	if not g1_ok:
		push_error("[rebuild-stepback] G1 FAILED -- one or more clips has the wrong length or loop_mode.")
		quit(1)
		return

	# ── G2: track-count parity + full bone resolution (both name forms) ─────
	var g2_ok := true
	var form_counts := {"as-given": 0, "alt": 0, "unresolved": 0}
	for name in built:
		if not _assert_complete(built[name], name, src_rot, src_total, form_counts):
			g2_ok = false
	print("[rebuild-stepback] G2 bone-name resolution across all three clips: as-given=%d alt=%d unresolved=%d"
		% [form_counts["as-given"], form_counts["alt"], form_counts["unresolved"]])
	if not g2_ok:
		quit(1)
		return
	if form_counts["unresolved"] > 0:
		push_error("[rebuild-stepback] G2 FAILED -- %d bone tracks did not resolve on Y Bot in EITHER "
			% form_counts["unresolved"] + "name form.")
		quit(1)
		return
	var resolved_total: int = form_counts["as-given"] + form_counts["alt"]
	if resolved_total <= 0:
		push_error("[rebuild-stepback] G2 FAILED -- resolved count is %d; a name-matching gate that "
			% resolved_total + "matches nothing passes vacuously.")
		quit(1)
		return

	var startup: Animation = built[NAMES[0]]
	var active: Animation = built[NAMES[1]]
	var recovery: Animation = built[NAMES[2]]

	# ── G3: Startup's END pose vs Recovery's END pose ────────────────────────
	# NOT a whole-clip-start-vs-whole-clip-end comparison -- Startup's own LAST
	# frame (the fully-sold lie) vs Recovery's own LAST frame (the settled,
	# ready-to-shoot stance) is the comparison that actually tests #296.
	# author_stepback.py re-proves this exact pair Blender-side for the same
	# reason.
	var g3_delta := _pose_delta_at(startup, startup.length, recovery, recovery.length)
	print("[rebuild-stepback] G3 startup-end vs recovery-end max bone delta = %.1f deg (want >= %.1f)"
		% [g3_delta, STARTUP_VS_RECOVERY_MIN_DEG])
	if g3_delta < STARTUP_VS_RECOVERY_MIN_DEG:
		push_error("[rebuild-stepback] G3 FAILED: only %.1f deg (< %.1f) -- Startup's end pose and "
			% [g3_delta, STARTUP_VS_RECOVERY_MIN_DEG] + "Recovery's end pose do not read as distinct (#296).")
		quit(1)
		return

	# ── G5: the torso BAND at Active's end ───────────────────────────────────
	# Two-sided, unlike rebuild_retreatdribble_clips.gd's one-sided G5 -- see
	# the header comment and author_stepback.py's
	# `_verify_torso_band_at_active_end`. Active's OWN end pose, consistent
	# with G3's "compare named phase instants, not whole-clip endpoints"
	# discipline.
	var active_lean := _spine_head_forward(active, active.length)
	print("[rebuild-stepback] G5 torso-forward at Active-end = %.4f m (want within +/-%.4f of vertical)"
		% [active_lean, TORSO_BAND_AT_ACTIVE_M])
	if absf(active_lean) > TORSO_BAND_AT_ACTIVE_M:
		var side := "FORWARD (driving in)" if active_lean > 0.0 else "BACKWARD (reads as a fadeaway)"
		push_error(("[rebuild-stepback] G5 FAILED: at Active's end the torso projects %.4f m off vertical "
			% active_lean) + ("(band +/-%.4f), i.e. leaning %s. " % [TORSO_BAND_AT_ACTIVE_M, side])
			+ "Handoff 06: 'upright and squares to the rim -- deliberately NOT leaning back'. "
			+ "Retune torso_back_deg at author_stepback.py's Active row; do NOT widen this band.")
		quit(1)
		return

	# ── G6: Recovery -> Jumpshot hand-off ────────────────────────────────────
	# See the header comment for why this gate exists, why it measures WORLD-
	# SPACE LANDMARK POSITION rather than raw per-bone rotation, and where its
	# threshold comes from. jumpshotstartup already lives in `lib` (this SAME
	# AnimationLibrary, built by an earlier rebuild_jumpshot_clips.gd run) --
	# no new source FBX needed.
	if not lib.has_animation("jumpshotstartup"):
		push_error("[rebuild-stepback] G6 FAILED -- assets/locomotion.res has no 'jumpshotstartup' clip to "
			+ "measure the hand-off against. Run tools/rebuild_jumpshot_clips.gd first.")
		quit(1)
		return
	var jumpshot_startup: Animation = lib.get_animation("jumpshotstartup")
	# Measured RELATIVE TO EACH CLIP'S OWN HIPS, not in a shared world/armature
	# frame -- the first version of this gate compared absolute FK positions
	# and read RightFoot jumping 0.55-0.63 m, which turned out to be mostly a
	# COORDINATE-ANCHOR ARTIFACT, not a real pose difference: Hips is the root
	# bone, so its POSITION track carries author_stepback.py's own
	# `hips_base` -- a fixed point captured from Dribble.fbx's own armature at
	# authoring time. jumpshotstartup's Hips track was authored against a
	# DIFFERENT source FBX (Goalkeeper Catch Stationary.fbx) with no
	# guaranteed shared origin, so the two clips' root positions are offset by
	# a near-constant vector that has nothing to do with how the BODY is
	# posed -- and because Hips is the base of the FK chain, that offset
	# propagates into every other landmark's absolute position too. The same
	# reasoning already governs `_verify_both_feet_drift_forward` in
	# author_stepback.py (ankle position measured relative to Hips, never
	# absolute) -- this gate applies it symmetrically to BOTH clips being
	# compared, which cancels the anchor mismatch and leaves only genuine
	# body-relative shape.
	var hips_recovery := _pose_origin(recovery, recovery.length, "mixamorig_Hips")
	var hips_jumpshot := _pose_origin(jumpshot_startup, 0.0, "mixamorig_Hips")
	var g6_worst := 0.0
	var g6_worst_bone := ""
	for landmark in HANDOFF_LANDMARK_BONES:
		var rel_recovery := _pose_origin(recovery, recovery.length, landmark) - hips_recovery
		var rel_jumpshot := _pose_origin(jumpshot_startup, 0.0, landmark) - hips_jumpshot
		var jump_m := rel_recovery.distance_to(rel_jumpshot)
		print("[rebuild-stepback] G6   %-24s jump=%.4f m  recovery_rel=%s jumpshot_rel=%s"
			% [landmark, jump_m, rel_recovery, rel_jumpshot])
		if jump_m > g6_worst:
			g6_worst = jump_m
			g6_worst_bone = landmark
	# The Hips landmark itself is trivially zero relative to itself, so its
	# own vertical settle is checked separately, in WORLD space -- a genuine
	# hip-height difference at the cut (both characters stand on the same
	# floor) IS a real visual pop, unlike the horizontal anchor artifact above.
	var hip_height_jump_m := absf(hips_recovery.y - hips_jumpshot.y)
	print("[rebuild-stepback] G6   %-24s jump=%.4f m  (world Y only)" % ["mixamorig_Hips(height)", hip_height_jump_m])
	if hip_height_jump_m > g6_worst:
		g6_worst = hip_height_jump_m
		g6_worst_bone = "mixamorig_Hips(height)"
	print("[rebuild-stepback] G6 stepbackrecovery-end vs jumpshotstartup-start worst landmark jump = %.4f m (%s, want <= %.2f)"
		% [g6_worst, g6_worst_bone, RECOVERY_JUMPSHOT_HANDOFF_MAX_M])
	if g6_worst > RECOVERY_JUMPSHOT_HANDOFF_MAX_M:
		push_error(("[rebuild-stepback] G6 FAILED: %s jumped %.4f m (> %.2f) between StepBackRecovery's end "
			% [g6_worst_bone, g6_worst, RECOVERY_JUMPSHOT_HANDOFF_MAX_M])
			+ "pose and JumpshotStartup's start pose. Every AnimationTree transition is a hard cut "
			+ "(xfade_time 0), so this SNAPS visibly at the step-back -> jump-shot chain (#253). Retune the "
			+ "Recovery row's final keypose in author_stepback.py to land closer to jumpshotstartup's opening "
			+ "pose (ball at chest/gather height, feet set, hips beginning to rise).")
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
		push_error("[rebuild-stepback] ResourceSaver.save failed with error %d" % err)
		quit(1)
		return

	print("[rebuild-stepback] saved %s; clips now: %s" % [LIB_PATH, str(lib.get_animation_list())])
	quit(0)


func _name_tick_index(name: StringName) -> int:
	var s := String(name)
	if s.contains("startup"):
		return 0
	if s.contains("active"):
		return 1
	return 2


# ── Body axes (verbatim rebuild_retreatdribble_clips.gd / rebuild_jabstep_clips.gd
# approach) ────────────────────────────────────────────────────────────────────
func _derive_body_axes() -> bool:
	var foot := _skel.find_bone("mixamorig_LeftFoot")
	var toe := _skel.find_bone("mixamorig_LeftToeBase")
	if foot < 0 or toe < 0:
		push_error("[rebuild-stepback] Y Bot has no LeftFoot/LeftToeBase to derive facing from.")
		return false
	var forward: Vector3 = _skel.get_bone_global_rest(toe).origin - _skel.get_bone_global_rest(foot).origin
	forward.y = 0.0
	if forward.length() < 0.001:
		push_error("[rebuild-stepback] Y Bot's foot->toe vector is vertical; cannot derive facing.")
		return false
	_forward = forward.normalized()
	_right = _forward.cross(_up).normalized()

	var lh := _skel.find_bone("mixamorig_LeftHand")
	var rh := _skel.find_bone("mixamorig_RightHand")
	if lh < 0 or rh < 0:
		push_error("[rebuild-stepback] Y Bot has no LeftHand/RightHand to verify the lateral axis.")
		return false
	var lh_lat: float = _skel.get_bone_global_rest(lh).origin.dot(_right)
	var rh_lat: float = _skel.get_bone_global_rest(rh).origin.dot(_right)
	print("[rebuild-stepback] axes: forward=%s right=%s | rest hand lateral L=%+.4f R=%+.4f"
		% [_forward, _right, lh_lat, rh_lat])
	if not (rh_lat > 0.0 and lh_lat < 0.0):
		push_error("[rebuild-stepback] the derived right axis puts the RIGHT hand at %+.4f and the LEFT "
			% rh_lat + "at %+.4f -- it is inverted (or the rig's bone names lie)." % lh_lat)
		return false
	return true


# Spine->Head vector's projection along `_forward`, at time `t` in `anim`, in
# metres. Used by G5 -- the same quantity author_stepback.py's own
# `_spine_head_forward_m` measures Blender-side, re-measured here on the SLICED
# Godot resource by manual FK.
func _spine_head_forward(anim: Animation, t: float) -> float:
	return (_pose_origin(anim, t, HEAD_BONE) - _pose_origin(anim, t, SPINE_BONE)).dot(_forward)


# ── Slicing (verbatim rebuild_retreatdribble_clips.gd / rebuild_jumpshot_clips.gd
# primitive) ───────────────────────────────────────────────────────────────────
func _slice(src: Animation, t0: float, t1: float, ticks: int) -> Animation:
	var out := Animation.new()
	out.loop_mode = Animation.LOOP_NONE
	out.length = float(ticks) / TPS

	for i in src.get_track_count():
		var type := src.track_get_type(i)
		if type != Animation.TYPE_ROTATION_3D and type != Animation.TYPE_POSITION_3D:
			# SCALE tracks are dropped deliberately -- Blender bakes full TRS,
			# and PlayerRigScaler's SetBonePoseScale would be overwritten every
			# frame by a per-bone scale track (README trap 13).
			continue

		var path := src.track_get_path(i)
		if bone_of(path) == "":
			# The bare "Armature" object-node tracks -- Player.tscn's rig has
			# no such node, so these resolve against nothing.
			continue

		var t := out.add_track(type)
		out.track_set_path(t, _rebase_path(path))
		for k in ticks + 1:
			var u := float(k) / float(ticks)
			var st: float = lerpf(t0, t1, u)
			var dt := float(k) / TPS
			match type:
				Animation.TYPE_ROTATION_3D:
					out.rotation_track_insert_key(t, dt, src.rotation_track_interpolate(i, st))
				Animation.TYPE_POSITION_3D:
					out.position_track_insert_key(t, dt, src.position_track_interpolate(i, st))
	return out


# The a45bd1d guard: a slice that lost bone tracks would rest-pose the missing
# bones the moment its state was entered, and it would do so silently.
func _assert_complete(anim: Animation, name: StringName, expected_rot: int, expected_total: int, form_counts: Dictionary) -> bool:
	var rot := _rotation_track_count(anim)
	var unresolved := []
	var bad_shape := []
	for i in anim.get_track_count():
		var path := anim.track_get_path(i)
		var b := bone_of(path)
		if b == "":
			bad_shape.append(String(path))
			continue
		if String(path).begins_with(ARMATURE_PREFIX):
			bad_shape.append(String(path))
			continue
		var res := _resolve_bone(b)
		var form: String = res[1]
		form_counts[form] = form_counts[form] + 1
		if res[0] < 0:
			unresolved.append(b)
	print("[rebuild-stepback]   '%s': len=%.4f tracks=%d rot=%d loop=%d unresolved=%s"
		% [name, anim.length, anim.get_track_count(), rot, anim.loop_mode, str(unresolved)])
	if rot != expected_rot or anim.get_track_count() != expected_total:
		push_error("[rebuild-stepback] '%s' has %d/%d tracks, source has %d/%d -- a slice must inherit "
			% [name, rot, anim.get_track_count(), expected_rot, expected_total]
			+ "the source's FULL body coverage or the untracked bones rest-pose at full weight.")
		return false
	if unresolved.size() > 0:
		push_error("[rebuild-stepback] '%s' has tracks that do not resolve on Y Bot in EITHER name form: %s"
			% [name, str(unresolved)])
		return false
	if bad_shape.size() > 0:
		push_error("[rebuild-stepback] '%s' has %d track(s) whose NODE PATH cannot bind on "
			% [name, bad_shape.size()]
			+ "scenes/Player.tscn (skeleton at 'Skeleton3D', no 'Armature' wrapper): %s."
			% str(bad_shape))
		return false
	return true


func _rotation_track_count(anim: Animation) -> int:
	var n := 0
	for i in anim.get_track_count():
		if anim.track_get_type(i) == Animation.TYPE_ROTATION_3D:
			n += 1
	return n


# Largest per-bone angular difference between a SINGLE named instant in clip
# `a` (time `ta`) and a single named instant in clip `b` (time `tb`).
func _pose_delta_at(a: Animation, ta: float, b: Animation, tb: float) -> float:
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
		var qa: Quaternion = a.rotation_track_interpolate(i, ta)
		var qb: Quaternion = b.rotation_track_interpolate(j, tb)
		var d: float = clampf(absf(qa.normalized().dot(qb.normalized())), -1.0, 1.0)
		worst = maxf(worst, rad_to_deg(2.0 * acos(d)))
	return worst


# Global origin of `bone` with `anim` applied at time `t`, by manual forward
# kinematics (verbatim rebuild_retreatdribble_clips.gd / rebuild_jabstep_clips.gd
# / rebuild_contest_clips.gd approach — see their headers for why not
# get_bone_global_pose()).
func _pose_origin(anim: Animation, t: float, bone: String) -> Vector3:
	var res := _resolve_bone(bone)
	var idx: int = res[0]
	if idx < 0:
		return Vector3.ZERO

	var rot_track_of := {}
	var pos_track_of := {}
	for i in anim.get_track_count():
		var ty := anim.track_get_type(i)
		if ty != Animation.TYPE_ROTATION_3D and ty != Animation.TYPE_POSITION_3D:
			continue
		var b_res := _resolve_bone(bone_of(anim.track_get_path(i)))
		var b: int = b_res[0]
		if b < 0:
			continue
		if ty == Animation.TYPE_ROTATION_3D:
			rot_track_of[b] = i
		else:
			pos_track_of[b] = i

	var chain := []
	var walk := idx
	while walk >= 0:
		chain.push_front(walk)
		walk = _skel.get_bone_parent(walk)

	var acc := Transform3D.IDENTITY
	for b in chain:
		var rest: Transform3D = _skel.get_bone_rest(b)
		var local := rest
		if rot_track_of.has(b):
			var q: Quaternion = anim.rotation_track_interpolate(rot_track_of[b], t)
			local = Transform3D(Basis(q).scaled(rest.basis.get_scale()), rest.origin)
		if pos_track_of.has(b):
			local.origin = anim.position_track_interpolate(pos_track_of[b], t)
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
