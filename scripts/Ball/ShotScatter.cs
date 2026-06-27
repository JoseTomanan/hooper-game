using System;
using Godot;

namespace Hooper.Ball;

/// <summary>
/// Pure geometry for distance-based shot scatter: offsets a shot target in the
/// XZ plane to simulate a miss, with the offset magnitude growing with shot
/// distance up to a cap.
///
/// ── Why pure / why here ───────────────────────────────────────────────────
/// Mirrors the headless-seam discipline of CourtBounds, ClearLine, and
/// DribbleCycle (ADR-0004): no Godot Node, no engine singletons, no System.Random
/// inside.  The random samples are INJECTED by the caller (BallController on the
/// server), which owns the seeded RNG.  This keeps the helper unit-testable
/// without engine infrastructure and keeps the RNG lifecycle with the object that
/// needs determinism guarantees (the server-authoritative BallController — see
/// ADR-0002 / ADR-0004 and class doc "Why server-only").
///
/// ── Why injected samples ──────────────────────────────────────────────────
/// Determinism discipline (ADR-0004) requires the random draw to be seeded and
/// server-owned.  If this helper owned a Random instance it would either create
/// a second, independently-seeded source of randomness (breaking the single
/// authoritative stream) or it would need the seed injected anyway.  Injecting
/// the two pre-drawn samples instead is simpler: the helper becomes a pure
/// function of its inputs, which is the easiest shape to test and reason about.
///
/// ── Why uniform-disc sampling (sqrt of radius01) ─────────────────────────
/// A naive approach — sample r directly as radius01 * maxRadius — concentrates
/// points near the centre, because equal increments of r cover larger and larger
/// areas as r grows.  Correcting by r = maxRadius * sqrt(radius01) distributes
/// misses uniformly across the disc area, so there is no artificial cluster
/// at the center or the rim.  This matters for shot-miss feel: a scatter that
/// always hugs the center reads as "off by a hair," not as a genuine miss.
///
/// ── Why XZ only ──────────────────────────────────────────────────────────
/// ShotTarget is the rim — a fixed world-space point at the basket height.
/// Scatter is a horizontal displacement of WHERE the ball is aimed, not a
/// change to its launch height.  Leaving Y unchanged preserves the arc's apex
/// geometry and means the ball still passes through roughly the right height
/// even on a miss — the rim contact check (RimBackboard) then decides whether
/// the offset target falls inside the rim ring or outside.
/// </summary>
public static class ShotScatter
{
    // 2π — used to convert a [0,1) uniform sample into a full-circle angle.
    // Defined as a constant rather than inline MathF.PI * 2f so the derivation
    // is visible at the use site and the value is computed once.
    private const float Tau = 2f * MathF.PI;

    /// <summary>
    /// Returns <paramref name="target"/> offset by a random displacement in
    /// the XZ plane, with the displacement radius proportional to
    /// <paramref name="distance"/> up to <paramref name="maxScatter"/>,
    /// then scaled by <paramref name="accuracyMultiplier"/>.
    /// Y is preserved unchanged — see class doc "Why XZ only."
    ///
    /// The two random samples must be drawn by the caller from a seeded,
    /// server-owned <see cref="System.Random"/> — see class doc "Why injected
    /// samples."  Both must be in [0, 1).
    /// </summary>
    /// <param name="target">World-space rim target to scatter from.</param>
    /// <param name="distance">
    /// XZ-plane distance (metres) from the shooter to <paramref name="target"/>.
    /// Callers compute this as the horizontal distance only (ignoring Y) so
    /// that a tall player shooting from the same floor position as a short one
    /// gets the same scatter magnitude.
    /// </param>
    /// <param name="angle01">
    /// Uniform [0, 1) sample that controls the scatter direction.  Converted
    /// to a full-circle angle via <c>2π × angle01</c>.
    /// </param>
    /// <param name="radius01">
    /// Uniform [0, 1) sample that controls the scatter radius within the disc.
    /// Passed through <c>sqrt</c> before scaling so the distribution is uniform
    /// over the disc area — see class doc "Why uniform-disc sampling."
    /// </param>
    /// <param name="scatterPerMeter">
    /// Base scatter radius per metre of shot distance (metres/metre).  The raw
    /// radius before capping is <c>scatterPerMeter × distance</c>.
    /// </param>
    /// <param name="maxScatter">
    /// Maximum scatter radius (metres), regardless of distance.  Acts as a hard
    /// cap so very long shots do not produce absurd misses.  The cap is applied
    /// BEFORE <paramref name="accuracyMultiplier"/>, so penalty stacking can
    /// intentionally push the final radius above this value — that is the point
    /// of the multiplier (issues #64/#65).
    /// </param>
    /// <param name="accuracyMultiplier">
    /// Combined accuracy penalty multiplier (≥ 1).  1.0 = no penalty (default,
    /// preserves prior #62 behaviour exactly).  Values above 1 scale the capped
    /// base radius upward — a moving or closely-contested shot can therefore
    /// scatter beyond <paramref name="maxScatter"/>.  Callers compute this as
    /// the product of individual penalty factors (movement × contest); the helper
    /// itself is agnostic to their origin.
    /// </param>
    /// <returns>
    /// <paramref name="target"/> plus an XZ offset of magnitude
    /// <c>min(scatterPerMeter × distance, maxScatter) × accuracyMultiplier × sqrt(radius01)</c>
    /// at angle <c>2π × angle01</c>.  Y is identical to
    /// <paramref name="target"/>.Y.
    /// </returns>
    public static Vector3 Scatter(
        Vector3 target,
        float   distance,
        float   angle01,
        float   radius01,
        float   scatterPerMeter,
        float   maxScatter,
        float   accuracyMultiplier = 1.0f)
    {
        // Maximum radius for this shot distance, capped at maxScatter.
        // scatterPerMeter * distance is the "natural" radius for this distance;
        // capping ensures a half-court heave does not produce a miss several
        // metres off the rim, which would feel unfair and look absurd.
        float rMax = MathF.Min(scatterPerMeter * distance, maxScatter);

        // Accuracy penalty: multiply by the combined movement+contest factor.
        // This happens AFTER the cap so penalties can push the radius above
        // maxScatter — a moving/contested shot is genuinely harder (issues #64/#65).
        float rScaled = rMax * accuracyMultiplier;

        // Uniform-disc radius: sqrt maps the uniform radius01 sample so that
        // the resulting point is uniformly distributed over the disc area,
        // not clustered at the centre (see class doc).
        float r = rScaled * MathF.Sqrt(radius01);

        // Convert the angle sample to a full-circle radian angle.
        float theta = Tau * angle01;

        // XZ displacement; Y is left unchanged — the scatter is a horizontal
        // aim offset, not a height change (see class doc "Why XZ only").
        return new Vector3(
            target.X + r * MathF.Cos(theta),
            target.Y,
            target.Z + r * MathF.Sin(theta));
    }
}
