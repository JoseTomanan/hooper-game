extends SceneTree
# Asset build tool (#283) — drafts the block clip family into
# assets/locomotion.res by SLICING assets/block_authored.fbx.
#
# Run:  godot --headless --path . -s tools/rebuild_block_clips.gd
# Idempotent: re-running re-derives all three clips from the pristine FBX
# rather than stacking edits (the previous build is removed before the new
# one lands).
#
# Produces THREE LOOP_NONE one-shots — block is UNHANDED (a block reads
# symmetrically; grill decision, #276, restated in docs/handoffs/anim-clips/
# 03-block.md). Like the contest family (#314) and the layup family (#313), and
# unlike the dribble-move family, this tool therefore slices THREE clips:
#   blockstartup    10 ticks / 0.16667 s
#   blockactive      8 ticks / 0.13333 s
#   blockrecovery   20 ticks / 0.33333 s
#
# ── What this move's gates prove, and why they INVERT contest's ──────────────
# Block and contest raise the same arms off the same source FBX. The ONLY thing
# separating them visually is the feet: BLOCK LEAVES THE GROUND, CONTEST DOES
# NOT. That read is what the commitment ladder in ContestMove.cs:53-54 (contest
# 6 < steal 8 < block 10 startup ticks) is priced against — a block that reads
# as a contest is a block the offence cannot punish correctly, and the ladder
# collapses.
#
# So where rebuild_contest_clips.gd's load-bearing gate G4 is "GROUNDED across
# all three segments", this tool's load-bearing G4 is the opposite claim —
# AIRBORNE during Active — and its control (G5) has to prove the opposite thing.
#
# ── Why G5 exists: an airborne gate hides its vacuity in the BASELINE ────────
# "The hips rose" is not vacuously satisfiable the way "the feet stayed down"
# is. But it is a DIFFERENCE, and the subtrahend is where the vacuity hides:
# measuring the Active window's peak against that same window's own minimum
# proves nothing, because every sample in it is already elevated. (This is not
# hypothetical — blender_anim_lib.verify_airborne raises rather than defaulting
# `ref_height`, specifically because the convenient default is the wrong one.)
#
# G4 therefore measures the rise against a reference taken from a DIFFERENT
# clip: the Hips height at the start of `blockstartup`. G5 is what earns that
# reference — it asserts the toes stay planted across the whole Startup window
# and across the back of Recovery, so the Startup pose is a genuine ground
# level rather than a number read off the jump itself.
#
# Together: G4 says "the body left the floor", G5 says "and the floor was
# really the floor, before and after". Neither is worth much alone.
#
# ── Why G6/G7 exist: a leap with the hands down is a box-out ─────────────────
# Vertical displacement is the PRIMARY read (the issue says so explicitly), but
# it is not the whole silhouette. G6 asserts the positive second half — both
# wrists clear the head during Active — and G7 is its control, asserting they do
# NOT during Startup, so the overhead extension is a readable EVENT rather than
# a pose the clip holds throughout (ADR-0003 legibility).
#
# G6 takes the LOWER of the two wrists, never the higher (README trap 17). This
# clip is symmetric and the claim is "BOTH arms went up"; taking the maximum
# would let a one-armed clip satisfy a two-armed gate, and a one-armed overhead
# pose is a steal or a one-handed swat, not this move. On a symmetric clip
# min == max, which is precisely why the wrong reduction is invisible in a green
# run and only surfaces once the clip becomes asymmetric — i.e. at exactly the
# moment the gate was supposed to catch something.
#
# ── Why this is a SLICE, not a compose (rebuild_contest_clips.gd precedent) ──
# tools/author_block.py (headless Blender, #315's blender_anim_lib machinery)
# already authored the FULL Startup/Active/Recovery arc as hand-keyed IK poses,
# baked at 60 Hz, on ONE timeline. This tool's job is therefore only to resample
# ("slice") the three named windows out of that timeline —
# rebuild_jumpshot_clips.gd's `_slice()` primitive, copied verbatim — and then
# PROVE geometrically that what got sliced is what the issue asked for.
#
# The proofs are RE-RUN here rather than inherited from the Blender side on
# purpose. author_block.py's gates measure Blender pose bones; these measure the
# SLICED Godot Animation resources by manual FK against Y Bot's rest pose.
# Everything between those two points — the FBX round-trip, the importer's
# fps/trimming/immutable-track settings, `_slice`'s resampling — is exactly the
# machinery that has silently corrupted clips in this repo before.
#
# ── The Mixamo bone-name-prefix trap (read before touching bone_of/_resolve) ─
# In Blender the bones are named `mixamorig:Hips` (colon). Godot's `ufbx`
# importer imports Mixamo-prefixed bones as `mixamorig_Hips` (UNDERSCORE). Both
# the source clip (imported from block_authored.fbx) and the target skeleton
# (Y Bot.fbx) go through that same importer, so in practice both sides should
# already agree on the underscore form — but "should" is exactly the kind of
# claim this repo's convention says to prove, not assume. `_resolve_bone()`
# tries BOTH forms and reports which one matched and how many tracks needed it,
# so a silent zero-match can never hide behind a green run.
#
# ── The a45bd1d full-body-coverage trap ──────────────────────────────────────
# A single-clip AnimationTree state plays at FULL WEIGHT, and Godot's
# AnimationMixer writes every bone the active clip does NOT track to the
# skeleton's rest transform. author_block.py's own
# `verify_all_bones_keyed(expected_count=52)` gate proves the SOURCE carries
# full-body coverage (52 rotation tracks + 1 Hips position track).
# `_assert_complete()` below re-proves that every SLICE inherits that coverage
# verbatim rather than trusting the source's own proof to survive slicing.
#
# ── The `Armature/` prefix trap (README trap 13/15, #281) ────────────────────
# Blender's FBX export wraps the skeleton in an Armature object, so a track
# imported from block_authored.fbx reads "Armature/Skeleton3D:mixamorig_Hips" —
# one level deeper than scenes/Player.tscn's rig, whose skeleton sits directly
# at "Skeleton3D". An unresolvable track binds to nothing: the clip plays as a
# SILENT no-op — the state machine still enters the right state, the duration
# still checks out, and the mesh never moves. `_rebase_path()` strips the prefix
# on every track, and `_assert_complete()` REJECTS (not skips) any surviving
# `Armature/`-prefixed path or any path with no bone subname.
#
# ── Where the three windows come from ────────────────────────────────────────
# Hardcoded, not derived: author_block.py's frame layout is DETERMINISTIC BY
# CONSTRUCTION — it keys its timeline at exact times computed from BlockMove's
# own frame data (10/8/20 ticks @ 60 Hz) and the import sets `trimming=false`,
# so those source times land exactly where the docstring says. This tool
# ASSERTS the guarantee (the source clip's total length) so a silently-
# retrimmed or wrong-fps import fails loudly instead of slicing garbage.
#
#   source seconds        segment      ticks
#   0.00000 -> 0.16667    Startup      10
#   0.16667 -> 0.30000    Active        8
#   0.30000 -> 0.63333    Recovery     20
#
# ── Cosmetic-only (issue #283's standing constraint) ─────────────────────────
# This tool writes ONE file: assets/locomotion.res. It reads no gameplay
# constant and changes no gameplay behaviour. DefensiveResolution.Succeeds, the
# #214 block reach gate, BlockMove.DefaultBlockGraceTicks and every ADR-0018
# window are untouched — the tick counts below are DUPLICATED from BlockMove's
# frame data for slicing, never read back into it (see the STARTUP_TICKS
# comment).

const LIB_PATH := "res://assets/locomotion.res"
const SRC_FBX := "res://assets/block_authored.fbx"
# Matches author_block.py's ACTION_NAME -- export_fbx() renames both the Blender
# action AND the scene to this so Godot's importer names the resulting
# AnimationPlayer take after it.
const SRC_CLIP := "block"

# Physics ticks per second (project.godot physics/common/physics_ticks_per_second).
const TPS := 60.0

# The node-path prefix Blender's Armature object wrapper adds; see _rebase_path.
const ARMATURE_PREFIX := "Armature/"

# BlockMove's frame data (scripts/Input/BlockMove.cs). Duplicated here because
# GDScript cannot read the C# constant -- so the duplication is made SAFE rather
# than avoided: BlockAnimTest's `block-segment-lengths` scenario asserts each
# clip's length equals BlockMove.DefaultFrameData's own tick count / 60, reading
# the C# side directly. Retune the move without re-running this tool and that
# harness scenario goes red and names this file.
const STARTUP_TICKS := 10
const ACTIVE_TICKS := 8
const RECOVERY_TICKS := 20

# Source-time windows, matching author_block.py's frame table exactly (frame
# numbers there ARE physics ticks at 60 Hz: 0/10/18/38).
const STARTUP := [0.0 / 60.0, 10.0 / 60.0]
const ACTIVE := [10.0 / 60.0, 18.0 / 60.0]
const RECOVERY := [18.0 / 60.0, 38.0 / 60.0]

# The producer exports frame_start=0, frame_end=38 (TOTAL_TICKS in
# author_block.py), so the imported clip's length must be ~38/60 s. A silently-
# retrimmed or wrong-fps import would shift every window above out from under
# the actual keyed poses -- this is what makes that failure loud instead of
# quietly slicing garbage. (Godot's own generated .fbx.import defaults are
# fps=30 / trimming=true / remove_immutable_tracks=true, ALL THREE of which
# corrupt this; block_authored.fbx.import overrides them.)
const EXPECTED_SRC_LENGTH_S := 38.0 / 60.0
const SRC_LENGTH_TOL_S := 0.02

const NAMES := ["blockstartup", "blockactive", "blockrecovery"]

# G3 legibility floor (#296's actual complaint -- Startup and Recovery must read
# as visibly different poses). Matches author_block.py's own
# POSE_DISTINCT_MIN_DEG=15.0 gate -- this tool re-proves it on the SLICED clips
# rather than trusting the source's Blender-side proof to survive the slice.
const STARTUP_VS_RECOVERY_MIN_DEG := 15.0

# G4 (the load-bearing gate): the Hips rise at least this far during Active,
# measured against a reference taken from the START of `blockstartup` -- a
# different clip, and one G5 independently proves grounded.
#
# 0.20 m is the issue's floor; author_block.py's table authors 0.30 m (the
# middle of the issue's 0.25-0.35 m band), so there is 50% headroom. The gap is
# deliberate: if the authored rise ever has to be trimmed to within a centimetre
# of this floor, the move has stopped being a jump and the right response is to
# fix the clip, not the threshold.
const HIPS_BONE := "mixamorig_Hips"
const MIN_HIP_RISE_M := 0.20

# G5 (G4's control): the toes stay in a tight band across the PLANTED windows --
# all of `blockstartup`, and `blockrecovery` from 40% onward (the landing). The
# eight ticks before that are the descent and the absorb, legitimately still in
# the air or arriving.
#
# Tolerance is 0.05 m rather than the Blender side's 0.02 m for the same reason
# rebuild_contest_clips.gd widens it: this measurement path adds the FBX
# round-trip, the importer, and `_slice`'s resampling on top of the pose the
# authoring script measured directly. The stricter claim is already proven
# upstream; the job here is to catch a CORRUPTION of that clip, not to re-prove
# authoring precision through a lossier instrument.
const TOE_BONES := ["mixamorig_LeftToeBase", "mixamorig_RightToeBase"]
const GROUND_BAND_TOL_M := 0.05
const RECOVERY_GROUNDED_FROM := 0.4

# G6: the second half of the silhouette. The LOWER of the two wrists must clear
# the head during Active -- see the header for why the lower one, and why
# against the HEAD rather than against the wrists' own Startup height (the layup
# G7 lesson: a relative-only check read a healthy margin while the hand sat
# 0.029 m BELOW the head). Matches author_block.py's WRIST_ABOVE_HEAD_MIN_M.
const WRIST_BONES := ["mixamorig_LeftHand", "mixamorig_RightHand"]
const HEAD_BONE := "mixamorig_Head"
const WRIST_ABOVE_HEAD_MIN_M := 0.10

# G7: G6's control. The same measurement must NOT already be satisfied during
# Startup. Without it, G6 passes on a clip that begins and ends with the arms up
# -- which telegraphs nothing (ADR-0003) and would also pass if the Startup
# slice had silently been cut from the Active window.
const WRIST_ABOVE_HEAD_STARTUP_MAX_M := 0.0

var _skel: Skeleton3D = null
var _right := Vector3.ZERO
var _forward := Vector3.ZERO
var _up := Vector3.UP


func bone_of(np: NodePath) -> String:
	return "" if np.get_subname_count() == 0 else String(np.get_subname(0))


# The node path an AnimationPlayer under scenes/Player.tscn can actually
# resolve. Blender's FBX export wraps the skeleton in an Armature object, so a
# track imported from block_authored.fbx reads
# "Armature/Skeleton3D:mixamorig_Hips" -- one level deeper than the rig, whose
# skeleton is at "Skeleton3D". Every stock-Mixamo clip already in locomotion.res
# uses the short form, so this rebases onto that shape rather than inventing a
# third convention.
#
# This is NOT cosmetic. An unresolvable track binds to nothing, so the clip
# plays as a no-op: the state machine still enters the right state, the clip
# still reports the right duration, and the mesh never moves.
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
# "unresolved". Called once per track by _assert_complete, which is what lets the
# report print an honest match count instead of assuming one spelling.
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


# Lowest toe height (along up) in `anim` at time `t`. The floor reading G5 is
# built from -- the toes, not the Hips, because a crouch lowers the hips without
# leaving the ground and a rise onto the balls of the feet raises them without
# landing.
func _lowest_toe(anim: Animation, t: float) -> float:
	var lowest := INF
	for b in TOE_BONES:
		lowest = minf(lowest, _pose_origin(anim, t, b).dot(_up))
	return lowest


# Hips height (along up) in `anim` at time `t`. G4's quantity. The HIPS rather
# than the toes here on purpose: the toes leave the floor slightly before the
# body's centre of mass does and land slightly after, so the hips are the honest
# measure of "the body left the ground" and are what blender_anim_lib's
# verify_airborne measures too -- keeping both instruments on the same quantity.
func _hips_height(anim: Animation, t: float) -> float:
	return _pose_origin(anim, t, HIPS_BONE).dot(_up)


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
		push_error("[rebuild-block] failed to load AnimationLibrary at %s" % LIB_PATH)
		quit(1)
		return

	_skel = _find(load("res://assets/Y Bot.fbx").instantiate(), "Skeleton3D")
	if _skel == null:
		push_error("[rebuild-block] could not find a Skeleton3D in Y Bot.fbx.")
		quit(1)
		return

	if not _derive_body_axes():
		quit(1)
		return

	var packed = load(SRC_FBX)
	if packed == null:
		push_error("[rebuild-block] failed to load %s" % SRC_FBX)
		quit(1)
		return
	var ap: AnimationPlayer = _find(packed.instantiate(), "AnimationPlayer")
	if ap == null:
		push_error("[rebuild-block] %s has no AnimationPlayer." % SRC_FBX)
		quit(1)
		return
	if not ap.has_animation(SRC_CLIP):
		push_error("[rebuild-block] %s has no AnimationPlayer clip '%s' (has: %s)"
			% [SRC_FBX, SRC_CLIP, str(ap.get_animation_list())])
		quit(1)
		return
	var src: Animation = ap.get_animation(SRC_CLIP)
	print("[rebuild-block] source '%s': len=%.5f tracks=%d"
		% [SRC_CLIP, src.length, src.get_track_count()])

	# ── Sanity: the source must be the full 0..38-frame export, not a silently-
	# retrimmed or wrong-fps import. This is what makes the hardcoded windows
	# above safe to trust (see header).
	if absf(src.length - EXPECTED_SRC_LENGTH_S) > SRC_LENGTH_TOL_S:
		push_error("[rebuild-block] source length %.5f s is not within %.3f s of the expected "
			% [src.length, SRC_LENGTH_TOL_S] + "%.5f s (38/60 s @ 60 fps) -- the import may have been "
			% EXPECTED_SRC_LENGTH_S + "retrimmed or baked at the wrong fps, which would silently shift "
			+ "every hardcoded slice window in this file off the authored poses.")
		quit(1)
		return

	# What a SLICE is expected to carry, derived from the source by applying the
	# same two filters _slice() applies -- never the source's raw counts. The
	# source holds full TRS for every bone plus the Armature object node; a slice
	# keeps rotation+position for bone tracks only.
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
	print("[rebuild-block] source has %d tracks; %d expected per slice after dropping "
		% [src.get_track_count(), src_total]
		+ "SCALE (fights PlayerRigScaler) and the Armature object node (unbindable on Player.tscn).")

	# ── Slice the three windows (verbatim rebuild_jumpshot_clips.gd primitive) ─
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
		print("[rebuild-block] G1 '%s': length=%.5f (expect %.5f) loop_mode=%d %s"
			% [name, anim.length, expected_len, anim.loop_mode, "OK" if (len_ok and loop_ok) else "FAIL"])
		if not (len_ok and loop_ok):
			g1_ok = false
	if not g1_ok:
		push_error("[rebuild-block] G1 FAILED -- one or more clips has the wrong length or loop_mode.")
		quit(1)
		return

	# ── G2: track-count parity + full bone resolution (both name forms) ──────
	var g2_ok := true
	var form_counts := {"as-given": 0, "alt": 0, "unresolved": 0}
	for name in built:
		if not _assert_complete(built[name], name, src_rot, src_total, form_counts):
			g2_ok = false
	print("[rebuild-block] G2 bone-name resolution across all three clips: as-given=%d alt=%d unresolved=%d"
		% [form_counts["as-given"], form_counts["alt"], form_counts["unresolved"]])
	if not g2_ok:
		quit(1)
		return
	if form_counts["unresolved"] > 0:
		push_error("[rebuild-block] G2 FAILED -- %d bone tracks did not resolve on Y Bot in EITHER "
			% form_counts["unresolved"] + "name form.")
		quit(1)
		return
	var resolved_total: int = form_counts["as-given"] + form_counts["alt"]
	if resolved_total <= 0:
		push_error("[rebuild-block] G2 FAILED -- resolved count is %d; a name-matching gate that "
			% resolved_total + "matches nothing passes vacuously.")
		quit(1)
		return

	var startup: Animation = built[NAMES[0]]
	var active: Animation = built[NAMES[1]]
	var recovery: Animation = built[NAMES[2]]

	# ── G3: Startup's END pose vs Recovery's END pose ────────────────────────
	# NOT a whole-clip-start-vs-whole-clip-end comparison -- Startup's own LAST
	# frame (the deepest load) vs Recovery's own LAST frame (the settled low,
	# wide, arms-down stance) is the comparison that actually tests #296.
	var g3_delta := _pose_delta_at(startup, startup.length, recovery, recovery.length)
	print("[rebuild-block] G3 startup-end vs recovery-end max bone delta = %.1f deg (want >= %.1f)"
		% [g3_delta, STARTUP_VS_RECOVERY_MIN_DEG])
	if g3_delta < STARTUP_VS_RECOVERY_MIN_DEG:
		push_error("[rebuild-block] G3 FAILED: only %.1f deg (< %.1f) -- Startup's end pose and "
			% [g3_delta, STARTUP_VS_RECOVERY_MIN_DEG] + "Recovery's end pose do not read as distinct (#296).")
		quit(1)
		return

	var samples := 8

	# ── G5 (run BEFORE G4, because G4 depends on its reference): GROUNDED
	# across the planted windows -- all of Startup, and Recovery from 40% on.
	# The shared floor reference is measured once across BOTH planted windows,
	# so a Recovery that landed uniformly higher than Startup stood cannot pass
	# by establishing its own floor. ────────────────────────────────────────
	var planted := _planted_samples(startup, recovery, samples)
	var ground_ref := INF
	for s in planted:
		ground_ref = minf(ground_ref, _lowest_toe(s[0], s[1]))
	print("[rebuild-block] G5 shared ground reference (lowest toe across the planted windows) = %.4f m"
		% ground_ref)

	var g5_worst := {}
	for s in planted:
		var clip_name: String = s[2]
		var exc: float = _lowest_toe(s[0], s[1]) - ground_ref
		g5_worst[clip_name] = maxf(g5_worst.get(clip_name, 0.0), exc)
	var g5_ok := true
	for clip_name in g5_worst:
		print("[rebuild-block] G5 '%s' (planted window): worst toe excursion above the shared floor = %.4f m (want <= %.2f)"
			% [clip_name, g5_worst[clip_name], GROUND_BAND_TOL_M])
		if g5_worst[clip_name] > GROUND_BAND_TOL_M:
			push_error("[rebuild-block] G5 FAILED: '%s' lifts the feet %.4f m (> %.2f) above the shared floor "
				% [clip_name, g5_worst[clip_name], GROUND_BAND_TOL_M]
				+ "during a window that must be PLANTED. G4's airborne rise is measured against the Startup "
				+ "pose, so a Startup that is itself off the ground makes G4's number meaningless -- it would "
				+ "be measuring the peak against an already-elevated baseline, which is the exact vacuity "
				+ "blender_anim_lib.verify_airborne refuses to allow by default.")
			g5_ok = false
	if not g5_ok:
		quit(1)
		return

	# ── G4 (the load-bearing gate): AIRBORNE during Active. The reference is the
	# Hips height at the START of `blockstartup` -- a different clip, and one G5
	# has just proven grounded. Sampled for the PEAK across Active, because the
	# apex is a moment, not the whole window. ───────────────────────────────
	var hips_ref := _hips_height(startup, 0.0)
	var g4_peak := -INF
	for s in (samples + 1):
		var t: float = active.length * float(s) / float(samples)
		g4_peak = maxf(g4_peak, _hips_height(active, t))
	var g4_rise := g4_peak - hips_ref
	print("[rebuild-block] G4 airborne in Active: hips peak %.4f m vs grounded Startup reference %.4f m -> rise = %.4f m (want >= %.2f)"
		% [g4_peak, hips_ref, g4_rise, MIN_HIP_RISE_M])
	if g4_rise < MIN_HIP_RISE_M:
		push_error("[rebuild-block] G4 FAILED: the hips rose only %.4f m above the grounded Startup "
			% g4_rise + "reference during Active (want >= %.2f). BLOCK LEAVES THE GROUND AND CONTEST DOES "
			% MIN_HIP_RISE_M + "NOT -- a block that squats and un-squats without leaving the floor is visually "
			+ "indistinguishable from a contest, and the commitment ladder in ContestMove.cs (contest 6 < "
			+ "steal 8 < block 10 startup) is priced on an opponent being able to tell them apart. Note that "
			+ "G3, G6 and G7 would all still PASS on such a clip, which is exactly why this gate exists.")
		quit(1)
		return

	# ── G6: the second half of the silhouette -- the LOWER wrist clears the head
	# during Active. Sampled for the BEST frame in the window. ──────────────
	var g6_best := -INF
	for s in (samples + 1):
		var t: float = active.length * float(s) / float(samples)
		g6_best = maxf(g6_best, _wrist_above_head(active, t))
	print("[rebuild-block] G6 arms up in Active: best lower-wrist-above-head = %+.4f m (want >= %.2f)"
		% [g6_best, WRIST_ABOVE_HEAD_MIN_M])
	if g6_best < WRIST_ABOVE_HEAD_MIN_M:
		push_error("[rebuild-block] G6 FAILED: the LOWER wrist peaked only %+.4f m above the head during "
			% g6_best + "Active (want >= %.2f) -- this is a leap with the hands down (a rebound box-out), "
			% WRIST_ABOVE_HEAD_MIN_M + "not a block. Taking the LOWER wrist is deliberate: a one-armed "
			+ "overhead pose is a steal or a one-handed swat, and it must not satisfy a two-armed gate.")
		quit(1)
		return

	# ── G7: G6's control -- the arms must NOT already be up during Startup. ──
	var g7_best := -INF
	for s in (samples + 1):
		var t: float = startup.length * float(s) / float(samples)
		g7_best = maxf(g7_best, _wrist_above_head(startup, t))
	print("[rebuild-block] G7 control -- arms DOWN in Startup: best lower-wrist-above-head = %+.4f m (want <= %.2f)"
		% [g7_best, WRIST_ABOVE_HEAD_STARTUP_MAX_M])
	if g7_best > WRIST_ABOVE_HEAD_STARTUP_MAX_M:
		push_error("[rebuild-block] G7 FAILED: the arms were already %+.4f m above the head during Startup "
			% g7_best + "(ceiling %.2f) -- the overhead extension has to be an EVENT the opponent can read, "
			% WRIST_ABOVE_HEAD_STARTUP_MAX_M + "not a pose the clip holds throughout, or the LONGEST wind-up "
			+ "in the game telegraphs nothing (ADR-0003). G6 would still pass on such a clip.")
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
		push_error("[rebuild-block] ResourceSaver.save failed with error %d" % err)
		quit(1)
		return

	print("[rebuild-block] saved %s; clips now: %s" % [LIB_PATH, str(lib.get_animation_list())])
	quit(0)


# The (clip, time, name) samples G5 treats as PLANTED: all of Startup, and
# Recovery from RECOVERY_GROUNDED_FROM onward. Kept as one function so the
# reference and the per-window excursions can never be computed over different
# sample sets -- which would let a frame set the floor without being held to it.
func _planted_samples(startup: Animation, recovery: Animation, samples: int) -> Array:
	var out := []
	for s in (samples + 1):
		var t: float = startup.length * float(s) / float(samples)
		out.append([startup, t, NAMES[0]])
	for s in (samples + 1):
		var u: float = float(s) / float(samples)
		if u < RECOVERY_GROUNDED_FROM:
			continue
		out.append([recovery, recovery.length * u, NAMES[2]])
	return out


func _name_tick_index(name: StringName) -> int:
	var s := String(name)
	if s.contains("startup"):
		return 0
	if s.contains("active"):
		return 1
	return 2


# ── Body axes (verbatim rebuild_contest_clips.gd approach) ────────────────────
# Derived from Y Bot's own REST pose, never from scenes/Player.tscn --
# BlendRestAnchor.cs re-anchors the UpLeg rests at runtime, and every foot/toe
# global rest downstream inherits the error (119.6 deg / 2.17x stride
# mismeasurement in #298). Checked, not assumed: forward.cross(up) points to this
# rig's right (the #255 lesson), verified below against the rest hand positions.
func _derive_body_axes() -> bool:
	var foot := _skel.find_bone("mixamorig_LeftFoot")
	var toe := _skel.find_bone("mixamorig_LeftToeBase")
	if foot < 0 or toe < 0:
		push_error("[rebuild-block] Y Bot has no LeftFoot/LeftToeBase to derive facing from.")
		return false
	var forward: Vector3 = _skel.get_bone_global_rest(toe).origin - _skel.get_bone_global_rest(foot).origin
	forward.y = 0.0
	if forward.length() < 0.001:
		push_error("[rebuild-block] Y Bot's foot->toe vector is vertical; cannot derive facing.")
		return false
	_forward = forward.normalized()
	_right = _forward.cross(_up).normalized()

	var lh := _skel.find_bone("mixamorig_LeftHand")
	var rh := _skel.find_bone("mixamorig_RightHand")
	if lh < 0 or rh < 0:
		push_error("[rebuild-block] Y Bot has no LeftHand/RightHand to verify the lateral axis.")
		return false
	var lh_lat: float = _skel.get_bone_global_rest(lh).origin.dot(_right)
	var rh_lat: float = _skel.get_bone_global_rest(rh).origin.dot(_right)
	print("[rebuild-block] axes: forward=%s right=%s | rest hand lateral L=%+.4f R=%+.4f"
		% [_forward, _right, lh_lat, rh_lat])
	if not (rh_lat > 0.0 and lh_lat < 0.0):
		push_error("[rebuild-block] the derived right axis puts the RIGHT hand at %+.4f and the LEFT "
			% rh_lat + "at %+.4f -- it is inverted (or the rig's bone names lie)." % lh_lat)
		return false
	return true


# ── Slicing (verbatim rebuild_jumpshot_clips.gd / rebuild_contest_clips.gd
# primitive) ──────────────────────────────────────────────────────────────────
# Resamples source range [t0, t1] into a clip of exactly `ticks` ticks at 60 tps,
# one key per gameplay tick (ticks + 1 keys, the last landing exactly on
# `length`). Keying at the tick rate rather than copying the source's own key
# times is what ties the clip to the move's frame data.
func _slice(src: Animation, t0: float, t1: float, ticks: int) -> Animation:
	var out := Animation.new()
	# Explicit, not inherited: the FBX import default happens to agree (a block
	# IS a one-shot) -- which is exactly why it must not be inherited silently.
	# 03-block.md calls this out by name: inheriting a default is how the next
	# person learns it the hard way.
	out.loop_mode = Animation.LOOP_NONE
	out.length = float(ticks) / TPS

	for i in src.get_track_count():
		var type := src.track_get_type(i)
		if type != Animation.TYPE_ROTATION_3D and type != Animation.TYPE_POSITION_3D:
			# SCALE tracks are dropped deliberately, not overlooked. Blender's
			# exporter bakes full TRS for every bone, so the source carries a
			# scale track per bone -- all identity (the authoring script's own
			# verify_pose_unscaled measured 4.8e-7). Keeping them would be worse
			# than useless: PlayerRigScaler applies the height/wingspan chains via
			# SetBonePoseScale, which writes the ANIMATED pose, so a per-bone
			# scale track overwrites it every frame the clip plays.
			continue

		var path := src.track_get_path(i)
		if bone_of(path) == "":
			# The bare "Armature" object-node tracks. Blender's FBX export wraps
			# the skeleton in an Armature object, and Godot imports it as a real
			# node, so the source holds position/rotation/scale tracks for the
			# object ITSELF. Player.tscn's rig has no such node -- its skeleton
			# sits directly at "Skeleton3D" -- so these resolve against nothing.
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
			# NOT a `continue`. A gate that skips every subname-less path silently
			# exempts precisely the tracks that were broken -- the bare "Armature"
			# object-node tracks -- and would report "unresolved=[]" while every
			# track in the clip failed to bind at runtime (README trap 15, proven
			# by #281). A track with no bone subname has no business in a skeletal
			# clip; say so instead of looking away.
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
	print("[rebuild-block]   '%s': len=%.4f tracks=%d rot=%d loop=%d unresolved=%s"
		% [name, anim.length, anim.get_track_count(), rot, anim.loop_mode, str(unresolved)])
	if rot != expected_rot or anim.get_track_count() != expected_total:
		push_error("[rebuild-block] '%s' has %d/%d tracks, source has %d/%d -- a slice must inherit "
			% [name, rot, anim.get_track_count(), expected_rot, expected_total]
			+ "the source's FULL body coverage or the untracked bones rest-pose at full weight.")
		return false
	if unresolved.size() > 0:
		push_error("[rebuild-block] '%s' has tracks that do not resolve on Y Bot in EITHER name form: %s"
			% [name, str(unresolved)])
		return false
	if bad_shape.size() > 0:
		push_error("[rebuild-block] '%s' has %d track(s) whose NODE PATH cannot bind on "
			% [name, bad_shape.size()]
			+ "scenes/Player.tscn (skeleton at 'Skeleton3D', no 'Armature' wrapper): %s. "
			% str(bad_shape)
			+ "Such a track binds to nothing and the clip plays as a silent no-op -- the state machine "
			+ "still enters, the duration still checks out, and the mesh never moves.")
		return false
	return true


func _rotation_track_count(anim: Animation) -> int:
	var n := 0
	for i in anim.get_track_count():
		if anim.track_get_type(i) == Animation.TYPE_ROTATION_3D:
			n += 1
	return n


# Largest per-bone angular difference between a SINGLE named instant in clip `a`
# (time `ta`) and a single named instant in clip `b` (time `tb`). Used for G3's
# specific "Startup's END pose vs Recovery's END pose" comparison -- two fixed
# poses, not two trajectories.
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
# unchanged rest transform and every geometric proof built on it passes vacuously
# at exactly 0.0000 (measured, #285). Manual FK depends on nothing but the rest
# pose and the clip's own keys.
#
# BOTH ROTATION_3D and POSITION_3D tracks are walked: the Hips carry the clip's
# only POSITION_3D track, and every bone downstream of them -- including the TOES
# G5's grounded band is measured on, and the HIPS G4's airborne rise IS -- either
# inherits that translation through the chain or is it. Dropping it would pin
# every reading at its rest height, so G4 would read a perfectly flat 0.0000 m
# rise and G5 a perfectly flat 0.0000 m excursion, and BOTH would be vacuous (G5
# passing, G4 correctly failing -- which is itself the reason to walk it rather
# than to trust that a broken FK would be noticed).
#
# Bone lookups go through `_resolve_bone()` so a track authored under either the
# colon or underscore Mixamo prefix form still walks the correct chain.
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
		# REPLACES the rest basis' rotation; scale carries over. POSITION_3D keys
		# likewise REPLACE the rest origin (only Hips carries one here).
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
