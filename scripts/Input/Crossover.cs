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
/// ── Structurally unfeintable (ADR-0003 amendment, issue #202) ──────────────
/// feintWindowFrames was 4 (a same-tick begin+feint yielded a free, zero-cost
/// abort — functionally the "size-up," not a real basketball move). #202
/// closes ADR-0003's flagged feint reconsideration: the in-and-out is now the
/// realistic, honest replacement for that recall (the ball never crosses on
/// an in-and-out, so it is not subject to "once the ball is headed to the
/// floor you cannot pull it back" the way a recalled crossover was). A
/// Crossover can therefore no longer be feinted at all — see docs/adr/
/// 0003-input-model-hybrid.md's amendment for the full reasoning and the
/// rejected alternatives.
///
/// Default frame data (tunable at construction if needed):
///   Startup:  6 ticks  — visibly telegraphed wind-up (~0.1s at 60Hz)
///   Active:   3 ticks  — the burst window
///   Recovery: 12 ticks — punish window (~0.2s at 60Hz)
///   Feint:    0 ticks  — a design constant, NOT a placeholder (#202): a
///             Crossover is now structurally unfeintable; see the class
///             doc's "Structurally unfeintable" section above.
/// </summary>
public sealed class Crossover : CommittedMove
{
    /// <summary>Default crossover frame data. Tunable per instance if needed.</summary>
    public static readonly MoveFrameData DefaultFrameData =
        new(startupFrames: 6, activeFrames: 3, recoveryFrames: 12, feintWindowFrames: 0);

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
