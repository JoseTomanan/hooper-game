using System;
using Godot;
using Hooper.Ball;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for ShotFacing — the heading-based shot accuracy penalty helper
/// (issue #81, ADR-0009 amendment 2026-06-27, ADR-0010).
/// Headless (ADR-0004): pure deterministic function, no engine state.
///
/// ── Test naming convention ───────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// One logical assertion per test.
/// </summary>
public class ShotFacingTests
{
    // Rim at origin (XZ), elevated — realistic basket position.
    private static readonly Vector3 Rim = new(0f, 3.05f, 0f);

    // Shooter standing 5 m directly in front of the rim (+Z).
    // At yaw = 0 (facing +Z) the shooter is squared up to this target.
    private static readonly Vector3 ShooterSquaredUp = new(0f, 0f, 5f);

    // Balance constant matching BallController's default.
    private const float K = 0.8f;

    // Tolerance for floating-point comparisons.
    private const float Epsilon = 1e-4f;

    // ── Squared-up shot (0°) ─────────────────────────────────────────────

    [Fact]
    public void Multiplier_SquaredUp_ReturnsOne()
    {
        // Shooter at (0, 0, 5), rim at origin: direction to rim is −Z,
        // i.e. targetYaw = Atan2(0-0, 0-5) = Atan2(0, -5) = π.
        // Heading π also means facing −Z (toward rim).  Angle diff = 0.
        float targetYaw = MathF.Atan2(Rim.X - ShooterSquaredUp.X, Rim.Z - ShooterSquaredUp.Z);
        float result = ShotFacing.Multiplier(targetYaw, ShooterSquaredUp, Rim, K);

        Assert.True(MathF.Abs(result - 1f) < Epsilon,
            $"Squared-up shot should give multiplier 1.0, got {result:F6}");
    }

    // ── Back-to-basket (180°) ────────────────────────────────────────────

    [Fact]
    public void Multiplier_BackToBasket_ReturnsOnePlusK()
    {
        // Heading = targetYaw + π → exactly back to basket.
        float targetYaw = MathF.Atan2(Rim.X - ShooterSquaredUp.X, Rim.Z - ShooterSquaredUp.Z);
        float backYaw   = NormaliseAngle(targetYaw + MathF.PI);

        float result   = ShotFacing.Multiplier(backYaw, ShooterSquaredUp, Rim, K);
        float expected = 1f + K;

        Assert.True(MathF.Abs(result - expected) < Epsilon,
            $"Back-to-basket (180°) should give {expected:F4}, got {result:F6}");
    }

    // ── 90° off — monotonicity ───────────────────────────────────────────

    [Fact]
    public void Multiplier_NinetyDegrees_StrictlyBetweenSquaredAndBack()
    {
        float targetYaw = MathF.Atan2(Rim.X - ShooterSquaredUp.X, Rim.Z - ShooterSquaredUp.Z);
        float ninetyYaw = NormaliseAngle(targetYaw + MathF.PI / 2f);

        float result      = ShotFacing.Multiplier(ninetyYaw, ShooterSquaredUp, Rim, K);
        float squaredUp   = 1f;
        float backBasket  = 1f + K;

        Assert.True(result > squaredUp,
            $"90° multiplier {result:F4} should exceed squared-up value {squaredUp}");
        Assert.True(result < backBasket,
            $"90° multiplier {result:F4} should be below back-to-basket value {backBasket}");
    }

    [Fact]
    public void Multiplier_NinetyDegrees_IsHalfwayBetweenMinAndMax()
    {
        // Linear mapping: 90° = π/2 → factor = 1 + K × 0.5 = 1 + K/2.
        float targetYaw = MathF.Atan2(Rim.X - ShooterSquaredUp.X, Rim.Z - ShooterSquaredUp.Z);
        float ninetyYaw = NormaliseAngle(targetYaw + MathF.PI / 2f);

        float result   = ShotFacing.Multiplier(ninetyYaw, ShooterSquaredUp, Rim, K);
        float expected = 1f + K * 0.5f;

        Assert.True(MathF.Abs(result - expected) < Epsilon,
            $"90° offset should give {expected:F4}, got {result:F6}");
    }

    // ── Degenerate case (shooter on rim, zero XZ vector) ─────────────────

    [Fact]
    public void Multiplier_ShooterOnRim_ReturnsOne_NoNaN()
    {
        // When the shooter occupies the same XZ position as the rim,
        // direction is undefined — the function must return 1.0 and not NaN.
        var onRim = new Vector3(Rim.X, 0f, Rim.Z); // same XZ, different Y

        float result = ShotFacing.Multiplier(0f, onRim, Rim, K);

        Assert.False(float.IsNaN(result), $"Got NaN for degenerate shooter-on-rim case");
        Assert.False(float.IsInfinity(result), $"Got Infinity for degenerate shooter-on-rim case");
        Assert.True(MathF.Abs(result - 1f) < Epsilon,
            $"Degenerate case should return 1.0, got {result:F6}");
    }

    // ── Symmetry: left vs right deviation ───────────────────────────────

    [Fact]
    public void Multiplier_NinetyLeft_EqualNinetyRight()
    {
        // 90° left and 90° right of the target must give the same multiplier:
        // the penalty depends only on the unsigned angular distance.
        float targetYaw  = MathF.Atan2(Rim.X - ShooterSquaredUp.X, Rim.Z - ShooterSquaredUp.Z);
        float leftYaw    = NormaliseAngle(targetYaw + MathF.PI / 2f);
        float rightYaw   = NormaliseAngle(targetYaw - MathF.PI / 2f);

        float leftResult  = ShotFacing.Multiplier(leftYaw,  ShooterSquaredUp, Rim, K);
        float rightResult = ShotFacing.Multiplier(rightYaw, ShooterSquaredUp, Rim, K);

        Assert.True(MathF.Abs(leftResult - rightResult) < Epsilon,
            $"90°-left ({leftResult:F6}) and 90°-right ({rightResult:F6}) should be equal");
    }

    // ── Convention sanity: known position + heading pointing at target ────

    [Fact]
    public void Multiplier_KnownPositionHeadingAtTarget_ReturnsOne()
    {
        // Shooter at world (+3, 0, +4), rim at world origin.
        // Direction to rim: dx = 0-3 = -3, dz = 0-4 = -4.
        // targetYaw = Atan2(-3, -4).
        // If heading == targetYaw the angle is 0 → multiplier 1.
        var shooter   = new Vector3(3f, 0f, 4f);
        var target    = new Vector3(0f, 3.05f, 0f);
        float heading = MathF.Atan2(target.X - shooter.X, target.Z - shooter.Z);

        float result = ShotFacing.Multiplier(heading, shooter, target, K);

        Assert.True(MathF.Abs(result - 1f) < Epsilon,
            $"Heading pointing directly at target should give 1.0, got {result:F6}");
    }

    // ── Zero K — penalty disabled ─────────────────────────────────────────

    [Fact]
    public void Multiplier_KIsZero_AlwaysReturnsOne()
    {
        // When FacingScatterK = 0, the penalty is disabled regardless of angle.
        float targetYaw = MathF.Atan2(Rim.X - ShooterSquaredUp.X, Rim.Z - ShooterSquaredUp.Z);
        float backYaw   = NormaliseAngle(targetYaw + MathF.PI); // worst case angle

        float result = ShotFacing.Multiplier(backYaw, ShooterSquaredUp, Rim, facingScatterK: 0f);

        Assert.True(MathF.Abs(result - 1f) < Epsilon,
            $"K=0 at 180° should give 1.0, got {result:F6}");
    }

    // ── Contract floor: multiplier is never below 1, even for a bad K ─────

    [Fact]
    public void Multiplier_NegativeK_NeverBelowOne()
    {
        // The doc contract promises "always >= 1 and finite" (and ShotScatter
        // multiplies the scatter radius by this with no downstream clamp). A
        // misconfigured negative FacingScatterK must NOT invert the penalty into
        // an accuracy *bonus* (or a negative radius). Worst-case angle = 180°.
        float targetYaw = MathF.Atan2(Rim.X - ShooterSquaredUp.X, Rim.Z - ShooterSquaredUp.Z);
        float backYaw   = NormaliseAngle(targetYaw + MathF.PI);

        float result = ShotFacing.Multiplier(backYaw, ShooterSquaredUp, Rim, facingScatterK: -0.8f);

        Assert.True(result >= 1f,
            $"Negative K must be floored to >= 1 (no inverted penalty), got {result:F6}");
    }

    // ── Helper ───────────────────────────────────────────────────────────

    private static float NormaliseAngle(float a)
    {
        while (a >  MathF.PI) a -= 2f * MathF.PI;
        while (a < -MathF.PI) a += 2f * MathF.PI;
        return a;
    }
}
