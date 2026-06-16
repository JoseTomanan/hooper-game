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
    public void Constructor_FeintWindowEqualToStartup_IsValid()
    {
        // Window exactly filling the startup phase means feint is available the whole startup.
        var fd = new MoveFrameData(startupFrames: 6, activeFrames: 4, recoveryFrames: 10, feintWindowFrames: 6);
        Assert.Equal(6, fd.FeintWindowFrames);
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
}
