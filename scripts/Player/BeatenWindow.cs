#nullable enable

namespace Hooper.Player;

/// <summary>
/// Pure, engine-free tracking of a defender's "beaten" window — the
/// whiff-punish blow-by lane (issue #100, ADR-0018 Amendment 2026-07-16).
///
/// A defender who was just beaten by a whiffed defensive committed move (a
/// failed steal, the first caller) cannot contest the handler's next burst
/// for a bounded number of physics ticks: both the committed
/// <c>ContestMove</c> factor (#99) and the passive proximity scatter factor
/// (ADR-0009 / #65) are suppressed against that defender while the window is
/// open. This is the offense's REWARD for a defender's wrong read — distinct
/// from the defender's OWN Recovery frames, which already gate how soon they
/// can act again (Recovery is the defender's cost; this window is the
/// offense's payoff, ADR-0018 §3's reaction tilt made concrete).
///
/// Deliberately generic over WHO/WHY triggered it. This struct only knows
/// "beaten until tick N" — it has no idea a steal exists. The trigger call
/// (<see cref="Trigger"/>) is reusable by any future defensive-whiff caller;
/// issue #196 (a crossover-transit steal whiff) is the first planned reuse,
/// and is expected to call the exact same API this issue wires up on
/// PlayerController, not a parallel mechanism.
///
/// Ticks, never wall-clock time (ADR-0004 determinism invariant) — the
/// caller passes the current physics tick (<c>BallController.PhysicsTick</c>,
/// the project's single source for "what tick is it") and a tick-count
/// window length; this struct never reads engine time itself.
/// </summary>
public readonly record struct BeatenWindow(int UntilTick)
{
    /// <summary>No active beaten window — the default, inert state.</summary>
    public static readonly BeatenWindow None = new(int.MinValue);

    /// <summary>
    /// True while <paramref name="currentTick"/> is still inside the window.
    /// Half-open like every other tick interval in this codebase
    /// (ADR-0018 §1): the window covers <c>[triggerTick, triggerTick +
    /// windowTicks)</c>, so the tick a window was triggered ON counts as
    /// beaten, and the tick it expires on does not.
    ///
    /// Deliberately has no LOWER bound check (only <see cref="UntilTick"/> is
    /// stored, not the trigger tick) — every real caller queries with the
    /// current, monotonically-increasing physics tick (ADR-0004: no
    /// wall-clock timers, ticks only ever move forward), so a query "before"
    /// the trigger tick can never actually occur in production. Not a
    /// truncated interval; a narrower one was never needed.
    /// </summary>
    public bool IsActive(int currentTick) => currentTick < UntilTick;

    /// <summary>
    /// Starts (or restarts) a beaten window of <paramref name="windowTicks"/>
    /// ticks beginning at <paramref name="currentTick"/> (inclusive). A
    /// second trigger while one is already active simply overwrites it with
    /// a fresh window from now — there is no stacking, matching every other
    /// discrete state in this codebase (force-set, never accumulated).
    /// </summary>
    public static BeatenWindow Trigger(int currentTick, int windowTicks) =>
        new(currentTick + windowTicks);
}
