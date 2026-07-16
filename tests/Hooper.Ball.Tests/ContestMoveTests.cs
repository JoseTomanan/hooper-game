using Hooper.Moves;

namespace Hooper.Ball.Tests;

// #99 — on-ball contest as a committed move (M10, ADR-0018 §2).
//
// Unlike steal (#96) and block (#98), contest never resolves a binary
// succeed/fail — it composes an ADDITIONAL accuracy factor on top of the
// existing passive proximity scatter (ADR-0009 / #65). The composition
// arithmetic itself (DefensiveResolution.ContestAppliesAt /
// ContestMoveFactor) is exercised in DefensiveResolutionTests.cs, mirroring
// where WithinBlockReach's tests live; this file pins ContestMove's own
// frame-data contract and the committed-move machine behavior specific to it
// (issue #216 / BlockMoveTests convention: generic machine mechanics are
// already covered by CommittedMoveMachineTests — this file only pins what is
// genuinely ContestMove-specific).
public class ContestMoveTests
{
    // ═══════════════════════════════════════════════════════════════════════
    // ContestMove frame data — sanity-pin the defaults
    //
    // The exact numbers are provisional (deferred to tuning issue #104 + the
    // per-milestone feel pass, ADR-0015). These tests document the intended
    // relative relationships mandated by ADR-0018 §3 (reaction-tilt asymmetry),
    // mirroring BlockMoveTests' equivalent pins for BlockMove.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void ContestMove_DefaultFrameData_ActiveFramesLeqDefaultBlockGraceTicks()
    {
        // ADR-0018 §3: a defensive Active is no wider than the vulnerable
        // window it targets. Contest shares the same shot-release vulnerable
        // window bound as block (BlockGraceTicks) — see ContestMove's own
        // class doc "Active: 8 ticks — bounded by BlockGraceTicks."
        var fd = ContestMove.DefaultFrameData;
        Assert.True(fd.ActiveFrames <= BlockMove.DefaultBlockGraceTicks,
            $"ContestMove Active ({fd.ActiveFrames}) must be <= the shot's grace window " +
            $"({BlockMove.DefaultBlockGraceTicks}). ADR-0018 §3: a defensive Active must not " +
            "be wider than the window it targets.");
    }

    [Fact]
    public void ContestMove_DefaultFrameData_RecoveryFramesGeqJumpShotRecovery()
    {
        // ADR-0018 §3: a missed/bad defensive commitment is at least as
        // punishable as the offensive move it counters.
        var contestFd = ContestMove.DefaultFrameData;
        var jumpShotFd = JumpShot.DefaultFrameData;
        Assert.True(contestFd.RecoveryFrames >= jumpShotFd.RecoveryFrames,
            $"ContestMove Recovery ({contestFd.RecoveryFrames}) must be >= JumpShot Recovery " +
            $"({jumpShotFd.RecoveryFrames}). ADR-0018 §3: a bad contest commit must be at " +
            "least as punishable as the shot it pressures.");
    }

    [Fact]
    public void ContestMove_DefaultFrameData_StartupShorterThanBlock()
    {
        // A hands-up closeout plant is a smaller, faster commitment than
        // block's full swat wind-up (see ContestMove's class doc) — pins the
        // documented relative ordering so a future retune that flips it is
        // a visible, deliberate decision, not a silent drift.
        var contestFd = ContestMove.DefaultFrameData;
        var blockFd = BlockMove.DefaultFrameData;
        Assert.True(contestFd.StartupFrames < blockFd.StartupFrames,
            $"ContestMove Startup ({contestFd.StartupFrames}) is documented as shorter than " +
            $"BlockMove Startup ({blockFd.StartupFrames}) — a closeout is a smaller commitment " +
            "than a full swat wind-up.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Frame-data proof: contest Recovery leaves the defender briefly unable
    // to act (a real committed-move cost — over-contesting is punishable,
    // not a free hands-up toggle).
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Begin_SecondContestDuringRecovery_ReturnsFalse()
    {
        var m = new CommittedMoveMachine();
        var fd = ContestMove.DefaultFrameData;

        m.Begin(new ContestMove(fd));
        TickN(m, fd.StartupFrames + fd.ActiveFrames); // now in Recovery

        Assert.Equal(MovePhase.Recovery, m.Phase);

        bool secondBegin = m.Begin(new ContestMove(fd));
        Assert.False(secondBegin,
            "A defender mid-Recovery from a contest cannot begin a new committed move — " +
            "over-contesting is a real, punishable cost (ADR-0018 §3), not a free toggle.");
    }

    [Fact]
    public void Tick_ThroughFullRecovery_MachineReturnsToInactive()
    {
        // The defender IS eventually freed once Recovery elapses — Recovery
        // costs time, it does not lock the defender out forever.
        var m = new CommittedMoveMachine();
        var fd = ContestMove.DefaultFrameData;

        m.Begin(new ContestMove(fd));
        TickN(m, fd.StartupFrames + fd.ActiveFrames + fd.RecoveryFrames);

        Assert.Equal(MovePhase.Inactive, m.Phase);
        Assert.True(m.Begin(new ContestMove(fd)),
            "Once Recovery fully elapses the defender must be able to commit a new move.");
    }

    [Fact]
    public void Begin_OneTickBeforeRecoveryEnds_StillRejected()
    {
        // Boundary pin: the LAST tick of Recovery still blocks a new Begin —
        // guards against an off-by-one that would free the defender early.
        var m = new CommittedMoveMachine();
        var fd = ContestMove.DefaultFrameData;

        m.Begin(new ContestMove(fd));
        TickN(m, fd.StartupFrames + fd.ActiveFrames + fd.RecoveryFrames - 1);

        Assert.Equal(MovePhase.Recovery, m.Phase);
        Assert.False(m.Begin(new ContestMove(fd)));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void TickN(CommittedMoveMachine m, int n)
    {
        for (int i = 0; i < n; i++) m.Tick();
    }
}
