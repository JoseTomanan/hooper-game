using Godot;
using Hooper.Player;

namespace Hooper.Ball.Tests;

// Issue #231 — the euro-step's Active-phase composition: forward progress
// toward the (rim-bent) heading RETAINED, plus a fixed lateral hop in the
// body-relative read direction.
//
// Heading is fixed at 0 throughout so the shared body basis reduces to exact
// float arithmetic — no epsilon tolerance needed (matches CrossoverBurstMathTests /
// DriveGatherMathTests). At heading 0: HeadingMath.Forward(0) == (0,1) and
// HandStateResolver.BurstWorldDir(0, +1) == (-1,0), so a +1 (player's-right)
// read displaces toward -X and forward drive is +Z.
public class EuroStepMathTests
{
    // Candidate magnitudes (NOT final — catalogued in #238's consolidated tuning
    // pass, dialed with the rest of the move set). Held here only to make the
    // arithmetic in these contract tests concrete.
    private const float ForwardDriveSpeed = 9f;
    private const float LateralHopSpeed = 5f;

    // Right read from a standstill: forward drive along heading (+Z) plus a
    // fixed lateral hop to the player's right (-X at heading 0).
    [Fact]
    public void RightRead_FromStandstill_DrivesForwardAndHopsRight()
    {
        Vector3 result = EuroStepMath.ComposeActiveVelocity(
            survivingVelocity: Vector3.Zero,
            heading: 0f,
            lateralSign: +1,
            forwardDriveSpeed: ForwardDriveSpeed,
            lateralHopSpeed: LateralHopSpeed);

        Assert.Equal(-LateralHopSpeed, result.X, precision: 4); // player's right == -X at heading 0
        Assert.Equal(ForwardDriveSpeed, result.Z, precision: 4); // forward toward the rim
        Assert.Equal(0f, result.Y, precision: 4);
    }

    // The read direction flips the lateral component's sign and nothing else —
    // a left read mirrors a right read across the drive line.
    [Fact]
    public void LeftRead_MirrorsRightReadLaterally()
    {
        Vector3 result = EuroStepMath.ComposeActiveVelocity(
            survivingVelocity: Vector3.Zero,
            heading: 0f,
            lateralSign: -1,
            forwardDriveSpeed: ForwardDriveSpeed,
            lateralHopSpeed: LateralHopSpeed);

        Assert.Equal(+LateralHopSpeed, result.X, precision: 4); // player's left == +X at heading 0
        Assert.Equal(ForwardDriveSpeed, result.Z, precision: 4); // forward is unchanged by the read side
    }

    // Surviving forward momentum from the gather is CARRIED, not re-zeroed — the
    // drive keeps its speed and the forward drive term adds on top (the
    // "SET absolute, never lose the survivor" contract every burst move shares).
    [Fact]
    public void SurvivingForwardMomentum_IsRetainedAndAddedTo()
    {
        var survivingVelocity = new Vector3(0f, 0f, 6f); // gather left 6 m/s toward the rim
        Vector3 result = EuroStepMath.ComposeActiveVelocity(
            survivingVelocity,
            heading: 0f,
            lateralSign: +1,
            forwardDriveSpeed: ForwardDriveSpeed,
            lateralHopSpeed: LateralHopSpeed);

        Assert.Equal(survivingVelocity.Z + ForwardDriveSpeed, result.Z, precision: 4);
        Assert.Equal(-LateralHopSpeed, result.X, precision: 4);
    }

    // The defining "not a lateral shuffle" property: the euro-step always makes
    // forward progress toward the rim, so the chained Layup finish stays
    // reachable. Proven from a standstill (isolating the composed impulse from
    // any surviving momentum) across both read sides.
    [Theory]
    [InlineData(+1)]
    [InlineData(-1)]
    public void AlwaysMakesForwardProgress_SoTheFinishStaysReachable(int lateralSign)
    {
        Vector3 result = EuroStepMath.ComposeActiveVelocity(
            survivingVelocity: Vector3.Zero,
            heading: 0f,
            lateralSign,
            forwardDriveSpeed: ForwardDriveSpeed,
            lateralHopSpeed: LateralHopSpeed);

        Assert.True(result.Z > 0f, $"expected forward progress toward the rim, got Z={result.Z}");
    }

    // The evade is real: the lateral displacement is nonzero. Guards against a
    // composition that silently drops the hop and degenerates into the plain
    // straight-line drive-gather (which would be a vacuous "euro-step").
    [Theory]
    [InlineData(+1)]
    [InlineData(-1)]
    public void LateralHop_IsNonZero_NotASilentStraightDrive(int lateralSign)
    {
        Vector3 result = EuroStepMath.ComposeActiveVelocity(
            survivingVelocity: Vector3.Zero,
            heading: 0f,
            lateralSign,
            forwardDriveSpeed: ForwardDriveSpeed,
            lateralHopSpeed: LateralHopSpeed);

        Assert.True(Mathf.Abs(result.X) > 0f, $"expected a real lateral hop, got X={result.X}");
    }
}
