using System;
using Godot;

namespace Hooper.Player;

/// <summary>
/// Pure C# heading math for the player capsule — no Godot Node inheritance,
/// no engine singletons, no _PhysicsProcess, no RPCs.
///
/// Introduced for issue #80: turning around must not be instant. A 180°
/// pivot reversal is the clearest example of arcade decoupling (ADR-0003) —
/// it costs no physical commitment, so it can never be read or punished.
/// Bounding the turn rate gives the reverse-pivot a real dwell time that
/// BOTH players can see and react to, restoring the mind game.
///
/// The non-linear rate (slower near 180°, near-free for small corrections)
/// serves two purposes that point the same direction:
///   1. Micro-aim corrections (small diffs) stay near-instant, so the
///      movement analog stick does not feel "sticky" or input-laggy —
///      the continuous neutral game (ADR-0003) is still fluid.
///   2. Large reversals (near 180°) incur the most cost, making a full
///      back-turn the most readable and exploitable commitment.
///
/// This is SERVER-AUTHORITATIVE: Heading lives on PlayerController, is
/// updated inside Move() (the shared server+prediction+reconcile step),
/// and is broadcast through ReceiveState just like pos/vel. The client
/// replays it during reconciliation — exactly the same pattern MovementMath
/// uses for Velocity. Cosmetic yaw previously computed from Velocity via
/// FacingResolver is now replaced by this authoritative Heading value.
/// (ADR-0002, ADR-0004 — cosmetic-only facing was explicitly NOT networked
/// per the old approach; this ADR-0010 decision elevates facing to
/// server-authoritative state so it feeds issue #81 shot accuracy.)
/// </summary>
public static class HeadingMath
{
    // Heading is stored and compared in radians; angles smaller than this
    // are considered "already at target" and the current yaw is returned
    // unchanged. Float epsilon prevents NaN from Atan2(0,0) and removes
    // unnecessary turn steps when the player is already perfectly aligned.
    private const float AngleEpsilon = 1e-5f;

    // wishDir magnitudes below this are treated as "no directional input"
    // and the heading is held unchanged — mirrors FacingResolver.SpeedEpsilon's
    // role of preventing snap-to-zero when the stick is released.
    private const float WishDirEpsilon = 0.01f;

    /// <summary>
    /// Steps <paramref name="currentYaw"/> one tick toward the facing implied
    /// by <paramref name="wishDir"/>, at a bounded non-linear angular rate.
    ///
    /// This runs inside Move() so it is called identically on:
    ///   - the server's authoritative tick,
    ///   - the client's local prediction tick, and
    ///   - each step of the reconciliation replay.
    /// Any divergence here is a netcode bug, not just a feel issue — keep
    /// the function pure (no role checks, no network calls, no side effects).
    /// </summary>
    /// <param name="currentYaw">Current heading in radians (Y-rotation, Godot convention).</param>
    /// <param name="wishDir">
    /// 2D intent vector from the left stick / WASD. Zero or near-zero means
    /// "no directional input"; the heading is held unchanged to prevent snap.
    /// </param>
    /// <param name="delta">Physics step duration in seconds.</param>
    /// <param name="maxTurnRateDeg">
    /// Nominal maximum turn speed in degrees/second, applied at small
    /// angular differences. Scaled down by the non-linear factor for large
    /// differences. Default 400 °/s. A 180° back-turn at backTurnSlowFactor
    /// 0.35 takes ≈ 0.75 s — this is the integrated time of the non-linear
    /// schedule, not the constant-rate 180/(rate×f) figure (which overestimates
    /// because the rate accelerates as the diff closes).
    /// </param>
    /// <param name="backTurnSlowFactor">
    /// [0, 1] fraction of maxTurnRateDeg applied at exactly 180°. The
    /// effective rate is lerped between maxTurnRateDeg (at diff=0) and
    /// maxTurnRateDeg × backTurnSlowFactor (at diff=π), so the slowdown
    /// ramps continuously — no sudden gear-change. 0 = completely frozen at
    /// 180°; 1 = linear (no slowdown). Default 0.35.
    /// </param>
    /// <returns>
    /// The new heading in radians, in the range [-π, π].
    /// </returns>
    public static float RotateToward(
        float currentYaw,
        Vector2 wishDir,
        double delta,
        float maxTurnRateDeg,
        float backTurnSlowFactor)
    {
        // No directional input — hold current heading (prevents Atan2 snap
        // when the stick is released, same guard as FacingResolver).
        if (wishDir.Length() < WishDirEpsilon)
            return currentYaw;

        // Desired yaw derived from the 2D intent vector.
        // Convention: Atan2(x, y) on the XZ ground plane — matches
        // FacingResolver.ResolveYaw(velocity, …) exactly (Atan2(vel.X, vel.Z)),
        // where wishDir.X→world X and wishDir.Y→world Z. A Y-rotation of 0
        // points the mesh toward +Z; this formula rotates it to face wishDir.
        float desiredYaw = MathF.Atan2(wishDir.X, wishDir.Y);

        // Shortest angular path in [-π, π].
        // Wrapping is important: without it a turn from +π to −π would travel
        // a full 2π arc the wrong way instead of a zero-cost crossing.
        float diff = AngleDiff(currentYaw, desiredYaw);

        // Already within epsilon — no movement needed.
        if (MathF.Abs(diff) < AngleEpsilon)
            return desiredYaw;

        // Non-linear scaling: at diff ≈ 0 the full rate applies; at diff ≈ π
        // only backTurnSlowFactor × maxTurnRateDeg applies. The lerp is driven
        // by |diff|/π, so it ramps continuously — a 90° turn is at half-cost,
        // a 180° turn is at minimum cost. This is the design intent (ADR-0003):
        // micro-corrections feel near-instant while a full reverse-pivot is
        // visibly slow and reads as a genuine commitment.
        float t           = MathF.Abs(diff) / MathF.PI;           // 0 at small diff, 1 at 180°
        float scaledRate  = Lerp(maxTurnRateDeg, maxTurnRateDeg * backTurnSlowFactor, t);
        float maxStep     = scaledRate * (float)delta * (MathF.PI / 180f); // convert deg/s → rad

        // Move currentYaw by at most maxStep radians, in the direction of diff.
        float step        = MathF.Min(MathF.Abs(diff), maxStep) * MathF.Sign(diff);
        float newYaw      = currentYaw + step;

        // Normalise to [-π, π] so the stored heading never accumulates drift
        // and callers can compare it against Atan2 outputs directly.
        return NormalizeAngle(newYaw);
    }

    /// <summary>
    /// The unit world-space forward direction (XZ plane) for a given heading —
    /// the inverse of the Atan2(x, z) convention <see cref="RotateToward"/> uses
    /// to derive heading from intent. Heading h ⇒ forward (sin h, cos h), so
    /// MathF.Atan2(Forward(h).X, Forward(h).Y) == h. Yaw 0 faces +Z, matching
    /// PlayerController.ApplyCosmetics (_mesh.Rotation.Y = Heading): anything
    /// positioned along this vector sits in front of the player's authoritative —
    /// and rendered — facing, not their (possibly zero) velocity direction.
    /// </summary>
    /// <param name="heading">Heading in radians, Y-rotation, Godot convention.</param>
    /// <returns>Unit (worldX, worldZ) forward direction.</returns>
    public static Vector2 Forward(float heading) =>
        new(MathF.Sin(heading), MathF.Cos(heading));

    // ── Internal helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Signed angular difference from <paramref name="from"/> to
    /// <paramref name="to"/>, in radians, taking the shortest path across
    /// the ±π boundary. Result is in (-π, π].
    /// </summary>
    private static float AngleDiff(float from, float to)
    {
        float d = to - from;
        // Wrap into (-π, π] so we always travel the short way.
        while (d >  MathF.PI) d -= 2f * MathF.PI;
        while (d < -MathF.PI) d += 2f * MathF.PI;
        return d;
    }

    /// <summary>
    /// Wraps <paramref name="angle"/> into the range [-π, π].
    /// </summary>
    private static float NormalizeAngle(float angle)
    {
        while (angle >  MathF.PI) angle -= 2f * MathF.PI;
        while (angle < -MathF.PI) angle += 2f * MathF.PI;
        return angle;
    }

    /// <summary>
    /// Linear interpolation between <paramref name="a"/> and
    /// <paramref name="b"/> by unclamped factor <paramref name="t"/>.
    /// Using our own to avoid pulling in Godot.Mathf and keep this
    /// self-contained for headless unit tests.
    /// </summary>
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
