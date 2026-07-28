# ADR-0012 — Ball-hand-side is server-authoritative state, not cosmetic

- **Status:** Accepted
- **Date:** 2026-06-28
- **Superseded-by:** —

---

## Context

M7b issue #73 added a ball-on-hand display cue: the ball mesh renders in the
holder's left or right hand, and switches hands when a crossover fires. That
work was deliberately **cosmetic-only**. `HandSideResolver`'s own doc states it
plainly —

> "the return value affects only which side of the holder the ball mesh renders,
> never authoritative ball/holder state (ADR-0004)."

— and #73 even deferred a future *steal-targeting* use of hand-side to "the M9
epic" precisely to keep that cosmetic boundary clean. Today the displayed hand
is **derived** from the crossover's burst direction inside `BallController`
(`HandSideResolver.Resolve(current, prevPhase, phase, burstDirection)`), reset
to a default on each possession change.

The M9 crossover mind-game (epic #75, sub-issues #83–#86) **breaks that boundary
on purpose.** The unified model is: *one right-stick flick, disambiguated by
which hand currently holds the ball* —

- flick toward the **empty** hand → **crossover** (ball swaps to that hand + a
  lateral separation burst), and
- flick toward the **ball** hand → **hesitation** (freeze/bait; no swap, no
  scripted burst).

That makes "which hand holds the ball" a **gameplay input** that decides how the
*next* committed move resolves — no longer a display detail. Under ADR-0002
(server-authoritative + client prediction), any state that drives move
resolution must be server-owned and predicted + reconciled on clients, the same
treatment `CommittedMoveMachine` (move phase) and the ball state already get. A
cosmetic value that two clients could disagree on would let them disagree about
whether a flick was a crossover or a hesitation — a divergence in *gameplay*,
not just appearance.

### Forces at play

1. **Hand-side now gates move resolution.** The crossover/hesi branch is chosen
   by comparing the flick side against the current hand (`IsCrossover`). If the
   server and a client hold different hand-side values, they resolve the *same
   input* into *different moves*. That is exactly the class of divergence
   server-authority exists to prevent (ADR-0002).

2. **It is discrete, like move phase — not continuous, like position.** Hand-side
   is one of two values and changes only at a discrete event (a crossover's
   Active tick). There is no meaningful "smooth correction" for it, so the
   reconciliation model is the move-machine's snap-to-authoritative
   (`ForceState`/`ShouldForceInactive`), **not** position's lerp-the-offset.

3. **The swap is a strict consequence of an already-reconciled event.** A
   crossover reaching Active is already server-authoritative and client-predicted
   (it is a `CommittedMove`). The hand swap rides that same event, so its
   correctness can be anchored to the move's correctness rather than reconciled
   as a fully independent channel (see Decision → reconciliation).

4. **The cosmetic display must not regress.** #73's ball-on-hand cue and the
   per-role DISPLAY path (#69) still need to work for own *and* remote players.
   The cleanest result is that the cosmetic layer now **reads** the authoritative
   hand instead of *deriving* its own from burst direction — strictly less logic
   in the cosmetic layer, and guaranteed agreement with gameplay.

5. **`Done means proven` is unchanged.** Promoting the state to authoritative
   does not change that the dual-instance editor verify (both clients agree on
   hand-side through a crossover) is what closes the `hitl` portion.

### Alternatives considered

1. **Keep hand-side cosmetic; disambiguate crossover/hesi some other way.**
   E.g. a separate button for hesitation, or an absolute (non-hand) rule.
   Rejected. It abandons the design's core elegance — *the same flick means
   different things depending on ball-hand* is the mind-game (#75's unified
   model). A separate button removes the read; an absolute rule reintroduces the
   "crosses the same way twice does nothing" bug #84 exists to fix. And it leaves
   a latent trap: any future feature that reads hand-side for gameplay (steals,
   #76) would still be reading an un-networked value.

2. **Make hand-side authoritative but reconcile it as a fully independent channel**
   (broadcast it and snap clients to the server value every tick).
   Rejected. Snapping every tick force-reverts a *correctly* predicted swap for
   the ~1 RTT until the confirming broadcast arrives, flickering the ball between
   hands on every legitimate crossover — the identical failure the move-machine's
   Step 0 documents for `FrameInPhase` (`ReconcileFromServer`). The swap must be
   corrected only when the move that caused it is corrected.

3. **Store authoritative hand-side on `BallController` (where the cosmetic lives
   today).**
   Rejected. The disambiguation is read at *input time* in
   `PlayerController.SampleMoveInput`, and the value resets per possession and is
   per-player. `PlayerController` already owns the directly-analogous
   authoritative-and-reconciled `Heading` (ADR-0010) and the move machine, and
   has a per-peer node identity for the RPC sender check. Hand-side belongs with
   the player whose hand it is; the ball *reads* it for display.

## Decision

**Ball-hand-side becomes server-authoritative state owned by the holder's
`PlayerController`, predicted on clients and reconciled — superseding #73's
cosmetic-only discipline for hand-side (and the deferred-to-M9 pointer in #73).**

- **Ownership & mutation.** `PlayerController` holds the authoritative `HandSide`.
  It changes *only* inside the authoritative simulation, on the tick a
  **crossover** enters Active (the swap), to the opposite hand. A hesitation does
  not change it. It resets to a default when the player gains possession — **Left
  as originally decided, amended to Right on 2026-07-28 (see Amendment below).**

- **Broadcast.** Hand-side is piggybacked on the existing `ReceiveState`
  broadcast, exactly like `Heading` (ADR-0010) — same UnreliableOrdered
  snapshot/staleness properties, no separate channel.

- **Prediction & reconciliation.** The client predicts its own swap at its local
  crossover Active-entry. On reconcile, hand-side is restored to the server value
  **only in the same branch that reverts a mispredicted move**
  (`CommittedMoveMachine.ShouldForceInactive` → `ForceState(Inactive,…)`). It is
  deliberately *not* snapped on every tick (alternative 2). The residual gap — the
  server's broadcast hand-side lags a *confirmed* swap by ~1 RTT — is invisible
  because nothing force-acts on it, and it is the same bounded, self-correcting
  artifact already documented for the phantom-second-move case in
  `ReconcileFromServer`.

- **Cosmetic display reads it.** `BallController`'s ball-on-hand display now
  **reads** the holder's authoritative `HandSide` instead of deriving its own via
  `HandSideResolver.Resolve(…, burstDirection)`. The per-role DISPLAY discipline
  (#69) is preserved: own/simulated copies and the client's remote copy both read
  the same authoritative-or-broadcast hand the rest of the display path uses.

- **The hand decision logic stays pure and unit-tested.** The swap
  (`Opposite`), the empty-hand side, the crossover/hesi gate (`IsCrossover`), and
  the heading→world burst projection live in a pure resolver
  (`HandStateResolver`) with no Godot dependency, mirroring `HeadingMath`,
  `MovementMath`, and `CommittedMoveMachine`.

## Consequences

**Easier:**
- Crossover/hesi disambiguation is now correct under prediction: both clients
  resolve a flick into the same move because they agree on hand-side.
- The cosmetic layer shrinks — it reads one authoritative value rather than
  re-deriving hand from burst direction and tracking its own phase history.
- Future hand-reading gameplay (steal-surface #76, pump-fake, etc.) inherits a
  networked, reconciled value for free rather than reading an un-replicated one.

**Harder / accepted tradeoffs:**
- One more field on the `ReceiveState` payload (an `int` hand-side), and the
  reconcile path gains a hand-restore in the move-revert branch. Bounded and
  mirrors the `Heading` precedent.
- The ~1 RTT post-confirmation staleness of the broadcast hand-side (force 2 /
  alternative 2) is accepted, not eliminated — documented inline at the reconcile
  site, consistent with the existing committed-move staleness trade-offs.
- **#73 / ADR-0004 wording must be updated.** #73's cosmetic-only language and the
  `HandSide`/`HandSideResolver` class docs that cite ADR-0004's cosmetic boundary
  are revised to point here: hand-side is authoritative as of this ADR; the *ball
  mesh offset* remains the only cosmetic part.
- **`Done means proven` is unchanged.** This ADR moves where the state lives and
  how it is networked; the dual-instance editor verify (both clients agree on
  hand-side across a crossover, and a mispredicted crossover's hand snaps back)
  remains the gate that closes the `hitl` portion of #83.
- **Reversible.** If authoritative hand-side proves not worth the payload, the
  crossover/hesi model would have to change with it (alternative 1) — so this is
  reversible only together with the M9 mind-game design, not in isolation.

---

## Amendment — 2026-07-28: the possession-reset default is **Right**, not Left

**Status:** Accepted. Amends the Ownership & mutation bullet above. The rest of
this ADR (authority, broadcast, prediction/reconciliation) is unchanged.

### Why

The reset default was never chosen on the merits — Left was picked in #73 when
hand-side was still cosmetic and nothing read it. Two facts, both established on
2026-07-28, make it the wrong default now.

**1. It is measurably wrong against the art.** `assets/locomotion.res`'s
`dribbleidle`/`dribblemove` clips are a **right-handed** dribble. Measured, not
asserted, by `tools/probe_dribble_hand.gd`: vertical pump range 0.0087 m on the
left hand vs **0.3450 m** on the right, a 39.58x ratio, against a probe that is
allowed to answer "I cannot tell" and declines to below a 3x margin. The
`Dribble` AnimationTree state has no hand-side split, so it plays that one
right-handed clip regardless of `HandSide`. The ball, meanwhile, is placed at
`HandRight(forward) * HandOffset * HandSign(holder)` and *does* follow the
authoritative value. With the default at Left the two disagree on every fresh
possession: `tests/integration/DribbleHandAlignmentTest.cs` measured the ball at
−0.18 m lateral while the animated hand pumped at +0.40 m — 0.54 m apart, on
opposite sides of the body. Setting the hand to Right on the same rig closes it
to 0.23 m and the scenario passes.

**2. Real ball says right.** ADR-0014 tier 1 (real half-court basketball): a
right-handed player receives and carries the ball in the right hand, and their
crossover runs right-to-left. Left-as-default modelled a left-handed player by
accident.

### What it costs, accepted deliberately

Flipping the default **inverts which physical stick flick produces a crossover
versus a hesitation, for the first possession of a game** — until the player
crosses over once, after which `HandSide` is driven by play rather than by the
default.

This is not a regression; it is this ADR's model working as specified.
`HandStateResolver.IsCrossover` is defined *relative to the hand holding the
ball* ("flick toward the empty hand → crossover"), so the starting hand and the
starting crossover direction cannot be chosen independently. Changing one
changes the other, necessarily.

Measured blast radius (full sweep, clean baseline of 1130 xUnit + 122 integration
+ 8 dual-instance, zero pre-existing red): **29 newly red integration scenarios,
0 crashes, 0 xUnit failures.** The xUnit suite is immune because it passes
`HandSide` explicitly to the pure resolvers rather than relying on the default.
Of the 29, roughly 18 hardcoded the literal `HandSide.Left` as "the starting
hand" and are stale constants; roughly 11 drive a fixed right-stick direction and
genuinely change which move they produce — the clearest being the mirror pair
`InAndOutTest::quick-return-empty-hand` (expected InAndOut, got hesitation) and
`quick-return-ball-hand` (expected Hesitation, got InAndOut), a complete
truth-table inversion rather than an isolated failure.

Those 11 are repaired by flipping the **input direction** they drive, never the
expected move — the design contract ("toward the empty hand crosses over") is
what the tests exist to pin, and it is unchanged by this amendment.

Human ruling 2026-07-28: proceed, with the input-direction consequence understood
and accepted.

### What this does NOT fix

Only the *fresh possession* case. After a crossover, `HandSide` is Left and the
single right-handed clip is wrong again — the ball renders on the correct side
per this ADR, and the animation does not follow it. The real fix is a genuine
left-hand dribble source plus a `DribbleLeft`/`DribbleRight` state split, which
would make this default cosmetically irrelevant (both polarities would render
correctly). This amendment is the honest interim, not the destination; the
residual case is tracked and its harness scenarios stay red on purpose rather
than being weakened to pass.

A programmatic mirror of the existing clip is **not** an acceptable substitute:
`tools/rebuild_crossover_clips.gd` records why the naive L↔R bone-swap plus axis
negation is mathematically wrong, and #280 rejected it.
