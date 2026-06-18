using Godot;

namespace Hooper.Networking;

/// <summary>
/// Server-side LAN discovery: periodically broadcasts a ServerBeacon over UDP so
/// browser clients on the same network can find this server (ADR-0007). The thin
/// engine wrapper around PacketPeerUDP — the packet format and bookkeeping are the
/// pure, unit-tested ServerBeacon / ServerList; this node is untested by design
/// (it touches the socket and the engine clock), exactly like BallController.
///
/// Activated by NetworkManager.ServerStarted, so it runs on a dedicated server
/// AND a listen-server host, and stays idle on a pure client (which never starts
/// a server). It broadcasts every BroadcastInterval seconds until the tree exits.
///
/// Sources (verified against live Godot docs, ADR-0001; Context7 MCP not connected
/// this session):
///   - https://docs.godotengine.org/en/stable/classes/class_packetpeerudp.html
///     Sender sequence: SetBroadcastEnabled(true) → SetDestAddress(addr, port) →
///     PutPacket(bytes). PutPacket/SetDestAddress return Error.
///
/// Scene wiring (editor, M6 — see EDITOR_TASKS.md):
///   - Add a Node "DiscoveryBroadcaster" under Main, attach this script.
///   - Assign NetworkManager (to listen for ServerStarted and read player counts)
///     and Players (the spawn root) in the Inspector.
/// </summary>
public partial class DiscoveryBroadcaster : Node
{
	/// <summary>UDP port discovery beacons are sent to. Distinct from the game port.</summary>
	[Export] public int DiscoveryPort { get; set; } = 7778;

	/// <summary>Seconds between beacons. ~1 Hz; the client expires after ~3 missed.</summary>
	[Export] public float BroadcastInterval { get; set; } = 1.0f;

	/// <summary>Human-readable server name shown in the browser.</summary>
	[Export] public string ServerName { get; set; } = "Hooper Server";

	/// <summary>NetworkManager — source of the ServerStarted signal. Assign in Inspector.</summary>
	[Export] public NetworkManager NetworkManager { get; set; }

	/// <summary>Spawn root, to count current players for the beacon. Assign in Inspector.</summary>
	[Export] public Node Players { get; set; }

	private PacketPeerUdp _udp;
	private int _gamePort;
	private bool _active;
	private double _sinceLastBroadcast;

	public override void _Ready()
	{
		if (NetworkManager != null)
			NetworkManager.ServerStarted += OnServerStarted;
		else
			GD.PrintErr("[DiscoveryBroadcaster] NetworkManager not assigned; cannot start broadcasting.");
	}

	public override void _ExitTree()
	{
		if (NetworkManager != null)
			NetworkManager.ServerStarted -= OnServerStarted;
		_udp?.Close();
	}

	private void OnServerStarted(int port)
	{
		_gamePort = port;

		_udp = new PacketPeerUdp();
		_udp.SetBroadcastEnabled(true);
		// Limited broadcast address: reaches the local subnet without needing to
		// know its mask. Note (ADR-0007): this does not reliably loop back on a
		// single host, which is why single-machine discovery can't be fully proven.
		Error err = _udp.SetDestAddress("255.255.255.255", DiscoveryPort);
		if (err != Error.Ok)
		{
			GD.PrintErr($"[DiscoveryBroadcaster] SetDestAddress failed: {err}; discovery disabled.");
			return;
		}

		_active = true;
		_sinceLastBroadcast = BroadcastInterval; // send one immediately
		GD.Print("[DiscoveryBroadcaster] Advertising game port ", _gamePort, " on discovery port ", DiscoveryPort);
	}

	public override void _Process(double delta)
	{
		if (!_active) return;

		_sinceLastBroadcast += delta;
		if (_sinceLastBroadcast < BroadcastInterval) return;
		_sinceLastBroadcast = 0.0;

		int curPlayers = Players?.GetChildCount() ?? 0;
		var beacon = new ServerBeacon(
			gamePort: (ushort)_gamePort,
			curPlayers: (byte)Mathf.Min(curPlayers, 255),
			maxPlayers: NetworkManager.MaxPlayersPerMatch,
			name: ServerName);

		Error err = _udp.PutPacket(beacon.Encode());
		if (err != Error.Ok)
			GD.PrintErr($"[DiscoveryBroadcaster] PutPacket failed: {err}");
	}
}
