# Deterministic contact kernel proof

- Issue: [#369](https://github.com/JoseTomanan/hooper-game/issues/369)
- Authority: ADR-0025 decisions 2–6
- Date: 2026-10-10

## Purpose and method

`ContactMath.Resolve` is a pure kernel; production contact remains solver-owned
until the dependent integration issue lands. `ContactKernelTest` instances
#356's `ContactHarnessSeam` and measures the real scene's capsule resources.
On each real physics callback it prescribes both proposed velocities, freezes
both snapshots, evaluates the kernel, then applies the two corrected endpoints.
It does **not** call `Move`, `TickCommittedMoveBehavior`, or `MoveAndSlide`.
This is kernel-adapter evidence, not production wiring or network proof.

The ordinary drive starts at Z=3 m against a stationary opponent at the origin.
The attacker attempts 6 m/s for 30 ticks at 60 Hz. The unset trial changes only
the opponent's authoritative heading by 180°. The disabled-kernel control
applies zero corrections and must fail the same absorption gate while reaching
the independently predicted free endpoint at the origin. Collision masks and
the shipped scene are unchanged; direct adapter movement does not ask Jolt to
resolve contact.

The one-tick crossing trial attempts 1200 m/s, traversing 20 m. Stationary
overlap and coincident-center trials require full separation without invented
velocity. The dimensions trial gives each fixture body a separate capsule
resource (radii 0.25/0.6 m, heights 1.2/2.4 m) and reads those values back.
Every enabled tick checks finite endpoints, separation (0.0002 m tolerance),
exact repeated-call equality, and exact role-reversed correction equality.
The xUnit suite covers additional predicate, sweep, cap, and sequence cases.

## Supported contract and ordering

Snapshots describe upright, horizontally centered capsules with arbitrary
positive radius, total height at least twice radius, and a Y center offset.
The effective horizontal contact radius includes the vertical distance between
the two capsule spine segments, so vertically disjoint bodies do not create
phantom contact. Motion is XZ only: nonzero vertical velocity is rejected.
This matches the shipped flat-court movement; this kernel does not implement
airborne or tilted-capsule contact.

Each player has a distinct stable peer ID. At exactly coincident horizontal
centers the lower ID gets the +X outward normal; the other gets −X. This explicit
tie rule promises role reversal, not rotational invariance at the degenerate
point. Null opponent returns zero correction, including at the origin.

Callers must capture both players before either is advanced. Evaluate once for
the pair, or evaluate reversed roles against those identical immutable
snapshots and apply only the caller's correction. Reading an opponent after
moving the first body invalidates that ordering promise. Client integration
must obtain raw last-broadcast state rather than display-smoothed transforms
(ADR-0025); #369 supplies no snapshot history or caller wiring.

The positional correction is relative to the **original free endpoint**:
`endpoint = position + originalVelocity * dt + correction.Position`.
The next velocity is `originalVelocity + correction.Velocity`. For a future
pre-solver caller that applies the corrected velocity over the entire tick,
the pre-step positional offset must instead be
`correction.Position - correction.Velocity * dt`; otherwise the impulse is
counted twice. World-obstacle handling remains the integration issue's proof.

## Preconditions, starting magnitudes, and sources

The evidence depends on Godot .NET 4.7.1, .NET 8, xUnit 2.5.3, the shipped
player scene, fixed 60 Hz ticks, and the documented kernel settings. Retuning
these inputs invalidates the numerical baseline.

Set requires both speed ≤0.5 m/s and an inclusive 60° authoritative-facing
half-cone toward the contact normal at impact. The speed is one shipped
acceleration quantum (30 m/s² / 60 Hz), one twelfth of top speed (6 m/s).
The cone reuses the scale of ADR-0018's existing authoritative exposure-cone
precedent. These are defensible initial magnitudes, **not measured legal-guarding
limits**. Real half-court defense, tier 2 under ADR-0014 and already settled by
ADR-0025, grounds the direction: set and facing absorbs; unset yields.

Normal closing motion is absorbed with zero restitution. A sole set body takes
zero response share; the unset body takes the full share. Equal setness shares
response equally (0.5/0.5). Tangential motion is preserved. Full positional
recovery is a correctness condition; no exponential recovery rate or duration
counter is introduced. Tuning remains #238 and human feel acceptance #173.

Official Godot 4.7 sources checked before implementation:

- [Capsule total height and radius](https://docs.godotengine.org/en/4.7/classes/class_capsuleshape3d.html#class-capsuleshape3d-property-height): height includes the hemispheres.
- [CollisionShape3D shape resource](https://docs.godotengine.org/en/4.7/classes/class_collisionshape3d.html#class-collisionshape3d-property-shape): geometry comes from the shape resource; scale remains one.
- [Node3D global position](https://docs.godotengine.org/en/4.7/classes/class_node3d.html#class-node3d-property-global-position): the adapter writes world-space endpoints.
- [Vector3 finite check](https://docs.godotengine.org/en/4.7/classes/class_vector3.html#class-vector3-method-is-finite) and [SceneTree exit code](https://docs.godotengine.org/en/4.7/classes/class_scenetree.html#class-scenetree-method-quit): explicit finite-state and exit-code gates.

The swept geometry and asymmetric response are project-owned pure math under
ADR-0025, not Godot framework recommendations. Repeatability here is identical
inputs on the tested runtime; it does not establish bit equality across CPU
architectures for transcendental heading calculations.

The sweep uses relative motion and perpendicular distance to the travel line,
forming the raw determinant before normalizing it. This avoids subtracting two
nearly equal fourth-power terms in
the quadratic discriminant, which can otherwise miss long glancing crossings.
Relative tick travel and correction components must fit finite single-precision
values; unrepresentable results are explicitly rejected. Endpoint application
still has Godot `Vector3` float precision: this proof establishes the court-scale
separation tolerance above, not sub-meter endpoint accuracy at astronomical
coordinates.

Per-body capsule center and spine calculations are grouped before combining
the pair, so role reversal does not change the order of cancellation within a
body's geometry. Dedicated regressions cover cancelling center offsets and
long axis-aligned and diagonal crossings.

## Reproduction

From the repository root, with `GODOT` set to the pinned console binary:

```powershell
dotnet build "HOOPER GAME.csproj" --configuration Debug -p:IncludeIntegrationHarness=true
dotnet test "tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj" --configuration Debug
python tools/harness_catalog.py run --tag contact-kernel-test --godot "$env:GODOT"
python tools/harness_catalog.py run --tag contact-fixture-test --godot "$env:GODOT"
python tools/contact_kernel_mutations.py --godot "$env:GODOT"
```

The catalog registers all seven adapter scenarios in the existing CI shards
and keeps set/unset/disabled trials together. It supplies unique workspace-local
Godot log files and bounded subprocess execution.

## Measurements and mutation evidence

Clean production and integration builds passed with zero warnings/errors.
The full xUnit suite passed 1173 tests, with five existing characterization
skips and zero failures; this includes all 18 contact-kernel cases.
The seven kernel-adapter scenarios passed, as did both existing #356 fixture
scenarios and all 61 repository contract tests.

| Kernel scenario | Ticks | Minimum horizontal separation (m) | Absorption gate |
|---|---:|---:|---|
| `set-drive` | 30 | 1.000000 | True |
| `unset-drive` | 30 | 1.000000 | False; defender yielded about 0.5 m |
| `disabled-kernel` | 30 | 0.000001 | False; free endpoint reached the origin |
| `overlap` | 1 | 1.000000 | False; stationary separation restored |
| `coincident` | 1 | 1.000000 | False; stable-ID tie separated bodies along X |
| `crossing` | 1 | 0.999998 | True; within the 0.0002 m tolerance |
| `dimensions` | 30 | 0.850000 | True; measured radius sum 0.85 m |

A mutation counts as detected only if it compiles and the named behavioral
assertion fails; build failure or process crash is not contact evidence.
`tools/contact_kernel_mutations.py` checks clean baselines first, rejects
unexpected failures, then restores the original kernel bytes and rebuilds
both assemblies in `finally`. Run it without concurrent source edits/builds.

Run `20261010T083838Z-bfceb23a` detected all six mutations. Each mutant built
with zero warnings/errors and exited 1 at the required assertion. The runner
confirmed exact source restoration and clean game/unit rebuilds. Local logs
are under `.godot/contact-mutation-20261010T083838Z-bfceb23a/`; the JSON summary
is `.godot/contact-kernel-mutations.json`.

| Mutation | Evidence surface | Observed failed assertion |
|---|---|---|
| `disable-kernel` | `set-drive`, tick 21 | Kernel non-overlap gate |
| `ignore-facing` | `unset-drive`, tick 30 | Wrong-facing opponent must yield under the same drive |
| `remove-overlap` | `overlap`, tick 1 | Kernel non-overlap gate |
| `disable-sweep` | `crossing`, tick 1 | Set absorption gate |
| `fixed-dimensions` | `dimensions`, tick 30 | Set absorption gate |
| `remove-speed-predicate` | `SetRequiresSpeedAndFacingWithInclusiveBoundaries` | `Assert.False`: expected false, actual true; exactly one test executed/failed |

Numerical regressions additionally failed before their fixes: a long glancing
crossing lost its chord in the quadratic discriminant, an exact diagonal
crossing lost its raw determinant after premature normalization, a huge finite
timestep returned infinite corrections, and cancelling capsule center offsets
invented contact only in the reversed pair. The corrected tests pass and make
these implementation choices reproducible.

## Human tuning ownership

These state checks do not establish contact feel, animation legibility, fouls,
or tuning acceptance. No editor action is needed for this kernel-only issue.
Production integration and its movement/replay/solver-masking evidence remain
separate work; #238/#173 retain magnitude and feel judgment.
