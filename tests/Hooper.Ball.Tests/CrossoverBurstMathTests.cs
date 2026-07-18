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
    // cross yourself into retreat). Code-review fix (#198): the naive
    // composition clamps forward to 0 AND has a near-zero lateral dot
    // product for a purely-backward exit vector, so it used to compose a
    // dead-zero impulse — a fully committed crossover producing a dead
    // stop the player didn't ask for. The fix falls back to the legacy
    // flick-driven lateral burst so the cross always crosses; this test
    // now pins THAT guaranteed behavior (nonzero magnitude, correct
    // lateral sign) instead of the too-weak "Z <= 0" it replaces.
    [Fact]
    public void ExitVectorPointingBackward_FallsBackToFlickDrivenLateralBurst()
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
        Assert.Equal(-BurstSpeed, result.X, precision: 4); // HandStateResolver.BurstWorldDir(0, +1) == (-1, 0)
        Assert.Equal(0f, result.Z, precision: 4);
        Assert.True(new Vector2(result.X, result.Z).Length() > 0f,
            "expected a nonzero burst (the dead-move fallback), not a silent dead stop");
    }

    // Burst-magnitude invariant (code review, optional item): across a sweep
    // of exit-vector angles from a dead stop (isolating the impulse itself
    // from any surviving-momentum contribution), the composed impulse must
    // never exceed the orthonormal-basis bound sqrt(burstSpeed^2 +
    // forwardBurstScale^2) — pins "no superhuman burst" now that a fallback
    // branch exists that could, in principle, stack on top of the primary
    // composition instead of replacing it.
    [Theory]
    [InlineData(0f)]
    [InlineData(30f)]
    [InlineData(60f)]
    [InlineData(90f)]
    [InlineData(120f)]
    [InlineData(150f)]
    [InlineData(180f)]
    [InlineData(210f)]
    [InlineData(270f)]
    [InlineData(315f)]
    public void ComposedImpulse_NeverExceedsOrthonormalBasisBound(float exitAngleDegrees)
    {
        float radians = Mathf.DegToRad(exitAngleDegrees);
        var exitVector = new Vector2(Mathf.Sin(radians), Mathf.Cos(radians));
        float bound = Mathf.Sqrt(BurstSpeed * BurstSpeed + ForwardBurstScale * ForwardBurstScale);

        Vector3 result = CrossoverBurstMath.ComposeActiveVelocity(
            survivingVelocity: Vector3.Zero, // isolates the impulse — no momentum to add on top
            heading: 0f,
            flickSign: +1,
            exitVector,
            burstSpeed: BurstSpeed,
            forwardBurstScale: ForwardBurstScale,
            exitDeadzone: ExitDeadzone);

        float magnitude = new Vector2(result.X, result.Z).Length();
        Assert.True(magnitude <= bound + 0.01f,
            $"exit angle {exitAngleDegrees}deg composed a {magnitude:F3} m/s impulse, exceeding the " +
            $"orthonormal-basis bound {bound:F3} m/s.");
    }

    // ── Issue #209: exit velocity must be a CONTINUOUS function of the inputs ──
    // The design identity (ADR-0003) wants a legible, continuous relationship
    // between commitment and result. A bit-exact input boundary that produces a
    // huge jump in exit velocity is the opposite of that — it makes the move
    // unreadable. The two tests below pin continuity across the two former
    // cliffs; a single adjacent-input step must never leap by an appreciable
    // fraction of the burst speed.

    // Helper: the maximum magnitude by which the composed exit velocity may
    // change between two neighbouring inputs. Well below the ~9 m/s pre-fix
    // cliffs, well above the real continuous per-step delta (~0.25 m/s), so it
    // cleanly separates a smooth blend from a discontinuous branch.
    private const float MaxContinuousStep = 1.0f;

    // Cliff A — the surviving-speed boundary (issue title). Because
    // ComposeActiveVelocity takes the post-bleed survivor directly, we feed it
    // survivor values straddling zero WITHOUT simulating Startup. The exit
    // vector is held neutral so the surviving-momentum branch (the one that used
    // to hard-switch on `survivingXZ.LengthSquared() < 0.0001f`) is exercised.
    // Pre-fix: survivor 0.0 → full ±9 flick, survivor 0.1 → zero impulse — a
    // ~9 m/s leap. Post-fix: continuous.
    [Fact]
    public void CliffA_SurvivingSpeedBoundary_ExitVelocityIsContinuous()
    {
        Vector3 At(float survivingSpeed) => CrossoverBurstMath.ComposeActiveVelocity(
            survivingVelocity: new Vector3(0f, 0f, survivingSpeed), // forward momentum, neutral exit
            heading: 0f,
            flickSign: +1,
            exitVector: Vector2.Zero, // neutral — drives the surviving-momentum branch
            burstSpeed: BurstSpeed,
            forwardBurstScale: ForwardBurstScale,
            exitDeadzone: ExitDeadzone);

        // The exact boundary pair the issue names (0.0 vs 0.1 m/s surviving).
        float boundaryJump = (At(0.1f) - At(0.0f)).Length();
        Assert.True(boundaryJump <= MaxContinuousStep,
            $"surviving 0.0 vs 0.1 m/s jumped {boundaryJump:F3} m/s (pre-fix ~9) — discontinuous.");

        // And the whole realistic surviving range [0, 6] in fine steps: no
        // single 0.1 m/s step may leap. Sweeping past the old boundary (and
        // past the blend's upper threshold) proves there is no residual step
        // anywhere, not just at one probe point.
        Vector3 prev = At(0f);
        for (float s = 0.1f; s <= 6.0f + 1e-4f; s += 0.1f)
        {
            Vector3 cur = At(s);
            float step = (cur - prev).Length();
            Assert.True(step <= MaxContinuousStep,
                $"surviving-speed step at {s:F2} m/s jumped {step:F3} m/s — discontinuous.");
            prev = cur;
        }
    }

    // Cliff B — the DeadImpulseFloor backward-cone boundary (owner's comment).
    // Sweep the exit-vector angle across the straight-back edge. Near dead-back
    // the composed impulse magnitude passes up through DeadImpulseFloor (0.5);
    // pre-fix it hard-snapped to the full ±9 flick below the floor, so an exit
    // ~3° off straight-back leapt ~8.5 m/s versus one just inside. Post-fix:
    // continuous. Parameterized over the three moves that share this function
    // (Crossover 9, BetweenTheLegs 7.5, BehindTheBack 6) — the threshold at
    // which the flick fallback yields is burst-speed-relative, so continuity
    // must hold for each tunable, not just Crossover's.
    [Theory]
    [InlineData(9.0f)]
    [InlineData(7.5f)]
    [InlineData(6.0f)]
    public void CliffB_BackwardConeEdge_ExitVelocityIsContinuous(float burstSpeed)
    {
        // Angle measured from +Z (forward); 180° is straight back. Sweep a
        // window around dead-back where the composed impulse crosses the floor.
        Vector3 AtAngle(float degrees)
        {
            float r = Mathf.DegToRad(degrees);
            return CrossoverBurstMath.ComposeActiveVelocity(
                survivingVelocity: Vector3.Zero, // isolate the impulse from momentum
                heading: 0f,
                flickSign: +1,
                exitVector: new Vector2(Mathf.Sin(r), Mathf.Cos(r)),
                burstSpeed: burstSpeed,
                forwardBurstScale: burstSpeed,
                exitDeadzone: ExitDeadzone);
        }

        // 0.1° sampling: fine enough that the fixed blend's steep-but-continuous
        // ramp near the pole stays well under MaxContinuousStep, while the
        // pre-fix DeadImpulseFloor snap (which leaps ~8.5 m/s across one sample
        // no matter how fine the grid) still trips the bound. See the header
        // insight on distinguishing a discontinuity from a steep slope.
        Vector3 prev = AtAngle(150f);
        for (float deg = 150.1f; deg <= 210.0f + 1e-4f; deg += 0.1f)
        {
            Vector3 cur = AtAngle(deg);
            float step = (cur - prev).Length();
            Assert.True(step <= MaxContinuousStep,
                $"burstSpeed {burstSpeed}: exit-angle step at {deg:F1}° jumped {step:F3} m/s — discontinuous.");
            prev = cur;
        }
    }
}
