using System;
using Godot;

namespace Hooper.Player;

/// <summary>Pure heading policy for post entry and the committed turnaround.
/// Caller derives whether post is requested; this helper creates no pivot debt.</summary>
public static class PostMath
{
    public readonly record struct PostStep(HeadingMath.HeadingStep Heading,
        bool Established, bool TranslationAllowed);

    /// <summary>An owed ordinary pivot consumes the whole tick, including its
    /// completion tick. Otherwise turn toward away-from-rim and plant until
    /// within entry tolerance; never snap the remaining angular error.</summary>
    public static PostStep Step(float currentYaw, HeadingMath.PivotState pivot,
        Vector2 awayFromRim, double delta, float postTurnRateDeg,
        float ordinaryTurnRateDeg, float backTurnSlowFactor, float pivotThresholdDeg,
        float entryToleranceDeg)
    {
        if (pivot.HasLatch)
            return new(HeadingMath.Step(currentYaw, pivot, Vector2.Zero, delta,
                ordinaryTurnRateDeg, backTurnSlowFactor, pivotThresholdDeg), false, false);

        // At the rim there is no defined away-facing. Preserve the finite
        // heading rather than snapping to Atan2(0, 0), and permit the stance.
        // Same length guard as HeadingMath; Vector2 API source:
        // https://docs.godotengine.org/en/4.7/classes/class_vector2.html#class-vector2-method-length
        if (awayFromRim.Length() < .01f)
            return new(new(currentYaw, HeadingMath.PivotState.None, false), true, true);

        float yaw = HeadingMath.RotateToward(currentYaw, awayFromRim, delta,
            postTurnRateDeg, backTurnSlowFactor);
        float target = MathF.Atan2(awayFromRim.X, awayFromRim.Y);
        bool established = MathF.Abs(AngleDiff(yaw, target)) <= entryToleranceDeg * MathF.PI / 180f;
        return new(new(yaw, HeadingMath.PivotState.None, !established), established, established);
    }

    /// <summary>Constant-rate shortest-arc turn toward the move's fixed target.
    /// An exactly opposite facing consistently turns in the positive direction.</summary>
    public static float StepTurn(float currentYaw, float targetYaw, double delta,
        float rateDeg)
    {
        float diff = AngleDiff(currentYaw, targetYaw);
        // +/- pi encode the same facing. Float rounding at shifted origins
        // must not choose a different side on another role's reconstruction.
        if (MathF.Abs(MathF.Abs(diff) - MathF.PI) <= 1e-6f)
            diff = MathF.PI;
        float maxStep = rateDeg * (float)delta * MathF.PI / 180f;
        float step = MathF.Min(MathF.Abs(diff), maxStep) * MathF.Sign(diff);
        return AngleDiff(0f, currentYaw + step);
    }

    private static float AngleDiff(float from, float to)
    {
        float diff = to - from;
        while (diff > MathF.PI) diff -= 2f * MathF.PI;
        while (diff < -MathF.PI) diff += 2f * MathF.PI;
        return diff;
    }
}
