# #442 — Normal-shot gather, palm contact and release

- **Issue:** #442; related #440, #418, #428, #173.
- **Architecture:** ADR-0002/0003/0004/0009/0010/0012/0027, unchanged.
- **Date:** 2026-11-06; current-main integration checked 2026-11-07.
- **Purpose:** record objective attachment and release evidence. The human owns appearance and feel acceptance in #173. This record does not accept either.

## Method

The frozen pickup is `c5865e42f1c41fd3e3197d08c0719b720d9d337a`, the existing #441 head. A detached checkout retained its compiled assembly and imported production assets before any #442 code was built. `Main.tscn`, real input, the production-scaled Y Bot and the actual ball mesh supply the before recordings. The defect predates #440.

The repaired path is `BeginCommittedMove → CaptureShotGather → CradleForShotStartup → TickHeld → ShotGather.Position → CheckJumpShotRelease → ApplyShootLocally → ShotArc`. The ball model receives simulation root, heading, entering hand, integer phase/frame and anchors belonging to that accepted move. It reads no skeleton, animation state, render delta or random source. The actual pre-cradle ball center survives the first gather frame. A fake captures the reached simulation center and returns to the ordinary Held station over the existing short Recovery.

The fixed-frame target profile was authored **offline** from the frozen normal-shot rig. `export_shot_hand_geometry.gd` exports raw mesh vertices, skin influences, bind transforms and normals. `generate_shot_gather_profile.py` chooses an actual pure-wrist-skinned palmar vertex using anatomy alone: nearest to wrist-local `(0, .065, .03)` with palmar normal Z > .8. It never selects a point using the ball. Right vertex **11216** and left vertex **10324** belong to `Skeleton3D/Alpha_Surface`; each has one hand-bone influence of weight 1. The committed calibration contains their raw rest coordinates, normal, bind-space conversion, source hashes and frozen reference wrist transforms. With weight 1, final wrist transform × calibrated vertex is the actual final skinned vertex; it is not a projected sphere contact point.

`ShotHandPose` runs after the body/entry/fake modifiers. It consumes the displayed ball target through the existing limb-preserving solver and writes bone rotations only. The right palm controls settled normal-shot contact for both retained HandSides. The simulation never consumes this result. Remote targets come from the same selected server envelope as the player pose, with matching shooter identity at release. Local ball presentation uses the independently measured holder mesh correction.

The capture snapshots bones at `SkeletonUpdated`, after all modifiers, and rendered images at `FramePostDraw`. These engine ordering choices follow the official [SkeletonModifier3D](https://docs.godotengine.org/en/4.7/classes/class_skeletonmodifier3d.html) and [Skeleton3D signal](https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-signal-skeleton-updated) contracts. Raw geometry follows [Mesh](https://docs.godotengine.org/en/4.7/classes/class_mesh.html) and [Skin](https://docs.godotengine.org/en/4.7/classes/class_skin.html). Separate front/side overlays project the actual skin vertex through each recorded [Camera3D](https://docs.godotengine.org/en/4.7/classes/class_camera3d.html#class-camera3d-method-unproject-position) transform; source images and metric inputs are unchanged.

`analyze_shot_attachment.py` rejects missing source ticks, changed held duplicates, mismatched player/ball pose clocks and reset-spanning windows. It checks every normal Startup8–17 pose and Active0, not a selected middle frame. It separately checks the independently evaluated physical model, holder correction, actual launch latch and first two real `ShotArc.Step` calls. An owning Held correction is reported as a correction stall; it cannot masquerade as a flight step. Authority and owning-client clocks remain independent. Actual fake acceptance must be recorded separately on both clocks inside Startup `[3,12)`.

Duplicate-ball comparisons permit at most **0.000001 m** of numerical displacement, independently of the 2 cm anatomical contact allowance. Repeated assignment of a historical world position through a moving float32 parent can round differently on reconstruction. The original owner report rejected 47 edges in 16 observer cases; the largest change was **0.00000015 m**. Numerical-only reanalysis passed those 72 role captures, but its owner fake tails were subsequently rejected by the stricter handoff detector described below. An operator regression first fails with exact equality, then accepts a 0.5 micrometre perturbation and rejects 2 micrometres; the existing 1 mm changed-duplicate mutation still fails. The original failed reports remain intact.

The role matrix is 36 local cases plus 36 predicted-owner and 36 separate-observer cases: two hands × three entries (Held, stationary dribble, moving dribble) × normal/early/late × offsets 0/7. Supplemental authority traces establish event outcomes. The executable catalog partitions owner offsets for scheduling while retaining all 108 role reports. All new reports and raw traces upload on green CI too.

## Preconditions and bounds

| Input or gate | Value |
|---|---:|
| Godot | 4.7.1 stable Mono, official `a13da4feb` |
| Fixed simulation step | 1/60 s |
| Actual sphere radius | 0.12 m |
| Settled palm/sphere surface tolerance | 0.02 m |
| Numerical model/integrator/segment tolerance | 0.0001 m |
| Duplicate held-ball transform tolerance | 0.000001 m |
| Normal Startup/Active/Recovery | 18 / 4 / 20 ticks |
| Legal fake window / remaining Recovery | `[3,12)` / 8 ticks |
| Contact window | Startup8–17 and first Active0 |
| Continuity neighborhood | complete shot plus eight neutral source ticks each side |

The calibration numbers are invalid for a changed rig or authored reference without regeneration. The analyzer verifies the Y Bot SHA256. Rig dimensions and segment lengths are measured from actual final poses against the incoming neutral rig. Unreachable targets must remain clamped rather than stretching a limb. Raw wrist-to-ball distances remain in every contact report.

The rendered package also preserves the exact calibration JSON as `calibration.json.gz`. Its uncompressed SHA256 is the index's original-source hash; its gzip hash is recorded separately. This avoids confusing Git's CRLF-to-LF text normalization with a changed calibration. The archived JSON and committed asset have identical parsed data.

Network captures use deliberate producer pacing to observe every source tick. Ordinary owner/server delay is 80 ms per simulated tick; the observer uses 16 ms. For owner fake episodes, authority's **Startup producer only** uses 160 ms so the last-legal predicted request reaches a legal authority window. Actual accepted frames are recorded and gated. Simulation dt, move gates and game networking remain unchanged. These recordings establish no latency, throughput or real-time performance result.

## Measurements

Frozen local baseline, normal offset 0, independently calibrated right palm:

| Entry / entering hand | Startup8 surface error (m) | Startup17 (m) | Active0 (m) |
|---|---:|---:|---:|
| Held / Right | 0.559325 | 1.258297 | 1.309995 |
| Held / Left | 0.757152 | 1.414328 | 1.461356 |
| Dribble / Right | 0.552804 | 1.258297 | 1.309995 |
| Dribble / Left | 0.755456 | 1.414328 | 1.461356 |
| Moving / Right | 0.546474 | 1.258297 | 1.309995 |
| Moving / Left | 0.751035 | 1.414328 | 1.461355 |

The final 36-case local headless matrix passes. These are state/final-pose measurements, not rendered appearance judgments:

| Measurement | Expected | Maximum measured |
|---|---:|---:|
| Settled right palm surface error | ≤0.02 m | 0.000000516 m |
| Physical Held center vs independently evaluated model | ≤0.0001 m | 0 m |
| Actual launch origin vs first-Active model endpoint | ≤0.0001 m | 0 m |
| Last-Held → release displacement minus own-root displacement | ≤0.0001 m | 0.000000300 m |
| First two actual flight-step position error | ≤0.0001 m | 0.000000323 m |
| First two actual flight-step velocity error | ≤0.0001 m/s | 0.000000334 m/s |
| Arm-segment length change vs incoming neutral | ≤0.0001 m | 0.000000855 m |

Settled wrist-to-center distance remains approximately **0.176180 m**. Zero wrist distance would bury the sphere in the wrist and is not the contact criterion. The sub-micrometre residuals describe the chosen skin vertex and floating-point calculation; they do not establish a complete finger/grip fit.

## Lifecycle and mutations

`ShotGatherLifecycleTest` connects real ENet peers, verifies actual owner/client roles, then injects explicitly synthetic packet orderings through production snapshot/reconciliation callbacks. **250 assertions pass.** A real sphere mesh is resolved through production `_Ready`; its initial offset proves that the rendered-position accessor reads the mesh rather than the root. The fixture checks Startup, first-Active and fake-Recovery stale Dribbling re-cradles, released-origin retention through stale Held into Recovery despite root movement, fake-to-neutral mesh handoff with repeated stale Held packets and an independently displaced holder mesh, loss to Loose/another holder without timeline cancellation, matching versus old-token invalidation, no resurrection from late valid state, same-holder award/fresh dribble, matching rejection, disconnect-reset callbacks and same-envelope shooter identity. This fixture does not claim to reproduce network latency or an actual socket disconnect; the reset callback is invoked explicitly.

Independent review found that a stale pre-Begin Dribbling correction on actual first Active0 skipped the predicted cradle and released from the bounce station. The added regression reproduced launch at `(-0.4429, 0.9698463, 6.5)` instead of the independently computed endpoint `(-0.27775693, 2.1740363, 6.0062814)`. Including Active in the existing valid-gather cradle path repairs that boundary. The same case proves the next two real integrated steps and no second release or world-origin drag when stale Dribbling arrives later during Active.

A later full-return audit found a separate presentation defect in **all 24 earlier owner fake episodes**. Physical/model and holder-frame residuals were zero throughout Startup/Recovery; nevertheless, the first neutral frame exposed an independent stale ball smoothing offset, producing **0.0292–0.2106 m** of visible displacement with zero physical/root/holder-offset displacement. The corrected path shares the holder's visual correction for every local/authority Held frame, including neutral after a fake, and synchronizes the otherwise-hidden ball offset. It adds no simulation latch. Non-local observers retain their coherent historical frame; dribbling and free flight retain their existing smoothing, and first-release special handling still requires a valid gather.

The operator now checks fake physical/model and independently measured holder/visible agreement throughout Startup, Recovery and the eight neutral tail ticks. Its immediate-neutral delta must equal the physical-ball delta plus the holder-correction delta within 0.0001 m; it does not require a stationary holder or hide a real correction. A joint-only gate could miss this ball-only handoff, so separate 5 cm physical-plus-mesh and 15 cm mesh-only mutations first pass the old detector and then fail their new intended gates. The earlier owner traces and the interrupted 435-case sweep remain diagnostic evidence, not the final accepted matrix.

Both required mutations were built and captured on the production seam, then reverted and rebuilt:

| Single mutation | Required detector result | Observed |
|---|---|---|
| Omit `ApplyShotGather`, retaining the old fixed Held root | Independent physical model/release gate fails | Fails Held model, launch endpoint and anatomical contact |
| Disable only `ShotHandPose`, retain corrected physics | Visual palm-contact gate fails | Fails actual Active0 anatomical contact |

Operator mutations additionally reject broken arc steps, unexplained Held stalls, missing correction telemetry, missing fake acceptance, changed duplicates, reset windows, authoritative HandSide changes, common ball/hand visual shifts and stretched limbs. The common-shift mutation preserves palm contact and still fails the independent holder-correction gate.

The existing positive form/heading/root/entry/fake/return thresholds remain intact. Attachment IK repairs one old goalkeeper loaded-elbow symptom; the old asset negative control now requires its still-independent asymmetry and wrist-flexion failures. It uses the complete production modifier stack rather than disabling IK only for that control. All ten form scenarios and the broader 136-case selected shared regression suite pass.

## Reproduction and evidence

Run from the repository root with `$env:GODOT` set to the pinned console executable. Every shipped runner supplies unique workspace-local Godot logs.

```powershell
dotnet build "HOOPER GAME.csproj" -p:IncludeIntegrationHarness=false
dotnet build "HOOPER GAME.csproj" -p:IncludeIntegrationHarness=true
dotnet test tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj
python -m unittest discover -s tests/repository -p 'test_*.py'
python tools/harness_catalog.py run --id shot-attachment-local --godot $env:GODOT --run-id attachment-local
python tools/harness_catalog.py run --id shot-attachment-owner-o0 --godot $env:GODOT --run-id attachment-owner-o0
python tools/harness_catalog.py run --id shot-attachment-owner-o7 --godot $env:GODOT --run-id attachment-owner-o7
python tools/harness_catalog.py run --id shot-gather-lifecycle --godot $env:GODOT --bash "C:/Program Files/Git/bin/bash.exe" --run-id attachment-lifecycle
```

On Windows, put Git Bash ahead of the unavailable WSL `bash` alias for repository wrapper tests. The filesystem sandbox denies Windows temporary-fixture ACL/signal-pipe operations; the complete suite was also run outside that sandbox. No test thresholds were changed for those environment failures.

The [evidence index](evidence/0442/README.md) links normal-speed whole-frame gameplay/front/side comparisons, separate calibrated skin overlays, raw traces/manifests, source hashes, numerical reports and rejected diagnostics. Calibration can be regenerated from its archived raw geometry and frozen reference trace using `tools/generate_shot_gather_profile.py`; the numeric outputs are committed in `ShotGatherProfile.cs` and `assets/shot_gather_calibration.json`.

## Delivery gate

The implementation is integrated on `fix/442-shot-ball-attachment`, based on current main `eaf15f7c1116445907bda519558924ba280c70b8`. Prerequisite PR #441 and the separate #443 fadeaway repair were already merged before this pickup. Merge commit `b5fd9e5` preserves the earlier unpublished #442 implementation and corrective-proof history without republishing #440 as new work.

Fresh current-main pickup captures reproduce the defect for both hands and all three entries. Independent raw-skin calibration confirms the selected vertex belongs to the anatomical palm; the normal-shot clips are exactly unchanged from the frozen reference despite #443 changing other clips in the library. See the [pickup archive](evidence/0442/pickup-20261107/README.md).

On the integrated implementation, ordinary and harness builds pass with no warnings/errors; full xUnit passes **1,327 tests with five intentional characterization skips**. Repository contracts pass **164 tests with one skip** outside the Windows filesystem sandbox; the rejected sandbox attempt is retained. Independent current-main correctness review reports no high-confidence introduced defects.

The exhaustive local catalog was interrupted before completion; its completed cases are retained and remaining IDs are being rerun. The new main-targeting PR supplies independent exhaustive GitHub CI. Neither an interrupted run nor a pending CI result is an accepted exhaustive pass. Issue #442 remains open until its PR passes the required gates and merges; appearance and feel remain in #173.

## Rejected attempts and verification limits

Earlier captures remain diagnostics. A pre-final owner attempt had a locally accepted late fake while authority continued to Active and released; it is not selected as zero-release evidence. It motivated explicit acceptance telemetry and producer scheduling. A local capture native-crashed during managed/native finalization after writing its complete manifest; that process result remains failed. Two interrupted batches used the earlier self-confirming ball-offset telemetry and are not accepted shared-correction proof. The fresh final batches use the independent model and holder measurements.

Shared JumpShot Startup plumbing also applies to an unfinished-turn fadeaway until classification; this keeps a single simulation path rather than branching physical attachment on cosmetic classification. Fadeaway's authored relative motion, separate Turnaround/Layup release families, arc solver, accuracy factors and server-only random draws retain their existing implementations. Shared gameplay/animation regressions cover the affected paths. #439 remains separate.

## Human review, without a tuning prescription

No editor operation is required for the implementation or automated proof. The films and skin overlays are review artifacts; they do not accept visual quality, physical feel, grip appearance, camera taste or balance. Those judgments remain with the human in #173. No new tuning recommendation or ADR change is made here.
