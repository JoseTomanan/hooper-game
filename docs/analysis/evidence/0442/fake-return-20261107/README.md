# Fake-return regression and repair evidence

**Issue:** #442. **Date:** 2026-11-07. **Purpose:** retain the failed network
form comparison, the focused scalar-decay probe and its complete eight-case
network rerun. These are headless state/final-pose measurements, not films or
human appearance/feel acceptance (#173).

[index.json](index.json) records each source path, original byte count/SHA256
and gzip byte count/SHA256. Archives preserve original bytes, including capture
labels and line endings; gzip uses level 9, zero modification time and no
filename header. Decompress each archive to recover its source.

## What failed and what changed

The RED report contains eight cases; only `left-late-1` fails. Its
Recovery-to-Inactive comparison includes the adjacent Recovery18-to-19 edge.
The actual Recovery19-to-Inactive edge already matches the legacy measurement
exactly. The repair changes only `ShotHandPose` presentation: when a fake returns
to retained Left, the departing right-hand binding uses squared remaining
weight. Its captured and neutral endpoints stay fixed. Simulation targets,
physical ball trajectory, committed-move timing, anchors and comparison
thresholds retain their implementations.

| Maximum body-space joint-origin step | RED current | RED legacy | Focused GREEN current | Focused GREEN legacy |
|---|---:|---:|---:|---:|
| Recovery18 to 19 | 0.138319981 m | 0.137954367 m | 0.108353277 m | 0.108959817 m |
| Full Recovery peak | 0.476763437 m | 0.470762615 m | 0.481523443 m | 0.479568586 m |

The existing comparator and its coordinate-precision allowance are unchanged;
no bound was widened. Each probe has a fresh matched legacy measurement under
the same modifier stack. The earlier Recovery peak increases slightly after the
repair and remains above the fresh legacy peak. It is disclosed here and in the
all-edge summary; no new peak threshold or whole-window motion improvement is
claimed. Matched authority physical-ball deltas are identical between the
legacy/current asset groups, and the scalar probe leaves the physical model
unchanged. [The pre-probe audit](red/fake-recovery-adjacent-audit.json.gz)
retains every bone's body/world positions and adjacent steps for all four fake
cases, with authority ball deltas joined by selected server tick.

## Archived proof

- [RED report](red/proof.json.gz), [its baseline](red/baseline.json.gz) and
  [failed process log](red/net-jumpshot-form-current.log.gz): all eight case
  verdicts, plus complete left-late before/after authority and observer raw
  episodes under `red/before/` and `red/after/`.
- [Focused GREEN report](green/proof.json.gz),
  [baseline](green/baseline.json.gz) and
  [complete Recovery-edge summary](green/probe-recovery-summary.json.gz):
  complete left-late authority/observer raw episodes under `green/before/` and
  `green/after/`, with both probe build logs.
- [Full GREEN network report](green-full/proof.json.gz),
  [baseline](green-full/baseline.json.gz) and
  [completed process log](green-full/c2-net-all-proof.log.gz): all eight
  legacy/current network cases pass. `green-full/before/` and
  `green-full/after/` retain all 32 complete authority/observer raw episodes,
  each with its manifest and trace. The
  [mismatched-boundary control](green-full/mismatched-boundary-control.json.gz)
  fails its intended Startup-to-Active displacement detector.

## Corrected-origin and local engine gates

[All eight block scenarios pass](engine-gates/block-all8-green.log.gz),
including the two reciprocal reach discriminators. The
[mutation ledger](engine-gates/corrected-origin-mutation-ledger.json.gz)
retains both builds and all four executed mutation/restoration cases; every
referenced stdout/native log and the
[original source snapshot](engine-gates/BallController-before-origin-mutation.cs.bytes.gz)
are archived alongside it. The
[original runner](engine-gates/run-corrected-origin-mutation.py.gz) records the
single-call mutation and exact-byte restoration.

| Production source | Corrected-origin reach-hit | Corrected-origin reach-miss |
|---|---|---|
| Omit only `TickHeld`'s `ApplyShotGather(holderBody)` call | Intended FAIL: old origin is outside reach, so the expected block never occurs | Intended FAIL: old origin is inside reach, so an unwanted block occurs |
| Restore the exact source bytes | PASS: corrected origin is inside reach and the block occurs | PASS: corrected origin is outside reach and the shot remains InFlight |

Both mutation cases exit 1; both restored cases exit 0. The restored source SHA256
is `8be5e8d51c338af0c8c72265a2dfcd2317ac7288e4d3ef32c5af19505c37d26f`,
identical to the archived pre-mutation bytes. This is a change to the actual
production release path for the negative control, rather than a changed
assertion or a second synthetic ball origin.

The [parent's independent recomputation](engine-gates/parent-net-form-verification.json.gz)
also passes all eight network form cases. The
[CI-budget contract log](engine-gates/ci-budget-catalog-tests.log.gz) records all
26 catalog repository tests passing.

The final local attachment rerun passes all 36 cases:
[summary](engine-gates/local36/summary.json.gz),
[per-case report](engine-gates/local36/local-proof.json.gz) and
[completed process log](engine-gates/c2-local36-proof.log.gz). This bounded
package archives those reports rather than duplicating the local raw matrix;
its original raw source path remains in the index. Final shared-role raw
evidence must be supplied by a separate accepted CI matrix.

## Source provenance and limits

The RED manifests retain commit label `2bb0c2090157c6396d0a8e4f1bad60c4d888e6d2`.
The focused GREEN manifests explicitly retain
`c2-left-fake-uncommitted-probe`: that probe compiled a dirty working tree based
on `2bb0c20`. The full GREEN rerun's manifests also retain the caller-supplied
`2bb0c20` label, despite using the C2 semantic repair. These labels are not
retagged and do not establish a clean executable exactly at their named commit.

The semantic repair was subsequently committed as
`ba324a6a4622b1fae34879d44ee176f259cb00d7`; its
[source patch](source/ba324a6-ShotHandPose.patch.gz) is archived from `git show`.
Final comment wording was refined after the probe build, without changing that
runtime expression. The following `6dd8e4d` commit changes only the CI job budget.
The source patch, raw capture hashes and build logs describe this provenance;
none is represented as an exact clean-commit executable attestation.

This package establishes the stated RED/GREEN form comparison, local 36-case
rerun and block-origin mutation outcomes while preserving the motion tradeoff.
It does not assert an exhaustive CI verdict, a final 108-role attachment matrix
or feel acceptance. Those delivery gates require their own final evidence.
