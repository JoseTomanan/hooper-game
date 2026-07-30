extends SceneTree
# Asset build tool (#285, extended by #294) — extracts FOUR dribble-loop
# blendspace endpoints, one idle/move PAIR PER BALL-HAND POLARITY, from their
# respective FBX sources into assets/locomotion.res.
#
# Run:  godot --headless --path . -s tools/rebuild_dribble_clips.gd
# Idempotent: re-running overwrites all four clips with freshly-derived ones,
# and purges the pre-#294 two-clip names (see POLARITIES/LEGACY_* below).
#
# Produces, per entry of POLARITIES below, named `dribble{idle,move}{suffix}`:
#   dribbleidle{suffix} — the REAL clip for that polarity's own idle FBX
#                 source, LOOPED, VERBATIM. For "right" this is the stock-
#                 Mixamo assets/Dribble.fbx clip `mixamo_com` (#285a's
#                 original contract, unchanged); for "left" it is the
#                 Blender-mirrored assets/dribble_idle_left.fbx clip
#                 `dribbleidleleft`. fix/298 round 2 proposed amending this
#                 contract for the leg chain; that was MEASURED, REJECTED and
#                 gated off behind CENTRE_IDLE_ENDPOINT := false (see that
#                 section below, which documents a rejected path -- read its
#                 closing paragraph, not just its premise). PROOF 4 is handed
#                 identity rotations and therefore asserts plain
#                 verbatim-ness today, for BOTH polarities.
#   dribblemove{suffix} — LOOPED, SAME LENGTH as that polarity's own idle
#                 endpoint. As of #300 it is AUTHORED in headless Blender
#                 (tools/author_dribble_move.py for "right"; a Blender mirror
#                 of that same authored clip for "left") and loaded verbatim
#                 from that polarity's own move FBX; the #285b lean-only draft
#                 and #298's run-delta leg transplant are superseded and
#                 survive only behind USE_AUTHORED_MOVE_CLIP := false.
#
# ── #294: one polarity, one independent pipeline instance ────────────────────
# Everything below this point (the corridor argument, the #298 world-frame
# delta, the stance-centring fix, the falsified idle-centring hypothesis) is a
# PER-POLARITY invariant: it must hold between a SINGLE polarity's own idle and
# move endpoints, never ACROSS polarities -- "left" and "right" are never
# blended against each other in the AnimationTree, so nothing here requires
# them to be close to one another, only each polarity's own pair to be close
# to itself. POLARITIES (below the LIB_PATH/SRC_FBX/YBOT_FBX constants) is what
# turns the single-polarity pipeline the rest of this file describes into four
# clips: the loop in _initialize() runs the ENTIRE pipeline (idle load ->
# retarget-if-needed -> move load -> the lean/crouch/stride dead path ->
# the five proofs) once per entry, reusing only what is genuinely
# polarity-INDEPENDENT across iterations -- the Y Bot rig's own facing/lean
# axis, its raw skeleton, and the shipped `run` clip.
#
# ── #294: the `Armature/` prefix trap is per-SOURCE, not per-polarity ────────
# Both "left" FBX sources (dribble_idle_left.fbx, dribble_move_left.fbx) are
# Blender exports, so BOTH come in with every track path one node level too
# deep (`Armature/Skeleton3D:mixamorig_Hips` vs. the library's own
# `Skeleton3D:mixamorig_Hips`) -- see _retarget_track_paths's own doc for why a
# MISADDRESSED path is silently worse than a missing one (it still reports the
# right length/track count and plays as a frozen no-op). Measured: 53/53 leg
# and torso tracks prefixed on BOTH left sources, 0/53 on the stock
# assets/Dribble.fbx. The MOVE clip goes through Blender for EITHER polarity
# (author_dribble_move.py or its mirror), so `_load_authored_move_clip`
# retargets it unconditionally regardless of polarity -- that part was never
# polarity-conditional. The IDLE clip is the one place this IS
# polarity-conditional: "right"'s idle source is the stock FBX itself (0/53,
# no retarget needed -- it doubles as the canonical reference clip everything
# else retargets against), while "left"'s idle source is the Blender mirror
# (53/53, MUST be retargeted before anything downstream reads it -- see the
# _load_polarity_idle_source doc for why this has to happen at SOURCE-LOAD
# time, before _proof_idle_stance_centred ever compares it to anything).
# POLARITIES' `idle_needs_retarget` flag is exactly this bit; get it backwards
# and the left idle plays as a silent frozen-rest no-op that every
# reachability/duration assertion still passes.
#
# ── Why both endpoints are derived from `Dribble`, not `Dribble` + `run` ──────
# #276's table suggested deriving the moving endpoint by remixing `Dribble` with
# `run`. Measured headlessly (Godot 4.7.1) that is unsafe, and this is the
# single most important thing to know before editing this file.
#
# locomotion.res holds clips from TWO different rotation representations:
#
#   family          clips                 distance from Y Bot's RAW bone rest
#   ------------    ------------------    -----------------------------------
#   Kenney          idle, run, pivot      Hips 155-158, Spine 140-152,
#   (retargeted,                          UpLeg 122-180   <- antipode zone
#    fix_silhouette)
#
#   stock Mixamo    catch, Dribble        Hips 4-38, Spine 4-33,
#   (straight)                            UpLeg 7-88      <- near rest
#
# Both render correctly on their own, because ROTATION_3D tracks are ABSOLUTE
# local rotations and a single-clip state plays at full weight (see
# BlendRestAnchor's doc). But a PARTIAL-weight blend is rest-anchored, and
# that is where the families cannot mix: at matched phase `dribble` sits
# 155-180 deg from `idle`/`run` on Hips and Spine. Blending across that gap is
# precisely the #287 degeneracy (two contributions near rest's antipode along
# different great circles, cancelling into a pose on neither clip's arc).
#
# It is worse than merely "not better": scripts/Player/BlendRestAnchor.cs
# re-anchors mixamorig_{Left,Right}UpLeg's REST to `idle`'s first key to fix the
# Locomotion blend. That anchor is a Kenney-family pose, so it puts a
# Dribble-family contribution for those same two bones ~92-147 deg from its own
# rest. The fix that stabilises Locomotion actively destabilises a Dribble
# blend built from mixed families.
#
# So: BOTH endpoints come from `Dribble`. Safety then rests on an invariant that
# does not depend on the rest at all — the two endpoints stay within a small
# angle of EACH OTHER, so every intermediate blend weight interpolates a short
# arc and there is nothing to cancel. DribbleLoopTest's `dribble-corridor`
# scenario is the standing proof of that (the #287 corridor sweep, re-pointed at
# this blendspace); it is what must stay green if this file is ever changed.
#
# ── #298: transplanting run's LEG stride as a world-frame delta ──────────────
# #298 grafts `run`'s leg motion onto `dribblemove`'s otherwise-frozen legs
# (0.0047 m fore/aft toe peak-to-peak vs run's 1.3943 m). This does NOT reopen
# the family-mixing danger above, for two reasons:
#
#   1. It is a WORLD-FRAME ROTATION DELTA, not a raw copy of run's local keys.
#      D(b,t) = G_run(b,t) * G_run(b,t0)^-1 measures how far run's leg has
#      turned AWAY from run's OWN neutral pose, expressed in run's own
#      rest-independent global-rotation space. Solving
#      G_new(b,t) = D(b,t) * G_dribble(b,t) then re-projects that SAME delta
#      onto dribble's own global rotation at each leg bone, root to tip. A
#      world-frame delta carries no information about which family's rest
#      either clip is expressed against — composing it onto dribble's chain
#      cannot import run's (or Kenney's) rest convention. The per-bone
#      self-check the tool runs (G_new(b,t) == D(b,t)*G_dribble(b,t), asserted
#      numerically within 1e-4 deg) is what actually pins this down; it is not
#      a visual-resemblance argument.
#   2. mixamorig_Hips — the one bone the two families disagree on
#      catastrophically (155-158 deg apart) — is NEVER modified by this step.
#      G_new(Hips,t) is pinned to dribble's own (stock-Mixamo, near-rest) Hips
#      rotation, untouched, for every sample. The leg chain is solved
#      root-to-tip UNDERNEATH that pinned Hips, so run's Hips convention never
#      enters dribblemove's data — not its rotation track, not its position
#      (crouch) track.
#
# The two dribble BlendSpace1D endpoints therefore still satisfy the corridor
# argument above: dribbleidle is completely untouched by this step (proved,
# not assumed — see the idle-untouched proof below), and dribblemove's
# Hips/Spine/upper body are exactly what they were before; only the leg chain
# now swings through a real stride. The `dribble-corridor` sweep was never
# about leg AMPLITUDE, only about the two endpoints staying close to each
# other, which the (still short-arc) Hips/Spine/upper body continues to
# guarantee.
#
# ── fix/298: centring the base stance's fore/aft stagger ─────────────────────
# An amplitude sweep of STRIDE_AMPLITUDE proved NO amplitude satisfies both
# PROOF 1 (the lead foot must alternate -- fore/aft split changes sign) and
# PROOF 6 (grounding -- support band within SUPPORT_BAND_TOLERANCE of `run`'s
# own): below ~0.55 the split never changes sign, above ~0.51 the support band
# blows the PROOF 6 gate. There is no amplitude in between.
#
# Root cause was the BASE stance, not the transplanted swing: `dribbleidle`
# holds the left toe a constant ~0.70 m ahead of the right the whole loop --
# a wide static fore/aft stagger already baked into the SOURCE clip.
# Overcoming +-0.35 m of static offset before the swing can even alternate
# forces a big amplitude, and a big amplitude drives the legs toward full
# extension at the swing extremes, which lifts both feet off the floor at
# once. The static offset both FORCES and PUNISHES a large amplitude -- the
# squeeze is structural, not a tuning problem amplitude alone can solve.
#
# The fix (CENTRE_STANCE_STAGGER, on by default) runs BEFORE the delta
# transplant above and is a separate, STATIC correction: solve one rotation
# per leg, `C_leg`, that -- pre-multiplied onto that leg's global rotation --
# moves its own time-averaged toe projection to the midpoint between the two
# legs' averages. Folded into the composition as
# `G_new(b,t) = D(b,t) * C_leg * G_dribble(b,t)`, this centres the swing
# around a symmetric base stance instead of dribbleidle's lopsided one, so a
# modest amplitude can both alternate and stay grounded. `C_leg` is solved by
# bisection (not a closed form) against the SAME FK helpers the rest of this
# file already trusts, and the solve is verified numerically (residual vs. a
# 0.01 m gate) rather than assumed to have converged -- see
# `_solve_stance_correction`. `mixamorig_Hips` is untouched by this step
# exactly as before. `dribbleidle` is NOT untouched -- see the next section;
# this paragraph described the ORIGINAL (dribblemove-only) shape of the fix,
# superseded below.
# CENTRE_STANCE_STAGGER=false restores the old, uncentred behaviour so the two
# can be compared directly.
#
# ── fix/298 (round 2): REJECTED -- centring dribbleidle too ─────────────────
# ⚠ THIS SECTION DOCUMENTS A PATH THAT WAS TRIED AND ABANDONED. It is written
# in the present tense because it was drafted while the change was live; the
# change did NOT ship. `CENTRE_IDLE_ENDPOINT` is false, PROOF 4 receives
# identity rotations, and dribbleidle's leg chain is verbatim. What actually
# resolved the corridor failure described below was lowering STRIDE_AMPLITUDE
# from 0.70 to 0.50 (0/90 frames), not centring both endpoints (2/90). Read to
# the end of the section before acting on any of it.
#
# Applying C_leg to dribblemove alone (the previous section) traded the base-
# stance squeeze for a NEW, worse defect: DribbleLoopTest's #287 corridor
# sweep (the `dribble-corridor` scenario) went from 0/90 to 1/90 frames
# violated -- `#287 worst [Dribble]: 'mixamorig_RightUpLeg' @ frame 34
# (t=0.567s, blend=2.27) angle_vs_ref0=118.0 angle_vs_ref6=167.4 ref_gap=69.9
# excess=38.1 deg`. Measured, not guessed: it is not gap size -- the
# Locomotion sweep this same test runs passes 0/90 at a 178.8 deg gap between
# ITS OWN two endpoints. What changed is that dribbleidle and dribblemove now
# differ by more than the swing -- dribblemove carries a STATIC stance change
# (C_leg) that dribbleidle does not, layered on top of the animated delta.
# BlendRestAnchor pins both UpLeg bones' REST to dribbleidle's frame-0 key,
# and the -30.67 deg right-leg correction pushed mixamorig_RightUpLeg into the
# antipode-cancellation neighbourhood relative to that anchor -- exactly the
# #287 degeneracy the corridor argument above exists to rule out, reintroduced
# by making the two endpoints differ by something the blend parameter itself
# does not control.
#
# The fix is to apply the IDENTICAL static correction to BOTH endpoints
# (`_apply_static_stance_correction`, called on `idle_clip` right after
# `_apply_leg_stride` returns its solved `C_leg` quaternions, and BEFORE the
# `_head_shift_along`/`_max_pose_delta` measurements below so those describe
# the clips actually saved). With both endpoints centred by the same `C_leg`,
# they once again differ ONLY by the animated swing delta `D` (identity on
# dribbleidle, the transplanted stride on dribblemove) -- a pure rotation
# about a single axis, the short/safe geodesic the corridor argument depends
# on. This AMENDS #285a's "dribbleidle is the real stock clip verbatim"
# contract for the LEG CHAIN ONLY -- torso, arms, and Hips remain exactly the
# source FBX clip, untouched. PROOF 4 (renamed `_proof_idle_stance_centred`)
# now asserts the new contract: dribbleidle's leg rotations must equal
# `C_leg * G_src(b,t)`, not raw `G_src(b,t)`.
#
# ── Why the lean goes on Spine, not Hips ─────────────────────────────────────
# mixamorig_Hips is the skeleton root, so leaning it tips the legs too and the
# figure pivots rigidly at the ankles (feet clip the floor). mixamorig_Spine's
# children are torso/arms/head only — the legs hang off Hips — so leaning there
# tilts the upper body over planted feet, which is what a drive posture is.
#
# ── Temp-draft status (#276 rules) ───────────────────────────────────────────
# `dribblemove` is explicitly TEMPORARY and its visual quality is deferred to
# #173 / ADR-0021 — it does not gate merge. It satisfies the #276 temp-draft
# bar: it is a COMPLETE 53-track clip (so the a45bd1d untracked-bones T-pose
# trap cannot bite), its tracks resolve to real Y Bot bones, and its silhouette
# is measurably distinct from the idle endpoint.

const LIB_PATH := "res://assets/locomotion.res"
# The canonical PATH-CONVENTION REFERENCE clip (see the file header's #294
# section). It is the only clip in the whole four-clip set whose track paths
# already sit on the library's own convention (`Skeleton3D:mixamorig_Hips`, no
# `Armature/` prefix), so it plays two distinct jobs: the retarget REFERENCE
# passed to every `_retarget_track_paths` call (idle and move, either
# polarity), and -- because it needs no retarget of its own -- the "right"
# polarity's own idle SOURCE. Loaded exactly ONCE, in _initialize().
const SRC_FBX := "res://assets/Dribble.fbx"
const SRC_CLIP := "mixamo_com"
const YBOT_FBX := "res://assets/Y Bot.fbx"

# ── #294: one idle/move pair per ball-hand polarity ──────────────────────────
# Replaces the single-polarity SRC_FBX/SRC_CLIP/IDLE_NAME/MOVE_NAME/
# AUTHORED_MOVE_FBX/AUTHORED_MOVE_CLIP constants this file used to hardcode
# before #294. `idle_needs_retarget` is the polarity-conditional half of the
# `Armature/` prefix trap the file header describes above: false for "right"
# (its idle source doubles as SRC_FBX/SRC_CLIP itself -- already on-
# convention), true for "left" (a Blender mirror -- measured 53/53 tracks
# prefixed).
const POLARITIES := [
	{
		"suffix": "right",
		# Deliberately the SAME literal path/clip as SRC_FBX/SRC_CLIP above --
		# not a copy that could drift out of sync with them, since equality
		# against SRC_FBX/SRC_CLIP is exactly how _load_polarity_idle_source
		# recognises "this polarity's idle source IS the already-loaded
		# canonical reference, reuse it" instead of reloading the same FBX.
		"idle_fbx": SRC_FBX,
		"idle_clip": SRC_CLIP,
		"move_fbx": "res://assets/dribble_move_authored.fbx",
		"move_clip": "dribblemove",
		"idle_needs_retarget": false,
	},
	{
		"suffix": "left",
		"idle_fbx": "res://assets/dribble_idle_left.fbx",
		"idle_clip": "dribbleidleleft",
		"move_fbx": "res://assets/dribble_move_left.fbx",
		"move_clip": "dribblemoveleft",
		"idle_needs_retarget": true,
	},
]

# Pre-#294 single-polarity clip names. Nothing in the game references them any
# more (POLARITIES above is what ships now) -- kept only so an idempotent
# re-run can find and remove a stale build rather than leaving it behind to
# silently corrupt the library's clip inventory (see the idempotency step at
# the end of _initialize()).
const LEGACY_IDLE_NAME := &"dribbleidle"
const LEGACY_MOVE_NAME := &"dribblemove"

# ── #300: the moving endpoint is AUTHORED, not derived ───────────────────────
# `tools/author_dribble_move.py` keyframes a drive-dribble gait cycle in
# headless Blender and exports the "right" polarity's move FBX (POLARITIES
# above); see that script's header for the method (foot trajectory + two-link
# IK) and for why it supersedes #298's delta transplant rather than tuning it.
# The "left" polarity's move FBX is a true Blender MIRROR of that same
# authored clip, not a second independent authoring pass.
#
# When true, each polarity's dribblemove is taken verbatim from its own
# move_fbx/move_clip (POLARITIES) and the entire #298 derivation below (lean ->
# crouch -> leg-stride transplant -> stance centring) is skipped. Each
# polarity's dribbleidle is unaffected either way: it is extracted from that
# polarity's own idle_fbx exactly as before -- #285a's contract that it is
# VERBATIM, never re-authored or IK-solved in Blender, holds for both
# polarities. ("left"'s idle source happens to itself be a Blender EXPORT -- a
# straight mirror, not a re-authoring -- which is exactly why it needs the
# `Armature/`-prefix retarget above; that is track-path bookkeeping, not new
# motion. A genuine Blender authoring round trip was separately measured at
# 0.396 deg pose error -- below LOOP_SEAM_TOLERANCE_DEG but NOT zero, which is
# why #285a's contract is *verbatim*, not merely close.)
#
# The #298 path is retained behind `false` rather than deleted so the two can
# be A/B'd during the #301 feel verify. DELETE IT once #301 passes -- it is
# superseded by human direction, not a falsified hypothesis anyone would
# re-derive, so it earns its keep only until the replacement is accepted.
const USE_AUTHORED_MOVE_CLIP := true

# Forward torso lean applied to the moving endpoint. Big enough to read as a
# drive posture at a glance (ADR-0003 legibility), small enough that the two
# blendspace endpoints stay a short arc apart — see the corridor argument above.
# Raised from 20 to 38 (#286 legibility fix): 20 degrees was too subtle to read
# against dribbleidle at a glance -- a sprinting player looked like a standing one.
const LEAN_DEGREES := 38.0
const LEAN_BONE := "mixamorig_Spine"

# Crouch applied to the moving endpoint's Hips POSITION_3D track (#286). A lean
# alone reads as "leaning", not "moving" -- pairing it with a lower stance is
# what makes the drive posture unmistakable. This is a POSITION delta, not a
# rotation, so it needs none of the parent-frame conjugation rotations require;
# subtracting from Y directly lowers the root in its own (world-aligned) space.
const CROUCH_DROP_M := 0.12
const CROUCH_BONE := "mixamorig_Hips"

# ── #298: leg-stride transplant constants ────────────────────────────────────
# The leg chain, root to tip, per side. mixamorig_Hips (CROUCH_BONE above) is
# their shared parent and is NEVER included here — its rotation is read
# (untouched) as the chain's starting point, never solved for or overwritten.
#
# {L}Toe_End is deliberately EXCLUDED even though the bone exists on Y Bot's
# skeleton (confirmed via a headless bone dump): neither the Dribble.fbx source
# clip nor `run` carries a ROTATION_3D track for it, and step 5 below forbids
# adding tracks that do not already exist. This matches the established fact
# that exactly 8 leg-chain ROTATION_3D tracks exist in the dribble clips (4
# bones/side): UpLeg, Leg, Foot, ToeBase.
const LEG_CHAIN_LEFT := [
	"mixamorig_LeftUpLeg", "mixamorig_LeftLeg", "mixamorig_LeftFoot", "mixamorig_LeftToeBase",
]
const LEG_CHAIN_RIGHT := [
	"mixamorig_RightUpLeg", "mixamorig_RightLeg", "mixamorig_RightFoot", "mixamorig_RightToeBase",
]

# fix/298: centre the base stance's fore/aft stagger BEFORE the delta transplant
# (see the file-header section above for the measured squeeze this removes).
# Setting this false restores the old uncentred behaviour, for A/B comparison
# against this fix only -- it is not a supported end-state.
const CENTRE_STANCE_STAGGER := true

# Bisection convergence gate for the stance-centring solve (see
# _solve_stance_correction). A silent non-convergence here would leave the
# stagger in place and quietly reproduce the exact squeeze the fix exists to
# remove, so this fails loud rather than saving a near-miss.
const STANCE_CENTRE_RESIDUAL_TOLERANCE_M := 0.01
# Bisection bracket half-width and iteration count for the same solve.
const STANCE_CENTRE_BRACKET_DEG := 60.0
const STANCE_CENTRE_ITERATIONS := 40

# Amplitude control on the transplanted delta: D_scaled = IDENTITY.slerp(D, this).
# 1.0 = full run-magnitude stride. Kept at 1.0 unless a proof below trips; if one
# does, that is reported rather than silently lowering this constant.
const STRIDE_AMPLITUDE := 0.50

# Step 5: replacement key grid — 63 keys at 1/30 s spacing spans exactly the
# clip's 2.100 s length (63 * 1/30 = 2.100), so the last authored key sits
# exactly one grid interval before `length`, which is what makes Godot's own
# LOOP_LINEAR wrap-interpolation land exactly on key 0 at time=length (verified
# empirically — see the loop-seam proof below).
const RESAMPLE_KEY_COUNT := 63
const RESAMPLE_DT := 1.0 / 30.0

# PROOF 1 (stride, the #298 acceptance criterion): 126 phases mirrors the
# harness's own 126-frame/2.1s measurement window (LocomotionClipTest.cs).
const STRIDE_PHASE_COUNT := 126
const STRIDE_MIN_PEAK_TO_PEAK_M := 0.15

# PROOF 0 (cadence): 240 evenly-spaced phases over the source dribble clip.
const CADENCE_PHASE_COUNT := 240
const CADENCE_MAX_PLAUSIBLE_BOUNCES := 8
# Fraction of the measured RightHand excursion span used as the hysteresis band
# for valley detection, so ordinary interpolation jitter between sampled phases
# can never register as a spurious extra bounce.
const CADENCE_HYSTERESIS_FRACTION := 0.2
const RIGHT_HAND_BONE := "mixamorig_RightHand"

# Step 2 (neutral reference t0): 120 evenly-spaced phases over `run`.
const T0_SAMPLE_COUNT := 120

# Step 4 self-check tolerance (algebra sanity, not a design threshold).
const SELF_CHECK_TOLERANCE_DEG := 1e-4
# PROOF 3a (wrap pose residual / loop_mode guard) tolerance, geodesic degrees.
#
# CALIBRATED, not guessed, and deliberately NOT 1e-3. It was 1e-3 first, and an
# amplitude sweep exposed that as a bad assertion: the residual read exactly
# 0.039565 deg at STRIDE_AMPLITUDE 0.30/0.40/0.50/0.60/0.80 and exactly 0.000000
# at 0.70/1.00. A value that is CONSTANT across amplitudes and then vanishes
# non-monotonically is not measuring the baked data at all -- it is float32
# quantization noise in Animation's stored quaternion keys (Godot's Quaternion is
# float32 internally; 1e-3 deg is below the precision of the very keys this proof
# inspects). Gating there fails the build on rounding.
#
# 0.5 deg sits above that observed ~0.04 deg noise floor and is still an order of
# magnitude below the failure this proof exists to catch: under LOOP_NONE,
# sampling at `length` returns the LAST key instead of wrapping to key 0, so the
# residual becomes the full seam step -- measured 5.05 deg at amplitude 0.70 and
# 7.22 deg at 1.00, i.e. 10-14x this tolerance. PROOF 3a now measures that
# would-be residual explicitly and refuses to pass if the margin is not there, so
# this tolerance cannot silently drift into vacuity.
#
# 0.5 deg is also visually irrelevant on its own terms: the real continuity
# question is velocity, and that is PROOF 3b's job (passing at ratio ~0.36
# against a 2.0 gate).
const LOOP_SEAM_TOLERANCE_DEG := 0.5
# PROOF 3a non-vacuity: the LOOP_NONE would-be residual must exceed the tolerance
# by at least this factor, or the gate has no power to catch the regression it
# exists for and says so instead of passing quietly.
const LOOP_SEAM_GUARD_MARGIN := 4.0
# PROOF 3b (seam velocity continuity): the angular step ACROSS the loop seam may
# be at most this multiple of the worst step taken inside the cycle. 2.0 is
# deliberately loose -- the seam step is a legitimate ordinary step when the
# resample lands an integer number of gait cycles in the loop, and this exists
# to catch an order-of-magnitude pop (a non-integer cycle count), not to police
# ordinary per-key variation.
const LOOP_SEAM_STEP_RATIO_MAX := 2.0

# PROOF 4's tolerance (fix/298 round 2 -- see the file header and PROOF 4
# itself for the amended contract this backs). 0.1 deg sits comfortably above
# the ~0.04 deg float32 quaternion-key noise floor already measured and
# documented on LOOP_SEAM_TOLERANCE_DEG above (Animation track values
# round-trip through a float32 Quaternion on write/read, the same source of
# noise), while staying tight enough to still catch the stride -- or any
# other motion -- leaking into the idle endpoint, which is the whole reason
# this proof exists.
const IDLE_CENTRING_TOLERANCE_DEG := 0.1

# Whether the static stance centring is ALSO applied to the dribbleidle endpoint.
# OFF, on measured evidence -- see the long FALSIFIED HYPOTHESIS note at its call
# site in _initialize. Short version: it costs #285a's "dribbleidle verbatim"
# contract and makes the #287 corridor STRICTLY WORSE (1/90 -> 2/90 frames, worst
# excess 38.1 -> 77.6 deg). While OFF, PROOF 4 is handed identity rotations
# instead of C_leg, which degenerates its check back into the original verbatim
# assertion -- so the proof follows the flag automatically rather than needing its
# own branch.
const CENTRE_IDLE_ENDPOINT := false

# PROOF 6 (vertical plausibility / grounding): a post-hoc measurement at
# STRIDE_AMPLITUDE=1.0 found support(t) = min(LeftFoot.y, RightFoot.y) - Hips.y
# ranging over a 0.4932 m band (both feet ~0.5 m off the floor at one phase) --
# a sprint legitimately has a flight phase, a driving dribbler must stay
# grounded. The shipped `run` clip defines what an acceptable vertical band
# looks like on this rig; a driving dribble should be no bouncier than a run,
# and 25% is slack for the crouched stance's different leg geometry rather
# than a licence to hop.
const SUPPORT_BAND_TOLERANCE := 1.25

# ── #294: generic "load one named clip out of one FBX" loader ────────────────
# Shared by the canonical-reference load, the per-polarity idle-source load,
# and (via _load_authored_move_clip below) the per-polarity move-clip load --
# one place to get the AnimationPlayer-lookup boilerplate right instead of
# three. Returns null (after push_error) rather than crashing, so the caller
# can quit non-zero without saving -- the same refuse-to-save discipline as
# the proofs.
func _load_clip(fbx_path: String, clip_name: String, log_tag: String) -> Animation:
	var packed = load(fbx_path)
	if packed == null:
		push_error("[rebuild-dribble] failed to load %s (%s)" % [fbx_path, log_tag])
		return null
	var root: Node = packed.instantiate()
	var ap: AnimationPlayer = root.get_node_or_null("AnimationPlayer")
	if ap == null or not ap.has_animation(clip_name):
		push_error("[rebuild-dribble] %s has no AnimationPlayer clip '%s' (%s)" % [fbx_path, clip_name, log_tag])
		return null
	return ap.get_animation(clip_name).duplicate(true)

# ── #300/#294: load a polarity's Blender-authored moving endpoint ────────────
# `fbx_path`/`clip_name` come from that polarity's own POLARITIES entry
# (move_fbx/move_clip) -- #300 originally hardcoded these to the "right"
# polarity's own constants; #294 generalised the signature so the identical
# retarget/normalize pipeline below runs unchanged for "left" too. Returns
# null (after push_error) rather than crashing, so the caller can quit
# non-zero without saving -- the same refuse-to-save discipline as the proofs.
func _load_authored_move_clip(fbx_path: String, clip_name: String, reference: Animation) -> Animation:
	var clip := _load_clip(fbx_path, clip_name, "authored move clip")
	if clip == null:
		push_error(("[rebuild-dribble] #300/#294: if this is the \"right\" polarity's move clip, regenerate it with:\n" +
			"  \"$BLENDER\" --background --python-exit-code 1 " +
			"--python tools/author_dribble_move.py -- assets/Dribble.fbx %s\n" +
			"(the \"left\" polarity's move FBX is a Blender mirror of that output, not independently authored.) " +
			"Godot also names a clip after the FBX take, which Blender names after the SCENE -- check that " +
			"the authoring/mirroring step renamed BOTH the action and the scene to '%s'.")
			% [fbx_path, clip_name])
		return null
	print("[rebuild-dribble] #300: authored move clip '%s' (%s): len=%.3f tracks=%d"
		% [clip_name, fbx_path, clip.length, clip.get_track_count()])
	if not _retarget_track_paths(clip, reference):
		return null
	_normalize_loop_grid(clip)
	return clip


# ── #294: resolve one polarity's idle SOURCE clip ────────────────────────────
# For "right", `idle_fbx`/`idle_clip` are literally SRC_FBX/SRC_CLIP (see
# POLARITIES' own comment), so this returns `reference` directly -- no second
# load of the same FBX, no retarget (it is already the canonical
# path-convention). For any other polarity ("left") this loads that
# polarity's own idle_fbx/idle_clip fresh and, if `idle_needs_retarget` is
# set, retargets it against `reference` immediately, BEFORE returning it --
# this is the SOURCE-LOAD-TIME fix the file header's #294 Armature/ section
# describes: every downstream reader (idle_clip's duplicate, and PROOF 4's
# `src` comparand in `_proof_idle_stance_centred`) must see only the corrected
# paths, or `_proof_idle_stance_centred` would pose `src` to all-rest and
# become a meaningless check.
func _load_polarity_idle_source(polarity: Dictionary, reference: Animation) -> Animation:
	if polarity["idle_fbx"] == SRC_FBX and polarity["idle_clip"] == SRC_CLIP:
		return reference
	var src := _load_clip(polarity["idle_fbx"], polarity["idle_clip"], "%s idle source" % polarity["suffix"])
	if src == null:
		return null
	if polarity["idle_needs_retarget"]:
		if not _retarget_track_paths(src, reference):
			return null
	return src


# ── #300: put the authored clip on locomotion.res's track-path convention ────
# Blender's FBX export wraps the skeleton in an extra `Armature` node, so Godot
# imports the authored clip with track paths one level deeper than every other
# clip in the library:
#
#   authored : "Armature/Skeleton3D:mixamorig_Hips"
#   library  : "Skeleton3D:mixamorig_Hips"
#
# On scenes/Player.tscn's live rig the deeper paths resolve to NOTHING, so every
# bone silently falls back to skeleton REST and the character renders a frozen
# stance. That is the a45bd1d rest-fallback trap arriving by a new route -- not a
# MISSING track, a MISADDRESSED one -- and it is invisible to a bone-name check,
# because `get_concatenated_subnames()` discards exactly the part that is wrong.
# (LocomotionClipTest's reassuring "total_bone_tracks=53 resolved=53" line
# resolves by bone name and reported this clip healthy while it rendered static.)
#
# So rewrite the node prefix to match the reference clip, and PROVE the result:
# every remapped path must exist in the reference. A bone the reference does not
# have means the rigs have diverged, which is a real problem worth failing on
# rather than papering over with a string edit.
func _retarget_track_paths(clip: Animation, reference: Animation) -> bool:
	var ref_paths := {}
	for i in reference.get_track_count():
		ref_paths[String(reference.track_get_path(i))] = true

	var prefix := ""
	for i in reference.get_track_count():
		var parts := String(reference.track_get_path(i)).split(":")
		if parts.size() >= 2:
			prefix = parts[0]
			break
	if prefix == "":
		push_error("[rebuild-dribble] #300: could not derive a node prefix from the reference clip.")
		return false

	var rewritten := 0
	var unknown: Array[String] = []
	for i in clip.get_track_count():
		var parts := String(clip.track_get_path(i)).split(":")
		if parts.size() < 2:
			continue
		var want := "%s:%s" % [prefix, parts[1]]
		if String(clip.track_get_path(i)) != want:
			clip.track_set_path(i, NodePath(want))
			rewritten += 1
		if not ref_paths.has(want):
			unknown.append(want)

	if not unknown.is_empty():
		push_error(("[rebuild-dribble] #300: %d authored track(s) address bones the reference clip does " +
			"not have, e.g. %s. The authoring rig and %s have diverged -- re-authoring against the " +
			"wrong source FBX would silently rest-pose those bones.")
			% [unknown.size(), unknown.slice(0, 4), SRC_FBX])
		return false

	print(("[rebuild-dribble] #300: track-path retarget -- rewrote %d/%d path(s) onto the '%s:' prefix; " +
		"all resolve against the reference clip.") % [rewritten, clip.get_track_count(), prefix])
	return true


# ── #300: put the authored clip on the tool's own loop-key convention ────────
# There are two legitimate ways to represent a loop, and this repo uses the
# second:
#   (a) INCLUDE the duplicate endpoint -- a key at t == length equal to key 0.
#       Blender bakes 64 frames over [0, 2.100] and naturally produces this.
#   (b) OMIT it -- the last key sits one grid interval BEFORE length (2.0667 s)
#       and LOOP_LINEAR synthesises the wrap. This is what RESAMPLE_KEY_COUNT
#       (63) builds, and what PROOF 3a/3b are calibrated against.
#
# Both play back identically. But (a) silently DISARMS PROOF 3a: that gate's
# power comes from `angle(last_key, key0)` being one ordinary gait step, and
# under (a) it is zero by construction, so the gate can no longer tell a
# LOOP_LINEAR clip from a LOOP_NONE one. The proof correctly refused to pass
# rather than report a meaningless green -- measured, on the first authored
# clip.
#
# So convert (a) -> (b) here: drop a final key that sits at `length` AND is
# redundant with key 0. The redundancy test is what makes this safe -- a track
# whose endpoint genuinely differs from its start is not a duplicate and is
# left alone, so this can never quietly delete real motion.
func _normalize_loop_grid(clip: Animation) -> void:
	const TIME_EPS := 1e-4
	const ROT_EPS_DEG := 0.01
	const POS_EPS_M := 1e-4
	var dropped := 0
	var inspected := 0
	for i in clip.get_track_count():
		var n := clip.track_get_key_count(i)
		if n < 3:
			continue
		inspected += 1
		if absf(clip.track_get_key_time(i, n - 1) - clip.length) > TIME_EPS:
			continue
		var redundant := false
		match clip.track_get_type(i):
			Animation.TYPE_ROTATION_3D:
				var qa: Quaternion = clip.track_get_key_value(i, 0)
				var qb: Quaternion = clip.track_get_key_value(i, n - 1)
				redundant = _quat_angle_deg(qa, qb) < ROT_EPS_DEG
			Animation.TYPE_POSITION_3D:
				var pa: Vector3 = clip.track_get_key_value(i, 0)
				var pb: Vector3 = clip.track_get_key_value(i, n - 1)
				redundant = pa.distance_to(pb) < POS_EPS_M
			_:
				continue
		if redundant:
			clip.track_remove_key(i, n - 1)
			dropped += 1
	print(("[rebuild-dribble] #300: loop-grid normalisation -- dropped %d redundant end key(s) " +
		"across %d track(s), restoring the omit-the-endpoint convention PROOF 3a's gate-power " +
		"check depends on.") % [dropped, inspected])


func bone_of(np: NodePath) -> String:
	return "" if np.get_subname_count() == 0 else String(np.get_subname(0))

func _initialize() -> void:
	var lib = load(LIB_PATH)
	if lib == null or not (lib is AnimationLibrary):
		push_error("[rebuild-dribble] failed to load AnimationLibrary at %s" % LIB_PATH)
		quit(1)
		return

	# ── canonical path-convention reference, loaded exactly ONCE (see SRC_FBX's
	# own doc comment above for the two jobs it does) ─────────────────────────
	var reference := _load_clip(SRC_FBX, SRC_CLIP, "canonical reference")
	if reference == null:
		quit(1)
		return
	print("[rebuild-dribble] canonical reference '%s': len=%.3f tracks=%d loop=%d"
		% [SRC_CLIP, reference.length, reference.get_track_count(), reference.loop_mode])

	# ── #294: polarity-INDEPENDENT setup, hoisted out of the per-polarity loop
	# below. These read the Y Bot rig or the shared animation library, never a
	# polarity's own idle/move clips, so there is exactly one of each no matter
	# how many entries POLARITIES lists.
	var lean_axis := _derive_body_right_axis()
	if lean_axis == Vector3.ZERO:
		push_error("[rebuild-dribble] could not derive the body's right axis from Y Bot's rest pose.")
		quit(1)
		return
	var lean := Quaternion(lean_axis, deg_to_rad(LEAN_DEGREES))

	var raw_skel := _load_raw_skeleton()
	if raw_skel == null:
		push_error("[rebuild-dribble] #298: could not load a Skeleton3D from a fresh %s instance." % YBOT_FBX)
		quit(1)
		return

	if not lib.has_animation("run"):
		push_error("[rebuild-dribble] #298: locomotion.res has no 'run' clip to source a stride from.")
		quit(1)
		return
	var run_anim: Animation = lib.get_animation("run")

	var leg_bone_names: Array = LEG_CHAIN_LEFT + LEG_CHAIN_RIGHT
	var identity_q: Array = [1.0, 0.0, 0.0, 0.0]

	# Idempotency: purge the pre-#294 two-clip names before adding anything.
	# Nothing references LEGACY_IDLE_NAME/LEGACY_MOVE_NAME any more (POLARITIES
	# above is what ships now), and leaving a stale pair behind would corrupt
	# the library's clip inventory exactly like a stale same-name rebuild
	# would -- see those consts' own doc comment.
	if lib.has_animation(LEGACY_IDLE_NAME):
		lib.remove_animation(LEGACY_IDLE_NAME)
	if lib.has_animation(LEGACY_MOVE_NAME):
		lib.remove_animation(LEGACY_MOVE_NAME)

	# ── #294: the rest of this function is the SAME single-polarity pipeline
	# #285/#298/#300 always ran, just run once per POLARITIES entry. See the
	# file header's "one polarity, one independent pipeline instance" section
	# for why nothing here needs "left" and "right" to be compared against
	# each other -- only each polarity's own idle/move pair.
	for polarity in POLARITIES:
		var suffix: String = polarity["suffix"]
		var idle_out_name := StringName("dribbleidle%s" % suffix)
		var move_out_name := StringName("dribblemove%s" % suffix)

		# ── source-load-time retarget (file header's #294 Armature/ section) ──
		var src := _load_polarity_idle_source(polarity, reference)
		if src == null:
			quit(1)
			return
		print("[rebuild-dribble] %s idle source '%s' (%s): len=%.3f tracks=%d loop=%d"
			% [suffix, polarity["idle_clip"], polarity["idle_fbx"], src.length, src.get_track_count(), src.loop_mode])

		# ── #285a: the real clip, verbatim, LOOPED ───────────────────────────
		# FBX import defaults to LOOP_NONE. `catch` (a one-shot grab) wanted
		# that; a dribble stance is a sustained loop and MUST be set
		# explicitly here -- the single easiest thing to get silently wrong in
		# this issue.
		var idle_clip: Animation = src.duplicate(true)
		idle_clip.loop_mode = Animation.LOOP_LINEAR

		# ── the moving endpoint ────────────────────────────────────────────
		# #285b/#298 derived it from `src`; #300 authors it in Blender
		# instead. Either way it MUST come out the same length as this
		# polarity's OWN idle endpoint: equal-length blend points advance by
		# the same delta and so stay phase-locked, which means the two
		# contributions differ ONLY by the drive posture at every frame --
		# never by dribble cycle phase. That is what keeps the blended arc
		# short at all times, and it is the corridor argument's load-bearing
		# assumption (per-polarity -- see the file header's #294 section).
		var move_clip: Animation
		if USE_AUTHORED_MOVE_CLIP:
			move_clip = _load_authored_move_clip(polarity["move_fbx"], polarity["move_clip"], reference)
			if move_clip == null:
				quit(1)
				return
			if not is_equal_approx(move_clip.length, src.length):
				push_error(("[rebuild-dribble] #300: %s's authored clip is %.4f s but its idle endpoint is " +
					"%.4f s. Unequal-length blendspace endpoints drift out of phase, so the two " +
					"contributions would differ by dribble PHASE as well as by posture -- which is " +
					"exactly what the #287 corridor argument assumes cannot happen.")
					% [suffix, move_clip.length, src.length])
				quit(1)
				return
		else:
			move_clip = src.duplicate(true)
		# FBX import defaults to LOOP_NONE for the authored clip too -- set it
		# explicitly in both paths rather than relying on the import.
		move_clip.loop_mode = Animation.LOOP_LINEAR

		# The authored clip already carries its own lean, crouch and bob,
		# keyframed in Blender -- re-applying them here would double them.
		# Dead while USE_AUTHORED_MOVE_CLIP is true; kept compiling/working
		# per that flag's own doc comment (the #301 A/B).
		var leaned := 0
		var crouched := 0
		if not USE_AUTHORED_MOVE_CLIP:
			leaned = _apply_lean(move_clip, LEAN_BONE, lean)
			if leaned <= 0:
				push_error("[rebuild-dribble] no '%s' rotation track found to lean -- refusing to save a "
					% LEAN_BONE + "moving endpoint identical to the idle one.")
				quit(1)
				return

			crouched = _apply_crouch(move_clip, CROUCH_BONE, CROUCH_DROP_M)
			if crouched <= 0:
				push_error("[rebuild-dribble] no '%s' position track found to crouch -- refusing to save a "
					% CROUCH_BONE + "moving endpoint without the drive stance.")
				quit(1)
				return

		# ── #298: leg-stride transplant onto move_clip ───────────────────────
		# #300: the authored clip's legs are keyframed absolutely, so there is
		# no transplanted delta and -- more importantly -- no static stagger
		# left to cancel; c_leg_{left,right}_d stay identity. Dead while
		# USE_AUTHORED_MOVE_CLIP is true.
		var c_leg_left_d: Array = identity_q
		var c_leg_right_d: Array = identity_q
		if not USE_AUTHORED_MOVE_CLIP:
			var leg_stride_result := _apply_leg_stride(move_clip, src, run_anim, raw_skel, lean_axis, _facing)
			if not leg_stride_result["ok"]:
				quit(1)
				return
			c_leg_left_d = leg_stride_result["c_leg_left_d"]
			c_leg_right_d = leg_stride_result["c_leg_right_d"]

		# fix/298 (round 2): apply the IDENTICAL static stance-centring
		# correction to dribbleidle BEFORE the head-shift/pose-delta
		# measurements below, so those describe the clips that actually get
		# saved. OFF by default -- see CENTRE_IDLE_ENDPOINT's own doc comment
		# for the falsified-hypothesis evidence (measured WORSE, not merely
		# unhelpful) that keeps this flag false.
		if CENTRE_IDLE_ENDPOINT:
			if not _apply_static_stance_correction(idle_clip, src, raw_skel, c_leg_left_d, c_leg_right_d):
				quit(1)
				return

		# Prove the lean goes FORWARD, geometrically, instead of trusting the
		# cross-product order: pose a real skeleton with each clip and check
		# the head actually moved along the facing axis. A sign error here
		# would draft a lean-BACK -- which reads as a step-back/retreat
		# posture, a different move rather than merely a rough-looking one,
		# so it is worth an assertion, independently for each polarity.
		var head_shift := _head_shift_along(idle_clip, move_clip, _facing)
		print("[rebuild-dribble] %s: head displacement along facing axis = %+.4f m" % [suffix, head_shift])
		if head_shift <= 0.0:
			push_error(("[rebuild-dribble] %s: the lean moved the head %.4f m along facing -- that is a lean " +
				"BACK. Check the lean-axis handedness in _derive_body_right_axis().") % [suffix, head_shift])
			quit(1)
			return

		# Guard the "distinct silhouette" bar with a real measurement rather
		# than trusting the edit landed (the repo's prove-match-count-> 0
		# convention).
		var spread := _max_pose_delta(idle_clip, move_clip)
		if USE_AUTHORED_MOVE_CLIP:
			print(("[rebuild-dribble] #300: %s moving endpoint taken verbatim from %s (clip '%s'); " +
				"max endpoint-to-endpoint pose delta = %.1f deg (PROOF 5 -- print only, the corridor " +
				"question is the harness's job)") % [suffix, polarity["move_fbx"], polarity["move_clip"], spread])
		else:
			print(("[rebuild-dribble] %s: leaned %d key(s) on '%s' by %.0f deg about %s, crouched %d key(s) on '%s' by %.2f m; " +
				"max endpoint-to-endpoint pose delta = %.1f deg (PROOF 5 -- print only, the corridor question is the " +
				"harness's job)") % [suffix, leaned, LEAN_BONE, LEAN_DEGREES, lean_axis, crouched, CROUCH_BONE, CROUCH_DROP_M, spread])
		if spread < 5.0:
			push_error("[rebuild-dribble] %s: endpoints differ by only %.1f deg -- not a distinct silhouette." % [suffix, spread])
			quit(1)
			return

		# ── #298 PROOFs 1-4, plus PROOF 6 -- all evaluated per polarity ──────
		if not _proof_stride(move_clip, raw_skel, _facing):
			quit(1)
			return
		if not _proof_anatomy(move_clip, raw_skel):
			quit(1)
			return
		if not _proof_loop_seam(move_clip, leg_bone_names):
			quit(1)
			return
		# When CENTRE_IDLE_ENDPOINT is off, hand PROOF 4 identity rotations:
		# its "dribbleidle == C_leg * source" check then reduces to
		# "dribbleidle == source", i.e. #285a's original verbatim contract,
		# with no second code path to keep in sync.
		var proof4_left: Array = c_leg_left_d if CENTRE_IDLE_ENDPOINT else identity_q
		var proof4_right: Array = c_leg_right_d if CENTRE_IDLE_ENDPOINT else identity_q
		if not _proof_idle_stance_centred(idle_clip, src, raw_skel, leg_bone_names, proof4_left, proof4_right):
			quit(1)
			return
		if not _proof_support_band(move_clip, run_anim, raw_skel):
			quit(1)
			return

		# Idempotency: drop any previous build of THIS polarity's two clips
		# before adding, so re-running re-derives them from the pristine FBX
		# rather than stacking edits.
		if lib.has_animation(idle_out_name):
			lib.remove_animation(idle_out_name)
		if lib.has_animation(move_out_name):
			lib.remove_animation(move_out_name)
		lib.add_animation(idle_out_name, idle_clip)
		lib.add_animation(move_out_name, move_clip)
		print("[rebuild-dribble] %s: added '%s' and '%s' to the library." % [suffix, idle_out_name, move_out_name])

	# Save ONCE, after every polarity has built and proved clean -- saving
	# per-polarity would leave a half-built library on disk if a later
	# polarity's proof failed partway through.
	var err := ResourceSaver.save(lib, LIB_PATH)
	if err != OK:
		push_error("[rebuild-dribble] ResourceSaver.save failed with error %d" % err)
		quit(1)
		return

	print("[rebuild-dribble] saved %s; clips now: %s" % [LIB_PATH, str(lib.get_animation_list())])
	quit(0)

var _facing := Vector3.ZERO   # set by _derive_body_right_axis, reused by the proof

# The axis to lean ABOUT, so that a POSITIVE rotation tips the torso forward.
# Derived from Y Bot's own rest pose rather than hardcoded, because "which world
# axis is forward" depends on the FBX's import orientation -- guessing it
# backwards would draft a lean-BACK (a step-back posture), which reads as a
# different move rather than merely looking rough.
#
# Handedness (Godot is right-handed, +Y up): a forward lean tips the spine's own
# up (+Y) TOWARD facing, so the axis must satisfy rot(axis, +theta): up -> facing.
# That is `up x facing`, NOT `facing x up` -- the two differ by sign and the
# wrong one leans back. _head_shift_along() is the standing proof, so this
# comment can never silently drift from what the code does.
#
# #298 also uses `_facing` (the module var this function sets as a side effect)
# as the world forward axis for the stride/anatomy proofs below, ALWAYS derived
# from a fresh, un-anchored Y Bot.fbx instance -- never from scenes/Player.tscn,
# whose BlendRestAnchor node mutates the UpLeg bones' rest and would silently
# swing this axis ~62 deg off (confirmed empirically; see the #298 task record).
func _derive_body_right_axis() -> Vector3:
	var ybot: Node = load(YBOT_FBX).instantiate()
	var skel: Skeleton3D = _find(ybot, "Skeleton3D")
	if skel == null:
		return Vector3.ZERO
	# Toes point forward: the foot->toe vector in the XZ plane is the facing.
	var foot := skel.find_bone("mixamorig_LeftFoot")
	var toe := skel.find_bone("mixamorig_LeftToeBase")
	if foot < 0 or toe < 0:
		return Vector3.ZERO
	var p_foot: Vector3 = skel.get_bone_global_rest(foot).origin
	var p_toe: Vector3 = skel.get_bone_global_rest(toe).origin
	var forward := (p_toe - p_foot)
	forward.y = 0.0
	if forward.length() < 0.001:
		return Vector3.ZERO
	forward = forward.normalized()
	_facing = forward
	var axis := Vector3.UP.cross(forward).normalized()
	print("[rebuild-dribble] derived facing=%s -> lean axis (up x facing)=%s" % [forward, axis])
	return axis

# Pre-multiplies every key of `bone`'s rotation track by `lean`. Pre-multiplication
# (world-space delta * local rotation) tilts the bone in the skeleton's frame,
# which is what a torso lean is; post-multiplying would twist it about its own
# already-rotated local axis instead.
func _apply_lean(anim: Animation, bone: String, lean: Quaternion) -> int:
	var touched := 0
	for i in anim.get_track_count():
		if anim.track_get_type(i) != Animation.TYPE_ROTATION_3D:
			continue
		if bone_of(anim.track_get_path(i)) != bone:
			continue
		for k in anim.track_get_key_count(i):
			var q: Quaternion = anim.track_get_key_value(i, k)
			anim.track_set_key_value(i, k, (lean * q).normalized())
			touched += 1
	return touched

# Lowers every key of `bone`'s POSITION_3D track by `drop` meters on Y. This is
# a straight component subtraction, not a rotation -- POSITION_3D keys are
# already in the bone's own (parent-relative) space, so no basis conjugation is
# needed the way rotations require (see the header's Y Bot antipode warning).
func _apply_crouch(anim: Animation, bone: String, drop: float) -> int:
	var touched := 0
	for i in anim.get_track_count():
		if anim.track_get_type(i) != Animation.TYPE_POSITION_3D:
			continue
		if bone_of(anim.track_get_path(i)) != bone:
			continue
		for k in anim.track_get_key_count(i):
			var p: Vector3 = anim.track_get_key_value(i, k)
			anim.track_set_key_value(i, k, Vector3(p.x, p.y - drop, p.z))
			touched += 1
	return touched

# Largest per-bone angular difference between the two endpoints at matched
# phase -- the honest measure of "are these silhouettes actually distinct".
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

# Poses a real Y Bot skeleton with each clip's frame 0 and returns how far the
# head moved along `axis` from the unleaned pose to the leaned one. Positive =
# the torso tipped toward the facing direction, i.e. a genuine forward lean.
func _head_shift_along(unleaned: Animation, leaned: Animation, axis: Vector3) -> float:
	var skel: Skeleton3D = _find(load(YBOT_FBX).instantiate(), "Skeleton3D")
	if skel == null:
		return 0.0
	var head := skel.find_bone("mixamorig_Head")
	if head < 0:
		return 0.0
	var before := _pose_and_read(skel, unleaned, head)
	var after := _pose_and_read(skel, leaned, head)
	return (after - before).dot(axis)

# Walks the bone chain by hand rather than calling get_bone_global_pose(): a
# Skeleton3D that was never added to the SceneTree does not recompute its global
# poses, so that call returns an unchanged transform and this proof would pass
# vacuously (it measured exactly 0.0000 m before this was fixed). Manual FK
# depends on nothing but the rest pose and the clip's own keys.
func _pose_and_read(skel: Skeleton3D, anim: Animation, bone_idx: int) -> Vector3:
	var poses := {}
	for i in anim.get_track_count():
		if anim.track_get_type(i) != Animation.TYPE_ROTATION_3D:
			continue
		var b := skel.find_bone(bone_of(anim.track_get_path(i)))
		if b < 0 or anim.track_get_key_count(i) <= 0:
			continue
		poses[b] = anim.track_get_key_value(i, 0)

	# Root-down chain to the target bone.
	var chain := []
	var walk := bone_idx
	while walk >= 0:
		chain.push_front(walk)
		walk = skel.get_bone_parent(walk)

	var acc := Transform3D.IDENTITY
	for b in chain:
		var rest: Transform3D = skel.get_bone_rest(b)
		# ROTATION_3D keys are absolute LOCAL rotations (see BlendRestAnchor's
		# doc), so an animated bone REPLACES the rest basis' rotation; scale and
		# origin carry over from the rest.
		var local := rest
		if poses.has(b):
			local = Transform3D(Basis(poses[b] as Quaternion).scaled(rest.basis.get_scale()), rest.origin)
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

# ═══════════════════════════════════════════════════════════════════════════
# #298 — moving-dribble leg-stride transplant
# ═══════════════════════════════════════════════════════════════════════════

func _load_raw_skeleton() -> Skeleton3D:
	var inst: Node = load(YBOT_FBX).instantiate()
	return _find(inst, "Skeleton3D")

# Angle (geodesic degrees) between two rotations, shortest-arc / double-cover
# safe (matches the convention LocomotionClipTest.cs uses).
func _quat_angle_deg(a: Quaternion, b: Quaternion) -> float:
	var d: float = clampf(absf(a.normalized().dot(b.normalized())), -1.0, 1.0)
	return rad_to_deg(2.0 * acos(d))

# Root-down bone-index chain for `bone_idx`, ending WITH it. Shared by both FK
# helpers below so a rotation-only walk and a full-transform walk never drift
# out of sync with each other.
func _bone_chain(skel: Skeleton3D, bone_idx: int) -> Array:
	var chain := []
	var walk := bone_idx
	while walk >= 0:
		chain.push_front(walk)
		walk = skel.get_bone_parent(walk)
	return chain

# bone_idx -> track_idx for every ROTATION_3D track in `anim` that resolves
# against `skel`. Built once per (skel, anim) pair and reused across every
# sample time, rather than re-scanning the whole track list per bone per frame.
func _rotation_track_cache(skel: Skeleton3D, anim: Animation) -> Dictionary:
	var cache := {}
	for i in anim.get_track_count():
		if anim.track_get_type(i) != Animation.TYPE_ROTATION_3D:
			continue
		var b := skel.find_bone(bone_of(anim.track_get_path(i)))
		if b >= 0:
			cache[b] = i
	return cache

# Same, for POSITION_3D tracks (only mixamorig_Hips carries one, on the dribble
# family -- run has none, per the header's fact table).
func _position_track_cache(skel: Skeleton3D, anim: Animation) -> Dictionary:
	var cache := {}
	for i in anim.get_track_count():
		if anim.track_get_type(i) != Animation.TYPE_POSITION_3D:
			continue
		var b := skel.find_bone(bone_of(anim.track_get_path(i)))
		if b >= 0:
			cache[b] = i
	return cache

# L_C(b,t): the clip's own ROTATION_3D value at time t, or Y Bot's rest
# rotation if `anim` carries no track for this bone (the algorithm's own
# fallback rule -- this is what lets mixamorig_{L,R}Toe_End sit correctly in a
# chain even though neither `run` nor the dribble source tracks it).
func _local_rotation(skel: Skeleton3D, rot_cache: Dictionary, anim: Animation, bone_idx: int, t: float) -> Quaternion:
	if rot_cache.has(bone_idx):
		return anim.rotation_track_interpolate(rot_cache[bone_idx], t).normalized()
	return skel.get_bone_rest(bone_idx).basis.orthonormalized().get_rotation_quaternion()

# G_C(b,t): pure ROTATION composition (no origins) from mixamorig_Hips down to
# and including `bone_idx`, i.e. G_C(Hips,t) = L_C(Hips,t) and
# G_C(child,t) = G_C(parent,t) * L_C(child,t). This is the exact quantity the
# #298 algorithm calls G_C(b,t) and D(b,t) is built from.
func _global_rotation(skel: Skeleton3D, rot_cache: Dictionary, anim: Animation, bone_idx: int, t: float) -> Quaternion:
	var acc := Quaternion.IDENTITY
	for b in _bone_chain(skel, bone_idx):
		acc = (acc * _local_rotation(skel, rot_cache, anim, b, t)).normalized()
	return acc

# Full transform (rotation AND position) FK from mixamorig_Hips down to
# `bone_idx`, used only where an actual world POSITION is needed (cadence
# bounce counting, the t0 neutral-reference search, and the anatomy/stride
# proofs) -- never for the D/G_new algebra itself, which is pure rotation per
# the algorithm's own "ignore origins entirely" rule.
func _global_transform(skel: Skeleton3D, rot_cache: Dictionary, pos_cache: Dictionary, anim: Animation, bone_idx: int, t: float) -> Transform3D:
	var acc := Transform3D.IDENTITY
	for b in _bone_chain(skel, bone_idx):
		var rest: Transform3D = skel.get_bone_rest(b)
		var q := _local_rotation(skel, rot_cache, anim, b, t)
		var origin: Vector3 = rest.origin
		if pos_cache.has(b):
			origin = anim.position_track_interpolate(pos_cache[b], t)
		acc = acc * Transform3D(Basis(q).scaled(rest.basis.get_scale()), origin)
	return acc

# ── fix/298: stance-centring solve helpers ───────────────────────────────────
# Identical FK walk to _global_transform, with one addition: right after the
# bone at `correct_bone_idx` is folded into the accumulator, `correction` is
# LEFT-multiplied onto the accumulated basis (leaving its already-computed
# origin untouched). Left-multiplying a global rotation and then continuing
# the ordinary root-to-tip walk underneath it is exactly what carries a single
# static correction down through every descendant bone -- the same
# associativity argument the #298 header's D(b,t) composition already relies
# on, just applied one level higher (at the leg root instead of at each leg
# bone individually). Plain (float32) Quaternion/Transform3D math is
# sufficient here: this function backs the bisection solve below, whose own
# convergence gate is 0.01 m -- far looser than the double-precision self-check
# elsewhere in this file, which exists for a different (exact-identity) reason.
func _global_transform_leg_corrected(skel: Skeleton3D, rot_cache: Dictionary, pos_cache: Dictionary,
		anim: Animation, bone_idx: int, t: float, correct_bone_idx: int, correction: Quaternion) -> Transform3D:
	var acc := Transform3D.IDENTITY
	var corr_basis := Basis(correction)
	for b in _bone_chain(skel, bone_idx):
		var rest: Transform3D = skel.get_bone_rest(b)
		var q := _local_rotation(skel, rot_cache, anim, b, t)
		var origin: Vector3 = rest.origin
		if pos_cache.has(b):
			origin = anim.position_track_interpolate(pos_cache[b], t)
		acc = acc * Transform3D(Basis(q).scaled(rest.basis.get_scale()), origin)
		if b == correct_bone_idx:
			acc = Transform3D(corr_basis * acc.basis, acc.origin)
	return acc

# Time-averaged hips-relative forward projection of `toe_idx`, over
# STRIDE_PHASE_COUNT phases of `anim` -- the plain (uncorrected) measurement
# step 2 of the stance-centring fix needs for avgL/avgR.
func _average_toe_projection(anim: Animation, skel: Skeleton3D, rot_cache: Dictionary, pos_cache: Dictionary,
		hips_idx: int, toe_idx: int, forward: Vector3) -> float:
	var total := 0.0
	for i in STRIDE_PHASE_COUNT:
		var t: float = float(i) * anim.length / float(STRIDE_PHASE_COUNT)
		var hips_pos: Vector3 = _global_transform(skel, rot_cache, pos_cache, anim, hips_idx, t).origin
		var toe_pos: Vector3 = _global_transform(skel, rot_cache, pos_cache, anim, toe_idx, t).origin
		total += (toe_pos - hips_pos).dot(forward)
	return total / float(STRIDE_PHASE_COUNT)

# Same measurement, but with `correction` applied at `upleg_idx` (see
# _global_transform_leg_corrected) -- this is what the bisection solve below
# evaluates at each candidate theta.
func _average_toe_projection_corrected(anim: Animation, skel: Skeleton3D, rot_cache: Dictionary, pos_cache: Dictionary,
		hips_idx: int, toe_idx: int, upleg_idx: int, correction: Quaternion, forward: Vector3) -> float:
	var total := 0.0
	for i in STRIDE_PHASE_COUNT:
		var t: float = float(i) * anim.length / float(STRIDE_PHASE_COUNT)
		var hips_pos: Vector3 = _global_transform(skel, rot_cache, pos_cache, anim, hips_idx, t).origin
		var toe_pos: Vector3 = _global_transform_leg_corrected(skel, rot_cache, pos_cache, anim, toe_idx, t, upleg_idx, correction).origin
		total += (toe_pos - hips_pos).dot(forward)
	return total / float(STRIDE_PHASE_COUNT)

# Solves theta such that C_leg = Quaternion(right_axis, theta), pre-multiplied
# onto this leg's global rotation at `upleg_idx`, moves this leg's own
# time-averaged toe projection to `target` (avgMid). BISECTION, not a closed
# form, per the #298 spec: the projection-vs-theta relationship is a rotation
# of a fixed 3D offset about a fixed axis (a sinusoid in theta), not something
# worth inverting in closed form when a 40-iteration bisection over a
# +-60 deg bracket is exact to a small fraction of a millimetre. The caller
# checks the returned residual against STANCE_CENTRE_RESIDUAL_TOLERANCE_M and
# fails loud rather than trusting convergence.
func _solve_stance_correction(anim: Animation, skel: Skeleton3D, rot_cache: Dictionary, pos_cache: Dictionary,
		hips_idx: int, toe_idx: int, upleg_idx: int, right_axis: Vector3, target: float, forward: Vector3) -> Dictionary:
	var lo := deg_to_rad(-STANCE_CENTRE_BRACKET_DEG)
	var hi := deg_to_rad(STANCE_CENTRE_BRACKET_DEG)
	var f_lo := _average_toe_projection_corrected(anim, skel, rot_cache, pos_cache, hips_idx, toe_idx, upleg_idx,
		Quaternion(right_axis, lo), forward) - target
	var theta := 0.0
	for i in STANCE_CENTRE_ITERATIONS:
		theta = (lo + hi) * 0.5
		var f_mid := _average_toe_projection_corrected(anim, skel, rot_cache, pos_cache, hips_idx, toe_idx, upleg_idx,
			Quaternion(right_axis, theta), forward) - target
		if sign(f_mid) == sign(f_lo):
			lo = theta
			f_lo = f_mid
		else:
			hi = theta

	var solved := Quaternion(right_axis, theta)
	var after := _average_toe_projection_corrected(anim, skel, rot_cache, pos_cache, hips_idx, toe_idx, upleg_idx,
		solved, forward)
	return {
		"theta_deg": rad_to_deg(theta),
		"after": after,
		"residual": absf(after - target),
		"c_leg_quat": solved,
	}

# ── Double-precision quaternion helpers ──────────────────────────────────────
# Godot's built-in Quaternion type does its internal math in `real_t`, which is
# float32 on a standard (non `precision=double`) build. Composing 3-4 of them
# in a chain (exactly what G_C(b,t) does) accumulates enough float32 rounding
# that the step-4 self-check -- which is otherwise an EXACT algebraic identity,
# G_new(b,t) === D(b,t)*G_dribble(b,t) -- measured spurious residuals up to
# 0.0396 deg from that alone. Confirmed empirically, not assumed: an isolated
# test composing random quaternions through the SAME chain depths (3, 2, 3
# multiplies for p/d/g respectively) via Godot's own Quaternion operators
# reproduced a 0.056 deg residual on the identical formula with no other
# change; redone with the manual double-precision arithmetic below, the same
# 2000-trial sweep's worst residual dropped to 2.4e-6 deg. GDScript's own
# `float` is always double-precision at the script level regardless of the
# engine's real_t build, so every step of the D/G_new algebra is done here as
# a plain [w,x,y,z] Array in double precision, and converted to a real
# Quaternion only at the one point that data must eventually take that
# rounding anyway: writing the final track key (Step 5).
func _dq_from(q: Quaternion) -> Array:
	return [q.w, q.x, q.y, q.z]

func _dq_to_quat(a: Array) -> Quaternion:
	return Quaternion(a[1], a[2], a[3], a[0]).normalized()

func _dq_mul(a: Array, b: Array) -> Array:
	var w1: float = a[0]; var x1: float = a[1]; var y1: float = a[2]; var z1: float = a[3]
	var w2: float = b[0]; var x2: float = b[1]; var y2: float = b[2]; var z2: float = b[3]
	return [
		w1 * w2 - x1 * x2 - y1 * y2 - z1 * z2,
		w1 * x2 + x1 * w2 + y1 * z2 - z1 * y2,
		w1 * y2 - x1 * z2 + y1 * w2 + z1 * x2,
		w1 * z2 + x1 * y2 - y1 * x2 + z1 * w2,
	]

func _dq_norm(a: Array) -> Array:
	var n: float = sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2] + a[3] * a[3])
	return [a[0] / n, a[1] / n, a[2] / n, a[3] / n]

# Unit-quaternion inverse == conjugate; every DQuat here is normalized
# immediately after each multiply, so this is exact, not an approximation.
func _dq_inv(a: Array) -> Array:
	return [a[0], -a[1], -a[2], -a[3]]

# `t<=0`/`t>=1` return an endpoint EXACTLY, with no trig call at all -- what
# STRIDE_AMPLITUDE=1.0 relies on: D_scaled must equal D_raw exactly at full
# amplitude, not merely close to it.
func _dq_slerp(a: Array, b: Array, t: float) -> Array:
	if t <= 0.0:
		return a
	if t >= 1.0:
		return b
	var dot: float = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3]
	var bb := b
	if dot < 0.0:
		bb = [-b[0], -b[1], -b[2], -b[3]]
		dot = -dot
	dot = clampf(dot, -1.0, 1.0)
	var theta: float = acos(dot)
	if theta < 1e-9:
		return a
	var sin_theta: float = sin(theta)
	var wa: float = sin((1.0 - t) * theta) / sin_theta
	var wb: float = sin(t * theta) / sin_theta
	return _dq_norm([
		a[0] * wa + bb[0] * wb, a[1] * wa + bb[1] * wb, a[2] * wa + bb[2] * wb, a[3] * wa + bb[3] * wb,
	])

func _dq_angle_deg(a: Array, b: Array) -> float:
	var d: float = clampf(absf(a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3]), -1.0, 1.0)
	return rad_to_deg(2.0 * acos(d))

# L_C(b,t), double precision -- identical fallback rule to _local_rotation.
func _local_rotation_d(skel: Skeleton3D, rot_cache: Dictionary, anim: Animation, bone_idx: int, t: float) -> Array:
	if rot_cache.has(bone_idx):
		return _dq_norm(_dq_from(anim.rotation_track_interpolate(rot_cache[bone_idx], t)))
	return _dq_norm(_dq_from(skel.get_bone_rest(bone_idx).basis.orthonormalized().get_rotation_quaternion()))

# G_C(b,t), double precision -- identical chain/composition rule to
# _global_rotation, just accumulated without Quaternion's float32 internals.
func _global_rotation_d(skel: Skeleton3D, rot_cache: Dictionary, anim: Animation, bone_idx: int, t: float) -> Array:
	var acc: Array = [1.0, 0.0, 0.0, 0.0]
	for b in _bone_chain(skel, bone_idx):
		acc = _dq_norm(_dq_mul(acc, _local_rotation_d(skel, rot_cache, anim, b, t)))
	return acc

# PROOF 0: counts local minima (with hysteresis) in mixamorig_RightHand's
# global Y relative to Hips, sampled at CADENCE_PHASE_COUNT evenly-spaced
# phases across the SOURCE dribble clip. The ball is on the RIGHT hand by
# default (commit f7d13fd), which is why RightHand is the probe bone -- each
# dribble bounce shows up as a Y valley as the hand follows the ball down and
# back up.
#
# Circular hysteresis valley count: the signal is one full loop of a
# LOOP_LINEAR clip, so a bounce straddling the sample array's wrap boundary
# (index n-1 -> 0) must not be missed or double-counted. This scans a margin
# of extra samples wrapped from the start, but only counts a confirmed minimum
# whose index falls within the original (un-wrapped) window.
func _count_bounces(skel: Skeleton3D, rot_cache: Dictionary, pos_cache: Dictionary, anim: Animation,
		hips_idx: int, hand_idx: int, clip_len: float) -> Dictionary:
	var n := CADENCE_PHASE_COUNT
	var ys := PackedFloat64Array()
	ys.resize(n)
	for i in n:
		var t: float = float(i) * clip_len / float(n)
		var hips_y: float = _global_transform(skel, rot_cache, pos_cache, anim, hips_idx, t).origin.y
		var hand_y: float = _global_transform(skel, rot_cache, pos_cache, anim, hand_idx, t).origin.y
		ys[i] = hand_y - hips_y

	var y_min: float = ys[0]
	var y_max: float = ys[0]
	for v in ys:
		y_min = minf(y_min, v)
		y_max = maxf(y_max, v)
	var span := y_max - y_min
	var hyst := span * CADENCE_HYSTERESIS_FRACTION

	var margin := 20
	var ext := PackedFloat64Array()
	for i in n:
		ext.append(ys[i])
	for i in margin:
		ext.append(ys[i])

	var count := 0
	var mode := "unknown"
	var extreme_val: float = ext[0]
	var extreme_idx := 0
	for i in range(1, ext.size()):
		var v: float = ext[i]
		if mode == "unknown":
			if v < extreme_val - 1e-9:
				extreme_val = v
				extreme_idx = i
				mode = "down"
			elif v > extreme_val + 1e-9:
				extreme_val = v
				extreme_idx = i
				mode = "up"
		elif mode == "down":
			if v < extreme_val:
				extreme_val = v
				extreme_idx = i
			elif v > extreme_val + hyst:
				if extreme_idx < n:
					count += 1
				extreme_val = v
				extreme_idx = i
				mode = "up"
		elif mode == "up":
			if v > extreme_val:
				extreme_val = v
				extreme_idx = i
			elif v < extreme_val - hyst:
				extreme_val = v
				extreme_idx = i
				mode = "down"

	return {"n": count, "span": span, "period": (clip_len / count) if count > 0 else 0.0}

# The full #298 leg-stride transplant: PROOF 0, step 2 (t0), steps 2.5/3-4
# (D/G_new algebra + self-check), and step 5 (writing the tracks). Returns a
# Dictionary {"ok": bool, "c_leg_left_d": Array, "c_leg_right_d": Array} --
# fix/298 (round 2) hoists the solved-by-bisection C_leg quaternions (double-
# precision DQuat arrays, see the _dq_* helpers) out to the caller, which
# applies the IDENTICAL correction to dribbleidle via
# `_apply_static_stance_correction` (see the file header). "ok"=false (with a
# push_error already logged) on any failure -- callers must quit(1) without
# saving; the c_leg_* entries are meaningless in that case.
func _apply_leg_stride(move_clip: Animation, src: Animation, run_anim: Animation, raw_skel: Skeleton3D, right_axis: Vector3, forward: Vector3) -> Dictionary:
	var hips_idx := raw_skel.find_bone(CROUCH_BONE)
	var hand_idx := raw_skel.find_bone(RIGHT_HAND_BONE)
	if hips_idx < 0 or hand_idx < 0:
		push_error("[rebuild-dribble] #298: could not find %s/%s on the raw Y Bot skeleton." % [CROUCH_BONE, RIGHT_HAND_BONE])
		return {"ok": false}
	var left_toe_idx := raw_skel.find_bone("mixamorig_LeftToeBase")
	var right_toe_idx := raw_skel.find_bone("mixamorig_RightToeBase")
	if left_toe_idx < 0 or right_toe_idx < 0:
		push_error("[rebuild-dribble] #298: could not find LeftToeBase/RightToeBase on the raw Y Bot skeleton.")
		return {"ok": false}

	var rot_cache_src := _rotation_track_cache(raw_skel, src)
	var pos_cache_src := _position_track_cache(raw_skel, src)
	var rot_cache_run := _rotation_track_cache(raw_skel, run_anim)
	var pos_cache_run := _position_track_cache(raw_skel, run_anim)

	var dribble_len: float = src.length
	var run_len: float = run_anim.length

	# ── PROOF 0: cadence ──────────────────────────────────────────────────
	var bounce := _count_bounces(raw_skel, rot_cache_src, pos_cache_src, src, hips_idx, hand_idx, dribble_len)
	var n: int = bounce["n"]
	print(("[rebuild-dribble] #298 PROOF 0: RightHand (ball-hand default, commit f7d13fd) bounce count over " +
		"the source dribble clip = %d (excursion span=%.4f m, hysteresis=%.4f m, per-bounce period=%.4f s)")
		% [n, bounce["span"], bounce["span"] * CADENCE_HYSTERESIS_FRACTION, bounce["period"]])
	if n < 1:
		push_error("[rebuild-dribble] #298 PROOF 0 FAILED: measured %d bounces (< 1) -- cannot derive a cadence." % n)
		return {"ok": false}
	if n > CADENCE_MAX_PLAUSIBLE_BOUNCES:
		push_error(("[rebuild-dribble] #298 PROOF 0 FAILED: measured %d bounces (> %d), implausible for a 2.1s " +
			"dribble loop -- refusing to resample run at an absurd rate.") % [n, CADENCE_MAX_PLAUSIBLE_BOUNCES])
		return {"ok": false}

	var seconds_per_cycle: float = dribble_len / float(n)
	print(("[rebuild-dribble] #298 cadence: N=%d, dribbleLen=%.4f s, runLen=%.4f s, " +
		"effective seconds-per-gait-cycle=%.4f s") % [n, dribble_len, run_len, seconds_per_cycle])

	# ── Step 2: neutral reference t0 ──────────────────────────────────────
	var best_t0 := 0.0
	var best_abs := INF
	for i in T0_SAMPLE_COUNT:
		var t: float = float(i) * run_len / float(T0_SAMPLE_COUNT)
		var hips_pos: Vector3 = _global_transform(raw_skel, rot_cache_run, pos_cache_run, run_anim, hips_idx, t).origin
		var lp: float = (_global_transform(raw_skel, rot_cache_run, pos_cache_run, run_anim, left_toe_idx, t).origin - hips_pos).dot(forward)
		var rp: float = (_global_transform(raw_skel, rot_cache_run, pos_cache_run, run_anim, right_toe_idx, t).origin - hips_pos).dot(forward)
		var d: float = absf(lp - rp)
		if d < best_abs:
			best_abs = d
			best_t0 = t
	print(("[rebuild-dribble] #298 neutral reference t0=%.4f s (min |projLeft - projRight| = %.6f m " +
		"over %d run phases)") % [best_t0, best_abs, T0_SAMPLE_COUNT])

	var side_chains := [LEG_CHAIN_LEFT, LEG_CHAIN_RIGHT]
	var identity_d: Array = [1.0, 0.0, 0.0, 0.0]

	# ── Step 2.5 (fix/298): centre the base stance's fore/aft stagger ───────
	# See the file header for WHY: dribbleidle's static stance holds the two
	# toes ~0.70 m apart fore/aft, which forces the amplitude sweep into a
	# region where alternation (PROOF 1) and grounding (PROOF 6) cannot both
	# pass. Solve one static rotation per leg, C_leg, that recentres that
	# leg's own time-averaged toe projection (measured on the pristine SOURCE
	# dribble clip, untouched) onto the midpoint between the two legs'
	# averages, BEFORE the delta transplant runs. Solved by bisection against
	# the same FK machinery the rest of this file trusts -- see
	# _solve_stance_correction -- and verified numerically rather than assumed
	# to have converged.
	var upleg_left_idx := raw_skel.find_bone(LEG_CHAIN_LEFT[0])
	var upleg_right_idx := raw_skel.find_bone(LEG_CHAIN_RIGHT[0])
	if upleg_left_idx < 0 or upleg_right_idx < 0:
		push_error("[rebuild-dribble] #298: could not find %s/%s on the raw Y Bot skeleton."
			% [LEG_CHAIN_LEFT[0], LEG_CHAIN_RIGHT[0]])
		return {"ok": false}

	var c_leg_left_d: Array = identity_d
	var c_leg_right_d: Array = identity_d
	var theta_left_deg := 0.0
	var theta_right_deg := 0.0

	if CENTRE_STANCE_STAGGER:
		var avg_l := _average_toe_projection(src, raw_skel, rot_cache_src, pos_cache_src, hips_idx, left_toe_idx, forward)
		var avg_r := _average_toe_projection(src, raw_skel, rot_cache_src, pos_cache_src, hips_idx, right_toe_idx, forward)
		var avg_mid := (avg_l + avg_r) * 0.5
		print(("[rebuild-dribble] #298 centre-stance-stagger: source-dribble time-averaged toe projection " +
			"avgL=%.4f m, avgR=%.4f m, avgMid=%.4f m (%d phases)") % [avg_l, avg_r, avg_mid, STRIDE_PHASE_COUNT])

		var left_solve := _solve_stance_correction(src, raw_skel, rot_cache_src, pos_cache_src,
			hips_idx, left_toe_idx, upleg_left_idx, right_axis, avg_mid, forward)
		var right_solve := _solve_stance_correction(src, raw_skel, rot_cache_src, pos_cache_src,
			hips_idx, right_toe_idx, upleg_right_idx, right_axis, avg_mid, forward)

		theta_left_deg = left_solve["theta_deg"]
		theta_right_deg = right_solve["theta_deg"]
		var residual_left: float = left_solve["residual"]
		var residual_right: float = right_solve["residual"]

		print(("[rebuild-dribble] #298 centre-stance-stagger LEFT:  theta=%+.4f deg, projection %.4f m -> %.4f m " +
			"(target %.4f m), residual=%.6f m") % [theta_left_deg, avg_l, left_solve["after"], avg_mid, residual_left])
		print(("[rebuild-dribble] #298 centre-stance-stagger RIGHT: theta=%+.4f deg, projection %.4f m -> %.4f m " +
			"(target %.4f m), residual=%.6f m") % [theta_right_deg, avg_r, right_solve["after"], avg_mid, residual_right])

		if residual_left > STANCE_CENTRE_RESIDUAL_TOLERANCE_M or residual_right > STANCE_CENTRE_RESIDUAL_TOLERANCE_M:
			push_error(("[rebuild-dribble] #298 centre-stance-stagger FAILED TO CONVERGE: left residual=%.6f m, " +
				"right residual=%.6f m (must both be <= %.4f m) -- a silent non-convergence here would leave the " +
				"stagger in place and quietly reproduce the exact squeeze this change exists to remove.")
				% [residual_left, residual_right, STANCE_CENTRE_RESIDUAL_TOLERANCE_M])
			return {"ok": false}

		c_leg_left_d = _dq_from(left_solve["c_leg_quat"] as Quaternion)
		c_leg_right_d = _dq_from(right_solve["c_leg_quat"] as Quaternion)
	else:
		print("[rebuild-dribble] #298 centre-stance-stagger DISABLED (CENTRE_STANCE_STAGGER=false) -- old uncentred behaviour.")

	# ── Steps 3-4: per-bone world-frame delta, solved root to tip ──────────
	# Done entirely in the double-precision DQuat representation (see the
	# helpers above) -- Godot's built-in Quaternion type's float32 internals
	# cannot carry the chained products here to within the self-check's 1e-4
	# deg tolerance (confirmed empirically, not assumed; see that comment).
	var new_locals := {}   # bone_name -> Array[Quaternion], length RESAMPLE_KEY_COUNT
	var worst_residual_deg := 0.0

	for chain_idx in side_chains.size():
		var chain: Array = side_chains[chain_idx]
		# C_leg is a STATIC per-leg property (solved once, above) -- constant
		# across every key/phase of this chain, not re-solved per sample.
		var c_leg_d: Array = c_leg_left_d if chain_idx == 0 else c_leg_right_d
		for k in RESAMPLE_KEY_COUNT:
			var t: float = float(k) * RESAMPLE_DT
			var u: float = fmod(t * float(n) / dribble_len, 1.0)
			var t_run: float = u * run_len

			# G_new(Hips,t) = L_dribble(Hips,t) -- untouched, never overwritten.
			var g_new_parent: Array = _local_rotation_d(raw_skel, rot_cache_src, src, hips_idx, t)

			for bone_name in chain:
				var b := raw_skel.find_bone(bone_name)
				var g_run_t := _global_rotation_d(raw_skel, rot_cache_run, run_anim, b, t_run)
				var g_run_t0 := _global_rotation_d(raw_skel, rot_cache_run, run_anim, b, best_t0)
				var d_raw: Array = _dq_norm(_dq_mul(g_run_t, _dq_inv(g_run_t0)))
				# Step 6 amplitude control (identity at STRIDE_AMPLITUDE=1.0).
				var d_scaled: Array = _dq_slerp(identity_d, d_raw, STRIDE_AMPLITUDE)

				var g_dribble_b := _global_rotation_d(raw_skel, rot_cache_src, src, b, t)
				# fix/298: fold the static per-leg centring correction in BEFORE
				# the delta -- G_new(b,t) = D(b,t) * C_leg * G_dribble(b,t).
				# C_leg is identity when CENTRE_STANCE_STAGGER is false, so this
				# reduces exactly to the pre-fix composition in that case.
				var g_dribble_b_corrected: Array = _dq_norm(_dq_mul(c_leg_d, g_dribble_b))
				var l_new: Array = _dq_norm(_dq_mul(_dq_mul(_dq_inv(g_new_parent), d_scaled), g_dribble_b_corrected))
				var g_new_b: Array = _dq_norm(_dq_mul(g_new_parent, l_new))

				# Step 4 self-check: G_new(b,t) must equal D(b,t)*C_leg*G_dribble(b,t).
				var expected: Array = _dq_norm(_dq_mul(d_scaled, g_dribble_b_corrected))
				var residual: float = _dq_angle_deg(g_new_b, expected)
				worst_residual_deg = maxf(worst_residual_deg, residual)

				if not new_locals.has(bone_name):
					new_locals[bone_name] = []
				new_locals[bone_name].append(_dq_to_quat(l_new))

				g_new_parent = g_new_b

	print(("[rebuild-dribble] #298 step-4 self-check: worst |G_new(b,t) - D(b,t)*C_leg*G_dribble(b,t)| = %.8f deg " +
		"(must be < %.4f deg)") % [worst_residual_deg, SELF_CHECK_TOLERANCE_DEG])
	if worst_residual_deg >= SELF_CHECK_TOLERANCE_DEG:
		push_error("[rebuild-dribble] #298 step-4 self-check FAILED: worst residual %.8f deg >= %.4f deg -- algebra bug."
			% [worst_residual_deg, SELF_CHECK_TOLERANCE_DEG])
		return {"ok": false}

	# ── Step 5: write the tracks (REPLACE keys; never add/remove tracks) ───
	for bone_name in new_locals.keys():
		var track_idx := -1
		for i in move_clip.get_track_count():
			if move_clip.track_get_type(i) == Animation.TYPE_ROTATION_3D and bone_of(move_clip.track_get_path(i)) == bone_name:
				track_idx = i
				break
		if track_idx < 0:
			push_error("[rebuild-dribble] #298: no existing rotation track for '%s' in dribblemove -- refusing to add one."
				% bone_name)
			return {"ok": false}

		while move_clip.track_get_key_count(track_idx) > 0:
			move_clip.track_remove_key(track_idx, 0)

		var values: Array = new_locals[bone_name]
		for k in RESAMPLE_KEY_COUNT:
			move_clip.track_insert_key(track_idx, float(k) * RESAMPLE_DT, values[k])

	print("[rebuild-dribble] #298: wrote %d keys x %d leg-chain tracks onto 'dribblemove': %s"
		% [RESAMPLE_KEY_COUNT, new_locals.size(), str(new_locals.keys())])

	return {"ok": true, "c_leg_left_d": c_leg_left_d, "c_leg_right_d": c_leg_right_d}

# fix/298 (round 2): applies the SAME static per-leg stance-centring
# correction solved above (`C_leg`, passed in as double-precision DQuat
# arrays) to `idle_clip` -- see the file header's "the SAME centring must
# also apply to dribbleidle" section for why leaving dribbleidle uncorrected
# reintroduced the #287 corridor failure. This is the IDENTICAL root-to-tip
# composition `_apply_leg_stride` already performs, with the animated delta
# `D` fixed at IDENTITY (there is no swing to transplant onto the idle
# endpoint, only the static correction):
#   G_new(b,t) = IDENTITY * C_leg * G_src(b,t) = C_leg * G_src(b,t)
# `mixamorig_Hips` is read (untouched) as the chain's root exactly as in
# `_apply_leg_stride`; it is never solved for or overwritten. Never adds or
# removes a track -- if a leg-chain rotation track is missing from either
# clip, this fails loud (mirrors `_apply_leg_stride`'s own guard) rather than
# silently skipping a bone.
func _apply_static_stance_correction(idle_clip: Animation, src: Animation, raw_skel: Skeleton3D,
		c_left_d: Array, c_right_d: Array) -> bool:
	var rot_cache_src := _rotation_track_cache(raw_skel, src)
	var hips_idx := raw_skel.find_bone(CROUCH_BONE)
	var side_chains := [LEG_CHAIN_LEFT, LEG_CHAIN_RIGHT]
	var identity_d: Array = [1.0, 0.0, 0.0, 0.0]

	var new_locals := {}   # bone_name -> Array[Quaternion], length RESAMPLE_KEY_COUNT
	var worst_residual_deg := 0.0

	for chain_idx in side_chains.size():
		var chain: Array = side_chains[chain_idx]
		var c_leg_d: Array = c_left_d if chain_idx == 0 else c_right_d
		for k in RESAMPLE_KEY_COUNT:
			var t: float = float(k) * RESAMPLE_DT

			# G_new(Hips,t) = L_src(Hips,t) -- untouched, never overwritten,
			# exactly as _apply_leg_stride treats it.
			var g_new_parent: Array = _local_rotation_d(raw_skel, rot_cache_src, src, hips_idx, t)

			for bone_name in chain:
				var b := raw_skel.find_bone(bone_name)
				var g_src_b := _global_rotation_d(raw_skel, rot_cache_src, src, b, t)
				var g_src_b_corrected: Array = _dq_norm(_dq_mul(c_leg_d, g_src_b))
				# D is identity here -- the only difference from
				# _apply_leg_stride's composition is that d_scaled is fixed
				# at identity_d instead of the transplanted run delta.
				var l_new: Array = _dq_norm(_dq_mul(_dq_mul(_dq_inv(g_new_parent), identity_d), g_src_b_corrected))
				var g_new_b: Array = _dq_norm(_dq_mul(g_new_parent, l_new))

				# Same self-check shape as _apply_leg_stride's step 4:
				# G_new(b,t) must equal D(b,t)*C_leg*G_src(b,t), D=identity.
				var expected: Array = _dq_norm(_dq_mul(identity_d, g_src_b_corrected))
				var residual: float = _dq_angle_deg(g_new_b, expected)
				worst_residual_deg = maxf(worst_residual_deg, residual)

				if not new_locals.has(bone_name):
					new_locals[bone_name] = []
				new_locals[bone_name].append(_dq_to_quat(l_new))

				g_new_parent = g_new_b

	print(("[rebuild-dribble] fix/298 idle-centring self-check: worst |G_new(b,t) - C_leg*G_src(b,t)| = " +
		"%.8f deg (must be < %.4f deg)") % [worst_residual_deg, SELF_CHECK_TOLERANCE_DEG])
	if worst_residual_deg >= SELF_CHECK_TOLERANCE_DEG:
		push_error("[rebuild-dribble] fix/298 idle-centring self-check FAILED: worst residual %.8f deg >= %.4f deg -- algebra bug."
			% [worst_residual_deg, SELF_CHECK_TOLERANCE_DEG])
		return false

	for bone_name in new_locals.keys():
		var track_idx := -1
		for i in idle_clip.get_track_count():
			if idle_clip.track_get_type(i) == Animation.TYPE_ROTATION_3D and bone_of(idle_clip.track_get_path(i)) == bone_name:
				track_idx = i
				break
		if track_idx < 0:
			push_error("[rebuild-dribble] fix/298: no existing rotation track for '%s' in dribbleidle -- refusing to add one."
				% bone_name)
			return false

		while idle_clip.track_get_key_count(track_idx) > 0:
			idle_clip.track_remove_key(track_idx, 0)

		var values: Array = new_locals[bone_name]
		for k in RESAMPLE_KEY_COUNT:
			idle_clip.track_insert_key(track_idx, float(k) * RESAMPLE_DT, values[k])

	print("[rebuild-dribble] fix/298: wrote %d keys x %d leg-chain tracks onto 'dribbleidle' (static stance centring): %s"
		% [RESAMPLE_KEY_COUNT, new_locals.size(), str(new_locals.keys())])

	return true

# PROOF 1 (the #298 acceptance criterion): FK the FINISHED dribblemove clip at
# STRIDE_PHASE_COUNT phases and require each foot's hips-relative
# forward-projected peak-to-peak to clear the floor, AND require the lead foot
# to actually alternate (fore/aft split changes sign) -- a static
# one-foot-forward stance can clear a peak-to-peak floor without being a
# stride at all.
func _proof_stride(move_clip: Animation, raw_skel: Skeleton3D, forward: Vector3) -> bool:
	var rot_cache := _rotation_track_cache(raw_skel, move_clip)
	var pos_cache := _position_track_cache(raw_skel, move_clip)
	var hips_idx := raw_skel.find_bone(CROUCH_BONE)
	var left_idx := raw_skel.find_bone("mixamorig_LeftToeBase")
	var right_idx := raw_skel.find_bone("mixamorig_RightToeBase")

	var left_min := INF
	var left_max := -INF
	var right_min := INF
	var right_max := -INF
	var split_min := INF
	var split_max := -INF

	for i in STRIDE_PHASE_COUNT:
		var t: float = float(i) * move_clip.length / float(STRIDE_PHASE_COUNT)
		var hips_pos: Vector3 = _global_transform(raw_skel, rot_cache, pos_cache, move_clip, hips_idx, t).origin
		var lp: float = (_global_transform(raw_skel, rot_cache, pos_cache, move_clip, left_idx, t).origin - hips_pos).dot(forward)
		var rp: float = (_global_transform(raw_skel, rot_cache, pos_cache, move_clip, right_idx, t).origin - hips_pos).dot(forward)
		left_min = minf(left_min, lp)
		left_max = maxf(left_max, lp)
		right_min = minf(right_min, rp)
		right_max = maxf(right_max, rp)
		var diff := lp - rp
		split_min = minf(split_min, diff)
		split_max = maxf(split_max, diff)

	var left_ptp := left_max - left_min
	var right_ptp := right_max - right_min
	print(("[rebuild-dribble] #298 PROOF 1: LeftToeBase ptp=%.4f m, RightToeBase ptp=%.4f m, " +
		"fore/aft split=[%.4f, %.4f] m (floor=%.2f m)")
		% [left_ptp, right_ptp, split_min, split_max, STRIDE_MIN_PEAK_TO_PEAK_M])

	var ok := true
	if left_ptp < STRIDE_MIN_PEAK_TO_PEAK_M:
		push_error("[rebuild-dribble] #298 PROOF 1 FAILED: LeftToeBase peak-to-peak %.4f m < floor %.2f m."
			% [left_ptp, STRIDE_MIN_PEAK_TO_PEAK_M])
		ok = false
	if right_ptp < STRIDE_MIN_PEAK_TO_PEAK_M:
		push_error("[rebuild-dribble] #298 PROOF 1 FAILED: RightToeBase peak-to-peak %.4f m < floor %.2f m."
			% [right_ptp, STRIDE_MIN_PEAK_TO_PEAK_M])
		ok = false
	if not (split_min < 0.0 and split_max > 0.0):
		push_error(("[rebuild-dribble] #298 PROOF 1 FAILED: fore/aft split [%.4f, %.4f] never changes sign -- " +
			"the lead foot does not alternate (a static one-foot-forward stance).") % [split_min, split_max])
		ok = false
	return ok

# PROOF 2 (anatomy guard): at every sampled phase the head must stay above the
# hips and both feet must stay below them. This is the guard against the
# failure mode that killed the earlier rejected runtime-blend approach, where
# the head ended up 0.42 m BELOW the hips.
func _proof_anatomy(move_clip: Animation, raw_skel: Skeleton3D) -> bool:
	var rot_cache := _rotation_track_cache(raw_skel, move_clip)
	var pos_cache := _position_track_cache(raw_skel, move_clip)
	var hips_idx := raw_skel.find_bone(CROUCH_BONE)
	var head_idx := raw_skel.find_bone("mixamorig_Head")
	var left_foot_idx := raw_skel.find_bone("mixamorig_LeftFoot")
	var right_foot_idx := raw_skel.find_bone("mixamorig_RightFoot")

	var worst_head := INF
	var worst_left_foot := -INF
	var worst_right_foot := -INF

	for i in STRIDE_PHASE_COUNT:
		var t: float = float(i) * move_clip.length / float(STRIDE_PHASE_COUNT)
		var hips_y: float = _global_transform(raw_skel, rot_cache, pos_cache, move_clip, hips_idx, t).origin.y
		var head_y: float = _global_transform(raw_skel, rot_cache, pos_cache, move_clip, head_idx, t).origin.y - hips_y
		var lf_y: float = _global_transform(raw_skel, rot_cache, pos_cache, move_clip, left_foot_idx, t).origin.y - hips_y
		var rf_y: float = _global_transform(raw_skel, rot_cache, pos_cache, move_clip, right_foot_idx, t).origin.y - hips_y
		worst_head = minf(worst_head, head_y)
		worst_left_foot = maxf(worst_left_foot, lf_y)
		worst_right_foot = maxf(worst_right_foot, rf_y)

	print(("[rebuild-dribble] #298 PROOF 2: worst head height=%.4f m (must be > 0), worst foot height " +
		"L=%.4f m R=%.4f m (must both be < 0)") % [worst_head, worst_left_foot, worst_right_foot])

	var ok := true
	if worst_head <= 0.0:
		push_error("[rebuild-dribble] #298 PROOF 2 FAILED: head sank to %.4f m relative to Hips (<= 0)." % worst_head)
		ok = false
	if worst_left_foot >= 0.0:
		push_error("[rebuild-dribble] #298 PROOF 2 FAILED: LeftFoot rose to %.4f m relative to Hips (>= 0)." % worst_left_foot)
		ok = false
	if worst_right_foot >= 0.0:
		push_error("[rebuild-dribble] #298 PROOF 2 FAILED: RightFoot rose to %.4f m relative to Hips (>= 0)." % worst_right_foot)
		ok = false
	return ok

# PROOF 3 (loop seam), in two parts, because the obvious formulation of this
# proof is VACUOUS and it is worth saying so out loud:
#
#   3a compares the pose at t=0 with the pose at t=length. On a LOOP_LINEAR clip
#      Godot's rotation_track_interpolate WRAPS, so sampling at `length` returns
#      key 0's own value and this residual is zero BY DEFINITION, no matter what
#      the authored data says. (An earlier version of this comment claimed the
#      opposite -- that the 63-key/(1/30)s grid made it "a genuine check on the
#      authored data". It does not. It measured exactly 0.000000 deg, which was
#      the sampling call agreeing with itself.) 3a is retained only because it
#      DOES catch a regression to LOOP_NONE, where sampling at `length` returns
#      the last key instead and the residual goes non-zero.
#
#   3b is the check with teeth. A visible pop at the wrap is a discontinuity in
#      angular VELOCITY, which pose continuity cannot see. So it compares the
#      angular step ACROSS the seam (last authored key -> key 0) against the
#      worst step taken anywhere INSIDE the cycle. Both span one RESAMPLE_DT, so
#      they are directly comparable. A non-integer number of run gait cycles per
#      dribble loop -- the realistic way this bake breaks -- would make the seam
#      step tower over every in-cycle step.
func _proof_loop_seam(move_clip: Animation, leg_bone_names: Array) -> bool:
	var worst := 0.0
	var worst_seam_step := 0.0
	var worst_cycle_step := 0.0
	var worst_ratio := 0.0
	var worst_ratio_bone := ""

	for bone_name in leg_bone_names:
		var track_idx := -1
		for i in move_clip.get_track_count():
			if move_clip.track_get_type(i) == Animation.TYPE_ROTATION_3D and bone_of(move_clip.track_get_path(i)) == bone_name:
				track_idx = i
				break
		if track_idx < 0:
			continue

		# ── PROOF 3a: loop_mode guard (definitionally zero while LOOPED) ──────
		# On a LOOP_LINEAR clip Godot's own interpolation WRAPS, so sampling at
		# `length` returns key 0's value and this residual is zero by
		# definition -- it is NOT evidence the motion is continuous. Its only
		# real power is catching a regression to LOOP_NONE (where sampling at
		# `length` would instead return the LAST key, 1/30 s earlier in the
		# cycle, and this would go non-zero). Kept for exactly that, and
		# labelled so nobody mistakes it for a continuity proof.
		var q0: Quaternion = move_clip.rotation_track_interpolate(track_idx, 0.0)
		var q_end: Quaternion = move_clip.rotation_track_interpolate(track_idx, move_clip.length)
		worst = maxf(worst, _quat_angle_deg(q0, q_end))

		# ── PROOF 3b: seam VELOCITY continuity (the check that has teeth) ─────
		# A visible pop at the wrap is a discontinuity in angular VELOCITY, not
		# in pose -- and pose continuity above cannot see it. So compare the
		# angular step taken ACROSS the seam (last authored key -> key 0)
		# against the worst step taken anywhere INSIDE the cycle. Both are one
		# RESAMPLE_DT apart, so they are directly comparable: if the resample
		# put a non-integer number of run gait cycles into the loop, the seam
		# step would jump far above every in-cycle step. A correct integer
		# cycle count makes the seam step just another ordinary step.
		var key_count := move_clip.track_get_key_count(track_idx)
		if key_count < 3:
			continue
		var q_last: Quaternion = move_clip.track_get_key_value(track_idx, key_count - 1)
		var seam_step := _quat_angle_deg(q_last, q0)
		var max_cycle_step := 0.0
		for k in key_count - 1:
			var qa: Quaternion = move_clip.track_get_key_value(track_idx, k)
			var qb: Quaternion = move_clip.track_get_key_value(track_idx, k + 1)
			max_cycle_step = maxf(max_cycle_step, _quat_angle_deg(qa, qb))
		if max_cycle_step > 1e-6:
			var ratio := seam_step / max_cycle_step
			if ratio > worst_ratio:
				worst_ratio = ratio
				worst_ratio_bone = bone_name
				worst_seam_step = seam_step
				worst_cycle_step = max_cycle_step

	# PROOF 3a's own non-vacuity check. `worst_seam_step` IS the residual this
	# proof would report under LOOP_NONE (where sampling at `length` returns the
	# last key instead of wrapping to key 0), so it is exactly the magnitude of
	# the regression 3a exists to catch. If it does not clear the tolerance by a
	# healthy margin, the gate has no power and must say so rather than pass
	# quietly -- otherwise raising the tolerance off the float32 noise floor
	# could silently have disarmed it.
	var guard_floor: float = LOOP_SEAM_TOLERANCE_DEG * LOOP_SEAM_GUARD_MARGIN
	print(("[rebuild-dribble] #298 PROOF 3a: worst pose residual at the wrap across leg tracks = %.6f deg " +
		"(unit: geodesic degrees; must be < %.4f). Gate power check: the LOOP_NONE would-be residual is " +
		"%.4f deg, which must exceed %.4f deg (= %.2fx the tolerance) for this gate to be able to catch a " +
		"loop_mode regression at all.")
		% [worst, LOOP_SEAM_TOLERANCE_DEG, worst_seam_step, guard_floor, LOOP_SEAM_GUARD_MARGIN])
	if worst_seam_step <= guard_floor:
		push_error(("[rebuild-dribble] #298 PROOF 3a VACUOUS: the LOOP_NONE would-be residual is only %.4f deg, " +
			"not clearing %.4f deg -- this gate can no longer distinguish a LOOP_LINEAR clip from a LOOP_NONE " +
			"one, so passing it would prove nothing. Either the stride shrank to nothing (check PROOF 1) or " +
			"LOOP_SEAM_TOLERANCE_DEG has been raised too far.") % [worst_seam_step, guard_floor])
		return false
	if worst >= LOOP_SEAM_TOLERANCE_DEG:
		push_error(("[rebuild-dribble] #298 PROOF 3a FAILED: worst wrap pose residual %.6f deg >= %.4f deg -- " +
			"the clip is most likely no longer LOOP_LINEAR.") % [worst, LOOP_SEAM_TOLERANCE_DEG])
		return false

	print(("[rebuild-dribble] #298 PROOF 3b: worst seam/in-cycle angular-step ratio = %.4f on '%s' " +
		"(seam step %.4f deg vs worst in-cycle step %.4f deg, both over one %.4f s grid interval; " +
		"must be <= %.2f)") % [worst_ratio, worst_ratio_bone, worst_seam_step, worst_cycle_step,
		RESAMPLE_DT, LOOP_SEAM_STEP_RATIO_MAX])
	if worst_ratio > LOOP_SEAM_STEP_RATIO_MAX:
		push_error(("[rebuild-dribble] #298 PROOF 3b FAILED: the step across the loop seam is %.4fx the " +
			"worst step inside the cycle on '%s' (%.4f deg vs %.4f deg) -- the loop will visibly POP. " +
			"The usual cause is a non-integer number of run gait cycles per dribble loop; check PROOF 0's " +
			"bounce count N and the t -> t_run resample.")
			% [worst_ratio, worst_ratio_bone, worst_seam_step, worst_cycle_step])
		return false
	return true

# PROOF 4 (fix/298 round 2 AMENDS the #285a "idle untouched" contract, LEG
# CHAIN ONLY -- torso/arms/Hips are still exactly the source FBX clip,
# untouched). See the file header's "the SAME centring must also apply to
# dribbleidle" section for why: leaving dribbleidle uncorrected while
# dribblemove got the static C_leg stance correction reintroduced the #287
# corridor failure, because the two endpoints then differed by more than the
# swing the blend parameter controls.
#
# The new contract: every leg-bone rotation track in the saved dribbleidle
# clip must equal `C_leg * G_src(b,t)` -- the SAME per-leg static correction
# `_apply_static_stance_correction` applied -- at every authored key time, to
# within IDLE_CENTRING_TOLERANCE_DEG. This re-derives the global rotation from
# the ACTUAL SAVED clip's tracks via the same chain-FK helpers used
# throughout this file, so unlike the construction-time self-check inside
# `_apply_static_stance_correction` it also catches a Step-5 write/read bug.
# It still guards what PROOF 4 always guarded: the stride (or any other
# motion) leaking into the idle endpoint would show up here as a deviation
# far above the tolerance.
func _proof_idle_stance_centred(idle_clip: Animation, src: Animation, raw_skel: Skeleton3D,
		leg_bone_names: Array, c_leg_left_d: Array, c_leg_right_d: Array) -> bool:
	var rot_cache_idle := _rotation_track_cache(raw_skel, idle_clip)
	var rot_cache_src := _rotation_track_cache(raw_skel, src)

	var worst := 0.0
	var ok := true
	for bone_name in leg_bone_names:
		var c_leg_d: Array = c_leg_left_d if bone_name in LEG_CHAIN_LEFT else c_leg_right_d
		var b := raw_skel.find_bone(bone_name)
		if b < 0 or not rot_cache_idle.has(b) or not rot_cache_src.has(b):
			push_error("[rebuild-dribble] #298 PROOF 4 FAILED: '%s' missing a rotation track on the saved dribbleidle clip or the source clip."
				% bone_name)
			ok = false
			continue

		var idle_track: int = rot_cache_idle[b]
		var key_count := idle_clip.track_get_key_count(idle_track)
		for k in key_count:
			var t: float = idle_clip.track_get_key_time(idle_track, k)
			var g_idle := _global_rotation_d(raw_skel, rot_cache_idle, idle_clip, b, t)
			var g_src := _global_rotation_d(raw_skel, rot_cache_src, src, b, t)
			var expected: Array = _dq_norm(_dq_mul(c_leg_d, g_src))
			worst = maxf(worst, _dq_angle_deg(g_idle, expected))

	print(("[rebuild-dribble] #298 PROOF 4: worst dribbleidle-vs-(C_leg*source) deviation across leg tracks = " +
		"%.6f deg (must be < %.4f deg)") % [worst, IDLE_CENTRING_TOLERANCE_DEG])
	if worst >= IDLE_CENTRING_TOLERANCE_DEG:
		push_error(("[rebuild-dribble] #298 PROOF 4 FAILED: dribbleidle deviates from C_leg*source by %.6f deg " +
			"(must be < %.4f deg) -- fix/298's amended contract requires the SAME static stance correction on both " +
			"endpoints; this either means the stride leaked into the idle endpoint or the two centrings drifted apart.")
			% [worst, IDLE_CENTRING_TOLERANCE_DEG])
		ok = false
	return ok

# Shared by PROOF 6's dribble and run measurements so both sides walk the
# IDENTICAL FK code path and phase count -- the only thing that makes gating
# the dribble band against a LIVE-measured run band (rather than a hardcoded
# number) meaningful.
#
#   support(t) = min(LeftFoot.y, RightFoot.y) - Hips.y   -- the #298 definition.
#   swing(t)   = max(LeftFoot.y, RightFoot.y) - Hips.y   -- the OTHER (lifted)
#                foot at the same instant; reported as ungated supporting
#                evidence, not gated, because a driving dribble is allowed a
#                normal swing-leg lift -- it is BOTH feet leaving the floor
#                together (a low `support`) that is the actual defect.
func _measure_support_and_swing(anim: Animation, raw_skel: Skeleton3D) -> Dictionary:
	var rot_cache := _rotation_track_cache(raw_skel, anim)
	var pos_cache := _position_track_cache(raw_skel, anim)
	var hips_idx := raw_skel.find_bone(CROUCH_BONE)
	var left_idx := raw_skel.find_bone("mixamorig_LeftFoot")
	var right_idx := raw_skel.find_bone("mixamorig_RightFoot")

	var support_min := INF
	var support_max := -INF
	var swing_min := INF
	var swing_max := -INF

	for i in STRIDE_PHASE_COUNT:
		var t: float = float(i) * anim.length / float(STRIDE_PHASE_COUNT)
		var hips_y: float = _global_transform(raw_skel, rot_cache, pos_cache, anim, hips_idx, t).origin.y
		var lf_y: float = _global_transform(raw_skel, rot_cache, pos_cache, anim, left_idx, t).origin.y - hips_y
		var rf_y: float = _global_transform(raw_skel, rot_cache, pos_cache, anim, right_idx, t).origin.y - hips_y
		var support: float = minf(lf_y, rf_y)
		var swing: float = maxf(lf_y, rf_y)
		support_min = minf(support_min, support)
		support_max = maxf(support_max, support)
		swing_min = minf(swing_min, swing)
		swing_max = maxf(swing_max, swing)

	return {
		"band": support_max - support_min,
		"support_min": support_min,
		"support_max": support_max,
		"swing_travel": swing_max - swing_min,
	}

# PROOF 6 (vertical plausibility / grounding, the #298 amplitude-defect proof):
# gates the FINISHED move_clip's support-band against the shipped `run` clip's
# OWN support-band -- measured LIVE inside this same run, via the identical FK
# helpers and identical STRIDE_PHASE_COUNT, rather than a hardcoded number.
# Hardcoding would rot silently the moment `run` changes; measuring live makes
# this proof self-maintaining. `run` is the control (a sprint's flight phase is
# legitimate); a driving dribble must be no bouncier than that, plus
# SUPPORT_BAND_TOLERANCE slack for the crouched stance's different leg
# geometry -- not a licence to hop.
func _proof_support_band(move_clip: Animation, run_anim: Animation, raw_skel: Skeleton3D) -> bool:
	var dribble := _measure_support_and_swing(move_clip, raw_skel)
	var run := _measure_support_and_swing(run_anim, raw_skel)

	var dribble_band: float = dribble["band"]
	var run_band: float = run["band"]
	var ratio: float = (dribble_band / run_band) if run_band > 0.0 else INF
	var gate: float = run_band * SUPPORT_BAND_TOLERANCE

	print(("[rebuild-dribble] #298 PROOF 6: dribble support band=%.4f m (min=%.4f, max=%.4f), " +
		"run support band=%.4f m (min=%.4f, max=%.4f), ratio=%.4fx (gate: dribble <= %.2fx run = %.4f m)")
		% [dribble_band, dribble["support_min"], dribble["support_max"],
			run_band, run["support_min"], run["support_max"], ratio, SUPPORT_BAND_TOLERANCE, gate])
	print(("[rebuild-dribble] #298 PROOF 6 (supporting evidence, ungated): swing-foot travel -- " +
		"dribble=%.4f m, run=%.4f m") % [dribble["swing_travel"], run["swing_travel"]])

	if dribble_band > gate:
		push_error(("[rebuild-dribble] #298 PROOF 6 FAILED: dribble support band %.4f m exceeds %.2fx the " +
			"run clip's own band (%.4f m, gate=%.4f m) -- the baked stride lifts both feet off the floor at " +
			"once like a sprint's flight phase, not a grounded drive. Lower STRIDE_AMPLITUDE.")
			% [dribble_band, SUPPORT_BAND_TOLERANCE, run_band, gate])
		return false
	return true
