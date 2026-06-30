using System;
using Godot;
using Hooper.Player;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for HeadingMath.RotateToward — the pure heading step
/// introduced by issue #80 and ADR-0010. Runs without a live Godot instance,
/// using Godot.Vector2 from the GodotSharp NuGet (same pattern as MovementMathTests).
///
/// RotateToward lives inside Move(), the shared server-authority / client-
/// prediction / reconciliation-replay step, so any divergence here is a
/// netcode bug — not just a feel tweak (ADR-0002).
///
/// The non-linear rate property (slower near 180°, near-free near 0°) is the
/// core design requirement of ADR-0003 and issue #80: micro-corrections must
/// feel near-instant while a full reverse-pivot reads as a visible commitment.
///
/// ── Test naming ──────────────────────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// Each test contains exactly one logical assertion.
/// </summary>
public class HeadingMathTests
{
    // Shared test defaults — match PlayerController's exported defaults
    // so the tests reflect real in-game behaviour.
    private const float MaxTurnDeg      = 530f;
    private const float BackTurnSlow    = 0.35f;
    private const double BigDelta       = 1.0 / 60.0;  // one physics tick at 60 Hz

    // ── Zero wishDir guard ────────────────────────────────────────────────────

    [Fact]
    public void RotateToward_ZeroWishDir_ReturnsCurrentYawUnchanged()
    {
        // No directional input — holding the heading prevents Atan2 snap
        // when the stick is released (mirror of FacingResolver.SpeedEpsilon).
        float currentYaw = 1.23f;
        float result = HeadingMath.RotateToward(
            currentYaw: currentYaw, wishDir: Vector2.Zero,
            delta: BigDelta, maxTurnRateDeg: MaxTurnDeg, backTurnSlowFactor: BackTurnSlow);

        Assert.Equal(currentYaw, result);
    }

    [Fact]
    public void RotateToward_NearZeroWishDir_ReturnsCurrentYawUnchanged()
    {
        // A magnitude of 1e-4 is above true zero but below WishDirEpsilon (0.01).
        // The guard must still hold so sub-pixel stick noise doesn't rotate the heading.
        float currentYaw = -2.0f;
        float result = HeadingMath.RotateToward(
            currentYaw: currentYaw, wishDir: new Vector2(0.001f, 0.001f),
            delta: BigDelta, maxTurnRateDeg: MaxTurnDeg, backTurnSlowFactor: BackTurnSlow);

        Assert.Equal(currentYaw, result);
    }

    // ── Small correction: one tick is enough ─────────────────────────────────

    [Fact]
    public void RotateToward_SmallDiff_TurnsTowardTarget()
    {
        // A tiny angular difference should produce a result closer to the
        // desired yaw than the starting yaw.
        float currentYaw  = 0f;
        // wishDir.Y=1 → desiredYaw = Atan2(0, 1) = 0; let's use a slight
        // X-tilt to get a small but nonzero desired yaw.
        Vector2 wishDir   = new Vector2(0.1f, 1f); // mostly forward, slight right
        float desiredYaw  = MathF.Atan2(wishDir.X, wishDir.Y);
        float result = HeadingMath.RotateToward(
            currentYaw: currentYaw, wishDir: wishDir,
            delta: BigDelta, maxTurnRateDeg: MaxTurnDeg, backTurnSlowFactor: BackTurnSlow);

        Assert.True(MathF.Abs(result - desiredYaw) < MathF.Abs(currentYaw - desiredYaw));
    }

    [Fact]
    public void RotateToward_SmallDiff_CanCompleteInOneTick()
    {
        // A 5° difference with MaxTurnDeg=530 and delta=1/60: max step per tick
        // is 530/60 ≈ 8.8° — well above 5°, so one tick must land exactly on target.
        float desiredYaw = 5f * MathF.PI / 180f;   // 5 degrees in radians
        float currentYaw = 0f;
        // Build a wishDir whose Atan2 equals desiredYaw: Atan2(sin, cos).
        Vector2 wishDir  = new Vector2(MathF.Sin(desiredYaw), MathF.Cos(desiredYaw));

        float result = HeadingMath.RotateToward(
            currentYaw: currentYaw, wishDir: wishDir,
            delta: BigDelta, maxTurnRateDeg: MaxTurnDeg, backTurnSlowFactor: BackTurnSlow);

        Assert.Equal(desiredYaw, result, precision: 4); // 4-decimal float tolerance
    }

    // ── Large turn: rate-limited in one tick ─────────────────────────────────

    [Fact]
    public void RotateToward_LargeDiff_IsRateLimitedInOneTick()
    {
        // A 170° difference: one tick at 530 °/s × 0.35 slow-factor (near-180°)
        // ≈ 3.1 °/tick — nowhere near 170°. The result must be strictly between
        // start and target, not at the target.
        float currentYaw = 0f;
        float desiredYaw = 170f * MathF.PI / 180f;
        Vector2 wishDir  = new Vector2(MathF.Sin(desiredYaw), MathF.Cos(desiredYaw));

        float result = HeadingMath.RotateToward(
            currentYaw: currentYaw, wishDir: wishDir,
            delta: BigDelta, maxTurnRateDeg: MaxTurnDeg, backTurnSlowFactor: BackTurnSlow);

        // Must be between start (0) and target (170°): hasn't reached target yet.
        Assert.True(result > currentYaw);
        Assert.True(result < desiredYaw);
    }

    [Fact]
    public void RotateToward_FullBackTurn_DoesNotCompleteInOneTick()
    {
        // A 180° (π) back-turn at BackTurnSlow=0.35: max step ≈ 530×0.35/60 ≈ 3.09 °.
        // One tick cannot cover 180° — this is the commitment cost of issue #80.
        float currentYaw = 0f;
        Vector2 wishDir  = new Vector2(0f, -1f); // desire = Atan2(0, -1) = π

        float result = HeadingMath.RotateToward(
            currentYaw: currentYaw, wishDir: wishDir,
            delta: BigDelta, maxTurnRateDeg: MaxTurnDeg, backTurnSlowFactor: BackTurnSlow);

        float desiredYaw = MathF.PI;
        Assert.True(MathF.Abs(result - currentYaw) < MathF.Abs(desiredYaw - currentYaw));
    }

    // ── Shortest path / wrap ──────────────────────────────────────────────────

    [Fact]
    public void RotateToward_AcrossPiBoundary_TakesShortestPath()
    {
        // currentYaw = +0.9π, desiredYaw = -0.9π (≈ the ±π boundary crossing).
        // Short path is 0.2π going backwards (through π); long path is 1.8π the
        // other way. The result must be FURTHER from currentYaw in the short
        // direction, i.e. moved toward -π not toward 0.
        float currentYaw = 0.9f * MathF.PI;
        float desiredYaw = -0.9f * MathF.PI;  // ≡ +1.1π — just past ±π
        Vector2 wishDir  = new Vector2(MathF.Sin(desiredYaw), MathF.Cos(desiredYaw));

        float result = HeadingMath.RotateToward(
            currentYaw: currentYaw, wishDir: wishDir,
            delta: BigDelta, maxTurnRateDeg: MaxTurnDeg, backTurnSlowFactor: BackTurnSlow);

        // Short-path step: result should have increased in magnitude (moved
        // toward +π from +0.9π), not decreased toward 0.
        Assert.True(MathF.Abs(result) > MathF.Abs(currentYaw)
                    || result > currentYaw);  // moved toward +π not toward 0
    }

    [Fact]
    public void RotateToward_ResultIsAlwaysInPiRange()
    {
        // No matter what inputs we feed, the output must stay in [-π, π].
        float currentYaw = 2.9f;   // near +π
        Vector2 wishDir  = new Vector2(-1f, 0f); // desiredYaw = Atan2(-1, 0) = -π/2

        float result = HeadingMath.RotateToward(
            currentYaw: currentYaw, wishDir: wishDir,
            delta: BigDelta, maxTurnRateDeg: MaxTurnDeg, backTurnSlowFactor: BackTurnSlow);

        Assert.True(result >= -MathF.PI && result <= MathF.PI);
    }

    // ── Non-linear rate: near-180° is slower than small corrections ──────────

    [Fact]
    public void RotateToward_NearBackTurn_AdvancesLessPerTickThanSmallTurn()
    {
        // Core ADR-0010 / issue #80 property: the heading advances LESS
        // per tick near 180° than near 0°, at the same nominal MaxTurnDeg.
        // Small turn: 10° from target.
        // Large turn: 170° from target.
        // Both advance for one tick; the small-diff advancement must be larger.

        float smallDiffDeg   = 10f;
        float largeDiffDeg   = 170f;
        float smallDiffRad   = smallDiffDeg * MathF.PI / 180f;
        float largeDiffRad   = largeDiffDeg * MathF.PI / 180f;

        // For the small turn: start at 0, desired at smallDiffRad.
        Vector2 smallWish    = new Vector2(MathF.Sin(smallDiffRad), MathF.Cos(smallDiffRad));
        float smallResult    = HeadingMath.RotateToward(0f, smallWish, BigDelta, MaxTurnDeg, BackTurnSlow);
        float smallAdvance   = MathF.Abs(smallResult);   // advance from 0

        // For the large turn: start at 0, desired at largeDiffRad.
        Vector2 largeWish    = new Vector2(MathF.Sin(largeDiffRad), MathF.Cos(largeDiffRad));
        float largeResult    = HeadingMath.RotateToward(0f, largeWish, BigDelta, MaxTurnDeg, BackTurnSlow);
        float largeAdvance   = MathF.Abs(largeResult);   // advance from 0

        // The small-diff tick must cover MORE radians than the large-diff tick.
        Assert.True(smallAdvance > largeAdvance,
            $"Expected small-diff advance ({smallAdvance:F5} rad) > large-diff advance ({largeAdvance:F5} rad)");
    }

    // ── Convergence ───────────────────────────────────────────────────────────

    [Fact]
    public void RotateToward_RepeatedCalls_EventuallyReachTarget()
    {
        // Regardless of starting yaw, repeatedly calling RotateToward must
        // converge to the desired yaw within a finite number of ticks.
        // 120 ticks = 2 s at 60 Hz — a 180° back-turn at max=530 takes ≈ 0.55 s
        // (~33 ticks); 120 ticks is a comfortable safety margin (~3.6× the true time).
        float currentYaw = 0f;
        Vector2 wishDir  = new Vector2(0f, -1f); // desiredYaw = π (full back-turn)
        float desiredYaw = MathF.PI;
        const float epsilon = 0.001f; // ~0.06° — well within rounding

        for (int i = 0; i < 120; i++)
        {
            currentYaw = HeadingMath.RotateToward(
                currentYaw: currentYaw, wishDir: wishDir,
                delta: BigDelta, maxTurnRateDeg: MaxTurnDeg, backTurnSlowFactor: BackTurnSlow);

            // Check angular closeness accounting for ±π wrap.
            float diff = currentYaw - desiredYaw;
            while (diff >  MathF.PI) diff -= 2f * MathF.PI;
            while (diff < -MathF.PI) diff += 2f * MathF.PI;
            if (MathF.Abs(diff) < epsilon) return; // converged — test passes
        }

        Assert.Fail($"Did not converge to desiredYaw {desiredYaw:F4} within 120 ticks; last yaw = {currentYaw:F4}");
    }

    // ── HeadingMath.Forward — cardinal headings ───────────────────────────────

    private const float Tol = 1e-5f;

    [Fact]
    public void Forward_HeadingZero_FacesPositiveZ()
    {
        // h=0 → mesh faces +Z (Y-rotation=0 points toward +Z in Godot),
        // so Forward(0) must be the unit vector (0, 1) in (worldX, worldZ).
        Vector2 fwd = HeadingMath.Forward(0f);
        Assert.True(MathF.Abs(fwd.X - 0f) < Tol, $"Expected X≈0, got {fwd.X}");
        Assert.True(MathF.Abs(fwd.Y - 1f) < Tol, $"Expected Y≈1, got {fwd.Y}");
    }

    [Fact]
    public void Forward_HeadingHalfPi_FacesPositiveX()
    {
        // h=π/2 → (sin π/2, cos π/2) = (1, 0): right along +X.
        Vector2 fwd = HeadingMath.Forward(MathF.PI / 2f);
        Assert.True(MathF.Abs(fwd.X - 1f) < Tol, $"Expected X≈1, got {fwd.X}");
        Assert.True(MathF.Abs(fwd.Y - 0f) < Tol, $"Expected Y≈0, got {fwd.Y}");
    }

    [Fact]
    public void Forward_HeadingPi_FacesNegativeZ()
    {
        // h=π → (sin π, cos π) = (0, -1): directly behind in +Z convention.
        Vector2 fwd = HeadingMath.Forward(MathF.PI);
        Assert.True(MathF.Abs(fwd.X - 0f) < Tol, $"Expected X≈0, got {fwd.X}");
        Assert.True(MathF.Abs(fwd.Y - (-1f)) < Tol, $"Expected Y≈-1, got {fwd.Y}");
    }

    [Fact]
    public void Forward_HeadingNegativeHalfPi_FacesNegativeX()
    {
        // h=-π/2 → (sin -π/2, cos -π/2) = (-1, 0): left along -X.
        Vector2 fwd = HeadingMath.Forward(-MathF.PI / 2f);
        Assert.True(MathF.Abs(fwd.X - (-1f)) < Tol, $"Expected X≈-1, got {fwd.X}");
        Assert.True(MathF.Abs(fwd.Y - 0f) < Tol, $"Expected Y≈0, got {fwd.Y}");
    }

    // ── HeadingMath.Forward — round-trip property ─────────────────────────────

    [Theory]
    [InlineData(0f)]
    [InlineData(MathF.PI / 4f)]
    [InlineData(MathF.PI / 2f)]
    [InlineData(MathF.PI - 0.001f)]   // near +π (avoid exact π where cos→-1 and sign differs)
    [InlineData(-MathF.PI / 4f)]
    [InlineData(-MathF.PI / 2f)]
    [InlineData(-MathF.PI + 0.001f)]  // near -π
    public void Forward_RoundTrip_Atan2EqualsOriginalHeading(float heading)
    {
        // Forward is the exact inverse of the Atan2(x, z) convention RotateToward
        // uses to derive heading from intent: Atan2(Forward(h).X, Forward(h).Y) ≡ h.
        // This pins that the ball-offset vector produced from Heading will feed back
        // into the same convention if ever re-derived, with no drift.
        Vector2 fwd      = HeadingMath.Forward(heading);
        float   roundTrip = MathF.Atan2(fwd.X, fwd.Y);
        Assert.True(MathF.Abs(roundTrip - heading) < Tol,
            $"Round-trip failed for h={heading}: got {roundTrip}");
    }

    // ── HeadingMath.Forward — unit length ─────────────────────────────────────

    [Theory]
    [InlineData(0f)]
    [InlineData(MathF.PI / 6f)]
    [InlineData(MathF.PI / 3f)]
    [InlineData(MathF.PI / 2f)]
    [InlineData(2f * MathF.PI / 3f)]
    [InlineData(-MathF.PI / 4f)]
    [InlineData(-2f)]
    public void Forward_AnyHeading_IsUnitLength(float heading)
    {
        // Forward must always be a unit vector — the callers (HolderForward in
        // BallController) do not renormalise, so length ≠ 1 would scale the
        // DribbleForwardOffset and HandOffset silently.
        Vector2 fwd = HeadingMath.Forward(heading);
        float   len = fwd.Length();
        Assert.True(MathF.Abs(len - 1f) < Tol,
            $"Expected unit length for h={heading}, got {len}");
    }
}
