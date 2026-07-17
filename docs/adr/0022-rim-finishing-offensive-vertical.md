# ADR-0022 — Rim-finishing offensive vertical (un-defer of #203)

- **Status:** Accepted
- **Date:** 2026-07-17
- **Superseded-by:** —

---

## Context

The euro-step was originally filed and then parked as issue #203: it is
rim-finishing footwork (a lateral evasive gather-step used while driving to
the rim) with nothing to finish *into*. The only shot the game has is
`JumpShot` — there is no "at the rim" context, and the euro-step's entire
purpose (sidestepping a shot-blocker on the way to a finish) half-lives in
M10's contest/block interaction, which itself only resolves against
`JumpShot` today.

Building the euro-step directly against that missing prerequisite would be an
arcade-decoupling anti-goal in miniature (CLAUDE.md §1): footwork whose
purpose (dodging a block) has nothing concrete to dodge *toward*, so the
"read" it creates is hollow. Issue #203 was therefore un-deferred as an
**umbrella epic** on 2026-07-17, and re-scoped to build the prerequisite
vertical first, in strict dependency order:

1. **#229 — layup / rim-finish shot type.** The contestable close finish.
2. **#230 — drive-gather.** The committed drive the euro-step is a lateral
   variant of.
3. **#231 — euro-step.** The original #203 content — gated `blocked` until
   #229 and #230 land.
4. **#232 (this record) — the decision record + taxonomy + glossary.**

This ADR is the decision record item. It does not implement anything; #229
and #230's own PRs carry whatever per-feature ADR content their specific
frame-data/tuning choices need. This ADR's job is narrower and prior to both:
record *that* a rim-finishing vertical is now an active M9 direction, *why*
it un-defers #203, and *which* of the two candidate homes — a new ADR number,
or an amendment to ADR-0009 (shot accuracy) — the decision belongs in.

### Why this needed a Decision Discipline call at all

Two things are new here that CLAUDE.md's milestone table and ADR-0009 do not
currently describe:

- A **second shot type** (`layup`, distinct from `JumpShot`) now exists as a
  design direction. ADR-0009 currently describes exactly one committed move
  (`JumpShot`) feeding the distance/scatter make model.
- A **new committed-move family** — the straight-line drive-gather, and the
  euro-step as its lateral evasive variant — is introduced as a mechanic
  category. Nothing in `docs/adr/` currently names "the drive" as a
  committed-move family the way #194/#198 named "the burst family" for
  dribble moves.

Per CLAUDE.md's Decision Discipline, this is recorded rather than left
implicit in the milestone table alone.

## Decision

**Rim-finishing (drive-gather → layup → euro-step) is a new ADR, not an
ADR-0009 amendment.** ADR-0009 amends when the *shot-accuracy composition
formula itself* changes (as its four prior amendments did: movement,
contest, facing, committed-contest penalties). The layup introduces no new
term to that formula — it is a **new committed move that becomes a second
consumer of the existing, unchanged `ShotScatter` → distance/scatter make
model** (ADR-0009), exactly as `JumpShot` is today. Filing that as an
ADR-0009 amendment would misrepresent the change: nothing about *how shots
are scored* is different; what's different is that a *second kind of shot*
now exists, which is a new architectural axis in its own right (parallel to
how ADR-0012 got its own number for ball-hand-side authority rather than
folding into ADR-0004 — a new decision, not a retune of an existing one).

Concretely, this un-defers #203 and establishes the following as the M9
rim-finishing vertical's shape, for #229/#230/#231 to build against:

1. **Layup / rim-finish (#229) reuses the existing make model verbatim.**
   It is a distinct committed move (its own `MoveFrameData` — Startup,
   Active with its own release point, Recovery), but it feeds the *same*
   `ShotScatter`/distance-curve/`RimBackboard` chain ADR-0009 already
   established, not a parallel accuracy model. It is contestable by the
   *existing* block timing window (#98/#214) with no new defensive
   primitive — reusing ADR-0018's `DefensiveResolution.Succeeds` exactly as
   `JumpShot` does today.
2. **Drive-gather (#230) is a committed move in the existing
   Startup/Active/Recovery machine**, using the *existing* hybrid-gather
   momentum model (#198's `GatherDecel`-style bleed, not an instant-zero
   plant) rather than inventing a second momentum scheme. Its Active window
   plants a forward line toward the rim that #229's layup launches from.
3. **Euro-step (#231) is the lateral evasive variant of the drive-gather**,
   reusing #194's exit-cone precedent (a clamped exit angle at Active-entry
   is the mechanism for "narrower/lateral," not a longer Recovery or a
   cooldown — both were rejected for BehindTheBack on the same grounds and
   the reasoning transfers). It is gated on #229 and #230 landing first,
   because its entire point — evading a block attempt en route to a finish
   — requires both a finish to evade toward and a drive to be evading
   *during*.

### Taxonomy (cite-grounded per ADR-0014)

- **Layup / rim-finish** — *Real half-court ball, tier 2.* The basic
  close-range finish at the rim: a short-range shot taken directly off a
  drive, at high percentage when uncontested, contestable by a help or
  on-ball shot-blocker. This is the tier-2 fact ADR-0009's own anchor table
  already encodes numerically (≤3 m ≈ 100% open) — #229 gives that anchor a
  *distinct committed move* to attach to, rather than leaving "close-range
  jump shot" as the only representation of it. *2K, tier 4 (taxonomy only,
  zero feel weight):* "layup" is the name 2K uses for this exact shot family
  and the control surface (separate button/gesture from the jump shot) is
  familiar; 2K's *feel* for layups (window dressing like contact animations,
  layup packages) carries no weight here and is explicitly out of scope
  (see #229's "no floater/dunk/contact-finish variants" scope cut).
- **Drive-gather** — *Real half-court ball, tier 2.* "The gather" is the
  moment a driving ball-handler picks up their dribble and commits their body
  toward the rim — the committed instant before a finish, the drive
  equivalent of the cradle ADR-0008's 2026-07-05 amendment already named for
  the shooting motion (`CradleForShotStartup`). Modeling it as its own
  committed move (rather than undifferentiated locomotion) is the same move
  ADR-0008 made for the shooting cradle: a real, physically-committed action
  deserves its own state, not an implicit side effect of movement.
  *Undisputed 3, tier 3:* the hybrid-gather momentum bleed (#198) — Startup
  decelerates existing velocity through a hard-but-finite decel rather than
  an instant plant — is exactly the "the body carries momentum into a
  commitment, it doesn't teleport-stop" legibility model Undisputed 3 sets
  for committed strikes; the drive-gather is declared **opt-in to that same
  model** rather than inventing a third momentum scheme, per #198's own
  "opt-in for future burst-family moves" framing.
- **Euro-step** — *Real half-court ball, tier 2.* A lateral evasive
  gather-step used while driving, to sidestep a defender's outstretched
  block attempt on the way to a finish — real basketball footwork, not an
  invented mechanic. *2K, tier 4 (taxonomy only):* 2K calls this exact move
  "Euro Step" and that name is adopted verbatim as the taxonomic term (2K's
  *feel* for it — auto-triggered animation packages, contact RNG — carries
  zero weight, per ADR-0014's closed 2K-taxonomy-only rule). *Undisputed 3,
  tier 3, by structural analogy, not direct precedent:* Undisputed 3 has no
  basketball footwork of its own, but #194's exit-cone precedent (a clamped
  lateral exit angle at Active-entry is the entire mechanism for "a
  narrower, evasive variant of an existing committed move," not a longer
  Recovery or a cooldown) is the Undisputed-3-modeled legibility pattern the
  euro-step inherits directly from BehindTheBack's own citation.

### CLAUDE.md §2 update (same commit)

The M9 milestone summary and status table are updated in this ADR's sibling
docs commit to list the rim-finishing chain (#203 umbrella: #229 → #230 →
#231, plus this doc, #232) as an active, no-longer-deferred part of M9's
remaining work.

## Consequences

**Easier / what this buys:**

- #229 and #230 (and eventually #231) now have a named vertical and a shared
  vocabulary to build against, instead of each independently re-deriving
  "what is a layup / drive-gather / euro-step in this codebase's terms."
- The euro-step is no longer built against a hollow prerequisite — the
  arcade-decoupling risk #203's original deferral flagged is resolved by
  sequencing, not by relaxing the identity bar.
- ADR-0009 stays a clean record of *the accuracy formula's* evolution; this
  ADR is a clean record of *a new shot type and move family existing at
  all*. Neither doc has to awkwardly carry the other's content.

**Harder / accepted tradeoffs:**

- This ADR intentionally does **not** pin frame-data numbers, exact tunable
  names, or the drive-gather's exact heading-integration approach — those
  are #229/#230's own implementation decisions, to be recorded in their own
  commits/PRs per the same-commit-as-code rule. A reader looking here for
  "what are the layup's Startup/Active/Recovery frame counts" will not find
  them; that is deliberate scope discipline (#232's own acceptance criteria:
  "no feature code in this leaf").
- **Rejected alternative — fold this into ADR-0009 as a fifth amendment.**
  Considered and rejected: ADR-0009's amendments (#64/#65/#81/#99/#100) each
  changed the accuracy *formula* itself (a new multiplicative factor). This
  decision changes *what kinds of shots exist*, not how any shot's accuracy
  is computed — conflating the two would make ADR-0009 harder to read as "the
  history of the make-percentage formula" and would bury a genuinely new
  architectural axis (a second shot type, a new move family) inside a
  document whose amendments are otherwise all formula-level.
- **Rejected alternative — build the euro-step first, treat drive-gather
  and layup as its dependencies to backfill later.** This is precisely the
  ordering #203's original deferral rejected: the euro-step's own purpose is
  incoherent without something to finish into and something to be gathering
  *during* — building it first would either produce a hollow no-op move or
  silently smuggle in an ad hoc finish/drive just to give it something to
  attach to, duplicating #229/#230's future work under worse conditions.
- **Rejected alternative — build a floater/dunk/contact-finish family now,
  since "rim-finishing" invites it.** Out of scope by #229's own text
  ("a rim-finishing family is explicitly not being built here; ADR-0014
  tracer-bullet scope") — this ADR affirms that scope cut rather than
  quietly expanding it. A single close-range finish (the layup) is the
  tracer bullet; additional finish variants are a future decision, not
  implied by this one.

### Cross-references

- Issue #203: the umbrella this ADR un-defers.
- Issue #229: layup / rim-finish shot type — reuses ADR-0009's make model
  verbatim, per this ADR's Decision.
- Issue #230: drive-gather — reuses #198's hybrid-gather momentum model, per
  this ADR's Decision.
- Issue #231: euro-step — reuses #194's exit-cone precedent, gated on #229
  and #230.
- ADR-0009 (shot accuracy): the accuracy model the layup plugs into
  unchanged — this ADR does **not** amend it.
- ADR-0003 (input model) + its #194/#198 amendments: the exit-cone and
  hybrid-gather precedents this ADR's taxonomy cites directly.
- ADR-0008 (possession rules) + its 2026-07-05 amendment: the cradle
  precedent the drive-gather's naming is modeled on.
- ADR-0014 (reference authority): the ranked-tier citations used throughout
  this ADR's Taxonomy section.
