namespace Hooper.Moves;

/// <summary>
/// The four phases of any committed move.
///
/// IMPORTANT: transitions are NEVER implicit. The CommittedMoveMachine requires
/// an explicit Tick() call to advance phases. If you add a new phase, add
/// explicit transition logic in CommittedMoveMachine.Tick() — do not let the
/// phase drift on its own.
///
///   Inactive  — no committed move is running; the player is in the neutral
///               analog game (left-stick movement). This is the default state.
///
///   Startup   — the move has been committed; it is in its wind-up frames.
///               Movement is locked — these frames must remain readable to the
///               opponent as a fair telegraph (ADR-0003 / frame legibility rule).
///               A feint may abort during this phase (if within the feint window).
///
///   Active    — the move's effect fires: e.g. the lateral separation burst of a
///               crossover. Exactly one thing changes in the world each time
///               JustEnteredActive is true. Cannot be cancelled.
///
///   Recovery  — the move is cooling down. Movement is locked; the player cannot
///               immediately re-input another committed move. This is the punish
///               window (ADR-0003: wrong reads are punished). No cancel possible.
/// </summary>
public enum MovePhase
{
    Inactive,
    Startup,
    Active,
    Recovery,
}
