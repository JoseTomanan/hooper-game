using Godot;
using Hooper.Player;

namespace Hooper.Ball.Tests;

public class HandStateResolverTests
{
    // ── Opposite ─────────────────────────────────────────────────────────────

    [Fact]
    public void Opposite_GivenLeft_ReturnsRight()
    {
        Assert.Equal(HandSide.Right, HandStateResolver.Opposite(HandSide.Left));
    }

    [Fact]
    public void Opposite_GivenRight_ReturnsLeft()
    {
        Assert.Equal(HandSide.Left, HandStateResolver.Opposite(HandSide.Right));
    }

    // ── EmptyHandSign ─────────────────────────────────────────────────────────

    [Fact]
    public void EmptyHandSign_BallInLeft_ReturnsPlusOne()
    {
        // Ball is in the left hand; the RIGHT hand is empty.
        Assert.Equal(+1, HandStateResolver.EmptyHandSign(HandSide.Left));
    }

    [Fact]
    public void EmptyHandSign_BallInRight_ReturnsMinusOne()
    {
        // Ball is in the right hand; the LEFT hand is empty.
        Assert.Equal(-1, HandStateResolver.EmptyHandSign(HandSide.Right));
    }

    // ── IsCrossover — truth table ─────────────────────────────────────────────

    [Fact]
    public void IsCrossover_LeftBallFlickRight_ReturnsTrue()
    {
        // Flick +1 (right), empty hand is on the right → crossover.
        Assert.True(HandStateResolver.IsCrossover(HandSide.Left, +1));
    }

    [Fact]
    public void IsCrossover_LeftBallFlickLeft_ReturnsFalse()
    {
        // Flick -1 (left), ball hand is on the left → hesitation.
        Assert.False(HandStateResolver.IsCrossover(HandSide.Left, -1));
    }

    [Fact]
    public void IsCrossover_LeftBallFlickZero_ReturnsFalse()
    {
        // No gesture → no move.
        Assert.False(HandStateResolver.IsCrossover(HandSide.Left, 0));
    }

    [Fact]
    public void IsCrossover_RightBallFlickLeft_ReturnsTrue()
    {
        // Flick -1 (left), empty hand is on the left → crossover.
        Assert.True(HandStateResolver.IsCrossover(HandSide.Right, -1));
    }

    [Fact]
    public void IsCrossover_RightBallFlickRight_ReturnsFalse()
    {
        // Flick +1 (right), ball hand is on the right → hesitation.
        Assert.False(HandStateResolver.IsCrossover(HandSide.Right, +1));
    }

    [Fact]
    public void IsCrossover_RightBallFlickZero_ReturnsFalse()
    {
        // No gesture → no move.
        Assert.False(HandStateResolver.IsCrossover(HandSide.Right, 0));
    }

    // ── IsCrossover — large-magnitude flick behaves as its sign ──────────────

    [Fact]
    public void IsCrossover_LeftBallLargePosFlick_ReturnsTrueLikeSignOne()
    {
        // +5 is the same decision as +1 — only sign matters.
        Assert.True(HandStateResolver.IsCrossover(HandSide.Left, +5));
    }

    [Fact]
    public void IsCrossover_RightBallLargeNegFlick_ReturnsTrueLikeSignMinusOne()
    {
        // -3 is the same decision as -1.
        Assert.True(HandStateResolver.IsCrossover(HandSide.Right, -3));
    }

    // ── BurstWorldDir ─────────────────────────────────────────────────────────

    private const float FloatTolerance = 1e-5f;

    private static void AssertApprox(float expected, float actual, string label = "")
    {
        Assert.True(
            MathF.Abs(actual - expected) < FloatTolerance,
            $"{label}: expected {expected}, got {actual}");
    }

    [Fact]
    public void BurstWorldDir_HeadingZeroFlickRight_ReturnsPosXZeroZ()
    {
        // heading 0 → forward is +Z, right is +X.
        // Expected burst: (cos0, -sin0) * +1 = (1, 0).
        Vector2 result = HandStateResolver.BurstWorldDir(heading: 0f, flickSign: +1);
        AssertApprox(1f, result.X, "X");
        AssertApprox(0f, result.Y, "Z");
    }

    [Fact]
    public void BurstWorldDir_HeadingZeroFlickLeft_ReturnsNegXZeroZ()
    {
        // Flip of previous: (1, 0) * -1 = (-1, 0).
        Vector2 result = HandStateResolver.BurstWorldDir(heading: 0f, flickSign: -1);
        AssertApprox(-1f, result.X, "X");
        AssertApprox(0f,  result.Y, "Z");
    }

    [Fact]
    public void BurstWorldDir_HeadingPiFlickRight_ReturnsNegXZeroZ()
    {
        // At heading π the body has turned 180°.
        // cos(π) = -1, sin(π) ≈ 0, so right vector becomes (-1, 0).
        // A +1 (body-right) flick now bursts in world -X: the body's right
        // reversed as the player turned around — exactly the ADR-0003 intent.
        Vector2 result = HandStateResolver.BurstWorldDir(heading: MathF.PI, flickSign: +1);
        AssertApprox(-1f, result.X, "X");
        AssertApprox(0f,  result.Y, "Z");
    }

    [Fact]
    public void BurstWorldDir_HeadingHalfPiFlickRight_ReturnsZeroXNegZ()
    {
        // At heading π/2 the body faces +X.
        // cos(π/2) ≈ 0, sin(π/2) = 1, right vector = (0, -1).
        Vector2 result = HandStateResolver.BurstWorldDir(heading: MathF.PI / 2f, flickSign: +1);
        AssertApprox(0f,  result.X, "X");
        AssertApprox(-1f, result.Y, "Z");
    }

    [Fact]
    public void BurstWorldDir_ArbitraryHeadingNonzeroFlick_ResultIsUnitLength()
    {
        // The formula (cos h, -sin h) is a unit vector for any heading h.
        // Multiplying by flickSign ±1 preserves unit length.
        Vector2 result = HandStateResolver.BurstWorldDir(heading: 0.7f, flickSign: +1);
        float length   = result.Length();
        AssertApprox(1f, length, "length");
    }
}
