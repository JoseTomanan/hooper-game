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
}
