using Godot;
using Hooper.Player;

namespace Hooper.Ball.Tests;

public class PostMathTests
{
    private static PostMath.PostStep Step(float yaw, Vector2 away,
        HeadingMath.PivotState pivot = default, double delta = 1.0 / 60.0) =>
        PostMath.Step(yaw, pivot, away, delta, 180f, 900f, .95f, 90f, 2f);

    [Fact]
    public void Entry_SideFacing_RotatesAtBoundedRateAndPlants()
    {
        var result = Step(0f, new Vector2(1f, 0f));
        Assert.InRange(result.Heading.NewYaw, .049f, .053f);
        Assert.False(result.Established);
        Assert.False(result.TranslationAllowed);
        Assert.False(result.Heading.Pivot.HasLatch);
    }

    [Fact]
    public void Entry_OwedPivotCompletesWithoutPostRotationOrTranslationOnCompletionTick()
    {
        var pivot = new HeadingMath.PivotState(true, .01f);
        var result = Step(0f, new Vector2(1f, 0f), pivot);
        var ordinary = HeadingMath.Step(0f, pivot, Vector2.Zero, 1.0 / 60.0, 900f, .95f, 90f);
        Assert.Equal(ordinary, result.Heading);
        Assert.False(result.Established);
        Assert.False(result.TranslationAllowed);
        Assert.False(result.Heading.Pivot.HasLatch);
    }

    [Fact]
    public void Entry_ZeroRimDirection_HoldsFiniteHeadingAndAllowsTranslation()
    {
        var result = Step(1.23f, Vector2.Zero);
        Assert.Equal(1.23f, result.Heading.NewYaw);
        Assert.True(float.IsFinite(result.Heading.NewYaw));
        Assert.True(result.Established);
        Assert.True(result.TranslationAllowed);
    }

    [Fact]
    public void Turnaround_UsesShortestBoundedArcAcrossWrap()
    {
        float current = 170f * MathF.PI / 180f;
        float target = -170f * MathF.PI / 180f;
        float result = PostMath.StepTurn(current, target, 1.0 / 60.0, 360f);
        Assert.Equal(176f * MathF.PI / 180f, result, precision: 5);
    }

    [Fact]
    public void Turnaround_ExactOppositeRepresentationsChoosePositiveArc()
    {
        float tick = 1f / 60f;
        float positive = PostMath.StepTurn(0f, MathF.PI, tick, 360f);
        float negative = PostMath.StepTurn(0f, -MathF.PI, tick, 360f);
        float wrapped = PostMath.StepTurn(.7f, .7f - MathF.PI, tick, 360f) - .7f;
        Assert.Equal(positive, negative);
        Assert.Equal(positive, wrapped, precision: 5);
        Assert.True(positive > 0f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(90f)]
    [InlineData(180f)]
    public void Entry_FrontSideOrBack_ConvergesWithoutSnapOrCreatingPivot(float targetDeg)
    {
        float target = targetDeg * MathF.PI / 180f;
        Vector2 away = HeadingMath.Forward(target);
        float yaw = 0f;
        bool established = false;
        for (int tick = 0; tick < 90; tick++)
        {
            var result = Step(yaw, away);
            float distance = MathF.Abs(MathF.IEEERemainder(result.Heading.NewYaw - yaw, 2f * MathF.PI));
            Assert.InRange(distance, 0f, 3f * MathF.PI / 180f + 1e-6f);
            Assert.False(result.Heading.Pivot.HasLatch);
            Assert.Equal(result.Established, result.TranslationAllowed);
            yaw = result.Heading.NewYaw;
            established = result.Established;
        }
        Assert.True(established);
        Assert.InRange(MathF.Abs(MathF.IEEERemainder(yaw - target, 2f * MathF.PI)), 0f, 1e-5f);
    }

    [Fact]
    public void Entry_AlreadyFacingAwayWithoutMovementInput_IsEstablished()
    {
        var result = Step(.7f, HeadingMath.Forward(.7f));
        Assert.Equal(.7f, result.Heading.NewYaw, precision: 5);
        Assert.True(result.TranslationAllowed);
        Assert.True(result.Established);
    }

    [Fact]
    public void Entry_OwedPivotStillInProgress_PreservesTargetAtOrdinaryRate()
    {
        var pivot = new HeadingMath.PivotState(true, MathF.PI);
        float yaw = 0f;
        int owedTicks = 0;
        while (pivot.HasLatch && owedTicks < 30)
        {
            // Change the post target each tick; none may redirect the debt.
            var away = HeadingMath.Forward(owedTicks % 2 == 0 ? .4f : -.4f);
            var ordinary = HeadingMath.Step(yaw, pivot, Vector2.Zero, 1.0 / 60.0, 900f, .95f, 90f);
            var result = Step(yaw, away, pivot);
            Assert.Equal(ordinary, result.Heading);
            Assert.False(result.Established);
            Assert.False(result.TranslationAllowed);
            yaw = result.Heading.NewYaw;
            pivot = result.Heading.Pivot;
            owedTicks++;
        }
        Assert.InRange(owedTicks, 2, 29);
        Assert.False(pivot.HasLatch);
    }

    [Fact]
    public void Entry_ToleranceAllowsTranslationWithoutSnappingHeading()
    {
        float target = 1f * MathF.PI / 180f;
        var result = Step(0f, HeadingMath.Forward(target), delta: 0.0);
        Assert.Equal(0f, result.Heading.NewYaw);
        Assert.True(result.Established);
    }

    [Theory]
    [InlineData(-90f)]
    [InlineData(90f)]
    [InlineData(-180f)]
    [InlineData(180f)]
    public void Turnaround_CompletesByThirtyTicksAndHoldsFixedTarget(float targetDeg)
    {
        float target = targetDeg * MathF.PI / 180f;
        float yaw = 0f;
        for (int tick = 0; tick < 48; tick++)
        {
            float next = PostMath.StepTurn(yaw, target, 1.0 / 60.0, 360f);
            Assert.InRange(MathF.Abs(MathF.IEEERemainder(next - yaw, 2f * MathF.PI)),
                0f, 6f * MathF.PI / 180f + 1e-6f);
            yaw = next;
            if (tick >= 29)
                Assert.InRange(MathF.Abs(MathF.IEEERemainder(yaw - target, 2f * MathF.PI)), 0f, 1e-5f);
        }
    }
}
