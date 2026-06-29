using System.Linq;
using Godot;

namespace HOOPERGAME.Tests.Integration;

// Dual-instance network harness — INCREMENT 1: the ENet handshake keystone.
//
// The single-instance smoke test (IntegrationSmokeTest) proved one headless
// Godot engine boots and pumps its fixed-tick loop in CI. This proves the next
// capability the whole server-authoritative model (ADR-0002) leans on and that a
// single process structurally cannot show: TWO headless Godot .NET processes
// establishing a real ENetMultiplayerPeer connection over localhost in CI.
//
// It is deliberately minimal — it asserts only that the transport handshake
// completes, NOT yet that authoritative state replicates (MultiplayerSpawner,
// reconciled ball/player state). That is increment 2+, layered on this once the
// two-process CI capability itself is proven green. Same staging discipline the
// single-instance smoke test used for the go/no-go bet (ADR-0016).
//
//   One process is launched per role:
//     godot --headless --path . res://tests/integration/NetHandshakeTest.tscn -- --harness-role=server --harness-port=PORT
//     godot --headless --path . res://tests/integration/NetHandshakeTest.tscn -- --harness-role=client --harness-port=PORT
//   Orchestrated by tests/integration/run-net-handshake.sh, which reads the
//   CLIENT's exit code as the verdict (a client cannot report "connected" unless
//   the server bound the port and completed the ENet handshake).
//
//   Exit: 0 = handshake observed, 1 = failed/timed out (via GetTree().Quit).
public partial class NetHandshakeTest : Node
{
    // Generous because Godot .NET cold-boot + ENet handshake on a CI runner is
    // slower and jitterier than the deterministic fixed-tick loop. The verdict is
    // pass/fail, not timing, so a wide ceiling costs nothing but flake-resistance.
    private const double TimeoutSeconds = 20.0;

    // After the server first sees a peer, hold briefly before quitting so the
    // CLIENT side finishes its own ConnectedToServer handshake — tearing the
    // server down the instant PeerConnected fires can abort the client mid-shake.
    private const double ServerLingerSeconds = 3.0;

    private string _role = "server";
    private int _port = 7777;
    private double _elapsed;
    private double _peerSeenAt = -1.0;
    private bool _connected;
    private bool _finished;

    public override void _Ready()
    {
        // Merge both arg arrays, exactly as DedicatedServerBootstrap does — which
        // array the "--" separator routes user args into varies, so read both.
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _role = ReadArg(args, "--harness-role", "server");
        _port = int.TryParse(ReadArg(args, "--harness-port", "7777"), out int p) ? p : 7777;

        GD.Print($"[net-harness] role={_role} port={_port} booting…");

        var peer = new ENetMultiplayerPeer();
        Error err;
        if (_role == "client")
        {
            err = peer.CreateClient("127.0.0.1", _port);
            Multiplayer.ConnectedToServer += OnClientConnected;
            Multiplayer.ConnectionFailed += OnClientFailed;
        }
        else
        {
            err = peer.CreateServer(_port, 2);
            Multiplayer.PeerConnected += OnServerPeerConnected;
        }

        if (err != Error.Ok)
        {
            GD.PrintErr($"[net-harness] {_role} peer create failed: {err}");
            Finish(1);
            return;
        }

        Multiplayer.MultiplayerPeer = peer;
        GD.Print($"[net-harness] {_role} peer up; waiting for handshake…");
    }

    // CLIENT: the handshake completed from our side — success.
    private void OnClientConnected()
    {
        GD.Print($"[net-harness] client connected as peer {Multiplayer.GetUniqueId()}");
        _connected = true;
    }

    private void OnClientFailed()
    {
        GD.PrintErr("[net-harness] client connection failed");
        Finish(1);
    }

    // SERVER: a client completed the handshake on our side. Linger briefly so the
    // client can finish its own side, then exit success.
    private void OnServerPeerConnected(long id)
    {
        GD.Print($"[net-harness] server saw peer {id}");
        if (_peerSeenAt < 0)
            _peerSeenAt = _elapsed;
        _connected = true;
    }

    public override void _Process(double delta)
    {
        if (_finished)
            return;

        _elapsed += delta;

        if (_role == "client")
        {
            if (_connected)
                Finish(0);
        }
        else // server
        {
            // Quit success once a peer connected AND we have lingered long enough
            // for the client to complete its half of the handshake.
            if (_connected && _elapsed - _peerSeenAt >= ServerLingerSeconds)
                Finish(0);
        }

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            GD.PrintErr($"[net-harness] {_role} timed out after {TimeoutSeconds}s without a handshake");
            Finish(1);
        }
    }

    private void Finish(int code)
    {
        _finished = true;
        GD.Print($"[net-harness] {_role} RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }

    // Supports "--flag value" (two tokens) and "--flag=value" (joined), mirroring
    // DedicatedServerArgs' tolerance for both spellings.
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
