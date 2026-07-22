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
