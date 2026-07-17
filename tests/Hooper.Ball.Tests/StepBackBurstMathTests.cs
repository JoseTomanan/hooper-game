using Godot;
using Hooper.Player;

namespace Hooper.Ball.Tests;

// Issue #197 — step-back's burst composition, which reuses
// CrossoverBurstMath.ComposeActiveVelocity (issue #198) via a heading flip
// (see StepBackBurstMath's doc for why that's a safe reuse, not a hack).
//
// Heading is fixed at 0 throughout: HeadingMath.Forward(0) = (0, 1) (true
// forward is +Z), so true backward is (0, -1) — matching the sign
// conventions CrossoverBurstMathTests already establishes for this codebase.
public class StepBackBurstMathTests
{
    private const float BurstSpeed = 10f;
    private const float ExitDeadzone = 0.15f;
    private const float ConeDegrees = 60f;

    // No steering input (below deadzone): a plain, straight-back hop — the
    // most common input (RS held down, nothing else) must never produce a
    // silent zero burst (see class doc's "no flick-sign fallback" rationale).
    [Fact]
    public void NeutralExit_ProducesStraightBackwardBurst()
    {
        Vector3 result = StepBackBurstMath.ComposeActiveVelocity(
            heading: 0f,
            exitVector: Vector2.Zero,
            burstSpeed: BurstSpeed,
            exitDeadzone: ExitDeadzone,
            maxExitAngleRadians: Mathf.DegToRad(ConeDegrees));

        Assert.Equal(0f, result.X, precision: 3);
        Assert.Equal(-BurstSpeed, result.Z, precision: 3);
    }

    // Pushing the left stick to the player's true world-right while stepping
    // back must burst toward TRUE RIGHT (positive X) with no FORWARD leak
    // (Z <= 0 — a pure 90-degrees-from-backward lateral push composes to
    // ~zero forward component, mirroring CrossoverBurstMath's own
    // "Stationary_PushLateral" case, not a guaranteed nonzero backward lean).
    // This is the handedness regression test for the "flip the heading"
    // reuse: internally rightAxis becomes the true LEFT axis (see class
    // doc), so a naive read of the internals would get the X sign backwards.
    [Fact]
    public void ExitVectorTrueRight_BurstsTowardTheRealRightWithNoForwardLeak()
    {
        Vector3 result = StepBackBurstMath.ComposeActiveVelocity(
            heading: 0f,
            exitVector: new Vector2(1f, 0f), // true world +X (player's right at heading 0)
            burstSpeed: BurstSpeed,
            exitDeadzone: ExitDeadzone,
            maxExitAngleRadians: Mathf.DegToRad(90f)); // wide enough to admit a pure-lateral exit

        Assert.True(result.X > 0f, $"expected a positive (true-right) X component, got {result.X}");
        Assert.True(result.Z <= 1e-3f, $"expected no forward (positive Z) leak, got {result.Z}");
    }

    // Mirror of the above: true world-left must burst toward true left.
    [Fact]
    public void ExitVectorTrueLeft_BurstsTowardTheRealLeftWithNoForwardLeak()
    {
        Vector3 result = StepBackBurstMath.ComposeActiveVelocity(
            heading: 0f,
            exitVector: new Vector2(-1f, 0f),
            burstSpeed: BurstSpeed,
            exitDeadzone: ExitDeadzone,
            maxExitAngleRadians: Mathf.DegToRad(90f));

        Assert.True(result.X < 0f, $"expected a negative (true-left) X component, got {result.X}");
        Assert.True(result.Z <= 1e-3f, $"expected no forward (positive Z) leak, got {result.Z}");
    }

    // "Back / back-left / back-right side-steps ONLY" (issue #197) — a
    // straight-forward push must never yield a forward (positive Z) burst,
    // regardless of how hard it's held. The narrow cone clamps it toward
    // true-backward instead.
    [Fact]
    public void ExitVectorPointingTrueForward_NeverBurstsForward()
    {
        Vector3 result = StepBackBurstMath.ComposeActiveVelocity(
            heading: 0f,
            exitVector: new Vector2(0f, 1f), // true forward push — the degenerate pole case
            burstSpeed: BurstSpeed,
            exitDeadzone: ExitDeadzone,
            maxExitAngleRadians: Mathf.DegToRad(ConeDegrees));

        Assert.True(result.Z <= 0f, $"expected no forward component from a forward-held stick, got Z={result.Z}");
        Assert.True(new Vector2(result.X, result.Z).Length() > 0f,
            "expected a nonzero burst (the pole tie-break), not a silent dead stop");
    }

    // Cone clamp: a hard lateral push (90° from true-backward) with a
    // narrower cone must still stay within maxExitAngleRadians of true
    // backward — the lateral component cannot dominate to the point where
    // more than half the impulse's angle sits outside the declared cone.
    [Fact]
    public void ExitVectorBeyondCone_ClampsToConeBoundary()
    {
        const float coneRad = 0.5f; // ~28.6 degrees — narrow
        Vector3 result = StepBackBurstMath.ComposeActiveVelocity(
            heading: 0f,
            exitVector: new Vector2(1f, 0f), // 90 degrees from true-backward
            burstSpeed: BurstSpeed,
            exitDeadzone: ExitDeadzone,
            maxExitAngleRadians: coneRad);

        float impulseAngleFromBackward = Mathf.Atan2(result.X, -result.Z);
        Assert.True(Mathf.Abs(impulseAngleFromBackward) <= coneRad + 0.01f,
            $"expected the burst within {coneRad} rad of true-backward, got angle={impulseAngleFromBackward}");
    }

    // Magnitude sanity: an unsteered step-back always produces exactly
    // burstSpeed of velocity — no silent scaling.
    [Fact]
    public void NeutralExit_MagnitudeEqualsBurstSpeed()
    {
        Vector3 result = StepBackBurstMath.ComposeActiveVelocity(
            heading: 0f,
            exitVector: Vector2.Zero,
            burstSpeed: BurstSpeed,
            exitDeadzone: ExitDeadzone,
            maxExitAngleRadians: Mathf.DegToRad(ConeDegrees));

        Assert.Equal(BurstSpeed, new Vector2(result.X, result.Z).Length(), precision: 3);
    }
}
