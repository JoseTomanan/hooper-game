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
/// Four-process proof for #374. A production dedicated Main advertises through
/// DiscoveryBroadcaster, two production browsers activate the exact ItemList row
/// sequentially, and both clients drive the authoritative players through Input.
/// Coordination files are barriers only; all verdicts read production nodes.
/// </summary>
public partial class DedicatedDiscoveryJoinTest : Node
{
	private const double TimeoutSeconds = 55.0;
	private const double ListenerBindTimeoutSeconds = 2.0;
	private const double NegativeObservationSeconds = 1.0;
	private const double BaselineSeconds = 0.5;
	private const double InputSeconds = 0.6;
	private const double SettleSeconds = 0.5;
	private const float PositionTolerance = 0.08f;
	private const float BaselineDriftTolerance = 0.05f;
	private const float DistinctSpawnDistance = 0.5f;
	private const float MaterialMovement = 1.0f;

	private string _role = "server";
	private string _coordinationDir = ".godot/dedicated-discovery-join";
	private int _port = 23467;
	private int _discoveryPort = 27786;
	private double _elapsed;
	private double _phaseStarted;
	private int _phase;
	private bool _finished;
	private bool _serverStarted;
	private bool _dedicatedListenerEverActive;
	private bool _listenerWasBound;
	private bool _inputPressed;
	private bool _baselineStarted;
	private int _myPeerId;
	private int _negativeBroadcastBaseline;
	private int _rosterChangedCount;
	private double _settleStarted = -1.0;
	private Vector3 _firstPosition;
	private Vector3 _secondPosition;
	private Vector3 _movementFirstStart;
	private Vector3 _movementSecondStart;
	private Vector3 _localMovementStart;
	private float _initialSeparation;

	private Node3D _main;
	private NetworkManager _network;
	private ServerBrowser _browser;
	private DiscoveryBroadcaster _broadcaster;
	private GameManager _game;
	private Node _players;

	public override void _Ready()
	{
		string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
		_role = HarnessArgs.ReadArg(args, "--harness-role", "server");
		_port = ReadIntArg(args, "--harness-port", 23467);
		_discoveryPort = ReadIntArg(args, "--harness-discovery-port", 27786);
		_coordinationDir = HarnessArgs.ReadArg(args, "--harness-coordination-dir", ".godot/dedicated-discovery-join");
		Directory.CreateDirectory(_coordinationDir);

		// Configure the detached production tree before AddChild triggers child
		// _Ready callbacks, socket binding, and the deferred dedicated bootstrap.
		_main = ResourceLoader.Load<PackedScene>("res://scenes/Main.tscn").Instantiate<Node3D>();
		_main.Name = "ProductionMain";
		_network = _main.GetNode<NetworkManager>("NetworkManager");
		_browser = _main.GetNode<ServerBrowser>("ServerBrowser");
		_broadcaster = _main.GetNode<DiscoveryBroadcaster>("DiscoveryBroadcaster");
		_game = _main.GetNode<GameManager>("GameManager");
		_players = _main.GetNode("Players");
		_browser.Discovery.DiscoveryPort = IsNegativeRole ? _discoveryPort + 1 : _discoveryPort;
		_browser.RefreshInterval = 0.1f;
		_broadcaster.DiscoveryPort = _discoveryPort;
		_broadcaster.DestinationAddress = "127.0.0.1";
		_broadcaster.BroadcastInterval = 0.25f;
		_game.RosterChanged += () => _rosterChangedCount++;

		if (IsServerRole)
			_network.ServerStarted += OnServerStarted;
		else if (!IsNegativeRole)
		{
			_network.GameReady += OnClientGameReady;
			_network.ConnectionFailed += OnClientConnectionFailed;
		}

		AddChild(_main);
		GD.Print($"[dedicated-discovery] role={_role} gamePort={_port} discoveryPort={_browser.Discovery.DiscoveryPort}.");
	}

	public override void _PhysicsProcess(double delta)
	{
		if (_finished) return;
		_elapsed += delta;

		if (IsServerRole && _browser.Discovery.IsListeningForHarness)
			_dedicatedListenerEverActive = true;

		if (IsServerRole) TickServer();
		else if (IsNegativeRole) TickNegativeControl();
		else TickClient();

		if (!_finished && _elapsed > TimeoutSeconds)
			Fail($"timed out in phase {_phase}; peer={_myPeerId}, players={PlayerIdText()}, listener={_browser.Discovery.IsListeningForHarness}, broadcasts={_broadcaster.SuccessfulBroadcastCountForHarness}, backingRows={_browser.RowCountForHarness}, uiRows={_browser.ServerListUi.ItemCount}, positions={PositionText()}.");
	}

	private bool IsServerRole => _role == "server";
	private bool IsNegativeRole => _role == "negative";

	private void TickNegativeControl()
	{
		if (!_browser.Discovery.IsListeningForHarness)
		{
			if (!_listenerWasBound && _elapsed <= ListenerBindTimeoutSeconds) return;
			Fail($"negative listener was not continuously bound on port {_discoveryPort + 1}.");
			return;
		}

		AssertNegativeEmptyAndBound();
		if (_finished) return;

		if (_phase == 0)
		{
			_listenerWasBound = true;
			Write("negative-bound-start", $"port={_discoveryPort + 1}");
			AdvancePhase();
		}
		else if (_phase == 1 && _elapsed - _phaseStarted >= NegativeObservationSeconds)
		{
			Write("negative-minimum-elapsed", $"seconds={_elapsed - _phaseStarted:F3}");
			AdvancePhase();
		}
		else if (_phase == 2 && Exists("server-negative-broadcasts"))
		{
			AssertNegativeEmptyAndBound();
			if (_finished) return;
			Write("negative-pass", $"listener=bound backingRows=0 uiRows=0 seconds={_elapsed - _phaseStarted + NegativeObservationSeconds:F3}");
			Pass("mismatched production listener stayed bound and empty while the server sent at least two successful beacons");
		}
	}

	private void AssertNegativeEmptyAndBound()
	{
		_listenerWasBound = true;
		if (_browser.RowCountForHarness != 0 || _browser.ServerListUi.ItemCount != 0)
			Fail($"mismatched listener observed a row; backing={_browser.RowCountForHarness}, ui={_browser.ServerListUi.ItemCount}.");
	}

	private void TickServer()
	{
		if (!_serverStarted) return;

		if (_phase == 0 && Exists("negative-bound-start"))
		{
			_negativeBroadcastBaseline = _broadcaster.SuccessfulBroadcastCountForHarness;
			AdvancePhase();
		}
		else if (_phase == 1 && Exists("negative-minimum-elapsed")
			&& _broadcaster.SuccessfulBroadcastCountForHarness - _negativeBroadcastBaseline >= 2)
		{
			Write("server-negative-broadcasts", $"baseline={_negativeBroadcastBaseline} current={_broadcaster.SuccessfulBroadcastCountForHarness}");
			AdvancePhase();
		}
		else if (_phase == 2 && Exists("negative-pass"))
			AdvancePhase();
		else if (_phase == 3 && TryReadPeerId("client-a-joined", out int clientAId)
			&& IsDedicatedIntermediate(clientAId))
		{
			Write("server-a-ready", $"peer={clientAId}");
			AdvancePhase();
		}
		else if (_phase == 4 && TryReadClientIds(out int firstId, out int secondId)
			&& IsPreMovementTopology(firstId, secondId, 0))
		{
			Write("server-topology", $"a={firstId} b={secondId}");
			AdvancePhase();
		}
		else if (_phase == 5 && Exists("client-a-topology") && Exists("client-b-topology")
			&& TryReadClientIds(out int stableFirstId, out int stableSecondId)
			&& IsPreMovementTopology(stableFirstId, stableSecondId, 0))
		{
			PlayerController first = Player(stableFirstId);
			PlayerController second = Player(stableSecondId);
			if (!_baselineStarted)
			{
				_baselineStarted = true;
				_firstPosition = first.Position;
				_secondPosition = second.Position;
				_phaseStarted = _elapsed;
			}
			else if (HorizontalDistance(first.Position, _firstPosition) > BaselineDriftTolerance
				|| HorizontalDistance(second.Position, _secondPosition) > BaselineDriftTolerance)
			{
				_firstPosition = first.Position;
				_secondPosition = second.Position;
				_phaseStarted = _elapsed;
			}
			else if (_elapsed - _phaseStarted >= BaselineSeconds)
			{
				_movementFirstStart = first.Position;
				_movementSecondStart = second.Position;
				_initialSeparation = HorizontalDistance(first.Position, second.Position);
				Write("move-go", $"a={VectorText(_movementFirstStart)} b={VectorText(_movementSecondStart)} separation={_initialSeparation:F3}");
				AdvancePhase();
			}
		}
		else if (_phase == 6 && TryReadClientIds(out int movingFirstId, out int movingSecondId))
		{
			if (!IsStableTopology(movingFirstId, movingSecondId, 0))
			{
				Fail("authoritative topology changed during movement.");
				return;
			}

			if (Exists("client-a-released") && Exists("client-b-released") && _settleStarted < 0.0)
				_settleStarted = _elapsed;

			if (_settleStarted >= 0.0 && _elapsed - _settleStarted >= SettleSeconds
				&& Exists("client-a-final") && Exists("client-b-final"))
				AssertServerMovementAndPass(movingFirstId, movingSecondId);
		}
	}

	private void TickClient()
	{
		if (_phase == 0)
		{
			if (!_browser.Discovery.IsListeningForHarness)
			{
				if (_elapsed > ListenerBindTimeoutSeconds)
					Fail($"production listener did not bind correct discovery port {_discoveryPort}.");
				return;
			}
			_listenerWasBound = true;

			int row = _browser.FindRowForHarness("127.0.0.1", _port);
			if (row < 0)
			{
				if (_browser.RowCountForHarness > 1 || _browser.ServerListUi.ItemCount > 1)
					Fail($"browser accumulated ambiguous rows before exact endpoint appeared; backing={_browser.RowCountForHarness}, ui={_browser.ServerListUi.ItemCount}.");
				return;
			}
			if (_browser.RowCountForHarness != 1 || _browser.ServerListUi.ItemCount != 1)
			{
				Fail($"exact endpoint was not the sole activatable row; index={row}, backing={_browser.RowCountForHarness}, ui={_browser.ServerListUi.ItemCount}.");
				return;
			}

			_browser.ServerListUi.EmitSignal(ItemList.SignalName.ItemActivated, (long)row);
			if (_browser.Discovery.IsListeningForHarness)
			{
				Fail("activating the real ItemList row did not synchronously stop discovery.");
				return;
			}
			Write($"{_role}-activated", $"ip=127.0.0.1 port={_port} row={row} backing=1 ui=1");
			AdvancePhase();
		}
		else if (_phase == 1 && _myPeerId > 1 && TryReadClientIds(out int firstId, out int secondId)
			&& IsPreMovementTopology(firstId, secondId, _myPeerId))
		{
			if ((_role == "client-a" && firstId != _myPeerId) || (_role == "client-b" && secondId != _myPeerId))
			{
				Fail($"role-local identity mismatch: role={_role}, local={_myPeerId}, a={firstId}, b={secondId}.");
				return;
			}
			Write($"{_role}-topology", $"local={_myPeerId} a={firstId} b={secondId}");
			AdvancePhase();
		}
		else if (_phase == 2 && Exists("server-topology") && Exists("client-a-topology") && Exists("client-b-topology")
			&& TryReadClientIds(out int readyFirstId, out int readySecondId)
			&& IsPreMovementTopology(readyFirstId, readySecondId, _myPeerId))
			AdvancePhase();
		else if (_phase == 3 && Exists("move-go") && TryReadClientIds(out int movingFirstId, out int movingSecondId)
			&& IsStableTopology(movingFirstId, movingSecondId, _myPeerId))
		{
			_localMovementStart = Player(_myPeerId).Position;
			Input.ActionPress(_role == "client-a" ? "move_left" : "move_right", 1.0f);
			_inputPressed = true;
			AdvancePhase();
		}
		else if (_phase == 4 && TryReadClientIds(out int activeFirstId, out int activeSecondId))
		{
			if (!IsStableTopology(activeFirstId, activeSecondId, _myPeerId))
			{
				Fail("client topology changed while production movement input was held.");
				return;
			}
			if (_elapsed - _phaseStarted >= InputSeconds)
			{
				ReleaseInput();
				Write($"{_role}-released", $"peer={_myPeerId} position={VectorText(Player(_myPeerId).Position)}");
				AdvancePhase();
			}
		}
		else if (_phase == 5 && _elapsed - _phaseStarted >= SettleSeconds
			&& TryReadClientIds(out int finalFirstId, out int finalSecondId)
			&& IsStableTopology(finalFirstId, finalSecondId, _myPeerId))
		{
			Vector3 final = Player(_myPeerId).Position;
			float signedX = final.X - _localMovementStart.X;
			float horizontal = HorizontalDistance(final, _localMovementStart);
			bool directionOk = _role == "client-a" ? signedX <= -MaterialMovement : signedX >= MaterialMovement;
			if (!directionOk || horizontal < MaterialMovement)
			{
				Fail($"local production movement was not material in the intended direction; start={VectorText(_localMovementStart)}, final={VectorText(final)}, deltaX={signedX:F3}, horizontal={horizontal:F3}.");
				return;
			}
			Write($"{_role}-final", $"peer={_myPeerId} start={VectorText(_localMovementStart)} final={VectorText(final)} deltaX={signedX:F3} horizontal={horizontal:F3}");
			// Remain connected until the server consumes both client proofs and
			// evaluates the still-live authoritative topology. Exiting here races
			// peer-disconnect processing against the server's final conjunction.
			AdvancePhase();
		}
		else if (_phase == 6 && Exists("server-final"))
			Pass("exact browser activation, role-local topology, and production input movement co-occurred");
	}

	private void AssertServerMovementAndPass(int firstId, int secondId)
	{
		Vector3 first = Player(firstId).Position;
		Vector3 second = Player(secondId).Position;
		float firstX = first.X - _movementFirstStart.X;
		float secondX = second.X - _movementSecondStart.X;
		float firstDistance = HorizontalDistance(first, _movementFirstStart);
		float secondDistance = HorizontalDistance(second, _movementSecondStart);
		float separationGain = HorizontalDistance(first, second) - _initialSeparation;
		if (firstX > -MaterialMovement || secondX < MaterialMovement
			|| firstDistance < MaterialMovement || secondDistance < MaterialMovement
			|| separationGain < MaterialMovement)
		{
			Fail($"authoritative movement insufficient; aDeltaX={firstX:F3}, bDeltaX={secondX:F3}, aDistance={firstDistance:F3}, bDistance={secondDistance:F3}, separationGain={separationGain:F3}.");
			return;
		}

		Write("server-final", $"aDeltaX={firstX:F3} bDeltaX={secondX:F3} aDistance={firstDistance:F3} bDistance={secondDistance:F3} separationGain={separationGain:F3}");
		Pass("server co-observed both client proofs, exact dedicated topology, and material authoritative separation");
	}

	private bool IsDedicatedIntermediate(int firstId)
	{
		List<PlayerController> players = LivePlayers();
		return firstId > 1 && players.Count == 1 && PlayerId(players[0]) == firstId
			&& !_players.HasNode("1") && NearSpawn(players[0].Position, _network.HostSpawn)
			&& _game.OpponentPeerIdFor(firstId) == 0 && DedicatedDiscoveryStayedOff;
	}

	private bool IsPreMovementTopology(int firstId, int secondId, int localPeerId)
	{
		if (!IsStableTopology(firstId, secondId, localPeerId)) return false;
		return NearSpawn(Player(firstId).Position, _network.HostSpawn)
			&& NearSpawn(Player(secondId).Position, _network.ClientSpawn)
			&& HorizontalDistance(Player(firstId).Position, Player(secondId).Position) >= DistinctSpawnDistance;
	}

	private bool IsStableTopology(int firstId, int secondId, int localPeerId)
	{
		if (firstId <= 1 || secondId <= 1 || firstId == secondId) return false;
		List<PlayerController> players = LivePlayers();
		int[] actual = players.Select(PlayerId).OrderBy(id => id).ToArray();
		int[] expected = new[] { firstId, secondId }.OrderBy(id => id).ToArray();
		return players.Count == 2 && actual.SequenceEqual(expected) && !actual.Contains(1)
			&& (localPeerId == 0 || actual.Contains(localPeerId))
			&& _game.OpponentPeerIdFor(firstId) == secondId
			&& _game.OpponentPeerIdFor(secondId) == firstId
			&& _rosterChangedCount > 0
			&& (!IsServerRole || DedicatedDiscoveryStayedOff);
	}

	private bool DedicatedDiscoveryStayedOff => !_browser.Discovery.IsListeningForHarness
		&& !_dedicatedListenerEverActive && DiscoveryListener.StartListeningCallCountForHarness == 0;

	private void OnServerStarted(int actualPort)
	{
		if (actualPort != _port || _players.GetChildCount() != 0 || _players.HasNode("1") || !DedicatedDiscoveryStayedOff)
		{
			Fail($"dedicated bootstrap invariant failed; actualPort={actualPort}, expectedPort={_port}, players={PlayerIdText()}, listener={_browser.Discovery.IsListeningForHarness}.");
			return;
		}
		_serverStarted = true;
		Write("server-ready", $"port={actualPort} players=0 discovery=off");
	}

	private void OnClientGameReady()
	{
		_myPeerId = Multiplayer.GetUniqueId();
		if (_browser.Discovery.IsListeningForHarness)
		{
			Fail("client GameReady arrived while discovery listener was still active.");
			return;
		}
		Write($"{_role}-joined", _myPeerId.ToString());
	}

	private void OnClientConnectionFailed() => Fail("browser-selected production connection failed");

	private bool TryReadClientIds(out int firstId, out int secondId)
	{
		firstId = 0;
		secondId = 0;
		return TryReadPeerId("client-a-joined", out firstId)
			&& TryReadPeerId("client-b-joined", out secondId)
			&& firstId > 1 && secondId > 1 && firstId != secondId;
	}

	private bool TryReadPeerId(string name, out int peerId)
	{
		peerId = 0;
		string path = Path.Combine(_coordinationDir, name);
		return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out peerId);
	}

	private List<PlayerController> LivePlayers() => _players.GetChildren().OfType<PlayerController>()
		.Where(player => !player.IsQueuedForDeletion()).ToList();
	private PlayerController Player(int id) => _players.GetNodeOrNull<PlayerController>(id.ToString());
	private static int PlayerId(PlayerController player) => int.TryParse(player.Name.ToString(), out int id) ? id : 0;
	private static bool NearSpawn(Vector3 actual, Vector3 expected) => HorizontalDistance(actual, expected) <= PositionTolerance;
	private static float HorizontalDistance(Vector3 a, Vector3 b) => new Vector2(a.X, a.Z).DistanceTo(new Vector2(b.X, b.Z));
	private static int ReadIntArg(string[] args, string name, int fallback) => int.TryParse(HarnessArgs.ReadArg(args, name, fallback.ToString()), out int value) ? value : fallback;
	private bool Exists(string name) => File.Exists(Path.Combine(_coordinationDir, name));
	private string PlayerIdText() => string.Join(",", LivePlayers().Select(PlayerId).OrderBy(id => id));
	private string PositionText() => string.Join(";", LivePlayers().OrderBy(PlayerId).Select(p => $"{PlayerId(p)}={VectorText(p.Position)}"));
	private static string VectorText(Vector3 value) => $"({value.X:F3},{value.Y:F3},{value.Z:F3})";

	private void AdvancePhase()
	{
		_phase++;
		_phaseStarted = _elapsed;
	}

	private void Write(string name, string contents)
	{
		string target = Path.Combine(_coordinationDir, name);
		string temporary = target + $".{System.Environment.ProcessId}.tmp";
		File.WriteAllText(temporary, contents);
		File.Move(temporary, target, true);
	}

	private void ReleaseInput()
	{
		if (!_inputPressed) return;
		Input.ActionRelease(_role == "client-a" ? "move_left" : "move_right");
		_inputPressed = false;
	}

	private void Pass(string detail)
	{
		if (_finished) return;
		ReleaseInput();
		GD.Print($"[dedicated-discovery] {_role} RESULT: PASS — {detail}");
		Finish(0);
	}

	private void Fail(string detail)
	{
		if (_finished) return;
		ReleaseInput();
		GD.PrintErr($"[dedicated-discovery] {_role} RESULT: FAIL — {detail}");
		Finish(1);
	}

	private void Finish(int exitCode)
	{
		_finished = true;
		GetTree().Quit(exitCode);
	}
}
