using System;
using Hooper.Player;

namespace Hooper.Ball.Tests;

// Issue #201 — pinning SpinHeadingMath.ArcHeading, the pure function behind
// the spin's ADR-0010 sanctioned heading-arc exception (see the ADR-0010
// amendment, docs/adr/0010-authoritative-heading.md, and SpinHeadingMath's
// own class doc for why this function must be pure — no live input, no
// Godot Node — to stay bit-identical across the server and every
// predicting/observing role).
public class SpinHeadingMathTests
{
    private const int ActiveFrames = 6; // matches Spin.DefaultFrameData.ActiveFrames

    // ── Progress reaches EXACTLY 1.0 on the LAST Active tick ────────────────
    // frameInPhase is 0-based; the contract is progress = (frameInPhase+1)/
    // activeFrames, so the LAST tick (frameInPhase == activeFrames-1) must
    // land on EXACTLY 1.0 -> the full ~180 degree (pi radian) arc. This is the
    // same tick PlayerController's Spin branch gates the hand swap on, so the
    // rotation completing and the hand swapping must be the same event.
    [Fact]
    public void RightSpin_OnLastActiveTick_ReachesFullPiRotation()
    {
        float result = SpinHeadingMath.ArcHeading(
            entryHeading: 0f, direction: +1, frameInPhase: ActiveFrames - 1, activeFrames: ActiveFrames);

        Assert.Equal(MathF.PI, result, precision: 5);
    }

    [Fact]
    public void LeftSpin_OnLastActiveTick_ReachesFullNegativePiRotation()
    {
        float result = SpinHeadingMath.ArcHeading(
            entryHeading: 0f, direction: -1, frameInPhase: ActiveFrames - 1, activeFrames: ActiveFrames);

        Assert.Equal(-MathF.PI, result, precision: 5);
    }

    // Sanity check on an intermediate tick: progress must be a strict
    // fraction, not already saturated — guards against an off-by-one that
    // would silently deliver the full arc early.
    [Fact]
    public void FirstActiveTick_ReachesOnlyAFractionOfTheArc()
    {
        float result = SpinHeadingMath.ArcHeading(
            entryHeading: 0f, direction: +1, frameInPhase: 0, activeFrames: ActiveFrames);

        float expected = MathF.PI * (1f / ActiveFrames);
        Assert.Equal(expected, result, precision: 5);
        Assert.True(result < MathF.PI, "expected the first tick to reach only a fraction of the full arc.");
    }

    // ── Direction sign: right (>=0) -> +pi, left (<0) -> -pi ────────────────
    // An exact 0 resolves to the ">= 0" (right) branch — the same "zero
    // defaults to the family's existing sign convention" behavior documented
    // on ArcHeading's own `direction` parameter doc (mirrors Crossover's
    // flickSign==0 case, never specially guarded).
    [Fact]
    public void ExactZeroDirection_ResolvesToTheRightBranch()
    {
        float result = SpinHeadingMath.ArcHeading(
            entryHeading: 0f, direction: 0, frameInPhase: ActiveFrames - 1, activeFrames: ActiveFrames);

        Assert.Equal(MathF.PI, result, precision: 5);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(int.MaxValue)]
    public void AnyNonNegativeDirection_RotatesRight(int direction)
    {
        float result = SpinHeadingMath.ArcHeading(
            entryHeading: 0f, direction, frameInPhase: ActiveFrames - 1, activeFrames: ActiveFrames);

        Assert.Equal(MathF.PI, result, precision: 5);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-5)]
    [InlineData(int.MinValue)]
    public void AnyNegativeDirection_RotatesLeft(int direction)
    {
        float result = SpinHeadingMath.ArcHeading(
            entryHeading: 0f, direction, frameInPhase: ActiveFrames - 1, activeFrames: ActiveFrames);

        Assert.Equal(-MathF.PI, result, precision: 5);
    }

    // ── Output normalized to [-pi, pi] across wrap boundaries ───────────────
    // Entry heading already near +pi, rotating further right, must wrap
    // around through -pi rather than escaping the range. Entry = pi/2 (90
    // degrees) rotating a full +pi lands at 3*pi/2, which normalizes to
    // -pi/2.
    [Fact]
    public void WrapBoundary_RightSpinPastPositivePi_NormalizesIntoRange()
    {
        float entryHeading = MathF.PI / 2f; // 90 degrees
        float result = SpinHeadingMath.ArcHeading(
            entryHeading, direction: +1, frameInPhase: ActiveFrames - 1, activeFrames: ActiveFrames);

        // pi/2 + pi = 3pi/2 -> normalized to -pi/2
        Assert.Equal(-MathF.PI / 2f, result, precision: 4);
        Assert.InRange(result, -MathF.PI, MathF.PI);
    }

    [Fact]
    public void WrapBoundary_LeftSpinPastNegativePi_NormalizesIntoRange()
    {
        float entryHeading = -MathF.PI / 2f; // -90 degrees
        float result = SpinHeadingMath.ArcHeading(
            entryHeading, direction: -1, frameInPhase: ActiveFrames - 1, activeFrames: ActiveFrames);

        // -pi/2 - pi = -3pi/2 -> normalized to +pi/2
        Assert.Equal(MathF.PI / 2f, result, precision: 4);
        Assert.InRange(result, -MathF.PI, MathF.PI);
    }

    // Every intermediate tick's output must also stay in range, not just the
    // final one — a partial arc starting near the boundary is the case most
    // likely to escape [-pi, pi] if normalization were only applied at the end.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void EveryActiveTick_StaysNormalizedInRange(int frameInPhase)
    {
        float entryHeading = MathF.PI - 0.1f; // deliberately close to the +pi boundary
        float result = SpinHeadingMath.ArcHeading(
            entryHeading, direction: +1, frameInPhase, activeFrames: ActiveFrames);

        Assert.InRange(result, -MathF.PI, MathF.PI);
        Assert.False(float.IsNaN(result));
        Assert.False(float.IsInfinity(result));
    }

    // ── activeFrames <= 0 defensive guard ───────────────────────────────────
    // MoveFrameData's own constructor already rejects activeFrames < 1 in
    // every real committed move, so this branch is unreachable through any
    // shipped path today — but ArcHeading is an independently unit-tested
    // pure function that must not propagate a divide-by-zero (NaN/Infinity)
    // if that invariant is ever violated by a future caller. It must degrade
    // to the normalized entry heading rather than corrupt state.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void NonPositiveActiveFrames_ReturnsNormalizedEntryHeading_NoNaNOrInfinity(int activeFrames)
    {
        float entryHeading = 1.23f;
        float result = SpinHeadingMath.ArcHeading(
            entryHeading, direction: +1, frameInPhase: 0, activeFrames);

        Assert.Equal(entryHeading, result, precision: 5);
        Assert.False(float.IsNaN(result));
        Assert.False(float.IsInfinity(result));
    }

    // Same guard, but with an out-of-range entry heading, proving the
    // fallback path itself still normalizes rather than passing the raw
    // value straight through.
    [Fact]
    public void NonPositiveActiveFrames_StillNormalizesAnOutOfRangeEntryHeading()
    {
        float entryHeading = MathF.PI * 1.5f; // 270 degrees, outside [-pi, pi]
        float result = SpinHeadingMath.ArcHeading(
            entryHeading, direction: +1, frameInPhase: 0, activeFrames: 0);

        Assert.InRange(result, -MathF.PI, MathF.PI);
        Assert.False(float.IsNaN(result));
        Assert.False(float.IsInfinity(result));
    }
}
