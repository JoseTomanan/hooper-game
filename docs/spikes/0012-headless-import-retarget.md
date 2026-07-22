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
  name-only remap (`Hips→mixamorig_Hips`, …) with **no rest-fixer pass**, so its
  track *binds* but its *pose orientation* is unverified. **`run`'s "Run" clip
  has no `mixamorig_Hips` track** at all (before and after) — any vertical hip
  bob was already absent. Both are pose caveats for the human verify #178.
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
