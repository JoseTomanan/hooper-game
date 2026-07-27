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

    // ── Fadeaway override (issue #243) ───────────────────────────────────────

    [Fact]
    public void Resolve_ActiveWithFadeaway_ReturnsFadeawayActive()
    {
        // A JumpShot released mid-pivot displays the distinct fadeaway/
        // off-balance clip instead of the normal Active clip (ADR-0003
        // legibility) — the trigger itself is FadeawayTriggerResolver's job;
        // this resolver just switches display state on the flag.
        Assert.Equal(MoveAnimState.FadeawayActive,
            MoveAnimResolver.Resolve(MovePhase.Active, isFadeaway: true));
    }

    [Fact]
    public void Resolve_ActiveWithoutFadeaway_ReturnsActive()
    {
        Assert.Equal(MoveAnimState.Active,
            MoveAnimResolver.Resolve(MovePhase.Active, isFadeaway: false));
    }

    [Theory]
    [InlineData(MovePhase.Inactive)]
    [InlineData(MovePhase.Startup)]
    [InlineData(MovePhase.Recovery)]
    public void Resolve_NonActiveWithFadeawayTrue_IgnoresFlag(MovePhase phase)
    {
        // isFadeaway only matters during Active — a fadeaway classification
        // stamped at release must not leak into Startup/Recovery/Locomotion
        // display, which stay on their normal generic clips.
        MoveAnimState withFlag    = MoveAnimResolver.Resolve(phase, isFadeaway: true);
        MoveAnimState withoutFlag = MoveAnimResolver.Resolve(phase, isFadeaway: false);

        Assert.Equal(withoutFlag, withFlag);
    }

    [Fact]
    public void Resolve_DefaultParameter_MatchesExplicitFalse()
    {
        // Every pre-#243 call site omits the new parameter; it must behave
        // exactly as isFadeaway: false so existing callers are unaffected.
        Assert.Equal(MoveAnimResolver.Resolve(MovePhase.Active, isFadeaway: false),
            MoveAnimResolver.Resolve(MovePhase.Active));
    }

    // ── Cross-flag precedence (issues #242 + #243 reconciled) ────────────────

    [Fact]
    public void Resolve_ActiveWithFadeawayAndPivotFlag_ReturnsFadeawayActive()
    {
        // Both flags are mutually exclusive by phase in practice (fadeaway is
        // stamped only during Active, the pivot latch only applies to
        // Inactive — see MoveAnimState's doc), but the resolver still needs a
        // defined answer if a caller ever passes both. Committed-move phases
        // always win over Pivot, so on Active isFadeaway alone decides; a
        // stray isPivotingInPlace=true must not steal the display away from
        // the fadeaway clip.
        Assert.Equal(MoveAnimState.FadeawayActive,
            MoveAnimResolver.Resolve(MovePhase.Active, isFadeaway: true, isPivotingInPlace: true));
    }

    [Fact]
    public void Resolve_InactiveWithPivotFlagAndFadeawayTrue_ReturnsPivot()
    {
        // Symmetric case: on Inactive, isFadeaway can't apply (it only bites
        // during Active), so isPivotingInPlace alone decides — a stray
        // isFadeaway=true must not suppress the Pivot display.
        Assert.Equal(MoveAnimState.Pivot,
            MoveAnimResolver.Resolve(MovePhase.Inactive, isFadeaway: true, isPivotingInPlace: true));
    }

    // ── Rebound grab flourish (issue #284) ───────────────────────────────────
    //
    // ReboundGrab is a cosmetic "Inactive flourish" like Pivot: it shows for a
    // short latch after a live rebound is secured, is anchored OUTSIDE the
    // MovePhase mapping, and a committed move always interrupts it. Precedence
    // within Inactive (grill decision + #284): ReboundGrab > Pivot > Locomotion.

    [Fact]
    public void Resolve_InactiveAndGrabbing_ReturnsReboundGrab()
    {
        // Tracer: while the grab latch holds during Inactive, the reach-and-
        // secure one-shot overrides the plain Locomotion display.
        Assert.Equal(MoveAnimState.ReboundGrab,
            MoveAnimResolver.Resolve(MovePhase.Inactive, isPlayingReboundGrab: true));
    }

    [Fact]
    public void Resolve_InactiveGrabbingAndPivoting_ReturnsReboundGrab()
    {
        // The load-bearing precedence call (user decision, #284): when a fresh
        // grab latch and a sustained pivot latch coincide, the grab wins for its
        // lifetime — the "just grabbed a board" read is fresher and more specific
        // than a turn-in-place. ReboundGrab > Pivot within Inactive.
        Assert.Equal(MoveAnimState.ReboundGrab,
            MoveAnimResolver.Resolve(MovePhase.Inactive, isPivotingInPlace: true, isPlayingReboundGrab: true));
    }

    [Theory]
    [InlineData(MovePhase.Startup, MoveAnimState.Startup)]
    [InlineData(MovePhase.Active, MoveAnimState.Active)]
    [InlineData(MovePhase.Recovery, MoveAnimState.Recovery)]
    public void Resolve_CommittedMoveAndGrabbing_IgnoresGrabFlag(MovePhase phase, MoveAnimState expected)
    {
        // "Beginning a move interrupts the flourish, never vice versa" (#284):
        // a committed-move phase must never yield the display to a stray grab
        // latch — the flourish is anchored strictly within Inactive, exactly as
        // Pivot is. In practice PlayerController drops the latch the instant a
        // move begins, but the resolver enforces the precedence itself rather
        // than trusting the caller.
        Assert.Equal(expected,
            MoveAnimResolver.Resolve(phase, isPlayingReboundGrab: true));
    }

    [Fact]
    public void Resolve_InactiveNotGrabbingNotPivoting_ReturnsLocomotion()
    {
        // Control anchoring the two guards above: with neither flourish latched,
        // Inactive maps to plain Locomotion — the grab flag defaults off and does
        // not perturb the ordinary idle/run game.
        Assert.Equal(MoveAnimState.Locomotion,
            MoveAnimResolver.Resolve(MovePhase.Inactive, isPivotingInPlace: false, isPlayingReboundGrab: false));
    }

    [Fact]
    public void Resolve_GrabDefaultParameter_MatchesExplicitFalse()
    {
        // Every pre-#284 call site omits isPlayingReboundGrab; it must behave
        // exactly as false so existing callers (and the #242/#243 tests above)
        // are unaffected.
        Assert.Equal(MoveAnimResolver.Resolve(MovePhase.Inactive, isPivotingInPlace: true, isPlayingReboundGrab: false),
            MoveAnimResolver.Resolve(MovePhase.Inactive, isPivotingInPlace: true));
    }

    [Fact]
    public void ResolveStateName_ReboundGrab_ReturnsGenericReboundGrab()
    {
        // ReboundGrab is a single shared flourish clip, not a committed move, so
        // it is never per-move-eligible (only Startup/Active/Recovery are) — even
        // if a stale moveId is passed alongside it, the name stays the generic
        // "ReboundGrab", mirroring how Pivot/Locomotion resolve. A per-move name
        // like "CrossoverReboundGrab" is a state the tree never has; Travel() to
        // it would silently no-op.
        Assert.Equal("ReboundGrab",
            MoveAnimResolver.ResolveStateName(MoveAnimState.ReboundGrab, "crossover"));
        Assert.Equal("ReboundGrab",
            MoveAnimResolver.ResolveStateName(MoveAnimState.ReboundGrab, null));
    }

    // ── Dribbling stance (issue #285) ────────────────────────────────────────
    //
    // Dribble is NOT a flourish — it REPLACES Locomotion as the neutral stance
    // for a player in live-dribble possession, so an opponent can read who has
    // the ball and that they can still drive (ADR-0003 legibility). It therefore
    // slots in directly above Locomotion and below both Inactive flourishes:
    // ReboundGrab > Pivot > Dribble > Locomotion.
    //
    // Why Pivot stays ABOVE Dribble (the load-bearing precedence call): a pivot
    // is a discrete footwork EVENT and the dribble loop is a sustained STANCE, so
    // the same reasoning #284 used for ReboundGrab applies unchanged. Decisively,
    // ranking Dribble higher would silently regress the shipped, harness-proven
    // #242 pivot display for every ball-handler who pivots — i.e. most of them —
    // and PivotAnimTest would be right to fail. Possession stays legible during a
    // pivot regardless, because the ball mesh renders in-hand off authoritative
    // hand-side (ADR-0012); the footwork read is the scarcer signal.

    [Fact]
    public void Resolve_InactiveAndDribbling_ReturnsDribble()
    {
        // Tracer: a live-dribbling holder in the neutral phase shows the dribble
        // stance instead of the possession-blind idle/run game.
        Assert.Equal(MoveAnimState.Dribble,
            MoveAnimResolver.Resolve(MovePhase.Inactive, isDribbling: true));
    }

    [Fact]
    public void Resolve_InactiveDribblingAndPivoting_ReturnsPivot()
    {
        // Pivot > Dribble (see the block comment): a dribbler turning in place
        // shows the plant/turn clip, preserving #242 unchanged.
        Assert.Equal(MoveAnimState.Pivot,
            MoveAnimResolver.Resolve(MovePhase.Inactive, isPivotingInPlace: true, isDribbling: true));
    }

    [Fact]
    public void Resolve_InactiveDribblingAndGrabbing_ReturnsReboundGrab()
    {
        // ReboundGrab > Dribble. These genuinely coincide in play: securing a
        // live rebound MAKES you the holder, so the grab latch and dribble
        // possession overlap for the latch's whole lifetime. The grab must win,
        // then settle into the dribble stance once it expires.
        Assert.Equal(MoveAnimState.ReboundGrab,
            MoveAnimResolver.Resolve(MovePhase.Inactive, isPlayingReboundGrab: true, isDribbling: true));
    }

    [Theory]
    [InlineData(MovePhase.Startup, MoveAnimState.Startup)]
    [InlineData(MovePhase.Active, MoveAnimState.Active)]
    [InlineData(MovePhase.Recovery, MoveAnimState.Recovery)]
    public void Resolve_CommittedMoveAndDribbling_IgnoresDribbleFlag(MovePhase phase, MoveAnimState expected)
    {
        // A committed move ALWAYS owns the display (ADR-0003: the startup/active/
        // recovery arc is the telegraph an opponent reads). Since a ball-handler
        // is dribbling for essentially every offensive committed move, this is the
        // common case rather than a defensive edge — if the flag leaked past the
        // phase mapping, every crossover would render as a dribble stance.
        Assert.Equal(expected, MoveAnimResolver.Resolve(phase, isDribbling: true));
    }

    [Fact]
    public void Resolve_InactiveNotDribbling_ReturnsLocomotion()
    {
        // Control: an off-ball defender (and a Held/dead-dribble holder, which
        // PlayerController reports as not-dribbling) keeps the existing neutral.
        Assert.Equal(MoveAnimState.Locomotion,
            MoveAnimResolver.Resolve(MovePhase.Inactive, isDribbling: false));
    }

    [Fact]
    public void Resolve_DribbleDefaultParameter_MatchesExplicitFalse()
    {
        // Every pre-#285 call site omits isDribbling; it must behave exactly as
        // false so the #242/#243/#284 mappings above are untouched.
        foreach (MovePhase phase in System.Enum.GetValues<MovePhase>())
        {
            Assert.Equal(MoveAnimResolver.Resolve(phase, isDribbling: false),
                MoveAnimResolver.Resolve(phase));
        }
    }

    [Fact]
    public void ResolveStateName_Dribble_ReturnsGenericDribble()
    {
        // Dribble is the neutral stance, not a committed move, so it is never
        // per-move-eligible (only Startup/Active/Recovery are) — exactly like
        // Locomotion and Pivot. A stale moveId alongside it must not produce
        // "CrossoverDribble", a state the tree does not have and that Travel()
        // would silently no-op against.
        Assert.Equal("Dribble", MoveAnimResolver.ResolveStateName(MoveAnimState.Dribble, "crossover"));
        Assert.Equal("Dribble", MoveAnimResolver.ResolveStateName(MoveAnimState.Dribble, null));
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

    // ── ResolveStateName: per-move display-state names (issue #277) ─────────
    //
    // ResolveStateName maps a MoveAnimState + moveId onto the actual
    // AnimationTree state name to Travel() to. The clipped-move table (SoT
    // for the .tscn state names) currently covers jumpshot/crossover/
    // behindtheback/steal/block; every other moveId — and every non-per-move-
    // eligible MoveAnimState — falls back to the generic phase name.

    [Fact]
    public void ResolveStateName_ClippedMoveActive_ReturnsPerMoveName()
    {
        // The base clipped-move case: a moveId in the table combined with
        // Active concatenates PascalCase prefix + phase name.
        Assert.Equal("CrossoverActive", MoveAnimResolver.ResolveStateName(MoveAnimState.Active, "crossover"));
    }

    [Fact]
    public void ResolveStateName_ClippedMultiWordMoveActive_PinsCasing()
    {
        // Multi-word PascalCase prefix ("BehindTheBack") must not collapse or
        // re-case — the .tscn state name mirrors this table exactly, so a
        // casing slip here would silently break Travel() at runtime.
        Assert.Equal("BehindTheBackActive", MoveAnimResolver.ResolveStateName(MoveAnimState.Active, "behindtheback"));
    }

    [Fact]
    public void ResolveStateName_ClippedMoveStartup_ReturnsPerMoveName()
    {
        Assert.Equal("JumpshotStartup", MoveAnimResolver.ResolveStateName(MoveAnimState.Startup, "jumpshot"));
    }

    [Fact]
    public void ResolveStateName_ClippedMoveRecovery_ReturnsPerMoveName()
    {
        Assert.Equal("StealRecovery", MoveAnimResolver.ResolveStateName(MoveAnimState.Recovery, "steal"));
    }

    [Fact]
    public void ResolveStateName_ClippedMoveFadeawayActive_ReturnsGenericFadeawayActive()
    {
        // Issue #243 exemption: even though "jumpshot" is clipped and a
        // fadeaway release is a jumpshot outcome, FadeawayActive must stay the
        // single shared fadeaway clip — NOT "JumpshotFadeawayActive" — because
        // there is only one fadeaway clip regardless of which move triggered it.
        Assert.Equal("FadeawayActive", MoveAnimResolver.ResolveStateName(MoveAnimState.FadeawayActive, "jumpshot"));
    }

    [Fact]
    public void ResolveStateName_ClippedMoveLocomotion_ReturnsGenericLocomotion()
    {
        // Locomotion has no committed move in progress by definition, so a
        // stray clipped moveId must never leak a per-move name onto it.
        Assert.Equal("Locomotion", MoveAnimResolver.ResolveStateName(MoveAnimState.Locomotion, "crossover"));
    }

    [Fact]
    public void ResolveStateName_ClippedMovePivot_ReturnsGenericPivot()
    {
        // Pivot is the in-place turn, not a committed move — never per-move.
        Assert.Equal("Pivot", MoveAnimResolver.ResolveStateName(MoveAnimState.Pivot, "block"));
    }

    [Fact]
    public void ResolveStateName_UnclippedMoveActive_ReturnsGenericFallback()
    {
        // "jab" is a real committed move (RequestBeginMove sends it) but is NOT
        // in the clipped-move table, so it must fall back to the shared Active
        // clip rather than throwing or resolving to a nonexistent state name.
        Assert.Equal("Active", MoveAnimResolver.ResolveStateName(MoveAnimState.Active, "jab"));
    }

    [Fact]
    public void ResolveStateName_NullMoveIdActive_ReturnsGenericFallback()
    {
        // No move in flight (own player's own-role reconstruction can pass
        // null) must degrade to the generic name, not throw.
        Assert.Equal("Active", MoveAnimResolver.ResolveStateName(MoveAnimState.Active, null));
    }

    [Fact]
    public void ResolveStateName_EmptyMoveIdActive_ReturnsGenericFallback()
    {
        Assert.Equal("Active", MoveAnimResolver.ResolveStateName(MoveAnimState.Active, string.Empty));
    }

    [Fact]
    public void ResolveStateName_CalledRepeatedlyWithSameInput_IsDeterministic()
    {
        // Purity guarantee mirrors Resolve_CalledRepeatedlyWithSameInput_IsDeterministic:
        // same (MoveAnimState, moveId) pair must always yield the same name, with
        // no I/O or hidden state — the renderer can call this every tick safely.
        string first  = MoveAnimResolver.ResolveStateName(MoveAnimState.Active, "crossover");
        string second = MoveAnimResolver.ResolveStateName(MoveAnimState.Active, "crossover");

        Assert.Equal(first, second);
    }
}
