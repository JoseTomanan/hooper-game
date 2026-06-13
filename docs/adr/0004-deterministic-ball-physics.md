# ADR-0004 — Ball physics: custom deterministic mini-physics, not engine physics

- **Status:** Accepted
- **Date:** 2026-05-28
- **Superseded-by:** —

---

## Context

The ball is a networked object. Its trajectory (arc, dribble bounce, rim
interaction) must produce identical results on the server and on every client
running local prediction — otherwise reconciliation produces visible snapping
and the game feels broken.

Two approaches exist:

1. **Use Godot's general physics engine (Godot Physics or Jolt)** for ball
   movement. Godot Physics/Jolt is not guaranteed deterministic across platforms,
   OS versions, or floating-point implementations. Physics engines are designed
   for visual plausibility, not bit-identical replay. Any divergence between
   server and client forces a correction, and frequent corrections make the ball
   feel jittery.

2. **Hand-author a deterministic mini-physics layer** — simple arc math for shot
   trajectories, authored bounce curves for dribbles, explicit rim/backboard
   collision math. All values are fixed-point or carefully bounded floats that
   produce the same result everywhere.

This decision is **engine-independent** and survived the move from S&box to Godot
unchanged. The reasoning is not about what Godot lacks — it is about what
determinism requires.

## Decision

Use a **custom deterministic mini-physics layer** for all owned ball moments
(dribble attach, shot arc, bounce, rim interaction). Do **not** use Godot
Physics or Jolt for these moments.

The ball is state-driven. Transitions between states (held, dribbling, in-flight,
loose) are explicit. The math for each state is hand-authored, self-contained,
and must be unit-testable without a running Godot instance.

## Consequences

**Easier:**
- Ball state is fully deterministic — server and clients always agree, so
  reconciliation is clean and the ball never snaps.
- The mini-physics code is self-contained and unit-testable outside Godot, making
  it the safest module to develop and verify in isolation.
- The "feel" of arc and bounce is explicitly authored, not emergent from a physics
  solver — designers control it directly.

**Harder / accepted tradeoffs:**
- Every ball interaction (new arc, new surface type, special rim behavior) must
  be hand-authored. There is no emergent physics variety for free.
- Godot Physics / Jolt must not be introduced for ball moments later, even if
  "just for visuals" — any physics-engine involvement risks non-determinism
  leaking into the authoritative state. If visual secondary effects are needed
  (ball spin, cloth, etc.), keep them purely cosmetic and client-side only,
  never part of the authoritative state.
