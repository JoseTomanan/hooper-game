using Godot;
using Hooper.Moves;

namespace Hooper.Ball.Tests;

// Issue #231 — the euro-step's input read: does a drive input carry a lateral
// read, and to which side. Body axis fixed to heading 0's right axis
// (BurstWorldDir(0, +1) == (-1,0)), so a world -X push is "player's right".
public class EuroStepReadResolverTests
{
    private static readonly Vector2 RightAxisAtHeading0 = new(-1f, 0f);
    private const float Deadzone = 0.15f;

    [Fact]
    public void ForwardPush_NoLateralRead_IsStraightDriveGather()
    {
        // Pure forward push (world +Z) has zero lateral component.
        int sign = EuroStepReadResolver.ResolveLateralSign(
            stick: new Vector2(0f, 1f), bodyRightAxis: RightAxisAtHeading0, deadzone: Deadzone);

        Assert.Equal(0, sign);
    }

    [Fact]
    public void PushToPlayersRight_ResolvesRightRead()
    {
        // World -X is the player's right at heading 0 (matches BurstWorldDir).
        int sign = EuroStepReadResolver.ResolveLateralSign(
            stick: new Vector2(-1f, 0f), bodyRightAxis: RightAxisAtHeading0, deadzone: Deadzone);

        Assert.Equal(+1, sign);
    }

    [Fact]
    public void PushToPlayersLeft_ResolvesLeftRead()
    {
        int sign = EuroStepReadResolver.ResolveLateralSign(
            stick: new Vector2(1f, 0f), bodyRightAxis: RightAxisAtHeading0, deadzone: Deadzone);

        Assert.Equal(-1, sign);
    }

    // A lateral tilt at EXACTLY the deadzone counts as neutral (strict-greater
    // gate) — guards the off-by-one flip at the boundary, matching the
    // crossover exit deadzone / LayupRangeResolver conventions.
    [Fact]
    public void LateralTiltAtExactlyDeadzone_CountsAsNeutral()
    {
        int sign = EuroStepReadResolver.ResolveLateralSign(
            stick: new Vector2(-Deadzone, 0f), bodyRightAxis: RightAxisAtHeading0, deadzone: Deadzone);

        Assert.Equal(0, sign);
    }

    // A diagonal drive (forward + lateral past the deadzone) still resolves a
    // lateral read — a euro-step can begin from a moving diagonal drive, not
    // only a pure sideways push.
    [Fact]
    public void DiagonalPush_PastDeadzone_ResolvesLateralRead()
    {
        int sign = EuroStepReadResolver.ResolveLateralSign(
            stick: new Vector2(-0.7f, 0.7f), bodyRightAxis: RightAxisAtHeading0, deadzone: Deadzone);

        Assert.Equal(+1, sign);
    }
}
