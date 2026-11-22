using Hooper.Moves;
using Hooper.Player;
using Xunit;

namespace Hooper.Ball.Tests;

public class OffensiveFollowUpQueueTests
{
    [Fact]
    public void RecoveryOffersKeepTheFirstTwoInputsInOrder()
    {
        var queue = new OffensiveFollowUpQueue<string>();
        Assert.False(queue.TryEnqueue("early", MovePhase.Recovery, 7));
        Assert.True(queue.TryEnqueue("first", MovePhase.Recovery, 6));
        Assert.True(queue.TryEnqueue("second", MovePhase.Recovery, 1));
        Assert.False(queue.TryEnqueue("overflow", MovePhase.Recovery, 1));
        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal("first", first);
        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal("second", second);
        Assert.False(queue.TryDequeue(out _));
    }

    [Theory]
    [InlineData(MovePhase.Inactive, 0, false)]
    [InlineData(MovePhase.Startup, 1, false)]
    [InlineData(MovePhase.Active, 1, false)]
    [InlineData(MovePhase.Recovery, 7, false)]
    [InlineData(MovePhase.Recovery, 6, true)]
    [InlineData(MovePhase.Recovery, 4, true)]
    [InlineData(MovePhase.Recovery, 1, true)]
    [InlineData(MovePhase.Recovery, 0, false)]
    public void AcceptsOnlyUnconsumedTicksInTheRecoveryWindow(MovePhase phase, int remaining, bool accepted)
    {
        var queue = new OffensiveFollowUpQueue<int>();
        Assert.Equal(accepted, queue.TryEnqueue(42, phase, remaining));
        Assert.Equal(accepted, queue.TryDequeue(out _));
    }

    [Fact]
    public void ClearingPossessionDropsEveryPendingInputAndAllowsFreshInputs()
    {
        var queue = new OffensiveFollowUpQueue<int>();
        Assert.True(queue.TryEnqueue(1, MovePhase.Recovery, 6));
        Assert.True(queue.TryEnqueue(2, MovePhase.Recovery, 5));
        queue.InvalidatePossession();
        Assert.True(queue.TryEnqueue(3, MovePhase.Recovery, 4));
        Assert.True(queue.TryDequeue(out var input));
        Assert.Equal(3, input);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void ABufferedFlickKeepsItsModifierAndResolvesAgainstTheCurrentHand()
    {
        var flick = new GestureResult(GestureKind.Crossover, 1);
        var beforeHandChange = OffensiveGestureResolver.Resolve(flick, true, true, HandSide.Right, true, true);
        var afterHandChange = OffensiveGestureResolver.Resolve(flick, true, true, HandSide.Left, true, true);
        Assert.IsType<Hesitation>(beforeHandChange.Move);
        Assert.IsType<Spin>(afterHandChange.Move);
        Assert.Equal(1f, afterHandChange.Param);
    }
}
