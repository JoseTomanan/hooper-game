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
    public void BurstWorldDir_HeadingZeroFlickRight_ReturnsNegXZeroZ()
    {
        // heading 0 → forward is +Z; body-right = cross(forward, up) = -X
        // (the same convention BallController.HandRight renders the ball with).
        // Expected burst: (-cos0, sin0) * +1 = (-1, 0). Sign hitl-verified
        // in-editor 2026-07-04 (#191): the old (+cos h, -sin h) was mirrored —
        // ball swapped right while the body burst left.
        Vector2 result = HandStateResolver.BurstWorldDir(heading: 0f, flickSign: +1);
        AssertApprox(-1f, result.X, "X");
        AssertApprox(0f,  result.Y, "Z");
    }

    [Fact]
    public void BurstWorldDir_HeadingZeroFlickLeft_ReturnsPosXZeroZ()
    {
        // Flip of previous: (-1, 0) * -1 = (1, 0).
        Vector2 result = HandStateResolver.BurstWorldDir(heading: 0f, flickSign: -1);
        AssertApprox(1f, result.X, "X");
        AssertApprox(0f, result.Y, "Z");
    }

    [Fact]
    public void BurstWorldDir_HeadingPiFlickRight_ReturnsPosXZeroZ()
    {
        // At heading π the body has turned 180°.
        // -cos(π) = 1, sin(π) ≈ 0, so body-right becomes (+1, 0).
        // A +1 (body-right) flick now bursts in world +X: the body's right
        // reversed as the player turned around — exactly the ADR-0003 intent.
        Vector2 result = HandStateResolver.BurstWorldDir(heading: MathF.PI, flickSign: +1);
        AssertApprox(1f, result.X, "X");
        AssertApprox(0f, result.Y, "Z");
    }

    [Fact]
    public void BurstWorldDir_HeadingHalfPiFlickRight_ReturnsZeroXPosZ()
    {
        // At heading π/2 the body faces +X.
        // -cos(π/2) ≈ 0, sin(π/2) = 1, body-right = (0, +1).
        Vector2 result = HandStateResolver.BurstWorldDir(heading: MathF.PI / 2f, flickSign: +1);
        AssertApprox(0f, result.X, "X");
        AssertApprox(1f, result.Y, "Z");
    }

    [Theory]
    // Pins BurstWorldDir to the SAME body-right convention the ball-in-hand
    // display uses (#191): BallController.HandRight(forward) = cross(forward,
    // up) = (-forward.Z, forward.X), with forward = HeadingMath.Forward. If
    // these two ever disagree again, the crossover bursts away from the hand
    // the ball visibly swaps into — the exact bug this fixed.
    [InlineData(0f)]
    [InlineData(0.7f)]
    [InlineData(MathF.PI / 2f)]
    [InlineData(-2.3f)]
    public void BurstWorldDir_FlickRight_MatchesBallRenderRightVector(float heading)
    {
        Vector2 forward = HeadingMath.Forward(heading);
        Vector2 bodyRight = new(-forward.Y, forward.X); // cross(forward, up) on XZ

        Vector2 result = HandStateResolver.BurstWorldDir(heading, flickSign: +1);

        AssertApprox(bodyRight.X, result.X, $"X @ heading {heading}");
        AssertApprox(bodyRight.Y, result.Y, $"Z @ heading {heading}");
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

    // ── TargetHandFromAim — issue #254 (steal aim→hand facing transform) ─────

    [Fact]
    public void TargetHandFromAim_ZeroRelativeHeading_MatchesNaiveMapping()
    {
        // Side-by-side / trailing defense: defender and holder face the SAME
        // way. This must reproduce the OLD naive `aim.X > 0 ? Right : Left`
        // mapping exactly — the control that proves the fix does NOT blanket-
        // invert everywhere.
        Assert.Equal(HandSide.Right,
            HandStateResolver.TargetHandFromAim(aimSign: +1f, defenderHeading: 0f, holderHeading: 0f));
        Assert.Equal(HandSide.Left,
            HandStateResolver.TargetHandFromAim(aimSign: -1f, defenderHeading: 0f, holderHeading: 0f));

        // Same-heading control still holds away from zero (e.g. both facing
        // the same arbitrary direction, not just the spawn default).
        Assert.Equal(HandSide.Right,
            HandStateResolver.TargetHandFromAim(aimSign: +1f, defenderHeading: 1.2f, holderHeading: 1.2f));
    }

    [Fact]
    public void TargetHandFromAim_180DegreesRelativeHeading_FullyInverts()
    {
        // Face-to-face stance (the #254 repro): the mapping must invert
        // relative to the naive same-heading case.
        Assert.Equal(HandSide.Left,
            HandStateResolver.TargetHandFromAim(aimSign: +1f, defenderHeading: MathF.PI, holderHeading: 0f));
        Assert.Equal(HandSide.Right,
            HandStateResolver.TargetHandFromAim(aimSign: -1f, defenderHeading: MathF.PI, holderHeading: 0f));
    }

    [Fact]
    public void TargetHandFromAim_90DegreesRelativeHeading_TiesTowardLeft()
    {
        // Perpendicular relative heading is the genuine ambiguity point
        // (cos(90°) == 0) — the tie-break defaults to Left, same convention
        // the old code used for any non-positive product.
        Assert.Equal(HandSide.Left,
            HandStateResolver.TargetHandFromAim(aimSign: +1f, defenderHeading: MathF.PI / 2f, holderHeading: 0f));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(MathF.PI)]
    [InlineData(-2.1f)]
    public void TargetHandFromAim_ResultIsContinuousInRelativeHeading_NotABlanketFlip(float holderHeading)
    {
        // Sweeping the relative heading through a full circle must produce
        // BOTH Left and Right at different points — a "just always invert"
        // implementation would be indistinguishable from the correct one at
        // 180° alone, but would fail this sweep (it can never agree with the
        // 0°-relative control case, which a blanket flip always contradicts).
        HandSide atZeroRelative = HandStateResolver.TargetHandFromAim(+1f, holderHeading, holderHeading);
        HandSide atOppositeRelative = HandStateResolver.TargetHandFromAim(+1f, holderHeading + MathF.PI, holderHeading);

        Assert.Equal(HandSide.Right, atZeroRelative);
        Assert.Equal(HandSide.Left, atOppositeRelative);
        Assert.NotEqual(atZeroRelative, atOppositeRelative);
    }
}
