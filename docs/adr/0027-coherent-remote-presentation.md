# ADR-0027 — Coherent remote player and ball presentation

- **Status:** Proposed
- **Date:** 2026-10-18
- **Superseded-by:** —

## Context

Issue #414 reproduces an authored crossover endpoint mismatch. Repairing that
endpoint does not repair late remote phase observation: a valid LAN capture
first displays Startup partway through its window and moves a foot 0.3269 m
across two physics ticks, against the measured 0.24 m continuity bound.
Independent player and ball snapshots have no shared server tick. A local
within-phase clock cannot recover time already lost before a phase arrives
while adopting every following phase immediately.

On 2026-10-18 the human explicitly extended #414 to design a coherent snapshot
buffer for remote player and ball visuals, with its visible delay and effect on
the defensive read stated. This extends presentation under ADR-0002 and
ADR-0012; it does not delay authoritative HandSide, change defensive resolution,
add rollback or alter committed-move windows.

## Proposed decision

Render a client's remote players and non-local ball from one bounded history
of complete server-tick presentation frames, while gameplay consumes live state.

- The ball publishes one complete presentation envelope after its server
  physics/resolution step. Production Main processes Players before Ball.
  The envelope includes both players' position, velocity, heading, hand,
  move identity/parameter/phase/counter and cosmetic posture/cue state, plus
  ball position/state/holder/clear and dribble/sweep measurements. A shared
  monotonically increasing server physics tick identifies the whole frame.
- A separate authority-only reliable RPC on channel 1 preserves every
  presentation frame. Existing gameplay state/input/acknowledgement RPCs stay
  unchanged on their existing channels. Presentation head-of-line blocking
  holds a coherent view rather than blocking gameplay messages or discarding
  short move phases. It still shares connection bandwidth with gameplay.
- Begin with three contiguous future frames: 50 ms at the shipped 60 Hz.
  This provisional value covers the measured two-tick arrival variation with
  one tick of margin; Godot does not prescribe this magnitude. Tuning/feel
  remain #238/#173. Consume exactly one server frame per local physics tick.
  On underflow, hold the complete view and refill three contiguous frames;
  never invent a phase, skip a missing frame or accelerate a phase to catch up.
- Freeze selection once per client physics tick. All remote display reads,
  player mesh transforms, ball mesh transforms and visual cues use that same
  selection, even if more RPCs arrive later in the tick. Remote AnimationTree
  evaluation is manual and advances only with consumed presentation ticks;
  holding a frame must also hold neutral and non-crossover animation time.
  Initialization/resynchronization may reset animation playback to its sampled
  server state/time. Crossover samples the buffered phase counter directly.
- Retain at most sixteen envelopes. If the newest queued tick exceeds the
  displayed tick by more than twelve (200 ms at 60 Hz), explicitly resynchronize
  to three contiguous frames behind the newest tick. Count the reset and expose
  it to proof. This bounds queued display lag and memory; it permits a visible
  discontinuity after outages or sustained clock/cadence mismatch. No continuity
  claim includes a reset, uninitialized history or an arbitrarily stalled peer.
- The host and client's own predicted player remain live. A ball with either
  live or historical local ownership also stays on the immediate prediction
  path, preventing delayed possession from pulling it out of the local hand.
  Ownership handoffs are an explicit presentation boundary, not proof of
  continuous motion across arbitrary steals, shots or awards.
- Live body transforms, HandSide, Heading, velocity, raw opponent contact
  snapshots, possession, move machines, reconciliation, score and HUD signals
  retain their current semantics. Display-specific accessors read history.
  The ball's live sweep code must not consume a historical DisplayMoveId.
  Reconnect clears history; game-over freezes gameplay but continues draining
  presentation to the final server pose.

## Alternatives considered

1. **Only advance a cosmetic phase clock locally.** Implemented and measured;
   it fixes duplicate snapshot starvation but cannot recover late first entry.
2. **Restart a late phase at cosmetic frame zero.** Moves the missing travel to
   the next server phase boundary; it does not prove continuity.
3. **Clamp limb speed or broadly blend across phases.** Can make a displacement
   test green while delaying the body behind the ball or hiding the committed
   read. It does not establish a coherent physical action.
4. **Buffer independent gameplay RPCs.** They lack a shared server tick and do
   not contain dribble/sweep clocks. Delaying them would also contaminate raw
   opponent contact samples and reconciliation.
5. **Unreliable presentation frames without reconstruction.** Losing a short
   phase recreates this defect. Reliable delivery on a separate channel makes
   the hold/refill behavior explicit without inventing missing state.

## Consequences and verification

The remote duel is displayed with additional bounded buffering delay; the
server's outcome timing does not move. This is an accepted implementation
tradeoff to evaluate in #173/#238, not a claim of latency-neutral fairness or
human feel acceptance. The stream adds per-tick bandwidth and allocations.
Capture payload size and report the cost rather than assuming it negligible.

Proof must reproduce late Startup delivery deterministically, exercise both
hands/motion/repetition, assert matching player/ball presentation ticks and full
phase order/counters, and distinguish buffer holds/resets from rendering gaps.
It must also show unchanged live hand/phase/possession and local prediction,
hold ordinary animation time on underflow, drain after game-over, clear history
on reconnect, and exercise faster/slower producer clocks and bounded resets.
The ordinary build, full unit suite and exhaustive existing harness remain gates.

## Official API sources

- [Godot 4.7 custom interpolation for network timing](https://docs.godotengine.org/en/4.7/tutorials/physics/interpolation/physics_interpolation_introduction.html).
- [RPC authority, reliability, matching NodePaths and channel separation](https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#channels).
- [Physics callback ordering](https://docs.godotengine.org/en/4.7/classes/class_node.html#class-node-method-physics-process).
- [Manual AnimationMixer evaluation](https://docs.godotengine.org/en/4.7/classes/class_animationmixer.html#class-animationmixer-property-callback-mode-process).
- [State-machine playback initialization](https://docs.godotengine.org/en/4.7/classes/class_animationnodestatemachineplayback.html#class-animationnodestatemachineplayback-method-start).
- [Variant arrays and their reference semantics](https://docs.godotengine.org/en/4.7/classes/class_variant.html).
