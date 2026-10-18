using Hooper.Player;

namespace Hooper.Ball.Tests;

public class CrossoverPoseClockTests
{
    [Fact]
    public void DuplicateThenBunchedSnapshotsDoNotStarveAnObservedStartup()
    {
        int[] received = [0, 0, 0, 3, 4, 5];
        int sampled = 0;
        for (int tick = 0; tick < received.Length; tick++)
        {
            sampled = CrossoverPoseClock.NextFrame(sampled, received[tick], 6, false, tick == 0, 1);
            Assert.Equal(tick, sampled);
        }
        Assert.Equal(5, sampled);
    }

    [Fact]
    public void BunchedCounterDoesNotCompressThreeIntervalsIntoTwo()
    {
        int sampled = CrossoverPoseClock.NextFrame(0, 1, 6, false, true, 0);
        sampled = CrossoverPoseClock.NextFrame(sampled, 4, 6, false, false, 2);
        Assert.Equal(3, sampled);
        sampled = CrossoverPoseClock.NextFrame(sampled, 5, 6, false, false, 2);
        Assert.Equal(5, sampled);
    }

    [Fact]
    public void RepeatedEvaluationInOnePhysicsTickConsumesNoPoseTime()
    {
        Assert.Equal(2, CrossoverPoseClock.NextFrame(2, 5, 6, false, false, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(5)]
    public void LocallySimulatedPoseUsesActualMachineCounter(int received)
    {
        Assert.Equal(received, CrossoverPoseClock.NextFrame(3, received, 6, true, false, 0));
    }

    [Fact]
    public void NewPhaseUsesItsOwnCounterImmediately()
    {
        Assert.Equal(0, CrossoverPoseClock.NextFrame(5, 0, 3, false, true, 0));
        Assert.Equal(2, CrossoverPoseClock.NextFrame(5, 2, 3, false, true, 0));
    }

    [Fact]
    public void StalePhaseHoldsItsEndpointWithoutInventingAnotherPhase()
    {
        Assert.Equal(5, CrossoverPoseClock.NextFrame(4, 0, 6, false, false, 100));
        Assert.Equal(5, CrossoverPoseClock.NextFrame(5, 0, 6, false, false, ulong.MaxValue));
    }
}
