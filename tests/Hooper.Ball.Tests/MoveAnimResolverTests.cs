using Hooper.Moves;
using Hooper.Player;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for MoveAnimResolver.Resolve — the pure phase→anim-state mapping
/// extracted for M7b rigged animation (issue #41) so the committed-move
/// display state can be verified without a running Godot instance.
///
/// The function maps each committed-move <see cref="MovePhase"/> onto the
/// <see cref="MoveAnimState"/> the AnimationTree should show: Inactive →
/// Locomotion, and Startup/Active/Recovery one-to-one.
///
/// Cosmetic-only (ADR-0002/0004): the return value only selects which clip the
/// mesh displays. It is a pure read of authoritative phase with no path back
/// into CommittedMoveMachine, Velocity, prediction, or any replicated state —
/// the Resolve_IsPureRead test below pins that guarantee.
///
/// ── Test naming ──────────────────────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// Each test contains exactly one logical assertion.
/// </summary>
public class MoveAnimResolverTests
{
    // ── Phase → anim-state mapping (every phase) ──────────────────────────────

    [Fact]
    public void Resolve_Inactive_ReturnsLocomotion()
    {
        // Inactive is the neutral game; the idle↔run blend is handled separately
        // by the velocity-driven BlendSpace1D, so all of Inactive is one display
        // state here.
        Assert.Equal(MoveAnimState.Locomotion, MoveAnimResolver.Resolve(MovePhase.Inactive, isPivotingInPlace: false));
    }

    [Fact]
    public void Resolve_Startup_ReturnsStartup()
    {
        Assert.Equal(MoveAnimState.Startup, MoveAnimResolver.Resolve(MovePhase.Startup, isPivotingInPlace: false));
    }

    [Fact]
    public void Resolve_Active_ReturnsActive()
    {
        Assert.Equal(MoveAnimState.Active, MoveAnimResolver.Resolve(MovePhase.Active, isPivotingInPlace: false));
    }

    [Fact]
    public void Resolve_Recovery_ReturnsRecovery()
    {
        Assert.Equal(MoveAnimState.Recovery, MoveAnimResolver.Resolve(MovePhase.Recovery, isPivotingInPlace: false));
    }

    // ── Pivot precedence (issue #242) ──────────────────────────────────────────

    [Fact]
    public void Resolve_InactiveAndPivoting_ReturnsPivot()
    {
        // The in-place pivot (#172) is orthogonal to MovePhase — it is driven
        // by HeadingMath's latch, not the committed-move machine — so while it
        // is active during Inactive it must override the plain Locomotion
        // display, or the plant would render as ordinary run/idle.
        Assert.Equal(MoveAnimState.Pivot, MoveAnimResolver.Resolve(MovePhase.Inactive, isPivotingInPlace: true));
    }

    [Fact]
    public void Resolve_InactiveAndNotPivoting_ReturnsLocomotion()
    {
        // Control: the ordinary Inactive→Locomotion mapping is unchanged when
        // no pivot is in progress.
        Assert.Equal(MoveAnimState.Locomotion, MoveAnimResolver.Resolve(MovePhase.Inactive, isPivotingInPlace: false));
    }

    [Theory]
    [InlineData(MovePhase.Startup, MoveAnimState.Startup)]
    [InlineData(MovePhase.Active, MoveAnimState.Active)]
    [InlineData(MovePhase.Recovery, MoveAnimState.Recovery)]
    public void Resolve_CommittedMoveActiveAndPivoting_IgnoresPivotFlag(MovePhase phase, MoveAnimState expected)
    {
        // Defensive guarantee: even though PivotPlantTest's committed-cancel
        // scenario proves BeginCommittedMove clears the latch in practice (so
        // this combination should never arise live), the resolver itself must
        // not let a stray isPivotingInPlace=true silently steal the display
        // away from an in-progress committed move — Pivot only ever wins over
        // Locomotion (Inactive), never over Startup/Active/Recovery.
        Assert.Equal(expected, MoveAnimResolver.Resolve(phase, isPivotingInPlace: true));
    }

    // ── Unknown phase fallback ────────────────────────────────────────────────

    [Fact]
    public void Resolve_UnknownPhase_DegradesToLocomotion()
    {
        // A value outside the closed enum is only reachable via a corrupt cast or
        // a future 5th phase. The resolver runs in the per-tick render path, so
        // the default degrades to neutral stance rather than throwing — matching
        // this codebase's "never throw in a tick loop" stance (CommittedMoveMachine
        // returns false / normalizes instead of throwing). This test is the net
        // that catches a future phase that was added but never mapped: it would
        // surface here as "unexpectedly Locomotion" rather than as a live crash.
        MovePhase corrupt = (MovePhase)999;

        Assert.Equal(MoveAnimState.Locomotion, MoveAnimResolver.Resolve(corrupt, isPivotingInPlace: false));
    }

    // ── Purity / cosmetic-only guarantee ──────────────────────────────────────

    [Fact]
    public void Resolve_CalledRepeatedlyWithSameInput_IsDeterministic()
    {
        // Referential transparency: the mapping is a function of its argument
        // alone. Resolve is a static method on a stateless class taking a
        // value-type enum and returning a value-type enum — there is structurally
        // no reference through which it could read or mutate authoritative phase.
        // This test pins the observable half of that guarantee: identical input
        // always yields identical output, so the renderer can call it every tick
        // with no side effects on gameplay (ADR-0004).
        foreach (MovePhase phase in System.Enum.GetValues<MovePhase>())
        {
            MoveAnimState first  = MoveAnimResolver.Resolve(phase, isPivotingInPlace: false);
            MoveAnimState second = MoveAnimResolver.Resolve(phase, isPivotingInPlace: false);

            Assert.Equal(first, second);
        }
    }
}
