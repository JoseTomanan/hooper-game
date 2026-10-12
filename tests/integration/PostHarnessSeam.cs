using Godot;
using Hooper.Moves;

namespace Hooper.Player
{

public partial class PlayerController
{
    internal MovePhase PostPhaseForHarness => _machine.Phase;
    internal int PostFrameForHarness => _machine.FrameInPhase;
    internal string PostMoveForHarness => _machine.CurrentMove?.Id ?? "";
    internal float PostTargetForHarness => _turnaroundTargetHeading;
    internal int PostTokenForHarness => _turnaroundToken;
    internal bool PostEstablishedForHarness => _postEstablished;
    internal void PostDispatchForHarness(string moveId, int token = 0)
        => ApplyRequestedMove(moveId, 999f, true, token);
    internal void PostPendingForHarness(bool held, Vector2 input)
    { _pendingPostRequest = held; _pendingInput = _pendingRawStick = input; }
    internal void PostResetMoveForHarness() => _machine.ForceState(MovePhase.Inactive, 0, null);
    internal bool PostFeintForHarness() => _machine.Feint();
    internal bool PostReleaseForHarness => JustReleasedJumpShot;
    internal int PostRecordForHarness(Vector2 movement, bool held, bool eligible, Vector3 rim,
        MovePhase phase = MovePhase.Inactive, float target = 0, int token = 0)
        => _buffer.Record(movement, postHeld: held, postEligible: eligible, rimSample: rim,
            turnaroundPhase: phase, turnaroundTarget: target, turnaroundToken: token);
    internal void PostReconcileForHarness(Vector3 position, Vector3 velocity, float heading, int ack,
        bool post, bool established, double delta, float turnTarget = 0, int token = 0)
    {
        ReceiveState(ack, position, velocity, token != 0 ? (int)MovePhase.Startup : (int)MovePhase.Inactive,
            0, token != 0 ? "turnaround" : "", turnTarget, heading, (int)HandSide.Right,
            false, false, 0, false, false, false, post, established, token);
        ReconcileFromServer(position, velocity, ack, heading, delta);
        _hasNewState = false;
    }
    internal void PostTurnMotionForHarness(MovePhase phase, float target, double delta)
        => TickTurnaroundMotion(phase, target, delta, null);
}
}

namespace Hooper.Ball
{
    public partial class BallController
    {
        internal void PostTickHeldForHarness() => TickHeld();
        internal void PostResolveOobForHarness() => ResolvePlayerOutOfBounds();
        internal void PostAwardForHarness(int peerId) => AwardPossession(peerId);
    }
}
