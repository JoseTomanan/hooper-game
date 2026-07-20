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

    /// <summary>
    /// Transit (crossover-sweep) steal spatial gate (issue #196, ADR-0018
    /// Amendment 2026-07-20). A THIRD steal shape, unioned with (not
    /// replacing) the live-dribble two-axis read (<see cref="StealSucceeds"/>)
    /// and the Held pump-fake window (<see cref="HeldStealSucceeds"/>):
    /// during a #195 ball-transit sweep, the side axis is dropped (the ball is
    /// between hands — see <see cref="BallController"/>.
    /// ResolveDribblingStealAttempts for why `targetHand` is ignored here) and
    /// replaced by a spatial one — is the defender within reach of the ball's
    /// actual SWEPT position, which self-encodes "exposed on the side the ball
    /// crosses into."
    ///
    /// ── Why a thin delegate, not a fresh implementation ──────────────────
    /// Identical XZ-only distance concept to <see cref="WithinBlockReach"/>
    /// (a grounded defender's reach doesn't care about the ball's height —
    /// same rationale, see that method's doc), just a different call site and
    /// a different tunable radius (<see cref="BallController.StealReachRadius"/>,
    /// NOT BlockReachRadius — a separate feel axis for a separate move).
    /// Named for call-site legibility rather than making
    /// ResolveDribblingStealAttempts call "WithinBlockReach" for a steal,
    /// mirroring the HeldStealSucceeds -> Succeeds and ContestAppliesAt ->
    /// Succeeds precedents already in this file: reuse the primitive, name
    /// the wrapper for the caller's own vocabulary.
    ///
    /// ── Timing is NOT part of this predicate ─────────────────────────────
    /// Unlike Succeeds/StealSucceeds/HeldStealSucceeds, this method takes no
    /// tick bounds at all — the timing axis (is a #195 sweep currently
    /// active?) is gated by the CALLER reading BallController's authoritative
    /// `_sweepActive` field directly (mirroring how ResolveDribblingStealAttempts
    /// already gates the live-dribble check by reading
    /// `defender.ActiveMove&lt;StealMove&gt;()`), not threaded through this pure
    /// method as an interval. The spatial test alone is what's left to encode
    /// once timing is externally satisfied.
    ///
    /// ── 2v2 extension point (deliberately NOT built here) ────────────────
    /// A facing-cone gate (defender must be facing the swept ball, heading
    /// within a cone) is deliberately OMITTED for 1v1: the on-ball defender
    /// always faces the handler, so a cone never bites — it would just add
    /// dead code with no discriminating power in the 1v1 case this issue
    /// scopes. The natural extension point for a help-defender side-poke,
    /// once 2v2 exists, is composing a heading-cone check alongside this
    /// reach test at the call site — not folding one in here.
    /// </summary>
    /// <param name="defenderPosition">Defender's world position (engine lookup resolved by the caller).</param>
    /// <param name="sweptBallPosition">
    /// The ball's authoritative GlobalPosition while a #195 sweep is active —
    /// TickDribbling writes the sweep's lateral/forward/vertical offsets
    /// directly into this position (not a cosmetic mesh offset), so it is the
    /// same honest, reconciled value on the server and every predicting
    /// client (ADR-0004).
    /// </param>
    /// <param name="reachRadius">Maximum XZ distance (metres) at which a transit steal can still connect.</param>
    public static bool WithinStealTransitReach(Vector3 defenderPosition, Vector3 sweptBallPosition, float reachRadius)
        => WithinBlockReach(defenderPosition, sweptBallPosition, reachRadius);

    /// <summary>
    /// Static Held-ball steal spatial gate (issue #255, ADR-0018 Amendment
    /// 2026-07-20, Route A "static proximity/facing exposure" — human-decided
    /// per the issue's decision comment). Thin delegate to
    /// <see cref="WithinBlockReach"/>, following the exact
    /// <see cref="WithinStealTransitReach"/> precedent: same XZ-only distance
    /// concept (a grounded defender's reach doesn't care about the Held
    /// ball's height), a distinct tunable radius
    /// (<see cref="BallController.HeldStealReachRadius"/>, a separate feel
    /// axis from BlockReachRadius/StealReachRadius), named for this call
    /// site's own vocabulary.
    /// </summary>
    /// <param name="defenderPosition">Defender's world position.</param>
    /// <param name="ballPosition">The Held ball's world position (== holder's carry position).</param>
    /// <param name="reachRadius">Maximum XZ distance (metres) at which a static Held steal can still connect.</param>
    public static bool WithinHeldStealReach(Vector3 defenderPosition, Vector3 ballPosition, float reachRadius)
        => WithinBlockReach(defenderPosition, ballPosition, reachRadius);

    /// <summary>
    /// Static Held-ball steal FACING gate (issue #255, ADR-0018 Amendment
    /// 2026-07-20, Route A) — the genuinely new predicate this issue adds.
    ///
    /// ── What this closes ─────────────────────────────────────────────────
    /// #206 (ADR-0018 Amendment 2026-07-19) closed the PUMP-FAKE dodge but
    /// explicitly left a plain, idle Held ball (no committed move in
    /// progress — <see cref="HeldStealSucceeds"/>'s window is null) fully
    /// immune: a holder who simply never shoots was untouchable. Route A
    /// (over Route B's closely-guarded/5-second rule, rejected — see the
    /// ADR-0018 amendment for the full comparison) adds a static exposure
    /// term so a STATIONARY Held ball is mildly exposed — hard to steal, not
    /// impossible — mirroring real half-court 1v1 (ADR-0014 tier 2): a
    /// gathered/triple-threat ball is protected by BODY POSITION, not
    /// sanctuary. A defender positioned on the ball-hand side can still
    /// reach in and poke it; a holder who pivots their shoulder/back toward
    /// the defender (shielding) denies that angle entirely.
    ///
    /// ── Why HandSide, not TargetHand (contrast HeldStealSucceeds) ────────
    /// The pump-fake window (<see cref="HeldStealSucceeds"/>) is deliberately
    /// TIMING-ONLY — its own doc explains a cradled ball has no discriminable
    /// hand-side to aim at DURING a pump-fake (the whole body is committed
    /// to the shot-startup animation, no read to bait). That reasoning does
    /// NOT apply here: a STATIC triple-threat hold has no committed-move
    /// startup consuming the body, so the holder's actual carry side
    /// (<see cref="HandSide"/>, ADR-0012) is a real, currently-true fact
    /// about where the ball physically sits relative to their torso — and
    /// exposing it is the exact design property that makes this a FACING
    /// read (reward defensive positioning) rather than a second timing axis.
    /// This predicate therefore reads <c>holderHand</c> + <c>holderHeading</c>
    /// together, never <c>TargetHand</c> — the defender does not need to
    /// "call" a side; whichever side the ball is actually carried on is the
    /// side that is exposed, full stop.
    ///
    /// ── The geometry (LOCKED to the ball-render convention, not re-derived) ──
    /// The holder's authoritative <see cref="PlayerController.Heading"/>
    /// (ADR-0010 — NEVER the cosmetic FacingResolver, exactly like
    /// <see cref="StealSucceeds"/>'s own HandSide read and ShotFacing's own
    /// citation) determines a body-relative "hand-side direction." This MUST
    /// be computed with the exact same formula
    /// <c>BallController.HandRight</c>/<c>BallController.HandSign</c> already
    /// use to place the ball mesh in-hand (verified by direct read of those
    /// methods during code review of this predicate's first version, which
    /// had derived an independent — and MIRRORED — convention that silently
    /// pointed the exposed cone at the PROTECTED side instead):
    ///
    ///   forward     = HeadingMath.Forward(heading)              // (worldX, worldZ)
    ///   right       = (-forward.Z, forward.X)                   // BallController.HandRight, flattened to XZ
    ///   handDirection = right * (hand == Right ? +1 : -1)        // BallController.HandSign
    ///
    /// At heading 0 (Forward == +Z, i.e. (0,1) in this (X,Z) pair), that
    /// gives <c>right = (-1, 0)</c> — world −X — so a Right-hand ball is
    /// carried toward −X and a Left-hand ball toward +X. This is Godot's
    /// right-handed, +Z-forward convention: a player facing +Z has their
    /// anatomical right toward −X, the opposite of the naive "right hand ⇒
    /// world +X" assumption a fresh reader (or a fresh predicate) is prone to
    /// making. Deriving this locally from the SAME two primitives the render
    /// reads — rather than re-deriving "which way is right" independently —
    /// is what keeps this predicate from drifting out of sync with the
    /// render again.
    ///
    /// The defender is "exposed to" iff the unit vector from the holder to
    /// the defender falls within <paramref name="halfConeRadians"/> of that
    /// hand-side direction — a dot-product cone test (both vectors unit
    /// length, so their dot product IS cos(angle-between)):
    ///
    ///   exposed ⟺ dot(toDefender, handDirection) ≥ cos(halfConeRadians) − ε
    ///
    /// Turning the holder's body (changing Heading) rotates the ENTIRE cone
    /// with it — this is exactly how "pivoting to shield the ball" falsifies
    /// the predicate without moving anyone: rotate 180° and a defender who
    /// was squarely on-axis (dot = 1) becomes squarely OFF-axis (dot = −1),
    /// well below any reasonable cone threshold. No separate "is the
    /// defender behind me" check is needed — the cone IS the shield.
    ///
    /// Half-open boundary convention: inclusive (≥), matching
    /// <see cref="StealSucceeds"/>'s own closed-band convention
    /// (<c>phase &gt;= lo &amp;&amp; phase &lt;= hi</c>) rather than the
    /// half-open [start, end) tick-interval convention <see cref="Succeeds"/>
    /// uses — this is a continuous spatial angle, not a discrete tick range,
    /// so "exactly at the cone edge" is defined to count as exposed. A tiny
    /// epsilon (<see cref="ConeBoundaryEpsilon"/>) is subtracted from the
    /// comparison threshold purely to absorb float round-trip error from
    /// composing two independent trig evaluations (the caller's cone-edge
    /// position via MathF.Sin/Cos, and this method's own HeadingMath.Forward)
    /// — without it, a geometrically-exact boundary case can land a few ULPs
    /// on the "wrong" side of a bare ≥ and flip a should-be-true result to
    /// false. It has no effect away from the exact edge.
    ///
    /// Degenerate case: holder and defender occupying the identical XZ point
    /// have no discriminable direction between them (division by zero) —
    /// returns false (not exposed) rather than throwing or producing NaN.
    /// This should not arise in practice (a defender at zero XZ distance
    /// from the holder has already failed <see cref="WithinHeldStealReach"/>'s
    /// caller-composed reach check in every realistic geometry, but the
    /// guard exists so this pure function is total regardless).
    /// </summary>
    /// <param name="holderPosition">Holder's world position (XZ used; Y ignored).</param>
    /// <param name="holderHeading">
    /// Holder's authoritative Heading in radians (ADR-0010 — Y-rotation,
    /// Godot convention, Atan2(x, z), yaw 0 faces +Z). Must be
    /// PlayerController.Heading, never a cosmetic facing value.
    /// </param>
    /// <param name="holderHand">Holder's authoritative carry HandSide (ADR-0012).</param>
    /// <param name="defenderPosition">Defender's world position (XZ used; Y ignored).</param>
    /// <param name="halfConeRadians">
    /// Half-angle (radians) of the exposed cone straddling the hand-side
    /// direction. Larger = easier to be "on the exposed side"; smaller =
    /// the holder must be almost squarely facing away from the defender's
    /// carry-side for exposure. Provisional tunable — see
    /// <see cref="BallController.HeldStealExposureConeDegrees"/>.
    /// </param>
    public static bool HeldStaticHandExposed(
        Vector3 holderPosition, float holderHeading, HandSide holderHand,
        Vector3 defenderPosition, float halfConeRadians)
    {
        float dx = defenderPosition.X - holderPosition.X;
        float dz = defenderPosition.Z - holderPosition.Z;
        float distSq = dx * dx + dz * dz;

        // Degenerate: coincident XZ positions have no discriminable
        // direction (would divide by zero below). See method doc.
        if (distSq < 1e-6f) return false;

        float invDist = 1f / MathF.Sqrt(distSq);
        var toDefender = new Vector2(dx * invDist, dz * invDist);

        // LOCKED to BallController.HandRight/HandSign's own formula (see this
        // method's class doc for the worked-through derivation and why a
        // fresh "Right = +90 degrees" assumption is the WRONG convention in
        // this codebase's coordinate system) — never re-derive this
        // independently.
        Vector2 forward = HeadingMath.Forward(holderHeading); // (worldX, worldZ)
        Vector2 right = new(-forward.Y, forward.X);           // BallController.HandRight, flattened to XZ
        float handSign = holderHand == HandSide.Right ? 1f : -1f; // BallController.HandSign
        Vector2 handDirection = right * handSign;

        float dot = toDefender.Dot(handDirection); // both unit vectors -> cos(angle-between)
        float cosHalfCone = MathF.Cos(halfConeRadians);

        return dot >= cosHalfCone - ConeBoundaryEpsilon;
    }

    /// <summary>
    /// Float round-trip tolerance for <see cref="HeldStaticHandExposed"/>'s
    /// inclusive cone-boundary comparison — see that method's own doc for why
    /// a bare ≥ is not float-safe at an exact geometric edge.
    /// </summary>
    private const float ConeBoundaryEpsilon = 1e-4f;
}
