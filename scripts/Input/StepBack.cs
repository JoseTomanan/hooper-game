#nullable enable

namespace Hooper.Moves;

/// <summary>
/// The step-back — the "hold" half of the vertical right-stick gesture pair
/// (issue #197, M9/epic #75), sharing one gesture-grammar extension with
/// <see cref="RetreatDribble"/> (the "quick" half).
///
/// In basketball, a step-back is a full-commitment retreat off the dribble
/// that creates the biggest separation in the taxonomy — at the cost of the
/// dribble itself. This is the risk/reward move: shoot or pivot from the
/// resulting dead <c>Held</c>, nothing else.
///
/// Key differences from <see cref="RetreatDribble"/>:
///   - Gathers: PlayerController's Active-entry handler calls
///     BallController.CradleForShotStartup, the SAME "cradle inside the
///     move" side effect #193 established for JumpShot/PumpFake (stops the
///     dribble, sets HasDribbled=true) — here fired at ACTIVE-entry rather
///     than Startup-entry, since the separation burst itself IS the gather
///     motion (there is no earlier "wind-up" moment that already commits
///     the ball the way JumpShot's Startup does).
///   - Left-stick exit shaping WITHIN A BACKWARD CONE ONLY (back / back-left
///     / back-right side-steps) — see StepBackBurstMath, which composes this
///     via #198's CrossoverBurstMath rather than a second hand-rolled
///     cone-clamp.
///   - No hand swap: there is no ball transit (unlike Crossover/
///     BehindTheBack), so #196's spatial steal term never applies to this
///     move. The counter to a step-back is the closeout, not a poke.
///   - feintWindowFrames: 0 — NOT specified explicitly by the issue, but
///     following the SAME "no free bait" reasoning the issue states for
///     RetreatDribble: this is the biggest-separation, highest-risk move in
///     the taxonomy specifically BECAUSE it spends the dribble; a
///     free-abort window would let a player probe for the same risk-free
///     hesitation-style read StepBack is supposed to cost real Held-dribble
///     currency for. Consistent with Hesitation's own "the absence of a
///     cancel IS the mind game" precedent (ADR-0003).
///
/// Default frame data (placeholder per the issue's own explicit numbers —
/// human tunes exact feel in-editor, deferred per ADR-0021):
///   Startup:  7 ticks  — the biggest separation move telegraphs the longest
///   Active:   4 ticks  — the burst + gather window
///   Recovery: 8 ticks  — punish window; dead-Held already costs the dribble,
///                        so Recovery does not also need to be punishing
///                        on top of that (unlike Crossover's family, which
///                        pays ONLY in Recovery)
///   Feint:    0 ticks  — see class doc above; a design call, not a
///                        placeholder — cite ADR-0014 tier 3 (Undisputed 3's
///                        "commitment has a cost" framing) if challenged.
/// </summary>
public sealed class StepBack : CommittedMove
{
    /// <summary>Default step-back frame data. Tunable per instance if needed.</summary>
    public static readonly MoveFrameData DefaultFrameData =
        new(startupFrames: 7, activeFrames: 4, recoveryFrames: 8, feintWindowFrames: 0);

    /// <summary>
    /// #253 cradle-race fix: the client's own zero-latency
    /// <c>GetBall()?.State == BallState.Dribbling</c> captured at THIS move's
    /// Begin, carried to the Active-entry gather so
    /// <see cref="Hooper.Ball.BallController.CradleForShotStartup"/> can resolve
    /// the same Reliable-Begin-overtakes-UnreliableOrdered-drive race #225 fixed
    /// for the Begin-time cradle-family moves (JumpShot/Layup/DriveGather/
    /// EuroStep). Those consume the boolean SYNCHRONOUSLY as a BeginCommittedMove
    /// parameter because they cradle at Begin; StepBack is deliberately exempt
    /// from that Begin-time cradle (it cradles at ACTIVE-entry — see the
    /// PlayerController.TickCommittedMoveBehavior StepBack branch), so the value
    /// must instead SURVIVE the several-tick Begin→Active-entry gap on the move
    /// instance itself — the same "payload known at Begin, consumed at
    /// Active-entry" idiom as <see cref="Spin.SpinDirection"/> /
    /// <see cref="Crossover.BurstDirection"/>.
    ///
    /// COUPLING NOTE: this value is NOT in the ReceiveState broadcast payload
    /// (which carries only the moveId, not per-move booleans). It survives
    /// reconciliation only because no reconcile path rebuilds an in-flight move
    /// FROM its moveId — the two ForceState callers either abort the move
    /// (move: null) or reuse the SAME _machine.CurrentMove instance (preserving
    /// this field). ApplyRequestedMove is the ONLY site that reconstructs a
    /// StepBack, and it does so server-side WITH the RPC-supplied value. If a
    /// future reconcile path is ever changed to rebuild a live move from its
    /// broadcast moveId, this boolean would silently reset to false and the
    /// client-own Active-entry cradle would lose it — thread it through the
    /// broadcast then, or this fix regresses.
    /// </summary>
    public bool ClientWasAlreadyDribbling { get; }

    /// <param name="clientWasAlreadyDribbling">
    /// See <see cref="ClientWasAlreadyDribbling"/>. Defaults false: every caller
    /// that isn't a real client Begin (or its server-side RPC reconstruction)
    /// has nothing to report and leaves the gather's normal Dribbling-guard to
    /// decide, exactly as before this fix.
    /// </param>
    /// <param name="frameData">Override frame data for tuning. Null uses <see cref="DefaultFrameData"/>.</param>
    public StepBack(bool clientWasAlreadyDribbling = false, MoveFrameData? frameData = null)
        : base(id: "stepback", displayName: "Step-Back", frameData: frameData ?? DefaultFrameData)
    {
        ClientWasAlreadyDribbling = clientWasAlreadyDribbling;
    }
}
