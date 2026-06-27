using System;
using Godot;

namespace Hooper.Ball;

/// <summary>
/// Pure geometry for heading-based shot accuracy penalty: returns a scatter
/// multiplier that grows as the shooter's facing diverges from the target.
///
/// ── Why pure / why here ───────────────────────────────────────────────────
/// Follows the headless-seam discipline (ADR-0004): no Godot Node, no engine
/// singletons, no RNG.  The caller (BallController on the server) reads
/// <c>holder.Heading</c> (server-authoritative, ADR-0010) and passes it in —
/// the same injection pattern MovementScatterK and ContestScatterK use for
/// their inputs.
///
/// ── Why holder.Heading, NOT FacingResolver ───────────────────────────────
/// <c>FacingResolver</c> is cosmetic-only and client-local (ADR-0004's
/// explicit rule).  Reading it here would make a server-authoritative make/miss
/// outcome depend on a cosmetic, non-replicated value — exactly the "ADR-0004
/// trap" that blocked a facing penalty at issue #65 time (ADR-0009
/// §Resolved 2026-06-27).  <c>Heading</c> was elevated to server-authoritative
/// state specifically so orientation-dependent outcomes (#81, and future
/// pass-angle / block-arc) have an honest authoritative source (ADR-0010).
///
/// ── Angle → multiplier mapping ───────────────────────────────────────────
/// A linear map from [0, π] onto [1, 1 + facingScatterK] was chosen:
///   multiplier = 1 + facingScatterK × (angle / π)
/// This is the simplest shape that satisfies the design constraints:
///   • Squared-up (0°) → no penalty (multiplier 1).
///   • Back-to-basket (180°) → maximum penalty (1 + facingScatterK).
///   • Every intermediate angle costs proportionally — no sharp threshold.
///   • Designer-legible: at facingScatterK = 0.8 the back-to-basket penalty
///     is 1.8×, slightly below the full ContestScatterK = 1.0 on-ball penalty
///     (1 + 1.0 = 2.0).  A back-to-basket shot without a defender is harder
///     than an open squared-up shot but not as punishing as a full closeout —
///     which matches the basketball intuition of a "turnaround fadeaway."
/// The default FacingScatterK = 0.8 was chosen to keep the facing factor
/// clearly below the contest cap so the two penalties stack without doubling
/// the scatter ceiling — a 90° shot under pressure is noticeably harder, but
/// still makeable.
/// </summary>
public static class ShotFacing
{
    /// <summary>
    /// Returns a scatter multiplier ≥ 1.0 based on how far the shooter's
    /// heading diverges from the direction to <paramref name="target"/>.
    ///
    /// Multiplier = 1.0 when squared up (angle = 0); 1 + facingScatterK at
    /// 180° (back to basket).  Linear between the two extremes.
    ///
    /// Reads <paramref name="headingYaw"/> from the server-authoritative
    /// <c>Heading</c> field (ADR-0010), NOT from FacingResolver — keeping the
    /// computation inside ADR-0004's authoritative boundary.
    /// </summary>
    /// <param name="headingYaw">
    /// Shooter's current heading in radians (Y-rotation, Godot convention:
    /// Atan2(x, z) on the XZ plane, −Z forward at yaw = 0).  Must be the
    /// server-authoritative <c>PlayerController.Heading</c>, NOT any cosmetic
    /// FacingResolver output.
    /// </param>
    /// <param name="shooterPos">Shooter's world-space position.</param>
    /// <param name="target">World-space shot target (rim centre).</param>
    /// <param name="facingScatterK">
    /// Balance knob: penalty magnitude at 180°.  0 = no facing penalty at any
    /// angle; 1.0 = back-to-basket doubles the scatter radius.  Default 0.8
    /// on BallController (see class doc for rationale).
    /// </param>
    /// <returns>
    /// A multiplier in [1, 1 + facingScatterK].  Always ≥ 1 and finite;
    /// returns 1.0 when the shooter is directly on top of the rim (degenerate
    /// XZ distance, would otherwise produce NaN from Atan2(0,0)).
    /// </returns>
    public static float Multiplier(
        float   headingYaw,
        Vector3 shooterPos,
        Vector3 target,
        float   facingScatterK)
    {
        float dx = target.X - shooterPos.X;
        float dz = target.Z - shooterPos.Z;

        // Guard against the degenerate case (shooter standing on the rim).
        // Atan2(0, 0) is undefined; returning 1.0 gives no facing penalty,
        // which is correct: you cannot meaningfully "face away" from a target
        // you are already standing on.
        if (dx * dx + dz * dz < 1e-6f)
            return 1f;

        // Target yaw: direction from shooter to rim in radians.
        // Convention matches HeadingMath.RotateToward's desiredYaw formula:
        // Atan2(x, z) on the XZ ground plane (Godot Y-up, −Z forward).
        // A yaw of 0 points toward +Z; this formula faces the shooter toward
        // the basket.
        float targetYaw = MathF.Atan2(dx, dz);

        // Shortest angular distance in [0, π] between heading and target.
        // We normalise the raw difference into (−π, π] via the floor trick
        // (avoids a while-loop, matches HeadingMath's intent exactly), then
        // take the absolute value to get unsigned angular deviation.
        float diff = headingYaw - targetYaw;
        diff = diff - MathF.Tau * MathF.Floor((diff + MathF.PI) / MathF.Tau);
        float angle = MathF.Abs(diff); // [0, π]

        // Linear penalty: 1.0 at 0°, (1 + facingScatterK) at 180°.
        // Each π/facingScatterK radians of deviation adds 1/π of the full
        // penalty — simple, monotonic, and balance-tunable via a single export.
        return 1f + facingScatterK * (angle / MathF.PI);
    }
}
