using Hooper.Moves;
using Hooper.Player;
using Xunit;

namespace Hooper.Ball.Tests;

public class HeldAnimResolverTests
{
    [Fact]
    public void NeutralHeldUsesCarryStance()
    {
        Assert.Equal(MoveAnimState.Held, MoveAnimResolver.Resolve(MovePhase.Inactive, isHeld: true));
        Assert.Equal(MoveAnimState.Locomotion, MoveAnimResolver.Resolve(MovePhase.Inactive));
        Assert.Equal(MoveAnimState.Dribble, MoveAnimResolver.Resolve(MovePhase.Inactive, isDribbling: true));
    }

    [Theory]
    [InlineData(MovePhase.Startup, MoveAnimState.Startup)]
    [InlineData(MovePhase.Active, MoveAnimState.Active)]
    [InlineData(MovePhase.Recovery, MoveAnimState.Recovery)]
    public void HeldNeverOverridesCommittedPhase(MovePhase phase, MoveAnimState expected)
    {
        Assert.Equal(expected, MoveAnimResolver.Resolve(phase, isHeld: true));
    }

    [Fact]
    public void HeldPreservesPivotAndReboundPriority()
    {
        Assert.Equal(MoveAnimState.Pivot,
            MoveAnimResolver.Resolve(MovePhase.Inactive, isPivotingInPlace: true, isHeld: true));
        Assert.Equal(MoveAnimState.ReboundGrab,
            MoveAnimResolver.Resolve(MovePhase.Inactive, isPivotingInPlace: true, isPlayingReboundGrab: true, isHeld: true));
    }

    [Theory]
    [InlineData(HandSide.Left, "HeldLeft")]
    [InlineData(HandSide.Right, "HeldRight")]
    public void CarryUsesCurrentHandWithoutMoveOriginCorrection(HandSide hand, string expected)
    {
        Assert.Equal(expected, MoveAnimResolver.ResolveStateName(MoveAnimState.Held, null, hand, hand));
        Assert.Equal(expected, MoveAnimResolver.ResolveStateName(MoveAnimState.Held, "crossover", hand, hand));
    }
}
