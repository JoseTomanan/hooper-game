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

- `tests/integration/SameSceneReuseProbe.tscn` replaces itself through
  `SceneTree.ChangeSceneToFile` and emits JSONL events with stable synthetic IDs.
- `tools/same_scene_batch_probe.py` launches either three isolated processes or
  one reused process, always gives Godot an explicit workspace-local log, and
  fails closed on malformed, unknown, duplicate, missing, timed-out, abrupt-exit,
  or assertion-failure outcomes.
- `tests/repository/test_same_scene_batch_probe.py` pins that result accounting.

The synthetic case set is exactly `probe-a-first`, `probe-b`, and
`probe-a-second`. It is intentionally not present in `CATALOG`.

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

The clean A→B→A run passed: each scene root had a different instance ID, every
case emitted exactly one result, and all explicit cleanup completed before the
next case observed state.

Then the probe omitted one cleanup operation at the first boundary. Every
mutation made `probe-b` fail, and cleanup after that failure let
`probe-a-second` pass. This proves both sensitivity and recovery rather than a
vacuous all-green path.

| Omitted cleanup | B observation | B exit | Final A |
|---|---|---:|---|
| static C# sentinel | `static C# state leaked` | fail | pass |
| `Input.ActionRelease` | `Input singleton state leaked` | fail | pass |
| cached resource metadata | `ResourceLoader cache mutation leaked` | fail | pass |
| autoload metadata | `autoload state leaked` | fail | pass |
| tree timer callback | `SceneTreeTimer callback leaked` | fail | pass |
| deferred callable | `deferred callback leaked` | fail | pass |
| cached scene-root reference | `cached node reference leaked (native_valid=False)` | fail | pass |

The cached-node result is especially important: freeing the native scene node
does not remove the stale managed reference from a static field. A validity
check can detect that one fixture, but it cannot discover every cache held by
arbitrary harness or production code.

## Result and failure semantics

The parent accounts by selected stable ID, not by process exit alone. A case is
green only after one explicit `result` event with `status=pass`.

| Injected outcome | Observed parent verdict |
|---|---|
| pass | all three IDs present exactly once; success |
| assertion failure | `probe-a-first: intentional assertion failure`; B and final A still recorded |
| timeout | last `start` names `probe-a-first`; all missing IDs and the retained log are reported |
| abrupt exit (23) | active `probe-a-first` named; all missing IDs and retained log reported |
| missing result | `missing result for probe-a-first` |
| duplicate result | `duplicate result for probe-a-first` |

Repository tests separately cover an unknown result ID and malformed/non-object
JSONL. All paths fail closed. Each process record retains its unique `.log` and
`.jsonl` paths under the selected output directory.

## Repeated timing measurement

Baseline revision: `7df6107c0cf163c54946073d0b87e8f0037f56ab`.

Environment: Godot `4.7.1.stable.mono.official.a13da4feb`; Windows
`10.0.19045`; Intel64 Family 6 Model 165 Stepping 3; Python 3.14.0. The exact
case set was the three synthetic IDs above. Isolated mode booted once per ID;
batch mode booted once for the full A→B→A sequence. Both modes used the same
scene, checks, runner, machine, and 15-second per-process timeout. Compilation
and the mutation-control runs preceded this recorded series, so both modes saw
the same warm filesystem/runtime-cache condition that dominates later cases in
a harness shard.

Command shape (run once per mode):

```powershell
python tools/same_scene_batch_probe.py run --godot $godotExe --mode isolated --repeat 7 --output-dir .godot/issue388-measure-final
python tools/same_scene_batch_probe.py run --godot $godotExe --mode batch --repeat 7 --output-dir .godot/issue388-measure-final
```

| Repeat | isolated, 3 boots (s) | batch, 1 boot (s) | saved (s) |
|---:|---:|---:|---:|
| 1 | 1.702035 | 0.564403 | 1.137632 |
| 2 | 1.701667 | 0.567507 | 1.134161 |
| 3 | 1.699876 | 0.567116 | 1.132760 |
| 4 | 1.704180 | 0.566167 | 1.138013 |
| 5 | 1.706436 | 0.567974 | 1.138462 |
| 6 | 1.699266 | 0.567163 | 1.132103 |
| 7 | 1.700760 | 0.567531 | 1.133229 |
| **mean** | **1.702031** | **0.566837** | **1.135194 (66.7%)** |
| **median** | **1.701667** | **0.567163** | **1.134504** |

Total boots were 21 isolated and 7 batched. The measurement characterizes
startup removal for this bounded synthetic workload; it is not a claim that
real harness cases have equivalent run time or reset safety.

## Post-sharding materiality

The latest successful `main` workflow at the decision point was
[run 37087227481](https://github.com/JoseTomanan/hooper-game/actions/runs/37087227481)
on the baseline revision. Its four harness steps took 81–90 seconds, and the
aggregate integration gate completed about 139 seconds after workflow start.
The rendered-evidence job completed about 118 seconds after start, so integration
was again the critical path in that run.

The catalog contains 260 single-process cases across 51 scenes. At the local
probe's roughly 0.568 seconds per avoided warm boot, grouping every case by scene has
a large theoretical ceiling. That extrapolation is intentionally not promoted
to a forecast: CI startup differs, controls and shards constrain grouping, and
none of those 260 cases currently has a proven reusable-process contract. With
the eligible set below, the realizable saving is exactly zero.

## Eligibility decision (fail closed)

**Eligible real catalog IDs: none.**

**Excluded sets:**

- All 260 `topology=single` catalog IDs: every current harness owns its terminal
  `GetTree().Quit` behavior, no ID declares and proves a reset surface, and the
  probe demonstrates seven independent state classes that scene replacement
  does not clear. Sharing a `.tscn` is not evidence of safety.
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
