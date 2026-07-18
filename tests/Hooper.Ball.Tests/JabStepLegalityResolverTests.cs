using Hooper.Ball;
using Hooper.Moves;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for JabStepLegalityResolver — the pure "can a JabStep legally
/// begin" gate (issue #200), extracted so it is verified without a running
/// Godot instance.
///
/// ── Test naming ──────────────────────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// </summary>
public class JabStepLegalityResolverTests
{
    [Fact]
    public void IsLegal_Held_True()
    {
        // Held covers BOTH live and dead Held — HasDribbled (which
        // distinguishes them) lives on BallController, not BallState, and the
        // jab does not care which. This single case stands in for both:
        // BallState carries no separate value for "dead" Held.
        Assert.True(JabStepLegalityResolver.IsLegal(BallState.Held));
    }

    [Fact]
    public void IsLegal_Dribbling_False()
    {
        // Not legal while Dribbling — Hesitation/hand-fake (#86) already
        // covers the equivalent bait off a live dribble.
        Assert.False(JabStepLegalityResolver.IsLegal(BallState.Dribbling));
    }

    [Fact]
    public void IsLegal_Loose_True()
    {
        // Not a state the gate is designed to police (a jab always runs
        // behind PlayerController's IsBallHolder check, so Loose/InFlight are
        // unreachable in practice) — included so the predicate's only carved-
        // out illegal state is Dribbling, not "anything but Held".
        Assert.True(JabStepLegalityResolver.IsLegal(BallState.Loose));
    }

    [Fact]
    public void IsLegal_InFlight_True()
    {
        Assert.True(JabStepLegalityResolver.IsLegal(BallState.InFlight));
    }

    [Fact]
    public void IsLegal_NullBallState_True()
    {
        // No ball reference resolvable (e.g. GetBall() returned null) is
        // treated as legal — this predicate only ever runs behind
        // PlayerController's own IsBallHolder access check, so a null state
        // here is not a real bypass (see the class doc).
        Assert.True(JabStepLegalityResolver.IsLegal(null));
    }
}
