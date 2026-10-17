using System;

namespace Hooper.Player;

/// <summary>Within-phase display time; authority alone supplies phase transitions.</summary>
public static class CrossoverPoseClock
{
    public static int NextFrame(int previousFrame, int receivedFrame, int frameCount,
        bool simulatedLocally, bool newEpoch, ulong elapsedPhysicsTicks)
    {
        if (simulatedLocally || newEpoch) return receivedFrame;
        int elapsed = (int)Math.Min((ulong)int.MaxValue, elapsedPhysicsTicks);
        return (int)Math.Min(frameCount - 1, (long)previousFrame + elapsed);
    }
}
