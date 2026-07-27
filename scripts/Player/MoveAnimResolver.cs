#nullable enable

using System.Collections.Generic;
using Hooper.Moves;

namespace Hooper.Player;

/// <summary>
/// Pure C# resolver mapping a committed-move <see cref="MovePhase"/> onto the
/// <see cref="MoveAnimState"/> the player mesh should display — no Godot Node
/// inheritance, no engine singletons, no _PhysicsProcess, no RPCs.
///
/// Extracted for M7b rigged animation (issue #41) so the phase→anim-state
/// mapping is unit-testable without a running Godot instance, exactly as
/// <see cref="FacingResolver"/> and <see cref="LeanResolver"/> are. The
/// AnimationTree integration in PlayerController calls Resolve each tick and
/// switches its state-machine playback to the returned state.
///
/// Cosmetic-only discipline: the return value selects only which animation clip
/// the mesh shows. It is a pure READ of authoritative phase and has no path back
/// into Velocity, CommittedMoveMachine, prediction, or any replicated state
/// (ADR-0002, ADR-0004). The renderer is downstream of gameplay; gameplay never
/// observes this value.
///
/// Note on <c>JustEnteredActive</c>: that one-shot signal is deliberately NOT a
/// parameter here. It is consumed directly by the node to fire one-shot effects
/// (the lateral burst), and re-triggering the Active clip from frame 0 on entry
/// is an AnimationTree concern, not a state-selection concern. Folding it in
/// would give Resolve a second input that doesn't change which state is shown,
/// muddying the pure mapping. Phase alone determines the displayed state.
/// </summary>
public static class MoveAnimResolver
{
    /// <summary>
    /// Returns the <see cref="MoveAnimState"/> the mesh should display for the
    /// given committed-move <paramref name="phase"/>.
    ///
    /// The four committed-move phases map one-to-one onto display states;
    /// <see cref="MovePhase.Inactive"/> maps to the NEUTRAL stance — either
    /// <see cref="MoveAnimState.Locomotion"/> (the possession-blind idle/run
    /// game) or <see cref="MoveAnimState.Dribble"/> when
    /// <paramref name="isDribbling"/> is true (issue #285) — and either can be
    /// overridden by an Inactive flourish: <see cref="MoveAnimState.Pivot"/>
    /// (issue #242) or <see cref="MoveAnimState.ReboundGrab"/> (issue #284).
    /// Both neutral stances blend on velocity separately, inside their own
    /// BlendSpace1D. Full Inactive precedence: ReboundGrab > Pivot > Dribble >
    /// Locomotion.
    /// </summary>
    /// <param name="phase">Current phase of the committed-move state machine
    /// (own player) or the broadcast phase (remote copy, issue #69).</param>
    /// <param name="isFadeaway">
    /// (Issue #243) True when the CURRENT (or, per DisplayFadeaway's own
    /// per-role reconstruction, the DISPLAYED) move is a JumpShot classified
    /// fadeaway/off-balance by FadeawayTriggerResolver. Only changes the
    /// result during <see cref="MovePhase.Active"/> — every other phase
    /// ignores it, since the fadeaway distinction is specifically about the
    /// release-frame clip, not the wind-up or landing. Defaults to false so
    /// every pre-#243 call site is unaffected.
    /// </param>
    /// <param name="isPivotingInPlace">
    /// The in-place pivot latch (issue #172's <c>IsPivotingInPlace</c>, own
    /// player via <c>_pivot.HasLatch</c> or remote copy via the adopted
    /// broadcast — both already correct for display, see PlayerController's
    /// TickClientRemotePlayer). Orthogonal to <paramref name="phase"/>: it
    /// overrides ONLY the Inactive→Locomotion mapping (issue #242) — a
    /// committed move already clears the latch on Begin (PivotPlantTest's
    /// committed-cancel scenario), so Startup/Active/Recovery never need to
    /// yield to it, but the resolver enforces that precedence itself rather
    /// than trusting the caller never to pass the combination. Mutually
    /// exclusive with <paramref name="isFadeaway"/> by phase (see
    /// <see cref="MoveAnimState"/>'s doc comment) — Active is what isFadeaway
    /// governs, Inactive is what this governs, so they never compete for the
    /// same call.
    /// </param>
    /// <param name="isDribbling">
    /// (Issue #285) True when the player this frame is in LIVE-dribble
    /// possession for DISPLAY purposes — <c>PlayerController.DisplayDribbling</c>,
    /// which reads the single replicated ball's <c>BallState.Dribbling</c> +
    /// holder, so it is already correct for every role. Changes the result only
    /// during <see cref="MovePhase.Inactive"/>, where it replaces the neutral
    /// <see cref="MoveAnimState.Locomotion"/> with
    /// <see cref="MoveAnimState.Dribble"/>; it ranks BELOW both Inactive
    /// flourishes (see below) and a committed move ignores it entirely, so an
    /// offensive move's telegraph is never overwritten by the stance. Defaults
    /// to false so every pre-#285 call site is unaffected.
    /// </param>
    /// <returns>The display animation state for that phase.</returns>
    public static MoveAnimState Resolve(MovePhase phase, bool isFadeaway = false, bool isPivotingInPlace = false, bool isPlayingReboundGrab = false, bool isDribbling = false)
    {
        switch (phase)
        {
            case MovePhase.Inactive:
                // Inactive precedence (grill decision + #284, extended by #285):
                // ReboundGrab > Pivot > Dribble > Locomotion.
                //
                // The two flourishes come first: the rebound grab is the
                // freshest, most specific event, so its short latch out-ranks a
                // sustained turn latch. Dribble is NOT a flourish — it is the
                // neutral stance that REPLACES Locomotion for a live-dribbling
                // holder — so it slots in directly above Locomotion, below both.
                //
                // Keeping Pivot above Dribble is deliberate (#285): a pivot is a
                // discrete footwork event and the dribble loop is a sustained
                // stance, so #284's "fresher/more specific event wins" reasoning
                // applies unchanged — and ranking Dribble higher would silently
                // regress #242's shipped pivot display for every ball-handler
                // who turns in place. Possession stays readable during a pivot
                // via the in-hand ball mesh (ADR-0012).
                //
                // All three only ever apply during Inactive: a committed move
                // (Startup/Active/Recovery) never reaches this branch, so none of
                // them can steal the display from a move in flight — which
                // matters most for isDribbling, since a ball-handler is dribbling
                // for essentially every offensive committed move.
                if (isPlayingReboundGrab) return MoveAnimState.ReboundGrab;
                if (isPivotingInPlace) return MoveAnimState.Pivot;
                return isDribbling ? MoveAnimState.Dribble : MoveAnimState.Locomotion;
            case MovePhase.Startup:
                return MoveAnimState.Startup;
            case MovePhase.Active:
                return isFadeaway ? MoveAnimState.FadeawayActive : MoveAnimState.Active;
            case MovePhase.Recovery:
                return MoveAnimState.Recovery;

            // Unrecognized phase → graceful fallback to neutral stance. MovePhase
            // is a closed enum today, so this is only reachable via a corrupt cast
            // or a future 5th phase. This runs in the per-tick render path, so it
            // degrades rather than throws — matching the codebase's "never throw in
            // a tick loop" stance (CommittedMoveMachine.Begin() returns false and
            // ForceState normalizes rather than throwing). A silently-unmapped
            // future phase animating as Locomotion is caught by the test
            // Resolve_UnknownPhase_DegradesToLocomotion, not by a runtime crash.
            default:
                return MoveAnimState.Locomotion;
        }
    }

    /// <summary>
    /// The per-move display-state table (issue #277) — the SINGLE SOURCE OF
    /// TRUTH for which committed moves get their own distinct AnimationTree
    /// clips, and the exact PascalCase spelling of each. The .tscn state
    /// machine's node names are hand-authored to mirror this dictionary
    /// exactly (ADR-0011); if a moveId is added here, the corresponding
    /// "&lt;Prefix&gt;Startup"/"&lt;Prefix&gt;Active"/"&lt;Prefix&gt;Recovery" states must
    /// also exist in the tree or Travel() silently no-ops onto whatever state
    /// was already playing.
    ///
    /// Deliberately NOT exhaustive over every <c>CommittedMove.Id</c>: moves
    /// without their own captured clip (jab, spin, betweenthelegs, ...) are
    /// meant to fall back to the shared generic clip via
    /// <see cref="ResolveStateName"/>'s default case — that is a placeholder-art
    /// decision (ADR-0020's low-spec fidelity ceiling doesn't require bespoke
    /// clips for every move), not an oversight. Rebound is intentionally absent
    /// too: it isn't a committed move (no MovePhase arc), so it never reaches
    /// this table at all.
    /// </summary>
    private static readonly Dictionary<string, string> ClippedMovePrefixes = new()
    {
        ["jumpshot"]      = "Jumpshot",
        ["crossover"]     = "Crossover",
        ["behindtheback"] = "BehindTheBack",
        ["steal"]         = "Steal",
        ["block"]         = "Block",
    };

    /// <summary>
    /// (Issue #280) The moves whose per-phase states are additionally split by
    /// hand side, so their clip name carries a "Left"/"Right" suffix naming the
    /// hand the ball STARTED in. A subset of
    /// <see cref="ClippedMovePrefixes"/> — a move must have its own clips before
    /// it can have handed ones.
    ///
    /// Why an explicit allowlist rather than "this move carries a burst
    /// direction": the burst param is populated for crossover, behindtheback,
    /// betweenthelegs AND inandout (see PlayerController.DisplayMove), and only
    /// some of those are hand-directional at all — an in-and-out never crosses
    /// the ball. Keying off the param would resolve a behind-the-back to
    /// "BehindTheBackStartupLeft", a state the tree does not have, and
    /// <c>Travel()</c> to a missing state only LOGS (#257) rather than throwing,
    /// so a shipped move would silently stop animating. Membership here is a
    /// promise that scenes/Player.tscn actually holds both variants.
    ///
    /// Equally important, and the reason this is not simply
    /// <c>ClippedMovePrefixes.Keys</c>: <see cref="OriginHand"/>'s
    /// phase-conditioned formula is only valid for a move that swaps the ball
    /// hand exactly at Active-entry. Crossover, BehindTheBack and BetweenTheLegs
    /// do (PlayerController's JustEnteredActive branch); Spin swaps on the LAST
    /// Active tick instead, and InAndOut never swaps. Adding a move here without
    /// checking its swap TIMING would produce a clip that is correct in Startup
    /// and inverted afterwards.
    ///
    /// #281 adds "behindtheback"; #282 adds "steal", which will need its own
    /// origin rule because a steal has no ball to swap — its handedness is the
    /// TARGET hand, not an origin.
    /// </summary>
    private static readonly HashSet<string> HandedMoves = new() { "crossover" };

    /// <summary>
    /// (Issue #280) The hand the ball was in when the currently-displayed move
    /// BEGAN, derived from the phase being displayed and the authoritative
    /// hand side as of this same frame.
    ///
    /// A crossover is the act of changing hands, so
    /// <c>PlayerController.HandSide</c> is NOT constant across the move: it
    /// flips on <c>JustEnteredActive</c>, the first Active tick. Reading it
    /// per-tick without this correction would display Startup on one polarity
    /// and Active/Recovery on the other — the wind-up telegraphing one direction
    /// and the cross itself playing the mirror of it, which is precisely the
    /// false read ADR-0003 forbids. Inverting the post-swap phases recovers the
    /// constant origin hand.
    ///
    /// Why derive rather than latch the hand at move-begin: the client's copy of
    /// a remote opponent has no local machine to latch on, and a latch set on
    /// the Inactive→Startup edge would simply be wrong if that peer dropped the
    /// Startup packets (6 ticks is ~100 ms). Deriving needs no history at all,
    /// and phase and hand side ride the SAME broadcast payload (see
    /// PlayerController's ReceiveState call), so the pair a remote peer reads is
    /// always a mutually-consistent server snapshot rather than two values that
    /// could arrive a tick apart.
    ///
    /// Why this is safe on the locally-simulated roles too: _PhysicsProcess runs
    /// the role tick — which advances the machine and applies the swap — strictly
    /// before ApplyAnimation, so on the Active-entry tick the resolver already
    /// sees the post-swap hand, which is exactly what this inversion assumes.
    ///
    /// Accepted imprecision: if possession is lost mid-move,
    /// BallController resets the holder's hand to Left and the remaining frames
    /// may show the wrong polarity. Per the #189 ruling the committed move plays
    /// to completion regardless, and with the ball gone there is no true
    /// direction left to telegraph.
    /// </summary>
    /// <param name="generic">The display state already resolved by <see cref="Resolve"/>.</param>
    /// <param name="ballHand">The holder's authoritative <see cref="HandSide"/> this frame.</param>
    public static HandSide OriginHand(MoveAnimState generic, HandSide ballHand) =>
        generic == MoveAnimState.Startup ? ballHand : HandStateResolver.Opposite(ballHand);

    /// <summary>
    /// Returns the exact AnimationTree state name the mesh's state machine
    /// should <c>Travel()</c> to, given the generic display state from
    /// <see cref="Resolve"/> and the moveId of the committed move currently
    /// (or, for a remote copy's reconstruction, the displayed) running.
    ///
    /// Why only Startup/Active/Recovery are per-move-eligible: those three are
    /// the committed-move commitment arc (ADR-0003 legibility) — the whole
    /// point of a per-move clip is to make each move's distinct wind-up/burst/
    /// cooldown READ differently so an opponent can tell crossover from
    /// behind-the-back at a glance. Locomotion and Pivot are never per-move
    /// because neither one IS a committed move (Locomotion is the no-move
    /// neutral game; Pivot is the in-place turn latch, orthogonal to
    /// CommittedMoveMachine — see MoveAnimState's doc). Dribble (#285) is
    /// exempt for the same reason as Locomotion, which it replaces: it is the
    /// no-move neutral stance for a live-dribbling holder, not a move — so
    /// "CrossoverDribble" is a state the tree deliberately does not have.
    /// FadeawayActive is
    /// exempt for the opposite reason: it IS tied to a committed move
    /// (JumpShot) but issue #243 deliberately built ONE shared fadeaway clip
    /// regardless of which move triggered it, so even though "jumpshot" is in
    /// the clipped-move table below, FadeawayActive must still resolve to the
    /// shared "FadeawayActive" name, never "JumpshotFadeawayActive" — a
    /// nonexistent state that Travel() would silently no-op against.
    ///
    /// Every other combination — an unclipped/unknown moveId (e.g. "jab",
    /// "spin", "betweenthelegs") on any phase, or a null/empty moveId (no move
    /// in flight) — degrades to the generic fallback name (<c>generic.ToString()</c>),
    /// matching Resolve's "never throw in a tick loop" stance: a moveId this
    /// resolver doesn't recognize just plays the shared clip instead of
    /// crashing or resolving to a state the tree doesn't have.
    ///
    /// Pure function of its two arguments (ADR-0002/0004 cosmetic-only
    /// discipline) — no Godot Node, no I/O, no hidden state; safe to call every
    /// tick from the renderer.
    /// </summary>
    /// <param name="generic">The display state already resolved by <see cref="Resolve"/>.</param>
    /// <param name="moveId">The <c>CommittedMove.Id</c> of the move currently
    /// (or, for display purposes, notionally) running — may be null or empty
    /// when no move is in flight.</param>
    /// <param name="ballHand">
    /// (Issue #280) The holder's authoritative <see cref="HandSide"/> as of this
    /// frame — <c>PlayerController.HandSide</c>, which is server-authoritative,
    /// predicted and broadcast (ADR-0012), so it is already correct for every
    /// role without a per-role branch. Read ONLY for a move in
    /// <see cref="HandedMoves"/>; every other move ignores it entirely.
    ///
    /// Deliberately REQUIRED rather than defaulted, unlike this class's other
    /// discriminators. Those degrade safely: a wrong <c>isFadeaway</c> falls
    /// back to the generic Active state, which exists and reads correctly. A
    /// wrong hand does not degrade — it resolves to a state that exists, plays
    /// cleanly, and telegraphs the WRONG DIRECTION. That is the false read this
    /// whole split exists to prevent, and a silent default is how the #255
    /// mirror bug shipped. Making it required turns "forgot the hand" into a
    /// compile error, which matters most for #281/#282 adding to
    /// <see cref="HandedMoves"/> later.
    /// </param>
    /// <returns>The AnimationTree state name to Travel() to.</returns>
    public static string ResolveStateName(MoveAnimState generic, string? moveId, HandSide ballHand)
    {
        bool phaseIsPerMoveEligible = generic is MoveAnimState.Startup or MoveAnimState.Active or MoveAnimState.Recovery;

        if (phaseIsPerMoveEligible
            && !string.IsNullOrEmpty(moveId)
            && ClippedMovePrefixes.TryGetValue(moveId, out string? prefix))
        {
            // (#280) A handed move's three phase states are split in two, and
            // the suffix names the hand the ball STARTED in — so "Left" is the
            // crossover that carries the ball toward the body's RIGHT. There is
            // no unsuffixed fallback: scenes/Player.tscn holds only the six
            // handed states, because HandSide is a two-valued enum and
            // OriginHand is total over it, so no third case can arise.
            return HandedMoves.Contains(moveId)
                ? prefix + generic + OriginHand(generic, ballHand)
                : prefix + generic;
        }

        return generic.ToString();
    }
}
