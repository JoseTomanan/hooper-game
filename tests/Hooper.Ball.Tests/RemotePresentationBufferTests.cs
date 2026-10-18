using Hooper.Networking;

namespace Hooper.Ball.Tests;

public class RemotePresentationBufferTests
{
    private sealed record Frame(long Tick, string Phase = "Startup", int Counter = 0, int Hand = 1);
    private static void Add(RemotePresentationBuffer<Frame> buffer, long first, long last)
    {
        for (long tick = first; tick <= last; tick++) buffer.Enqueue(tick, new Frame(tick));
    }

    [Fact]
    public void StartsWithThreeFutureFramesAndFreezesSelectionForTheWholeLocalTick()
    {
        var b = new RemotePresentationBuffer<Frame>();
        Add(b, 10, 12);
        Assert.Null(b.Advance(1));
        Add(b, 13, 13);
        Assert.Null(b.Advance(1));
        Assert.Equal(10, b.Advance(2)!.Tick);
        Assert.Equal(10, b.Advance(2)!.Tick);
        Assert.Equal(11, b.Advance(3)!.Tick);
    }

    [Fact]
    public void DuplicateAndStaleDeliveryCannotReplaceTheDisplayedOrQueuedFrame()
    {
        var b = new RemotePresentationBuffer<Frame>(); Add(b, 10, 13);
        var first = b.Advance(1);
        b.Enqueue(10, new Frame(10, "Wrong")); b.Enqueue(9, new Frame(9));
        b.Enqueue(11, new Frame(11, "Wrong"));
        Assert.Same(first, b.Advance(1));
        Assert.Equal("Startup", b.Advance(2)!.Phase);
    }

    [Fact]
    public void LateStartupBurstRetainsEveryCounterAndTheAuthoritativeHandBoundary()
    {
        var b = new RemotePresentationBuffer<Frame>();
        for (long i = 0; i <= 3; i++) b.Enqueue(i, new Frame(i, "Neutral"));
        Assert.Equal(0, b.Advance(1)!.Tick);
        for (ulong stamp = 2; stamp <= 7; stamp++)
        {
            b.Enqueue((long)stamp + 2, new Frame((long)stamp + 2, "Neutral"));
            Assert.Equal("Neutral", b.Advance(stamp)!.Phase);
        }
        for (ulong stamp = 8; stamp <= 10; stamp++) Assert.Equal("Neutral", b.Advance(stamp)!.Phase);
        Assert.Equal(9, b.Advance(11)!.Tick);
        Assert.True(b.IsHolding);
        for (int i = 10; i <= 19; i++) b.Enqueue(i, new Frame(i,
            i < 16 ? "Startup" : i < 19 ? "Active" : "Recovery", i < 16 ? i - 10 : i < 19 ? i - 16 : 0, i < 16 ? 1 : -1));
        var displayed = Enumerable.Range(12, 10).Select(stamp => b.Advance((ulong)stamp)!).ToArray();
        Assert.Equal(Enumerable.Range(10, 10).Select(x => (long)x), displayed.Select(f => f.Tick));
        Assert.Equal(Enumerable.Range(0, 6), displayed.Take(6).Select(f => f.Counter));
        Assert.Equal(Enumerable.Range(0, 3), displayed.Skip(6).Take(3).Select(f => f.Counter));
        Assert.All(displayed.Take(6), f => { Assert.Equal("Startup", f.Phase); Assert.Equal(1, f.Hand); });
        Assert.All(displayed.Skip(6).Take(3), f => { Assert.Equal("Active", f.Phase); Assert.Equal(-1, f.Hand); });
        Assert.Equal(new Frame(19, "Recovery", 0, -1), displayed.Last());
        Assert.Equal(0, b.ResyncCount);
    }

    [Fact]
    public void UnderflowHoldsTheWholeFrameAndRequiresThreeContiguousFutureFrames()
    {
        var b = new RemotePresentationBuffer<Frame>(); Add(b, 0, 3);
        for (ulong stamp = 1; stamp <= 4; stamp++) b.Advance(stamp);
        var last = b.Current;
        Assert.Same(last, b.Advance(5)); Assert.True(b.IsHolding);
        Add(b, 4, 4); Add(b, 6, 7);
        Assert.Same(last, b.Advance(6));
        Add(b, 5, 5);
        Assert.Equal(4, b.Advance(7)!.Tick);
        Assert.False(b.IsHolding);
    }

    [Fact]
    public void ProducerOutrunResynchronizesOnlyAtANewLocalTickAndBoundsStorage()
    {
        var b = new RemotePresentationBuffer<Frame>(); Add(b, 0, 3); b.Advance(1);
        Add(b, 4, 40);
        Assert.InRange(b.Count, 0, 16);
        Assert.Equal(0, b.Advance(1)!.Tick);
        Assert.Equal(0, b.ResyncCount);
        Assert.Equal(37, b.Advance(2)!.Tick);
        Assert.Equal(1, b.ResyncCount);
    }

    [Fact]
    public void FasterConsumerHoldsWithoutInventingSourceTicks()
    {
        var b = new RemotePresentationBuffer<Frame>(); Add(b, 0, 3);
        var seen = Enumerable.Range(0, 20).Select(stamp => b.Advance((ulong)stamp)!.Tick).ToArray();
        Assert.Equal(new long[] { 0, 1, 2, 3 }, seen.Take(4));
        Assert.All(seen.Skip(4), tick => Assert.Equal(3, tick));
    }

    [Fact]
    public void ResetClearsHistoryAndAllowsAReconnectedServerTickEpoch()
    {
        var b = new RemotePresentationBuffer<Frame>(); Add(b, 100, 103); b.Advance(10);
        b.Reset(); Assert.Null(b.Current); Assert.Equal(0, b.Count);
        Add(b, 0, 3); Assert.Equal(0, b.Advance(10)!.Tick);
    }
}
