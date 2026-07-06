using System;

namespace Hooper.Ball;

/// <summary>
/// Pure curve for the crossover's authoritative cross-body ball transit
/// (#195). On a same-holder crossover, PlayerController.HandSide flips in a
/// single tick; without this curve BallController.TickHeld/TickDribbling
/// would read the new HandSign() immediately and the ball's in-hand XZ would
/// jump straight across the body — a teleport, not a transit.
///
/// ── Why a pure static class (ADR-0004 / ADR-0016) ─────────────────────────
/// Same rationale as DeadDribbleRule/OobResolution: the curve itself is the
/// entire risk surface here (get the easing and the dip shape right), so it
/// is extracted behind a Node-free seam that unit tests can pin directly,
/// independent of BallController's holder-lookup glue.
///
/// ── Inputs ─────────────────────────────────────────────────────────────
/// t is normalised progress through the sweep, in [0, 1] — BallController
/// derives it from a tick counter since the swap divided by a duration in
/// ticks (both deterministic per ADR-0004: the same fixed-tick math runs
/// identically on the server, the predicting holder-client, and the
/// remote client, with no new netcode).
/// fromSign/toSign are the lateral hand-offset sign the ball is leaving and
/// arriving at — ordinarily ±1 (HandSign's old/new values), but the caller
/// may pass an already-interpolated lateral factor as fromSign when a
/// re-cross interrupts a sweep in progress (issue #195's rule 3: restart
/// from the ball's CURRENT interpolated position, never jump back).
/// </summary>
public static class CrossoverBallSweep
{
    /// <summary>
    /// Returns (lateralFactor, verticalDip) for normalised progress t.
    ///
    /// lateralFactor: smoothstep-eased interpolation from fromSign to toSign.
    /// Smoothstep (not linear) gives the transit a soft ease-in/ease-out
    /// instead of a constant-velocity slide, matching a real cross-body
    /// dribble's push-off and catch.
    ///
    /// verticalDip: a single-arch curve — 0 at t=0 and t=1, maximum (1) at
    /// t=0.5 — representing the low, protective dip a real crossover takes
    /// through the middle of the transit. The caller multiplies this by a
    /// tunable depth (BallController.CrossoverSweepDipDepth) and subtracts
    /// it from the ball's height.
    /// </summary>
    /// <param name="t">Normalised sweep progress, clamped to [0, 1].</param>
    /// <param name="fromSign">Lateral factor at t=0 (old side, or the sweep's current position on a re-cross).</param>
    /// <param name="toSign">Lateral factor at t=1 (new side).</param>
    public static (float LateralFactor, float VerticalDip) Offset(float t, float fromSign, float toSign)
    {
        float clamped = t < 0f ? 0f : (t > 1f ? 1f : t);

        // Smoothstep: 3t^2 - 2t^3. Monotonic on [0,1] with zero slope at both
        // endpoints, so lateralFactor moves smoothly from fromSign to toSign
        // without overshoot and without a hard velocity change at the seams.
        float eased = clamped * clamped * (3f - 2f * clamped);
        float lateralFactor = fromSign + (toSign - fromSign) * eased;

        // Single-arch dip: sin(pi * t) is 0 at t=0 and t=1, and 1 at t=0.5 —
        // exactly the "0 at both ends, max at t=0.5" shape the issue calls
        // for, using the same "hand-authored curve over a physics solver"
        // determinism rationale as DribbleCycle's cosine height curve.
        float verticalDip = MathF.Sin(MathF.PI * clamped);

        return (lateralFactor, verticalDip);
    }
}
