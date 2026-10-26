# Slow travel through ordinary heading turns

- Issue: [#430](https://github.com/JoseTomanan/hooper-game/issues/430)
- Decisions: ADR-0003 and ADR-0010, scoped amendments dated 2026-10-25
- Production changes: `f72e10a` (motion), `aabdcb4` (stepping contact),
  `6ada504` / `0582d96` (continuous limb solves and complete neutral handoff)
- Baseline: `37904cc`; Godot 4.7.1 Mono / Godot.NET.Sdk 4.7.1 / .NET 8
- Measured 2026-10-25/26. Final appearance and speed acceptance remain #184/#173;
  tuning remains #238. This report supplies objective evidence for that recheck.

## Method and reproduction

`PivotPlantTest` retains the production-input flick, held, boundary and committed
preemption controls. Its `movement-matrix` calls the real fixed-step
`PlayerController.Move`, comparing every heading/latch sample with the unchanged
`HeadingMath.Step`. It measures eight angles, three locomotion modes, standing
and incoming momentum, and held/released input: 96 cases. Separate transitions
exercise stance, post and translation locks; the production raw-opponent lookup
and contact solver have an identical clear-space control.

Reconciliation proof uses real `ReceiveState -> ReconcileFromServer`, retained
input histories, acknowledged-prefix pruning and deliberately corrupted local
position, velocity, heading and latch. Four pace histories (ordinary, sprint,
protection, and sprint -> protection -> ordinary), four contexts and four
acknowledgment points give 64 trajectories. Fully acknowledged snapshots also
check the empty-buffer path. This is deterministic replay proof, not a claim of
historical opponent reconstruction: replay still uses the latest raw opponent
snapshot under the existing contact contract.

`NetSprintTest --harness-scenario=moving-turn` additionally uses real scoped ENet
peers, guarded input RPC warmup and `TickClientOwnPlayer`. Forty actual Input
prediction ticks record changing pace, retargeting and release. Each predicted
state and the subsequent forced replay are compared with independently scripted
authority steps. The current ordinary mode cannot substitute for historical
sprint/protection inputs and pass this comparison.

`run_moving_turn_measurements.py` repeats 45, 90, 91, 120, 135, 170, 179.9 and
180 degrees in the real Main scene: idle, running, left-hand dribble and
right-hand dribble, held and flick, three repetitions each. The fixture uses
production Input actions, a real crossover for the left hand, and the final
`SkeletonUpdated` pose. It records the vector actually consumed by
`Input.GetVector`; the exact-90 control uses the opposite cardinal quarter-turn
to avoid a float wrap one ULP above the strict boundary. Setup resets occur only
between trials. Local headless fixed-fps captures make no rendered or real-time
performance claim.

The separate #416 native matrix repeats idle/run/both hands, held/flick and
forward controls locally and on a genuine buffered remote observer. Each has
three cycles. The unchanged entry/exit whole-pose guards cover nine effectors
(0.39 m per source tick) and all 65 bone rotations (50 degrees per source tick),
including release and two source intervals after actual settled relinquishment.
These thresholds come from the original
forward-locomotion control, not the repaired turn. Remote trials require coherent
source ticks, boundary images, holds and unchanged presentation epoch/resync;
owner reset and protection comparisons remain separate controls.

```powershell
dotnet build 'HOOPER GAME.csproj' --configuration Debug -p:IncludeIntegrationHarness=true
dotnet test tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj --configuration Debug
python tools/run_moving_turn_measurements.py --godot "$env:GODOT" --output .godot/430-angles-recheck
python tools/run_pivot_regression.py --godot "$env:GODOT" --rendered --resolution 480x270 --local-render-delay-ms 40 --output .godot/430-native-recheck --port 25501
python tools/harness_catalog.py run --all --godot "$env:GODOT" --bash 'C:\Program Files\Git\bin\bash.exe' --run-id 430-recheck
```

Use fresh output directories. Native attempts at both 960x540 and 480x270 could
outrun the bounded PNG-writer queue, including a serial attempt. The final local
captures give image writing 40 ms of wall time per frame while retaining fixed
1/60 simulation delta; this is recorded in each manifest. Remote captures retain
real-time timing with no injected delay. Failed/partial captures and the earlier
prototype matrix are excluded from passing evidence.

The final native matrix retains nine local 960x540 captures and the first four
remote 960x540 pairs. The remote left-flick trial's cycle2 had a rendered gap
greater than two physics ticks, despite passing all pose/contact bounds; that
trial is excluded. Its targeted retry and the remaining four remote cases use
480x270 with unchanged guards. The trial ledger records this mixed-resolution
coverage explicitly. Both before/after gameplay clips use valid 960x540 trials.

## Policy and preconditions

Ordinary heading remains 900 degrees/s with factor 0.95 at a half-turn, strict
`>90` latch creation, held retargeting and released completion. Sprint retains
its existing rate multiplier. The turn target speed is
`modeSpeed * (0.65 - 0.30 * clamp(abs(remainingYaw) / pi, 0, 1))`.
Thus the target rises from 35% to 65% of the existing mode speed. These are
bounded implementation defaults authorized by the requested slow interval,
not measured basketball speeds or human-accepted tuning.

At 60 Hz each velocity step uses 45 m/s^2 acceleration, or 70 m/s^2 braking
while opposing the requested direction, shedding excess speed or releasing.
The choice applies to the entire tick: a braking tick may cross zero toward
the target. The target is bounded; incoming momentum is not hard-clamped to
65%. Release targets zero, without latch-driven travel. Completion resumes
ordinary acceleration on the same tick. Stance still waits through completion;
post retains finite entry braking. Translation locks and production body contact
remain in the shared motion path. No persistent simulation or wire field is added.

The entry bridge remains six source ticks and landing remains four; the exit
bridge now takes seven source ticks. Moving
turns place the next foot under the displayed body with 0.10 s velocity lead,
0.16 m lateral offset and a 0.10 m swing arc over eight source-tick intervals.
The support toe stays fixed within each stance; the next foot lands before
support transfers. Both legs share one reachable pelvis correction before either
leg solve. Each solve starts from both legs' last displayed world orientations,
preserving the previous bend and twist rather than letting a freshly blended
knee switch the IK branch. Current target foot orientations remain placement
goals. Remote holds do not advance these clocks. A delayed first movement
starts its swing from the last displayed feet; a stopped/released body does not
command new transfers.

The replacement contact proof independently measures actual toes against world
targets within 2 mm, grounded stance duration, the incoming support toe on the
preceding frame, and the completed swing footprint moving with body travel.
It replaces only the obsolete requirement to pin one toe for an entire moving
turn. Fixed stance contact and released controls remain. Sixteen reach-sphere
projection iterations are a bounded numerical solve, not a proof for arbitrary
rigs or terrain; the shipped rig and flat Main court are preconditions.

The expanded angle proof initially failed 11 of 64 captures: running held/flick
at 91, 120, 135, 170 and 179.9 degrees, plus the left-held 91-degree exit.
All repetitions reproduced the failures, including a 179.81-degree one-tick knee
rotation, despite passing foot-contact targets. The historical 180-degree driver
turns in the opposite direction from the positive near-180 probes, explaining
why it missed them. Seeding prior limb orientations removed the active flips.
Lengthening the exit to eight or ten intervals merely moved some failures; a
bend-circle prototype passed analytic unit tests but did not remove full-scene
joint twist. Those prototypes are discarded.

The final repair removes duplicate leg IK from the generic pivot bridge. Its
arm endpoint solves remain; dedicated `StepFeet`/`Plant` still solve leg contact
during the turn. The unconstrained exit uses the existing continuous global
bone-rotation blend. Minimal vertical pelvis clearance prevents a toe from
arcing below the flat court during that blend. The exit chooses its quaternion
hemisphere on the first nonzero blend sample; the zero-weight sample may still
contain the departing Pivot animation. This removes a measured 52-degree/tick
long-arc exit without changing the heading clock.

A wider run then found 16 failing captures. Both dribble hands' raw destination
toes reached approximately -0.216 m after the fixed exit window. Returning to
the raw tree on release also produced 51-84-degree limb steps. The repair now
retains authored endpoint rotation blending and minimal floor clearance through
travelling neutral, and uses a six-tick bridge from the last displayed pose when
travel slows. It can re-arm after resumed movement. Generic limb IK is excluded
from that neutral interpolation to avoid choosing a new limb branch. Protect,
post, stance, committed moves and rebound poses retain their own ownership.
Source holds retain the final exit pose and do not advance settling; reset clears
the handoff. Relinquishment requires stopped travel and a floor-safe endpoint.

The separate running-held toe-tip failures (up to 0.395475 m per tick) were
ordinary rotation arcs plus body travel, rather than knee flips. Seven exit
intervals distribute that displacement within the original forward-control
envelope. Entry remains six intervals. Both toes, all bone rotations and nine
effectors are checked through release, actual settled relinquishment and two
subsequent source intervals, retaining the original 0.39 m / 50-degree limits.
Shared `CrossoverPoseBridge` code and other callers are unchanged.

## Mechanical timing and discriminating failures

The continuous 180-degree reference integrates the unchanged rate schedule:
`-pi / (r * (1-f)) * ln(f) = 0.205173 s`, with `r=900*pi/180` and `f=0.95`.
Production reaches the target on step 13, or 0.216667 s. The difference is below
one 60 Hz step (0.016667 s); per-angle baseline equality is checked every tick.
Twelve true-latch samples, thirteen completion steps, AnimationTree playback and
the later seven-interval pose handoff are different measurements.

| Angle | Idle held completion steps | Path while latch is true (m) |
|---|---:|---:|
| 45 / 90 | no latch | ordinary movement unchanged |
| 91 | 7 | 0.251727 |
| 120 | 9 | 0.368584 |
| 135 | 10 | 0.422830 |
| 170 | 12 | 0.513414 |
| 179.9 | 13 | 0.563975 |
| 180 | 13 | 0.563820 |

These are direct Move matrix measurements from rest at ordinary 6 m/s mode
speed. The 2 cm movement threshold exceeds approximately micrometre solver
noise by four orders of magnitude, while remaining below a real reduced-speed
step. Ordinary 180-degree released input moves 0.0125 m from rest and then
stops; incoming 6 m/s motion brakes finitely rather than disappearing.

The real-scene 180-degree captures separate those motion/animation endpoints:

| Entry / input | Active path (m) | Peak active speed (m/s) | Heading complete | Full speed | Stopped | Pose relinquished |
|---|---:|---:|---:|---:|---:|---:|
| Idle, held | 0.563819 | 3.8497 | 13 | 15 | 31 | 33 |
| Running, held | 0.535039 | 4.8333 | 13 | 15 | 31 | 33 |
| Either dribble hand, held | 0.563820 | 3.8497 | 13 | 15 | 31 | 33 |
| Idle / either hand, flick | 0.012500 | 0.7500 | 13 | — | 2 | 22 |
| Running, flick | 0.208334 | 4.8333 | 13 | — | 6 | 22 |

Tick columns count consumed 60 Hz intervals from the real pre-input sample;
divide by 60 for seconds. Held input releases at stageAge25 (interval26), flick
at stageAge1 (interval2). Full speed means the first sample at least 5.9 m/s.
The animation has twelve Pivot samples, from playback0 to0.166667 s, while the
heading completes at 0.216667 s and the final handoff settles later. Incoming
momentum explains the running peak above the reduced target; it is finite
braking, not an instantaneous full-speed reverse command.

All 64 angle/context/input captures (192 cycles) pass the expanded pose guard.
Worst actual toe-target error is 0.000101 m (rounded upward), below the 2 mm
contact tolerance; worst measured effector step is 0.350578 m, below 0.39 m.
Additional real Input sequences release at 25, resume the exact original wish
at 28 before stopping, then release again at 40, checking both finite stops and
that the second handoff also relinquishes neutral without a pose pop.
All twelve resumed-travel cycles stop at stageAge45 and relinquish at 47;
their worst step is 0.317358 m and worst rotation 48.166368 degrees per source
tick. The source-byte restoration record and mutation failures are retained in
[mutation results](evidence/0430/mutation-results.json) and
[restored hashes](evidence/0430/mutation-restoration.json).

Three actual temporary mutations were built and exercised, then restored by
exact source-byte hashes and a clean rebuild:

| Mutation | Discriminating result |
|---|---|
| Restore old `Velocity = Vector3.Zero` turn gate | 91-degree idle held case fails: zero active displacement |
| Target full mode speed throughout turn | 91-degree case fails the reduced-speed envelope at tick 5 |
| Freeze swing destination at its starting footprint | Real capture completes, verifier fails actual footprint relocation and contact |

The disabled-`PivotPose` controls also remain mandatory in the full continuity
matrix. A process exit or completed capture alone is never counted as a pass.

## Gameplay-distance footage and evidence

The four clips retain the full gameplay viewport and normal source-time cadence:

| View | Before: old zero-velocity gate | After: slow travel and handoff |
|---|---|---|
| Local | [Before local](evidence/0430/before-local.gif) | [After local](evidence/0430/after-local.gif) |
| Buffered remote observer | [Before remote](evidence/0430/before-remote.gif) | [After remote](evidence/0430/after-remote.gif) |

The before pair is a controlled old-gate counterfactual at `aabdcb4`: only the
ordinary-turn velocity assignment was restored to zero, with exact source-byte
restoration checked afterward. It isolates the stationary-then-move behavior;
it is not presented as an untouched historical checkout. A separate local
capture of unmodified `37904cc` supplied the
[historical local baseline](evidence/0430/historical-local.gif). After clips use
the final repaired candidate. The frame index records exact source PNG hashes,
physics/source ticks, camera, resolution and compressed original traces. No
camera zoom, pose reconstruction or frame interpolation is used.

Local rendering uses fixed 60 Hz simulation with 40 ms wall delay for image
compression; GIF durations restore normal simulation speed. Remote rendering
uses actual real-time peers without injected pacing. These are presentation
evidence, not performance benchmarks or a human feel acceptance.

## Validation record

- Game build with integration harness both included and excluded.
- Unit suite: 1,288 passed, five intentionally skipped characterization cases.
- Repository contracts: 106 tests, one skip; canonical skill generation check.
- [Angle matrix](evidence/0430/angle-matrix-summary.json): 64 captures / 192
  cycles, all passing. Captured before the repair commit was created, so its
  original label is retained alongside the compiled-production provenance.
- [Native matrix](evidence/0430/native-matrix-summary.json): all 27 predefined
  role reports pass, with two actual disabled-modifier negatives, posture/history
  comparisons and four resumed-travel cases. Maximum step 0.382944721 m per
  source tick, rotation 49.4198229 degrees, contact error 0.000008684 m (rounded
  upward). Owner-history reset compares 20,280 bone pairs and protection compares
  975, with zero world-position error.
- [Resumed travel](evidence/0430/resumed-travel-summary.json) and the three
  mutation controls all pass their discriminating criteria. Exact source-byte
  restoration and clean rebuild/movement-matrix pass afterward.

The catalog contains 455 cases, including the expanded movement/prediction
scenarios and the complete angle capture composite. CI runs the exhaustive four
shards and a separate rendered-pivot job. The commands above reproduce the full
catalog; this report does not substitute a compile or selected unit tests for
those checks.

## Source grounding

The existing engine contracts remain the implementation boundary:
[CharacterBody3D.MoveAndSlide](https://docs.godotengine.org/en/4.7/classes/class_characterbody3d.html#class-characterbody3d-method-move-and-slide)
consumes velocity in physics steps;
[Vector3.MoveToward](https://docs.godotengine.org/en/4.7/classes/class_vector3.html#class-vector3-method-move-toward)
clamps the bounded velocity change at its target;
[SkeletonModifier3D](https://docs.godotengine.org/en/4.7/classes/class_skeletonmodifier3d.html)
runs after animation evaluation, and
[Skeleton3D.SkeletonUpdated](https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-signal-skeleton-updated)
provides the final evaluated pose used in measurements.
The capture driver uses documented
[Input.ActionPress](https://docs.godotengine.org/en/4.7/classes/class_input.html#class-input-method-action-press)
and [fixed-fps command-line semantics](https://docs.godotengine.org/en/4.7/tutorials/editor/command_line_tutorial.html).

## Human recheck; no tuning prescription

No editor wiring is required. The historical stationary-turn / 0.35-second
checklist is superseded for ordinary movement-stick turns. Review slow travel,
stepping/weight transfer, the visible pelvis assistance and the handoff into
locomotion in #184/#173. Objective continuity limits do not establish visual
quality or speed preference; those issues remain open, with tuning in #238.
