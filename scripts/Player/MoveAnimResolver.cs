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
    /// <returns>The display animation state for that phase.</returns>
    public static MoveAnimState Resolve(MovePhase phase)
    {
        switch (phase)
        {
            case MovePhase.Inactive:
                return MoveAnimState.Locomotion;
            case MovePhase.Startup:
                return MoveAnimState.Startup;
            case MovePhase.Active:
                return MoveAnimState.Active;
            case MovePhase.Recovery:
                return MoveAnimState.Recovery;

            // TODO(you): decide the fallback for an unrecognized phase value.
            //
            // MovePhase is a closed enum today, so this case is only reachable if
            // someone adds a 5th phase later (or a corrupt/cast value arrives).
            // This function runs in the per-tick render path. Two options:
            //
            //   (a) graceful: `return MoveAnimState.Locomotion;`
            //       — degrades to neutral stance, never crashes the render path.
            //       Matches this codebase's "never throw in a tick loop" stance
            //       (see CommittedMoveMachine.ForceState's doubt-cycle comment and
            //       Begin() returning false instead of throwing).
            //
            //   (b) strict: `throw new ArgumentOutOfRangeException(nameof(phase), ...);`
            //       — fails loud so a future unmapped phase is caught in tests
            //       immediately rather than silently rendering as Locomotion.
            //
            // Replace the line below with your choice. The test
            // MoveAnimResolverTests.Resolve_UnknownPhase_* pins whichever you pick.
            default:
                return MoveAnimState.Locomotion;
        }
    }
}
