using Godot;
using Hooper.Moves;

namespace Hooper.Player;

public partial class PlayerController
{
    internal void StanceNetRawSnapshot(Vector3 position, Vector3 velocity, float heading)
    { _serverPos = position; _serverVel = velocity; _serverHeading = heading; _hasContactSnapshot = true; }
    internal ContactOpponent? StanceNetOpponent => GetContactOpponent(server: false);
    internal bool StanceNetReceived => _hasContactSnapshot && _serverStance;
    internal void StanceNetMutateHistory(string mutation)
    {
        var frames = new System.Collections.Generic.List<PredictionBuffer.InputFrame>(_buffer.ReplayInputs());
        int sequence = _buffer.LastSequence;
        _buffer.Acknowledge(sequence);
        foreach (var frame in frames)
            _buffer.Record(frame.Movement, frame.Sprint, frame.Protect,
                mutation == "drop-posture" ? false : frame.PostureHeld,
                mutation == "current-eligibility" ? StanceEligible() : frame.StanceEligible);
    }
    internal void StanceNetCorrection(Vector3 position, Vector3 velocity, float heading, int ack, double dt)
    {
        ReceiveState(ack, position, velocity, (int)MovePhase.Inactive, 0, "", 0,
            heading, (int)HandSide.Right, false, false, 0, false, false, false);
        ReconcileFromServer(position, velocity, ack, heading, dt);
        _hasNewState = false;
    }
}
