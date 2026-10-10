using Godot;
using Hooper.Moves;
using Hooper.Player;

namespace Hooper.Player
{
    public partial class PlayerController
    {
        internal void StepProductionContactPairForHarness(PlayerController other, double delta,
            Vector2 firstInput, Vector2 secondInput)
        {
            _pendingInput = _pendingRawStick = firstInput;
            other._pendingInput = other._pendingRawStick = secondInput;
            TickServerContactPair(other, delta);
        }
        internal bool BeginProductionContactMoveForHarness(CommittedMove move) => BeginCommittedMove(move);
        internal int ProductionContactPairStepsForHarness => _contactPairSteps;
        internal int ProductionContactMotionStepsForHarness => _contactMotionSteps;
        internal bool ProductionContactCommittedForHarness => _lastContactWasCommitted;
        internal int ProductionContactBufferedInputsForHarness => _buffer.Count;
        internal void SeedProductionContactRawForHarness(Vector3 position, float heading)
        {
            // Adapter proof invokes the actual receive method; ENet delivery is separately
            // exercised by NetProductionContactTest's three peers.
            ReceiveState(0, position, Vector3.Zero, (int)MovePhase.Inactive, 0, "", 0,
                heading, (int)HandSide.Left, false, false, 0, false);
            // Presentation consumption must not invalidate the persistent contact sample.
            _hasNewState = false;
        }
        internal void ClearProductionContactRawForHarness() => _hasContactSnapshot = false;
        internal void ReconcileProductionContactEmptyForHarness(double delta) =>
            ReconcileFromServer(Vector3.Zero, Vector3.Zero, 0, Mathf.Pi, delta);
    }
}

namespace Hooper.Ball
{
    public partial class BallController
    {
        // Calls the shipped candidate enumeration and pickup rule; no copied contest policy.
        internal int ResolveProductionContactRecoveryForHarness() => ResolveLooseBallRecovery();
    }
}
