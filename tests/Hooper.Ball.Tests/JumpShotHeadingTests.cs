using System;
using Godot;
using Hooper.Player;
using Xunit;
using Hooper.Moves;
using System.Linq;

namespace Hooper.Ball.Tests;

public class JumpShotHeadingTests
{
    [Theory]
    [InlineData(0f, 3.1415927f)]
    [InlineData(0f, -3.1415927f)]
    [InlineData(3.1415927f, 0f)]
    [InlineData(-3.1415927f, 0f)]
    public void FixedYaw_EquivalentHalfTurnsChoosePositiveBoundedStep(float current, float target)
    {
        float actual = HeadingMath.RotateTowardFixedYaw(current, target, 1.0 / 60, 900, .95f);
        float step = MathF.IEEERemainder(actual - current, 2 * MathF.PI);
        Assert.InRange(step, .24870f, .24872f); // 900 * .95 / 60 degrees, in radians
    }

    [Fact]
    public void ConfirmOlderShot_RepairsHistoricalTargetWithoutRetargetingNewerShot()
    {
        var buffer = new PredictionBuffer();
        buffer.Record(Vector2.Zero, shotPhase: MovePhase.Startup, shotTarget: .1f, shotToken: 1);
        buffer.Record(Vector2.Zero, shotPhase: MovePhase.Active, shotTarget: .1f, shotToken: 1);
        buffer.Record(Vector2.Zero, shotPhase: MovePhase.Startup, shotTarget: -.5f, shotToken: 3);
        buffer.ConfirmShotTarget(1, .8f);
        Assert.Equal(new[] { .8f, .8f, -.5f }, buffer.ReplayInputs().Select(f => f.ShotTarget));
    }

    [Fact]
    public void RejectShot_RemovesOnlyRejectedInstanceTurnAcrossPhases()
    {
        var buffer = new PredictionBuffer();
        buffer.Record(Vector2.Zero, shotPhase: MovePhase.Startup, shotTarget: .1f, shotToken: 1);
        buffer.Record(Vector2.Zero, shotPhase: MovePhase.Recovery, shotTarget: .1f, shotToken: 1);
        buffer.Record(Vector2.Zero, shotPhase: MovePhase.Startup, shotTarget: -.5f, shotToken: 3);
        buffer.RejectShot(1);
        var frames = buffer.ReplayInputs().ToArray();
        Assert.All(frames.Take(2), frame => { Assert.Equal(MovePhase.Inactive, frame.ShotPhase); Assert.Equal(0, frame.ShotToken); Assert.Equal(0, frame.ShotTarget); });
        Assert.Equal(3, frames[2].ShotToken);
        Assert.Equal(MovePhase.Startup, frames[2].ShotPhase);
    }
}
