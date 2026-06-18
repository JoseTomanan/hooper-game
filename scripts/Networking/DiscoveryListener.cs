using System.Collections.Generic;
using Godot;

namespace Hooper.Networking;

/// <summary>
/// Client-side LAN discovery: listens for ServerBeacon broadcasts and maintains a
/// live ServerList the server browser displays (ADR-0007). Thin engine wrapper
/// around PacketPeerUDP — decode + bookkeeping are the pure, unit-tested
/// ServerBeacon / ServerList; this node is untested by design.
///
/// Listening is started ON DEMAND (StartListening), not in _Ready, so the socket
/// is only bound while the browser is open. That matters because a UDP listener
/// binds the discovery port exclusively: two browser clients on one machine would
/// contend for it — a single-host limitation noted in ADR-0007's acceptance.
///
/// Sources (verified against live Godot docs, ADR-0001; Context7 MCP not connected
/// this session):
///   - https://docs.godotengine.org/en/stable/classes/class_packetpeerudp.html
///     Listener sequence: Bind(port) → poll GetAvailablePacketCount() → GetPacket()
///     + GetPacketIp(). Bind returns Error; GetPacketIp() is the sender's address.
///
/// Scene wiring (editor, M6 — see EDITOR_TASKS.md): the ServerBrowser owns and
/// drives this node; see ServerBrowser for the assignment.
/// </summary>
public partial class DiscoveryListener : Node
{
	/// <summary>UDP port to listen on. Must match DiscoveryBroadcaster.DiscoveryPort.</summary>
	[Export] public int DiscoveryPort { get; set; } = 7778;

	/// <summary>Seconds without a beacon before a server is dropped from the list.</summary>
	[Export] public float TimeoutSeconds { get; set; } = 3.0f;

	/// <summary>A fresh snapshot of currently-discovered servers, for the browser to draw rows.</summary>
	public IReadOnlyList<ServerListEntry> DiscoveredServers => _servers.Servers;

	private readonly ServerList _servers = new();
	private PacketPeerUdp _udp;
	private bool _listening;

	/// <summary>
	/// Binds the discovery port and begins collecting beacons. Idempotent — a
	/// second call while already listening is ignored.
	/// </summary>
	public void StartListening()
	{
		if (_listening) return;

		_udp = new PacketPeerUdp();
		// bind_address "*" = all interfaces. Returns Error; a failure here usually
		// means another process already holds the discovery port on this host.
		Error err = _udp.Bind(DiscoveryPort, "*");
		if (err != Error.Ok)
		{
			GD.PrintErr($"[DiscoveryListener] Bind({DiscoveryPort}) failed: {err}. " +
			            "Another instance on this machine may already be listening.");
			_udp = null;
			return;
		}

		_listening = true;
		GD.Print("[DiscoveryListener] Listening for servers on discovery port ", DiscoveryPort);
	}

	/// <summary>Stops listening and releases the discovery port.</summary>
	public void StopListening()
	{
		if (!_listening) return;
		_udp?.Close();
		_udp = null;
		_listening = false;
	}

	public override void _ExitTree() => StopListening();

	public override void _Process(double delta)
	{
		if (!_listening) return;

		double now = Time.GetTicksMsec() / 1000.0;

		// Drain every packet that arrived since the last frame.
		while (_udp.GetAvailablePacketCount() > 0)
		{
			byte[] data = _udp.GetPacket();
			// C# binding capitalizes the initialism (GetPacketIP, not GetPacketIp
			// as the GDScript docs render it) — ADR-0001 naming churn.
			string senderIp = _udp.GetPacketIP();

			// Strict decode: any non-beacon UDP traffic on this port is dropped.
			if (ServerBeacon.TryDecode(data, out ServerBeacon beacon))
				_servers.Observe(senderIp, beacon, now);
		}

		// Expire servers that stopped broadcasting (closed / left the network).
		_servers.PruneExpired(now, TimeoutSeconds);
	}
}
