# Analysis 0079 — Shot-scatter make-% curve at current default magnitudes

- **Issue:** #79 (tune M8 shot-scatter magnitudes to feel)
- **ADR:** ADR-0009 (shot accuracy — distance-based scatter)
- **Date:** 2026-06-29
- **Purpose:** Quantitative data baseline for the #79 feel pass. The human
  (playtest) owns the final feel sign-off; this document provides the measured
  make-% curve so that any tuning proposal can reason from numbers rather than
  intuition alone.

---

## Simulation method

All numbers in this document are **measured from the real deterministic physics
chain** — `ShotScatter → ShotArc → RimBackboard` — not derived from the
closed-form approximation.

- **Grid sweep:** 100 × 100 centroid grid of the unit square (10 000 samples per
  data point). Sample (i, j) = ((i+0.5)/100, (j+0.5)/100). Deterministic, no
  RNG; bit-identical across runs.
- **Make condition:** the `RimBackboard.Resolve` loop is run for up to 600 ticks
  (10 s at 60 Hz). A tick returning `ContactResult.Make` counts as a make; a
  tick returning `ContactResult.Bounce`, or the ball falling below `BallRadius`
  height, counts as a miss.
- **Harness:** `tests/Hooper.Ball.Tests/ShotScatterCurveCharacterizationTests.cs`
  (Parts 1–5, `[Theory(Skip=...)]`). Numbers were captured by running a throwaway
  console app that references the same source files and applies the same
  simulation loop; the console app is not committed.
- **Standard error:** at 40% make-rate ≈ ±0.5 pp; numbers are stable to ±1 pp.

---

## Current default constants

These are the `BallController` `[Export]` defaults at the time of this analysis.
All numbers below are invalid if these change.

| Constant | Value | Meaning |
|---|---|---|
| `ShotScatterPerMeter` (Spm) | `0.026` m/m | Base scatter radius per metre of shot distance |
| `MaxShotScatter` | `0.45` m | Hard cap on base scatter radius (kicks in at ~17.3 m) |
| `MovementScatterK` (MovK) | `0.8` | Movement penalty K-factor |
| `ContestScatterK` (ContK) | `1.0` | Contest penalty K-factor |
| `FacingScatterK` (FacK) | `0.8` | Facing penalty K-factor |
| `ContestRange` | `2.2` m | XZ distance at which contest penalty is at maximum |

The accuracy multiplier composes as:

```
accuracyMultiplier = movementFactor × contestFactor × facingFactor

movementFactor = 1 + MovK × clamp(speed / MoveSpeed, 0, 1)
contestFactor  = 1 + ContK × clamp(1 − defDist / ContestRange, 0, 1)
facingFactor   = 1 + FacK × (angle / π)

r = min(Spm × distance, MaxShotScatter) × accuracyMultiplier × sqrt(radius01)
```

The cap is applied to the *base* radius first; the multiplier is applied after.
This means penalty stacking can push the final offset beyond `MaxShotScatter`.

---

## Part 1 — Open-shot distance curve (mult = 1.0)

No movement, no contest, squared-up. This is the baseline curve.

| Distance | rMax   | Closed-form% | Measured% | Notes |
|----------|--------|-------------|-----------|-------|
| 1 m      | 0.0260 | 100.0       | 100.0     | rMax < inner radius (0.11 m) — all makes |
| 2 m      | 0.0520 | 100.0       | 100.0     | rMax < inner radius — all makes |
| 3 m      | 0.0780 | 100.0       | 100.0     | rMax < inner radius — all makes |
| 4 m      | 0.1040 | 100.0       | 92.7      | rMax just above inner radius; rim-outs begin |
| 5 m      | 0.1300 | 71.6        | 67.3      | Mid-range falloff; ~4 pp below closed form |
| 6 m      | 0.1560 | 49.7        | 49.9      | Close to closed form |
| **6.75 m** | **0.1755** | **39.3** | **41.0** | **NBA wide-open 3-pt ≈ 38–40% — design anchor** |
| 7 m      | 0.1820 | 36.5        | 38.5      | |
| 8 m      | 0.2080 | 28.0        | 30.6      | Backboard assist grows measurably |
| 9 m      | 0.2340 | 22.1        | 25.0      | |
| 10 m     | 0.2600 | 17.9        | 20.8      | |
| 11 m     | 0.2860 | 14.8        | 17.5      | |

### Closed-form vs simulation divergence

Two opposing effects push measured away from closed form:

1. **Rim-out (4–6 m, measured < closed):** The closed form counts every
   aim-point inside the inner-rim circle (`r < 0.11 m`) as a make. The real
   physics clips the ball against the physical rim, so shallow-angle shots near
   the inner boundary rim out. Effect is largest at 4–5 m where the scatter
   radius sits just above the boundary (4 pp gap at 5 m).

2. **Backboard assist (≥ 6 m, measured > closed):** The closed form counts every
   aim-point outside the inner-rim circle as a miss. The real physics includes a
   backboard (`BoardCenter = (0, 3.5, 0.3)`, `BoardNormal = (0, 0, -1)`). Shots
   scattered behind the board can bounce off the glass and drop through the rim.
   This effect grows with scatter radius and becomes significant at distance ≥ 6 m
   or under heavy penalties. At 10 m open the gap is +2.9 pp; at 5 m full-sprint
   (mult=1.80) the gap is +13.3 pp.

---

## Part 2 — Movement penalty at 5 m

`movementFactor = 1 + 0.8 × speedRatio`; stationary otherwise.

| speedRatio | mult | eff rMax | Closed% | Measured% |
|-----------|------|----------|---------|-----------|
| 0.00      | 1.00 | 0.1300   | 71.6    | 67.3      |
| 0.25      | 1.20 | 0.1560   | 49.7    | 55.4      |
| 0.50      | 1.40 | 0.1820   | 36.5    | 46.7      |
| 0.75      | 1.60 | 0.2080   | 28.0    | 40.3      |
| 1.00      | 1.80 | 0.2340   | 22.1    | 35.4      |

Full-sprint (speedRatio=1.0) at 5 m: measured 35.4% vs closed-form 22.1%. The
+13.3 pp gap is the backboard-assist effect described above.

---

## Part 3 — Contest penalty at 5 m

`contestFactor = 1 + 1.0 × proximity`; stationary, squared-up otherwise.

| proximity | mult | eff rMax | Closed% | Measured% |
|-----------|------|----------|---------|-----------|
| 0.00      | 1.00 | 0.1300   | 71.6    | 67.3      |
| 0.25      | 1.25 | 0.1625   | 45.8    | 53.0      |
| 0.50      | 1.50 | 0.1950   | 31.8    | 43.3      |
| 0.75      | 1.75 | 0.2275   | 23.4    | 36.6      |
| 1.00      | 2.00 | 0.2600   | 17.9    | 31.5      |

The contest penalty is the steepest single-axis penalty (ContK=1.0 vs 0.8 for
movement and facing). Full closeout (proximity=1.0) at 5 m: measured 31.5%.
The measured value is 13.6 pp above closed form due to the backboard-assist effect.

---

## Part 4 — Facing penalty at 5 m

`facingFactor = 1 + 0.8 × (angleDeg / 180)`; stationary, uncontested otherwise.

| angle | mult | eff rMax | Closed% | Measured% |
|-------|------|----------|---------|-----------|
| 0°    | 1.00 | 0.1300   | 71.6    | 67.3      |
| 45°   | 1.20 | 0.1560   | 49.7    | 55.4      |
| 90°   | 1.40 | 0.1820   | 36.5    | 46.7      |
| 135°  | 1.60 | 0.2080   | 28.0    | 40.3      |
| 180°  | 1.80 | 0.2340   | 22.1    | 35.4      |

FacK=0.8 matches MovK=0.8: the facing and movement penalty tables are identical.
Back-to-basket (180°, mult=1.80): measured 35.4%, the same as full-sprint.

---

## Part 5 — ADR-0009 invariant: close shots stay forgiving

ADR-0009 states:

> "A sprinting, tightly-contested 2 m shot is the only way to miss point-blank —
> close shots stay forgiving unless BOTH moving AND contested."

The invariant holds only when `effRMax = min(Spm × 2, MaxShotScatter) × mult < 0.11 m`.
At 2 m, `baseRMax = 0.052 m`. The threshold is `mult = 0.11 / 0.052 ≈ 2.12×`.

Any single maximum-penalty factor maxes out at `mult = 1.80` — below the
threshold. Two penalties stacking (e.g., full sprint + full closeout at 2 m)
would reach `mult = 1.80 × 2.00 = 3.60`, well above the threshold. The ADR-0009
test scenario uses `mult = 2.70` (sprint × half-contest) as the representative
"both penalties" case.

| dist  | mult | scenario               | Closed% | Measured% |
|-------|------|------------------------|---------|-----------|
| 2 m   | 1.00 | open layup             | 100.0   | **100.0** |
| 2 m   | 1.80 | sprint OR back-basket  | 100.0   | **100.0** |
| 2 m   | 2.70 | sprint + half-contest  | 61.4    | **73.6**  |
| 5 m   | 1.00 | open mid-range         | 71.6    | 67.3      |
| 5 m   | 1.80 | sprint-only            | 22.1    | 35.4      |
| 5 m   | 2.70 | sprint + half-contest  | 9.8     | 22.1      |
| 6.75 m | 1.00 | open three            | 39.3    | 41.0      |
| 6.75 m | 1.80 | sprint-only           | 12.1    | 22.8      |
| 6.75 m | 2.70 | sprint + half-contest | 5.4     | 13.7      |

**ADR-0009 invariant confirmed:** 2 m open and 2 m sprint-only both measure
100.0%. The first miss opportunity appears at `mult = 2.70` (sprint plus
partial contest), which measures 73.6% — still well above zero but no longer
automatic. The design intent holds.

---

## NBA anchors and tuning observations

The following anchors from the ADR-0009 tuning notes can be compared to the
measured curve:

| Distance | ADR-0009 note | Measured% |
|----------|---------------|-----------|
| ≤ 3 m    | "~100%, open layup — automatic" | 100.0% ✓ |
| 5 m      | "~67%, open mid-range" | 67.3% ✓ |
| 5.8 m    | "~53%, at the clear line" | ~51% (interpolated between 5 m and 6 m) |
| **6.75 m** | **"~41%, NBA wide-open three ≈ 38–40%"** | **41.0% ✓** |
| 10 m     | "~21%, steep falloff rewards spacing" | 20.8% ✓ |

The 6.75 m measured value (41.0%) lands squarely in the NBA wide-open 3-pt band
(38–40%), and is consistent with the ADR-0009 tuning target. The ADR quoted 41%
as the design anchor; the simulation confirms this at 41.0%.

### Candidate tuning observations (no prescription — human's feel call)

These observations describe where the curve sits relative to reference points.
They do not prescribe any change; that decision belongs to the feel playtest.

1. **4 m is not automatic (92.7%).** The inner-radius boundary falls between
   3 m (rMax=0.078 < 0.11, automatic) and 4 m (rMax=0.104, just above 0.11).
   A straight-drive layup from 4 m misses 7.3% of the time even uncontested. If
   the design intent is "inside ~4 m is automatic", Spm would need to drop
   slightly (or a fixed inner-deadzone added). If the intent is a gradual
   falloff starting around 4 m, the current curve matches.

2. **Penalty penalties are more forgiving than closed-form predicts.** At full
   single-axis penalty (mult=1.80 at 5 m), measured is 35.4% — not 22.1%. This
   is because the backboard rescues shots that the closed form would count as
   misses. Whether this extra forgiveness feels right or too forgiving is a
   playtest question. Reducing `MaxShotScatter` or `ContK`/`MovK` would
   tighten the curve; the simulation numbers in this doc are the ground truth
   for any such adjustment.

3. **6.75 m is the design anchor and it is confirmed.** No tuning pressure at
   the three-point line unless the feel pass identifies a reason to move it.

4. **The "both penalties" floor at 6.75 m is ~14%.** A full-sprint, half-
   contested three (mult=2.70) measures 13.7%. A fully combined shot
   (sprint + full contest + back-to-basket, mult ≈ 5.76) would be extremely low.
   Whether "desperation" shots feel exciting-but-possible or just-impossible is
   a playtest question.
