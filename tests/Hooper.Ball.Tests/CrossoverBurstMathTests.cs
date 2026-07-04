using Godot;
using Hooper.Player;

namespace Hooper.Ball.Tests;

// Issue #198 — the four emergent-move table rows from the moving-crossover
// spec (docs/handoffs/M9-move-taxonomy.md §2, grilled 2026-07-04), all
// produced by ONE parameterized composition function rather than a move zoo.
//
// Heading is fixed at 0 throughout so HandStateResolver's convention
// (forward = (sin h, cos h) = (0,1); right = (-cos h, sin h) = (-1,0) for a
// +1 flick) reduces to exact float arithmetic — no epsilon tolerance needed.
public class CrossoverBurstMathTests
{
    private const float BurstSpeed = 9f;
    private const float ForwardBurstScale = 9f;
    private const float ExitDeadzone = 0.15f;

    // Row 1 — Stationary, push forward at Active-entry: "Cross → explode
    // forward." No lateral separation; pure forward burst.
    [Fact]
    public void Stationary_PushForward_BurstsForwardWithNoLateralComponent()
    {
        Vector3 result = CrossoverBurstMath.ComposeActiveVelocity(
            survivingVelocity: Vector3.Zero,
            heading: 0f,
            flickSign: +1,
            exitVector: new Vector2(0f, 1f), // pure forward push
            burstSpeed: BurstSpeed,
            forwardBurstScale: ForwardBurstScale,
            exitDeadzone: ExitDeadzone);

        Assert.Equal(0f, result.X, precision: 4);
        Assert.Equal(ForwardBurstScale, result.Z, precision: 4);
    }

    // Row 2 — Driving forward, push diagonal at Active-entry: "Change-of-
    // direction cross." Surviving forward momentum plus a lateral kick —
    // both components present, and the player never dead-stops.
    [Fact]
    public void DrivingForward_PushDiagonal_RetainsForwardAndGainsLateral()
    {
        var survivingVelocity = new Vector3(0f, 0f, 6f); // driving at 6 m/s forward
        Vector3 result = CrossoverBurstMath.ComposeActiveVelocity(
            survivingVelocity,
            heading: 0f,
            flickSign: +1,
            exitVector: new Vector2(0.7f, 0.7f), // diagonal: forward + player's right
            burstSpeed: BurstSpeed,
            forwardBurstScale: ForwardBurstScale,
            exitDeadzone: ExitDeadzone);

        Assert.True(result.Z > 0f, $"expected retained forward component, got Z={result.Z}");
        Assert.True(result.X != 0f, $"expected a lateral (diagonal) component, got X={result.X}");
        Assert.True(new Vector2(result.X, result.Z).Length() > 0f, "expected nonzero overall speed");
    }

    // Row 3 — Driving forward, neutral/straight exit at Active-entry:
    // "Push-cross" — hands swap, ~no separation. The exit vector adds no
    // impulse; the player simply continues on the surviving momentum.
    [Fact]
    public void DrivingForward_NeutralExit_ProducesNearZeroLateralSeparation()
    {
        var survivingVelocity = new Vector3(0f, 0f, 6f);
        Vector3 result = CrossoverBurstMath.ComposeActiveVelocity(
            survivingVelocity,
            heading: 0f,
            flickSign: +1,
            exitVector: Vector2.Zero, // neutral — no steering input
            burstSpeed: BurstSpeed,
            forwardBurstScale: ForwardBurstScale,
            exitDeadzone: ExitDeadzone);

        Assert.Equal(0f, result.X, precision: 4); // no lateral separation
        Assert.Equal(survivingVelocity.Z, result.Z, precision: 4); // momentum untouched
    }

    // Row 4 — Stationary, push lateral at Active-entry: the classic
    // side-to-side shuffle. Pure lateral, no forward component.
    [Fact]
    public void Stationary_PushLateral_BurstsLaterallyWithNoForwardComponent()
    {
        Vector3 result = CrossoverBurstMath.ComposeActiveVelocity(
            survivingVelocity: Vector3.Zero,
            heading: 0f,
            flickSign: +1,
            exitVector: new Vector2(-1f, 0f), // pure player's-right push (matches flickSign +1's side)
            burstSpeed: BurstSpeed,
            forwardBurstScale: ForwardBurstScale,
            exitDeadzone: ExitDeadzone);

        Assert.Equal(-BurstSpeed, result.X, precision: 4);
        Assert.Equal(0f, result.Z, precision: 4);
    }

    // Fallback — stationary AND the exit vector is neutral (below deadzone):
    // no basketball scenario in the table covers "no steering input at all,"
    // so this preserves the pre-#198 pure flick-driven lateral burst rather
    // than silently reducing a bare crossover flick to a no-op.
    [Fact]
    public void Stationary_NeutralExit_FallsBackToFlickDrivenLateralBurst()
    {
        Vector3 result = CrossoverBurstMath.ComposeActiveVelocity(
            survivingVelocity: Vector3.Zero,
            heading: 0f,
            flickSign: +1,
            exitVector: Vector2.Zero,
            burstSpeed: BurstSpeed,
            forwardBurstScale: ForwardBurstScale,
            exitDeadzone: ExitDeadzone);

        Assert.Equal(-BurstSpeed, result.X, precision: 4); // HandStateResolver.BurstWorldDir(0, +1) == (-1, 0)
        Assert.Equal(0f, result.Z, precision: 4);
    }

    // A raw stick reading at exactly the deadzone magnitude counts as
    // neutral (strictly-greater-than gate, matching the fallback branch),
    // guarding against an off-by-one flip at the boundary.
    [Fact]
    public void ExitVectorAtExactlyDeadzone_CountsAsNeutral()
    {
        var survivingVelocity = new Vector3(0f, 0f, 6f);
        Vector3 result = CrossoverBurstMath.ComposeActiveVelocity(
            survivingVelocity,
            heading: 0f,
            flickSign: +1,
            exitVector: new Vector2(0f, ExitDeadzone), // magnitude exactly == deadzone
            burstSpeed: BurstSpeed,
            forwardBurstScale: ForwardBurstScale,
            exitDeadzone: ExitDeadzone);

        Assert.Equal(survivingVelocity.Z, result.Z, precision: 4);
    }

    // Backward exit input never reverses the burst — a crossover explodes
    // forward or lateral, never backward (real-ball rationale: you don't
    // cross yourself into retreat).
    [Fact]
    public void ExitVectorPointingBackward_ContributesNoForwardBurst()
    {
        Vector3 result = CrossoverBurstMath.ComposeActiveVelocity(
            survivingVelocity: Vector3.Zero,
            heading: 0f,
            flickSign: +1,
            exitVector: new Vector2(0f, -1f), // pure backward push
            burstSpeed: BurstSpeed,
            forwardBurstScale: ForwardBurstScale,
            exitDeadzone: ExitDeadzone);

        Assert.True(result.Z <= 0f, $"expected no forward burst from backward input, got Z={result.Z}");
    }
}
