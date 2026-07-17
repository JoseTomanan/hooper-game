using Godot;
using Hooper.Moves;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for RightStickGestureRecognizer — the pure C# class that maps
/// right-stick Vector2 samples to GestureResult values.
///
/// All tests run headlessly (no Godot node required). The recognizer is fed
/// Vector2 samples directly, matching how PlayerInputGlue drives it each tick.
///
/// ── Test naming ──────────────────────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// Each test contains exactly one logical assertion.
///
/// ── Timing model ─────────────────────────────────────────────────────────────
/// On the first tick the stick crosses FlickThreshold we start counting.
/// The gesture fires when:
///   (a) Stick returns to deadzone within FeintWindowTicks → Feint fires.
///   (b) Stick stays above threshold for FeintWindowTicks+1 ticks → Crossover fires.
/// After firing, the recognizer waits for the stick to return to the deadzone
/// before it can fire again (debounce).
///
/// With defaults (FlickThreshold=0.6, DeadzoneRadius=0.2, FeintWindowTicks=4):
///   Crossover fires on the 5th consecutive tick above threshold.
///   Feint fires the tick the stick re-enters the deadzone (if within 4 ticks).
/// </summary>
public class RightStickGestureRecognizerTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Recognizer with default thresholds (flick=0.6, deadzone=0.2, feintWindow=4).</summary>
    private static RightStickGestureRecognizer NewRecognizer() =>
        new(flickThreshold: 0.6f, deadzoneRadius: 0.2f, feintWindowTicks: 4);

    /// <summary>
    /// Feeds the recognizer n ticks of the given stick value and returns the
    /// last GestureResult only. Used to advance state without caring about
    /// intermediate results.
    /// </summary>
    private static GestureResult SampleN(RightStickGestureRecognizer r, Vector2 stick, int n)
    {
        GestureResult last = GestureResult.None;
        for (int i = 0; i < n; i++)
            last = r.Sample(stick);
        return last;
    }

    /// <summary>
    /// Drives the recognizer to fire a Crossover (5 ticks of right flick at
    /// default settings) so subsequent tests can start from a fired state.
    /// </summary>
    private static void FireCrossover(RightStickGestureRecognizer r) =>
        SampleN(r, new Vector2(0.9f, 0f), 5); // 5 = FeintWindowTicks+1

    // ═════════════════════════════════════════════════════════════════════════
    // 1. No motion
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Sample_ZeroVector_ReturnsNone()
    {
        var r = NewRecognizer();
        GestureResult result = r.Sample(Vector2.Zero);
        Assert.Equal(GestureKind.None, result.Kind);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 2. Below threshold
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Sample_BelowThreshold_ReturnsNone()
    {
        // X=0.3 is below the default FlickThreshold of 0.6.
        var r = NewRecognizer();
        GestureResult result = r.Sample(new Vector2(0.3f, 0f));
        Assert.Equal(GestureKind.None, result.Kind);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 3. Exact threshold — >= check means it registers (but needs window to commit)
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Sample_ExactThreshold_EventuallyFiresCrossover()
    {
        // At exactly FlickThreshold the gesture timing starts. After
        // FeintWindowTicks+1 ticks at the threshold a Crossover fires.
        var r = NewRecognizer(); // FlickThreshold=0.6, FeintWindowTicks=4
        GestureResult result = SampleN(r, new Vector2(0.6f, 0f), 5);
        Assert.Equal(GestureKind.Crossover, result.Kind);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 4. Right flick — Direction = +1
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Sample_RightFlick_DirectionIsPositiveOne()
    {
        var r = NewRecognizer();
        GestureResult result = SampleN(r, new Vector2(0.9f, 0f), 5);
        Assert.Equal(1f, result.Direction);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 5. Left flick — Direction = -1
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Sample_LeftFlick_DirectionIsNegativeOne()
    {
        var r = NewRecognizer();
        GestureResult result = SampleN(r, new Vector2(-0.9f, 0f), 5);
        Assert.Equal(-1f, result.Direction);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 6. Timing: crossover fires on tick FeintWindowTicks+1, not before
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Sample_AtFeintWindowTicks_StillReturnsNone()
    {
        // On the 4th tick above threshold (== FeintWindowTicks) the crossover
        // window is still open — no gesture committed yet.
        var r = NewRecognizer();
        GestureResult result = SampleN(r, new Vector2(0.9f, 0f), 4);
        Assert.Equal(GestureKind.None, result.Kind);
    }

    [Fact]
    public void Sample_AtFeintWindowTicksPlusOne_FiresCrossover()
    {
        // On the 5th tick (FeintWindowTicks+1) the crossover commits.
        var r = NewRecognizer();
        GestureResult result = SampleN(r, new Vector2(0.9f, 0f), 5);
        Assert.Equal(GestureKind.Crossover, result.Kind);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 7. Debounce: after crossover fires, subsequent ticks return None
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Sample_StickHeldAfterCrossoverFires_ReturnsNone()
    {
        var r = NewRecognizer();
        FireCrossover(r);
        // Stick still held past threshold — must not re-fire.
        GestureResult result = r.Sample(new Vector2(0.9f, 0f));
        Assert.Equal(GestureKind.None, result.Kind);
    }

    [Fact]
    public void Sample_StickHeldPastThresholdManyTicks_NeverRefiresWithoutDeadzone()
    {
        var r = NewRecognizer();
        FireCrossover(r);
        for (int i = 0; i < 10; i++)
        {
            GestureResult result = r.Sample(new Vector2(0.9f, 0f));
            Assert.Equal(GestureKind.None, result.Kind);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 8. Debounce: re-arm after deadzone — fires again on next flick sequence
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Sample_ReturnToDeadzoneThenFlick_FiresAgain()
    {
        var r = NewRecognizer();
        FireCrossover(r);
        // Return to deadzone (stick.Length() < 0.2).
        r.Sample(Vector2.Zero);
        // Second crossover sequence — must fire after FeintWindowTicks+1 ticks.
        GestureResult second = SampleN(r, new Vector2(0.9f, 0f), 5);
        Assert.Equal(GestureKind.Crossover, second.Kind);
    }

    [Fact]
    public void Sample_ReturnToDeadzoneThenLeftFlick_DirectionIsNegativeOne()
    {
        var r = NewRecognizer();
        FireCrossover(r);
        // Return to deadzone.
        r.Sample(Vector2.Zero);
        // Second gesture to the left.
        GestureResult second = SampleN(r, new Vector2(-0.9f, 0f), 5);
        Assert.Equal(-1f, second.Direction);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 9. Debounce: does NOT re-arm while stick is in mid-zone (above deadzone,
    //    below threshold)
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Sample_MidZoneAfterFire_DoesNotRearm()
    {
        var r = NewRecognizer();
        FireCrossover(r);
        // Stick in mid-zone: above DeadzoneRadius (0.2) but below FlickThreshold (0.6).
        r.Sample(new Vector2(0.4f, 0f));
        // Flick again past threshold without having hit the deadzone first —
        // recognizer must stay locked, returning None.
        GestureResult result = SampleN(r, new Vector2(0.9f, 0f), 5);
        Assert.Equal(GestureKind.None, result.Kind);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 10. Vertical gestures — single-sample still returns None (window not
    //     closed on the very first tick, same as a fresh horizontal flick)
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Sample_PureVerticalPush_SingleTickReturnsNone()
    {
        // X=0, Y=1.0: a fresh downward flick starts timing on this tick but
        // does not commit until FeintWindowTicks+1 ticks have elapsed (#197 —
        // same one-tick-not-enough timing model as a fresh horizontal flick).
        var r = NewRecognizer();
        GestureResult result = r.Sample(new Vector2(0f, 1.0f));
        Assert.Equal(GestureKind.None, result.Kind);
    }

    [Fact]
    public void Sample_LargeVerticalWithSmallHorizontal_SingleTickReturnsNone()
    {
        // X=0.3 (below threshold=0.6), Y=0.95 (above) — vertical is the only
        // axis clearing threshold, so this starts a VERTICAL gesture (#197),
        // still None on the very first tick.
        var r = NewRecognizer();
        GestureResult result = r.Sample(new Vector2(0.3f, 0.95f));
        Assert.Equal(GestureKind.None, result.Kind);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 11. Vertical gesture pair (#197) — StepBack (hold) / RetreatDribble (quick)
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Sample_DownHeldPastWindow_FiresStepBack()
    {
        var r = NewRecognizer();
        GestureResult result = SampleN(r, new Vector2(0f, 0.9f), 5);
        Assert.Equal(GestureKind.StepBack, result.Kind);
    }

    [Fact]
    public void Sample_DownQuickReturnToDeadzoneWithinWindow_FiresRetreatDribble()
    {
        var r = NewRecognizer();
        r.Sample(new Vector2(0f, 0.9f)); // tick 1 above threshold — starts timing
        GestureResult result = r.Sample(Vector2.Zero); // returns to deadzone within window
        Assert.Equal(GestureKind.RetreatDribble, result.Kind);
    }

    [Fact]
    public void Sample_UpwardPush_NeverStartsVerticalGesture()
    {
        // Only DOWNWARD vertical motion is recognized (this project's
        // GetVector convention — aim_down is the positive-Y binding).
        var r = NewRecognizer();
        GestureResult result = SampleN(r, new Vector2(0f, -0.9f), 5);
        Assert.Equal(GestureKind.None, result.Kind);
    }

    [Fact]
    public void Sample_StepBackAndRetreatDribble_CarryZeroDirection()
    {
        // The vertical pair has no left/right payload — Direction stays 0
        // regardless of Kind (#197).
        var r1 = NewRecognizer();
        GestureResult stepBack = SampleN(r1, new Vector2(0f, 0.9f), 5);
        Assert.Equal(0f, stepBack.Direction);

        var r2 = NewRecognizer();
        r2.Sample(new Vector2(0f, 0.9f));
        GestureResult retreat = r2.Sample(Vector2.Zero);
        Assert.Equal(0f, retreat.Direction);
    }

    [Fact]
    public void Sample_VerticalGestureDebounce_NoRefireWithoutDeadzone()
    {
        var r = NewRecognizer();
        SampleN(r, new Vector2(0f, 0.9f), 5); // fires StepBack
        for (int i = 0; i < 10; i++)
        {
            GestureResult result = r.Sample(new Vector2(0f, 0.9f));
            Assert.Equal(GestureKind.None, result.Kind);
        }
    }

    [Fact]
    public void Sample_VerticalGestureReturnToDeadzoneThenFlick_FiresAgain()
    {
        var r = NewRecognizer();
        SampleN(r, new Vector2(0f, 0.9f), 5); // fires StepBack
        r.Sample(Vector2.Zero); // re-arm
        GestureResult second = SampleN(r, new Vector2(0f, 0.9f), 5);
        Assert.Equal(GestureKind.StepBack, second.Kind);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 12. Axis disambiguation (#197) — dominant-axis-wins with hysteresis
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Sample_HorizontalClearlyDominant_ResolvesHorizontal()
    {
        // X=0.9 vs Y=0.65 — horizontal exceeds vertical well past the default
        // 0.1 hysteresis band, so the gesture is Horizontal (Crossover).
        var r = NewRecognizer();
        GestureResult result = SampleN(r, new Vector2(0.9f, 0.65f), 5);
        Assert.Equal(GestureKind.Crossover, result.Kind);
    }

    [Fact]
    public void Sample_VerticalClearlyDominant_ResolvesVertical()
    {
        // Y=0.9 vs X=0.65 — vertical exceeds horizontal well past the
        // hysteresis band, so the gesture is Vertical (StepBack).
        var r = NewRecognizer();
        GestureResult result = SampleN(r, new Vector2(0.65f, 0.9f), 5);
        Assert.Equal(GestureKind.StepBack, result.Kind);
    }

    [Fact]
    public void Sample_AxesWithinHysteresisBand_TieBreaksToHorizontal()
    {
        // X=0.7, Y=0.72 — both clear FlickThreshold(0.6), and the gap (0.02)
        // is inside the default 0.1 hysteresis band: a near-45° flick.
        // Deterministic fixed tie-break favors Horizontal (see the
        // recognizer's "Axis disambiguation" doc).
        var r = NewRecognizer();
        GestureResult result = SampleN(r, new Vector2(0.7f, 0.72f), 5);
        Assert.Equal(GestureKind.Crossover, result.Kind);
    }

    [Fact]
    public void Sample_AxisLockedAtStart_DoesNotSwitchMidGesture()
    {
        // Gesture starts purely vertical (X=0), then the player's stick
        // drifts to also clear the horizontal threshold. The axis decided at
        // gesture START stays locked — this must still resolve as the
        // vertical gesture (StepBack), never flip to Crossover mid-flight.
        var r = NewRecognizer();
        r.Sample(new Vector2(0f, 0.9f));   // tick 1: vertical-only start, axis locked to Vertical
        r.Sample(new Vector2(0.9f, 0.9f)); // tick 2: horizontal now also clears threshold
        r.Sample(new Vector2(0.9f, 0.9f)); // tick 3
        r.Sample(new Vector2(0.9f, 0.9f)); // tick 4
        GestureResult result = r.Sample(new Vector2(0.9f, 0.9f)); // tick 5 (FeintWindowTicks+1)
        Assert.Equal(GestureKind.StepBack, result.Kind);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Direction sentinel — GestureResult.None has Direction == 0
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Sample_ZeroVector_DirectionIsZero()
    {
        var r = NewRecognizer();
        GestureResult result = r.Sample(Vector2.Zero);
        Assert.Equal(0f, result.Direction);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Feint gesture — quick flick and return within FeintWindowTicks
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Sample_QuickReturnToDeadzoneWithinWindow_FiresFeint()
    {
        // Push past threshold for 1 tick, then return to deadzone before
        // FeintWindowTicks expires → Feint.
        var r = NewRecognizer();
        r.Sample(new Vector2(0.9f, 0f)); // tick 1 above threshold — starts timing
        GestureResult result = r.Sample(Vector2.Zero); // returns to deadzone within window
        Assert.Equal(GestureKind.Feint, result.Kind);
    }

    [Fact]
    public void Sample_FeintPreservesDirection()
    {
        // Direction of the feint matches the direction of the initial flick.
        var r = NewRecognizer();
        r.Sample(new Vector2(-0.9f, 0f)); // left flick
        GestureResult result = r.Sample(Vector2.Zero);
        Assert.Equal(-1f, result.Direction);
    }

    [Fact]
    public void Sample_FeintDebounce_NoRefireWithoutDeadzone()
    {
        // After a feint fires, the recognizer waits for re-arm before firing again.
        var r = NewRecognizer();
        r.Sample(new Vector2(0.9f, 0f));
        r.Sample(Vector2.Zero); // feint fires here
        // Immediately sample deadzone again — no re-fire yet (still in locked state
        // because we just used the deadzone to fire).
        r.Sample(Vector2.Zero);
        // Now push past threshold again.
        GestureResult result = SampleN(r, new Vector2(0.9f, 0f), 5);
        // Should fire because we did pass through deadzone (the second Zero sample
        // re-armed us after the feint already fired).
        Assert.Equal(GestureKind.Crossover, result.Kind);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Custom threshold — constructor overrides are respected
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Sample_CustomThreshold_BelowCustomThresholdReturnsNone()
    {
        // Threshold raised to 0.9 — a 0.7 push must not start timing.
        var r = new RightStickGestureRecognizer(flickThreshold: 0.9f, deadzoneRadius: 0.2f, feintWindowTicks: 4);
        GestureResult result = SampleN(r, new Vector2(0.7f, 0f), 5);
        Assert.Equal(GestureKind.None, result.Kind);
    }

    [Fact]
    public void Sample_CustomThreshold_AtCustomThresholdFires()
    {
        var r = new RightStickGestureRecognizer(flickThreshold: 0.9f, deadzoneRadius: 0.2f, feintWindowTicks: 4);
        GestureResult result = SampleN(r, new Vector2(0.9f, 0f), 5);
        Assert.Equal(GestureKind.Crossover, result.Kind);
    }
}
