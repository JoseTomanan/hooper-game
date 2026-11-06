# #442 — Normal-shot attachment evidence

The production ball now gathers from its actual incoming center and launches from the deterministic raised endpoint. These artifacts measure attachment, release and state continuity. Appearance and feel remain unaccepted in #173.

- [Method, measurements, limits and reproduction](../../0442-shot-attachment-proof.md)
- [Whole-frame gameplay, front and side comparisons](rendered-complete/README.md): 30 normal-speed before/after films, six-milestone sheets and separate anatomical overlays. The original frame, camera, source-clock and SHA256 index accompanies them.
- [Raw archive index](raw-index.json): original-byte gzip traces/manifests, geometry, calibration reference, mutations and rejected attempts. Every entry records original and archive SHA256 hashes. `null` metadata is unknown, never inferred.
- [Independent review](review.md)

The objective matrix covers 36 local cases, 36 owning-client cases and 36 separate-observer cases: both entering hands, Held/stationary-dribble/moving-dribble entries, normal/early/late episodes and continuous offsets 0/7. Headless records establish state and final-pose measurements; they do not establish rendered appearance. Network producers are deliberately paced; projected 60 Hz film playback is not a performance or latency measurement.

The raw `final-owner-report` includes its original failed numerical report. Exact duplicate-ball equality rejected 47 submicrometre transform-rounding edges in 16 observers; the largest edge was 0.00000015 m. Reanalysis uses the independently bounded 0.000001 m numerical allowance, retaining rejection of material movement, missing ticks and reset windows. The original result remains recorded rather than rewritten. Earlier `diagnostic-*` attempts remain rejected for the reasons in the analysis document. Mutation reports deliberately fail their target detectors and are not positive behavior proof.

The unfinished first packaging attempt is preserved locally under `.godot/442-interrupted-rendered`; it is superseded by the complete package linked above. Source images were not edited, and failed attempts were not relabeled as passing captures.

Implementation is isolated on `fix/442-shot-attachment`. Issue #442 requires PR #441 to merge before this work receives its own main-targeting PR. #441 remains untouched by this task; #442 stays open until its required rebase, final CI and integration gates pass. No editor operation is required for the automated work.
