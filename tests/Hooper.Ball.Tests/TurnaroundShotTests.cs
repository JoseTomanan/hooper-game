using Hooper.Moves;

namespace Hooper.Ball.Tests;

public class TurnaroundShotTests
{
    [Fact]
    public void Begin_CapturesFixedHeadingAndUnfeintableTurnAndShotTimeline()
    {
        var shot = new TurnaroundShot(.71f);
        var machine = new CommittedMoveMachine();
        Assert.True(machine.Begin(shot));
        Assert.Equal("turnaround", shot.Id);
        Assert.Equal(.71f, shot.TargetHeading);
        for (int tick = 0; tick < 48; tick++)
        {
            Assert.Equal(MovePhase.Startup, machine.Phase);
            Assert.False(machine.Feint());
            machine.Tick();
        }
        Assert.Equal(MovePhase.Active, machine.Phase);
        Assert.True(machine.JustEnteredActive);
        for (int tick = 0; tick < 4; tick++)
        {
            Assert.Equal(MovePhase.Active, machine.Phase);
            machine.Tick();
            Assert.False(machine.JustEnteredActive);
        }
        for (int tick = 0; tick < 20; tick++)
        {
            Assert.Equal(MovePhase.Recovery, machine.Phase);
            Assert.False(machine.Feint());
            machine.Tick();
        }
        Assert.False(machine.IsActive);
        Assert.Equal(.71f, shot.TargetHeading);
    }
}
