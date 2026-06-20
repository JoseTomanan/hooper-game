using Godot;
using Hooper.Ball;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for ReboundContest — the pure "who recovers the loose ball" rule
/// (ADR-0008, issue #48). Runs headlessly (ADR-0004): the contest is a
/// deterministic function of positions, which is exactly what lets clients
/// predict a pickup and reconcile to the server without divergence, so it is
/// proven here without a live engine.
///
/// ── Test naming convention ───────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// One logical assertion per test.
/// </summary>
public class ReboundContestTests
{
    private static ReboundContest.Candidate At(int peerId, float x, float z) =>
        new(peerId, new Vector3(x, 0f, z));

    // ═════════════════════════════════════════════════════════════════════
    // Nobody in reach
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_NoCandidates_ReturnsZero()
    {
        int winner = ReboundContest.Resolve(Vector3.Zero, new ReboundContest.Candidate[0], 1.0f);
        Assert.Equal(0, winner);
    }

    [Fact]
    public void Resolve_AllOutsideRadius_ReturnsZero()
    {
        var candidates = new[] { At(1, 5f, 0f), At(2, 0f, 5f) };
        int winner = ReboundContest.Resolve(Vector3.Zero, candidates, 1.0f);
        Assert.Equal(0, winner);
    }

    [Fact]
    public void Resolve_JustOutsideRadius_ReturnsZero()
    {
        // Distance 1.001 with radius 1.0 — must not count as in reach.
        var candidates = new[] { At(1, 1.001f, 0f) };
        int winner = ReboundContest.Resolve(Vector3.Zero, candidates, 1.0f);
        Assert.Equal(0, winner);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Single recoverer in reach
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_SinglePlayerInRadius_ReturnsThatPlayer()
    {
        var candidates = new[] { At(7, 0.5f, 0f) };
        int winner = ReboundContest.Resolve(Vector3.Zero, candidates, 1.0f);
        Assert.Equal(7, winner);
    }

    [Fact]
    public void Resolve_AtExactRadius_CountsAsInReach()
    {
        // Boundary: distance exactly equal to the radius recovers (<=, not <).
        var candidates = new[] { At(3, 1.0f, 0f) };
        int winner = ReboundContest.Resolve(Vector3.Zero, candidates, 1.0f);
        Assert.Equal(3, winner);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Both in reach — nearer wins
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_BothInRadius_NearerWins()
    {
        var candidates = new[] { At(1, 0.9f, 0f), At(2, 0.2f, 0f) };
        int winner = ReboundContest.Resolve(Vector3.Zero, candidates, 1.0f);
        Assert.Equal(2, winner);
    }

    [Fact]
    public void Resolve_NearerWins_RegardlessOfCandidateOrder()
    {
        // Same contest as above, candidates supplied in the other order — the
        // winner must not depend on iteration order.
        var candidates = new[] { At(2, 0.2f, 0f), At(1, 0.9f, 0f) };
        int winner = ReboundContest.Resolve(Vector3.Zero, candidates, 1.0f);
        Assert.Equal(2, winner);
    }

    [Fact]
    public void Resolve_OnlyOneOfTwoInRadius_FartherOutOfReachLoses()
    {
        // Peer 2 is nearer but outside the radius; peer 1 is in reach and wins.
        var candidates = new[] { At(1, 0.8f, 0f), At(2, 3f, 0f) };
        int winner = ReboundContest.Resolve(Vector3.Zero, candidates, 1.0f);
        Assert.Equal(1, winner);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Exact tie — deterministic tiebreak (lower peer id)
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_ExactDistanceTie_LowerPeerIdWins()
    {
        // Equidistant from the ball; lower peer id breaks the tie so the result
        // is deterministic on every peer regardless of order.
        var candidates = new[] { At(5, 0.5f, 0f), At(3, -0.5f, 0f) };
        int winner = ReboundContest.Resolve(Vector3.Zero, candidates, 1.0f);
        Assert.Equal(3, winner);
    }

    [Fact]
    public void Resolve_ExactDistanceTie_LowerPeerIdWins_RegardlessOfOrder()
    {
        var candidates = new[] { At(3, -0.5f, 0f), At(5, 0.5f, 0f) };
        int winner = ReboundContest.Resolve(Vector3.Zero, candidates, 1.0f);
        Assert.Equal(3, winner);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Height is ignored — floor-plane (XZ) distance only (regression #48)
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_PlayerDirectlyAboveBall_YIgnored_InReach()
    {
        // Regression for the full-3D-distance bug: a player standing on a
        // resting ball (capsule centre Y≈1.0, ball Y≈0.12) was rejected by
        // the radius gate because the vertical gap alone was ~0.88 m — larger
        // than the effective XZ reach of a 1.0 m PickupRadius. Fix: drop Y
        // before measuring so PickupRadius means "metres of court reach."
        //
        // Player is directly above ball (XZ offset = 0). Old 3D metric:
        // distSq = 1.0² = 1.0, radius 0.5 → rejected (0). New XZ metric:
        // distSq = 0 → wins.
        var ball = new Vector3(0f, 0f, 0f);
        var candidates = new[] { new ReboundContest.Candidate(1, new Vector3(0f, 1.0f, 0f)) };
        int winner = ReboundContest.Resolve(ball, candidates, 0.5f);
        Assert.Equal(1, winner);
    }

    [Fact]
    public void Resolve_RealWorldRestingBall_PlayerNearby_InReach()
    {
        // Concrete real-world scenario: ball resting at Y=0.12 (BallRadius),
        // player capsule centre at Y=1.0, 0.5 m apart in X.
        // Old 3D distSq = 0.5²+0.88² ≈ 1.024 > radiusSq=1.0 → nobody wins.
        // New XZ distSq = 0.25 → peer 1 wins.
        var ball = new Vector3(0f, 0.12f, 0f);
        var candidates = new[] { new ReboundContest.Candidate(1, new Vector3(0.5f, 1.0f, 0f)) };
        int winner = ReboundContest.Resolve(ball, candidates, 1.0f);
        Assert.Equal(1, winner);
    }
}
