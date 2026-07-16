using Hooper.Moves;

namespace Hooper.Ball.Tests;

public class RetreatDribbleTests
{
    // ── Identity ──────────────────────────────────────────────────────────────

    [Fact]
    public void RetreatDribble_Id_IsRetreatDribble()
    {
        Assert.Equal("retreatdribble", new RetreatDribble().Id);
    }

    [Fact]
    public void RetreatDribble_DisplayName_IsRetreatDribble()
    {
        Assert.Equal("Retreat Dribble", new RetreatDribble().DisplayName);
    }

    [Fact]
    public void RetreatDribble_FeintWindowFrames_IsZero()
    {
        // The quick gesture itself IS the feint (#197) — a second, free
        // abort on top of that would make this a zero-cost bait tool.
        Assert.Equal(0, new RetreatDribble().FrameData.FeintWindowFrames);
    }

    [Fact]
    public void RetreatDribble_IsNotStepBack()
    {
        CommittedMove move = new RetreatDribble();
        Assert.IsNotType<StepBack>(move);
    }

    // ── Full lifecycle through CommittedMoveMachine ───────────────────────────

    /// <summary>
    /// Drives a RetreatDribble through its complete Startup→Active→Recovery→
    /// Inactive lifecycle using DefaultFrameData (startup=3, active=2,
    /// recovery=4) — the living documentation of the placeholder timings.
    /// </summary>
    [Fact]
    public void RetreatDribble_FullLifecycle_JustEnteredActiveOnceAndReturnToInactive()
    {
        var machine = new CommittedMoveMachine();
        var move    = new RetreatDribble();

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
    public void RetreatDribble_FeintOnFirstStartupTick_ReturnsFalse()
    {
        var machine = new CommittedMoveMachine();
        machine.Begin(new RetreatDribble());

        machine.Tick();
        Assert.Equal(MovePhase.Startup, machine.Phase);

        bool feinted = machine.Feint();

        Assert.False(feinted, "RetreatDribble cannot be feinted (feintWindowFrames = 0).");
        Assert.Equal(MovePhase.Startup, machine.Phase);
    }
}
