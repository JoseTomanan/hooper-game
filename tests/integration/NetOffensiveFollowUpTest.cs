using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Networking;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

public partial class NetOffensiveFollowUpTest : Node3D
{
    private const string Prefix = "[net-offensive-follow-up]";
    private const int SettleTicks = 45;
    private const int AuthorityDelayTicks = 10;
    private static readonly string[] FullSequence = { "crossover", "hesitation", "hesitation" };
    private static readonly string[] ResetClientSequence = { "crossover", "hesitation" };
    private static readonly string[] ResetServerSequence = { "crossover" };
    private static readonly string[] RejectClientSequence = { "crossover", "hesitation", "jumpshot" };
    private static readonly string[] RejectServerSequence = { "crossover", "jumpshot" };

    private NetworkManager _network;
    private Node _players;
    private BallController _ball;
    private int _clientId;
    private int _frame;
    private int _readyAt;
    private int _pauseUntil;
    private int _stable;
    private int _exitAt;
    private bool _server;
    private bool _reset;
    private bool _rejectHead;
    private bool _fixtureReady;
    private bool _inputStarted;
    private bool _pausedOnce;
    private bool _observedDeferred;
    private bool _observedStaleRecovery;
    private bool _resetAtAuthority;
    private bool _observedResetAtClient;
    private bool _overflowPressed;
    private bool _rejectedClientHead;
    private bool _cradledAtAuthority;
    private bool _localPass;
    private bool _clientPass;
    private bool _finished;
    private int _firstPossession;
    private int _lastMoveEnd;
    private CommittedMove _previousMove;
    private MovePhase _previousPhase;
    private int _previousFrameInPhase;
    private readonly List<string> _moves = new();
    private readonly List<int> _requestIds = new();

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        string role = HarnessArgs.ReadArg(args, "--harness-role", "server");
        string scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "delayed");
        _server = role == "server";
        _reset = scenario == "reset";
        _rejectHead = scenario == "reject-head";
        if (role is not ("server" or "client") || scenario is not ("delayed" or "reset" or "reject-head"))
        {
            Fail($"invalid role/scenario {role}/{scenario}");
            return;
        }

        // Observe the completed production tick, then schedule the next input.
        // https://docs.godotengine.org/en/4.7/classes/class_node.html#class-node-property-process-physics-priority
        ProcessPhysicsPriority = 100;
        _network = GetNode<NetworkManager>("NetworkManager");
        _players = GetNode("Players");
        _ball = GetNode<BallController>("Ball");
        _network.PlayerScenePath = "res://tests/integration/HarnessNetPlayer.tscn";
        int port = int.Parse(HarnessArgs.ReadArg(args, "--harness-port", "23479"));
        if (_server)
        {
            Multiplayer.PeerConnected += id => _clientId = (int)id;
            _network.ServerStarted += _ => GD.Print($"{Prefix} READY server");
            _network.HostGame(port);
        }
        else
        {
            Multiplayer.ConnectedToServer += () => _clientId = Multiplayer.GetUniqueId();
            Multiplayer.ConnectionFailed += () => Fail("client could not connect");
            _network.JoinGame("127.0.0.1", port);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _frame++;
        if (_exitAt != 0)
        {
            if (_frame >= _exitAt) Finish(true);
            return;
        }
        if (_frame > 1500)
        {
            Fail($"timed out: moves={string.Join(",", _moves)} deferred={_observedDeferred} " +
                 $"stale={_observedStaleRecovery} reset={_observedResetAtClient}/{_resetAtAuthority}");
            return;
        }
        if (_clientId <= 1) return;
        var actor = _players.GetNodeOrNull<PlayerController>(_clientId.ToString());
        if (actor == null) return;

        if (!_fixtureReady)
        {
            PrepareFixture(actor);
            return;
        }

        var (move, phase, inPhase, requestId) = actor.FollowUpSimulationForHarness;
        if (!ObserveMove(move, phase, inPhase, requestId, !actor.IsPhysicsProcessing())) return;
        if (_server)
            DriveAuthorityFixture(actor, move, phase, inPhase);
        else
            DriveClientInput(actor, move, phase, inPhase);

        if (_finished || _localPass || _moves.Count == 0) return;
        bool ballReady = _rejectHead
            ? _ball.SuccessfulShotReleasesForHarness > 0
            : _ball.State == BallState.Dribbling && _ball.StateMachine.HolderPeerId == _clientId;
        if (phase != MovePhase.Inactive || !ballReady)
        {
            _stable = 0;
            return;
        }
        if (++_stable < SettleTicks) return;
        VerifyAndSignal();
    }

    private void PrepareFixture(PlayerController actor)
    {
        if (_server)
        {
            if (_players.GetNodeOrNull<PlayerController>("1") is not { } host) return;
            actor.GlobalPosition = new Vector3(-3, 0, 6);
            host.GlobalPosition = new Vector3(4, 0, 7);
            _ball.PostAwardForHarness(_clientId);
            _ball.TryStartDribble(_clientId);
        }
        if (_ball.StateMachine.HolderPeerId != _clientId || _ball.State != BallState.Dribbling ||
            actor.DisplayHandSide != HandSide.Right)
            return;

        _firstPossession = _ball.AuthoritativePossessionRevision;
        _readyAt = _frame;
        _fixtureReady = true;
    }

    private bool ObserveMove(CommittedMove move, MovePhase phase, int frame, int requestId, bool paused)
    {
        if (paused && (!ReferenceEquals(move, _previousMove) || phase != _previousPhase || frame != _previousFrameInPhase))
            return Fail("the authoritative predecessor advanced while the test held its physics clock");

        if (move != _previousMove && move != null)
        {
            int expectedId = _moves.Count + 1;
            if (_rejectHead && _server && _moves.Count == 1) expectedId = 3;
            if (phase != MovePhase.Startup || frame != 1 || requestId != expectedId)
                return Fail($"begin {move.Id} gave request {requestId}, {phase}/{frame}; expected #{expectedId}, Startup/1");
            bool rejectionTransition = _rejectHead && move is JumpShot &&
                (_server || _previousMove is Hesitation);
            if (_rejectHead && !_server && _previousMove is Hesitation && move is JumpShot)
                _rejectedClientHead = _previousPhase != MovePhase.Recovery ||
                    _previousFrameInPhase < _previousMove.FrameData.RecoveryFrames - 1;
            if (_moves.Count != 0 && !rejectionTransition && _frame != _lastMoveEnd + 1)
                return Fail($"gap between {_moves[^1]} and {move.Id}: ended {_lastMoveEnd}, began {_frame}");

            _moves.Add(move.Id);
            _requestIds.Add(requestId);
            GD.Print($"{Prefix} {Role} begin #{requestId} {move.Id} on frame {_frame}");
        }
        else if (_previousMove != null && !paused)
        {
            int expectedFrame = _previousFrameInPhase + 1;
            var data = _previousMove.FrameData;
            int phaseLength = _previousPhase switch
            {
                MovePhase.Startup => data.StartupFrames,
                MovePhase.Active => data.ActiveFrames,
                _ => data.RecoveryFrames,
            };
            if (expectedFrame == phaseLength)
            {
                var nextPhase = _previousPhase switch
                {
                    MovePhase.Startup => MovePhase.Active,
                    MovePhase.Active => MovePhase.Recovery,
                    _ => MovePhase.Inactive,
                };
                if (phase != nextPhase || frame != 0)
                    return Fail($"{_previousMove.Id} did not finish {_previousPhase} exactly once: {phase}/{frame}");
            }
            else if (phase != _previousPhase || frame != expectedFrame)
            {
                // The lost-possession variant permits the server to reject the
                // speculative first follow-up. The predecessor itself must finish.
                bool rejectedResetFollowUp = _reset && !_server && _moves.Count == 2 &&
                    phase == MovePhase.Inactive && move == null;
                bool rejectedDribbleFollowUp = _rejectHead && !_server && _moves.Count == 2 &&
                    phase == MovePhase.Inactive && move == null;
                _rejectedClientHead |= rejectedDribbleFollowUp;
                if (!rejectedResetFollowUp && !rejectedDribbleFollowUp)
                    return Fail($"{_previousMove.Id}: expected {_previousPhase}/{expectedFrame}; got {phase}/{frame}");
            }
        }

        if (move == null && _previousMove != null) _lastMoveEnd = _frame;
        _previousMove = move;
        _previousPhase = phase;
        _previousFrameInPhase = frame;
        return true;
    }

    private void DriveAuthorityFixture(PlayerController actor, CommittedMove move, MovePhase phase, int frame)
    {
        if (!_pausedOnce && move is Crossover && phase == MovePhase.Recovery &&
            frame == move.FrameData.RecoveryFrames - 5)
        {
            // Keep networking and ball physics running while the authoritative
            // predecessor owes five more recovery ticks. This forces a real
            // deferred Begin even when both ENet peers run on loopback.
            actor.SetPhysicsProcess(false);
            _pausedOnce = true;
            _pauseUntil = _frame + AuthorityDelayTicks;
        }
        if (_pauseUntil != 0 && _frame == _pauseUntil)
        {
            actor.SetPhysicsProcess(true);
            _pauseUntil = 0;
        }

        if (actor.DeferredOffensiveBeginsForHarness == 0) return;
        _observedDeferred = true;
        if (_rejectHead && !_cradledAtAuthority)
        {
            // Keep the same possession. Once the head reaches its authority
            // execution opportunity, Held makes only that dribble intent illegal.
            _cradledAtAuthority = move is Crossover && phase == MovePhase.Recovery &&
                _ball.StateMachine.StopDribble() && _ball.AuthoritativePossessionRevision == _firstPossession;
            if (!_cradledAtAuthority) Fail("the head was not made illegal during its authoritative deferral");
        }
        if (!_reset || _resetAtAuthority) return;

        if (move is not Crossover || phase != MovePhase.Recovery || !_ball.StateMachine.GoLoose() ||
            _ball.StateMachine.HolderPeerId != 0)
        {
            Fail("reset did not occur while the deferred request's predecessor was recovering");
            return;
        }
        // A loss and regain between snapshots must invalidate by possession
        // revision, because the last and next snapshot share the same holder.
        _ball.PostAwardForHarness(_clientId);
        _ball.TryStartDribble(_clientId);
        _resetAtAuthority = _ball.State == BallState.Dribbling &&
            _ball.StateMachine.HolderPeerId == _clientId &&
            _ball.AuthoritativePossessionRevision > _firstPossession;
        if (!_resetAtAuthority) Fail("same-holder reset did not establish a new live-dribble possession");
    }

    private void DriveClientInput(PlayerController actor, CommittedMove move, MovePhase phase, int frame)
    {
        // Both held and quick action samples are read by the production recognizer.
        // https://docs.godotengine.org/en/4.7/classes/class_input.html#class-input-method-action-press
        if (!_inputStarted && _frame >= _readyAt + SettleTicks)
        {
            _inputStarted = true;
            Input.ActionPress("aim_left");
            return;
        }
        if (move is Crossover && phase == MovePhase.Startup) Input.ActionRelease("aim_left");
        if (move is Crossover && phase == MovePhase.Recovery)
        {
            int remaining = move.FrameData.RecoveryFrames - frame;
            if (remaining == 6 || (remaining == 3 && !_rejectHead))
                Input.ActionPress("aim_left", remaining == 6 ? 1f : 0.9f);
            else if (remaining is 5 or 2)
                Input.ActionRelease("aim_left");

            // In the two-tap case, the second tap resolves before the shot
            // edge so the legal shot must lose to capacity.
            if (remaining == 2)
            {
                Input.ActionPress(_ball.ShootAction);
                _overflowPressed = !_rejectHead;
            }
            else if (remaining == 1)
                Input.ActionRelease(_ball.ShootAction);
        }

        if (move is Hesitation && actor.LastAuthoritativeMoveForFollowUpHarness == ("crossover", MovePhase.Recovery))
            _observedStaleRecovery = true;
        if (_reset && _ball.AuthoritativePossessionRevision > _firstPossession &&
            _ball.StateMachine.HolderPeerId == _clientId)
            _observedResetAtClient = true;
    }

    private void VerifyAndSignal()
    {
        string[] expected = _reset ? (_server ? ResetServerSequence : ResetClientSequence)
            : _rejectHead ? (_server ? RejectServerSequence : RejectClientSequence)
            : FullSequence;
        if (!_moves.SequenceEqual(expected))
        {
            Fail($"expected {string.Join(",", expected)}; observed {string.Join(",", _moves)}");
            return;
        }
        if (_server && (!_observedDeferred || (_reset && !_resetAtAuthority)))
        {
            Fail("authority never observed the deferred Begin or the configured possession reset");
            return;
        }
        if (!_server && ((!_rejectHead && !_overflowPressed) || !_observedStaleRecovery ||
            (_reset && !_observedResetAtClient)))
        {
            Fail("client never observed the overflow setup, stale predecessor snapshot or configured reset");
            return;
        }
        if (_rejectHead && (_ball.SuccessfulShotReleasesForHarness != 1 ||
            (_server ? !_cradledAtAuthority : !_rejectedClientHead)))
        {
            Fail($"reject-head did not preserve exactly one tail shot; releases={_ball.SuccessfulShotReleasesForHarness} " +
                $"cradled={_cradledAtAuthority} corrected={_rejectedClientHead}");
            return;
        }
        if (!_rejectHead && _ball.SuccessfulShotReleasesForHarness != 0)
        {
            Fail("the overflow shot reached a one-shot ball effect");
            return;
        }
        GD.Print($"{Prefix} {Role} matched {string.Join(",", _moves)}; requests={string.Join(",", _requestIds)}; " +
            $"shots={_ball.SuccessfulShotReleasesForHarness}, deferred={_observedDeferred}, stale={_observedStaleRecovery}");
        _localPass = true;
        if (!_server) RpcId(1, MethodName.ClientVerified);
        MaybeComplete();
    }

    // Both processes stay connected until they have independently verified their timelines.
    // https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#remote-procedure-calls
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClientVerified()
    {
        if (!_server || Multiplayer.GetRemoteSenderId() != _clientId) return;
        _clientPass = true;
        MaybeComplete();
    }

    private void MaybeComplete()
    {
        if (!_server || !_localPass || !_clientPass) return;
        RpcId(_clientId, MethodName.Complete);
        _exitAt = _frame + 12;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Complete()
    {
        if (!_server && _localPass) Finish(true);
    }

    private string Role => _server ? "server" : "client";

    private bool Fail(string reason)
    {
        GD.PrintErr($"{Prefix} {Role} FAIL: {reason}");
        Finish(false);
        return false;
    }

    private void Finish(bool success)
    {
        _finished = true;
        int code = success ? 0 : 1;
        GD.Print($"{Prefix} {Role} RESULT: {(success ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
