# ADR-0006 — Renderer: Godot Mobile (not Compatibility, not Forward+)

- **Status:** Accepted
- **Date:** 2026-06-14
- **Superseded-by:** —

---

## Context

When initializing the Godot 4.6 project we had to pick one of Godot's three
rendering methods. The choice sets a target-hardware contract: it determines
which GPU features shaders and materials may assume, and switching later is not
free (materials authored against one method can render differently under
another).

- **Forward+** — Vulkan/D3D12 clustered renderer. Highest feature ceiling
  (SDFGI, volumetric fog, full post). Heaviest runtime; modern dGPUs only.
- **Mobile** — Vulkan/D3D12, mobile/desktop-tuned. Mid feature set; substantially
  lighter than Forward+. Requires a Vulkan- or D3D12-capable GPU.
- **Compatibility** — OpenGL 3.3 / GLES3 / WebGL2. Lightest; broadest reach
  (old integrated GPUs, browser export). Lowest feature ceiling.

ADR-0001 makes **low-spec hardware** a hard requirement and notes Windows-first
with cross-platform (incl. web) kept *possible but not committed*. On that basis
the initial recommendation was **Compatibility**, as the absolute lightest option
with the widest reach.

The developer chose **Mobile** intentionally instead.

## Decision

Use the **Mobile** rendering method. On Windows it runs over the **D3D12** driver
(`rendering_device/driver.windows="d3d12"`).

Mobile is far lighter than Forward+ — satisfying ADR-0001's low-spec requirement
in practice — while retaining better lighting/material fidelity than
Compatibility. The competitive-legibility art direction
(ADR-0003) does not need any Forward+-exclusive feature, so Forward+ is rejected
outright. Compatibility is rejected as the daily renderer because Mobile's
fidelity headroom is judged worth the cost below.

Target hardware is therefore **Vulkan/D3D12-capable Windows machines**, not the
absolute oldest pre-Vulkan integrated GPUs that only Compatibility would reach.

## Consequences

**Easier:**
- Better default lighting/material fidelity than Compatibility, while staying
  lightweight enough for the low-spec target (no Forward+ weight).
- Single, modern GPU-API path (D3D12) on the Windows-first target.

**Harder / given up:**
- **Drops the lowest-spec, pre-Vulkan/pre-D3D12 hardware** that Compatibility
  would have run on. Accepted: the target is modest-but-not-ancient machines.
- **Closes the WebGL2 / browser export path**, which only Compatibility supports.
  Accepted: web was never a committed pillar (ADR-0001 lists it as "possible
  later," not promised). Revisit this ADR if browser/Steam-Deck-class reach
  becomes a goal.
- Renderer switches are not free; this is now a soft commitment. If the
  low-spec/reach constraints tighten, reopen this decision and re-evaluate
  Compatibility, updating Status/Superseded-by here.
