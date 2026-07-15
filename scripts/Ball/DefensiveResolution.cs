using System;
using Godot;
using Hooper.Player;

namespace Hooper.Ball;

/// <summary>
/// Pure decision helper for the M10 defensive committed-move success rule
/// (issue #95 foundation, implemented in #96 steal / #98 block / #99 contest).
///
/// ── Why pure / why here ───────────────────────────────────────────────────
/// Mirrors the headless-seam discipline of OobResolution, ClearLine, and
/// FlightTermination (ADR-0004): no Godot Node, no engine singletons.
/// BallController.ResolveStealAttempts already owns all engine lookups
/// (IsServer, player-node iteration, DribbleCycle.Phase, HandSide) and passes
/// their RESULTS in; this class encodes only the pure success predicates so
/// they are unit-testable without a Godot runtime.
///
/// ── The shared overlap predicate (ADR-0018 §1) ───────────────────────────
/// A defensive committed move SUCCEEDS iff its Active window overlaps the
/// target's currently-open vulnerable window, expressed as half-open integer-
/// tick intervals [start, end) on the deterministic physics-tick clock:
///
///   Succeeds(as, ae, vs, ve) ⟺ as &lt; ve ∧ vs &lt; ae
///
/// This is the ONE predicate all three M10 moves call. The defender's Active
/// interval comes from MoveFrameData (StartupFrames → ActiveFrames); the
/// vulnerable interval is defined per move in ADR-0018 §2.
///
/// ── Steal-specific check (ADR-0018 §2) ───────────────────────────────────
/// The steal is a TWO-AXIS read: (1) the dribble phase must be inside an
/// exposed band [loExposed, hiExposed] straddling 0.5 (ball near the floor),
/// AND (2) the defender must target the authoritative ball-hand (ADR-0012).
/// Failing either axis is a miss; the defender still pays Recovery (§3).
///
/// StealSucceeds is a point-in-band test (a single tick is inside the band iff
/// the phase is currently in [lo, hi]).  The interval overlap ADR-0018 requires
/// is produced by the CALLER re-checking it on every Active tick against the
/// live dribble phase (BallController.ResolveStealAttempts, issue #96) — the
/// union of those in-band point tests over the Active window IS the overlap.
/// BlockSucceeds (#98) will instead call the full interval Succeeds form
/// because the shot's release window has a concrete start tick in InFlight.
/// </summary>
public static class DefensiveResolution
{
    /// <summary>
    /// The shared defensive success predicate (ADR-0018 §1).
    ///
    /// Returns true iff the half-open Active interval [activeStart, activeEnd)
    /// overlaps the half-open vulnerable interval [vulnStart, vulnEnd) on the
    /// deterministic physics-tick clock.  Two adjacent intervals — one ending
    /// exactly where the other begins — do NOT overlap (half-open convention).
    ///
    /// Used by steal (#96), block (#98), and on-ball contest (#99) so all
    /// three moves share a single auditable success definition.
    /// </summary>
    /// <param name="activeStart">First tick of the defender's Active phase (inclusive).</param>
    /// <param name="activeEnd">First tick after the defender's Active phase (exclusive).</param>
    /// <param name="vulnStart">First tick of the target's vulnerable window (inclusive).</param>
    /// <param name="vulnEnd">First tick after the target's vulnerable window (exclusive).</param>
    public static bool Succeeds(int activeStart, int activeEnd, int vulnStart, int vulnEnd)
        => activeStart < vulnEnd && vulnStart < activeEnd;

    /// <summary>
    /// Steal-specific success check: dribble-phase band (timing axis) AND
    /// authoritative hand-side (side axis), both required (ADR-0018 §2).
    ///
    /// Called by BallController.ResolveStealAttempts on every tick the defender's
    /// machine is in the Active phase of a StealMove.  At each such tick:
    ///   • <paramref name="phase"/> is the current DribbleCycle.Phase [0, 1).
    ///   • This is a point-in-band test: the current tick is in the vulnerable
    ///     window iff phase ∈ [loExposed, hiExposed]. The caller's per-Active-tick
    ///     repetition is what yields the ADR-0018 interval overlap.
    ///   • <paramref name="holderHand"/> is the authoritative HandSide from the
    ///     holder's PlayerController (ADR-0012) — never the cosmetic mesh offset.
    ///
    /// Returns false immediately if hands disagree; the phase test is only
    /// reached when the side is correct, matching the "two-axis" mental model
    /// (side wrong → miss, regardless of timing).
    /// </summary>
    /// <param name="phase">Current DribbleCycle.Phase in [0, 1).</param>
    /// <param name="loExposed">Low bound of the exposed phase band (inclusive). Default 0.35.</param>
    /// <param name="hiExposed">High bound of the exposed phase band (inclusive). Default 0.65.</param>
    /// <param name="targetHand">Which hand the defender aimed the steal at.</param>
    /// <param name="holderHand">Authoritative HandSide of the ball-handler (ADR-0012).</param>
    public static bool StealSucceeds(
        float phase, float loExposed, float hiExposed,
        HandSide targetHand, HandSide holderHand)
    {
        // Axis 2 — hand side (ADR-0012 / ADR-0018 §2): checked first because
        // a wrong-side steal is always a miss, no timing computation needed.
        if (targetHand != holderHand) return false;

        // Axis 1 — timing (ADR-0018 §2): the ball is only stealable while the
        // dribble phase is inside the exposed band (ball near the floor).
        // This is equivalent to Succeeds where vulnStart ≤ currentTick < vulnEnd
        // and the current tick happens to be inside the interval.
        return phase >= loExposed && phase <= hiExposed;
    }

    /// <summary>
    /// Block-specific spatial gate (issue #214): does the defender's position
    /// fall within arm's reach of the ball at block-resolution time?
    ///
    /// ADR-0018 §2's block success test (<see cref="Succeeds"/>) is
    /// timing-only — it says nothing about WHERE the defender is, which let a
    /// defender anywhere on the court "block" a shot on timing alone (a
    /// spatially unconditioned block deletes the spacing axis from the
    /// shot/block duel, CLAUDE.md §1). This predicate is the missing spatial
    /// axis; <see cref="BallController"/>.ResolveBlockAttempts (the sole
    /// caller) composes it with <see cref="Succeeds"/> — BOTH must hold for a
    /// block to connect, neither replaces the other.
    ///
    /// ADR-0014 citation (real half-court ball, tier 2): a block only
    /// connects within arm's reach of the release point. Rather than
    /// invent a new number, the reach default reuses this codebase's own
    /// already-cited "arm's-length closeout" anchor —
    /// <see cref="BallController.ContestRange"/> (2.2 m, issue #65) — the
    /// same physical concept (how close a defender's arm can reach) applied
    /// to a different defensive move. See <see cref="BallController.BlockReachRadius"/>.
    ///
    /// XZ-only distance, matching <see cref="BallController.ContestRange"/>'s
    /// own XZ-only convention (and <see cref="DefensiveKnockDirection.SafeHorizontal"/>'s):
    /// a full 3D distance to the ball would collapse this gate almost
    /// immediately after release, because the ball's height climbs fast while
    /// the defender stays grounded — the reach that matters is "close enough
    /// to reach into the shot's lane," not "as tall as the ball is high."
    /// </summary>
    /// <param name="defenderPosition">Defender's world position (engine lookup resolved by the caller).</param>
    /// <param name="ballPosition">Ball's world position at resolution time (engine lookup resolved by the caller).</param>
    /// <param name="reachRadius">Maximum XZ distance (metres) at which a block can still connect.</param>
    public static bool WithinBlockReach(Vector3 defenderPosition, Vector3 ballPosition, float reachRadius)
    {
        float dx = defenderPosition.X - ballPosition.X;
        float dz = defenderPosition.Z - ballPosition.Z;
        float distanceXZ = MathF.Sqrt(dx * dx + dz * dz);
        return distanceXZ <= reachRadius;
    }
}
