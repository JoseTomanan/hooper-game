using Godot;

namespace Hooper.Player;

/// <summary>
/// Pure C# math for the euro-step (issue #231, ADR-0022) — no Godot Node
/// inheritance, no engine singletons, no RPCs. The lateral-evasive sibling of
/// <see cref="DriveGatherMath"/>: where the drive-gather's Active window is a
/// straight-line attack toward the rim (its own doc says "that lateral shaping
/// is the euro-step's (#231) job, explicitly out of this move's scope"), THIS
/// is the function that doc comment promised.
///
/// ── The "two-beat" made concrete (issue #231, ADR-0014 tier 2) ───────────────
/// Beat 1 (the gather) happens during Startup, reusing DriveGather's exact
/// Startup path: Heading bends toward the rim via HeadingMath.RotateToward
/// (bounded turn rate, ADR-0010 — NOT an instant snap, which would be the
/// arcade decoupling ADR-0003 forbids) and the dribble is cradled. Beat 2 (the
/// plant-and-finish-the-other-way) is THIS composition, SET once on the move's
/// JustEnteredActive tick: whatever forward momentum survived the gather bleed,
/// carried onward toward the rim, PLUS a decisive lateral displacement in the
/// body-relative READ direction (which way the defender is NOT). The lateral
/// shift is what repositions the launch point so a subsequent Layup (the chain,
/// per the proven gather-to-layup-chain precedent) finishes from an angle a
/// defender committed to the original drive line can no longer contest.
///
/// ── Why forward progress is RETAINED, not replaced (design, ADR-0003) ────────
/// A euro-step that only stepped sideways would be a lateral shuffle, not a rim
/// attack — the finish must stay reachable. So the composition keeps a forward
/// drive term toward the (already rim-bent) Heading and ADDS the lateral kick,
/// rather than swapping one for the other.
///
/// ── Why a FIXED lateral magnitude, not a stick-scaled cone (legibility) ──────
/// The left stick chooses the step's DIRECTION (the read), but its magnitude is
/// a fixed hop (lateralHopSpeed), mirroring RetreatDribble's fixed backward hop
/// — NOT a continuously stick-scaled exit cone like the crossover family. The
/// issue frames the euro-step as "a read, not a free dodge": a fixed, known
/// displacement is one the defender can anticipate and punish; a stick-scaled
/// magnitude would hand the attacker an unreadable analog dodge, exactly the
/// arcade decoupling ADR-0003 rules out. (A stick-shaped cone remains a future
/// #238 tuning option, not this tracer bullet's shape.)
///
/// ── "SET once on JustEnteredActive, never += " (netcode, ADR-0002/0004) ──────
/// Like every burst-family move (see CrossoverBurstMath's class doc), this
/// composes an ABSOLUTE Active-entry velocity from the surviving momentum, never
/// an additive increment — additive velocity overshoots when a reconciliation
/// replay re-runs the Active-entry tick.
/// </summary>
public static class EuroStepMath
{
    /// <summary>
    /// Composes the euro-step's Active-phase velocity: the surviving forward
    /// momentum carried toward the (rim-bent) <paramref name="heading"/>, plus a
    /// forward drive term and a fixed lateral hop in the body-relative read
    /// direction. SET once on JustEnteredActive (see class doc), Y always 0.
    /// </summary>
    /// <param name="survivingVelocity">
    /// The XZ velocity carried INTO Active — whatever Startup's gather bleed left
    /// behind. Y is ignored/passed through as 0; this controller applies no
    /// gravity.
    /// </param>
    /// <param name="heading">
    /// Player's authoritative heading in radians (ADR-0010), already bent toward
    /// the rim during Startup. Yaw 0 faces +Z, so HeadingMath.Forward(0) == (0,1)
    /// and HandStateResolver.BurstWorldDir(0, +1) == (-1,0) (player's right).
    /// </param>
    /// <param name="lateralSign">
    /// Body-relative step direction: +1 = step to the player's right, -1 = to the
    /// player's left. The "read" — chosen client-side from the left stick's
    /// lateral tilt at press and carried on the RequestBeginMove RPC param, the
    /// same reconstruction payload convention Crossover.BurstDirection uses.
    /// </param>
    /// <param name="forwardDriveSpeed">
    /// Forward-toward-rim burst magnitude (m/s) — keeps the finish reachable
    /// (reuses the drive-gather's forward role; a euro-step is a drive variant).
    /// </param>
    /// <param name="lateralHopSpeed">Fixed lateral displacement magnitude (m/s) — the evade.</param>
    /// <returns>The Active-phase velocity (Y always 0).</returns>
    public static Vector3 ComposeActiveVelocity(
        Vector3 survivingVelocity,
        float heading,
        int lateralSign,
        float forwardDriveSpeed,
        float lateralHopSpeed)
    {
        // The orthonormal body basis every burst-family move shares. `forward`
        // points along the rim-bent heading; `right` is the +1 body-right axis
        // (so `right * lateralSign` is the read direction). Reusing
        // HandStateResolver.BurstWorldDir keeps this file and the hand-state
        // logic from ever silently disagreeing on which way is "right".
        Vector2 forward = HeadingMath.Forward(heading);
        Vector2 right = HandStateResolver.BurstWorldDir(heading, +1);
        Vector2 survivingXZ = new(survivingVelocity.X, survivingVelocity.Z);

        // All three terms ADD — the surviving momentum is carried (never
        // re-zeroed), a forward drive term keeps the rim attack alive so the
        // chained finish stays reachable, and a fixed lateral hop in the read
        // direction is the evade. Keeping the hop additive-to (not a
        // replacement-of) the forward drive is what makes the euro-step a
        // contestable rim attack rather than a sideways escape hatch — the
        // ADR-0003 legibility guarantee, in one expression.
        Vector2 resultXZ = survivingXZ
            + forward * forwardDriveSpeed
            + right * (lateralSign * lateralHopSpeed);
        return new Vector3(resultXZ.X, 0f, resultXZ.Y);
    }
}
