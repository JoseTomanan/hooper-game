#nullable enable

namespace Hooper.Moves;

/// <summary>
/// The crossover — the first concrete committed move.
///
/// In basketball, a crossover is a dribble move where the ball-handler
/// switches the ball from one hand to the other in a quick lateral motion to
/// create separation from the defender. Here it is modelled as the first
/// playable example of the committed-move framework (ADR-0003, issue #17).
///
/// Active-phase effect: a lateral separation burst is applied to the player's
/// velocity for exactly one tick (JustEnteredActive). The direction of the
/// burst is determined by which way the right-stick gesture was flicked.
///
/// Default frame data (tunable at construction if needed):
///   Startup:  6 ticks  — visibly telegraphed wind-up (~0.1s at 60Hz)
///   Active:   3 ticks  — the burst window
///   Recovery: 12 ticks — punish window (~0.2s at 60Hz)
///   Feint:    4 ticks  — feint legal on the first 4 startup frames
/// </summary>
public sealed class Crossover : CommittedMove
{
    /// <summary>Default crossover frame data. Tunable per instance if needed.</summary>
    public static readonly MoveFrameData DefaultFrameData =
        new(startupFrames: 6, activeFrames: 3, recoveryFrames: 12, feintWindowFrames: 4);

    /// <summary>
    /// Direction of the lateral burst: +1 = right, -1 = left.
    /// Set by the gesture recognizer based on the right-stick flick direction.
    /// </summary>
    public float BurstDirection { get; }

    /// <param name="burstDirection">+1 = burst right, -1 = burst left.</param>
    /// <param name="frameData">Override frame data; null uses DefaultFrameData.</param>
    public Crossover(float burstDirection, MoveFrameData? frameData = null)
        : base(id: "crossover", displayName: "Crossover", frameData: frameData ?? DefaultFrameData)
    {
        BurstDirection = burstDirection;
    }
}
