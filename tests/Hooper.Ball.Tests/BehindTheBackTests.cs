using Hooper.Moves;

namespace Hooper.Ball.Tests;

// Issue #194 — BehindTheBack is its OWN CommittedMove subclass (human call:
// NOT a parameter/flag on Crossover, cleaner to refine independently —
// docs/handoffs/M9-move-taxonomy.md). These tests pin its identity/frame-data
// contract and prove it needs zero special-casing in CommittedMoveMachine —
// composition over hierarchy means the machine that already sequences
// Crossover sequences this too, for free, because both are ordinary
// CommittedMove subclasses.
public class BehindTheBackTests
{
    [Fact]
    public void Constructor_SetsStableNetworkId()
    {
        // "behindtheback" is the wire moveId RequestBeginMove/MoveIdOf speak
        // (mirrors Crossover's "crossover") — a stable string other peers'
        // RequestBeginMove dispatch and BallController's sweep-path check
        // depend on; a rename here is a silent netcode break.
        var move = new BehindTheBack(burstDirection: 1f);
        Assert.Equal("behindtheback", move.Id);
    }

    [Fact]
    public void Constructor_StoresBurstDirection()
    {
        var move = new BehindTheBack(burstDirection: -1f);
        Assert.Equal(-1f, move.BurstDirection);
    }

    [Fact]
    public void DefaultFrameData_RecoveryIsShorterThanCrossovers()
    {
        // Spec (#194): "Recovery: comparable to, or slightly shorter than,
        // Crossover's" — never longer (a safer move must not also cost more
        // to recover from).
        Assert.True(BehindTheBack.DefaultFrameData.RecoveryFrames <= Crossover.DefaultFrameData.RecoveryFrames,
            $"BehindTheBack recovery ({BehindTheBack.DefaultFrameData.RecoveryFrames}) must be <= " +
            $"Crossover's ({Crossover.DefaultFrameData.RecoveryFrames}).");
    }

    [Fact]
    public void Machine_SequencesBehindTheBack_ThroughFullPhaseGraph()
    {
        // No special-casing needed in CommittedMoveMachine: BehindTheBack is
        // an ordinary CommittedMove, so Begin/Tick sequence it exactly like
        // Crossover — the discriminator this test pins against a class-
        // umbrella regression (if BehindTheBack silently became a Crossover
        // flag again, this test would still incidentally pass, but the
        // Id-based tests above would not).
        var machine = new CommittedMoveMachine();
        var move = new BehindTheBack(burstDirection: 1f);

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
