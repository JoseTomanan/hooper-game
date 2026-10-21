using Godot;
using Hooper.Player;

namespace Hooper.Ball.Tests;

public class MovingCrossoverLateralTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void HeldForwardMovingCross_StepsTowardEmptyHandAndKeepsDrive(int sign)
    {
        Vector3 result = CrossoverBurstMath.ComposeCrossoverActiveVelocity(
            new Vector3(0, 0, 2), 0f, sign, new Vector2(0, 1), 9f, 9f, 0.15f);

        Assert.True(-sign * result.X >= 4f, $"missing lateral step: {result}");
        Assert.Equal(11f, result.Z, precision: 4);
    }
}
