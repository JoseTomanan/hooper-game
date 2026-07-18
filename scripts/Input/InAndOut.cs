#nullable enable

namespace Hooper.Moves;

/// <summary>
/// The in-and-out — the crossover's twin: an identical Startup telegraph, but
/// the ball never crosses (issue #202, un-deferred 2026-07-16, ADR-0003
/// amendment). Composes <see cref="Crossover"/>'s burst with
/// <see cref="Hesitation"/>'s hand behavior — both halves are shipped and
/// precedented; this move introduces no new burst math and no new wire state.
///
/// ── Why this is not "just a feinted crossover" ──────────────────────────────
/// A feinted Crossover (Startup → Inactive, <see cref="MoveFrameData.
/// FeintWindowFrames"/> &gt; 0) yields zero separation, zero recovery, zero
/// cost — that is the size-up, a free abort. An in-and-out is a move that
/// BEATS the defender: it runs a full Startup → Active → Recovery lifecycle
/// and pays the Active burst's real cost, exactly like a Crossover does. See
/// the ADR-0003 amendment this issue's PR carries for the full reasoning
/// (real half-court ball: the ball never crosses over on an in-and-out — the
/// hand rides the outside of the ball, pushes it toward the fake, and the
/// SAME hand recovers it inside on one continuous dribble cadence — the one
/// physically honest dribble fake, and therefore the realistic replacement
/// for the recall model ADR-0003 flagged as non-realistic for dribble moves).
///
/// ── The cost model (why this does not dominate the crossover) ──────────────
/// A Crossover changes <see cref="Hooper.Player.HandSide"/>, invalidating a
/// committed defender's side-axis steal read (DefensiveResolution.
/// StealSucceeds checks targetHand == holderHand FIRST). An in-and-out does
/// NOT change HandSide (see below), so the defender's side read stays valid —
/// and unlike the crossover, an in-and-out has no ball transit, so it carries
/// none of #196's spatial steal-window exposure. Different risk profiles, not
/// strictly better: faster and safer, but sells less (see the burst-scalar
/// tunable's doc, PlayerController.InAndOutBurstSpeed).
///
/// ── Does not modify HandSide (contrast Crossover/BehindTheBack/BetweenTheLegs) ──
/// Follows the Hesitation precedent: the ball stays in the SAME hand for the
/// whole lifecycle. PlayerController.TickCommittedMoveBehavior's Active-entry
/// branch composes the burst through the SAME CrossoverBurstMath.
/// ComposeActiveVelocity Crossover uses, but skips the HandSide flip that
/// branch applies for Crossover/BehindTheBack/BetweenTheLegs.
///
/// ── Negated flick sign into the shared composition ──────────────────────────
/// BurstDirection below carries the SAME body-relative-flick-toward-the-EMPTY-
/// hand sign convention as Crossover.BurstDirection (needed so
/// RequestBeginMove's wire payload and HandStateResolver.IsCrossover's
/// dispatch stay uniform across the whole family). But CrossoverBurstMath.
/// ComposeActiveVelocity's own `flickSign` parameter is used ONLY as (a) the
/// stationary + neutral-exit fallback lateral direction and (b) the
/// exact-backward-pole tiebreak — both of which must point toward the BALL
/// hand for an in-and-out (the direction of the sell), not the empty hand
/// BurstDirection encodes. PlayerController therefore passes the NEGATED sign
/// into that composition call for this move only — see
/// TickCommittedMoveBehavior's Active-phase switch.
///
/// Default frame data (tunable at construction if needed):
///   Startup:  4 ticks  — quicker than Crossover's 6 (tier-2 grounded: less
///             ball travel is a genuinely faster motion in real ball), and
///             precedented (Hesitation ships at 4 too). The ~33ms telegraph
///             difference from a Crossover is below human reaction (~200ms),
///             so legibility is preserved — the defender still reads the
///             SAME Startup animation either way; only the outcome (does the
///             ball actually cross?) discriminates the two moves.
///   Active:   3 ticks  — matches Crossover's Active exactly: the burst
///             window is the same length regardless of whether the ball
///             physically transits, because the BURST (not the transit) is
///             what the Active phase times.
///   Recovery: 12 ticks — matches Crossover's Recovery exactly: the same
///             commitment cost for the family's Active-phase payoff.
///   Feint:    0 ticks  — a design constant, not a placeholder (mirrors
///             Hesitation/JabStep's identical reasoning). A fake of a fake is
///             incoherent, and ADR-0003's dribble-move recall ban (closed by
///             this issue's amendment) already bars recall on dribble moves;
///             in-and-out is itself the realistic replacement for that
///             recall, so it cannot also carry one.
/// </summary>
public sealed class InAndOut : CommittedMove
{
    /// <summary>Default in-and-out frame data. Tunable per instance if needed.</summary>
    public static readonly MoveFrameData DefaultFrameData =
        new(startupFrames: 4, activeFrames: 3, recoveryFrames: 12, feintWindowFrames: 0);

    /// <summary>
    /// Body-relative flick sign, SAME convention as Crossover.BurstDirection:
    /// +1 = flick toward the player's right, -1 = toward the player's left —
    /// i.e. the direction of the (unrealized) hand swap the flick's motion
    /// resembles, not the direction the burst itself travels. See the class
    /// doc's "Negated flick sign" section for how PlayerController consumes
    /// this at Active-entry.
    /// </summary>
    public float BurstDirection { get; }

    /// <param name="burstDirection">
    /// +1 = flick toward the player's right (empty-hand side), -1 = left.
    /// Same wire convention as Crossover/BehindTheBack/BetweenTheLegs.
    /// </param>
    /// <param name="frameData">Override frame data; null uses DefaultFrameData.</param>
    public InAndOut(float burstDirection, MoveFrameData? frameData = null)
        : base(id: "inandout", displayName: "In-and-Out", frameData: frameData ?? DefaultFrameData)
    {
        BurstDirection = burstDirection;
    }
}
