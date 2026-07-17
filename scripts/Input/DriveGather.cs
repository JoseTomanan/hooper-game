#nullable enable

namespace Hooper.Moves;

/// <summary>
/// The drive-gather — the second leaf of the M9 rim-finishing vertical
/// (issue #230, ADR-0022, epic #203). A distinct committed move from every
/// existing burst-family move (Crossover/BehindTheBack/BetweenTheLegs): its
/// Startup opts into the SAME hybrid-gather momentum model (#198's
/// GatherDecel-style bleed) via its own tunable, per ADR-0022's explicit
/// "reuse the existing model, do not invent a second one" instruction — but
/// its Active window is a straight-line drive line toward the rim, not a
/// player-steered lateral burst (the euro-step, #231, is that lateral
/// variant; this move is deliberately its straight-line sibling).
///
/// ── Real-ball identity (ADR-0014 tier 2, cited in ADR-0022) ─────────────────
/// "The gather" is the moment a driving ball-handler picks up their dribble
/// and commits their body toward the rim — the drive equivalent of the
/// shooting cradle ADR-0008's 2026-07-05 amendment already named
/// (CradleForShotStartup). PlayerController's BeginCommittedMove fires that
/// SAME cradle call for this move at Startup-begin (not Active-entry like
/// StepBack) because the gather itself — not a later burst — IS this move's
/// entire identity: real ball's gather step happens the instant the dribble
/// is picked up, not after a separation burst.
///
/// Default frame data (tunable at construction if needed):
///   Startup:   6 ticks — quick gather commit (~0.10s at 60Hz), matching the
///              burst family's own Startup range (Crossover/BehindTheBack/
///              BetweenTheLegs all use 6) — the gather itself is a fast plant,
///              not a lengthy wind-up like a jump shot's.
///   Active:    10 ticks — deliberately LONGER than every burst-family move's
///              Active window (Crossover 3, BehindTheBack 3, StepBack 4):
///              those are single-impulse separation bursts, but a drive has
///              to cover real ground toward the rim before a finish is even
///              reachable — real half-court ball's gather-to-rim is a
///              multi-step sequence, not one dribble move's worth of motion.
///   Recovery:  14 ticks — matches Layup's own Recovery (14): a drive that
///              doesn't convert into a finish still costs a real punish
///              window (ADR-0003), same order of magnitude as the finish
///              move it sets up.
///   Feint: NONE (feintWindowFrames = 0). ADR-0022 itself frames the gather as
///              "the committed instant before a finish" — an irreversible
///              plant, not a probe. Consistent with StepBack's/Layup's own
///              "no free bait for the highest-commitment moves" precedent
///              (ADR-0014 tier 3, Undisputed 3's "commitment has a cost").
/// </summary>
public sealed class DriveGather : CommittedMove
{
    /// <summary>Default drive-gather frame data. Tunable per instance if needed.</summary>
    public static readonly MoveFrameData DefaultFrameData =
        new(startupFrames: 6, activeFrames: 10, recoveryFrames: 14, feintWindowFrames: 0);

    /// <param name="frameData">Override frame data; null uses DefaultFrameData.</param>
    public DriveGather(MoveFrameData? frameData = null)
        : base(id: "drivegather", displayName: "Drive-Gather", frameData: frameData ?? DefaultFrameData)
    {
    }
}
