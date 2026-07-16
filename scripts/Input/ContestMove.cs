#nullable enable

namespace Hooper.Moves;

/// <summary>
/// The on-ball contest — the third M10 defensive committed move (issue #99,
/// epic #89, ADR-0018 §2).
///
/// Unlike steal (#96) and block (#98), contest is NOT a binary succeed/fail
/// overlap move. ADR-0018 §2 defines it as "a committed amplifier of the
/// passive scatter": a committed contest whose Active window overlaps the
/// shot's release tick applies an ADDITIONAL, discrete accuracy factor ON TOP
/// OF the existing passive proximity scatter (ADR-0009 / #65's
/// <c>contestFactor</c>) — never replacing it. Actively pressuring a shot
/// (closing out, hands up, timed to the release) is strictly stronger than
/// merely standing near the shooter, but it spends a committed move —
/// Recovery — to do so, so over-contesting is itself punishable.
///
/// ── ADR-0003 legibility commitment ──────────────────────────────────────
/// The contest is NOT an instant hands-up toggle (move-and-strike, ADR-0003
/// anti-goal). Startup telegraphs the commitment to both players; Recovery
/// is punishable on a bad commit — a badly-timed contest is a blow-by
/// opportunity (#100), exactly like a whiffed steal or block.
///
/// ── Why timing-only, no spatial reach gate (contrast with block, #214) ───
/// Block was amended (2026-07-16) to ALSO require BlockReachRadius proximity,
/// because block grants a binary success and a defender anywhere on the
/// court could otherwise "block" on pure timing. Contest never grants a
/// binary success — it only scales an ALREADY-proximity-gated passive term:
/// ADR-0009 / #65's <c>contestFactor</c> already requires the defender be
/// within <c>ContestRange</c> before it has any effect at all. Composing a
/// second, independent spatial gate on top would double-count proximity with
/// no basis in ADR-0018 §2's text, which describes contest's composition as
/// purely a function of Active-overlaps-release-window. If a future review
/// wants contest to ALSO require close-range presence independent of the
/// passive term, that is a new decision, not this one.
///
/// ── Why the composition resolves as a single-tick overlap ────────────────
/// Shot scatter (ADR-0009) is computed exactly ONCE, at the moment
/// <c>BallController.ApplyShootLocally</c> releases the ball — there is no
/// ongoing "shot in flight, keep contesting it" recomputation the way
/// block's multi-tick grace window has. So the "release window" ADR-0018 §2
/// refers to for contest collapses to the single release tick:
/// <c>DefensiveResolution.ContestAppliesAt(contestActiveStart,
/// contestActiveEnd, releaseTick)</c> — a defender is either in their
/// contest's Active phase on the exact tick the shot releases, or they are
/// not. See <c>BallController.ApplyShootLocally</c>'s contest composition
/// comment for the call site, and <c>DefensiveResolution.ContestAppliesAt</c>'s
/// own doc for the full reasoning.
///
/// ── Frame data (provisional, tuning deferred to #104) ─────────────────────
/// Startup:  6 ticks — a hands-up closeout plant is a smaller, faster
///           commitment than block's full swat wind-up (BlockMove Startup=10)
///           or steal's swipe wind-up (StealMove Startup=8); still a real,
///           visible telegraph (ADR-0003), not instant.
/// Active:   8 ticks — bounded by BlockGraceTicks (default 10, ADR-0018 §3: a
///           defensive Active must not be wider than the vulnerable window it
///           targets) so a long active phase can't paper over bad timing.
/// Recovery: 20 ticks — matches JumpShot/StealMove/BlockMove's Recovery so a
///           bad contest commit (early or late) is as punishable as a missed
///           shot or a whiffed steal/block (ADR-0018 §3 reaction tilt).
/// Feint:    4 ticks — same convention as StealMove/BlockMove: feintable in
///           the first 4 Startup frames, zero recovery cost (abort to
///           Inactive) so an obvious fake is cheaper than a committed whiff.
/// </summary>
public sealed class ContestMove : CommittedMove
{
    /// <summary>Default contest frame data. Tunable per instance if needed.</summary>
    public static readonly MoveFrameData DefaultFrameData =
        new(startupFrames: 6, activeFrames: 8, recoveryFrames: 20, feintWindowFrames: 4);

    /// <param name="frameData">Override frame data; null uses DefaultFrameData.</param>
    public ContestMove(MoveFrameData? frameData = null)
        : base(id: "contest", displayName: "Contest", frameData: frameData ?? DefaultFrameData)
    {
    }
}
