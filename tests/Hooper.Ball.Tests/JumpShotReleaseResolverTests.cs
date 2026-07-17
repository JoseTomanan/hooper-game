using Hooper.Moves;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for JumpShotReleaseResolver — the pure "should the ball release
/// this tick" decision extracted for M7b (issue #74) so it is verified
/// without a running Godot instance.
///
/// ── Test naming ──────────────────────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// </summary>
public class JumpShotReleaseResolverTests
{
    [Fact]
    public void ShouldRelease_JustEnteredActiveWithJumpShot_True()
    {
        Assert.True(JumpShotReleaseResolver.ShouldRelease(justEnteredActive: true, currentMove: new JumpShot()));
    }

    [Fact]
    public void ShouldRelease_JustEnteredActiveWithCrossover_False()
    {
        // A crossover entering Active must never release the ball — only a
        // JumpShot does.
        Assert.False(JumpShotReleaseResolver.ShouldRelease(justEnteredActive: true, currentMove: new Crossover(1f)));
    }

    [Fact]
    public void ShouldRelease_NotJustEnteredActive_False()
    {
        // Covers "never before [Active]" — even with a JumpShot running,
        // every tick that isn't the single Active-entry pulse must not release.
        Assert.False(JumpShotReleaseResolver.ShouldRelease(justEnteredActive: false, currentMove: new JumpShot()));
    }

    [Fact]
    public void ShouldRelease_NullMove_False()
    {
        Assert.False(JumpShotReleaseResolver.ShouldRelease(justEnteredActive: true, currentMove: null));
    }

    // ── Layup (issue #229, ADR-0022) ──────────────────────────────────────────

    [Fact]
    public void ShouldRelease_JustEnteredActiveWithLayup_True()
    {
        // The layup is a distinct committed move from JumpShot, but releases
        // on the exact same JustEnteredActive convention (ADR-0022).
        Assert.True(JumpShotReleaseResolver.ShouldRelease(justEnteredActive: true, currentMove: new Layup()));
    }

    [Fact]
    public void ShouldRelease_NotJustEnteredActiveWithLayup_False()
    {
        Assert.False(JumpShotReleaseResolver.ShouldRelease(justEnteredActive: false, currentMove: new Layup()));
    }

    [Fact]
    public void ShouldRelease_ViaMachine_FiresExactlyOnceAcrossFullLifecycle()
    {
        // End-to-end through the real machine: a full Startup->Active->
        // Recovery->Inactive run must report exactly one release tick, never
        // zero (missed) and never more than one (double-release).
        var machine = new CommittedMoveMachine();
        var frameData = new MoveFrameData(startupFrames: 3, activeFrames: 2, recoveryFrames: 3, feintWindowFrames: 0);
        machine.Begin(new JumpShot(frameData));

        int releaseCount = 0;
        int totalTicks = frameData.StartupFrames + frameData.ActiveFrames + frameData.RecoveryFrames;
        for (int i = 0; i < totalTicks; i++)
        {
            machine.Tick();
            if (JumpShotReleaseResolver.ShouldRelease(machine.JustEnteredActive, machine.CurrentMove))
                releaseCount++;
        }

        Assert.Equal(1, releaseCount);
    }

    [Fact]
    public void ShouldRelease_ViaMachine_NeverFiresWhileStillInStartup()
    {
        // Tick() transitions Startup->Active ON the StartupFrames-th call
        // (FrameInPhase reaches the threshold and EnterPhase runs within that
        // same Tick()), so only the ticks STRICTLY BEFORE that one are still
        // genuinely "in Startup" — this loop deliberately stops one short.
        var machine = new CommittedMoveMachine();
        var frameData = new MoveFrameData(startupFrames: 5, activeFrames: 2, recoveryFrames: 3, feintWindowFrames: 0);
        machine.Begin(new JumpShot(frameData));

        for (int i = 0; i < frameData.StartupFrames - 1; i++)
        {
            machine.Tick();
            Assert.Equal(MovePhase.Startup, machine.Phase);
            Assert.False(JumpShotReleaseResolver.ShouldRelease(machine.JustEnteredActive, machine.CurrentMove));
        }
    }
}
