# #442 — Normal-shot attachment evidence

The production ball now gathers from its actual incoming center and launches from the deterministic raised endpoint. These artifacts measure attachment, release and state continuity. Appearance and feel remain unaccepted in #173.

- [Method, measurements, limits and reproduction](../../0442-shot-attachment-proof.md)
- [Whole-frame gameplay, front and side comparisons](rendered-complete/README.md): 30 normal-speed before/after films, six-milestone sheets and separate anatomical overlays. The original frame, camera, source-clock and SHA256 index accompanies them.
- [Exact calibration JSON bytes](rendered-complete/calibration.json.gz) preserve its capture-time hash across Git's text line-ending normalization; the decompressed JSON is semantically identical to the repository asset.
- [Raw archive index](raw-index.json): original-byte gzip traces/manifests, geometry, calibration reference, mutations and rejected attempts. Every entry records original and archive SHA256 hashes. `null` metadata is unknown, never inferred.
- [Independent review](review.md)

The objective matrix covers 36 local cases, 36 owning-client cases and 36 separate-observer cases: both entering hands, Held/stationary-dribble/moving-dribble entries, normal/early/late episodes and continuous offsets 0/7. Headless records establish state and final-pose measurements; they do not establish rendered appearance. Network producers are deliberately paced; projected 60 Hz film playback is not a performance or latency measurement.

The raw `final-owner-report` includes its original failed numerical report. Exact duplicate-ball equality rejected 47 submicrometre transform-rounding edges in 16 observers; the largest edge was 0.00000015 m. A numerical-only reanalysis passed with the independently bounded 0.000001 m allowance. A subsequent stricter detector rejected all 24 owner fake tails: the first neutral tick exposed a 0.0292–0.2106 m stale smoothing offset despite unchanged physics and holder correction. Those earlier owner fake traces and their numerical-only reanalysis are **superseded diagnostics**, not accepted final return evidence. Earlier `diagnostic-*` attempts and the interrupted 435-case sweep also remain diagnostic. Source results are preserved rather than rewritten; mutation reports deliberately fail their intended detectors.

The unfinished first packaging attempt is preserved locally under `.godot/442-interrupted-rendered`; it is superseded by the complete package linked above. Source images were not edited, and failed attempts were not relabeled as passing captures.

[Fresh current-main pickup and independent calibration audit](pickup-20261107/README.md) preserve the reproduced baseline at `eaf15f7`, clip equivalence and original-byte capture hashes.

Implementation is integrated on `fix/442-shot-ball-attachment` at `b5fd9e5`, after prerequisite #441 and separate #443 merged. Fresh builds, unit/repository suites and independent review pass. The interrupted local catalog is incomplete; its remaining IDs and exhaustive PR CI are pending. Earlier rejected owner fake traces remain diagnostics until fresh captures pass the stricter return detector. No editor operation is required for the automated work.
