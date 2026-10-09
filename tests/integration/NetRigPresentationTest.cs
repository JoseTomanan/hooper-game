using System.Collections.Generic;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Networking;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// #367: production Main/Player.tscn rigs on two real ENet peers. The harness
// RPCs carry only default observations and readiness/completion acknowledgements. Production
// ReceiveState broadcasts alone drive the remote defensive AnimationTree arc.
// This proves default parity, not replication of custom builds or visual taste.
public partial class NetRigPresentationTest : Node
{
    private const float Epsilon = 0.0001f;
    private const double ServerTimeoutSeconds = 60;
    private const double ClientTimeoutSeconds = 45;
    private string _role;
    private string _scenario;
    private int _port;
    private int _clientPeerId;
    private int _myPeerId;
    private int _stage;
    private double _elapsed;
    private bool _finished;
    private bool _sampleSent;
    private bool _sampleReceived;
    private bool _clientReady;
    private bool _moveBegun;
    private bool _clientProofSent;
    private bool _clientProofComplete;
    private bool _serverProofSent;
    private double _serverExitAt;
    private NetworkManager _network;
    private Node _players;
    private BallController _ball;
    private readonly Dictionary<string, Vector3> _authoredHierarchy = new();
    private readonly Dictionary<string, Vector3> _authoredBoneRestScales = new();
    private string[] _serverBoneNames;
    private Vector3[] _serverRestScales;
    private Vector3[] _serverPoseScales;
    private string[] _serverHierarchyPaths;
    private Vector3[] _serverHierarchyScales;
    private string _lastDisplay = "unobserved";

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _role = HarnessArgs.ReadArg(args, "--harness-role", "server");
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "left");
        _port = int.TryParse(HarnessArgs.ReadArg(args, "--harness-port", "23462"), out int p) ? p : 23462;
        if (!Require((_role == "server" || _role == "client") && (_scenario == "left" || _scenario == "right"), "unknown role/scenario.")) return;

        var main = GetNode("Main");
        _network = main.GetNode<NetworkManager>("NetworkManager");
        _players = main.GetNode("Players");
        _ball = main.GetNode<BallController>("Ball");
        if (!Require(_network.PlayerScenePath == "res://scenes/Player.tscn", "production PlayerScenePath was replaced.")) return;

        // Capture serialized local scales before _Ready/scaling/animation can
        // change them. Imported skeleton ancestors legitimately include .01;
        // assuming every visual ancestor is unit would reject a correct FBX.
        // Source: https://docs.godotengine.org/en/4.7/classes/class_packedscene.html#class-packedscene-method-instantiate
        var fresh = GD.Load<PackedScene>("res://scenes/Player.tscn").Instantiate<PlayerController>();
        bool found = AnimationHarnessResources.TryFindSkeleton(fresh, out Skeleton3D authored, out string diagnostic);
        int authoredBoneCount = found ? authored.GetBoneCount() : 0;
        if (found)
        {
            foreach (Node3D node in SkeletonAncestors(authored, fresh))
                _authoredHierarchy[fresh.GetPathTo(node).ToString()] = node.Transform.Basis.Scale;
            // Capture independent pre-_Ready bone rest scales as well. Runtime
            // modifiers may intentionally change rotations, so only scale is pinned.
            // Source: https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-method-get-bone-rest
            for (int i = 0; i < authored.GetBoneCount(); i++)
                _authoredBoneRestScales[authored.GetBoneName(i)] = authored.GetBoneRest(i).Basis.Scale;
        }
        fresh.Free();
        if (!Require(found && _authoredHierarchy.Count > 0 && authoredBoneCount == 65 && _authoredBoneRestScales.Count == 65,
            $"authored hierarchy/65 unique bone rests unavailable: {diagnostic}")) return;

        // Signals/peer IDs and RPC authority follow the documented C# pattern.
        // Source: https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#managing-connections
        _network.ConnectionFailed += ConnectionFailed;
        if (_role == "client")
        {
            Multiplayer.ConnectedToServer += ClientConnected;
            Multiplayer.ServerDisconnected += ConnectionFailed;
            _network.JoinGame("127.0.0.1", _port);
        }
        else
        {
            Multiplayer.PeerConnected += PeerConnected;
            _network.HostGame(_port);
        }
        GD.Print($"[net-rig] role={_role} scenario={_scenario} port={_port} booted production Main.");
    }

    private void ClientConnected()
    {
        _myPeerId = Multiplayer.GetUniqueId();
        Require(_myPeerId > 1 && !Multiplayer.IsServer(), $"expected a genuine client, got peer={_myPeerId} server={Multiplayer.IsServer()}.");
    }
    private void PeerConnected(long id)
    {
        if (Require(_clientPeerId == 0 && id > 1, $"unexpected connected client {id}.")) _clientPeerId = (int)id;
    }
    private void ConnectionFailed() { Require(false, "network connection failed/disconnected before proof completed."); }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        if (_role == "server") TickServer(); else TickClient();
        double timeout = _role == "server" ? ServerTimeoutSeconds : ClientTimeoutSeconds;
        if (!_finished && _elapsed > timeout)
            Require(false, $"timed out: stage={_stage}, lastDisplay={_lastDisplay}, sampleSent={_sampleSent}, sampleReceived={_sampleReceived}, ready={_clientReady}, moveBegun={_moveBegun}.");
    }

    private void TickServer()
    {
        if (!Require(Multiplayer.IsServer() && Multiplayer.GetUniqueId() == 1, "listen-server authority lost.")) return;
        if (_clientPeerId <= 1) return;
        var host = _players.GetNodeOrNull<PlayerController>("1");
        if (host == null)
        {
            if (_moveBegun) Require(false, "host rig disappeared before server proof completed.");
            return;
        }
        if (!Require(_ball.State == BallState.Held && _ball.StateMachine.HolderPeerId == 1,
            $"setup requires Held ball owned by host1; got {_ball.State}/{_ball.StateMachine.HolderPeerId}.")) return;
        if (_moveBegun)
        {
            if (!ValidateDefaults(host, "server throughout move/completion", out _)) return;
            if (_clientProofComplete && !_serverProofSent)
            {
                if (!Require(host.PhaseForHarness == MovePhase.Inactive && host.ActiveAnimNodeForHarness == "Locomotion",
                    $"server final machine/node must be Inactive/Locomotion, got {host.PhaseForHarness}/{host.ActiveAnimNodeForHarness}.")) return;
                if (!Require(RpcId(_clientPeerId, MethodName.ServerProofComplete) == Error.Ok,
                    "could not send final reliable server proof acknowledgement.")) return;
                _serverProofSent = true;
                // Give the reliable reply a bounded flush interval. Invariants
                // continue running even if the client disconnects after its ACK.
                _serverExitAt = _elapsed + 0.5;
                GD.Print("[net-rig] server final Held/default/Inactive/Locomotion proof complete; flushing reply.");
            }
            if (_serverProofSent && _elapsed >= _serverExitAt) Finish(0);
            return;
        }
        if (_players.GetNodeOrNull<PlayerController>(_clientPeerId.ToString()) == null) return;

        if (!_sampleSent)
        {
            if (!ValidateDefaults(host, "server default", out Skeleton3D skeleton)) return;
            int count = skeleton.GetBoneCount();
            var names = new string[count];
            var rests = new Vector3[count];
            var poses = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                names[i] = skeleton.GetBoneName(i);
                rests[i] = skeleton.GetBoneRest(i).Basis.Scale;
                poses[i] = skeleton.GetBonePoseScale(i);
            }
            var hierarchy = SkeletonAncestors(skeleton, host).ToArray();
            string[] paths = hierarchy.Select(node => host.GetPathTo(node).ToString()).ToArray();
            Vector3[] scales = hierarchy.Select(node => node.Transform.Basis.Scale).ToArray();
            // Packed string/vector arrays are Variant-compatible C# RPC data.
            // Source: https://docs.godotengine.org/en/4.7/tutorials/scripting/c_sharp/c_sharp_variant.html#variant-compatible-types
            if (!Require(RpcId(_clientPeerId, MethodName.ReceiveDefaults, names, rests, poses, paths, scales) == Error.Ok,
                "could not send reliable default observation.")) return;
            _sampleSent = true;
            GD.Print("[net-rig] server sent 65-bone default sample; waiting for loaded client acknowledgement.");
        }
        if (!_clientReady) return;
        // Held is not generally immune. Here the actor IS the holder, and the
        // production steal resolver excludes self-steals, preserving the arc.
        if (!Require(host.PhaseForHarness == MovePhase.Inactive && host.BeginMoveForHarness(new StealMove(HandSide.Left, _scenario == "left" ? -1f : 1f)),
            "server failed to begin the single steal through production choke point.")) return;
        _moveBegun = true;
        GD.Print($"[net-rig] server began one {_scenario} steal after ready acknowledgement.");
    }

    // These RPCs exchange evidence only; neither writes player/scaler/move state.
    // Source: https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#remote-procedure-calls
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveDefaults(string[] names, Vector3[] rests, Vector3[] poses, string[] paths, Vector3[] scales)
    {
        if (_finished) return;
        if (!Require(_role == "client" && !_sampleReceived && Multiplayer.GetRemoteSenderId() == 1,
            "default sample must arrive once from server1.")) return;
        if (!Require(names.Length == 65 && names.Distinct().Count() == 65 && rests.Length == 65 && poses.Length == 65 &&
                     paths.Length == _authoredHierarchy.Count && paths.Distinct().Count() == paths.Length && scales.Length == paths.Length,
            "malformed default observation cardinality.")) return;
        _serverBoneNames = names;
        _serverRestScales = rests;
        _serverPoseScales = poses;
        _serverHierarchyPaths = paths;
        _serverHierarchyScales = scales;
        _sampleReceived = true;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClientReady()
    {
        if (_finished) return;
        // Sender ID is meaningful only during RPC execution; validate the actual
        // connected client, rather than accepting a caller-supplied peer ID.
        // Source: https://docs.godotengine.org/en/4.7/classes/class_multiplayerapi.html#class-multiplayerapi-method-get-remote-sender-id
        int sender = Multiplayer.GetRemoteSenderId();
        if (!Require(_role == "server" && _sampleSent && !_clientReady && sender == _clientPeerId &&
                     Multiplayer.GetPeers().Contains(sender), $"invalid ready sender {sender}.")) return;
        _clientReady = true;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClientProofComplete()
    {
        if (_finished) return;
        int sender = Multiplayer.GetRemoteSenderId();
        if (!Require(_role == "server" && _moveBegun && _clientReady && !_clientProofComplete &&
                     sender == _clientPeerId && Multiplayer.GetPeers().Contains(sender),
            $"invalid final client proof sender/state: sender={sender}.")) return;
        _clientProofComplete = true;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ServerProofComplete()
    {
        if (_finished) return;
        if (!Require(_role == "client" && !Multiplayer.IsServer() && Multiplayer.GetRemoteSenderId() == 1 &&
                     _clientProofSent && _stage == 4, "invalid final server proof acknowledgement.")) return;
        TickClient();
        if (_finished) return;
        GD.Print($"[net-rig] PASS {_scenario}: ordered remote arc/defaults and server final proof acknowledged.");
        Finish(0);
    }

    private void TickClient()
    {
        if (_myPeerId <= 1) return;
        if (!Require(!Multiplayer.IsServer(), "client became authority, invalidating remote proof.")) return;
        var remote = _players.GetNodeOrNull<PlayerController>("1");
        var local = _players.GetNodeOrNull<PlayerController>(_myPeerId.ToString());
        if (remote == null || local == null)
        {
            if (_clientReady) Require(false, "a proven loaded player rig disappeared before completion acknowledgement.");
            return;
        }
        if (!_sampleReceived) return;
        if (!Require(remote != local && remote.Name != _myPeerId.ToString() && remote.PhaseForHarness == MovePhase.Inactive,
            "player1 must remain remote with an Inactive local machine throughout the observation.")) return;
        if (!ValidateDefaults(remote, "client remote default", out Skeleton3D skeleton) || !CompareServerSample(remote, skeleton)) return;
        if (!_clientReady)
        {
            if (!Require(remote.DisplayMove().phase == MovePhase.Inactive,
                "remote move began before readiness acknowledgement.")) return;
            // Nodes can exist one tick before playback's initial tree update.
            // Wait for the actual baseline node; a broken tree still times out.
            if (remote.ActiveAnimNodeForHarness != "Locomotion") return;
            if (!Require(RpcId(1, MethodName.ClientReady) == Error.Ok, "could not acknowledge loaded/default client rig.")) return;
            _clientReady = true;
            GD.Print("[net-rig] client default sample matches authored hierarchy/server; acknowledged ready.");
            return;
        }

        MovePhase phase = remote.DisplayMove().phase;
        string id = remote.DisplayMoveId();
        // This seam reads playback.GetCurrentNode(), never the requested target.
        // Source: https://docs.godotengine.org/en/4.7/classes/class_animationnodestatemachineplayback.html#class-animationnodestatemachineplayback-method-get-current-node
        string node = remote.ActiveAnimNodeForHarness;
        _lastDisplay = $"phase={phase} id={id} actual-node={node}";
        // Keep validating defaults while awaiting the server's final proof; do
        // not advance stage 4 or send the completion request a second time.
        if (_stage == 4)
        {
            Require(phase == MovePhase.Inactive && node == "Locomotion",
                $"remote final phase/node changed while awaiting acknowledgement: {_lastDisplay}.");
            return;
        }
        MovePhase expected = _stage == 0 ? MovePhase.Startup : _stage == 1 ? MovePhase.Active : _stage == 2 ? MovePhase.Recovery : MovePhase.Inactive;
        string expectedNode = _stage < 3 ? $"Steal{expected}{(_scenario == "left" ? "Left" : "Right")}" : "Locomotion";
        if (phase != expected || node != expectedNode || (_stage < 3 && id != "steal")) return;
        GD.Print($"[net-rig] same-tick stage={_stage} phase={phase} id={id} actual-node={node}; all65 default bone scales/hierarchy match.");
        _stage++;
        if (_stage == 4)
        {
            if (!Require(RpcId(1, MethodName.ClientProofComplete) == Error.Ok,
                "could not send reliable final client proof observation.")) return;
            _clientProofSent = true;
            GD.Print($"[net-rig] client observed {_scenario} arc/defaults; awaiting final server acknowledgement.");
        }
    }

    private bool ValidateDefaults(PlayerController player, string step, out Skeleton3D skeleton)
    {
        var scaler = player.GetNodeOrNull<PlayerRigScaler>("RigScaler");
        bool found = AnimationHarnessResources.TryFindSkeleton(player, out skeleton, out string diagnostic);
        if (!Require(found && scaler != null && scaler.SkeletonResolvedForHarness, $"{step}: missing production rig/scaler: {diagnostic}")) return false;
        if (!Require(Approx(scaler.Height, 1f) && Approx(scaler.Wingspan, 1f) && skeleton.GetBoneCount() == 65,
            $"{step}: expected default1/1 and65 real bones.")) return false;
        if (!Require(Approx(player.Transform.Basis.Scale, Vector3.One) &&
                     Approx(player.GetNode<Node3D>("CharacterModel").Transform.Basis.Scale, Vector3.One), $"{step}: player/CharacterModel authored scale must remain unit.")) return false;
        for (int i = 0; i < 65; i++)
        {
            // Both runtime rest and pose must match the named fresh scene rest:
            // equal rest+pose drift on both peers must not establish its own baseline.
            // Sources: https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-method-get-bone-rest
            // https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-method-get-bone-pose-scale
            Vector3 rest = skeleton.GetBoneRest(i).Basis.Scale;
            Vector3 pose = skeleton.GetBonePoseScale(i);
            string name = skeleton.GetBoneName(i);
            if (!Require(_authoredBoneRestScales.TryGetValue(name, out Vector3 expected) &&
                         Approx(rest, expected) && Approx(pose, expected),
                $"{step}: '{name}' pose={pose}, runtime rest={rest}, fresh authored rest={expected}.")) return false;
        }
        var hierarchy = SkeletonAncestors(skeleton, player).ToArray();
        if (!Require(hierarchy.Length == _authoredHierarchy.Count, $"{step}: visual hierarchy length drifted.")) return false;
        foreach (Node3D node in hierarchy)
        {
            string path = player.GetPathTo(node).ToString();
            // Compare local BASIS SCALE only: rotations/positions legitimately
            // differ between authoritative and display-lerped peers over RTT.
            // Sources: https://docs.godotengine.org/en/4.7/classes/class_node3d.html#class-node3d-property-transform
            // https://docs.godotengine.org/en/4.7/classes/class_basis.html#class-basis-method-get-scale
            if (!Require(_authoredHierarchy.TryGetValue(path, out Vector3 expected) && Approx(node.Transform.Basis.Scale, expected),
                $"{step}: '{path}' runtime visual scale={node.Transform.Basis.Scale} differs from authored hierarchy.")) return false;
        }
        return true;
    }

    private bool CompareServerSample(PlayerController player, Skeleton3D skeleton)
    {
        for (int i = 0; i < _serverBoneNames.Length; i++)
        {
            int bone = skeleton.FindBone(_serverBoneNames[i]);
            if (!Require(bone >= 0, $"remote skeleton missing server bone '{_serverBoneNames[i]}'.")) return false;
            if (!Require(Approx(skeleton.GetBoneRest(bone).Basis.Scale, _serverRestScales[i]) &&
                         Approx(skeleton.GetBonePoseScale(bone), _serverPoseScales[i]), $"remote/server default mismatch at '{_serverBoneNames[i]}'.")) return false;
        }
        for (int i = 0; i < _serverHierarchyPaths.Length; i++)
        {
            var node = player.GetNodeOrNull<Node3D>(_serverHierarchyPaths[i]);
            if (!Require(node != null && Approx(node.Transform.Basis.Scale, _serverHierarchyScales[i]),
                $"remote/server default visual scale mismatch at '{_serverHierarchyPaths[i]}'.")) return false;
        }
        return true;
    }

    private static IEnumerable<Node3D> SkeletonAncestors(Skeleton3D skeleton, PlayerController player)
    {
        for (Node node = skeleton; node != null; node = node.GetParent())
        {
            if (node is Node3D spatial) yield return spatial;
            if (node == player) yield break;
        }
    }
    // NaN/infinity must fail, never silently satisfy an approximate read.
    private static bool Approx(float a, float b) => float.IsFinite(a) && float.IsFinite(b) && Mathf.Abs(a - b) < Epsilon;
    private static bool Approx(Vector3 a, Vector3 b) => Approx(a.X, b.X) && Approx(a.Y, b.Y) && Approx(a.Z, b.Z);
    private bool Require(bool condition, string diagnostic)
    {
        if (condition) return true;
        GD.PrintErr($"[net-rig] FAIL role={_role} scenario={_scenario}: {diagnostic}");
        Finish(1);
        return false;
    }
    private void Finish(int code)
    {
        if (_finished) return;
        _finished = true;
        GD.Print($"[net-rig] {_role} RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
