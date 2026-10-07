using System;
namespace HOOPERGAME.Tests.Integration;

// Units only. Move-specific expectations and acceptance tolerances stay local.
public static class ClipDuration
{
    public static double SecondsForTicks(double ticks, double ticksPerSecond)
    { ValidateRate(ticksPerSecond); return ticks / ticksPerSecond; }
    public static double TicksForSeconds(double seconds, double ticksPerSecond)
    { ValidateRate(ticksPerSecond); return seconds * ticksPerSecond; }
    public static double DeviationSeconds(double observedSeconds, double expectedTicks, double ticksPerSecond)
        => Math.Abs(observedSeconds - SecondsForTicks(expectedTicks, ticksPerSecond));
    private static void ValidateRate(double rate)
    {
        if (!double.IsFinite(rate) || rate <= 0)
            throw new ArgumentOutOfRangeException(nameof(rate), "Tick rate must be finite and positive.");
    }
}
