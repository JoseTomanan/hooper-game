#nullable enable

namespace Hooper.Moves;

/// <summary>
/// The jab step — triple threat's stance bait (issue #200, ADR-0008 #193
/// amendment). #193 gave triple threat (live Held) a dual dribble/shoot
/// threat and a pivot latch (#172), but nothing in that stance actually
/// BAITS the defender: the entire point of triple threat as a mind game is
/// threatening the drive without committing to it. The jab is that tool — a
/// quick, honest foot-stab that sells "I might drive" without surrendering
/// the pivot or leaving Held.
///
/// ── Why this is purely informational (ADR-0003 legibility, not movement) ──
/// Real ball's jab step has essentially zero displacement — it is a foot
/// gesture the DEFENDER reads, not a separation tool the ball-handler uses to
/// gain space (that is what a real drive/crossover/euro-step are for). This
/// move therefore carries NO burst payload and produces no ball movement —
/// unlike <see cref="Crossover"/>/<see cref="BehindTheBack"/>/
/// <see cref="EuroStep"/>, which all set Velocity on JustEnteredActive, the
/// jab's Active phase is a pure "wait" the PlayerController tick loop does
/// not intercept at all (see PlayerController.TickCommittedMoveBehavior: the
/// Startup/Active switches fall through to their existing defaults for any
/// move they don't explicitly branch on). The legibility payoff is entirely
/// in the frame-data telegraph (Startup/Active are visible to the opponent),
/// not in a physical result.
///
/// ── Why feintWindowFrames is 0 (design constant, not a placeholder) ────────
/// The jab step IS the feint — there is nothing further to cancel. Giving it
/// a nonzero feint window would let a player abort the jab itself for free,
/// which would make the "bait" costless and collapse the mind game the same
/// way a recallable Hesitation would (see Hesitation's own class doc for the
/// identical reasoning). Mirrors Hesitation/RetreatDribble's
/// structurally-unfeintable pattern, not JumpShot's pump-fake one.
///
/// ── Legality (issue #200) ───────────────────────────────────────────────
/// Legal from a live OR dead Held possession (BallState.Held covers both —
/// HasDribbled distinguishes live/dead, but the jab does not care which).
/// NOT legal while Dribbling — that space is already covered by
/// Hesitation/hand-fake (#86), which is the honest "freeze the live dribble"
/// bait. See JabStepLegalityResolver for the pure gate PlayerController.
/// BeginCommittedMove enforces (the INVERSE of the existing dead-dribble
/// gate, which blocks Crossover/Hesitation/etc. FROM Held, not FROM
/// Dribbling).
///
/// Default frame data (placeholder — hesi-shaped, per the issue; feel tuning
/// deferred to the consolidated human pass, #173):
///   Startup:  3 ticks — the foot-stab wind-up; short because the whole
///                       point is a QUICK read, not a long telegraph.
///   Active:   2 ticks — the stab itself; brief, matching the real gesture's
///                       near-instant snap.
///   Recovery: 4 ticks — returning to the triple-threat stance; a short but
///                       real punish window if the defender didn't bite.
///   Feint:    0 ticks — no feint; the jab is already the feint (see above).
/// </summary>
public sealed class JabStep : CommittedMove
{
    /// <summary>
    /// Default jab-step frame data. All three counts are placeholder
    /// magnitudes (hesi-shaped, per the issue) — the feel pass, not this
    /// class, decides whether they read right. feintWindowFrames = 0 is the
    /// one non-placeholder value; see the class doc for why.
    /// </summary>
    public static readonly MoveFrameData DefaultFrameData =
        new(startupFrames: 3, activeFrames: 2, recoveryFrames: 4, feintWindowFrames: 0);

    /// <param name="frameData">
    /// Override frame data for tuning. Null uses <see cref="DefaultFrameData"/>.
    /// </param>
    public JabStep(MoveFrameData? frameData = null)
        : base(id: "jab", displayName: "Jab Step", frameData: frameData ?? DefaultFrameData)
    {
    }
}
