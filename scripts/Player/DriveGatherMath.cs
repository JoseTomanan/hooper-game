using Godot;

namespace Hooper.Player;

/// <summary>
/// Pure C# math for the drive-gather (issue #230, ADR-0022) — no Godot Node
/// inheritance, no engine singletons, no RPCs.
///
/// Two small, independently-testable pieces, kept separate because they
/// apply in two different phases of the move:
///   1. <see cref="WishDirToward"/> — the Startup-phase steering target: the
///      direction from the player toward the rim, fed into the SAME
///      HeadingMath.RotateToward function Move() itself uses (ADR-0010), so
///      the drive line resolves via the bounded turn-rate path, never an
///      instant snap.
///   2. <see cref="ComposeActiveVelocity"/> — the Active-entry burst: whatever
///      momentum survived Startup's hybrid-gather bleed, plus a forward
///      impulse along the (now-resolved) drive-line heading. SET once on
///      JustEnteredActive, never re-derived mid-Active — the same "SET not
///      +=" rule every burst-family move in this codebase follows (see
///      CrossoverBurstMath's class doc), so replayed reconciliation ticks
///      never overshoot.
/// </summary>
public static class DriveGatherMath
{
    /// <summary>
    /// Direction (world XZ, unit length) from <paramref name="fromXZ"/> toward
    /// <paramref name="targetXZ"/> — the Startup-phase steering target fed into
    /// HeadingMath.RotateToward. Returns Vector2.Zero (no steering input, same
    /// "hold current heading" convention RotateToward already treats a
    /// near-zero wishDir as) when the two points coincide, so a degenerate
    /// same-position case never produces a NaN from normalizing a zero vector.
    /// </summary>
    public static Vector2 WishDirToward(Vector2 fromXZ, Vector2 targetXZ)
    {
        Vector2 diff = targetXZ - fromXZ;
        return diff.LengthSquared() > 0.0001f ? diff.Normalized() : Vector2.Zero;
    }

    /// <summary>
    /// Composes the Active-phase velocity: whatever XZ momentum survived
    /// Startup's gather bleed, plus a forward impulse along
    /// <paramref name="heading"/> (the drive line HeadingMath.RotateToward
    /// resolved during Startup) at magnitude <paramref name="driveSpeed"/>.
    ///
    /// Unlike CrossoverBurstMath's family, there is no exit-vector/exit-cone
    /// steering here by design (ADR-0022): the drive-gather is a straight-line
    /// attack, not a player-shaped lateral burst — that lateral shaping is the
    /// euro-step's (#231) job, explicitly out of this move's scope.
    /// </summary>
    /// <param name="survivingVelocity">
    /// The XZ velocity carried into Active — whatever Startup's hybrid-gather
    /// bleed left behind. Y is ignored/passed through as 0.
    /// </param>
    /// <param name="heading">Player's authoritative heading in radians (ADR-0010), yaw 0 faces +Z.</param>
    /// <param name="driveSpeed">Forward drive-line burst magnitude (m/s).</param>
    /// <returns>The Active-phase velocity (Y always 0).</returns>
    public static Vector3 ComposeActiveVelocity(Vector3 survivingVelocity, float heading, float driveSpeed)
    {
        Vector2 forward = HeadingMath.Forward(heading);
        Vector2 survivingXZ = new(survivingVelocity.X, survivingVelocity.Z);
        Vector2 resultXZ = survivingXZ + forward * driveSpeed;
        return new Vector3(resultXZ.X, 0f, resultXZ.Y);
    }
}
