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
    // ═══════════════════════════════════════════════════════════════════════
    // Block interval overlap — DefensiveResolution.Succeeds used in full
    // interval form (ADR-0018 §2: block uses the interval form, not point-in-band)
    //
    // Vulnerable window: [inFlightStartTick, inFlightStartTick + blockGraceTicks)
    // Defender Active:   [blockActiveStart, blockActiveStart + activeFrames)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void BlockSucceeds_ActiveWindowOverlapsVulnWindow_ReturnsTrue()
    {
        // Defender entered Active at tick 10, window lasts 8 frames → [10, 18)
        // Shot went InFlight at tick 12, grace = 10 → vulnerable [12, 22)
        // Overlap [12, 18) is non-empty → block succeeds.
        Assert.True(DefensiveResolution.Succeeds(
            activeStart: 10, activeEnd: 18,
            vulnStart:   12, vulnEnd:   22));
    }

    [Fact]
    public void BlockFails_ActiveEndsBeforeVulnOpens_ReturnsFalse()
    {
        // Defender committed too early: Active [5, 11) ends before shot is
        // even released (vulnStart 12). A pre-shot block attempt always fails.
        Assert.False(DefensiveResolution.Succeeds(
            activeStart: 5,  activeEnd: 11,
            vulnStart:   12, vulnEnd:   22));
    }

    [Fact]
    public void BlockFails_ActiveStartsAfterGraceExpires_ReturnsFalse()
    {
        // Defender reacted too late: Active [25, 33) starts after the grace
        // window closes (vulnEnd 22). Ball is already past the defender.
        Assert.False(DefensiveResolution.Succeeds(
            activeStart: 25, activeEnd: 33,
            vulnStart:   12, vulnEnd:   22));
    }

    [Fact]
    public void BlockFails_AdjacentWindows_ReturnsFalse()
    {
        // Half-open semantics: Active [5, 12) ends exactly where vuln [12, 22) begins.
        // The overlap is empty — a single-tick miss. Defender was a hair too early.
        Assert.False(DefensiveResolution.Succeeds(
            activeStart: 5,  activeEnd: 12,
            vulnStart:   12, vulnEnd:   22));
    }

    [Fact]
    public void BlockSucceeds_ActiveEndsExactlyAtGraceEnd_IsLastValidTick()
    {
        // Defender's Active [15, 22) ends exactly when grace closes [12, 22).
        // The half-open intervals share the boundary 22; overlap is [15, 22) which
        // is non-empty — this is still a valid block (last tick of grace window).
        Assert.True(DefensiveResolution.Succeeds(
            activeStart: 15, activeEnd: 22,
            vulnStart:   12, vulnEnd:   22));
    }

    [Fact]
    public void BlockSucceeds_ActiveFullyContainsVuln_ReturnsTrue()
    {
        // Defender timed it perfectly: their window wraps the entire grace period.
        Assert.True(DefensiveResolution.Succeeds(
            activeStart: 10, activeEnd: 35,
            vulnStart:   12, vulnEnd:   22));
    }

    [Fact]
    public void BlockSucceeds_VulnFullyContainsActive_ReturnsTrue()
    {
        // The shot's grace window is wider than the defender's Active window — the
        // block still connects because the overlap is non-empty.
        Assert.True(DefensiveResolution.Succeeds(
            activeStart: 14, activeEnd: 18,
            vulnStart:   12, vulnEnd:   22));
    }

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
        // vulnerable window it must hit. BlockGraceTicks is an exported tunable
        // (default 10); BlockMove.DefaultFrameData.ActiveFrames must not exceed it.
        // This pins the relationship — the exact values are deferred to #104.
        var fd = BlockMove.DefaultFrameData;
        const int DefaultBlockGraceTicks = 10; // mirrors BallController.BlockGraceTicks default
        Assert.True(fd.ActiveFrames <= DefaultBlockGraceTicks,
            $"BlockMove Active ({fd.ActiveFrames}) must be ≤ grace window ({DefaultBlockGraceTicks}). " +
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
