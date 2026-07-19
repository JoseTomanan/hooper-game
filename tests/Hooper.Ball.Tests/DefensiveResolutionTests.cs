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

    // ═══════════════════════════════════════════════════════════════════════
    // DistanceXZSquared — shared XZ-distance helper (issue #99 folded-forward
    // cleanup from PR #220 review: retires the drift channel where this exact
    // dx*dx+dz*dz computation existed independently in WithinBlockReach and
    // in BallController.ApplyShootLocally's passive contest-proximity calc).
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void DistanceXZSquared_SamePosition_ReturnsZero()
    {
        Assert.Equal(0f, DefensiveResolution.DistanceXZSquared(
            new Vector3(1f, 2f, 3f), new Vector3(1f, 2f, 3f)));
    }

    [Fact]
    public void DistanceXZSquared_KnownOffset_ReturnsSquaredDistance()
    {
        // 3-4-5 triangle in the XZ plane: dx=3, dz=4 -> distance 5 -> squared 25.
        Assert.Equal(25f, DefensiveResolution.DistanceXZSquared(
            new Vector3(3f, 0f, 4f), new Vector3(0f, 0f, 0f)));
    }

    [Fact]
    public void DistanceXZSquared_IgnoresY_SameAsCoplanarPositions()
    {
        // XZ-only: a large Y difference must not change the result — this is
        // the property WithinBlockReach's ball-height gate depends on.
        float withHeightGap = DefensiveResolution.DistanceXZSquared(
            new Vector3(3f, 10f, 4f), new Vector3(0f, 0f, 0f));
        float coplanar = DefensiveResolution.DistanceXZSquared(
            new Vector3(3f, 0f, 4f), new Vector3(0f, 0f, 0f));
        Assert.Equal(coplanar, withHeightGap);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ContestAppliesAt — contest's timing gate (issue #99, ADR-0018 §2)
    //
    // Contest's "release window" collapses to a single tick (shot scatter is
    // computed exactly once, at release — see ContestAppliesAt's own doc).
    // These mirror Succeeds' boundary tests above but against the collapsed
    // single-tick vulnerable interval [releaseTick, releaseTick + 1).
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void ContestAppliesAt_ActiveWindowContainsReleaseTick_ReturnsTrue()
    {
        // Defender's Active window [5,10) contains the release tick 7.
        Assert.True(DefensiveResolution.ContestAppliesAt(
            contestActiveStart: 5, contestActiveEnd: 10, releaseTick: 7));
    }

    [Fact]
    public void ContestAppliesAt_ReleaseTickEqualsActiveStart_ReturnsTrue()
    {
        // Entering Active on the exact release tick still connects (the
        // interval's start bound is inclusive).
        Assert.True(DefensiveResolution.ContestAppliesAt(
            contestActiveStart: 7, contestActiveEnd: 15, releaseTick: 7));
    }

    [Fact]
    public void ContestAppliesAt_ReleaseTickEqualsActiveEnd_ReturnsFalse()
    {
        // Half-open convention: an Active window ending exactly on the
        // release tick has already closed — adjacent, not overlapping.
        Assert.False(DefensiveResolution.ContestAppliesAt(
            contestActiveStart: 0, contestActiveEnd: 7, releaseTick: 7));
    }

    [Fact]
    public void ContestAppliesAt_ReleaseTickBeforeActiveStarts_ReturnsFalse()
    {
        // Committed too late — the defender's Active hasn't opened yet when
        // the shot released.
        Assert.False(DefensiveResolution.ContestAppliesAt(
            contestActiveStart: 10, contestActiveEnd: 18, releaseTick: 7));
    }

    [Fact]
    public void ContestAppliesAt_ReleaseTickAfterActiveEnds_ReturnsFalse()
    {
        // Committed too early — the defender's Active already ended before
        // the shot released.
        Assert.False(DefensiveResolution.ContestAppliesAt(
            contestActiveStart: 0, contestActiveEnd: 5, releaseTick: 7));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ContestMoveFactor — the composed ADDITIONAL accuracy factor
    // (issue #99, ADR-0018 §2: contest composes on top of the passive
    // proximity term, ADR-0009 / #65, it never replaces it)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void ContestMoveFactor_ContestActiveAtRelease_ReturnsOnePlusK()
    {
        Assert.Equal(1.5f, DefensiveResolution.ContestMoveFactor(
            contestActiveAtRelease: true, contestMoveScatterK: 0.5f));
    }

    [Fact]
    public void ContestMoveFactor_ContestNotActiveAtRelease_ReturnsOne()
    {
        // No active contest -> factor is exactly 1 (no effect) -> a shot with
        // no contest behaves identically to before #99 existed.
        Assert.Equal(1f, DefensiveResolution.ContestMoveFactor(
            contestActiveAtRelease: false, contestMoveScatterK: 0.5f));
    }

    [Fact]
    public void ContestMoveFactor_ComposesMultiplicativelyOnTopOfPassiveProximity()
    {
        // ADR-0018 §2: contest composes an ADDITIONAL factor ON TOP OF the
        // existing passive proximity term (ADR-0009 / #65's contestFactor),
        // never replacing it. Demonstrate the composition arithmetic directly:
        // a passive proximity factor of 1.5 (e.g. a defender partway inside
        // ContestRange) combined with an active contest (K=0.5) must MULTIPLY
        // to 2.25, not add to 2.0 — guards against a double-counting or
        // additive-composition regression.
        const float passiveContestFactor = 1.5f;
        float contestMoveFactor = DefensiveResolution.ContestMoveFactor(
            contestActiveAtRelease: true, contestMoveScatterK: 0.5f);

        float composed = passiveContestFactor * contestMoveFactor;

        Assert.Equal(2.25f, composed, precision: 5);
        Assert.NotEqual(2.0f, composed); // the additive-composition regression this guards against
    }

    [Fact]
    public void ContestMoveFactor_NoActiveContest_PassiveTermUnchanged()
    {
        // With no active contest, composing with the passive term must be a
        // no-op — a shot contested only passively (no committed move) sees
        // exactly the pre-#99 accuracy multiplier.
        const float passiveContestFactor = 1.5f;
        float contestMoveFactor = DefensiveResolution.ContestMoveFactor(
            contestActiveAtRelease: false, contestMoveScatterK: 0.5f);

        Assert.Equal(passiveContestFactor, passiveContestFactor * contestMoveFactor);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // HeldStealSucceeds — Held-ball steal vulnerable window (issue #206,
    // ADR-0018 Amendment 2026-07-19, Option A pump-fake-window variant).
    //
    // Timing-only (no hand-side axis — see the method's own doc for the
    // ADR-0014 tier-2 self-resolution), so this is a thin composition over
    // the shared Succeeds predicate. The boundary matrix here exists so the
    // half-open convention and the delegation itself are pinned independent
    // of Succeeds' own tests, per the campaign's "full xUnit boundary matrix"
    // requirement.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void HeldStealSucceeds_ActiveInsideVulnerableWindow_ReturnsTrue()
    {
        // Defender's Active window [10,18) sits fully inside the holder's
        // JumpShot Startup window [0,18) — a well-timed poke during the gather.
        Assert.True(DefensiveResolution.HeldStealSucceeds(
            activeStart: 10, activeEnd: 18, vulnStart: 0, vulnEnd: 18));
    }

    [Fact]
    public void HeldStealSucceeds_SingleTickOverlap_ReturnsTrue()
    {
        // Smallest possible overlap: [14,22) ∩ [18,26) = [18,22) — real.
        Assert.True(DefensiveResolution.HeldStealSucceeds(
            activeStart: 14, activeEnd: 22, vulnStart: 18, vulnEnd: 26));
    }

    [Fact]
    public void HeldStealSucceeds_ActiveEntirelyBeforeWindow_ReturnsFalse()
    {
        // Defender's Active window ends before the holder even begins the
        // JumpShot's Startup — no shot attempt exists yet to be vulnerable.
        Assert.False(DefensiveResolution.HeldStealSucceeds(
            activeStart: 0, activeEnd: 5, vulnStart: 10, vulnEnd: 28));
    }

    [Fact]
    public void HeldStealSucceeds_ActiveEntirelyAfterWindow_ReturnsFalse()
    {
        // Defender reacted too late — the vulnerable window (e.g. a short
        // feint-Recovery tail) already closed before Active opened.
        Assert.False(DefensiveResolution.HeldStealSucceeds(
            activeStart: 30, activeEnd: 38, vulnStart: 10, vulnEnd: 28));
    }

    [Fact]
    public void HeldStealSucceeds_AdjacentIntervals_ActiveEndsAtVulnStart_ReturnsFalse()
    {
        // Half-open boundary: Active ends exactly when the vulnerable window
        // opens — [5,10) vs [10,20) share only the boundary tick, empty overlap.
        Assert.False(DefensiveResolution.HeldStealSucceeds(
            activeStart: 5, activeEnd: 10, vulnStart: 10, vulnEnd: 20));
    }

    [Fact]
    public void HeldStealSucceeds_AdjacentIntervals_VulnEndsAtActiveStart_ReturnsFalse()
    {
        // Reversed adjacency: the vulnerable window closes exactly when
        // Active opens.
        Assert.False(DefensiveResolution.HeldStealSucceeds(
            activeStart: 20, activeEnd: 28, vulnStart: 10, vulnEnd: 20));
    }

    [Fact]
    public void HeldStealSucceeds_ActiveFullyContainsVulnerableWindow_ReturnsTrue()
    {
        // A long Active window (should not occur with StealMove's real 8-tick
        // ActiveFrames, but the pure predicate must still be correct for any
        // interval shape) fully wraps a short feint-Recovery tail.
        Assert.True(DefensiveResolution.HeldStealSucceeds(
            activeStart: 0, activeEnd: 40, vulnStart: 20, vulnEnd: 28));
    }

    [Fact]
    public void HeldStealSucceeds_VulnerableWindowFullyContainsActive_ReturnsTrue()
    {
        // The reverse containment — Active fully inside the (longer) Startup
        // window.
        Assert.True(DefensiveResolution.HeldStealSucceeds(
            activeStart: 5, activeEnd: 13, vulnStart: 0, vulnEnd: 18));
    }
}
