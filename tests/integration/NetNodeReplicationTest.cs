using System.Linq;
using Godot;
using Hooper.Networking;

namespace HOOPERGAME.Tests.Integration;

// Dual-instance network harness — INCREMENT 3: real node replication via the SHIPPED netcode.
//
// Increments 1 (handshake) and 2 (authoritative RPC state) proved the transport
// and the per-tick state channel using throwaway harness code. This increment
// drives the REAL game netcode end-to-end:
//
//   • The server boots through NetworkManager.StartDedicatedServer — the headless
//     authoritative-host path (ADR-0007) that has never been proven to run on CI
//     (issue #32). No local player; players exist only for connecting clients.
//   • The client joins through NetworkManager.JoinGame.
//   • On connect, the server's OnPeerConnected → SpawnPlayer instantiates the
//     spawnable under the Players root, named by the peer id (the identity
//     contract), and the sibling MultiplayerSpawner replicates that node's
//     EXISTENCE to the client automatically (auto-spawn, because the scene is in
//     _spawnable_scenes). See NetworkManager's class doc.
//
// What this proves that increment 2 does not: the spawner actually materialises a
// server-authored node on a real remote peer, at the identical path both machines
// agree on (Players/<peerId>). The node's transform/state is NOT the spawner's job
// — that rides the RPC tick loop increment 2 already covers — so the spawnable is a
// bare Node3D and the assertion is existence + peer-id naming, nothing more.
//
//   godot --headless --path . res://tests/integration/NetNodeReplicationTest.tscn -- --harness-role=server --harness-port=PORT
//   godot --headless --path . res://tests/integration/NetNodeReplicationTest.tscn -- --harness-role=client --harness-port=PORT
//
//   Exit: 0 = the client observed its own player node replicate under Players/,
//   1 = failed/timed out (via GetTree().Quit). Orchestrated by
//   run-net-node-replication.sh, which reads the client's exit code as the verdict.
public partial class NetNodeReplicationTest : Node3D
{
    private const double TimeoutSeconds = 20.0;

    // After the client has seen its node replicate, linger briefly so the server's
    // spawn path is provably exercised on both ends before teardown.
    private const double ServerLingerSeconds = 3.0;

    private string _role = "server";
    private int _port = 7777;
    private double _elapsed;
    private bool _finished;

    private NetworkManager _net;
    private Node _players;

    // Server: when a client connected (so we know the spawn fired) and can linger.
    private bool _peerConnected;
    private double _connectedAt = -1.0;

    // Client: our own peer id, captured on connect; we wait for Players/<id>.
    private int _myPeerId = -1;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _role = ReadArg(args, "--harness-role", "server");
        _port = int.TryParse(ReadArg(args, "--harness-port", "7777"), out int p) ? p : 7777;

        GD.Print($"[net-noderepl] role={_role} port={_port} booting…");

        _net = GetNode<NetworkManager>("NetworkManager");
        _players = GetNode("Players");

        // Drive the spawn at the bare harness spawnable, not the heavyweight
        // Player.tscn (see HarnessPlayer.tscn for why). This matches the path in
        // the MultiplayerSpawner's _spawnable_scenes so auto-spawn replication fires.
        _net.PlayerScenePath = "res://tests/integration/HarnessPlayer.tscn";

        if (_role == "client")
        {
            // Capture our peer id the moment the handshake completes; the server
            // names our node after it, so that's what we wait to see replicate.
            Multiplayer.ConnectedToServer += OnClientConnected;
            Multiplayer.ConnectionFailed += OnClientFailed;
            _net.JoinGame("127.0.0.1", _port);
        }
        else
        {
            // The real headless authoritative-host path (ADR-0007 / #32): no local
            // player, spawns only for connecting peers.
            Multiplayer.PeerConnected += OnServerPeerConnected;
            _net.StartDedicatedServer(_port);
        }
    }

    private void OnClientConnected()
    {
        _myPeerId = Multiplayer.GetUniqueId();
        GD.Print($"[net-noderepl] client connected as peer {_myPeerId}; awaiting Players/{_myPeerId}");
    }

    private void OnClientFailed()
    {
        GD.PrintErr("[net-noderepl] client connection failed");
        Finish(1);
    }

    private void OnServerPeerConnected(long id)
    {
        GD.Print($"[net-noderepl] server saw peer {id}; SpawnPlayer should have fired");
        _peerConnected = true;
        if (_connectedAt < 0)
            _connectedAt = _elapsed;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished)
            return;

        _elapsed += delta;

        if (_role == "client")
        {
            // The identity contract: the server named our node after our own peer
            // id, so the node we wait for is Players/<myPeerId>. Its appearance is
            // MultiplayerSpawner replication of a server-authored spawn — exactly
            // the existence channel every dual-instance verify relies on.
            if (_myPeerId > 0 && _players.HasNode(_myPeerId.ToString()))
            {
                GD.Print($"[net-noderepl] client observed replicated node Players/{_myPeerId}");
                Finish(0);
            }
        }
        else // server
        {
            // Exit success once a client has connected (so SpawnPlayer ran) and we
            // have lingered, mirroring the increment-2 server cadence.
            if (_peerConnected
                && _connectedAt >= 0
                && _elapsed - _connectedAt >= ServerLingerSeconds)
            {
                Finish(0);
            }
        }

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            GD.PrintErr($"[net-noderepl] {_role} timed out: peerConnected={_peerConnected} myPeerId={_myPeerId}");
            Finish(1);
        }
    }

    private void Finish(int code)
    {
        _finished = true;
        GD.Print($"[net-noderepl] {_role} RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }

    private static string ReadArg(string[] args, string flag, string fallback)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == flag && i + 1 < args.Length)
                return args[i + 1];
            if (args[i].StartsWith(flag + "="))
                return args[i].Substring(flag.Length + 1);
        }
        return fallback;
    }
}
