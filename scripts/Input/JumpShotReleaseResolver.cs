#nullable enable

namespace Hooper.Moves;

/// <summary>
/// Pure C# decision for "should the ball release this tick" (M7b, issue
/// #74) — no Godot Node inheritance. Extracted so
/// PlayerController.JustReleasedJumpShot (which BallController.
/// CheckJumpShotRelease reads) is unit-testable, mirroring MoveAnimResolver/
/// DisplayPhaseResolver's pattern: the decision itself is pure; the Node-
/// bound classes are thin glue reading CommittedMoveMachine fields into this
/// function's parameters.
///
/// The ball's release moment is deliberately keyed off JustEnteredActive, not
/// just Phase == Active — JustEnteredActive is already a single-tick pulse
/// (CommittedMoveMachine's own contract), so this fires exactly once per
/// jump shot, on the tick the move enters Active, never on every tick the
/// move happens to remain Active.
/// </summary>
public static class JumpShotReleaseResolver
{
    /// <returns>
    /// True only when <paramref name="justEnteredActive"/> is true AND
    /// <paramref name="currentMove"/> is a <see cref="JumpShot"/> — a
    /// Crossover (or any other future move) entering Active never releases
    /// the ball.
    /// </returns>
    public static bool ShouldRelease(bool justEnteredActive, CommittedMove? currentMove)
        => justEnteredActive && currentMove is JumpShot;
}
