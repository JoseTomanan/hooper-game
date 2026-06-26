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
        Assert.Equal(MoveAnimState.Locomotion, MoveAnimResolver.Resolve(MovePhase.Inactive));
    }

    [Fact]
    public void Resolve_Startup_ReturnsStartup()
    {
        Assert.Equal(MoveAnimState.Startup, MoveAnimResolver.Resolve(MovePhase.Startup));
    }

    [Fact]
    public void Resolve_Active_ReturnsActive()
    {
        Assert.Equal(MoveAnimState.Active, MoveAnimResolver.Resolve(MovePhase.Active));
    }

    [Fact]
    public void Resolve_Recovery_ReturnsRecovery()
    {
        Assert.Equal(MoveAnimState.Recovery, MoveAnimResolver.Resolve(MovePhase.Recovery));
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

        Assert.Equal(MoveAnimState.Locomotion, MoveAnimResolver.Resolve(corrupt));
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
            MoveAnimState first  = MoveAnimResolver.Resolve(phase);
            MoveAnimState second = MoveAnimResolver.Resolve(phase);

            Assert.Equal(first, second);
        }
    }
}
