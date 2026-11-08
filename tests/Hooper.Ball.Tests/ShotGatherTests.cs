using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace Hooper.Ball.Tests;

public class ShotGatherTests
{
    [Fact]
    public void FirstGatherFrameRetainsActualIncomingDribbleCenter()
    {
        Vector3 root = new(2f, .25f, 4f);
        Vector3 incoming = new(2.4f, .12f, 4.5f);
        var state = new ShotGatherState(true, incoming - root, HandSide.Left, .5f);
        Vector3 actual = ShotGather.Position(state, MovePhase.Startup, 1, root, 1.3f,
            new(2.4f, 1f, 4.5f), new(.1f, 2f, .3f));
        Assert.True(actual.DistanceTo(incoming) < .000001f, $"gather teleported from {incoming} to {actual}");
    }

    [Fact]
    public void SetFrameUsesRightShootingTargetForEitherEnteringHand()
    {
        foreach (var hand in new[] { HandSide.Left, HandSide.Right })
        {
            var state = new ShotGatherState(true, new(.4f, -.5f, .5f), hand, .4f);
            Vector3 root = new(1f, 1f, 2f), target = new(.1f, 1.5f, .3f);
            Vector3 actual = ShotGather.Position(state, MovePhase.Startup, 8, root, .7f, Vector3.Zero, target);
            Vector3 expected = root + new Basis(Vector3.Up, .7f) * target;
            Assert.True(actual.DistanceTo(expected) < .000001f);
        }
    }

    [Fact]
    public void FakeStartsAtLastReachedCenterAndReturnsDuringEightTickRecovery()
    {
        Vector3 root = new(1f, .998f, 3f), reached = new(.8f, 1.7f, 3.3f), held = new(.56f, 1f, 3.5f);
        var state = new ShotGatherState(true, new(.4f, -.2f, .5f), HandSide.Right, .5f, true, reached - root);
        Assert.True(ShotGather.Position(state, MovePhase.Recovery, 13, root, 1f, held, Vector3.Zero).DistanceTo(reached) < .000001f);
        Assert.True(ShotGather.Position(state, MovePhase.Recovery, 19, root, 1f, held, Vector3.Zero).DistanceTo(held) < .000001f);
        float previous = float.MaxValue;
        for (int frame = 13; frame <= 19; frame++)
        {
            var p = ShotGather.Position(state, MovePhase.Recovery, frame, root, 1f, held, Vector3.Zero);
            Assert.True(p.DistanceTo(held) <= previous);
            previous = p.DistanceTo(held);
        }
    }

    [Fact]
    public void ActiveZeroUsesTheEndpointAndNoLaterPhaseReattaches()
    {
        var state = new ShotGatherState(true, Vector3.Zero, HandSide.Right, 0);
        Vector3 root = new(2, 1, 3), target = new(-.2f, 1.3f, .1f), held = new(2, 1, 3.5f);
        var last = ShotGather.Position(state, MovePhase.Startup, 17, root, .4f, held, target);
        var release = ShotGather.Position(state, MovePhase.Active, 0, root, .4f, held, target);
        Assert.True(last.DistanceTo(release) < .000001f);
        Assert.Equal(held, ShotGather.Position(state, MovePhase.Recovery, 0, root, .4f, held, target));
        Assert.Equal(held, ShotGather.Position(default, MovePhase.Startup, 8, root, .4f, held, target));
    }

    [Fact]
    public void ReleasedCorrectionRetainsAbsoluteOriginThroughRecovery()
    {
        var origin = new Vector3(2, 2.2f, 4);
        var state = new ShotGatherState(true, Vector3.Zero, HandSide.Right, 0, Released: true, ReleaseOrigin: origin);
        Assert.Equal(origin, ShotGather.Position(state, MovePhase.Recovery, 10,
            new(3, 1, 4), .7f, new(3, 1, 4.5f), new(.1f, 1.2f, .2f)));
    }
}
