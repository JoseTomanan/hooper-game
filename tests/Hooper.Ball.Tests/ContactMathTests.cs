using Godot;
using Hooper.Player;
namespace Hooper.Ball.Tests;
public class ContactMathTests
{
    private static ContactOpponent Body(Vector3 position = default, Vector3 velocity = default, float heading = 0, long peer = 1) => new(position, velocity, heading, .5f, 2f, 1f, peer);
    [Fact]
    public void NullOpponentHasZeroCorrectionsEvenAtOrigin()
    {
        Assert.Equal(default(ContactResponse), ContactMath.Resolve(Body(), null, 1d / 60));
    }
    [Fact]
    public void SetRequiresSpeedAndFacingWithInclusiveBoundaries()
    {
        var settings = new ContactSettings(.5f, .75f);
        Assert.True(ContactMath.IsSet(Body(velocity: new(0, 0, .5f), heading: .75f), new(0, 2), settings));
        Assert.True(ContactMath.IsSet(Body(heading: MathF.BitDecrement(.75f)), new(0, 1), settings));
        Assert.False(ContactMath.IsSet(Body(heading: MathF.BitIncrement(.75f)), new(0, 1), settings));
        Assert.False(ContactMath.IsSet(Body(velocity: new(0, 0, MathF.BitIncrement(.5f))), new(0, 1), settings));
        Assert.True(ContactMath.IsSet(Body(velocity: new(0, 0, MathF.BitDecrement(.5f))), new(0, 1), settings));
        Assert.False(ContactMath.IsSet(Body(heading: MathF.PI), new(0, 1), settings));
        Assert.False(ContactMath.IsSet(Body(), Vector2.Zero, settings));
    }

    [Fact]
    public void SetDefenderAbsorbsDriveWithoutMoving()
    {
        var attacker = Body(new(0, 0, -2), new(0, 0, 6));
        var defender = Body(heading: MathF.PI, peer: 2);
        var response = ContactMath.Resolve(attacker, defender, .5);
        Assert.Equal(new Vector3(0, 0, -2), response.Self.Position);
        Assert.Equal(new Vector3(0, 0, -6), response.Self.Velocity);
        Assert.Equal(default, response.Opponent);
    }

    [Fact]
    public void CoincidentStationaryBodiesSeparateByStablePeerOrder()
    {
        var response = ContactMath.Resolve(Body(), Body(peer: 2), 0);
        Assert.Equal(new Vector3(.5f, 0, 0), response.Self.Position);
        Assert.Equal(new Vector3(-.5f, 0, 0), response.Opponent.Position);
        Assert.Equal(Vector3.Zero, response.Self.Velocity);
    }

    [Fact]
    public void CapsuleHeightIncludesCapsAndCenterOffsetDeterminesCapContact()
    {
        var self = Body() with { CapsuleRadius = .25f, CapsuleHeight = 1f, CapsuleCenterHeight = 0f };
        var other = Body(new(.3f, 1.1f, 0), peer: 2) with { CapsuleRadius = .75f, CapsuleHeight = 2f, CapsuleCenterHeight = 0f };
        // Segment half lengths .25 + .25, gap=.6, effective XZ radius=.8.
        var response = ContactMath.Resolve(self, other, 0);
        var swapped = ContactMath.Resolve(other, self, 0);
        Assert.Equal(response.Self, swapped.Opponent);
        Assert.Equal(response.Opponent, swapped.Self);
        Assert.InRange(response.Self.Position.X, -.250001f, -.249999f);
        Assert.InRange(response.Opponent.Position.X, .249999f, .250001f);
        Assert.Equal(default(ContactResponse), ContactMath.Resolve(self, other with { Position = new(.3f, 1.5f, 0) }, 0));
        Assert.Equal(default(ContactResponse), ContactMath.Resolve(self, other with { CapsuleCenterHeight = 2f }, 0));
    }

    [Fact]
    public void UnsupportedOrNonfiniteSnapshotsAreRejected()
    {
        var good = Body(peer: 2);
        foreach (var bad in new[] { Body() with { CapsuleRadius = 0 }, Body() with { CapsuleHeight = .9f }, Body() with { Velocity = new(0, 1, 0) }, Body() with { Heading = float.NaN }, Body() with { Position = new(float.PositiveInfinity, 0, 0) }, Body() with { CapsuleCenterHeight = float.NaN } })
            Assert.ThrowsAny<ArgumentException>(() => ContactMath.Resolve(bad, good, .1));
        Assert.ThrowsAny<ArgumentException>(() => ContactMath.Resolve(Body(), Body(), .1));
        foreach (double dt in new[] { -.1, double.NaN, double.PositiveInfinity })
            Assert.ThrowsAny<ArgumentException>(() => ContactMath.Resolve(Body(), good, dt));
        foreach (var settings in new[] { new ContactSettings(-1), new ContactSettings(float.NaN), new ContactSettings(.5f, -1), new ContactSettings(.5f, 4) })
            Assert.ThrowsAny<ArgumentException>(() => ContactMath.Resolve(Body(), good, .1, settings));
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1f, 0f)]
    public void UnsetDefenderSharesClosingImpulse(float defenderSpeed, float defenderHeading)
    {
        var response = ContactMath.Resolve(Body(new(0, 0, -2), new(0, 0, 6)), Body(velocity: new(0, 0, defenderSpeed), heading: defenderHeading, peer: 2), .5);
        float correction = (6 - defenderSpeed) / 2;
        Assert.Equal(new Vector3(0, 0, -correction), response.Self.Velocity);
        Assert.Equal(new Vector3(0, 0, correction), response.Opponent.Velocity);
    }
    [Fact]
    public void HighSpeedFullCrossingCannotPassThroughSetDefender()
    {
        var attacker = Body(new(0, 0, -10), new(0, 0, 1200));
        var response = ContactMath.Resolve(attacker, Body(heading: MathF.PI, peer: 2), 1d / 60);
        Assert.Equal(new Vector3(0, 0, -11), response.Self.Position);
        Assert.Equal(new Vector3(0, 0, -1200), response.Self.Velocity);
    }
    [Fact]
    public void NoHitTangentAndSeparatingProduceNoResponse()
    {
        var other = Body(peer: 2);
        foreach (var self in new[] { Body(new(-2, 0, 2), new(4, 0, 0)), Body(new(-2, 0, 1), new(4, 0, 0)), Body(new(0, 0, -1), new(0, 0, -6)) })
            Assert.Equal(default(ContactResponse), ContactMath.Resolve(self, other, 1));
    }
    [Fact]
    public void ExactEndContactChangesNextVelocityWithoutMovingEndpoint()
    {
        var response = ContactMath.Resolve(Body(new(0, 0, -2), new(0, 0, 6)), Body(heading: MathF.PI, peer: 2), 1d / 6);
        Assert.Equal(Vector3.Zero, response.Self.Position);
        Assert.Equal(new Vector3(0, 0, -6), response.Self.Velocity);
    }
    [Fact]
    public void GlancingContactPreservesTangentAndContinuesRemainingTick()
    {
        // Touching normal=(-.6,-.8), tangent=(.8,-.6), incident=(6,8)+tangent.
        var self = Body(new(-.6f, 0, -.8f), new(6.8f, 0, 7.4f));
        var other = Body(heading: MathF.Atan2(-.6f, -.8f), peer: 2);
        var response = ContactMath.Resolve(self, other, .1);
        var velocity = self.Velocity + response.Self.Velocity;
        Assert.InRange(velocity.X, .79999f, .80001f);
        Assert.InRange(velocity.Z, -.60001f, -.59999f);
        Assert.InRange(response.Self.Position.X, -.60001f, -.59999f);
        Assert.InRange(response.Self.Position.Z, -.80001f, -.79999f);
    }
    [Fact]
    public void FrozenSnapshotSequencesReplayBitIdenticallyAndSwapSymmetrically()
    {
        var first = Body(new(-.2f, 0, 0), new(3, 0, 1));
        var second = Body(new(.2f, 0, 0), new(-1, 0, 2), peer: 2);
        var expected = new List<ContactResponse>();
        for (int i = 0; i < 80; i++)
        {
            var result = ContactMath.Resolve(first, second, 1d / 60);
            var reversed = ContactMath.Resolve(second, first, 1d / 60);
            Assert.Equal(result.Self, reversed.Opponent);
            Assert.Equal(result.Opponent, reversed.Self);
            expected.Add(result);
            first = Advance(first, result.Self);
            second = Advance(second, result.Opponent);
        }
        for (int repeat = 0; repeat < 5; repeat++)
        {
            first = Body(new(-.2f, 0, 0), new(3, 0, 1));
            second = Body(new(.2f, 0, 0), new(-1, 0, 2), peer: 2);
            foreach (var result in expected)
            {
                Assert.Equal(result, ContactMath.Resolve(first, second, 1d / 60));
                first = Advance(first, result.Self);
                second = Advance(second, result.Opponent);
            }
        }
    }
    private static ContactOpponent Advance(ContactOpponent state, ContactCorrection correction) => state with { Position = state.Position + state.Velocity * (1f / 60) + correction.Position, Velocity = state.Velocity + correction.Velocity };
    [Fact]
    public void LongGlancingSweepRetainsSubmeterChordAndExactSwapSymmetry()
    {
        var self = Body(new(-1e8f, 0, .5f), new(2e8f, 0, 0));
        var other = Body(peer: 2);
        var response = ContactMath.Resolve(self, other, 1);
        Assert.True(response.Self.Velocity.X < 0);
        Assert.True(float.IsFinite(response.Self.Velocity.X));
        Assert.True(float.IsFinite(response.Self.Position.X));
        var swapped = ContactMath.Resolve(other, self, 1);
        Assert.Equal(response.Self, swapped.Opponent);
        Assert.Equal(response.Opponent, swapped.Self);
    }
    [Fact]
    public void UnrepresentableTravelIsRejectedInsteadOfReturningInfiniteCorrection()
    {
        Assert.ThrowsAny<ArgumentException>(() => ContactMath.Resolve(
            Body(new(0, 0, -2), new(0, 0, 6)), Body(heading: MathF.PI, peer: 2), 1e40));
    }
    [Fact]
    public void UnrepresentableVelocityCorrectionIsExplicitlyRejected()
    {
        var self = Body(new(-.9f, 0, -.375f), new(float.MaxValue, 0, float.MaxValue));
        var other = Body(heading: MathF.Atan2(-.9f, -.375f), peer: 2);
        Assert.ThrowsAny<ArgumentException>(() => ContactMath.Resolve(self, other, 1e-5));
    }
    [Fact]
    public void CancelledWorldCenterOffsetsAreDisjointInBothPairOrders()
    {
        var self = Body(new(0, 1e20f, 0)) with { CapsuleCenterHeight = -1e20f };
        var other = Body(new(.5f, 4, 0), peer: 2) with { CapsuleCenterHeight = 0 };
        Assert.Equal(default(ContactResponse), ContactMath.Resolve(self, other, 0));
        Assert.Equal(default(ContactResponse), ContactMath.Resolve(other, self, 0));
    }

    [Fact]
    public void HugeDiagonalCenterCrossingPreservesRawDeterminantAndSwapSymmetry()
    {
        var self = Body(new(-4.564524167960986e16f, 0, -2.394750498688205e16f), new(9.129048335921972e16f, 0, 4.78950099737641e16f));
        var other = Body(peer: 2);
        var response = ContactMath.Resolve(self, other, 1);
        Assert.True(response.Self.Velocity.X < 0);
        Assert.True(response.Self.Velocity.Z < 0);
        var swapped = ContactMath.Resolve(other, self, 1);
        Assert.Equal(response.Self, swapped.Opponent);
        Assert.Equal(response.Opponent, swapped.Self);
    }
}
