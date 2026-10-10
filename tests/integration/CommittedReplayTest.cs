using System;
using System.Linq;
using System.Text.Json;
using Godot;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

public partial class CommittedReplayTest : Node3D
{
    private ContactHarnessSeam _fixture, _oracle;
    private string _role, _move, _contact;
    private int _tick;
    private double _elapsed;
    private bool _started, _finished, _waitingComplete;
    private double _serverCompleteAt = -1;
    private int _corrections, _committedCorrections, _startup, _active, _rows, _opponentContacts, _committedContacts;
    private float _preBeginSpeed;

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        _role = HarnessArgs.ReadArg(args, "--harness-role", "server");
        _move = HarnessArgs.ReadArg(args, "--move", "neutral");
        _contact = HarnessArgs.ReadArg(args, "--contact", "contact");
        if (_role is not ("server" or "client") || _move is not ("neutral" or "crossover" or "gather") || _contact is not ("contact" or "no-contact"))
        { Finish(false, "unknown arguments"); return; }
        int port = int.Parse(HarnessArgs.ReadArg(args, "--harness-port", "7777"));
        var peer = new ENetMultiplayerPeer();
        // Source: https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#initializing-the-network
        Error error = _role == "server" ? peer.CreateServer(port, 1) : peer.CreateClient("127.0.0.1", port);
        Multiplayer.MultiplayerPeer = peer;
        if (error != Error.Ok) { Finish(false, error.ToString()); return; }
        if (_role == "server") Multiplayer.PeerConnected += id => RpcId(id, MethodName.Configure, (int)id);
        else Multiplayer.ConnectionFailed += () => Finish(false, "connection failed");
        GD.Print("[committed-replay] READY " + _role);
    }
    private void ConfigureLocal(int clientId)
    {
        _fixture = new ContactHarnessSeam { Name = "Fixture", FirstName = clientId.ToString(), SecondName = "1" };
        AddChild(_fixture);
        if (_role == "client")
        {
            // Source: https://docs.godotengine.org/en/4.7/classes/class_viewport.html#class-viewport-property-own-world-3d
            var viewport = new SubViewport { Name = "OracleWorld", OwnWorld3D = true };
            AddChild(viewport);
            _oracle = new ContactHarnessSeam { Name = "Fixture", FirstName = clientId.ToString(), SecondName = "1" };
            viewport.AddChild(_oracle);
            if (_fixture.First.GetWorld3D().Space == _oracle.First.GetWorld3D().Space)
                throw new InvalidOperationException("oracle physics space is not isolated");
        }
        foreach (var fixture in _oracle == null ? new[] { _fixture } : new[] { _fixture, _oracle })
        {
            fixture.First.ReplayHarnessAssertIdentity(clientId);
            fixture.Second.ReplayHarnessAssertIdentity(1);
            fixture.Reset(new Vector3(0, 0, 4), Mathf.Pi, new Vector3(0, 0, -fixture.First.MoveSpeed),
                Vector3.Zero, 0, Vector3.Zero);
            if (_contact == "no-contact")
                fixture.First.CollisionLayer = fixture.First.CollisionMask = fixture.Second.CollisionLayer = fixture.Second.CollisionMask = 0;
        }
    }
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Configure(int clientId)
    {
        ConfigureLocal(clientId);
        RpcId(1, MethodName.ReadyRoster, clientId, _fixture.First.GetPath().ToString(), _fixture.Second.GetPath().ToString());
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReadyRoster(int clientId, string firstPath, string secondPath)
    {
        if (Multiplayer.GetRemoteSenderId() != clientId) { Finish(false, "invalid roster sender"); return; }
        ConfigureLocal(clientId);
        if (firstPath != _fixture.First.GetPath().ToString() || secondPath != _fixture.Second.GetPath().ToString())
        { Finish(false, "RPC paths differ"); return; }
        RpcId(clientId, MethodName.Start);
        _started = true;
        GD.Print("[committed-replay] roster barrier verified");
    }
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Start() { _started = true; Input.ActionPress("move_forward"); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Complete()
    {
        if ((_contact == "contact" && (_opponentContacts == 0 || (_move != "neutral" && _committedContacts == 0)))
            || (_contact == "no-contact" && _opponentContacts != 0))
        { Finish(false, $"contact premise failed: contacts={_opponentContacts} committed={_committedContacts}"); return; }
        RpcId(Multiplayer.GetRemoteSenderId(), MethodName.Completed);
        _serverCompleteAt = _elapsed + 0.25;
    }
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Completed() { Finish(true, "actual corrections, confirmed phases and replay measured"); }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        if (_serverCompleteAt >= 0) { if (_elapsed >= _serverCompleteAt) Finish(true, "server completed actual ticks"); return; }
        if (_elapsed > 20) { Finish(false, "timeout"); return; }
        if (!_started) return;
        if (_waitingComplete) return;
        try
        {
            _tick++;
            double fixedDelta = 1.0 / Engine.PhysicsTicksPerSecond;
            if (Math.Abs(delta - fixedDelta) > 0.000001) throw new InvalidOperationException("unexpected physics delta");
            if (_role == "server")
            {
                // The paired tick uses peer 1's actual host role, which reads Input
                // rather than the remote-input slot this instrument previously seeded.
                // Source: https://docs.godotengine.org/en/4.7/classes/class_input.html#class-input-method-action-press
                if (_tick == 90) Input.ActionPress("move_right", 1);
                _fixture.First.ReplayHarnessPair(_fixture.Second, fixedDelta, _tick < 90 ? Vector2.Zero : Vector2.Right);
                // #370 replaces the solver. Keep the instrument's contact premise tied
                // to an actual deterministic response, rather than a now-masked slide.
                int opponentHits = _fixture.First.ReplayContactResponse ? 1 : 0;
                _opponentContacts += opponentHits;
                if (_fixture.First.ReplayPhase != "Inactive") _committedContacts += opponentHits;
                if (_tick < 90 && (_fixture.Second.Velocity.Length() > 0.00001 || _fixture.Second.GlobalPosition.Length() > 0.00001))
                    throw new InvalidOperationException("stationary defender premise violated");
                if (_tick > 120 && _fixture.Second.GlobalPosition.Length() < 0.1)
                    throw new InvalidOperationException("moving defender premise not exercised");
                GD.Print("SERVER_ROW " + JsonSerializer.Serialize(new {
                    tick = _tick, ack = _fixture.First.ReplayAck, first_position = PlayerController.ReplayVector(_fixture.First.GlobalPosition),
                    first_velocity = PlayerController.ReplayVector(_fixture.First.Velocity), first_phase = _fixture.First.ReplayPhase, first_frame = _fixture.First.ReplayFrame,
                    second_position = PlayerController.ReplayVector(_fixture.Second.GlobalPosition), second_velocity = PlayerController.ReplayVector(_fixture.Second.Velocity),
                    second_phase = _fixture.Second.ReplayPhase, second_frame = _fixture.Second.ReplayFrame,
                    separation = _fixture.Snapshot.HorizontalSeparation, first_slides = _fixture.First.GetSlideCollisionCount(), second_slides = _fixture.Second.GetSlideCollisionCount(), opponent_hits = opponentHits
                }));
                return;
            }
            // The no-contact oracle remains exact. Contact rows are diagnostic only;
            // they do not reconstruct historical raw opponent samples (ADR-0025).
            _oracle.Second.ResetContactStateForHarness(_fixture.Second.GlobalPosition, _fixture.Second.Heading, _fixture.Second.Velocity);
            var row = _fixture.First.ReplayHarnessReconcile(_oracle.First, fixedDelta, _tick);
            if (row != null)
            {
                row["opponent_raw_position"] = PlayerController.ReplayVector(_fixture.Second.ReplayRawPosition);
                row["opponent_raw_velocity"] = PlayerController.ReplayVector(_fixture.Second.ReplayRawVelocity);
                row["opponent_raw_heading"] = _fixture.Second.ReplayRawHeading;
                row["opponent_display_position"] = PlayerController.ReplayVector(_fixture.Second.GlobalPosition);
                row["opponent_display_velocity"] = PlayerController.ReplayVector(_fixture.Second.Velocity);
                row["opponent_display_heading"] = _fixture.Second.Heading;
                row["opponent_raw_display_distance"] = _fixture.Second.GlobalPosition.DistanceTo(_fixture.Second.ReplayRawPosition);
                // System.Text.Json rejects nonfinite floats, so corrupted data fails the trial.
                GD.Print("REPLAY_ROW " + JsonSerializer.Serialize(row));
                _rows++;
                int replay = (int)row["replay_count"];
                if (replay > 0 && (float)row["correction"] > 0.00001f) _corrections++;
                if ((string)row["server_move"] == (_move == "gather" ? "drivegather" : _move))
                {
                    if ((string)row["server_phase"] == "Startup") _startup++;
                    if ((string)row["server_phase"] == "Active") _active++;
                    // Delayed Inactive snapshots can cancel the client's predicted
                    // move before the server's Active snapshot arrives. Observe that
                    // existing behavior rather than requiring overlapping phases.
                    if (replay > 0 && (int)row["replay_committed_count"] > 0 && (int)row["replay_zero_count"] > 0
                        && (string)row["server_phase"] != "Inactive" && (float)row["correction"] > 0.00001f) _committedCorrections++;
                }
                if (_contact == "no-contact" && ((float)row["oracle_position_error"] > 0.0001f || (float)row["oracle_velocity_error"] > 0.0001f))
                    throw new InvalidOperationException("neutral-only replay oracle mismatch");
            }
            if (_tick == 25 && _move != "neutral")
            {
                _preBeginSpeed = _fixture.First.Velocity.Length();
                if (_preBeginSpeed < 0.1f || !_fixture.First.ReplayHarnessBegin(_move == "gather" ? "drivegather" : _move))
                    throw new InvalidOperationException("move did not begin with meaningful velocity");
            }
            _fixture.First.ReplayHarnessClient(fixedDelta);
            _fixture.Second.ReplayHarnessRemote();
            if (_tick >= 180)
            {
                if (_rows < 30 || _corrections == 0 || (_move != "neutral" && (_startup == 0 || _active == 0 || _committedCorrections == 0)))
                    throw new InvalidOperationException($"vacuous evidence: rows={_rows} corrections={_corrections} committedCorrections={_committedCorrections} startup={_startup} active={_active}");
                GD.Print("REPLAY_SUMMARY " + JsonSerializer.Serialize(new { rows = _rows, corrections = _corrections, committed_corrections = _committedCorrections, startup = _startup, active = _active, pre_begin_speed = _preBeginSpeed }));
                RpcId(1, MethodName.Complete);
                _waitingComplete = true;
            }
        }
        catch (Exception exception) { Finish(false, exception.Message); }
    }
    private void Finish(bool success, string reason)
    {
        if (_finished) return;
        _finished = true;
        Input.ActionRelease("move_forward");
        Input.ActionRelease("move_right");
        GD.Print($"[harness] {(success ? "PASS" : "FAIL")} committed-replay {_role}: {reason}");
        GetTree().Quit(success ? 0 : 1);
    }
}
