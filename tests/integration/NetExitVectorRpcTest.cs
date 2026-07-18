using System.Linq;
using Godot;
using Hooper.Moves;
using Hooper.Networking;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Dual-instance network harness — issue #210: prove the discrete
// RequestExitVector RPC is fired from the REAL client input hook and
// reconstructed on the REAL server, not merely exercised through a harness
// seam standing in for the wire transmission.
//
// Why this MUST be dual-instance (ADR-0016, hooper-verification-and-qa): the
// RequestExitVector send site lives inside TickClientOwnPlayer, which only
// ever runs on a role that is "!IsServer && IsLocalPlayer" — structurally
// unreachable in a single offline instance, where Multiplayer.IsServer() is
// unconditionally true (OfflineMultiplayerPeer). Only a genuine second peer
// can occupy that role.
//
// Topology: SERVER boots via NetworkManager.HostGame (listen-server), owning
// player "1" (unused by this proof). CLIENT joins via JoinGame and becomes
// player "2" — a player the SERVER sees as REMOTE (TickServerRemotePlayer).
// The CLIENT drives a REAL Crossover through the UNMODIFIED production input
// path: it holds the right-stick gesture (aim_right) past
// RightStickGestureRecognizer's FeintWindowTicks so SampleMoveInput commits a
// genuine Crossover (no BeginXForHarness seam involved for the move itself —
// see the orchestrator's explicit "not merely exercised through
// BeginMoveForHarness" requirement), then holds a real, distinct left-stick
// direction (move_right) as its exit vector. Crossover does not require ball
// possession to begin (BeginCommittedMove's dead-Held gate only applies
// `&& IsBallHolder` — player "2" never holds the ball in this scenario), so
// no tipoff/dribble setup is needed.
//
// The SERVER is the verdict-bearing process here (a deliberate deviation
// from this repo's other dual-instance harnesses, which all read the
// CLIENT's exit code — see run-net-exitvector-rpc.sh's header comment for
// why): the fact under test — "did the SERVER's own authoritative
// composition use the correct exit vector" — is server-side ground truth,
// not something the client can observe without its OWN local re-prediction
// masking the server's actual value (TickClientOwnPlayer recomposes the
// burst from its OWN live rawStick on its OWN JustEnteredActive tick,
// immediately after any reconcile snap, so the client's own Velocity can
// never be used to inspect what the SERVER actually composed).
//
// Two scenarios, SAME pass condition (composed Velocity for player "2" on
// the SERVER matches the pure CrossoverBurstMath oracle fed the TRUE exit
// vector), differing only in whether the server's _pendingRawStick cache is
// deliberately poisoned to a DIFFERENT, wrong decoy direction throughout the
// run (SetPendingRawStickForHarness — the SAME seam #198's MovingCrossoverTest
// already uses, simulating "the streamed cache holds a stale/wrong sample"):
//   "steady"   — no poisoning. The control: proves the ordinary, unpoisoned
//                path is unaffected by this fix (client==server, no jitter).
//   "poisoned" — continuous poisoning to the OPPOSITE direction. The actual
//                regression proof: pre-#210 code read _pendingRawStick
//                DIRECTLY and would have composed the DECOY direction here;
//                post-#210 code prefers the RequestExitVector RPC's value
//                and must still compose the TRUE direction.
// Verified RED against the pre-fix code (see this test's PR description for
// the stash/run/restore evidence) — the "poisoned" scenario fails without
// the fix and passes with it; "steady" passes either way, as expected of a
// control.
//
//   godot --headless --path . res://tests/integration/NetExitVectorRpcTest.tscn -- --harness-role=server --harness-port=PORT --harness-scenario=steady|poisoned
//   godot --headless --path . res://tests/integration/NetExitVectorRpcTest.tscn -- --harness-role=client --harness-port=PORT --harness-scenario=steady|poisoned
//
// Exit (server process, the verdict): 0 = the composed velocity matched the
// TRUE-exit-vector oracle within tolerance; 1 = mismatch or timeout.
public partial class NetExitVectorRpcTest : Node3D
{
	private const double ServerTimeoutSeconds = 40.0;
	private const double ClientTimeoutSeconds = 30.0;
	private const float VelocityTolerance = 0.05f;

	// The exit vector the CLIENT genuinely feeds via real move_right input,
	// and the DECOY the server's _pendingRawStick is poisoned to when
	// scenario == "poisoned" — deliberately the OPPOSITE lateral direction so
	// a fix that fails to prefer the RPC'd value produces a clearly different
	// (not just slightly off) composed velocity.
	private static readonly Vector2 TrueExitVector = new(1f, 0f);
	private static readonly Vector2 DecoyExitVector = new(-1f, 0f);

	private const int SettleFrames = 60;
	// The CLIENT must not start its gesture until the SERVER has reached
	// ServerStep.Observe — a full Crossover (Startup 6 + Active 3 + Recovery
	// 12 = 21 ticks) completes in well under a second, comfortably inside the
	// server's OWN SettleFrames window if the client acted immediately on
	// connect. This margin (comfortably > SettleFrames) is what a doubt-cycle
	// re-run of this harness caught RED: without it, the server only starts
	// watching AFTER the move already finished, so the assertion never fires
	// at all (a vacuous "never observed" failure, not a real divergence).
	private const int ClientWaitFrames = 120;
	// Held just past RightStickGestureRecognizer's default FeintWindowTicks
	// (4) so the gesture commits as the "hold" Crossover on tick 5, never a
	// QuickReturn — and NOT held any longer than that: Startup (6 ticks)
	// begins the instant the gesture commits regardless of how much longer
	// aim_right stays held, so overholding here burns Startup ticks with a
	// zero exit vector and leaves less margin for move_right to register
	// before Active begins (a doubt-cycle re-run of this harness caught this
	// RED: AimHoldFrames=10 left only ~1 Startup tick of margin, which lost
	// the race against Input.ActionPress's own one-frame-later analog
	// registration — see hooper-debugging-playbook's ActionPress gotcha).
	private const int AimHoldFrames = 6;

	private string _role = "server";
	private string _scenario = "poisoned";
	private int _port = 23461;
	private int _frame;
	private double _elapsed;
	private bool _finished;

	private NetworkManager _net;
	private Node _players;

	// ── Server state ─────────────────────────────────────────────────────────
	private enum ServerStep { AwaitClient, Settle, Observe }
	private ServerStep _serverStep = ServerStep.AwaitClient;
	private int _stepDeadlineFrame;
	// ENet assigns the connecting peer a large pseudo-random 32-bit id, NOT a
	// sequential "2" — only the host is guaranteed id 1. Captured from the
	// real PeerConnected signal so the server looks up the correct spawned
	// node instead of a hardcoded, wrong name.
	private long _remotePeerId = -1;

	// ── Client state ─────────────────────────────────────────────────────────
	private enum ClientStep { Connecting, Waiting, HoldAim, HoldExit, Done }
	private ClientStep _clientStep = ClientStep.Connecting;
	private int _clientStepStartFrame;
	private int _myPeerId = -1;

	public override void _Ready()
	{
		string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
		_role = HarnessArgs.ReadArg(args, "--harness-role", "server");
		_scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "poisoned");
		_port = int.TryParse(HarnessArgs.ReadArg(args, "--harness-port", "23461"), out int p) ? p : 23461;

		GD.Print($"[net-exitvec] role={_role} scenario={_scenario} port={_port} booting…");

		_net = GetNode<NetworkManager>("NetworkManager");
		_players = GetNode("Players");
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
			_net.HostGame(_port);
		}
	}

	private void OnClientConnected()
	{
		_myPeerId = Multiplayer.GetUniqueId();
		GD.Print($"[net-exitvec] client connected as peer {_myPeerId}");
		if (_myPeerId == 1)
		{
			Fail("client was assigned peer id 1 — it would be the server's OWN player, voiding the remote-input proof.");
			Finish(1);
		}
	}

	private void OnClientFailed()
	{
		Fail("client connection failed.");
		Finish(1);
	}

	private void OnServerPeerConnected(long id)
	{
		GD.Print($"[net-exitvec] server saw peer {id}");
		_remotePeerId = id;
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
			Fail($"{_role} timed out at frame {_frame} (step={(_role == "client" ? _clientStep.ToString() : _serverStep.ToString())}).");
			Finish(1);
		}
	}

	// ── Server: host, (optionally) poison the streamed cache, observe ───────
	private void TickServer()
	{
		PlayerController remote = _remotePeerId > 0
			? _players.GetNodeOrNull<PlayerController>(_remotePeerId.ToString())
			: null;

		// Poisoning runs from the moment the node exists, EVERY tick,
		// unconditionally, for the "poisoned" scenario only — this simulates
		// "the streamed _pendingRawStick cache always holds a stale/wrong
		// sample" (the worst-case, maximally adversarial jitter condition),
		// deliberately stronger than a single dropped packet so the test
		// isn't sensitive to exact cross-process tick alignment. Harmless
		// before any committed move begins — _pendingRawStick is inert until
		// TickCommittedMoveBehavior's burst-family branch reads it.
		if (_scenario == "poisoned" && remote != null)
			remote.SetPendingRawStickForHarness(DecoyExitVector);

		switch (_serverStep)
		{
			case ServerStep.AwaitClient:
				break; // OnServerPeerConnected advances this step.

			case ServerStep.Settle:
				if (_frame < _stepDeadlineFrame) break;
				_serverStep = ServerStep.Observe;
				break;

			case ServerStep.Observe:
				if (remote == null) break; // transient lookup miss — keep waiting

				if (_frame % 30 == 0)
					GD.Print($"[net-exitvec] server DIAG frame={_frame} phase={remote.DisplayMove().phase}");

				var active = remote.ActiveMove<Crossover>();
				if (active == null) break; // not in Active yet (or already past it)

				// Oracle: the SAME production composition function, fed the
				// TRUE exit vector directly — not a hand-derived expected
				// number, so this can't silently drift from
				// CrossoverBurstMath's own behavior. survivingVelocity is
				// Vector3.Zero and heading is 0 by construction: the client
				// never presses move_* until AFTER Crossover has begun (see
				// TickClient's HoldAim/HoldExit split), and Move()/heading
				// rotation is skipped entirely while a committed move
				// IsActive — so nothing perturbs either input to the oracle.
				Vector3 expected = CrossoverBurstMath.ComposeActiveVelocity(
					Vector3.Zero, 0f, System.Math.Sign(active.Value.Move.BurstDirection),
					TrueExitVector, remote.BurstSpeed, remote.ForwardBurstScale, remote.ExitDeadzone);

				float diff = (expected - remote.Velocity).Length();
				GD.Print($"[net-exitvec] server observed Active frameInPhase={active.Value.FrameInPhase} " +
					$"expected={expected} actual={remote.Velocity} diff={diff:F4}");

				if (diff <= VelocityTolerance)
				{
					GD.Print($"[net-exitvec] PASS scenario={_scenario} — composed velocity matched the TRUE-exit-vector oracle (diff={diff:F4}).");
					Finish(0);
				}
				else if (active.Value.FrameInPhase >= active.Value.Move.FrameData.ActiveFrames - 1)
				{
					// Last Active tick observed and still mismatched — the
					// composition is a one-shot SET on JustEnteredActive that
					// never re-fires, so there is nothing left to wait for.
					Fail($"composed velocity diverged from the TRUE-exit-vector oracle: expected={expected} actual={remote.Velocity} diff={diff:F4}");
					Finish(1);
				}
				break;
		}
	}

	// ── Client: connect, drive REAL production input, then idle ─────────────
	private void TickClient()
	{
		if (_frame % 15 == 0 && _myPeerId > 0)
		{
			var me = _players.GetNodeOrNull<PlayerController>(_myPeerId.ToString());
			GD.Print($"[net-exitvec] client DIAG frame={_frame} step={_clientStep} aimRightStrength={Input.GetActionStrength("aim_right"):F2} myPhase={me?.DisplayMove().phase}");
		}
		switch (_clientStep)
		{
			case ClientStep.Connecting:
				if (_myPeerId <= 0) return; // not connected yet
				if (Multiplayer.IsServer())
				{
					Fail("client process reports Multiplayer.IsServer() == true — remote-input proof void.");
					Finish(1);
					return;
				}
				_clientStep = ClientStep.Waiting;
				_clientStepStartFrame = _frame;
				break;

			case ClientStep.Waiting:
				// See ClientWaitFrames' doc: give the SERVER time to reach
				// ServerStep.Observe before this process's Crossover even
				// begins — otherwise the whole Startup->Active->Recovery
				// cycle can complete before the server ever starts watching.
				if (_frame - _clientStepStartFrame < ClientWaitFrames) break;
				_clientStep = ClientStep.HoldAim;
				_clientStepStartFrame = _frame;
				Input.ActionPress("aim_right", 1.0f);
				GD.Print($"[net-exitvec] client: holding aim_right from frame {_frame} to commit a real Crossover gesture.");
				break;

			case ClientStep.HoldAim:
				// Release the gesture stick once it has been held long
				// enough to commit (RightStickGestureRecognizer.Sample fires
				// GestureKind.Crossover on the tick ticksAboveThreshold
				// exceeds FeintWindowTicks) — SampleMoveInput's production
				// dispatch (the plain-Crossover branch, no move_size_up/
				// move_finesse modifiers held) begins the REAL move and
				// fires the REAL RequestBeginMove RPC, no seam involved.
				if (_frame - _clientStepStartFrame >= AimHoldFrames)
				{
					Input.ActionRelease("aim_right");
					// Only NOW start pressing the TRUE exit vector — never
					// before the Crossover has begun (Startup freezes
					// Heading and skips Move() entirely for the move's
					// whole duration, so pressing move_right here cannot
					// rotate Heading away from the 0 the server-side oracle
					// assumes; pressing it BEFORE Crossover begins would
					// let ordinary Move() turn the player first).
					Input.ActionPress("move_right", 1.0f);
					_clientStep = ClientStep.HoldExit;
					_clientStepStartFrame = _frame;
					GD.Print($"[net-exitvec] client: gesture held, now pressing move_right (the TRUE exit vector) from frame {_frame}.");
				}
				break;

			case ClientStep.HoldExit:
				// Hold the true exit vector through Crossover's Startup (6
				// ticks) + Active (3 ticks) + a margin, then this process's
				// job is done — the SERVER carries the verdict. Keep ticking
				// (never call Finish here) so the JustEnteredActive-timed
				// RequestExitVector RPC actually fires from a live process;
				// exit only via the shared timeout once the server side has
				// had time to reach its own verdict.
				break;
		}
	}

	private void Fail(string message) => GD.PrintErr($"[net-exitvec] FAIL ({_role}): {message}");

	private void Finish(int code)
	{
		_finished = true;
		GD.Print($"[net-exitvec] {_role} RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
		GetTree().Quit(code);
	}
}
