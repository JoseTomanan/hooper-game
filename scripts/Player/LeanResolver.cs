using System;
using Hooper.Moves;

namespace Hooper.Player;

/// <summary>
/// Pure C# lean/tilt resolver for the player mesh — no Godot Node
/// inheritance, no engine singletons, no _PhysicsProcess, no RPCs.
///
/// Extracted for M7a static readability (issues #38/#39) so that burst
/// lean can be unit-tested without a running Godot instance.
///
/// Design rationale by phase:
///
///   Active — the burst fires here (ADR-0003: effect fires in Active).
///     Leaning during Active makes the burst visually readable as bodily
///     weight being thrown into the move, not a floating push.  The
///     degree of tilt (≈12°) is enough to telegraph the commitment
///     without exaggerating it.
///
///   Startup — tilt is zero.  The Startup frame is the telegraph window;
///     the opponent reads *which direction* the move is going from the
///     player's body being still and wound up.  A pre-lean would give the
///     read away too early and collapse the mind-game.
///
///   Recovery — tilt is zero.  The Recovery frame is the punish window;
///     an upright, slowing stop reads as vulnerability and commitment.
///     Leaning during recovery would read as comedy jank rather than the
///     deliberate, weight-bearing stop that ADR-0003 requires.
///
///   Inactive — tilt is zero.  No burst is in flight; neutral stance.
///
/// Cosmetic-only discipline: the return value affects only the visual mesh
/// transform, never Velocity, never any authoritative or replicated state
/// (ADR-0004).  The exact axis/direction of tilt is hitl visual sign-off —
/// the human verifies in-editor that the lean reads as intended.
/// </summary>
public static class LeanResolver
{
    // Approximately 12 degrees expressed in radians.
    // Chosen to be visibly readable as bodily weight without looking
    // exaggerated at the camera angles the game uses.
    private const float LeanRadians = 0.21f;

    /// <summary>
    /// Returns the tilt angle (in radians) to apply to the mesh along the
    /// burst axis, based on the current committed-move phase.
    /// </summary>
    /// <param name="phase">Current phase of the committed move state machine.</param>
    /// <param name="burstDirection">
    /// Signed direction of the burst: positive = right, negative = left,
    /// zero = no burst (returns 0 regardless of phase).
    /// </param>
    /// <returns>
    /// A signed lean in radians during <see cref="MovePhase.Active"/>,
    /// or <c>0f</c> for all other phases.
    /// </returns>
    public static float ResolveTilt(MovePhase phase, float burstDirection)
    {
        if (phase != MovePhase.Active)
            return 0f;

        return Math.Sign(burstDirection) * LeanRadians;
    }
}
