#nullable enable

namespace Hooper.Moves;

/// <summary>
/// Between-the-legs — the balanced midpoint of the crossover family (issue
/// #199, epic #75/M9). GREENLIT spec, approved 2026-07-04 from the M9
/// move-taxonomy triage session (amended 2026-07-04: startup framing
/// corrected to share Crossover's, not the longest of the three).
///
/// Human framing: "less explosive than a front cross, less shielded than a
/// behind-the-back, moderate in every dimension" — a real midpoint, not a
/// third pole that trades time for anything. Per the same
/// docs/handoffs/M9-move-taxonomy.md "one parameterized composition, not a
/// move zoo" principle #194 established, this is its OWN CommittedMove
/// subclass (own tunables to independently refine) but shares physics with
/// Crossover/BehindTheBack via composition: all three feed the SAME pure
/// composition helpers (CrossoverBurstMath.ComposeActiveVelocity,
/// CrossoverBallSweep) with different tunables, never a structural
/// special-case or a third hand-rolled burst function.
///
/// ── Tradeoff profile vs. Crossover/BehindTheBack (all differences live in
/// TUNABLES the PlayerController/BallController pass in for this move type,
/// never a structural special-case) ────────────────────────────────────────
///   - Same startup as Crossover: the ball travels the same distance (hand
///     to hand), so BTL is differentiated purely on EFFECTIVENESS axes, not
///     timing — see PlayerController.BetweenTheLegsBurstSpeed/
///     ForwardBurstScale/GatherDecel/ExitConeDegrees, each a genuine
///     midpoint between Crossover's and BehindTheBack's own tunable.
///   - Ball transit path: THROUGH THE LEGS, a third path alongside
///     Crossover's in-front sweep and BehindTheBack's behind-body sweep
///     (BallController.BetweenTheLegsDipDepth — see CrossoverBallSweep's
///     BallSweepPath.ThroughLegs).
///   - Ball exposure: small but nonzero (the ball dips toward the floor
///     between the legs at the bounce point) — between Crossover's full
///     exposure and BehindTheBack's near-zero exposure. The #196 spatial
///     steal-window geometry that would key off this is BLOCKED on #195 for
///     even Crossover/BehindTheBack today (still open, per the milestone
///     table) — so this move's identity notes the exposure but does not
///     implement new steal-window code; that lands once #196 itself does,
///     for all three moves at once.
///
/// Default frame data mirrors Crossover's Startup/Active with a Recovery
/// GENUINELY MIDWAY between BehindTheBack's (10) and Crossover's (12) — the
/// "balanced midpoint" identity applied literally, not merely "comparable to
/// Crossover's" the way BehindTheBack's own doc reads:
///   Startup:  6 ticks  — same visible wind-up as Crossover
///   Active:   3 ticks  — same swap-transit window
///   Recovery: 11 ticks — midpoint of BehindTheBack's 10 and Crossover's 12
///   Feint:    4 ticks  — same feint-legal window as Crossover/BehindTheBack
/// </summary>
public sealed class BetweenTheLegs : CommittedMove
{
    /// <summary>Default between-the-legs frame data. Tunable per instance if needed.</summary>
    public static readonly MoveFrameData DefaultFrameData =
        new(startupFrames: 6, activeFrames: 3, recoveryFrames: 11, feintWindowFrames: 4);

    /// <summary>
    /// Direction of the lateral burst: +1 = right, -1 = left. Same
    /// body-relative flick-sign convention as Crossover.BurstDirection and
    /// BehindTheBack.BurstDirection — see HandStateResolver's doc for the
    /// sign convention.
    /// </summary>
    public float BurstDirection { get; }

    /// <param name="burstDirection">+1 = burst right, -1 = burst left.</param>
    /// <param name="frameData">Override frame data; null uses DefaultFrameData.</param>
    public BetweenTheLegs(float burstDirection, MoveFrameData? frameData = null)
        : base(id: "betweenthelegs", displayName: "Between the Legs", frameData: frameData ?? DefaultFrameData)
    {
        BurstDirection = burstDirection;
    }
}
