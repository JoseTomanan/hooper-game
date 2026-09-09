using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Hooper.Networking;
using Hooper.Player;
using Hooper.Systems;

namespace HOOPERGAME.Tests.Integration;

/// <summary>
/// Production-scene topology proof for #372. Each process wraps the shipped
/// Main.tscn, enters through NetworkManager's real host/join/dedicated paths,
/// and asserts the resulting player nodes, roster mirror, spawns, and HUD.
/// Files only coordinate the independent processes and exchange their real peer
/// IDs; every gameplay assertion reads production nodes in one physics tick.
/// </summary>
public partial class DedicatedRosterTest : Node
{
	private const double TimeoutSeconds = 55.0;
	private const double ListenerBindTimeoutSeconds = 2.0;
	private const float PositionTolerance = 0.08f;
	private const float DistinctSpawnDistance = 0.5f;

	private string _scenario = "dedicated";
	private string _role = "server";
	private string _coordinationDir = ".godot/dedicated-roster";
	private int _port = 23465;
	private int _discoveryPort = 27784;
	private double _elapsed;
	private int _phase;
	private bool _finished;
	private bool _serverStarted;
	private bool _dedicatedListenerEverActive;
	private int _scoreChangedCount;
	private int _rosterChangedCount;

	private Node3D _main;
	private NetworkManager _network;
	private ServerBrowser _browser;
	private GameManager _game;
	private ScoreHud _hud;
	private Node _players;
	private int _myPeerId;

	public override void _Ready()
	{
		string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
		_scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "dedicated");
		_role = HarnessArgs.ReadArg(args, "--harness-role", "server");
		_port = ReadIntArg(args, "--harness-port", 23465);
		_discoveryPort = ReadIntArg(args, "--harness-discovery-port", 27784);
		_coordinationDir = HarnessArgs.ReadArg(args, "--harness-coordination-dir", ".godot/dedicated-roster");
		Directory.CreateDirectory(_coordinationDir);

		// Configure the detached production tree before AddChild triggers its
		// child-first _Ready callbacks and the discovery socket can bind.
		_main = ResourceLoader.Load<PackedScene>("res://scenes/Main.tscn").Instantiate<Node3D>();
		_main.Name = "ProductionMain";
		_network = _main.GetNode<NetworkManager>("NetworkManager");
		_browser = _main.GetNode<ServerBrowser>("ServerBrowser");
		_game = _main.GetNode<GameManager>("GameManager");
		_hud = _main.GetNode<ScoreHud>("CanvasLayer/Label");
		_players = _main.GetNode("Players");
		_browser.Discovery.DiscoveryPort = _discoveryPort;
		DiscoveryListener.ResetStartListeningCallCountForHarness();
		_game.ScoreChanged += () => _scoreChangedCount++;
		_game.RosterChanged += () => _rosterChangedCount++;

		if (IsServerRole)
			_network.ServerStarted += OnServerStarted;
		else
		{
			_network.GameReady += OnClientGameReady;
			_network.ConnectionFailed += OnClientConnectionFailed;
		}

		AddChild(_main);

		if (_scenario == "dedicated" && !IsServerRole)
			Callable.From(() => _network.JoinGame("127.0.0.1", _port)).CallDeferred();
		else if (_scenario == "listen" && _role == "client")
			Callable.From(() => _network.JoinGame("127.0.0.1", _port)).CallDeferred();

		GD.Print($"[dedicated-roster] scenario={_scenario} role={_role} main={_main.Name} gamePort={_port} discoveryPort={_discoveryPort}.");
	}

	public override void _PhysicsProcess(double delta)
	{
		if (_finished) return;
		_elapsed += delta;

		// This latch makes the dedicated assertion non-vacuous across time: a
		// listener that briefly bound before bootstrap cannot hide by stopping.
		if (_scenario == "dedicated" && _role == "server" && _browser.Discovery.IsListeningForHarness)
			_dedicatedListenerEverActive = true;

		if (_scenario == "dedicated") TickDedicated();
		else if (_scenario == "listen") TickListen();
		else Fail($"unknown scenario '{_scenario}'.");

		if (!_finished && _elapsed > TimeoutSeconds)
			Fail($"timed out in phase {_phase}; scenario={_scenario}, role={_role}, peer={_myPeerId}, players={PlayerIdText()}, scoreSignals={_scoreChangedCount}, rosterSignals={_rosterChangedCount}, hud='{_hud.Text}', listenerCalls={DiscoveryListener.StartListeningCallCountForHarness}.");
	}

	private bool IsServerRole => _role is "server" or "host";

	private void TickDedicated()
	{
		if (_role == "server") TickDedicatedServer();
		else if (_role == "client-a") TickDedicatedClientA();
		else if (_role == "client-b") TickDedicatedClientB();
		else if (_role == "client-c") TickDedicatedClientC();
		else Fail($"unknown dedicated role '{_role}'.");
	}

	private void TickDedicatedServer()
	{
		if (!_serverStarted) return;

		if (_phase == 0 && TryReadPeerId("client-a-joined", out int clientAId)
			&& IsDedicatedIntermediate(clientAId))
		{
			Write("server-a-ready", $"peer={clientAId} spawn=host");
			_phase = 1;
		}

		if (_phase == 1 && TryReadInitialDedicatedIds(out int fullClientAId, out int clientBId)
			&& IsFullTopology(fullClientAId, clientBId, localPeerId: 0))
		{
			Write("server-full", $"a={fullClientAId} b={clientBId}");
			_phase = 2;
		}

		if (_phase == 2 && Exists("client-a-full") && Exists("client-b-full")
			&& TryReadInitialDedicatedIds(out int currentClientAId, out int currentClientBId)
			&& IsFullTopology(currentClientAId, currentClientBId, localPeerId: 0))
			_phase = 3;

		if (_phase == 3 && TryReadInitialDedicatedIds(out int departedClientAId, out int remainingClientBId)
			&& IsDisconnectedTopology(remainingClientBId, departedClientAId))
		{
			Write("server-shrunk", $"remaining={remainingClientBId} departed={departedClientAId}");
			_phase = 4;
		}

		if (_phase == 4 && Exists("client-b-shrunk")
			&& TryReadReplacementDedicatedIds(out int replacementClientCId, out int defenderClientBId)
			&& IsFullTopology(replacementClientCId, defenderClientBId, localPeerId: 0))
		{
			Write("server-replacement-full", $"offense={replacementClientCId} defense={defenderClientBId}");
			_phase = 5;
		}

		if (_phase == 5 && Exists("client-b-replacement-full") && Exists("client-c-replacement-full")
			&& TryReadReplacementDedicatedIds(out int finalClientCId, out int finalClientBId)
			&& IsFullTopology(finalClientCId, finalClientBId, localPeerId: 0))
		{
			// An asymmetric authoritative score makes the clients' public HUD text
			// reveal which roster member each side actually resolved as its opponent.
			_game.RegisterBasket(finalClientCId);
			Write("replacement-scored", $"scorer={finalClientCId}");
			_phase = 6;
		}

		if (_phase == 6 && TryReadReplacementDedicatedIds(out int scoredClientCId, out int scoredClientBId)
			&& _scoreChangedCount == 1
			&& _game.ScoreOf(scoredClientCId) == 1
			&& _game.ScoreOf(scoredClientBId) == 0
			&& Exists("client-b-hud-scored")
			&& Exists("client-c-hud-scored"))
			Pass("dedicated server observed offense-seat reuse, exact remote roster replacement, and the identity-proving score");
	}

	private void TickDedicatedClientA()
	{
		if (_myPeerId <= 1) return;

		if (_phase == 0 && IsDedicatedIntermediate(_myPeerId))
		{
			Write("client-a-ready", $"peer={_myPeerId} spawn=host");
			_phase = 1;
		}

		if (_phase == 1 && TryReadInitialDedicatedIds(out int clientAId, out int clientBId)
			&& clientAId == _myPeerId
			&& IsFullTopology(clientAId, clientBId, _myPeerId))
		{
			Write("client-a-full", $"a={clientAId} b={clientBId}");
			_phase = 2;
		}

		// A deliberately exits first, but only while its complete local topology
		// still co-occurs with both other roles' full-topology barriers.
		if (_phase == 2 && Exists("server-full") && Exists("client-b-full")
			&& TryReadInitialDedicatedIds(out int finalClientAId, out int finalClientBId)
			&& finalClientAId == _myPeerId
			&& IsFullTopology(finalClientAId, finalClientBId, _myPeerId))
			Pass("dedicated client A agreed on the exact two-remote production topology before disconnecting");
	}

	private void TickDedicatedClientB()
	{
		if (_myPeerId <= 1) return;
		if (_phase == 0 && TryReadInitialDedicatedIds(out int clientAId, out int clientBId)
			&& clientBId == _myPeerId
			&& IsFullTopology(clientAId, clientBId, _myPeerId))
		{
			Write("client-b-full", $"a={clientAId} b={clientBId}");
			_phase = 1;
		}

		if (_phase == 1 && Exists("server-full") && Exists("client-a-full")
			&& TryReadInitialDedicatedIds(out int stableClientAId, out int stableClientBId)
			&& stableClientBId == _myPeerId
			&& IsFullTopology(stableClientAId, stableClientBId, _myPeerId))
			_phase = 2;

		if (_phase == 2 && TryReadInitialDedicatedIds(out int departedClientAId, out int remainingClientBId)
			&& remainingClientBId == _myPeerId
			&& IsDisconnectedTopology(remainingClientBId, departedClientAId))
		{
			Write("client-b-shrunk", $"remaining={remainingClientBId} departed={departedClientAId}");
			_phase = 3;
		}

		if (_phase == 3 && TryReadReplacementDedicatedIds(out int replacementClientCId, out int defenderClientBId)
			&& defenderClientBId == _myPeerId
			&& IsFullTopology(replacementClientCId, defenderClientBId, _myPeerId))
		{
			Write("client-b-replacement-full", $"offense={replacementClientCId} defense={defenderClientBId}");
			_phase = 4;
		}

		if (_phase == 4 && Exists("server-replacement-full") && Exists("client-c-replacement-full")
			&& TryReadReplacementDedicatedIds(out int finalClientCId, out int finalClientBId)
			&& finalClientBId == _myPeerId
			&& IsFullTopology(finalClientCId, finalClientBId, _myPeerId))
			_phase = 5;

		if (_phase == 5 && Exists("replacement-scored")
			&& TryReadReplacementDedicatedIds(out int scoredClientCId, out int scoredClientBId)
			&& scoredClientBId == _myPeerId
			&& _scoreChangedCount == 1
			&& _game.ScoreOf(scoredClientCId) == 1
			&& _game.ScoreOf(scoredClientBId) == 0
			&& _hud.Text == "You: 0   Opponent: 1")
		{
			Write("client-b-hud-scored", _hud.Text);
			_phase = 6;
		}

		if (_phase == 6 && Exists("client-c-hud-scored"))
			Pass("dedicated client B retained defense and its public HUD mapped the scored replacement offense peer");
	}

	private void TickDedicatedClientC()
	{
		if (_myPeerId <= 1) return;

		if (_phase == 0 && TryReadReplacementDedicatedIds(out int clientCId, out int clientBId)
			&& clientCId == _myPeerId
			&& IsFullTopology(clientCId, clientBId, _myPeerId))
		{
			Write("client-c-replacement-full", $"offense={clientCId} defense={clientBId}");
			_phase = 1;
		}

		if (_phase == 1 && Exists("server-replacement-full") && Exists("client-b-replacement-full")
			&& TryReadReplacementDedicatedIds(out int finalClientCId, out int finalClientBId)
			&& finalClientCId == _myPeerId
			&& IsFullTopology(finalClientCId, finalClientBId, _myPeerId))
			_phase = 2;

		if (_phase == 2 && Exists("replacement-scored")
			&& TryReadReplacementDedicatedIds(out int scoredClientCId, out int scoredClientBId)
			&& scoredClientCId == _myPeerId
			&& _scoreChangedCount == 1
			&& _game.ScoreOf(scoredClientCId) == 1
			&& _game.ScoreOf(scoredClientBId) == 0
			&& _hud.Text == "You: 1   Opponent: 0")
		{
			Write("client-c-hud-scored", _hud.Text);
			_phase = 3;
		}

		if (_phase == 3 && Exists("client-b-hud-scored"))
			Pass("dedicated client C reclaimed offense and its public HUD mapped the existing defender");
	}

	private void TickListen()
	{
		if (_role == "host") TickListenHost();
		else if (_role == "client") TickListenClient();
		else Fail($"unknown listen role '{_role}'.");
	}

	private void TickListenHost()
	{
		if (_phase == 0)
		{
			// Positive control for the dedicated negative: the same production
			// listener and wiring must successfully bind when this is a real client.
			if (!_browser.Discovery.IsListeningForHarness)
			{
				if (_elapsed > ListenerBindTimeoutSeconds)
					Fail($"production discovery listener did not bind control port {_discoveryPort}; dedicated negative would be vacuous.");
				return;
			}

			_network.HostGame(_port);
			_phase = 1;
			return;
		}

		if (_phase == 1 && _serverStarted && IsListenIntermediate())
		{
			Write("host-ready", "peer=1 spawn=host listener-control=bound");
			_phase = 2;
		}

		if (_phase == 2 && TryReadPeerId("client-joined", out int clientId)
			&& IsFullTopology(1, clientId, 1))
		{
			Write("host-full", $"host=1 client={clientId}");
			_phase = 3;
		}

		if (_phase == 3 && Exists("client-full")
			&& TryReadPeerId("client-joined", out int finalClientId)
			&& IsFullTopology(1, finalClientId, 1))
			Pass("listen-server host retained peer 1 and HUD mapped the replicated remote roster member");
	}

	private void TickListenClient()
	{
		if (_myPeerId <= 1 || !TryReadPeerId("client-joined", out int clientId)) return;
		if (_phase == 0 && clientId == _myPeerId && IsFullTopology(1, clientId, _myPeerId))
		{
			Write("client-full", $"host=1 client={clientId}");
			_phase = 1;
		}

		if (_phase == 1 && Exists("host-full")
			&& IsFullTopology(1, clientId, _myPeerId))
			Pass("listen-server client mapped peer 1 through the same replicated roster and HUD path");
	}

	private bool IsDedicatedIntermediate(int clientAId)
	{
		List<PlayerController> players = LivePlayers();
		return clientAId > 1
			&& players.Count == 1
			&& PlayerId(players[0]) == clientAId
			&& !_players.HasNode("1")
			&& NearSpawn(players[0].Position, _network.HostSpawn)
			&& _game.OpponentPeerIdFor(clientAId) == 0
			&& NoFalseScoreSignal
			&& HasRosterSignal
			&& DedicatedDiscoveryStayedOff;
	}

	private bool IsListenIntermediate()
	{
		List<PlayerController> players = LivePlayers();
		return players.Count == 1
			&& PlayerId(players[0]) == 1
			&& NearSpawn(players[0].Position, _network.HostSpawn)
			&& _game.OpponentPeerIdFor(1) == 0
			&& NoFalseScoreSignal
			&& HasRosterSignal
			&& DiscoveryListener.StartListeningCallCountForHarness > 0;
	}

	private bool IsFullTopology(int firstId, int secondId, int localPeerId)
	{
		if (firstId <= 0 || secondId <= 0 || firstId == secondId) return false;
		List<PlayerController> players = LivePlayers();
		if (players.Count != 2) return false;
		int[] actualIds = players.Select(PlayerId).OrderBy(id => id).ToArray();
		int[] expectedIds = new[] { firstId, secondId }.OrderBy(id => id).ToArray();
		if (!actualIds.SequenceEqual(expectedIds)) return false;
		if (_scenario == "dedicated" && actualIds.Contains(1)) return false;
		if (localPeerId > 0 && !actualIds.Contains(localPeerId)) return false;

		PlayerController first = _players.GetNodeOrNull<PlayerController>(firstId.ToString());
		PlayerController second = _players.GetNodeOrNull<PlayerController>(secondId.ToString());
		if (first == null || second == null) return false;

		// One conjunction is intentional: no role can accumulate individually
		// true facts from different topologies and report a manufactured pass.
		return NearSpawn(first.Position, _network.HostSpawn)
			&& NearSpawn(second.Position, _network.ClientSpawn)
			&& HorizontalDistance(first.Position, second.Position) >= DistinctSpawnDistance
			&& _game.OpponentPeerIdFor(firstId) == secondId
			&& _game.OpponentPeerIdFor(secondId) == firstId
			// 0-0 is a topology sanity check only. Existing scoring harnesses prove
			// authoritative mutation and replication; absence cannot prove delivery.
			&& _game.ScoreOf(firstId) == 0
			&& _game.ScoreOf(secondId) == 0
			&& NoFalseScoreSignal
			&& HasRosterSignal
			&& (_scenario != "dedicated" || DedicatedDiscoveryStayedOff);
	}

	private bool IsDisconnectedTopology(int remainingId, int departedId)
	{
		List<PlayerController> players = LivePlayers();
		return remainingId > 1 && departedId > 1 && remainingId != departedId
			&& players.Count == 1
			&& PlayerId(players[0]) == remainingId
			&& !_players.HasNode(departedId.ToString())
			&& _game.OpponentPeerIdFor(remainingId) == 0
			&& NoFalseScoreSignal
			&& HasRosterSignal
			&& DedicatedDiscoveryStayedOff;
	}

	private bool NoFalseScoreSignal => _scoreChangedCount == 0;
	private bool HasRosterSignal => _rosterChangedCount > 0;
	private bool DedicatedDiscoveryStayedOff =>
		_role != "server"
		|| (!_browser.Discovery.IsListeningForHarness
			&& !_dedicatedListenerEverActive
			&& DiscoveryListener.StartListeningCallCountForHarness == 0);

	private List<PlayerController> LivePlayers() => _players.GetChildren()
		.OfType<PlayerController>()
		.Where(player => !player.IsQueuedForDeletion())
		.ToList();

	private static int PlayerId(PlayerController player) =>
		int.TryParse(player.Name.ToString(), out int id) ? id : 0;

	// NetworkManager's spawn exports are ground-plane anchors. PlayerController's
	// CharacterBody settles vertically against the production court collider, so
	// role/spawn identity is the stable horizontal X/Z coordinate, not transient Y.
	private static bool NearSpawn(Vector3 actual, Vector3 expected) =>
		HorizontalDistance(actual, expected) <= PositionTolerance;

	private static float HorizontalDistance(Vector3 a, Vector3 b) =>
		new Vector2(a.X, a.Z).DistanceTo(new Vector2(b.X, b.Z));

	private void OnServerStarted(int actualPort)
	{
		if (actualPort != _port)
		{
			Fail($"production server bound port {actualPort}, expected {_port}.");
			return;
		}

		if (_scenario == "dedicated")
		{
			// ServerStarted is the production-ready barrier. Re-check every empty
			// topology fact here, after the deferred dedicated bootstrap completed.
			if (_players.GetChildCount() != 0 || _players.HasNode("1"))
			{
				Fail("dedicated production server created a phantom local player 1 before accepting clients.");
				return;
			}
			if (!DedicatedDiscoveryStayedOff)
			{
				Fail($"dedicated production ServerBrowser listened on client-only UDP port {_discoveryPort}.");
				return;
			}
			Write("server-ready", $"port={actualPort} players=0 discovery=off");
		}

		_serverStarted = true;
	}

	private void OnClientGameReady()
	{
		_myPeerId = Multiplayer.GetUniqueId();
		string marker = _scenario == "dedicated" ? $"{_role}-joined" : "client-joined";
		Write(marker, _myPeerId.ToString());
		GD.Print($"[dedicated-roster] {_role} joined production server as peer {_myPeerId}.");
	}

	private void OnClientConnectionFailed() => Fail("production NetworkManager.JoinGame failed");

	private bool TryReadInitialDedicatedIds(out int clientAId, out int clientBId)
	{
		clientAId = 0;
		clientBId = 0;
		return TryReadPeerId("client-a-joined", out clientAId)
			&& TryReadPeerId("client-b-joined", out clientBId)
			&& clientAId > 1 && clientBId > 1 && clientAId != clientBId;
	}

	private bool TryReadReplacementDedicatedIds(out int clientCId, out int clientBId)
	{
		clientCId = 0;
		clientBId = 0;
		if (!TryReadPeerId("client-c-joined", out clientCId)
			|| !TryReadPeerId("client-b-joined", out clientBId)
			|| !TryReadPeerId("client-a-joined", out int departedClientAId))
			return false;

		return clientCId > 1 && clientBId > 1
			&& clientCId != clientBId && clientCId != departedClientAId;
	}

	private bool TryReadPeerId(string name, out int peerId)
	{
		peerId = 0;
		string path = Path.Combine(_coordinationDir, name);
		if (!File.Exists(path)) return false;
		return int.TryParse(File.ReadAllText(path).Trim(), out peerId);
	}

	private static int ReadIntArg(string[] args, string name, int fallback) =>
		int.TryParse(HarnessArgs.ReadArg(args, name, fallback.ToString()), out int value) ? value : fallback;

	private string PlayerIdText() => string.Join(",", LivePlayers().Select(PlayerId).OrderBy(id => id));
	private bool Exists(string name) => File.Exists(Path.Combine(_coordinationDir, name));

	private void Write(string name, string contents)
	{
		string target = Path.Combine(_coordinationDir, name);
		string temporary = target + $".{System.Environment.ProcessId}.tmp";
		File.WriteAllText(temporary, contents);
		File.Move(temporary, target, true);
	}

	private void Pass(string detail)
	{
		if (_finished) return;
		GD.Print($"[dedicated-roster] {_scenario}/{_role} RESULT: PASS — {detail}");
		Finish(0);
	}

	private void Fail(string detail)
	{
		if (_finished) return;
		GD.PrintErr($"[dedicated-roster] {_scenario}/{_role} FAIL: {detail}");
		Finish(1);
	}

	private void Finish(int code)
	{
		_finished = true;
		GetTree().Quit(code);
	}
}
