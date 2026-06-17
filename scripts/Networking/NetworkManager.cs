using Godot;

namespace Hooper.Networking;

/// <summary>
/// Owns the ENet transport layer: hosting, joining, peer lifecycle, and
/// server-side player spawning.
///
/// Architecture (ADR-0002, ADR-0007):
///   - Two coexisting server-entry paths share one bringup core (StartServer):
///       • HostGame — listen-server: the host is simultaneously server + player 1.
///       • StartDedicatedServer — headless: authoritative host with NO local
///         player; every player node is a connecting client (ADR-0007). This
///         path is transitional scaffolding's eventual replacement, not an
///         add-on to it — see ADR-0007's coexistence note.
///   - Godot's MultiplayerSpawner replicates the *existence* of player nodes.
///     Position is handled by our own tick loop (see PlayerController), not by
///     MultiplayerSynchronizer — because reconciliation requires our own code.
///   - This node never touches movement math. Its only job is connection
///     lifecycle and spawning player nodes when peers connect/disconnect.
///
/// Scene wiring (editor, issue #7):
///   - Attach this script to a Node named "NetworkManager" in Main.tscn.
///   - Assign the Players export to the "Players" Node3D spawn root.
///   - A MultiplayerSpawner sibling must have SpawnPath = Players and
///     Player.tscn in its spawnable scene list (editor step, issue #7).
///
/// Source: https://docs.godotengine.org/en/stable/tutorials/networking/high_level_multiplayer.html
/// </summary>
public partial class NetworkManager : Node
{
	/// <summary>Default port, shown pre-filled in the Lobby UI.</summary>
	public const int DefaultPort = 7777;

	/// <summary>
	/// Maximum simultaneous peers. 1 = 1v1 + one slot of headroom.
	/// The hard limit before we open a second match is 2 players.
	/// </summary>
	private const int MaxClients = 2;

	/// <summary>
	/// Players in a full match — the 1v1 cap, reported in discovery beacons so a
	/// browser can show "1/2" (ADR-0007). The real ceiling is enforced by the
	/// MultiplayerSpawner's spawn_limit (=2) in Main.tscn, not by MaxClients.
	/// </summary>
	public const int MaxPlayersPerMatch = 2;

	// ── Exports ─────────────────────────────────────────────────────────────

	/// <summary>
	/// Spawn root: the Node3D whose children are player nodes.
	/// Assign this in the Godot Inspector (editor, issue #7).
	/// The MultiplayerSpawner must point its SpawnPath here too.
	/// </summary>
	[Export] public Node Players { get; set; }

	/// <summary>
	/// Path to Player.tscn, used when the server spawns a player node.
	/// Matches what the MultiplayerSpawner's spawnable-scene list holds.
	/// </summary>
	[Export] public string PlayerScenePath { get; set; } = "res://scenes/Player.tscn";

	// ── Signals ─────────────────────────────────────────────────────────────

	/// <summary>
	/// Emitted on every peer (host and client) when the lobby is ready —
	/// the Lobby UI listens to this to hide itself and reveal the court.
	/// </summary>
	[Signal] public delegate void GameReadyEventHandler();

	/// <summary>
	/// Emitted when the connection fails (client side only).
	/// </summary>
	[Signal] public delegate void ConnectionFailedEventHandler();

	/// <summary>
	/// Emitted on the SERVER (both listen-server and dedicated) once the ENet
	/// server is up, carrying the game port. DiscoveryBroadcaster listens for this
	/// to begin advertising the server on the LAN (ADR-0007). A pure client never
	/// starts a server, so it never fires there.
	/// </summary>
	[Signal] public delegate void ServerStartedEventHandler(int port);

	// ── State ───────────────────────────────────────────────────────────────

	/// <summary>
	/// True once HostGame or JoinGame has been called successfully.
	/// Guards against double-call which would stack signal handlers and
	/// produce duplicate spawns (found in doubt-driven review, cycle 1).
	/// M1b is one-shot (one game per process launch) so this is sufficient.
	/// </summary>
	private bool _started;

	/// <summary>
	/// True once StartDedicatedServer ran — this process is a headless
	/// authoritative host with no local player (ADR-0007). Currently
	/// informational (logging / future branch points); the spawn path itself
	/// needs no per-tick check because role dispatch already falls out of node
	/// naming (no node named "1" on the server).
	/// </summary>
	private bool _isDedicated;

	// ── Host / Join ──────────────────────────────────────────────────────────

	/// <summary>
	/// Start a listen-server on <paramref name="port"/>. The host is also
	/// player 1: after the server is up we spawn our own player node here.
	/// Called by Lobby.cs when the user presses "Host".
	/// </summary>
	public void HostGame(int port)
	{
		if (!StartServer(port)) return;

		// Spawn our own (host) player node immediately — no PeerConnected fires
		// for ourselves, so we do it here. The server is always peer 1.
		// This is the listen-server topology: host = server + player 1 (ADR-0002).
		SpawnPlayer(1);

		// Host is ready as soon as the server is up; the client becomes ready
		// in ConnectedToServer. Emitting here lets the host's Lobby hide itself.
		// Trade-off (doubt cycle 1): the lobby hides before a client joins, which
		// is intentional for M1b UX (host sees the court while waiting).
		EmitSignal(SignalName.GameReady);
	}

	/// <summary>
	/// Start a HEADLESS dedicated server on <paramref name="port"/> (ADR-0007).
	/// Called by DedicatedServerBootstrap when the process is launched with the
	/// --dedicated flag, NOT from the Lobby UI.
	///
	/// The difference from HostGame is precisely the two listen-server
	/// assumptions a headless host has no business making:
	///   • It does NOT SpawnPlayer(1) — peer 1 is the server, which has no local
	///     player. Player nodes are created only for connecting clients, via
	///     OnPeerConnected. PlayerController routes every such node to its
	///     TickServerRemotePlayer role (the IsServer && IsLocalPlayer role is
	///     simply never reached, since no node is named "1"). See ADR-0007.
	///   • It does NOT emit GameReady — that signal exists only to tell the Lobby
	///     overlay to hide itself, and a dedicated server has no Lobby. Gameplay
	///     ticking depends on the multiplayer peer + spawned nodes, not on this
	///     signal, so omitting it changes nothing about the simulation.
	/// </summary>
	public void StartDedicatedServer(int port)
	{
		if (!StartServer(port)) return;

		_isDedicated = true;
		GD.Print("[NetworkManager] Dedicated server running headless; awaiting clients.");
		// No SpawnPlayer, no GameReady — see method doc.
	}

	/// <summary>
	/// Shared server-bringup core for BOTH HostGame (listen-server) and
	/// StartDedicatedServer (headless). Factoring this out is what keeps the two
	/// entry paths from drifting as the netcode evolves (ADR-0007 names that the
	/// primary coexistence risk). Returns true on success; on failure it emits
	/// ConnectionFailed (so the Lobby can recover) and returns false.
	/// </summary>
	private bool StartServer(int port)
	{
		if (_started)
		{
			GD.PrintErr("[NetworkManager] StartServer called while already started; ignoring.");
			return false;
		}

		// Source: https://docs.godotengine.org/en/stable/classes/class_enetmultiplayerpeer.html
		var peer = new ENetMultiplayerPeer();
		Error err = peer.CreateServer(port, MaxClients);
		if (err != Error.Ok)
		{
			GD.PrintErr($"[NetworkManager] CreateServer failed: {err}");
			// Emit ConnectionFailed so Lobby re-enables its buttons.
			// (Found in doubt-driven review, cycle 1: silent failure left lobby stuck.)
			// Harmless on the dedicated path — nothing listens to it there.
			EmitSignal(SignalName.ConnectionFailed);
			return false;
		}

		_started = true;

		// Assign to the scene-tree Multiplayer singleton. From this point
		// _PhysicsProcess on all nodes runs under the multiplayer context.
		// Source: high_level_multiplayer.html — "Multiplayer.MultiplayerPeer = peer"
		Multiplayer.MultiplayerPeer = peer;

		// Subscribe to peer lifecycle events.
		// ⚠ Handler parameter is long, not int — Godot's C# signal bindings
		//   map GDScript int to C# long in event delegates.
		// Source: high_level_multiplayer.html C# example
		Multiplayer.PeerConnected    += OnPeerConnected;
		Multiplayer.PeerDisconnected += OnPeerDisconnected;

		GD.Print("[NetworkManager] Server up on port ", port);

		// Tell the discovery broadcaster (if any) to start advertising on the LAN.
		// Fires for both topologies, so listen-server host games are discoverable
		// too — a free win, same code path (ADR-0007).
		EmitSignal(SignalName.ServerStarted, port);
		return true;
	}

	/// <summary>
	/// Connect to an existing server at <paramref name="ip"/>:<paramref name="port"/>.
	/// Called by Lobby.cs when the user presses "Join".
	/// </summary>
	public void JoinGame(string ip, int port)
	{
		if (_started)
		{
			GD.PrintErr("[NetworkManager] JoinGame called while already started; ignoring.");
			return;
		}

		// Source: https://docs.godotengine.org/en/stable/classes/class_enetmultiplayerpeer.html
		var peer = new ENetMultiplayerPeer();
		Error err = peer.CreateClient(ip, port);
		if (err != Error.Ok)
		{
			GD.PrintErr($"[NetworkManager] CreateClient failed: {err}");
			EmitSignal(SignalName.ConnectionFailed);
			return;
		}

		_started = true;
		Multiplayer.MultiplayerPeer = peer;

		// For clients, connection success arrives via ConnectedToServer.
		// Connection failure arrives via ConnectionFailed (MultiplayerAPI signal).
		Multiplayer.ConnectedToServer  += OnConnectedToServer;
		Multiplayer.ConnectionFailed   += OnConnectionFailed;

		GD.Print("[NetworkManager] Joining ", ip, ":", port);
	}

	// ── Lifecycle ────────────────────────────────────────────────────────────

	/// <summary>
	/// Unsubscribe all signal handlers so Godot doesn't hold dangling references
	/// if this node is freed (e.g., scene reload).
	/// (Found in doubt-driven review, cycle 1.)
	/// </summary>
	public override void _ExitTree()
	{
		if (Multiplayer == null) return;
		Multiplayer.PeerConnected     -= OnPeerConnected;
		Multiplayer.PeerDisconnected  -= OnPeerDisconnected;
		Multiplayer.ConnectedToServer -= OnConnectedToServer;
		Multiplayer.ConnectionFailed  -= OnConnectionFailed;
	}

	// ── Server-side: peer lifecycle ──────────────────────────────────────────

	/// <summary>
	/// Called on the SERVER when a client successfully connects.
	/// Only the server receives this — clients get ConnectedToServer instead.
	/// Spawn a player node for the newly-joined peer.
	/// </summary>
	private void OnPeerConnected(long id)
	{
		// Only the server should spawn players. Clients receive the spawn via
		// MultiplayerSpawner replication automatically.
		if (!Multiplayer.IsServer()) return;

		GD.Print("[NetworkManager] Peer connected: ", id);
		SpawnPlayer((int)id);
	}

	/// <summary>
	/// Called on the SERVER (and other clients) when a peer disconnects.
	/// Remove their player node.
	/// </summary>
	private void OnPeerDisconnected(long id)
	{
		if (!Multiplayer.IsServer()) return;

		GD.Print("[NetworkManager] Peer disconnected: ", id);
		DespawnPlayer((int)id);
	}

	// ── Client-side: connection result ───────────────────────────────────────

	/// <summary>
	/// Called on the CLIENT when the handshake with the server completes.
	/// The client's own player node arrives via MultiplayerSpawner, not from
	/// here — we just emit GameReady so the Lobby hides itself.
	/// </summary>
	private void OnConnectedToServer()
	{
		GD.Print("[NetworkManager] Connected to server as peer ", Multiplayer.GetUniqueId());
		EmitSignal(SignalName.GameReady);
	}

	/// <summary>Called on the CLIENT when the connection to the server fails.</summary>
	private void OnConnectionFailed()
	{
		GD.PrintErr("[NetworkManager] Connection failed.");
		Multiplayer.MultiplayerPeer = null;
		EmitSignal(SignalName.ConnectionFailed);
	}

	// ── Spawn helpers (server-only) ──────────────────────────────────────────

	/// <summary>
	/// Instantiates Player.tscn under the Players spawn root, names it by
	/// peer ID, and stores which peer it belongs to so PlayerController can
	/// route inputs correctly.
	///
	/// Naming by peer ID is the identity contract:
	///   • The node path is identical on every machine (both have "Players/1",
	///     "Players/2", …), so RPCs targeting them by path resolve correctly.
	///   • Each PlayerController checks Name == Multiplayer.GetUniqueId() to
	///     know if it's the locally-controlled player.
	///
	/// MultiplayerSpawner replicates the AddChild to all clients automatically
	/// because Players is its SpawnPath and Player.tscn is in SpawnableScenes.
	/// </summary>
	private void SpawnPlayer(int peerId)
	{
		if (Players == null)
		{
			GD.PrintErr("[NetworkManager] Players node is not assigned. Wire it in the Inspector.");
			return;
		}

		string name = peerId.ToString();

		// Guard: if a node with this name already exists, don't spawn a duplicate.
		// Prevents a second node named "1@2" (Godot's auto-rename) breaking the
		// identity contract that PlayerController relies on.
		// (Found in doubt-driven review, cycle 1: duplicate spawn risk.)
		if (Players.HasNode(name))
		{
			GD.Print("[NetworkManager] Player node already exists for peer ", peerId, "; skipping.");
			return;
		}

		var scene = GD.Load<PackedScene>(PlayerScenePath);
		if (scene == null)
		{
			GD.PrintErr("[NetworkManager] Could not load player scene at: ", PlayerScenePath);
			return;
		}

		Node player = scene.Instantiate();

		// Naming by peer ID is the identity contract described above.
		// All player nodes keep default authority (server = peer 1) — we do NOT
		// call SetMultiplayerAuthority because the server owns all transforms.
		// Clients identify "their" node by matching Name to GetUniqueId().
		// (Peer IDs are 32-bit in practice; cast from long is safe for M1b.)
		player.Name = name;

		Players.AddChild(player);
		GD.Print("[NetworkManager] Spawned player for peer ", peerId);
	}

	/// <summary>
	/// Removes the player node for a disconnected peer.
	/// MultiplayerSpawner propagates the RemoveChild to clients automatically.
	/// </summary>
	private void DespawnPlayer(int peerId)
	{
		Node player = Players?.GetNodeOrNull(peerId.ToString());
		if (player == null)
		{
			GD.PrintErr("[NetworkManager] No player node found for peer ", peerId);
			return;
		}

		player.QueueFree();
		GD.Print("[NetworkManager] Despawned player for peer ", peerId);
	}
}
