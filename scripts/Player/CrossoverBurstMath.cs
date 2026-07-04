using Godot;

namespace Hooper.Player;

/// <summary>
/// Pure C# burst-vector composition for the moving crossover (issue #198) —
/// no Godot Node inheritance, no engine singletons, no RPCs.
///
/// Extracted so PlayerController.TickCommittedMoveBehavior's Active-entry
/// branch can call ONE headless-testable function instead of hand-rolling the
/// momentum/exit math inline. One parameterized composition, not a move zoo
/// (docs/handoffs/M9-move-taxonomy.md §2, grilled 2026-07-04) — every row of
/// the emergent-move table below falls out of the SAME formula:
///
///   Incoming            | Left stick at Active-entry | Emergent move
///   ---------------------|-----------------------------|---------------------------------
///   Stationary            | Push forward                | Cross → explode forward
///   Driving forward        | Push diagonal                | Change-of-direction cross
///   Driving forward        | Neutral / straight           | Push-cross (~no separation)
///   Stationary            | Push lateral                 | Classic side-to-side shuffle
///
/// ── Momentum model (hybrid gather, "model C") ───────────────────────────────
/// This function does NOT bleed momentum itself — that happens continuously
/// during Startup (PlayerController decelerates Velocity toward zero via
/// GatherDecel every Startup tick, never an instant zero). By the time this
/// function runs (JustEnteredActive), <paramref name="survivingVelocity"/> is
/// whatever momentum the plant did not manage to bleed. Active's job is only
/// to REDIRECT that survivor and add the burst impulse — never to re-zero it,
/// which is the bounded-retention amendment recorded in the ADR-0003 amendment
/// this issue's PR carries.
///
/// ── Exit vector drives the burst; the flick only picks the hand-swap side ──
/// The right-stick flick (<paramref name="flickSign"/>) still tells
/// HandStateResolver which hand becomes empty (the ball-swap), but it is NOT
/// the primary source of the burst's WORLD direction anymore — the analog
/// left stick, snapshotted at Active-entry (model A: parameterizes the move,
/// never cancels it — ADR-0003-safe), is. <paramref name="exitVector"/> is
/// already WORLD-SPACE (the same (X,Z) convention PlayerController.Move's
/// wishDir uses — ADR-0002, camera-independent), decomposed here into the
/// player's heading-relative forward/right axes so the SAME left-stick push
/// reads consistently as "forward" or "lateral" regardless of which way the
/// player is currently facing.
///
/// A backward-pointing exit vector contributes no forward burst (real-ball
/// rationale: a crossover explodes forward or lateral, never backward — you
/// don't cross yourself into retreat). When the exit vector is neutral
/// (magnitude at or below <paramref name="exitDeadzone"/>) AND there is no
/// surviving momentum either, the function falls back to the pre-#198 pure
/// flick-driven lateral burst (HandStateResolver.BurstWorldDir) rather than
/// silently reducing a bare stationary crossover flick to a no-op — no table
/// row covers "no steering input at all," so this preserves the legacy feel
/// for that untested edge case.
/// </summary>
public static class CrossoverBurstMath
{
    /// <summary>
    /// Composes the Active-phase velocity for a crossover.
    /// </summary>
    /// <param name="survivingVelocity">
    /// The XZ velocity carried INTO Active — whatever Startup's gather-bleed
    /// left behind (see class doc). Y is ignored/passed through as 0; this
    /// controller applies no gravity.
    /// </param>
    /// <param name="heading">Player's authoritative heading in radians (ADR-0010), yaw 0 faces +Z.</param>
    /// <param name="flickSign">
    /// Body-relative right-stick flick sign: +1 = player's right, -1 =
    /// player's left. Used ONLY as the stationary+neutral-exit fallback
    /// direction (see class doc) — not the primary burst direction.
    /// </param>
    /// <param name="exitVector">
    /// World-space left-stick reading (PlayerController.Move's wishDir
    /// convention) snapshotted at Active-entry. Typical analog magnitude is
    /// 0..1; direction, not magnitude scale, is what matters here — it is
    /// re-normalized before use.
    /// </param>
    /// <param name="burstSpeed">Lateral burst impulse magnitude (m/s).</param>
    /// <param name="forwardBurstScale">Forward burst impulse magnitude (m/s) for the exit vector's forward-aligned component.</param>
    /// <param name="exitDeadzone">Exit-vector magnitude at/below which the stick counts as neutral (no steering input).</param>
    /// <returns>The Active-phase velocity (Y always 0).</returns>
    public static Vector3 ComposeActiveVelocity(
        Vector3 survivingVelocity,
        float heading,
        int flickSign,
        Vector2 exitVector,
        float burstSpeed,
        float forwardBurstScale,
        float exitDeadzone)
    {
        Vector2 forwardAxis = HeadingMath.Forward(heading);
        // Reuse HandStateResolver's own +1-flick convention for "right" so
        // the two files can never silently disagree on which way is which.
        Vector2 rightAxis = HandStateResolver.BurstWorldDir(heading, +1);

        Vector2 survivingXZ = new(survivingVelocity.X, survivingVelocity.Z);
        Vector2 impulse;

        if (exitVector.LengthSquared() > exitDeadzone * exitDeadzone)
        {
            Vector2 exitDir = exitVector.Normalized();
            float forwardAmount = exitDir.Dot(forwardAxis);
            float lateralAmount = exitDir.Dot(rightAxis);

            // Clamp negative (backward-pointing) forward contribution to
            // zero — see class doc's "never backward" rationale. Lateral is
            // NOT clamped: a crossover can push either body-relative side.
            float forwardContribution = Mathf.Max(forwardAmount, 0f);

            impulse = rightAxis * (lateralAmount * burstSpeed)
                    + forwardAxis * (forwardContribution * forwardBurstScale);
        }
        else if (survivingXZ.LengthSquared() < 0.0001f)
        {
            // Stationary + no steering input at all: fall back to the
            // pre-#198 pure flick-driven lateral burst (see class doc).
            impulse = rightAxis * (flickSign * burstSpeed);
        }
        else
        {
            // Driving + neutral exit: "push-cross" — no impulse added, the
            // player simply continues on whatever momentum survived Startup.
            impulse = Vector2.Zero;
        }

        Vector2 resultXZ = survivingXZ + impulse;
        return new Vector3(resultXZ.X, 0f, resultXZ.Y);
    }
}
