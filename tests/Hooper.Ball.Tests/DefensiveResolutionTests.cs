using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace Hooper.Ball.Tests;

// #96 — steal as a committed move + server-authoritative resolution.
//
// ADR-0018 §1: DefensiveResolution.Succeeds is the shared overlap predicate
// all three M10 defensive committed moves call. Unit-tested pure because it
// must be bit-identical on server and every predicting client (ADR-0004).
//
// ADR-0018 §2: the steal's two-axis read — *when* (dribble phase in the
// exposed band) AND *which hand* (defender targets the authoritative HandSide).
// Both axes are tested independently and in combination.
public class DefensiveResolutionTests
{
    // ═══════════════════════════════════════════════════════════════════════
    // Succeeds — shared half-open interval overlap predicate (ADR-0018 §1)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Succeeds_ActiveOverlapsVuln_ReturnsTrue()
    {
        // Canonical overlap: [5,10) ∩ [7,12) = [7,10) → non-empty → true.
        Assert.True(DefensiveResolution.Succeeds(
            activeStart: 5, activeEnd: 10, vulnStart: 7, vulnEnd: 12));
    }

    [Fact]
    public void Succeeds_ActiveContainsVuln_ReturnsTrue()
    {
        // Active window fully wraps the vulnerable window.
        Assert.True(DefensiveResolution.Succeeds(
            activeStart: 5, activeEnd: 15, vulnStart: 7, vulnEnd: 12));
    }

    [Fact]
    public void Succeeds_VulnContainsActive_ReturnsTrue()
    {
        // Vulnerable window fully wraps the active window.
        Assert.True(DefensiveResolution.Succeeds(
            activeStart: 7, activeEnd: 9, vulnStart: 5, vulnEnd: 12));
    }

    [Fact]
    public void Succeeds_SingleTickOverlap_ReturnsTrue()
    {
        // Smallest possible overlap: [5,8) ∩ [7,10) = [7,8) — one tick.
        Assert.True(DefensiveResolution.Succeeds(
            activeStart: 5, activeEnd: 8, vulnStart: 7, vulnEnd: 10));
    }

    [Fact]
    public void Succeeds_ActiveBeforeVuln_ReturnsFalse()
    {
        // Defender guessed too early: [5,7) ends before [10,12) starts.
        Assert.False(DefensiveResolution.Succeeds(
            activeStart: 5, activeEnd: 7, vulnStart: 10, vulnEnd: 12));
    }

    [Fact]
    public void Succeeds_ActiveAfterVuln_ReturnsFalse()
    {
        // Defender reacted too late: [10,12) starts after [5,7) ends.
        Assert.False(DefensiveResolution.Succeeds(
            activeStart: 10, activeEnd: 12, vulnStart: 5, vulnEnd: 7));
    }

    [Fact]
    public void Succeeds_AdjacentIntervals_ReturnsFalse()
    {
        // Half-open semantics: [5,7) and [7,10) share the boundary tick 7
        // but the overlap is empty — [7,7) — so this must fail.
        // A defender whose Active ends exactly when the vulnerable window
        // opens is a single-tick miss; the predicate is strict.
        Assert.False(DefensiveResolution.Succeeds(
            activeStart: 5, activeEnd: 7, vulnStart: 7, vulnEnd: 10));
    }

    [Fact]
    public void Succeeds_AdjacentIntervals_OtherOrder_ReturnsFalse()
    {
        // Reversed adjacency: [7,10) starts exactly when [5,7) ends.
        Assert.False(DefensiveResolution.Succeeds(
            activeStart: 7, activeEnd: 10, vulnStart: 5, vulnEnd: 7));
    }

    [Fact]
    public void Succeeds_ActiveEndsExactlyAtVulnEnd_ReturnsTrue()
    {
        // Moved from BlockMoveTests (issue #216 original body row 7, test
        // dedup) — a distinct boundary configuration none of the cases above
        // cover: active's END coincides with vuln's END ([15,22) vs [12,22)),
        // unlike Succeeds_AdjacentIntervals*'s shared START boundary. The
        // half-open predicate is asymmetric here: sharing a START boundary
        // excludes (both those tests return false), but sharing an END
        // boundary while everything else overlaps still succeeds — overlap
        // [15,22) is non-empty. This is the real "last valid tick" case a
        // defensive move relies on: connecting on the exact final tick of
        // the vulnerable window it targets.
        Assert.True(DefensiveResolution.Succeeds(
            activeStart: 15, activeEnd: 22, vulnStart: 12, vulnEnd: 22));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // StealSucceeds — two-axis steal read (ADR-0018 §2)
    //
    // Axis 1: dribble phase in the exposed band (Phase ≈ 0.5, ball low)
    // Axis 2: defender targets the authoritative HandSide (ADR-0012)
    //
    // Both must be satisfied; failing either axis is a miss.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void StealSucceeds_PhaseInBandCorrectHand_ReturnsTrue()
    {
        // Perfect read: phase is in the exposed band AND the defender
        // targets the hand that actually holds the ball.
        Assert.True(DefensiveResolution.StealSucceeds(
            phase: 0.5f,
            loExposed: 0.35f, hiExposed: 0.65f,
            targetHand: HandSide.Left, holderHand: HandSide.Left));
    }

    [Fact]
    public void StealSucceeds_PhaseAtLoBoundary_ReturnsTrue()
    {
        // Phase exactly at the lo boundary is inside the inclusive [lo, hi].
        Assert.True(DefensiveResolution.StealSucceeds(
            phase: 0.35f,
            loExposed: 0.35f, hiExposed: 0.65f,
            targetHand: HandSide.Right, holderHand: HandSide.Right));
    }

    [Fact]
    public void StealSucceeds_PhaseAtHiBoundary_ReturnsTrue()
    {
        // Phase exactly at the hi boundary is inside the inclusive [lo, hi].
        Assert.True(DefensiveResolution.StealSucceeds(
            phase: 0.65f,
            loExposed: 0.35f, hiExposed: 0.65f,
            targetHand: HandSide.Left, holderHand: HandSide.Left));
    }

    [Fact]
    public void StealSucceeds_PhaseInBandWrongHand_ReturnsFalse()
    {
        // Wrong-side read: phase is in the band (timing correct) but
        // the defender reached for the wrong hand (ADR-0018 §2 hand axis).
        Assert.False(DefensiveResolution.StealSucceeds(
            phase: 0.5f,
            loExposed: 0.35f, hiExposed: 0.65f,
            targetHand: HandSide.Right, holderHand: HandSide.Left));
    }

    [Fact]
    public void StealSucceeds_PhaseOutsideBandCorrectHand_ReturnsFalse()
    {
        // Mistimed read: defender reached for the correct hand, but the
        // ball is back at hand-height (phase ≈ 0.1, not exposed).
        Assert.False(DefensiveResolution.StealSucceeds(
            phase: 0.1f,
            loExposed: 0.35f, hiExposed: 0.65f,
            targetHand: HandSide.Left, holderHand: HandSide.Left));
    }

    [Fact]
    public void StealSucceeds_PhaseJustBelowLo_ReturnsFalse()
    {
        // Phase just below the exposed band — a near-miss on timing.
        Assert.False(DefensiveResolution.StealSucceeds(
            phase: 0.34f,
            loExposed: 0.35f, hiExposed: 0.65f,
            targetHand: HandSide.Left, holderHand: HandSide.Left));
    }

    [Fact]
    public void StealSucceeds_PhaseJustAboveHi_ReturnsFalse()
    {
        // Phase just above the exposed band — the ball is rising back to hand.
        Assert.False(DefensiveResolution.StealSucceeds(
            phase: 0.66f,
            loExposed: 0.35f, hiExposed: 0.65f,
            targetHand: HandSide.Left, holderHand: HandSide.Left));
    }

    [Fact]
    public void StealSucceeds_PhaseOutsideBandWrongHand_ReturnsFalse()
    {
        // Both axes wrong: mistimed AND wrong hand.
        Assert.False(DefensiveResolution.StealSucceeds(
            phase: 0.1f,
            loExposed: 0.35f, hiExposed: 0.65f,
            targetHand: HandSide.Right, holderHand: HandSide.Left));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GoLoose transition — BallStateMachine contract
    //
    // The steal's on-success action is StateMachine.GoLoose() from Dribbling
    // (ADR-0008 §Amendment 2026-06-30). We pin the pure state-machine
    // contract here so a regression is caught without the Node-bound
    // BallController — the server-side call site cannot be tested headlessly.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void GoLoose_FromDribbling_Succeeds()
    {
        // The successful steal path: Dribbling → Loose (ADR-0008, issue #97).
        var sm = new BallStateMachine(initialHolderPeerId: 1);
        sm.StartDribble();
        bool ok = sm.GoLoose();
        Assert.True(ok);
    }

    [Fact]
    public void GoLoose_FromDribbling_ClearsHolder()
    {
        // After a steal the ball is loose with no holder; the scramble
        // awards possession via the existing proximity contest (ADR-0008 §2).
        var sm = new BallStateMachine(initialHolderPeerId: 1);
        sm.StartDribble();
        sm.GoLoose();
        Assert.Equal(0, sm.HolderPeerId);
    }

    [Fact]
    public void GoLoose_FromDribbling_StateIsLoose()
    {
        // Confirm the transition to Loose — the scramble entry point.
        var sm = new BallStateMachine(initialHolderPeerId: 1);
        sm.StartDribble();
        sm.GoLoose();
        Assert.Equal(BallState.Loose, sm.Current);
    }

    [Fact]
    public void GoLoose_CalledTwice_SecondCallReturnsFalse()
    {
        // GoLoose is idempotent via the edge guard: once Loose the second
        // call returns false — exactly-once semantics for the steal path.
        var sm = new BallStateMachine(initialHolderPeerId: 1);
        sm.StartDribble();
        sm.GoLoose();
        bool second = sm.GoLoose();
        Assert.False(second);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ShouldForceInactive — reconcile gate, StealMove flavour
    //
    // A client that predicted a StealMove (machine in Active) but the server
    // rejected it (still Inactive) must snap back via ForceState. This is the
    // same reconcile logic CommittedMoveMachineTests already proves; this test
    // documents it explicitly for the StealMove path so a future refactor
    // cannot accidentally exclude steal from reconciliation (ADR-0002).
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void ShouldForceInactive_ClientStealActiveServerInactive_ReturnsTrue()
    {
        // Client predicted a steal; server says Inactive (the defender was
        // still mid-Recovery when Begin() arrived server-side — the steal
        // was rejected). ShouldForceInactive fires → ForceState(Inactive)
        // snaps the client back, mirroring the crossover misprediction path.
        bool result = CommittedMoveMachine.ShouldForceInactive(
            localPhase: MovePhase.Active,
            localIsActive: true,
            serverPhase: MovePhase.Inactive);

        Assert.True(result);
    }

    [Fact]
    public void ShouldForceInactive_ClientStealStartupServerInactive_ReturnsFalse()
    {
        // Startup grace window: the client's Begin() takes ~1 RTT to reach the
        // server; during that window the client is in Startup and the server is
        // still Inactive. ShouldForceInactive must NOT fire during Startup —
        // the grace window that prevents flickering every legitimate steal.
        bool result = CommittedMoveMachine.ShouldForceInactive(
            localPhase: MovePhase.Startup,
            localIsActive: true,
            serverPhase: MovePhase.Inactive);

        Assert.False(result);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // WithinBlockReach — proximity/reach gate on block resolution (issue #214)
    //
    // ADR-0014 real-ball bar: a block only connects within arm's reach of the
    // ball's release point — DefensiveResolution.Succeeds (the timing overlap
    // above) is silent on WHERE the defender is, which let a defender anywhere
    // on the court "block" on timing alone. This predicate is the missing
    // spatial axis, composed with (not replacing) Succeeds by the caller
    // (BallController.ResolveBlockAttempts): both must hold for a block to
    // connect.
    //
    // XZ-only distance, mirroring the existing ContestRange/#65 "arm's-length
    // closeout" proximity term (same physical concept, same codebase
    // convention) rather than a full 3D distance to the ball — the ball's
    // height climbs quickly after release while the defender stays grounded,
    // so a 3D distance would make the gate collapse to near-zero within a
    // couple of ticks regardless of how close the defender actually is.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void WithinBlockReach_DefenderAtBallPosition_ReturnsTrue()
    {
        // Zero distance is trivially within any non-negative reach radius.
        Assert.True(DefensiveResolution.WithinBlockReach(
            defenderPosition: new Vector3(0f, 0f, 5f),
            ballPosition: new Vector3(0f, 0f, 5f),
            reachRadius: 2.2f));
    }

    [Fact]
    public void WithinBlockReach_DistanceInsideRadius_ReturnsTrue()
    {
        // 1 m away, well inside a 2.2 m reach.
        Assert.True(DefensiveResolution.WithinBlockReach(
            defenderPosition: new Vector3(1f, 0f, 5f),
            ballPosition: new Vector3(0f, 0f, 5f),
            reachRadius: 2.2f));
    }

    [Fact]
    public void WithinBlockReach_DistanceExactlyAtRadius_ReturnsTrue()
    {
        // Boundary is inclusive, matching StealSucceeds' inclusive-bound
        // convention ([loExposed, hiExposed] — a defender exactly at the
        // edge of their reach still connects).
        Assert.True(DefensiveResolution.WithinBlockReach(
            defenderPosition: new Vector3(2.2f, 0f, 5f),
            ballPosition: new Vector3(0f, 0f, 5f),
            reachRadius: 2.2f));
    }

    [Fact]
    public void WithinBlockReach_DistanceBeyondRadius_ReturnsFalse()
    {
        // 6 m away (e.g. defender at the far baseline, shooter at mid-court) —
        // the exact "across the court" case issue #214 exists to gate out.
        Assert.False(DefensiveResolution.WithinBlockReach(
            defenderPosition: new Vector3(0f, 0f, -1f),
            ballPosition: new Vector3(0f, 0f, 5f),
            reachRadius: 2.2f));
    }

    [Fact]
    public void WithinBlockReach_JustBeyondRadius_ReturnsFalse()
    {
        // A near-miss just past the boundary — pins the strict inequality.
        Assert.False(DefensiveResolution.WithinBlockReach(
            defenderPosition: new Vector3(2.21f, 0f, 5f),
            ballPosition: new Vector3(0f, 0f, 5f),
            reachRadius: 2.2f));
    }

    [Fact]
    public void WithinBlockReach_IgnoresVerticalSeparation_ReturnsTrue()
    {
        // XZ-only distance (matching ContestRange's own convention): a large
        // height difference between a grounded defender and a ball that has
        // already climbed several metres into its arc must NOT by itself
        // fail the reach gate — only horizontal distance counts. Without
        // this the gate would collapse to near-zero within a couple of
        // ticks of every release, regardless of true proximity.
        Assert.True(DefensiveResolution.WithinBlockReach(
            defenderPosition: new Vector3(0f, 0f, 5f),
            ballPosition: new Vector3(0f, 3f, 5f),
            reachRadius: 2.2f));
    }
}
