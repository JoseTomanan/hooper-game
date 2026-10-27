#nullable enable

using System;
using Godot;
using Hooper.Moves;

namespace Hooper.Player;

/// <summary>A value snapshot of live controller exports, with no independent defaults.</summary>
public readonly record struct FourMoveBurstTuning(
    float BurstSpeed, float ForwardBurstScale, float ExitDeadzone,
    float BehindTheBackBurstSpeed, float BehindTheBackForwardBurstScale, float BehindTheBackExitConeDegrees,
    float BetweenTheLegsBurstSpeed, float BetweenTheLegsForwardBurstScale, float BetweenTheLegsExitConeDegrees,
    float InAndOutBurstSpeed, float InAndOutForwardBurstScale);

public readonly record struct BurstActiveEntry(Vector3 Velocity, bool SwapHands);

/// <summary>
/// Active-entry policy for these four concrete moves. The caller owns timing,
/// role-aware exit sampling, state mutation and contact integration; this module
/// only selects the existing calculation and its live parameters (#437).
/// </summary>
public static class FourMoveBurstResolver
{
    public static bool TryResolve(
        CommittedMove? move, Vector3 survivingVelocity, float heading, Vector2 exitVector,
        in FourMoveBurstTuning tuning, out BurstActiveEntry result)
    {
        switch (move)
        {
            case Crossover crossover:
                // #424's moving cut belongs to Crossover alone.
                result = new(CrossoverBurstMath.ComposeCrossoverActiveVelocity(
                    survivingVelocity, heading, Math.Sign(crossover.BurstDirection), exitVector,
                    tuning.BurstSpeed, tuning.ForwardBurstScale, tuning.ExitDeadzone), SwapHands: true);
                return true;

            case BehindTheBack behind:
                result = new(CrossoverBurstMath.ComposeActiveVelocity(
                    survivingVelocity, heading, Math.Sign(behind.BurstDirection), exitVector,
                    tuning.BehindTheBackBurstSpeed, tuning.BehindTheBackForwardBurstScale, tuning.ExitDeadzone,
                    Mathf.DegToRad(tuning.BehindTheBackExitConeDegrees)), SwapHands: true);
                return true;

            case BetweenTheLegs between:
                result = new(CrossoverBurstMath.ComposeActiveVelocity(
                    survivingVelocity, heading, Math.Sign(between.BurstDirection), exitVector,
                    tuning.BetweenTheLegsBurstSpeed, tuning.BetweenTheLegsForwardBurstScale, tuning.ExitDeadzone,
                    Mathf.DegToRad(tuning.BetweenTheLegsExitConeDegrees)), SwapHands: true);
                return true;

            case InAndOut inAndOut:
                // The ball never crosses: fallback and backward-pole direction
                // point toward the ball hand, and no hand swap is requested.
                result = new(CrossoverBurstMath.ComposeActiveVelocity(
                    survivingVelocity, heading, -Math.Sign(inAndOut.BurstDirection), exitVector,
                    tuning.InAndOutBurstSpeed, tuning.InAndOutForwardBurstScale, tuning.ExitDeadzone,
                    Mathf.DegToRad(180f)), SwapHands: false);
                return true;

            default:
                result = default;
                return false;
        }
    }
}
