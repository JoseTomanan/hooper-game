using Hooper.Moves;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for JumpShot — the M7b (#74) committed move that replaces the
/// old instant shoot trigger with a real startup → active → recovery arc.
///
/// ── Test naming ──────────────────────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// </summary>
public class JumpShotTests
{
    [Fact]
    public void Constructor_Default_IdIsJumpshot()
    {
        var shot = new JumpShot();
        Assert.Equal("jumpshot", shot.Id);
    }

    [Fact]
    public void Constructor_Default_UsesDefaultFrameData()
    {
        var shot = new JumpShot();
        Assert.Same(JumpShot.DefaultFrameData, shot.FrameData);
    }

    [Fact]
    public void Constructor_CustomFrameData_Stored()
    {
        var custom = new MoveFrameData(startupFrames: 10, activeFrames: 2, recoveryFrames: 10, feintWindowFrames: 0);
        var shot = new JumpShot(custom);
        Assert.Same(custom, shot.FrameData);
    }

    [Fact]
    public void BeginOnMachine_EntersStartup()
    {
        var machine = new CommittedMoveMachine();
        bool began = machine.Begin(new JumpShot());

        Assert.True(began);
        Assert.Equal(MovePhase.Startup, machine.Phase);
    }

    // ── Pump-fake activation (#77) ────────────────────────────────────────────

    [Fact]
    public void DefaultFrameData_FeintWindowIsNonZero_Equals12()
    {
        // #77 activates the pump-fake: feint window is now 12 of 18 startup frames,
        // leaving a 6-frame committed tail (point of no return).
        Assert.Equal(12, JumpShot.DefaultFrameData.FeintWindowFrames);
    }

    [Fact]
    public void DefaultFrameData_FeintRecoveryFramesEquals8()
    {
        // A pump-fake costs 8 recovery ticks — shorter than the full 20-tick landing
        // recovery because you never left the ground. Provisional tuning for feel (M10).
        Assert.Equal(8, JumpShot.DefaultFrameData.FeintRecoveryFrames);
    }

    [Fact]
    public void DefaultFrameData_FeintWindowStrictlyLessThanStartup()
    {
        // Invariant: the committed-tail rule (ADR-0003) — window < startup.
        // 12 < 18 gives a 6-frame tail where no feint is possible.
        Assert.True(JumpShot.DefaultFrameData.FeintWindowFrames < JumpShot.DefaultFrameData.StartupFrames);
    }

    [Fact]
    public void DefaultFrameData_FeintRecoveryNotExceedsRecovery()
    {
        // Invariant: feintRecoveryFrames <= recoveryFrames.
        Assert.True(JumpShot.DefaultFrameData.FeintRecoveryFrames <= JumpShot.DefaultFrameData.RecoveryFrames);
    }

    [Fact]
    public void Integration_BeginThenFeint_LandsInRecovery_JustEnteredActiveNeverSet()
    {
        // Integration test: a fresh CommittedMoveMachine running a JumpShot —
        // tick into the feint window, call Feint(), then tick the full feint
        // recovery, asserting JustEnteredActive is never set (ball never releases).
        var machine = new CommittedMoveMachine();
        machine.Begin(new JumpShot());

        // Tick a few frames into the window (must be < FeintWindowFrames = 12).
        for (int i = 0; i < 5; i++) machine.Tick();

        bool feinted = machine.Feint();
        Assert.True(feinted);
        Assert.Equal(MovePhase.Recovery, machine.Phase);

        // Tick through the remaining feint-recovery frames.
        int feintRecovery = JumpShot.DefaultFrameData.FeintRecoveryFrames;
        for (int i = 0; i < feintRecovery; i++)
        {
            Assert.False(machine.JustEnteredActive,
                $"JustEnteredActive was true on tick {i} of feint recovery — ball must not release on a pump-fake");
            machine.Tick();
        }

        Assert.Equal(MovePhase.Inactive, machine.Phase);
    }
}
