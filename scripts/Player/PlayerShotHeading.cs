using System;
using Godot;
using Hooper.Moves;

namespace Hooper.Player;

public partial class PlayerController
{
    // Retained after phase completion so late snapshots can repair history.
    private int _lastShotToken, _serverShotToken;
    private float _lastShotTarget, _serverShotTarget;

    private float SnapshotShotTarget()
    {
        float heading = float.IsFinite(Heading) ? Heading : 0f;
        var ball = GetBall();
        if (ball == null) return heading;
        Vector3 direction = ball.RimCenter - GlobalPosition;
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Z)
            || new Vector2(direction.X, direction.Z).LengthSquared() < .0001f) return heading;
        return MathF.Atan2(direction.X, direction.Z);
    }

    private void ConfirmShotTarget(int token, float target)
    {
        if (token == 0) return;
        _buffer.ConfirmShotTarget(token, target);
        if (_machine.CurrentMove is JumpShot shot && shot.BeginToken == token)
            shot.TargetHeading = target;
    }

    // Reliable per-request results avoid treating stale Inactive snapshots as
    // rejection of a newer Begin. Authority and matching RPC signatures:
    // https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#remote-procedure-calls
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveShotBeginResult(int token, bool accepted, float target)
    {
        if (accepted) ConfirmShotTarget(token, target);
        else
        {
            _buffer.RejectShot(token);
            if (_machine.CurrentMove is JumpShot shot && shot.BeginToken == token)
            {
                _machine.ForceState(MovePhase.Inactive, 0, null);
                HandSide = _serverHandSide;
            }
        }
    }

    /// <summary>Motion only: no machine advancement, classification or payload.</summary>
    private void TickShotHeadingMotion(MovePhase phase, float target, double delta, ContactOpponent? opponent)
    {
        if (phase == MovePhase.Startup)
        {
            Heading = HeadingMath.RotateTowardFixedYaw(Heading, target, delta, MaxTurnRateDeg, BackTurnSlowFactor);
            Velocity = Vector3.Zero;
        }
        else if (phase == MovePhase.Recovery)
            Velocity = Velocity.MoveToward(Vector3.Zero, Decel * (float)delta);
        IntegrateContactMotion(delta, opponent);
    }
}
