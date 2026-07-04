namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for DeadDribbleRule — the pure decision behind #193's dead-
/// dribble rule (ADR-0008 amendment). Mirrors OobResolution/ClearLine's test
/// style: the rule itself is a single boolean predicate, so these tests pin
/// both branches plus the naming of the flag it reads.
/// </summary>
public class DeadDribbleRuleTests
{
    [Fact]
    public void CanStartDribble_LiveDribble_ReturnsTrue()
    {
        // hasDribbled=false models a fresh possession's live dribble (or one
        // that has never been cradled yet) — StartDribble must be legal.
        Assert.True(DeadDribbleRule.CanStartDribble(hasDribbled: false));
    }

    [Fact]
    public void CanStartDribble_DeadDribble_ReturnsFalse()
    {
        // hasDribbled=true models a possession whose dribble was already
        // cradled (a jump shot / pump-fake Startup) — StartDribble is refused
        // for the rest of this possession, real ball's "can't dribble again
        // once you've picked it up" rule.
        Assert.False(DeadDribbleRule.CanStartDribble(hasDribbled: true));
    }
}
