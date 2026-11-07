# #442 pickup evidence — 2026-11-07

- **Issue:** #442; ADR-0004 and ADR-0027.
- **Pickup:** `eaf15f7c1116445907bda519558924ba280c70b8` (current main at pickup).
- **Purpose:** preserve the fresh red production attachment baseline and an independent audit of the existing calibration. Human appearance/feel acceptance remains #173.

## Method and scope

Six fresh **headless local** normal-shot episodes use Main, real input and the production-scaled rig: both entering hands × Held, stationary dribble and moving dribble, offset 0. Every manifest reports the pickup SHA, Godot 4.7.1 Mono, `images=false` and one release. These are diagnostic state/final-pose records, not films, owner/observer proof or evidence that #442 passes.

Final hand transforms were captured at `SkeletonUpdated`; the raw traces retain live/rendered ball centers, move/phase/frame, heading/HandSide, selected ticks and the independently latched release origin. The calibrated measurements transform the independently selected right palmar skin vertex into each final pose, then compute `abs(distance(palmWorld, visibleBallCenter) - 0.12)`. Wrist/center distance is reported separately. The existing 0.02 m settled-contact allowance is unchanged.

[Inventory](inventory.json) records every original source path, repository-relative source path, source/archive SHA256, source/archive byte count, purpose and capture commit where applicable. Gzip files preserve **original bytes**, with level 9, empty filename and `mtime=0`. No source report was relabeled as passing. All 17 archives were independently decompressed and checked against both inventory hashes and the original files.

## Fresh red measurements

All values are sphere-surface error in metres. Each is above the existing 0.02 m allowance.

| Entry / entering hand | Startup8 | Startup17 | Actual Active0 |
|---|---:|---:|---:|
| Held / Right | 0.559325 | 1.258297 | 1.309995 |
| Dribble / Right | 0.552804 | 1.258297 | 1.309995 |
| Moving / Right | 0.546474 | 1.258297 | 1.309995 |
| Held / Left | 0.757152 | 1.414328 | 1.461356 |
| Dribble / Left | 0.755456 | 1.414328 | 1.461356 |
| Moving / Left | 0.751035 | 1.414328 | 1.461355 |

Raw captures are in `baseline/<case>/{manifest.json,trace.jsonl}.gz`. The two `pickup-measurements*.json.gz` files retain the original wrist measurements and independently calibrated surface measurements. Archive byte/hash totals are in `inventory.json`.

## Independent skin and animation audit

The audit rechecked the existing raw [hand geometry](../raw/baseline/hand-geometry.json.gz) against the committed calibration, without using the ball to select anatomy. `Alpha_Surface` surface 0, right vertex **11216** and left vertex **10324**, exactly match their raw geometry entries. Each is influenced solely by its named Hand bone at weight **1**. Mesh/skeleton model transforms are identity; rest × bind is identity within floating-point roundoff. Recomputing wrist-local position directly from raw mesh vertex × skin bind differs from the recorded calibration by only 0.000000009 m (right) and 0.000000033 m (left). Thus final wrist transform × calibrated local point is an actual final skinned surface point, not a projected sphere point. This verifies the selected skin point; it does not establish a complete finger/grip fit.

The Y Bot SHA256 matches the calibration: `1358860131281330486977d84c1d95a4721ebdc5b8a7564995c691bf3112dc8e`. `held.res` and Player.tscn hashes also match. The full locomotion resource hash changed after #439, so the audit loaded the actual frozen `c5865e4` and current resources in Godot and compared **every normal-shot track**, including paths, types, interpolation, loop-wrap, enabled flags, key times, values and transitions, plus clip length/step/loop mode:

| Clip | Tracks | Keys | Comparison |
|---|---:|---:|---|
| jumpshotstartup | 53 | 1007 | Exact match |
| jumpshotactive | 53 | 265 | Exact match |
| jumpshotrecovery | 53 | 1113 | Exact match |
| fadeawayactive | 53 | 265 | Different, expected from #439 |

The raw clip dump and read-only extraction script are in `calibration-audit/442-calibration-clip-audit.{json,gd}.gz`. The summary `442-calibration-independent-audit.json.gz` includes the hashes, independently recomputed skin points and a bounded fake-return discriminator.

That discriminator investigated an older left Held late-fake Recovery16 sample: the selected proximal palm vertex's signed sphere gap is **−0.021649 m**, which is penetration rather than separation. Nearby actual palmar vertices **5680/5681** (same pure Hand skin, 0.03434 m along the palm) have signed gaps **−0.0001525 m**; vertex **10323** is **−0.0018322 m** and vertex **16885** is **+0.0068824 m**. The actual palmar skin crosses the sphere. This does **not** establish a detached hand or justify changing the settled-contact tolerance. The older source is already preserved at `../raw/final-local/left-held-late-o0-1/trace.jsonl.gz`.

## Reproduce the clip comparison

Run from the repository root. Binary `.res` data must pass through Python bytes; PowerShell text redirection would corrupt it. The extraction script writes only ignored `.godot` output.

```powershell
@'
from pathlib import Path
import gzip, subprocess
root = Path("docs/analysis/evidence/0442/pickup-20261107")
Path(".godot/442-calibration-clip-audit.gd").write_bytes(gzip.decompress(
    (root / "calibration-audit/442-calibration-clip-audit.gd.gz").read_bytes()))
Path(".godot/442-calibration-baseline-locomotion.res").write_bytes(subprocess.check_output([
    "git", "show", "c5865e42f1c41fd3e3197d08c0719b720d9d337a:assets/locomotion.res"]))
'@ | python -
& $env:GODOT --version
& $env:GODOT --headless --path . --log-file .godot/442-calibration-clip-repro.log --script res://.godot/442-calibration-clip-audit.gd
@'
import json
from pathlib import Path
baseline, current = json.loads(Path(".godot/442-calibration-clip-audit.json").read_text()).values()
for name in ("jumpshotstartup", "jumpshotactive", "jumpshotrecovery"):
    assert baseline[name] == current[name], name
    print(name, "exact match")
assert baseline["fadeawayactive"] != current["fadeawayactive"]
'@ | python -
```

The audited raw clip dump SHA256 is `7439c44132efb0d450a515c391921306769c81d1440f96f7f35c188d760636f8`. Use a fresh workspace-local log name for concurrent runs. APIs follow the official [Animation track reference](https://docs.godotengine.org/en/4.7/classes/class_animation.html), [Mesh array reference](https://docs.godotengine.org/en/4.7/classes/class_mesh.html) and [Skin bind reference](https://docs.godotengine.org/en/4.7/classes/class_skin.html).

## Reproduce the pickup capture

Use an isolated checkout of `eaf15f7c1116445907bda519558924ba280c70b8`, with the pinned console executable in `$env:GODOT`. Build that checkout's assembly before capture; passing `--commit` labels provenance and does not select executable code.

```powershell
dotnet build "HOOPER GAME.csproj" -p:IncludeIntegrationHarness=false
dotnet build "HOOPER GAME.csproj" -p:IncludeIntegrationHarness=true
python tools/run_jumpshot_transition_capture.py --godot "$env:GODOT" --output .godot/440/442-pickup-repro --topology local --entries held dribble moving --origins Right Left --variants normal --offsets 0 --headless --commit eaf15f7c1116445907bda519558924ba280c70b8
```

No tuning prescription or appearance/feel acceptance is made. This package freezes the red pickup and calibration checks; repaired production behavior and final integration gates require separate evidence.
