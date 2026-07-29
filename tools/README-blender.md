# Headless-Blender animation authoring

How to author an animation clip for this project in Blender with no GUI, and the
traps that have already cost measured time. Introduced by #315 as the shared
foundation for the per-move clip batch (#281–#318).

Blender is **not** a build dependency. Nothing here is needed to build, test, or
run the game — only to author or re-author an animation clip.

## The toolchain

Portable ZIP, no installer, no admin. `$BLENDER` points at the executable, the
same convention as `$GODOT`:

```bash
"$BLENDER" --background --version          # expect a "Blender <version> LTS" banner
```

Verified 2026-07-29 on Blender **5.2.0 LTS**. No version pin exists because
nothing here depends on a specific Blender API version — but see the
slotted-Action note under Traps, which is a hard 4.4+ break.

> **Gotcha:** `BLENDER` is a **User**-scope environment variable, so it is
> invisible to a shell process that was already running when it was set. Re-read
> it explicitly rather than trusting `$env:BLENDER`:
> `[Environment]::GetEnvironmentVariable("BLENDER","User")`

## The pipeline

```
1. author    "$BLENDER" --background --python-exit-code 1 \
                 --python tools/author_<move>.py -- <src.fbx> assets/<move>_authored.fbx
2. import    assets/<move>_authored.fbx.import        (hand-authored)
3. rebuild   "$GODOT" --headless --path . -s tools/rebuild_<move>_clips.gd
                 -> slices + time-scales into assets/locomotion.res
4. wire      scenes/Player.tscn — AnimationNodeAnimation sub-resources, states,
                 and one transition sub-resource PER EDGE
5. resolve   scripts/Player/MoveAnimResolver.cs — ClippedMovePrefixes
                 (+ HandedMoves only if the move swaps hands at Active-entry)
6. prove     tests/integration/<Move>AnimTest.cs + .tscn, registered in
                 .github/workflows/ci.yml
```

**`--python-exit-code 1` is mandatory at step 1.** Blender's
`--background --python` exits **0 even on an uncaught traceback**. Without the
flag a crashed authoring run reports success to the pipeline. Every `verify_*`
helper in the library raises `SystemExit` rather than logging, specifically so
this flag turns a failed proof into a failed build.

## The three tools

| Tool | What it does |
|---|---|
| `blender_anim_lib.py` | The shared machinery: rest-derived rig geometry, two-link IK, `plant_foot` / `aim_arm` posing primitives, the keypose timeline, the proof helpers, and the export settings. Import it; do not copy it. |
| `compare_fbx_anim.py` | The equivalence gate. Compares two FBX files pose-by-pose and exits nonzero if they differ. **Use this instead of `cmp`/`git diff`** — see below. |
| `selftest_anim_lib.py` | Proves the library's own new machinery (arm IK, timeline interpolation, and that the proof gates actually fail when they should). Run it after touching the library. |

```bash
"$BLENDER" --background --python-exit-code 1 \
    --python tools/selftest_anim_lib.py -- assets/Dribble.fbx     # expect SELFTEST_OK
```

## Verifying an authoring change

An FBX export is **not byte-reproducible**, so `cmp` and `git diff` cannot tell
you whether you changed the animation. Measured 2026-07-29: two runs of one
*unchanged* script produced files differing in **12 598 bytes** while their poses
were bit-identical. Blender derives FBX object UUIDs from `hash(key)`
(`io_scene_fbx/fbx_utils.py:_key_to_uuid`, which carries its own
`TODO: Check this is robust enough for our needs!`) and those vary per process.
`PYTHONHASHSEED=0` does **not** fix it.

So compare poses:

```bash
"$BLENDER" --background --python-exit-code 1 --python tools/compare_fbx_anim.py \
    -- <new.fbx> <reference.fbx>          # exits 0 + CMP_OK if identical
```

Measured discriminating power on the dribble clip:

| Change | worst rotation | worst location |
|---|---|---|
| refactor with no behaviour change | `0.000000 deg` | `0.00000000 m` |
| `STRIDE_LENGTH_M` 0.60 → 0.61 (1 cm) | `0.696609 deg` | `0.00514797 m` |

It is exact-zero on encoding noise and catches a 1 cm spec change at well under
this project's own `LOOP_SEAM_TOLERANCE_DEG` of 0.5°.

**Reading a nonzero result.** Rotations bit-identical, with location deltas under
1 µm that *grow with depth down the kinematic chain* (Hips smallest, fingertips
largest) is float32 quantization accumulating along the chain, not a behaviour
change. A handful of pairs differing by a lot is a real, localised change.

## Traps

1. **Everything is armature space.** `pose_bone.matrix` / `.head` are
   armature-space; `arm.matrix_world @ p` is world-space and carries Mixamo's
   0.01 cm→m object scale. Straddling them is a silent **100×** error, and an
   *asymmetric* one: a child bone's head is recomputed from its parent so a bad
   translation is absorbed and only rotation survives, but on the **root** (Hips)
   the translation *is* the edit and it vanishes without a trace. Author spec
   constants in metres and convert through `RigGeometry.m()`; never hand-roll the
   factor.
2. **Derive rest geometry only from a raw FBX**, never from a `Player.tscn` rig.
   `BlendRestAnchor` rotates both UpLeg rests at `_Ready` and every foot/toe
   global rest inherits the error (119.6°) — that cost a 2.17× stride
   mismeasurement in #298. `assets/Y Bot.fbx` is the raw rig.
3. **`Action.fcurves` is gone in Blender 4.4+** (slotted Actions). Verified on
   5.2: `Action` has no `fcurves` attribute at all. The path is
   `action.layers[…].strips[…].channelbags[…].fcurves` — use
   `blender_anim_lib._action_fcurves()`. Prefer only ever calling
   `keyframe_insert`, which sidesteps the API entirely.
4. **`bpy.context.view_layer.update()` after every `pose_bone.matrix`
   assignment.** It looks redundant and it is not: the next bone's `head` read is
   stale without it, which aims a child bone from a position its parent no longer
   occupies and quietly breaks the IK chain. `plant_foot` and `aim_arm` do this
   internally — do not "clean up" those calls.
5. **Measurement must not perturb the artifact.** The FBX exporter's output is
   sensitive to which frame the scene is sitting on when it runs. Measured:
   proof helpers that stepped frames without restoring shifted every exported
   bone position by up to 0.85 µm. Anything that samples poses must wrap itself
   in `preserve_frame()`.
6. **Matrix multiplication is not bitwise associative.** Regrouping a product
   `(A@B)@C` → `A@(B@C)` perturbs the low bits, which shows up as a spurious
   sub-degree delta when comparing against a reference clip. Preserve the
   original grouping when refactoring pose arithmetic.
7. **A leaf bone is not a keyed bone.** Of the Y Bot's 65 bones, the Mixamo
   source clips key 52; the 13 exceptions are all terminators (`HeadTop_End`,
   `*Toe_End`, ten finger tips). `verify_all_bones_keyed` exempts them by
   default. Every *other* unkeyed bone is the `a45bd1d` trap: a single-clip
   AnimationTree state falls back to skeleton **rest** for bones the clip omits,
   so a hand-keyed clip that touches only the gesturing limb makes the arms
   T-pose the moment the move plays. A Blender export bakes the whole armature,
   which makes that failure structurally absent — do not narrow the export.
8. **`remove_immutable_tracks` silently drops constant tracks.** Set
   `remove_immutable_tracks=false` in the `.fbx.import` block. A bone held at a
   constant non-rest pose for a whole segment — very common in these short clips
   — is exactly what "immutable" means, and dropping it re-arms trap 7. This
   killed #297's first fix attempt.
9. **Scale channels already exist in every source action.** `Dribble.fbx` carries
   156 of them (52 bones × 3 axes), and these scripts key *into* the source
   action, so "assert no scale fcurves" would fail on every run while catching
   nothing. The real risk is a non-unit `aim_matrix` basis writing a non-identity
   **pose** scale, which is what `verify_pose_unscaled` checks. Baseline on the
   untouched source: 2.4e-7 off unit.

## Segments of ≤3 ticks are single poses, and that is correct

Several moves have a phase of 3 ticks or fewer; at 30 fps a 0.033 s Active
segment is **one frame**. Do not try to animate inside it. Author one distinct,
held **impact pose** per phase and let the read come from *pose contrast between
phases* — the UFC Undisputed 3 model: the startup pose is the tell, the active
frame is the commitment, the recovery pose is the punish window. Three readable
silhouettes, not three little movies.

Concretely: **Startup and Recovery must never be the same pose.** That identity
is the defect #296 reports, and `verify_pose_distinct` exists to make it
impossible rather than something a reviewer has to catch by eye.

## Easing is chosen for you, per phase

You do not pick an easing curve per move. `PHASE_EASING` maps the keypose
**label** to the curve for the segment starting at it — Blender's own fcurve
convention, where a keyframe's interpolation governs the interval to the *next*
key. Label your keyposes `Startup` / `Active` / `Recovery` (case-insensitive) and
the right curve is applied:

| Segment | Curve | Endpoint velocity | Reads as |
|---|---|---|---|
| `Startup` → `Active` | `ease_in` | 0 → 2 | the weight gathers, then goes |
| `Active` → `Recovery` | `ease_out` | 2 → 0 | explode out, decelerate |
| `Recovery` → anything | `ease_in_out` | 0 → 0 | a settle back toward neutral |

**Why not smoothstep everywhere.** Smoothstep has zero velocity at *both* ends,
so the body would glide into the Active pose and arrive at rest. That is the
visual signature of what [ADR-0003](../docs/adr/0003-input-model-hybrid.md) names
as the primary anti-goal — arcade decoupling of action from physical commitment.
The Active frame is the commitment; arriving at it decelerating undoes the read.
Athletic movement is asymmetric: load slow, release fast, settle.

The curves are quadratic rather than cubic on purpose. A 6-tick startup is six
frames at 30 fps, so a more dramatic curve buys almost nothing and risks reading
as a stutter — and per the section above, the read comes from pose contrast
*between* phases anyway. The easing only has to avoid fighting that.

Overriding, most specific first: `Keypose(..., easing=ease_linear)` for one
segment, `bake_timeline(..., easing=...)` for a whole timeline (the cyclic-gait
case — an unrecognised label already falls back to `ease_in_out`, so a gait
authorer is unaffected). `bake_timeline` **logs the resolved curve per segment**,
so check the authoring output rather than guessing:

```
[author] segment 'Startup' -> 'Active' (0.000s..0.200s): easing=ease_in
```

## Two measured limitations you inherit

Both are pre-existing (from #300), both are proven not to affect the shipped
dribble clip, and both are fenced off by measurements that print every run.
Neither was fixed in #315 because correcting either changes the exported clip
and breaks the `0 / 4160` equivalence gate that proves the extraction is clean.

### 1. `geom.right` points at the character's LEFT

Measured: the left shoulder sits at `+0.1343 m` along `geom.right`, the right
shoulder at `-0.1804 m`. `derive_axes` negates `right` alongside `forward` in a
branch that **fires on every Mixamo rig**, and nothing downstream re-checks the
sign anatomically.

The leg IK is unaffected — knees measured bending *forward* by `+0.129 m` (L) and
`+0.069 m` (R) mean displacement from the hip→ankle chord — and a symmetric gait
hides it completely, which is why it survived #300.

**It will not stay hidden in a handed move.** `hand_target = hips + geom.right * x`
puts the hand on the character's **left**. For behind-the-back, the ball-hand
sweep, between-the-legs, or anything that swaps hands, derive the side from bone
positions (e.g. `LeftHand.head - Hips.head`) rather than from `geom.right`, and
give the clip a **non-symmetric** assertion — a mirrored clip passes every
symmetric check ever written (the #255 lesson).

`selftest_anim_lib.py` section 0 pins this convention, so if the sign is ever
corrected the selftest fails loudly and tells you to re-run the equivalence gate.

### 2. `plant_foot` is inexact for lateral targets

`Matrix.Rotation(-hip_offset, 4, right)` only rotates the component of the
hip→ankle direction perpendicular to `right`, so a target with a sideways
component achieves less than the requested hip angle:

```
cos(theta_eff) = cos²(alpha) + sin²(alpha)·cos(hip_offset)
```

The knee lands off the IK circle and the ankle falls **short**. Measured on the
dribble clip: **`worst_ankle_ik_err_m = 0.0299`** — 3 cm, and that gait is nearly
planar. Moves with real lateral footwork (euro-step, spin, step-back, defensive
slides) will be worse.

`aim_arm` does **not** share this defect: it builds its rotation axis as
`dir_wrist.cross(hint)`, perpendicular by construction, so its solve is exact
(sub-micron wrist error in the selftest). The fix is to use the same
construction here.

`plant_foot` returns `(solve_triple, achieved_ankle_error)` — report it. Without
it the shortfall is invisible, because the solve triple describes the *request*,
not the *result*.

## Reach budgets (measured on the Y Bot rig)

| Chain | Length |
|---|---|
| femur | 0.4060 m |
| tibia | 0.4210 m |
| **hip→ankle reach** | **0.8270 m** |
| humerus | 0.2740 m |
| ulna | 0.2761 m |
| **shoulder→wrist reach** | **0.5502 m** |

An arm reaches barely two-thirds as far as a leg, so a `hand_target` sized by eye
against leg geometry silently exceeds it. `aim_arm` therefore treats over-reach as
**fatal**: a clamped two-link solve produces a locked, straight arm that reads as
a mannequin rather than a reach. Pull the target in instead.
