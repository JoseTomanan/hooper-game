#nullable enable

using Hooper.Ball;

namespace Hooper.Moves;

/// <summary>
/// Pure legality gate for beginning a <see cref="JabStep"/> (issue #200) —
/// mirrors <c>LayupRangeResolver</c>/<c>EuroStepReadResolver</c>'s pattern:
/// the decision itself is pure and unit-tested; PlayerController.
/// BeginCommittedMove is the thin Node-side glue that reads GetBall()?.State
/// into this function's parameter.
///
/// ── The gate, and why it is the INVERSE of the existing dead-dribble gate ──
/// BeginCommittedMove already refuses Crossover/Hesitation/BehindTheBack/
/// BetweenTheLegs/RetreatDribble/DriveGather/EuroStep FROM a Held possession
/// (dead or live) — those are all genuine dribble/drive moves that need a
/// live bounce to act on. The jab is the opposite case: it is triple
/// threat's OWN stance bait (ADR-0008 #193 amendment), so it must be legal
/// FROM Held (dead or live — BallState.Held covers both; HasDribbled is what
/// distinguishes live/dead Held, and the jab does not care which) and
/// illegal FROM Dribbling, where Hesitation/hand-fake (#86) already covers
/// the equivalent bait off a live dribble. Two separate real-ball moves,
/// gated on opposite sides of the same BallState boundary — not a
/// contradiction, a taxonomy (real half-court ball, ADR-0014 tier 2: you jab
/// from a stationary stance, you hesitate off a live bounce; they are not
/// interchangeable footwork).
///
/// A null <paramref name="ballState"/> (no ball reference resolvable, e.g. a
/// defender with no ball at all) is treated as legal — this predicate only
/// ever runs behind PlayerController's own <c>IsBallHolder</c> access check
/// (see PlayerController.SampleMoveInput's "move_jab" dispatch), so a null
/// state here is not a real bypass: nothing reaches this gate without
/// already holding the ball.
/// </summary>
public static class JabStepLegalityResolver
{
    /// <returns>
    /// True unless <paramref name="ballState"/> is <see cref="BallState.Dribbling"/>.
    /// </returns>
    public static bool IsLegal(BallState? ballState) => ballState != BallState.Dribbling;
}
