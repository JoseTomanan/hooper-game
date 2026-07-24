# Spike — Text-authored AnimationTree loads & runs identically to an editor-authored one

- **Issue:** #87
- **Gates:** ADR-0011's deliberate exclusion of AnimationTree graph authoring; M7b (#54) Steps 3–4.
- **Date:** 2026-06-28
- **Verdict:** **PASS — fully proven.** AFK structural + headless-load proof (31/31)
  *and* the human's one-time in-editor visual confirm both passed (2026-06-28). The
  text-authored tree was swapped into `Player.tscn`'s `AnimationTree.tree_root`, run,
  and confirmed to animate identically (then reverted). ADR-0011's AnimationTree
  exclusion is **lifted**; AnimationTree graph authoring is now AFK.

## Question

ADR-0011 lets Claude author `.tscn`/`.tres`/`project.godot` by text-edit, but
**excludes** AnimationTree graph authoring (state machines + BlendSpace1D) because
authoring those graphs blind was unproven here. This spike asks: does a
**hand-authored** `AnimationTree` (StateMachine + BlendSpace1D) (1) load with no
resource errors, and (2) run identically to the same graph built in Godot's visual
editor?

## Method

The editor-authored baseline already exists: the AnimationTree a human built in the
Godot editor for #68 (commit `a4d1630`), embedded as sub-resources in
`scenes/Player.tscn`. The spike compares an independently hand-written copy against it.

Artifacts (all under `scenes/spike/`):

| File | What it is |
|------|-----------|
| `extract_baseline.gd` | Headless tool: loads `Player.tscn`, `ResourceSaver.save()`s the AnimationTree's `tree_root` → `loco_baseline.tres`. Guarantees the comparison target is the **genuine human artifact**, not a transcription. |
| `loco_baseline.tres` | The extracted editor-authored state machine. |
| `loco_text.tres` | **Hand-authored** equivalent, written from the design spec with **fresh sub_resource IDs and independent node ordering** (not copy-pasted). This is the thing under test. |
| `verify_animtree.gd` | Headless verifier: loads both, deep-compares structure, sweeps the blend parameter. |

That `loco_text.tres` is genuinely independent (different IDs `aaaa1`/`t0001…`,
different state ordering, different MD5) is confirmed by `diff` — it is not a copy.

Reproduce:

```sh
GODOT=".../Godot_v4.6.3-stable_mono_win64_console.exe"
# (a) project + spike resources load clean
"$GODOT" --headless --editor --quit --path .
# (b) structural + behavioral comparison
"$GODOT" --headless --script res://scenes/spike/verify_animtree.gd --path .
```

## Result — 31/31 checks PASS

- **Loads with no resource errors.** Both `.tres` load as `AnimationNodeStateMachine`;
  full `--editor` project load exits 0 with no error lines referencing the spike files
  (only the benign "missing `.uid` file, re-created from cache" warning, because `.uid`
  sidecars are gitignored).
- **Structurally identical** to the editor baseline:
  - same 6 states (Start, End, Locomotion, Startup, Active, Recovery);
  - matching node types per state (Locomotion = `AnimationNodeBlendSpace1D`, the
    committed-move states = `AnimationNodeAnimation`, Start/End = virtual states);
  - BlendSpace1D: 2 points, `idle`@0.0 → `run`@6.0, `min_space −0.2` / `max_space 6.2`;
  - all 13 transitions present with matching `(from, to)` and `advance_mode`
    (Start→Locomotion = 2/auto, the rest = 1).
- **Blend output (Tier 1):** 25 samples across `[−0.2, 6.2]` — deterministic 1D blend
  weights computed from the point positions are identical between the two trees.

### Why structural identity ⇒ "runs identically"

An `AnimationNodeStateMachine`'s runtime behavior is fully determined by its states,
each node's parameters, and the transition wiring/properties — Godot keeps no hidden
per-resource state. Two state machines equal on all those fields are behaviorally
equivalent. Hence the structural deep-equality is the substantive proof; the Tier-1
weight sweep corroborates the BlendSpace1D specifically.

### What was NOT proven headlessly (Tier 2)

Live `AnimationTree.advance()` + skeleton bone-pose sampling needs a running main-loop
frame pump; a `SceneTree._init()` tool script exits before any `_process()` tick, so a
single-frame advance can't be driven headlessly without a custom `MainLoop` runner +
`DisplayServer`. This was time-boxed out. It is exactly the "confirm the runs-identically
judgment" step the issue earmarks as the **HITL tail** (see below).

> **Superseded (issue #287, 2026-07-23):** the "not proven headlessly" claim above
> no longer holds. `tests/integration/LocomotionClipTest`'s corridor-sweep family
> drives a live `AnimationTree` (`active = true`, `advance(dt)`) and samples
> `Skeleton3D` bone poses per frame **headlessly** — no custom `MainLoop`, no
> `DisplayServer` — by attaching the tree to an ordinary `Node`'s `_PhysicsProcess`
> under `--headless`. Two gotchas made it work: the first `advance(dt)` after
> `active = true` only *primes* at t=0 and swallows `dt` (call `advance(0.0)` then
> `advance(dt)`), and a `SceneTree`-rooted scene runs physics frames without a
> display. Live-blend pose verification is now an ordinary AFK harness capability.

## Gotchas found while text-authoring (the durable value)

1. **`transitions` is a flat interleaved `Array`**, not a typed array:
   `["from","to", SubResource(...), "from","to", SubResource(...), …]` in repeating
   triples. **Highest fragility** — an off-by-one in the number of
   `AnimationNodeStateMachineTransition` sub_resource blocks silently misparses the whole
   array. 13 transitions ⇒ exactly 13 sub_resource blocks.
2. **Default `advance_mode` is `1`, not `0`.** An empty
   `[sub_resource type="AnimationNodeStateMachineTransition"]` deserializes to
   `advance_mode = 1` ("Enabled"). Write `advance_mode = 2` only on the auto-advance edge
   (Start→Locomotion); leave the rest as empty blocks.
3. **`min_space`/`max_space` are 32-bit floats.** `-0.2` reads back as
   `-0.20000000298023`. Compare with `is_equal_approx()`, never string equality.
4. **`Start`/`End` are virtual states.** They appear in `get_node_list()` but resolve to
   `AnimationNodeStartState`/`AnimationNodeEndState`; in `.tres` set only
   `states/Start/position` / `states/End/position`, never a `/node`.
5. **No `uid=` needed in a standalone `.tres`** — `[gd_resource type="…" format=3]`
   without a `uid` loads fine.
6. **GDScript API**: iterate states with `get_node_list()` (returns
   `Array[StringName]`); there is no `get_node_count()`/indexed `get_node_name(i)`.

## Recommendation

Lift ADR-0011's AnimationTree-graph-authoring exclusion and move it to AFK, **gated on
the one-time human visual confirm** below (Done means proven). On confirm, update
ADR-0011 Consequences + CLAUDE.md §3 to drop AnimationTree graph authoring from the HITL
exclusions, keeping the standing scene-edit guardrails (isolated single-concern commit +
headless load check) and the gotchas above as the authoring checklist.

## HITL confirm — DONE (2026-06-28)

The human swapped `loco_text.tres` into `scenes/Player.tscn`'s `AnimationTree.tree_root`,
ran the game, confirmed idle→run and the committed-move states animate identically to the
editor-authored baseline, and reverted the swap. With that, the ADR-0011/CLAUDE.md flip
landed (exclusion lifted) and issue #87 is closed.

### Bonus finding
Loading the hand-authored `loco_text.tres` in the editor round-trips it harmlessly: the
editor adds a `uid://` header and reorders the `sub_resource` blocks, with **no behavioral
change**. So a hand-authored `.tres` is stable under an editor open/save cycle.
