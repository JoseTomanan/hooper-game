#nullable enable

using Hooper.Player;

namespace Hooper.Moves;

/// <summary>
/// The steal attempt — the first M10 defensive committed move (issue #96,
/// epic #89, ADR-0018).
///
/// In half-court 1v1 a steal is a timed swipe at the dribble: the defender
/// commits with a visible wind-up (Startup), the attempt window opens (Active),
/// and if that window overlaps the dribble-exposed phase on the correct hand
/// the ball goes Loose (ADR-0008 §Amendment 2026-06-30, ADR-0018 §2).
///
/// ── ADR-0003 legibility commitment ──────────────────────────────────────
/// The steal is NOT an instant button (move-and-strike, ADR-0003 anti-goal).
/// Startup telegraphs the commitment to both players; Recovery is punishable
/// on a whiff — a missed steal is a blow-by opportunity (#100).
///
/// ── Two-axis read (ADR-0018 §2) ─────────────────────────────────────────
/// The defender must commit BOTH to a timing window (Active overlaps the low
/// dribble phase) AND to a side (TargetHand matches the holder's authoritative
/// HandSide, ADR-0012).  BallController.ResolveStealAttempts evaluates both
/// axes via DefensiveResolution.StealSucceeds on EVERY tick the machine is in
/// the Active phase (not just its entry tick) — the interval-overlap the ADR
/// requires, produced by re-checking a point-in-band test each Active tick
/// rather than sampling once (issue #96 remediation).
///
/// ── Reaction-tilt asymmetry (ADR-0018 §3) ───────────────────────────────
/// A defensive Active is no wider than the offensive vulnerable window it
/// must hit; Recovery is at least as long as the offensive move's.  The
/// provisional defaults below are placeholders — the exact tick counts are
/// deferred to tuning issue #104 and the per-milestone feel pass (ADR-0015).
///
/// Default frame data (provisional, tuning deferred to #104):
///   Startup:  8 ticks  — visible telegraph (~0.13s at 60 Hz)
///   Active:   8 ticks  — no wider than the default exposed phase band at
///                        60 Hz / 0.6 s period (~10.8 ticks, stay under it)
///   Recovery: 20 ticks — matches JumpShot.DefaultFrameData.RecoveryFrames
///                        so a missed steal is as punishable as a missed shot
///   Feint:    4 ticks  — feintable in the first 4 Startup frames, zero
///                        recovery cost (abort to Inactive) so an obvious
///                        fake is less punishable than a committed whiff
/// </summary>
public sealed class StealMove : CommittedMove
{
    /// <summary>Default steal frame data. Tunable per instance if needed.</summary>
    public static readonly MoveFrameData DefaultFrameData =
        new(startupFrames: 8, activeFrames: 8, recoveryFrames: 20, feintWindowFrames: 4);

    /// <summary>
    /// The hand side the defender is targeting — the right-stick flick
    /// direction disambiguated to a body-relative hand.
    ///
    /// Compared against the handler's authoritative HandSide (ADR-0012) by
    /// DefensiveResolution.StealSucceeds on every Active tick (see the class
    /// doc's "Two-axis read").  Body-relative: a flick toward the handler's
    /// LEFT side targets HandSide.Left.
    ///
    /// This is the "side" axis of the two-axis steal read (ADR-0018 §2):
    /// a steal committed to the wrong side fails even on perfect timing.
    /// </summary>
    public HandSide TargetHand { get; }

    /// <param name="targetHand">
    /// Which hand the defender is stealing toward.  Derived from the
    /// right-stick flick direction in PlayerController.SampleMoveInput.
    /// </param>
    /// <param name="frameData">Override frame data; null uses DefaultFrameData.</param>
    public StealMove(HandSide targetHand, MoveFrameData? frameData = null)
        : base(id: "steal", displayName: "Steal", frameData: frameData ?? DefaultFrameData)
    {
        TargetHand = targetHand;
    }
}
