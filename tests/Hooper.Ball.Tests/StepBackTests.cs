using Hooper.Moves;

namespace Hooper.Ball.Tests;

public class StepBackTests
{
    // ── Identity ──────────────────────────────────────────────────────────────

    [Fact]
    public void StepBack_Id_IsStepBack()
    {
        Assert.Equal("stepback", new StepBack().Id);
    }

    [Fact]
    public void StepBack_DisplayName_IsStepBack()
    {
        Assert.Equal("Step-Back", new StepBack().DisplayName);
    }

    [Fact]
    public void StepBack_FeintWindowFrames_IsZero()
    {
        // The biggest-separation, highest-risk move in the taxonomy commits
        // fully — a free-abort window would let a player probe the read
        // without ever paying the dead-Held cost (#197).
        Assert.Equal(0, new StepBack().FrameData.FeintWindowFrames);
    }

    [Fact]
    public void StepBack_IsNotRetreatDribble()
    {
        CommittedMove move = new StepBack();
        Assert.IsNotType<RetreatDribble>(move);
    }

    // ── Full lifecycle through CommittedMoveMachine ───────────────────────────

    /// <summary>
    /// Drives a StepBack through its complete Startup→Active→Recovery→
    /// Inactive lifecycle using DefaultFrameData (startup=7, active=4,
    /// recovery=8) — the living documentation of the placeholder timings.
    /// </summary>
    [Fact]
    public void StepBack_FullLifecycle_JustEnteredActiveOnceAndReturnToInactive()
    {
        var machine = new CommittedMoveMachine();
        var move    = new StepBack();

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
    public void StepBack_FeintOnFirstStartupTick_ReturnsFalse()
    {
        var machine = new CommittedMoveMachine();
        machine.Begin(new StepBack());

        machine.Tick();
        Assert.Equal(MovePhase.Startup, machine.Phase);

        bool feinted = machine.Feint();

        Assert.False(feinted, "StepBack cannot be feinted (feintWindowFrames = 0).");
        Assert.Equal(MovePhase.Startup, machine.Phase);
    }
}
