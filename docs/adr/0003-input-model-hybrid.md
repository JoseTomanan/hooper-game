# ADR-0003 — Input model: hybrid analog + discrete committed moves

- **Status:** Accepted
- **Date:** 2026-05-28
- **Superseded-by:** —

---

## Context

The game's identity is a skill-based duel where **spacing and commitment** are
the core interactions — closer to a fighting game than an arcade basketball game.
Two pure-input archetypes were considered:

1. **Fully analog / flow-based** — every action is a gradient; the player can
   cancel or redirect at any moment. This is the model of modern "smooth" sports
   games. Problem: it eliminates commitment. If every move is cancellable, there
   is no punish window and no mind game — spacing becomes irrelevant.

2. **Fully discrete / frame-data** — every action is a locked commitment with
   startup and recovery, like a traditional fighting game. Problem: there is no
   continuous neutral game. The spacing spine (separation creation vs. denial)
   requires a fluid movement layer underneath the committed moves.

Neither pure model fits a game whose identity is "the duel lives in the space
between two players."

## Decision

Use a **hybrid input model**:

- **Left analog stick** = movement, positioning, change of pace. This is the
  **continuous neutral game** — fluid, cancellable at any time, with no frame
  commitment. It drives the spacing spine.
- **Discrete buttons / right-stick gestures** = committed "break" moves
  (crossover, spin, hesitation, drive). Each has real **startup and recovery
  frames** — once initiated, it cannot be flow-cancelled. The right-stick surface
  is 2K-familiar but the moves resolve as locked-in commitments, not flow.
- **No flow-cancel on committed moves.** A committed move runs to completion (or
  to its punish window). This is the source of the mind game.

### Legibility is a technical requirement, not just style

Committed moves **must** have visibly telegraphed wind-up animations. Animation
may be deliberately clunkier than a "polished" game to keep moves readable by
the opponent. Do **not** smooth away startup frames in the name of feel — those
frames are the product.

## Consequences

**Easier:**
- The commitment/mind-game layer emerges naturally from the input model; it does
  not need to be bolted on separately.
- The right-stick surface is immediately legible to players who have touched 2K.

**Harder / accepted tradeoffs:**
- Startup and recovery frames must be faithfully represented in animation, not
  blended away. This requires deliberate animator discipline.
- Networking committed moves over a predicted/reconciled tick loop is harder than
  networking continuous analog input — committed moves are the hardest netcode
  case after the ball (Milestone 4).
- "Flow-cancel" feature requests from playtesters should be treated as
  contradicting the design identity, not as refinements.
