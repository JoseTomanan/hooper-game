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
}
