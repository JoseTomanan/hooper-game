---
name: hooper-duel-design-reference
description: Game-design domain pack for hooper-game — the design identity (duel/spacing spine/commitment layer/anti-goals), frame-data vocabulary and the actual shipped tick counts per committed move, half-court 1v1 possession rules (ADR-0008 + amendments), the defensive timing-window model (ADR-0018), the shot-accuracy scatter model (ADR-0009), ADR-0014's reference-tier precedence for self-resolving design calls, and a verified glossary. Load this BEFORE writing or reviewing any code/issue that touches move balance, possession rules, defensive success conditions, shot accuracy, or any "what would real ball / Undisputed 3 / 2K do here" design call — it is what makes a self-resolution citable instead of guessed. Do NOT load this for engine wiring, netcode mechanics, or ADR/issue process questions that don't involve game-design content.
---

# hooper-duel-design-reference

The game-design domain pack for hooper-game: basketball rules and fighting-game
vocabulary **as this project uses them**, not as a generic dictionary would
define them. This skill exists so a zero-context Sonnet agent can make a
reference-grounded design call correctly and cite it per ADR-0014, instead of
guessing or escalating something the references already settle.

Ground truth verified 2026-07-12 against `docs/adr/`, `CONTEXT.md`, and
`scripts/Input/`, `scripts/Ball/DefensiveResolution.cs`. Re-verification
commands are in **Provenance** at the bottom — run them before trusting a
number in a stale checkout.

---

## 1. The design identity (read this first — it is the supreme tier)

One sentence, locked, do not relitigate without being explicitly asked
(CLAUDE.md §1): **the duel is the space between two players and who breaks
first.** Deliberately NOT arcade like NBA 2K. Closer in spirit to a fighting
game (Tekken) crossed with the competitive legibility of *UFC Undisputed 3*.

Three layers, NOT co-equal — this ordering matters when you're deciding where
a new mechanic belongs:

1. **Spine — footwork/spacing.** Separation creation (offense) vs. denial
   (defense). This is load-bearing; everything else lives *inside* it, not
   beside it.
2. **Commitment/mind-game layer, on top of the spine.** Both players read and
   commit to a move; wrong reads are punished. This layer exists *because*
   committed moves cannot be flow-cancelled (ADR-0003).
3. **Subordinate systems, living INSIDE the spine, not co-equal with it:**
   timing windows (shot release/steal/block) and stamina/resource (deferred,
   M11). "Subordinate" is an operational instruction: if you're tempted to
   design the timing-window or stamina system as its own pillar with its own
   parallel mechanic, that is out of bounds — fold it into the frame-data
   grammar the spine and commitment layer already use (this is literally what
   ADR-0018 did: "the tilt belongs in the existing frame-data grammar … not a
   parallel mechanic that would risk becoming a co-equal pillar").

### The two anti-goals — what they mean operationally when writing code

**Primary anti-goal — arcade decoupling (named after EA Sports UFC 5).**
Action that floats free of physical constraint: a shot released with feet
unplanted, movement and striking (or shooting, or dribbling) happening at the
same time, any move that can be started and then freely cancelled with no
cost. Operationally, before you land a change, ask:

- Does the body visibly commit (feet plant / velocity zeroed or bled per
  #198's bounded model) before the effect fires? If a move's Active-phase
  effect can happen without a preceding, visible Startup, that's the
  anti-goal.
- Can the player back out of the effect for free once Active has fired? There
  is deliberately **no `Cancel()`** in `CommittedMoveMachine` — only a
  windowed `Feint()` (Startup-phase abort only) and a server-driven
  `EndActiveEarly()` (defensive-loss consequence, not a player option). If
  your change adds a way to bail out of Active or Recovery for free, it is
  the anti-goal, not a refinement.
- Is a punish window (Recovery, or the equivalent vulnerable-window overlap
  for defense) still real and still costs something on a miss? If you're
  shortening or removing Recovery to make a move "feel better," check whether
  you're actually erasing the punish window the mind game depends on.

**Secondary anti-goal — manufactured jank (named after Goat Simulator).** The
opposite failure: exaggerating commitment into comedy or clunkiness *as an
aesthetic choice* rather than as the minimum realism/legibility requires.
Operationally: don't pad Startup/Recovery numbers "for readability" past what
the read actually needs, and don't add stiff/jerky animation flourishes that
aren't honestly derived from the physical commitment. Polish is fine — a
smooth, well-produced animation is welcome *as long as it honestly shows the
body committing* (ADR-0003's 2026-06-17 refinement: polish is not the enemy,
decoupling is).

Both anti-goals are bounds on the SAME axis (how much does a move cost to
throw, and how honestly is that cost shown) — arcade decoupling is "too
little cost, hidden," jank is "cost exaggerated past what's needed." A
design call that trades one for the other (e.g. "let's remove the plant to
make it feel snappier" or "let's make Recovery absurdly long so it reads
super clearly") is drifting toward one anti-goal even while claiming to fix
the other. Target feel is *Undisputed 3* — sit between the two, don't ping
between them.

**When NOT to use this section:** if the question is about *how* to encode a
frame-data change in C# (which field, which class), that's
`hooper-architecture-contract`, not this skill — this section is about
*whether* a proposed change is identity-compatible.

---

## 2. Frame-data vocabulary (ADR-0003, Tekken-borrowed)

- **Startup** — wind-up before the move's effect is live. Must visibly
  telegraph; `MoveFrameData` throws if `StartupFrames < 1`.
- **Active** — the frames where the effect is live (crossover burst fires,
  shot releases, steal/block window is open). `MoveFrameData` throws if
  `ActiveFrames < 1`.
- **Recovery** — frames after Active where the player cannot act. **The
  punish window** lives here — this is literally where the mind game pays
  off or fails. `MoveFrameData` throws if `RecoveryFrames < 1` (a zero-frame
  Recovery removes the punish window entirely, which is the whole point of
  the frame-data model — see ADR-0003).
- **Feint window** — a sub-range of Startup, `[FeintMinStartupFrames,
  FeintWindowFrames)`, where the player can abort to Inactive (dribble moves)
  or to a cheaper Recovery (`FeintRecoveryFrames`, e.g. JumpShot's pump-fake).
  `FeintWindowFrames = 0` means the move is **structurally unfeintable**
  (Hesitation) — not a placeholder, a design constant (see §3 "Feints vs.
  hesitation" note below).
- **Punish window** — CONTEXT.md's synonym for the Recovery phase from the
  *opponent's* point of view: the frames where the opponent can act before
  the committing player regains control.

### Frame numbers as shipped (verified against `scripts/Input/*.cs`, 2026-07-12)

This table is the AUTHORITATIVE frame-data reference for the skill library;
`hooper-architecture-contract` §3 and `hooper-research-frontier` carry
abbreviated copies that point back here. If a number changes in code, fix it
here first, then sweep the copies.

All counts are integer physics ticks (`1/Engine.PhysicsTicksPerSecond`), never
wall-clock time — this is an ADR-0004 determinism invariant, not a style
choice.

| Move | Startup | Active | Recovery | Feint window | Feint recovery | Feint min-startup | Notes |
|---|---|---|---|---|---|---|---|
| `Crossover` | 6 | 3 | 12 | 4 | 0 (abort→Inactive) | 0 | Burst-family; #198 bleeds momentum through Startup instead of zeroing it (this move only) |
| `BehindTheBack` | 6 | 3 | 10 | 4 | 0 | 0 | "Safer crossover" (#194) — same Startup/Active as Crossover, shorter Recovery, narrower 50° exit cone (see §7 "exit cone") — explicitly NOT a longer Recovery or a cooldown (both rejected) |
| `Hesitation` | 4 | 8 | 6 | **0 — structurally unfeintable** | — | — | No burst payload; ball stays in same hand; "the absence of a cancel IS the mind game" |
| `JumpShot` | 18 | 4 | 20 | 12 (legal window `[3, 12)`) | 8 | 3 | Ball releases on the FIRST Active tick (`JustEnteredActive`). Feint illegal before frame 3 (no zero-startup invisible fake). Frames `[12,18)` are the "committed tail" |
| `StealMove` | 8 | 8 | 20 | 4 | 0 | 0 | Recovery matches JumpShot's so a whiffed steal is as punishable as a missed shot |
| `BlockMove` | 10 | 8 | 20 | 4 | 0 | 0 | Startup shorter than JumpShot's 18 — the defender *reacts* to a visible shot attempt rather than *initiating* one; that gap IS the reaction tilt (see below) |

All six default-frame-data blocks live at the top of their respective class
files as doc comments — read the class doc, not just the `MoveFrameData`
literal, before changing a number; the doc explains *why* that number, and
changing it without updating the doc is a documentation-rot bug your PR will
be reviewed for.

**Provisional vs. settled:** StealMove/BlockMove frame data is explicitly
marked "provisional (tuning deferred to #104 and the per-milestone feel
pass)" in their own doc comments — treat these numbers as *shipped
defaults*, not *locked balance*. Hesitation's doc calls its numbers
"intentionally placeholder … except FeintWindowFrames=0, which is a design
constant"; JumpShot's feint values are "provisional — tuning deferred."
Do not cite "it's already 8/8/20" as a design justification on its own —
cite the *reasoning* in the class doc.

**Feints vs. hesitation (ADR-0003, 2026-06-28 refinement):** a combat-sport
feint (recall a wound-up strike inside Startup — the Undisputed 3 model
`Feint()` implements) does NOT apply to dribble moves: once the ball is
headed to the floor you physically cannot pull it back (tier-2 real-ball
authority). The realistic dribble fake is the **Hesitation** — its own honest
committed move with no recall, not "a crossover you cancelled." `Feint()` is
kept and wired for Crossover/BehindTheBack but is flagged in the ADR as
non-realistic for dribbling and slated for reconsideration — do not build
new dribble mechanics on top of the recall model. The pump-fake (JumpShot
feint) is unaffected: no ball-committed-to-the-floor problem exists there.

### Reaction-tilt, made concrete (ADR-0018 §3)

"Defense gets a deliberate asymmetric tilt toward reaction" (CLAUDE.md §1) is
implemented purely as `MoveFrameData` shape, not a separate system:

- A defensive move's **Active is no wider than the offensive vulnerable
  window it must hit** — a defender can't paper over bad timing with a long
  active phase.
- A defensive move's **Recovery is at least as long as the comparable
  offensive move's** — a missed defensive commitment is *more* punishable
  than a missed offensive one. Concretely: StealMove and BlockMove both carry
  Recovery=20, matching JumpShot's Recovery=20, and BlockMove's Active=8 is
  bounded by `BlockGraceTicks` (default 10, exported on `BallController`)
  per its own doc comment ("Active must be ≤ blockGraceTicks … any wider and
  the defender could cover mismatched timing with a longer arm").

If you are asked to make a defensive move "feel stronger" by widening its
Active window without a matching Recovery/vulnerable-window justification,
that is drifting away from this tilt — flag it, don't silently comply
(CLAUDE.md Decision Discipline).

---

## 3. Half-court 1v1 possession rules (ADR-0008 + 7 amendments)

Read `docs/adr/0008-possession-rules.md` in full before touching possession
code — the nine amendments each closed a real bug/gap and the base decision
text alone is now incomplete. Summary:

**Base decision (2026-06-20):**
1. **Make-it-take-it.** Scorer keeps the ball (`_lastShooterPeerId`) as a
   fresh `Held` possession, not loose.
2. **Live rebound by proximity.** Loose ball → nearer player within pickup
   radius wins (total order on distance ⇒ server and client prediction
   agree). No dead-ball reset, no check-ball.
3. **Take-it-back/clear.** A make only counts if `IsCleared` — the handler
   carried the ball behind the clear line THIS possession. Uncleared make →
   **no points AND a turnover to the defender**, not just a no-count.

**Amendment 2026-06-21 (make-it-take-it starts pre-cleared):** the *scorer's
own* next possession starts `cleared: true` — no defensive moment exists in
that instant, so double-punishing the scorer with a take-back on the trip
they just earned by clearing was rejected. Every OTHER possession change
(rebound, steal, block, turnover, OOB) still starts uncleared — the clear
rule keeps its full bite there.

**Amendment 2026-06-28 (loose-ball OOB):** a loose ball crossing the court
line is a dead ball; possession is awarded to the opponent side (originally
keyed on last shooter, corrected below). The old clamp-to-inbounds survives
only as a fallback (no opponent present; non-server peers, corrected by the
next `ReceiveState`).

**Amendment 2026-06-29 (held-ball OOB + in-flight termination):** extends OOB
beyond loose-ball. (1) An in-flight arc with no rim/board contact now
terminates on floor contact or court-line crossing (`FlightTermination`) —
previously it integrated forever ("ball disappears" bug). (2) A player
CARRYING the ball who crosses the court line triggers an immediate dead-ball
turnover to the opponent via the new `BallStateMachine.Turnover` edge
(Held/Dribbling → Held-by-new-holder; no loose scramble). Recipient must be
a live, in-bounds node or the award is withheld — this "no-strobe" gate
prevents a both-players-OOB award ping-ponging back next tick at 60 Hz, and
prevents handing the ball to a disconnected ghost.

**Amendment 2026-06-29b (void shot released OOB, #120):** closes the
same-tick gap where a holder crosses the line and releases before the OOB
check runs — an OOB release is now voided as a turnover BEFORE
`StateMachine.Shoot()` runs; the make can never count. Boundary convention:
the court edge itself is IN-BOUNDS (strict `<`/`>`), so a toe-on-the-line
release still scores.

**Amendment 2026-06-29c (pre-shot loose-ball OOB fallback):** before anyone
has ever possessed the ball, "opposite the last toucher" has nobody to be
opposite of — resolves to the in-play clamp fallback rather than an
arbitrary spawn-order award.

**Amendment 2026-06-29d (clear = genuine crossing, not static position, #135):**
the clear flag was a POSITION test (currently behind the line ⇒ cleared),
which let a player who rebounds their own miss *while already behind the
line* be cleared the same tick — no take-back happened. Fixed to a CROSSING
test (`ClearLine.Advance`): the holder must have been inside the line at
some point this possession, then carry the ball behind it — inside→behind.
Recovering the ball while already behind the line does NOT clear; that
holder must drive inside and bring it back out.

**Amendment 2026-06-30 (last-TOUCHER, not last-shooter, #118 part 1):** the
loose-ball OOB recipient is "opposite `_lastToucherPeerId`" (updated on EVERY
possession change: tipoff, rebound, turnover, OOB, make-it-take-it), not
"opposite `_lastShooterPeerId`" (moves only on a shot). The bug: a rebounder
who fumbled the ball OOB was awarded it back, because the last-shooter proxy
still named the original shooter. This is the real streetball
"last-toucher-out → other ball" rule the 2026-06-28 prose always claimed but
the code didn't implement until this amendment.

**Amendment 2026-06-30 (#97, steal/block as turnover paths):** a successful
steal (`Dribbling → Loose`) or block (`InFlight → Loose`) both route through
the EXISTING loose-ball proximity scramble — no new resolution mechanism —
and the resulting possession starts uncleared, same reasoning as any other
change of possession.

**Amendment 2026-07-05 (#193, triple-threat Held-start + dead-dribble rule):**
two new rules layered on top, nothing above superseded:

- **Rule 1 — every new possession starts in live `Held`, not an instant
  dribble.** Rebounds, OOB turnovers, scramble wins, make-it-take-it awards,
  and the tipoff all land the holder in `BallState.Held` (shoot straight
  from Held, or push the stick past deadzone to auto-`StartDribble`). This
  is what "triple threat" means in THIS codebase — narrower than the
  real-ball ritual: **"pass" is explicitly out of scope** (1v1 has no
  recipient), and the check-ball ceremony belongs to M12, not here.
- **Rule 2 — the dead-dribble rule.** `BallController.HasDribbled` latches
  true the instant a `JumpShot` begins from `Dribbling` (the shooting gather
  cradles the ball — this covers the pump-fake too, since a feint is a
  Startup-abort of the SAME `Begin()`, not a separate action). While
  latched, `StartDribble()` — and any dribble-family committed move
  (Crossover, Hesitation, BehindTheBack) — is refused, from dead OR live
  Held. **A feinted pump-fake strands the player in dead `Held` for the rest
  of the possession** — a deliberate, real cost to feinting off the dribble.
  Resets on every possession change. Explicitly rejected as out of scope:
  travel calls and the 5-second closely-guarded count ("bare-minimum
  realism — don't build enforcement nobody asked for yet"). That scope cut
  is what left the #206 gap (Held-ball is steal-immune) — see §6's worked
  example.

---

## 4. The defensive model (ADR-0018)

**The one shared predicate**, `DefensiveResolution.Succeeds` (pure,
`scripts/Ball/DefensiveResolution.cs`):

```
Succeeds(activeStart, activeEnd, vulnStart, vulnEnd)
   ⇔ activeStart < vulnEnd  &&  vulnStart < activeEnd
```

Half-open integer-tick intervals `[start, end)` on the deterministic
physics-tick clock — two intervals that are exactly adjacent (one ends where
the other starts) do NOT overlap. This is the single success test for all
defensive moves in principle; in practice only Block calls it directly
(see below).

**Why no hidden RNG, ever, on a defensive outcome:** a flat steal%/block%
roll was explicitly rejected (ADR-0018 "Rejected alternatives" — "a hidden
RNG outcome is not a read, removes the mind game, and is the arcade
anti-goal"). This is structural, not incidental: if you are ever asked to
add a percentage-chance modifier to steal/block success, that is a tier-1
identity contradiction (CLAUDE.md Decision Discipline STOP), not a balance
tweak — flag it before writing code. (Shot ACCURACY, by contrast, IS
randomized — §5 — because a shot resolves the offense's own execution
against fixed court geometry, not a defender's timed read against a
telegraphed opponent commitment. Don't conflate the two.)

**Steal — two-axis read.** `DefensiveResolution.StealSucceeds(phase,
loExposed=0.35, hiExposed=0.65, targetHand, holderHand)`:
1. **Side axis (checked first):** `targetHand` must equal the holder's
   authoritative `HandSide` (ADR-0012) — wrong side is an unconditional
   miss, no timing computation needed.
2. **Timing axis:** `DribbleCycle.Phase` (`[0,1)`, current point in the
   dribble cycle) must sit inside the exposed band `[0.35, 0.65]` straddling
   0.5 — the ball near the floor, away from the hand. Phase 0/1 (ball back
   at hand height) is not stealable.

**Implementation quirk you must not copy elsewhere** (ADR-0018 Amendment
2026-07-01): Steal does NOT call `Succeeds` with a precomputed interval —
`StealSucceeds` is a point-in-band test re-evaluated on EVERY tick the
defender's machine is in Active (`BallController.ResolveStealAttempts`). The
union of those per-tick checks *is* the interval overlap the ADR requires;
it's built this way because the dribble's vulnerable window is a *repeating*
phase band with no fixed start/end tick to hand `Succeeds` in advance. **The
ADR itself flags this as "not a template" for future moves** — Block has a
real start tick and calls `Succeeds` directly; that is the form anything new
should follow.

**Block — one-axis read, full interval form.** Vulnerable window:
`[JumpShot Active start, InFlight start + BlockGraceTicks)` — the same tick
by construction, since release fires on JumpShot's Active entry.
`BlockGraceTicks` defaults to 10 and must stay ≥ `BlockMove.ActiveFrames`
(8). No hand-side axis — the ball is airborne, not gripped, so there is
nothing to target by side. Known accepted gap: **no reach/proximity term
yet** (issue #214, open as of 2026-07-12) — a defender across the court can
currently "block" on pure timing; do not silently "fix" this ad hoc, it is
an owned, deferred issue.

**Contest — composes, does not replace (issue #99, not yet built as of
2026-07-12).** Per ADR-0018 §2, a committed contest is NOT a binary
succeed/fail overlap move — it applies an ADDITIONAL discrete accuracy
penalty on top of the EXISTING passive proximity scatter (ADR-0009/#65, §5)
when its Active overlaps the shooter's release window. Getting the
composition right (no double-counting the passive proximity term) is
explicitly #99's stated job.

**Authority constraint (ADR-0018 §4):** every input to every defensive check
is server-authoritative, predicted-and-reconciled state — `Heading`
(ADR-0010, never the cosmetic `FacingResolver`), `HandSide` (ADR-0012),
`DribbleCycle.Phase`, `BallStateMachine`. No defensive outcome is ever
computed from cosmetic state. Same invariant as ADR-0009's facing rule in
§5 — cosmetic never feeds authoritative.

---

## 5. Shot accuracy model (ADR-0009 + 3 amendments)

Server-only, seeded (`_shotRng` from exported `ShotScatterSeed`, default
12345), pure `ShotScatter.Scatter(...)` — the two `[0,1)` RNG samples are
injected by the caller, never drawn inside the helper (headless-testable,
ADR-0004 seam discipline). Clients always predict a dead-centre arc;
`ReconcileFromServer` snaps them to the real scattered arc within ~1 RTT —
scatter is just another source of ordinary predict/reconcile divergence, no
new netcode.

**Geometry:** uniform-disc XZ sampling,
`r = min(scatterPerMeter × distance, maxScatter) × accuracyMultiplier × sqrt(radius01)`,
`theta = 2π × angle01`. **Order matters:** the cap (`MaxShotScatter`) applies
FIRST, the accuracy multiplier SECOND — penalty stacking can intentionally
push the final offset past the nominal cap (a moving, contested shot is
genuinely harder, not just harder up to the same ceiling).

**`accuracyMultiplier = movementFactor × contestFactor × facingFactor`**
(all server-only, all folded into the same `Scatter` call):

- **`movementFactor` (#64):**
  `1 + MovementScatterK(0.8) × clamp(holder.Velocity.Length()/holder.MoveSpeed, 0, 1)`.
  Continuous, not a discrete planted/not-planted threshold — resolved
  2026-06-27: shot stillness is a property of the *analog-movement* half of
  ADR-0003's hybrid model; a discrete planted state would conflate the
  spacing spine with the commitment layer. (The ADR's amendment text says
  default 1.0; the Tuning section then fixed 0.8 — the tuned value is the
  shipped one. Re-verify with the Provenance command if it matters.)
- **`contestFactor` (#65):**
  `1 + ContestScatterK(1.0) × clamp(1 − defDist/ContestRange(2.2 m), 0, 1)`.
  Proximity-ONLY, deliberately: at #65's time the only orientation available
  for a facing-based contest was the cosmetic `FacingResolver`, which
  ADR-0004 forbids feeding an authoritative outcome. ADR-0010 later created
  authoritative `Heading`, but #65's passive term was never retrofitted —
  proximity-alone is what ships. Read that as *deferred*, not
  rejected-forever; #99 (committed contest, §4) is the separate mechanic
  that layers on top.
- **`facingFactor` (#81):** `1 + FacingScatterK(0.8) × (angle/π)` where
  `angle` is the shortest angular distance `[0,π]` between the SHOOTER's
  authoritative `Heading` (ADR-0010 — never `FacingResolver`) and the
  direction to the rim. Squared up = 1.00×; back-to-basket = 1.80× —
  deliberately below the max contest penalty (2.0×): a no-defender
  turnaround fadeaway is harder than an open look, but less punishing than
  a full closeout.

**Design anchors** (fitted by simulating the real
`ShotScatter → ShotArc → RimBackboard` chain, preserved as a regression test
in `tests/Hooper.Ball.Tests/ShotMakeCurveTests.cs` and measured in
`docs/analysis/0079-shot-scatter-curve.md`; `ShotScatterPerMeter = 0.026`,
`MaxShotScatter = 0.45`):

| Distance | Make% (open, stationary) | Real-world anchor |
|---|---|---|
| ≤ 3 m | ~100% | open layup — automatic |
| 5 m | ~67% | open mid-range |
| 5.8 m | ~53% | at the clear line |
| 6.75 m | ~41% | NBA wide-open three ≈ 38–40% |
| 10 m | ~21% | steep falloff rewards spacing |

A 2 m shot only drops below ~100% once the combined penalty multiplier
exceeds ~2.12× — you must be moving AND contested to miss point-blank, which
reads as a genuinely bad decision rather than random punishment. Penalty
composition example from the ADR: an open 5 m shot (~67%) → ~43% contested,
~35% on the move, ~22% both.

**A shot's make/miss has RNG; a defender's steal/block success does NOT
(§4).** If a design request conflates them ("just give block a hit chance
like shots have"), that is the rejected flat-RNG defensive model returning
under a new name — flag it, don't implement.

---

## 6. ADR-0014 reference-tier precedence (self-resolving design calls)

Strict lexical order — a higher tier ALWAYS wins, no balancing:

1. **Locked design identity + ADRs.** Supreme. A request contradicting a
   locked ADR is a STOP-and-flag (CLAUDE.md Decision Discipline), never a
   "tier-1 vote" to weigh.
2. **Real half-court 1v1 basketball.** Physical truth + pickup-rules anchor
   (make-it-take-it, winner's-outs, check-ball, clear-it). Scoped to
   half-court 1v1; full-court NBA/FIBA rules apply ONLY by analogy where the
   half-court game is silent (e.g. what a carry or double-dribble is).
3. **UFC Undisputed 3.** Governs commitment & feel: how a move commits, its
   Startup→Active→Recovery arc, the legibility of the read.
4. **NBA 2K.** Authoritative ONLY for basketball taxonomy (what a crossover /
   hesi / size-up / pull-up / and-one IS) and control-surface familiarity
   (right-stick dribble moves). **Zero weight on feel** — where 2K's feel
   conflicts with tiers 1–3, 2K loses by rule. **The 2K test:** *2K can tell
   you what a mechanic is called and where the button lives. It can never
   tell you how it should feel, or whether it should exist.* Mechanic
   existence is always a tier-1 identity call.

The reference set **{real half-court ball, Undisputed 3, NBA 2K} is closed**
— invoking a fourth reference (Tekken for frame-data convention, And1 for
showmanship, …) requires an ADR amendment, never an ad-hoc mid-task appeal.

**Two worked citations from this repo's own history** (the pattern to copy):

- *Real half-court ball, tier 2 — informing issue #206.* #206: a Held-ball
  holder is currently steal-immune, so pump-fake-mash stalling is untouchable.
  The citable real-ball fact: closely-guarded/5-second-count and travel rules
  exist in real ball precisely to punish this stalling. Note the live
  tension: ADR-0008's #193 amendment explicitly put travel/5-second OUT of
  scope ("bare-minimum realism — don't build enforcement nobody asked for
  yet"), and #206 is the recorded consequence, now the active campaign
  target (see `hooper-held-ball-steal-campaign`). Resolve by checking which
  issue/ADR is currently authoritative — do not re-derive from first
  principles or silently extend the amendment.
- *Undisputed 3, tier 3 — the feint-cancel window model.* ADR-0003's
  `Feint()` (abort a committed move inside its Startup window) is modeled
  directly on Undisputed 3's modifier+strike feint: wind up, pull back,
  within startup. And note the tier interplay that followed: tier-2
  real-ball later overrode it for dribble moves specifically (a ball headed
  to the floor cannot be recalled → Hesitation is its own unfeintable move,
  not a cancelled crossover; ADR-0003 2026-06-28 refinement), while the
  general mechanic survives where tier 2 is silent (JumpShot's pump-fake).
  This is the textbook example of the lexical order actually operating.

**Escalation triggers — stop and ask instead of self-resolving:**
identity/spine/anti-goal change; a hard ADR contradiction; a genuine
reference deadlock (the tiers are silent, or all point at an arcade answer
tier 1 forbids with no honest alternative); high-stakes
irreversible/authoritative decisions (default `/doubt-driven-development`;
ask if doubt survives).

**Cite-or-ask.** Every non-trivial self-resolution states, in the commit/PR
body (and a new ADR if the call is architectural): which reference, which
tier, and the specific real behavior modelled. *"It's more fun"* or *"2K has
it"* with no tier grounding is NOT a valid self-resolution — fun/feel routes
to an HITL tuning issue or the human. If you cannot name the reference and
tier, that is the signal to ask, not to guess.

---

## 7. Glossary

Verified against CONTEXT.md and cross-checked against the ADRs/issues above.
CONTEXT.md's own "Sources" line scopes it to ADR-0001–0008 only — it has NOT
been updated for ADR-0009–0019, so treat it as incomplete for anything past
the M6b era. Entries marked **[STALE]** lag a later ADR — read the note, not
the CONTEXT.md prose.

| Term | Meaning in THIS project | Source |
|---|---|---|
| **Duel** | The 1v1 fundamental unit resolved by footwork, spacing, and commitment reads. | CLAUDE.md §1 |
| **Spine / spacing spine** | Footwork+spacing; load-bearing; everything else lives inside it. | CLAUDE.md §1 |
| **Cradle** | The shooting gather — picking the ball up off the dribble into the shot. NOT a separate `CommittedMove` or discrete input; it is a side effect of `JumpShot`'s `Begin()` (`CradleForShotStartup` → `StopDribble()`). Modeling it as its own move was explicitly rejected ("the gather is inherent to the shooting motion" — real ball and 2K agree). | ADR-0008 Amendment 2026-07-05 |
| **Dead dribble** | Post-cradle state: `HasDribbled=true`; `StartDribble()` and every dribble-family move refused until the next possession change. Real-ball "can't dribble again once you've picked it up" — WITHOUT the accompanying travel/5-second enforcement (explicitly out of scope; see #206). | ADR-0008 Amendment 2026-07-05 |
| **Exit cone** | The half-angle the left-stick exit vector is clamped within at Active-entry, relative to the player's forward axis: 50° default for BehindTheBack, effectively unclamped (~180°) for Crossover. The SOLE mechanism for "fewer follow-ups" (#194) — a longer Recovery and a 2K-style cooldown were both explicitly rejected. `CrossoverBurstMath.ComposeActiveVelocity`'s `maxExitAngleRadians` parameter. | ADR-0003 #194 amendment |
| **Momentum bleed / gather-bleed** | Crossover-scoped (#198; opt-in for future burst-family moves): Startup decelerates existing velocity through a hard-but-finite decel (`GatherDecel`) instead of instant-zeroing it, so a driving player is not dead-stopped by the plant. Every other move (Hesitation, JumpShot, StealMove, BlockMove) keeps the instant-zero plant. Never applies to Recovery, for any move. | ADR-0003 #198 amendment |
| **Size-up** / **size-up-as-feint** | Deliberately inputting a right-stick dribble move for its Startup telegraph only, then feint-cancelling before Active — baiting a defensive commitment. Modeled on 2K14's Pro Stick, where every right-stick input is already a committed move with visible startup (no held "size-up stance" exists). | CONTEXT.md, ADR-0003 |
| **Whiff punish / blow-by (lane)** | The payoff of a MISSED defensive commitment: the defender pays Recovery (20 ticks for steal/block) with nothing to show, opening a driving lane past them. This is issue **#100 — open, not yet built as of 2026-07-12**; "blow-by lane" is the conceptual payoff ADR-0018 §3's Recovery asymmetry exists FOR, not yet a named mechanic in code. Don't cite unbuilt code as precedent. | ADR-0018 §3, issue #100 |
| **Cosmetic facing [STALE]** | CONTEXT.md still describes facing as velocity-derived, client-local, never networked. That describes `FacingResolver`, which still exists and is still display-only — but it is superseded as the load-bearing orientation by **ADR-0010's server-authoritative `Heading`** (tracked in `Move()`, broadcast, replayed in reconciliation). Authoritative outcomes (shot facing penalty, defensive checks) read `Heading`, NEVER `FacingResolver`. Code reading `FacingResolver` for anything affecting score/possession/defense is a bug. | CONTEXT.md vs ADR-0010 |
| **Timing windows [STALE]** | CONTEXT.md's shot-release/steal/block timing-window entries describe an aspirational NBA-2K-style green-window/meter framing ("perfect (green) result", "foul or failed reach" — neither meters nor fouls exist). **What shipped is ADR-0018's tick-interval-overlap model** (§4): a binary Active-overlaps-vulnerable-window test, no meter, no gradient. Treat the CONTEXT.md prose as historical intent. | CONTEXT.md vs ADR-0018 |
| **Burst lean** | Cosmetic-only tilt of the player visual toward the burst direction during a crossover's Active phase; returns upright in Recovery. Never affects velocity or authoritative state. | CONTEXT.md, ADR-0003/0004 |
| **Punish window** | The Recovery-phase frames where the opponent can act before the committing player regains control — where wrong reads are resolved. | ADR-0003, CONTEXT.md |
| **Flow-cancel** | PROHIBITED. Cancelling a committed move mid-execution by redirecting input (the modern "smooth" sports-game model). Rejected because it eliminates the punish window and therefore the mind game. Playtester requests for it contradict the identity; they do not refine it. | ADR-0003 |
| **Feint-cancel window** | The legal Startup sub-range `[FeintMinStartupFrames, FeintWindowFrames)` for aborting a move — §2. Undisputed 3-modeled; not applicable to dribble moves (see §2's feints-vs-hesitation note). | ADR-0003 |
| **Hesitation (hesi)** | A first-class, structurally unfeintable committed move (the freeze/stutter that baits a reaction) — NOT a cancelled crossover. No burst payload, no hand swap; the "go" is the player's own left-stick drive after it resolves. | ADR-0003, `Hesitation.cs` |
| **Clear line / take-it-back / clear** | Exported world-space threshold near the top of the key; the inside→behind CROSSING (not static position) a new holder must complete before a basket counts. | ADR-0008 (+#135) |
| **Make-it-take-it** | Scorer keeps possession for the next trip, starting pre-cleared; an uncleared make scores nothing and turns the ball over. | ADR-0008 (+2026-06-21) |
| **Live rebound / loose-ball contest** | Nearer player within `PickupRadius` wins a loose ball — a deterministic total order on distance so server and client prediction agree. | ADR-0008 §Decision-2 |
| **Triple threat** | In THIS codebase: the fresh live-`Held` start of every possession (shoot or drive; pass is out of scope in 1v1) plus the pivot mind game — narrower than the real-ball term. | ADR-0008 #193 amendment |
| **Reaction tilt** | Defense's deliberate asymmetry, expressed purely in `MoveFrameData`: tighter Active, Recovery ≥ the comparable offensive move's. §2. | ADR-0018 §3 |

---

## 8. The feel-vs-state boundary — what the harness can prove vs. what is feel

This determines whether a `hitl` issue can close on CI green (ADR-0016) or
must wait for the batched human pass (#114 is the current combined M9+M10
gate, per ADR-0015).

**Harness-provable (state-checkable — closes on green CI):**
- Exact frame-data assertions: Startup lasts exactly N ticks, the burst
  fires on the Active-entry tick, a feint inside/outside the legal window is
  accepted/refused.
- Possession-rule outcomes: a make-it-take-it award starts cleared, a
  rebound goes to the nearer player, an OOB turnover fires to the correct
  recipient, dead-dribble refuses a second `StartDribble` — the
  `TripleThreatTest`/`OobTurnoverTest`/`StealTurnoverTest`-style headless
  integration tests (`tests/integration/`).
- Defensive success/failure for SCRIPTED inputs: a steal at a scripted
  dribble phase + hand succeeds/fails exactly as `DefensiveResolution`
  predicts; a block whose Active overlaps/misses the vulnerable interval.
- Aggregate shot-accuracy statistics over a deterministic sweep (the
  `ShotMakeCurveTests.cs` regression pattern) — the make% CURVE matching the
  ADR-0009 anchors, not any single shot.

**Irreducibly feel (batched to the human pass — never auto-accepted):**
- Whether a frame count FEELS readable/fair in real-time play. The harness
  proves "Recovery is 12 ticks"; only a human judges "12 ticks reads as a
  fair punish window."
- Animation legibility — does the Startup telegraph actually read as a
  telegraph to a human opponent, versus merely existing in the state machine.
- Whether the reaction-tilt magnitude feels like a fair asymmetry rather
  than a non-event or a curb-stomp.
- All of #104's explicit tuning targets (steal exposed-band bounds,
  knock/swat speeds, `BlockGraceTicks`) — deferred wholesale to the feel
  pass by design.

Rule of thumb: if the criterion can be phrased "assert X equals Y after a
scripted input sequence," it is harness-provable. If the honest phrasing is
"does this feel right," it is feel — do not write a test that fakes
state-checkability for a feel judgment (asserting an animation timing value
AS a proxy for "is legible" proves the number, not the legibility).

---

## When NOT to use this skill

- **How systems are coded** (which class owns what, choke points like
  `BeginCommittedMove`, netcode mechanics, pure-vs-node split, invariants) →
  `hooper-architecture-contract`.
- **The cite-or-ask PROCESS itself** (how/where to record the citation, when
  to escalate as a workflow matter, afk/hitl/merge discipline) →
  `hooper-change-control`. This skill supplies the CONTENT you'd cite; that
  one supplies the PROCESS for citing it.
- **Executing the #206 held-ball-steal work specifically** →
  `hooper-held-ball-steal-campaign` (§6 here only uses it as a citation
  example).
- **Tunable/export defaults and how to add one** → `hooper-config-and-flags`.
- **Running or extending the harness** → `hooper-verification-and-qa` (§8
  here only tells you which SIDE of the boundary a criterion is on).

---

## Provenance and maintenance

**Date-stamped 2026-07-12; reviewed and corrected 2026-07-15** (ADR-0008
amendment count 7 → 9; ADR-0003 refinement style clarified; frame-data table
marked authoritative for the library). Verified against:
- `docs/adr/0003-input-model-hybrid.md` (+5 inline `**Refined:**` bullets —
  no `## Amendment` headings),
  `0008-possession-rules.md` (+9 amendments), `0009-shot-accuracy-scatter.md`
  (+3 amendments + tuning section), `0014-reference-game-decision-authority.md`,
  `0018-defensive-timing-window-model.md` (+2026-07-01 amendment) — all
  `Status: Accepted`, none superseded, at verification time.
- `CONTEXT.md` (full read; staleness cross-checked against ADR-0010/0018).
- `scripts/Input/Crossover.cs`, `BehindTheBack.cs`, `Hesitation.cs`,
  `JumpShot.cs`, `StealMove.cs`, `BlockMove.cs`, `MoveFrameData.cs`
  (frame-data table read from the `DefaultFrameData` literals directly).
- `scripts/Ball/DefensiveResolution.cs` (predicate + 0.35/0.65 defaults),
  `scripts/Ball/BallController.cs` (`BlockGraceTicks = 10` export).
- Issue tracker snapshot 2026-07-12: #99, #100, #214 open/unbuilt; #206 open
  (campaign target); #104/#114 open feel gates.

**Re-verification commands** (run from the repo root — quote paths, the repo
path contains spaces):

```
# §2 frame-data table still matches the code:
grep -n "startupFrames:" scripts/Input/Crossover.cs scripts/Input/BehindTheBack.cs scripts/Input/Hesitation.cs scripts/Input/JumpShot.cs scripts/Input/StealMove.cs scripts/Input/BlockMove.cs

# §4 steal band + block grace:
grep -n "loExposed\|hiExposed" scripts/Ball/DefensiveResolution.cs
grep -n "BlockGraceTicks" scripts/Ball/BallController.cs

# §5 scatter constants:
grep -n "MovementScatterK\|ContestScatterK\|ContestRange\|FacingScatterK\|ShotScatterPerMeter\|MaxShotScatter" scripts/Ball/BallController.cs

# Whether #99 (contest), #100 (whiff-punish), #214 (block reach), #206
# (held-ball steal) have moved since 2026-07-12 (changes §4/§6/§7 notes):
gh issue view 99 -R JoseTomanan/hooper-game
gh issue view 100 -R JoseTomanan/hooper-game
gh issue view 214 -R JoseTomanan/hooper-game
gh issue view 206 -R JoseTomanan/hooper-game

# Whether any ADR cited here has since been superseded or amended:
grep -n "Superseded-by\|## Amendment" docs/adr/0003-input-model-hybrid.md docs/adr/0008-possession-rules.md docs/adr/0009-shot-accuracy-scatter.md docs/adr/0014-reference-game-decision-authority.md docs/adr/0018-defensive-timing-window-model.md
```

If any command surfaces a delta, treat the affected section as stale and
re-verify against the ADR/issue text directly before citing it in a design
decision.
