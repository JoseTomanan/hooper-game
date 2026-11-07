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
        var phase = _machine.Phase;
        int frame = _machine.FrameInPhase;
        bool accepted = _machine.Feint();
        ObserveShotFeintForHarness(phase, frame, accepted);
        if (!accepted) return false;
        if (shot is { Gather.Valid: true } && ball?.State == BallState.Held
            && ball.StateMachine.HolderPeerId == OwnPeerId)
            shot.Gather = shot.Gather with { Faking = true, FakeOffset = offset };
        return true;
    }

    partial void ObserveShotFeintForHarness(MovePhase phase, int frame, bool accepted);

    private void ConfirmShotGather(int token, ShotGatherState confirmed)
    {
        if (token == 0 || _machine.CurrentMove is not JumpShot shot || shot.BeginToken != token) return;
        // A matching invalidation can represent an award/loss whose transient
        // ball packet was skipped. Same-holder Held alone cannot identify it.
        // Older Begins cannot invalidate the current shot's attachment.
        if (!confirmed.Valid) { shot.Gather = default; return; }
        if (!shot.Gather.Valid || !confirmed.EntryOffset.IsFinite() || !confirmed.FakeOffset.IsFinite()
            || !float.IsFinite(confirmed.DribblePhase)) return;
        // Stale Startup packets cannot undo a locally accepted fake or rewind
        // its eight-tick clock. Only the anchors for this Begin are confirmed.
        shot.Gather = shot.Gather with { EntryOffset = confirmed.EntryOffset, Hand = confirmed.Hand,
            DribblePhase = confirmed.DribblePhase, Faking = shot.Gather.Faking || confirmed.Faking,
            FakeOffset = confirmed.Faking ? confirmed.FakeOffset : shot.Gather.FakeOffset };
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
