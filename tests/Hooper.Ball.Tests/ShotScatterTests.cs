using Godot;
using Hooper.Ball;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for ShotScatter — the pure XZ-plane shot-target offset helper
/// (issue #62, ADR-0009).  Headless (ADR-0004): pure deterministic function,
/// no engine state.
///
/// ── Test naming convention ───────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// One logical assertion per test.
/// </summary>
public class ShotScatterTests
{
    // Representative rim target used across tests.
    private static readonly Vector3 Target = new(0f, 3.05f, 0f);

    // Typical tunables matching BallController defaults.
    private const float ScatterPerMeter = 0.03f;
    private const float MaxScatter      = 0.4f;

    // Tolerance for floating-point comparisons.
    private const float Epsilon = 1e-5f;

    // ── Zero-distance tests ───────────────────────────────────────────────

    [Fact]
    public void Scatter_ZeroDistance_ReturnsTargetUnchanged_AnyAngle()
    {
        // At zero distance the raw radius = scatterPerMeter * 0 = 0,
        // so no offset is applied regardless of angle or radius samples.
        Vector3 result = ShotScatter.Scatter(Target, 0f, 0.42f, 0.99f, ScatterPerMeter, MaxScatter);
        Assert.True(MathF.Abs(result.X - Target.X) < Epsilon, $"X differs: {result.X} vs {Target.X}");
        Assert.True(MathF.Abs(result.Z - Target.Z) < Epsilon, $"Z differs: {result.Z} vs {Target.Z}");
    }

    [Fact]
    public void Scatter_ZeroDistance_YPreserved()
    {
        Vector3 result = ShotScatter.Scatter(Target, 0f, 0.5f, 0.5f, ScatterPerMeter, MaxScatter);
        Assert.True(MathF.Abs(result.Y - Target.Y) < Epsilon, $"Y changed: {result.Y} vs {Target.Y}");
    }

    // ── Cap tests ─────────────────────────────────────────────────────────

    [Fact]
    public void Scatter_VeryLargeDistance_OffsetMagnitudeCappedAtMaxScatter()
    {
        // A 1000 m shot would produce scatterPerMeter * 1000 = 30 m raw radius;
        // the cap must clamp it to MaxScatter regardless.
        // Using radius01 = 1 (via the limit, approaching from below) and sqrt(1)=1
        // means the full cap radius is applied — XZ offset magnitude == MaxScatter.
        // We pass radius01 = 0.9999f as a practical proxy for "near 1."
        float   distance = 1000f;
        Vector3 result   = ShotScatter.Scatter(Target, distance, 0f, 0.9999f, ScatterPerMeter, MaxScatter);

        float offsetX = result.X - Target.X;
        float offsetZ = result.Z - Target.Z;
        float mag     = MathF.Sqrt(offsetX * offsetX + offsetZ * offsetZ);

        Assert.True(mag <= MaxScatter + Epsilon,
            $"Offset magnitude {mag:F6} exceeds MaxScatter {MaxScatter}");
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    [InlineData(0.9999f)]
    public void Scatter_LargeDistance_OffsetMagnitudeNeverExceedsMaxScatter_AnyRadius(float radius01)
    {
        Vector3 result = ShotScatter.Scatter(Target, 999f, 0.3f, radius01, ScatterPerMeter, MaxScatter);

        float offsetX = result.X - Target.X;
        float offsetZ = result.Z - Target.Z;
        float mag     = MathF.Sqrt(offsetX * offsetX + offsetZ * offsetZ);

        Assert.True(mag <= MaxScatter + Epsilon,
            $"radius01={radius01}: magnitude {mag:F6} exceeds MaxScatter {MaxScatter}");
    }

    // ── Determinism test ──────────────────────────────────────────────────

    [Fact]
    public void Scatter_SameInputs_ReturnIdenticalOutput()
    {
        // Pure function: same arguments must always produce the same result,
        // regardless of call order or context.
        Vector3 first  = ShotScatter.Scatter(Target, 5f, 0.37f, 0.62f, ScatterPerMeter, MaxScatter);
        Vector3 second = ShotScatter.Scatter(Target, 5f, 0.37f, 0.62f, ScatterPerMeter, MaxScatter);

        Assert.True(MathF.Abs(first.X - second.X) < Epsilon, $"X differs between calls: {first.X} vs {second.X}");
        Assert.True(MathF.Abs(first.Y - second.Y) < Epsilon, $"Y differs between calls: {first.Y} vs {second.Y}");
        Assert.True(MathF.Abs(first.Z - second.Z) < Epsilon, $"Z differs between calls: {first.Z} vs {second.Z}");
    }

    // ── Radius scales with distance below the cap ─────────────────────────

    [Fact]
    public void Scatter_RadiusScalesWithDistance_BelowCap()
    {
        // With radius01 approaching 1 (sqrt ≈ 1), the offset magnitude should
        // be approximately scatterPerMeter * distance when below the cap.
        // We use angle01 = 0 so the full offset lands on the +X axis, making
        // it easy to measure.  radius01 = 1 is the limit; we test with a
        // value where sqrt(radius01) ≈ 1 to keep it finite.
        float   distance  = 3f;   // 3 m → raw radius = 0.03 * 3 = 0.09 m (below MaxScatter 0.4)
        float   radius01  = 1f;   // sqrt(1) = 1 — full radius applied
        float   angle01   = 0f;   // theta = 0 → cos(0)=1, sin(0)=0 → offset on +X only

        Vector3 result = ShotScatter.Scatter(Target, distance, angle01, radius01, ScatterPerMeter, MaxScatter);

        float expectedMag = ScatterPerMeter * distance; // 0.09 m
        float actualOffsetX = result.X - Target.X;      // cos(0)*r = r
        float actualOffsetZ = result.Z - Target.Z;      // sin(0)*r = 0

        Assert.True(MathF.Abs(actualOffsetX - expectedMag) < Epsilon,
            $"X offset {actualOffsetX:F6} should be ~{expectedMag:F6}");
        Assert.True(MathF.Abs(actualOffsetZ) < Epsilon,
            $"Z offset {actualOffsetZ:F6} should be ~0");
    }

    [Fact]
    public void Scatter_RadiusScalesWithDistance_LargerDistance()
    {
        // Double the distance → double the offset magnitude (still below cap).
        float   distance  = 6f;   // 6 m → raw radius = 0.03 * 6 = 0.18 m (below MaxScatter 0.4)
        float   radius01  = 1f;
        float   angle01   = 0f;

        Vector3 result = ShotScatter.Scatter(Target, distance, angle01, radius01, ScatterPerMeter, MaxScatter);

        float expectedMag   = ScatterPerMeter * distance; // 0.18 m
        float actualOffsetX = result.X - Target.X;

        Assert.True(MathF.Abs(actualOffsetX - expectedMag) < Epsilon,
            $"X offset {actualOffsetX:F6} should be ~{expectedMag:F6}");
    }

    // ── Y-preservation test ───────────────────────────────────────────────

    [Fact]
    public void Scatter_YComponentNeverChanged()
    {
        // Scatter is an XZ-plane operation: the rim height must be preserved
        // exactly regardless of distance or samples.
        var customTarget = new Vector3(2f, 4.5f, -3f);
        Vector3 result = ShotScatter.Scatter(customTarget, 7f, 0.88f, 0.44f, ScatterPerMeter, MaxScatter);

        Assert.True(MathF.Abs(result.Y - customTarget.Y) < Epsilon,
            $"Y changed: {result.Y} vs {customTarget.Y}");
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(0.99f, 0.99f)]
    public void Scatter_YPreservedAcrossSamples(float angle01, float radius01)
    {
        Vector3 result = ShotScatter.Scatter(Target, 10f, angle01, radius01, ScatterPerMeter, MaxScatter);
        Assert.True(MathF.Abs(result.Y - Target.Y) < Epsilon,
            $"angle01={angle01}, radius01={radius01}: Y changed to {result.Y}");
    }

    // ── AccuracyMultiplier tests (issues #64/#65) ─────────────────────────

    [Fact]
    public void Scatter_AccuracyMultiplierOne_MatchesPriorBehavior()
    {
        // Multiplier 1.0 must produce the same result as the six-argument
        // overload that defaulted to 1.0 — existing callers are unaffected.
        float distance = 5f;
        float angle01  = 0.37f;
        float radius01 = 0.62f;

        Vector3 withDefault  = ShotScatter.Scatter(Target, distance, angle01, radius01, ScatterPerMeter, MaxScatter);
        Vector3 withExplicit = ShotScatter.Scatter(Target, distance, angle01, radius01, ScatterPerMeter, MaxScatter, 1.0f);

        Assert.True(MathF.Abs(withDefault.X  - withExplicit.X)  < Epsilon, $"X differs: {withDefault.X} vs {withExplicit.X}");
        Assert.True(MathF.Abs(withDefault.Y  - withExplicit.Y)  < Epsilon, $"Y differs");
        Assert.True(MathF.Abs(withDefault.Z  - withExplicit.Z)  < Epsilon, $"Z differs: {withDefault.Z} vs {withExplicit.Z}");
    }

    [Fact]
    public void Scatter_AccuracyMultiplierTwo_DoublesEffectiveRadius()
    {
        // With angle01 = 0 the full offset lands on the +X axis, making the
        // magnitude trivially readable as result.X - Target.X.
        // radius01 = 1 → sqrt(1) = 1 → r = rMax * multiplier exactly.
        float distance   = 3f;    // raw radius = 0.03 * 3 = 0.09 m (below cap)
        float angle01    = 0f;
        float radius01   = 1f;

        Vector3 base1x = ShotScatter.Scatter(Target, distance, angle01, radius01, ScatterPerMeter, MaxScatter, 1.0f);
        Vector3 base2x = ShotScatter.Scatter(Target, distance, angle01, radius01, ScatterPerMeter, MaxScatter, 2.0f);

        float offset1x = base1x.X - Target.X;
        float offset2x = base2x.X - Target.X;

        // Multiplier 2 should double the offset radius.
        Assert.True(MathF.Abs(offset2x - 2f * offset1x) < Epsilon,
            $"Expected doubled offset {2f * offset1x:F6}, got {offset2x:F6}");
    }

    [Fact]
    public void Scatter_AccuracyMultiplierTwo_CanExceedMaxScatter()
    {
        // The cap (maxScatter) is applied BEFORE the multiplier, so a penalty
        // multiplier of 2 on a capped shot should produce an offset exceeding
        // maxScatter — that is the intended penalty mechanic (#64/#65).
        // Use a long shot so the raw radius hits the cap.
        float distance = 1000f;  // raw = 0.03 * 1000 = 30 m → clamped to MaxScatter
        float angle01  = 0f;
        float radius01 = 1f;     // sqrt(1) = 1 → r = rMax * multiplier

        Vector3 result = ShotScatter.Scatter(Target, distance, angle01, radius01, ScatterPerMeter, MaxScatter, 2.0f);

        float offsetX = result.X - Target.X;
        float offsetZ = result.Z - Target.Z;
        float mag     = MathF.Sqrt(offsetX * offsetX + offsetZ * offsetZ);

        // With multiplier 2 the expected magnitude is MaxScatter * 2.
        float expected = MaxScatter * 2f;
        Assert.True(MathF.Abs(mag - expected) < Epsilon,
            $"Expected magnitude {expected:F6}, got {mag:F6}");

        // And it must exceed MaxScatter (proving the cap was applied before multiply).
        Assert.True(mag > MaxScatter,
            $"Expected mag {mag:F6} to exceed MaxScatter {MaxScatter}");
    }

    [Fact]
    public void Scatter_AccuracyMultiplierComposesMultiplicatively()
    {
        // If movement factor = 1.5 and contest factor = 1.4, the combined
        // multiplier = 2.1.  The result with multiplier 2.1 must equal the
        // product of applying each factor independently (they are not applied
        // sequentially — the combined value is passed as one parameter, so
        // this test verifies the caller's multiplication matches the expected
        // geometric composition rather than, e.g., addition).
        float distance   = 4f;
        float angle01    = 0f;
        float radius01   = 1f;

        float movFactor     = 1.5f;
        float conFactor     = 1.4f;
        float combined      = movFactor * conFactor; // 2.1

        Vector3 resultCombined  = ShotScatter.Scatter(Target, distance, angle01, radius01, ScatterPerMeter, MaxScatter, combined);
        // Verify against expected: rMax * combined * sqrt(1) at theta=0 → offset on +X
        float rMax     = MathF.Min(ScatterPerMeter * distance, MaxScatter); // 0.03*4 = 0.12
        float expected = rMax * combined; // 0.12 * 2.1 = 0.252

        float actualOffsetX = resultCombined.X - Target.X;
        Assert.True(MathF.Abs(actualOffsetX - expected) < Epsilon,
            $"Expected X offset {expected:F6}, got {actualOffsetX:F6}");
    }

    [Fact]
    public void Scatter_AccuracyMultiplierOne_YPreserved()
    {
        // Y must remain unchanged even with an explicit multiplier.
        Vector3 result = ShotScatter.Scatter(Target, 5f, 0.3f, 0.7f, ScatterPerMeter, MaxScatter, 3.0f);
        Assert.True(MathF.Abs(result.Y - Target.Y) < Epsilon,
            $"Y changed with multiplier: {result.Y} vs {Target.Y}");
    }
}
