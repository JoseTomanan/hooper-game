# ADR-0023 — Server-authoritative move gates tolerate prediction divergence; they never substitute a different move

- **Status:** Accepted
- **Date:** 2026-07-17
- **Superseded-by:** —

---

## Context

Some committed moves are legal only from certain positions. The first is the
layup (#229, [ADR-0022](0022-rim-finishing-offensive-vertical.md)): pressing
shoot within `LayupRange` (4.0 m of `RimCenter`, XZ) begins a `Layup`, farther
out a `JumpShot`. Per [ADR-0002](0002-networking-server-authoritative.md) the
client's choice of *which* move to request is not authoritative, so
`PlayerController.RequestBeginMove` re-asserts the range gate server-side — a
tampered client must not be able to send `moveId="layup"` from anywhere on the
court and collect the layup's much shorter frame data (8/4/14 vs. the jump
shot's 18/4/20).

That gate has a defect (#236). The client presses at its *predicted* position;
the RPC reaches the server one-way-latency later, by which time the server's
authoritative copy of a driving player has travelled up to `MoveSpeed` × that
latency further. Around the 4 m boundary the two disagree, the server's gate
returns false, and — because the branch had no `else` — **nothing begins**. The
shoot press is eaten in exactly the at-the-rim zone the layup exists for.

Three shapes of fix were considered.

**Rejected: substitute a `JumpShot` when out of range** (#236's own prescribed
fix). Superficially attractive — a jump shot is unconditionally legal at any
range, so honoring the shot intent with the server's correct move type looks
like it strictly dominates dropping it. It does not, and the reason is
load-bearing enough to record here rather than in a commit message.

`PlayerController.cs` documents a standing invariant: *"the server only ever
moves out of Inactive by echoing back the EXACT moveId the client's own RPC
requested … so the two sides' move Ids can never disagree while both are
non-Inactive."* Both of the reconciliation gates depend on it:

| Gate | Fires when | Under a JumpShot substitution |
|------|-----------|-------------------------------|
| `CommittedMoveMachine.ShouldForceInactive` | `serverPhase == Inactive` | Never — the server is mid-JumpShot |
| `CommittedMoveMachine.ShouldForceRecovery` | `localMoveId == serverMoveId` | Never — `"layup" != "jumpshot"` |

So a substitution produces a divergence **no code path can correct**: the client
runs a Layup to completion (releasing at tick 8) while the server runs a
JumpShot (releasing at tick 18). The client's predicted in-flight ball is
snapped back to `Held` and re-released ten ticks later — a visible double-shot
on the shooter's own screen — and the two players watch different telegraphs,
which is a direct hit on the legibility requirement the defender's block read
(ADR-0018) is timed against.

Critically, this is *worse than the status quo*. The existing reject is not a
netcode bug — it is the netcode working. A server that stays `Inactive` is
precisely what `ShouldForceInactive` reconciles against, so today's dropped
press is a **bounded, self-correcting** artifact of the same documented class as
the "phantom second move" trade-off. #236's premise — that dropping the input is
the defect — inverts which behavior is the safe one.

**Rejected for now: true lag compensation.** Evaluate the gate against the
client's position at the acked press-time sequence number. This is the
root-cause fix and would retire the whole class of boundary-straddle bug rather
than the layup's instance of it. It is out of scope here: `PredictionBuffer`
stores `Queue<(int seq, Vector2 input)>` — inputs, not positions — so the server
keeps no position history to rewind to. Building one is new machinery, and
ADR-0002 already names lag compensation the hardest part of the project. Worth
its own ADR and issue if a second range-gated move justifies it.

## Decision

**A server-authoritative gate on a committed move may widen its own threshold by
a bounded network tolerance, but must never begin a move other than the one the
client requested.** Out-of-tolerance requests are rejected — the server begins
nothing and stays `Inactive`, which the existing reconciliation already
corrects.

Concretely for the layup: the server gates at `LayupRange +
LayupRangeNetTolerance` instead of at `LayupRange`. The client keeps gating at
the bare `LayupRange` — the tolerance is a server-side allowance for its own
uncertainty about where the client was at press time, not a widening of the move
itself.

This preserves the invariant by construction. The server's `moveId` is still
always the client's `moveId` or nothing; no new correction primitive is needed;
the anti-tamper property survives intact, because a tamperer at 8 m is still
rejected and still gets nothing.

The tolerance's **default value** (0.5 m) is derived, not guessed: `MoveSpeed`
(6.0 m/s) × ~83 ms of one-way latency ≈ 0.5 m, which is also the divergence
figure #236's own repro cites. The theoretical worst case within this codebase's
existing latency assumption is larger — `PlayerController`'s reconciliation
comment treats `StartupFrames` of one-way trip time as covering "all but
pathological RTTs", and the layup's 8-tick startup is 133 ms, or 0.8 m at full
sprint. 0.8 m was deliberately not chosen: it widens the anti-tamper surface by
20% of the gate to buy a case (>83 ms one-way, at a full sprint, straddling the
boundary) that is already rare. Where that number *sits* is a balance question
and therefore [#238](https://github.com/JoseTomanan/hooper-game/issues/238)'s to
dial; this ADR fixes only that a tolerance exists and what it is for.

## Consequences

**Easier.** The honest driving player's shoot press is no longer eaten at the
boundary — the case #236 was filed about. Future range-gated moves (the
euro-step, #231, is the next one, and it crosses this gate mid-lateral-
displacement when divergence is at its worst) inherit both the pattern and the
reasoning instead of re-deriving it or reaching for the substitution trap.

**Harder / given up.** A tamperer gains the layup between 4.0 m and 4.5 m rather
than being cut off at exactly 4.0 m. This is accepted: the frame-data advantage
is bounded and the animation would read as absurd at that distance, whereas the
alternative that closes the gap perfectly (lag compensation) costs machinery
this milestone does not justify.

The boundary now has a small band in which client and server disagree about
which move is *ideal* while agreeing on which move *runs*. That is the trade
being made — a slightly wider gate in exchange for the invariant holding.

**Follow-on.** `LayupRangeNetTolerance` is a new tunable magnitude and is
catalogued in #238 alongside `LayupRange` itself; the two interact and must be
dialed together. If a future move needs a tolerance meaningfully larger than a
half-metre, that is the signal to reconsider real lag compensation and supersede
this ADR rather than to keep widening bands.
