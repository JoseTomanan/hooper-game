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

    /// <summary>
    /// (Issue #282) The defender's OWN body-relative aim sign, exactly as read
    /// off the aim stick: positive = the defender's body-RIGHT, non-positive =
    /// body-left.  The un-transformed input to
    /// <see cref="HandStateResolver.TargetHandFromAim"/>, kept alongside that
    /// method's output because the two answer different questions and only one
    /// of them can drive an animation.
    ///
    /// ── Why both are stored, and why they are NOT redundant ──────────────
    /// <see cref="TargetHand"/> is HOLDER-relative ("which of the ball-handler's
    /// hands is under attack") — that is what
    /// DefensiveResolution.StealSucceeds compares against the handler's
    /// authoritative HandSide, so it is the gameplay axis.  This field is
    /// DEFENDER-relative ("which way does THIS body reach"), and the two are
    /// related by the #254 facing transform:
    ///
    ///     TargetHand == AimSign * sign(cos(defenderHeading - holderHeading))
    ///
    /// so they AGREE when the two players face the same way (cos = +1,
    /// side-by-side/trailing defence) and are OPPOSITE face-to-face (cos = -1).
    /// A caller passing values that differ is therefore normal, not a bug —
    /// there is no "they must match" invariant to enforce here.
    ///
    /// ── Why the animation needs this one and not TargetHand ──────────────
    /// The clip poses the DEFENDER's skeleton, so its polarity has to be a fact
    /// about the defender's body.  Selecting the clip from the holder-relative
    /// TargetHand would animate the mirror-image arm for every face-to-face
    /// duel — the arm sweeping away from the hand it is supposedly attacking.
    /// That is precisely the ADR-0003 false read the handed split exists to
    /// prevent: the state would exist, play cleanly, and telegraph the wrong
    /// side.  See <see cref="ReachSide"/> and MoveAnimResolver's
    /// TargetHandedMoves.
    ///
    /// Fixed at construction like the burst-family payloads, so it is constant
    /// across Startup/Active/Recovery and needs no phase conditioning.
    /// </summary>
    public float AimSign { get; }

    /// <summary>
    /// (Issue #282) <see cref="AimSign"/> as the two-valued side the animation
    /// layer names its clips by — the side of the defender's OWN body the
    /// swiping hand travels to.
    ///
    /// A derived property rather than a second stored field so the sign and the
    /// side cannot drift apart.  Reuses <see cref="HandSide"/> rather than a new
    /// enum because the AnimationTree state names are built by string
    /// concatenation and this type's ToString() already yields exactly the
    /// "Left"/"Right" suffix scenes/Player.tscn is authored with; the repo
    /// convention is to reuse the existing hand-side type for any body-side
    /// predicate rather than re-derive a parallel one.
    ///
    /// Tie-break: an exactly-zero aim sign resolves to Left, matching
    /// <see cref="HandStateResolver.TargetHandFromAim"/>'s own "&gt; 0f, else
    /// Left" shape.  SampleMoveInput never produces 0 (it quantises to +/-1f
    /// before constructing), so this is a total-function formality rather than
    /// a reachable case.
    /// </summary>
    public HandSide ReachSide => AimSign > 0f ? HandSide.Right : HandSide.Left;

    /// <param name="targetHand">
    /// Which hand the defender is stealing toward.  Derived from the
    /// right-stick flick direction in PlayerController.SampleMoveInput.
    /// </param>
    /// <param name="aimSign">
    /// (Issue #282) The defender's own body-relative aim sign that
    /// <paramref name="targetHand"/> was derived FROM — see
    /// <see cref="AimSign"/>.  Deliberately REQUIRED rather than defaulted:
    /// a wrong reach side does not degrade, it resolves to a clip that exists
    /// and plays cleanly while telegraphing the wrong direction, and a silent
    /// default is how the #255 mirror bug shipped.  Making it required turns
    /// "forgot the reach side" into a compile error at every future
    /// construction site.
    /// </param>
    /// <param name="frameData">Override frame data; null uses DefaultFrameData.</param>
    public StealMove(HandSide targetHand, float aimSign, MoveFrameData? frameData = null)
        : base(id: "steal", displayName: "Steal", frameData: frameData ?? DefaultFrameData)
    {
        TargetHand = targetHand;
        AimSign = aimSign;
    }
}
