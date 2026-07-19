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
    /// (the neutral idle/run game, blended separately from velocity).
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
    /// <returns>The display animation state for that phase.</returns>
    public static MoveAnimState Resolve(MovePhase phase, bool isFadeaway = false)
    {
        switch (phase)
        {
            case MovePhase.Inactive:
                return MoveAnimState.Locomotion;
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
}
