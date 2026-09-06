using System;
using System.IO;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Networking;
using Hooper.Player;
using Hooper.Systems;

namespace HOOPERGAME.Tests.Integration;

/// <summary>
/// Three-process #375 journey. Every role instances the shipped Main.tscn;
/// the server enters through the real --dedicated bootstrap and clients join
/// by activating the exact expected row in the production ServerBrowser.
/// Harness-only socket configuration is applied while Main is detached, before
/// child _Ready callbacks bind discovery sockets (Godot Node lifecycle).
/// </summary>
public partial class DedicatedGameJourneyTest : Node
{
	private const double TimeoutSeconds = 75.0;
	private const double DiscoveryNegativeSeconds = 4.0;
	private const double DefenderRunSeconds = 0.75;
	private const double ShooterRunSeconds = 0.55;
	private const double MutationObservationSeconds = 0.6;
	private const float MinDisplacement = 1.0f;
	private const float MinOpenSeparation = 3.0f;

	private string _role = "server";
	private string _scenario = "healthy";
	private int _port = 23462;
	private int _discoveryPort = 27780;
	private string _coordinationDir;
	private double _elapsed;
	private bool _finished;

	private Node3D _main;
	private NetworkManager _network;
	private DiscoveryBroadcaster _broadcaster;
	private ServerBrowser _browser;
	private GameManager _game;
	private BallController _ball;
	private Node _players;

	private int _myPeerId;
	private int _shooterPeerId;
	private int _defenderPeerId;
	private int _phase;
	private double _phaseAt;
	private Vector3 _movementStart;
	private bool _baselineWritten;
	private bool _playReadyWritten;
	private bool _serverDefenderControlled;
	private bool _serverShooterControlled;
	private Vector3 _serverDefenderStart;
	private Vector3 _serverShooterStart;
	private bool _shootPressed;
	private ulong _shootReleaseFrame;
	private int _shotsRequested;
	private bool _sawMutationInFlight;

	private int _serverScore;
	private bool _sawFlightSinceScore;
	private int _awaitingAwardForScore;
	private int _serverPossessionChanges;
	private int _possessionCountAtWinningScore = -1;
	private bool _terminalProofComplete;
	private double _terminalScoreAt;

	public override void _Ready()
	{
		string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
		_role = HarnessArgs.ReadArg(args, "--harness-role", "server");
		_scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "healthy");
		_port = ReadIntArg(args, "--harness-port", 23462);
		_discoveryPort = ReadIntArg(args, "--harness-discovery-port", 27780);
		_coordinationDir = HarnessArgs.ReadArg(args, "--harness-coordination-dir", ".godot/dedicated-game");
		Directory.CreateDirectory(_coordinationDir);

		// Configure the detached production tree before AddChild triggers reverse
		// child-first _Ready. This is the only reliable point before UDP bind/start.
		_main = ResourceLoader.Load<PackedScene>("res://scenes/Main.tscn").Instantiate<Node3D>();
		_network = _main.GetNode<NetworkManager>("NetworkManager");
		_broadcaster = _main.GetNode<DiscoveryBroadcaster>("DiscoveryBroadcaster");
		_browser = _main.GetNode<ServerBrowser>("ServerBrowser");
		_game = _main.GetNode<GameManager>("GameManager");
		_ball = _main.GetNode<BallController>("Ball");
		_players = _main.GetNode("Players");

		_broadcaster.DestinationAddress = "127.0.0.1";
		_broadcaster.DiscoveryPort = _discoveryPort;
		_browser.Discovery.DiscoveryPort = _scenario == "discovery-disabled"
			? _discoveryPort + 1
			: _discoveryPort;

		if (_role == "server")
		{
			_network.ServerStarted += OnServerStarted;
			_ball.PossessionChanged += OnServerPossessionChanged;
			_game.ScoreChanged += OnServerScoreChanged;
		}
		else
		{
			_network.GameReady += OnClientGameReady;
			_network.ConnectionFailed += OnClientConnectionFailed;
		}

		AddChild(_main);
		if (_game.TargetScore != 5)
			Fail($"shipped Main.tscn TargetScore drifted to {_game.TargetScore}; #375 requires the production five-point journey.");
		GD.Print($"[dedicated-game] role={_role} scenario={_scenario} main=Main.tscn gamePort={_port} discoveryPort={_browser.Discovery.DiscoveryPort}.");
	}

	public override void _PhysicsProcess(double delta)
	{
		if (_finished) return;
		_elapsed += delta;

		if (_role == "server") TickServer();
		else TickClient();

		if (!_finished && _elapsed > TimeoutSeconds)
			Fail($"timed out in phase {_phase}; role={_role}, scenario={_scenario}, peer={_myPeerId}, score={ScoreText()}, holder={_ball.StateMachine.HolderPeerId}.");
	}

	private void TickServer()
	{
		if (!Exists("server-ready")) return;
		if (_scenario == "discovery-disabled")
		{
			if (Exists("client-a-pass"))
				Pass("discovery-disabled client completed its bound-listener negative control");
			return;
		}

		if (_ball.State == BallState.InFlight)
			_sawFlightSinceScore = true;

		if (!_playReadyWritten && Exists("client-a-baseline") && Exists("client-b-baseline"))
		{
			_shooterPeerId = ReadPeerId("client-a-joined");
			_defenderPeerId = ReadPeerId("client-b-joined");
			if (_shooterPeerId <= 1 || _defenderPeerId <= 1 || _shooterPeerId == _defenderPeerId
				|| _game.OpponentPeerIdFor(_shooterPeerId) != _defenderPeerId)
			{
				Fail($"invalid dedicated roster: shooter={_shooterPeerId}, defender={_defenderPeerId}, opponent={_game.OpponentPeerIdFor(_shooterPeerId)}.");
				return;
			}

			if (_scenario == "score-rpc-disabled")
			{
				_game.SuppressScoreRpcForHarness = true;
				GD.Print("[dedicated-game] score RPC suppression armed after both clients confirmed a healthy 0-0 roster mirror.");
			}

			_playReadyWritten = true;
			_serverShooterStart = _players.GetNode<PlayerController>(_shooterPeerId.ToString()).GlobalPosition;
			_serverDefenderStart = _players.GetNode<PlayerController>(_defenderPeerId.ToString()).GlobalPosition;
			Write("play-ready", $"shooter={_shooterPeerId} defender={_defenderPeerId}");
		}
		if (_playReadyWritten)
			ObserveAuthoritativeControls();

		if (_scenario == "score-rpc-disabled")
		{
			if (_serverScore >= 1)
				Write("server-scored", $"shooter={_shooterPeerId} score={_serverScore}");
			if (_serverScore >= 1 && Exists("client-a-stale") && Exists("client-b-stale"))
			{
				Write("server-release", $"score advanced to {_serverScore}; both clients stayed at baseline while ball state continued replicating");
				if (Exists("client-a-pass") && Exists("client-b-pass"))
					Pass("score-RPC mutation discriminated after healthy baseline and unrelated authoritative ball updates");
			}
			return;
		}

		if (_possessionCountAtWinningScore < 0) return;
		if (!Enumerable.Range(1, 5).All(score => Exists($"shot-request-{score}"))
			|| !Enumerable.Range(1, 4).All(score => Exists($"cycle-{score}-ready")))
		{
			Fail("terminal state lacked all five real-input requests or four nonterminal cleared-reset receipts.");
			return;
		}
		if (_serverPossessionChanges != _possessionCountAtWinningScore)
		{
			Fail("terminal basket emitted a possession transition after the winning ScoreChanged event.");
			return;
		}
		if (_elapsed - _terminalScoreAt < MutationObservationSeconds) return;
		if (!Exists("client-a-terminal") || !Exists("client-b-terminal")) return;

		// Client disconnect cleanup can legitimately change possession after the
		// proof interval. Close the observation window before releasing clients.
		_terminalProofComplete = true;
		Write("server-release", $"winner={_game.WinnerPeerId} score={_serverScore} possessionEvents={_serverPossessionChanges}");
		if (Exists("client-a-pass") && Exists("client-b-pass"))
			Pass("full authoritative game, five ordered score cycles, terminal agreement, and no terminal award verified");
	}

	private void TickClient()
	{
		if (_scenario == "discovery-disabled")
		{
			TickDiscoveryNegative();
			return;
		}

		// The server releases clients only after verifying their receipts, and
		// stays online until both have written PASS. Complete that barrier before
		// touching production state again.
		string receipt = _scenario == "healthy" ? $"{_role}-terminal" : $"{_role}-stale";
		if (Exists(receipt) && Exists("server-release"))
		{
			Pass(_scenario == "healthy"
				? $"{_role} agreed with authoritative terminal state"
				: $"{_role} stayed score-stale while authoritative ball state continued updating");
			return;
		}

		if (_myPeerId == 0)
		{
			TryActivateExpectedBrowserRow();
			return;
		}

		PlayerController own = _players.GetNodeOrNull<PlayerController>(_myPeerId.ToString());
		if (own == null) return;

		int opponentId = _game.OpponentPeerIdFor(_myPeerId);
		if (!_baselineWritten && opponentId != 0
			&& _players.GetChildren().OfType<PlayerController>().Count() == 2
			&& _game.ScoreOf(_myPeerId) == 0 && _game.ScoreOf(opponentId) == 0)
		{
			_baselineWritten = true;
			Write($"{_role}-baseline", $"peer={_myPeerId} opponent={opponentId} score=0-0");
			GD.Print($"[dedicated-game] {_role} confirmed both production players and healthy roster/score RPC baseline.");
		}
		if (!_baselineWritten || !Exists("play-ready")) return;

		_shooterPeerId = ReadPeerId("client-a-joined");
		_defenderPeerId = ReadPeerId("client-b-joined");
		if (_scenario == "score-rpc-disabled" && _ball.State == BallState.InFlight)
			_sawMutationInFlight = true;
		if (_scenario == "score-rpc-disabled" && Exists("server-scored"))
		{
			TickScoreMutationClient();
			return;
		}

		if (_role == "client-b") TickDefender(own);
		else TickShooter(own);
	}

	private void TickDiscoveryNegative()
	{
		if (!_browser.Discovery.IsListeningForHarness)
		{
			if (_elapsed > 1.0) Fail($"mismatched discovery listener failed to bind port {_discoveryPort + 1}; no-row result would be vacuous.");
			return;
		}
		if (_browser.DiscoveredExpectedPortForHarness(_port))
		{
			Fail("discovery-disabled mutation unexpectedly produced the target server row.");
			return;
		}
		if (_elapsed >= DiscoveryNegativeSeconds)
			Pass($"listener bound {_discoveryPort + 1} but target beacon on {_discoveryPort} produced no browser row");
	}

	private void TryActivateExpectedBrowserRow()
	{
		if (_phase != 0) return; // one browser action while the handshake is pending
		var servers = _browser.Discovery.DiscoveredServers;
		for (int i = 0; i < servers.Count; i++)
		{
			ServerListEntry entry = servers[i];
			if (entry.GamePort != _port || entry.Ip != "127.0.0.1") continue;
			if (_browser.ServerListUi.ItemCount != servers.Count) return;

			GD.Print($"[dedicated-game] {_role} activating exact browser row {i}: {entry.Ip}:{entry.GamePort}.");
			_browser.ServerListUi.EmitSignal(ItemList.SignalName.ItemActivated, i);
			_phase = 1;
			return;
		}
	}

	private void TickDefender(PlayerController own)
	{
		if (_phase == 1)
		{
			_movementStart = own.GlobalPosition;
			Input.ActionPress("move_right");
			_phase = 2;
			_phaseAt = _elapsed;
			return;
		}
		if (_phase == 2 && _elapsed - _phaseAt >= DefenderRunSeconds)
		{
			Input.ActionRelease("move_right");
			float moved = own.GlobalPosition.DistanceTo(_movementStart);
			if (moved < MinDisplacement)
			{
				Fail($"defender production input was not controllable: moved {moved:F2}m.");
				return;
			}
			Write("defender-cleared", $"peer={_myPeerId} moved={moved:F2}");
			_phase = 3;
		}

		if (_scenario == "healthy" && _game.IsGameOver)
			FinishClientTerminal("client-b");
	}

	private void TickShooter(PlayerController own)
	{
		if (_phase == 1 && Exists("defender-cleared"))
		{
			_movementStart = own.GlobalPosition;
			Input.ActionPress("move_forward");
			_phase = 2;
			_phaseAt = _elapsed;
			return;
		}
		if (_phase == 2 && _elapsed - _phaseAt >= ShooterRunSeconds)
		{
			Input.ActionRelease("move_forward");
			float moved = own.GlobalPosition.DistanceTo(_movementStart);
			PlayerController opponent = _players.GetNodeOrNull<PlayerController>(_defenderPeerId.ToString());
			float separation = opponent == null ? 0f : own.GlobalPosition.DistanceTo(opponent.GlobalPosition);
			if (moved < MinDisplacement || separation < MinOpenSeparation)
			{
				Fail($"open-lane setup failed: shooterMoved={moved:F2}m separation={separation:F2}m.");
				return;
			}
			Write("shooter-positioned", $"peer={_myPeerId} moved={moved:F2} separation={separation:F2}");
			_phase = 3;
		}

		if (_scenario == "healthy" && _game.IsGameOver)
		{
			FinishClientTerminal("client-a");
			return;
		}

		int nextScore = _shotsRequested + 1;
		bool priorCycleReady = nextScore == 1 || Exists($"cycle-{nextScore - 1}-ready");
		if (_phase == 3 && Exists("server-controls-ready") && priorCycleReady && !_game.IsGameOver
			&& _ball.State != BallState.InFlight
			&& _ball.StateMachine.HolderPeerId == _myPeerId
			&& _ball.IsCleared && own.DisplayMove().phase == MovePhase.Inactive
			&& !_shootPressed)
		{
			_shotsRequested = nextScore;
			Write($"shot-request-{nextScore}", $"peer={_myPeerId} scoreBefore={_game.ScoreOf(_myPeerId)}");
			Input.ActionPress("ball_shoot");
			_shootPressed = true;
			_shootReleaseFrame = Engine.GetPhysicsFrames() + 1;
		}
		if (_shootPressed && Engine.GetPhysicsFrames() >= _shootReleaseFrame)
		{
			Input.ActionRelease("ball_shoot");
			_shootPressed = false;
		}
	}

	private void TickScoreMutationClient()
	{
		if (_game.ScoreOf(_shooterPeerId) != 0)
		{
			Fail($"score RPC mutation leaked authoritative score {_game.ScoreOf(_shooterPeerId)} to {_role}.");
			return;
		}

		// Make-it-take-it is server-only. Seeing its cleared holder while the
		// score stays stale proves the unrelated Ball.ReceiveState stream is alive;
		// a disconnected client cannot satisfy this mutation control.
		if (!_sawMutationInFlight || _ball.StateMachine.HolderPeerId != _shooterPeerId || !_ball.IsCleared) return;
		if (_phaseAt == 0) _phaseAt = _elapsed;
		if (_elapsed - _phaseAt < MutationObservationSeconds) return;

		if (!Exists($"{_role}-stale"))
			Write($"{_role}-stale", $"score=0 ballHolder={_ball.StateMachine.HolderPeerId} cleared={_ball.IsCleared}");
		if (Exists("server-release"))
			Pass($"{_role} stayed score-stale while authoritative ball state continued updating");
	}

	private void OnServerStarted(int actualPort)
	{
		if (actualPort != _port) Fail($"dedicated bootstrap bound {actualPort}, expected {_port}.");
		else Write("server-ready", $"port={actualPort} destination={_broadcaster.DestinationAddress} discoveryPort={_broadcaster.DiscoveryPort}");
	}

	private void ObserveAuthoritativeControls()
	{
		PlayerController shooter = _players.GetNodeOrNull<PlayerController>(_shooterPeerId.ToString());
		PlayerController defender = _players.GetNodeOrNull<PlayerController>(_defenderPeerId.ToString());
		if (shooter == null || defender == null) return;

		if (!_serverDefenderControlled && Exists("defender-cleared")
			&& defender.GlobalPosition.DistanceTo(_serverDefenderStart) >= MinDisplacement)
			_serverDefenderControlled = true;

		if (!_serverShooterControlled && Exists("shooter-positioned")
			&& shooter.GlobalPosition.DistanceTo(_serverShooterStart) >= MinDisplacement
			&& shooter.GlobalPosition.DistanceTo(defender.GlobalPosition) >= MinOpenSeparation)
			_serverShooterControlled = true;

		if (_serverDefenderControlled && _serverShooterControlled && !Exists("server-controls-ready"))
			Write("server-controls-ready", $"shooter={shooter.GlobalPosition} defender={defender.GlobalPosition}");
	}

	private void OnServerScoreChanged()
	{
		if (_shooterPeerId == 0) return; // roster-only 0-0 broadcasts
		int score = _game.ScoreOf(_shooterPeerId);
		if (score == _serverScore) return; // roster refresh, not a basket
		if (score != _serverScore + 1)
		{
			Fail($"authoritative score jumped {_serverScore} -> {score}; expected exactly one point per input cycle.");
			return;
		}
		if (!Exists($"shot-request-{score}") || !_sawFlightSinceScore)
		{
			Fail($"score {score} lacked its matching real-input request and observed InFlight transition.");
			return;
		}

		_serverScore = score;
		_sawFlightSinceScore = false;
		GD.Print($"[dedicated-game] authoritative score advanced exactly once to {_serverScore}/{_game.TargetScore}.");

		if (_scenario == "score-rpc-disabled") return;
		if (_serverScore < _game.TargetScore)
		{
			_awaitingAwardForScore = _serverScore;
			return;
		}

		if (!_game.IsGameOver || _game.WinnerPeerId != _shooterPeerId)
		{
			Fail($"target score reached without matching terminal state: winner={_game.WinnerPeerId} gameOver={_game.IsGameOver}.");
			return;
		}
		_possessionCountAtWinningScore = _serverPossessionChanges;
		_terminalScoreAt = _elapsed;
	}

	private void OnServerPossessionChanged(int holderPeerId, bool cleared)
	{
		if (_terminalProofComplete) return;
		_serverPossessionChanges++;
		if (_possessionCountAtWinningScore >= 0)
		{
			Fail($"post-terminal possession event #{_serverPossessionChanges}: holder={holderPeerId} cleared={cleared}.");
			return;
		}

		if (_awaitingAwardForScore == 0) return;
		if (holderPeerId != _shooterPeerId || !cleared || _ball.HasDribbled
			|| _ball.State != BallState.Held)
		{
			Fail($"score {_awaitingAwardForScore} reset was wrong: holder={holderPeerId}, cleared={cleared}, hasDribbled={_ball.HasDribbled}, state={_ball.State}.");
			return;
		}

		Write($"cycle-{_awaitingAwardForScore}-ready", $"holder={holderPeerId} cleared=true hasDribbled=false");
		_awaitingAwardForScore = 0;
	}

	private void OnClientGameReady()
	{
		_myPeerId = Multiplayer.GetUniqueId();
		Write($"{_role}-joined", $"peer={_myPeerId}");
		GD.Print($"[dedicated-game] {_role} joined through ServerBrowser as peer {_myPeerId}.");
	}

	private void OnClientConnectionFailed() => Fail("client connection failed after browser activation");

	private void FinishClientTerminal(string name)
	{
		if (!_game.IsGameOver || _game.WinnerPeerId != _shooterPeerId
			|| _game.ScoreOf(_shooterPeerId) != 5 || _game.ScoreOf(_defenderPeerId) != 0
			|| _ball.StateMachine.HolderPeerId != 0)
		{
			Fail($"{name} terminal mirror disagreed: gameOver={_game.IsGameOver}, winner={_game.WinnerPeerId}, score={_game.ScoreOf(_shooterPeerId)}-{_game.ScoreOf(_defenderPeerId)}, holder={_ball.StateMachine.HolderPeerId}.");
			return;
		}
		if (!Exists($"{name}-terminal"))
			Write($"{name}-terminal", $"winner={_game.WinnerPeerId} score={_game.ScoreOf(_shooterPeerId)}");
		if (Exists("server-release"))
			Pass($"{name} agreed with authoritative terminal state");
	}

	private static int ReadIntArg(string[] args, string name, int fallback) =>
		int.TryParse(HarnessArgs.ReadArg(args, name, fallback.ToString()), out int value) ? value : fallback;

	private int ReadPeerId(string name)
	{
		string value = File.ReadAllText(Path.Combine(_coordinationDir, name)).Trim();
		return value.StartsWith("peer=") && int.TryParse(value[5..], out int id) ? id : 0;
	}

	private string ScoreText() => _shooterPeerId == 0 ? "unassigned" : $"{_game.ScoreOf(_shooterPeerId)}-{_game.ScoreOf(_defenderPeerId)}";
	private bool Exists(string name) => File.Exists(Path.Combine(_coordinationDir, name));
	private void Write(string name, string contents) => File.WriteAllText(Path.Combine(_coordinationDir, name), contents);
	private void Pass(string detail) { if (_finished) return; Write($"{_role}-pass", detail); GD.Print($"[dedicated-game] {_role} RESULT: PASS — {detail}"); Finish(0); }
	private void Fail(string detail) { if (_finished) return; GD.PrintErr($"[dedicated-game] {_role} FAIL: {detail}"); Finish(1); }
	private void Finish(int code)
	{
		_finished = true;
		Input.ActionRelease("move_forward");
		Input.ActionRelease("move_right");
		Input.ActionRelease("ball_shoot");
		GetTree().Quit(code);
	}
}
