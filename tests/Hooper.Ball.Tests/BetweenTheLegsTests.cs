using Hooper.Moves;

namespace Hooper.Ball.Tests;

// Issue #199 — BetweenTheLegs is its OWN CommittedMove subclass (same human
// call #194 made for BehindTheBack: NOT a parameter/flag on Crossover,
// cleaner to refine independently — docs/handoffs/M9-move-taxonomy.md). These
// tests pin its identity/frame-data contract and prove it needs zero
// special-casing in CommittedMoveMachine — composition over hierarchy means
// the machine that already sequences Crossover/BehindTheBack sequences this
// too, for free, because all three are ordinary CommittedMove subclasses.
public class BetweenTheLegsTests
{
    [Fact]
    public void Constructor_SetsStableNetworkId()
    {
        // "betweenthelegs" is the wire moveId RequestBeginMove/MoveIdOf speak
        // (mirrors Crossover's "crossover"/BehindTheBack's "behindtheback") —
        // a stable string other peers' RequestBeginMove dispatch and
        // BallController's sweep-path check depend on; a rename here is a
        // silent netcode break.
        var move = new BetweenTheLegs(burstDirection: 1f);
        Assert.Equal("betweenthelegs", move.Id);
    }

    [Fact]
    public void Constructor_StoresBurstDirection()
    {
        var move = new BetweenTheLegs(burstDirection: -1f);
        Assert.Equal(-1f, move.BurstDirection);
    }

    [Fact]
    public void DefaultFrameData_StartupMatchesCrossovers()
    {
        // Spec (#199, post-filing correction): the ball travels the same
        // distance as a standard crossover, so BTL shares Crossover's
        // startup — NOT the longest of the three.
        Assert.Equal(Crossover.DefaultFrameData.StartupFrames, BetweenTheLegs.DefaultFrameData.StartupFrames);
    }

    [Fact]
    public void DefaultFrameData_RecoveryIsBetweenBehindTheBackAndCrossovers()
    {
        // Spec (#199): "Frame placeholder: ... recovery ~10-12 (comparable
        // to Crossover's)" combined with the "balanced midpoint" identity —
        // strictly between BehindTheBack's (safer, shorter) and Crossover's
        // (more explosive, longer) recovery, not equal to either.
        Assert.True(BetweenTheLegs.DefaultFrameData.RecoveryFrames > BehindTheBack.DefaultFrameData.RecoveryFrames,
            $"BetweenTheLegs recovery ({BetweenTheLegs.DefaultFrameData.RecoveryFrames}) must be > " +
            $"BehindTheBack's ({BehindTheBack.DefaultFrameData.RecoveryFrames}).");
        Assert.True(BetweenTheLegs.DefaultFrameData.RecoveryFrames < Crossover.DefaultFrameData.RecoveryFrames,
            $"BetweenTheLegs recovery ({BetweenTheLegs.DefaultFrameData.RecoveryFrames}) must be < " +
            $"Crossover's ({Crossover.DefaultFrameData.RecoveryFrames}).");
    }

    [Fact]
    public void Machine_SequencesBetweenTheLegs_ThroughFullPhaseGraph()
    {
        // No special-casing needed in CommittedMoveMachine: BetweenTheLegs is
        // an ordinary CommittedMove, so Begin/Tick sequence it exactly like
        // Crossover/BehindTheBack — the discriminator this test pins against
        // a class-umbrella regression (if BetweenTheLegs silently became a
        // Crossover flag again, this test would still incidentally pass, but
        // the Id-based tests above would not).
        var machine = new CommittedMoveMachine();
        var move = new BetweenTheLegs(burstDirection: 1f);

        Assert.True(machine.Begin(move));
        Assert.Equal(MovePhase.Startup, machine.Phase);

        for (int i = 0; i < move.FrameData.StartupFrames; i++) machine.Tick();
        Assert.Equal(MovePhase.Active, machine.Phase);

        for (int i = 0; i < move.FrameData.ActiveFrames; i++) machine.Tick();
        Assert.Equal(MovePhase.Recovery, machine.Phase);

        for (int i = 0; i < move.FrameData.RecoveryFrames; i++) machine.Tick();
        Assert.Equal(MovePhase.Inactive, machine.Phase);
    }
}
