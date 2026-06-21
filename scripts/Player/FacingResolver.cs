using Godot;

namespace Hooper.Player;

/// <summary>
/// Pure C# yaw resolver for the player mesh — no Godot Node inheritance,
/// no engine singletons, no _PhysicsProcess, no RPCs.
///
/// Extracted for M7a static readability (issues #38/#39) so that facing
/// direction can be unit-tested without a running Godot instance.
/// CharacterBody3D cannot be instantiated headlessly, so this logic lives
/// here as a plain static class; the visual node integration calls
/// ResolveYaw and applies the result to the mesh's rotation — the capsule
/// collision shape and authoritative velocity are never touched.
///
/// Cosmetic-only discipline: the return value affects only the visual mesh
/// transform, never Velocity, never any authoritative or replicated state.
/// The deterministic ball and server-authoritative netcode (ADR-0002,
/// ADR-0004) are completely unaware of this value.
/// </summary>
public static class FacingResolver
{
    // Below this horizontal speed the player is considered stationary.
    // The guard serves two purposes:
    //   1. Prevents Atan2(0, 0) — which is 0, not NaN, but would snap the
    //      mesh to face +X the instant the player releases the stick.
    //   2. Avoids snapping facing to the drift direction while the player
    //      is coasting to a stop — slow drift is not intentional facing.
    // A value of 0.1 m/s is well below the minimum intentional movement
    // speed (Accel fires at 30 m/s²; first tick is ~0.5 m/s at 60 fps).
    private const float SpeedEpsilon = 0.1f;

    /// <summary>
    /// Returns the yaw angle (in radians) the mesh should face, derived
    /// from the horizontal component of <paramref name="velocity"/>.
    ///
    /// Godot coordinate convention: Y is up, -Z is forward.  A Y-rotation
    /// of 0 points the mesh toward +Z, so the formula
    ///   Atan2(velocity.X, velocity.Z)
    /// rotates the mesh to face the velocity direction.  The exact
    /// sign/visual result is hitl sign-off — the human verifies in-editor
    /// that the mesh faces the run direction.
    /// </summary>
    /// <param name="velocity">World-space velocity (Y component is ignored).</param>
    /// <param name="currentYaw">The mesh's current Y-rotation in radians.</param>
    /// <returns>
    /// <paramref name="currentYaw"/> unchanged when horizontal speed is
    /// below <see cref="SpeedEpsilon"/>; otherwise the yaw of the velocity
    /// direction on the XZ plane.
    /// </returns>
    public static float ResolveYaw(Vector3 velocity, float currentYaw)
    {
        float horizontalSpeed = new Vector2(velocity.X, velocity.Z).Length();
        if (horizontalSpeed < SpeedEpsilon)
            return currentYaw;

        return Mathf.Atan2(velocity.X, velocity.Z);
    }
}
