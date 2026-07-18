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
/// <summary>
/// The in-hand ball transit path a hand-swap sweep is playing (#195's
/// in-front baseline, #194's behind-body pull-back, and #199's
/// through-the-legs bounce). Replaced a bare bool (isBehindBody) when #199
/// added a third path — a second bool would have needed an "impossible
/// combination" comment; an enum makes the three paths mutually exclusive by
/// construction.
/// </summary>
public enum BallSweepPath
{
    /// <summary>Crossover's sweep: stays in front of the holder's centerline.</summary>
    InFront,

    /// <summary>BehindTheBack's sweep: pulls behind the holder's centerline.</summary>
    BehindBody,

    /// <summary>BetweenTheLegs's sweep: stays in front (like InFront) but dips deeper toward the floor.</summary>
    ThroughLegs,

    /// <summary>
    /// Spin's sweep (#201): pulled in tight against the rotating body's
    /// centerline — neither InFront's full forward extension nor BehindBody's
    /// negative (fully-behind) pull. ADR-0014 tier-1 (real half-court ball):
    /// during a real spin the ball handler cradles the ball hard against the
    /// hip/torso as the body rotates between it and the defender — it is
    /// tucked CLOSE, not extended out front (that would defeat the shield)
    /// and not swung fully behind the back either (a spin's body itself is
    /// the shield; there is no separate behind-the-back pull to also play).
    /// See CrossoverBallSweep.ForwardOffset's own doc for the exact formula
    /// and why it stays non-negative, unlike BehindBody's.
    /// </summary>
    BodyShield,
}

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

    /// <summary>
    /// The in-hand forward offset (metres) for this tick, given the current
    /// sweep's verticalDip curve value and which <see cref="BallSweepPath"/>
    /// is active. Extracted to a pure static (#211 code-review fix) so
    /// mutation coverage can pin this formula directly — it previously lived
    /// as a private BallController method with zero xUnit coverage and
    /// survived a sign-flip mutation.
    ///
    /// Crossover's in-front sweep never touches the forward axis — only the
    /// existing lateral/dip terms — so InFront returns the plain baseline
    /// (DribbleForwardOffset), bit-identical to pre-#194 behaviour.
    /// A BehindTheBack sweep instead pulls the ball BACK along that same
    /// single-arch curve (0 at both ends, peak at t=0.5 — reusing
    /// verticalDip's shape rather than a second pure curve, since it is
    /// exactly the "how far through the transit" progress both the dip and
    /// this pull-back need): at the peak, behindDepth (BehindTheBackSweepDepth,
    /// 0.7 by default) exceeds baseline (DribbleForwardOffset, 0.5 by
    /// default), so the ball's forward offset goes NEGATIVE — genuinely
    /// behind the holder's centerline, the "shielded, away from the
    /// defender" transit the issue calls for.
    /// A BetweenTheLegs sweep (#199) travels THROUGH the legs, not around
    /// the body — it stays in front like InFront (same baseline, forward
    /// axis untouched); its distinguishing depth is a deeper VERTICAL dip
    /// (BallController.BetweenTheLegsDipDepth, applied by the caller
    /// alongside this forward offset), not a forward pull-back.
    /// A Spin sweep (#201) pulls the ball toward the body's centerline along
    /// the SAME single-arch curve, using its own (smaller) shieldDepth —
    /// smaller than baseline BY DESIGN, so the result stays POSITIVE (the
    /// ball tucked in tight against the hip, still marginally in front) and
    /// never crosses to negative the way BehindBody's larger behindDepth
    /// deliberately does. Reusing verticalDip's shared "how far through the
    /// transit" progress keeps this a one-curve, many-consumers composition
    /// (#194's precedent) rather than a fourth bespoke curve.
    /// </summary>
    /// <param name="baseline">The holder's steady-state forward offset (BallController.DribbleForwardOffset).</param>
    /// <param name="verticalDip">This tick's sweep dip curve value, from <see cref="Offset"/>.</param>
    /// <param name="behindDepth">How far behind baseline the peak of a behind-body sweep pulls (BallController.BehindTheBackSweepDepth).</param>
    /// <param name="shieldDepth">How far toward the centerline the peak of a body-shield sweep pulls (BallController.SpinBodyShieldDepth) — must stay less than baseline so the result never goes negative.</param>
    /// <param name="path">Which transit path this sweep is playing.</param>
    public static float ForwardOffset(float baseline, float verticalDip, float behindDepth, float shieldDepth, BallSweepPath path) =>
        path switch
        {
            BallSweepPath.BehindBody => baseline - verticalDip * behindDepth,
            BallSweepPath.BodyShield => baseline - verticalDip * shieldDepth,
            _                        => baseline,
        };
}
