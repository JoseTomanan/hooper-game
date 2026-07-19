using System;
using Godot;
using Hooper.Player;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for FadeawayTriggerResolver — the pure legibility trigger
/// deciding whether a JumpShot release counts as "fadeaway/off-balance"
/// (issue #243, parent #185's build half). Headless (ADR-0004): pure
/// deterministic function, no engine state.
///
/// Mirrors ShotFacingTests' rim/shooter fixture and angle-construction style
/// so the two stay obviously in sync — this resolver reuses ShotFacing's own
/// angle computation and threshold constant, never a second one.
///
/// ── Test naming convention ───────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// One logical assertion per test.
/// </summary>
public class FadeawayTriggerResolverTests
{
    private static readonly Vector3 Rim = new(0f, 3.05f, 0f);
    private static readonly Vector3 ShooterSquaredUp = new(0f, 0f, 5f);

    [Fact]
    public void IsFadeaway_SquaredUp_ReturnsFalse()
    {
        float targetYaw = MathF.Atan2(Rim.X - ShooterSquaredUp.X, Rim.Z - ShooterSquaredUp.Z);

        bool result = FadeawayTriggerResolver.IsFadeaway(targetYaw, ShooterSquaredUp, Rim);

        Assert.False(result, "A squared-up release must NOT trigger the fadeaway anim.");
    }

    [Fact]
    public void IsFadeaway_BackToBasket_ReturnsTrue()
    {
        float targetYaw = MathF.Atan2(Rim.X - ShooterSquaredUp.X, Rim.Z - ShooterSquaredUp.Z);
        float backYaw   = NormaliseAngle(targetYaw + MathF.PI);

        bool result = FadeawayTriggerResolver.IsFadeaway(backYaw, ShooterSquaredUp, Rim);

        Assert.True(result, "A back-to-basket (180°) release must trigger the fadeaway anim.");
    }

    [Fact]
    public void IsFadeaway_ExactlyAtThreshold_ReturnsTrue()
    {
        // 90° exactly equals ShotFacing.MateriallyDivergentAngleRadians — the
        // boundary is inclusive (>=), matching DefensiveResolution's own
        // half-open-interval convention of being explicit about edges.
        float targetYaw   = MathF.Atan2(Rim.X - ShooterSquaredUp.X, Rim.Z - ShooterSquaredUp.Z);
        float ninetyYaw   = NormaliseAngle(targetYaw + ShotFacing.MateriallyDivergentAngleRadians);

        bool result = FadeawayTriggerResolver.IsFadeaway(ninetyYaw, ShooterSquaredUp, Rim);

        Assert.True(result, "Exactly at the 90° threshold should already count as fadeaway.");
    }

    [Fact]
    public void IsFadeaway_JustBelowThreshold_ReturnsFalse()
    {
        float targetYaw = MathF.Atan2(Rim.X - ShooterSquaredUp.X, Rim.Z - ShooterSquaredUp.Z);
        float justBelow = NormaliseAngle(targetYaw + ShotFacing.MateriallyDivergentAngleRadians - 0.05f);

        bool result = FadeawayTriggerResolver.IsFadeaway(justBelow, ShooterSquaredUp, Rim);

        Assert.False(result, "Just below the 90° threshold must still read as squared-up enough.");
    }

    [Fact]
    public void IsFadeaway_ShooterOnRim_ReturnsFalse_NoNaN()
    {
        // Degenerate case: shooter's XZ coincides with the rim's — ShotFacing.
        // AngleFromTarget returns 0 here, which must never trigger fadeaway.
        var onRim = new Vector3(Rim.X, 0f, Rim.Z);

        bool result = FadeawayTriggerResolver.IsFadeaway(0f, onRim, Rim);

        Assert.False(result, "Degenerate on-rim case must not trigger fadeaway.");
    }

    private static float NormaliseAngle(float a)
    {
        while (a >  MathF.PI) a -= 2f * MathF.PI;
        while (a < -MathF.PI) a += 2f * MathF.PI;
        return a;
    }
}
