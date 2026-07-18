#nullable enable

namespace Hooper.Moves;

/// <summary>
/// The euro-step — the third and final leaf of the M9 rim-finishing vertical
/// (issue #231, ADR-0022, epic #203). A distinct committed move from the
/// straight-line <see cref="DriveGather"/> (#230) it is a lateral variant of:
/// its Active window applies a lateral displacement (EuroStepMath) that shifts
/// the drive angle past an on-ball defender, so a subsequent <see cref="Layup"/>
/// (#229) finishes from an angle a defender committed to the ORIGINAL drive line
/// can no longer contest.
///
/// ── Why the euro-step is a displacement move, and the finish is a CHAIN ───────
/// The euro-step does NOT itself release the ball. Mirroring the proven
/// drive-gather -> layup relationship (DriveGatherTest's "gather-to-layup-chain"
/// scenario: the gather completes to Inactive, THEN a real Layup begins), this
/// move only carries the player laterally to the displaced launch position; the
/// finish is a SEPARATE, client-predicted, server-gated Layup begun from there.
/// That keeps the euro-step out of JumpShotReleaseResolver entirely and lets the
/// displaced finish cross the SAME ADR-0023 range gate (evaluated at the
/// displaced GlobalPosition) verbatim — no second gate, no abort-during-Active
/// reconciliation path. See PlayerController's "eurostep" dispatch and its
/// Active-entry branch for the two halves.
///
/// ── Real-ball identity (ADR-0014 tier 2, cited in ADR-0022) ──────────────────
/// The euro-step is a two-beat rim finish: gather one direction, then plant and
/// step the OTHER to slide past a shot-blocker. Beat 1 (the gather) reuses
/// DriveGather's Startup path verbatim — Heading bends toward the rim at the
/// bounded turn rate (ADR-0010, not an instant snap) and the dribble is cradled.
/// Beat 2 (the plant) is the Active-entry lateral displacement (EuroStepMath).
///
/// Default frame data — CANDIDATES, not final. These magnitudes interact with
/// the rest of the move set and are catalogued for #238's consolidated tuning
/// pass; they are dialed there, not tuned solo (matching DriveGather/Layup's
/// own "candidate, not a dial" precedent).
///   Startup:  6 ticks — the gather commit, same as DriveGather's (the euro-step
///             gathers identically; only its Active beat differs).
///   Active:  14 ticks — deliberately LONGER than DriveGather's straight-line 10:
///             a lateral gather-then-plant covers two beats (sideways THEN past
///             the defender) where the straight drive covers one, so the
///             displacement needs more Active ground to complete before the
///             finish is reachable.
///   Recovery:16 ticks — a mistimed euro-step (read the defender wrong, or no
///             finish available) costs a real punish window (ADR-0003), slightly
///             longer than DriveGather's 14: a lateral commitment is harder to
///             recover balance from than a straight plant.
///   Feint: NONE (feintWindowFrames = 0) — the highest-commitment finish moves
///             (DriveGather/Layup/StepBack) all forbid a free abort; the lateral
///             plant is an irreversible commitment, not a probe (ADR-0014 tier 3,
///             Undisputed 3's "commitment has a cost").
/// </summary>
public sealed class EuroStep : CommittedMove
{
    /// <summary>Default euro-step frame data. Tunable per instance if needed.</summary>
    public static readonly MoveFrameData DefaultFrameData =
        new(startupFrames: 6, activeFrames: 14, recoveryFrames: 16, feintWindowFrames: 0);

    /// <summary>
    /// Body-relative direction of the lateral step (the "read"): +1 = step to the
    /// player's right, -1 = to the player's left. Chosen client-side from the
    /// left stick's lateral tilt at press and carried on the RequestBeginMove
    /// RPC param, the same reconstruction-payload convention
    /// <see cref="Crossover.BurstDirection"/> uses (the world direction is
    /// re-derived from Heading when the move reaches Active — see EuroStepMath).
    /// </summary>
    public float LateralDirection { get; }

    /// <param name="lateralDirection">+1 = step right, -1 = step left.</param>
    /// <param name="frameData">Override frame data; null uses DefaultFrameData.</param>
    public EuroStep(float lateralDirection, MoveFrameData? frameData = null)
        : base(id: "eurostep", displayName: "Euro-Step", frameData: frameData ?? DefaultFrameData)
    {
        LateralDirection = lateralDirection;
    }
}
