using Godot;
using Hooper.Moves;
using Hooper.Player;

namespace Hooper.Ball.Tests;

/// <summary>
/// Pure composition tests for the M9 crossover/hesi disambiguation + swap
/// (issues #84/#85/#86, ADR-0012). These pin the integration CONTRACT that
/// PlayerController.SampleMoveInput + TickCommittedMoveBehavior implement, at
/// the pure level — PlayerController itself extends a Godot Node and cannot be
/// instantiated headlessly, but the decision logic it calls
/// (HandStateResolver) can, so the behaviour it composes is verified here.
///
/// The integration does, per flick:
///   1. classify via HandStateResolver.IsCrossover(hand, flickSign);
///   2. if crossover → swap the ball to the opposite hand (Opposite) and burst
///      in the heading-relative world direction (BurstWorldDir);
///   3. if not → a hesitation: hand unchanged, no burst.
/// </summary>
public class CrossoverHesiSequenceTests
{
    /// <summary>
    /// Mirrors the integration's per-flick resolution: returns the hand AFTER
    /// the flick resolves, and reports whether it was a crossover (vs a hesi).
    /// A crossover swaps; a hesitation leaves the hand untouched.
    /// </summary>
    private static HandSide ResolveFlick(HandSide hand, int flickSign, out bool wasCrossover)
    {
        wasCrossover = HandStateResolver.IsCrossover(hand, flickSign);
        return wasCrossover ? HandStateResolver.Opposite(hand) : hand;
    }

    [Fact]
    public void BallInLeft_FlickRight_CrossesAndSwapsToRight()
    {
        // Flick toward the empty (right) hand → crossover + swap. (#84)
        HandSide result = ResolveFlick(HandSide.Left, +1, out bool wasCrossover);

        Assert.True(wasCrossover);
        Assert.Equal(HandSide.Right, result);
    }

    [Fact]
    public void BallInRight_FlickRight_IsHesitationNoSwap()
    {
        // Same physical flick, but now toward the ball (right) hand → hesitation:
        // no swap, the hand stays put. This is the disambiguation #86 adds. (#84/#86)
        HandSide result = ResolveFlick(HandSide.Right, +1, out bool wasCrossover);

        Assert.False(wasCrossover);
        Assert.Equal(HandSide.Right, result);
    }

    [Fact]
    public void RepeatedRightFlicks_DoNotCrossTwice_ViaHandGate()
    {
        // The regression #84 fixes: under the OLD absolute mapping, crossing the
        // same way twice left the ball in the same hand silently. Now the hand
        // gate makes the SECOND same-direction flick a hesitation, not a no-op
        // crossover — you cannot cross the same way twice in a row.
        HandSide hand = HandSide.Left;

        hand = ResolveFlick(hand, +1, out bool firstCross);
        Assert.True(firstCross);                 // Left + flick-right → crossover
        Assert.Equal(HandSide.Right, hand);

        hand = ResolveFlick(hand, +1, out bool secondCross);
        Assert.False(secondCross);               // Right + flick-right → hesitation
        Assert.Equal(HandSide.Right, hand);      // hand unchanged
    }

    [Fact]
    public void AlternatingFlicks_CrossEveryTime()
    {
        // Flicking toward the empty hand each time (alternating direction) keeps
        // crossing — the ball ping-pongs Left↔Right as a real crossover sequence.
        HandSide hand = HandSide.Left;

        hand = ResolveFlick(hand, +1, out bool c1); // Left  → Right
        hand = ResolveFlick(hand, -1, out bool c2); // Right → Left
        hand = ResolveFlick(hand, +1, out bool c3); // Left  → Right

        Assert.True(c1 && c2 && c3);
        Assert.Equal(HandSide.Right, hand);
    }

    [Theory]
    // Facing heading 0 (faces +Z): the crossover burst goes toward the world
    // side the flick points to. The sign is hitl visual sign-off; here we pin
    // the INTERNAL consistency — a crossover always bursts toward the empty
    // hand it swaps into.
    [InlineData(+1)] // ball Left, flick right → burst world +X
    [InlineData(-1)] // ball Right, flick left → burst world -X
    public void CrossoverBurst_AtHeadingZero_PointsTowardFlickSide(int flickSign)
    {
        HandSide hand = flickSign > 0 ? HandSide.Left : HandSide.Right;
        Assert.True(HandStateResolver.IsCrossover(hand, flickSign)); // precondition: this flick crosses

        Vector2 burst = HandStateResolver.BurstWorldDir(0f, flickSign);

        Assert.Equal(flickSign, MathF.Sign(burst.X));
        Assert.True(MathF.Abs(burst.Y) < 1e-5f); // purely lateral at heading 0
    }
}
