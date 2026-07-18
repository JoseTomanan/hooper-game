# ADR-0024 — HITL restructured to asynchronous evidence: harness-first decomposition, rendered-evidence review, default-with-veto

- **Status:** Proposed (accepting this branch's merge is the human's async nod — see Context)
- **Date:** 2026-07-18
- **Superseded-by:** —

---

## Context

On 2026-07-18 the human instructed, verbatim: *"Think of how it can be
improved, and how current HITL tasks can be restructured to be AFK (e.g.,
tuning, feel tests). I need to defer the HITL tests for as long as I need to,
because I am busy."* This is the same class of scheduling directive that
produced ADR-0021 (feel deferred indefinitely), extended one step: not just
*defer* the human checks, but *restructure* them so as much as possible stops
requiring a live human editor session at all.

Because the mechanisms below are agent-designed (the deferral itself is
human-ordered, the specific machinery is not), this ADR ships as
**Status: Proposed** on the workflow branch. Merging the branch is the
acceptance act — a minutes-scale async decision, consistent with the policy
the ADR itself proposes. Until merged, nothing here overrides ADR-0013/0015/
0016/0021 as written.

### The finding: `hitl` is one label covering three different kinds of work

Open `hitl` debt as of 2026-07-18 (re-verify:
`gh issue list --label hitl --state open`): #112, #114, #129, #153, #156,
#162, #173, #178, #184, #185. Plus two `afk` issues blocked on a human
decision: #170 (asset-license pick) and #206 (design decision gate). Reading
every body, they split three ways:

1. **State-checkable dual-instance verifies written in the pre-ADR-0016
   idiom** ("human runs two windows and watches"): #112 (M11 stamina
   drain/regen/reconcile), #129 (M12 session lifecycle), and the sync half of
   #162 (same audio cue fires at the same logical moment on both peers).
   ADR-0016 already rules these harness territory — the harness stands up
   dual instances today (`tests/integration/Net*Test.cs`) — but the issues
   were never decomposed, so harness-checkable criteria sit hostage to human
   scheduling.
2. **Visual/audio "does it read" judgments**: #153 (net/fence look), #178
   (rig proportions eyeball), the feel halves of #184/#185 (animation clip
   look), the mix half of #162. Today these require the human at a keyboard
   with the editor open. But the *judgment* needs a human; the *live session*
   does not — if deterministic rendered evidence (screenshots, short clips)
   can be captured in CI and attached to the PR/issue, the human can judge
   from their phone, weeks later, in minutes.
3. **Bounded decision gates blocking AFK work**: #156 (SFX sourcing —
   already decoupled 2026-07-18 by #244's synthesized placeholders, the
   precedent for this pattern), #170's license pick, #206's design gate.
   Each blocks a lane of AFK work on a small human decision that has a
   defensible, citable, *revertible* default.

### Forces at play

1. **ADR-0015 gate 4 is untouchable.** "Feel is never auto-accepted as feel."
   Every mechanism below moves the *medium* or the *timing* of a human
   judgment, never replaces the judge. Anything that would auto-close a feel
   criterion without human eyes is out of scope, exactly as ADR-0021 drew the
   line.
2. **The live session is the bottleneck, not the judgment.** A dual-instance
   editor session costs the human an hour of setup + attention. An artifact
   review costs minutes and needs no machine. Same judge, ~10× cheaper.
3. **Deferred feel debt compounds.** #173's checklist grows every milestone
   (ADR-0021 accepted this). Pre-captured evidence and scripted scenarios cap
   the eventual pass's cost — the pass becomes review-and-check-off, not
   rediscover-and-set-up.
4. **Rendered capture on CI is unproven here.** `--headless` does not render;
   capture needs movie-maker mode / an offscreen rendering context (software
   Vulkan or GL fallback) on a hosted runner. That is a spike-shaped bet with
   a clean fallback — the ADR-0016 go/no-go pattern applies verbatim.
5. **Software-rendered evidence ≠ the target renderer.** CI capture will not
   be the Mobile/D3D12 pipeline (ADR-0006). For legibility/look judgments at
   ADR-0020's fidelity ceiling that is acceptable; final art sign-off stays
   with the consolidated human pass on real hardware.

## Decision

Four standing rules:

**1. Harness-first decomposition is mandatory and retroactive.** Every open
and future `hitl` issue is decomposed — at filing, or at first pickup for the
existing backlog — into (a) state-checkable criteria, which move to `afk`
harness-scenario issues that close on merge (ADR-0015/0016), and (b) an
irreducible-feel residue, which folds into #173 (ADR-0021). A `hitl` issue
may no longer hold harness-checkable criteria hostage to human scheduling.
The #83–#86 → #114 traceability convention applies both directions (sources
named in the new issues, destinations named on the old).

**2. Rendered-evidence capture becomes the fourth verification surface,
gated on a spike.** A CI job captures deterministic screenshots/clips of the
scenarios a visual `hitl` issue names (movie-maker mode or offscreen render;
spike proves GO/NO-GO on hosted runners, exactly the ADR-0016 bet shape).
Visual/audio-mix `hitl` issues convert from "live editor session" to **async
artifact review**: the evidence is attached to the issue, and the human's
judgment — whenever they choose to give it — closes the same criteria a live
session would have. Gate 4 intact: a human still judges; only the medium and
timing move. **No-go fallback:** the affected items simply stay in #173 as
they are today — the bet risks only the spike's effort.

**3. Default-with-veto for bounded decision gates.** A human decision that
blocks AFK work, where a defensible default exists — an asset pick bounded to
CC0-only licensing (removing the legal risk that made it human-only), a
design call with a clear ADR-0014-citable recommendation — gets a **decision
brief** posted on the issue (recommendation, tier citations, rejected
alternatives, revert path), and AFK work proceeds on the default immediately.
The human may veto asynchronously at any time; the default is a per-PR revert
away (ADR-0015's existing guarantee). **Not applicable** to identity/
anti-goal changes, ADR contradictions, or irreversible calls — ADR-0014's
escalation triggers still hard-block, unchanged.

**4. Legibility floors in the harness.** Where a feel criterion has a
measurable core — telegraph startup ≥ a human-reaction floor in ticks, a
distinct animation state actually entered on the remote peer, an audio cue
event fired on the same logical tick on both instances — the harness asserts
that floor as a regression net while the feel judgment stays deferred.
Floors are **necessary, never sufficient**: they never close a feel item
(that would be gate-4 violation by proxy); they keep deferred-feel work from
silently regressing while nobody is looking.

### Application to the current backlog

| Issue | Restructure |
|---|---|
| #112 (M11 verify) | Decompose: drain/regen curve, reconcile-under-latency, both-clients-see-fatigue → dual-instance harness scenarios (`afk`, filed when M11 activates); feel sign-off ("gassed when it matters") → #173. |
| #129 (M12 verify) | Decompose: menu→match flow, check-ball, HUD values, shot-clock turnover, rematch reset, disconnect→forfeit → harness scenarios (`afk`); residue (grace-window resume best-effort, look) → #173. |
| #162 (audio verify) | Sync half (same cue, same logical moment, no rollback ghosts, whistle transitions) → dual-instance harness scenarios (`afk`); mix sign-off → evidence review (captured audio) or #173. |
| #153, #178, #184/#185 feel halves | Convert to async artifact review once the capture spike is GO; structural parts of #178 (retarget track counts, independent height/wingspan scaling, collider match) are harness-checkable now and split out `afk`. |
| #156 | Already decoupled by #244 (synthesized placeholders, `afk`); remaining human work is a pure file-swap + the standing import-dialog HITL exclusion. No further action. |
| #170 | Default-with-veto: bound sourcing to CC0-only per ADR-0020's fidelity ceiling, post the decision brief, proceed. |
| #206 | Default-with-veto: post the ranked decision brief (the campaign doc already contains the menu), recommend the top ADR-0014-citable option, proceed on it. |
| #114 / #173 | Unchanged in kind (ADR-0021): still the single deferred consolidated pass — but each constituent gains pre-captured evidence and a scripted replay so the eventual pass is check-off, not setup. |

## Alternatives considered

1. **Auto-accept feel from harness/evidence green.** Rejected outright — the
   same gate-4 violation ADR-0021 already rejected. Evidence review still
   ends in a human judgment; floors never close feel items.
2. **Keep dual-instance verifies whole until the human is available.**
   Rejected: it contradicts ADR-0016's own scope rule (state-checkable ⇒
   harness-closeable) and leaves finished, provable work reading as blocked —
   the exact dual-label deadlock smell ADR-0013 killed, one level up.
3. **A GPU/self-hosted runner for target-renderer-faithful capture.**
   Rejected for now, same reasoning as ADR-0016 alternative 3: heavier than
   the judgment needs. ADR-0020 caps fidelity at 2K14-old-gen; software
   rendering is adequate evidence for legibility and look-direction calls.
   Revisit only if the spike fails for softer reasons.
4. **Drop the veto — agent decides bounded gates outright.** Rejected: for
   taste/licensing that crosses deferral into acceptance. The veto preserves
   the human's authority at near-zero attention cost and keeps the brief on
   the record.
5. **Amend ADR-0016/0021 in place instead of a new number.** Rejected: a new
   verification surface (rendered evidence) plus a new decision protocol
   (default-with-veto) is a new axis, not a retune — the ADR-0012 "new
   boundary gets a new number" precedent. This ADR *extends* 0013/0015/0016/
   0021 and retracts none of them.

## Consequences

**Easier:**
- No open lane of work blocks on a live human session. The human's remaining
  duties are: async artifact review (minutes, any device, any time), async
  vetoes (optional), and the one consolidated #173 pass whenever they judge
  the game sufficiently built (ADR-0021, unchanged).
- The M11/M12 activation path (ADR-0017) stays fully walkable: their verify
  issues decompose into harness scenarios the autopilot can prove itself.
- #173's eventual cost shrinks instead of compounding — evidence accumulates
  next to each constituent as the work lands.

**Harder / accepted tradeoffs:**
- **New machinery + spike risk** for the capture pipeline. Contained: the
  no-go fallback is the status quo (items wait in #173), so only spike effort
  is at risk.
- **Software-rendered captures can diverge from the D3D12 target renderer**
  (ADR-0006). Accepted for legibility/direction judgments; final art
  sign-off remains on target hardware in the consolidated pass.
- **A vetoed default can run in the codebase for a while before the veto
  lands.** Accepted: bounded by CC0-only licensing constraints, the
  on-the-record brief, and per-PR revertability.
- **More issues** (decomposition multiplies tracker entries). Accepted: the
  tracker becomes honest about what is provable now vs. genuinely waiting on
  a human — the same trade ADR-0013 made.

**Follow-up issues to file on acceptance** (each `afk` unless noted):
capture-pipeline spike (GO/NO-GO, ADR-0016 pattern); harness decomposition
of #112, #129, #162-sync (filed when their milestones activate, per
ADR-0017); structural-assertion split of #178; decision briefs on #170 and
#206; legibility-floor scenarios for the shipped M9/M10 move set.

**Documentation updated in this commit** (Decision Discipline): CLAUDE.md ADR
table + §3 issue-tracker rules; EDITOR_TASKS.md preamble.
