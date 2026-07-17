#nullable enable

namespace Hooper.Moves;

/// <summary>
/// The retreat dribble — the "quick" half of the vertical right-stick
/// gesture pair (issue #197, M9/epic #75), sharing one gesture-grammar
/// extension with <see cref="StepBack"/> (the "hold" half).
///
/// In basketball, a retreat dribble is a light backward hop off a live
/// dribble that creates a sliver of separation without spending the
/// dribble — cheap, low-commitment, the opposite end of the risk/reward
/// spectrum from StepBack's dead-Held gather.
///
/// Key differences from <see cref="StepBack"/>:
///   - No gather: the ball stays Dribbling throughout. Unlike StepBack this
///     move never calls BallController.CradleForShotStartup — HasDribbled
///     is untouched (#193).
///   - No left-stick exit shaping: the burst is a fixed hop straight back
///     along Heading. StepBack's whole "biggest separation" identity comes
///     from the exit-cone shaping; a light bait move doesn't need it.
///   - feintWindowFrames: 0 — NOT because this move is somehow "more
///     committed" than Crossover, but the opposite: the quick right-stick
///     flick-and-release gesture that triggers it IS already the feint (the
///     recognizer only reports RetreatDribble on the deadzone-return path —
///     see RightStickGestureRecognizer). Also allowing a SECOND, free abort
///     on top of that would make the retreat dribble a zero-cost bait tool,
///     which the issue spec explicitly calls out as unwanted ("a fakeable
///     retreat dribble would be free bait with no cost").
///
/// Default frame data (placeholder, mirrors Hesitation's "light move" scale
/// — the human tunes the exact feel in-editor, deferred to the
/// per-milestone pass per ADR-0021):
///   Startup:  3 ticks — a brief, visible weight-shift back
///   Active:   2 ticks — the hop itself
///   Recovery: 4 ticks — short punish window; this is a cheap move BY DESIGN
///   Feint:    0 ticks — see class doc above; not a placeholder
/// </summary>
public sealed class RetreatDribble : CommittedMove
{
    /// <summary>Default retreat-dribble frame data. Tunable per instance if needed.</summary>
    public static readonly MoveFrameData DefaultFrameData =
        new(startupFrames: 3, activeFrames: 2, recoveryFrames: 4, feintWindowFrames: 0);

    /// <param name="frameData">Override frame data for tuning. Null uses <see cref="DefaultFrameData"/>.</param>
    public RetreatDribble(MoveFrameData? frameData = null)
        : base(id: "retreatdribble", displayName: "Retreat Dribble", frameData: frameData ?? DefaultFrameData)
    {
    }
}
