# ADR-0003 — Input model: hybrid analog + discrete committed moves

- **Status:** Accepted
- **Date:** 2026-05-28
- **Refined:** 2026-06-17 — anti-goal reframed from "polished sports-game feel" to
  *arcade decoupling of action from physical commitment*; polish is no longer
  treated as the enemy. See *Frame legibility* below.
- **Refined:** 2026-06-28 (M9, #85/#86) — added the *no stick free-aim* anti-goal
  (the right stick is a binary L/R trigger, not an analog aim) and the design note
  that *combat-sport feints do not apply to dribbling*. See *Right-stick is a
  binary trigger* and *Feints vs. hesitation* below.
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

### Right-stick is a binary trigger, not an analog aim (M9, #85)

The right-stick committed-move gestures are read as a **binary left/right
trigger only** — the sign of the horizontal flick, nothing more.
`RightStickGestureRecognizer` already collapses the stick to `±1` and ignores
magnitude and the vertical axis; that is deliberate and **must stay that way.**

- **Anti-goal — burst toward wherever the stick points.** There is no "free-aim"
  analog burst. A crossover/hesitation does not fire in an arbitrary stick-chosen
  direction; the flick selects *which side* (the player's left or right), and the
  resulting **world** direction follows the player's server-authoritative heading
  (ADR-0010) — the burst follows the body, not the screen and not a free analog
  vector. Free-aim dribble bursts are not basketball-realistic and would dissolve
  the legible, committed read this ADR is built on.
- **Heading-relative, not camera/screen-relative.** For a fixed half-court camera,
  "the player's left/right" (heading-relative) is the meaningful interpretation of
  a directional flick. Stick-right while facing forward bursts world-right; stick-
  right while facing reversed bursts world-left.

A request to "let the stick aim the dribble" contradicts this decision; it does
not refine it.

### Feints vs. hesitation — combat-sport feints do not apply to dribbling (M9, #86)

A traditional combat-sport **feint** recalls/aborts a committed strike inside its
startup window — you wind up a punch and pull it back. `CommittedMoveMachine.Feint()`
(Startup → Inactive within the feint window) models exactly that recall.

That recall is **not basketball-realistic for a dribble move:** once the ball is
headed to the floor you physically cannot pull it back. The realistic fake is a
**hesitation** — its own honest dribble setup (a freeze/stutter that baits a
reaction), **not** "a crossover you cancelled." The hesitation is therefore a
distinct committed move (no hand-swap, no scripted burst; the player drives the
exit with the left stick), *not* a `Feint()` of a crossover.

`Feint()` is **kept for now** (it is wired and harmless) but is flagged here as
**non-realistic and slated for reconsideration** — do not build new dribble
mechanics on top of the recall model.

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
