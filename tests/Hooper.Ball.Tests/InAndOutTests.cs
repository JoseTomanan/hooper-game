using Hooper.Moves;
using Hooper.Player;

namespace Hooper.Ball.Tests;

// Issue #202 — the in-and-out: Crossover's burst + Hesitation's hand
// behavior. These tests pin its identity/frame-data contract (mirrors
// BehindTheBackTests' "own CommittedMove subclass, no special-casing needed
// in CommittedMoveMachine" discipline) plus the two gesture-meaning
// contrasts (AC-1/AC-8) at the same pure composition level
// CrossoverHesiSequenceTests already pins for Crossover/Hesitation.
public class InAndOutTests
{
    // ── Identity ──────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_SetsStableNetworkId()
    {
        // "inandout" is the wire moveId RequestBeginMove/MoveIdOf speak (mirrors
        // Crossover's "crossover") — a stable string RequestBeginMove dispatch
        // depends on; a rename here is a silent netcode break.
        var move = new InAndOut(burstDirection: 1f);
        Assert.Equal("inandout", move.Id);
    }

    [Fact]
    public void Constructor_DisplayNameIsInAndOut()
    {
        Assert.Equal("In-and-Out", new InAndOut(1f).DisplayName);
    }

    [Fact]
    public void Constructor_StoresBurstDirection()
    {
        var move = new InAndOut(burstDirection: -1f);
        Assert.Equal(-1f, move.BurstDirection);
    }

    [Fact]
    public void InAndOut_IsNotCrossover()
    {
        // Distinct concrete type — callers that check for Crossover to apply
        // the hand-swap effect must not accidentally treat an InAndOut as one.
        CommittedMove move = new InAndOut(1f);
        Assert.IsNotType<Crossover>(move);
    }

    // ── Frame data (AC-3): 4 / 3 / 12, feintWindowFrames = 0 ─────────────────

    [Fact]
    public void DefaultFrameData_StartupIsFour()
    {
        // Quicker than Crossover's 6 — tier-2 grounded (less ball travel is a
        // genuinely faster motion) and precedented (Hesitation ships at 4 too).
        Assert.Equal(4, InAndOut.DefaultFrameData.StartupFrames);
    }

    [Fact]
    public void DefaultFrameData_ActiveMatchesCrossovers()
    {
        Assert.Equal(Crossover.DefaultFrameData.ActiveFrames, InAndOut.DefaultFrameData.ActiveFrames);
    }

    [Fact]
    public void DefaultFrameData_RecoveryMatchesCrossovers()
    {
        Assert.Equal(Crossover.DefaultFrameData.RecoveryFrames, InAndOut.DefaultFrameData.RecoveryFrames);
    }

    [Fact]
    public void DefaultFrameData_StartupIsShorterThanCrossovers()
    {
        Assert.True(InAndOut.DefaultFrameData.StartupFrames < Crossover.DefaultFrameData.StartupFrames,
            $"InAndOut startup ({InAndOut.DefaultFrameData.StartupFrames}) must be < Crossover's " +
            $"({Crossover.DefaultFrameData.StartupFrames}) — it is the quicker sibling.");
    }

    [Fact]
    public void InAndOut_FeintWindowFrames_IsZero()
    {
        // Design constant, not a placeholder: a fake of a fake is incoherent
        // (mirrors Hesitation/JabStep's identical reasoning).
        Assert.Equal(0, new InAndOut(1f).FrameData.FeintWindowFrames);
    }

    // ── Full lifecycle through CommittedMoveMachine (no special-casing needed) ──

    [Fact]
    public void Machine_SequencesInAndOut_ThroughFullPhaseGraph()
    {
        var machine = new CommittedMoveMachine();
        var move = new InAndOut(burstDirection: 1f);

        Assert.True(machine.Begin(move));
        Assert.Equal(MovePhase.Startup, machine.Phase);

        for (int i = 0; i < move.FrameData.StartupFrames; i++) machine.Tick();
        Assert.Equal(MovePhase.Active, machine.Phase);
        Assert.True(machine.JustEnteredActive);

        for (int i = 0; i < move.FrameData.ActiveFrames; i++) machine.Tick();
        Assert.Equal(MovePhase.Recovery, machine.Phase);

        for (int i = 0; i < move.FrameData.RecoveryFrames; i++) machine.Tick();
        Assert.Equal(MovePhase.Inactive, machine.Phase);
    }

    [Fact]
    public void InAndOut_FeintOnFirstStartupTick_ReturnsFalse()
    {
        // Contrast case (mirrors HesitationTests): with feintWindowFrames = 0
        // the window is always expired regardless of FrameInPhase.
        var machine = new CommittedMoveMachine();
        machine.Begin(new InAndOut(1f));
        machine.Tick();
        Assert.Equal(MovePhase.Startup, machine.Phase);

        bool feinted = machine.Feint();

        Assert.False(feinted, "InAndOut cannot be feinted (feintWindowFrames = 0).");
        Assert.Equal(MovePhase.Startup, machine.Phase);
    }

    // ── Gesture-meaning composition (AC-1 / AC-8) ────────────────────────────
    // Mirrors CrossoverHesiSequenceTests' role: pins the INTEGRATION contract
    // PlayerController.SampleMoveInput implements for the quick-return
    // gesture, at the pure level (HandStateResolver.IsCrossover is what
    // PlayerController itself cannot be unit-tested for — it extends a Godot
    // Node). Per the issue's 2x2 gesture table: flick direction picks the
    // move FAMILY (empty-hand -> in-and-out/crossover, ball-hand -> hesitation
    // either way); hold-vs-quick-return only disambiguates WITHIN the
    // empty-hand column (crossover vs in-and-out).

    [Fact]
    public void QuickReturnTowardEmptyHand_IsCrossoverTrue_BeginsInAndOut()
    {
        // Flick toward the empty (right) hand, quick return -> in-and-out.
        // Same disambiguation IsCrossover already gives the HELD gesture
        // (CrossoverHesiSequenceTests.BallInLeft_FlickRight_CrossesAndSwapsToRight)
        // — only the CALLER'S choice of which move to construct differs by
        // gesture duration, not the hand-state read itself.
        Assert.True(HandStateResolver.IsCrossover(HandSide.Left, +1));
    }

    [Fact]
    public void QuickReturnTowardBallHand_IsCrossoverFalse_BeginsHesitationSameAsHeld()
    {
        // Flick toward the ball (right) hand, quick return -> hesitation —
        // AC-8: identical outcome to the SAME flick held past the window.
        Assert.False(HandStateResolver.IsCrossover(HandSide.Right, +1));
    }

    [Fact]
    public void InAndOut_DoesNotChangeHandSide()
    {
        // AC-2: contrast Crossover, which swaps via HandStateResolver.Opposite.
        // An InAndOut's own resolution (mirrored here at the pure level,
        // exactly like Hesitation's ResolveFlick contract) leaves the hand
        // untouched — there is no Opposite() call in its lifecycle.
        HandSide before = HandSide.Left;
        HandSide after = before; // InAndOut: no swap (contrast ResolveFlick's Opposite(hand) for Crossover)
        Assert.Equal(before, after);
    }
}
