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
    public void DefaultFrameData_FeintWindowIsZero()
    {
        // Deliberately zero — see JumpShot's class doc: the existing generic
        // Feint() input path (PlayerController.SampleMoveInput) is
        // move-agnostic, so any nonzero value here would silently enable the
        // deferred pump-fake (#77) before #77's own scope activates it.
        Assert.Equal(0, JumpShot.DefaultFrameData.FeintWindowFrames);
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
}
