using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using Hooper.Networking;
using Hooper.Player;
using Hooper.Moves;

namespace HOOPERGAME.Tests.Integration;

/// <summary>Three independent real ENet peers; observe production integration and replay.</summary>
public partial class NetProductionContactTest : Node3D
{
    private string _role, _move;
    private bool _contact, _configured, _running, _finished, _registered, _sampleSent;
    private int _driverId, _defenderId, _tick, _ready, _done, _streak, _corrections, _replays;
    private int _responses, _committedResponses, _sourceDistinct, _pendingBefore;
    private bool _newBefore, _ordinaryFailed;
    private float _correctionDistance;
    private int _replayThisTick;
    private MovePhase _correctedPhase;
    private float _minimum = float.MaxValue;
    private double _elapsed;
    private PlayerController _driver, _defender, _own, _other;
    private Vector3? _settled;
    private readonly HashSet<int> _readyPeers = new(), _donePeers = new();
    private readonly HashSet<MovePhase> _correctionPhases = new(), _replayPhases = new();
    private Node3D _players;
    private ContactPostObserver _post;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _role = HarnessArgs.ReadArg(args, "--harness-role", "server");
        _move = HarnessArgs.ReadArg(args, "--move", "neutral");
        _contact = HarnessArgs.ReadArg(args, "--contact", "contact") == "contact";
        int port = int.Parse(HarnessArgs.ReadArg(args, "--harness-port", "7777"));
        // Early input/correction sampling and late observation bracket the actual callbacks.
        // Source: https://docs.godotengine.org/en/4.7/classes/class_node.html#class-node-property-process-physics-priority
        ProcessPhysicsPriority = -100;
        _post = new ContactPostObserver { Harness = this, ProcessPhysicsPriority = 100 };
        AddChild(_post);
        _players = GetNode<Node3D>("Players");
        _players.ChildEnteredTree += child => {
            if (child is PlayerController player) {
                player.SetPhysicsProcess(false);
                player.SetProcess(false);
            }
        };
        var net = GetNode<NetworkManager>("NetworkManager");
        if (_role == "server") {
            net.StartDedicatedServer(port);
            GD.Print("[production-contact] READY server");
        } else net.JoinGame("127.0.0.1", port);
    }

    public override void _Process(double delta)
    {
        _elapsed += delta;
        if (!_finished && _elapsed > 25) Finish(false, "timeout");
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        try {
            // _Ready can re-enable a spawned body after ChildEnteredTree, so hold it
            // before the next physics callback until the reliable configuration barrier.
            if (!_running) foreach (var player in _players.GetChildren().OfType<PlayerController>()) {
                player.SetPhysicsProcess(false); player.SetProcess(false);
            }
            if (_role != "server" && !_registered && _players.GetChildCount() == 2
                && Multiplayer.MultiplayerPeer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected) {
                _registered = true; RpcId(1, MethodName.RegisterRole, _role);
            }
            if (!_running) return;
            _tick++;
            if (_role == "driver") {
                if (_tick == 1) Input.ActionPress("move_forward", 1);
                if (_tick == 2 && _move != "neutral") Require(_own.ContactNetBegin(_move), "natural move begin rejected");
                if (_tick == 150) Input.ActionRelease("move_forward");
            } else if (_role == "defender") {
                if (_tick == 90) Input.ActionPress("move_right", 1);
                if (_tick == 110) Input.ActionRelease("move_right");
            }
            if (_role != "server") {
                _newBefore = _own.ContactNetNewState;
                _pendingBefore = _own.ContactNetPending;
                _correctionDistance = 0;
                _replayThisTick = 0;
            }
        } catch (Exception e) { Finish(false, e.Message); }
    }

    // Reliable setup uses identical root and replicated peer-id node paths on all three peers.
    // Source: https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#remote-procedure-calls
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RegisterRole(string role)
    {
        if (_role != "server") return;
        int sender = Multiplayer.GetRemoteSenderId();
        if (role == "driver") _driverId = sender; else if (role == "defender") _defenderId = sender;
        else { Finish(false, "invalid client role"); return; }
        if (_driverId == 0 || _defenderId == 0 || _configured) return;
        Configure(_driverId, _defenderId);
        Rpc(MethodName.Configure, _driverId, _defenderId);
    }

    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Configure(int driver, int defender)
    {
        try {
            _driverId = driver; _defenderId = defender;
            Require(_players.GetChildCount() == 2 && driver != defender && driver != 1 && defender != 1,
                "dedicated topology must contain exactly two remote bodies");
            _driver = _players.GetNode<PlayerController>(driver.ToString());
            _defender = _players.GetNode<PlayerController>(defender.ToString());
            // Leave a .25 m initial gap for committed trials: stale Inactive snapshots
            // may end prediction early (#368), so the workload must reach contact first.
            // Earlier 1.4 m trials narrowly missed contact before that known correction.
            float start = _move == "neutral" ? 3 : 1.25f;
            _driver.ResetContactStateForHarness(new Vector3(0,0,start), Mathf.Pi, new Vector3(0,0,-6));
            _defender.ResetContactStateForHarness(Vector3.Zero, 0, Vector3.Zero);
            foreach (PlayerController p in new[] { _driver, _defender }) {
                p.SetPhysicsProcess(false); p.SetProcess(false);
                var shape = p.GetNode<CollisionShape3D>("CollisionShape3D");
                Require(shape.Shape is CapsuleShape3D capsule && Math.Abs(capsule.Radius - .5) < .000001,
                    "shipped capsule premise changed");
                var animation = p.GetNodeOrNull<AnimationTree>("AnimationTree");
                if (animation != null) animation.Active = false;
                if (!_contact) p.CollisionLayer = p.CollisionMask = 0;
            }
            if (_role != "server") {
                _own = _role == "driver" ? _driver : _defender;
                _other = _role == "driver" ? _defender : _driver;
                Require(_own.ContactNetPeerId == Multiplayer.GetUniqueId(), "local input ownership");
                _own.ContactNetObserve(ObserveMotion);
                _own.ContactNetObserveReconciliation(ObserveReconciliation);
                RpcId(1, MethodName.ReadyToRun);
            } else {
                _driver.ContactNetObserve(ObserveMotion);
                _defender.ContactNetObserve(ObserveMotion);
            }
            _configured = true;
        } catch(Exception e) { Finish(false,e.Message); }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReadyToRun()
    {
        if (_role != "server") return;
        _readyPeers.Add(Multiplayer.GetRemoteSenderId()); _ready = _readyPeers.Count;
        if (_ready != 2) return;
        Rpc(MethodName.BeginRun); BeginRun();
    }
    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BeginRun()
    {
        _running = true;
        foreach (PlayerController p in new[] { _driver, _defender }) p.SetPhysicsProcess(true);
    }

    private void ObserveMotion(ContactOpponent? used, ContactResponse response, bool committed)
    {
        try {
            if (_finished) return;
            bool effect = response.Self.Position.LengthSquared() > 1e-12f || response.Self.Velocity.LengthSquared() > 1e-12f;
            // A neutral replay inherits the machine's current phase but is not a
            // committed integration. _hasNewState remains true throughout replay.
            bool replay = _role != "server" && _own.ContactNetNewState;
            bool committedIntegration = committed && !replay;
            if (effect) { _responses++; if(committedIntegration) _committedResponses++; }
            float distinction = 0;
            if (_role != "server") {
                if (!_contact) Require(used == null, "disabled contact still found opponent");
                else if (_other.ContactNetHasSnapshot) {
                    Require(used is { } sample && sample.Position == _other.ContactNetRawPosition
                        && sample.Velocity == _other.ContactNetRawVelocity && sample.Heading == _other.ContactNetRawHeading,
                        "actual movement used display state instead of raw broadcast");
                    distinction = _other.GlobalPosition.DistanceTo(_other.ContactNetRawPosition);
                    if (distinction > .01f) _sourceDistinct++;
                }
            }
            Row(new { kind="motion", tick=_tick, committed, replay, committed_integration=committedIntegration,
                effect, source_gap=distinction,
                used=used is { } u ? V(u.Position) : null,
                raw=_role == "server" ? null : V(_other.ContactNetRawPosition),
                display=_role == "server" ? null : V(_other.GlobalPosition) });
        } catch(Exception e) { Finish(false,e.Message); }
    }

    private void ObserveReconciliation(Vector3 before, Vector3 after, int replayCount)
    {
        _correctionDistance = before.DistanceTo(after);
        _replayThisTick = replayCount;
        _correctedPhase = _own.ContactNetPhase;
        Row(new { kind="reconciliation",tick=_tick,actual_body_correction=_correctionDistance,
            replay_count=replayCount,before_position=V(before),after_position=V(after),phase=_correctedPhase.ToString() });
    }

    internal void ObserveAfterPhysics()
    {
        if (!_running || _finished) return;
        try {
            Require(Finite(_driver.GlobalPosition) && Finite(_driver.Velocity)
                && Finite(_defender.GlobalPosition) && Finite(_defender.Velocity), "nonfinite body state");
            Require(NoPlayerSlide(_driver) && NoPlayerSlide(_defender), "engine solver player collision");
            float separation = _role == "server" ? Horizontal(_driver.GlobalPosition, _defender.GlobalPosition)
                : _other.ContactNetHasSnapshot ? Horizontal(_own.GlobalPosition, _other.ContactNetRawPosition) : 100;
            _minimum = Math.Min(_minimum,separation);
            if (_contact) Require(separation >= 1 - .0002f, "post-integration capsule overlap");
            else if(separation < 1-.0002f) _ordinaryFailed = true;
            if (_role != "server") {
                bool corrected = _newBefore && !_own.ContactNetNewState && _correctionDistance > .00001f;
                bool replayed = _newBefore && _pendingBefore > 0 && _replayThisTick > 0;
                if(corrected) { _corrections++; _correctionPhases.Add(_correctedPhase); }
                if(replayed) { _replays++; _replayPhases.Add(_correctedPhase); }
                float error = _settled is { } sample ? _own.GlobalPosition.DistanceTo(sample) : -1;
                if(error >= 0 && error <= .01f) _streak++; else _streak = 0;
                Row(new { kind="state", tick=_tick, separation, corrected, replayed, pending=_pendingBefore,
                    correction_distance=_correctionDistance,replay_count=_replayThisTick,
                    phase=_own.ContactNetPhase.ToString(), settled_error=error, convergence_streak=_streak });
                if(_tick >= 300) {
                    Require(_settled != null && _streak >= 10, "settled authority convergence <=.01 for ten observations");
                    Require(_replays > 0, "real nonempty replay required on each client");
                    // Perfect baseline defender prediction may need no body correction.
                    // The driving client independently proves a nonzero correction.
                    if (_role == "driver") Require(_corrections > 0, "driver needs real body correction");
                    if(_contact) Require(_sourceDistinct > 5,"raw/display distinction did not discriminate");
                    if(_role == "driver" && _contact) {
                        Require(_responses > 0, "no production contact response");
                        if(_move != "neutral") {
                            Require(_committedResponses > 0, "no contact during committed displacement");
                            Require(_correctionPhases.Any(p => p != MovePhase.Inactive), "no real correction during committed move");
                        }
                    }
                    RpcId(1,MethodName.Completed); _running = false;
                }
            } else {
                Row(new { kind="state",tick=_tick,separation,pair_steps=_driver.ProductionContactPairStepsForHarness });
                if(_tick == 220 && !_sampleSent) {
                    Require(_driver.Velocity.Length() < .00001 && _defender.Velocity.Length() < .00001,"authority must settle before reliable sample");
                    if (_contact) {
                        Require(_responses > 0, "authority must execute actual contact response");
                        if (_move != "neutral") Require(_committedResponses > 0, "authority omitted committed contact response");
                    }
                    _sampleSent=true;
                    RpcId(_driverId,MethodName.SettledSample,_driver.GlobalPosition);
                    RpcId(_defenderId,MethodName.SettledSample,_defender.GlobalPosition);
                }
                if(!_contact && _tick >= 220) {
                    Require(_ordinaryFailed,"no-contact control must fail ordinary separation gate");
                    Require(_driver.GlobalPosition.Z < -3,"no-contact movement premise");
                    Require(_driver.ProductionContactPairStepsForHarness == 0,"disabled pair still participated");
                }
            }
        } catch(Exception e) { Finish(false,e.Message); }
    }
    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SettledSample(Vector3 position) => _settled = position;
    [Rpc(MultiplayerApi.RpcMode.AnyPeer,TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Completed()
    {
        if(_role != "server") return;
        _donePeers.Add(Multiplayer.GetRemoteSenderId()); _done=_donePeers.Count;
        if(_done!=2)return;
        Rpc(MethodName.AllPassed); Finish(true,"two client gates and authority gates");
    }
    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AllPassed() => Finish(true,"contact/correction/replay/source/convergence gates");
    private static object V(Vector3 p) => new { x=p.X,y=p.Y,z=p.Z };
    private static float Horizontal(Vector3 a,Vector3 b) => new Vector2(a.X-b.X,a.Z-b.Z).Length();
    private static bool Finite(Vector3 p) => float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z);
    private static bool NoPlayerSlide(PlayerController p) {
        for(int i=0;i<p.GetSlideCollisionCount();i++) if(p.GetSlideCollision(i).GetCollider() is PlayerController)return false;
        return true;
    }
    private static void Require(bool condition,string detail) { if(!condition)throw new InvalidOperationException(detail); }
    private static void Row(object row) => GD.Print("CONTACT_ROW "+JsonSerializer.Serialize(row));
    private void Finish(bool passed,string detail) {
        if(_finished)return; _finished=true;
        GD.Print("CONTACT_SUMMARY "+JsonSerializer.Serialize(new { role=_role,move=_move,contact=_contact,
            passed,detail,minimum_separation=_minimum,responses=_responses,committed_responses=_committedResponses,
            corrections=_corrections,replays=_replays,source_distinct=_sourceDistinct,
            correction_phases=_correctionPhases.Select(x=>x.ToString()),replay_phases=_replayPhases.Select(x=>x.ToString()),convergence_streak=_streak }));
        GD.Print($"[harness] {(passed?"PASS":"FAIL")} production-contact {_role}: {detail}");
        // Defer quit for reliable verdict packets to leave the dedicated server.
        if(passed) GetTree().CreateTimer(.5).Timeout += ()=>GetTree().Quit(0);
        else GetTree().Quit(1);
    }
}

public partial class ContactPostObserver : Node
{
    internal NetProductionContactTest Harness;
    public override void _PhysicsProcess(double delta) => Harness.ObserveAfterPhysics();
}
