# ADR-0015 — Autonomous merge for the AFK lane; "Done means proven" becomes proven-by-harness

- **Status:** Accepted
- **Date:** 2026-06-29
- **Superseded-by:** —

---

## Context

CLAUDE.md §3 has two human-gated rules that, together, make a human the
throughput bottleneck for *every* unit of work:

> **"The human owns merges**, as they own commits: open the PR with `gh`, but let
> the human review and merge."

> **"Done means proven, not written.** A `hitl` issue is only closed after the
> human confirms it in the editor."

Those rules were correct while the human was the only verification surface: if a
person must run the game to know it works, then a person must also be the one to
decide a PR is safe to merge. The two gates were really one gate wearing two
hats.

The human has now chosen **maximum autonomy**: an automated pipeline should take
an `afk` issue from dispatch to merged-on-`main` with no human in the critical
path, and the `hitl`-verification gate should be satisfied by an automated
*harness* rather than a person watching the editor. This is a deliberate trade of
human oversight-per-change for throughput, made with eyes open and recorded here
because it changes two locked rules (Decision Discipline, CLAUDE.md §4).

This ADR is the **governance** half of that change. The **mechanism** that makes
it safe — an actual headless Godot harness that can assert engine behaviour, not
just compile C# — is ADR-0016, and this ADR is conditional on it: autonomous
merge is only as trustworthy as the green signal it merges on.

### Forces at play

1. **The gate was a proxy, not the goal.** "Human merges" never protected
   anything by itself; it protected `main` from un-verified change. If a harness
   can verify, the *proxy* (a human clicking merge) can be replaced without
   weakening the *goal* (a releasable `main`).
2. **The signal is already strong and getting stronger.** CI already builds the
   game project on its own compile surface (catching game-only errors the test
   project masks) and runs ~250 deterministic unit tests. ADR-0016 adds headless
   scene verification on top. Merging on that combined green is a defensible bar.
3. **The residual risk is *feel*, not *correctness*.** A harness asserts state
   ("the ball went loose," "the score incremented," "the remote phase rendered"),
   not feel ("does the telegraph read," "is 12° of lean grounded or floaty").
   Feel is the one thing that genuinely needs a human — and it is a *milestone*
   judgment, not a *per-PR* one. So the human checkpoint moves from per-change to
   per-milestone (see Consequences).
4. **Reversibility is cheap.** Every change still lands as a reviewable PR with
   focused commits (merge-don't-squash, ADR-0011 guardrails). A bad autonomous
   merge is a `git revert` of a single PR, not an unwind. The audit trail is
   unchanged; only *who/what presses merge* changes.
5. **Autonomy without a hard stop is dangerous.** An agent that can merge its own
   work can merge its own *broken* work if the green signal is faked or skipped.
   The decision must come bundled with non-bypassable gates, not just permission.

### Alternatives considered

1. **Status quo — human owns every merge and every `hitl` close.**
   Rejected. It is precisely the bottleneck the human asked to remove, and force 1
   shows the gate was a proxy a harness can stand in for.
2. **Auto-merge the `afk` lane, but keep ALL `hitl` closes human.**
   Rejected as the *primary* model but **retained as the fallback** (see the
   go/no-go in ADR-0016). If headless verification proves impossible, only the
   `afk` lane auto-merges and `hitl` stays human — a strictly weaker but still
   useful autonomy. We adopt the stronger model *because* ADR-0016 succeeded; we
   fall back to this if it regresses.
3. **Auto-merge with a post-hoc human audit queue (merge first, human reviews a
   digest later).**
   Rejected. It keeps the human in the loop without keeping them in the *path*,
   which sounds ideal but means broken merges sit on `main` until the audit
   catches them — worse than a pre-merge harness gate that never lets red land.
4. **Require two agents to agree (proposer + independent reviewer) before merge,
   no human.**
   Partially adopted. `/code-review` on each PR is exactly this — an
   independent-context review pass — but it gates *alongside* CI, it does not
   *replace* the harness. Two agents agreeing on a wrong reading is still wrong;
   the harness is the objective tiebreaker.

## Decision

**An `afk` issue may go from dispatch to merged on `main` with no human in the
critical path, and a `hitl`-verification issue may be closed by the headless
harness (ADR-0016) instead of a human editor session — subject to non-bypassable
gates.**

Concretely:

- **Autonomous merge (AFK lane).** A worker opens a branch-per-issue PR with
  `Closes #X` in the body (unchanged). The orchestrator merges it (merge-commit,
  not squash — unchanged) **only when ALL of these are green**:
  1. CI build of `HOOPER GAME.csproj` passes.
  2. The full `dotnet test` suite passes (0 failed).
  3. The headless integration harness (ADR-0016) passes for any issue whose
     acceptance criteria are harness-checkable.
  4. `/code-review` returns no unresolved correctness findings.
- **"Done means proven" is redefined, not weakened.** Proven now means **proven
  by the harness** for everything the harness can assert. A `hitl` issue whose
  acceptance criteria are expressible as harness assertions is closed when those
  assertions pass in CI. The *bar* (proof before close) is unchanged; the
  *prover* changes from a human to the harness.
- **The irreducible human checkpoint is feel, batched per milestone.** Anything
  the harness cannot assert — feel, telegraph readability, lean degrees, "does it
  look right" — is collected into **one human feel-acceptance pass per
  milestone**, not per issue. Feel values ship as research-backed defaults with
  cited rationale (source-driven-development) and the autopilot proceeds on them;
  the human pass is a backstop against arcade-decoupling (CLAUDE.md's primary
  anti-goal) creeping in unseen, not a per-change gate.

**Non-bypassable gates (mandatory; autonomy is conditional on them):**
1. **No merge on red.** Any failing gate above blocks merge, full stop. The
   orchestrator does not merge "with known failures."
2. **No agent reports done on red.** A `Stop`/`SubagentStop` hook runs
   `dotnet build "HOOPER GAME.csproj"` + `dotnet test` and blocks an agent from
   reporting completion while either is red. This is the local mirror of gate 1.
3. **One reviewable PR per issue, merge-commit preserved.** Every autonomous
   merge is a single revertible PR with its focused commit history intact
   (ADR-0011 / merge-don't-squash). Auditability is non-negotiable.
4. **Feel is never auto-accepted as feel.** The harness may assert the *state*
   produced by a feel value (e.g. "lean is non-zero during Active phase only")
   but must not claim the value *feels* right. That claim is reserved for the
   per-milestone human pass.

## Consequences

**Easier:**
- AFK throughput stops being gated on human availability. An issue can be picked
  up, built, verified, reviewed, and merged end-to-end while the human is away —
  the literal goal of "maximum autonomy."
- The `afk`/`hitl` distinction sharpens: `hitl` now means "needs verification a
  harness *can* perform" (auto-closeable) versus the genuinely human residue
  (feel), which is batched and rare.

**Harder / accepted tradeoffs:**
- **The green signal must be trustworthy or this is actively dangerous** (force
  5). This ADR is therefore *conditional on ADR-0016*: if the harness cannot
  actually assert engine behaviour, we fall back to alternative 2 (afk-only
  auto-merge, human keeps `hitl`). The gates above exist to make a faked or
  skipped green impossible to merge on.
- **Feel regressions can land before the milestone pass catches them.** Accepted:
  they are cosmetic by construction (the harness guarantees state-correctness),
  cheaply revertible (force 4 of ADR-0011), and the per-milestone pass is the
  designed backstop. We trade "no feel regression ever reaches `main`" for
  throughput, knowingly.
- **The human loses per-change veto.** This is the point, not a side effect. The
  veto moves up to (a) the per-milestone feel pass and (b) the always-available
  `git revert`. Recorded plainly so it is a chosen trade, not a drift.
- **Documentation must be updated in the accepting commit** (Decision Discipline):
  CLAUDE.md §3's "human owns merges" and "Done means proven" paragraphs are
  amended to point here, and the at-a-glance ADR table gains this row.
- **Reversible.** If autonomous merge produces too many bad landings, revert to
  alternative 2 or to full human merge without unwinding any code — the pipeline
  is configuration, not architecture.
