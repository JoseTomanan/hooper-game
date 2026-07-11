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
walls sit well outside the court rectangle (≈ X ±10 vs the court's X ±7.62), and
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

> **Note — issue #118 part 1 (last-shooter vs last-toucher) is now resolved by
> Amendment 2026-06-30 below.** The award key moved from last-*shooter* to
> last-*toucher*; this part-2 guard carried over unchanged (a last-toucher id is
> likewise `0` before anyone has touched the ball), now keyed off
> `_lastToucherPeerId` instead of `_lastShooterPeerId`.

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

## Amendment — 2026-06-30 (issue #118 part 1: last-toucher, not last-shooter)

**Status remains Accepted.** Corrects *who* the loose-ball OOB turnover is awarded
to (the 2026-06-28 rule). The when (a loose ball crossing the play line is a dead
ball), the server-gating, and the clamp fallbacks are all unchanged.

**The fidelity gap.** The 2026-06-28 amendment awarded the ball **opposite the last
shooter** (`OtherPlayerPeerId(_lastShooterPeerId)`). `_lastShooterPeerId` moves
**only on a shot** — never on a rebound, catch, or any other possession change. So
after a rebound the rebounder becomes the holder while `_lastShooterPeerId` still
names the original shooter; if the rebounder then fumbles the ball OOB,
`OtherPlayerPeerId(lastShooter)` awards it **back to the rebounder who knocked it
out** — the inverse of the real streetball "last-toucher-out → other ball" rule.

**Rule — award opposite the last TOUCHER.** The recipient is now
`OtherPlayerPeerId(_lastToucherPeerId)`, where `_lastToucherPeerId` is the peer id
of the player who most recently **possessed** the ball, updated on **every**
possession change — the tipoff and every `AwardPossession` (rebound, make-it-take-it,
OOB award, carry turnover) — not just on a shot. A player who last touched the ball
and puts it out loses it, exactly as on a real half-court (ADR-0014 real-ball
authority, which the original prose already invoked with the words "last-toucher-out").

**Why this is the right reference call.** The 2026-06-28 amendment's own text named
the rule "last-toucher-out" while the code implemented the simpler last-*shooter*
proxy; the spec was internally inconsistent. Resolving toward last-*toucher* makes
code and prose agree and matches the top-ranked reference (real half-court ball).
Confirmed with the human before landing (issue #118 was filed as a design call).

**Authority / netcode — unchanged.** `_lastToucherPeerId` needs no broadcast: only
the server issues an OOB `Award` (`OobResolution` gates `Award` on `isServer`), so
only the server's value drives a real turnover, and the *result* (a possession
change) is what `ReceiveState` already carries — identical to how `_lastShooterPeerId`
is used for scoring. `_lastShooterPeerId` is retained for its own, correctly
last-*shooter* uses (scorer attribution, make-it-take-it, the uncleared-make
turnover, shot-scatter contest).

**Pre-touch (part 2) carries over unchanged.** Before the tipoff `_lastToucherPeerId`
is `0`; `OobResolution.ResolveRecipient` short-circuits a `0` toucher to recipient
`0` → `ClampFallback`, so a loose ball with no possession history still clamps rather
than awarding arbitrarily (Amendment 2026-06-29c, now keyed off the toucher).

**Code:** the recipient source is the pure, headless-tested
`OobResolution.ResolveRecipient(lastToucher, opponentOfToucher)` (`LooseBallOobRecipientTests`);
`BallController` writes `_lastToucherPeerId` in `TryAssignTipoffHolder` and
`AwardPossession`, and `TickLoose` feeds it through `ResolveRecipient`.

## Amendment — 2026-06-30 (issue #97: steal/block as defense-induced turnover paths)

**Status remains Accepted.** This amendment records two new possession-change
causes — defense-induced turnovers — that ADR-0008 did not previously cover. No
prior rule changes; this fills a gap in the taxonomy before the implementing
issues (#96 steal, #98 block) are built.

**Two new turnover paths:**

- **Steal (issue #96):** A successful steal transitions the ball
  `Dribbling → Loose`. The defender's `Active` window overlaps the dribble-exposed
  phase on the correct hand (ADR-0018 §2); on a successful overlap, `HolderPeerId`
  clears and the ball enters `BallState.Loose` at the point of dispossession.
- **Block (issue #98):** A successful block transitions the ball
  `InFlight → Loose`. The defender's `Active` window overlaps the shot's
  release/early-flight window (ADR-0018 §2); on a successful overlap, the in-flight
  arc terminates and the ball enters `BallState.Loose`.

Both are defense-induced **changes of possession** and both route through the
**existing loose-ball proximity scramble** (§Decision-2): each tick the ball is
loose, the server checks which player closes within pickup radius and awards via
`BallStateMachine.Catch(peerId)`, with the nearer player winning when both are in
range. No new resolution mechanism is introduced — these paths simply join the
rebound scramble already defined by §Decision-2.

**Possession after a steal or block starts uncleared.** This is consistent with
the 2026-06-21 amendment's reasoning: a change of possession to the
defender or scramble-winner must clear the ball before scoring, so the defensive
moment carries genuine weight rather than becoming an instant put-back path. The
same logic applies here — a steal that converted directly into a score would erase
the clear rule's barrier in exactly the scenario it was designed for (defense
winning possession off offense). The 2026-06-21 amendment narrowed the uncleared
start to *changes* of possession (specifically preserving make-it-take-it as
pre-cleared); steal and block are unambiguously changes of possession and therefore
start uncleared by that same reasoning.

**What did NOT change:**
- The clear-line geometry, `IsCleared` broadcast path, and HUD are all unchanged.
- The existing `AwardPossession` path (with its `cleared: false` default) handles
  awarding from the Loose scramble — no new possession-award code is needed beyond
  what #96/#98 add to initiate the `Dribbling → Loose` and `InFlight → Loose`
  transitions.
- Make-it-take-it (§Decision-1) and its pre-cleared start (Amendment 2026-06-21)
  are unchanged — those are offense-retained-possession paths, not changes of
  possession.

**Implementers:** ADR-0018 defines the shared success predicate
(`DefensiveResolution.Succeeds`) and the per-move vulnerable windows that govern
*when* a steal or block connects. Issue #96 (steal) implements the
`Dribbling → Loose` transition; issue #98 (block) implements the
`InFlight → Loose` transition.

## Amendment — 2026-07-05 (issue #193: triple threat stance — Held-start
possessions + the dead-dribble rule)

**Status remains Accepted.** This amendment adds two new rules on top of the
existing possession machinery; nothing above is superseded. It was speced by
the M9 move-taxonomy triage session (2026-07-04) and grilled with the human
before landing — recorded here per CLAUDE.md's Decision Discipline (the
implementation PR (#204) cited this amendment in eight places before it
actually existed; this section closes that gap).

**Scope note — what this is NOT.** "Pass" is explicitly out of scope: 1v1
half-court has no recipient. The check-ball ritual (a start-of-possession
handoff ceremony) belongs to M12 (match flow), not here. What "triple threat"
means in this codebase is narrower: the dual threat (dribble / shoot) plus the
pivot mind game (#172, already landed), sharpened by the two rules below.

**Rule 1 — every possession now starts in a fresh, LIVE `Held` stance, not an
instant dribble.** Previously `BallController.AwardPossession` unconditionally
chained `Catch`/`Turnover` into `StateMachine.StartDribble()`, so a rebound,
an OOB turnover, a steal/block scramble recovery, and a make-it-take-it award
all dropped the new holder straight into `BallState.Dribbling` — the player
never got the "triple threat" beat real ball gives a new possession. `
AwardPossession` no longer makes that call; the new holder lands in `Held`
with the ball live. They may shoot directly from `Held` (already legal per
§Decision rules above — unchanged), or drive by pushing the stick past
deadzone, which auto-fires `StartDribble()`
(`PlayerController.CheckAutoStartDribble` → `BallController.TryStartDribble`).
The tipoff (`TryAssignTipoffHolder`, which never routes through
`AwardPossession`) starts the same way for consistency — the opening
possession is also a fresh live `Held`.

**Rule 2 — the dead-dribble rule.** A new per-possession, server-authoritative
flag, `BallController.HasDribbled`, tracks whether the CURRENT possession's
dribble has already been cradled (picked up). While set, `StartDribble()` is
refused (`DeadDribbleRule.CanStartDribble`) — the real half-court "you can't
dribble again once you've picked it up" rule. The flag:
- **Resets to `false`** on every possession change (`AwardPossession`, covering
  rebound/turnover/make-it-take-it uniformly) and on the tipoff
  (`TryAssignTipoffHolder`) — there is no separate "on score" reset, because a
  make-it-take-it award already IS a possession change and already routes
  through `AwardPossession`.
- **Sets to `true`** the instant the holder BEGINS a `JumpShot` from
  `Dribbling` (`PlayerController.BeginCommittedMove` → `BallController.
  CradleForShotStartup`, which calls `StateMachine.StopDribble()` as the side
  effect). This also covers the pump-fake: a feint is a Startup-phase ABORT of
  the *same* `Begin()` (`CommittedMoveMachine.Feint()`), not a second one, so
  there is no separate hook for it — a canceled pump-fake still leaves the
  flag set. This matches real ball and 2K: the gather is inherent to the
  shooting motion, not a separate discrete input or `CommittedMove`. The
  consequence is deliberate: **a feinted pump-fake strands the player in dead
  `Held`** for the rest of the possession — a real cost to feinting off the
  dribble that keeps the read-and-punish mind game legible (CLAUDE.md §1).
- **Also gates any dribble move**, not just a raw `StartDribble()` attempt
  (code-review finding on #204): `Crossover`/`Hesitation` are dribble moves in
  real ball too, so `BeginCommittedMove` refuses either while the acting
  player holds the ball in `Held` state — dead OR live. Without this, a
  dead-`Held` player could still throw a crossover (full separation burst, and
  the Active-entry `HandSide` flip would authoritatively teleport the
  Held-tracked ball to the other hand), escaping Rule 2's whole point. From a
  LIVE `Held` possession the same gate applies for the same real-ball reason —
  the player must start dribbling (push the stick) before a crossover is
  legal, exactly as Rule 1 already requires for a raw drive.

**Known accepted race (server-gated, not fixed here):** `RequestBeginMove`
travels Reliable while `SubmitInput` travels UnreliableOrdered — separate
channels with no cross-ordering guarantee. A client that drives and then
pump-fakes within roughly one tick can have the server process
`Begin(JumpShot)` before the drive's `SubmitInput` arrives: the server still
sees `Held` (not yet `Dribbling`), so `CradleForShotStartup` no-ops and
`HasDribbled` stays `false` server-side, while the client's own prediction set
it `true` — the client's optimistic value is then force-corrected back to
`false` by the next `ReceiveState` broadcast (HasDribbled is broadcast — see
below), a silent, narrow dead-dribble bypass bounded to ~1 RTT of packet
timing. A robust fix is cross-channel input/RPC ordering, which is out of
scope for this amendment; the guard's "why" is documented at
`CradleForShotStartup`'s `Dribbling` check, referencing the cradle-race
follow-up issue linked from PR #204's conversation.

**Netcode note — `HasDribbled` IS broadcast, unlike a first draft assumed.**
The original implementation reasoned no peer ever needs another peer's copy of
this flag, so it was left unpredicted/unbroadcast. Doubt-driven review on #204
found a real gap: `ReconcileFromServer`'s `ForceState` can re-point
`HolderPeerId` at a DIFFERENT peer than a client locally predicted (e.g.
disagreeing on a loose-ball scramble's winner) without touching
`HasDribbled`, so a stale `true` could wrongly refuse a legitimate drive for
the rest of what is, per the corrected identity, actually a brand-new
possession — unlike `IsCleared`'s already-accepted ≤1-RTT cosmetic window,
nothing bounded how long that staleness could persist. `HasDribbled` is now
broadcast on the same `ReceiveState` payload as `IsCleared` and force-corrected
the same unconditional way in `ReconcileFromServer`.

**Rejected — travel and 5-second closely-guarded violations.** Real half-court
ball pairs the dead-dribble rule with a travel call (moving the pivot foot
after picking up the dribble) and a 5-second closely-guarded count. Both are
explicitly OUT of scope for #193: bare-minimum realism — don't build
enforcement nobody asked for yet. If either is wanted later, it is a separate,
separately-recorded decision, not a silent extension of this amendment.

**Rejected — a separate "cradle" input or `CommittedMove`.** Modeling the
gather as its own discrete action (a button press, or a dedicated move
subclass) was considered and rejected: it does not match real ball or 2K,
where the gather is inherent to the shooting motion, and it would have
required a second RPC/prediction path for no gameplay benefit over piggy-
backing on the existing `JumpShot` Begin.

**Code:** `DeadDribbleRule.CanStartDribble` (pure, headless-tested predicate,
`tests/Hooper.Ball.Tests/DeadDribbleRuleTests.cs`); `BallController.HasDribbled`,
`CradleForShotStartup`, `TryStartDribble`; `PlayerController.
CheckAutoStartDribble` and the `BeginCommittedMove` dribble-move gate. Proven
end to end by the headless `TripleThreatTest` harness (ADR-0016), which also
sweeps `OobTurnoverTest`/`StealTurnoverTest`'s stale "award possession ⇒
Dribbling" assumptions the prior behaviour left them with.
