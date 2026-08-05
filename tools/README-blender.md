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
10. **A guard on a near-degenerate quantity must fire while the answer is still
    wrong, not once it is undefined.** Every cross-product / Gram-Schmidt guard
    here protects a *direction*, and a direction is noise long before its
    magnitude reaches zero. Measured in Blender's float32 vectors: the direction
    error of a normalised residual grows as roughly `1.1e-5/θ` degrees, so the
    old `< 1e-6` guards were admitting planes that were already **10° wrong**
    and, at `1e-7`, **62° wrong** — silently, with the orthonormality assert
    passing happily on the resulting basis. Thresholds live in
    `BEND_PLANE_MIN_SIN` / `LANDMARK_MIN_COS`; both are set from that curve and
    both are proven inert against real work (see limitation 3). The general
    form: **never write `== 0.0` or `< 1e-6` on a quantity whose *usefulness*
    degrades continuously — measure where it stops meaning anything, and guard
    there.**

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

## Three measured limitations, all now FIXED (#321, #320, #338)

The first two were pre-existing from #300 and both were deferred out of #315
because fixing them appeared to change the exported clip; the third was found by
the `/code-review` pass on the PR that fixed them. This section records what they
were, because the *shape* of each defect is the reusable lesson and each left
permanent gates behind.

### 1. Lateral axis: `geom.right` → `geom.lateral` + `geom.body_right` (#320)

`geom.right` pointed at the character's **LEFT** — left shoulder `+0.1343 m`
along it, right shoulder `-0.1804 m`. All six authoring scripts already worked
around it with a local `BODY_RIGHT = -geom.right`, so it was never a live bug,
just a naming landmine with six copies of its own antidote.

**Resolved by splitting the two meanings, not by flipping the sign:**

| Use it for | Attribute |
|---|---|
| "which side of the **body**" — every hand/foot **placement** | `geom.body_right` |
| a side-reference **axis** — `aim_matrix`'s `side_axis`, a `Matrix.Rotation` axis | `geom.lateral` |

There is **no `geom.right`** any more. It was removed rather than aliased, so a
stale call site raises `AttributeError` at the point of the mistake.

**Why not just correct the sign?** Measured, because the answer is not obvious:
negating only `forward` and re-authoring the dribble gives **4096 of 4160 pairs
rotation-differing**, every leg bone rotated `179.99°`, worst location delta
`0.8418 m` on `HeadTop_End`. The axis is load-bearing in two *non-anatomical*,
sign-critical roles — `aim_matrix`'s roll reference (flip it → legs inside out)
and the torso-lean rotation axis (flip it → the lean reverses and the whole upper
body swings). "Correcting" it produces a broken clip, not a mirrored one.

`geom.body_right` is returned as `±geom.lateral` — never re-derived — so the
migration is bit-identical: the dribble re-authors to **0 / 4160** and no asset
changed. Its sign is measured against the **shoulder** span (independent of the
hip pair `lateral` is built from) and cross-checked against the hip span;
disagreement raises rather than guessing.

**The gates it left behind** (`selftest_anim_lib.py` section 0):
`lateral_axis_sign_pinned` (the basis sign, *not* an anatomical claim — it exists
because flipping it silently rolls every posed bone 180°), and
`body_right_points_rig_right`, which is **non-symmetric**: it names the sides and
fails if they swap. That is the property nothing in #300 or #315 had — every
check there was side-agnostic or read the side off whichever chain it was handed
(the #255 lesson). Mutation-proven: making `derive_body_right` return `lateral`
unchanged fails it at `R=-0.1804 / L=+0.1343` while every other check stays green.

### 2. `plant_foot`'s leg IK was inexact for lateral targets (#321)

The femur direction was `Matrix.Rotation(-hip_offset, 4, right) @ dir_ankle`, and
a rotation about `right` only moves the component of `dir_ankle` *perpendicular*
to `right`, so a laterally-offset target achieved less than the requested angle:

```
cos(theta_eff) = cos²(alpha) + sin²(alpha)·cos(hip_offset)
```

The knee landed off the IK circle, the tibia was aimed at the true target from
that wrong knee, and the ankle fell **short** — `worst_ankle_ik_err_m = 0.0299`,
3 cm, against a whole ground band of `0.0315 m`.

**Fixed** by using `aim_arm`'s construction: `axis = dir_ankle.cross(forward)`,
perpendicular by construction, rotated by a **positive** `hip_offset` (same sign
convention as `aim_arm` — don't "tidy" one to match the other). A fore/aft target
at hip height names no bend plane and now raises, mirroring `aim_arm`'s
degenerate branch. `worst_ankle_ik_err_m` is now `0.000000`, and the femur solve
no longer reads the lateral axis at all.

**Read the formula at `alpha = 90°`: the shortfall vanishes exactly.** Every clip
authored before #321 is a near-sagittal gait, and every assertion written for
them measured precisely that angle — which is how a 3 cm error survived two
milestones of green gates. This is the reusable lesson: *a gate that only ever
samples the one input where the bug is zero is not a gate.* The new selftest
cases keep a sagittal **control** alongside the lateral ones for exactly that
reason (it read `9e-8` on the buggy solver too).

**The other gate it left behind** is more important than the ankle measurement:
a two-link solve has a whole **circle** of valid knee positions and every one of
them lands the ankle exactly on target, so the ankle-error check is structurally
**blind** to a knee bending backwards. `knee_bends_forward_*` measures the knee's
displacement from the hip→ankle *chord* along `forward`. Mutation-proven:
flipping the rotation sense leaves `ankle_reaches_*` PASSING at `1.3e-7 m` while
the knee gate reads `-0.2024 m` and fails. If you add another IK primitive, it
needs both gates — reaching the target does not mean the joint bent the right way.

> ⚠️ **Six authored clips have not been regenerated** since #321 —
> `behindtheback`, `block`, `contest`, `jabstep`, `layup`, `steal`. Their
> committed FBX files were produced by the old solver, so re-running their
> authoring scripts now yields a non-zero diff *by design*, and **#338 added a
> second layer to that same drift.** Both layers are confined to the leg chain
> (zero arm/spine/head/hips/hand rotation in any of the six). Re-running them
> today, against the committed asset:
>
> | clip | worst rotation | worst location |
> |---|---|---|
> | `jabstep` | `5.62°` | `0.0186 m` |
> | `steal` | `6.00°` | `0.0172 m` |
> | `contest` | `8.57°` | `0.0281 m` |
> | `behindtheback` | `12.02°` | `0.0400 m` |
> | `block` | `18.33°` | `0.0673 m` |
> | **`layup`** | **`74.22°`** | **`0.4374 m`** |
>
> The **location** column is *identical* to what #321 alone produced — #338 is a
> pure roll change and moves no joint — so the `layup` positional outlier is
> still entirely #321's, and #338 does not disturb the "was the layup spec tuned
> against the buggy solver?" question that has to be answered before it is
> regenerated. Tracked separately; do not interpret a non-zero diff on those six
> as your own change.

`plant_foot` returns `(solve_triple, achieved_ankle_error)` — keep reporting it.
The solve triple describes the *request*, not the *result*.

### 3. `plant_foot`'s bone ROLL was ill-conditioned, and mis-scoped (#338)

#321 made the femur *position* solve exact for any target direction. The **roll**
did not get the same treatment: all three of `plant_foot`'s `aim_matrix` calls —
femur, tibia, foot — were handed `geom.lateral` as their roll reference.

**For the femur and tibia that is wrong continuously, not just at a
singularity.** A knee is a hinge, so the femur's roll decides which way the
kneecap faces, and it should face along the direction the knee actually bends —
i.e. the roll reference should be the **normal of the hip-knee-ankle plane**.
`geom.lateral` equals that normal only for a purely sagittal leg; as the leg
abducts the two diverge by the abduction angle. Measured over every `plant_foot`
call each authoring script makes:

| clip | worst | mean | | clip | worst | mean |
|---|---|---|---|---|---|---|
| `dribble` | `22.47°` | `18.82°` | | `behindtheback` | `12.74°` | `5.93°` |
| `layup` | `56.53°` | `7.94°` | | `contest` | `9.27°` | `6.69°` |
| `block` | `18.11°` | `9.64°` | | `steal` | `7.47°` | `2.88°` |
| | | | | `jabstep` | `6.24°` | `5.18°` |

**Fixed** by splitting the reference by joint role — `hinge_axis` (the bend-plane
normal) for the femur and tibia, `sole_axis` (`geom.lateral`) for the foot.

**Two things the issue got wrong, both caught by measuring rather than
reasoning**, and both worth knowing because the same shape will recur:

1. **The sign.** #338 proposed passing `axis`, the vector `plant_foot` already
   computes as `dir_ankle.cross(forward)`. That is `180.000000°` from
   `geom.lateral` for a sagittal leg, so passing it as written rolls every leg
   bone 180° — the same catastrophe #320 measured. The **negation**,
   `forward × dir_ankle`, is `0.000000°` from `geom.lateral` for a sagittal leg:
   it agrees with the old behaviour exactly where the old behaviour was right.
2. **The scope.** Applying it to the **foot** too — the literal one-line fix —
   rolls the planted sole out of horizontal by `22.16°` (dribble) / `46.35°`
   (layup); the character stands on the edges of their feet. `geom.lateral` is
   *horizontal*, which is the whole reason it belongs on the foot and nowhere
   else. **`verify_grounded` cannot see this**: it measures ankle and toe
   *heights*, and a foot rolled about its own long axis keeps both.

**What bounds the foot's reference**, since it keeps a fixed axis: its
conditioning depends on `toe_dir`, *not* on abduction. It degenerates only for a
foot aimed sideways (`toe_dir` within `BEND_PLANE_MIN_SIN` of `geom.lateral`),
which nothing in the tree does. A move that genuinely needs one — some
defensive-slide shapes — should pass an explicit sole reference.

**The framing this corrects.** #338 describes the defect as a "visible leg-twist
pop" from a roll *discontinuity*. The discontinuity is real — measured at
**1800° of roll per degree of target movement** — but it sits at exactly 90° of
abduction *and* near-full extension, i.e. an ankle at hip height on a straight
leg. No foot plant reaches that. What is reachable, and what every shipped clip
already carried, is the continuous misalignment tabulated above.

**The gates it left behind** (`selftest_anim_lib.py` sections 4d–4g):
`hinge_aligned_*` (the femur/tibia roll axis vs the realized bend-plane normal,
read off the *posed skeleton*, with a sagittal **control** that read `1.000000`
even pre-fix), `sole_stays_level_*`, `plant_foot_refuses_noise_plane_*` /
`aim_arm_refuses_noise_plane_*`, and `body_right_refuses_coinflip`. All
mutation-proven: pre-fix the hinge gates read `0.969190` at 22° of abduction and
`0.000002` at the conditioning limit, and handing the foot the hinge axis fails
`sole_stays_level_*` at `±21.80°` / `±45.00°` while every other gate in the
library — `verify_grounded` included — stays green.

**Proven inert on real work**, which is what makes the widened guards (trap 10)
hardening rather than a behaviour change: over every `plant_foot` call of all
seven authoring scripts the smallest `|dir_ankle × forward|` is `0.402019` and
the smallest Gram-Schmidt residual is `0.759086` — 400× and 759× clear of
`BEND_PLANE_MIN_SIN`. The real rig's landmark margins are `0.99999998`
(shoulders) and `1.00000000` (hips), 1000× clear of `LANDMARK_MIN_COS`.

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
