extends SceneTree
# Asset build tool (#311) — drafts the drive-gather clip family into
# assets/locomotion.res by SLICING assets/drivegather_authored.fbx.
#
# Run:  godot --headless --path . -s tools/rebuild_drivegather_clips.gd
# Idempotent: re-running re-derives all three clips from the pristine FBX
# rather than stacking edits (the previous build is removed before the new
# one lands).
#
# Produces THREE LOOP_NONE one-shots — drive-gather is UNHANDED (it ENDS the
# dribble; there is no ball transit and no second polarity), so — like retreat
# dribble (#305), step-back (#306), jab step (#304), layup (#313), contest
# (#314), hesitation (#307) and spin (#310) — this tool slices THREE clips, not
# six:
#   drivegatherstartup    6 ticks  / 0.10000 s
#   drivegatheractive     10 ticks / 0.16667 s
#   drivegatherrecovery   14 ticks / 0.23333 s
#
# ── Step-back's machinery, one move forward ──────────────────────────────────
# tools/author_drivegather.py's docstring covers the full contrast with
# author_stepback.py (forward not backward, a Startup that LOADS BACK before it
# goes, per-hand arm channels, grounded throughout rather than airborne). This
# file inherits the SAME slicing/proof machinery rebuild_stepback_clips.gd
# established and adapts only what the motion actually differs on:
#
#   G4 is NEW — the two-hands-on-the-ball convergence, which is this move's
#      RULES SIGNAL rather than a look (see below).
#   G5 is a STEP LENGTH (ankle-to-ankle separation), where step-back's G5 was a
#      torso band.
#   G6 measures the hand-off into `layupstartup`, not `jumpshotstartup`.
#
# ── Why G4 exists at all, and why it carries its own control ─────────────────
# The gather is the frame after which the dribble is DEAD. ADR-0022 builds the
# rim-finishing vertical on it, and MoveAnimState's own doc names showing a
# live-dribble loop past that point as "an actively FALSE read, which is worse
# than no signal". So "both hands arrive on the ball" is a rules signal, and
# this tool asserts it rather than trusting the Blender side.
#
# It asserts the SEPARATION at Startup's end in the same gate. A convergence
# ceiling alone passes on a clip whose hands were never apart — which is exactly
# what the generic `locomotion/idle` fallback does, since it holds both arms in
# one fixed relationship for every phase. The premise is what makes the ceiling
# mean "they CAME together".
#
# ── Why this is a SLICE, not a compose ───────────────────────────────────────
# tools/author_drivegather.py (headless Blender, #315's blender_anim_lib
# machinery) already authored the full Startup/Active/Recovery arc as hand-keyed
# IK poses, baked at 60 Hz, on ONE timeline. This tool's job is therefore only
# to resample ("slice") the three named windows out of that timeline and then
# PROVE geometrically that what got sliced is what the issue asked for.
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
# skeleton's rest transform. author_drivegather.py's own
# `verify_all_bones_keyed(expected_count=52)` gate already proves the SOURCE
# carries full-body coverage; `_assert_complete()` below re-proves that every
# SLICE inherits that coverage verbatim rather than trusting the source's own
# proof to survive slicing.
#
# ── The `Armature/` prefix trap (README trap 13, #281) ───────────────────────
# Blender's FBX export wraps the skeleton in an Armature object, so a track
# imported from drivegather_authored.fbx reads
# "Armature/Skeleton3D:mixamorig_Hips" — one level deeper than
# scenes/Player.tscn's rig, whose skeleton sits directly at "Skeleton3D". An
# unresolvable track binds to nothing: the clip plays as a SILENT no-op — the
# state machine still enters the right state, the duration still checks out, and
# the mesh never moves. `_rebase_path()` strips the prefix on every track, and
# `_assert_complete()` rejects (not skips) any surviving `Armature/`-prefixed
# path or any path with no bone subname.
#
# ── Where the three windows come from ────────────────────────────────────────
# Hardcoded, not derived: author_drivegather.py's frame layout is DETERMINISTIC
# BY CONSTRUCTION — it keys its timeline at exact times computed from
# DriveGather's own frame data (6/10/14 ticks @ 60 Hz) and the import sets
# `trimming=false`, so those source times land exactly where the docstring says.
# This tool ASSERTS the guarantee (the source clip's total length) so a
# silently-retrimmed or wrong-fps import fails loudly instead of slicing garbage.
#
#   source seconds        segment      ticks
#   0.00000 -> 0.10000    Startup       6
#   0.10000 -> 0.26667    Active       10
#   0.26667 -> 0.50000    Recovery     14
#
# ── Cosmetic-only (issue #311's standing constraint) ─────────────────────────
# This tool writes ONE file: assets/locomotion.res. It reads no gameplay
# constant and changes no gameplay behaviour. In particular it never touches
# DriveGatherBurstSpeed, DriveGatherDecel, BallState or HasDribbled, so
# DriveGatherTest's `dead-dribble-gate` scenario is out of this file's reach by
# construction. The tick counts below are DUPLICATED from DriveGather's frame
# data for slicing, never read back into it.

const LIB_PATH := "res://assets/locomotion.res"
const SRC_FBX := "res://assets/drivegather_authored.fbx"
# Matches author_drivegather.py's ACTION_NAME -- export_fbx() renames both the
# Blender action AND the scene to this so Godot's importer names the resulting
# AnimationPlayer take after it.
const SRC_CLIP := "drivegather"

# Physics ticks per second (project.godot physics/common/physics_ticks_per_second).
const TPS := 60.0

# The node-path prefix Blender's Armature object wrapper adds; see _rebase_path.
const ARMATURE_PREFIX := "Armature/"

# DriveGather's frame data (scripts/Input/DriveGather.cs DefaultFrameData).
# Duplicated here because GDScript cannot read the C# constant -- so the
# duplication is made SAFE rather than avoided: DriveGatherAnimTest's
# `drivegather-segment-lengths` scenario asserts each clip's length equals
# DriveGather.DefaultFrameData's own tick count / 60, reading the C# side
# directly. Retune the move without re-running this tool and that harness
# scenario goes red and names this file.
const STARTUP_TICKS := 6
const ACTIVE_TICKS := 10
const RECOVERY_TICKS := 14

# Source-time windows, matching author_drivegather.py's frame table exactly
# (frame numbers there ARE physics ticks at 60 Hz: 0/6/16/30).
const STARTUP := [0.0 / 60.0, 6.0 / 60.0]
const ACTIVE := [6.0 / 60.0, 16.0 / 60.0]
const RECOVERY := [16.0 / 60.0, 30.0 / 60.0]

# The producer exports frame_start=0, frame_end=30 (TOTAL_TICKS in
# author_drivegather.py), so the imported clip's length must be ~30/60 s. A
# silently-retrimmed or wrong-fps import would shift every window above out from
# under the actual keyed poses.
const EXPECTED_SRC_LENGTH_S := 30.0 / 60.0
const SRC_LENGTH_TOL_S := 0.02

const NAMES := ["drivegatherstartup", "drivegatheractive", "drivegatherrecovery"]

# G3 legibility floor (#296's actual complaint -- Startup and Recovery must read
# as visibly different poses). Matches author_drivegather.py's own
# STARTUP_END_VS_RECOVERY_END_MIN_DEG = 15.0 gate -- this tool re-proves it on
# the SLICED clips rather than trusting the source's Blender-side proof to
# survive the slice untouched. Blender-side measured 58.179 deg.
const STARTUP_VS_RECOVERY_MIN_DEG := 15.0

# G4: the RULES SIGNAL -- see the header. Same two figures
# author_drivegather.py's `_verify_hand_convergence` asserts (Blender-side
# measured 0.5776 m apart / 0.2200 m together), re-measured here by manual FK on
# the SLICED Godot resource rather than trusted from the Blender side.
const WRIST_L_BONE := "mixamorig_LeftHand"
const WRIST_R_BONE := "mixamorig_RightHand"
const HANDS_CONVERGED_MAX_M := 0.30
const HANDS_APART_MIN_M := 0.40

# G5: the step length at Active's end -- the ankle-to-ankle FORE SEPARATION, not
# an ankle's offset from the hips. See author_drivegather.py's docstring for the
# leg-reach measurement that rules the latter reading out on this rig.
# Blender-side measured +0.7000 m.
const ANKLE_L_BONE := "mixamorig_LeftFoot"
const ANKLE_R_BONE := "mixamorig_RightFoot"
const STEP_LENGTH_MIN_M := 0.55

# G6: the recovery -> layup hand-off. PlayerController treats the finish as a
# SEPARATE "layup" request begun from the displaced position (the comment at
# PlayerController.cs:2332), so DriveGatherRecovery -> LayupStartup genuinely
# occurs at runtime, and every AnimationTree transition is a hard cut
# (`grep -c xfade_time scenes/Player.tscn` == 0) -- so a large pose
# discontinuity there SNAPS.
#
# Handoff 11 says whichever of #311/#313 lands second owns this assertion. #313
# landed first and did not take it, so it lives here.
#
# METRIC, and why it is NOT a raw per-bone rotation delta: inherited wholesale
# from rebuild_stepback_clips.gd's G6, whose header records the measurement.
# Raw per-bone rotation distance on round, twist-dominated limb segments is
# swamped by BONE-ROLL CONVENTION differences between authoring pipelines (a
# control comparison against the unrelated `idle` clip showed the same 150-180
# deg readings), and ABSOLUTE FK position is a coordinate-ANCHOR artifact (Hips
# is the root bone, so its position track carries each script's own `hips_base`,
# captured from a different source FBX with no shared origin). Measuring each
# landmark RELATIVE TO ITS OWN CLIP'S HIPS cancels both.
const HANDOFF_LANDMARK_BONES := ["mixamorig_LeftHand", "mixamorig_RightHand",
	"mixamorig_Head", "mixamorig_LeftFoot", "mixamorig_RightFoot"]

# Inherited from rebuild_stepback_clips.gd's RECOVERY_JUMPSHOT_HANDOFF_MAX_M
# rather than re-derived: the same metric, the same rig, the same "hard cut at
# the most-watched moment" claim, so a second number would be two thresholds for
# one question. Do NOT raise it to make a bad hand-off pass; retune
# drivegatherrecovery's final keypose in author_drivegather.py instead.
#
# MEASURED on the shipped clips: worst landmark 0.1851 m, which is 2.4x of
# headroom. The per-landmark breakdown is printed on every run and is worth
# reading, because the composition of that number is itself a finding:
#
#   LeftHand / RightHand   0.0000 m   authored AT layupstartup's own values
#   LeftFoot / RightFoot   0.0006 m   likewise
#   Head                   0.1038 m   see below
#   Hips (world Y)         0.1851 m   PRE-EXISTING, and not this clip's
#
# The Hips reading is a CROSS-SOURCE offset that #311 neither introduced nor can
# fix. `layupstartup` is authored off `Goalkeeper Catch Stationary.fbx`, whose
# standing hips_base sits at world Y 0.9239; every Dribble.fbx-sourced clip in
# this batch — drivegather, stepback, retreatdribble, hesitation, spin, jabstep,
# inandout, betweenthelegs — carries the dribble crouch's 0.6389 instead. So a
# ~0.19 m vertical hip pop already exists on EVERY transition into the layup,
# not just this one. Absorbing it here was considered and rejected: it would
# mean ending Recovery with the hips 0.185 m ABOVE the standing baseline, which
# reads as mid-jump and puts the lead leg past its 0.8270 m reach budget. Raised
# as a finding in #311's PR rather than hidden inside one clip's keypose table.
#
# The Head reading is this clip's own residual, already reduced from 0.3570 m —
# see author_drivegather.py's frame-30 row for the source-baseline trap that
# caused it.
const RECOVERY_LAYUP_HANDOFF_MAX_M := 0.45

var _skel: Skeleton3D = null
var _right := Vector3.ZERO
var _forward := Vector3.ZERO
var _up := Vector3.UP


func bone_of(np: NodePath) -> String:
	return "" if np.get_subname_count() == 0 else String(np.get_subname(0))


# The node path an AnimationPlayer under scenes/Player.tscn can actually
# resolve. See header — Blender's FBX export wraps the skeleton in an Armature
# object, so a track imported from drivegather_authored.fbx reads
# "Armature/Skeleton3D:mixamorig_Hips" — one level deeper than the rig.
func _rebase_path(np: NodePath) -> NodePath:
	var s := String(np)
	if s.begins_with(ARMATURE_PREFIX):
		return NodePath(s.substr(len(ARMATURE_PREFIX)))
	return np


# The Mixamo bone-name-prefix trap (see header): try the name as given, then the
# opposite colon/underscore form. Returns -1 only if NEITHER form resolves.
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
		push_error("[rebuild-drivegather] failed to load AnimationLibrary at %s" % LIB_PATH)
		quit(1)
		return

	_skel = _find(load("res://assets/Y Bot.fbx").instantiate(), "Skeleton3D")
	if _skel == null:
		push_error("[rebuild-drivegather] could not find a Skeleton3D in Y Bot.fbx.")
		quit(1)
		return

	if not _derive_body_axes():
		quit(1)
		return

	var packed = load(SRC_FBX)
	if packed == null:
		push_error("[rebuild-drivegather] failed to load %s" % SRC_FBX)
		quit(1)
		return
	var ap: AnimationPlayer = _find(packed.instantiate(), "AnimationPlayer")
	if ap == null:
		push_error("[rebuild-drivegather] %s has no AnimationPlayer." % SRC_FBX)
		quit(1)
		return
	if not ap.has_animation(SRC_CLIP):
		push_error("[rebuild-drivegather] %s has no AnimationPlayer clip '%s' (has: %s)"
			% [SRC_FBX, SRC_CLIP, str(ap.get_animation_list())])
		quit(1)
		return
	var src: Animation = ap.get_animation(SRC_CLIP)
	print("[rebuild-drivegather] source '%s': len=%.5f tracks=%d"
		% [SRC_CLIP, src.length, src.get_track_count()])

	# ── Sanity: the source must be the full 0..30-frame export, not a
	# silently-retrimmed or wrong-fps import.
	if absf(src.length - EXPECTED_SRC_LENGTH_S) > SRC_LENGTH_TOL_S:
		push_error("[rebuild-drivegather] source length %.5f s is not within %.3f s of the expected "
			% [src.length, SRC_LENGTH_TOL_S] + "%.5f s (30/60 s @ 60 fps) -- the import may have been "
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
	print("[rebuild-drivegather] source has %d tracks; %d expected per slice after dropping "
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
		print("[rebuild-drivegather] G1 '%s': length=%.5f (expect %.5f) loop_mode=%d %s"
			% [name, anim.length, expected_len, anim.loop_mode, "OK" if (len_ok and loop_ok) else "FAIL"])
		if not (len_ok and loop_ok):
			g1_ok = false
	if not g1_ok:
		push_error("[rebuild-drivegather] G1 FAILED -- one or more clips has the wrong length or loop_mode.")
		quit(1)
		return

	# ── G2: track-count parity + full bone resolution (both name forms) ──────
	var g2_ok := true
	var form_counts := {"as-given": 0, "alt": 0, "unresolved": 0}
	for name in built:
		if not _assert_complete(built[name], name, src_rot, src_total, form_counts):
			g2_ok = false
	print("[rebuild-drivegather] G2 bone-name resolution across all three clips: as-given=%d alt=%d unresolved=%d"
		% [form_counts["as-given"], form_counts["alt"], form_counts["unresolved"]])
	if not g2_ok:
		quit(1)
		return
	if form_counts["unresolved"] > 0:
		push_error("[rebuild-drivegather] G2 FAILED -- %d bone tracks did not resolve on Y Bot in EITHER "
			% form_counts["unresolved"] + "name form.")
		quit(1)
		return
	var resolved_total: int = form_counts["as-given"] + form_counts["alt"]
	if resolved_total <= 0:
		push_error("[rebuild-drivegather] G2 FAILED -- resolved count is %d; a name-matching gate that "
			% resolved_total + "matches nothing passes vacuously.")
		quit(1)
		return

	var startup: Animation = built[NAMES[0]]
	var active: Animation = built[NAMES[1]]
	var recovery: Animation = built[NAMES[2]]

	# ── G3: Startup's END pose vs Recovery's END pose ────────────────────────
	# NOT a whole-clip-start-vs-whole-clip-end comparison -- Startup's own LAST
	# frame (the deepest gather lean) vs Recovery's own LAST frame (the settled,
	# ready-to-rise stance) is the comparison that actually tests #296.
	var g3_delta := _pose_delta_at(startup, startup.length, recovery, recovery.length)
	print("[rebuild-drivegather] G3 startup-end vs recovery-end max bone delta = %.1f deg (want >= %.1f)"
		% [g3_delta, STARTUP_VS_RECOVERY_MIN_DEG])
	if g3_delta < STARTUP_VS_RECOVERY_MIN_DEG:
		push_error("[rebuild-drivegather] G3 FAILED: only %.1f deg (< %.1f) -- Startup's end pose and "
			% [g3_delta, STARTUP_VS_RECOVERY_MIN_DEG] + "Recovery's end pose do not read as distinct (#296).")
		quit(1)
		return

	# ── G4: the RULES SIGNAL -- one hand, then two ───────────────────────────
	# Both halves in one gate: the Startup separation is the PREMISE that makes
	# the Active convergence mean "they came together" rather than "they were
	# never apart". See the header.
	var gap_startup := _bone_gap_m(startup, startup.length, WRIST_L_BONE, WRIST_R_BONE)
	var gap_active := _bone_gap_m(active, active.length, WRIST_L_BONE, WRIST_R_BONE)
	print("[rebuild-drivegather] G4 wrist gap: startup-end=%.4f m (want >= %.2f) active-end=%.4f m (want <= %.2f)"
		% [gap_startup, HANDS_APART_MIN_M, gap_active, HANDS_CONVERGED_MAX_M])
	# Inverted comparisons: every comparison against NAN is false, so a poisoned
	# reading must be caught by an `if not (ok)` form or it SKIPS the gate and
	# passes (#310 needed three such guards and found the last only by mutation).
	if not (gap_startup >= HANDS_APART_MIN_M):
		push_error("[rebuild-drivegather] G4 FAILED: at Startup's end the wrists are %.4f m apart "
			% gap_startup + "(floor %.2f). Startup is a ONE-HANDED dribble; without a real separation "
			% HANDS_APART_MIN_M + "here the Active convergence proves nothing. (A NAN reading lands here too "
			+ "-- one of the landmark bones did not resolve.)")
		quit(1)
		return
	if not (gap_active <= HANDS_CONVERGED_MAX_M):
		push_error("[rebuild-drivegather] G4 FAILED: at Active's end the wrists are %.4f m apart "
			% gap_active + "(ceiling %.2f). The gather is the frame after which the DRIBBLE IS DEAD, and "
			% HANDS_CONVERGED_MAX_M + "handoff 11 requires the off-hand to come clearly ONTO the ball, not "
			+ "near it. Retune the Active row's lh_* channels in author_drivegather.py; do NOT widen this "
			+ "ceiling -- an ambiguous gather is the actively-false read MoveAnimState's own doc names.")
		quit(1)
		return

	# ── G5: the step length at Active's end ──────────────────────────────────
	var step_m := (_pose_origin(active, active.length, ANKLE_R_BONE)
		- _pose_origin(active, active.length, ANKLE_L_BONE)).dot(_forward)
	print("[rebuild-drivegather] G5 step length (lead R ankle - trail L ankle, along forward) at Active-end "
		+ "= %.4f m (want >= %.2f)" % [step_m, STEP_LENGTH_MIN_M])
	if not (step_m >= STEP_LENGTH_MIN_M):
		push_error("[rebuild-drivegather] G5 FAILED: %.4f m (< %.2f). Handoff 11: the gather step is the "
			% [step_m, STEP_LENGTH_MIN_M] + "biggest stride in the game -- it has to clear locomotion/run's "
			+ "own measured 0.6418 m. A NEGATIVE reading means the leg roles are swapped; a NAN one means a "
			+ "landmark bone did not resolve.")
		quit(1)
		return

	# ── G6: Recovery -> Layup hand-off ───────────────────────────────────────
	# `layupstartup` already lives in `lib` (this SAME AnimationLibrary, built by
	# an earlier rebuild_layup_clips.gd run) -- no new source FBX needed.
	if not lib.has_animation("layupstartup"):
		push_error("[rebuild-drivegather] G6 FAILED -- assets/locomotion.res has no 'layupstartup' clip to "
			+ "measure the hand-off against. Run tools/rebuild_layup_clips.gd first.")
		quit(1)
		return
	var layup_startup: Animation = lib.get_animation("layupstartup")
	var hips_recovery := _pose_origin(recovery, recovery.length, "mixamorig_Hips")
	var hips_layup := _pose_origin(layup_startup, 0.0, "mixamorig_Hips")
	if is_nan(hips_recovery.x) or is_nan(hips_layup.x):
		push_error("[rebuild-drivegather] G6 FAILED -- mixamorig_Hips did not resolve on the rig, so the "
			+ "hand-off measurement has no reference frame.")
		quit(1)
		return
	var g6_worst := 0.0
	var g6_worst_bone := ""
	for landmark in HANDOFF_LANDMARK_BONES:
		var rel_recovery := _pose_origin(recovery, recovery.length, landmark) - hips_recovery
		var rel_layup := _pose_origin(layup_startup, 0.0, landmark) - hips_layup
		var jump_m := rel_recovery.distance_to(rel_layup)
		print("[rebuild-drivegather] G6   %-24s jump=%.4f m  recovery_rel=%s layup_rel=%s"
			% [landmark, jump_m, rel_recovery, rel_layup])
		if is_nan(jump_m):
			push_error("[rebuild-drivegather] G6 FAILED -- landmark '%s' did not resolve; poisoned rather "
				% landmark + "than treated as a zero jump.")
			quit(1)
			return
		if jump_m > g6_worst:
			g6_worst = jump_m
			g6_worst_bone = landmark
	# The Hips landmark itself is trivially zero relative to itself, so its own
	# vertical settle is checked separately, in WORLD space -- a genuine
	# hip-height difference at the cut (both characters stand on the same floor)
	# IS a real visual pop, unlike the horizontal anchor artifact above.
	var hip_height_jump_m := absf(hips_recovery.y - hips_layup.y)
	print("[rebuild-drivegather] G6   %-24s jump=%.4f m  (world Y only)" % ["mixamorig_Hips(height)", hip_height_jump_m])
	if hip_height_jump_m > g6_worst:
		g6_worst = hip_height_jump_m
		g6_worst_bone = "mixamorig_Hips(height)"
	print("[rebuild-drivegather] G6 drivegatherrecovery-end vs layupstartup-start worst landmark jump = "
		+ "%.4f m (%s, want <= %.2f)" % [g6_worst, g6_worst_bone, RECOVERY_LAYUP_HANDOFF_MAX_M])
	if not (g6_worst <= RECOVERY_LAYUP_HANDOFF_MAX_M):
		push_error(("[rebuild-drivegather] G6 FAILED: %s jumped %.4f m (> %.2f) between DriveGatherRecovery's "
			% [g6_worst_bone, g6_worst, RECOVERY_LAYUP_HANDOFF_MAX_M])
			+ "end pose and LayupStartup's start pose. PlayerController begins the finish as a SEPARATE "
			+ "\"layup\" request from the displaced position (PlayerController.cs:2332) and every "
			+ "AnimationTree transition is a hard cut (xfade_time 0), so this SNAPS at the drive -> finish "
			+ "chain. Retune the Recovery row's final keypose in author_drivegather.py -- it is already "
			+ "authored at author_layup.py's own frame-0 channel values, so a regression here means one of "
			+ "the two tables moved.")
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
		push_error("[rebuild-drivegather] ResourceSaver.save failed with error %d" % err)
		quit(1)
		return

	print("[rebuild-drivegather] saved %s; clips now: %s" % [LIB_PATH, str(lib.get_animation_list())])
	quit(0)


func _name_tick_index(name: StringName) -> int:
	var s := String(name)
	if s.contains("startup"):
		return 0
	if s.contains("active"):
		return 1
	return 2


# ── Body axes (verbatim rebuild_stepback_clips.gd approach) ───────────────────
func _derive_body_axes() -> bool:
	var foot := _skel.find_bone("mixamorig_LeftFoot")
	var toe := _skel.find_bone("mixamorig_LeftToeBase")
	if foot < 0 or toe < 0:
		push_error("[rebuild-drivegather] Y Bot has no LeftFoot/LeftToeBase to derive facing from.")
		return false
	var forward: Vector3 = _skel.get_bone_global_rest(toe).origin - _skel.get_bone_global_rest(foot).origin
	forward.y = 0.0
	if forward.length() < 0.001:
		push_error("[rebuild-drivegather] Y Bot's foot->toe vector is vertical; cannot derive facing.")
		return false
	_forward = forward.normalized()
	_right = _forward.cross(_up).normalized()

	var lh := _skel.find_bone("mixamorig_LeftHand")
	var rh := _skel.find_bone("mixamorig_RightHand")
	if lh < 0 or rh < 0:
		push_error("[rebuild-drivegather] Y Bot has no LeftHand/RightHand to verify the lateral axis.")
		return false
	var lh_lat: float = _skel.get_bone_global_rest(lh).origin.dot(_right)
	var rh_lat: float = _skel.get_bone_global_rest(rh).origin.dot(_right)
	print("[rebuild-drivegather] axes: forward=%s right=%s | rest hand lateral L=%+.4f R=%+.4f"
		% [_forward, _right, lh_lat, rh_lat])
	if not (rh_lat > 0.0 and lh_lat < 0.0):
		push_error("[rebuild-drivegather] the derived right axis puts the RIGHT hand at %+.4f and the LEFT "
			% rh_lat + "at %+.4f -- it is inverted (or the rig's bone names lie)." % lh_lat)
		return false
	return true


# Straight-line distance between two bones' FK origins, at time `t` in `anim`.
# Returns NAN if either bone fails to resolve -- see `_pose_origin`.
func _bone_gap_m(anim: Animation, t: float, bone_a: String, bone_b: String) -> float:
	var a := _pose_origin(anim, t, bone_a)
	var b := _pose_origin(anim, t, bone_b)
	if is_nan(a.x) or is_nan(b.x):
		return NAN
	return a.distance_to(b)


# ── Slicing (verbatim rebuild_stepback_clips.gd / rebuild_jumpshot_clips.gd
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
			# The bare "Armature" object-node tracks -- Player.tscn's rig has no
			# such node, so these resolve against nothing.
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
	print("[rebuild-drivegather]   '%s': len=%.4f tracks=%d rot=%d loop=%d unresolved=%s"
		% [name, anim.length, anim.get_track_count(), rot, anim.loop_mode, str(unresolved)])
	if rot != expected_rot or anim.get_track_count() != expected_total:
		push_error("[rebuild-drivegather] '%s' has %d/%d tracks, source has %d/%d -- a slice must inherit "
			% [name, rot, anim.get_track_count(), expected_rot, expected_total]
			+ "the source's FULL body coverage or the untracked bones rest-pose at full weight.")
		return false
	if unresolved.size() > 0:
		push_error("[rebuild-drivegather] '%s' has tracks that do not resolve on Y Bot in EITHER name form: %s"
			% [name, str(unresolved)])
		return false
	if bad_shape.size() > 0:
		push_error("[rebuild-drivegather] '%s' has %d track(s) whose NODE PATH cannot bind on "
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


# Largest per-bone angular difference between a SINGLE named instant in clip `a`
# (time `ta`) and a single named instant in clip `b` (time `tb`).
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
# kinematics (verbatim rebuild_stepback_clips.gd approach — see its header for
# why not get_bone_global_pose()).
#
# Returns a NAN vector, NOT Vector3.ZERO, when the bone does not resolve. A Zero
# fallback makes an unresolvable bone read as "no gap" / "no jump" and print
# PASS while measuring nothing -- mutation-proven in #305, and the reason every
# caller above tests the result with an INVERTED comparison.
func _pose_origin(anim: Animation, t: float, bone: String) -> Vector3:
	var res := _resolve_bone(bone)
	var idx: int = res[0]
	if idx < 0:
		return Vector3(NAN, NAN, NAN)

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
