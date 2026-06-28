# ADR-0014 — Reference-game decision authority

- **Status:** Accepted
- **Date:** 2026-06-28
- **Superseded-by:** —

---

## Context

A recurring friction: the agent (Claude Code) routes design questions to the
human that should not need a human at all — questions whose answer is simply
*"do what real half-court basketball does,"* *"commit the move the way UFC
Undisputed 3 does,"* or *"call it what NBA 2K calls it."* These are
reference-grounded, not taste calls. Asking the human to adjudicate them wastes a
decision the references already settle, and trains the human to expect a
question where they wanted an answer.

The project already names its references — scattered, not consolidated:

- CLAUDE.md's design identity: *"deliberately NOT arcade like NBA 2K,"* target
  feel *Undisputed 3*, primary anti-goal *arcade decoupling (EA UFC 5)*.
- [ADR-0003](0003-input-model-hybrid.md) carries a formal **Reference axis**
  (Target: *UFC Undisputed 3*; anti-targets: *EA UFC 5*, *Goat Simulator*) and
  notes the right-stick surface is *"2K-familiar"* while the moves resolve as
  *"locked-in commitments, not flow."*
- [ADR-0008](0008-possession-rules.md) owns the half-court 1v1 possession ruleset
  (make-it-take-it, live rebound, take-it-back/clear).

What was missing is a **precedence rule**: NBA 2K is simultaneously a
basketball-realism / rules reference **and** the named anti-reference for *feel*.
Without an explicit arbitration order, "abide by 2K" and "NOT arcade like 2K"
contradict each other, and the agent cannot safely self-resolve — so it asks.

This ADR records the arbitration order, the scope of what the agent may decide
from references alone, and the boundary of what still goes to the human. The
intent is to **reduce** the human-decision load, not eliminate it: genuine design
calls still route to the human (see *Escalation triggers*).

### Forces at play

1. **2K is dual-natured.** It is authoritative for basketball *taxonomy* and
   *control-surface familiarity*, and an explicit anti-reference for *feel*. A
   single reference cannot rank both ways without an explicit split.
2. **Self-resolution must stay auditable.** Granting the agent authority is only
   safe if each self-resolved call is recorded with its grounding, so the human
   can spot-check and overturn it as a reviewer rather than supply it as an oracle.
3. **"Feel" is the human's, by standing rule.** [ADR-0011](0011-claude-authors-scenes.md)
   reserves feel/tuning judgment and in-engine verification to the human. Any
   authority granted here must not quietly annex feel-tuning.
4. **The game is half-court 1v1, not full-court.** "Real basketball" must be
   scoped, or full-court rules (backcourt, team fouls, full shot clock) get
   imported into a game where they are nonsense.

### Alternatives considered

1. **Keep asking the human for all reference-grounded calls (status quo).**
   Rejected. It is the friction this ADR removes — the human is used as an oracle
   for answers the references already fix.
2. **"Just do what NBA 2K does."** Rejected. It contradicts the locked design
   identity (*NOT arcade like 2K*) and would import the primary anti-goal —
   arcade decoupling — under cover of "realism."
3. **Let the agent self-resolve everything, feel included.** Rejected. It annexes
   the human's feel/tuning authority (ADR-0011) and removes the human from
   genuine design calls they want to keep.

## Decision

**Reference-grounded design questions are resolved by the agent from a fixed,
ranked set of references — on the record — and only genuine design calls escalate
to the human.**

### 1. Precedence (strict lexical order — higher tier always wins, no balancing)

1. **Locked design identity + ADRs.** The spacing/commitment spine, the anti-goals,
   and anything in `docs/adr/`. Supreme — no reference overrides these. (A request
   that contradicts a locked ADR is still a STOP-and-flag, per CLAUDE.md Decision
   Discipline; this ADR does not loosen that.)
2. **Real half-court 1v1 basketball.** The physical-truth and rules anchor — what
   is bodily honest (feet plant; a dribble cannot be recalled off the floor) and
   what the pickup game's rules are. Scoped to **half-court 1v1 / pickup
   convention** (make-it-take-it, winner's outs, check-ball, clear-it); full-court
   NBA/FIBA rules apply **only by analogy where the half-court game is silent**
   (e.g. what a carry or double-dribble is). [ADR-0008](0008-possession-rules.md)
   remains the authority for the possession ruleset itself.
3. **UFC Undisputed 3.** Governs **commitment & feel**: how a move commits, its
   startup → active → recovery arc, and the legibility of the read. This is the
   target feel-axis already recorded in [ADR-0003](0003-input-model-hybrid.md);
   this ADR points at it rather than restating it.
4. **NBA 2K.** Authoritative **only** for basketball **taxonomy** (what a
   crossover, hesi, size-up, pull-up, and-one, take-foul *is*) and
   **control-surface familiarity** (dribble moves on the right stick, etc.). Its
   feel and arcade tendencies (shot meters, magnetic/auto-blended animations,
   ankle-breaker cinematics, free-cancels) carry **zero** weight; wherever 2K's
   feel conflicts with tiers 1–3, **2K loses by rule.**

**The 2K test:** *2K can tell me what a mechanic is called and where the button
lives. It can never tell me how the mechanic should feel, or whether it should
exist.* Mechanic **existence** is always a tier-1 identity call, never "2K has it
so we should too."

### 2. What the agent self-resolves vs. defers

- **Structural / directional / rules decisions → self-resolve** (with citation).
  E.g. "is the hesi its own committed move or a cancel?" (own move, per ADR-0003);
  "which hand does the crossover end on?"; "does an offensive rebound reset the
  clear?". These have reference-grounded right answers.
- **Pure-rules numbers → self-finalize**, no issue. Numbers real basketball fixes
  exactly with no feel component (a `TargetScore` default, shot-clock seconds).
- **Feel-tuned magnitudes → author a starting value, then file an HITL verify
  issue.** Frame counts, stamina cost, turn-rate caps, scatter magnitude. The
  agent ships a *defensible, cited* first value and files a single-purpose `hitl`
  issue ([ADR-0013](0013-afk-hitl-separate-issues.md)); the human closes it on
  feel, or tweaks. **No up-front ask.** This is the ADR-0009 (scatter) / ADR-0010
  (turn-rate) pattern.

### 3. Escalation triggers — what still goes to the human *before* coding

Self-resolution is the default for reference-grounded calls. Stop and ask
(`AskUserQuestion` or `/grill-me`), or run `/doubt-driven-development`, **only**
when:

1. **Design-identity / spine / anti-goal change** — anything that would relitigate
   *what the game is*. (Mechanic *existence within* the identity is self-resolved;
   changing the identity is not.)
2. **ADR contradiction** — a hard STOP-and-flag, unchanged from Decision Discipline.
3. **Genuine reference deadlock** — the precedence order does not cleanly resolve
   it: the references are silent, or they all point at an arcade answer tier-1
   forbids with no honest alternative in view.
4. **High-stakes irreversible / authoritative decisions** — netcode, authority,
   or state where a confident-but-wrong answer is costly to unwind. Default to
   `/doubt-driven-development`; ask if doubt survives.

### 4. Recording rule — cite-or-ask

A self-resolution is only trustworthy if it is auditable. For any **non-trivial**
decision (one that would otherwise have prompted a question):

- **(a) Cite or ask.** State, in the commit/PR body (and a new ADR if the call
  rises to architectural), **which reference, which tier, and the specific real
  behaviour being modelled** — and pass ADR-0003's **"contradicts vs. refines"**
  test. If the reference and tier cannot be named, that is the signal to ask, not
  to guess.
- **(b) No bare-preference appeals.** *"It's more fun," "it's smoother,"* or *"2K
  has it"* — with no tier grounding — is **not** a valid self-resolution. Fun/feel
  routes to the HITL tuning issue (§2) or to the human (§3). The references
  constrain *structure*; they do not license taste calls dressed as references.

Trivial calls need no citation.

### 5. The reference set is closed

The reference set is **{ real half-court 1v1 basketball, UFC Undisputed 3, NBA 2K }**,
**closed by default and extensible only by ADR.** Invoking a fourth reference (a
fighting game for frame-data convention, And1/streetball for showmanship, etc.) is
an ADR amendment with reasoning — never an ad-hoc mid-task appeal. (The showmanship
layer is explicitly out of scope at time of writing.)

## Consequences

**Easier:**
- Reference-grounded questions stop reaching the human as questions; they reach
  them as **reviewable cited decisions** instead. The human is a reviewer who can
  overturn, not an oracle who must supply.
- The 2K contradiction is resolved structurally: 2K is authoritative for taxonomy
  and controls, mute on feel — so *"abide by 2K"* and *"NOT arcade like 2K"* stop
  fighting.
- Feel-tuning keeps its home (ADR-0011 / ADR-0013) — the agent seeds a value and
  files the verify issue rather than asking, and the human's feel authority is
  untouched.

**Harder / accepted tradeoffs:**
- **Citation discipline.** Every non-trivial self-resolved call must name its
  reference + tier in the commit/PR body. This is overhead on each such decision,
  and the cost of keeping the authority auditable.
- **Judgment at the boundary.** "Genuine reference deadlock" and "high-stakes
  irreversible" are judgment calls; mis-classifying a real design question as
  self-resolvable is the failure mode to watch. When unsure which side of the
  line a call sits on, treat it as escalation, not self-resolution.
- **Does not lower any bar.** This changes *who answers reference-grounded
  questions*, not the design identity, the ADRs, or `Done means proven`.

**Reversible.** This is a decision-authority convention, not code; if self-resolution
proves to overreach, the project can tighten the escalation triggers or revert to
asking, without touching anything that ships.
