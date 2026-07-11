#nullable enable

namespace Hooper.Moves;

/// <summary>
/// The block attempt — the second M10 defensive committed move (issue #98,
/// epic #89, ADR-0018 §2).
///
/// A block in half-court 1v1 is a timed, planted, standing swat at the ball
/// during its release / early-flight window: the defender commits with a
/// visible plant (Startup — no jump/vertical motion yet; Startup zeroes
/// velocity, the defender stays grounded), the block window opens (Active),
/// and if that window overlaps the shot's vulnerable interval —
/// [JumpShot.Active start, InFlight start + blockGraceTicks), the same tick
/// by construction since release fires on JumpShot's Active entry (see
/// BallController.ResolveBlockAttempts) — the ball goes Loose (ADR-0008
/// §Amendment 2026-06-30, ADR-0018 §2). A leaping/jumping presentation is
/// deferred to the animation pass (#102); the underlying mechanic here is
/// grounded.
///
/// ── ADR-0003 legibility commitment ──────────────────────────────────────
/// The block is NOT an instant button (move-and-strike, ADR-0003 anti-goal).
/// Startup telegraphs the commitment to both players; Recovery is punishable
/// on a whiff — a missed block is a blow-by / uncontested-layup opportunity
/// (#100).
///
/// ── Block vs steal: interval form (ADR-0018 §2) ─────────────────────────
/// Steal uses a POINT-IN-BAND check (fires on a single JustEnteredActive tick
/// against the current dribble phase). Block uses the FULL INTERVAL FORM of
/// DefensiveResolution.Succeeds: the defender's entire Active window
/// [blockActiveStart, blockActiveEnd) is compared against the shot's
/// vulnerable interval [inFlightStartTick, inFlightStartTick + blockGraceTicks).
/// This is the right model because the shot vulnerability is a real duration
/// (the ball rises away from the hand over several ticks), not an instantaneous
/// point like the dribble's floor-contact moment.
///
/// ── No TargetHand (no side axis) ────────────────────────────────────────
/// The steal is a TWO-AXIS read (when + which hand). The block is a ONE-AXIS
/// read (when): the ball is no longer in the handler's hand when it can be
/// blocked — it's already airborne — so there is no hand-side to target.
/// BallController.ResolveBlockAttempts checks only the timing overlap.
///
/// ── Reaction-tilt asymmetry (ADR-0018 §3) ───────────────────────────────
/// A defensive Active is no wider than the offensive vulnerable window it
/// must hit; Recovery is at least as long as the JumpShot's. The provisional
/// defaults below are placeholders — the exact tick counts are deferred to
/// tuning issue #104 and the per-milestone feel pass (ADR-0015).
///
/// Default frame data (provisional, tuning deferred to #104):
///   Startup:  10 ticks — planted swat wind-up (~0.17s at 60 Hz; no jump yet,
///             see the class doc above). Shorter than the
///             JumpShot's 18-tick Startup because the defender reacts to a
///             visible shot attempt rather than initiating one from scratch;
///             that is the "reaction tilt" (ADR-0018 §3). But NOT instant:
///             the telegraph must still be readable (ADR-0003).
///   Active:    8 ticks — the block window. Must be ≤ blockGraceTicks (default 10)
///             per ADR-0018 §3; any wider and the defender could cover mismatched
///             timing with a longer arm.
///   Recovery: 20 ticks — matches JumpShot.DefaultFrameData.RecoveryFrames so a
///             missed block is as punishable as a missed shot (ADR-0018 §3).
///   Feint:     4 ticks — feintable in the first 4 Startup frames, zero
///             recovery cost (abort to Inactive) so a faked leap is cheaper
///             than a fully committed whiff. Numbers mirror StealMove.
/// </summary>
public sealed class BlockMove : CommittedMove
{
    /// <summary>Default block frame data. Tunable per instance if needed.</summary>
    public static readonly MoveFrameData DefaultFrameData =
        new(startupFrames: 10, activeFrames: 8, recoveryFrames: 20, feintWindowFrames: 4);

    /// <param name="frameData">Override frame data; null uses DefaultFrameData.</param>
    public BlockMove(MoveFrameData? frameData = null)
        : base(id: "block", displayName: "Block", frameData: frameData ?? DefaultFrameData)
    {
    }
}
