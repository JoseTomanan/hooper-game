---
name: hooper-config-and-flags
description: Catalog of every hooper-game config axis — [Export] tunables (default, .tscn override, units, status), project.godot settings, config-like constants, duplicated-constant tripwires, new-axis checklist. Load before adding, tuning, or trusting any Export, project.godot setting, InputMap action, or constant — scenes silently override code defaults (#217 trap).
---

# hooper-config-and-flags

Catalog of every tunable in hooper-game: what it is, what it defaults to, whether
a `.tscn` silently overrides that default, and whether you are allowed to change
it. Read this before touching any `[Export]` field, `project.godot` setting, or
numeric constant that looks like config.

Jargon at first use: an **`[Export]`** is a C# attribute that makes a node
property visible to Godot's Inspector and to `.tscn` scene files — a `.tscn` may
carry a `PropertyName = value` line under a node that **overrides** the C#
default when the scene is loaded. A **harness** is a headless Godot integration
test scene under `tests/integration/` (ADR-0016).

**The #1 trap this skill exists to prevent:** an `[Export]` field's C# default
and its `.tscn` override can disagree, and which one you get depends on HOW the
node was instantiated:

- Loaded through a real scene tree (the actual game, and any harness scene that
  instances the `.tscn` via `instance=ExtResource(...)`) → gets the `.tscn`
  override.
- Built directly in C# (`new BallController()` etc. with no scene load, as some
  harness/test code does) → gets the **raw C# default**, silently skipping the
  override.

This is exactly the class of trap issue #217 caught (BoardCenter/RimCenter
inconsistency — §1a below). Every table row below flags whether an override
exists so you don't get bitten twice.

## When NOT to use this

- For *invariants and system architecture* (why a value exists, what breaks if
  you violate an ordering rule) → `hooper-architecture-contract`.
- For *what a value means in design terms* (what the "0.35–0.65 exposed band"
  models about real dribbling, why 900°/s turn rate was chosen) →
  `hooper-duel-design-reference`.
- For *whether you're allowed to change a value at all* (afk vs hitl, ADR
  discipline, who retunes feel) → `hooper-change-control`.

---

## 1. The `[Export]` catalog

Every `[Export]` in `scripts/` as of 2026-07-12 (verified via
`grep -rn "\[Export" scripts/`; `tests/integration/` contains none), grouped by
class.

**Status legend:**

- **settled** — shipped value, safe to rely on, not up for ad-hoc retuning.
- **pending-design** — an open design fork tracked by an issue; do not "fix" it,
  it's owned.
- **feel-only** — provisional value explicitly deferred to a milestone human
  feel pass (#104, #114) under ADR-0014/0015. Agents must NOT retune these solo.

### BallController (`scripts/Ball/BallController.cs`) — the biggest export surface

| Export | Code default | `.tscn` override | Units | Status |
|---|---|---|---|---|
| `Players` (Node) | — | wired in Main.tscn | — | wiring |
| `DribbleHandHeight` | 1.0f | none | m | settled |
| `DribbleForwardOffset` | 0.5f | none | m | settled |
| `HandOffset` | 0.18f | **Ball.tscn: `HandOffset = 0.4`** | m | settled (scene wins in-game) |
| `CrossoverSweepDuration` | 0.12f | none | s (converted to a deterministic tick count at runtime) | settled |
| `CrossoverSweepDipDepth` | 0.15f | none | m | settled |
| `BehindTheBackSweepDepth` | 0.7f | none | m | settled |
| `DribblePeriod` | 0.6f | none | s | settled |
| `ShotApexHeight` | 4.0f | none | m | settled |
| `Gravity` | 9.8f | none | m/s² | settled |
| `RimCenter` | (0, 3.05, 0) | none | m (world) | settled — **see §1a** |
| `ShotTarget` | (0, 3.05, 0) | none | m (world) | settled |
| `RimRadius` | 0.23f | none | m | settled |
| `BallRadius` | 0.12f | none | m | settled |
| `RimRestitution` | 0.65f | none | coefficient | settled |
| `BoardCenter` | (0, 3.5, 0.3) | none | m (world) | settled — **see §1a** |
| `BoardNormal` | (0, 0, -1) | none | unit vector | settled |
| `BoardHalfWidth` | 0.46f | none | m | settled |
| `BoardHalfHeight` | 0.30f | none | m | settled |
| `BoardRestitution` | 0.65f | none | coefficient | settled |
| `FloorRestitution` | 0.76f | none | coefficient (COR) | settled — derived from the NBA ball-inflation spec, pinned by `FloorBounceTests.RegulationDrop_ReboundTop_LandsInNbaLegalBand` |
| `FloorHorizontalDecay` | 0.9f | none | multiplier per bounce | settled |
| `FloorSettleSpeed` | 0.6f | none | m/s | settled |
| `ShootAction` | "ball_shoot" | none | InputMap action name | settled — must match the `[input]` entry in project.godot (§2) |
| `PickupRadius` | 1.0f | none | m (XZ) | settled |
| `ClearLineDistance` | 5.8f | none | m | settled |
| `CourtMin` | `CourtBounds.DefaultMin` = (-7.62, -1.0) | none | m (XZ) | settled — single source of truth is `CourtBounds`, §3 |
| `CourtMax` | `CourtBounds.DefaultMax` = (7.62, 11.88) | none | m (XZ) | settled |
| `ShotScatterEnabled` | true | none | bool | settled |
| `ShotScatterPerMeter` | 0.026f | none | m of scatter radius per m of shot distance | settled — duplicated in a characterization test, §4 |
| `MaxShotScatter` | 0.45f | none | m (radius cap) | settled — duplicated, §4 |
| `ShotScatterSeed` | 12345 | none | RNG seed | settled-but-open — deterministic per server run; per-match reseed is an OPEN question, §3 |
| `MovementScatterK` | 0.8f (Range 0–3) | none | multiplier strength | settled (#64 CLOSED — continuous speed-ratio model chosen, ADR-0009 amendment 2026-06-27). Code comment "Default continuous pending human review" is STALE — trust the ADR. Note: the ADR-0009 amendment text says 1.0f but live code is 0.8f (ADR-vs-code drift, not a skill error) |
| `ContestScatterK` | 1.0f (Range 0–3) | none | multiplier strength | settled (#65 CLOSED — proximity-alone model chosen, ADR-0009 amendment 2026-06-27). Code comment "pending human review" is STALE — trust the ADR |
| `ContestRange` | 2.2f | none | m (XZ) | settled — ~arm's-length closeout, pairs with ContestScatterK |
| `FacingScatterK` | 0.8f (Range 0–3) | none | multiplier strength | settled (issue #81, ADR-0009 amendment 2026-06-27; reads authoritative `Heading`, never cosmetic `FacingResolver`) |
| `ReconcileLerpRate` | 0.3f | none | per-tick lerp factor | settled |
| `ReconcileSnapThreshold` | 0.001f | none | m | settled |
| `StealLoExposed` | 0.35f | none | dribble-phase fraction [0,1] | **feel-only (#104)** |
| `StealHiExposed` | 0.65f | none | dribble-phase fraction [0,1] | **feel-only (#104)** |
| `StealKnockSpeed` | 1.5f | none | m/s | **feel-only (#104)** |
| `StealKnockRiseSpeed` | 1.0f | none | m/s | **feel-only (#104)** |
| `BlockGraceTicks` | 10 | none | ticks @ 60 Hz (≈0.17 s) | **feel-only (#104)** — hard constraint: must stay ≥ `BlockMove.ActiveFrames` (currently 8) per ADR-0018 §3, or a perfectly-timed block can whiff |
| `BlockSwatSpeed` | 2.0f | none | m/s (horizontal, away from basket) | **feel-only (#104)** |
| `BlockSwatDropSpeed` | 1.0f | none | m/s (downward) | **feel-only (#104)** |
| `MadeFlashDuration` | 1.0f | none | s | settled |

#### §1a — the #217 RimCenter/BoardCenter class of trap, confirmed live

`Ball.tscn` does **not** override `RimCenter` or `BoardCenter` — the game and any
scene-loaded harness get the C# defaults `(0, 3.05, 0)` / `(0, 3.5, 0.3)`. But
`Main.tscn`'s *visual* rim/backboard meshes sit at DIFFERENT world positions
(verified 2026-07-12):

```
Rim mesh transform origin:       (0, 3.05, 0.3)      # Z = 0.3, not 0
Backboard mesh transform origin: (0, 3.361642, 0.03) # Y and Z differ from BoardCenter
```

The *physics* rim/board (the exports the deterministic ball resolves against)
and the *visual* meshes do not coincide. This is a known condition, not a bug to
hot-fix: the export values are authoritative; the meshes are presentation.

Why it matters operationally (the #217 lesson): a harness that builds a
`BallController` tree **in code** (no `.tscn` load) gets raw C# defaults for
every export. Today Rim/Board have no scene override so code-built and
scene-loaded agree — but the moment anyone adds one (as `HandOffset = 0.4`
proves is normal practice), every code-built harness silently diverges from the
shipped game. Rules:

1. Before trusting an export value in a harness, check the code default AND the
   owning `.tscn` (§7 one-liners).
2. If a harness builds nodes in code, explicitly set overridden values to match
   the scene, or assert them (the `exports` scenario pattern, §6 step 5).
3. If you add a `.tscn` override to an export a harness relies on, grep
   `tests/integration/` for that export name in the same change.
4. Every "X didn't happen" harness assertion needs a control scenario proving X
   *can* happen in the same setup — otherwise a defaults mismatch reads as a
   pass (the deeper #217 lesson).

### GameManager (`scripts/Systems/GameManager.cs`)

| Export | Code default | `.tscn` override | Units | Status |
|---|---|---|---|---|
| `TargetScore` | 11 | **Main.tscn: `TargetScore = 5`** | points to win | settled — **CRITICAL: the running game plays to 5, not 11.** Grep `scripts/` and you see 11; grep `scenes/Main.tscn` and you see 5. Trust the scene for live/scene-loaded gameplay; the code default only applies to a code-built `GameManager`. The pure `Scoreboard` class carries its own `TargetScore` default (11) that `GameManager` configures — tests constructing `Scoreboard` directly see 11. |

### PlayerController (`scripts/Player/PlayerController.cs`)

`scenes/Player.tscn` sets ONLY `VisualRoot = NodePath("CharacterModel")` and
`AnimationTreePath = NodePath("AnimationTree")` (wiring). No numeric override
exists — every value below is the code default in every code path.

| Export | Code default | Units | Status |
|---|---|---|---|
| `MoveSpeed` | 6.0f | m/s | settled |
| `MaxTurnRateDeg` | 900f | deg/s | settled — ADR-0010 amendment history 400→530 (#134)→900 (#172 follow-up); asserted live by `PivotPlantTest.tscn -- --harness-scenario=exports` |
| `BackTurnSlowFactor` | 0.95f | multiplier [0,1] | settled — same history 0.35→0.90→0.95; asserted by the same `exports` scenario |
| `PivotThresholdDeg` | 90f | deg | settled (#172 pivot-plant gate); asserted by the same `exports` scenario |
| `Accel` | 45.0f | m/s² | settled |
| `Decel` | 70.0f | m/s² | settled |
| `ReconcileLerpRate` | 0.3f | per-tick lerp factor | settled — mirrors BallController's same-named tunable by convention, not shared code |
| `ReconcileSnapThreshold` | 0.001f | m | settled |
| `VisualRoot` (NodePath) | — | — | wiring (set in Player.tscn) |
| `AnimationTreePath` (NodePath) | — | — | wiring (set in Player.tscn) |
| `BurstSpeed` | 9.0f | m/s | settled (crossover Active burst) |
| `ForwardBurstScale` | 9.0f | scale factor | settled |
| `GatherDecel` | 40.0f | m/s² | settled |
| `ExitDeadzone` | 0.15f | stick magnitude [0,1] | settled |
| `BehindTheBackBurstSpeed` | 6.0f | m/s | settled |
| `BehindTheBackForwardBurstScale` | 6.0f | scale factor | settled |
| `BehindTheBackGatherDecel` | 55.0f | m/s² | settled |
| `BehindTheBackExitConeDegrees` | 50.0f | deg | settled-but-design-sensitive — ADR-0003 amendment (2026-07-06, #194) names exit-cone width the SOLE "fewer follow-ups" lever; retunes go through the feel pass, not solo |

### NetworkManager (`scripts/Networking/NetworkManager.cs`)

| Export | Code default | `.tscn` override | Units | Status |
|---|---|---|---|---|
| `Players` (Node) | — | wired in Main.tscn | — | wiring — must point at the SAME `Players` node as Ball and DiscoveryBroadcaster or the peer-id identity contract breaks (`hooper-architecture-contract`) |
| `PlayerScenePath` | "res://scenes/Player.tscn" | none | resource path | settled |
| `HostSpawn` | (0, 0, 6) | none | m (world) | settled |
| `ClientSpawn` | (0, 0, 3) | none | m (world) | settled |

### DiscoveryBroadcaster / DiscoveryListener (`scripts/Networking/`)

| Export | Code default | `.tscn` override | Units | Status |
|---|---|---|---|---|
| `DiscoveryBroadcaster.DiscoveryPort` | 7778 | none | UDP port | settled (ADR-0007) — see §3, "HOOP" beacon |
| `DiscoveryBroadcaster.BroadcastInterval` | 1.0f | none | s | settled |
| `DiscoveryBroadcaster.ServerName` | "Hooper Server" | none | string | settled |
| `DiscoveryBroadcaster.NetworkManager` / `.Players` | — | wired in Main.tscn | — | wiring |
| `DiscoveryListener.DiscoveryPort` | 7778 | none | UDP port | settled — must equal the broadcaster's; independent fields, no cross-check test, keep in sync by hand (§4) |
| `DiscoveryListener.TimeoutSeconds` | 3.0f | none | s (server-entry expiry) | settled |

### ServerBrowser (`scripts/Networking/ServerBrowser.cs`)

| Export | Code default | Units | Status |
|---|---|---|---|
| `RefreshInterval` | 0.5f | s | settled |
| `Discovery` / `NetworkManager` / `ServerListUi` | — | — | wiring |

### CourtVisuals (`scripts/Systems/CourtVisuals.cs`)

Main.tscn wires the script with no property overrides.

| Export | Code default | Units | Status |
|---|---|---|---|
| `IndicatorHeight` | 0.01f | m above floor | settled |
| `BoundLineHeight` | 0.02f | m above floor | settled |
| `BoundLineThickness` | 0.04f | m | settled |
| (commented-out) `LineWidth` | 0.03f | m | NOT live — the export line is commented out in source; do not treat it as an active axis |

### BlobShadow (`scripts/Systems/BlobShadow.cs`)

| Export | Code default | Units | Status |
|---|---|---|---|
| `FloorY` | 0.005f | m (shadow plane height) | settled |

### Node-reference-only exports (wiring, not tunables)

`DedicatedServerBootstrap.NetworkManager`/`.Lobby`; `Lobby.IpField`/
`.PortField`/`.HostButton`/`.JoinButton`/`.NetworkManager`. All `[Export]` node
references assigned in `.tscn` — nothing to retune, only keep wired when
renaming/moving nodes.

---

## 2. `project.godot` settings that matter

Verified by reading `project.godot` in full (2026-07-12):

- `config/features = PackedStringArray("4.6", "C#", "Mobile")` — engine version
  tag + C# + the Mobile rendering-method feature tag (ADR-0006).
- `run/main_scene = "uid://c047mrir711mo"` — **by uid, not path.** That uid is
  `scenes/Main.tscn` (its `[gd_scene]` header, line 1). Re-uid'ing Main.tscn
  breaks the main scene silently.
- `[dotnet] project/assembly_name = "HOOPER GAME"` — the assembly name contains
  a space; every path built from it must be quoted (quoting discipline in
  `hooper-build-and-env`).
- `[physics] 3d/physics_engine = "Jolt Physics"` — not default Godot Physics.
  Consequence (repo convention): never non-uniformly scale a `CylinderShape3D`/
  `CapsuleShape3D`/`SphereShape3D` collision shape — Jolt silently clamps it;
  author size on the shape resource. `BoxShape3D` exempt. (Note the
  deterministic ball itself never uses Jolt — ADR-0004; Jolt only handles
  player/world colliders.)
- `[rendering] rendering_device/driver.windows = "d3d12"` and
  `renderer/rendering_method = "mobile"` — ADR-0006 (Mobile renderer, D3D12 on
  Windows), not Compatibility/Forward+.
- **`physics_ticks_per_second` is NOT explicitly set** — no
  `physics/common/physics_ticks_per_second` line exists in `project.godot`, so
  the engine runs at Godot's built-in default of **60**. All fixed-dt math
  assumes this (`dt = 1.0f / Engine.PhysicsTicksPerSecond`; durations are tick
  counts). If an editor save ever writes an explicit value here, treat it as a
  red-alert diff: every tick-denominated tunable in §1 (`BlockGraceTicks`,
  committed-move frame data, sweep durations converted to ticks) changes
  real-time meaning. See `hooper-architecture-contract` (fixed-dt invariant).
- **`[input]` — the InputMap.** All actions, verified bindings (physical
  keycodes decoded):

  | Action | Key | Gamepad | Deadzone |
  |---|---|---|---|
  | `move_left` | A | — | 0.2 |
  | `move_right` | D | — | 0.2 |
  | `move_forward` | W | — | 0.2 |
  | `move_backward` | S | — | 0.2 |
  | `ball_shoot` | Space | — | 0.2 |
  | `aim_right` | L | right-stick X+ (axis 2, +1.0) | 0.5 |
  | `aim_left` | J | right-stick X− (axis 2, −1.0) | 0.5 |
  | `aim_down` | K | right-stick Y+ (axis 3, +1.0) | 0.5 |
  | `aim_up` | I | right-stick Y− (axis 3, −1.0) | 0.5 |
  | `move_feint` | E | joypad button 9 | 0.2 |
  | `def_steal` | Z | joypad button 2 | 0.2 |
  | `def_block` | X | joypad button 3 | 0.2 |
  | `def_contest` | C | joypad button 1 | 0.2 |
  | `move_size_up` | Q | joypad button 10 | 0.2 |

  Note the deliberate deadzone asymmetry: the four `aim_*` right-stick actions
  use 0.5 while everything else uses 0.2 — a higher bar so stick drift doesn't
  register as an aim gesture (context: the 2026-07-03 phantom-feint bug, fixed
  by `FeintGateResolver`; history in `hooper-failure-archaeology`).
  `BallController.ShootAction` defaults to the string `"ball_shoot"` — renaming
  the InputMap action requires updating that export too.

---

## 3. Known constants that look like config

- **`GameManager.TargetScore`: code 11, live game 5** — see §1 GameManager row.
  The single most important "code says X, game plays Y" fact in this catalog.
- **`ShotScatterSeed = 12345`** (fixed `[Export]` on `BallController`) — the
  server-side scatter RNG (`_shotRng`, drawn only when `IsServer`) is seeded
  from this export, so the miss pattern is deterministic per server run. Ideal
  for reproducible tests. **Whether a per-match reseed is planned for shipping
  is an OPEN question** as of 2026-07-12 — do not assume it, and do not "fix" it
  by randomizing the seed without an owned issue and design sign-off.
- **`CourtBounds.DefaultMin` / `DefaultMax`**
  (`scripts/Ball/CourtBounds.cs:55,58`): `(-7.62, -1.0)` / `(7.62, 11.88)` in XZ
  metres. **Single source of truth for the court rectangle** —
  `BallController.CourtMin/CourtMax` merely default to these. To resize the
  court, change `CourtBounds`, never a per-export override. (History: a
  court-outline-vs-CourtMin/Max mismatch was a real scale bug, fixed
  2026-07-03.)
- **LAN discovery: UDP port 7778 + `"HOOP"` magic beacon** (ADR-0007) —
  `DiscoveryBroadcaster`/`DiscoveryListener` both default `DiscoveryPort =
  7778`. The `"HOOP"` magic string marks the beacon wire format; grep
  `scripts/Networking/` for `HOOP` for the exact format before touching it.
- **Harness env vars** (dual-instance bash orchestrators in
  `tests/integration/`): `SERVER_BIND_WAIT` — seconds the script sleeps for the
  server to boot and bind, default **6**, env-overridable; `HARNESS_PORT` —
  ENet port, env-overridable, per-script defaults (verified in the scripts):

  | Script | Default `HARNESS_PORT` |
  |---|---|
  | `run-net-handshake.sh` | 23456 |
  | `run-net-state-sync.sh` | 23457 |
  | `run-net-node-replication.sh` | 23458 |
  | `run-net-behindtheback-sweep.sh` | 23459 |

  **Ports 23456–23459 are taken.** A new dual-instance harness must claim the
  next free port (23460+) as its default — never reuse these four.

---

## 4. Duplicated-constant tripwires

### `ShotScatterCurveCharacterizationTests` mirrors BallController exports

`tests/Hooper.Ball.Tests/ShotScatterCurveCharacterizationTests.cs` hardcodes
local mirrors of live `BallController` exports (verified, lines 53–67):

```csharp
private const float Spm        = 0.026f;   // ShotScatterPerMeter
private const float MaxScatter = 0.45f;    // MaxShotScatter
private static readonly Vector3 RimCenter   = new(0f, 3.05f, 0f);
private static readonly Vector3 BoardCenter = new(0f, 3.5f, 0.3f);
```

It must hardcode them because it is a plain xUnit test — no scene, no
`BallController` node (node-derived classes are deliberately excluded from the
test assembly) — yet it needs the same numbers to reproduce the make-% curve.

**Why this is a tripwire:** retune `ShotScatterPerMeter` or `MaxShotScatter` on
`BallController` (a plausible #104/#114 feel-pass outcome) and forget this file,
and the characterization suite silently keeps characterizing the OLD curve. No
compile error, no obvious failure.

**The CI safety net:** `DefaultsMatchShotMakeCurveBands` (same file, ~line 350)
cross-checks that the local consts reproduce the same make-% bands as
`ShotMakeCurveTests` (which drives the real scatter→arc→rim chain).
Interpretation rule, from the file's own comments: **if
`DefaultsMatchShotMakeCurveBands` fails while `ShotMakeCurveTests` passes, the
mirrored consts drifted — update `Spm`/`MaxScatter` (and `RimCenter`/
`BoardCenter` if those moved) in the characterization file to match the new
BallController defaults, in the same commit as the retune.**

Reading test results: 5 tests in this same file are permanently skipped — the
green baseline is 664 passed / 5 skipped / 669 total as of 2026-07-12. The skips
are normal; `DefaultsMatchShotMakeCurveBands` itself runs. Confirm with the §7
grep if in doubt.

### Other manual-sync pairs (no automated cross-check — hand-verify)

- `DiscoveryBroadcaster.DiscoveryPort` ↔ `DiscoveryListener.DiscoveryPort`
  (both 7778; independent fields).
- `BallController.ReconcileLerpRate/SnapThreshold` ↔ same-named
  `PlayerController` exports (equal by convention; nothing enforces it).
- `BallController.ShootAction` ("ball_shoot") ↔ the `ball_shoot` InputMap entry.
- `BlockGraceTicks` (10) ≥ `BlockMove.ActiveFrames` (8) — an inequality
  constraint from ADR-0018 §3, documented in the export's doc comment.
- `Scoreboard.TargetScore` (pure class, 11) ↔ `GameManager.TargetScore` export
  (11, overridden to 5 in Main.tscn).

---

## 5. Design forks living in exports — status and traps

| Fork | Export(s) | Issue | Current default | Status |
|---|---|---|---|---|
| Movement-penalty shape | `MovementScatterK` | **#64 — RESOLVED** | 0.8, continuous speed-ratio | Settled 2026-06-27 (ADR-0009 amendment): continuous speed-ratio penalty chosen over a discrete planted/not-planted threshold. The code comment "Default continuous pending human review" is STALE. Do not relitigate. |
| Contest facing | `ContestScatterK`, `ContestRange` | **#65 — RESOLVED** | 1.0 / 2.2 m, proximity-only | Settled 2026-06-27 (ADR-0009 amendment): proximity-alone chosen over facing-required. The "pending human review" code comment is STALE. Deliberately-minimal slice; the code comment still validly forbids growing it into block/steal logic. |
| Block reach | (none yet — timing-only) | **#214 — OPEN** | N/A — `DefensiveResolution.Succeeds` has no proximity term | Whether/how to add a reach/proximity term. Today a defender across the court can technically "block"; this is a KNOWN placeholder, not a bug to hot-fix. |

If your task touches one of these, cite the issue and leave the default alone —
unless your task *is* that issue, and even then follow ADR-0014 (cite-or-ask)
and ADR-0015; see `hooper-change-control`.

Separately, **every `feel-only` row in §1** (steal window bounds, knock/swat
speeds, `BlockGraceTicks`) is provisional pending the milestone feel pass (#104,
and the combined M9+M10 pass #114). If a value "feels wrong" while you're
testing, file it against the feel-pass issue — do not retune solo
(ADR-0014/0015).

---

## 6. How to add a new export/config axis (checklist)

1. **Pure-class param vs. node export.** If the value feeds a pure, engine-free
   class (`ShotArc`, `RimBackboard`, `DefensiveResolution`, …), pass it as a
   constructor/method parameter from the owning node's `[Export]` — pure classes
   must stay engine-free (no `[Export]`, no `Node`, no `Random`), which is what
   keeps them headless-testable and deterministic. Add a NEW `[Export]` on the
   node (`BallController`/`PlayerController`/…) only if it's a genuinely new
   tunable, not derivable from existing ones.
2. **Choose the default with a citation.** If it models a reference behaviour
   (real half-court ball > Undisputed 3 > 2K, ADR-0014), name the reference and
   tier in the commit/PR body — cite-or-ask.
3. **Think through the `.tscn` override implication.** Will any scene override
   your default? If yes, scene-loaded vs. code-built trees diverge (§1a, the
   #217 trap): grep `tests/integration/` for harnesses touching the node and
   either set or assert the value there. Prefer NO scene override when the code
   default can simply be correct.
4. **Document units inline** in the XML doc comment: ticks vs. seconds vs.
   metres vs. multiplier, stated in prose (any `BallController` export is the
   house-style example). Tick-denominated durations must never masquerade as
   wall-clock ones (fixed-dt invariant).
5. **If the value is load-bearing, add/extend a harness `exports` scenario.**
   Precedent: `tests/integration/PivotPlantTest.cs` scenario `exports`
   (`godot --headless --path . res://tests/integration/PivotPlantTest.tscn --
   --harness-scenario=exports`) asserts `BackTurnSlowFactor`/`MaxTurnRateDeg`/
   `PivotThresholdDeg` match the ADR-amended values on a real scene-loaded
   node. Cheap insurance against §1a drift.
6. **Update the characterization cross-check if you duplicated a constant.** If
   the value is (or becomes) mirrored in `ShotScatterCurveCharacterizationTests`
   (§4), change both files in the same commit and confirm
   `DefaultsMatchShotMakeCurveBands` passes. If you create a NEW duplicated
   constant elsewhere, add a comparable cross-check test — or better, don't
   duplicate.
7. **Feel values get filed, not tuned.** If the axis is a "does this feel right"
   knob (speed, window width, knock force) rather than a correctness constant,
   land a clearly-labelled provisional default, note the deferral in the doc
   comment (the "#104 + per-milestone feel pass (ADR-0015)" phrasing used by
   `BlockSwatSpeed` is the template), and file/annotate it for the milestone
   feel pass. Never iterate on feel without the human.

---

## 7. Re-verification commands (regenerate this catalog)

Run from the repo root; quote everything (repo path and csproj contain spaces).

```bash
# §1 — full [Export] catalog:
grep -rn "\[Export" scripts/ --include=*.cs
grep -rn "\[Export" tests/integration/        # expect: no matches (as of 2026-07-12)

# §1 — .tscn numeric overrides (property lines under script-bearing nodes):
grep -n "TargetScore" scenes/Main.tscn        # expect: TargetScore = 5
grep -n "HandOffset" scenes/Ball.tscn         # expect: HandOffset = 0.4
grep -n "^\[node\|^script = \|^[A-Z][A-Za-z]* = " scenes/Main.tscn scenes/Ball.tscn scenes/Player.tscn

# §1a — visual vs physics rim/board positions:
grep -n "RimCenter\|BoardCenter" scripts/Ball/BallController.cs | head -5
grep -n -A1 'name="Rim"\|name="Backboard"' scenes/Main.tscn

# §2 — project.godot:
grep -n "physics_ticks_per_second\|physics_engine\|driver.windows\|rendering_method\|assembly_name\|main_scene\|features" project.godot
grep -n "^move_\|^ball_shoot\|^aim_\|^def_\|^move_feint\|^move_size_up" project.godot

# §3 — CourtBounds single source of truth:
grep -n "DefaultMin\|DefaultMax" scripts/Ball/CourtBounds.cs

# §3 — discovery port + beacon:
grep -rn "7778\|HOOP" scripts/Networking/DiscoveryBroadcaster.cs scripts/Networking/DiscoveryListener.cs

# §3 — harness ports/env vars:
grep -n "HARNESS_PORT\|SERVER_BIND_WAIT" tests/integration/*.sh

# §4 — mirrored constants + cross-check test:
grep -n "const float Spm\|const float MaxScatter\|RimCenter\|BoardCenter\|DefaultsMatchShotMakeCurveBands" \
  tests/Hooper.Ball.Tests/ShotScatterCurveCharacterizationTests.cs

# §5 — open forks still open in code:
grep -n "#64\|#65\|#214" scripts/Ball/BallController.cs
# …and on the tracker:
gh issue view 64 --repo JoseTomanan/hooper-game --json state
gh issue view 65 --repo JoseTomanan/hooper-game --json state
gh issue view 214 --repo JoseTomanan/hooper-game --json state

# §6 step 5 — the exports-scenario precedent:
grep -n "harness-scenario=exports\|\"exports\"" tests/integration/PivotPlantTest.cs
```

---

## Provenance and maintenance

Date-stamped **2026-07-12**; reviewed and corrected 2026-07-15 (frontmatter
description shortened to survive listing truncation; #64/#65 rows corrected
from pending-design to settled — those issues closed 2026-06-27 via ADR-0009
amendments, and the "pending human review" code comments are stale).
Every number, path, and name in this file was
verified directly against the repo at that date: `grep -rn "\[Export"` over
`scripts/` and `tests/integration/` (zero in the latter); a full read of
`project.godot`; the node-property blocks of `scenes/Main.tscn`,
`scenes/Ball.tscn`, `scenes/Player.tscn`; `scripts/Ball/CourtBounds.cs`; the
#64/#65/#214/BlockGraceTicks/BlockSwatSpeed doc-comment regions of
`scripts/Ball/BallController.cs`;
`tests/Hooper.Ball.Tests/ShotScatterCurveCharacterizationTests.cs` (mirrored
consts at lines 53–67, cross-check test ~line 350); and all four
`tests/integration/run-net-*.sh` scripts. Discovery digests were used as leads
only — every load-bearing value was independently re-read from source.

Most-likely-to-drift facts and their checks (commands in §7):

- The `[Export]` set grows with every milestone — re-run the §1 greps.
- `Main.tscn: TargetScore = 5` and `Ball.tscn: HandOffset = 0.4` are the only
  two numeric scene overrides today; any editor save could add more — re-run
  the override greps after ANY `.tscn` change.
- The `feel-only` rows flip to settled when the #104/#114 feel pass lands —
  check those issues with `gh issue view`.
- #64 and #65 are CLOSED (settled 2026-06-27 via ADR-0009 amendments); only
  #214 (block reach) remains open — its row closes when the issue does.
- The mirrored constants in `ShotScatterCurveCharacterizationTests` must move in
  lockstep with any `ShotScatterPerMeter`/`MaxShotScatter`/`RimCenter`/
  `BoardCenter` retune — `DefaultsMatchShotMakeCurveBands` is the tripwire.
- 664 passed / 5 skipped / 669 total is the green `dotnet test` baseline as of
  2026-07-12; a changed skip count means the characterization file changed.
