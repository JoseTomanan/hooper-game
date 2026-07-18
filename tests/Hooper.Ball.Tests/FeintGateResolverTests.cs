using Hooper.Moves;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for FeintGateResolver — the pure "does this tick's feint input
/// actually call CommittedMoveMachine.Feint()" decision.
///
/// Bug fix (/diagnose, 2026-07-03): a jump shot's windup animation played
/// every time, but the ball sometimes never left the hand. Root cause: the
/// shared right-stick "Pro Stick" gesture recognizes a Feint from ANY quick
/// aim-stick flick-and-return (RightStickGestureRecognizer), regardless of
/// which committed move is running. PlayerController.SampleMoveInput composed
/// that gesture with the explicit "move_feint" press into a single
/// unconditional feintInput and called CommittedMoveMachine.Feint() with it —
/// for EVERY move, including JumpShot. JumpShot's pump-fake (#77) silently
/// consumes the shot: Feint() routes Startup -> Recovery without ever setting
/// JustEnteredActive, so the release never fires, while the windup animation
/// (driven by Phase alone — see MoveAnimResolver) plays regardless. An
/// incidental aim-stick flick while shooting therefore read as "I pressed
/// shoot and nothing happened."
///
/// ── Test naming ──────────────────────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// </summary>
public class FeintGateResolverTests
{
    [Fact]
    public void ShouldFeint_ExplicitPressWithJumpShot_True()
    {
        Assert.True(FeintGateResolver.ShouldFeint(
            explicitFeintPressed: true, gestureKind: GestureKind.None, currentMove: new JumpShot()));
    }

    [Fact]
    public void ShouldFeint_ExplicitPressWithNoMove_True()
    {
        Assert.True(FeintGateResolver.ShouldFeint(
            explicitFeintPressed: true, gestureKind: GestureKind.None, currentMove: null));
    }

    [Fact]
    public void ShouldFeint_ExplicitPressTakesPriorityOverGestureKind()
    {
        // Explicit press must always work regardless of what the gesture
        // recognizer independently reports the same tick.
        Assert.True(FeintGateResolver.ShouldFeint(
            explicitFeintPressed: true, gestureKind: GestureKind.Crossover, currentMove: new JumpShot()));
    }

    [Fact]
    public void ShouldFeint_GestureQuickReturnWithCrossover_True()
    {
        // Unaffected by the fix: the shared gesture still free-aborts a
        // crossover exactly as before (FeintRecoveryFrames == 0 for Crossover).
        Assert.True(FeintGateResolver.ShouldFeint(
            explicitFeintPressed: false, gestureKind: GestureKind.QuickReturn, currentMove: new Crossover(1f)));
    }

    [Fact]
    public void ShouldFeint_GestureQuickReturnWithHesitation_True()
    {
        Assert.True(FeintGateResolver.ShouldFeint(
            explicitFeintPressed: false, gestureKind: GestureKind.QuickReturn, currentMove: new Hesitation()));
    }

    [Fact]
    public void ShouldFeint_GestureQuickReturnWithNoMove_True()
    {
        // No move running means Feint() will no-op regardless (Phase != Startup);
        // the gate must not special-case or throw on a null move.
        Assert.True(FeintGateResolver.ShouldFeint(
            explicitFeintPressed: false, gestureKind: GestureKind.QuickReturn, currentMove: null));
    }

    [Fact]
    public void ShouldFeint_GestureQuickReturnWithJumpShot_False()
    {
        // THE FIX: an ambiguous aim-stick gesture must never silently eat a
        // jump shot's ball release. Only the explicit "move_feint" action may
        // pump-fake a JumpShot.
        Assert.False(FeintGateResolver.ShouldFeint(
            explicitFeintPressed: false, gestureKind: GestureKind.QuickReturn, currentMove: new JumpShot()));
    }

    [Fact]
    public void ShouldFeint_NoneKindNoExplicit_False()
    {
        Assert.False(FeintGateResolver.ShouldFeint(
            explicitFeintPressed: false, gestureKind: GestureKind.None, currentMove: new JumpShot()));
    }

    [Fact]
    public void ShouldFeint_CrossoverGestureKindWithJumpShot_False()
    {
        // A completed Crossover gesture (not a Feint) must never itself feint
        // anything — covers the "wrong GestureKind" branch distinctly from
        // "no gesture at all".
        Assert.False(FeintGateResolver.ShouldFeint(
            explicitFeintPressed: false, gestureKind: GestureKind.Crossover, currentMove: new JumpShot()));
    }
}
