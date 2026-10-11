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
}
