# Committed-move reconciliation before contact integration

- Issue: [#368](https://github.com/JoseTomanan/hooper-game/issues/368)
- Authority: ADR-0002, ADR-0003, ADR-0025
- Measurement date: 2026-10-10

## Purpose

Characterize the existing reconciliation approximation before #370 activates
deterministic contact. This is a measurement of physics state, not a feel
assessment. Production movement, reconciliation, collision masks, and smoothing
remain unchanged by this investigation.

## Method

The instrument uses two real ENet processes and #356's `ContactHarnessSeam`,
which instances the shipped `Player.tscn` with real capsule geometry and scene
exports. Fresh processes isolate each motion/contact/delay condition. Server
input, state broadcast, client prediction, and reconciliation use the existing
`PlayerController` methods through integration-only partial-class seams.

Each client runs 180 physics ticks at 60 Hz. At client tick 25 the selected
move begins through the production begin method and request RPC; gesture
classification is bypassed. The defender is stationary for its first 89 server
ticks, then receives rightward input. Names are assigned before parenting and a
reliable roster barrier verifies matching RPC paths and cached peer identities.
Contact conditions must register actual opponent slide collisions, including
collisions during committed motion. No-contact conditions disable both players'
layers and masks and must register zero opponent collisions.

This isolates body motion: there is no ball, court, rim target, or animation
judgment. Gather therefore follows the initial heading rather than steering
toward a ball-provided rim target. It does not prove cradle, finishing, or feel.
Server and client counters begin on different arrivals of the Start message;
equal tick numbers must not be joined as simultaneous states. Sequence/ack
fields identify the input-buffer relationship instead.

Record the incoming authoritative snapshot, predicted body immediately before
correction, body immediately after replay, phase identities, replay length, and
the opponent's raw snapshot and displayed collider. The correction measurement
excludes the local mesh's smoothing offset. An incoming authoritative snapshot
is stale: its distance from the current client is **not** a same-tick
server/client error measurement.

Startup and Active observations are counted independently of replay length.
Separately, each move trial must show a nonzero body correction with nonempty
replay, retained committed-origin zero inputs, and a server snapshot identifying
the requested non-Inactive move. This does not require or prove Active-origin
inputs surviving in the buffer during a confirmed Active snapshot. Correction
is the total snap-plus-replay displacement, not a causal estimate of error
introduced solely by neutral replay.

An independent player in an isolated physics world evaluates the surviving
buffered inputs through the production neutral `Move()` method. The actual
reconciliation endpoint is compared with this neutral replay reference. A
targeted mutation of the production replay path must fail this comparison for
committed motion while preserving the neutral control.

The oracle equality gate applies only without contact (position and velocity
tolerance 0.0001). Cloning body transforms does not clone every internal solver
state; differences in the contact oracle are diagnostic and cannot prove that
production selects another replay path. Neither oracle reconstructs historical
opponent transforms.

The delay runner relays UDP traffic in both directions on loopback. It measures
the time each datagram spends in its forwarding queue using a monotonic clock;
that queue dwell is injected delay, not an estimate of total network RTT.

## Preconditions and sources

The measurement depends on Godot .NET 4.7.1, the shipped player scene, fixed
physics timestep, movement exports, committed frame data, and existing solver
contact. Changing those inputs invalidates the numerical baseline.

Before measurement, the code predicts a discriminating difference even without
contact. At 60 Hz and sufficient incoming speed, a zero-input `Move()` step
reduces speed by `70/60` m/s, while crossover Startup reduces it by `40/60`
and gather Startup by `45/60`. Thus neutral replay removes an extra 0.5 m/s
(crossover) or 0.4167 m/s (gather) in one tick: 0.00833 m or 0.00694 m less
displacement for that isolated tick. These are conditional analytic differences,
not predictions of total network correction. Active bursts introduce another
difference because neutral replay never recomposes their entry impulse.

- [Godot 4.7 C# ENet and RPC setup](https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#remote-procedure-calls): matching node paths and the production RPC transport.
- [CharacterBody3D.MoveAndSlide](https://docs.godotengine.org/en/4.7/classes/class_characterbody3d.html#class-characterbody3d-method-move-and-slide): consumes the engine physics timestep and can change velocity.
- [Viewport.OwnWorld3D](https://docs.godotengine.org/en/4.7/classes/class_viewport.html#class-viewport-property-own-world-3d): isolates reference capsules from the live simulation.
- [Input.action_press](https://docs.godotengine.org/en/4.7/classes/class_input.html#class-input-method-action-press): supplies the movement action read by production input polling.
- [Python 3.14 socket.sendto](https://docs.python.org/3.14/library/socket.html#socket.socket.sendto) and [time.monotonic](https://docs.python.org/3.14/library/time.html#time.monotonic): datagram forwarding and elapsed-time measurement.
- [Python 3.14 Popen](https://docs.python.org/3.14/library/subprocess.html#subprocess.Popen): child ownership, bounded waits, and hidden Windows execution.

## Results

The final instrument (`e053627`, based on `152aaf8`) passed all **24 trials**:
two fresh runs of each of twelve conditions, producing **3,764 reconciliation
observations**. Runtime: Godot 4.7.1 .NET, .NET SDK 8.0.421, Python 3.14.0,
Windows, 60 Hz physics. Both directions forwarded actual ENet traffic. For the
zero-delay relay, per-trial directional mean dwell ranged from 0.020 to 0.039 ms
(maximum individual dwell 0.185 ms). For requested 30 ms each way, mean dwell
ranged from 32.299 to 34.629 ms, minimum individual dwell was 30.030 ms, and
maximum was 46.000 ms. The requested injection is 60 ms round-trip; neither that
number nor these queue means includes the rest of the ENet/physics RTT.

The table reports the range of the **two per-trial peak body corrections** in
server-confirmed Startup/Active (neutral uses Inactive), in metres. Buffer
maximum is across the whole trial. Display lag is the range of two peak distances
between the latest raw opponent snapshot and its displayed collider, not network
error. Counts separated by `/` belong to the two independent trials.

| Motion | Contact | Injected ms/way | Rows | Peak correction (m) | Max buffer | Peak raw/display distance (m) | Server committed opponent hits |
|---|---|---:|---:|---:|---:|---:|---:|
| neutral | contact | 0 | 178/163 | 0.150–0.188 | 4 | 0.333–0.333 | 0/0 |
| neutral | contact | 30 | 155/168 | 0.200–0.224 | 8 | 0.433–0.505 | 0/0 |
| neutral | no-contact | 0 | 174/136 | 0.175–0.200 | 2 | 0.333–0.446 | 0/0 |
| neutral | no-contact | 30 | 174/125 | 0.200–0.287 | 8 | 0.333–0.616 | 0/0 |
| crossover | contact | 0 | 140/141 | 0.314–0.489 | 4 | 0.438–0.528 | 11/1 |
| crossover | contact | 30 | 167/152 | 0.477–0.676 | 8 | 0.435–0.468 | 12/13 |
| crossover | no-contact | 0 | 149/146 | 0.019–0.058 | 3 | 0.428–0.438 | 0/0 |
| crossover | no-contact | 30 | 116/168 | 0.844–0.844 | 8 | 0.435–0.631 | 0/0 |
| gather | contact | 0 | 156/158 | 0.489–0.489 | 4 | 0.333–0.428 | 1/1 |
| gather | contact | 30 | 159/173 | 0.154–0.710 | 9 | 0.433–0.509 | 1/15 |
| gather | no-contact | 0 | 156/175 | 0.175–0.176 | 4 | 0.333–0.435 | 0/0 |
| gather | no-contact | 30 | 164/171 | 0.815–0.815 | 9 | 0.333–0.333 | 0/0 |

All no-contact oracle position and velocity discrepancies were zero. Production
reconciliation therefore matched neutral `Move()` replay, including windows
retaining committed-origin zero inputs. Nonzero body corrections and the
different neutral/committed per-tick deceleration already exist without contact.
The 0.844 m crossover and 0.815 m gather peaks also occur without contact; they
cannot be blamed on the future deterministic contact policy. They include
transport, phase gates, authoritative snap, and replay effects, so the numbers
do not assign the whole correction to one mechanism. Contact comparisons are
descriptive, not same-tick paired causal estimates.

The raw/display separation reaches 0.631 m in these conditions. Production
currently collides against the displayed capsule; substituting raw snapshots in
#370 removes that display dependency but cannot reconstruct historical opponent
motion across an eight- or nine-input replay window. Contact oracle discrepancies
are retained in the artifact without an equality claim.

### Control and phase findings

A targeted mutation replaced the production reconciliation loop's unconditional
`Move()` call with committed behavior while the local machine was active, and
neutral `Move()` otherwise. Delayed no-contact crossover failed at client tick
26 with a **0.147222 m** position and **2.166666 m/s** velocity mismatch against
the neutral oracle. Its neutral control passed with 171 observations and zero
oracle discrepancy. The failure was the oracle assertion in the retained client
log; Windows subsequently reported a UDP endpoint error as the failed peer
exited. The production source was restored byte-for-byte and rebuilt before the
final trials. This establishes discrimination between replay paths; it does not
make the mutated branch a correct historical committed replay implementation.

Two earlier evidence gates incorrectly required phase and buffer conditions to
overlap. The retained delayed no-contact gather trial (`9d09616`) received a
stale server Inactive snapshot at client tick 31: local Active became Inactive
with six committed-origin inputs still buffered. Later server Startup snapshots
had qualifying committed corrections; later Active snapshots had none of those
inputs left. Separately, baseline no-contact crossover (`504f47d`) observed
server Active with a fully acknowledged, empty buffer. Both are real production
observations, not reasons to silently rerun a condition. The final gates separate
phase observations from the independently required committed correction.

The final matrix also captured stale-phase cancellation in delayed contact gather
repeat 1. `ReconcileFromServer`'s existing `ShouldForceInactive` branch cancels a
local Active/Recovery machine on an incoming Inactive snapshot, and does not
reconstruct the move from a later confirming snapshot. This pre-existing behavior
is separate from neutral-only positional replay and remains unchanged here.

The committed [measurement artifact](0368-committed-replay-results.json) contains
all final per-trial summaries, premise counts, selected complete state rows,
mutation results, and both earlier gate failures. Raw complete JSONL and
native/stdout logs remain in the ignored `.godot/committed-replay/` output and
can be regenerated with the command below.

## Contact integration constraint

ADR-0025 records two distinct limitations: committed ticks are replayed through
neutral movement, and replay has only the latest opponent snapshot. The latter
cannot reconstruct the opponent's historical trajectory across the replay
window. This investigation does not implement snapshot history or rewrite the
committed replay path. The integration constraint follows from those limitations
and the measured results above.

For #370, account for the documented approximation explicitly: evaluate the same
pure contact policy in neutral `Move()` and committed execution using the latest
**raw** opponent snapshot, and apply depenetration on every movement path,
including neutral reconciliation replay. Masking solver contact by itself is
insufficient. Add contact-removal controls and delayed committed-motion trials
that prove non-overlap and finite state through corrections. A neutral replay
oracle match is evidence of the current path, not evidence that contact replays
the committed trajectory exactly.

These pre-contact measurements do not demonstrate a production replay rewrite
as a necessary prerequisite for #370. ADR-0025 records both limitations and
assigns replay-path asymmetry its own investigation. They do constrain #370's
verification: the next task cannot require
same-phase client/server agreement or exact historical contact reconstruction,
nor infer convergence or acceptable feel from a single peak correction. If
#370's implemented contact cannot meet its non-overlap/correction criteria under
this approximation, that failed criterion supplies the corrective prerequisite
and must become a blocker before integration closes. No corrective production
issue is asserted on the strength of pre-contact measurements alone.

## Reproduction

From the repository root in PowerShell, with `GODOT` pointing to the pinned
4.7.1 .NET console binary:

```powershell
dotnet build 'HOOPER GAME.csproj' -p:IncludeIntegrationHarness=true
python tools/run_committed_replay.py --godot "$env:GODOT" --all --repeat 2 --output .godot/committed-replay/reproduction
```

Each condition owns fresh peers and sockets. Server readiness is bounded to
10 seconds and peer execution to 30 seconds after client launch; owned children
are killed and reaped on failure. Both exit-zero and PASS markers are required.
The runner retains client/server stdout, workspace-local native logs, JSONL
state rows, measured relay dwell, and premise counts in stdout. The executable
harness catalog runs all twelve conditions in CI and preserves contact/control
groups together. Exact numbers vary with ENet delivery and OS scheduling;
repeatability here means a bounded procedure and independently checked premises,
not bit-identical network timing.
