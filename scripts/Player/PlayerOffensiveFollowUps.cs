using System.Collections.Generic;
using Godot;
using Hooper.Ball;
using Hooper.Moves;

namespace Hooper.Player;

public partial class PlayerController
{
    private enum OffensiveAction { Gesture, Drive, Jab, Shoot }

    private readonly record struct OffensiveInput(OffensiveAction Action,
        GestureResult Gesture = default, bool SizeUp = false, bool Finesse = false, Vector2 DriveStick = default);

    private readonly OffensiveFollowUpQueue<OffensiveInput> _offensiveFollowUps = new();
    private int _followUpLocalPossession;
    private int _followUpAuthoritativePossession;
    private int _unconfirmedShotRelease;
    private bool _waitingForRejectedShotCorrection;

    private void RefreshFollowUpPossession()
    {
        var ball = GetBall();
        int local = ball?.StateMachine.PossessionRevision ?? 0;
        int authoritative = ball?.AuthoritativePossessionRevision ?? 0;
        bool awaitingShot = _unconfirmedShotRelease != 0;
        if (awaitingShot && _waitingForRejectedShotCorrection &&
            ball?.AppliedBallSnapshot >= _followUpBallBarrier)
        {
            _unconfirmedShotRelease = 0;
            _waitingForRejectedShotCorrection = false;
            awaitingShot = false;
            // This local holder change is the correction of the speculative release.
            _followUpLocalPossession = local;
        }

        bool authorityChanged = authoritative != _followUpAuthoritativePossession;
        bool localChanged = local != _followUpLocalPossession || !HasFollowUpPossession;
        if (authorityChanged)
        {
            _unconfirmedShotRelease = 0;
            _waitingForRejectedShotCorrection = false;
        }
        if (authorityChanged || (localChanged && !awaitingShot))
            _offensiveFollowUps.InvalidatePossession();

        _followUpLocalPossession = local;
        _followUpAuthoritativePossession = authoritative;
    }

    internal void InvalidateFollowUpsForShotRelease()
    {
        if (!IsServer && CurrentOffensiveRequest > _lastOffensiveResult && _offensiveFollowUps.Count != 0)
        {
            // Keep the remaining intents inaccessible until authority decides whether
            // this speculative shot really started or the original possession survived.
            _unconfirmedShotRelease = CurrentOffensiveRequest;
            return;
        }
        _offensiveFollowUps.InvalidatePossession();
    }

    private bool HasFollowUpPossession =>
        _unconfirmedShotRelease == 0 && IsBallHolder &&
        (IsServer || GetBall()?.ConfirmedHolderForFollowUps == OwnPeerId);

    // A rejected gather may have changed local ball legality. A queued tail
    // gets its turn once that correction is applied, even if the reliable
    // rejection arrived ahead of the ball snapshot on its separate stream.
    private bool CanResolveFollowUp(bool isServer) => isServer ||
        (_lastOffensiveResult == _lastOffensiveRequest &&
         (_followUpBallBarrier == 0 || GetBall()?.AppliedBallSnapshot >= _followUpBallBarrier));

    private void OfferOffensiveInput(OffensiveInput input, bool isServer)
    {
        if (!_machine.IsActive)
        {
            if (_offensiveFollowUps.Count == 0)
                TryBeginOffensiveInput(input, isServer, queued: false);
            return;
        }

        if (HasFollowUpPossession && _machine.CurrentMove is { } current)
            _offensiveFollowUps.TryEnqueue(input, _machine.Phase,
                current.FrameData.RecoveryFrames - _machine.FrameInPhase);
    }

    private bool TryBeginOffensiveInput(OffensiveInput input, bool isServer, bool queued)
    {
        var ball = GetBall();
        bool dribbling = ball?.State == BallState.Dribbling;
        float param = 0;

        CommittedMove move;
        switch (input.Action)
        {
            case OffensiveAction.Gesture:
                var selected = OffensiveGestureResolver.Resolve(input.Gesture, input.SizeUp, input.Finesse,
                    HandSide, IsBallHolder, dribbling);
                if (selected.Move == null) return false;
                (move, param) = selected;
                break;
            case OffensiveAction.Drive:
                param = EuroStepReadResolver.ResolveLateralSign(
                    input.DriveStick, HandStateResolver.BurstWorldDir(Heading, +1), ExitDeadzone);
                move = param == 0 ? new DriveGather() : new EuroStep(param);
                break;
            case OffensiveAction.Jab:
                move = new JabStep();
                break;
            case OffensiveAction.Shoot when ball != null:
                if (IsPosting)
                {
                    if (!CanBeginTurnaround()) return false;
                    move = new TurnaroundShot(SnapshotTurnaroundTarget());
                }
                else
                {
                    float distance = Mathf.Sqrt(DefensiveResolution.DistanceXZSquared(GlobalPosition, ball.RimCenter));
                    move = LayupRangeResolver.IsLayupRange(distance, ball.LayupRange) ? new Layup() : new JumpShot();
                }
                break;
            default:
                return false;
        }

        if (!BeginCommittedMove(move)) return false;

        if (move is JumpShot or Layup) CaptureShotInitiationSpeed();
        if (move is TurnaroundShot) _turnaroundToken = isServer ? 0 : _buffer.LastSequence + 1;
        if (!isServer)
        {
            int shotToken = move switch
            {
                JumpShot shot => shot.BeginToken,
                TurnaroundShot => _turnaroundToken,
                _ => 0,
            };
            bool wasDribbling = move is TurnaroundShot ||
                (dribbling && move is JumpShot or Layup or StepBack or DriveGather or EuroStep);
            SendOffensiveBegin(MoveIdOf(move), param, wasDribbling, shotToken, queued);
        }
        return true;
    }
    private sealed class OffensiveBegin
    {
        internal readonly int Id;
        internal readonly string Move;
        internal readonly float Param;
        internal readonly bool WasDribbling;
        internal readonly int ShotToken;
        internal readonly int Possession;
        internal readonly bool Queued;
        internal Vector2? Exit;
        internal bool Feint;

        internal OffensiveBegin(int id, string move, float param, bool wasDribbling, int shotToken, int possession, bool queued)
            => (Id, Move, Param, WasDribbling, ShotToken, Possession, Queued)
                = (id, move, param, wasDribbling, shotToken, possession, queued);
    }

    private readonly Queue<OffensiveBegin> _deferredOffensiveBegins = new(2);
    private int _lastOffensiveRequest;
    private int _activeOffensiveRequest;
    private int _pendingOffensiveFeint;
    private int _lastOffensiveResult;
    private long _followUpBallBarrier;
    private CommittedMove _offensiveRequestMove;

    private int CurrentOffensiveRequest =>
        _machine.IsActive && ReferenceEquals(_offensiveRequestMove, _machine.CurrentMove) ? _activeOffensiveRequest : 0;

    private void SendOffensiveBegin(string move, float param, bool wasDribbling, int shotToken, bool queued)
    {
        int predecessor = _lastOffensiveRequest;
        _activeOffensiveRequest = ++_lastOffensiveRequest;
        _offensiveRequestMove = _machine.CurrentMove;
        RpcId(1, MethodName.RequestOffensiveBegin, _activeOffensiveRequest, move, param,
            wasDribbling, shotToken, queued, predecessor, _followUpAuthoritativePossession);
    }

    // Reliable ordering preserves discrete requests and their per-instance effects.
    // https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#remote-procedure-calls
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestOffensiveBegin(int id, string move, float param, bool wasDribbling,
        int shotToken, bool queued, int predecessor, int possession)
    {
        if (!IsServer || Multiplayer.GetRemoteSenderId() != OwnPeerId || id <= _lastOffensiveRequest) return;

        int previous = _lastOffensiveRequest;
        _lastOffensiveRequest = id;
        var request = new OffensiveBegin(id, move, param, wasDribbling, shotToken, possession, queued);
        if (GetGameManager()?.IsGameOver == true || !float.IsFinite(param) ||
            move is not ("crossover" or "behindtheback" or "betweenthelegs" or "spin" or "hesitation" or
                         "inandout" or "stepback" or "retreatdribble" or "drivegather" or "eurostep" or
                         "jab" or "jumpshot" or "layup" or "turnaround") ||
            (queued && (predecessor != previous || !OwnsFollowUpPossession(request))))
        {
            ReportOffensiveBegin(request, false);
            return;
        }

        if (!_machine.IsActive && _deferredOffensiveBegins.Count == 0)
            ApplyOffensiveBegin(request);
        else if (queued && _deferredOffensiveBegins.Count < 2)
            _deferredOffensiveBegins.Enqueue(request);
        else
            ReportOffensiveBegin(request, false);
    }

    private bool OwnsFollowUpPossession(OffensiveBegin request)
        => IsBallHolder && GetBall()?.AuthoritativePossessionRevision == request.Possession;

    private void DrainOffensiveBegins()
    {
        while (_deferredOffensiveBegins.TryPeek(out var pending) && !OwnsFollowUpPossession(pending))
        {
            _deferredOffensiveBegins.Dequeue();
            ReportOffensiveBegin(pending, false);
        }

        if (_pendingOffensiveFeint != 0 &&
            (_pendingOffensiveFeint != CurrentOffensiveRequest || _machine.Phase != MovePhase.Startup ||
             _machine.FrameInPhase >= _machine.CurrentMove.FrameData.FeintMinStartupFrames))
        {
            if (_pendingOffensiveFeint == CurrentOffensiveRequest) FeintWithGather();
            _pendingOffensiveFeint = 0;
        }

        while (!_machine.IsActive && _deferredOffensiveBegins.TryDequeue(out var request))
        {
            if (OwnsFollowUpPossession(request))
                ApplyOffensiveBegin(request);
            else
                ReportOffensiveBegin(request, false);
        }
    }

    private void ApplyOffensiveBegin(OffensiveBegin request)
    {
        if (request.Queued && !MatchesAuthoritativeOffensiveContext(request))
        {
            ReportOffensiveBegin(request, false);
            return;
        }

        ApplyRequestedMove(request.Move, request.Param, request.WasDribbling, request.ShotToken);
        bool accepted = _machine.IsActive;
        if (accepted)
        {
            _activeOffensiveRequest = request.Id;
            _offensiveRequestMove = _machine.CurrentMove;
            if (request.Exit is { } exit) SetAuthoritativeExitVector(exit);
            if (request.Feint) _pendingOffensiveFeint = request.Id;
        }
        ReportOffensiveBegin(request, accepted);
    }

    private bool MatchesAuthoritativeOffensiveContext(OffensiveBegin request)
    {
        bool emptyHand = HandStateResolver.IsCrossover(HandSide, (int)request.Param);
        return request.Move switch
        {
            "crossover" or "behindtheback" or "betweenthelegs" or "spin" or "inandout"
                => request.Param is -1f or 1f && emptyHand,
            "hesitation" => request.Param is -1f or 1f && !emptyHand,
            "jumpshot" or "layup" => !IsPosting,
            _ => true,
        };
    }

    private void ReportOffensiveBegin(OffensiveBegin request, bool accepted)
        => RpcId(OwnPeerId, MethodName.ReceiveOffensiveBeginResult, request.Id,
            request.Move, request.ShotToken, accepted, _lastShotTarget,
            (int)HandSide, accepted ? 0L : GetBall()?.NextBallSnapshot ?? 0L);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveOffensiveBeginResult(int id, string move, int shotToken, bool accepted,
        float shotTarget, int hand, long ballBarrier)
    {
        _lastOffensiveResult = System.Math.Max(_lastOffensiveResult, id);
        if (id == _unconfirmedShotRelease)
        {
            if (accepted)
            {
                _unconfirmedShotRelease = 0;
                _offensiveFollowUps.InvalidatePossession();
            }
            else
                _waitingForRejectedShotCorrection = true;
        }

        if (move == "jumpshot")
            ReceiveShotBeginResult(shotToken, accepted, shotTarget);
        else if (!accepted)
        {
            if (move == "turnaround") _buffer.RejectTurnaround(shotToken);
            if (CurrentOffensiveRequest == id)
                _machine.ForceState(MovePhase.Inactive, 0, null);
        }

        if (!accepted && id == _lastOffensiveRequest)
        {
            HandSide = (HandSide)hand;
            _followUpBallBarrier = ballBarrier;
        }
    }

    private void ApplyRequestedFeint(int id)
    {
        if (id != 0 && _machine.Phase == MovePhase.Startup &&
            _machine.FrameInPhase < _machine.CurrentMove.FrameData.FeintMinStartupFrames)
            _pendingOffensiveFeint = id;
        else
            FeintWithGather();
    }

    private OffensiveBegin FindDeferredOffensiveBegin(int id)
    {
        foreach (var request in _deferredOffensiveBegins)
            if (request.Id == id) return request;
        return null;
    }
}
