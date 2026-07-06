#nullable enable

namespace Hooper.Moves;

/// <summary>
/// The behind-the-back — a safer, less-committal sibling of the crossover
/// (issue #194, epic #75/M9).
///
/// Human framing: "a slightly safer crossover — less stealable, less
/// explosive, fewer follow-up options." Per the M9 move-taxonomy handoff
/// (docs/handoffs/M9-move-taxonomy.md), this is deliberately its OWN
/// CommittedMove subclass rather than a parameter/flag on <see cref="Crossover"/>
/// — a cleaner surface to tune independently. Composition, not inheritance,
/// is how it shares physics with Crossover: both feed the SAME pure
/// composition helpers (CrossoverBurstMath.ComposeActiveVelocity,
/// CrossoverBallSweep) with different tunables, rather than one subclassing
/// the other or a shared base carrying move-specific behaviour.
///
/// ── Tradeoff profile vs. Crossover (all differences live in TUNABLES the
/// PlayerController/BallController pass in for this move type, never in a
/// structural special-case) ──────────────────────────────────────────────
///   - Less stealable: the ball transits BEHIND the body (BallController's
///     AdvanceHandSweep selects a behind-body depth pull-back instead of
///     Crossover's in-front sweep) — a near-zero transit steal window.
///   - Less explosive: smaller Active-phase burst (PlayerController's
///     BehindTheBackBurstSpeed/BehindTheBackForwardBurstScale, both lower
///     than Crossover's BurstSpeed/ForwardBurstScale).
///   - Heavier gather bleed: BehindTheBackGatherDecel bleeds Startup
///     momentum harder than Crossover's GatherDecel.
///   - Fewer follow-ups: encoded ONLY as a NARROWER left-stick exit cone
///     (CrossoverBurstMath's maxExitAngleRadians, #194) — explicitly NOT a
///     longer recovery or a chain/cooldown restriction (both rejected per
///     the taxonomy handoff: a safer move shouldn't also be slower to
///     recover from, and cooldowns are a 2K-taxonomy anti-pattern under
///     ADR-0014's reference ranking).
///
/// Default frame data mirrors Crossover's Startup/Active (the same
/// telegraph + transit-swap duration) with a SHORTER Recovery — "comparable
/// to, or slightly shorter than, Crossover's" per the spec, never longer:
///   Startup:  6 ticks  — same visible wind-up as Crossover
///   Active:   3 ticks  — same swap-transit window
///   Recovery: 10 ticks — slightly shorter than Crossover's 12
///   Feint:    4 ticks  — same feint-legal window as Crossover
/// </summary>
public sealed class BehindTheBack : CommittedMove
{
    /// <summary>Default behind-the-back frame data. Tunable per instance if needed.</summary>
    public static readonly MoveFrameData DefaultFrameData =
        new(startupFrames: 6, activeFrames: 3, recoveryFrames: 10, feintWindowFrames: 4);

    /// <summary>
    /// Direction of the lateral burst: +1 = right, -1 = left. Same
    /// body-relative flick-sign convention as Crossover.BurstDirection —
    /// see HandStateResolver's doc for the sign convention.
    /// </summary>
    public float BurstDirection { get; }

    /// <param name="burstDirection">+1 = burst right, -1 = burst left.</param>
    /// <param name="frameData">Override frame data; null uses DefaultFrameData.</param>
    public BehindTheBack(float burstDirection, MoveFrameData? frameData = null)
        : base(id: "behindtheback", displayName: "Behind the Back", frameData: frameData ?? DefaultFrameData)
    {
        BurstDirection = burstDirection;
    }
}
