using Godot;
using Hooper.Ball;

namespace Hooper.Ball.Tests;

/// <summary>
/// Characterisation + regression tests for the TUNED shot-scatter values
/// (issues #62/#64/#65, ADR-0009).  These drive the real deterministic physics
/// (ShotScatter → ShotArc → RimBackboard) over a deterministic stratified sweep
/// of the scatter sample space and assert the resulting make-percentage curve
/// matches the values the tunables were chosen for — open layups automatic, an
/// open three ≈ 41 % (real NBA wide-open ≈ 38–40 %), long shots falling off
/// steeply — and that the movement (#64) and contest (#65) penalties drag make%
/// down in the right direction and magnitude.
///
/// Why a sweep instead of a closed form: a make requires the scattered aim point
/// to land inside the inner-rim radius, which the closed form approximates as
/// (0.11 / rMax)² — but the real RimBackboard also rims out shallow-arc shots
/// near the boundary that the closed form would count as makes.  Running the
/// actual integrator captures that nuance, so this test guards the real feel.
///
/// IMPORTANT: the constants below MUST mirror the BallController [Export]
/// defaults.  If those are retuned, update these bands (they are deliberately
/// wide enough to absorb small tuning, tight enough to catch a real regression).
/// </summary>
public class ShotMakeCurveTests
{
    // ── Must match BallController export defaults ────────────────────────────
    private const float Spm        = 0.026f;  // ShotScatterPerMeter
    private const float MaxScatter = 0.45f;   // MaxShotScatter

    // ── Fixed geometry (matches BallController + RimBackboard defaults) ───────
    private const float Dt         = 1f / 60f;
    private const float RimRadius  = 0.23f;
    private const float BallRadius = 0.12f;
    private const float Apex       = 4.0f;
    private const float Gravity    = 9.8f;
    private const float HandHeight = 1.0f;
    private static readonly Vector3 RimCenter   = new(0, 3.05f, 0);
    private static readonly Vector3 BoardCenter = new(0, 3.5f, 0.3f);
    private static readonly Vector3 BoardNormal = new(0, 0, -1);

    /// <summary>
    /// Simulates one shot from <paramref name="distance"/> metres, aimed at the
    /// rim but scattered by the injected unit-square sample, under an accuracy
    /// <paramref name="mult"/>.  Returns true on a clean make.
    /// </summary>
    private static bool IsMake(float distance, float angle01, float radius01, float mult)
    {
        Vector3 target  = ShotScatter.Scatter(RimCenter, distance, angle01, radius01, Spm, MaxScatter, mult);
        Vector3 release = new(0, HandHeight, -distance);
        var arc = new ShotArc(release, target, Apex, Gravity);
        var rim = new RimBackboard(RimCenter, RimRadius, BallRadius, 0.65f,
                                   BoardCenter, BoardNormal, 0.46f, 0.30f, 0.65f);
        for (int tick = 0; tick < 600; tick++)
        {
            arc.Step(Dt);
            ContactResult r = rim.Resolve(arc);
            if (r == ContactResult.Make) return true;
            if (r == ContactResult.Bounce) return false;
            if (arc.Position.Y <= BallRadius) return false; // floor = miss
        }
        return false;
    }

    /// <summary>
    /// Deterministic stratified make-percentage: sweeps a side×side grid of the
    /// (angle01, radius01) unit square so the result is reproducible with no RNG.
    /// </summary>
    private static double MakePct(float distance, float mult, int side = 100)
    {
        int makes = 0, total = 0;
        for (int i = 0; i < side; i++)
            for (int j = 0; j < side; j++)
            {
                float a = (i + 0.5f) / side;
                float r = (j + 0.5f) / side;
                if (IsMake(distance, a, r, mult)) makes++;
                total++;
            }
        return 100.0 * makes / total;
    }

    [Theory]
    // distance, lower-bound %, upper-bound %  (open, stationary shot)
    [InlineData(2.0,  99.0, 100.1)]  // close: automatic when open
    [InlineData(5.0,  60.0,  75.0)]  // mid-range
    [InlineData(6.75, 33.0,  48.0)]  // three-point: ~41 %, NBA wide-open ≈ 38–40 %
    [InlineData(10.0, 12.0,  27.0)]  // long: steep falloff
    public void OpenShotMakeCurveMatchesRealisticBands(double distance, double lo, double hi)
    {
        double pct = MakePct((float)distance, mult: 1f);
        Assert.True(pct >= lo && pct <= hi,
            $"Open make% at {distance} m was {pct:0.0}%, expected [{lo}, {hi}].");
    }

    [Fact]
    public void MovementAndContestPenaltiesReduceMakePercentage()
    {
        // At 5 m an open shot is the most makeable; moving, contesting, and doing
        // both must each strictly lower make% — and both-at-once must be lowest.
        double open      = MakePct(5f, mult: 1.0f);
        double moving    = MakePct(5f, mult: 1.8f); // full sprint: 1 + 0.8
        double contested = MakePct(5f, mult: 1.5f); // ~1 m defender: 1 + 1.0·0.5
        double both      = MakePct(5f, mult: 2.7f); // 1.8 × 1.5

        Assert.True(moving    < open,      $"moving {moving:0.0}% should be < open {open:0.0}%");
        Assert.True(contested < open,      $"contested {contested:0.0}% should be < open {open:0.0}%");
        Assert.True(both      < moving,    $"both {both:0.0}% should be < moving {moving:0.0}%");
        Assert.True(both      < contested, $"both {both:0.0}% should be < contested {contested:0.0}%");
    }

    [Fact]
    public void CloseOpenShotsAreForgivingButPressuredCloseShotsCanMiss()
    {
        // A 2 m shot is automatic when open OR only moving OR only contested —
        // close shots stay forgiving — but a sprinting, tightly-contested 2 m
        // shot (combined multiplier ~2.7) genuinely starts to miss.
        Assert.True(MakePct(2f, 1.0f) >= 99.0, "open 2 m should be automatic");
        Assert.True(MakePct(2f, 1.8f) >= 99.0, "moving-only 2 m should still be automatic");
        Assert.True(MakePct(2f, 1.5f) >= 99.0, "contested-only 2 m should still be automatic");
        double pressured = MakePct(2f, 2.7f);
        Assert.True(pressured < 90.0 && pressured > 40.0,
            $"sprinting+contested 2 m was {pressured:0.0}%, expected a real but not hopeless dip.");
    }

    [Fact]
    public void FacingPenaltyDragsMakePercentDownMonotonically()
    {
        // Pins the #81 off-facing penalty end-to-end: ShotFacing.Multiplier
        // (at the BallController default FacingScatterK = 0.8) composed onto the
        // real make curve. Squared-up must be unpenalised; side-on and
        // back-to-basket must drop make% monotonically. Bands are wide enough to
        // absorb small FacingScatterK tuning, tight enough to catch a regression.
        // If FacingScatterK is retuned, update these bands and explain why.
        const float K = 0.8f;
        Vector3 rim     = new(0, 3.05f, 0);
        Vector3 shooter = new(0, 0, 5f);          // 5 m in front of the rim (+Z)
        float toRimYaw  = Mathf.Atan2(rim.X - shooter.X, rim.Z - shooter.Z);

        float squaredMult = ShotFacing.Multiplier(toRimYaw,                          shooter, rim, K); // 1.0
        float sideMult    = ShotFacing.Multiplier(Norm(toRimYaw + Mathf.Pi / 2f),    shooter, rim, K); // 1.4
        float backMult    = ShotFacing.Multiplier(Norm(toRimYaw + Mathf.Pi),         shooter, rim, K); // 1.8

        double squared = MakePct(5f, squaredMult);
        double side    = MakePct(5f, sideMult);
        double back    = MakePct(5f, backMult);

        Assert.True(side < squared, $"side-on {side:0.0}% should be < squared-up {squared:0.0}%");
        Assert.True(back < side,    $"back-to-basket {back:0.0}% should be < side-on {side:0.0}%");
        Assert.True(side >= 40.0 && side <= 53.0, $"side-on (90°) make% was {side:0.0}%, expected [40, 53].");
        Assert.True(back >= 28.0 && back <= 43.0, $"back-to-basket (180°) make% was {back:0.0}%, expected [28, 43].");
    }

    // Wrap an angle into [-π, π] for the facing test above.
    private static float Norm(float a)
    {
        while (a >  Mathf.Pi) a -= 2f * Mathf.Pi;
        while (a < -Mathf.Pi) a += 2f * Mathf.Pi;
        return a;
    }
}
