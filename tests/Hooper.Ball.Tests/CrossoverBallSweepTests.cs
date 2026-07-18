namespace Hooper.Ball.Tests;

/// <summary>
/// Pins CrossoverBallSweep (#195): the pure lateral smoothstep + single-arch
/// vertical dip curve BallController feeds ticksSinceSwap/durationTicks into
/// to make the crossover's in-hand ball transit honest (no teleport).
/// </summary>
public class CrossoverBallSweepTests
{
    [Fact]
    public void AtStart_LateralFactorIsTheOldSide()
    {
        var (lateral, _) = CrossoverBallSweep.Offset(t: 0f, fromSign: -1f, toSign: 1f);

        Assert.Equal(-1f, lateral, precision: 5);
    }

    [Fact]
    public void AtEnd_LateralFactorIsTheNewSide()
    {
        var (lateral, _) = CrossoverBallSweep.Offset(t: 1f, fromSign: -1f, toSign: 1f);

        Assert.Equal(1f, lateral, precision: 5);
    }

    [Fact]
    public void AtStart_NoDip()
    {
        var (_, dip) = CrossoverBallSweep.Offset(t: 0f, fromSign: -1f, toSign: 1f);

        Assert.Equal(0f, dip, precision: 5);
    }

    [Fact]
    public void AtEnd_NoDip()
    {
        var (_, dip) = CrossoverBallSweep.Offset(t: 1f, fromSign: -1f, toSign: 1f);

        Assert.Equal(0f, dip, precision: 5);
    }

    [Fact]
    public void AtMidpoint_LateralFactorIsTheCenterline()
    {
        // Halfway through an old-side(-1) to new-side(+1) sweep, the
        // centerline is 0 — the mid-body region the harness asserts the
        // ball's authoritative XZ actually occupies (no teleport).
        var (lateral, _) = CrossoverBallSweep.Offset(t: 0.5f, fromSign: -1f, toSign: 1f);

        Assert.Equal(0f, lateral, precision: 5);
    }

    [Fact]
    public void AtMidpoint_DipIsMaximal()
    {
        var (_, dip) = CrossoverBallSweep.Offset(t: 0.5f, fromSign: -1f, toSign: 1f);

        Assert.Equal(1f, dip, precision: 5);
    }

    [Theory]
    [InlineData(0.1f)]
    [InlineData(0.3f)]
    [InlineData(0.6f)]
    [InlineData(0.9f)]
    public void Dip_NeverExceedsTheMidpointMaximum(float t)
    {
        var (_, midDip) = CrossoverBallSweep.Offset(t: 0.5f, fromSign: -1f, toSign: 1f);
        var (_, dip) = CrossoverBallSweep.Offset(t, fromSign: -1f, toSign: 1f);

        Assert.True(dip <= midDip, $"dip at t={t} ({dip}) exceeded the t=0.5 maximum ({midDip}).");
    }

    [Fact]
    public void LateralTravel_IsMonotonicAcrossTheSweep()
    {
        // Sampling left-to-right (old side -1 -> new side +1), the lateral
        // factor must never regress — a real crossover doesn't wobble back
        // toward the hand it just left mid-transit.
        float previous = float.NegativeInfinity;
        for (float t = 0f; t <= 1f; t += 0.05f)
        {
            var (lateral, _) = CrossoverBallSweep.Offset(t, fromSign: -1f, toSign: 1f);
            Assert.True(lateral >= previous - 1e-6f, $"lateral factor regressed at t={t}: {lateral} < {previous}.");
            previous = lateral;
        }
    }

    [Fact]
    public void LateralTravel_IsMonotonicInReverseDirectionToo()
    {
        // The reverse crossover (new side -1 -> old side +... well, +1 -> -1)
        // must also be monotonic — this is the shape a re-cross mid-sweep
        // produces (rule 3: restart toward the OTHER side from wherever the
        // ball currently sits).
        float previous = float.PositiveInfinity;
        for (float t = 0f; t <= 1f; t += 0.05f)
        {
            var (lateral, _) = CrossoverBallSweep.Offset(t, fromSign: 1f, toSign: -1f);
            Assert.True(lateral <= previous + 1e-6f, $"lateral factor rose at t={t}: {lateral} > {previous}.");
            previous = lateral;
        }
    }

    [Theory]
    [InlineData(0.25f, 0.15625f)]
    [InlineData(0.75f, 0.84375f)]
    public void LateralFactor_MatchesTheSmoothstepFraction_NotLinear(float t, float expectedEasedFraction)
    {
        // Pins the mandated smoothstep (3t^2 - 2t^3) against the specific
        // fraction it produces at these two ts — a mutant that swapped in a
        // linear ease (eased = t, giving 0.25/0.75 here instead) would still
        // pass every other test in this file but fails this one.
        var (lateral, _) = CrossoverBallSweep.Offset(t, fromSign: -1f, toSign: 1f);

        float expectedLateral = -1f + (1f - (-1f)) * expectedEasedFraction;
        Assert.Equal(expectedLateral, lateral, precision: 5);

        // Sanity: the expected fraction really is the non-linear smoothstep
        // value, distinct from a linear ease at the same t.
        Assert.NotEqual(t, expectedEasedFraction, precision: 5);
    }

    [Fact]
    public void TBelowZero_ClampsToTheStartEndpoint()
    {
        var atNegative = CrossoverBallSweep.Offset(t: -0.5f, fromSign: -1f, toSign: 1f);
        var atZero = CrossoverBallSweep.Offset(t: 0f, fromSign: -1f, toSign: 1f);

        Assert.Equal(atZero.LateralFactor, atNegative.LateralFactor, precision: 5);
        Assert.Equal(atZero.VerticalDip, atNegative.VerticalDip, precision: 5);
    }

    [Fact]
    public void TAboveOne_ClampsToTheEndEndpoint()
    {
        var atTooFar = CrossoverBallSweep.Offset(t: 1.5f, fromSign: -1f, toSign: 1f);
        var atOne = CrossoverBallSweep.Offset(t: 1f, fromSign: -1f, toSign: 1f);

        Assert.Equal(atOne.LateralFactor, atTooFar.LateralFactor, precision: 5);
        Assert.Equal(atOne.VerticalDip, atTooFar.VerticalDip, precision: 5);
    }

    [Fact]
    public void ReCross_RestartsFromTheCurrentInterpolatedPosition_NotTheOldSide()
    {
        // Rule 3 (issue #195): a re-cross mid-sweep restarts from the ball's
        // CURRENT interpolated position, never jumping back to the old side.
        // Model that here as the caller would: sample the in-progress sweep's
        // lateral factor, then start a NEW sweep with that value as fromSign.
        var (midway, _) = CrossoverBallSweep.Offset(t: 0.4f, fromSign: -1f, toSign: 1f);

        var (restarted, _) = CrossoverBallSweep.Offset(t: 0f, fromSign: midway, toSign: -1f);

        Assert.Equal(midway, restarted, precision: 5);
        Assert.NotEqual(-1f, restarted); // must NOT have snapped back to the old side
    }

    // #211 code-review fix: CrossoverBallSweep.ForwardOffset was previously a
    // private BallController method (SweepForwardOffset) with zero xUnit
    // coverage, and survived a sign-flip mutation as a result. These cases
    // pin the formula directly: Crossover's in-front sweep never touches the
    // forward axis (InFront is baseline-only, regardless of dip), and
    // BehindTheBack's behind-body sweep pulls the ball back along the dip
    // curve, going negative (genuinely behind the holder) once behindDepth
    // exceeds baseline at the dip's peak. #199 added BallSweepPath.ThroughLegs
    // (BetweenTheLegs) — its forward offset behaves identically to InFront
    // (the through-the-legs distinguishing depth is a separate VERTICAL dip
    // BallController applies, not a forward pull-back), pinned below too.
    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void ForwardOffset_InFrontSweep_ReturnsBaselineRegardlessOfDip(float verticalDip)
    {
        float result = CrossoverBallSweep.ForwardOffset(baseline: 0.5f, verticalDip, behindDepth: 0.7f, shieldDepth: 0.45f, path: BallSweepPath.InFront);

        Assert.Equal(0.5f, result, precision: 5);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void ForwardOffset_ThroughLegsSweep_ReturnsBaselineRegardlessOfDip(float verticalDip)
    {
        // BetweenTheLegs (#199): the forward axis is untouched, exactly like
        // InFront — the distinguishing depth is a separate vertical dip term
        // the caller (BallController) applies on top, not this method.
        float result = CrossoverBallSweep.ForwardOffset(baseline: 0.5f, verticalDip, behindDepth: 0.7f, shieldDepth: 0.45f, path: BallSweepPath.ThroughLegs);

        Assert.Equal(0.5f, result, precision: 5);
    }

    [Fact]
    public void ForwardOffset_BehindBodySweep_AtPeakDip_GoesNegative()
    {
        // Peak dip (t=0.5, verticalDip=1.0): baseline 0.5 - depth 0.7 = -0.2,
        // genuinely behind the holder's centerline — the "shielded, away
        // from the defender" transit issue #194 calls for.
        float result = CrossoverBallSweep.ForwardOffset(baseline: 0.5f, verticalDip: 1.0f, behindDepth: 0.7f, shieldDepth: 0.45f, path: BallSweepPath.BehindBody);

        Assert.Equal(-0.2f, result, precision: 5);
    }

    [Fact]
    public void ForwardOffset_BehindBodySweep_AtSweepEndpoints_ReturnsBaseline()
    {
        // At either endpoint of the transit (verticalDip=0), the sweep
        // hasn't pulled back at all yet — the ball sits at the plain
        // baseline in front of the holder, same as Crossover.
        float result = CrossoverBallSweep.ForwardOffset(baseline: 0.5f, verticalDip: 0f, behindDepth: 0.7f, shieldDepth: 0.45f, path: BallSweepPath.BehindBody);

        Assert.Equal(0.5f, result, precision: 5);
    }

    // ── BodyShield (#201, ADR-0010/ADR-0014 tier-1 real-ball citation — see
    // BallSweepPath.BodyShield's own doc) ────────────────────────────────────
    [Fact]
    public void ForwardOffset_BodyShieldSweep_AtPeakDip_PullsTightButStaysPositive()
    {
        // Peak dip (t=0.5, verticalDip=1.0): baseline 0.5 - shieldDepth 0.45
        // = 0.05 — pulled in tight against the body's centerline, but
        // deliberately NOT negative (contrast BehindBody's -0.2 at the same
        // dip depth): a spin's body IS the shield, so the ball tucks close
        // rather than swinging fully behind.
        float result = CrossoverBallSweep.ForwardOffset(baseline: 0.5f, verticalDip: 1.0f, behindDepth: 0.7f, shieldDepth: 0.45f, path: BallSweepPath.BodyShield);

        Assert.Equal(0.05f, result, precision: 5);
        Assert.True(result > 0f, "BodyShield's forward offset must stay positive — the body itself is the shield, not a behind-the-back pull.");
    }

    [Fact]
    public void ForwardOffset_BodyShieldSweep_AtSweepEndpoints_ReturnsBaseline()
    {
        float result = CrossoverBallSweep.ForwardOffset(baseline: 0.5f, verticalDip: 0f, behindDepth: 0.7f, shieldDepth: 0.45f, path: BallSweepPath.BodyShield);

        Assert.Equal(0.5f, result, precision: 5);
    }

    [Fact]
    public void ForwardOffset_BodyShieldSweep_PullsInLessThanBehindBodySweep()
    {
        // At identical peak dip, BodyShield must pull in LESS aggressively
        // than BehindBody — tucked tight to the centerline, not swung all
        // the way behind it.
        float shieldResult = CrossoverBallSweep.ForwardOffset(baseline: 0.5f, verticalDip: 1.0f, behindDepth: 0.7f, shieldDepth: 0.45f, path: BallSweepPath.BodyShield);
        float behindResult = CrossoverBallSweep.ForwardOffset(baseline: 0.5f, verticalDip: 1.0f, behindDepth: 0.7f, shieldDepth: 0.45f, path: BallSweepPath.BehindBody);

        Assert.True(shieldResult > behindResult, $"expected BodyShield ({shieldResult}) to pull in LESS than BehindBody ({behindResult}).");
    }
}
