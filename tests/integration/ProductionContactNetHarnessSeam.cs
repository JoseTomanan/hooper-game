using System;
using Godot;
using Hooper.Moves;

namespace Hooper.Player;

public partial class PlayerController
{
    internal int ContactNetPeerId => OwnPeerId;
    internal bool ContactNetNewState => _hasNewState;
    internal bool ContactNetHasSnapshot => _hasContactSnapshot;
    internal int ContactNetPending => _buffer.Count;
    internal Vector3 ContactNetRawPosition => _serverPos;
    internal Vector3 ContactNetRawVelocity => _serverVel;
    internal float ContactNetRawHeading => _serverHeading;
    internal MovePhase ContactNetPhase => _machine.Phase;
    internal void ContactNetObserve(Action<ContactOpponent?, ContactResponse, bool> observer) =>
        _contactMotionObserver = observer;
    internal void ContactNetObserveReconciliation(Action<Vector3, Vector3, int> observer) =>
        _contactReconciliationObserver = observer;
    internal bool ContactNetBegin(string move)
    {
        CommittedMove request = move == "gather" ? new DriveGather() : new Crossover(1);
        if (!BeginCommittedMove(request)) return false;
        // Same production request as local input: reliable RPC, owner sender validation.
        // Source: https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#remote-procedure-calls
        RpcId(1, MethodName.RequestBeginMove, move == "gather" ? "drivegather" : "crossover", move == "gather" ? 0f : 1f, false);
        return true;
    }
}
