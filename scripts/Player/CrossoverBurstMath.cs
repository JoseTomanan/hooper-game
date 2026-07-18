using System;
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
/// don't cross yourself into retreat). There are two DISTINCT fallbacks to
/// the pre-#198 pure flick-driven lateral burst (HandStateResolver.BurstWorldDir),
/// both existing so a committed crossover never silently no-ops. Issue #209
/// turned each from a HARD branch into a continuous BLEND (a bit-exact branch
/// on a physical quantity is a feel cliff — see ADR-0003), but the fallback's
/// endpoint behaviour is unchanged:
///   1. Exit vector neutral (magnitude at or below <paramref name="exitDeadzone"/>):
///      lerp from the full flick burst at zero surviving momentum toward pure
///      push-cross (momentum only) as surviving speed rises through
///      <see cref="PushCrossBlendSpeed"/>. The old code hard-switched on
///      surviving == 0 (no table row covers "no steering input at all"); the
///      blend keeps the surviving-0 endpoint identical and removes the cliff.
///   2. Exit vector ACTIVE but held anti-parallel to forwardAxis (a straight-back
///      pull) — the "never backward" clamp above composes a near-zero impulse on
///      its own (see DeadImpulseFloor's doc), which would otherwise dead-stop a
///      fully committed move the player just paid startup frames for. As its
///      magnitude falls through <see cref="DeadImpulseFloor"/> the composition
///      lerps toward the flick burst, so exact dead-back still gets the full
///      flick while just-off-back is continuous with its neighbourhood.
/// </summary>
public static class CrossoverBurstMath
{
    /// <summary>
    /// Floor on the exit-vector-driven impulse's magnitude (m/s) below which
    /// the composition BLENDS toward the legacy flick-driven lateral burst
    /// (code review, #198 fix round; blended rather than hard-snapped in #209).
    /// An exit vector held anti-parallel to
    /// forwardAxis — the player pulls the stick straight back during the
    /// crossover — clamps forwardContribution to 0 (see the "never backward"
    /// rationale below) AND has a near-zero lateral dot product (it is
    /// nearly co-linear with forwardAxis, just negative), so the composed
    /// impulse is itself near Vector2.Zero: a fully committed crossover that
    /// produces a dead stop with no burst at all. That is exactly the
    /// silent no-op ADR-0003 rules out for a committed move — the player
    /// paid the startup frames and got nothing back. Well below the tuned
    /// default burst speeds (9 m/s) so it never fires for a genuine
    /// diagonal/lateral/forward exit, only the degenerate backward cone.
    /// </summary>
    private const float DeadImpulseFloor = 0.5f;

    /// <summary>
    /// Surviving-momentum speed (m/s) at which the neutral-exit fallback has
    /// fully handed off from the flick-driven lateral shuffle to a pure
    /// push-cross (issue #209). The composition lerps the flick burst by
    /// <c>1 - clamp(survivingSpeed / PushCrossBlendSpeed, 0, 1)</c>, so a player
    /// with no retained momentum gets the full shuffle and one at/above this
    /// speed continues on momentum alone — continuous in between, replacing the
    /// old bit-exact zero test that made 4.0 m/s a feel cliff.
    ///
    /// 2.0 m/s is not arbitrary: it is <c>MoveSpeed (6) − gather-bleed
    /// (GatherDecel 40 × Crossover Startup 6 ticks / 60 = 4)</c>, i.e. the
    /// MAXIMUM momentum a crossover can carry into Active. So the whole
    /// realistic surviving range [0, 2] maps across the full blend. The three
    /// moves sharing this function bleed at different rates (BehindTheBack /
    /// BetweenTheLegs bleed harder, retaining less), so they reach a smaller
    /// fraction of the way to push-cross under this single momentum-scale
    /// threshold — an accepted consequence; the exact value is a feel default
    /// deferred to the consolidated tuning pass (#238) / feel pass (#173,
    /// ADR-0021). Continuity — not this magnitude — is what #209 pins.
    /// </summary>
    private const float PushCrossBlendSpeed = 2.0f;

    /// <summary>
    /// "No clamp" sentinel for <c>maxExitAngleRadians</c> — 180 degrees, i.e.
    /// every direction the "never backward" forward-contribution clamp
    /// already allows through. A caller that omits the parameter (every
    /// pre-#194 Crossover call site) is bit-identical to the original
    /// unclamped composition.
    /// </summary>
    private const float FullExitCone = MathF.PI;

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
    /// <param name="maxExitAngleRadians">
    /// The "exit cone" (issue #194): the exit vector's angle is clamped to
    /// within this many radians of forwardAxis BEFORE decomposing into
    /// forward/lateral contributions. Defaults to an unclamped 180 degrees
    /// (<see cref="FullExitCone"/>) — every pre-#194 caller (plain
    /// Crossover) gets exactly the old un-narrowed behaviour, since the
    /// "never backward" clamp below already bounds the effective cone to a
    /// forward hemisphere on its own. BehindTheBack (#194) passes a
    /// genuinely narrower angle here: "fewer follow-ups" is modelled ONLY as
    /// this narrower cone (docs/handoffs/M9-move-taxonomy.md), never as a
    /// recovery-frame or cooldown penalty.
    /// </param>
    /// <returns>The Active-phase velocity (Y always 0).</returns>
    public static Vector3 ComposeActiveVelocity(
        Vector3 survivingVelocity,
        float heading,
        int flickSign,
        Vector2 exitVector,
        float burstSpeed,
        float forwardBurstScale,
        float exitDeadzone,
        float maxExitAngleRadians = FullExitCone)
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

            // Exit-cone clamp (#194): only touches the pair when the cone is
            // genuinely narrower than FullExitCone AND the exit direction
            // actually exceeds it — so a full-cone caller (plain Crossover)
            // never pays the atan2/cos/sin round-trip and stays bit-
            // identical to the pre-#194 direct-dot-product computation.
            // {forwardAxis, rightAxis} is an orthonormal basis and exitDir is
            // unit length, so forwardAmount/lateralAmount ARE exactly
            // cos/sin of the signed angle between exitDir and forwardAxis —
            // recomputing them from a clamped angle is a like-for-like swap,
            // not an approximation.
            if (maxExitAngleRadians < FullExitCone)
            {
                if (lateralAmount == 0f && forwardAmount < 0f)
                {
                    // Exact backward pole (#211 code-review fix). exitDir sits
                    // dead-on -forwardAxis, so lateralAmount is a sum of
                    // products that are each individually a signed zero —
                    // MathF.Atan2(±0.0, negative) returns ±π depending on
                    // THAT sign, which is an IEEE-754 implementation detail,
                    // not gameplay. ADR-0004 requires server and client to
                    // compute identically on every build, so the bend
                    // direction can never ride Atan2's sign-of-zero — it must
                    // be picked from an explicit signal. Bend to the side the
                    // flick already committed to (flickSign), matching the
                    // DeadImpulseFloor fallback below (`rightAxis * flickSign
                    // * burstSpeed`) so the pole case is continuous with its
                    // neighbourhood instead of an arbitrary left/right pick.
                    float clampedAngle = flickSign >= 0 ? maxExitAngleRadians : -maxExitAngleRadians;
                    forwardAmount = MathF.Cos(clampedAngle);
                    lateralAmount = MathF.Sin(clampedAngle);
                }
                else
                {
                    float angle = MathF.Atan2(lateralAmount, forwardAmount);
                    float clampedAngle = Mathf.Clamp(angle, -maxExitAngleRadians, maxExitAngleRadians);
                    if (clampedAngle != angle)
                    {
                        forwardAmount = MathF.Cos(clampedAngle);
                        lateralAmount = MathF.Sin(clampedAngle);
                    }
                }
            }

            // Clamp negative (backward-pointing) forward contribution to
            // zero — see class doc's "never backward" rationale. Lateral is
            // NOT clamped: a crossover can push either body-relative side.
            float forwardContribution = Mathf.Max(forwardAmount, 0f);

            impulse = rightAxis * (lateralAmount * burstSpeed)
                    + forwardAxis * (forwardContribution * forwardBurstScale);

            // Never let a committed crossover silently no-op (see
            // DeadImpulseFloor's doc) — a backward-held exit vector composes a
            // near-zero impulse, so we blend it toward the pre-#198 flick
            // burst as its magnitude falls through the floor. Issue #209 (the
            // owner's folded-in comment): the old HARD snap (magnitude < floor
            // → full ±burstSpeed flick, else the composed impulse verbatim)
            // was a second cliff of the same class — an exit ~3° off dead-back
            // composed ~0.5 m/s while one just inside the cone got the full 9.
            // The lerp is continuous at BOTH ends: at the floor (t→1) it meets
            // the composed impulse exactly, and at zero composed magnitude
            // (t→0, exact dead-back) it meets the full flick. The endpoints are
            // identical to the old snap; only the transition is now smooth.
            //
            // ACCEPTED TRADE (do not "fix" without reading this): on the side
            // of dead-back where the composed impulse points OPPOSITE the flick
            // (anti-flick side), the segment from flick to composed passes
            // through ~0 magnitude, so a STANDING (surviving≈0) plain Crossover
            // held ~3° off straight-back there gets a near-dead-stop — the very
            // no-op this fallback guards against. It is mathematically forced:
            // a continuous path from a small +X composed vector to a large −X
            // flick MUST cross zero; the only escape (slerp AROUND through ±Y)
            // picks its rotation side from the sign-of-zero of two near-
            // antiparallel vectors, the exact ADR-0004 hazard the Atan2-pole
            // code above already fights. Continuity (this issue's acceptance
            // bar) wins over the no-op guarantee in this degenerate backward
            // cone; the band is narrow, only plain Crossover reaches it (the
            // narrower BehindTheBack/BetweenTheLegs cones clamp away), and the
            // feel there is deferred to the tuning pass (#238).
            //
            // Magnitude bound preserved: lerp(a, b) lies on the segment between
            // a and b, so |result| ≤ max(|flick|, |composed|) = burstSpeed ≤
            // the orthonormal-basis bound — the composed impulse can never
            // stack ON TOP of the fallback (ComposedImpulse_NeverExceeds… stays
            // green).
            float impulseMagSq = impulse.LengthSquared();
            if (impulseMagSq < DeadImpulseFloor * DeadImpulseFloor)
            {
                Vector2 flickBurst = rightAxis * (flickSign * burstSpeed);
                float floorT = MathF.Sqrt(impulseMagSq) / DeadImpulseFloor;
                impulse = flickBurst.Lerp(impulse, floorT);
            }
        }
        else
        {
            // Neutral exit (no steering input): BLEND continuously between the
            // two table rows this branch serves, instead of branching on a
            // bit-exact `survivingXZ.LengthSquared() < 0.0001f` test (issue
            // #209 — that test made a player surviving exactly 0 explode with
            // the full ±burstSpeed flick while one surviving 0.1 m/s got
            // nothing, a ~9 m/s feel cliff antithetical to ADR-0003's legible
            // commitment). The endpoints are unchanged:
            //   surviving 0            → full flick-driven lateral burst
            //                            (the "stationary, no steering" row)
            //   surviving ≥ blend speed → zero impulse, pure push-cross
            //                            (the "driving, neutral exit" row)
            // and everything between is a linear ramp, so exit velocity is now
            // continuous across the former boundary.
            float survivingSpeed = survivingXZ.Length();
            float pushCrossT = Mathf.Clamp(survivingSpeed / PushCrossBlendSpeed, 0f, 1f);
            impulse = rightAxis * (flickSign * burstSpeed) * (1f - pushCrossT);
        }

        Vector2 resultXZ = survivingXZ + impulse;
        return new Vector3(resultXZ.X, 0f, resultXZ.Y);
    }
}
