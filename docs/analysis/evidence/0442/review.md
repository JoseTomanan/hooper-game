# #442 independent correctness review

Review scope: changes from frozen #441 head `c5865e4` through `f2633d6`, followed by the corrective diffs `ad704b0` and `7de642e`. Two fresh-context reviewers independently applied the repository's code-review and doubt-driven contracts. Neither edited the artifact during review. No review was posted externally.

| Pass | Finding | Resolution and discriminating evidence |
|---|---|---|
| Authority, prediction and release | A pre-Begin Dribbling snapshot at the owner's actual first Active0 bypassed the gather path and released from the bounce station. | `ad704b0` includes Active in the valid predicted re-cradle path. A new production-callback lifecycle case first fails at the incorrect origin, then proves the model origin, two real arc steps and no repeated release after a later correction. Connected-owner fixture: 190 assertions pass. |
| Presentation and proof | Exact equality rejected float32 roundoff on duplicate historical ball frames. | `7de642e` bounds duplicate displacement at 0.000001 m. Regression first fails under exact equality, then accepts 0.5 micrometres and rejects 2 micrometres. Existing 1 mm mutation remains rejecting. The unchanged 72 owner/observer captures pass reanalysis. |
| Independent corrective-diff review | No high-confidence introduced defects found. | Reviewed both production fixes, actual execution paths and the assertions; checked RED/GREEN records rather than accepting a reported green alone. |

The broader review covered deterministic gather inputs, matching Begin tokens, holder invalidation, reconciliation, release sequencing, retained HandSide, coherent observer targets, anatomical calibration, actual arc-step telemetry, executable catalog registration, packager clocks and existing positive/negative form gates. This is a correctness review, not acceptance of visual quality, feel, balance or network performance. Main-targeting publication and exhaustive GitHub CI remain gated by the issue's explicit #441 dependency.
