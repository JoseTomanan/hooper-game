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
    /// <see cref="MovePhase.Inactive"/> maps to <see cref="MoveAnimState.Locomotion"/>
    /// (the neutral idle/run game, blended separately from velocity) — UNLESS
    /// <paramref name="isPivotingInPlace"/> is true, in which case Inactive
    /// maps to <see cref="MoveAnimState.Pivot"/> instead (issue #242).
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
    /// <returns>The display animation state for that phase.</returns>
    public static MoveAnimState Resolve(MovePhase phase, bool isFadeaway = false, bool isPivotingInPlace = false)
    {
        switch (phase)
        {
            case MovePhase.Inactive:
                return isPivotingInPlace ? MoveAnimState.Pivot : MoveAnimState.Locomotion;
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
    /// CommittedMoveMachine — see MoveAnimState's doc). FadeawayActive is
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
    /// <returns>The AnimationTree state name to Travel() to.</returns>
    public static string ResolveStateName(MoveAnimState generic, string? moveId)
    {
        bool phaseIsPerMoveEligible = generic is MoveAnimState.Startup or MoveAnimState.Active or MoveAnimState.Recovery;

        if (phaseIsPerMoveEligible
            && !string.IsNullOrEmpty(moveId)
            && ClippedMovePrefixes.TryGetValue(moveId, out string? prefix))
        {
            return prefix + generic;
        }

        return generic.ToString();
    }
}
