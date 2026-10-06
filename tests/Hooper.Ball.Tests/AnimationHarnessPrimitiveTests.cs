using HOOPERGAME.Tests.Integration;
namespace Hooper.Ball.Tests;
public class AnimationHarnessPrimitiveTests
{
    [Fact]
    public void DurationUsesExplicitRateWithoutRoundingOrThresholds()
    {
        Assert.Equal(7.0 / 120, ClipDuration.SecondsForTicks(7, 120));
        Assert.Equal(7.25, ClipDuration.TicksForSeconds(7.25 / 120, 120));
        Assert.Equal(0.002, ClipDuration.DeviationSeconds(0.002, 0, 120), 12);
        Assert.Equal(0.003, ClipDuration.DeviationSeconds(0.247, 30, 120), 12);
        Assert.Equal(0.003, ClipDuration.DeviationSeconds(0.253, 30, 120), 12);
    }
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)] [InlineData(double.NegativeInfinity)]
    public void DurationRejectsRatesThatCannotConvertTime(double rate)
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => ClipDuration.SecondsForTicks(7, rate));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => ClipDuration.TicksForSeconds(0.2, rate));
    }
    [Fact]
    public void ReportingPreservesCallerIdentityDetailsAndExitCode()
    {
        var report = new HarnessReport("other-move", "unusual-scenario");
        Assert.Equal("[other-move] FAIL: supplied detail", report.Failure("supplied detail"));
        Assert.Equal("[other-move] RESULT: FAIL (exit 7)", report.Result(7));
        Assert.Equal("unusual-scenario", report.Scenario);
        Assert.Equal("[other-move] RESULT: PASS (exit 0)", report.Result(0));
    }
}
