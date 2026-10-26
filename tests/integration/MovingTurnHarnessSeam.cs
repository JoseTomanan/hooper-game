using Godot;
using Hooper.Moves;

namespace Hooper.Player;

// Snapshot/transport adapters only: the simulation and buffered replay remain
// the shipped Move and ReconcileFromServer implementations.
public partial class PlayerController
{
    internal readonly record struct MovingTurnSnapshot(Vector3 Position, Vector3 Velocity,
        float Heading, HeadingMath.PivotState Pivot, bool Stance, bool Post, bool Established);
    internal MovingTurnSnapshot CaptureMovingTurnForHarness() => new(GlobalPosition, Velocity,
        Heading, _pivot, _effectiveStance, _effectivePost, _postEstablished);
    internal int ClearMovingTurnHistoryForHarness()
    { _buffer.Acknowledge(_buffer.LastSequence); return _buffer.LastSequence; }
    internal int MovingTurnLastSequenceForHarness => _buffer.LastSequence;
    internal void RecordMovingTurnForHarness(PredictionBuffer.InputFrame frame) =>
        _buffer.Record(frame.Movement, frame.Sprint, frame.Protect, frame.PostureHeld,
            frame.StanceEligible, frame.PostHeld, frame.PostEligible, frame.RimSample,
            translationLocked: frame.TranslationLocked);
    internal void ReconcileMovingTurnForHarness(MovingTurnSnapshot snapshot, int ack, double delta)
    {
        ReceiveState(ack, snapshot.Position, snapshot.Velocity, (int)MovePhase.Inactive, 0, "", 0,
            snapshot.Heading, (int)HandSide.Right, false, snapshot.Pivot.HasLatch,
            snapshot.Pivot.LatchedYaw, false, false, snapshot.Stance, snapshot.Post, snapshot.Established);
        ReconcileFromServer(snapshot.Position, snapshot.Velocity, ack, snapshot.Heading, delta);
        _hasNewState = false;
    }
    internal ContactOpponent? MovingTurnOpponentForHarness => GetContactOpponent(server: false);
}
