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

    public static IEnumerable<object[]> Orientations()
    {
        foreach (float heading in new[] { 0f, MathF.PI / 2f, MathF.PI })
        foreach (int sign in new[] { -1, 1 })
            yield return new object[] { heading, sign };
    }

    [Theory]
    [MemberData(nameof(Orientations))]
    public void ForwardAndReleasedExits_KeepMomentumAndBoundedCutInEitherHand(float heading, int sign)
    {
        Vector2 forward = Forward(heading);
        Vector2 flickSide = Right(heading) * sign;
        foreach (float survivorSpeed in new[] { 2f, 5f })
        foreach (bool released in new[] { false, true })
        {
            Vector3 result = Compose(forward * survivorSpeed, heading, sign,
                released ? Vector2.Zero : forward);

            AssertClose(4.5f, Horizontal(result).Dot(flickSide));
            AssertClose(survivorSpeed + (released ? 0f : 9f), Horizontal(result).Dot(forward));
            Assert.Equal(0f, result.Y);
        }
    }

    [Theory]
    [MemberData(nameof(Orientations))]
    public void StationaryCross_AllExitKindsRemainExactlyLegacy(float heading, int sign)
    {
        Vector2 forward = Forward(heading);
        Vector2 right = Right(heading);
        foreach (Vector2 exit in new[]
        {
            Vector2.Zero, forward, right, (forward + right).Normalized(),
            (forward - right).Normalized(), -forward,
        })
        {
            Assert.Equal(Legacy(Vector2.Zero, heading, sign, exit),
                Compose(Vector2.Zero, heading, sign, exit));
        }
    }

    [Theory]
    [MemberData(nameof(Orientations))]
    public void MovingDiagonalExits_RetainLegacySteering(float heading, int sign)
    {
        Vector2 forward = Forward(heading);
        Vector2 right = Right(heading);
        foreach (int exitSide in new[] { -1, 1 })
        {
            Vector2 exit = (forward + right * exitSide).Normalized();
            Vector3 legacy = Legacy(forward * 2f, heading, sign, exit);
            Vector3 result = Compose(forward * 2f, heading, sign, exit);

            Assert.True(result.DistanceTo(legacy) < 0.00001f,
                $"diagonal exit changed: legacy={legacy}, result={result}");
            Assert.True(Horizontal(result).Dot(right) * exitSide > 6f,
                "diagonal steering must still produce its own lateral exit");
        }
    }

    [Fact]
    public void Composition_IsIndependentOfInterveningCalls()
    {
        Vector2 survivor = new(0f, 2f);
        Vector2 exit = new(0f, 1f);
        Vector3 first = Compose(survivor, 0f, 1, exit);
        AssertClose(4.5f, -first.X);
        AssertClose(11f, first.Z);

        Compose(new Vector2(5f, 0f), MathF.PI / 2f, -1, Vector2.Zero);
        Legacy(Vector2.Zero, MathF.PI, -1, new Vector2(0f, -1f));

        Assert.Equal(first, Compose(survivor, 0f, 1, exit));
    }

    [Theory]
    [InlineData(false, 0f)]
    [InlineData(true, 0f)]
    [InlineData(false, 2f)]
    [InlineData(true, 2f)]
    public void SurvivorSpeedBlend_IsContinuousAtStationaryAndSaturation(bool released, float speed)
    {
        const float epsilon = 0.00001f;
        Vector2 exit = released ? Vector2.Zero : new Vector2(0f, 1f);
        Vector3 center = Compose(new Vector2(0f, speed), 0f, 1, exit);
        Vector3 above = Compose(new Vector2(0f, speed + epsilon), 0f, 1, exit);
        Assert.True(above.DistanceTo(center) < 0.001f);
        if (speed > 0f)
        {
            Vector3 below = Compose(new Vector2(0f, speed - epsilon), 0f, 1, exit);
            Assert.True(below.DistanceTo(center) < 0.001f);
        }
    }

    [Theory]
    [InlineData(1, 30f)]
    [InlineData(-1, 30f)]
    [InlineData(1, -30f)]
    [InlineData(-1, -30f)]
    [InlineData(1, -14.3f)]
    [InlineData(-1, -14.3f)]
    public void SteeringBlend_IsContinuousAtFadeBoundaryAndCountersteeringCancellation(int sign, float degrees)
    {
        const float heading = MathF.PI / 2f;
        const float epsilon = 0.00001f;
        Vector2 forward = Forward(heading);
        Vector2 flickSide = Right(heading) * sign;
        float angle = degrees * MathF.PI / 180f;
        Vector2 Exit(float radians) => forward * MathF.Cos(radians) + flickSide * MathF.Sin(radians);
        Vector3 center = Compose(forward * 2f, heading, sign, Exit(angle));
        Vector3 above = Compose(forward * 2f, heading, sign, Exit(angle + epsilon));
        Vector3 below = Compose(forward * 2f, heading, sign, Exit(angle - epsilon));

        Assert.True(above.DistanceTo(center) < 0.001f);
        Assert.True(below.DistanceTo(center) < 0.001f);
        if (degrees == -14.3f)
        {
            // Opposite steering may cancel the cut; the accepted contract is
            // continuity, not a universal nonzero displacement in flick direction.
            Assert.InRange(MathF.Abs(Horizontal(center).Dot(flickSide)), 0f, 0.05f);
            Assert.True(Horizontal(center).Dot(forward) > 10f);
        }
    }

    [Theory]
    [InlineData(5f)]
    [InlineData(10f)]
    [InlineData(30f)]
    public void HighSprintSurvivors_CannotAmplifyCutOrChangeForwardComposition(float speed)
    {
        const float heading = MathF.PI / 2f;
        Vector2 forward = Forward(heading);
        Vector2 right = Right(heading);
        foreach (int sign in new[] { -1, 1 })
        foreach (Vector2 exit in new[]
        {
            Vector2.Zero, forward, -forward,
            forward * MathF.Cos(MathF.PI / 12f) + right * MathF.Sin(MathF.PI / 12f),
            forward * MathF.Cos(MathF.PI / 12f) - right * MathF.Sin(MathF.PI / 12f),
            (forward + right).Normalized(), (forward - right).Normalized(),
        })
        {
            Vector3 legacy = Legacy(forward * speed, heading, sign, exit);
            Vector3 result = Compose(forward * speed, heading, sign, exit);
            Vector2 extra = Horizontal(result - legacy);

            Assert.InRange(extra.Length(), 0f, 4.5001f);
            Assert.InRange(extra.Dot(right) * sign, -0.00001f, 4.5001f);
            AssertClose(Horizontal(legacy).Dot(forward), Horizontal(result).Dot(forward));
        }
    }

    private static Vector2 Forward(float heading) => new(MathF.Sin(heading), MathF.Cos(heading));
    private static Vector2 Right(float heading) => new(-MathF.Cos(heading), MathF.Sin(heading));
    private static Vector2 Horizontal(Vector3 value) => new(value.X, value.Z);

    private static Vector3 Compose(Vector2 survivor, float heading, int sign, Vector2 exit) =>
        CrossoverBurstMath.ComposeCrossoverActiveVelocity(
            new Vector3(survivor.X, 0f, survivor.Y), heading, sign, exit, 9f, 9f, 0.15f);

    private static Vector3 Legacy(Vector2 survivor, float heading, int sign, Vector2 exit) =>
        CrossoverBurstMath.ComposeActiveVelocity(
            new Vector3(survivor.X, 0f, survivor.Y), heading, sign, exit, 9f, 9f, 0.15f);

    private static void AssertClose(float expected, float actual) =>
        Assert.InRange(MathF.Abs(actual - expected), 0f, 0.00001f);
}
