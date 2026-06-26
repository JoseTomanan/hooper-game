#nullable enable

namespace Hooper.Moves;

/// <summary>
/// The jump shot — the first committed move that isn't a dribble move: it
/// gives the previously-instant "press shoot, ball leaves hand" trigger a
/// real startup → active → recovery arc (M7b, issue #74), so a shot attempt
/// is as legible to the defender as a crossover's wind-up already is
/// (ADR-0003).
///
/// Unlike Crossover, this move carries no directional payload — there is
/// nothing extra to reconstruct on the wire beyond the move identity itself
/// (PlayerController.MoveParamOf already returns 0 for any non-Crossover
/// move). The ball's actual release is NOT this class's responsibility: it
/// fires on the holder's JustEnteredActive tick, read by
/// BallController.CheckJumpShotRelease via PlayerController.JustReleasedJumpShot
/// (JumpShotReleaseResolver) — this class only carries identity + timing.
///
/// Default frame data (tunable at construction if needed):
///   Startup:  18 ticks — gather/jump wind-up (~0.3s at 60Hz), deliberately
///             longer than the crossover's 6 — a shot is a bigger commitment.
///   Active:    4 ticks — the release window; the ball leaves the hand on the
///             FIRST of these ticks (JustEnteredActive), the same one-shot
///             convention Crossover's burst already uses.
///   Recovery: 20 ticks — landing / punish window (~0.33s at 60Hz).
///   Feint:     0 ticks — deliberately NO feint window yet. The existing
///             generic Feint() input path (PlayerController.SampleMoveInput)
///             is move-agnostic: any nonzero FeintWindowFrames here would
///             silently turn on the pump-fake (issue #77) before #77's own
///             scope says it's ready to wire. Zero keeps a pump-fake
///             impossible until #77 is activated and deliberately raises
///             this value alongside wiring the feint input for it.
/// </summary>
public sealed class JumpShot : CommittedMove
{
    /// <summary>Default jump-shot frame data. Tunable per instance if needed.</summary>
    public static readonly MoveFrameData DefaultFrameData =
        new(startupFrames: 18, activeFrames: 4, recoveryFrames: 20, feintWindowFrames: 0);

    /// <param name="frameData">Override frame data; null uses DefaultFrameData.</param>
    public JumpShot(MoveFrameData? frameData = null)
        : base(id: "jumpshot", displayName: "Jump Shot", frameData: frameData ?? DefaultFrameData)
    {
    }
}
