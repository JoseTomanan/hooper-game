# StepBack animation harness primitive extraction

- Issue: [#390](https://github.com/JoseTomanan/hooper-game/issues/390)
- Related decisions: ADR-0011, ADR-0016
- Date: 2026-10-05–06
- Baseline: `447302999c031aba05ad0be2ac68d1f77723906a`

This records behavior and diagnostic parity for the bounded StepBack extraction.
It provides evidence for the refactor, without making animation or feel decisions.

## Method and preconditions

Run all eight catalog entries for `StepBackAnimTest.tscn` before and after the
extraction using Godot `4.7.1.stable.mono.official.a13da4feb` and the Debug game
assembly. Compare every line beginning `[stepback-anim]` in the native logs,
including observations, per-state/per-clip diagnostics, verdicts, and results.
The physics rate is 60 ticks/second. StepBack frame data, resources, scenes,
tolerances, argument parsing, and timeout remain unchanged.

Baseline command (PowerShell, with `$taskGodot` resolved from `GODOT`):

```powershell
python tools/harness_catalog.py run --scene StepBackAnimTest.tscn --run-id issue390-before --godot $taskGodot
```

Native logs are local artifacts beneath `.godot/harness-runs/issue390-before/`.
They are not committed. Outcomes are determined by the real Godot process exit
code as well as the reported verdict. Existing root-certificate-store warnings
occur in the baseline and are outside this extraction.

## Pre-extraction inventory

StepBack contains 921 lines, 18 private methods, and eight catalog scenarios.
Two standalone mechanical method bodies are candidates for removal:
`FindSkeleton` (recursive discovery) and `LoadStateMachine` (serialized scene
inspection). `Fail` and `Finish` mix mechanical formatting with caller-owned
lifecycle; only formatting/result transport can move. Library/clip lookup,
duration observation/conversion, and assignment inspection are inline mechanics.

| Scenario | Local proof that must remain | Baseline |
|---|---|---|
| `stepback-phases` | Observe Startup, Active, Recovery in order; preserve existing placeholder observation diagnostics | PASS, ticks 6/4/8 |
| `stepback-no-placeholder-leak` | Explicit three state-to-clip mappings; caller-specified idle/run placeholders | PASS |
| `stepback-segment-lengths` | 7/4/8 frame windows at actual tick rate; seconds-domain deviation greater than `1e-3` fails | PASS, deviations 0.000000 s |
| `stepback-edges` | All 12 required graph edges, including doubled dribble entries/exits | PASS |
| `stepback-startup-differs-from-recovery` | Last valid Startup/Recovery poses; local bones, quaternion math, 15-degree floor | PASS, 17.41 degrees |
| `stepback-active-displaces-back` | Last Startup to last Active trail-foot-relative displacement | PASS, -0.1457 m |
| `control-stepback-startup-displaces-forward` | Startup establishes its own opposite-sign displacement from first valid to last Startup tick | PASS, +0.0165 m |
| `stepback-recovery-hands-off-to-jumpshot` | Independent local FK, hips-relative landmarks, 0.45 m ceiling | PASS, worst 0.3483 m |

The control establishes its premise independently: Startup travel must exceed
+0.01 m and has its own valid multi-tick sampling requirements. Active uses its
own phase endpoints and requires travel below -0.02 m. Neither verdict is inferred
from the other. Bone sampling, NaN handling, pose deltas, transition assertions,
phase observation, and handoff calculations are outside the shared boundary.

## Source verification

The helpers use the version-specific official Godot documentation:

- [Node children traversal](https://docs.godotengine.org/en/4.7/classes/class_node.html#class-node-method-get-children)
- [Serialized scene inspection](https://docs.godotengine.org/en/4.7/classes/class_scenestate.html#class-scenestate-method-get-node-property-value)
- [Animation library lookup](https://docs.godotengine.org/en/4.7/classes/class_animationlibrary.html#class-animationlibrary-method-get-animation)
- [State-machine node inspection](https://docs.godotengine.org/en/4.7/classes/class_animationnodestatemachine.html#class-animationnodestatemachine-method-get-node)
- [Assigned animation name](https://docs.godotengine.org/en/4.7/classes/class_animationnodeanimation.html#class-animationnodeanimation-property-animation)
- [Clip duration in seconds](https://docs.godotengine.org/en/4.7/classes/class_animation.html#class-animation-property-length)
- [Process exit result](https://docs.godotengine.org/en/4.7/classes/class_scenetree.html#class-scenetree-method-quit)
- [Resource existence before loading](https://docs.godotengine.org/en/4.7/classes/class_resourceloader.html#class-resourceloader-method-exists)
- [Packing owned fixture nodes](https://docs.godotengine.org/en/4.7/classes/class_packedscene.html#class-packedscene-method-pack)

Context7 was unavailable in this session. Official web documentation and the
pinned 4.7.1 C# compile/runtime surfaces verify the engine calls instead.

## Post-extraction evidence

All eight scenarios pass after extraction. Comparing the native log lines with
the `[stepback-anim]` prefix produces exact equality: eight log files and 56
diagnostic lines. This includes observed ticks, numeric values, graph mappings,
per-clip lengths, verdict explanations, and exit-result strings. Post-extraction
logs are beneath `.godot/harness-runs/issue390-after/`.

| Affected surface | Before | After |
|---|---:|---:|
| StepBack source lines | 921 | 919 |
| StepBack private methods | 18 | 18 |
| Local recursive/serialized discovery bodies | 2 | 0 |
| Local failure/result formatting bodies | 2 | 0 |
| Extracted helper files / lines | 0 / 0 | 3 / 135 |
| Existing StepBack catalog scenarios | 8 | 8 |
| New helper characterization scenarios | 0 | 3 |

Method counts exclude fields, properties, constructors, and public lifecycle
callbacks. Discovery and reporting wrappers remain in StepBack; their reusable
mechanics now live in helpers. The complete source grows because the extraction
also adds explicit missing-resource handling and independently tested helpers.
The two-line StepBack reduction is not the evidence of correctness.

The primary-agent diff review confirmed that the pose-sampling and FK methods,
phase observation, all four live verdicts, required graph edges, frame windows,
tolerances, control premises, timeouts, argument parser, and exit codes retain
their baseline contracts. Matching assignment text now also requires an actual
named animation resource. This is an explicit issue requirement, rather than a
new animation expectation.

An independent comparison of 13 lifecycle, observation, verdict, control,
geometry, and edge methods confirmed exact source equality after line-ending
normalization. The three static scenarios with migrated inline mechanics were
also reviewed directly against the baseline diff.

Focused pure tests cover caller identity, supplied failure details, nonzero exit
formatting, non-rounded tick conversions at 120 Hz, invalid rates, and absolute
deviation both below and above a nonzero expected duration. An adversarial review
found the original deviation test's zero-only expectation would not catch a
signed-difference implementation. With the added below-expected observation,
removing `Math.Abs` produced a real assertion failure (expected +0.003 s, observed
-0.003 s); restoring it passed all seven focused tests.

Three new runtime catalog cases characterize nested/root skeleton discovery,
nested animation-tree discovery, serialized state-machine discovery, wrong and
missing resources, named animation identity, observed seconds, and explicit
state-to-clip checks. The synthetic mapping intentionally separates qualified
clip names from library-local names. Correct, wrong genuine, placeholder,
unassociated, missing-resource, missing-state, and non-animation-state results
are tested distinctly. Even an expectation naming a placeholder or a nonexistent
clip must fail. The reporting failure probe prints FAIL and exits 7; it is not
registered as a successful catalog case.

Both game build modes pass with zero warnings/errors. The full unit suite passes
1,155 tests, with five intentionally skipped characterization theories. All 60
repository tests pass, including validation of the 273-entry frozen catalog
baseline and the three appended cases. Repository subprocess fixtures required
unsandboxed execution; sandboxed attempts failed on temporary-directory access.

## Mutation and failure-parity evidence

All nine deliberate implementation mutations compiled successfully and were
rejected by the specified check. Runtime mutation checks returned exit 1 and a
concrete FAIL diagnostic; the reporting unit mutation failed an actual xUnit
assertion. Compilation failure and interrupted processes were not counted.

| Mutation | Discriminating check | Observed rejection |
|---|---|---|
| Format a nonzero result as PASS | Pure reporting contract | Expected FAIL versus actual PASS; unit exit 1 |
| Quit with 0 after a reported failure | Intentional reporting failure probe, external exit assertion | Process exit 0 contradicts required exit 7 |
| Fabricate a skeleton when absent | `primitives-discovery` | Absent skeleton silently accepted; exit 1 |
| Fabricate a named animation when absent | `primitives-resources`, `primitives-state-clips` | Missing animation accepted / MissingClip classification lost; exits 1 |
| Add one tick to observed duration | `primitives-resources`, `stepback-segment-lengths` | Observed seconds changed / 0.016667 s deviation exceeds 0.001 s; exits 1 |
| Return Correct for a wrong genuine clip | `primitives-state-clips` | Actual Correct versus expected WrongClip; exit 1 |
| Return Correct for a placeholder | `primitives-state-clips` | Actual Correct versus expected Placeholder; exit 1 |
| Return Correct for a missing state | `primitives-state-clips` | Missing state not distinguished; exit 1 |
| Return Correct for a missing clip | `primitives-state-clips` | Actual Correct versus expected MissingClip; exit 1 |

The separate signed-deviation mutation is recorded above. After restoration,
source bytes matched the committed implementation; the game build, seven focused
unit tests, three helper scenarios, and eight StepBack scenarios passed. The
restored intentional failure probe exited 7.

Four failure probes ran against both the pre-extraction StepBack source and the
extracted implementation. Wrong genuine and placeholder probes changed the
**actual state assignment in memory**, preserving StepBack's expected mapping.
The duration probe increased only its expected Startup window by one tick. No
production scene or animation resource file was edited.

| Failure probe | Prefix diagnostic lines in each version | Before / after exit | Exact native-log parity |
|---|---:|---|---|
| Unknown scenario | 3 | 1 / 1 | Equal |
| Actual wrong genuine clip | 7 | 1 / 1 | Equal |
| Actual placeholder clip | 7 | 1 / 1 | Equal |
| One-tick expected duration change | 7 | 1 / 1 | Equal |

The primary agent checked the actual mutation diagnostics and compared the native
UTF-8 log lines independently. Local evidence is beneath
`.godot/issue390-mutations/`, including `evidence.json`, per-mutation build/test
logs, native scenario logs, and the restoration results. The temporary mutation
runner is not committed.

## Complete harness

On 2026-10-06, the complete catalog sweep passed **276 / 276 cases**, with
process exit 0 and the final summary `RESULT: 276 passed / 276 run`. The primary
agent checked all 276 RUN/PASS markers and the aggregate result; no failure,
timeout, or crash markers appeared. No cases were retried to obtain this result.
This includes all eight StepBack cases, three helper characterizations, and the
existing multiprocess journeys.

Reproduce from the repository root in PowerShell:

```powershell
$taskGodot = [Environment]::GetEnvironmentVariable('GODOT', 'User')
python -u tools/harness_catalog.py run --all --run-id issue390-full-resume-oct06 --godot $taskGodot --bash 'C:/Program Files/Git/bin/bash.exe' *> .godot/issue390-full-resume-oct06.txt
```

The full console log is `.godot/issue390-full-resume-oct06.txt`; native direct-case
logs are under `.godot/harness-runs/issue390-full-resume-oct06/`, and multiprocess
adapter logs remain under `.godot/harness-logs/`. These are gitignored local
artifacts. The sweep ran unsandboxed to allow child-process temporary-directory
access, using the same pinned Godot 4.7.1 engine. No builds or source changes ran
concurrently. The earlier interrupted sweep has no complete summary and is not
counted as evidence.

## Interpretation

These measurements describe a refactor's proof boundaries. They do not propose
clip retuning, new pose thresholds, or any feel judgment. No editor steps are
required for this harness-only change.
