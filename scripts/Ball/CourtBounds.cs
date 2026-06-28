using Godot;

namespace Hooper.Ball;

/// <summary>
/// Pure geometry for the half-court play area: clamps a world position to the
/// court rectangle so a loose ball cannot roll off the floor edge.
///
/// ── Why pure / why here ───────────────────────────────────────────────────
/// Mirrors the headless-seam discipline of ClearLine and ReboundContest
/// (ADR-0004): no Godot Node, no engine singletons.  BallController calls
/// Clamp() in TickLoose; the bounds are exported fields there so a single
/// source of truth covers both this clamp and the matching StaticBody3D walls
/// the human places in the editor (EDITOR_TASKS.md — issue #46 court-bound step).
///
/// ── Why XZ only ─────────────────────────────────────────────────────────
/// The floor is at Y = 0 and the clamp only applies to a loose ball resting
/// on or settling toward that floor.  Y is managed by the floor-contact check
/// in TickLoose (BallRadius floor), not by court bounds.  Height is irrelevant
/// to "is the ball inside the court rectangle."
///
/// ── Why only TickLoose ───────────────────────────────────────────────────
/// A shot arc (TickInFlight) must travel freely toward the rim — clamping
/// mid-flight would break trajectories.  Only a loose ball that has left the
/// rim-contact phase should be bounded.
/// </summary>
public static class CourtBounds
{
    /// <summary>
    /// Returns <paramref name="position"/> with its X and Z components clamped
    /// to the rectangle [<paramref name="min"/>.X … <paramref name="max"/>.X] ×
    /// [<paramref name="min"/>.Y … <paramref name="max"/>.Y].  Y is preserved
    /// unchanged — see class doc "Why XZ only."
    /// </summary>
    /// <param name="position">World position to clamp (typically the ball centre).</param>
    /// <param name="min">
    /// Floor-plane lower bound: X = court left edge, Y = court near edge (smallest Z).
    /// Named <c>min</c> not <c>minXZ</c> to keep call sites concise; the Vector2
    /// type makes the XZ-only semantics explicit at the point of declaration.
    /// </param>
    /// <param name="max">Floor-plane upper bound: X = court right edge, Y = court far edge (largest Z).</param>
    /// <returns>
    /// The clamped position: X and Z are within [min, max]; Y is identical to
    /// the input.
    /// </returns>
    public static Vector3 Clamp(Vector3 position, Vector2 min, Vector2 max)
    {
        return new Vector3(
            Mathf.Clamp(position.X, min.X, max.X),
            position.Y,
            Mathf.Clamp(position.Z, min.Y, max.Y)
        );
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="position"/> lies
    /// outside the play-court rectangle [<paramref name="min"/> …
    /// <paramref name="max"/>] in the XZ plane.  Y is ignored — same reasoning
    /// as <see cref="Clamp"/> ("Why XZ only").
    ///
    /// Same rectangle as <see cref="Clamp"/>, opposite verb: detect the crossing
    /// instead of clamping back into it.  The boundary itself is considered
    /// <em>in-bounds</em> (strict less-than / greater-than), consistent with
    /// <see cref="Clamp"/> treating the edge as inside — a ball resting exactly
    /// on the sideline is still in play.
    /// </summary>
    /// <param name="position">World position to test (typically the ball centre).</param>
    /// <param name="min">Floor-plane lower bound: X = court left edge, Y = court near edge (smallest Z).</param>
    /// <param name="max">Floor-plane upper bound: X = court right edge, Y = court far edge (largest Z).</param>
    /// <returns>
    /// <see langword="true"/> if X or Z (or both) fall outside the [min, max]
    /// rectangle; <see langword="false"/> if the position is inside or exactly
    /// on a boundary edge.
    /// </returns>
    public static bool IsOutOfBounds(Vector3 position, Vector2 min, Vector2 max)
    {
        return position.X < min.X || position.X > max.X
            || position.Z < min.Y || position.Z > max.Y;
    }
}
