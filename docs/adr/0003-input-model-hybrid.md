# ADR-0003 — Input model: hybrid analog + discrete committed moves

- **Status:** Accepted
- **Date:** 2026-05-28
- **Refined:** 2026-06-17 — anti-goal reframed from "polished sports-game feel" to
  *arcade decoupling of action from physical commitment*; polish is no longer
  treated as the enemy. See *Frame legibility* below.
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

### Frame legibility is a competitive requirement

A committed move must engage the **whole body**: feet plant, weight transfers, and
the action runs a startup → active → recovery arc that cannot be decoupled or
cancelled mid-way. This physical commitment does double duty — it is what makes the
game read as *real*, and it is simultaneously the source of the read-and-punish
mind game. Both players must be able to see the opponent's startup in real time; if
a move can fire without the body committing to it, the punish window disappears and
the core interaction collapses.

Realism and competitive integrity therefore point the **same** direction. We are
not trading one against the other — the body honestly committing is what buys us
both at once.

This is bounded on both sides:

- **The anti-goal — arcade decoupling.** Action that floats free of physical
  constraint: a shot released with the feet unplanted, moving and striking at the
  same time, any move that can be started and freely cancelled. This is the
  *arcadey* feel we are avoiding. It fails twice over — it looks unreal *and* it
  erases the commitment frames the duel is built on. **Smoothness/polish is not the
  enemy:** a highly polished animation is welcome *as long as* it honestly shows
  the body committing. The fault is decoupling action from the body, never
  rendering that action cleanly.
- **The other bound — manufactured jank.** Commitment must not be exaggerated into
  comedy or clunkiness-as-style. Weight and recovery exist only to the precise
  degree realism and legibility require — not as an aesthetic statement.

**Reference axis:**
- **Target — *UFC Undisputed 3*.** Strikes are committed: the body plants, the
  player is accountable to startup and recovery, and reads are fair because the
  commitment is visible.
- **Primary anti-target — recent *EA Sports UFC* (UFC 5).** Feet not planted on
  strikes, movement and striking decoupled, actions that cost no bodily
  commitment — it reads as arcadey and poorly grounded, and it would destroy the
  mind game.
- **Secondary anti-target — *Goat Simulator*.** The opposite failure: clunkiness
  as comedy/jank. Do not manufacture exaggerated clunk for its own sake.

Animation notes, playtest requests, or assets pushing toward either anti-target
contradict this decision; they do not refine it.

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
