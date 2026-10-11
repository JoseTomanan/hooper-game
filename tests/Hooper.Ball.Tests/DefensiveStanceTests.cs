using Godot;
using Hooper.Player;

namespace Hooper.Ball.Tests;

public class DefensiveStanceTests
{
    [Fact]
    public void ReplayRetainsHeldPostureAndSampledEligibilityAcrossPossessionChange()
    {
        var buffer = new PredictionBuffer();
        buffer.Record(Vector2.Left, postureHeld: true, stanceEligible: true);
        buffer.Record(Vector2.Right, postureHeld: false, stanceEligible: true);
        buffer.Record(Vector2.Left, postureHeld: true, stanceEligible: false);
        Assert.Equal(new[] { true, false, true }, buffer.ReplayInputs().Select(f => f.PostureHeld));
        Assert.Equal(new[] { true, true, false }, buffer.ReplayInputs().Select(f => f.StanceEligible));
    }

    [Fact]
    public void IdleStanceTurnsTowardAttackerAtBoundedRateWithoutPlanting()
    {
        var step = HeadingMath.StepDefensiveStance(0, HeadingMath.PivotState.None,
            Vector2.Right, 1.0 / 60, 360, 900, .95f, 90);
        Assert.InRange(step.NewYaw, .09f, .106f);
        Assert.False(step.Pivot.HasLatch);
        Assert.False(step.IsPivotingInPlace);
    }

    [Fact]
    public void StanceCompletesOwedPivotBeforeTurningTowardAttacker()
    {
        var pivot = new HeadingMath.PivotState(true, MathF.PI);
        float yaw = 0;
        int planted = 0;
        for (int i = 0; i < 20; i++)
        {
            var step = HeadingMath.StepDefensiveStance(yaw, pivot, Vector2.Right,
                1.0 / 60, 360, 900, .95f, 90);
            yaw = step.NewYaw;
            if (step.Pivot.HasLatch)
            {
                planted++;
                Assert.Equal(MathF.PI, step.Pivot.LatchedYaw);
                Assert.True(step.IsPivotingInPlace);
            }
            else
            {
                Assert.InRange(MathF.Abs(Mathf.AngleDifference(yaw, MathF.PI)), 0, .00001f);
                Assert.False(step.IsPivotingInPlace);
                Assert.True(planted > 0);
                return;
            }
            pivot = step.Pivot;
        }
        Assert.Fail("ordinary pivot never finished");
    }

    [Theory]
    [InlineData(1.2f)]
    [InlineData(-2.9f)]
    public void CoincidentAttackerRetainsHeading(float heading)
    {
        var step = HeadingMath.StepDefensiveStance(heading, HeadingMath.PivotState.None,
            Vector2.Zero, 1.0 / 60, 360, 900, .95f, 90);
        Assert.Equal(heading, step.NewYaw);
        Assert.False(step.Pivot.HasLatch);
    }

    [Fact]
    public void BehindAndWrappingTargetsNeverExceedPerTickCap()
    {
        float heading = 3.12f;
        foreach (var target in new[] { Vector2.Down, Vector2.Left, Vector2.Up, Vector2.Right })
        {
            var step = HeadingMath.StepDefensiveStance(heading, HeadingMath.PivotState.None,
                target, 1.0 / 60, 360, 900, .95f, 90);
            Assert.InRange(MathF.Abs(Mathf.AngleDifference(heading, step.NewYaw)), 0, MathF.PI / 30 + .000001f);
            Assert.False(step.IsPivotingInPlace);
            heading = step.NewYaw;
        }
    }
}
