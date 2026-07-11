using System.Linq;
using Godot;

namespace HOOPERGAME.Tests.Integration;

// Dual-instance network harness — INCREMENT 2: server-authoritative state sync.
//
// Increment 1 (NetHandshakeTest) proved two headless Godot .NET processes
// complete an ENet handshake. This proves the next thing every dual-instance
// verify actually needs: an AUTHORITATIVE value set on the server REACHES the
// client over the wire, monotonically and in order.
//
// It mirrors the game's REAL state-sync mechanism, not a toy. Per NetworkManager's
// own design note, MultiplayerSpawner only replicates node *existence* — the
// authoritative *state* (player positions, ball) is propagated by the game's own
// `Rpc(MethodName.ReceiveState, …)` broadcasts and reconciled in the tick loop
// (ADR-0002). This harness exercises exactly that path: the server (multiplayer
// authority, peer 1) increments an authoritative tick counter every physics frame
// and RPCs it to the client; the client asserts it receives a strictly-increasing
// stream. A client that only handshook but received no broadcast would time out.
//
//   godot --headless --path . res://tests/integration/NetStateSyncTest.tscn -- --harness-role=server --harness-port=PORT
//   godot --headless --path . res://tests/integration/NetStateSyncTest.tscn -- --harness-role=client --harness-port=PORT
//
//   Exit: 0 = the client received the required run of in-order authoritative
//   updates, 1 = failed/timed out (via GetTree().Quit). Orchestrated by
//   run-net-state-sync.sh, which reads the client's exit code as the verdict.
public partial class NetStateSyncTest : Node
{
    private const double TimeoutSeconds = 20.0;

    // How many in-order authoritative updates the client must receive to pass.
    // At 60 Hz this is well under a second once connected, but proves a sustained
    // stream rather than a single lucky packet.
    private const int RequiredUpdates = 15;

    // After the client has its run of updates, linger briefly so the server-side
    // broadcast loop is provably exercised before teardown.
    private const double ServerLingerSeconds = 3.0;

    private string _role = "server";
    private int _port = 7777;
    private double _elapsed;
    private bool _finished;
    private bool _peerConnected;

    // Server: the authoritative value being broadcast.
    private int _serverTick;
    private double _connectedAt = -1.0;

    // Client: tracking the received stream for monotonicity + count.
    private int _lastReceived = -1;
    private int _updatesReceived;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _role = HarnessArgs.ReadArg(args, "--harness-role", "server");
        _port = int.TryParse(HarnessArgs.ReadArg(args, "--harness-port", "7777"), out int p) ? p : 7777;

        GD.Print($"[net-statesync] role={_role} port={_port} booting…");

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
            GD.PrintErr($"[net-statesync] {_role} peer create failed: {err}");
            Finish(1);
            return;
        }

        Multiplayer.MultiplayerPeer = peer;
        GD.Print($"[net-statesync] {_role} peer up; waiting…");
    }

    private void OnClientConnected()
    {
        GD.Print($"[net-statesync] client connected as peer {Multiplayer.GetUniqueId()}");
        _peerConnected = true;
    }

    private void OnClientFailed()
    {
        GD.PrintErr("[net-statesync] client connection failed");
        Finish(1);
    }

    private void OnServerPeerConnected(long id)
    {
        GD.Print($"[net-statesync] server saw peer {id}; beginning authoritative broadcast");
        _peerConnected = true;
        if (_connectedAt < 0)
            _connectedAt = _elapsed;
    }

    // Runs on the CLIENT only: the server is the node's multiplayer authority
    // (peer 1, the default), so RpcMode.Authority means only the server may invoke
    // this remotely. Reliable + ordered so the monotonic assertion is meaningful.
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void ReceiveAuthoritativeTick(int serverTick)
    {
        if (serverTick <= _lastReceived)
        {
            GD.PrintErr($"[net-statesync] OUT-OF-ORDER authoritative value: {serverTick} after {_lastReceived}");
            Finish(1);
            return;
        }
        _lastReceived = serverTick;
        _updatesReceived++;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished)
            return;

        _elapsed += delta;

        if (_role == "server")
        {
            // Broadcast the authoritative value every tick once a client is on —
            // the same per-tick ReceiveState cadence the real sim uses (ADR-0002).
            if (_peerConnected)
            {
                _serverTick++;
                Rpc(MethodName.ReceiveAuthoritativeTick, _serverTick);

                // Exit success once we have surely streamed enough for the client
                // to have met its bar, plus a linger.
                if (_connectedAt >= 0
                    && _serverTick >= RequiredUpdates
                    && _elapsed - _connectedAt >= ServerLingerSeconds)
                {
                    Finish(0);
                }
            }
        }
        else // client
        {
            if (_updatesReceived >= RequiredUpdates)
            {
                GD.Print($"[net-statesync] client received {_updatesReceived} in-order updates (last={_lastReceived})");
                Finish(0);
            }
        }

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            GD.PrintErr($"[net-statesync] {_role} timed out: connected={_peerConnected} received={_updatesReceived}");
            Finish(1);
        }
    }

    private void Finish(int code)
    {
        _finished = true;
        GD.Print($"[net-statesync] {_role} RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
