using System.Collections.Generic;
using Godot;

namespace Hooper.Ball;

/// <summary>
/// Pure, deterministic resolution of "who recovers a loose ball" (ADR-0008,
/// issue #48). No Godot Node, no engine singletons — the same test seam as
/// BallStateMachine / ShotArc, so the rebound rule is unit-testable headlessly
/// (ADR-0004). BallController feeds it the ball position and each player's
/// position every loose tick and acts on the returned peer id.
///
/// ── Why a pure function, and why determinism matters here ─────────────────
/// The loose-ball contest is PREDICTED on every peer, not just the server
/// (issue #48): a client predicts its own pickup immediately so the recoverer
/// regains control with zero input lag, then reconciles to the server's
/// broadcast like any other ball state. For that prediction to be clean, the
/// rule must be a deterministic function of positions — given the same ball
/// and player positions, every peer must compute the same winner. "Nearer
/// wins" is a total order on a single scalar (squared distance), so it is;
/// the only place two candidates can tie is exact-equal distance, which we
/// break by lower peer id so even that degenerate case is deterministic
/// rather than dependent on iteration/collection order.
/// </summary>
public static class ReboundContest
{
    /// <summary>
    /// One player's candidacy for a loose ball: their peer id and current
    /// world position. A readonly struct (not a tuple) so call sites and tests
    /// read clearly and there is no allocation per candidate.
    /// </summary>
    public readonly struct Candidate
    {
        public readonly int PeerId;
        public readonly Vector3 Position;

        public Candidate(int peerId, Vector3 position)
        {
            PeerId = peerId;
            Position = position;
        }
    }

    /// <summary>
    /// Resolves which player recovers the loose ball.
    /// </summary>
    /// <param name="ballPosition">Current world position of the loose ball.</param>
    /// <param name="candidates">All players eligible to recover (typically both peers).</param>
    /// <param name="pickupRadius">
    /// Max distance (metres) a player may be from the ball and still recover it.
    /// A candidate outside this radius cannot win.
    /// </param>
    /// <returns>
    /// The winning peer id, or 0 if no candidate is within the pickup radius.
    /// 0 is never a valid peer id (Godot multiplayer convention), so the caller
    /// reads 0 as "nobody recovered it this tick — keep the ball loose."
    /// </returns>
    public static int Resolve(Vector3 ballPosition, IReadOnlyList<Candidate> candidates, float pickupRadius)
    {
        // Compare squared distances to avoid a sqrt per candidate; the radius
        // gate squares too. Identical math on every peer (no transcendental
        // functions whose last-bit results could differ across builds), which
        // is what keeps the predicted winner bit-identical to the server's.
        float radiusSq = pickupRadius * pickupRadius;

        int winner = 0;
        float bestDistSq = float.PositiveInfinity;

        foreach (Candidate c in candidates)
        {
            float distSq = (c.Position - ballPosition).LengthSquared();
            if (distSq > radiusSq) continue; // out of reach — cannot recover

            // Nearer wins. On an exact tie, lower peer id wins so the result
            // does not depend on the order candidates were supplied in.
            if (distSq < bestDistSq || (distSq == bestDistSq && c.PeerId < winner))
            {
                bestDistSq = distSq;
                winner = c.PeerId;
            }
        }

        return winner;
    }
}
