using Godot;

namespace Hooper.Player;

/// <summary>
/// Pure C# burst-vector composition for step-back (issue #197) — reuses
/// <see cref="CrossoverBurstMath"/>.ComposeActiveVelocity (issue #198) rather
/// than hand-rolling a second cone-clamp implementation, per that class's own
/// "one parameterized composition, not a move zoo" principle
/// (docs/handoffs/M9-move-taxonomy.md §2).
///
/// ── Why "flip the heading" is a safe reuse, not a hack ──────────────────────
/// CrossoverBurstMath.ComposeActiveVelocity's "never backward" clamp only
/// ever forbids contribution AGAINST whatever <c>heading</c> it's given.
/// Feeding it <c>heading + π</c> makes its internal "forward" axis equal to
/// the player's TRUE backward direction — so the SAME clamp that stops a
/// Crossover from exploding backward instead stops a step-back from
/// exploding forward, and the SAME exit-cone clamp narrows a cone around
/// TRUE BACKWARD instead of true forward ("back / back-left / back-right
/// side-steps only" per the issue spec). The function's internal
/// <c>rightAxis</c> becomes the player's true LEFT under this flip, but that
/// only relabels an internal basis vector: decomposing exitVector against an
/// orthonormal {rightAxis, forwardAxis} basis and recomposing against that
/// SAME basis reproduces exitVector exactly (mod clamping), so the final
/// WORLD-space result still points wherever the player actually pushed the
/// stick, regardless of which internal label carries which real-world side.
///
/// ── Why the exit vector is never left at raw player input ───────────────────
/// CrossoverBurstMath's own "no steering input" fallback
/// (<c>rightAxis * flickSign * burstSpeed</c>) exists for a move that always
/// carries a flick-sign payload. Step-back's gesture is vertical-only — there
/// is no left/right flick sign to fall back on. Passing flickSign=0 would
/// silently produce a ZERO burst on the single most common input (the player
/// holds RS down and does nothing else — a plain, unsteered step-back) —
/// exactly the "committed move that pays startup and gets nothing back"
/// failure CrossoverBurstMath's own DeadImpulseFloor exists to prevent for
/// Crossover. This class avoids the whole failure class by pre-substituting
/// a synthetic "straight back" exit vector whenever the real one is in the
/// deadzone, so ComposeActiveVelocity always sees a live, non-degenerate
/// exit vector and never reaches that flickSign-dependent fallback branch.
/// </summary>
public static class StepBackBurstMath
{
    /// <summary>
    /// Composes step-back's Active-phase velocity.
    /// </summary>
    /// <param name="heading">Player's authoritative heading (radians, ADR-0010).</param>
    /// <param name="exitVector">
    /// World-space left-stick reading (PlayerController.Move's wishDir
    /// convention) snapshotted at Active-entry — #198's model, reused
    /// verbatim for the backward cone.
    /// </param>
    /// <param name="burstSpeed">
    /// Backward burst magnitude (m/s) — shared for both the straight-back and
    /// back-lateral components (passed as both burstSpeed and
    /// forwardBurstScale below): no reason to weight one axis over the other
    /// for a symmetric backward cone.
    /// </param>
    /// <param name="exitDeadzone">Exit-vector magnitude at/below which the stick counts as neutral.</param>
    /// <param name="maxExitAngleRadians">
    /// Half-angle of the backward cone (issue #197's "back / back-left /
    /// back-right side-steps only").
    /// </param>
    /// <returns>The Active-phase velocity (Y always 0).</returns>
    public static Vector3 ComposeActiveVelocity(
        float heading,
        Vector2 exitVector,
        float burstSpeed,
        float exitDeadzone,
        float maxExitAngleRadians)
    {
        Vector2 effectiveExit = exitVector.LengthSquared() > exitDeadzone * exitDeadzone
            ? exitVector
            : -HeadingMath.Forward(heading); // neutral stick: a plain, straight-back hop

        return CrossoverBurstMath.ComposeActiveVelocity(
            survivingVelocity: Vector3.Zero, // step-back instant-zero-plants during Startup — no momentum to carry through
            heading: heading + Mathf.Pi,     // flip so the shared math's "forward" means true-backward (see class doc)
            flickSign: 1,                    // fixed tie-break for the rare exact-forward-push pole — no flick-sign payload exists for a vertical gesture
            exitVector: effectiveExit,
            burstSpeed: burstSpeed,
            forwardBurstScale: burstSpeed,
            exitDeadzone: exitDeadzone,
            maxExitAngleRadians: maxExitAngleRadians);
    }
}
