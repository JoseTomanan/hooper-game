# Spike 0014 — same-scene harness process reuse (#388)

**Status:** NO-GO — keep every catalog case process-isolated. Same-scene identity
is not an eligibility rule, the current 260 single-process cases have no proven
reset declaration, and their scenes terminate the process themselves. Production
batching issue #394 should close `wontfix` under its stated contract unless a new
spike supplies a narrower, independently proven eligibility mechanism.

## Contract and authority boundary

This spike did not modify `tools/harness_catalog.py`, its frozen migration
fixture, CI, or any gameplay assertion. The existing one-`HarnessCase`-per-process
runner remains the only authoritative harness surface (ADR-0015/ADR-0016).

The retained probe is deliberately separate:

- `tests/integration/SameSceneReuseProbe.tscn` embeds the unmodified assertion
  path from `StealFacingMappingTest`, replaces itself through
  `SceneTree.ChangeSceneToFile`, and emits JSONL events under that family's
  stable catalog IDs. Its opt-in completion seam does not change the
  authoritative scene's normal `GetTree().Quit` path.
- `tools/same_scene_batch_probe.py` launches either three isolated processes or
  one reused process, always gives Godot an explicit workspace-local log, and
  fails closed on malformed, unknown, duplicate, missing, timed-out, abrupt-exit,
  or assertion-failure outcomes.
- `tests/repository/test_same_scene_batch_probe.py` pins that result accounting.

The characterized family is exactly
`steal-facing-mapping-test-face-to-face` (A) and
`steal-facing-mapping-test-side-by-side` (B). Both are live catalog IDs for
`StealFacingMappingTest.tscn`; the timing sequence is A→B→A.

## Why scene replacement is not reset

Godot 4.7 documents that a scene change removes the outgoing scene immediately,
frees it at frame end, then adds the new scene; references to the old node become
invalid. It does not say that process-global state is reset. The same docs define
`Input` as a global singleton, `SceneTreeTimer` as tree-owned and automatically
freed only after timeout, and the default resource load mode as reuse of cached
instances. Those are distinct lifetimes and require distinct cleanup contracts.

Sources (all version-matched to the project's Godot 4.7.1 line):

- [SceneTree scene-change order and invalidated references](https://docs.godotengine.org/en/4.7/classes/class_scenetree.html#class-scenetree-method-change-scene-to-file)
- [SceneTreeTimer lifetime](https://docs.godotengine.org/en/4.7/classes/class_scenetree.html#class-scenetree-method-create-timer)
- [Input singleton and action release](https://docs.godotengine.org/en/4.7/classes/class_input.html#class-input-method-action-release)
- [ResourceLoader cache modes](https://docs.godotengine.org/en/4.7/classes/class_resourceloader.html#enum-resourceloader-cachemode)

## Reset-boundary canaries

Authoritative isolated A and B both passed. The prototype then ran A→B, B→A,
and A→A in isolated and reused modes. All six runs agreed, per invocation, on
these gameplay observations—not only the verdict string:

| Catalog case | `ever_loose` | final state | holder | toucher at steal | verdict frame |
|---|---:|---|---:|---:|---:|
| A, face-to-face | true | Held | 1 | 2 | 25 |
| B, side-by-side | true | Held | 1 | 2 | 25 |

Every reused invocation also received a fresh scene-root instance. Repeated A
uses an invocation ordinal alongside the unchanged catalog ID, so exactly-once
accounting can distinguish A(1) from A(2) without inventing a fake case ID.

The probe then injected one cross-boundary mutation after the first real case.
Every mutation made B fail, and cleanup after detection let the final A pass.
This proves sensitivity and recovery rather than a vacuous all-green path.

| Injected boundary mutation | B observation | B exit | Final A |
|---|---|---:|---|
| static C# sentinel | `static C# state leaked` | fail | pass |
| unreleased input action | `Input singleton state leaked`; gameplay ends Dribbling instead of Held | fail | pass |
| cached resource metadata | `ResourceLoader cache mutation leaked` | fail | pass |
| autoload metadata | `autoload state leaked` | fail | pass |
| tree timer callback | `SceneTreeTimer callback leaked` | fail | pass |
| deferred callable | `deferred callback leaked` | fail | pass |
| cached scene-root reference | `cached node reference leaked (native_valid=False)` | fail | pass |

The cached-node result is especially important: freeing the native scene node
does not remove the stale managed reference from a static field. A validity
check can detect that one fixture, but it cannot discover every cache held by
arbitrary harness or production code.

Timers and deferred calls are characterization-only exclusions, not claimed
reset successes. The clean protocol schedules neither. Their mutation runs
prove that already-pending tree-owned work crosses scene replacement; the probe
has no general cancellation handle for arbitrary harness work. Any case that
can leave either pending is therefore excluded rather than "cleaned" by
zeroing the probe's observation counter.

## Result and failure semantics

The parent accounts by ordered `(stable catalog ID, invocation)` pairs, not by
process exit alone. A case is green only after its matching `start` followed by
exactly one explicit `result` with `status=pass`. Overlapping or reordered
starts, results without an active matching start, and stale prior event files
all fail closed; the parent removes both deterministic event and log paths
before launch.

| Injected outcome | Observed parent verdict |
|---|---|
| pass | A(1), B(2), and A(3) each start and pass once |
| assertion failure | A invocation 1 names `intentional assertion failure`; B and final A still record |
| timeout | active A invocation 1 and all missing invocations are named; log retained |
| abrupt exit (23) | active A invocation 1 and all missing invocations are named; log retained |
| missing result | A(1) remains active; later starts/results are quarantined and every missing invocation is named |
| duplicate result | `duplicate result for ...face-to-face invocation 1` |

Repository tests separately cover an unknown result ID and malformed/non-object
JSONL. All paths fail closed. Each process record retains its unique `.log` and
`.jsonl` paths under the selected output directory.

## Repeated timing measurement

Baseline revision: `7df6107c0cf163c54946073d0b87e8f0037f56ab`.

Environment: Godot `4.7.1.stable.mono.official.a13da4feb`; Windows
`10.0.19045`; Intel64 Family 6 Model 165 Stepping 3; Python 3.14.0. The exact
case sequence was A→B→A from the real family above. Isolated mode booted once per invocation;
batch mode booted once for the full A→B→A sequence. Both modes used the same
scene, checks, runner, machine, and 15-second per-process timeout. Compilation
and the mutation-control runs preceded this recorded series, so both modes saw
the same warm filesystem/runtime-cache condition that dominates later cases in
a harness shard.

Command shape (run once per mode):

```powershell
python tools/same_scene_batch_probe.py run --godot $godotExe --mode isolated --repeat 7 --output-dir .godot/issue388-facing-measure
python tools/same_scene_batch_probe.py run --godot $godotExe --mode batch --repeat 7 --output-dir .godot/issue388-facing-measure
```

| Repeat | isolated, 3 boots (s) | batch, 1 boot (s) | saved (s) |
|---:|---:|---:|---:|
| 1 | 3.530499 | 1.783066 | 1.747434 |
| 2 | 4.142204 | 1.785558 | 2.356646 |
| 3 | 3.235944 | 1.873110 | 1.362834 |
| 4 | 3.033233 | 1.872064 | 1.161170 |
| 5 | 3.130011 | 1.873330 | 1.256681 |
| 6 | 3.157690 | 1.871022 | 1.286668 |
| 7 | 3.123392 | 1.782278 | 1.341114 |
| **mean** | **3.336139** | **1.834347** | **1.501792 (45.0%)** |
| **median** | **3.157690** | **1.871022** | **1.286668** |

Total boots were 21 isolated and 7 batched. The measurement characterizes
startup removal for this bounded real harness family; it is not a claim that
uncharacterized harness cases have equivalent run time or reset safety.

## Post-sharding materiality

The latest successful `main` workflow at the decision point was
[run 37087227481](https://github.com/JoseTomanan/hooper-game/actions/runs/37087227481)
on the baseline revision. Its four harness steps took 81–90 seconds, and the
aggregate integration gate completed about 139 seconds after workflow start.
The rendered-evidence job completed about 118 seconds after start, so integration
was again the critical path in that run.

The catalog contains 260 single-process cases across 51 scenes. At the local
probe's roughly 0.751 seconds per avoided warm boot, grouping every case by scene has
a large theoretical ceiling. That extrapolation is intentionally not promoted
to a forecast: CI startup differs, controls and shards constrain grouping, and
none of those 260 cases currently declares an authoritative reusable-process
contract. With the eligible set below, the realizable saving is exactly zero.

## Eligibility decision (fail closed)

**Characterized as reuse-safe inside the bounded probe:** the A/B
`StealFacingMappingTest` family above. Their isolated and reused observations
match in every required order, and every injected leak is discriminated.

**Proposed production-eligible IDs: none.** Moving only this two-case family to
production would avoid one boot—about 0.75 seconds locally—which is not material
against a 139-second post-sharding integration gate. Generalizing beyond it
would require per-family completion/reset refactors and proof not supplied here.

**Excluded sets:**

- The two characterized IDs: excluded from production on immaterial benefit;
  their probe-only completion seam is not an authoritative batching contract.
- The other 258 `topology=single` catalog IDs: no ID declares and proves a reset
  surface, and the probe demonstrates seven independent state classes that
  scene replacement does not clear. Cases with pending tree timers or deferred
  work are categorically excluded. Sharing a `.tscn` is not evidence of safety.
- All 13 `topology=multiprocess` catalog IDs: their server/client topology and
  per-role crash/timeout isolation are incompatible with this single-process,
  same-scene prototype.

Eligibility is fail closed: a future case is isolated unless an authoritative
design supplies an explicit reset declaration, adversarial leak controls, and
crash/timeout containment without weakening its assertions. Static source
inspection or matching scene paths alone cannot opt a case in.

## Verdict

**NO-GO for #394's production batching change.** The speed signal is real and
material enough to revisit only if the harness architecture later adopts a
cooperative case lifecycle with independently testable reset ownership. Today,
implementing batching would require changing existing harness termination and
inventing per-case safety declarations; that exceeds this spike and creates a
credible false-green path. The isolated fallback is therefore not merely a
fallback—it remains the sole execution mode.

No editor or feel verification is required. This is harness/tooling evidence,
not gameplay or presentation judgment.
