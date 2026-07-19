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
///   Feint window: frames 3..11 of 18 startup ticks (#77 pump-fake, #138 floor).
///             The legal feint window is [feintMinStartup, feintWindow) = [3, 12):
///             a feint is illegal for the first 3 frames so the shot wind-up is
///             always visibly telegraphed before it can be aborted (a same-tick
///             shoot+feint would otherwise be a zero-startup invisible fake —
///             the arcade-decoupling anti-goal, ADR-0003). Frames 12..17 are the
///             "committed tail" — the point of no return. startup = 18, tail = 6.
///   Feint recovery: 8 ticks — a pump-fake costs 8 ticks of recovery, shorter
///             than the full 20-tick landing recovery because you never left
///             the ground. CommittedMoveMachine.Feint() enters Recovery at the
///             pre-advanced offset (RecoveryFrames - FeintRecoveryFrames = 12)
///             so exactly 8 ticks remain before the machine returns to Inactive.
///             These values are provisional — tuning for feel is deferred to M10.
/// </summary>
public sealed class JumpShot : CommittedMove
{
    /// <summary>Default jump-shot frame data. Tunable per instance if needed.</summary>
    public static readonly MoveFrameData DefaultFrameData =
        new(startupFrames: 18, activeFrames: 4, recoveryFrames: 20,
            feintWindowFrames: 12, feintRecoveryFrames: 8, feintMinStartupFrames: 3);

    /// <param name="frameData">Override frame data; null uses DefaultFrameData.</param>
    public JumpShot(MoveFrameData? frameData = null)
        : base(id: "jumpshot", displayName: "Jump Shot", frameData: frameData ?? DefaultFrameData)
    {
    }

    /// <summary>
    /// (Issue #243) Whether THIS release was classified fadeaway/off-balance
    /// by FadeawayTriggerResolver — set once, at THIS move's own
    /// JustEnteredActive tick (PlayerController.TickCommittedMoveBehavior),
    /// not at construction. Unlike Crossover's BurstDirection (fixed at
    /// Begin() from the input stick), the fadeaway classification depends on
    /// the shooter's Heading at RELEASE, which can still be turning through
    /// the whole of Startup — so it cannot be known until Active is entered.
    /// Defaults false (squared-up) until that tick sets it. Mutable rather
    /// than an init-only property for exactly that reason.
    /// </summary>
    public bool IsFadeaway { get; set; }
}
