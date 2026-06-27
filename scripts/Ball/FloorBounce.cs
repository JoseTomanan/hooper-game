using System;
using Godot;

namespace Hooper.Ball;

/// <summary>
/// Pure deterministic floor-bounce resolver for a loose basketball (issue #66,
/// M8 realism pass).
///
/// ── Why pure / why here ───────────────────────────────────────────────────
/// ADR-0004 mandates bit-identical ball motion on every peer. This helper has
/// no Godot Node inheritance, no engine singletons, no Random, no DateTime.
/// BallController calls Resolve() in TickLoose; the tunables (FloorRestitution,
/// FloorHorizontalDecay, FloorSettleSpeed) are exported fields there so the
/// human can dial them in the editor without touching code. Same headless-seam
/// discipline as CourtBounds and RimBackboard.
///
/// ── Why a helper instead of inlining in BallController? ──────────────────
/// BallController extends Node3D and therefore cannot be instantiated by the
/// headless xUnit test project (it would pull in engine state). Putting the
/// bounce math in this pure static class lets us unit-test it directly —
/// same pattern as CourtBounds.Clamp and RimBackboard.Resolve.
///
/// ── Physics model ────────────────────────────────────────────────────────
/// The floor plane is at Y = 0; the ball centre rests one ball-radius above it.
/// Contact is defined as:  position.Y &lt;= ballRadius.
///
/// For an up-normal n̂ = (0, 1, 0) the general restitution reflection
///
///     v' = v − (1 + e)(v · n̂)n̂
///
/// simplifies to just flipping the Y component and scaling it:
///
///     v'Y = −vY * e        (vertical: reflected and energy-reduced)
///     v'X = vX * hDecay    (horizontal: mild friction each contact)
///     v'Z = vZ * hDecay
///
/// where e = FloorRestitution ∈ [0..1] and hDecay = FloorHorizontalDecay ∈ [0..1].
///
/// ── Settle threshold (why it exists) ─────────────────────────────────────
/// A basketball bouncing at 0.1 m/s looks like it is barely moving; at 0.02 m/s
/// the oscillation is invisible but the integrator still runs — and over
/// hundreds of ticks it re-accumulates tiny Y errors that make the ball jitter
/// in place. Settling (zeroing velocity) when the post-bounce vertical speed
/// would fall below FloorSettleSpeed prevents infinite micro-bounce loops and
/// is standard practice in rigid-body physics solvers.
///
/// Settle check: if  |vY| * floorRestitution &lt; settleSpeed  the bounce would
/// be imperceptible; zero all velocity instead. No separate horizontal check is
/// needed: once vertical motion can no longer bounce the ball, the tiny
/// remaining horizontal drift is irrelevant and zeroing it is correct (the ball
/// is effectively at rest).
/// </summary>
public static class FloorBounce
{
    /// <summary>
    /// Returns the post-contact (position, velocity) for a ball that has
    /// reached or crossed the floor plane at Y = <paramref name="ballRadius"/>.
    ///
    /// Safe to call regardless of floor contact: when position.Y &gt; ballRadius
    /// the inputs are returned unchanged so the caller need not guard the call
    /// site itself (though BallController already guards with the same check for
    /// performance).
    /// </summary>
    /// <param name="position">Ball centre world position after ShotArc.Step(dt).</param>
    /// <param name="velocity">Ball velocity after ShotArc.Step(dt).</param>
    /// <param name="ballRadius">
    ///     Ball radius in metres (≈ 0.12 regulation). The floor contact plane is
    ///     Y = ballRadius (ball centre, not surface, relative to Y = 0 floor).
    /// </param>
    /// <param name="floorRestitution">
    ///     Coefficient of restitution for floor contact [0..1].
    ///     1 = perfectly elastic (no energy loss), 0 = dead-stop.
    ///     A regulation basketball on hardwood is roughly 0.55.
    /// </param>
    /// <param name="horizontalDecay">
    ///     Fraction of horizontal speed retained after each floor contact [0..1].
    ///     Models rolling friction / slip: 0.8 means 20% of XZ speed is lost
    ///     on each bounce. 1.0 = frictionless floor.
    /// </param>
    /// <param name="settleSpeed">
    ///     Post-bounce vertical speed threshold (m/s). When
    ///     |velocity.Y| * floorRestitution is below this value the ball settles
    ///     immediately (velocity zeroed) rather than producing a micro-bounce.
    ///     Prevents infinite-bounce jitter at rest. 0.5 m/s is imperceptible.
    /// </param>
    /// <returns>
    ///     A tuple of the corrected (position, velocity).  The caller must write
    ///     both back into the ShotArc to take effect.
    /// </returns>
    public static (Vector3 position, Vector3 velocity) Resolve(
        Vector3 position, Vector3 velocity,
        float ballRadius, float floorRestitution,
        float horizontalDecay, float settleSpeed)
    {
        // Only apply when the ball centre has reached or crossed the floor plane.
        // Returning inputs unchanged keeps the helper safe for unconditional calls.
        if (position.Y > ballRadius)
            return (position, velocity);

        // ── Depenetrate ───────────────────────────────────────────────────────
        // Push the ball centre exactly to the floor contact plane.  This must
        // happen unconditionally — even if we end up settling, the final resting
        // position is ON the floor, not below it.
        position.Y = ballRadius;

        // ── Settle check ──────────────────────────────────────────────────────
        // If the post-bounce vertical speed would be below the settle threshold,
        // the bounce is imperceptible. Zero all velocity and return the resting
        // position.  This covers two cases:
        //   1. Ball is already moving upward (it bounced last tick and is rising
        //      away from the floor — it tunnelled back in due to gravity). In this
        //      case vY is positive and the ball is not incoming; let it rise.
        //   2. Ball is moving downward but too slowly to produce a visible bounce.
        //
        // We check vY < 0 first: only an incoming (downward-moving) ball needs
        // the settle test.  A ball moving upward at floor level is a stale
        // depenetration step — reflect it away so it can exit the floor.
        if (velocity.Y < 0f && MathF.Abs(velocity.Y) * floorRestitution < settleSpeed)
        {
            // Post-bounce speed would be below the visible threshold. Settle: the
            // ball comes to rest on the floor, all momentum absorbed.
            return (position, Vector3.Zero);
        }

        // ── Reflect ───────────────────────────────────────────────────────────
        // For the up-normal (0, 1, 0) the reflection v' = v − (1+e)(v·n̂)n̂
        // simplifies to: flip vY and scale by restitution; apply horizontal decay.
        //
        // vY must be incoming (negative) to reflect. If it is already positive
        // the ball is moving away from the floor — do not reflect again; just
        // return the depenetrated position with velocity unchanged (the ball will
        // rise back above the floor on the next tick).
        if (velocity.Y >= 0f)
            return (position, velocity);

        float newVY = -velocity.Y * floorRestitution;
        float newVX =  velocity.X * horizontalDecay;
        float newVZ =  velocity.Z * horizontalDecay;

        return (position, new Vector3(newVX, newVY, newVZ));
    }
}
