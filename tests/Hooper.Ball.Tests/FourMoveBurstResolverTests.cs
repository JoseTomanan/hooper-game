using Godot;
using Hooper.Moves;
using Hooper.Player;

namespace Hooper.Ball.Tests;

public class FourMoveBurstResolverTests
{
    private static readonly FourMoveBurstTuning Tuning = new(
        7.25f, 8.75f, .12f, 4.25f, 5.75f, 31f, 6.25f, 7.75f, 57f, 2.25f, 3.75f);
    private static readonly Vector3 Survivor = new(.3f, 0f, 1.7f);
    private const float Heading = .7f;

    [Theory]
    [InlineData(.7f, 1, 0f)]
    [InlineData(-.35f, -1, 0f)]
    [InlineData(0f, 0, 0f)]
    [InlineData(.7f, 1, .11f)]
    [InlineData(-.35f, -1, .13f)]
    public void CrossoverUsesItsOwnCompositionAndNormalizedSign(float direction, int sign, float exitMagnitude)
    {
        Vector2 exit = HeadingMath.Forward(Heading) * exitMagnitude;
        var expected = CrossoverBurstMath.ComposeCrossoverActiveVelocity(
            Survivor, Heading, sign, exit, 7.25f, 8.75f, .12f);
        AssertResolved(new Crossover(direction), exit, expected, swapHands: true);
    }

    [Theory]
    [InlineData(.7f, 1, 0f)]
    [InlineData(-.35f, -1, 0f)]
    [InlineData(0f, 0, 0f)]
    [InlineData(.7f, 1, .11f)]
    [InlineData(-.35f, -1, .13f)]
    public void BehindTheBackUsesItsProfileAndNormalizedSign(float direction, int sign, float exitMagnitude)
    {
        Vector2 exit = HeadingMath.Forward(Heading) * exitMagnitude;
        var expected = CrossoverBurstMath.ComposeActiveVelocity(
            Survivor, Heading, sign, exit, 4.25f, 5.75f, .12f, Mathf.DegToRad(31f));
        AssertResolved(new BehindTheBack(direction), exit, expected, swapHands: true);
    }

    [Theory]
    [InlineData(.7f, 1, 0f)]
    [InlineData(-.35f, -1, 0f)]
    [InlineData(0f, 0, 0f)]
    [InlineData(.7f, 1, .11f)]
    [InlineData(-.35f, -1, .13f)]
    public void BetweenTheLegsUsesItsProfileAndNormalizedSign(float direction, int sign, float exitMagnitude)
    {
        Vector2 exit = HeadingMath.Forward(Heading) * exitMagnitude;
        var expected = CrossoverBurstMath.ComposeActiveVelocity(
            Survivor, Heading, sign, exit, 6.25f, 7.75f, .12f, Mathf.DegToRad(57f));
        AssertResolved(new BetweenTheLegs(direction), exit, expected, swapHands: true);
    }

    [Theory]
    [InlineData(.7f, 1, 0f)]
    [InlineData(-.35f, -1, 0f)]
    [InlineData(0f, 0, 0f)]
    [InlineData(.7f, 1, .11f)]
    [InlineData(-.35f, -1, .13f)]
    public void InAndOutReversesTheNormalizedSignWithoutSwappingHands(float direction, int sign, float exitMagnitude)
    {
        Vector2 exit = HeadingMath.Forward(Heading) * exitMagnitude;
        var expected = CrossoverBurstMath.ComposeActiveVelocity(
            Survivor, Heading, -sign, exit, 2.25f, 3.75f, .12f, Mathf.DegToRad(180f));
        AssertResolved(new InAndOut(direction), exit, expected, swapHands: false);
    }

    [Theory]
    [InlineData(true, 31f, 4.25f, 5.75f)]
    [InlineData(false, 57f, 6.25f, 7.75f)]
    public void NarrowConeProfilesPassTheirOwnDegreeConversion(
        bool behindTheBack, float coneDegrees, float burstSpeed, float forwardScale)
    {
        float exitAngle = Mathf.DegToRad(coneDegrees + 2f);
        Vector2 exit = HeadingMath.Forward(Heading) * MathF.Cos(exitAngle)
            + HandStateResolver.BurstWorldDir(Heading, 1) * MathF.Sin(exitAngle);
        CommittedMove move = behindTheBack ? new BehindTheBack(.7f) : new BetweenTheLegs(.7f);
        var expected = CrossoverBurstMath.ComposeActiveVelocity(
            Survivor, Heading, 1, exit, burstSpeed, forwardScale, .12f, Mathf.DegToRad(coneDegrees));
        AssertResolved(move, exit, expected, swapHands: true);
    }

    [Fact]
    public void UnsupportedMovesReturnDefaultWithoutEvaluatingBurstMath()
    {
        CommittedMove?[] moves = [null, new Hesitation(), new Spin(.7f), new JumpShot()];
        foreach (CommittedMove? move in moves)
        {
            string? id = move?.Id;
            MoveFrameData? frameData = move?.FrameData;
            Assert.False(FourMoveBurstResolver.TryResolve(move,
                new Vector3(float.NaN, float.NaN, float.NaN), float.NaN,
                Vector2.Zero, default, out var result));
            Assert.Equal(default(BurstActiveEntry), result);
            Assert.Equal(id, move?.Id);
            Assert.Equal(frameData, move?.FrameData);
        }
        Assert.Equal(.7f, ((Spin)moves[2]!).SpinDirection);
    }

    [Fact]
    public void EveryCallUsesLiveTuningWithoutCachingMoveOrInputState()
    {
        var move = new Crossover(.7f);
        Vector2 exit = HeadingMath.Forward(Heading) * .11f;
        var changed = Tuning with { BurstSpeed = 9.5f, ForwardBurstScale = 11.5f, ExitDeadzone = .1f };
        var beforeExpected = CrossoverBurstMath.ComposeCrossoverActiveVelocity(
            Survivor, Heading, 1, exit, 7.25f, 8.75f, .12f);
        var afterExpected = CrossoverBurstMath.ComposeCrossoverActiveVelocity(
            Survivor, Heading, 1, exit, 9.5f, 11.5f, .1f);

        Assert.True(FourMoveBurstResolver.TryResolve(move, Survivor, Heading, exit, Tuning, out var before));
        Assert.True(FourMoveBurstResolver.TryResolve(move, Survivor, Heading, exit, changed, out var after));
        Assert.True(FourMoveBurstResolver.TryResolve(move, Survivor, Heading, exit, Tuning, out var again));
        Assert.Equal(new BurstActiveEntry(beforeExpected, true), before);
        Assert.Equal(new BurstActiveEntry(afterExpected, true), after);
        Assert.NotEqual(before, after);
        Assert.Equal(before, again);
    }

    [Fact]
    public void WarmedDispatchAllocatesNoManagedMemoryForAllFourFamilies()
    {
        CommittedMove[] moves = [new Crossover(.7f), new BehindTheBack(-.35f),
            new BetweenTheLegs(.7f), new InAndOut(-.35f)];
        Vector3 accumulator = Vector3.Zero;
        for (int i = 0; i < 400; i++)
        {
            FourMoveBurstResolver.TryResolve(moves[i % 4], Survivor, Heading,
                Vector2.Zero, Tuning, out var result);
            accumulator += result.Velocity;
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            var tuning = Tuning with { BurstSpeed = 7.25f + (i % 2) };
            Vector2 exit = new((i % 3) * .2f, .3f);
            for (int j = 0; j < 4; j++)
            {
                FourMoveBurstResolver.TryResolve(moves[j], Survivor, Heading + (i % 2),
                    exit, tuning, out var result);
                accumulator += result.Velocity;
            }
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
        Assert.True(float.IsFinite(accumulator.X) && float.IsFinite(accumulator.Z));
        Assert.NotEqual(Vector3.Zero, accumulator);
    }

    private static void AssertResolved(CommittedMove move, Vector2 exit, Vector3 expected, bool swapHands)
    {
        Assert.True(FourMoveBurstResolver.TryResolve(move, Survivor, Heading, exit, Tuning, out var result));
        Assert.Equal(expected, result.Velocity);
        Assert.Equal(swapHands, result.SwapHands);
    }
}
