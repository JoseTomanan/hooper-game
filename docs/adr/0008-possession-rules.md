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
