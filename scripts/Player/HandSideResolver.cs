using Hooper.Moves;

namespace Hooper.Player;

/// <summary>
/// Pure C# resolver for which hand the ball-handler's ball is currently
/// displayed in — no Godot Node inheritance, no engine singletons. Mirrors
/// FacingResolver/LeanResolver's pattern (M7a, issues #38/#39), extended for
/// M7b's ball-on-hand cue (issue #73): "a dribble move where the ball-handler
/// switches the ball from one hand to the other" (Crossover's own doc) — this
/// is the resolver that makes that switch actually visible.
///
/// The hand flips exactly once per directional move, on the tick the DISPLAY
/// phase first becomes Active with a nonzero burst direction — the same tick
/// LeanResolver's burst lean fires, for the same reason (the switch is the
/// move's visible payoff, not something to telegraph in Startup or linger
/// through Recovery).
///
/// ── Why compare phases instead of reading JustEnteredActive directly ──────
/// CommittedMoveMachine.JustEnteredActive is a LOCAL-ONLY one-shot pulse: it
/// is never set on a forced resync (see ForceState's doc), and the client's
/// copy of a REMOTE holder never ticks its own machine at all (the #69 gap).
/// Comparing the previous and current DISPLAY phase (DisplayPhaseResolver's
/// output, read every tick regardless of role) instead works identically for
/// every role — own-simulated AND remote-displayed alike — exactly like
/// PlayerController.ApplyAnimation already does for MoveAnimResolver's
/// Travel()-on-change check.
///
/// Cosmetic-only discipline: the return value affects only which side of the
/// holder the ball mesh renders, never authoritative ball/holder state
/// (ADR-0004).
/// </summary>
public static class HandSideResolver
{
    /// <summary>
    /// Resolves the hand side to display this tick.
    /// </summary>
    /// <param name="current">The hand side displayed last tick.</param>
    /// <param name="previousPhase">The display phase from the previous tick.</param>
    /// <param name="currentPhase">The display phase this tick.</param>
    /// <param name="burstDirection">
    /// The active move's burst direction (+1 = right, -1 = left, 0 = no
    /// directional payload — e.g. a JumpShot). Only consulted on the
    /// Active-entry tick; ignored otherwise.
    /// </param>
    /// <returns>
    /// The flipped hand side on the tick <paramref name="currentPhase"/>
    /// first becomes Active with a nonzero burst direction; otherwise
    /// <paramref name="current"/> unchanged.
    /// </returns>
    public static HandSide Resolve(HandSide current, MovePhase previousPhase, MovePhase currentPhase, float burstDirection)
    {
        bool justEnteredActive = currentPhase == MovePhase.Active && previousPhase != MovePhase.Active;
        if (!justEnteredActive || burstDirection == 0f)
            return current;

        return burstDirection > 0f ? HandSide.Right : HandSide.Left;
    }
}
