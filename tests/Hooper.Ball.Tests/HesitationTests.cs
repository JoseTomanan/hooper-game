using Hooper.Moves;

namespace Hooper.Ball.Tests;

public class HesitationTests
{
    // ── Identity ──────────────────────────────────────────────────────────────

    [Fact]
    public void Hesitation_Id_IsHesitation()
    {
        Assert.Equal("hesitation", new Hesitation().Id);
    }

    [Fact]
    public void Hesitation_DisplayName_IsHesitation()
    {
        Assert.Equal("Hesitation", new Hesitation().DisplayName);
    }

    [Fact]
    public void Hesitation_FeintWindowFrames_IsZero()
    {
        // A hesitation is an honest commitment with no recall window.
        // FeintWindowFrames = 0 is a design constant, not a tunable default.
        Assert.Equal(0, new Hesitation().FrameData.FeintWindowFrames);
    }

    // ── Type identity (is not Crossover) ──────────────────────────────────────

    [Fact]
    public void Hesitation_IsNotCrossover()
    {
        // The two moves are distinct concrete types. A Hesitation carries no
        // burst direction field — callers that check for Crossover to read
        // BurstDirection must not accidentally treat a Hesitation as one.
        CommittedMove move = new Hesitation();
        Assert.IsNotType<Crossover>(move);
    }

    // ── Full lifecycle through CommittedMoveMachine ───────────────────────────

    /// <summary>
    /// Drives a Hesitation through its complete Startup→Active→Recovery→Inactive
    /// lifecycle and asserts that:
    ///   - JustEnteredActive fires exactly once (on the Active-entry tick), and
    ///   - the machine returns to Inactive after all recovery frames exhaust.
    ///
    /// Uses DefaultFrameData (startup=4, active=8, recovery=6) so that the
    /// frame counts are the living documentation of the placeholder timings.
    /// </summary>
    [Fact]
    public void Hesitation_FullLifecycle_JustEnteredActiveOnceAndReturnToInactive()
    {
        var machine = new CommittedMoveMachine();
        var hesi    = new Hesitation(); // DefaultFrameData: startup=4, active=8, recovery=6

        bool started = machine.Begin(hesi);
        Assert.True(started, "Begin should succeed from Inactive.");
        Assert.Equal(MovePhase.Startup, machine.Phase);

        // ── Startup phase: tick StartupFrames times ───────────────────────────
        // The machine enters Active on the StartupFrames-th tick.
        int justEnteredActiveCount = 0;

        for (int i = 0; i < hesi.FrameData.StartupFrames; i++)
        {
            machine.Tick();
            if (machine.JustEnteredActive) justEnteredActiveCount++;
        }

        // After StartupFrames ticks the machine should be in Active.
        Assert.Equal(MovePhase.Active, machine.Phase);
        Assert.Equal(1, justEnteredActiveCount);

        // ── Active phase: tick ActiveFrames times to exhaust it ──────────────
        // EnterPhase(Active) resets FrameInPhase to 0 during the final Startup
        // tick above. Active ends when FrameInPhase reaches ActiveFrames, which
        // requires exactly ActiveFrames more Tick() calls. JustEnteredActive was
        // already set (and counted) on the entry; it clears at the top of every
        // subsequent Tick(), so it must stay false for all these ticks.
        for (int i = 0; i < hesi.FrameData.ActiveFrames; i++)
        {
            machine.Tick();
            // JustEnteredActive must be false on every tick after the entry tick.
            Assert.False(machine.JustEnteredActive, $"JustEnteredActive should be false on Active tick {i}.");
            if (machine.JustEnteredActive) justEnteredActiveCount++;
        }

        // After all Active ticks the machine should have entered Recovery.
        Assert.Equal(MovePhase.Recovery, machine.Phase);

        // ── Recovery phase: tick RecoveryFrames times → Inactive ─────────────
        for (int i = 0; i < hesi.FrameData.RecoveryFrames; i++)
        {
            machine.Tick();
        }

        Assert.Equal(MovePhase.Inactive, machine.Phase);
        Assert.False(machine.IsActive);

        // Guard: JustEnteredActive fired exactly once across the entire lifecycle.
        Assert.Equal(1, justEnteredActiveCount);
    }

    // ── Feint guard ───────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts that Feint() returns false on the very first startup tick of a
    /// Hesitation, documenting that a hesitation has NO recall window.
    ///
    /// (Updated #202): Hesitation was ALWAYS unfeintable — this pins that this
    /// stays true even now that Crossover/BehindTheBack have JOINED it
    /// (feintWindowFrames: 0 for the whole dribble-move family, ADR-0003
    /// amendment) rather than being the sole contrast case. The test still
    /// pins DefaultFrameData.FeintWindowFrames == 0 explicitly so an
    /// accidental change is caught immediately.
    /// </summary>
    [Fact]
    public void Hesitation_FeintOnFirstStartupTick_ReturnsFalse()
    {
        var machine = new CommittedMoveMachine();
        machine.Begin(new Hesitation());

        // Advance one tick so FrameInPhase = 1 (we are in Startup).
        machine.Tick();
        Assert.Equal(MovePhase.Startup, machine.Phase);

        bool feinted = machine.Feint();

        // Feint must be impossible: FeintWindowFrames = 0, so the window is
        // always expired regardless of FrameInPhase.
        Assert.False(feinted, "Hesitation cannot be feinted (feintWindowFrames = 0).");

        // The machine must still be in Startup — the failed feint is a no-op.
        Assert.Equal(MovePhase.Startup, machine.Phase);
    }
}
