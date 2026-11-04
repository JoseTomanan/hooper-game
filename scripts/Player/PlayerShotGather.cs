using Godot;
using Hooper.Ball;
using Hooper.Moves;

namespace Hooper.Player;

public partial class PlayerController
{
    private ShotGatherState _serverShotGather;
    internal JumpShot SimulationShot => _machine.CurrentMove as JumpShot;
    internal int SimulationPeerId => OwnPeerId;
    internal bool SimulationIsLocalPlayer => IsLocalPlayer;
    internal MovePhase SimulationMovePhase => _machine.Phase;
    internal int SimulationMoveFrame => _machine.FrameInPhase;
    internal bool TryShotBallTarget(out Vector3 target)
    {
        var ball = GetBall();
        target = default;
        return ball != null && ball.TryShotDisplayTarget(this, out target);
    }

    private bool FeintWithGather()
    {
        var shot = SimulationShot;
        var ball = GetBall();
        Vector3 offset = ball == null ? Vector3.Zero : ball.GlobalPosition - GlobalPosition;
        if (!_machine.Feint()) return false;
        if (shot is { Gather.Valid: true } && ball?.State == BallState.Held
            && ball.StateMachine.HolderPeerId == OwnPeerId)
            shot.Gather = shot.Gather with { Faking = true, FakeOffset = offset };
        return true;
    }

    private void ConfirmShotGather(int token, Vector3 entry, int hand, float dribble, bool faking, Vector3 fake)
    {
        if (token == 0 || !entry.IsFinite() || !fake.IsFinite() || !float.IsFinite(dribble)) return;
        if (_machine.CurrentMove is not JumpShot shot || shot.BeginToken != token || !shot.Gather.Valid) return;
        // Stale Startup packets cannot undo a locally accepted fake or rewind
        // its eight-tick clock. Only the anchors for this Begin are confirmed.
        shot.Gather = shot.Gather with { EntryOffset = entry, Hand = (HandSide)hand, DribblePhase = dribble,
            Faking = shot.Gather.Faking || faking, FakeOffset = faking ? fake : shot.Gather.FakeOffset };
    }

    private Godot.Collections.Array CaptureShotGatherSnapshot()
    {
        var gather = SimulationShot?.Gather ?? default;
        return new() { gather.Valid, gather.EntryOffset, (int)gather.Hand, gather.DribblePhase, gather.Faking, gather.FakeOffset };
    }

    private void ReceiveShotGatherSnapshot(Godot.Collections.Array values)
    {
        _serverShotGather = default;
        if (values == null || values.Count != 6) return;
        _serverShotGather = new(values[0].AsBool(), values[1].AsVector3(), (HandSide)values[2].AsInt32(),
            values[3].AsSingle(), values[4].AsBool(), values[5].AsVector3());
    }
}
