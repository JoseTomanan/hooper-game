using Hooper.Moves;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for Layup — the M9 rim-finishing vertical's first leaf (issue
/// #229, ADR-0022): a distinct committed move from JumpShot, with its own
/// frame data, contestable by the same block window.
///
/// ── Test naming ──────────────────────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// </summary>
public class LayupTests
{
    [Fact]
    public void Constructor_Default_IdIsLayup()
    {
        var layup = new Layup();
        Assert.Equal("layup", layup.Id);
    }

    [Fact]
    public void Constructor_Default_UsesDefaultFrameData()
    {
        var layup = new Layup();
        Assert.Same(Layup.DefaultFrameData, layup.FrameData);
    }

    [Fact]
    public void Constructor_CustomFrameData_Stored()
    {
        var custom = new MoveFrameData(startupFrames: 5, activeFrames: 2, recoveryFrames: 8, feintWindowFrames: 0);
        var layup = new Layup(custom);
        Assert.Same(custom, layup.FrameData);
    }

    [Fact]
    public void BeginOnMachine_EntersStartup()
    {
        var machine = new CommittedMoveMachine();
        bool began = machine.Begin(new Layup());

        Assert.True(began);
        Assert.Equal(MovePhase.Startup, machine.Phase);
    }

    [Fact]
    public void DefaultFrameData_IsUnfeintable()
    {
        // A design constant (like Hesitation's), not a placeholder — a
        // pump-fake variant of the layup is explicitly out of #229's scope.
        Assert.Equal(0, Layup.DefaultFrameData.FeintWindowFrames);
    }

    [Fact]
    public void DefaultFrameData_ShorterStartupThanJumpShot()
    {
        // Real-ball citation (ADR-0014 tier 2): a close-range finish is a
        // quicker gather than a set shot's full wind-up.
        Assert.True(Layup.DefaultFrameData.StartupFrames < JumpShot.DefaultFrameData.StartupFrames);
    }

    [Fact]
    public void DefaultFrameData_ShorterRecoveryThanJumpShot()
    {
        // A layup's finish is close to the floor — less descent to recover
        // from than a jump shot's landing — but still a real punish window.
        Assert.True(Layup.DefaultFrameData.RecoveryFrames < JumpShot.DefaultFrameData.RecoveryFrames);
        Assert.True(Layup.DefaultFrameData.RecoveryFrames >= 1);
    }

    [Fact]
    public void DefaultFrameData_SameActiveFramesAsJumpShot()
    {
        // Both release on the single JustEnteredActive pulse (the same
        // one-shot convention) — the Active width itself doesn't need to
        // differ for that to hold, and keeping it identical avoids an
        // arbitrary extra number with no citation behind it.
        Assert.Equal(JumpShot.DefaultFrameData.ActiveFrames, Layup.DefaultFrameData.ActiveFrames);
    }

    [Fact]
    public void Integration_FullLifecycle_ReturnsToInactive()
    {
        var machine = new CommittedMoveMachine();
        machine.Begin(new Layup());

        int total = Layup.DefaultFrameData.StartupFrames
            + Layup.DefaultFrameData.ActiveFrames
            + Layup.DefaultFrameData.RecoveryFrames;

        for (int i = 0; i < total; i++) machine.Tick();

        Assert.Equal(MovePhase.Inactive, machine.Phase);
    }
}
