# ADR-0008 — Half-court 1v1 possession rules

- **Status:** Accepted
- **Date:** 2026-06-20
- **Superseded-by:** —

---

## Context

Milestones M2–M5 proved a single shot end-to-end: tipoff holder, dribble, a
committed shot, deterministic rim/backboard contact, and a scored basket with a
win condition. They stop exactly one shot in, by design. `BallController.TryShoot`
documents the ceiling: after `Shoot()` clears `HolderPeerId` to 0, only `Catch()`
can restore a holder, and nothing ever calls `Catch()` — so `TickLoose` settles
the ball dead on the floor forever. With `TargetScore > 1` the game can therefore
never finish: no player can rebound a miss, and possession never resets after a
make. M5 gave us scoring; it did not give us a *game*.

M6b (epic #46) turns that single shot into a continuous half-court 1v1. That
requires a possession ruleset — who gets the ball after a make, how a loose ball
is recovered, and what makes a basket count. Half-court 1v1 (pickup / streetball)
has a settled real-world convention, and the design identity (the duel; CLAUDE.md
§1) wants possession changes to *mean* something rather than be free. This ADR
records the ruleset every other M6b sub-issue (#48 rebound, #49 make-it-take-it,
#50 clear) implements. It changes no architecture — it sits on top of the locked
server-authoritative model (ADR-0002) and the deterministic mini-physics ball
(ADR-0004).

The forces:

- **Fidelity to the half-court duel.** The rules should be the ones two players
  on a real half-court would use, so the game reads as basketball, not an
  abstraction.
- **Server-authoritative legibility (ADR-0002).** Every possession outcome must be
  decided by the server and broadcast; clients may predict but never arbitrate.
- **Determinism (ADR-0004).** Any contest the client predicts (a rebound) must be
  resolvable from the same inputs on both sides, so prediction and the server
  agree and reconciliation stays clean.

## Decision

Adopt the standard half-court pickup ruleset, server-authoritative throughout.

**1. Make-it-take-it.** The scorer keeps possession for the next trip. On a
non-winning made basket the server hands the ball straight back to the scorer
(`_lastShooterPeerId`) as a fresh `Held` possession rather than leaving it loose;
a game-winning basket does not reset — the existing game-over freeze stands
(issue #49).

**2. Live rebound by proximity.** A missed shot leaves the ball `Loose` and in
play. Each tick the ball is loose, the server checks whether a player is within an
exported pickup radius and, if so, awards possession via
`BallStateMachine.Catch(peerId)` and broadcasts the new holder over the existing
`ReceiveState` path. When both players are within reach the **nearer** player
wins — a total order on a single scalar (distance), so the server's decision and
each client's prediction compute the same winner from the same positions, keeping
prediction clean (ADR-0002/0004). There is no dead-ball reset and no separate
check-ball step (issue #48).

**3. Take-it-back ("clear").** A basket only counts once the current possession
has been *cleared*: the handler must carry the ball behind a clear line — an
exported world-space threshold near the top of the key — before scoring. The
server tracks a per-possession `IsCleared` flag:

- Set **false** on every change of possession (a rebound, #48) and after every
  made basket (the make-it-take-it reset, #49). Every new possession starts
  uncleared.
- Set **true** the moment the current holder carries the ball behind the clear
  line.
- A `Make` detected while the possession is **not cleared** does **not** call
  `RegisterBasket`; the ball turns over instead of scoring (issue #50).

The `IsCleared` flag is treated as discrete server-authoritative state and
broadcast the same way `GameManager` broadcasts score — there is no meaningful
partial correction for a bool, so it is forced to match on clients with no
prediction channel, exactly the treatment the `GameManager` class doc already
gives score.

## Consequences

**Easier / what this buys:**

- The game becomes playable to any `TargetScore > 1` — the keystone that unblocks
  the M6b acceptance test (#52) and, downstream, the dedicated-server full-game
  verification (#32).
- Possession changes carry weight: the clear rule means a steal or rebound is not
  an instant put-back, which is what makes the spacing/commitment duel (CLAUDE.md
  §1) matter on defense as well as offense.
- It rides the existing plumbing. Rebound and make-it-take-it both reuse one
  server-side holder-assignment path (`Catch` + `ReceiveState` broadcast);
  `IsCleared` reuses the score-broadcast pattern. No new authority model, no new
  ADR for the netcode — this is application of ADR-0002/0004, not a change to them.

**Harder / accepted tradeoffs:**

- **New tunables become balance surface.** Pickup radius (#48) and the clear-line
  position (#50) are exported for editor tuning; their values are not settled by
  this ADR and will need play-testing. They are gameplay constants, not
  architectural commitments.
- **The clear rule adds a scoring pre-condition that must be visible.** A make
  that does not count because the possession was not cleared is confusing unless
  the player can see the cleared/uncleared state — hence the possession+clear HUD
  (#51). Legibility (CLAUDE.md §1) makes the HUD part of the rule, not optional
  polish.
- **Proximity rebound is intentionally simple.** "Nearer wins inside a radius" has
  no jostle, no box-out, no tip — those live in the spacing spine and are
  out of scope here. The simple rule is chosen precisely because it is
  deterministic and predictable; a richer contest would be a later, separately
  recorded decision.
- **No alternating possession / no inbound.** Make-it-take-it means a hot hand can
  keep the ball; that is the intended half-court tension, not a defect.

**Rejected alternatives:**

- **Loser's-ball / alternating possession** (give the ball to the non-scorer, or
  alternate). Rejected in favor of make-it-take-it: alternating possession is the
  organized-game convention, not the half-court-duel convention this design is
  built on, and it removes the "keep scoring to keep the ball" pressure.
- **Live rebound with no clear line.** Rejected: it lets a rebounder score
  instantly off the glass, erasing the possession change as a meaningful event and
  weakening the defensive half of the duel.
- **Auto-reset check-ball with no live rebound** (dead-ball after every shot, ball
  teleports to the new handler). Rejected: it skips the rebound contest entirely,
  removing a genuine 50/50 spacing moment and making misses consequence-free.

## Open engine-API items

None. This ADR introduces no new engine-facing calls — `Catch`, `ReceiveState`,
and the score-broadcast pattern all already exist and are proven. The exported
pickup radius and clear-line threshold are ordinary Godot `[Export]` fields.

---

## Amendment — 2026-06-21 (issue #46 "minor tasks before closing epic")

**Status remains Accepted.** The take-it-back rule is preserved; only its
application to make-it-take-it possessions is narrowed.

**Change to §Decision-3 (take-it-back / clear):** A possession that begins via
make-it-take-it (#49) now starts **cleared**. The original text stated "every
new possession starts uncleared" and §Consequences listed "no alternating
possession / no inbound" as a deliberate tension. Play-testing revealed that
forcing the scorer to also take it back on the possession they *just earned by
clearing* is double-punishment with no defensive purpose: the defender cannot
steal the ball mid-award, so there is no defensive moment the clear requirement
creates in this window. The take-it-back rule retains its full meaning on
**changes** of possession (rebound, steal, turnover), where the new holder is
the defender or scramble winner and the clear requirement is the barrier between
them and an instant put-back score.

**What did NOT change:**
- Rebounds (`TickLoose → AwardPossession`) still start uncleared.
- Take-it-back turnovers (uncleared make → defender gets ball) still start uncleared.
- The clear-line geometry, `IsCleared` broadcast path, and HUD are unchanged.

**Code:** `BallController.AwardPossession` accepts an optional `cleared` flag
(default `false`); `ResolveServerMake` passes `cleared: true` on the counting-make
path only.

---

## Amendment — 2026-06-28 (issue #63 out-of-bounds rule)

**Status remains Accepted.**

**New rule — loose-ball out-of-bounds:** A loose ball whose XZ position crosses
the play-court boundary is out of bounds. The play is dead; the server awards
possession to the player **opposite the last shooter** (`OtherPlayerPeerId(_lastShooterPeerId)`),
starting uncleared (same as any turnover — the new holder must take it back before
scoring, per §Decision-3 / Amendment 2026-06-21).

**Replaces the unconditional clamp at the play-court line.** `TickLoose`
previously clamped a loose ball back inside the rectangle so it could never leave.
That clamp survives only as a **fallback** for two cases:

1. **No opponent present** (solo editor test, defender == 0): with nobody to award
   the ball to, the clamp keeps the ball in play; losing it forever would break
   solo sessions. Mirrors the `ResolveServerMake` defender == 0 "leave loose" pattern.
2. **Non-server peers:** clients keep the clamp unchanged. The server's authoritative
   `ReceiveState` broadcast reconciles any divergence — identical to how all other
   possession changes propagate.

**Why server-gated (not predicted):** OOB is a dead-ball ruling, not a live
scramble. There is no 50/50 proximity contest to predict (unlike rebounds). Gating
the award on `IsServer` eliminates prediction-flip risk — two clients briefly
disagreeing on the new holder — for zero gameplay cost, exactly the reasoning that
gates `ResolveServerMake`.

**Scope is loose-ball only.** A player carrying the ball who steps out of bounds
is a natural later expansion; it is deliberately out of scope here. The holder is
already wall-bounded (StaticBody3D walls), so no holder-OOB case can currently
occur, and the design choice (who gets it — last holder or opponent) belongs to a
future held-ball-OOB decision, not this one.

**Rejected alternative — predict OOB like a rebound:** Rejected because OOB is
a dead ball. Predicting it on clients as if it were a live scramble would require
clients to resolve "who does OOB go to" from potentially stale remote-player
positions, opening a flip-possession window without any gameplay upside — there is
no contested recovery for clients to feel immediately.

**Code:** `CourtBounds.IsOutOfBounds` (added to `scripts/Ball/CourtBounds.cs`) is
the pure geometry check (XZ-only, boundary inclusive). `BallController.TickLoose`
checks `IsOutOfBounds` before running `ResolveLooseBallRecovery`; an OOB turnover
returns immediately so the rebound step is skipped that tick.

## Amendment — 2026-06-29 (held-ball OOB + in-flight termination)

**Status remains Accepted.** This amendment extends the 2026-06-28 rule from
loose-ball-only to *all* out-of-bounds cases and fixes the bug that let a missed
shot never end. It **supersedes the "Scope is loose-ball only" paragraph above**,
including its premise that "the holder is already wall-bounded … so no holder-OOB
case can currently occur."

**Walls are a far backstop, not the play boundary.** The scene's `StaticBody3D`
walls sit well outside the court rectangle (≈ X ±10 vs the court's X ±4.88), and
the deterministic mini-physics ball never consults them anyway (ADR-0004). The
*court line* (`CourtMin`/`CourtMax`) is the real boundary for both ball and player;
the walls only stop a player from running to infinity.

**New rule 1 — in-flight termination.** A shot/pass arc that makes no rim or
backboard contact (an air ball, a wide-scattered shot, a long pass) now ends the
`InFlight` state the moment the ball reaches the floor **or** crosses the court
line, via the pure `FlightTermination.ShouldGoLoose` predicate in the
`ContactResult.None` branch of `TickInFlight`. Previously such a ball integrated
forever — falling through the floor or sailing past the walls (the "ball
disappears" bug) — because the only containment, `CourtBounds`, lived in
`TickLoose`, which a never-terminating flight never reached. Once Loose, the
existing path resolves it (FloorBounce + rebound in bounds; the loose-ball OOB
award when out).

**New rule 2 — held-ball (player) OOB turnover.** When the player **currently
holding** the ball (state Held or Dribbling) crosses the court line, the server
awards possession directly to the opponent — a dead-ball turnover, starting
uncleared (the new holder must take it back before scoring). This is the
half-court 1v1 analogue of stepping out with the ball. Recipient: the **opponent
of the current holder** (`OtherPlayerPeerId(HolderPeerId)`) — chosen to match real
half-court rules and *Undisputed 3* (ADR-0014 reference authority), and distinct
from the loose-ball rule's "opposite the last *shooter*," because a live holder,
not a shot, is what went out.

**Mechanism — the `Turnover` edge.** A live possession cannot go through `Catch`
(legal only from InFlight/Loose). A new `BallStateMachine.Turnover` edge
(Held/Dribbling → Held-by-new-holder) models the dead-ball handoff — no loose
scramble. `AwardPossession` now selects the legal edge by current state
(Loose/InFlight → `Catch`; Held/Dribbling → `Turnover`), so one method serves both
the rebound/make awards and the OOB turnover.

**Recipient-eligibility gate.** The held-ball turnover fires only if the opponent
is a **live node AND in-bounds**. Two reasons: (1) if both players are OOB,
awarding to an also-OOB opponent would turn the ball straight back next tick — a
60 Hz possession strobe; gating on the recipient being in-bounds breaks the
ping-pong. (2) a disconnected/ghost opponent must not be handed the ball (it would
park at the origin). An ineligible recipient is passed as `0`, which
`OobResolution.Resolve` maps to no award.

**Why server-gated:** identical reasoning to the loose-ball rule — a dead-ball
ruling has no 50/50 contest to predict, so gating on `IsServer` removes
prediction-flip risk for zero cost. A dispossessed client keeps its predicted
possession for ≤1 RTT until the `ReceiveState` broadcast corrects it; any shot it
fires in that window is reconciled away (the accepted client-prediction cost,
ADR-0002).

**Known accepted limitations (surfaced by adversarial review, judged minor):**
- *One-tick ball position:* on the turnover tick the ball's broadcast position is
  still the old holder's hand (the state switch ran before the turnover check);
  `TickHeld`/`TickDribbling` re-centres it on the new holder the next tick. A
  sub-frame artefact, smoothed, consistent with the loose-ball award.
- *Shoot-while-OOB:* ~~shot release is processed before the OOB check, so a holder
  who crosses the line and releases on the same tick gets the shot off~~ **Resolved
  by Amendment 2026-06-29b (issue #120)** — an OOB release is now voided as a
  turnover before `Shoot()` runs. See below.

**Code:** `FlightTermination.ShouldGoLoose` and `BallStateMachine.Turnover` (both
unit-tested headless), `BallController.ResolvePlayerOutOfBounds` (server-only,
called before `UpdateClearStatus` so the clear check sees the new holder), and the
state-driven edge selection in `AwardPossession`.

## Amendment — 2026-06-29b (issue #120: void a shot released while OOB)

**Status remains Accepted.** Closes the *Shoot-while-OOB* edge the 2026-06-29
amendment deliberately deferred.

**The gap.** Shot release is applied in `ApplyShootLocally`, called from the
`TickHeld`/`TickDribbling` state switch — which runs *before*
`ResolvePlayerOutOfBounds` in `_PhysicsProcess`. So a holder who has crossed the
court line and releases on the same tick reached `StateMachine.Shoot()` →
`InFlight` (clearing the holder) *first*; the later player-OOB check then saw
`InFlight` and no-opped. A **make from an out-of-bounds release counted.**

**New rule — OOB at release is a turnover, not a shot.** Per the real-ball
authority (ADR-0014): the ball is dead the instant the handler is out of bounds,
so a shot released from out of bounds must not count. On the **server**,
`ApplyShootLocally` now checks `CourtBounds.IsOutOfBounds(HolderPosition())`
*before* `Shoot()`; if the holder is out of bounds it invokes the **same**
`ResolvePlayerOutOfBounds` the carry rule uses and returns without shooting.

**Why reuse `ResolvePlayerOutOfBounds` rather than a release-specific check:**
one OOB definition, one recipient-eligibility path (opponent must be a live node
**and** in-bounds), one ADR-0008 award, and the same no-award outcome when no
recipient is eligible. At the call site the state is still `Held`/`Dribbling`, so
that method resolves the turnover exactly as it does on a carry tick. This is the
right altitude: "released while OOB" and "carried while OOB" are the *same* dead
ball, not two rules.

**Boundary convention.** The court edge is **in-bounds** (`CourtBounds.IsOutOfBounds`
uses strict `<`/`>`), so a release with a toe on the line still scores — the
"toe-on-line" edge #120 raised is answered by the single existing definition, not
a new one.

**Why server-gated:** identical to the other two OOB rules — a dead-ball ruling
has no contest to predict. A predicting client still shoots locally and is
reconciled to the turnover by the next `ReceiveState` broadcast (the accepted
client-prediction cost, ADR-0002).

**Code:** `BallController.ApplyShootLocally` (server-gated guard before
`StateMachine.Shoot()`). Decision logic proven headless over the pure
collaborators (`CourtBounds.IsOutOfBounds` + `OobResolution.Resolve`) in
`OobShotReleaseTests` — the same Node-can't-run-headless pattern (ADR-0004) used
by `FlightTerminationIntegrationTests`.

## Amendment — 2026-06-29c (issue #118 part 2: pre-shot loose-ball OOB)

**Status remains Accepted.** A correctness clarification to the 2026-06-28
loose-ball OOB rule; the award logic is unchanged once a shot exists.

**The edge.** The loose-ball OOB award computes its recipient as
`OtherPlayerPeerId(_lastShooterPeerId)`. Before any shot has been fired
`_lastShooterPeerId` is its default `0`, and `OtherPlayerPeerId(0)` returns the
**first player in spawn order** (its parse loop merely skips id 0) — an arbitrary
award with no game context.

**Rule.** With no last shooter there is nothing to award *opposite of*, so the
recipient resolves to `0` → `OobResolution.ClampFallback`: the ball clamps and
stays in play rather than teleporting possession to a spawn-order-arbitrary
player. Real-ball: a loose ball with no possession history is nobody's turnover.

**Code:** `BallController.TickLoose` (`_lastShooterPeerId == 0 ? 0 : OtherPlayerPeerId(...)`
short-circuit). Pinned headless in `LooseBallOobRecipientTests`.

> **Note — issue #118 part 1 (last-shooter vs last-toucher) is NOT resolved here.**
> Switching the award key from last-*shooter* to last-*toucher* would reverse the
> deliberate 2026-06-28 amendment, so per ADR-0014 it is left as a design call for
> the human rather than self-resolved. This part-2 guard is forward-compatible
> with either outcome (a last-toucher id is likewise 0 before anyone has touched).

## Amendment — 2026-06-29d (issue #135: clear = a genuine take-back, not a static position)

**Status remains Accepted.** Tightens *how* §Decision-3's clear flag flips; the
when (every change of possession resets it) and the broadcast treatment are
unchanged.

**The gap (surfaced by the #57 doubt-driven retro-audit).** §Decision-3 reads
"Set true the moment the current holder **carries** the ball behind the clear
line," but the implementation was a static *position* test: each server tick it set
`IsCleared = true` whenever the holder was currently behind the line. So a player
who recovered a loose ball while **already** behind the line — an offensive rebound
of their own missed three from behind the arc — was cleared the same tick the
rebound reset it, and could put back another three with no take-back. That erases
the possession change as a defensive moment, the exact failure §Consequences
rejected under "Live rebound with no clear line."

**Rule — crossing-detection.** A possession clears only on a genuine take-back: the
holder must have been **inside** the clear line at some point during this
possession and then carry the ball **behind** it (inside → behind). Recovering the
ball while already behind the line is not a take-back and does not clear; that
holder must drive inside the line and bring it back out. This is the literal
reading of "carries … behind the clear line." Human design call (the lenient
"secured behind the line = cleared" real-ball convention was the considered
alternative); resolved toward the stricter take-back because the clear rule's whole
purpose is the defensive barrier against an instant put-back (CLAUDE.md §1; ADR-0014
real-ball + *Undisputed 3* authority).

**What did NOT change:**
- Every possession still *starts* uncleared on a change of possession, cleared on a
  make-it-take-it / tipoff (Amendment 2026-06-21 + tipoff pre-clear). The latch is
  moot for pre-cleared possessions — the clear check early-returns on `IsCleared`.
- `IsCleared` is still server-authoritative and never predicted; clients take it
  verbatim from `ReceiveState`. The new "has been inside this possession" latch is
  **server-only** state (only the server runs the clear check), so it cannot desync
  client prediction.
- The clear-line geometry, broadcast path, and HUD are unchanged.

**Code:** the decision is the pure, headless-tested `ClearLine.Advance(cleared,
hasBeenInside, …)` (crossing logic, `ClearLineTests`); `BallController` holds a
server-only `_holderHasBeenInsideClearLine` latch reset in `AwardPossession` and
advanced in `UpdateClearStatus`.
