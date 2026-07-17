using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Networking;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Dual-instance network harness — issue #102 (ADR-0016, ADR-0018, ADR-0003):
// prove the offense's remote-display fix (#69, DisplayPhaseResolver) also
// covers a DEFENSIVE committed move, and that the whiff-punish "beaten" cue
// (#100's blow-by lane) is visible on a genuine remote client, not just
// locally on the server that judged it.
//
// What the single-instance harnesses (StealTurnoverTest, BlowByWindowTest)
// structurally cannot reach: with no MultiplayerPeer assigned, Godot uses
// OfflineMultiplayerPeer (Multiplayer.IsServer() hardcoded true), so
// DisplayPhaseResolver.LocalMachineDrivesDisplay and BeatenDisplayResolver.
// LocalStateIsAuthoritative are BOTH true for every node — the one role that
// reads the BROADCAST instead of local truth (the client's copy of a remote
// player) only exists in a second process. Proving the defensive telegraph
// AND the beaten cue actually render off broadcast state needs two real
// ENet peers.
//
// Topology: SERVER boots via NetworkManager.HostGame (listen-server) so it
// owns player "1" — the node that will commit the StealMove and later go
// Beaten. The CLIENT's copy of player "1" is then !IsServer && !IsLocalPlayer
// — DisplayPhaseResolver's and BeatenDisplayResolver's lone broadcast-reading
// role for BOTH predicates at once.
//
// Flow ("telegraph" scenario): server hosts → client joins → server begins a
// REAL StealMove on its own player "1" via BeginMoveForHarness (the same
// BeginCommittedMove choke point production input reaches) → the ball is
// never dribbled (BallStateMachine defaults to Held, and ResolveStealAttempts
// only fires while Dribbling — see its own doc), so the steal is GUARANTEED
// to whiff naturally (never resolves early via EndActiveEarly) → Active
// expires into Recovery → BallController.ResolveBeatenWindowTriggers grants
// the blow-by window server-side → both facts (phase arc + beaten flag) ride
// the server's per-tick ReceiveState broadcast → the client's REMOTE copy of
// player "1" must show Startup, then Active, then Recovery via
// DisplayMove(), and DisplayBeaten() must go true, with NO local machine on
// the client ever having run the move (Multiplayer.IsServer() == false is
// asserted throughout).
//
// Flow ("control" scenario): identical topology, but the server NEVER begins
// a move on player "1". The client must never observe Startup/Active/
// Recovery or DisplayBeaten()==true on the remote node for the whole run —
// the counterfactual proving "telegraph"'s positive observations are a real
// effect of the committed move, not a harness that always reads true
// (the same anti-vacuous-pass discipline BlockTurnoverTest's control-make and
// ContestScatterTest's no-contest controls already follow).
//
//   godot --headless --path . res://tests/integration/NetDefensiveTelegraphTest.tscn -- --harness-role=server --harness-port=PORT --harness-scenario=telegraph
//   godot --headless --path . res://tests/integration/NetDefensiveTelegraphTest.tscn -- --harness-role=client --harness-port=PORT --harness-scenario=telegraph
//
// Exit: 0 = the CLIENT observed the expected sequence for its scenario (see
// per-scenario Verdict), on a process where Multiplayer.IsServer() is false.
// 1 = failed/timed out. Orchestrated by run-net-defensive-telegraph.sh, which
// reads the CLIENT's exit code as the verdict for each scenario.
public partial class NetDefensiveTelegraphTest : Node3D
{
	private const double ServerTimeoutSeconds = 60.0;
	private const double ClientTimeoutSeconds = 45.0;

	private const int SettleFrames = 60;
	// StealMove default frame data is 8/8/20 (Startup/Active/Recovery) — give
	// the client comfortable margin past Recovery's natural end (36 ticks
	// total from begin) to observe the beaten window opening.
	private const int ControlObserveFrames = 90;

	private string _role = "server";
	private string _scenario = "telegraph";
	private int _port = 23460;
	private int _frame;
	private double _elapsed;
	private bool _finished;

	private NetworkManager _net;
	private Node _players;
	private BallController _ball;

	// ── Server state ─────────────────────────────────────────────────────────
	private enum ServerStep { AwaitClient, Settle, Begin, Linger }
	private ServerStep _serverStep = ServerStep.AwaitClient;
	private int _stepDeadlineFrame;
	private bool _moveBegun;

	// ── Client state ─────────────────────────────────────────────────────────
	private int _myPeerId = -1;
	private bool _observing;
	private int _controlFramesObserved;

	// Pass evidence for "telegraph": the phase arc must be seen IN ORDER
	// (Startup before Active before Recovery — not just "each seen at some
	// point," which could pass on a re-triggered move) and the beaten flag
	// must be seen true at least once AFTER Recovery was first observed.
	private bool _sawStartup;
	private bool _sawActiveAfterStartup;
	private bool _sawRecoveryAfterActive;
	private bool _sawBeatenAfterRecovery;

	// Pass evidence for "control": none of the above may ever be observed.
	private bool _controlViolated;
	private string _controlViolationReason = "";

	public override void _Ready()
	{
		string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
		_role = HarnessArgs.ReadArg(args, "--harness-role", "server");
		_scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "telegraph");
		_port = int.TryParse(HarnessArgs.ReadArg(args, "--harness-port", "23460"), out int p) ? p : 23460;

		GD.Print($"[net-def-telegraph] role={_role} scenario={_scenario} port={_port} booting…");

		_net = GetNode<NetworkManager>("NetworkManager");
		_players = GetNode("Players");
		_ball = GetNode<BallController>("Ball");

		// Real PlayerController, no cosmetic children — see HarnessNetPlayer.tscn's
		// own doc (issue #212). Must match the MultiplayerSpawner's
		// _spawnable_scenes.
		_net.PlayerScenePath = "res://tests/integration/HarnessNetPlayer.tscn";

		if (_role == "client")
		{
			Multiplayer.ConnectedToServer += OnClientConnected;
			Multiplayer.ConnectionFailed += OnClientFailed;
			_net.JoinGame("127.0.0.1", _port);
		}
		else
		{
			Multiplayer.PeerConnected += OnServerPeerConnected;
			// Listen-server on purpose — see the class doc's topology note.
			_net.HostGame(_port);
		}
	}

	private void OnClientConnected()
	{
		_myPeerId = Multiplayer.GetUniqueId();
		GD.Print($"[net-def-telegraph] client connected as peer {_myPeerId}");
		// The proof rests on player "1" (the node the server commits the
		// move on) being REMOTE here. ENet reserves peer id 1 for the
		// server, but assert rather than assume — if this client ever held
		// id 1, every assertion below would read a locally-simulated
		// machine and the harness would prove nothing.
		if (_myPeerId == 1)
		{
			Fail("client was assigned peer id 1 — player 1 would be local, voiding the remote-display proof.");
			Finish();
		}
	}

	private void OnClientFailed()
	{
		Fail("client connection failed.");
		Finish();
	}

	private void OnServerPeerConnected(long id)
	{
		GD.Print($"[net-def-telegraph] server saw peer {id}");
		if (_serverStep == ServerStep.AwaitClient)
		{
			_serverStep = ServerStep.Settle;
			_stepDeadlineFrame = _frame + SettleFrames;
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		if (_finished) return;
		_elapsed += delta;
		_frame++;

		if (_role == "client") TickClient();
		else TickServer();

		double timeout = _role == "client" ? ClientTimeoutSeconds : ServerTimeoutSeconds;
		if (!_finished && _elapsed > timeout)
		{
			Fail($"{_role} timed out at frame {_frame}: " + (_role == "client"
				? $"scenario={_scenario} observing={_observing} sawStartup={_sawStartup} " +
				  $"sawActiveAfterStartup={_sawActiveAfterStartup} sawRecoveryAfterActive={_sawRecoveryAfterActive} " +
				  $"sawBeatenAfterRecovery={_sawBeatenAfterRecovery} controlFramesObserved={_controlFramesObserved}"
				: $"step={_serverStep} moveBegun={_moveBegun}"));
			Finish();
		}
	}

	// ── Server: host, and (scenario-dependent) begin a real StealMove ────────
	private void TickServer()
	{
		switch (_serverStep)
		{
			case ServerStep.AwaitClient:
				break; // OnServerPeerConnected advances this step.

			case ServerStep.Settle:
				if (_frame < _stepDeadlineFrame) break;
				_serverStep = ServerStep.Begin;
				break;

			case ServerStep.Begin:
				if (_scenario == "telegraph")
				{
					var defender = _players.GetNodeOrNull<PlayerController>("1");
					if (defender == null)
					{
						Fail("server lost its own player node Players/1.");
						Finish();
						return;
					}
					// Ball is left in its default Held state on purpose (never
					// dribbled) — ResolveStealAttempts only fires while
					// Dribbling (see its own doc), so this steal is
					// GUARANTEED to run its full Active window and whiff
					// naturally, never resolving early. That natural whiff is
					// exactly what BallController.ResolveBeatenWindowTriggers
					// (unconditional on ball state, unlike ResolveStealAttempts)
					// grants the blow-by window for.
					bool began = defender.BeginMoveForHarness(new StealMove(HandSide.Left));
					_moveBegun = began;
					if (!began)
					{
						Fail("BeginMoveForHarness(StealMove) returned false — machine was not Inactive at begin.");
						Finish();
						return;
					}
					GD.Print($"[net-def-telegraph] frame {_frame}: server began StealMove on player 1.");
				}
				else
				{
					GD.Print($"[net-def-telegraph] frame {_frame}: control scenario — never beginning a move.");
				}
				_serverStep = ServerStep.Linger;
				break;

			case ServerStep.Linger:
				// Keep broadcasting long enough for the client to finish its
				// observation window; the run script reads the CLIENT's exit
				// code as the verdict and kills this process on exit.
				if (_elapsed > ServerTimeoutSeconds - 2.0)
				{
					GD.Print("[net-def-telegraph] server lingered to near-timeout; client should have finished by now.");
				}
				break;
		}
	}

	// ── Client: watch the broadcast render the defensive telegraph + beaten cue ──
	private void TickClient()
	{
		if (_myPeerId <= 0) return; // not connected yet

		// Standing invariant of the whole proof: this process must never be
		// the multiplayer authority. If it were, DisplayMove()/DisplayBeaten()
		// would read local machine/window state on every node and the
		// assertions below would be vacuously satisfiable single-instance.
		if (Multiplayer.IsServer())
		{
			Fail("client process reports Multiplayer.IsServer() == true — remote-display proof void.");
			Finish();
			return;
		}

		var remote = _players.GetNodeOrNull<PlayerController>("1");

		if (!_observing)
		{
			if (remote != null)
			{
				_observing = true;
				GD.Print($"[net-def-telegraph] client observing remote player 1 at frame {_frame}");
			}
			return;
		}

		if (remote == null) return; // transient lookup miss — skip the tick, keep accumulators

		(MovePhase phase, _) = remote.DisplayMove();
		bool beaten = remote.DisplayBeaten();

		if (_scenario == "control")
		{
			_controlFramesObserved++;
			if (phase != MovePhase.Inactive)
			{
				_controlViolated = true;
				_controlViolationReason = $"observed non-Inactive DisplayMove() phase {phase} at frame {_frame} with no move ever begun.";
			}
			if (beaten)
			{
				_controlViolated = true;
				_controlViolationReason = $"observed DisplayBeaten()==true at frame {_frame} with no move ever begun.";
			}

			if (_controlFramesObserved >= ControlObserveFrames)
			{
				Verdict();
				return;
			}
		}
		else // "telegraph"
		{
			// Co-occurring, in-order evidence (doubt cycle 1, finding #1 — the
			// same discipline NetBehindTheBackSweepTest's own field doc
			// documents): each stage only counts once the PRIOR stage has
			// already been seen, so a re-triggered or out-of-order broadcast
			// can't satisfy this by accident.
			if (!_sawStartup && phase == MovePhase.Startup)
			{
				_sawStartup = true;
				GD.Print($"[net-def-telegraph] frame {_frame}: remote DisplayMove() == Startup.");
			}
			else if (_sawStartup && !_sawActiveAfterStartup && phase == MovePhase.Active)
			{
				_sawActiveAfterStartup = true;
				GD.Print($"[net-def-telegraph] frame {_frame}: remote DisplayMove() == Active (after Startup).");
			}
			else if (_sawActiveAfterStartup && !_sawRecoveryAfterActive && phase == MovePhase.Recovery)
			{
				_sawRecoveryAfterActive = true;
				GD.Print($"[net-def-telegraph] frame {_frame}: remote DisplayMove() == Recovery (after Active).");
			}

			if (_sawRecoveryAfterActive && !_sawBeatenAfterRecovery && beaten)
			{
				_sawBeatenAfterRecovery = true;
				GD.Print($"[net-def-telegraph] frame {_frame}: remote DisplayBeaten() == true (after Recovery).");
			}

			if (_sawStartup && _sawActiveAfterStartup && _sawRecoveryAfterActive && _sawBeatenAfterRecovery)
			{
				Verdict();
				return;
			}
		}
	}

	private void Verdict()
	{
		bool pass;

		if (_scenario == "control")
		{
			pass = !_controlViolated;
			if (pass)
			{
				GD.Print($"[net-def-telegraph] PASS — scenario=control, {_controlFramesObserved} frames observed, " +
					"never saw a non-Inactive phase or DisplayBeaten()==true on the remote node with no move begun.");
			}
			else
			{
				Fail($"scenario=control: {_controlViolationReason}");
			}
		}
		else
		{
			pass = _sawStartup && _sawActiveAfterStartup && _sawRecoveryAfterActive && _sawBeatenAfterRecovery;
			if (pass)
			{
				GD.Print("[net-def-telegraph] PASS — scenario=telegraph: remote client observed the defensive " +
					"Startup → Active → Recovery arc AND the whiff-punish beaten cue, all broadcast-derived, " +
					"on a process where Multiplayer.IsServer() == false.");
			}
			else
			{
				Fail($"scenario=telegraph incomplete: sawStartup={_sawStartup}, " +
					$"sawActiveAfterStartup={_sawActiveAfterStartup}, sawRecoveryAfterActive={_sawRecoveryAfterActive}, " +
					$"sawBeatenAfterRecovery={_sawBeatenAfterRecovery}.");
			}
		}

		Finish(pass ? 0 : 1);
	}

	private void Fail(string message) => GD.PrintErr($"[net-def-telegraph] FAIL ({_role}, scenario={_scenario}): {message}");

	private void Finish(int code = 1)
	{
		_finished = true;
		if (code != 0)
			GD.Print($"[net-def-telegraph] {_role} RESULT: FAIL (exit {code})");
		else
			GD.Print($"[net-def-telegraph] {_role} RESULT: PASS (exit {code})");
		GetTree().Quit(code);
	}
}
