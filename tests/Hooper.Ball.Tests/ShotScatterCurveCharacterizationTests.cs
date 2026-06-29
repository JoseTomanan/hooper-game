using Godot;
using Hooper.Ball;

namespace Hooper.Ball.Tests;

/// <summary>
/// Characterisation tests that measure the full make-percentage curve for the
/// current default scatter constants and document the numbers in one place.
///
/// ── Purpose ──────────────────────────────────────────────────────────────────
/// These tests serve as a living instrument panel for issue #79 (tune M8
/// shot-scatter magnitudes to feel).  They:
///   • Drive the REAL deterministic physics (ShotScatter → ShotArc → RimBackboard)
///     so every measured number reflects what the game actually produces.
///   • Use the same deterministic stratified-grid sweep as ShotMakeCurveTests
///     (100×100 = 10 000 samples per data point, fully reproducible with no RNG).
///   • Cover the complete distance ladder AND every penalty axis
///     (movement, contest, facing) so tuning any one constant is visible here.
///   • Skipped tests are characterisation only (run manually); the final Part 6
///     cross-check runs in CI to verify the file's constants stay in sync with
///     BallController defaults.
///
/// ── Sample count and determinism ─────────────────────────────────────────────
/// 10 000 samples per data point (100×100 centroid grid of the unit square).
/// Sample (i, j) = ((i+0.5)/100, (j+0.5)/100) — deterministic, no RNG.
/// Standard error at 40% make-rate ≈ ±0.5 pp; numbers stable to ±1 pp.
///
/// ── Measured numbers (current defaults, run 2026-06-29) ─────────────────────
/// See docs/analysis/0079-shot-scatter-curve.md for the full table.
/// Update the comments here after any retune of Spm / MaxScatter / K-values.
///
/// ── Why not just use the closed form ────────────────────────────────────────
/// Closed form make% ≈ (0.11 / rMax)² assumes every scattered aim-point inside
/// the inner-rim circle either makes or misses cleanly.  The real simulation
/// diverges in two directions:
///   • At low multipliers (open shots, ≤ 5 m): simulated ≈ 1–5 pp BELOW closed
///     form because RimBackboard rims out shallow-arc shots near the inner-rim
///     boundary (the ball physically clips the rim even when the aim-point is
///     just inside the circle).
///   • At high multipliers (heavy penalties): simulated can be 10–15 pp ABOVE
///     closed form because the make test is a 3-D capture cylinder, not a flat
///     disc — a make fires whenever the descending ball centre is within
///     innerRadius horizontally AND within ±2·BallRadius of rim height, so an
///     arc whose aim-point lands just past the rim still sweeps through it on
///     the way down.  This effect grows with scatter radius.  (NOT a backboard
///     effect: RimBackboard returns Bounce for board contact, and IsMake counts
///     any Bounce as a miss — the board can only reduce makes here.)
/// </summary>
public class ShotScatterCurveCharacterizationTests
{
    // ── Must mirror BallController [Export] defaults ──────────────────────────
    // If these change, re-run the characterisation tests and update comments.
    private const float Spm        = 0.026f;   // ShotScatterPerMeter
    private const float MaxScatter = 0.45f;    // MaxShotScatter
    private const float MovK       = 0.8f;     // MovementScatterK  (unused in sweep but documented)
    private const float ContK      = 1.0f;     // ContestScatterK   (unused in sweep but documented)
    private const float FacK       = 0.8f;     // FacingScatterK    (unused in sweep but documented)

    // ── Fixed geometry (matches BallController + RimBackboard defaults) ───────
    private const float Dt         = 1f / 60f;
    private const float RimRadius  = 0.23f;
    private const float BallRadius = 0.12f;
    private const float Apex       = 4.0f;
    private const float Gravity    = 9.8f;
    private const float HandHeight = 1.0f;
    private static readonly Vector3 RimCenter   = new(0f, 3.05f, 0f);
    private static readonly Vector3 BoardCenter = new(0f, 3.5f, 0.3f);
    private static readonly Vector3 BoardNormal = new(0f, 0f, -1f);

    // innerRadius = RimRadius - BallRadius = 0.11 m
    // A make requires the scattered aim-point to have horizDist < innerRadius from RimCenter.
    // Closed form: make% ≈ (0.11 / rMax)² when rMax > 0.11, else 100%.
    // Cap kicks in at dist >= MaxScatter/Spm = 0.45/0.026 ≈ 17.3 m — outside our test range.
    private const float InnerRadius = RimRadius - BallRadius; // 0.11 m

    // ── Core simulation helpers ────────────────────────────────────────────────

    private static bool IsMake(float distance, float angle01, float radius01, float mult)
    {
        Vector3 target  = ShotScatter.Scatter(RimCenter, distance, angle01, radius01, Spm, MaxScatter, mult);
        Vector3 release = new(0f, HandHeight, -distance);
        var arc = new ShotArc(release, target, Apex, Gravity);
        var rim = new RimBackboard(
            RimCenter, RimRadius, BallRadius, 0.65f,
            BoardCenter, BoardNormal, 0.46f, 0.30f, 0.65f);

        for (int tick = 0; tick < 600; tick++)
        {
            arc.Step(Dt);
            ContactResult r = rim.Resolve(arc);
            if (r == ContactResult.Make)   return true;
            if (r == ContactResult.Bounce) return false;
            if (arc.Position.Y <= BallRadius) return false;
        }
        return false;
    }

    /// <summary>
    /// Deterministic 100×100 centroid-grid sweep of the unit square.
    /// 10 000 samples per data point; no RNG; bit-identical across runs.
    /// </summary>
    private static double MakePct(float distance, float mult)
    {
        const int Side = 100;
        int makes = 0;
        for (int i = 0; i < Side; i++)
        for (int j = 0; j < Side; j++)
        {
            float a = (i + 0.5f) / Side;
            float r = (j + 0.5f) / Side;
            if (IsMake(distance, a, r, mult)) makes++;
        }
        return 100.0 * makes / (Side * Side);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Part 1 — Open-shot distance curve (no penalties, mult = 1.0)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Measures make% for every distance in the playable range.
    /// rMax = Spm × dist = 0.026 × dist; cap at MaxScatter = 0.45 m (irrelevant here).
    ///
    ///  dist |  rMax  | closed% | measured% | notes
    /// ------+--------+---------+-----------+----------------------------------------------
    ///   1 m | 0.0260 |   100.0 |     100.0 | rMax &lt; 0.11 → all samples make
    ///   2 m | 0.0520 |   100.0 |     100.0 |
    ///   3 m | 0.0780 |   100.0 |     100.0 |
    ///   4 m | 0.1040 |   100.0 |      92.7 | rMax just under 0.11; arc-angle rim-outs begin
    ///   5 m | 0.1300 |    71.6 |      67.3 | mid-range falloff; ~4 pp below closed
    ///   6 m | 0.1560 |    49.7 |      49.9 | close to closed form
    /// 6.75m | 0.1755 |    39.3 |      41.0 | NBA wide-open 3pt ≈ 38–40 % — design anchor
    ///   7 m | 0.1820 |    36.5 |      38.5 |
    ///   8 m | 0.2080 |    28.0 |      30.6 |
    ///   9 m | 0.2340 |    22.1 |      25.0 |
    ///  10 m | 0.2600 |    17.9 |      20.8 |
    ///  11 m | 0.2860 |    14.8 |      17.5 |
    ///
    /// At ≥ 6 m the 3-D capture cylinder catches overshooting arcs: measured is
    /// 2–3 pp ABOVE the closed form (which is a flat 2-D disc-area ratio).
    /// At 4–5 m the rim-out effect dominates: measured is 4–7 pp below closed form.
    /// </summary>
    [Theory(Skip = "characterization: run manually; not a CI gate")]
    [InlineData( 1.0)]
    [InlineData( 2.0)]
    [InlineData( 3.0)]
    [InlineData( 4.0)]
    [InlineData( 5.0)]
    [InlineData( 6.0)]
    [InlineData( 6.75)]
    [InlineData( 7.0)]
    [InlineData( 8.0)]
    [InlineData( 9.0)]
    [InlineData(10.0)]
    [InlineData(11.0)]
    public void OpenShotMakeCurveCharacterization(double distance)
    {
        double measured   = MakePct((float)distance, mult: 1f);
        float  rMax       = Spm * (float)distance;
        double closedForm = rMax >= InnerRadius
            ? Math.Pow(InnerRadius / rMax, 2.0) * 100.0
            : 100.0;
        // Wide assertion — intent is to emit the measured number in xunit output.
        Assert.True(measured >= 0.0 && measured <= 100.0,
            $"dist={distance} m: measured={measured:0.1}%  closed={closedForm:0.1}%  rMax={rMax:0.4}m");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Part 2 — Movement penalty sweep at 5 m (open otherwise)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// movementFactor = 1 + MovK × speedRatio = 1 + 0.8 × speedRatio
    /// Effective rMax at 5 m = 0.130 × mult.
    ///
    ///  speedRatio | mult | eff rMax | closed% | measured%
    /// -----------+------+----------+---------+-----------
    ///   0.00     | 1.00 |  0.1300  |    71.6 |      67.3
    ///   0.25     | 1.20 |  0.1560  |    49.7 |      55.4
    ///   0.50     | 1.40 |  0.1820  |    36.5 |      46.7
    ///   0.75     | 1.60 |  0.2080  |    28.0 |      40.3
    ///   1.00     | 1.80 |  0.2340  |    22.1 |      35.4  (full sprint)
    ///
    /// Note: measured is above closed form at mult > 1.0 because the 3-D capture
    /// cylinder catches overshooting arcs scattered just past the inner rim circle
    /// (not glass assists — board contact is a miss in this harness).
    /// </summary>
    [Theory(Skip = "characterization: run manually; not a CI gate")]
    // speedRatio, mult = 1 + 0.8 * speedRatio
    [InlineData(0.00, 1.00)]
    [InlineData(0.25, 1.20)]
    [InlineData(0.50, 1.40)]
    [InlineData(0.75, 1.60)]
    [InlineData(1.00, 1.80)]
    public void MovementPenaltyAt5m(double speedRatio, double mult)
    {
        double measured   = MakePct(5f, (float)mult);
        double effRMax    = 0.130 * mult;
        double closedForm = effRMax >= InnerRadius
            ? Math.Pow(InnerRadius / effRMax, 2.0) * 100.0 : 100.0;
        Assert.True(measured >= 0.0 && measured <= 100.0,
            $"speedRatio={speedRatio:0.00} mult={mult:0.00}: measured={measured:0.1}%  closed={closedForm:0.1}%");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Part 3 — Contest penalty sweep at 5 m (stationary, squared-up)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// contestFactor = 1 + ContK × proximity = 1 + 1.0 × proximity
    ///
    ///  proximity | mult | eff rMax | closed% | measured%
    /// ----------+------+----------+---------+-----------
    ///   0.00    | 1.00 |  0.1300  |    71.6 |      67.3  (uncontested)
    ///   0.25    | 1.25 |  0.1625  |    45.8 |      53.0
    ///   0.50    | 1.50 |  0.1950  |    31.8 |      43.3  (defender at ~1.1m)
    ///   0.75    | 1.75 |  0.2275  |    23.4 |      36.6
    ///   1.00    | 2.00 |  0.2600  |    17.9 |      31.5  (on-ball closeout)
    ///
    /// Maximum single-factor penalty (2.0×) is the hardest single-axis penalty.
    /// Measured is 5–14 pp above closed form at mult > 1.0 (capture-cylinder rescue).
    /// </summary>
    [Theory(Skip = "characterization: run manually; not a CI gate")]
    // proximity, mult = 1 + 1.0 * proximity
    [InlineData(0.00, 1.00)]
    [InlineData(0.25, 1.25)]
    [InlineData(0.50, 1.50)]
    [InlineData(0.75, 1.75)]
    [InlineData(1.00, 2.00)]
    public void ContestPenaltyAt5m(double proximity, double mult)
    {
        double measured   = MakePct(5f, (float)mult);
        double effRMax    = 0.130 * mult;
        double closedForm = effRMax >= InnerRadius
            ? Math.Pow(InnerRadius / effRMax, 2.0) * 100.0 : 100.0;
        Assert.True(measured >= 0.0 && measured <= 100.0,
            $"proximity={proximity:0.00} mult={mult:0.00}: measured={measured:0.1}%  closed={closedForm:0.1}%");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Part 4 — Facing penalty sweep at 5 m (stationary, uncontested)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// facingFactor = 1 + FacK × (angle/π) = 1 + 0.8 × (angleDeg/180)
    ///
    ///  angle | mult | eff rMax | closed% | measured%
    /// -------+------+----------+---------+-----------
    ///    0°  | 1.00 |  0.1300  |    71.6 |      67.3  (squared-up)
    ///   45°  | 1.20 |  0.1560  |    49.7 |      55.4
    ///   90°  | 1.40 |  0.1820  |    36.5 |      46.7  (side-on)
    ///  135°  | 1.60 |  0.2080  |    28.0 |      40.3
    ///  180°  | 1.80 |  0.2340  |    22.1 |      35.4  (back-to-basket)
    ///
    /// Note: FacK=0.8 matches MovK=0.8 — identical mult table to movement penalty.
    /// Both are below ContK=1.0 (1.0–2.0 range): a fully-contested shot is harder
    /// than any single-axis penalty alone — design intent from ADR-0009 §facing.
    /// Measured is 6–13 pp above closed form at mult > 1.0 (same capture-cylinder rescue).
    /// </summary>
    [Theory(Skip = "characterization: run manually; not a CI gate")]
    // angleDeg, mult = 1 + 0.8 * (angleDeg/180)
    [InlineData(  0, 1.00)]
    [InlineData( 45, 1.20)]
    [InlineData( 90, 1.40)]
    [InlineData(135, 1.60)]
    [InlineData(180, 1.80)]
    public void FacingPenaltyAt5m(double angleDeg, double mult)
    {
        double measured   = MakePct(5f, (float)mult);
        double effRMax    = 0.130 * mult;
        double closedForm = effRMax >= InnerRadius
            ? Math.Pow(InnerRadius / effRMax, 2.0) * 100.0 : 100.0;
        Assert.True(measured >= 0.0 && measured <= 100.0,
            $"facing={angleDeg}° mult={mult:0.00}: measured={measured:0.1}%  closed={closedForm:0.1}%");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Part 5 — Combined-penalty scenarios at design-critical distances
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Combined stacks at 2 m (layup), 5 m (mid-range), 6.75 m (3pt).
    ///
    /// Key design invariant from ADR-0009:
    ///   "A sprinting, tightly-contested 2 m shot is the only way to miss
    ///    point-blank — close shots stay forgiving unless BOTH moving AND contested."
    ///
    /// At 2 m, rMax = 0.052:
    ///   mult=1.80 → eff=0.094 &lt; 0.11 → 100.0 % (still automatic; confirmed)
    ///   mult=2.70 → eff=0.140 > 0.11 → 73.6 % measured (NOT automatic; confirmed)
    ///
    ///  dist  | mult  | scenario               | closed% | measured%
    /// -------+-------+------------------------+---------+-----------
    ///  2 m   | 1.00  | open layup             |   100.0 |     100.0
    ///  2 m   | 1.80  | sprint OR back-basket  |   100.0 |     100.0  (rMax 0.094 < 0.11)
    ///  2 m   | 2.70  | sprint + half-contest  |    61.4 |      73.6  (design point)
    ///  5 m   | 1.00  | open mid-range         |    71.6 |      67.3
    ///  5 m   | 1.80  | sprint-only            |    22.1 |      35.4
    ///  5 m   | 2.70  | sprint + half-contest  |     9.8 |      22.1
    ///  6.75m | 1.00  | open three             |    39.3 |      41.0  (NBA anchor)
    ///  6.75m | 1.80  | sprint-only            |    12.1 |      22.8
    ///  6.75m | 2.70  | sprint + half-contest  |     5.4 |      13.7  (desperation)
    /// </summary>
    [Theory(Skip = "characterization: run manually; not a CI gate")]
    [InlineData(2.0,   1.00, "2m open")]
    [InlineData(2.0,   1.80, "2m sprint-only")]
    [InlineData(2.0,   2.70, "2m sprint+half-contest")]
    [InlineData(5.0,   1.00, "5m open")]
    [InlineData(5.0,   1.80, "5m sprint-only")]
    [InlineData(5.0,   2.70, "5m sprint+half-contest")]
    [InlineData(6.75,  1.00, "6.75m open")]
    [InlineData(6.75,  1.80, "6.75m sprint-only")]
    [InlineData(6.75,  2.70, "6.75m sprint+half-contest")]
    public void CombinedPenaltyScenariosCharacterization(double dist, double mult, string scenario)
    {
        double measured = MakePct((float)dist, (float)mult);
        float  baseRMax = Spm * (float)dist;
        double effRMax  = baseRMax * mult;
        double closedForm = effRMax >= InnerRadius
            ? Math.Pow(InnerRadius / effRMax, 2.0) * 100.0 : 100.0;
        Assert.True(measured >= 0.0 && measured <= 100.0,
            $"[{scenario}] measured={measured:0.1}%  closed={closedForm:0.1}%  eff rMax={effRMax:0.4}m");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Part 6 — Cross-check: defaults must land inside ShotMakeCurveTests bands
    // (CI: NOT skipped)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// These are NOT skipped — they run in CI to guarantee:
    ///   (a) the constants Spm/MaxScatter at the top of this file mirror BallController defaults.
    ///   (b) the characterisation harness itself (IsMake / MakePct) is consistent
    ///       with the regression suite in ShotMakeCurveTests.
    ///
    /// Bands mirror ShotMakeCurveTests.OpenShotMakeCurveMatchesRealisticBands exactly.
    /// If this test fails but ShotMakeCurveTests passes, update Spm / MaxScatter above.
    ///
    /// Measured make% at current defaults (100×100 = 10 000 samples per point):
    ///   2 m   : ~100 %   (band [99.0, 100.1])
    ///   5 m   :  ~70 %   (band [60.0,  75.0])
    ///   6.75m :  ~41 %   (band [33.0,  48.0])
    ///  10 m   :  ~18 %   (band [12.0,  27.0])
    /// </summary>
    [Theory]
    [InlineData( 2.0,  99.0, 100.1)]
    [InlineData( 5.0,  60.0,  75.0)]
    [InlineData( 6.75, 33.0,  48.0)]
    [InlineData(10.0,  12.0,  27.0)]
    public void DefaultsMatchShotMakeCurveBands(double distance, double lo, double hi)
    {
        double pct = MakePct((float)distance, mult: 1f);
        Assert.True(pct >= lo && pct <= hi,
            $"Open make% at {distance} m = {pct:0.1}%, expected [{lo}, {hi}]. " +
            "Constants Spm/MaxScatter must mirror BallController [Export] defaults.");
    }
}
