using Hooper.Moves;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for JabStep (issue #200) — triple threat's stance bait.
///
/// ── Test naming ──────────────────────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// </summary>
public class JabStepTests
{
    // ── Identity ──────────────────────────────────────────────────────────────

    [Fact]
    public void JabStep_Id_IsJab()
    {
        Assert.Equal("jab", new JabStep().Id);
    }

    [Fact]
    public void JabStep_DisplayName_IsJabStep()
    {
        Assert.Equal("Jab Step", new JabStep().DisplayName);
    }

    [Fact]
    public void JabStep_FeintWindowFrames_IsZero()
    {
        // The jab IS the feint — there is nothing further to cancel. This is a
        // design constant (mirrors Hesitation's identical reasoning), not a
        // tunable placeholder.
        Assert.Equal(0, new JabStep().FrameData.FeintWindowFrames);
    }

    [Fact]
    public void JabStep_DefaultFrameData_MatchesPlaceholderSpec()
    {
        // Pins the issue's placeholder magnitudes (~3 startup / 2 active /
        // 4 recovery) as the living documentation of the shipped defaults —
        // any accidental retune is caught here, not silently in the harness.
        MoveFrameData fd = JabStep.DefaultFrameData;
        Assert.Equal(3, fd.StartupFrames);
        Assert.Equal(2, fd.ActiveFrames);
        Assert.Equal(4, fd.RecoveryFrames);
    }

    // ── Type identity (is not Hesitation) ─────────────────────────────────────

    [Fact]
    public void JabStep_IsNotHesitation()
    {
        // Distinct concrete types — callers that pattern-match on move type
        // (PlayerController.TickCommittedMoveBehavior's switches) must not
        // accidentally conflate the two hesi-shaped, no-burst moves.
        CommittedMove move = new JabStep();
        Assert.IsNotType<Hesitation>(move);
    }

    // ── Full lifecycle through CommittedMoveMachine ───────────────────────────

    /// <summary>
    /// Drives a JabStep through its complete Startup→Active→Recovery→Inactive
    /// lifecycle and asserts that:
    ///   - JustEnteredActive fires exactly once (on the Active-entry tick), and
    ///   - the machine returns to Inactive after all recovery frames exhaust.
    ///
    /// Uses DefaultFrameData (startup=3, active=2, recovery=4) so the frame
    /// counts are the living documentation of the placeholder timings.
    /// </summary>
    [Fact]
    public void JabStep_FullLifecycle_JustEnteredActiveOnceAndReturnToInactive()
    {
        var machine = new CommittedMoveMachine();
        var jab     = new JabStep(); // DefaultFrameData: startup=3, active=2, recovery=4

        bool started = machine.Begin(jab);
        Assert.True(started, "Begin should succeed from Inactive.");
        Assert.Equal(MovePhase.Startup, machine.Phase);

        int justEnteredActiveCount = 0;

        // ── Startup phase ─────────────────────────────────────────────────────
        for (int i = 0; i < jab.FrameData.StartupFrames; i++)
        {
            machine.Tick();
            if (machine.JustEnteredActive) justEnteredActiveCount++;
        }

        Assert.Equal(MovePhase.Active, machine.Phase);
        Assert.Equal(1, justEnteredActiveCount);

        // ── Active phase ──────────────────────────────────────────────────────
        for (int i = 0; i < jab.FrameData.ActiveFrames; i++)
        {
            machine.Tick();
            Assert.False(machine.JustEnteredActive, $"JustEnteredActive should be false on Active tick {i}.");
            if (machine.JustEnteredActive) justEnteredActiveCount++;
        }

        Assert.Equal(MovePhase.Recovery, machine.Phase);

        // ── Recovery phase → Inactive ─────────────────────────────────────────
        for (int i = 0; i < jab.FrameData.RecoveryFrames; i++)
        {
            machine.Tick();
        }

        Assert.Equal(MovePhase.Inactive, machine.Phase);
        Assert.False(machine.IsActive);

        Assert.Equal(1, justEnteredActiveCount);
    }

    // ── Feint guard ───────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts that Feint() returns false on the very first startup tick of a
    /// JabStep, documenting that the jab has NO recall window — the jab IS the
    /// feint, so a further abort would make the bait free (see the class doc).
    /// </summary>
    [Fact]
    public void JabStep_FeintOnFirstStartupTick_ReturnsFalse()
    {
        var machine = new CommittedMoveMachine();
        machine.Begin(new JabStep());

        machine.Tick();
        Assert.Equal(MovePhase.Startup, machine.Phase);

        bool feinted = machine.Feint();

        Assert.False(feinted, "JabStep cannot be feinted (feintWindowFrames = 0).");
        Assert.Equal(MovePhase.Startup, machine.Phase);
    }
}
