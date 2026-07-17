using Hooper.Moves;

namespace Hooper.Ball.Tests;

public class DriveGatherTests
{
    // ── Identity ──────────────────────────────────────────────────────────────

    [Fact]
    public void DriveGather_Id_IsDriveGather()
    {
        Assert.Equal("drivegather", new DriveGather().Id);
    }

    [Fact]
    public void DriveGather_DisplayName_IsDriveGather()
    {
        Assert.Equal("Drive-Gather", new DriveGather().DisplayName);
    }

    [Fact]
    public void DriveGather_FeintWindowFrames_IsZero()
    {
        // ADR-0022: "the committed instant before a finish" — an irreversible
        // plant, not a probe. No free abort (#230).
        Assert.Equal(0, new DriveGather().FrameData.FeintWindowFrames);
    }

    [Fact]
    public void DriveGather_IsNotLayupOrStepBack()
    {
        CommittedMove move = new DriveGather();
        Assert.IsNotType<Layup>(move);
        Assert.IsNotType<StepBack>(move);
    }

    // ── Full lifecycle through CommittedMoveMachine ───────────────────────────

    /// <summary>
    /// Drives a DriveGather through its complete Startup→Active→Recovery→
    /// Inactive lifecycle using DefaultFrameData (startup=6, active=10,
    /// recovery=14) — the living documentation of the tuned timings, and proof
    /// that Active is deliberately the longest window of any burst-family move
    /// (a drive covers real ground, not a single impulse).
    /// </summary>
    [Fact]
    public void DriveGather_FullLifecycle_JustEnteredActiveOnceAndReturnToInactive()
    {
        var machine = new CommittedMoveMachine();
        var move    = new DriveGather();

        bool started = machine.Begin(move);
        Assert.True(started, "Begin should succeed from Inactive.");
        Assert.Equal(MovePhase.Startup, machine.Phase);

        int justEnteredActiveCount = 0;
        for (int i = 0; i < move.FrameData.StartupFrames; i++)
        {
            machine.Tick();
            if (machine.JustEnteredActive) justEnteredActiveCount++;
        }
        Assert.Equal(MovePhase.Active, machine.Phase);
        Assert.Equal(1, justEnteredActiveCount);

        for (int i = 0; i < move.FrameData.ActiveFrames; i++)
        {
            machine.Tick();
            Assert.False(machine.JustEnteredActive, $"JustEnteredActive should be false on Active tick {i}.");
        }
        Assert.Equal(MovePhase.Recovery, machine.Phase);

        for (int i = 0; i < move.FrameData.RecoveryFrames; i++)
            machine.Tick();

        Assert.Equal(MovePhase.Inactive, machine.Phase);
        Assert.False(machine.IsActive);
        Assert.Equal(1, justEnteredActiveCount);
    }

    // ── Feint guard ───────────────────────────────────────────────────────────

    [Fact]
    public void DriveGather_FeintOnFirstStartupTick_ReturnsFalse()
    {
        var machine = new CommittedMoveMachine();
        machine.Begin(new DriveGather());

        machine.Tick();
        Assert.Equal(MovePhase.Startup, machine.Phase);

        bool feinted = machine.Feint();

        Assert.False(feinted, "DriveGather cannot be feinted (feintWindowFrames = 0).");
        Assert.Equal(MovePhase.Startup, machine.Phase);
    }

    // ── Active window is the longest of the burst family ─────────────────────

    [Fact]
    public void DriveGather_ActiveFrames_LongerThanEveryBurstFamilyMove()
    {
        // A drive has to cover real ground toward the rim before a finish is
        // reachable — unlike a single-impulse separation burst (#230).
        int driveActive = DriveGather.DefaultFrameData.ActiveFrames;
        Assert.True(driveActive > new Crossover(1f).FrameData.ActiveFrames);
        Assert.True(driveActive > new BehindTheBack(1f).FrameData.ActiveFrames);
        Assert.True(driveActive > new BetweenTheLegs(1f).FrameData.ActiveFrames);
        Assert.True(driveActive > new StepBack().FrameData.ActiveFrames);
    }
}
