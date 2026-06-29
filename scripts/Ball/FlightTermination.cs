using Godot;

namespace Hooper.Ball;

/// <summary>
/// Pure predicate for ending the InFlight ball state when a shot/pass arc makes
/// NO rim or backboard contact (issue #63 follow-up — the "ball disappears" bug).
///
/// ── Why this exists ───────────────────────────────────────────────────────
/// TickInFlight resolves the arc against the basket each tick and transitions to
/// Loose on a rim/backboard Bounce or a Make. But RimBackboard.Resolve returns
/// ContactResult.None for a clean miss — an air ball, a shot scattered wide of
/// the rim, or a long pass that never reaches the basket. With None, the old
/// TickInFlight did nothing, so the arc kept integrating forever: the ball fell
/// straight through the floor (Y → −∞) or sailed through the scene walls to
/// infinity. The walls cannot stop it because the deterministic mini-physics
/// ball never consults Godot's collision system (ADR-0004) — its only
/// containment is CourtBounds, which lives in TickLoose and a never-terminating
/// flight never reaches.
///
/// This predicate gives TickInFlight the missing exit: a flight with no rim
/// contact ends — goes Loose — the moment the ball reaches the floor OR crosses
/// the court line. Once Loose, the existing TickLoose path takes over
/// (FloorBounce + rebound contest in bounds; OobResolution award when OOB).
///
/// ── Why pure / why here ───────────────────────────────────────────────────
/// Mirrors the headless-seam discipline of CourtBounds, OobResolution, and
/// FloorBounce (ADR-0004): no Godot Node, no engine singletons. TickInFlight
/// owns the engine state and passes the ball's position and the court rectangle
/// in as plain values, so this rule is unit-testable without a Godot runtime.
///
/// ── Why floor OR out-of-bounds ────────────────────────────────────────────
///   • Floor: a missed shot or air ball that comes down inside the court has
///     landed — it is now a loose ball to be rebounded, not still "in flight."
///   • Out-of-bounds: a shot/pass whose arc carries it across the court line
///     (in the air or along the floor) is a dead ball; TickLoose's OobResolution
///     then awards possession to the opponent of the last shooter (ADR-0008
///     §Amendment 2026-06-28). Catching it here, in flight, means a ball sailing
///     over the sideline turns over immediately instead of flying away first.
/// </summary>
public static class FlightTermination
{
    /// <summary>
    /// Returns <see langword="true"/> when an in-flight ball that has NOT
    /// contacted the rim or backboard this tick should transition to Loose:
    /// either it has reached the floor (Y ≤ <paramref name="ballRadius"/>) or it
    /// has crossed the court rectangle (<see cref="CourtBounds.IsOutOfBounds"/>).
    ///
    /// Call ONLY on ContactResult.None — a Bounce or Make already drives its own
    /// transition in TickInFlight.
    /// </summary>
    /// <param name="position">Current ball-centre world position (post-Step).</param>
    /// <param name="ballRadius">
    /// Ball radius (metres). The floor contact fires when the ball centre has
    /// descended to one radius above the floor plane — the same threshold
    /// TickLoose's FloorBounce uses, so the hand-off is seamless.
    /// </param>
    /// <param name="courtMin">Court lower bound: X = left edge, Y = near edge (smallest Z).</param>
    /// <param name="courtMax">Court upper bound: X = right edge, Y = far edge (largest Z).</param>
    public static bool ShouldGoLoose(Vector3 position, float ballRadius, Vector2 courtMin, Vector2 courtMax)
    {
        return position.Y <= ballRadius
            || CourtBounds.IsOutOfBounds(position, courtMin, courtMax);
    }
}
