using System;
using System.Collections.Generic;
using Godot;
using Hooper.Moves;

namespace Hooper.Player;

// Harness-only access: production RPCs, prediction, phase gates and replay remain unchanged.
public partial class PlayerController
{
    private readonly Dictionary<int, bool> _replayHarnessCommitted = new();
    internal void ReplayHarnessAssertIdentity(int id) { if (OwnPeerId != id) throw new InvalidOperationException("identity mismatch"); }
    internal void ReplayHarnessServer(double delta, Vector2? input = null)
    {
        if (input.HasValue) { _pendingInput = input.Value; _pendingRawStick = input.Value; }
        TickServerRemotePlayer(delta);
    }
    internal void ReplayHarnessClient(double delta)
    {
        TickClientOwnPlayer(delta);
        _replayHarnessCommitted[_buffer.LastSequence] = _machine.IsActive;
    }
    internal string ReplayPhase => _machine.Phase.ToString();
    internal int ReplayFrame => _machine.FrameInPhase;
    internal int ReplayAck => _serverAckedSeq;
    internal void ReplayHarnessRemote() => TickClientRemotePlayer();
    internal Vector3 ReplayRawPosition => _serverPos;
    internal Vector3 ReplayRawVelocity => _serverVel;
    internal float ReplayRawHeading => _serverHeading;
    internal bool ReplayHarnessBegin(string id)
    {
        CommittedMove move = id == "crossover" ? new Crossover(1) : new DriveGather();
        if (!BeginCommittedMove(move)) return false;
        // RPCs require matching node paths on both peers.
        // Source: https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#remote-procedure-calls
        RpcId(1, MethodName.RequestBeginMove, id, id == "crossover" ? 1f : 0f, false);
        return true;
    }
    internal Dictionary<string, object> ReplayHarnessReconcile(PlayerController oracle, double delta, int tick)
    {
        if (!_hasNewState) return null;
        Vector3 before = GlobalPosition, beforeVelocity = Velocity;
        string phaseBefore = _machine.Phase.ToString();
        int frameBefore = _machine.FrameInPhase;
        Vector3 authPosition = _serverPos, authVelocity = _serverVel;
        ReconcileFromServer(_serverPos, _serverVel, _serverAckedSeq, _serverHeading, delta);
        _hasNewState = false;
        foreach (int seq in new List<int>(_replayHarnessCommitted.Keys))
            if (seq <= _serverAckedSeq || seq <= _buffer.LastSequence - _buffer.Capacity) _replayHarnessCommitted.Remove(seq);
        int committed = 0, zeros = 0;
        foreach (bool value in _replayHarnessCommitted.Values) if (value) committed++;
        oracle.ResetContactStateForHarness(authPosition, _serverHeading, authVelocity);
        oracle._pivot = new HeadingMath.PivotState(_serverPivotHasLatch, _serverPivotLatchedYaw);
        foreach (Vector2 input in _buffer.Replay()) { if (input == Vector2.Zero) zeros++; oracle.Move(input, 1.0 / Engine.PhysicsTicksPerSecond); }
        float oraclePositionError = oracle.GlobalPosition.DistanceTo(GlobalPosition);
        float oracleVelocityError = oracle.Velocity.DistanceTo(Velocity);
        return new Dictionary<string, object> {
            ["tick"] = tick, ["sequence"] = _buffer.LastSequence, ["ack"] = _serverAckedSeq,
            ["replay_count"] = _buffer.Count, ["phase_before"] = phaseBefore, ["frame_before"] = frameBefore,
            ["replay_committed_count"] = committed, ["replay_zero_count"] = zeros,
            ["phase_after"] = _machine.Phase.ToString(), ["frame_after"] = _machine.FrameInPhase,
            ["server_phase"] = _serverMovePhase.ToString(), ["server_move"] = _serverMoveId,
            ["before_position"] = ReplayVector(before), ["before_velocity"] = ReplayVector(beforeVelocity),
            ["auth_position"] = ReplayVector(authPosition), ["auth_velocity"] = ReplayVector(authVelocity),
            ["after_position"] = ReplayVector(GlobalPosition), ["after_velocity"] = ReplayVector(Velocity),
            ["correction"] = before.DistanceTo(GlobalPosition), ["velocity_correction"] = beforeVelocity.DistanceTo(Velocity),
            ["replay_displacement"] = authPosition.DistanceTo(GlobalPosition),
            ["oracle_position_error"] = oraclePositionError, ["oracle_velocity_error"] = oracleVelocityError
        };
    }
    internal static float[] ReplayVector(Vector3 value) => new[] { value.X, value.Y, value.Z };
}
