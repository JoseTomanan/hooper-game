using Hooper.Ball;
using Hooper.Moves;

namespace Hooper.Ball.Tests;

// #98 — block as a committed move vs the shot (M10, ADR-0018 §2, ADR-0008 Amendment 2026-06-30).
//
// The block uses the FULL INTERVAL FORM of DefensiveResolution.Succeeds (unlike
// steal's point-in-band): the defender's Active window [blockActiveStart, blockActiveEnd)
// must overlap the shot's vulnerable window [inFlightStartTick, inFlightStartTick + blockGraceTicks).
//
// On success: BallStateMachine.GoLoose() from InFlight → Loose.
// The ball is re-contestable via the existing loose-ball scramble (ADR-0008 §Decision-2).
// A ball knocked Loose while InFlight MUST NOT score (the key correctness requirement).
public class BlockMoveTests
{
    // Block's use of DefensiveResolution.Succeeds (the shared half-open
    // interval overlap predicate, ADR-0018 §1) is exercised generically in
    // DefensiveResolutionTests.cs — this file no longer re-asserts those
    // generic cases with block-flavored numbers (issue #216 original body
    // row 7, test dedup). The one genuinely distinct boundary case this file
    // used to pin (Active's end exactly at the vulnerable window's end) moved
    // to DefensiveResolutionTests.Succeeds_ActiveEndsExactlyAtVulnEnd_ReturnsTrue
    // — it wasn't already covered there and isn't block-specific either, so
    // it belongs with the predicate's other generic boundary tests, not here.

    // ═══════════════════════════════════════════════════════════════════════
    // GoLoose from InFlight — BallStateMachine contract
    //
    // ADR-0008 Amendment 2026-06-30: a successful block transitions InFlight → Loose.
    // The existing loose-ball scramble then awards possession (ADR-0008 §Decision-2).
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void GoLoose_FromInFlight_Succeeds()
    {
        // Block path: shot (Held→InFlight), then block (InFlight→Loose).
        var sm = new BallStateMachine(initialHolderPeerId: 1);
        sm.Shoot();
        bool blocked = sm.GoLoose();
        Assert.True(blocked);
    }

    [Fact]
    public void GoLoose_FromInFlight_StateIsLoose()
    {
        // After a block the ball enters the loose-ball scramble (ADR-0008 §Decision-2).
        var sm = new BallStateMachine(initialHolderPeerId: 1);
        sm.Shoot();
        sm.GoLoose();
        Assert.Equal(BallState.Loose, sm.Current);
    }

    [Fact]
    public void GoLoose_FromInFlight_ClearsHolder()
    {
        // No player "owns" the blocked ball; the scramble awards it via proximity
        // (ADR-0008 §Decision-2), not an immediate assignment.
        var sm = new BallStateMachine(initialHolderPeerId: 1);
        sm.Shoot();
        sm.GoLoose();
        Assert.Equal(0, sm.HolderPeerId);
    }

    [Fact]
    public void GoLoose_FromInFlight_CalledTwice_SecondReturnsFalse()
    {
        // Exactly-once semantics: the ball is already Loose so the second
        // GoLoose call is invalid — the edge guard returns false.
        var sm = new BallStateMachine(initialHolderPeerId: 1);
        sm.Shoot();
        sm.GoLoose();
        bool second = sm.GoLoose();
        Assert.False(second);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // KEY CORRECTNESS REQUIREMENT: a blocked shot is NOT re-counted as a make.
    //
    // The only code that calls RegisterBasket is BallController.TickInFlight,
    // which runs only when State == InFlight. Once GoLoose() transitions the
    // ball to Loose, TickLoose runs instead — RegisterBasket is never reached.
    //
    // This test pins the state-machine contract that makes that guarantee hold:
    // after GoLoose() from InFlight, Current == Loose (not InFlight), so the
    // TickInFlight / RimBackboard / make-detection path is unreachable.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void BlockedShot_StateIsLoose_NotInFlight_MakeDetectionPathUnreachable()
    {
        // Replicate the block event: shot goes InFlight, block sends it Loose.
        // The state MUST be Loose (not InFlight) so BallController's TickInFlight
        // branch, which is the only caller of RegisterBasket, is never entered
        // for this ball arc. (ADR-0008 Amendment 2026-06-30: InFlight → Loose.)
        var sm = new BallStateMachine(initialHolderPeerId: 1);

        sm.Shoot(); // Held → InFlight (ball is now "in the air")
        Assert.Equal(BallState.InFlight, sm.Current); // sanity: shot is live

        bool blocked = sm.GoLoose(); // block connects: InFlight → Loose
        Assert.True(blocked);        // block was legal
        Assert.Equal(BallState.Loose, sm.Current); // NOT InFlight → cannot score
        Assert.Equal(0, sm.HolderPeerId);           // no holder — open scramble
    }

    [Fact]
    public void BlockedShot_BallIsRecontestable_CanBeCaughtFromLoose()
    {
        // The loose scramble must be able to award the ball to either player
        // (ADR-0008 §Decision-2 proximity pick-up). Catching from Loose → Held
        // is the normal possession-award path; confirm it still works after a block.
        var sm = new BallStateMachine(initialHolderPeerId: 1);
        sm.Shoot();
        sm.GoLoose(); // block

        // Either player can pick it up — here player 2 (the blocker) wins the scramble.
        bool caught = sm.Catch(newHolderPeerId: 2);
        Assert.True(caught);
        Assert.Equal(BallState.Held, sm.Current);
        Assert.Equal(2, sm.HolderPeerId);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ShouldForceInactive — reconcile gate, BlockMove flavour (ADR-0002)
    //
    // A client that predicted a BlockMove (machine in Active) but the server
    // rejected it (still Inactive) must snap back via ForceState. Mirrors the
    // StealMove reconcile tests — documents that block is not accidentally
    // excluded from the generic reconciliation path.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void ShouldForceInactive_ClientBlockActiveServerInactive_ReturnsTrue()
    {
        // Client predicted a block; server says Inactive (defender was mid-Recovery
        // when Begin() arrived). ShouldForceInactive → ForceState(Inactive) snaps
        // the client back — the same path the crossover and steal use (ADR-0002).
        bool result = CommittedMoveMachine.ShouldForceInactive(
            localPhase:  MovePhase.Active,
            localIsActive: true,
            serverPhase: MovePhase.Inactive);

        Assert.True(result);
    }

    [Fact]
    public void ShouldForceInactive_ClientBlockStartupServerInactive_ReturnsFalse()
    {
        // Startup grace window: the client sent Begin() which takes ~1 RTT to
        // reach the server. During that window the client is in Startup while
        // the server is still Inactive. ShouldForceInactive must NOT fire here —
        // this prevents the "legitimate block startup" from being cancelled before
        // the RPC arrives at the server.
        bool result = CommittedMoveMachine.ShouldForceInactive(
            localPhase:  MovePhase.Startup,
            localIsActive: true,
            serverPhase: MovePhase.Inactive);

        Assert.False(result);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // BlockMove frame data — sanity-pin the defaults
    //
    // The exact numbers are provisional (deferred to tuning issue #104 + the
    // per-milestone feel pass, ADR-0015). These tests document the intended
    // relative relationships mandated by ADR-0018 §3 (reaction-tilt asymmetry):
    //   • Active ≤ grace window it must hit (no free misses)
    //   • Recovery ≥ JumpShot.RecoveryFrames (missed block = at least as punishable)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void BlockMove_DefaultFrameData_ActiveFramesLeqDefaultGraceWindow()
    {
        // ADR-0018 §3: a defensive Active is no wider than the offensive
        // vulnerable window it must hit. BlockMove.DefaultFrameData.ActiveFrames
        // must not exceed BlockMove.DefaultBlockGraceTicks — the SAME symbol
        // BallController.BlockGraceTicks' [Export] default now derives from
        // (issue #216 original body row 7 — this used to be a hand-copied
        // "10" literal here that nothing enforced agreed with the real
        // default). This pins the relationship — the exact values are
        // deferred to #104.
        var fd = BlockMove.DefaultFrameData;
        Assert.True(fd.ActiveFrames <= BlockMove.DefaultBlockGraceTicks,
            $"BlockMove Active ({fd.ActiveFrames}) must be ≤ grace window ({BlockMove.DefaultBlockGraceTicks}). " +
            "ADR-0018 §3: a defensive Active must not be wider than the window it targets.");
    }

    [Fact]
    public void BlockMove_DefaultFrameData_RecoveryFramesGeqJumpShotRecovery()
    {
        // ADR-0018 §3: a missed defensive commitment is at least as punishable as
        // the offensive move it counters. A missed block must pay at least as much
        // Recovery as the JumpShot it tried to block.
        var blockFd     = BlockMove.DefaultFrameData;
        var jumpShotFd  = JumpShot.DefaultFrameData;
        Assert.True(blockFd.RecoveryFrames >= jumpShotFd.RecoveryFrames,
            $"BlockMove Recovery ({blockFd.RecoveryFrames}) must be ≥ JumpShot Recovery ({jumpShotFd.RecoveryFrames}). " +
            "ADR-0018 §3: a missed block must be at least as punishable as the shot it tried to stop.");
    }
}
