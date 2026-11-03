# #440 jump-shot evidence

[Proof record](../../0440-jumpshot-transition-proof.md) explains the reproduced
defects, repair, objective results, references and remaining ball/hand gap.
Appearance and smoothness remain for the human pass in #173.

| Review surface | Inventory |
|---|---|
| [Gameplay comparisons](gameplay/README.md) | Held, stationary dribble and moving dribble; both hands; normal and earliest/latest legal fakes |
| [Front diagnostic view](front/README.md) | Normal shots from both entering hands |
| [Side diagnostic view](side/README.md) | Normal shots from both entering hands |
| [Separate ENet observer](network/README.md) | Normal shots from both entering hands; paced capture; shared player/ball source ticks |
| [Rejected owning-client diagnostics](owner-diagnostics/README.md) | Both hands; every owner physics frame retained with ball-buffer reset markers; **no rendered-owner continuity verdict** |

Every comparison package retains complete source frames resized into labeled
panels. Normal-speed films play consecutive simulation source ticks at 60 Hz;
slow films play the same poses at 2.4x slow motion. Paced captures establish no
latency or performance result. The baseline's changed poses on held ticks are
shown separately in linked raw-edge diagnostics, not concealed as continuity.
Frame indexes retain original PNG hashes and observed camera/clock metadata;
compressed raw traces, manifests and authority events sit alongside each film.

The [local](final-local.json.gz) and [owner/observer](final-owner.json.gz) reports
contain the accepted 108-role objective matrix from source `90a1cf3`. The immutable before reports
remain [local](baseline-local.json.gz) and [network](baseline-owner.json.gz).
[Asset substitution controls](asset-only-control.json.gz),
[matrix verdict](summary.json.gz) and [report hashes](reports-sha256.json)
retain the discriminating controls and integrity checks.
[Final metric summary](final-metrics.json.gz) retains the measured aggregate
and gather results discussed in the proof record.
The final committed-source network form check retains
[8 passing legacy cases](net-form-baseline.json.gz),
[8 passing repaired cases](net-form-proof.json.gz) and the
[intended mismatched-boundary mutation rejection](net-form-mismatched-boundary-control.json.gz).
The [final local QA summary](qa-summary.json.gz) records both builds, unit and
repository tests, 106 single-process cases and the network checks from source
`90a1cf3`. This is selected verification; it does not claim a full final-head
catalog run. The [network log bundle](qa-network-logs.json.gz) retains the
two final runner consoles and all twelve crossover peer logs, with exact
original bytes, source paths, SHA256 hashes and decoded text.
The hash index records both compressed-file and uncompressed-payload SHA256 for
every top-level gzip report, trace, manifest and historical log in this folder.

The [startup-speed regression report](startup-speed-regression-before.json.gz)
preserves the reproduced RED result before the Startup entry-speed repair.
Its raw observer evidence remains available for the left hand
([manifest](startup-speed-red-left-manifest.json.gz),
[trace](startup-speed-red-left-trace.jsonl.gz)) and right hand
([manifest](startup-speed-red-right-manifest.json.gz),
[trace](startup-speed-red-right-trace.jsonl.gz)). These captures identify their
earlier source commit in each manifest.
The [focused passing report](startup-speed-focused-pass.json.gz) retains the
initial GREEN check from `.godot/418/review-split8-20261103-a/proof.json`.
Its captures identify source `4c1` with the working tree equivalent to
`90a1cf3`; the final committed-source matrix remains the acceptance report.

Additional Startup motion diagnostics retain the
[compact edge summary](startup-compact-summary.json.gz) and
[full frozen-before audit](startup-frozen-pre440-audit.json.gz).
These measurements are diagnostic, with no new acceptance threshold. The
summary's `split8` after captures identify source `4c1` with the working tree
equivalent to `90a1cf3`; frozen-before comparisons cross topologies. The proof
record explains those provenance limits.

Historical mutation logs from before the Startup entry-speed repair retain the
[Recovery clock bypass](440-clock-mutation-final-console.log.gz),
[stale-anchor restoration](440-reset-mutation-console.log.gz) and
[disconnected primary clip](440-audit-mutation-console.log.gz).
[Earlier exact asset verification](440-verify-assets-final-console.log.gz) records
preservation of unrelated clips and derived endpoints.
