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
            MoveAnimResolver.ResolveStateName(MoveAnimState.ReboundGrab, "crossover", HandSide.Left, HandSide.Right));
        Assert.Equal("ReboundGrab",
            MoveAnimResolver.ResolveStateName(MoveAnimState.ReboundGrab, null, HandSide.Left, HandSide.Right));
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
    public void ResolveStateName_Dribble_IsNeverPerMove()
    {
        // Dribble is the neutral stance, not a committed move, so it is never
        // per-move-eligible (only Startup/Active/Recovery are) — exactly like
        // Locomotion and Pivot. A stale moveId alongside it must not produce
        // "CrossoverDribble", a state the tree does not have and that Travel()
        // would silently no-op against.
        //
        // (#294) It IS hand-split, which is a separate axis — so the assertion
        // is "the moveId made no difference", not "the name is bare 'Dribble'".
        // Written as a moveId-invariance check rather than two literals so it
        // keeps testing the per-move exemption independently of the hand suffix.
        // reachSide (#282) is passed as the opposite of ballHand throughout, the
        // convention this file uses elsewhere (e.g.
        // ResolveStateName_ReachSideAndBallHand_AreNotInterchangeable below) to
        // prove the argument the assertion isn't about is truly inert.
        Assert.Equal(MoveAnimResolver.ResolveStateName(MoveAnimState.Dribble, null, HandSide.Left, HandSide.Right),
            MoveAnimResolver.ResolveStateName(MoveAnimState.Dribble, "crossover", HandSide.Left, HandSide.Right));
        Assert.Equal(MoveAnimResolver.ResolveStateName(MoveAnimState.Dribble, null, HandSide.Right, HandSide.Left),
            MoveAnimResolver.ResolveStateName(MoveAnimState.Dribble, "behindtheback", HandSide.Right, HandSide.Left));
        Assert.Equal(MoveAnimResolver.ResolveStateName(MoveAnimState.Dribble, null, HandSide.Left, HandSide.Right),
            MoveAnimResolver.ResolveStateName(MoveAnimState.Dribble, "jumpshot", HandSide.Left, HandSide.Right));
    }

    [Fact]
    public void ResolveStateName_Dribble_IsSuffixedByTheAuthoritativeHand()
    {
        // (#294) The defect this closes: the stance played one right-handed clip
        // regardless of HandSide, so after a crossover left the ball in the left
        // hand the animation still dribbled right — the ball and the animated
        // hand on opposite sides of the body.
        Assert.Equal("DribbleLeft", MoveAnimResolver.ResolveStateName(MoveAnimState.Dribble, null, HandSide.Left, HandSide.Right));
        Assert.Equal("DribbleRight", MoveAnimResolver.ResolveStateName(MoveAnimState.Dribble, null, HandSide.Right, HandSide.Left));
    }

    [Fact]
    public void ResolveStateName_Dribble_IsNotInvertedByOriginHand()
    {
        // The load-bearing negative. OriginHand is the obvious-looking place to
        // route this — it already turns a HandSide into a state-name suffix —
        // but it returns Opposite(ballHand) for every non-Startup phase, because
        // its job is undoing a crossover's Active-entry ball-hand flip. Dribble
        // is only ever resolved during MovePhase.Inactive, where there is no flip
        // to undo, so routing through it would produce the EXACT MIRROR of the
        // truth: a state that exists, plays cleanly, and telegraphs the wrong
        // hand (the ADR-0003 false read).
        //
        // This asserts the two disagree rather than just asserting the correct
        // answer, so a future refactor that "unifies" the two suffix paths fails
        // here with a message that says why, instead of shipping a silent mirror.
        foreach (HandSide hand in new[] { HandSide.Left, HandSide.Right })
        {
            HandSide viaOriginHand = MoveAnimResolver.OriginHand(MoveAnimState.Dribble, hand);
            Assert.NotEqual(hand, viaOriginHand);
            Assert.NotEqual("Dribble" + viaOriginHand,
                MoveAnimResolver.ResolveStateName(MoveAnimState.Dribble, null, hand, viaOriginHand));
        }
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
    //
    // ── Test convention: ballHand and reachSide are deliberately OPPOSED ─────
    // (#282) ResolveStateName takes two same-typed HandSide parameters —
    // ballHand (a fact about the ball) and reachSide (a fact about the
    // defender's own body) — read by two DISJOINT move sets (HandedMoves vs
    // TargetHandedMoves). Every call below that is not itself pinning the
    // steal/reachSide axis passes reachSide as the OPPOSITE of ballHand,
    // never the same value, so that a resolver — or a future edit to this
    // file — that silently swapped the two arguments changes the expected
    // answer and goes RED instead of passing by luck. Do not "tidy" these
    // into matching values.

    [Fact]
    public void ResolveStateName_ClippedMoveActive_ReturnsPerMoveName()
    {
        // The base clipped-move case: a moveId in the table combined with
        // Active concatenates PascalCase prefix + phase name. Crossover is
        // additionally HANDED (#280), so the name carries the origin-hand
        // suffix; see the #280 block below for the full mapping and for why
        // Active inverts the hand it is given.
        Assert.Equal("CrossoverActiveLeft",
            MoveAnimResolver.ResolveStateName(MoveAnimState.Active, "crossover", HandSide.Right, HandSide.Left));
    }

    [Fact]
    public void ResolveStateName_ClippedMultiWordMoveActive_PinsCasing()
    {
        // Multi-word PascalCase prefix ("BehindTheBack") must not collapse or
        // re-case — the .tscn state name mirrors this table exactly, so a
        // casing slip here would silently break Travel() at runtime.
        //
        // (#281) Behind-the-back is now HANDED as well as clipped, so the name
        // also carries the origin-hand suffix and Active INVERTS the hand it is
        // given: the move swaps the ball at Active-entry, so the hand observed
        // during Active is already the destination hand and OriginHand maps it
        // back to the hand the move started in. Passing Left here therefore
        // resolves to ...Right. Pinning the inversion (not just the casing) is
        // the point: a resolver that dropped it would still return a state that
        // EXISTS and plays cleanly, while telegraphing the wrong direction —
        // the ADR-0003 false read, and how the #255 mirror bug shipped green.
        Assert.Equal("BehindTheBackActiveRight",
            MoveAnimResolver.ResolveStateName(MoveAnimState.Active, "behindtheback", HandSide.Left, HandSide.Right));
        Assert.Equal("BehindTheBackActiveLeft",
            MoveAnimResolver.ResolveStateName(MoveAnimState.Active, "behindtheback", HandSide.Right, HandSide.Left));

        // Startup does NOT invert — the swap has not happened yet, so the hand
        // observed IS the origin hand. Asserting both phases is what makes this
        // a non-symmetric check rather than a constant-suffix one.
        Assert.Equal("BehindTheBackStartupLeft",
            MoveAnimResolver.ResolveStateName(MoveAnimState.Startup, "behindtheback", HandSide.Left, HandSide.Right));
    }

    [Fact]
    public void ResolveStateName_ClippedButUnhandedMove_IgnoresHand()
    {
        // The guard that clipped does NOT imply handed (#280). Block is
        // clipped (ClippedMovePrefixes) but genuinely unhanded — a single
        // unsuffixed state per phase — and stays so: it has no ball to swap
        // and no aim-derived reach side either.
        //
        // (#282) Steal used to be the other example here, but it is no longer
        // unhanded — it moved to its OWN handed axis, split by reachSide
        // instead of ballHand, TargetHandedMoves (see the dedicated steal
        // section below). Block is now the sole unhanded exemplar.
        //
        // A hand passed here must be ignored, whichever hand it is — widening
        // HandedMoves ahead of the .tscn would resolve to a missing state, and
        // Travel() to a missing state only logs (#257) rather than throwing,
        // so the move would silently stop animating instead of failing loudly.
        Assert.Equal("BlockActive",
            MoveAnimResolver.ResolveStateName(MoveAnimState.Active, "block", HandSide.Left, HandSide.Right));
        Assert.Equal("BlockActive",
            MoveAnimResolver.ResolveStateName(MoveAnimState.Active, "block", HandSide.Right, HandSide.Left));
    }

    [Fact]
    public void ResolveStateName_ClippedMoveStartup_ReturnsPerMoveName()
    {
        Assert.Equal("JumpshotStartup", MoveAnimResolver.ResolveStateName(MoveAnimState.Startup, "jumpshot", HandSide.Left, HandSide.Right));
    }

    [Fact]
    public void ResolveStateName_ClippedMoveRecovery_ReturnsPerMoveName()
    {
        // (#282) steal is now split by reachSide, not ballHand — see the
        // dedicated steal section below for the full derivation. ballHand is
        // set to the OPPOSITE of reachSide here per this file's convention, so
        // a resolver that read the wrong axis would answer "StealRecoveryRight"
        // instead.
        Assert.Equal("StealRecoveryLeft",
            MoveAnimResolver.ResolveStateName(MoveAnimState.Recovery, "steal", HandSide.Right, HandSide.Left));
    }

    [Fact]
    public void ResolveStateName_ClippedMoveFadeawayActive_ReturnsGenericFadeawayActive()
    {
        // Issue #243 exemption: even though "jumpshot" is clipped and a
        // fadeaway release is a jumpshot outcome, FadeawayActive must stay the
        // single shared fadeaway clip — NOT "JumpshotFadeawayActive" — because
        // there is only one fadeaway clip regardless of which move triggered it.
        Assert.Equal("FadeawayActive", MoveAnimResolver.ResolveStateName(MoveAnimState.FadeawayActive, "jumpshot", HandSide.Left, HandSide.Right));
    }

    [Fact]
    public void ResolveStateName_ClippedMoveLocomotion_ReturnsGenericLocomotion()
    {
        // Locomotion has no committed move in progress by definition, so a
        // stray clipped moveId must never leak a per-move name onto it — and
        // with crossover now handed (#280), must not leak a hand suffix either.
        // "LocomotionLeft" is a state the tree has never had.
        Assert.Equal("Locomotion",
            MoveAnimResolver.ResolveStateName(MoveAnimState.Locomotion, "crossover", HandSide.Left, HandSide.Right));
        Assert.Equal("Locomotion",
            MoveAnimResolver.ResolveStateName(MoveAnimState.Locomotion, "crossover", HandSide.Right, HandSide.Left));
    }

    [Fact]
    public void ResolveStateName_ClippedMovePivot_ReturnsGenericPivot()
    {
        // Pivot is the in-place turn, not a committed move — never per-move.
        Assert.Equal("Pivot", MoveAnimResolver.ResolveStateName(MoveAnimState.Pivot, "block", HandSide.Left, HandSide.Right));
    }

    [Fact]
    public void ResolveStateName_UnclippedMoveActive_ReturnsGenericFallback()
    {
        // "jab" is a real committed move (RequestBeginMove sends it) but is NOT
        // in the clipped-move table, so it must fall back to the shared Active
        // clip rather than throwing or resolving to a nonexistent state name.
        Assert.Equal("Active", MoveAnimResolver.ResolveStateName(MoveAnimState.Active, "jab", HandSide.Left, HandSide.Right));
    }

    [Fact]
    public void ResolveStateName_NullMoveIdActive_ReturnsGenericFallback()
    {
        // No move in flight (own player's own-role reconstruction can pass
        // null) must degrade to the generic name, not throw.
        Assert.Equal("Active", MoveAnimResolver.ResolveStateName(MoveAnimState.Active, null, HandSide.Left, HandSide.Right));
    }

    [Fact]
    public void ResolveStateName_EmptyMoveIdActive_ReturnsGenericFallback()
    {
        Assert.Equal("Active", MoveAnimResolver.ResolveStateName(MoveAnimState.Active, string.Empty, HandSide.Left, HandSide.Right));
    }

    [Fact]
    public void ResolveStateName_CalledRepeatedlyWithSameInput_IsDeterministic()
    {
        // Purity guarantee mirrors Resolve_CalledRepeatedlyWithSameInput_IsDeterministic:
        // same (MoveAnimState, moveId) pair must always yield the same name, with
        // no I/O or hidden state — the renderer can call this every tick safely.
        string first  = MoveAnimResolver.ResolveStateName(MoveAnimState.Active, "crossover", HandSide.Left, HandSide.Right);
        string second = MoveAnimResolver.ResolveStateName(MoveAnimState.Active, "crossover", HandSide.Left, HandSide.Right);

        Assert.Equal(first, second);
    }

    // ── Hand-side mirror: crossover (issue #280) ─────────────────────────────
    //
    // A crossover is directional and ball-hand-side is authoritative (ADR-0012),
    // so playing the wrong polarity is not a cosmetic blemish — it is a FALSE
    // TELEGRAPH. The defender reads the wind-up to decide which way to commit,
    // and ADR-0003 makes that read a competitive requirement.
    //
    // The subtlety these tests exist for: PlayerController.HandSide is NOT
    // constant across the move. A crossover IS the act of changing hands, and
    // the swap lands on JustEnteredActive — the first Active tick. So the naive
    // "suffix the current hand" would give StartupRight → ActiveLeft →
    // RecoveryLeft: the wind-up telegraphing one direction and the cross itself
    // playing the mirror of it. MoveAnimResolver.OriginHand corrects for that by
    // inverting every post-swap phase, recovering the hand the ball STARTED in,
    // which is constant for the whole move and is what the suffix names.
    //
    // Every assertion below is NON-SYMMETRIC under an L↔R swap, deliberately.
    // The #255 mirror bug shipped precisely because its test was symmetric and
    // therefore passed on a broken mirror.

    [Fact]
    public void ResolveStateName_CrossoverStartup_SuffixesTheCurrentHand()
    {
        // Startup is the only phase BEFORE the swap, so there the current hand
        // already IS the origin hand and passes through unchanged.
        Assert.Equal("CrossoverStartupLeft",
            MoveAnimResolver.ResolveStateName(MoveAnimState.Startup, "crossover", HandSide.Left, HandSide.Right));
        Assert.Equal("CrossoverStartupRight",
            MoveAnimResolver.ResolveStateName(MoveAnimState.Startup, "crossover", HandSide.Right, HandSide.Left));
    }

    [Fact]
    public void ResolveStateName_CrossoverActiveAndRecovery_InvertThePostSwapHand()
    {
        // Active and Recovery are both AFTER the swap, so the authoritative hand
        // now holds the DESTINATION and must be inverted to recover the origin.
        // A ball that began in the left hand is in the right one by Active.
        Assert.Equal("CrossoverActiveLeft",
            MoveAnimResolver.ResolveStateName(MoveAnimState.Active, "crossover", HandSide.Right, HandSide.Left));
        Assert.Equal("CrossoverRecoveryLeft",
            MoveAnimResolver.ResolveStateName(MoveAnimState.Recovery, "crossover", HandSide.Right, HandSide.Left));

        Assert.Equal("CrossoverActiveRight",
            MoveAnimResolver.ResolveStateName(MoveAnimState.Active, "crossover", HandSide.Left, HandSide.Right));
        Assert.Equal("CrossoverRecoveryRight",
            MoveAnimResolver.ResolveStateName(MoveAnimState.Recovery, "crossover", HandSide.Left, HandSide.Right));
    }

    [Fact]
    public void ResolveStateName_CrossoverAcrossOneMove_KeepsASinglePolarity()
    {
        // The load-bearing test, and the one that would have caught the naive
        // implementation. It replays the hand values a real left-origin crossover
        // actually presents to the resolver, tick by tick: Left during the 6
        // Startup ticks, then Right from the Active-entry swap onward (the
        // ordering is real — _PhysicsProcess runs the role tick, which applies
        // the swap, strictly before ApplyAnimation, so the resolver sees the
        // post-swap hand on that very tick).
        //
        // All three names must carry the SAME suffix. A per-tick read of HandSide
        // would produce Left/Right/Right here and still look plausible in
        // isolation — only comparing the three exposes it.
        string startup  = MoveAnimResolver.ResolveStateName(MoveAnimState.Startup,  "crossover", HandSide.Left,  HandSide.Right);
        string active   = MoveAnimResolver.ResolveStateName(MoveAnimState.Active,   "crossover", HandSide.Right, HandSide.Left);
        string recovery = MoveAnimResolver.ResolveStateName(MoveAnimState.Recovery, "crossover", HandSide.Right, HandSide.Left);

        Assert.Equal("CrossoverStartupLeft",  startup);
        Assert.Equal("CrossoverActiveLeft",   active);
        Assert.Equal("CrossoverRecoveryLeft", recovery);

        // And the mirror image of that same move, so a resolver hard-coded to one
        // polarity cannot pass. This is the non-symmetric control (#255).
        Assert.Equal("CrossoverStartupRight",
            MoveAnimResolver.ResolveStateName(MoveAnimState.Startup, "crossover", HandSide.Right, HandSide.Left));
        Assert.Equal("CrossoverActiveRight",
            MoveAnimResolver.ResolveStateName(MoveAnimState.Active, "crossover", HandSide.Left, HandSide.Right));
        Assert.Equal("CrossoverRecoveryRight",
            MoveAnimResolver.ResolveStateName(MoveAnimState.Recovery, "crossover", HandSide.Left, HandSide.Right));
    }

    [Fact]
    public void ResolveStateName_CrossoverPolarities_AreNeverTheSameName()
    {
        // The cheapest possible guard against a resolver that ignores its hand
        // argument entirely — which every assertion above would still pass if the
        // expected strings were also wrong in the same direction.
        foreach (MoveAnimState phase in new[] { MoveAnimState.Startup, MoveAnimState.Active, MoveAnimState.Recovery })
        {
            Assert.NotEqual(
                MoveAnimResolver.ResolveStateName(phase, "crossover", HandSide.Left, HandSide.Right),
                MoveAnimResolver.ResolveStateName(phase, "crossover", HandSide.Right, HandSide.Left));
        }
    }

    // ── Hand-side split: steal, a DIFFERENT axis (issue #282) ────────────────
    //
    // Steal is clipped (ClippedMovePrefixes) but its handed split is NOT the
    // ball-hand-swap axis crossover/behindtheback use — a steal has no ball to
    // swap. It is split by reachSide instead: the DEFENDER's own body-relative
    // aim sign (StealMove.AimSign/ReachSide), fixed at construction and
    // constant for the whole move. See MoveAnimResolver.TargetHandedMoves'
    // doc comment for the full derivation of why ballHand — the natural-
    // looking but wrong discriminator — would silently animate the mirror-
    // image arm for every face-to-face duel.

    [Theory]
    [InlineData(MoveAnimState.Startup, "StealStartup")]
    [InlineData(MoveAnimState.Active, "StealActive")]
    [InlineData(MoveAnimState.Recovery, "StealRecovery")]
    public void ResolveStateName_StealAllPhases_SuffixesTheReachSide(MoveAnimState phase, string expectedPrefix)
    {
        // ballHand is fixed at Left throughout — it must play no part; see
        // ResolveStateName_StealIgnoresBallHand below for the direct version
        // of that same claim.
        Assert.Equal(expectedPrefix + "Left",
            MoveAnimResolver.ResolveStateName(phase, "steal", HandSide.Left, HandSide.Left));
        Assert.Equal(expectedPrefix + "Right",
            MoveAnimResolver.ResolveStateName(phase, "steal", HandSide.Left, HandSide.Right));
    }

    [Fact]
    public void ResolveStateName_StealAcrossOneMove_KeepsASinglePolarity()
    {
        // Mirrors ResolveStateName_CrossoverAcrossOneMove_KeepsASinglePolarity's
        // discipline, but pins the OPPOSITE claim: crossover's ball hand flips
        // mid-move and OriginHand corrects for it, but steal's reachSide must
        // NOT flip even though its ballHand does (simulating a hand swap
        // happening mid-steal, e.g. the holder recycling into a new dribble).
        // This is the test that catches the predictable wrong implementation
        // the #282 handoff explicitly warns against: routing steal through
        // OriginHand(generic, ballHand) instead of reading reachSide directly.
        //
        // reachSide is deliberately Right while the ballHand sequence below
        // (Left at Startup, Right from Active on) is exactly the flip pattern
        // OriginHand exists to normalize BACK to Left — so a mutant reading
        // OriginHand(generic, ballHand) instead of reachSide would answer
        // "...Left" for all three phases here, not "...Right". Mutation-
        // verified (#282): a first draft of this test used reachSide: Left,
        // which coincided with what OriginHand recovers from this exact
        // ballHand sequence, so the OriginHand mutant passed it BY ACCIDENT —
        // reachSide: Right is what actually makes the two implementations
        // diverge.
        const HandSide reachSide = HandSide.Right;

        string startup  = MoveAnimResolver.ResolveStateName(MoveAnimState.Startup,  "steal", HandSide.Left,  reachSide);
        string active   = MoveAnimResolver.ResolveStateName(MoveAnimState.Active,   "steal", HandSide.Right, reachSide);
        string recovery = MoveAnimResolver.ResolveStateName(MoveAnimState.Recovery, "steal", HandSide.Right, reachSide);

        Assert.Equal("StealStartupRight",  startup);
        Assert.Equal("StealActiveRight",   active);
        Assert.Equal("StealRecoveryRight", recovery);
    }

    [Fact]
    public void ResolveStateName_StealIgnoresBallHand()
    {
        // With reachSide fixed, either value of ballHand must give the SAME
        // name — the direct converse of ResolveStateName_StealAllPhases_
        // SuffixesTheReachSide's per-phase Left/Left vs Left/Right pair.
        Assert.Equal(
            MoveAnimResolver.ResolveStateName(MoveAnimState.Active, "steal", HandSide.Left,  HandSide.Right),
            MoveAnimResolver.ResolveStateName(MoveAnimState.Active, "steal", HandSide.Right, HandSide.Right));
    }

    [Fact]
    public void ResolveStateName_ReachSideAndBallHand_AreNotInterchangeable()
    {
        // THE transposition-hazard pin, and the most important new test in
        // this section (#282). ballHand and reachSide share the exact same
        // type (HandSide), so a call site — or a resolver implementation —
        // that swapped the two would compile silently.
        //
        // Startup is chosen deliberately: it is the one per-move-eligible
        // phase OriginHand never inverts, so the two expected answers below
        // are not a coincidental match. "steal" must read reachSide (Right)
        // and completely ignore ballHand (Left); "crossover" must read
        // ballHand (Left) via OriginHand and completely ignore reachSide
        // (Right). If either move read the OTHER axis, the suffix below would
        // silently flip to the other hand.
        Assert.Equal("StealStartupRight",
            MoveAnimResolver.ResolveStateName(MoveAnimState.Startup, "steal", HandSide.Left, HandSide.Right));
        Assert.Equal("CrossoverStartupLeft",
            MoveAnimResolver.ResolveStateName(MoveAnimState.Startup, "crossover", HandSide.Left, HandSide.Right));
    }

    [Fact]
    public void ResolveStateName_HandedSetsAreDisjoint()
    {
        // HandedMoves and TargetHandedMoves are both PRIVATE, so disjointness
        // can only be pinned BEHAVIOURALLY, through the public function.
        // ResolveStateName_StealIgnoresBallHand already proves ballHand plays
        // no part in steal's answer; this test additionally proves steal's
        // answer does NOT match what HandedMoves' OriginHand formula would
        // have produced for the very same (phase, ballHand) pair — if steal
        // were (wrongly) also a HandedMoves member, or TargetHandedMoves were
        // checked after HandedMoves instead of before, this would fail.
        const HandSide ballHand = HandSide.Right;
        const HandSide reachSide = HandSide.Right;

        string stealActive = MoveAnimResolver.ResolveStateName(MoveAnimState.Active, "steal", ballHand, reachSide);
        // What steal WOULD have produced had it been routed through
        // HandedMoves' OriginHand formula instead of reading reachSide.
        string wrongByHandedFormula = "Steal" + MoveAnimState.Active + MoveAnimResolver.OriginHand(MoveAnimState.Active, ballHand);

        Assert.Equal("StealActiveRight", stealActive);
        Assert.NotEqual(wrongByHandedFormula, stealActive);
    }

    [Fact]
    public void ResolveStateName_StealPolarities_AreNeverTheSameName()
    {
        // Mirrors ResolveStateName_CrossoverPolarities_AreNeverTheSameName:
        // the cheapest possible guard against a resolver that ignores
        // reachSide entirely.
        foreach (MoveAnimState phase in new[] { MoveAnimState.Startup, MoveAnimState.Active, MoveAnimState.Recovery })
        {
            Assert.NotEqual(
                MoveAnimResolver.ResolveStateName(phase, "steal", HandSide.Left, HandSide.Left),
                MoveAnimResolver.ResolveStateName(phase, "steal", HandSide.Left, HandSide.Right));
        }
    }

    [Fact]
    public void OriginHand_InvertsExactlyThePostSwapPhases()
    {
        // OriginHand's own contract, independent of any name concatenation.
        Assert.Equal(HandSide.Left,  MoveAnimResolver.OriginHand(MoveAnimState.Startup,  HandSide.Left));
        Assert.Equal(HandSide.Right, MoveAnimResolver.OriginHand(MoveAnimState.Startup,  HandSide.Right));

        Assert.Equal(HandSide.Right, MoveAnimResolver.OriginHand(MoveAnimState.Active,   HandSide.Left));
        Assert.Equal(HandSide.Left,  MoveAnimResolver.OriginHand(MoveAnimState.Active,   HandSide.Right));
        Assert.Equal(HandSide.Right, MoveAnimResolver.OriginHand(MoveAnimState.Recovery, HandSide.Left));
        Assert.Equal(HandSide.Left,  MoveAnimResolver.OriginHand(MoveAnimState.Recovery, HandSide.Right));
    }
}
