using Godot;
using Hooper.Ball;
using Xunit;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for DefensiveKnockDirection.SafeHorizontal — the shared
/// degenerate-safe horizontal-direction helper hoisted out of the steal knock
/// and block swat direction math (issue #216 original body row 4). Pure C#,
/// no Node inheritance, no engine singletons.
///
/// ── Test naming convention ───────────────────────────────────────────────
/// [Subject]_[Scenario]_[ExpectedOutcome]
/// </summary>
public class DefensiveKnockDirectionTests
{
    [Fact]
    public void SafeHorizontal_NonDegenerateXZDelta_ReturnsNormalizedHorizontalDirection()
    {
        var delta = new Vector3(3f, 5f, 4f); // Y should be ignored/zeroed

        Vector3 result = DefensiveKnockDirection.SafeHorizontal(delta);

        // XZ magnitude is 5 (3-4-5 triangle); normalized XZ is (0.6, 0, 0.8).
        Assert.Equal(0.6f, result.X, 3);
        Assert.Equal(0f, result.Y, 3);
        Assert.Equal(0.8f, result.Z, 3);
        Assert.Equal(1f, result.Length(), 3);
    }

    [Fact]
    public void SafeHorizontal_ExactlyAtOrigin_ReturnsZero()
    {
        Vector3 result = DefensiveKnockDirection.SafeHorizontal(Vector3.Zero);

        Assert.Equal(Vector3.Zero, result);
    }

    [Fact]
    public void SafeHorizontal_TinyDeltaBelowThreshold_ReturnsZero()
    {
        // LengthSquared = 0.00000002, well under the 0.0001f threshold —
        // this is the degenerate case (holder/defender/rim XZ effectively
        // coincident), not a real direction to normalize.
        var delta = new Vector3(0.0001f, 0f, 0.0001f);

        Vector3 result = DefensiveKnockDirection.SafeHorizontal(delta);

        Assert.Equal(Vector3.Zero, result);
    }

    [Fact]
    public void SafeHorizontal_PureYDelta_IsDegenerateInXZ_ReturnsZero()
    {
        // A delta that is ENTIRELY vertical (e.g. ball and rim share the same
        // XZ but differ in height) has a zero horizontal projection — even
        // though the raw 3-D vector is far from the origin.
        var delta = new Vector3(0f, 10f, 0f);

        Vector3 result = DefensiveKnockDirection.SafeHorizontal(delta);

        Assert.Equal(Vector3.Zero, result);
    }

    [Fact]
    public void SafeHorizontal_ResultAlwaysHasZeroY()
    {
        var delta = new Vector3(1f, 999f, 1f);

        Vector3 result = DefensiveKnockDirection.SafeHorizontal(delta);

        Assert.Equal(0f, result.Y);
    }

    [Fact]
    public void SafeHorizontal_AtExactThresholdBoundary_IsExclusive()
    {
        // LengthSquared exactly 0.0001f (X = 0.01, Z = 0) must NOT normalize —
        // the threshold check is a strict ">", matching RimBackboard's own
        // exclusive-boundary convention elsewhere in this codebase.
        var delta = new Vector3(0.01f, 0f, 0f);

        Vector3 result = DefensiveKnockDirection.SafeHorizontal(delta);

        Assert.Equal(Vector3.Zero, result);
    }
}
