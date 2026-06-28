using Hooper.Moves;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for MoveFrameData — the immutable frame-count record for a committed move.
///
/// Construction-time validation is the only meaningful behavior: once constructed,
/// the object is a plain data bag. All tests verify that valid data is accepted
/// and invalid data is rejected with ArgumentOutOfRangeException.
///
/// ── Test naming ──────────────────────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// Each test contains exactly one logical assertion.
/// </summary>
public class MoveFrameDataTests
{
    // ── Valid construction ────────────────────────────────────────────────────

    [Fact]
    public void Constructor_ValidArgs_StartupFramesStored()
    {
        var fd = new MoveFrameData(startupFrames: 6, activeFrames: 4, recoveryFrames: 10, feintWindowFrames: 3);
        Assert.Equal(6, fd.StartupFrames);
    }

    [Fact]
    public void Constructor_ValidArgs_ActiveFramesStored()
    {
        var fd = new MoveFrameData(startupFrames: 6, activeFrames: 4, recoveryFrames: 10, feintWindowFrames: 3);
        Assert.Equal(4, fd.ActiveFrames);
    }

    [Fact]
    public void Constructor_ValidArgs_RecoveryFramesStored()
    {
        var fd = new MoveFrameData(startupFrames: 6, activeFrames: 4, recoveryFrames: 10, feintWindowFrames: 3);
        Assert.Equal(10, fd.RecoveryFrames);
    }

    [Fact]
    public void Constructor_ValidArgs_FeintWindowFramesStored()
    {
        var fd = new MoveFrameData(startupFrames: 6, activeFrames: 4, recoveryFrames: 10, feintWindowFrames: 3);
        Assert.Equal(3, fd.FeintWindowFrames);
    }

    [Fact]
    public void Constructor_FeintWindowZero_IsValid()
    {
        // A move with no feint window is legal (feint is simply impossible on it).
        var fd = new MoveFrameData(startupFrames: 6, activeFrames: 4, recoveryFrames: 10, feintWindowFrames: 0);
        Assert.Equal(0, fd.FeintWindowFrames);
    }

    [Fact]
    public void Constructor_FeintWindowEqualToStartup_Throws()
    {
        // Tightened for #77: window must be strictly < startup so at least one
        // committed-tail Startup frame always exists (ADR-0003). A window == startup
        // would eliminate the point of no return. See Constructor_FeintWindowEqualsStartup_NowThrows
        // in the FeintRecoveryFrames section below for the same assertion presented
        // as a new-rule test; this existing test is updated to match the new rule.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MoveFrameData(startupFrames: 6, activeFrames: 4, recoveryFrames: 10, feintWindowFrames: 6));
    }

    // ── Invalid construction ──────────────────────────────────────────────────

    [Fact]
    public void Constructor_StartupFramesZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MoveFrameData(startupFrames: 0, activeFrames: 4, recoveryFrames: 10, feintWindowFrames: 0));
    }

    [Fact]
    public void Constructor_ActiveFramesZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MoveFrameData(startupFrames: 6, activeFrames: 0, recoveryFrames: 10, feintWindowFrames: 0));
    }

    [Fact]
    public void Constructor_RecoveryFramesZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MoveFrameData(startupFrames: 6, activeFrames: 4, recoveryFrames: 0, feintWindowFrames: 0));
    }

    [Fact]
    public void Constructor_FeintWindowExceedsStartup_Throws()
    {
        // A feint window larger than the startup phase makes no sense — the window
        // would extend into Active where a feint can't fire anyway.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MoveFrameData(startupFrames: 4, activeFrames: 4, recoveryFrames: 10, feintWindowFrames: 5));
    }

    [Fact]
    public void Constructor_NegativeStartupFrames_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MoveFrameData(startupFrames: -1, activeFrames: 4, recoveryFrames: 10, feintWindowFrames: 0));
    }

    [Fact]
    public void Constructor_NegativeFeintWindow_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MoveFrameData(startupFrames: 6, activeFrames: 4, recoveryFrames: 10, feintWindowFrames: -1));
    }

    // ── FeintRecoveryFrames ───────────────────────────────────────────────────

    [Fact]
    public void Constructor_OmittingFeintRecoveryFrames_DefaultsToZero()
    {
        // All existing call sites omit the parameter — the default must be 0 so
        // nothing changes for Crossover, Hesitation, JumpShot (pre-#77), etc.
        var fd = new MoveFrameData(startupFrames: 6, activeFrames: 4, recoveryFrames: 10, feintWindowFrames: 3);
        Assert.Equal(0, fd.FeintRecoveryFrames);
    }

    [Fact]
    public void Constructor_ValidFeintRecoveryFrames_IsStored()
    {
        var fd = new MoveFrameData(startupFrames: 18, activeFrames: 4, recoveryFrames: 20,
            feintWindowFrames: 12, feintRecoveryFrames: 8);
        Assert.Equal(8, fd.FeintRecoveryFrames);
    }

    [Fact]
    public void Constructor_NegativeFeintRecoveryFrames_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MoveFrameData(startupFrames: 18, activeFrames: 4, recoveryFrames: 20,
                feintWindowFrames: 12, feintRecoveryFrames: -1));
    }

    [Fact]
    public void Constructor_FeintRecoveryFramesExceedsRecovery_Throws()
    {
        // feintRecoveryFrames > recoveryFrames is nonsensical — a feint that
        // costs more recovery than the completed move removes the commitment
        // trade-off (ADR-0003) and breaks the pre-advance arithmetic in Feint().
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MoveFrameData(startupFrames: 18, activeFrames: 4, recoveryFrames: 20,
                feintWindowFrames: 12, feintRecoveryFrames: 21));
    }

    [Fact]
    public void Constructor_FeintWindowOneBeforeStartup_IsAccepted()
    {
        // window == startupFrames - 1 is the maximum valid value under the
        // strict-< rule; this is the boundary case that must be accepted.
        var fd = new MoveFrameData(startupFrames: 6, activeFrames: 4, recoveryFrames: 10, feintWindowFrames: 5);
        Assert.Equal(5, fd.FeintWindowFrames);
    }
}
