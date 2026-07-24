# Spike 0012 — headless FBX retarget (BoneMap + SkeletonProfile) proves out ADR-0011's import-dialog lift

- **Supports:** [ADR-0011](../adr/0011-claude-authors-scenes.md) (import-dialog exclusion, narrowed 2026-07-22)
- **Issue / PR:** #267 / #270
- **Verdict:** PASS — the FBX retarget subset is driveable by a plain
  `godot --headless --import` pass; no editor GUI session is required.

---

## What was in question

ADR-0011 lifted `.tscn`/`.res`/`project.godot` authoring to AFK but left
"editor **import-dialog** settings not already scriptable headlessly" as a
standing HITL exclusion. The #170 rig swap (Kenney `characterMedium` → Mixamo
**Y Bot**) left every locomotion clip in `assets/locomotion.res` bound to the
old rig's deform-bone names (`Hips`, `Chest`, `LeftUpLeg`, …), which resolve
**0 tracks** against Y Bot's `mixamorig_`-prefixed skeleton — the literal
T-pose bug. Fixing it needs Godot's FBX **retarget** (BoneMap + SkeletonProfile
+ rest fixer), historically an import-dialog operation. Could that be done
headlessly, or did it force a human editor session?

## The proven route (fully headless, Godot 4.7.1)

1. **A hand-written `retarget/bone_map` entry in a `*.fbx.import` `_subresources`
   block IS honored** by a plain `godot --headless --import`. Godot rewrites the
   `ExtResource` guess into canonical `Resource("res://…")` form on import,
   proving it was consumed.
2. **Retarget renames the source bones to whatever the `SkeletonProfile` names
   its own bones.** So author a **custom `SkeletonProfile` whose canonical bone
   names ARE the literal `mixamorig_*` names**, built programmatically from Y
   Bot's real rest pose (`set_bone_name` / `set_bone_parent` /
   `set_reference_pose`, then `ResourceSaver.save()` →
   `assets/retarget/MixamoProfile.tres`). Apply the retarget **only to the source
   clips** (`idle.fbx` / `run.fbx`) via their own `.fbx.import` — **never to
   `Y Bot.fbx`.** Their Kenney tracks rename straight to `mixamorig_*`, binding
   to the untouched Y Bot rig.
3. A per-clip `BoneMap` (`assets/retarget/idle_bonemap.tres` /
   `run_bonemap.tres`) maps the profile's `mixamorig_*` names onto each source
   FBX's own Kenney names. Flat `.tres` format:
   `bone_map/<ProfileBoneName> = &"<SourceBoneName>"`.

## Gotchas (the authoring checklist)

- **Retarget the SOURCE clips only, never `Y Bot.fbx`.** `RigScale` /
  `PlayerRigScaler` classify height/wingspan bones by the `mixamorig_` prefix
  ([[project_mixamo_import_bone_name_prefix]] in agent memory) — retargeting the
  shipped skeleton would rename those bones and silently no-op the rig scaler.
  `Y Bot.fbx.import` must keep `_subresources={}`.
- **The retargeted skeleton NODE is renamed to `GeneralSkeleton`** (not just the
  bones), so an extracted clip's track node-prefix (`Root/Skeleton3D:…`) won't
  match Y Bot's own `Skeleton3D` node — reconcile the node path when rebuilding
  `locomotion.res`.
- **Kenney→mixamorig renames beyond a plain prefix add:** `Chest→Spine1`,
  `UpperChest→Spine2`, `Left/RightToes→ToeBase`. IK/Ctrl/Roll and `*_end` bones
  are inert (static tracks) — leave them UNMAPPED (this narrows the track count,
  e.g. idle 40→31, expected).
- **`pivot` has no source `.fbx`** (hand-authored, 4 bones) — it gets a
  name-only remap (`Hips→mixamorig_Hips`, …) with **no rest-fixer pass**, so at
  the time of this spike its *pose orientation* was unverified beyond track
  *binding*. **RESOLVED in #273** (see the addendum below): the name-only
  remap left the keys expressed against the KENNEY rig's rest orientations, not
  Y Bot's — a separate rest-delta correction pass (analogous in spirit to the
  rest fixer below, but hand-derived since pivot has no source FBX for the
  importer to run against) fixed it. Track *binding* and rest-anchored *pose*
  are both now state-checkable and proven; only visual pose *quality* stays a
  human feel caveat (#178/#173).
- **`run`'s "Run" clip has no `mixamorig_Hips` track** at all (before and
  after) — any vertical hip bob was already absent. Still an open pose caveat
  for the human verify #178.
- **Route B (programmatic track-path/bone-name remap on the orphaned `.res`)
  was rejected:** the two rigs' rest quaternions differ arbitrarily, so a bare
  rename binds to the right bone at the wrong orientation. Godot's rest-fixer,
  which the import pipeline runs for free, supplies the per-bone rest-delta math.

## Proof

`tests/integration/LocomotionClipTest.cs` (wired into CI's `integration-test`
job) instantiates the live `scenes/Player.tscn`, finds the real `Skeleton3D`,
and asserts every bone track in idle/run/pivot resolves — with vacuous-pass
guards (library must carry the three clips; each clip must have >0 bone tracks).
Verified RED on the pre-fix clips (pivot 0/4, unresolved `Hips`/`Spine`/
`LeftUpLeg`/`RightUpLeg`) and GREEN after (idle 31/31, run 22/22, pivot 4/4).
Track *binding* is state-checkable and now proven; pose *correctness* stays the
deferred human feel judgment (#178/#173, ADR-0021).

## Addendum (issue #271, 2026-07-22) — two more import-time gotchas

A human in-editor pass after #267 found the retargeted idle/run still had two
defects the binding harness above cannot see (it only proves tracks *resolve*,
not that the resulting pose/loop is correct). Both were import-config gaps,
not the extraction step:

1. **The FBX importer's per-clip default is `loop_mode = LOOP_NONE`.** Nothing
   in the retarget pipeline sets it, so `run` visibly froze after one pass in
   the editor. **A per-animation `settings/loop_mode` key in `_subresources`
   IS honored headlessly** — confirmed empirically: `"animations": {"Root|Idle":
   {"settings/loop_mode": 1}}` (the key is the FBX **take name**, `Root|Idle`/
   `Root|Run` here, not the AnimationLibrary's shorter `idle`/`run` key) flips
   the reimported clip to `LOOP_LINEAR`. **But it is not worth using**: on
   round-trip Godot expands that one key into the *entire* per-animation
   import-option schema, including a `slice_1`..`slice_N` block that runs into
   the **thousands of lines** (a `_subresources` set this way ballooned
   `idle.fbx.import` from ~60 lines to 2000+) — unreviewable and fragile for a
   hand-authored import file (ADR-0011). **Chosen fix: set `anim.loop_mode =
   Animation.LOOP_LINEAR` programmatically in the extraction/rebuild step**
   instead, exactly as this issue's fallback allowed. `.fbx.import` stays
   untouched for loop mode; only the rebuilt `.res` clips carry it.
2. **`overwrite_axis=true` alone anchors the retarget at the SOURCE rest, not
   world pose.** Every arm-chain bone's frame-0 key landed exactly on Y Bot's
   raw import rest (`0.000000°` deviation) because the rest-fixer preserved
   delta-from-source-rest, and Y Bot's own rest happens to be a T-pose
   (arms-horizontal is baked into the Shoulder rest). Whenever the source
   rig's neutral pose differs from the target profile's reference pose (here:
   Kenney's arms-down neutral vs. Mixamo's T-pose), `overwrite_axis` is not
   enough — add `retarget/rest_fixer/fix_silhouette/enable=true` (plus
   `.../threshold=15.0`, Godot's default) to the same per-node `_subresources`
   block used for the bone map. This round-trips as a clean 2-line diff (no
   schema explosion, unlike the animations key above) and empirically moved
   idle/run's `LeftArm`/`RightArm` first-key-vs-rest deviation from `0.0°` to
   `50-56°`, matching a natural arms-down neutral, while leg/foot motion ranges
   stayed in the same ballpark as before the fix (sanity-checked via the
   diagnostic probe, not just the binding count) — no filter exclusion was
   needed for this rig/clip pair.

Both fixes are confirmed by `LocomotionClipTest`'s two new assertion families
(`loop_mode == Linear` per clip; first-key-vs-rest `>= 10°` for `LeftArm`/
`RightArm` on `idle`/`run`) — RED before, GREEN after. See PR for #271.

## Addendum (issue #273, 2026-07-23) — pivot's "no rest-fixer pass" caveat, closed

A human in-editor 180° pivot after #271 landed a garbage pose (limbs splayed,
body collapsed) — the caveat flagged above (`pivot` "pose orientation is
unverified") turned out to be a real bug, not just an untested gap. Root
cause, confirmed numerically: pivot's name-only remap (#267) carried the
rotation keys over **as authored against the KENNEY rig's rest
orientations**. Godot `ROTATION_3D` tracks are absolute local rotations, so
Y Bot's bones were handed the raw Kenney rest quats verbatim — pivot's first
key matched Kenney's rest **bit-for-bit** (`0.000000°` deviation on all 4
bones) while sitting 177–180° off Y Bot's own rest on Hips/LeftUpLeg/
RightUpLeg (Spine's rest happens to coincide across rigs at ~0°, which is why
the pose read "collapsed" rather than uniformly rotated).

Because `pivot` has no source FBX, the importer's rest fixer (the
`fix_silhouette` mechanism from the #271 addendum above) cannot run against
it — the same hand-derivation gap this spike's gotcha list already flagged.
The fix instead re-derives the equivalent correction by hand, per rotation
key:

```
q_fixed = ybot_rest * kenney_rest⁻¹ * q_key
```

`kenney_rest` is hardcoded per bone (the 4 quats are the pivot clip's own
pre-fix first keys — provenance: extracted at rev `80d66c5`, the #242
authoring commit; the source Kenney rig no longer exists in the tree).
`ybot_rest` is read live from `res://assets/Y Bot.fbx`'s `Skeleton3D`, so the
correction stays valid even if the rig is ever re-imported. Left-
multiplication by a unit quaternion is an isometry, so the authored inter-key
motion (the real pivot animation, 6–10° pairwise per track) is preserved
exactly — only the rest anchor moves. Applied via a one-off headless GDScript
pass (not shipped, same disposable-tool precedent as this spike's own
extraction scripts), writing the corrected `pivot` animation back into
`assets/locomotion.res`; `assets/Y Bot.fbx.import` stays untouched.

Confirmed by `LocomotionClipTest`'s third assertion family (opposite polarity
of the #271 T-pose-anchor guard: every pivot rotation key must sit **within**
15° of Y Bot's rest, not far from it, plus a `>= 3°` per-track pairwise-motion
floor so a degenerate "fix" can't collapse the clip to static rests) — RED
before (Hips 179.83°, LeftUpLeg 177.55°, RightUpLeg 177.99°, matching this
addendum's fact table bit-for-bit) and GREEN after (6.0–10.0° on all 4
tracks, motion intact). See PR for #273.

**Net effect on the gotcha above:** pivot's pose is now numerically anchored
to Y Bot's rests, on the same footing as idle/run's rest-fixer pass — track
*binding* and rest-anchored *pose* are both proven by the harness. Only
whether the pose *looks right* remains open, deferred to the human feel pass
(#178/#173, ADR-0021).

---

## Addendum (issue #287, 2026-07-23) — the idle<->run BlendSpace1D mixer degeneracy, closed

#275/PR #286 fixed the *data-level* half of the idle<->run start/stop-run
twitch (RightUpLeg's 180° retarget twist + hemisphere-sign normalization).
A dedicated continuous-drive probe then proved that half insufficient: the
LIVE `AnimationNodeBlendSpace1D` mixer still produced out-of-corridor leg
poses at INTERMEDIATE blend weights during a real 0->6 ramp — 53/90 sampled
frames (1/60s steps over 1.5s) violated a (reference-gap + 10°) corridor
around phase-matched idle/run reference rigs, worst `mixamorig_RightUpLeg` @
blend 3.0: 175.9° from BOTH references vs a 31.9° reference-to-reference gap
(134.0° excess). This is a genuinely different bug class from #275's: stored
key *data* was already hemisphere-clean per #286's own inertness proof
(whole-track sign flips give byte-identical blend output), yet the LIVE
blend output still misbehaved.

### Mechanism

Godot's `AnimationTree` mixer blends multiple simultaneous contributions
(here: the two `AnimationNodeAnimation` children a `BlendSpace1D` weights
between) via a REST-ANCHORED accumulation, not a naive two-point SLERP of
absolute poses. A #287 diagnostic probe measured `mixamorig_LeftUpLeg`/
`RightUpLeg`'s own rest-relative deviation across each clip's FULL timeline
independently: `idle` sits 162–177° from Y Bot's raw T-pose rest, `run` sits
131–180° — i.e. **both** clips individually sit near the ANTIPODE of rest
for these two bones, not merely far apart from each other. When two
contributions both sit near rest's antipode along different great circles,
their rest-relative components partially cancel at intermediate blend
weights, producing a pose on NEITHER clip's arc — exactly the observed
134° excess.

### Rejected alternatives (tried, in order, before the authorized fix)

1. **Synthesized intermediate blend-point clips** (the issue's own first
   recommendation): a `mid` clip built as per-shared-track
   `slerp(idle_key, run_key, 0.5)` (20-sample uniform grid, timeline-
   normalized), inserted as a third `BlendSpace1D` point at pos 3.0.
   Empirically **made it WORSE**: 65/90 violated (up from the 53/90
   baseline). Bisecting further (adding `lo_mid`/`hi_mid` at 1.5/4.5, five
   total blend points) improved but did not close it: 28/90 violated, worst
   excess 107.3°. Explained by the mechanism above: because BOTH idle and
   run individually sit near rest's antipode across their ENTIRE timeline,
   the "both contributions near antipode" degeneracy condition holds for
   *any* pair of points drawn from this same pose family, not just the
   idle/run endpoints — absolute-space slerp midpoints give diminishing but
   non-closing returns (53 -> 65 -> 28, not converging toward 0) no matter
   how many are added.
2. **RESET-track anchoring**: found byte-inert to the mixer output (0/90
   unchanged either way) — Godot's built-in `RESET` animation is an
   editor/tooling convention with no runtime effect on `AnimationTree`
   blending; it does not participate in the mixer's rest-relative
   accumulation at all.
3. **Re-representing stored quats (sign/hemisphere)**: already proven inert
   to blend output in #286 (whole-track sign flips give byte-identical blend
   output) — ruled out again here for the same reason.

### Authorized fix: scoped runtime rest-normalization (`BlendRestAnchor.cs`)

`scripts/Player/BlendRestAnchor.cs`, wired as a node into `scenes/Player.tscn`
(single-concern scene-edit commit, ADR-0011), re-anchors EXACTLY TWO
`Skeleton3D` bone rests — `mixamorig_LeftUpLeg` and `mixamorig_RightUpLeg`,
and no others — to `locomotion/idle`'s own first ROTATION_3D key (read from
the live `AnimationLibrary` at runtime, never hardcoded, so it stays
self-maintaining if the clips are ever rebuilt), instead of Y Bot's raw
T-pose import rest. This removes the "both contributions near rest's
antipode" condition at its source, rather than trying to shrink the gap
between two antipodal contributions.

Why this is safe (state-checkable, not a feel judgment):
- `ROTATION_3D` animation tracks are ABSOLUTE local rotations in Godot, so
  any bone driven at full weight by a single clip never consults rest at
  all — both `BlendSpace1D` endpoints (full idle @ blend 0, full run @
  blend 6) are UNCHANGED, confirmed byte-identical before/after (same key
  values, same output) by the harness.
- `pivot` (the plant/turn clip) animates both UpLeg bones directly on its
  own 4 rotation tracks and always plays at full weight — its own dedicated
  `AnimationNodeStateMachine` state, never blended with anything else — so
  its rendered pose is unaffected. The `LocomotionClipTest` #273 family
  (pivot rest-delta) is still green with numbers UNCHANGED from before this
  fix (Hips 7.999956°, Spine 6.000054°, LeftUpLeg 10.000049°, RightUpLeg
  10.000049°) — but only because that family was updated to compare against
  a SEPARATE, freshly-instantiated raw `Y Bot.fbx` reference skeleton
  instead of `Player.tscn`'s own (now rest-anchored) skeleton copy; the
  #271 T-pose-anchor family was updated the same way for the same reason.
  Without that reference-source fix, #273's pivot LeftUpLeg/RightUpLeg
  checks would have started silently grading against the fix's own output
  instead of Y Bot's true rest — a false-negative sinkhole.
- Every `AnimationNodeStateMachineTransition` in `scenes/Player.tscn` has an
  unset (Godot-default 0) `xfade_time` — verified directly by grepping the
  `.tscn` text for `xfade`, zero matches — so every state-to-state switch
  (Locomotion/Startup/Active/Recovery/Pivot/FadeawayActive) is a hard cut,
  never a cross-blend. The Locomotion `BlendSpace1D` (idle<->run) is
  therefore the ONLY multi-contribution blend surface in the whole
  `AnimationTree`; this fix cannot expose any new partial-weight surface.
- `PlayerRigScaler.CaptureBaseline()` (#170) reads ONLY `GetBoneRest(i)
  .Basis.Scale` for every bone, and its own writes go to the ANIMATED POSE
  (`SetBonePoseScale`), never rest. `BlendRestAnchor` mutates ONLY rest's
  ROTATION (rebuilding the basis from a pure anchor quaternion, then
  re-scaling by the ORIGINAL rest's own `Basis.Scale` so the value
  `RigScaler` reads back is byte-identical either way) and leaves Origin
  untouched — the two nodes' `_Ready()` order is provably irrelevant.

Result, confirmed by `LocomotionClipTest`'s #287 corridor-sweep family:
**0/90 violations** (down from 53/90), `mixamorig_Hips` control still 0/90
(the sweep stayed discriminating throughout), all four other families
(#271/#273/#275 track-resolution/loop-mode/T-pose-anchor/pivot-rest-delta/
blend-compatibility) green with numbers unchanged from the pre-fix baseline.
Pose *quality* (whether the corrected blend *looks* right, beyond staying
in-corridor) remains the deferred human feel judgment (#178/#173,
ADR-0021) — this addendum only closes the state-checkable defect.
