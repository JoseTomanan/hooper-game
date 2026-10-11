namespace Hooper.Player
{
    public partial class PlayerController
    {
        internal void SprintTickHostForHarness(double delta) => TickServerOwnPlayer(delta);
        internal bool SprintBeginShotForHarness()
        { ApplyRequestedMove("jumpshot", 0, false); return _machine.CurrentMove is Hooper.Moves.JumpShot; }
        internal bool SprintPumpFakeForHarness()
        {
            if (!BeginJumpShotForHarness()) return false;
            while (_machine.FrameInPhase < _machine.CurrentMove.FrameData.FeintMinStartupFrames) _machine.Tick();
            return _machine.Feint();
        }
        internal void SprintBindBallForHarness(Hooper.Ball.BallController ball) => _ball = ball;
        internal void SprintTickClientForHarness(double delta, bool suppressCorrection = false)
        { if (suppressCorrection) _hasNewState = false; TickClientOwnPlayer(delta); }
        internal void SprintTickServerForHarness(double delta) => TickServerRemotePlayer(delta);
        internal int SprintAckForHarness => _serverAckedSeq;
        internal int SprintPendingForHarness => _buffer.Count;
        internal int SprintPreparePendingForHarness()
        { _buffer.Acknowledge(_buffer.LastSequence); _hasNewState = false; return _buffer.LastSequence; }
        internal void SprintForceCorrectionForHarness(Godot.Vector3 position, Godot.Vector3 velocity, float heading, int ack, double delta)
        {
            ReceiveState(ack, position, velocity, (int)Hooper.Moves.MovePhase.Inactive, 0, "", 0,
                heading, (int)HandSide.Right, false, false, 0, false);
            ReconcileFromServer(position, velocity, ack, heading, delta);
            _hasNewState = false;
        }
        internal void SprintFinishMoveForHarness() { while (_machine.IsActive) _machine.Tick(); }
        internal void SprintEnterActiveForHarness()
        { while (_machine.Phase == Hooper.Moves.MovePhase.Startup) _machine.Tick(); }
    }
}
namespace Hooper.Ball
{
    public partial class BallController
    {
        internal bool SprintShootForHarness(Hooper.Player.PlayerController holder) => ApplyShootLocally(holder);
        internal void SprintResolveStealForHarness(float phase)
        {
            _dribble.Reset();
            _dribble.Advance(phase * _dribble.Period);
            ResolveStealAttempts();
        }
    }
}
