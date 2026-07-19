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
/// live dribble phase (BallController.ResolveDribblingStealAttempts, issue #96;
/// split out of ResolveStealAttempts in #206) — the union of those in-band
/// point tests over the Active window IS the overlap.
/// Block (#98) instead calls the full interval Succeeds form directly,
/// because the shot's release window has a concrete start tick in InFlight.
///
/// ── Block-specific spatial gate (issue #214) ─────────────────────────────
/// Succeeds alone is timing-only — it says nothing about WHERE the defender
/// is. WithinBlockReach is the missing spatial axis, composed by the caller
/// (BallController.ResolveBlockAttempts) alongside Succeeds: BOTH must hold
/// for a block to connect. See WithinBlockReach's own doc for the ADR-0014
/// citation and the XZ-only distance rationale.
///
/// ── Contest-specific composition (issue #99, ADR-0018 §2) ────────────────
/// Unlike steal/block, contest never grants a binary succeed/fail — it
/// applies an ADDITIONAL multiplicative accuracy factor on top of the
/// existing passive proximity scatter (ADR-0009 / #65) when the defender's
/// committed contest is Active on the exact tick the shot releases.
/// ContestAppliesAt/ContestMoveFactor are that composition's pure pieces —
/// see each method's own doc.
///
/// ── Shared XZ-distance helper (issue #99 folded-forward cleanup, PR #220
/// review comment) ─────────────────────────────────────────────────────────
/// DistanceXZSquared retires a drift channel: the dx*dx+dz*dz XZ-distance
/// computation previously existed independently in WithinBlockReach AND in
/// BallController.ApplyShootLocally's passive contestFactor calc — two
/// copies of "XZ distance between two positions" with nothing enforcing they
/// agreed. Both now share this one implementation.
/// </summary>
public static class DefensiveResolution
{
    /// <summary>
    /// Shared XZ-plane squared-distance helper. XZ-only for the same reason
    /// <see cref="WithinBlockReach"/> and <see cref="BallController.ContestRange"/>
    /// are XZ-only (see WithinBlockReach's own doc): a grounded defender vs. a
    /// ball that climbs quickly into its arc has a height difference that
    /// carries no defensive-spacing information in this project.
    ///
    /// Squared, not the sqrt distance, so callers that only need a boolean
    /// "within radius" comparison (WithinBlockReach) can compare against
    /// <c>radius * radius</c> and skip a per-tick <c>MathF.Sqrt</c> — the same
    /// pattern <see cref="ReboundContest.Resolve"/> already uses to keep a
    /// predicted (client) result bit-identical to the server's. A caller that
    /// needs the actual metric distance (e.g. a linear proximity ratio) takes
    /// <c>MathF.Sqrt</c> of the result itself.
    /// </summary>
    public static float DistanceXZSquared(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return dx * dx + dz * dz;
    }

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
    /// Called by BallController.ResolveDribblingStealAttempts (the live-dribble
    /// branch of the ResolveStealAttempts dispatcher, #206) on every tick the
    /// defender's machine is in the Active phase of a StealMove.  At each such tick:
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
        // Squared-distance comparison — no per-tick MathF.Sqrt (issue #99
        // folded-forward cleanup #2 from PR #220 review: block is client-
        // predicted + reconciled (ADR-0018 §4), so a boundary divergence
        // already self-heals via reconciliation, and .NET's MathF.Sqrt is
        // IEEE-754 correctly-rounded anyway — but comparing squared distances
        // is strictly cheaper and matches ReboundContest.Resolve's own
        // established pattern for exactly this reason).
        return DistanceXZSquared(defenderPosition, ballPosition) <= reachRadius * reachRadius;
    }

    /// <summary>
    /// Contest-specific timing gate (issue #99, ADR-0018 §2): is the
    /// defender's committed contest Active on the exact tick the shot
    /// releases?
    ///
    /// Contest never grants steal/block's binary succeed/fail overlap — the
    /// shot's "release window" it composes against collapses to a SINGLE tick,
    /// because shot scatter (ADR-0009) is computed exactly once, at the moment
    /// <c>BallController.ApplyShootLocally</c> releases the ball. There is no
    /// ongoing "shot in flight, keep contesting it" recomputation the way
    /// block's multi-tick grace window has. So the vulnerable interval handed
    /// to the shared <see cref="Succeeds"/> predicate is
    /// <c>[releaseTick, releaseTick + 1)</c> — a single tick — which makes
    /// this call algebraically equivalent to "is the defender in Active phase
    /// on a ContestMove right now." Routing it through <see cref="Succeeds"/>
    /// anyway (rather than inlining that equivalence) keeps contest's
    /// resolution auditable against the same shared ADR-0018 §1 predicate
    /// steal and block use, and keeps this method open to a future release
    /// window that widens past a single tick without a call-site rewrite.
    /// </summary>
    /// <param name="contestActiveStart">First tick of the defender's ContestMove Active phase (inclusive).</param>
    /// <param name="contestActiveEnd">First tick after the defender's ContestMove Active phase (exclusive).</param>
    /// <param name="releaseTick">The physics tick the shot released (BallController._inFlightStartTick).</param>
    public static bool ContestAppliesAt(int contestActiveStart, int contestActiveEnd, int releaseTick)
        => Succeeds(contestActiveStart, contestActiveEnd, releaseTick, releaseTick + 1);

    /// <summary>
    /// The contest's ADDITIONAL accuracy factor (issue #99, ADR-0018 §2) —
    /// composed multiplicatively ON TOP OF the existing passive proximity
    /// scatter (ADR-0009 / #65's <c>contestFactor</c>), never replacing it.
    /// Returns <c>1 + contestMoveScatterK</c> when the committed contest was
    /// Active at release (see <see cref="ContestAppliesAt"/>); otherwise
    /// returns 1 (no effect) so a shot with no active contest behaves exactly
    /// as it did before #99 existed.
    /// </summary>
    /// <param name="contestActiveAtRelease">Result of <see cref="ContestAppliesAt"/>.</param>
    /// <param name="contestMoveScatterK">
    /// Balance knob: the extra multiplier strength when a committed contest
    /// connects. Provisional default lives on
    /// <see cref="BallController.ContestMoveScatterK"/> — tuning deferred to
    /// #104 + the per-milestone feel pass (ADR-0015).
    /// </param>
    public static float ContestMoveFactor(bool contestActiveAtRelease, float contestMoveScatterK)
        => contestActiveAtRelease ? 1f + contestMoveScatterK : 1f;

    /// <summary>
    /// Held-ball steal success check (issue #206, ADR-0018 Amendment
    /// 2026-07-19, human-decided Option A "pump-fake window"). A `Held` ball
    /// was previously steal-immune outright (<see cref="BallController"/>.
    /// ResolveStealAttempts early-returned unless the ball was Dribbling) —
    /// which let a holder mash JumpShot's pump-fake to dodge any steal read
    /// on reaction, inverting the mind-game the timing-window model exists to
    /// create (CLAUDE.md §1). This predicate composes the SAME shared overlap
    /// primitive (<see cref="Succeeds"/>) block already uses: a Held steal
    /// connects iff the defender's StealMove Active window overlaps the
    /// holder's JumpShot Startup-or-feint-Recovery vulnerable window (both
    /// half-open absolute-tick intervals — see
    /// <see cref="PlayerController.HeldStealVulnerableWindow"/> for how the
    /// vulnerable interval itself is derived).
    ///
    /// ── Why no hand-side axis here (ADR-0014 tier-2 self-resolution) ───────
    /// The live-dribble steal (<see cref="StealSucceeds"/>) gates on a
    /// TargetHand match because a bouncing dribble is inherently
    /// one-handed and exposes a discriminable side. A Held cradle is not: in
    /// real half-court 1v1, a gathered/triple-threat ball is protected with
    /// the whole body, not dribbled to one side, so "which hand did you aim
    /// at" has no real-ball referent for a stationary cradle. Requiring a
    /// hand match here would just hand the holder ANOTHER axis to dodge on
    /// (aim your body so the "wrong" hand faces the defender), diluting the
    /// exact design property that won this option in the decision brief: a
    /// pump-fake exposes the gather, full stop, no second read to bait. This
    /// predicate is therefore TIMING-ONLY, mirroring block's own
    /// timing-plus-separately-composed-reach shape rather than steal's
    /// timing-plus-hand shape — see <see cref="WithinBlockReach"/>'s doc for
    /// the parallel "compose an ADDITIONAL axis at the call site, don't fold
    /// it in here" convention if a future spatial term is ever added.
    /// </summary>
    /// <param name="activeStart">First tick of the defender's StealMove Active phase (inclusive).</param>
    /// <param name="activeEnd">First tick after the defender's StealMove Active phase (exclusive).</param>
    /// <param name="vulnStart">First tick of the holder's Held-steal vulnerable window (inclusive).</param>
    /// <param name="vulnEnd">First tick after the holder's Held-steal vulnerable window (exclusive).</param>
    public static bool HeldStealSucceeds(int activeStart, int activeEnd, int vulnStart, int vulnEnd)
        => Succeeds(activeStart, activeEnd, vulnStart, vulnEnd);
}
