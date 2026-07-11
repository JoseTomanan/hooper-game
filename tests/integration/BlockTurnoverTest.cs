using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Player;
using Hooper.Moves;
using Hooper.Systems;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #98 (ADR-0016): the block-turnover
// mechanic, end to end. Unit tests already pin the pure DefensiveResolution
// predicate and BlockMove's frame-data contract; what they CANNOT reach is the
// live engine glue: BallController.ResolveBlockAttempts reading a REAL
// defender's committed-move Phase/FrameInPhase against a REAL shooter's
// InFlight release tick, the pre-switch-plus-release-tick-top-up call ordering
// that makes "a blocked shot cannot score" true, and the resulting swat
// velocity/last-toucher/Recovery side effects.
//
//   godot --headless --path . res://tests/integration/BlockTurnoverTest.tscn -- --harness-scenario=success
//   godot --headless --path . res://tests/integration/BlockTurnoverTest.tscn -- --harness-scenario=whiff
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "success".
//
// ── Why a single offline instance is the server ───────────────────────────
// Same reasoning as StealTurnoverTest/TripleThreatTest: with no MultiplayerPeer
// assigned, Godot uses OfflineMultiplayerPeer (is_server() hardcoded true,
// unique_id 1), so BallController.IsServer is true and BOTH player nodes'
// _machine.Tick() advances every physics frame regardless of role (see
// PlayerController's class doc) — the free clock that walks the shooter's
// JumpShot and the defender's BlockMove through Startup->Active with no
// per-tick poking once each is begun via the harness seam.
//
// ── Scenario "success": defender's Active overlaps the release grace window ──
// The shooter (peer "1", the tipoff holder) begins a REAL JumpShot via the
// TripleThreatHarnessSeam's BeginJumpShotForHarness (the same BeginCommittedMove
// path production input reaches). The defender (peer "2") begins a REAL
// BlockMove via BlockHarnessSeam's BeginBlockForHarness, timed via
// ComputeDefenderBeginFrame so its Active window's very first tick lands
// exactly on the shot's release tick (frame arithmetic derived from the LIVE
// JumpShot/BlockMove frame data and BallController.BlockGraceTicks, not
// hardcoded, so this survives #104 retuning). ADR-0018 §2's full interval
// form (DefensiveResolution.Succeeds) then reports an overlap on the very
// first tick BallController evaluates it — the release-tick top-up call
// (BallController._PhysicsProcess) is what makes that first tick observable
// at all, since the pre-switch call that same tick still saw Held/Dribbling.
//
// ── Scenario "whiff": defender's Active begins strictly after the grace window closes ──
// ComputeDefenderBeginFrame places the Active window's entry tick one tick
// past the vulnerable window's exclusive end, so DefensiveResolution.Succeeds'
// half-open-interval check can never true — the shot must proceed unblocked
// (state stays InFlight) and the defender still pays Recovery for the whiff.
//
// ── Self-block (dropped scenario) ──────────────────────────────────────────
// ADR-0018/BallController.ResolveBlockAttempts excludes the shooter from being
// their own blocker via the _lastShooterPeerId check. This is NOT reachable
// through the harness seam (or even a tampered production client) under the
// current single-CommittedMoveMachine-per-player model: BeginBlockForHarness
// is exactly _machine.Begin(new BlockMove()), which only succeeds from
// Phase == Inactive — but the shooter's OWN machine is occupied by their
// JumpShot's Active (4 ticks) then Recovery (20 ticks) for the shot's ENTIRE
// vulnerable window (BlockGraceTicks == 10 ticks after release), so it never
// returns to Inactive until long after the grace window has closed. Forcing
// this scenario would require bypassing _machine's phase graph entirely
// (e.g. a raw field write), which is reimplementing the guard rather than
// exercising real glue — the opposite of what this harness is for. Dropped;
// the exclusion is defensive/future-proofing under the current architecture,
// not a reachable live bug surface today.
public partial class BlockTurnoverTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int ArmFrames = 2; // ticks for TryAssignTipoffHolder to run

    // Extra ticks to let the loose-ball scramble settle into a fresh Held
    // possession before rendering the "success" verdict, and to let the
    // "whiff" verdict observe a state well clear of any one-tick read skew.
    private const int VerdictMarginFrames = 6;

    // Holder position for the shot (issue #98): far enough from RimCenter's
    // (0, *, 0) XZ that the block's swat direction (computed from
    // GlobalPosition - RimCenter, see ResolveBlockAttempts) is unambiguously
    // non-degenerate — this discriminates a real "away from the rim" swat
    // from the original "toward the rim" shot velocity by the sign of Z
    // alone, without needing exact geometry.
    private static readonly Vector3 ShooterPosition = new(0f, 0f, 5f);

    private string _scenario = "success";

    private BallController _ball;
    private GameManager _gameManager;
    private PlayerController _shooter; // peer "1" — always the tipoff holder in this code-built tree
    private PlayerController _defender; // peer "2"

    private int _frame;
    private double _elapsed;
    private bool _finished;

    private bool _shooterBegun;
    private bool _defenderBegun;
    private int _shooterBeginFrame;
    private int _releaseFrame = -1; // set once we observe InFlight (the shot's true release tick)
    private int _defenderBeginFrame;

    private bool _everLoose;
    private int _toucherAtBlock = -1;   // latched on the first Loose tick
    private Vector3 _velocityAtBlock;   // latched on the first Loose tick
    private bool _velocityLatched;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "success");
        GD.Print($"[block-turnover] scenario={_scenario} booting headless…");

        // Same code-built-tree pattern as StealTurnoverTest/TripleThreatTest —
        // avoids fragile .tscn ext_resource/uid wiring for a throwaway harness.
        // Tree pre-order matches scenes/Main.tscn's declaration order (Players,
        // then Ball, then GameManager) so per-tick observation timing mirrors
        // production (see StealTurnoverTest's class doc for why this ordering
        // is load-bearing for the frame arithmetic below).
        var players = new Node3D { Name = "Players" };
        _shooter  = new PlayerController { Name = "1" };
        _defender = new PlayerController { Name = "2" };
        players.AddChild(_shooter);
        players.AddChild(_defender);

        _ball = new BallController { Name = "Ball", Players = players };

        // Distance-based shot scatter (ADR-0009) would otherwise draw a
        // seeded-RNG offset even for a "clean" aim — disabling it makes the
        // unblocked shot a GUARANTEED clean make (aimTarget == ShotTarget ==
        // RimCenter unconditionally), which is what makes "score unchanged"
        // in the success scenario a real proof that the block intercepted
        // the shot, not a lucky miss the block had nothing to do with.
        _ball.ShotScatterEnabled = false;

        _gameManager = new GameManager { Name = "GameManager" };

        AddChild(players);      // matches scenes/Main.tscn: Players before Ball
        AddChild(_ball);
        AddChild(_gameManager);

        // Live-scene frame-data invariant (ADR-0018 §3): a defensive Active
        // window can never be wider than the offensive vulnerable window it
        // must hit — read from the LIVE ball node's export (not the static
        // class constant alone), so a .tscn/export override that breaks this
        // invariant would be caught here even though the pure xUnit test
        // (hardcoded const) cannot see it.
        int liveBlockGraceTicks = _ball.BlockGraceTicks;
        int liveBlockActiveFrames = BlockMove.DefaultFrameData.ActiveFrames;
        if (liveBlockActiveFrames > liveBlockGraceTicks)
        {
            Fail($"ADR-0018 §3 invariant violated: BlockMove.ActiveFrames ({liveBlockActiveFrames}) " +
                 $"must be <= BallController.BlockGraceTicks ({liveBlockGraceTicks}).");
            Finish();
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        // Step 1: once the tipoff has assigned a holder, begin the shooter's
        // REAL JumpShot through the SAME BeginCommittedMove path production
        // input uses (TripleThreatHarnessSeam's seam, issue #193).
        if (!_shooterBegun && _frame >= ArmFrames)
        {
            if (_ball.StateMachine.HolderPeerId != 1)
            {
                Fail($"expected the tipoff to award peer 1 (this code-built tree's first child); got {_ball.StateMachine.HolderPeerId}.");
                Finish();
                return;
            }

            bool began = _shooter.BeginJumpShotForHarness();
            _shooterBegun = true;
            if (!began)
            {
                Fail("BeginJumpShotForHarness returned false — shooter's machine was not Inactive at begin.");
                Finish();
                return;
            }

            _shooterBeginFrame = _frame;

            // Position the shooter away from the rim's XZ (see ShooterPosition's
            // doc) BEFORE the release tick, so the eventual swat-direction
            // check is unambiguous. Positioning here (Startup, well before
            // Active/release) is harmless — TickHeld/TickDribbling re-centres
            // the ball on the holder's position every tick regardless.
            _shooter.GlobalPosition = ShooterPosition;

            // Defender's begin frame is computed from the SHOOTER's actual
            // begin frame plus the LIVE JumpShot/BlockMove frame data and
            // BlockGraceTicks — not hardcoded — so it survives #104 retuning.
            int jumpShotStartup = JumpShot.DefaultFrameData.StartupFrames;
            int releaseFrame = _shooterBeginFrame + (jumpShotStartup - 1);
            _defenderBeginFrame = ComputeDefenderBeginFrame(releaseFrame);

            GD.Print($"[block-turnover] frame {_frame}: shooter begun JumpShot " +
                     $"(expected release frame {releaseFrame}); defender scheduled to begin at frame {_defenderBeginFrame}.");
        }

        // Step 2: begin the defender's REAL BlockMove at the computed frame.
        if (_shooterBegun && !_defenderBegun && _frame == _defenderBeginFrame)
        {
            bool began = _defender.BeginBlockForHarness();
            _defenderBegun = true;
            if (!began)
            {
                Fail("BeginBlockForHarness returned false — defender's machine was not Inactive at begin.");
                Finish();
                return;
            }
            GD.Print($"[block-turnover] frame {_frame}: defender begun BlockMove.");
        }

        // Latch the true release tick from live ball state — ApplyShootLocally
        // sets BallState.InFlight synchronously inside this same physics frame
        // once JumpShot's Active begins, so the FIRST tick we observe InFlight
        // is the release tick (used only for logging/sanity here; the
        // defender's begin frame was already scheduled off the PREDICTED
        // release frame above, exactly like StealTurnoverTest schedules off
        // predicted dribble-phase frames rather than waiting to observe them).
        if (_releaseFrame < 0 && _ball.State == BallState.InFlight)
        {
            _releaseFrame = _frame;
            GD.Print($"[block-turnover] frame {_frame}: ball entered InFlight (shot released).");
        }

        // Latch the turnover: catch the Loose state even if the loose-ball
        // scramble re-awards possession a frame or two later. Mirrors
        // StealTurnoverTest's _everLoose/_toucherAtSteal latch discipline.
        if (_ball.State == BallState.Loose)
        {
            if (!_everLoose)
            {
                _toucherAtBlock = _ball.LastToucherPeerIdForHarness;
                _everLoose = true;
            }
            if (!_velocityLatched)
            {
                _velocityAtBlock = _ball.VelocityForHarness;
                _velocityLatched = true;
            }
        }

        if (_scenario == "whiff")
        {
            TickWhiff();
            return;
        }

        TickSuccess();
    }

    // Where to begin the defender's BlockMove so its Active window lands in
    // the wanted relationship to the shot's vulnerable window
    // [releaseFrame, releaseFrame + BlockGraceTicks) (ADR-0018 §2). Derived
    // from LIVE tunables (JumpShot/BlockMove frame data, BlockGraceTicks), not
    // hardcoded, so this survives #104 retuning of any of them.
    //
    // CommittedMoveMachine.Tick() semantics (see that class's doc): Begin()
    // sets FrameInPhase=0 the SAME frame it's called; because Players ticks
    // BEFORE Ball each frame (matching production — see the class doc's
    // "Why a single offline instance is the server"), the FIRST Tick() call
    // also lands on that same begin frame, advancing FrameInPhase to 1. A
    // move's Active phase is therefore entered on frame
    // (beginFrame + StartupFrames - 1) with FrameInPhase reset to 0 there —
    // the same arithmetic StealTurnoverTest's ComputeBeginFrame doc derives
    // for the dribble-phase case, specialized here to a move's own Startup
    // count instead of a periodic dribble cycle.
    private int ComputeDefenderBeginFrame(int releaseFrame)
    {
        int blockStartup = BlockMove.DefaultFrameData.StartupFrames;
        int graceTicks = _ball.BlockGraceTicks;

        if (_scenario == "whiff")
            // Active entry lands ONE TICK PAST the vulnerable window's
            // exclusive end (releaseFrame + graceTicks) — guarantees
            // DefensiveResolution.Succeeds' half-open-interval overlap test
            // can never be true (defActiveStart < vulnEnd fails outright).
            return (releaseFrame + graceTicks + 1) - (blockStartup - 1);

        // "success": Active entry lands EXACTLY on the release tick itself —
        // guarantees an overlap (defActiveStart == vulnStart).
        return releaseFrame - (blockStartup - 1);
    }

    private void TickSuccess()
    {
        // Wait for the FULL turnover to complete — not merely that the ball
        // was ever observed Loose (see StealTurnoverTest's "anti-hollow-green"
        // rationale for why _everLoose alone is insufficient). Since #193,
        // AwardPossession settles the loose-ball scramble into a fresh, live
        // Held possession, not Dribbling.
        bool settled = _everLoose && _ball.State == BallState.Held && _ball.StateMachine.HolderPeerId != 0;

        if (settled)
        {
            Verdict();
            return;
        }

        if (_elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame} waiting for the block's loose-ball scramble to settle. " +
                 $"everLoose={_everLoose}, state={_ball.State}, defenderBegun={_defenderBegun}, releaseFrame={_releaseFrame}.");
            Finish();
        }
    }

    private void TickWhiff()
    {
        // No discrete "the shot is definitely safe" event exists to wait for
        // cheaply (the shot's natural flight is tens of ticks long) — instead
        // wait past the point the defender's own Active window closes (a
        // computable, non-guessed boundary: their Active started at
        // _defenderBeginFrame + StartupFrames - 1 and runs ActiveFrames
        // ticks), plus a small margin, then assert the ABSENCE of a block
        // this whole time. This is legitimate per ADR-0016 (asserting real
        // engine state), unlike guessing when a DIFFERENT event will occur.
        if (!_defenderBegun)
        {
            if (_elapsed > TimeoutSeconds)
            {
                Fail($"timed out at frame {_frame} before the defender's scheduled begin frame {_defenderBeginFrame}.");
                Finish();
            }
            return;
        }

        int blockStartup = BlockMove.DefaultFrameData.StartupFrames;
        int blockActive = BlockMove.DefaultFrameData.ActiveFrames;
        int defenderActiveEnd = _defenderBeginFrame + (blockStartup - 1) + blockActive;
        int verdictFrame = defenderActiveEnd + VerdictMarginFrames;

        if (_frame >= verdictFrame)
        {
            Verdict();
            return;
        }

        if (_elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame} before reaching verdict frame {verdictFrame}.");
            Finish();
        }
    }

    private void Verdict()
    {
        if (_scenario == "whiff")
        {
            VerdictWhiff();
            return;
        }

        VerdictSuccess();
    }

    private void VerdictSuccess()
    {
        bool turnoverCompleted = _everLoose && _ball.State == BallState.Held && _ball.StateMachine.HolderPeerId != 0;

        // Last toucher (#118 rule, mirrors the steal harness's discriminator):
        // the defender, not the shooter, must be charged as the last toucher
        // the instant the ball goes Loose — otherwise a swatted ball that
        // sails OOB before the scramble recovers it would wrongly hand the
        // turnover back to the offense.
        bool toucherCorrect = _toucherAtBlock == 2;

        // No basket registered for the blocked arc (make-detection path
        // unreachable): with ShotScatterEnabled disabled, an UNBLOCKED shot
        // from this setup is a guaranteed clean make — so both scores
        // remaining 0 after the scramble has fully settled is real proof the
        // block intercepted the shot before TickInFlight's make-detection
        // ever ran, not a lucky miss.
        bool scoreUnchanged = _gameManager.ScoreOf(1) == 0 && _gameManager.ScoreOf(2) == 0;

        // Velocity after the block is the swat (away from the rim / downward),
        // not the original shot velocity (toward the rim). ShooterPosition's Z
        // (+5) is far enough from RimCenter's Z (0) that the swat's Z
        // component must be POSITIVE (away from the rim, back toward the
        // shooter) — the original shot velocity would have had a NEGATIVE Z
        // component (toward the rim at Z=0). The swat is also DOWNWARD
        // (Y < 0), contrasting the shot's rising arc at this early point in
        // flight.
        bool velocityIsSwat = _velocityLatched && _velocityAtBlock.Z > 0f && _velocityAtBlock.Y < 0f;

        // Spent once: the block resolved the defender's Active phase early
        // (EndResolvedDefensiveMove -> CommittedMoveMachine.EndActiveEarly),
        // so by verdict time the defender's machine must be in Recovery (not
        // still Active, and not yet back to Inactive — RecoveryFrames is 20
        // ticks, comfortably longer than the settle margin this verdict waits
        // on top of the block tick).
        bool defenderSpentOnce = _defender.PhaseForHarness == MovePhase.Recovery;

        bool pass = turnoverCompleted && toucherCorrect && scoreUnchanged && velocityIsSwat && defenderSpentOnce;

        if (pass)
        {
            GD.Print($"[block-turnover] PASS — scenario=success, finalState={_ball.State}, " +
                     $"holder={_ball.StateMachine.HolderPeerId}, toucherAtBlock={_toucherAtBlock}, " +
                     $"velocityAtBlock={_velocityAtBlock}, defenderPhase={_defender.PhaseForHarness}, " +
                     $"score1={_gameManager.ScoreOf(1)}, score2={_gameManager.ScoreOf(2)}.");
        }
        else
        {
            Fail($"scenario=success expected a completed turnover, toucherAtBlock=2, unchanged score, a swat " +
                 $"velocity (Z>0, Y<0), and defender Recovery, but got turnoverCompleted={turnoverCompleted}, " +
                 $"toucherAtBlock={_toucherAtBlock}, scoreUnchanged={scoreUnchanged} (score1={_gameManager.ScoreOf(1)}, " +
                 $"score2={_gameManager.ScoreOf(2)}), velocityAtBlock={_velocityAtBlock} " +
                 $"(latched={_velocityLatched}), defenderPhase={_defender.PhaseForHarness}.");
        }
        Finish(pass ? 0 : 1);
    }

    private void VerdictWhiff()
    {
        // The shot must proceed unblocked: no block-induced early transition
        // to Loose ever happened, and the ball is still InFlight (its natural
        // flight is far longer than this verdict's margin — see TickWhiff).
        bool shotProceeded = !_everLoose && _ball.State == BallState.InFlight;

        // The last toucher must be unchanged from the tipoff (peer 1) — the
        // defender's whiffed attempt touched nothing.
        bool toucherUnchanged = _ball.LastToucherPeerIdForHarness == 1;

        // The defender still pays Recovery for the whiff (ADR-0018 §3's
        // reaction-tilt asymmetry applies to a miss just as much as a hit) —
        // by the verdict frame (defenderActiveEnd + margin) their machine has
        // left Active either way, whether the block resolved (it must not
        // have here) or simply expired.
        bool defenderPaysRecovery = _defender.PhaseForHarness == MovePhase.Recovery;

        bool pass = shotProceeded && toucherUnchanged && defenderPaysRecovery;

        if (pass)
        {
            GD.Print($"[block-turnover] PASS — scenario=whiff, everLoose={_everLoose}, " +
                     $"finalState={_ball.State}, toucher={_ball.LastToucherPeerIdForHarness}, " +
                     $"defenderPhase={_defender.PhaseForHarness}.");
        }
        else
        {
            Fail($"scenario=whiff expected everLoose=false, state=InFlight, toucher=1, and defender Recovery, " +
                 $"but got everLoose={_everLoose}, state={_ball.State}, toucher={_ball.LastToucherPeerIdForHarness}, " +
                 $"defenderPhase={_defender.PhaseForHarness}.");
        }
        Finish(pass ? 0 : 1);
    }

    private void Fail(string message) => GD.PrintErr($"[block-turnover] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[block-turnover] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
