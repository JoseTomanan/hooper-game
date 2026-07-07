using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Networking;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Dual-instance network harness — issue #212 (ADR-0016): prove the
// BROADCAST-DRIVEN BehindTheBack sweep path on a real remote client.
//
// What the single-instance BehindTheBackTest structurally cannot reach: in one
// process Multiplayer.IsServer() is unconditionally true, so
// DisplayPhaseResolver.LocalMachineDrivesDisplay is true for every node and
// PlayerController.DisplayMoveId() always reads the LIVE local machine. The
// one role that reads the broadcast _serverMoveId instead — the client's copy
// of a REMOTE player (!IsServer && !IsLocalPlayer) — only exists in a second
// process. That branch is exactly what BallController.AdvanceHandSweep samples
// (#194) to decide whether an in-flight hand sweep renders as a shielded
// behind-body transit, so proving it needs two real ENet peers.
//
// Topology: the SERVER boots via NetworkManager.HostGame — the listen-server
// path — because HostGame spawns a player named "1" (the host's own player),
// giving the server a holder IT simulates. On the CLIENT that same node is
// !IsServer && !IsLocalPlayer: the lone broadcast-display role in
// DisplayPhaseResolver's matrix. A dedicated server (increment 3's topology)
// has no player of its own, so there would be no server-side holder to begin
// the move.
//
// Flow: server hosts → tipoff lands on player "1" (the only node present) →
// client joins (its player spawns as the idle defender) → server drives
// TryStartDribble(1) then begins a real BehindTheBack on its own player via
// the BeginBehindTheBackForHarness seam (the same BeginCommittedMove path
// production uses). The server's per-tick ReceiveState broadcast carries the
// flipped HandSide and moveId="behindtheback" in the same snapshot; the
// client's ball detects the flip locally (AdvanceHandSweep), samples the
// remote holder's DisplayMoveId() — the _serverMoveId branch — and must
// render the shielded transit with NO local machine ever having run the move.
//
//   godot --headless --path . res://tests/integration/NetBehindTheBackSweepTest.tscn -- --harness-role=server --harness-port=PORT
//   godot --headless --path . res://tests/integration/NetBehindTheBackSweepTest.tscn -- --harness-role=client --harness-port=PORT
//
// Exit: 0 = the CLIENT observed, on the SAME live-sweep tick (co-occurring,
// not independently latched), (a) the sweep flagged behind-body, (c) the
// remote holder's DisplayMoveId() returning "behindtheback", and — on such
// ticks — (b) the ball's forward offset going genuinely NEGATIVE (behind the
// holder's centerline); all on a process where Multiplayer.IsServer() is
// false. 1 = failed/timed out (via GetTree().Quit). Orchestrated by
// run-net-behindtheback-sweep.sh, which reads the client's exit code as the
// verdict.
public partial class NetBehindTheBackSweepTest : Node3D
{
	private const double ServerTimeoutSeconds = 60.0;
	private const double ClientTimeoutSeconds = 40.0;

	// Server pacing. SettleFrames covers spawner replication + the client's
	// first reconciled ball snapshot after connect; AttemptIntervalFrames
	// comfortably exceeds a full BehindTheBack startup+active+recovery
	// (~21 ticks) so every re-attempt finds the machine Inactive again.
	private const int SettleFrames = 60;
	private const int ActionMarginFrames = 3;
	private const int AttemptIntervalFrames = 90;
	private const int MaxAttempts = 3;
	private const double ServerLingerSeconds = 15.0;

	private string _role = "server";
	private int _port = 23459;
	private int _frame;
	private double _elapsed;
	private bool _finished;

	private NetworkManager _net;
	private Node _players;
	private BallController _ball;

	// ── Server state ─────────────────────────────────────────────────────────
	private enum ServerStep { AwaitClient, Settle, StartDribble, VerifyDribble, Attempting, Linger }
	private ServerStep _serverStep = ServerStep.AwaitClient;
	private int _stepDeadlineFrame;
	private int _attempts;
	private int _begunCount;
	private double _lingerStartedAt = -1.0;

	// ── Client state ─────────────────────────────────────────────────────────
	private int _myPeerId = -1;
	private bool _observing;
	private bool _sawLiveSweep;

	// Pass evidence. Deliberately NOT three independent latches accumulated
	// across the whole run (doubt cycle 1, finding #1): the behind-body flag,
	// the broadcast moveId, and the forward offset must CO-OCCUR on the same
	// live-sweep tick, otherwise three unrelated moments could add up to a
	// false pass. _sawBehindBodySweepWithRemoteMoveId records that a live
	// sweep tick held both discrete facts at once; the offset minimum is only
	// accumulated on exactly those ticks.
	private bool _sawBehindBodySweepWithRemoteMoveId;
	private float _minForwardOffsetDuringSweep = float.PositiveInfinity;

	public override void _Ready()
	{
		string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
		_role = HarnessArgs.ReadArg(args, "--harness-role", "server");
		_port = int.TryParse(HarnessArgs.ReadArg(args, "--harness-port", "23459"), out int p) ? p : 23459;

		GD.Print($"[net-btb-sweep] role={_role} port={_port} booting…");

		_net = GetNode<NetworkManager>("NetworkManager");
		_players = GetNode("Players");
		_ball = GetNode<BallController>("Ball");

		// Spawn the harness spawnable (real PlayerController, no cosmetic
		// children — see HarnessNetPlayer.tscn) instead of the heavyweight
		// Player.tscn. Must match the MultiplayerSpawner's _spawnable_scenes.
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
		GD.Print($"[net-btb-sweep] client connected as peer {_myPeerId}");
		// The proof rests on the holder ("1") being a REMOTE node here. ENet
		// reserves peer id 1 for the server, but assert rather than assume —
		// if this ever held id 1, every assertion below would be reading a
		// locally-simulated machine and the harness would prove nothing.
		if (_myPeerId == 1)
		{
			Fail("client was assigned peer id 1 — the holder would be local, voiding the remote-display proof.");
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
		GD.Print($"[net-btb-sweep] server saw peer {id}");
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
				? $"observing={_observing} sawLiveSweep={_sawLiveSweep} sawBehindBodySweepWithRemoteMoveId={_sawBehindBodySweepWithRemoteMoveId} minForwardOffset={_minForwardOffsetDuringSweep:F4}"
				: $"step={_serverStep} attempts={_attempts} begun={_begunCount}"));
			Finish();
		}
	}

	// ── Server: host, hand the ball to player 1, drive real BehindTheBacks ───
	private void TickServer()
	{
		switch (_serverStep)
		{
			case ServerStep.AwaitClient:
				break; // OnServerPeerConnected advances this step.

			case ServerStep.Settle:
				if (_frame < _stepDeadlineFrame) break;
				// Tipoff is server-only and fires on the first tick a player
				// node exists (TryAssignTipoffHolder) — player "1" spawned in
				// HostGame, so by now the holder must be assigned.
				if (_ball.StateMachine.HolderPeerId != 1)
				{
					Fail($"expected tipoff to land on player 1; holder={_ball.StateMachine.HolderPeerId}.");
					Finish();
					return;
				}
				_serverStep = ServerStep.StartDribble;
				break;

			case ServerStep.StartDribble:
				// BehindTheBack cannot Begin from Held (#193's dead-dribble
				// gate) — the possession needs a live dribble first, same as
				// the single-instance shielded-sweep scenario.
				_ball.TryStartDribble(1);
				_serverStep = ServerStep.VerifyDribble;
				_stepDeadlineFrame = _frame + ActionMarginFrames;
				break;

			case ServerStep.VerifyDribble:
				if (_frame < _stepDeadlineFrame) break;
				if (_ball.State != BallState.Dribbling)
				{
					Fail($"expected TryStartDribble(1) to reach Dribbling; got state={_ball.State}.");
					Finish();
					return;
				}
				_serverStep = ServerStep.Attempting;
				_stepDeadlineFrame = _frame; // first attempt immediately
				break;

			case ServerStep.Attempting:
				if (_frame < _stepDeadlineFrame) break;
				var holder = _players.GetNodeOrNull<PlayerController>("1");
				if (holder == null)
				{
					Fail("server lost its own player node Players/1.");
					Finish();
					return;
				}
				// The same BeginCommittedMove path production uses (see the
				// seam's doc). Repeated attempts give the client several
				// sweeps to observe; a refusal mid-recovery is impossible at
				// this spacing but tolerated (only the success count gates).
				bool began = holder.BeginBehindTheBackForHarness(1f);
				_attempts++;
				if (began) _begunCount++;
				GD.Print($"[net-btb-sweep] server attempt {_attempts}/{MaxAttempts}: began={began}");
				if (_attempts >= MaxAttempts)
				{
					if (_begunCount == 0)
					{
						Fail("no BehindTheBack ever began on the server-side holder.");
						Finish();
						return;
					}
					_serverStep = ServerStep.Linger;
					_lingerStartedAt = _elapsed;
				}
				else
				{
					_stepDeadlineFrame = _frame + AttemptIntervalFrames;
				}
				break;

			case ServerStep.Linger:
				// Keep broadcasting long enough for the client to finish its
				// observation window; the run script's verdict is the CLIENT's
				// exit code, and it also kills this process on exit.
				if (_elapsed - _lingerStartedAt >= ServerLingerSeconds)
				{
					GD.Print($"[net-btb-sweep] server RESULT: PASS (exit 0) — {_begunCount} move(s) begun, lingered {ServerLingerSeconds}s");
					Finish(0);
				}
				break;
		}
	}

	// ── Client: watch the broadcast render the shielded transit ─────────────
	private void TickClient()
	{
		if (_myPeerId <= 0) return; // not connected yet

		// Standing invariant of the whole proof: this process must never be
		// the multiplayer authority. If it were, DisplayMoveId() would read
		// the local machine on every node and the assertions below would be
		// vacuously satisfiable single-instance.
		if (Multiplayer.IsServer())
		{
			Fail("client process reports Multiplayer.IsServer() == true — remote-display proof void.");
			Finish();
			return;
		}

		var holder = _players.GetNodeOrNull<PlayerController>("1");

		if (!_observing)
		{
			// Observation starts once this peer's reconciled ball agrees the
			// REMOTE player 1 is dribbling — everything from here on is
			// broadcast-derived state.
			if (holder != null && _ball.State == BallState.Dribbling && _ball.StateMachine.HolderPeerId == 1)
			{
				_observing = true;
				GD.Print($"[net-btb-sweep] client observing: remote holder 1 dribbling at frame {_frame}");
			}
			return;
		}

		if (holder == null) return; // transient lookup miss — skip the tick, keep accumulators

		// All three pieces of evidence are sampled on the SAME tick, and only
		// while the sweep is live (doubt cycle 1, finding #1 — see the field
		// doc on _sawBehindBodySweepWithRemoteMoveId):
		//   (a) the client's OWN ball copy — which ran AdvanceHandSweep
		//       locally off the broadcast-adopted HandSide flip — flags the
		//       live sweep behind-body (a stale _sweepIsBehindBody from a
		//       finished sweep can't satisfy this);
		//   (c) the direct probe of the branch under test: on this process
		//       the holder is !IsServer && !IsLocalPlayer, so DisplayMoveId()
		//       must be returning the broadcast _serverMoveId — there is no
		//       live local machine for it to read (TickClientRemotePlayer
		//       never advances one);
		//   (b) on exactly those ticks, the rendered ball must have been
		//       pulled behind the holder's centerline.
		if (_ball.SweepActiveForHarness)
		{
			_sawLiveSweep = true;
			bool behindBody = _ball.SweepIsBehindBodyForHarness;
			bool remoteMoveId = holder.DisplayMoveId() == "behindtheback";
			if (behindBody && remoteMoveId)
			{
				_sawBehindBodySweepWithRemoteMoveId = true;

				// Holder heading stays 0 throughout — spawned at the default
				// heading (facing +Z) and committed moves never turn (heading
				// only advances in Move(), ADR-0010) — so forward = (0,0,1)
				// and the forward offset reduces to a same-tick relative Z
				// subtraction, the convention BehindTheBackTest.ForwardOffset
				// uses. (The holder may TRANSLATE during the Active burst;
				// that's fine — both positions are read on the same tick.)
				float forwardOffset = _ball.GlobalPosition.Z - holder.GlobalPosition.Z;
				if (forwardOffset < _minForwardOffsetDuringSweep)
					_minForwardOffsetDuringSweep = forwardOffset;
			}
		}

		if (_sawBehindBodySweepWithRemoteMoveId && _minForwardOffsetDuringSweep < 0f)
		{
			GD.Print($"[net-btb-sweep] PASS — behind-body sweep rendered from broadcast state on a true client: " +
				$"minForwardOffsetDuringSweep={_minForwardOffsetDuringSweep:F4} < 0 on ticks where the live sweep was behind-body AND DisplayMoveId()=='behindtheback' remotely.");
			GD.Print("[net-btb-sweep] client RESULT: PASS (exit 0)");
			Finish(0);
		}
	}

	private void Fail(string message) => GD.PrintErr($"[net-btb-sweep] FAIL ({_role}): {message}");

	private void Finish(int code = 1)
	{
		_finished = true;
		if (code != 0)
			GD.Print($"[net-btb-sweep] {_role} RESULT: FAIL (exit {code})");
		GetTree().Quit(code);
	}
}
