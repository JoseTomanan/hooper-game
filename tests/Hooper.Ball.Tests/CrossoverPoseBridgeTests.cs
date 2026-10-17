using Godot;
using Hooper.Player;

namespace Hooper.Ball.Tests;

public class CrossoverPoseBridgeTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    public void EndpointsPreserveEveryBoneTransformAndScale(float weight)
    {
        int[] parents = [-1, 0, 1];
        var anchor = Leg(0, .2f, 1.3f);
        var animated = Leg(1, 1.1f, 1.3f);
        var bridge = new CrossoverPoseBridge(parents, [new(0, 1, 2)]);
        var actual = bridge.Blend(anchor, animated, weight).Pose;
        var expected = weight == 0 ? anchor : animated;
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(expected[i].Origin.DistanceTo(actual[i].Origin) < .00001f);
            Assert.True(expected[i].Basis.X.DistanceTo(actual[i].Basis.X) < .00001f);
            Assert.True(expected[i].Basis.Y.DistanceTo(actual[i].Basis.Y) < .00001f);
            Assert.True(expected[i].Basis.Z.DistanceTo(actual[i].Basis.Z) < .00001f);
        }
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.3f)]
    public void ReachableAnkleFollowsTheInterpolatedGroundTarget(float scale)
    {
        int[] parents = [-1, 0, 1];
        var anchor = Leg(0, -.5f, scale);
        var animated = Leg(1, .5f, scale);
        var a = CrossoverPoseBridge.Forward(parents, anchor);
        var b = CrossoverPoseBridge.Forward(parents, animated);
        var bridge = new CrossoverPoseBridge(parents, [new(0, 1, 2)]);
        foreach (float weight in new[] { .1f, .25f, .5f, .75f, .9f })
        {
            var result = bridge.Blend(anchor, animated, weight);
            var world = CrossoverPoseBridge.Forward(parents, result.Pose);
            Assert.True(world[2].Origin.DistanceTo(a[2].Origin.Lerp(b[2].Origin, weight)) < .0001f);
            Assert.Equal(0, result.ClampCount);
            Assert.True(result.MaxReachError < .0001f);
        }
    }

    [Fact]
    public void SupportPathCanStayLinearWhileBodyAndArmsEase()
    {
        int[] parents = [-1, 0, 1];
        var anchor = Leg(0, -.5f, 1);
        var animated = Leg(1, .5f, 1);
        var a = CrossoverPoseBridge.Forward(parents, anchor);
        var b = CrossoverPoseBridge.Forward(parents, animated);
        var bridge = new CrossoverPoseBridge(parents, [new(0, 1, 2, IsSupport: true)]);
        var result = bridge.Blend(anchor, animated, .104f, supportWeight: .2f);
        var actual = CrossoverPoseBridge.Forward(parents, result.Pose)[2].Origin;
        Assert.True(actual.DistanceTo(a[2].Origin.Lerp(b[2].Origin, .2f)) < .0001f);
        Assert.True(actual.DistanceTo(a[2].Origin.Lerp(b[2].Origin, .104f)) > .05f);
        Assert.Equal(0, result.ClampCount);
    }

    [Fact]
    public void UnreachableTargetRetainsFiniteBonesAndReportsResidual()
    {
        int[] parents = [-1, 0, 1, 2];
        Transform3D[] anchor = [Transform3D.Identity, At(new Vector3(2, 0, 0)), At(new Vector3(0, -.2f, 0)), At(new Vector3(0, -.2f, 0))];
        var animated = (Transform3D[])anchor.Clone();
        animated[0] = new Transform3D(new Basis(Vector3.Back, Mathf.Pi), Vector3.Zero);
        var result = new CrossoverPoseBridge(parents, [new(1, 2, 3)]).Blend(anchor, animated, .5f);
        Assert.True(result.ClampCount > 0);
        Assert.True(result.MaxReachError > 1f);
        Assert.All(result.Pose, pose => Assert.True(pose.IsFinite()));
    }

    [Fact]
    public void ZeroLengthChainRemainsFinite()
    {
        Transform3D[] pose = [Transform3D.Identity, Transform3D.Identity, Transform3D.Identity];
        var result = new CrossoverPoseBridge([-1, 0, 1], [new(0, 1, 2)]).Blend(pose, pose, .5f);
        Assert.All(result.Pose, bone => Assert.True(bone.IsFinite()));
    }

    [Fact]
    public void CrossingHalfTurnDoesNotSwitchInterpolationBranch()
    {
        var bridge = new CrossoverPoseBridge([-1], []);
        Transform3D[] anchor = [Transform3D.Identity];
        Quaternion? previous = null;
        foreach (float degrees in new[] { 170f, 175f, 180f, 185f, 190f })
        {
            Transform3D[] animated = [new(new Basis(Vector3.Back, Mathf.DegToRad(degrees)), Vector3.Zero)];
            var current = bridge.Blend(anchor, animated, .5f).Pose[0].Basis.GetRotationQuaternion();
            if (previous.HasValue)
                Assert.True(previous.Value.AngleTo(current) < Mathf.DegToRad(4), "The interpolated body must not reverse at the quaternion hemisphere boundary.");
            previous = current;
        }
    }

    private static Transform3D At(Vector3 point) => new(Basis.Identity, point);
    private static Transform3D[] Leg(float x, float angle, float scale) =>
    [
        new Transform3D(new Basis(Vector3.Back, angle).Scaled(Vector3.One * scale), new Vector3(x, 0, 0)),
        At(new Vector3(0, -1, 0)), At(new Vector3(0, -1, 0))
    ];
}
