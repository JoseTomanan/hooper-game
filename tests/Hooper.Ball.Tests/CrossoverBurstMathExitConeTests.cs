using Godot;
using Hooper.Player;

namespace Hooper.Ball.Tests;

// Issue #194 — CrossoverBurstMath.ComposeActiveVelocity gains an optional
// maxExitAngleRadians parameter so BehindTheBack can share the SAME pure
// composition Crossover uses (#198) while clamping the exit vector to a
// NARROWER cone around the player's forward axis (docs/handoffs/M9-move-
// taxonomy.md: "fewer follow-ups" = narrower exit cone ONLY, no recovery/
// cooldown penalties). Composition over hierarchy — the cone is a parameter
// on the shared function, not a second copy of the math.
//
// Heading fixed at 0 throughout, same convention as CrossoverBurstMathTests
// (forward = (0,1), right = (-1,0) for a +1 flick) so exact float arithmetic
// applies with no epsilon beyond the usual precision:4 rounding.
public class CrossoverBurstMathExitConeTests
{
    private const float BurstSpeed = 6f;
    private const float ForwardBurstScale = 6f;
    private const float ExitDeadzone = 0.15f;

    // A pure-lateral exit vector (matches the crossover's classic side-to-
    // side shuffle input) is at a 90-degree angle from forward. A 45-degree
    // cone must clamp that angle down to 45 degrees, producing BOTH a
    // forward AND a lateral component instead of the unclamped pure-lateral
    // burst — this is the entire "narrower exit cone" behaviour BehindTheBack
    // needs.
    [Fact]
    public void NarrowCone_ClampsPureLateralExit_TowardForward()
    {
        Vector3 result = CrossoverBurstMath.ComposeActiveVelocity(
            survivingVelocity: Vector3.Zero,
            heading: 0f,
            flickSign: +1,
            exitVector: new Vector2(-1f, 0f), // pure player's-right push, 90deg from forward
            burstSpeed: BurstSpeed,
            forwardBurstScale: ForwardBurstScale,
            exitDeadzone: ExitDeadzone,
            maxExitAngleRadians: Mathf.DegToRad(45f));

        float expectedForward = ForwardBurstScale * Mathf.Cos(Mathf.DegToRad(45f));
        float expectedLateral = -(BurstSpeed * Mathf.Sin(Mathf.DegToRad(45f)));

        Assert.Equal(expectedLateral, result.X, precision: 3);
        Assert.Equal(expectedForward, result.Z, precision: 3);
        // The discriminator against the OLD (unclamped) behaviour: a pure
        // lateral push used to produce Z == 0 exactly (Stationary_PushLateral
        // in CrossoverBurstMathTests). Under the narrow cone it must not.
        Assert.True(result.Z > 0.5f, $"expected a forward component from the clamp, got Z={result.Z}");
    }

    // Omitting maxExitAngleRadians (or passing an angle >= 180deg) must
    // reproduce the pre-#194 Crossover behaviour bit-for-bit (within the
    // usual float precision) — existing callers (Crossover, all of
    // CrossoverBurstMathTests) must never see a behaviour change from this
    // parameter's addition.
    [Fact]
    public void DefaultCone_ReproducesUnclampedPureLateralBurst()
    {
        Vector3 result = CrossoverBurstMath.ComposeActiveVelocity(
            survivingVelocity: Vector3.Zero,
            heading: 0f,
            flickSign: +1,
            exitVector: new Vector2(-1f, 0f),
            burstSpeed: BurstSpeed,
            forwardBurstScale: ForwardBurstScale,
            exitDeadzone: ExitDeadzone);

        Assert.Equal(-BurstSpeed, result.X, precision: 4);
        Assert.Equal(0f, result.Z, precision: 4);
    }

    // A diagonal exit vector already WITHIN the narrow cone must pass
    // through unaffected — the clamp only bites when the requested angle
    // exceeds the cone, never attenuating an already-legal direction.
    [Fact]
    public void NarrowCone_LeavesWithinConeExitUnclamped()
    {
        var withinCone = new Vector2(0.3f, 0.95f); // ~17.5deg from forward
        Vector3 clamped = CrossoverBurstMath.ComposeActiveVelocity(
            survivingVelocity: Vector3.Zero,
            heading: 0f,
            flickSign: +1,
            exitVector: withinCone,
            burstSpeed: BurstSpeed,
            forwardBurstScale: ForwardBurstScale,
            exitDeadzone: ExitDeadzone,
            maxExitAngleRadians: Mathf.DegToRad(45f));

        Vector3 unclamped = CrossoverBurstMath.ComposeActiveVelocity(
            survivingVelocity: Vector3.Zero,
            heading: 0f,
            flickSign: +1,
            exitVector: withinCone,
            burstSpeed: BurstSpeed,
            forwardBurstScale: ForwardBurstScale,
            exitDeadzone: ExitDeadzone);

        Assert.Equal(unclamped.X, clamped.X, precision: 3);
        Assert.Equal(unclamped.Z, clamped.Z, precision: 3);
    }

    // A narrow cone actually SUBSUMES the unclamped model's "never
    // backward" DeadImpulseFloor fallback: clamping the angle to within
    // +/-45deg of forward means forwardAmount is ALWAYS cos(<=45deg) > 0
    // after the clamp, so even a pure-backward exit vector gets bent into
    // the cone and produces a real forward+lateral burst — it can never
    // land in the near-zero dead-stop the unclamped path guards against
    // with its separate fallback branch. This is a strictly SAFER property
    // for a narrow-cone move (BehindTheBack), not a gap: there is no
    // direction the exit vector can point that dead-stops the move.
    [Fact]
    public void NarrowCone_BackwardExit_StillBendsIntoForwardBiasedBurst()
    {
        Vector3 result = CrossoverBurstMath.ComposeActiveVelocity(
            survivingVelocity: Vector3.Zero,
            heading: 0f,
            flickSign: +1,
            exitVector: new Vector2(0f, -1f), // pure backward push
            burstSpeed: BurstSpeed,
            forwardBurstScale: ForwardBurstScale,
            exitDeadzone: ExitDeadzone,
            maxExitAngleRadians: Mathf.DegToRad(45f));

        Assert.True(result.Z > 0.5f, $"expected the cone clamp to bend a backward push forward, got Z={result.Z}");
        Assert.True(new Vector2(result.X, result.Z).Length() > 0.5f, "expected a real burst, not a dead stop");
    }

    // #211 code-review fix: an exit vector pointing EXACTLY backward is the
    // degenerate pole where lateralAmount is a sum of two signed-zero
    // products (0 * -1 and -1 * 0 here) and forwardAmount is exactly -1.
    // MathF.Atan2(±0.0, -1) returns ±PI depending on that sign of zero, which
    // is an implementation detail of how the dot products round, NOT a
    // gameplay signal — left uncontrolled, the clamp could bend the burst to
    // either cone edge non-deterministically across builds, violating
    // ADR-0004 (server and client must compute identically). The fix picks
    // the bend side explicitly from flickSign instead: bend to the SAME side
    // the flick already committed to. This theory pins the SIGN of result.X
    // for both flick directions so a regression back to trusting Atan2's
    // sign-of-zero fails loudly.
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void NarrowCone_ExactBackwardPole_BendsTowardFlickCommittedSide(int flickSign)
    {
        Vector3 result = CrossoverBurstMath.ComposeActiveVelocity(
            survivingVelocity: Vector3.Zero,
            heading: 0f,
            flickSign: flickSign,
            exitVector: new Vector2(0f, -1f), // exactly backward: the Atan2 pole
            burstSpeed: BurstSpeed,
            forwardBurstScale: ForwardBurstScale,
            exitDeadzone: ExitDeadzone,
            maxExitAngleRadians: Mathf.DegToRad(45f));

        // rightAxis at heading 0 is (-1, 0) (see class doc), so a burst
        // toward the +1-flick's own committed side (positive lateralAmount)
        // lands on NEGATIVE world X, and the -1-flick's side lands on
        // POSITIVE world X — matching the DeadImpulseFloor fallback's own
        // `rightAxis * flickSign * burstSpeed` convention.
        if (flickSign >= 0)
        {
            Assert.True(result.X < -0.1f, $"expected a +1 flick to bend toward its own (negative-X) side, got X={result.X}");
        }
        else
        {
            Assert.True(result.X > 0.1f, $"expected a -1 flick to bend toward its own (positive-X) side, got X={result.X}");
        }
    }
}
