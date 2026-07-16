# ADR-0020 — Performance & asset target: low-to-mid-spec devices (NBA 2K14 old-gen class)

- **Status:** Accepted
- **Date:** 2026-07-16
- **Superseded-by:** —

---

## Context

ADR-0001 already names "low-spec hardware" as a hard requirement in the abstract,
and ADR-0006 picked the Godot **Mobile** renderer specifically because it "far
lighter than Forward+ — satisfying ADR-0001's low-spec requirement in practice"
while giving up the absolute-lowest pre-Vulkan/pre-D3D12 floor that only
Compatibility would reach. What neither ADR ever pinned down is a **concrete
fidelity ceiling** — "low-spec" was a direction, not a number. Every asset,
poly-budget, and shader decision since (rig sourcing for #170, texture
resolution, transparency technique) has therefore had no bright line to check
itself against, and each has had to reason about hardware fitness from first
principles instead of a shared reference point.

On 2026-07-16 the human, in-session, supplied the missing number as an
**external commitment already fixed outside this project**, verbatim: "Low- to
mid-spec devices (think NBA 2K14 old gen)." This is not a design preference
being made now — it is a pre-existing constraint (whatever hardware the human
is actually targeting for distribution) being disclosed and recorded so the
autopilot can stop guessing. Per CLAUDE.md's Decision Discipline, an external
commitment that amends a locked ADR (here, sharpening ADR-0006's soft "low-spec"
direction into a hard ceiling) must be written down as its own ADR rather than
silently absorbed into asset-sourcing choices.

### Forces at play

1. **ADR-0006 already committed to the *renderer* half of this**, but not the
   *asset/content* half. Mobile-renderer-capable hardware spans a huge range
   (from a mid-range 2018 laptop iGPU up to a current flagship dGPU); without an
   asset-fidelity ceiling, "runs on Mobile renderer" doesn't tell an agent
   whether a given poly count, texture size, or shader is actually in-budget.
2. **NBA 2K14 old-gen (Xbox 360 / PS3 era) is a legible, checkable reference
   point.** It has publicly documented budgets (character models in the low
   tens of thousands of polys, texture atlases in the hundreds-of-KB-to-low-MB
   range, baked lighting, no dynamic global illumination) that an agent can
   target without needing to profile the human's actual hardware.
3. **Solo AI-driven dev with no dedicated technical artist** (CLAUDE.md §1) means
   asset-sourcing decisions (e.g. #170's realistic player rig) get made by an
   agent picking from public asset packs. A fidelity ceiling changes which packs
   are even candidates — e.g. it rules out scan-based photogrammetry rigs
   (Mixamo-class, current-gen poly/texture budgets) in favor of stylized
   low-poly packs (Quaternius-class, CC0).
4. **This does not reopen a closed milestone.** M15 (Mobile, performance &
   release readiness) was closed `wontfix` on 2026-07-04 — release-readiness
   engineering (build size, load times, platform certification) is explicitly
   off the roadmap. This ADR is a *standing content/perf constraint* on ongoing
   work (M8b asset realism, M9/M10 features), not a reopening of that milestone
   or a resurrection of its acceptance criteria.
5. **The design identity already prizes legibility over fidelity** (CLAUDE.md
   §1: "Legibility is a competitive requirement, not an aesthetic … committed
   moves must engage the whole body … Bounded — primary anti-goal: arcade
   decoupling"). A low-poly, low-texture-budget target is not in tension with
   that identity; readable silhouettes and telegraphed animation don't need
   current-gen fidelity to read clearly — arguably they read *more* clearly with
   less visual noise competing for the eye.

### Alternatives considered

1. **Current-gen fidelity target (PBR materials, high-poly rigs, dynamic GI).**
   Rejected: contradicts the human's disclosed external hardware commitment
   outright; also fights ADR-0006's renderer choice (Mobile has a real fidelity
   ceiling before Forward+-class features become available at all) and adds
   asset cost with no payoff against the design identity's legibility-first
   priority.
2. **Leave "low-spec" as an unpinned direction (status quo).** Rejected: it is
   exactly the ambiguity the human's commitment was meant to resolve — every
   asset call would keep re-litigating "how low is low" from scratch, wasting
   time the human explicitly wants to stop wasting ("no time is wasted").
3. **Pin to a *newer* console generation (e.g. PS4/Xbox One base) as the
   ceiling.** Rejected: the human's stated reference is specifically old-gen
   (360/PS3-era) 2K14, not a later 2K entry; picking a newer generation would
   silently raise the bar past what was actually disclosed.

## Decision

**All asset, visual, and performance choices target low-to-mid-spec hardware,
calibrated to *NBA 2K14* on old-gen consoles (Xbox 360 / PS3 era) as the
fidelity ceiling.** This is a human external commitment made 2026-07-16 and is
now a standing constraint on all ongoing and future work, not a one-time
decision for a single asset.

Concretely:

- **Poly budgets stay low.** Character/prop poly counts target old-gen-console
  norms (low tens of thousands of triangles for a rigged player character), not
  current-gen or scan-based counts.
- **Prefer alpha-scissor/cutout over alpha-blend transparency.** Cutout
  (`ALPHA_SCISSOR` / `alpha_hash`-class techniques) is materially cheaper on the
  Mobile renderer (ADR-0006) than sorted alpha-blend — old-gen engines leaned on
  cutout for exactly this reason (foliage, nets, fences), and #153's net/fence
  visuals are the concrete case this ceiling governs.
- **Low-resolution textures are acceptable and expected**, not a placeholder
  embarrassment — texture budgets target old-gen atlas sizes (hundreds of KB to
  low single-digit MB per material set), not current-gen 4K PBR sets.
- **Asset sourcing favors low-poly packs over scan-based ones.** For #170's
  realistic player rig specifically: prefer CC0 stylized/low-poly packs (e.g.
  Quaternius-class) over scan-based photogrammetry rigs (Mixamo-class), which
  assume a current-gen poly/texture budget this ADR rules out.
- **Aligns with, rather than fights, ADR-0006.** The Mobile renderer's fidelity
  ceiling (no Forward+-exclusive features) was already the right ceiling for
  this target; this ADR pins the *content* side of the budget to match the
  *renderer* side that was already decided.
- **Does NOT reopen M15.** M15 ("Mobile, performance & release readiness") is
  closed `wontfix` (2026-07-04) and stays closed — this ADR is a constraint on
  ongoing asset/perf work, not a release-readiness milestone or a resurrection
  of M15's acceptance criteria.

## Consequences

**Easier:**
- Asset-sourcing and shader decisions have a concrete, checkable reference
  point ("would this run acceptably on old-gen console-class hardware?")
  instead of an ungrounded "low-spec" direction — this directly serves the
  human's stated goal of not wasting time relitigating fidelity per-asset.
- #170's rig search narrows immediately to low-poly CC0 packs, removing a
  whole class of scan-based candidates from consideration.
- Consistent with ADR-0006's renderer choice rather than in tension with it —
  no future asset work will need to "discover" that a current-gen-fidelity
  asset doesn't fit the Mobile renderer's budget after the fact.

**Harder / accepted tradeoffs:**
- **Visual fidelity is capped below what the Mobile renderer could technically
  support.** Accepted: the human's external commitment is the hard constraint
  here, not the renderer's technical ceiling — we deliberately build to the
  lower of the two.
- **Existing or in-flight assets sourced without this ceiling in mind may need
  revisiting.** Accepted as ordinary follow-on work, evaluated case-by-case
  (e.g. #170 is still blocked on a human asset-license pick; this ADR narrows
  but does not resolve that pick).
- **Reversible.** This is a content/perf budget, not an architecture choice —
  if the human's external hardware commitment changes, update this ADR's
  ceiling (new Status/Superseded-by or an amendment) with no code-architecture
  impact; ADR-0006's renderer choice is unaffected either way.
