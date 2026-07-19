namespace Hooper.Player;

/// <summary>
/// The animation states a player mesh can display for the committed-move layer.
///
/// This is a DISPLAY-ONLY vocabulary (M7b, issue #41). It exists so the
/// AnimationTree can switch between distinct clips per committed-move phase,
/// making the startup → active → recovery commitment arc visible to both
/// players (the load-bearing requirement of ADR-0003).
///
/// It is intentionally a parallel enum to <see cref="Hooper.Moves.MovePhase"/>,
/// NOT a reuse of it. MovePhase is authoritative gameplay state owned by
/// CommittedMoveMachine; MoveAnimState is what the renderer shows. Keeping them
/// as separate types means a future presentation change (e.g. collapsing two
/// phases onto one clip, or adding a display-only flourish state) cannot
/// accidentally widen into the authoritative phase graph — the compiler keeps
/// the cosmetic layer downstream of gameplay (ADR-0004 cosmetic-only discipline).
///
///   Locomotion — no committed move is running (MovePhase.Inactive). The neutral
///                game; the actual idle↔run blend is driven separately by the
///                AnimationTree's velocity-fed BlendSpace1D, so this is a single
///                state here rather than distinct Idle/Run entries.
///
///   Startup    — the move's wind-up (MovePhase.Startup). The telegraph window
///                the opponent reads.
///
///   Active     — the move's effect frames (MovePhase.Active). The burst fires.
///
///   Recovery   — the move's cooldown (MovePhase.Recovery). The punish window.
///
///   Pivot      — the planted-feet in-place turn (issue #172's
///                IsPivotingInPlace, animated for issue #242/#184). Orthogonal
///                to the MovePhase-driven states above: it is driven by
///                HeadingMath's pivot latch, not by CommittedMoveMachine, and
///                only ever coincides with MovePhase.Inactive — beginning a
///                committed move clears any in-progress pivot latch (see
///                PivotPlantTest's committed-cancel scenario), so Pivot never
///                needs to out-rank Startup/Active/Recovery.
/// </summary>
public enum MoveAnimState
{
    Locomotion,
    Startup,
    Active,
    Recovery,
    Pivot,
}
