using Hooper.Player;

namespace Hooper.Ball.Tests;

// #100 — the whiff-punish blow-by lane's pure clock (ADR-0018 Amendment
// 2026-07-16). BeatenWindow is deliberately dumb: it only knows "beaten
// until tick N" and has no idea WHY (a failed steal today; #196's failed
// transit steal tomorrow) — these tests pin exactly that narrow contract:
// trigger sets a window, the window is active for its whole span, and it
// expires deterministically on a pure tick comparison (ADR-0004 — no
// wall-clock timer anywhere in this codebase's ball/committed-move math).
public class BeatenWindowTests
{
    [Fact]
    public void None_IsNeverActive()
    {
        // The default/inert value must not accidentally read as "beaten" at
        // any tick a caller might plausibly pass, including tick 0 and
        // negative ticks (a caller that hasn't started counting yet).
        Assert.False(BeatenWindow.None.IsActive(0));
        Assert.False(BeatenWindow.None.IsActive(1_000_000));
        Assert.False(BeatenWindow.None.IsActive(-1));
    }

    [Fact]
    public void Trigger_IsActive_OnTheTriggeringTick()
    {
        // Half-open [triggerTick, triggerTick + windowTicks): the tick the
        // window was triggered ON counts as beaten — the caller (BallController.
        // ResolveBeatenWindowTriggers) fires this the SAME tick the whiff is
        // detected, and any shot resolved that same tick must still see the
        // suppression.
        BeatenWindow window = BeatenWindow.Trigger(currentTick: 100, windowTicks: 20);
        Assert.True(window.IsActive(100));
    }

    [Fact]
    public void Trigger_IsActive_ThroughoutTheWindow()
    {
        BeatenWindow window = BeatenWindow.Trigger(currentTick: 100, windowTicks: 20);

        // Active for every tick in [100, 120) — spot-check the interior, not
        // just the boundary, since a fencepost bug could pass a boundary-only
        // check while being wrong for most of the window.
        Assert.True(window.IsActive(100));
        Assert.True(window.IsActive(110));
        Assert.True(window.IsActive(119));
    }

    [Fact]
    public void Trigger_ExpiresExactlyOnTheBoundaryTick()
    {
        // Half-open: the tick the window's length runs out on (100 + 20 = 120)
        // is NOT active — matches every other tick interval in this codebase
        // (ADR-0018 §1's Succeeds predicate uses the same half-open convention).
        BeatenWindow window = BeatenWindow.Trigger(currentTick: 100, windowTicks: 20);
        Assert.False(window.IsActive(120));
        Assert.False(window.IsActive(121));
    }

    [Fact]
    public void Trigger_TwiceInARow_OverwritesRatherThanStacks()
    {
        // No stacking/accumulation (mirrors this codebase's "SET, never
        // accumulated" discipline for _smoothOffset elsewhere) — a second
        // trigger while one is already active simply restarts the clock from
        // the new tick, it does not extend the old window's end by addition.
        BeatenWindow first  = BeatenWindow.Trigger(currentTick: 100, windowTicks: 20); // until 120
        BeatenWindow second = BeatenWindow.Trigger(currentTick: 110, windowTicks: 20); // until 130, not 140

        Assert.Equal(130, second.UntilTick);
        Assert.NotEqual(first.UntilTick + 20, second.UntilTick);
    }

    [Fact]
    public void Trigger_ZeroWindowTicks_IsNeverActive()
    {
        // A degenerate zero-length window is legal input (windowTicks isn't
        // validated here — the export's own [Export] default is what's
        // trusted to be sane) and must simply never read as active, not
        // throw or behave undefined.
        BeatenWindow window = BeatenWindow.Trigger(currentTick: 100, windowTicks: 0);
        Assert.False(window.IsActive(100));
    }
}
