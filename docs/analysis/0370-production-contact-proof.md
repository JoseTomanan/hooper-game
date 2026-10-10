# Production contact across movement and prediction

- Issue: [#370](https://github.com/JoseTomanan/hooper-game/issues/370)
- Authority: ADR-0002, ADR-0003, ADR-0008, ADR-0012, ADR-0025
- Measurement date: 2026-10-10
- Discipline: source-driven development with doubt-driven review

## Implementation and sources

The server stages both players' input, heading and proposed velocity, resolves
one immutable pair, moves both bodies, repairs remaining overlap against world
geometry, and then publishes both snapshots. A physics-frame guard prevents the
second player callback from integrating the pair again. Clients pass their
opponent's last raw broadcast position, velocity and heading into movement;
display interpolation never supplies the sample. Snapshot availability persists
after presentation consumes a broadcast. Solo and disabled-collider bodies do
not acquire phantom opponents.

| Engine contract | Implementation consequence | Official source |
|---|---|---|
| `MoveAndSlide` integrates velocity using the physics timestep and can modify velocity | Preserve world-clipped velocity; subtract the contact impulse's timestep contribution from the positional correction before integrating | [CharacterBody3D](https://docs.godotengine.org/en/4.7/classes/class_characterbody3d.html#class-characterbody3d-method-move-and-slide) |
| `MoveAndCollide` takes displacement | Sweep positional repairs through world geometry, including a sliding remainder | [PhysicsBody3D](https://docs.godotengine.org/en/4.7/classes/class_physicsbody3d.html#class-physicsbody3d-method-move-and-collide) |
| Collision masks choose scanned layers | Players use layer 2 and scan world layer 1; Jolt no longer resolves player pairs | [CollisionObject3D](https://docs.godotengine.org/en/4.7/classes/class_collisionobject3d.html#class-collisionobject3d-property-collision-mask) |
| Capsule height includes hemispheres | Read actual centered, upright capsule dimensions; reject unsupported transforms | [CapsuleShape3D](https://docs.godotengine.org/en/4.7/classes/class_capsuleshape3d.html#class-capsuleshape3d-property-height) |
| Physics frame count identifies the current step | Integrate a server pair once per frame | [Engine](https://docs.godotengine.org/en/4.7/classes/class_engine.html#class-engine-method-get-physics-frames) |
| Physics processing priority orders callbacks | Place harness inputs before production callbacks and observations afterward | [Node](https://docs.godotengine.org/en/4.7/classes/class_node.html#class-node-property-process-physics-priority) |
| RPC endpoints require matching paths and declarations | Use real replicated player nodes and dedicated-server roster/configuration barriers | [High-level multiplayer](https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#remote-procedure-calls) |
| Input actions feed polling methods | The migrated replay fixture's host player receives real `move_right` input rather than a remote-input slot | [Input](https://docs.godotengine.org/en/4.7/classes/class_input.html#class-input-method-action-press) |

The neutral path and all three committed phases share the contact integrator.
Client prediction transfers the immutable opponent's response share to its own
body, including the swept impulse; endpoint-only separation would miss a crossing
that finishes beyond the opponent. Server resolution applies the two shares to
their respective bodies. World-blocked final separation can transfer its
unfulfilled share to the other participant. Reconciliation repairs overlap even
when every buffered input has been acknowledged.

## Local proof

`ProductionContactTest` uses the shipped player scene and actual phase machine.
Its 18 scenarios cover set versus unset drive, disabled contact, initial overlap,
coincidence, high-speed crossing, floor, wall, corner, Startup/Active/Recovery,
all 16 shipped move timelines, positional box-out, pickup tie ordering, solver
masking, immutable-sample crossing and empty-buffer reconciliation.

Separation tolerance is 0.0002 m for the fixture's 1 m combined capsule radius.
The 1,200 m/s crossing velocity is an explicit fixture stress input. Production
tunables are unchanged. Timeline tests compare natural frame counts with an
independent committed-move machine and a disabled-contact control. Floor checks
require a downward collision query with an upward floor normal, then disable the
world mask and require that same query to miss. The box-out proof calls the real
ball recovery enumeration: contact changes positions, while nearest-player and
lower-peer-ID tie rules remain unchanged.

## Dedicated-server proof

`NetProductionContactTest` runs one dedicated server and two independent ENet
clients. Independent UDP relays delay both directions of both client links.
The matrix covers neutral, crossover and drive-gather, contact and no-contact,
at baseline and 30 ms per-way queue delay, with two repetitions. The runner
records measured queue dwell separately from the requested delay and requires
exit-zero plus explicit PASS from all three processes.

All 24 matrix trials passed. A subsequent assertion audit separated neutral
replay from actual committed integration in the response counter. The eight
committed-contact trials (two moves, two delays, two repetitions) then passed
again with that stronger assertion. The 24 trials prove the topology/control
matrix; the final eight independently prove actual committed contact. Their
compact metrics and the earlier failed premises are retained in
[0370-production-contact-network-results.json](0370-production-contact-network-results.json).

Assertions observe actual movement sample values, finite state, capsule
separation, absence of player solver slides, nonempty reconciliation replay and
settled authoritative convergence. Contact runs require raw/display distinction
and exact raw position/velocity/heading sourcing. Actual body correction is
measured after reconciliation and replay, before new prediction or cosmetic
smoothing. The driver must undergo corrections; the defender may predict
exactly. Committed response counts exclude neutral replay while a move machine
is active. Sustained convergence requires at least ten observations within
0.01 m of the reliably delivered stationary authoritative sample.

The first network workload started committed moves at 1.4 m separation. Delayed
crossover/drive-gather sometimes missed contact before the pre-existing stale
Inactive snapshot cancellation documented by #368. The final workload starts
them at 1.25 m, still outside the combined radius, to exercise contact before
that cancellation. No production threshold or move duration changed. An initial
requirement that both clients must correct was also invalid: one defender replayed
194 times and converged without any physical correction. Its zero correction is
retained as evidence of exact prediction, not counted as a correction.

## Results and reproduction

Final gate results and mutation evidence are recorded in
[0370-production-contact-results.json](0370-production-contact-results.json). Raw logs and per-tick JSON rows live in
the ignored `.godot/production-contact/` directory and can be regenerated:

```powershell
dotnet build "HOOPER GAME.csproj" --configuration Debug -p:IncludeIntegrationHarness=true
python tools/harness_catalog.py run --scene ProductionContactTest.tscn --godot $env:GODOT
python tools/run_production_contact.py --godot $env:GODOT --all --repeat 2 --output .godot/production-contact/reproduction
python tools/production_contact_mutations.py --godot $env:GODOT
python tools/harness_catalog.py run --all --godot $env:GODOT --bash "C:/Program Files/Git/bin/bash.exe"
```

Each mutation must compile, pass its clean baseline, and then fail its named
behavioral assertion with exit 1. The runner restores exact source bytes and
rebuilds afterward. Mutations disable the kernel, substitute display state,
skip the committed Active integrator and re-enable player solver collision.
Compilation errors and native crashes do not count as assertion kills.

The full catalog's initial sandboxed run passed all 291 single-process entries.
All 39 shell-launched entries failed before Godot startup because Git Bash could
not create its signal pipe (`Win32 error 5`). They were rerun outside that
restriction. That run caught an instrument migration error: the replay fixture's
peer 1 uses the actual host tick, so seeding its remote-input slot no longer moved
it. The instrument now presses the host's real input action at tick 90, retaining
the existing stationary/moving assertions. Its UDP relay also handles Windows
connection-reset notifications after peer shutdown, while still requiring both
processes' exit-zero/PASS markers and bounded completion. Microsoft documents
UDP `WSAECONNRESET` as an ICMP port-unreachable result in
[recvfrom](https://learn.microsoft.com/en-us/windows/win32/api/winsock2/nf-winsock2-recvfrom).
The affected twelve
replay cases passed after those repairs; no production tuning or thresholds
change.

Final gates: both build configurations passed with zero warnings/errors,
1,173 xUnit tests passed with five intentional skips, 61 repository contracts
passed, and all 330 catalog entries passed across the recorded runs (291 direct,
27 network, then the twelve repaired replay cases). All 18 local contact cases,
32 dedicated-server trials and four compiled assertion mutations passed their
respective gates. The merged catalog evidence retains both earlier failed runs
and identifies the final passing run for every unique catalog ID.

## Limits

Prediction still uses the latest opponent snapshot for every buffered replay
step; it does not retain opponent history. Reconciliation still replays neutral
movement and retains #368's stale-Inactive cancellation behavior. Consequently,
this proves raw sourcing, nonoverlap, actual correction and eventual settled
convergence, not exact moving-state reconstruction or matched committed phases.
The immutable snapshot's velocity participates in the kernel's relative sweep;
crossing its old position alone does not imply crossing its projected trajectory.

There are no new possession rules, pickup priorities, sprint/stance mechanics,
fouls or M11 activation. Feel and tuning remain in #173 and #238. No editor
steps are required for these state-checkable criteria.
