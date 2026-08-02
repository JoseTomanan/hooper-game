extends SceneTree
# Asset build tool (#314) — drafts the contest clip family into
# assets/locomotion.res by SLICING assets/contest_authored.fbx.
#
# Run:  godot --headless --path . -s tools/rebuild_contest_clips.gd
# Idempotent: re-running re-derives all three clips from the pristine FBX
# rather than stacking edits (the previous build is removed before the new
# one lands).
#
# Produces THREE LOOP_NONE one-shots — contest is UNHANDED (author_contest.py's
# own module docstring: the pose is symmetric BY DESIGN, both arms go up
# together, so there is no second polarity to author or prove distinct from
# this one). Like the layup family (#313) and unlike the dribble-move family,
# this tool therefore slices THREE clips, not six:
#   conteststartup    6 ticks / 0.10000 s
#   contestactive     8 ticks / 0.13333 s
#   contestrecovery  20 ticks / 0.33333 s
#
# ── What this move's gates prove, and why they are the INVERSE of layup's ────
# Contest and block raise the same arms. The ONLY thing that separates them
# visually is the feet: block leaves the ground, contest does not. That read is
# what the commitment ladder in ContestMove.cs:53-54 (contest 6 < steal 8 <
# block 10 startup ticks) is priced against — a contest that reads as a block
# is a contest the opponent cannot punish correctly, and the ladder collapses.
#
# So where rebuild_layup_clips.gd's load-bearing gate is "the Hips LEFT the
# ground" (G4 airborne) with a grounded-Startup control, this tool's
# load-bearing gate G4 is the opposite claim — GROUNDED across all three
# segments — and its control has to prove the opposite thing.
#
# ── Why G5/G6 exist: a grounded gate passes vacuously on a dead clip ─────────
# "The feet stayed on the floor" is the single most vacuously-satisfiable
# assertion in this repo: a rig that never moves at ALL satisfies it perfectly,
# as does a clip whose tracks failed to bind (README trap 15 — the mesh never
# moves and every duration/reachability assertion still passes). G4 therefore
# cannot stand alone. G5 asserts the POSITIVE half of the read — both wrists
# clear the head during Active — and G6 is its control, asserting they do NOT
# during Startup, so the overhead extension is a readable EVENT rather than a
# pose the clip holds throughout (ADR-0003 legibility).
#
# Together: G4 says "the feet never left", G5 says "the arms went up anyway",
# G6 says "and they were down beforehand". No two of the three pass on a clip
# that does nothing.
#
# ── Why this is a SLICE, not a compose (rebuild_layup_clips.gd /
# rebuild_steal_clips.gd precedent) ──────────────────────────────────────────
# tools/author_contest.py (headless Blender, #315's blender_anim_lib machinery)
# already authored the FULL Startup/Active/Recovery arc as hand-keyed IK poses,
# baked at 60 Hz, on ONE timeline. This tool's job is therefore only to
# resample ("slice") the three named windows out of that timeline —
# rebuild_jumpshot_clips.gd's `_slice()` primitive, copied verbatim — and then
# PROVE geometrically that what got sliced is what the issue asked for.
#
# The proofs are RE-RUN here rather than inherited from the Blender side on
# purpose. author_contest.py's gates measure Blender pose bones; these measure
# the SLICED Godot Animation resources by manual FK against Y Bot's rest pose.
# Everything between those two points — the FBX round-trip, the importer's
# fps/trimming/immutable-track settings, `_slice`'s resampling — is exactly the
# machinery that has silently corrupted clips in this repo before.
#
# ── The Mixamo bone-name-prefix trap (read before touching bone_of/_resolve) ─
# In Blender the bones are named `mixamorig:Hips` (colon) — see
# blender_anim_lib.py's HIPS/SPINE constants. Godot 4.6+'s `ufbx` importer
# imports Mixamo-prefixed bones as `mixamorig_Hips` (UNDERSCORE) instead. Both
# the source clip (imported from contest_authored.fbx) and the target skeleton
# (Y Bot.fbx) go through that same importer, so in practice both sides should
# already agree on the underscore form — but "should" is exactly the kind of
# claim this repo's convention says to prove, not assume. `_resolve_bone()`
# below therefore tries BOTH forms and reports which form actually matched and
# how many tracks needed it, so a silent zero-match can never hide behind a
# green run.
#
# ── The a45bd1d full-body-coverage trap ──────────────────────────────────────
# A single-clip AnimationTree state plays at FULL WEIGHT, and Godot's
# AnimationMixer writes every bone the active clip does NOT track to the
# skeleton's rest transform. author_contest.py's own
# `verify_all_bones_keyed(expected_count=52)` gate already proves the SOURCE
# carries full-body coverage (52 rotation tracks + 1 Hips position track, 53
# total — same shape as every other authored source in this family).
# `_assert_complete()` below re-proves that every SLICE inherits that coverage
# verbatim rather than trusting the source's own proof to survive slicing.
#
# ── The `Armature/` prefix trap (README trap 15, #281) ───────────────────────
# Blender's FBX export wraps the skeleton in an Armature object, so a track
# imported from contest_authored.fbx reads "Armature/Skeleton3D:mixamorig_Hips"
# — one level deeper than scenes/Player.tscn's rig, whose skeleton sits
# directly at "Skeleton3D". An unresolvable track binds to nothing: the clip
# plays as a SILENT no-op — the state machine still enters the right state,
# the duration still checks out, and the mesh never moves. `_rebase_path()`
# strips the prefix on every track, and `_assert_complete()` rejects (not
# skips) any surviving `Armature/`-prefixed path or any path with no bone
# subname, validating that paths bind as NODE PATHS on Player.tscn's shape,
# not merely as bone names.
#
# ── Where the three windows come from ────────────────────────────────────────
# Hardcoded, not derived: author_contest.py's frame layout is DETERMINISTIC BY
# CONSTRUCTION — it keys its timeline at exact times computed from ContestMove's
# own frame data (6/8/20 ticks @ 60 Hz) and the import sets `trimming=false`,
# so those source times land exactly where the docstring says. This tool
# ASSERTS the guarantee (the source clip's total length) so a silently-
# retrimmed or wrong-fps import fails loudly instead of slicing garbage.
#
#   source seconds        segment      ticks
#   0.00000 -> 0.10000    Startup       6
#   0.10000 -> 0.23333    Active        8
#   0.23333 -> 0.56667    Recovery     20
#
# ── Cosmetic-only (issue #314's standing constraint) ─────────────────────────
# This tool writes ONE file: assets/locomotion.res. It reads no gameplay
# constant and changes no gameplay behaviour. DefensiveResolution.Succeeds,
# StealReachRadius, the #214 reach gate, the on-ball contest scatter penalty
# and every ADR-0018 window are untouched — the tick counts below are
# DUPLICATED from ContestMove's frame data for slicing, never read back into
# it (see the STARTUP_TICKS comment).

const LIB_PATH := "res://assets/locomotion.res"
const SRC_FBX := "res://assets/contest_authored.fbx"
# Matches author_contest.py's ACTION_NAME -- export_fbx() renames both the
# Blender action AND the scene to this so Godot's importer names the
# resulting AnimationPlayer take after it.
const SRC_CLIP := "contest"

# Physics ticks per second (project.godot physics/common/physics_ticks_per_second).
const TPS := 60.0

# The node-path prefix Blender's Armature object wrapper adds; see _rebase_path.
const ARMATURE_PREFIX := "Armature/"

# ContestMove's frame data (scripts/Input/ContestMove.cs). Duplicated here
# because GDScript cannot read the C# constant -- so the duplication is made
# SAFE rather than avoided: ContestAnimTest's `contest-segment-lengths`
# scenario asserts each clip's length equals ContestMove.DefaultFrameData's own
# tick count / 60, reading the C# side directly. Retune the move without
# re-running this tool and that harness scenario goes red and names this file.
const STARTUP_TICKS := 6
const ACTIVE_TICKS := 8
const RECOVERY_TICKS := 20

# Source-time windows, matching author_contest.py's frame table exactly (frame
# numbers there ARE physics ticks at 60 Hz: 0/6/14/34).
const STARTUP := [0.0 / 60.0, 6.0 / 60.0]
const ACTIVE := [6.0 / 60.0, 14.0 / 60.0]
const RECOVERY := [14.0 / 60.0, 34.0 / 60.0]

# The producer exports frame_start=0, frame_end=34 (TOTAL_TICKS in
# author_contest.py), so the imported clip's length must be ~34/60 s. A
# silently-retrimmed or wrong-fps import would shift every window above out
# from under the actual keyed poses -- this is what makes that failure loud
# instead of quietly slicing garbage. (Godot's own generated .fbx.import
# defaults are fps=30 / trimming=true / remove_immutable_tracks=true, ALL
# THREE of which corrupt this; contest_authored.fbx.import overrides them.)
const EXPECTED_SRC_LENGTH_S := 34.0 / 60.0
const SRC_LENGTH_TOL_S := 0.02

const NAMES := ["conteststartup", "contestactive", "contestrecovery"]

# G3 legibility floor (#296's actual complaint -- Startup and Recovery must
# read as visibly different poses). Matches author_contest.py's own
# POSE_DISTINCT_MIN_DEG=15.0 gate -- this tool re-proves it on the SLICED
# clips rather than trusting the source's Blender-side proof to survive the
# slice untouched.
const STARTUP_VS_RECOVERY_MIN_DEG := 15.0

# G4 (the load-bearing gate): every one of the three clips stays GROUNDED --
# the toes never leave a tight vertical band above a floor reference measured
# ONCE across all three clips.
#
# The shared reference matters as much as the tolerance, and this is the part
# a naive per-clip check gets wrong: three independent grounded checks each
# establish their own floor, so a segment that floated uniformly 0.30 m above
# the other two would pass all three. Measuring the floor once over the whole
# family and holding every clip to it keeps per-clip failure attribution while
# making the gate strictly stronger. (author_contest.py's `ground_ref` does the
# same thing Blender-side, for the same reason.)
#
# Tolerance is 0.05 m rather than the Blender side's 0.02 m: this measurement
# path adds the FBX round-trip, the importer, and `_slice`'s resampling on top
# of the pose the authoring script measured directly. The stricter 0.02 m claim
# is already proven upstream; the job here is to catch a corruption of that
# clip, not to re-prove authoring precision through a lossier instrument.
const TOE_BONES := ["mixamorig_LeftToeBase", "mixamorig_RightToeBase"]
const GROUND_BAND_TOL_M := 0.05

# G5: the POSITIVE half of the read, and the reason G4 is not vacuous. The
# LOWER of the two wrists must clear the head during Active. The lower one on
# purpose: this clip is symmetric and the claim is "BOTH arms went up" --
# taking the higher wrist would let a clip that raised one arm and left the
# other down satisfy an "arms up" gate, which is a steal or a one-handed block
# silhouette, not a contest. Matches author_contest.py's
# WRIST_ABOVE_HEAD_MIN_M=0.10.
#
# Measured against the HEAD, not against the wrists' own Startup height: "arms
# raised" is a claim about the arms relative to the BODY (the layup G7 lesson
# -- a relative-only check read a healthy margin while the hand sat 0.029 m
# BELOW the head).
const WRIST_BONES := ["mixamorig_LeftHand", "mixamorig_RightHand"]
const HEAD_BONE := "mixamorig_Head"
const WRIST_ABOVE_HEAD_MIN_M := 0.10

# G6: G5's control. The same measurement must NOT already be satisfied during
# Startup -- the overhead extension has to be an EVENT the opponent can read,
# not a pose the clip holds throughout. Without this, G5 passes on a clip that
# begins and ends with the arms up, which telegraphs nothing (ADR-0003) and
# would also pass if the Startup slice had silently been cut from the Active
# window.
const WRIST_ABOVE_HEAD_STARTUP_MAX_M := 0.0

var _skel: Skeleton3D = null
var _right := Vector3.ZERO
var _forward := Vector3.ZERO
var _up := Vector3.UP


func bone_of(np: NodePath) -> String:
	return "" if np.get_subname_count() == 0 else String(np.get_subname(0))


# The node path an AnimationPlayer under scenes/Player.tscn can actually
# resolve. Blender's FBX export wraps the skeleton in an Armature object, so a
# track imported from contest_authored.fbx reads
# "Armature/Skeleton3D:mixamorig_Hips" -- one level deeper than the rig, whose
# skeleton is at "Skeleton3D". Every stock-Mixamo clip already in
# locomotion.res uses the short form, so this rebases onto that shape rather
# than inventing a third convention.
#
# This is NOT cosmetic. An unresolvable track binds to nothing, so the clip
# plays as a no-op: the state machine still enters the right state, the clip
# still reports the right duration, and the mesh never moves.
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


# Lowest toe height (along up) in `anim` at time `t`. The floor reading G4 is
# built from -- the toes, not the Hips, because a crouch lowers the hips
# without leaving the ground and a rise onto the balls of the feet raises them
# without landing. The feet ARE the read (see the header).
func _lowest_toe(anim: Animation, t: float) -> float:
	var lowest := INF
	for b in TOE_BONES:
		lowest = minf(lowest, _pose_origin(anim, t, b).dot(_up))
	return lowest


# Lower of the two wrists relative to the Head, in metres, at time `t`.
# See WRIST_ABOVE_HEAD_MIN_M for why the LOWER wrist and why vs. the head.
func _wrist_above_head(anim: Animation, t: float) -> float:
	var head_h := _pose_origin(anim, t, HEAD_BONE).dot(_up)
	var lowest := INF
	for b in WRIST_BONES:
		lowest = minf(lowest, _pose_origin(anim, t, b).dot(_up))
	return lowest - head_h


func _initialize() -> void:
	var lib = load(LIB_PATH)
	if lib == null or not (lib is AnimationLibrary):
		push_error("[rebuild-contest] failed to load AnimationLibrary at %s" % LIB_PATH)
		quit(1)
		return

	_skel = _find(load("res://assets/Y Bot.fbx").instantiate(), "Skeleton3D")
	if _skel == null:
		push_error("[rebuild-contest] could not find a Skeleton3D in Y Bot.fbx.")
		quit(1)
		return

	if not _derive_body_axes():
		quit(1)
		return

	var packed = load(SRC_FBX)
	if packed == null:
		push_error("[rebuild-contest] failed to load %s" % SRC_FBX)
		quit(1)
		return
	var ap: AnimationPlayer = _find(packed.instantiate(), "AnimationPlayer")
	if ap == null:
		push_error("[rebuild-contest] %s has no AnimationPlayer." % SRC_FBX)
		quit(1)
		return
	if not ap.has_animation(SRC_CLIP):
		push_error("[rebuild-contest] %s has no AnimationPlayer clip '%s' (has: %s)"
			% [SRC_FBX, SRC_CLIP, str(ap.get_animation_list())])
		quit(1)
		return
	var src: Animation = ap.get_animation(SRC_CLIP)
	print("[rebuild-contest] source '%s': len=%.5f tracks=%d"
		% [SRC_CLIP, src.length, src.get_track_count()])

	# ── Sanity: the source must be the full 0..34-frame export, not a
	# silently-retrimmed or wrong-fps import. This is what makes the hardcoded
	# windows above safe to trust (see header).
	if absf(src.length - EXPECTED_SRC_LENGTH_S) > SRC_LENGTH_TOL_S:
		push_error("[rebuild-contest] source length %.5f s is not within %.3f s of the expected "
			% [src.length, SRC_LENGTH_TOL_S] + "%.5f s (34/60 s @ 60 fps) -- the import may have been "
			% EXPECTED_SRC_LENGTH_S + "retrimmed or baked at the wrong fps, which would silently shift "
			+ "every hardcoded slice window in this file off the authored poses.")
		quit(1)
		return

	# What a SLICE is expected to carry, derived from the source by applying the
	# same two filters _slice() applies -- never the source's raw counts. The
	# source holds full TRS for every bone plus the Armature object node; a
	# slice keeps rotation+position for bone tracks only.
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
	print("[rebuild-contest] source has %d tracks; %d expected per slice after dropping "
		% [src.get_track_count(), src_total]
		+ "SCALE (fights PlayerRigScaler) and the Armature object node (unbindable on Player.tscn).")

	# ── Slice the three windows (verbatim rebuild_jumpshot_clips.gd primitive;
	# no swing/polarity composition -- the motion is already fully authored and
	# single-polarity, per author_contest.py's own module docstring) ──────────
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
		print("[rebuild-contest] G1 '%s': length=%.5f (expect %.5f) loop_mode=%d %s"
			% [name, anim.length, expected_len, anim.loop_mode, "OK" if (len_ok and loop_ok) else "FAIL"])
		if not (len_ok and loop_ok):
			g1_ok = false
	if not g1_ok:
		push_error("[rebuild-contest] G1 FAILED -- one or more clips has the wrong length or loop_mode.")
		quit(1)
		return

	# ── G2: track-count parity + full bone resolution (both name forms) ─────
	var g2_ok := true
	var form_counts := {"as-given": 0, "alt": 0, "unresolved": 0}
	for name in built:
		if not _assert_complete(built[name], name, src_rot, src_total, form_counts):
			g2_ok = false
	print("[rebuild-contest] G2 bone-name resolution across all three clips: as-given=%d alt=%d unresolved=%d"
		% [form_counts["as-given"], form_counts["alt"], form_counts["unresolved"]])
	if not g2_ok:
		quit(1)
		return
	if form_counts["unresolved"] > 0:
		push_error("[rebuild-contest] G2 FAILED -- %d bone tracks did not resolve on Y Bot in EITHER "
			% form_counts["unresolved"] + "name form.")
		quit(1)
		return
	var resolved_total: int = form_counts["as-given"] + form_counts["alt"]
	if resolved_total <= 0:
		push_error("[rebuild-contest] G2 FAILED -- resolved count is %d; a name-matching gate that "
			% resolved_total + "matches nothing passes vacuously.")
		quit(1)
		return

	var startup: Animation = built[NAMES[0]]
	var active: Animation = built[NAMES[1]]
	var recovery: Animation = built[NAMES[2]]

	# ── G3: Startup's END pose vs Recovery's END pose ────────────────────────
	# NOT a whole-clip-start-vs-whole-clip-end comparison -- Startup's own LAST
	# frame (the full tell) vs Recovery's own LAST frame (the settled balanced
	# stance) is the comparison that actually tests #296.
	var g3_delta := _pose_delta_at(startup, startup.length, recovery, recovery.length)
	print("[rebuild-contest] G3 startup-end vs recovery-end max bone delta = %.1f deg (want >= %.1f)"
		% [g3_delta, STARTUP_VS_RECOVERY_MIN_DEG])
	if g3_delta < STARTUP_VS_RECOVERY_MIN_DEG:
		push_error("[rebuild-contest] G3 FAILED: only %.1f deg (< %.1f) -- Startup's end pose and "
			% [g3_delta, STARTUP_VS_RECOVERY_MIN_DEG] + "Recovery's end pose do not read as distinct (#296).")
		quit(1)
		return

	# ── G4 (the load-bearing gate): GROUNDED on all three clips, against ONE
	# shared floor reference measured across the whole family. See the constant
	# for why the shared reference is what makes this stronger than three
	# independent per-clip checks. ───────────────────────────────────────────
	var samples := 8
	var ground_ref := INF
	for name in NAMES:
		var anim: Animation = built[name]
		for s in (samples + 1):
			var t: float = anim.length * float(s) / float(samples)
			ground_ref = minf(ground_ref, _lowest_toe(anim, t))
	print("[rebuild-contest] G4 shared ground reference (lowest toe across all three clips) = %.4f m" % ground_ref)

	var g4_ok := true
	for name in NAMES:
		var anim: Animation = built[name]
		var worst := 0.0
		for s in (samples + 1):
			var t: float = anim.length * float(s) / float(samples)
			worst = maxf(worst, _lowest_toe(anim, t) - ground_ref)
		print("[rebuild-contest] G4 '%s': worst toe excursion above the shared floor = %.4f m (want <= %.2f)"
			% [name, worst, GROUND_BAND_TOL_M])
		if worst > GROUND_BAND_TOL_M:
			push_error("[rebuild-contest] G4 FAILED: '%s' lifts the feet %.4f m (> %.2f) above the shared "
				% [name, worst, GROUND_BAND_TOL_M] + "floor -- contest has become a block. Contest and block "
				+ "raise the same arms; the feet are the entire legibility read, and the commitment ladder "
				+ "in ContestMove.cs (contest 6 < steal 8 < block 10 startup) is priced on it.")
			g4_ok = false
	if not g4_ok:
		quit(1)
		return

	# ── G5: the POSITIVE half of the read -- the lower wrist clears the head
	# during Active. Without this, G4 passes perfectly on a clip that never
	# moves at all (see the header's vacuity section). Sampled for the BEST
	# frame in the window, because the apex is a moment, not the whole clip. ──
	var g5_best := -INF
	for s in (samples + 1):
		var t: float = active.length * float(s) / float(samples)
		g5_best = maxf(g5_best, _wrist_above_head(active, t))
	print("[rebuild-contest] G5 arms up in Active: best lower-wrist-above-head = %+.4f m (want >= %.2f)"
		% [g5_best, WRIST_ABOVE_HEAD_MIN_M])
	if g5_best < WRIST_ABOVE_HEAD_MIN_M:
		push_error("[rebuild-contest] G5 FAILED: the LOWER wrist peaked only %+.4f m above the head "
			% g5_best + "during Active (want >= %.2f) -- the arms never went up, so this is not a "
			% WRIST_ABOVE_HEAD_MIN_M + "contest. Note that G4 would still PASS on this clip, which is "
			+ "exactly why this gate exists.")
		quit(1)
		return

	# ── G6: G5's control -- the arms must NOT already be up during Startup, or
	# the overhead extension is a pose the clip holds rather than an event the
	# opponent can read (ADR-0003). Also catches a Startup slice accidentally
	# cut from the Active window. ────────────────────────────────────────────
	var g6_best := -INF
	for s in (samples + 1):
		var t: float = startup.length * float(s) / float(samples)
		g6_best = maxf(g6_best, _wrist_above_head(startup, t))
	print("[rebuild-contest] G6 control -- arms DOWN in Startup: best lower-wrist-above-head = %+.4f m (want <= %.2f)"
		% [g6_best, WRIST_ABOVE_HEAD_STARTUP_MAX_M])
	if g6_best > WRIST_ABOVE_HEAD_STARTUP_MAX_M:
		push_error("[rebuild-contest] G6 FAILED: the arms were already %+.4f m above the head during "
			% g6_best + "Startup (ceiling %.2f) -- the overhead extension has to be an EVENT the opponent "
			% WRIST_ABOVE_HEAD_STARTUP_MAX_M + "can read, not a pose the clip holds throughout, or the "
			+ "wind-up telegraphs nothing (ADR-0003). G5 would still pass on such a clip.")
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
		push_error("[rebuild-contest] ResourceSaver.save failed with error %d" % err)
		quit(1)
		return

	print("[rebuild-contest] saved %s; clips now: %s" % [LIB_PATH, str(lib.get_animation_list())])
	quit(0)

func _name_tick_index(name: StringName) -> int:
	var s := String(name)
	if s.contains("startup"):
		return 0
	if s.contains("active"):
		return 1
	return 2


# ── Body axes (verbatim rebuild_steal_clips.gd / rebuild_behindtheback_clips.gd
# approach) ────────────────────────────────────────────────────────────────────
# Derived from Y Bot's own REST pose, never from scenes/Player.tscn --
# BlendRestAnchor.cs re-anchors the UpLeg rests at runtime, and every
# foot/toe global rest downstream inherits the error (119.6 deg / 2.17x stride
# mismeasurement in #298). Checked, not assumed: forward.cross(up) points to
# this rig's right (the #255 lesson), verified below against the rest hand
# positions.
func _derive_body_axes() -> bool:
	var foot := _skel.find_bone("mixamorig_LeftFoot")
	var toe := _skel.find_bone("mixamorig_LeftToeBase")
	if foot < 0 or toe < 0:
		push_error("[rebuild-contest] Y Bot has no LeftFoot/LeftToeBase to derive facing from.")
		return false
	var forward: Vector3 = _skel.get_bone_global_rest(toe).origin - _skel.get_bone_global_rest(foot).origin
	forward.y = 0.0
	if forward.length() < 0.001:
		push_error("[rebuild-contest] Y Bot's foot->toe vector is vertical; cannot derive facing.")
		return false
	_forward = forward.normalized()
	_right = _forward.cross(_up).normalized()

	var lh := _skel.find_bone("mixamorig_LeftHand")
	var rh := _skel.find_bone("mixamorig_RightHand")
	if lh < 0 or rh < 0:
		push_error("[rebuild-contest] Y Bot has no LeftHand/RightHand to verify the lateral axis.")
		return false
	var lh_lat: float = _skel.get_bone_global_rest(lh).origin.dot(_right)
	var rh_lat: float = _skel.get_bone_global_rest(rh).origin.dot(_right)
	print("[rebuild-contest] axes: forward=%s right=%s | rest hand lateral L=%+.4f R=%+.4f"
		% [_forward, _right, lh_lat, rh_lat])
	if not (rh_lat > 0.0 and lh_lat < 0.0):
		push_error("[rebuild-contest] the derived right axis puts the RIGHT hand at %+.4f and the LEFT "
			% rh_lat + "at %+.4f -- it is inverted (or the rig's bone names lie)." % lh_lat)
		return false
	return true


# ── Slicing (verbatim rebuild_jumpshot_clips.gd / rebuild_steal_clips.gd
# primitive) ──────────────────────────────────────────────────────────────────
# Resamples source range [t0, t1] into a clip of exactly `ticks` ticks at
# 60 tps, one key per gameplay tick (ticks + 1 keys, the last landing exactly
# on `length`). Keying at the tick rate rather than copying the source's own
# key times is what ties the clip to the move's frame data.
func _slice(src: Animation, t0: float, t1: float, ticks: int) -> Animation:
	var out := Animation.new()
	# Explicit, not inherited: the FBX import default happens to agree (these
	# ARE one-shots) -- which is exactly why it must not be inherited silently.
	out.loop_mode = Animation.LOOP_NONE
	out.length = float(ticks) / TPS

	for i in src.get_track_count():
		var type := src.track_get_type(i)
		if type != Animation.TYPE_ROTATION_3D and type != Animation.TYPE_POSITION_3D:
			# SCALE tracks are dropped deliberately, not overlooked. Blender's
			# exporter bakes full TRS for every bone, so the source carries 65
			# scale tracks -- all identity (the authoring script's own
			# verify_pose_unscaled measured this). Keeping them would be worse
			# than useless: PlayerRigScaler applies the height/wingspan chains
			# via SetBonePoseScale, which writes the ANIMATED pose, so a
			# per-bone scale track overwrites it every frame the clip plays.
			continue

		var path := src.track_get_path(i)
		if bone_of(path) == "":
			# The bare "Armature" object-node tracks. Blender's FBX export
			# wraps the skeleton in an Armature object, and Godot imports it as
			# a real node, so the source holds position/rotation/scale tracks
			# for the object ITSELF. Player.tscn's rig has no such node -- its
			# skeleton sits directly at "Skeleton3D" -- so these resolve
			# against nothing.
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
# `form_counts` accumulates which bone-name spelling actually resolved, across
# all three clips, so the caller can print an honest cross-clip match report
# instead of a single clip's number that could get lucky.
func _assert_complete(anim: Animation, name: StringName, expected_rot: int, expected_total: int, form_counts: Dictionary) -> bool:
	var rot := _rotation_track_count(anim)
	var unresolved := []
	var bad_shape := []
	for i in anim.get_track_count():
		var path := anim.track_get_path(i)
		var b := bone_of(path)
		if b == "":
			# NOT a `continue`. A gate that skips every subname-less path
			# silently exempts precisely the tracks that were broken -- the
			# bare "Armature" object-node tracks -- and would report
			# "unresolved=[]" while every track in the clip failed to bind at
			# runtime (README trap 15, proven by #281). A track with no bone
			# subname has no business in a skeletal clip; say so instead of
			# looking away.
			bad_shape.append(String(path))
			continue
		if String(path).begins_with(ARMATURE_PREFIX):
			# Resolves as a BONE NAME but not as a NODE PATH: Player.tscn's
			# skeleton is at "Skeleton3D", not "Armature/Skeleton3D". Checking
			# only the bone name is what made the original #281 gate blind to
			# this.
			bad_shape.append(String(path))
			continue
		var res := _resolve_bone(b)
		var form: String = res[1]
		form_counts[form] = form_counts[form] + 1
		if res[0] < 0:
			unresolved.append(b)
	print("[rebuild-contest]   '%s': len=%.4f tracks=%d rot=%d loop=%d unresolved=%s"
		% [name, anim.length, anim.get_track_count(), rot, anim.loop_mode, str(unresolved)])
	if rot != expected_rot or anim.get_track_count() != expected_total:
		push_error("[rebuild-contest] '%s' has %d/%d tracks, source has %d/%d -- a slice must inherit "
			% [name, rot, anim.get_track_count(), expected_rot, expected_total]
			+ "the source's FULL body coverage or the untracked bones rest-pose at full weight.")
		return false
	if unresolved.size() > 0:
		push_error("[rebuild-contest] '%s' has tracks that do not resolve on Y Bot in EITHER name form: %s"
			% [name, str(unresolved)])
		return false
	if bad_shape.size() > 0:
		push_error("[rebuild-contest] '%s' has %d track(s) whose NODE PATH cannot bind on "
			% [name, bad_shape.size()]
			+ "scenes/Player.tscn (skeleton at 'Skeleton3D', no 'Armature' wrapper): %s. "
			% str(bad_shape)
			+ "Such a track binds to nothing and the clip plays as a silent no-op -- the state "
			+ "machine still enters, the duration still checks out, and the mesh never moves.")
		return false
	return true


func _rotation_track_count(anim: Animation) -> int:
	var n := 0
	for i in anim.get_track_count():
		if anim.track_get_type(i) == Animation.TYPE_ROTATION_3D:
			n += 1
	return n


# Largest per-bone angular difference between a SINGLE named instant in clip
# `a` (time `ta`) and a single named instant in clip `b` (time `tb`). Used for
# G3's specific "Startup's END pose vs Recovery's END pose" comparison -- two
# fixed poses, not two trajectories.
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
# kinematics.
#
# Deliberately NOT get_bone_global_pose(): a Skeleton3D that was never added to
# the SceneTree does not recompute its global poses, so that call returns the
# unchanged rest transform and every geometric proof built on it passes
# vacuously at exactly 0.0000 (measured, #285). Manual FK depends on nothing
# but the rest pose and the clip's own keys.
#
# BOTH ROTATION_3D and POSITION_3D tracks are walked (verbatim
# rebuild_steal_clips.gd's version, not rebuild_behindtheback_clips.gd's
# rotation-only one): the Hips carry the clip's only POSITION_3D track, and
# every bone downstream of them -- including the TOES that G4's grounded band
# is measured on -- inherits that translation through the chain. Dropping it
# would leave the toes pinned at their rest height, so G4 would read a
# perfectly flat 0.0000 m excursion on ANY clip and pass vacuously. So a
# POSITION_3D key REPLACES `rest.origin` for its bone, the same way a
# ROTATION_3D key REPLACES the rest rotation.
#
# Bone lookups go through `_resolve_bone()` so a track authored under either
# the colon or underscore Mixamo prefix form still walks the correct chain
# (see the header trap).
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
		# ROTATION_3D keys are absolute LOCAL rotations, so an animated bone
		# REPLACES the rest basis' rotation; scale carries over. POSITION_3D
		# keys likewise REPLACE the rest origin (only Hips carries one here).
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
