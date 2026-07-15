---
name: hooper-external-positioning
description: What is genuinely novel about hooper-game vs. prior art, and what must be proven before claiming it publicly. Load this before writing anything outward-facing — README updates, devlogs, papers, talks, release notes, social posts — or any time a claim compares this project to other agentic-coding, netcode, or game-design work. Provides a graded claim inventory, an unproven-until list, a reproducibility standard for any external number, and house rules against oversell.
---

# Hooper external positioning

This skill governs **outward-facing claims** about `hooper-game`: what you may
say to an audience outside this repo, how confidently, and what has to be true
first. It does not cover internal docs (ADRs, handoffs, analysis) — see
`hooper-docs-and-writing` for those — and it does not decide what to build
next — see `hooper-research-frontier` for that. This skill is downstream of
both: it tells you what you have *earned the right to say* about work those
two skills describe.

The human has designated three frontier directions for this project
(2026-07-12), and every claim below is organized under one of them:

1. **AI-driven dev methodology** — the ADR-governed agent authority model and
   the autonomous-merge-on-harness-proof loop (ADR-0013 → ADR-0019).
2. **Netcode at indie scale** — server-authoritative + client prediction,
   deterministic ball mini-physics, built and tested by a solo non-gamedev
   developer.
3. **Legible competitive design** — fighting-game frame data applied to a
   basketball 1v1 duel, with legibility as a *competitive requirement*
   (CLAUDE.md §1), not an aesthetic choice.

## The governing rule: no claim outruns its proof

Every claim below is graded **Proven / Partial / Candidate / Do-not-claim**.
Only "Proven" claims may appear in outward-facing text without a hedge.
"Partial" and "Candidate" claims must carry an explicit qualifier in the same
sentence ("not yet cross-machine verified," "single-process only," etc.) —
never bury the caveat in a footnote three paragraphs later. "Do-not-claim"
items are things the repo cannot currently back up at all; if you're tempted
to write one, stop and either gather the proof first or leave it out.

---

## 1. Claim inventory

For each candidate claim: what the repo actually demonstrates today, the
prior art it must be positioned against (so it doesn't read as naive), and
the specific proof bar before it can be claimed without a hedge.

### 1.1 "A solo non-gamedev developer shipped a server-authoritative netcode game via AI agents, with autonomous merge gated on a headless harness."

**Grade: Partial.**

What's actually demonstrated:
- A real ADR-governed institution exists and has run for real work: ADR-0013
  (afk/hitl separation) through ADR-0019 (session-driven orchestration) —
  six ADRs specifically about *how the agents are allowed to operate*, not
  about the game (ADR-0018, inside that number range, is a game-design ADR
  about defensive timing windows and doesn't count toward this claim). This is documented, dated, and has real incidents recorded
  against it (PR #169's deleted-base auto-close; the #83–#86 dual-label
  deadlock; the #134 code-before-ADR drift) — it is not aspirational process
  theater, it visibly survived contact with real failures. See
  `hooper-change-control` for the incident details.
- Autonomous merge is real and gated: `.claude/agents/orchestrator.md` +
  `.claude/agents/issue-worker.md` implement dispatch→review→merge with
  hard gates (CI build, full `dotnet test`, headless harness for
  harness-checkable issues, clean `/code-review`) per ADR-0015, and "no merge
  on red, ever" is enforced by the orchestrator's independent `gh pr checks`
  confirmation (the local Stop-hook gate is deliberately weaker and is not
  the real gate — see `hooper-change-control` §4).
- `dotnet test tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj
  --configuration Debug` — 664 passed / 5 skipped (verified live
  2026-07-12) — is a real, substantial, engine-free unit suite, and
  `tests/integration/` is a real headless-Godot harness (~20 scenario test
  files driving real scenes, plus 4 dual-process network scripts).

Prior art this must be positioned against:
- **Agentic coding tools generally** (Claude Code, Devin, Cursor agents,
  GitHub Copilot Workspace, etc.) — "an AI wrote the code" is not novel by
  itself in 2026.
- **CI-gated automerge** (Mergify, GitHub auto-merge on required checks,
  Renovate/Dependabot automerge) — "merge only when checks pass" is a
  solved, common pattern.

The genuinely novel part is neither of those individually — it's the
**"proven-by-harness" redefinition of "done"** (ADR-0015/0016 replacing a
human-in-the-loop verification step with a headless-Godot state-assertion
surface, adopted specifically because unit tests structurally cannot reach
`Node`-derived engine code) **combined with** an explicit, ADR-numbered
**agent authority model** that defines what an agent may decide unilaterally
(ADR-0014's reference-tier "cite-or-ask" ladder) versus what must escalate
to a human (design-identity changes, ADR contradictions, feel judgments).
ADR-0013→0019 read together are a *designed institution* for agent
authority, with its own recorded incidents and amendments — claim *that
specific combination*, not "AI wrote a game."

Proof bar before claiming this without a hedge:
- The claim currently rests on **single-machine / single-network**
  verification. Cross-machine dedicated-server play is explicitly **not**
  human-verified — issue #32 is open (confirmed `state: OPEN` 2026-07-12).
  Do not claim "shipped a networked game" without naming that gap.
- "Shipped" is not yet true in the release sense at all — no public release
  exists (see §2). Say "built," not "shipped," until a release is real.
- If claiming "fully autonomous," cite the specific PRs/issues that went
  dispatch→review→merge with zero human intervention in the critical path —
  not the process's *design* for zero intervention. Design intent and
  demonstrated runs are different claims.
- ADR-0019 explicitly rejected unattended cron / stored-credential
  orchestration — the loop only runs inside a live, human-started session.
  Any claim implying a background/always-on agent is false; say
  "session-driven," and remember human feel passes still gate milestone
  closure (ADR-0015/0017).

### 1.2 "Deterministic mini-physics + client prediction/reconciliation, implemented in Godot 4 C#."

**Grade: Partial — solid as a reference implementation, weak as a technique claim.**

What's actually demonstrated:
- A genuinely from-scratch, engine-free deterministic ball simulation
  (`scripts/Ball/` pure classes: `ShotArc`, `RimBackboard`, `FloorBounce`,
  `BallStateMachine`, sequenced by the `BallController` node) using
  fixed-`dt` trapezoidal integration, chosen over semi-implicit Euler
  because Euler undershot by `0.5*g*t*dt` and clanged the front rim
  (documented in `ShotArc.cs` — a reasoned decision, not a default).
- A real client-prediction/server-reconciliation implementation in
  `scripts/Player/PlayerController.cs`: input buffering
  (`PredictionBuffer`), replay-on-reconcile at fixed `dt`, a documented
  "NETCODE LAW" against comparing a ~1-RTT-stale broadcast frame counter,
  and a mesh-offset smoothing scheme that hard-snaps the physics body but
  lerps only the visual mesh child.
- This is *tested*, not just written: unit tests for the pure math, plus
  dual-instance integration harnesses (`tests/integration/run-net-handshake.sh`,
  `run-net-state-sync.sh`, `run-net-node-replication.sh`,
  `run-net-behindtheback-sweep.sh`) that boot two real headless Godot
  processes and assert on actual wire behavior (ENet handshake, in-order
  authoritative tick sync, spawner replication, remote-display sync).

Prior art this must be positioned against — and this is the load-bearing
caveat:
- **Glenn Fiedler's "Gaffer On Games"** series is the canonical public
  reference for exactly this class of technique (fixed-timestep
  deterministic simulation, client-side prediction, server reconciliation)
  and predates this project by well over a decade.
- **Overwatch's and Rocket League's GDC netcode talks** describe
  production-scale versions of the same prediction/reconciliation pattern
  at far greater rigor (rollback windows, interest management,
  determinism at scale).
- **Existing Godot rollback/netcode addons** in the community ecosystem
  already implement prediction/rollback patterns for Godot.

Given that prior art, **do not claim this is a novel technique.** It isn't.
The honest, defensible claim is narrower: a **documented, tested, open,
from-scratch reference implementation** of well-established
netcode/determinism theory in Godot 4 C#, built end-to-end by a
non-networking-specialist via AI-assisted development, with design
decisions and their rejected alternatives traceable to ADR-0002/ADR-0004
and in-code "why not the obvious approach" comments. Frame it as *"a small,
readable, tested reference you can read start to finish,"* never as *"we
invented client-side prediction."*

Proof bar before claiming determinism specifically: cross-platform
bit-identical behavior has not been verified — dev runs on Windows, CI on a
Linux runner, but no seeded same-scenario byte-identity comparison across
OS/CPU exists. Do not claim "deterministic across platforms" without
running and citing that specific check; claim "deterministic under a fixed
timestep with seeded server-side RNG," which is what the tests actually
pin.

### 1.3 "A fighting-game frame-data model applied to basketball 1v1, with legibility framed as a competitive requirement."

**Grade: Candidate — the framing is real, the mechanical depth is partial (M10 defense is mid-build).**

What's actually demonstrated:
- Real Startup/Active/Recovery frame data on every committed move
  (`scripts/Input/MovePhase.cs`, `MoveFrameData.cs`), enforced structurally:
  no flow-cancel exists, `Feint()` is the only windowed abort, and
  movement/auto-dribble are hard-blocked while a move is active — the
  engine actually withholds control during commitment; this is not cosmetic
  framing bolted onto an arcade action-cancel system (ADR-0003, whose
  primary anti-goal is exactly that "arcade decoupling").
- A real, documented reference-authority hierarchy (ADR-0014) that ranks
  real half-court basketball above *UFC Undisputed 3* feel above 2K
  taxonomy, with 2K's *feel* given **zero weight by rule** — a distinctive,
  citable design-governance artifact.
- ADR-0018's shared `DefensiveResolution.Succeeds` predicate — integer-tick
  half-open interval overlap between a defender's Active window and a
  target's vulnerable window, with no hidden RNG — is a genuine mechanical
  instantiation of "legibility means a fair, checkable read," including the
  symmetric committed-read defense (defenders commit with real
  startup/recovery too, and whiffs are punishable).

Prior art this must be positioned against:
- **Fighting games themselves** (Tekken is named in CLAUDE.md §1; Street
  Fighter et al.) are where frame-data-as-legibility originates — credit
  them as the source of the vocabulary; do not present frame data as this
  project's invention.
- **2K's shot-timing meters / green windows** are the nearest existing
  basketball-genre analog to a legible timing window — name them and
  distinguish: this project explicitly gives 2K's *feel* zero authority
  (ADR-0014 tier 4), which is itself the interesting thing to explain, not
  elide.

The distinctive, claimable part is the **combination**: startup/active/
recovery commitment applied to a basketball duel so that *both* players'
commitment is legible (legibility-as-competitive-requirement, bounded by
named anti-goals), plus a symmetric committed-read defense resolved by
transparent window overlap rather than a hidden percentage roll — not the
mere presence of "frame data" or "timing windows," which exist piecemeal
elsewhere.

Proof bar before claiming this is "done" or plays well: M10 (defense) is an
**active, mid-build umbrella epic** (CLAUDE.md §2) — steal and block exist
and are harness-tested, but block currently has **no reach/proximity term**
(deferred to #214; a documented placeholder, not a secret gap), and the
combined M9+M10 **feel pass (#114) is still open** (confirmed `state: OPEN`
2026-07-12). Structural claims (frame data exists, commitment is enforced,
resolution is transparent) are fine today; feel claims ("the reads are
satisfying," "steals feel fair") are off-limits until #114 closes.

---

## 2. Unproven-until list

These may **never** be implied as done, live, or verified in outward-facing
text, regardless of how confident the underlying engineering feels:

| Claim | Status (2026-07-12) | What would prove it |
|---|---|---|
| Cross-machine dedicated-server play works | **Not human-verified.** Issue #32 (`hitl`) open. Everything proven so far is dual-process-on-one-machine (localhost harness scripts) — cross-machine is untested in confirmed practice. | #32 closed on a logged in-editor cross-machine session, or a documented cross-machine harness run. |
| A public release exists | **None.** No git tags, no GitHub releases (`git tag` and `gh release list` both empty, checked 2026-07-12). | A tagged, published build reproducible per §3 (note the export-preset gap there). |
| Performance / mobile numbers | **None exist.** M15 (mobile, performance & release readiness) is a DEFERRED planning epic, not started. | M15 activated (ADR-0017 order) plus a characterization doc (0079 pattern, §3) measuring real frame time / device targets. |
| Feel quality ("the game feels good") | **Not signed off.** The combined M9+M10 feel pass (#114) is open; its M10 section is explicitly a placeholder until that code merges. | #114 closed by the human feel pass. |
| "Fully autonomous, no human in the loop" | **False as stated.** ADR-0019 requires a live human-started session (no unattended cron); humans own feel passes and milestone-closure sign-off. | Not provable as stated — reframe as "session-driven autonomous merge with human feel gates." |
| Cross-platform deterministic ball behavior | **Not verified.** No seeded byte-identity comparison across OS/CPU exists. | A documented run of the same seeded scenario on two OS/CPU combinations with byte-identical output logged. |

If a draft contains language that implies any row above without its
qualifier, the draft is not ready to publish — fix the sentence, don't just
mentally note the caveat.

---

## 3. Reproducibility standard for any external number or claim

Any claim made to an outside audience must be **reproducible from a fresh
clone plus documented commands.** Concretely:

1. **State the exact commands.** If you cite "664 tests pass," accompany it
   with the command that regenerates it:
   `dotnet test "tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj" --configuration Debug`
   (run from the repo root — note the repo path contains spaces, quote it in
   shells). Numbers go stale; the regenerating command does not. ADR-0016
   itself contains a stale hardcoded test count in-repo — a documented
   example of why bare numbers rot. Don't repeat that mistake externally.
2. **Export presets are gitignored — a release claim is not reproducible
   today.** `export_presets.cfg` and `export.cfg` are excluded from version
   control (`.gitignore` lines 13–14, confirmed 2026-07-12). A fresh clone
   cannot produce an exportable binary without first authoring presets that
   don't exist in the repo. Do not claim "anyone can build a release from
   this repo" until that gap is closed (commit sanitized presets, or
   document the exact editor steps to recreate them).
3. **Numbers come from the characterization/harness surfaces, method
   stated.** Follow the `docs/analysis/0079-shot-scatter-curve.md` pattern:
   name the exact instrument that produced the number (e.g. "measured via
   `tests/Hooper.Ball.Tests/ShotScatterCurveCharacterizationTests.cs`, a
   deterministic 100×100 stratified-grid sweep, 10,000 samples per point,
   no RNG, through the real `ShotScatter → ShotArc → RimBackboard` chain"),
   never a bare assertion. A number with no named method is not citable
   externally.
4. **"The harness proves X" claims must name the scenario and its
   control.** Per the discipline this repo already follows internally (see
   `hooper-verification-and-qa`): never cite a harness pass in isolation —
   name the specific scenario (e.g. "`BlockTurnoverTest` scenario
   `success`") *and* its counterfactual control (`control-make`, which
   proves the identical unblocked shot scores, so "score stayed 0-0" in the
   blocked case is meaningful rather than vacuous). Every "X didn't happen"
   assertion needs its control scenario named alongside it.
5. **Fresh-clone check before publishing a "try it yourself" claim.** If a
   devlog or README section tells an outside reader to run something, walk
   the literal sequence from a clean checkout at least once before
   publishing. Note the headless harness additionally needs a Godot
   4.6.3 .NET binary (CI provisions it via `chickensoft-games/setup-godot@v2`,
   version 4.6.3, `use-dotnet: true`; locally, point at your own install) —
   an instruction that omits this prerequisite is not reproducible.

---

## 4. House rules

- **No oversell.** State what is proven; label everything else `open` or
  `candidate` explicitly in the published text, not just in your head.
  "We built a netcode game" while cross-machine play is unverified is
  oversell; "we built and locally-verified a server-authoritative netcode
  architecture; cross-machine play is an open verification item (#32)" is
  honest.
- **Design-identity and roadmap statements must match CLAUDE.md §1/§2 in
  spirit.** If an external doc describes what the game *is* (spine,
  commitment layer, legibility, anti-goals) or where the roadmap stands
  (milestone table), cross-check against CLAUDE.md's current wording before
  publishing. Calling a DEFERRED milestone "in progress," or describing a
  mechanic §1 lists as an anti-goal as a feature, is a positioning bug, not
  a stylistic choice.
- **External docs never contradict ADRs.** If a claim would assert something
  an ADR's Decision explicitly rejected (e.g. implying rollback netcode when
  ADR-0002 rejected rollback for server-authoritative), that is the same
  "ADR contradiction" stop-and-flag class `hooper-change-control` §5–6
  applies inside the repo — treat it identically outside. Fix the claim;
  never reinterpret the ADR to fit a punchier sentence.
- **Grade every claim before it ships** on the Proven/Partial/Candidate/
  Do-not-claim scale. A Candidate claim presented with Proven-level
  confidence is the single most common failure mode this skill exists to
  prevent.
- **Prior art goes in the same paragraph as the claim**, not in a citations
  footnote at the end — a reader who encounters only the claim sentence must
  still get the honest framing. The §1 inventory models this.
- **When in doubt, undersell rather than hedge-and-bury.** A flat, modest
  sentence needing no caveat beats a strong sentence with a caveat three
  clauses later that most readers won't parse.

---

## When NOT to use this

- **Deciding what to build next, or where this project could advance the
  state of the art** — use `hooper-research-frontier`. That skill is about
  *doing* frontier work; this one is about *honestly describing* work
  already done.
- **Writing internal documents** — ADRs, amendments, handoffs, analysis
  docs like `0079-shot-scatter-curve.md` — use `hooper-docs-and-writing`
  for house style and templates. This skill governs only claims made to an
  audience outside the repo's own working process.
- **Whether a change needs a human before landing, or how an issue/PR/ADR
  is structured** — use `hooper-change-control`. This skill assumes the
  engineering already went through those gates correctly.
- **Sourcing the underlying facts** — test counts and harness mechanics
  belong to `hooper-verification-and-qa`; system invariants and the ADR
  ledger to `hooper-architecture-contract`; netcode theory-as-applied to
  `hooper-netcode-reference`; design rules and frame data to
  `hooper-duel-design-reference`. Pull the fact from its owning skill,
  then apply this skill's §3 reproducibility standard before quoting it
  externally.

---

## Provenance and maintenance

Verified against the live repo and `gh` on **2026-07-12**; reviewed and
corrected 2026-07-15 (process-ADR count seven → six: ADR-0018 in the
13–19 range is a game-design ADR, not an agent-authority one):
- Issue #32 (`hitl`, "Editor: test headless server + connect via browser")
  — state `OPEN`.
- Issue #114 (`hitl`, combined M9+M10 feel & dual-instance verify) — state
  `OPEN`.
- `git tag` and `gh release list` — both empty (no public release).
- `.gitignore` lines 13–14 — `export.cfg` and `export_presets.cfg`
  gitignored.
- Unit-test count 664 passed / 5 skipped — from the live
  `dotnet test tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj` run in this
  skill library's discovery pass (2026-07-12).
- ADR ledger: 20 files (`0000` template + `0001`–`0019`), all
  `Status: Accepted`, no gaps — cross-checked against `docs/adr/`.
- `docs/analysis/0079-shot-scatter-curve.md` exists and matches the method
  cited in §3.
- README.md's existing public framing read in full — nothing above
  contradicts it.

Re-verify before reuse if any of these may have drifted:

- `gh issue view 32 --json state` and `gh issue view 114 --json state` —
  confirm both still open before repeating the §2 table; if either closes,
  move its row and upgrade the affected §1 grade.
- `git tag` and `gh release list` — confirm still empty before repeating
  "no public release exists."
- `grep -n "export" .gitignore` (from the repo root)
  — confirm `export.cfg`/`export_presets.cfg` still excluded before
  repeating the §3 release-reproducibility gap.
- `dotnet test "tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj" --configuration Debug`
  — re-run before citing a test count; never reuse a stale number.
- `ls docs/adr/*.md | wc -l` — confirm the ADR count (20) before repeating it.
- Re-read CLAUDE.md §1/§2 before publishing anything restating design
  identity or milestone status — both change more often than this skill
  will be re-verified.
