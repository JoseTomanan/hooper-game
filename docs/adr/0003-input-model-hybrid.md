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
- **Refined:** 2026-07-03 (#172) — human-authorized, bounded relaxation of the
  back-turn legibility requirement for the movement-stick pivot specifically:
  the commitment read moved from raw turn-rate cost into the plant-then-pivot
  gate itself. See *Back-turn legibility relaxed for the movement-stick pivot*
  below; cross-linked with the ADR-0010 amendment of the same date.
- **Refined:** 2026-07-04 (M9, #198) — a committed move's Startup plant may now
  BLEED momentum via a bounded hard decel instead of instant-zeroing it, and
  Active may REDIRECT the survivor with a left-stick-driven exit rather than
  firing a fixed burst. Bounded and scoped to the moving crossover only — see
  *Bounded momentum retention through Startup's plant* below.
- **Refined:** 2026-07-06 (M9, #194) — a burst-family move may narrow its
  left-stick exit vector to a CONE tighter than the forward hemisphere the
  #198 amendment already bounds it to, as its ONLY "fewer follow-ups" cost —
  never a recovery-frame or chain/cooldown penalty. See *Exit-cone width as
  the sole "fewer follow-ups" lever* below.
- **Refined:** 2026-07-20 (M9/M10, #189) — human design ruling: the "no
  flow-cancel" rule also covers **external-event** interruption, not just
  player-initiated cancellation. A committed move whose payload an external
  event (a steal or OOB carry-turnover) has made structurally impossible still
  runs to completion; the wasted planted time IS the punishment. See *External
  events do not interrupt a committed move* below.
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

`Feint()` was **kept for now** (it was wired and harmless) but was flagged here
as **non-realistic and slated for reconsideration** — do not build new dribble
mechanics on top of the recall model. **That reconsideration is closed by the
amendment immediately below (M9, #202).**

### The in-and-out closes the dribble-move feint reconsideration (M9, #202)

The reconsideration flagged above is now resolved: the **in-and-out** is the
realistic replacement for the recall model, and `Feint()` on a dribble move
(Crossover, BehindTheBack) is removed rather than kept alongside it.

**Why the in-and-out, and not merely "keep the recall around" (ADR-0014 tier-2
citation — real half-court ball):** the reason a combat-sport feint doesn't
apply to a dribble move is stated two paragraphs up — "once the ball is headed
to the floor you physically cannot pull it back." That reasoning does **not**
bite the in-and-out: in a real in-and-out dribble, the ball **never crosses
over**. The hand rides the outside of the ball, pushes it out toward the fake,
and the **same** hand recovers it inside on one continuous dribble cadence —
there is no ball-in-flight to "pull back," because nothing was ever released
toward the other hand. It is the one dribble fake that is physically honest,
and therefore the realistic **replacement** for the recall model this ADR
already said was coming, not a new mechanic bolted on top of it.

Mechanically, `InAndOut` (`scripts/Input/InAndOut.cs`) is Crossover's burst
composed through the SAME `CrossoverBurstMath.ComposeActiveVelocity` at a
reduced magnitude, plus Hesitation's "no hand swap" rule — no new burst math,
no new wire state, no new committed-move machinery. The right-stick quick-return
gesture (`GestureKind`, renamed from `Feint` to `QuickReturn` — a pure rename,
not a new grammar cell) now BEGINS this move (or a Hesitation, by the same
`HandStateResolver.IsCrossover` hand-state read the held gesture already uses)
instead of aborting whatever move was in Startup.

**What changed:**
- `Crossover.DefaultFrameData` and `BehindTheBack.DefaultFrameData`:
  `feintWindowFrames` 4 → **0**. Both dribble moves are now **structurally
  unfeintable** — `CommittedMoveMachine.Feint()`'s own window guard already
  refuses everything when `feintWindowFrames == 0` (no special case added).
- `FeintGateResolver` (the /diagnose 2026-07-03 bug-fix class that withheld the
  ambiguous gesture-sourced feint from an in-progress `JumpShot`) is **deleted**,
  not kept as dead code. Its whole reason to exist was gating an AMBIGUOUS
  gesture that could reach either a harmless dribble-move abort or a
  shot-eating `JumpShot` pump-fake. Once the gesture no longer calls `Feint()`
  at all (it calls `Begin()` on an `InAndOut`/`Hesitation` instead), there is
  no ambiguity left to gate: an incidental aim-stick flick during an
  in-progress `JumpShot`'s Startup now attempts `Begin(InAndOut)` against a
  non-`Inactive` machine, which `CommittedMoveMachine.Begin()` already refuses
  as a silent no-op (its documented contract, unrelated to this issue) — the
  exact bug class `FeintGateResolver` existed to prevent is now prevented
  structurally, by the same mechanism that already refuses a second `Begin()`
  on any in-progress move, not by a gate someone has to remember to keep
  calling. `CommittedMoveMachine.Feint()` itself is UNCHANGED and still serves
  `JumpShot`'s pump-fake (#77) via the explicit `move_feint` key — the only
  input path left that ever calls it.

**Rejected alternatives (recorded so a future session does not silently
re-open this):**
- **Treat the crossover's existing feint as already being the in-and-out.**
  This was the ORIGINAL (2026-07-04) deferral rationale for issue #202 itself,
  and it is wrong on the facts: `Crossover`'s `feintRecoveryFrames` is 0, so
  `Feint()` routes Startup → Inactive with **zero** separation, zero recovery,
  zero cost — that is the size-up (a free abort), not a move that beats a
  defender. Equating the two would equate a scoring move with a no-op.
  Rejected 2026-07-16 (see issue #202's un-deferral).
- **Keep `Feint()` available on Crossover/BehindTheBack alongside the new
  in-and-out** (belt-and-suspenders). Rejected: this ADR already named the
  recall non-realistic; now that a realistic, honest replacement exists,
  keeping the unrealistic one alongside it serves no design purpose and
  re-opens the exact "free zero-cost abort" hole the in-and-out was filed to
  close. A dribble move's only legitimate "I didn't mean that" tool is now the
  in-and-out itself (which is not free — it pays a real Startup→Active→Recovery
  cost) or the Hesitation it forks into from the ball-hand side of the flick.
- **Build `InAndOut` as a parameter/flag on `Crossover`** rather than its own
  `CommittedMove` subclass. Rejected for the same reason `BehindTheBack` (#194)
  and `BetweenTheLegs` (#199) already were: composition over hierarchy — all
  three share the SAME `CrossoverBurstMath` composition with different
  tunables/behavior flags, rather than one subclassing another or a shared
  base carrying move-specific behavior (`docs/handoffs/M9-move-taxonomy.md`).
- **Give `InAndOut` its own feint window.** Rejected: a fake of a fake is
  incoherent, and this ADR's own dribble-move recall ban (closed above) rules
  it out on the same grounds a Crossover/BehindTheBack feint window now is.
  `InAndOut.DefaultFrameData` sets `feintWindowFrames: 0` as a design constant,
  mirroring `Hesitation`/`JabStep`'s identical reasoning.

A request to re-add a feint window to any dribble move, or to reintroduce
`FeintGateResolver` as a gate on some future ambiguous gesture, contradicts
this closed reconsideration; it does not refine it.

### Back-turn legibility relaxed for the movement-stick pivot (#172)

The *Frame legibility is a competitive requirement* section above ties
legibility directly to raw commitment cost — historically, "slow" was the only
knob the back-turn had, via ADR-0010's non-linear turn rate. Issue #172 (human
design call, exercised under ADR-0014's design authority) re-examined that
back-turn against the *NBA 2K* pivot rather than a pure-realism or pure-esport
reference, and made a bounded, explicit trade: **arcadeness allowed,
competitiveness deferred**, scoped narrowly to the movement-stick reverse-pivot.

What changed and what didn't:

- `BackTurnSlowFactor` moved from 0.35 → 0.90 (#172), then → 0.95 with
  `MaxTurnRateDeg` 530 → 900 (a #172 follow-up feel pass), so the raw yaw rate
  itself is no longer the primary legibility carrier for a back-turn.
- In its place, `HeadingMath.Step`'s new flick-to-latch **plant-then-pivot
  gate** (ADR-0010's #172 amendment) now carries the commitment read: a facing
  change past `PivotThresholdDeg` (90°) plants the feet — zero displacement,
  `Velocity` forced to `Vector3.Zero` — for the pivot's whole duration, and a
  committed move (e.g. a defender's steal) cancels an in-progress pivot rather
  than letting it silently coexist with a punish window.
- The result is a **faster-resolving but still honestly committed** pivot
  (≈0.20 s full 180° after a #172 follow-up feel pass, down from the pre-#172
  ≈0.55 s) — not the pre-ADR-0010 *instant*
  pivot this ADR's Context section names as the original arcade-decoupling
  problem. The plant is still a real, server-authoritative, observable cost;
  only *which mechanism* carries that cost changed (frozen feet, not slow yaw).

This is deliberately scoped to the **movement-stick reverse-pivot only**. It
does not touch the right-stick binary-trigger model, the no-flow-cancel rule
for committed "break" moves, or the Startup/Active/Recovery frame-data
contract for crossovers, hesitations, jump shots, or defensive reads — those
retain their full, un-relaxed legibility requirement. A request to extend this
relaxation to any other committed move contradicts this note's scope; it does
not refine it. See the ADR-0010 amendment of the same date for the mechanical
detail (`PivotState`, the plant gate, and the re-rejection of visual-only
heading) this note's design-authority record complements.

### Bounded momentum retention through Startup's plant (M9, #198)

Since M3, every committed move's Startup phase has hard-zeroed `Velocity`
every tick: `Velocity = Vector3.Zero`. This reads as an honest, fully-planted
commitment — but for the moving crossover it produced a real gap in the move
taxonomy (`docs/handoffs/M9-move-taxonomy.md` §2, grilled 2026-07-04): a
player driving at full speed who throws a crossover was instantly and
completely dead-stopped by the plant, making "drive → cross → change
direction" — a real, common basketball move — mechanically impossible. Only
"stand still, plant, shuffle sideways" could ever be expressed.

**Decision: hybrid gather (model C).** Startup now BLEEDS momentum via a hard
decel (`PlayerController.GatherDecel`, tuned steeper than the open-field
`Decel` but bounded so it does not always fully zero within the Startup
window) instead of an instant zero. Whatever momentum survives is what
Active's burst redirects — never re-added to, never re-zeroed. This is
recorded here, not silently implemented, because it visibly touches this
ADR's central anti-goal:

- **Why this is NOT the arcade-decoupling anti-goal.** The anti-goal is
  action decoupled from bodily commitment — a strike that fires with the feet
  unplanted, or a move that costs nothing to throw. Bounded momentum
  retention does not remove the plant: Startup still visibly decelerates the
  body (a fast player is CLEARLY seen slowing down into the plant, frame by
  frame, exactly as legible as the old instant-zero), and the deceleration
  rate is a tuned, finite bleed — not free carry-through. What changes is
  only WHETHER the plant is allowed to leave a residue of momentum once it
  completes, not whether the plant itself is honestly rendered. The
  commitment read (defender sees the startup frames; the body pays a real,
  visible cost) is unaffected.
- **Why this is bounded, not general-purpose.** This amendment applies to the
  moving crossover's Startup→Active handoff specifically (and, by the shared
  `CommittedMove` skeleton, any future move built the same way — see M9's
  behind-the-back/between-the-legs/spin sub-issues, which are explicitly
  speced to reuse this same momentum model). It is NOT a general license to
  soften every committed move's plant: the gather-bleed is explicitly
  scoped to the Crossover (and, by the same skeleton, future burst-family
  moves that opt into it) — `TickCommittedMoveBehavior`'s Startup branch
  gates on `_machine.CurrentMove is Crossover`, and every other move
  (Hesitation, StealMove, JumpShot, …) keeps the pre-#198 instant-zero
  plant. Code review (fix round) caught this ADR text previously claiming
  the opposite for the JumpShot — that the bleed rule "is not special-cased
  away for it." The code was right and the prose was wrong: a JumpShot's
  Startup hard-zeroes every tick exactly like it always has, and that is a
  stationary-commitment move that keeps the instant plant, not a
  bleed-then-redirect one. This amendment also does not touch Recovery
  (still a hard decel-to-zero punish window) for any move, including the
  Crossover itself.
- **Exit direction is separately bounded (model A, "snapshotted at
  Active-entry").** The left stick shapes WHERE the surviving momentum + burst
  go, but only at the single moment Active begins — it PARAMETERIZES the
  move (which direction the already-committed burst takes), it does not let
  the player re-decide mid-Active or cancel the move. Startup/Active/Recovery
  stay intact and un-flow-cancellable exactly as this ADR already requires;
  see `CrossoverBurstMath` (scripts/Player/CrossoverBurstMath.cs) for the pure
  composition this produces.

A request to extend momentum retention to a move's PUNISH window (Recovery),
or to let the exit vector be re-read continuously through Active (turning it
into a steerable, flow-cancellable action), contradicts this amendment's
scope; it does not refine it.

### Exit-cone width as the sole "fewer follow-ups" lever (M9, #194)

BehindTheBack (issue #194) is a deliberately SAFER sibling of the moving
crossover: "a slightly safer crossover — less stealable, less explosive,
fewer follow-up options" (human framing, `docs/handoffs/M9-move-taxonomy.md`).
Three of those four properties are ordinary tunables (smaller burst speed,
steeper Startup gather-bleed, a shielded ball-transit path) that need no ADR
amendment — they are just different numbers on the same #198 model. The
fourth, "fewer follow-ups," is the one that touches this ADR: it is modelled
**exclusively** as a NARROWER exit cone — `CrossoverBurstMath.
ComposeActiveVelocity`'s left-stick exit vector is clamped to within a tuned
half-angle of the player's forward axis (`PlayerController.
BehindTheBackExitConeDegrees`, 50° by default) before being decomposed into
forward/lateral burst contributions, rather than the effectively-unclamped
~180° cone the plain #198 amendment already left every OTHER burst-family
move (Crossover) with.

Two alternatives were explicitly considered and **rejected** by the human
during the M9 taxonomy triage (2026-07-04), and are recorded here so a future
session does not silently re-introduce either:

- **Longer Recovery.** Rejected — contradicts "safer / lower-commitment": a
  move that costs LESS to commit to (smaller burst, less stealable) should
  not also cost MORE to recover from. BehindTheBack's Recovery is instead
  tuned comparable-to-or-shorter-than Crossover's (10 ticks vs. 12).
- **Chain/cooldown restrictions** (2K-style artificial combo lockout).
  Rejected under ADR-0014's reference ranking — neither real half-court ball
  nor *Undisputed 3* gate a dribble move by an artificial cooldown; that is a
  2K-taxonomy pattern this project explicitly does not follow for this call.

**Why a narrower cone is a legitimate "commitment" lever and not arcade
decoupling:** the anti-goal this ADR polices is action decoupled from bodily
commitment (an unplanted strike, a free-to-throw move). A narrower exit cone
does not touch the plant, the Startup telegraph, or the burst's existence —
it only bounds WHICH directions the player's own left-stick steering can
redirect the already-committed burst into, once Active begins. The body still
visibly plants, still visibly bursts; the player just has less choice over
where. This is squarely within the "exit direction parameterizes, never
cancels" bound the #198 amendment already established (model A) — narrowing
the parameter's domain is not a new kind of relaxation.

**Implementation note:** `ComposeActiveVelocity` gained an optional
`maxExitAngleRadians` parameter for this (`scripts/Player/
CrossoverBurstMath.cs`), defaulting to an unclamped 180° so every pre-#194
Crossover call site is bit-for-bit unaffected — the cone is a parameter on
the SAME shared pure function, not a forked copy of the math (composition
over hierarchy, matching BehindTheBack's own "own CommittedMove subclass,
not a Crossover flag" structure).

### External events do not interrupt a committed move (M9/M10, #189)

The Context and Decision above frame "no flow-cancel" against
**player-initiated** cancellation — you cannot bail out of your own committed
move because you changed your mind, and that absence is the mind game. Issue
#189 surfaced the adjacent, previously-unaddressed case: an **external** event
during a committed move's Startup/Active — a defensive steal during the
dribble gather (ADR-0018 #96) or a carry-OOB turnover (ADR-0008 #63/#118) —
takes the ball away and makes the move's payload structurally impossible (a
`JumpShot` whose release can never resolve, because `BallController` reads the
ball's CURRENT `HolderPeerId`, which is no longer the shooter). Nothing on
`CommittedMoveMachine` reacts to this; the windup → active → recovery ticks to
completion regardless, and the shot animation plays with no ball ever leaving
the hand.

**Ruling (human, 2026-07-20): this is intended behavior, not a gap.** The
committed move runs to completion even when an external event has already
voided its payload. The player is planted for the move's full duration and
loses that time — and **the lost time is exactly the punishment** for having
committed to a move the defender read and broke up. This applies to **all**
committed moves, not just `JumpShot`.

**Why this is a scope clarification, not a contradiction:** the "no
flow-cancel" principle exists so that committing to a move is a real,
punishable bet. An external interrupt that shortcut the animation would
*refund* that bet — the moment a steal landed, the loser would instantly get
their body back and their time returned, erasing the very punishment the read
earned. Letting the move play out is therefore not a grudging side effect of
having no `Cancel()`; it is the same commitment principle the ADR already
states, now confirmed to hold whether the move ends because it finished, because
the player was punished, or because the ball was taken. The reality reference
agrees (ADR-0014 tier-1): a stripped shooter does not teleport back to neutral;
they finish the useless shooting motion while the defender is already gone.

**Rejected alternative — an external-event interrupt/abort carve-out
(triage's staged recommendation).** Add some form of `Interrupt()` to
`CommittedMoveMachine` that a possession change fires, so the animation can
abort or show a "whiff" the instant the ball is lost. Rejected by the human
ruling above: it refunds the commitment (see the paragraph above) and buys
nothing the status quo lacks — no phantom basket can score today, because the
holder check already fails once possession moves. Any *cosmetic* whiff cue is a
separate **feel** question, deferred to the consolidated human feel pass (#173,
ADR-0021), not a mechanics change. A request to add an external-event cancel
path to `CommittedMoveMachine` contradicts this ruling; it does not refine it.

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
