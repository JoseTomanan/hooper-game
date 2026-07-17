using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Player;
using Hooper.Moves;
using Hooper.Systems;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #229 (ADR-0022, ADR-0016): the
// layup / rim-finish committed move, end to end. Unit tests already pin the
// pure Layup frame-data contract (LayupTests.cs), the release-resolver
// extension (JumpShotReleaseResolverTests.cs), and the range-selection
// decision (LayupRangeResolverTests.cs). What they CANNOT reach is the live
// engine glue this harness proves:
//   1. A real Layup, begun through the SAME BeginCommittedMove choke point
//      production input reaches, actually releases the ball and scores
//      through the UNCHANGED ShotScatter -> ShotArc -> RimBackboard chain
//      (ADR-0009/ADR-0022's "same make model, no parallel formula" claim).
//   2. A correctly-timed BlockMove resolves against a Layup's Active window
//      exactly as it already does against a JumpShot (ADR-0018's
//      DefensiveResolution.Succeeds, reused verbatim — no new defensive
//      primitive per ADR-0022).
//   3. A mistimed BlockMove leaves the Layup completely unaffected — the
//      control this claim is checked against.
//
//   godot --headless --path . res://tests/integration/LayupTest.tscn -- --harness-scenario=uncontested-make
//   godot --headless --path . res://tests/integration/LayupTest.tscn -- --harness-scenario=block-success
//   godot --headless --path . res://tests/integration/LayupTest.tscn -- --harness-scenario=block-whiff
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "uncontested-make".
//
// ── Why a single offline instance is the server ───────────────────────────
// Same reasoning as BlockTurnoverTest/StealTurnoverTest/TripleThreatTest:
// with no MultiplayerPeer assigned, Godot uses OfflineMultiplayerPeer
// (is_server() hardcoded true, unique_id 1), so BallController.IsServer is
// true and BOTH player nodes' _machine.Tick() advances every physics frame
// regardless of role.
//
// ── Scenario "uncontested-make": clean layup, no defender ever attempts a block ──
// The shooter (peer "1", the tipoff holder) is positioned well inside
// LayupRange (2m from RimCenter's XZ, LayupRange default 4.0m) and begins a
// REAL Layup via BeginMoveForHarness — the SAME BeginCommittedMove path
// production input reaches (SampleMoveInput's shoot branch, once
// LayupRangeResolver selects it). ShotScatterEnabled is disabled so the
// shot's geometry is deterministic (aimTarget == ShotTarget == RimCenter
// unconditionally) — the same "clean make" setup BlockTurnoverTest's
// control-make scenario uses, proving the layup's release actually reaches
// RimBackboard's make detection and RegisterBasket, not merely that the
// move machine runs its frame data.
//
// ── Scenario "block-success": defender's Active overlaps the release grace window ──
// Same scheduling math as BlockTurnoverTest's "success", specialized to
// Layup's own (shorter) Startup instead of JumpShot's: the defender begins a
// REAL BlockMove via BeginMoveForHarness, timed so its Active window's very
// first tick lands exactly on the Layup's release tick. ADR-0018's full
// interval-overlap form (DefensiveResolution.Succeeds) reports an overlap on
// the first tick BallController evaluates it — proving Layup's vulnerable
// window ([_inFlightStartTick, _inFlightStartTick + BlockGraceTicks)) is
// computed and consumed identically to JumpShot's, since BallController
// never branches on which move produced the InFlight transition.
//
// ── A real scheduling consequence of Layup's shorter Startup ─────────────
// BlockMove.Startup (10) is LONGER than Layup.Startup (8) — unlike
// BlockTurnoverTest's JumpShot pairing (Startup 18), there is not enough
// runway between the shooter's begin tick and the release tick to fit the
// defender's own Startup AFTER the shooter has already begun. The exact
// scheduling formula this file uses (ComputeDefenderBeginFrame) derives a
// NEGATIVE offset from the shooter's begin frame for "block-success" — the
// defender must commit to the block BEFORE the shooter even begins their
// gather. This is not a harness artifact: a real defender can legitimately
// anticipate a quick finish and commit early, and if their timing is right,
// still stuffs it. ArmFrames is sized generously (15, not the minimal 2 the
// tipoff assignment itself needs) purely to give this pre-commit scheduling
// enough headroom to land on frame >= 1.
//
// ── Scenario "block-whiff": the control this claim is checked against ────────
// Identical shooter/defender setup to "block-success", but the defender's
// BlockMove is scheduled so its Active window lands entirely OUTSIDE the
// vulnerable window (mirrors BlockTurnoverTest's "whiff" placement: Active
// entry exactly on the vulnerable window's exclusive end). The layup must
// proceed completely unaffected and score — proving "block-success" isn't a
// vacuous pass (e.g. every Layup happens to whiff regardless of timing).
public partial class LayupTest : Node
{
    private const double TimeoutSeconds = 15.0;

    // Ticks before the shooter attempts to begin its Layup. Set well past
    // the ~2 ticks TryAssignTipoffHolder actually needs (unlike
    // BlockTurnoverTest's ArmFrames=2) so the "block-success" scenario's
    // defender pre-commit (see this class's doc — BlockMove.Startup exceeds
    // Layup.Startup, so the defender must begin BEFORE the shooter) always
    // has a reachable, positive begin frame regardless of exact frame-data
    // tuning.
    private const int ArmFrames = 15;

    private const int VerdictMarginFrames = 6;

    // Well inside LayupRange (4.0m default) so LayupRangeResolver's own
    // < comparison is not itself under test here (that's
    // LayupRangeResolverTests' job) — this harness proves the ENGINE GLUE a
    // real Layup drives once begun, not the selection threshold.
    private static readonly Vector3 ShooterPosition = new(0f, 0f, 2f);

    // 1m from ShooterPosition — comfortably inside the default 2.2m
    // BlockReachRadius (issue #214), same positioning discipline
    // BlockTurnoverTest's DefenderPositionInRange uses, so a reach-gate
    // regression cannot silently explain either block scenario's outcome.
    private static readonly Vector3 DefenderPosition = new(1f, 0f, 2f);

    private string _scenario = "uncontested-make";
    private bool IsBlockScenario => _scenario == "block-success" || _scenario == "block-whiff";

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
    private int _predictedReleaseFrame = -1;
    private int _defenderBeginFrame;

    private bool _everLoose;
    private int _toucherAtBlock = -1;
    private Vector3 _velocityAtBlock;
    private bool _velocityLatched;
    private bool _defenderRecoveryAtBlock;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "uncontested-make");
        GD.Print($"[layup] scenario={_scenario} booting headless…");

        var players = new Node3D { Name = "Players" };
        _shooter  = new PlayerController { Name = "1" };
        _defender = new PlayerController { Name = "2" };
        players.AddChild(_shooter);
        players.AddChild(_defender);

        _defender.GlobalPosition = DefenderPosition;

        _ball = new BallController { Name = "Ball", Players = players };

        // Distance-based shot scatter (ADR-0009) would otherwise draw a
        // seeded-RNG offset even for a "clean" aim — disabling it makes an
        // unblocked layup a GUARANTEED clean make, exactly like
        // BlockTurnoverTest's control-make setup, so "scores" / "score
        // unchanged" verdicts below are real proof, not luck.
        _ball.ShotScatterEnabled = false;

        // Matches BlockTurnoverTest's explicit override (issue #217/#218) —
        // kept self-documenting rather than relying on the raw code default.
        _ball.BoardCenter = new Vector3(0f, 3.205f, -0.27f);

        _gameManager = new GameManager { Name = "GameManager" };

        AddChild(players);      // matches scenes/Main.tscn: Players before Ball
        AddChild(_ball);
        AddChild(_gameManager);

        // The defender's begin frame does NOT depend on runtime state — it
        // is derived entirely from the FIXED ArmFrames constant (the
        // shooter's assumed begin frame, verified against reality at
        // ArmFrames in _PhysicsProcess) plus the live Layup/BlockMove frame
        // data — so it can be computed once, here, rather than lazily after
        // the shooter actually begins (which would be too late: see this
        // class's doc on why the defender must sometimes pre-commit BEFORE
        // the shooter's own begin tick).
        if (IsBlockScenario)
        {
            int layupStartup = Layup.DefaultFrameData.StartupFrames;
            int predictedReleaseFrame = ArmFrames + (layupStartup - 1);
            _defenderBeginFrame = ComputeDefenderBeginFrame(predictedReleaseFrame);

            if (_defenderBeginFrame < 1)
            {
                Fail($"computed defender begin frame {_defenderBeginFrame} is before frame 1 — a frame-data " +
                     $"change (Layup/BlockMove Startup or BlockGraceTicks) made this scenario's placement " +
                     $"unschedulable. Increase ArmFrames or re-derive the scheduling for the new tunables.");
                Finish();
            }
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        // Step 0 (block scenarios only): the defender's begin frame was
        // precomputed in _Ready() from the FIXED ArmFrames constant, because
        // — as this class's doc explains — "block-success" requires the
        // defender to pre-commit BEFORE the shooter's own begin tick
        // (BlockMove.Startup exceeds Layup.Startup). This trigger is
        // therefore deliberately NOT gated on _shooterBegun.
        if (IsBlockScenario && !_defenderBegun && _frame == _defenderBeginFrame)
        {
            bool defenderBegan = _defender.BeginMoveForHarness(new BlockMove());
            _defenderBegun = true;
            if (!defenderBegan)
            {
                Fail("BeginMoveForHarness(BlockMove) returned false — defender's machine was not Inactive at begin.");
                Finish();
                return;
            }
            GD.Print($"[layup] frame {_frame}: defender begun BlockMove.");
        }

        // Step 1: once the tipoff has assigned a holder, begin the shooter's
        // REAL Layup through the SAME BeginCommittedMove path production
        // input uses.
        if (!_shooterBegun && _frame >= ArmFrames)
        {
            if (_ball.StateMachine.HolderPeerId != 1)
            {
                Fail($"expected the tipoff to award peer 1 (this code-built tree's first child); got {_ball.StateMachine.HolderPeerId}.");
                Finish();
                return;
            }

            bool began = _shooter.BeginMoveForHarness(new Layup());
            _shooterBegun = true;
            if (!began)
            {
                Fail("BeginMoveForHarness(Layup) returned false — shooter's machine was not Inactive at begin.");
                Finish();
                return;
            }

            _shooterBeginFrame = _frame;

            // Same premise-guard as BlockTurnoverTest: an uncleared make
            // routes to ADR-0008's take-it-back branch (no points, turnover),
            // which would be indistinguishable from a block by score alone.
            if (!_ball.IsCleared)
            {
                Fail($"the tipoff possession is not cleared (IsCleared=false) at shot begin — the make " +
                     $"would be swallowed by the take-it-back rule (ADR-0008), so no scoring assertion in " +
                     $"this scenario can mean anything.");
                Finish();
                return;
            }

            _shooter.GlobalPosition = ShooterPosition;

            // Predicted release frame from the LIVE Layup frame data, not
            // hardcoded — survives any future tuning of Layup's Startup.
            // Also sanity-checks the fixed-ArmFrames assumption Step 0's
            // precomputed _defenderBeginFrame relies on: _shooterBeginFrame
            // must equal ArmFrames exactly (guaranteed by this block's own
            // `_frame >= ArmFrames` gate combined with the tipoff already
            // having resolved by then), so no separate assertion is added
            // here beyond that structural guarantee.
            int layupStartup = Layup.DefaultFrameData.StartupFrames;
            _predictedReleaseFrame = _shooterBeginFrame + (layupStartup - 1);

            if (IsBlockScenario)
            {
                GD.Print($"[layup] frame {_frame}: shooter begun Layup (predicted release frame " +
                         $"{_predictedReleaseFrame}); defender {(_defenderBegun ? "already begun" : "scheduled")} " +
                         $"at frame {_defenderBeginFrame}.");
            }
        }

        if (_ball.State == BallState.Loose)
        {
            if (!_everLoose)
            {
                _toucherAtBlock = _ball.LastToucherPeerIdForHarness;
                _defenderRecoveryAtBlock = _defender.PhaseForHarness == MovePhase.Recovery;
                _everLoose = true;
            }
            if (!_velocityLatched)
            {
                _velocityAtBlock = _ball.VelocityForHarness;
                _velocityLatched = true;
            }
        }

        switch (_scenario)
        {
            case "uncontested-make":
                TickMake();
                return;
            case "block-success":
                TickBlockSuccess();
                return;
            case "block-whiff":
                TickBlockWhiff();
                return;
            default:
                Fail($"unknown scenario '{_scenario}'.");
                Finish();
                return;
        }
    }

    // Same derivation discipline as BlockTurnoverTest.ComputeDefenderBeginFrame,
    // specialized to Layup's Startup instead of JumpShot's.
    private int ComputeDefenderBeginFrame(int releaseFrame)
    {
        int blockStartup = BlockMove.DefaultFrameData.StartupFrames;
        int graceTicks = _ball.BlockGraceTicks;

        if (_scenario == "block-whiff")
            // Active entry lands EXACTLY on the vulnerable window's exclusive
            // end — the first tick the half-open interval [vulnStart, vulnEnd)
            // excludes (same boundary pin as BlockTurnoverTest's "whiff").
            return (releaseFrame + graceTicks) - (blockStartup - 1);

        // "block-success": Active entry lands exactly on the release tick —
        // guarantees a timing overlap.
        return releaseFrame - (blockStartup - 1);
    }

    private int ComputeUnblockedMakeTicks()
    {
        float tTotal = ShotArc.ComputeFlightTime(
            releaseY:   _ball.DribbleHandHeight,
            targetY:    _ball.RimCenter.Y,
            apexHeight: _ball.ShotApexHeight,
            gravity:    _ball.Gravity);
        return Mathf.CeilToInt(tTotal * Engine.PhysicsTicksPerSecond);
    }

    private void TickMake()
    {
        if (_gameManager.ScoreOf(1) > 0)
        {
            VerdictMake();
            return;
        }
        if (_elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame} waiting for the uncontested layup to score. " +
                 $"state={_ball.State}, score1={_gameManager.ScoreOf(1)}.");
            Finish();
        }
    }

    private void VerdictMake()
    {
        bool pass = _gameManager.ScoreOf(1) == 1 && _gameManager.ScoreOf(2) == 0;
        if (pass)
        {
            GD.Print($"[layup] PASS — scenario=uncontested-make, score1={_gameManager.ScoreOf(1)}, " +
                     $"score2={_gameManager.ScoreOf(2)}.");
        }
        else
        {
            Fail($"expected the uncontested layup to score exactly once, but got score1={_gameManager.ScoreOf(1)}, " +
                 $"score2={_gameManager.ScoreOf(2)}.");
        }
        Finish(pass ? 0 : 1);
    }

    private void TickBlockSuccess()
    {
        bool settled = _everLoose && _ball.State == BallState.Held && _ball.StateMachine.HolderPeerId != 0;
        if (settled)
        {
            VerdictBlockSuccess();
            return;
        }
        if (_elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame} waiting for the block's loose-ball scramble to settle. " +
                 $"everLoose={_everLoose}, state={_ball.State}, defenderBegun={_defenderBegun}, " +
                 $"predictedReleaseFrame={_predictedReleaseFrame}.");
            Finish();
        }
    }

    private void VerdictBlockSuccess()
    {
        bool turnoverCompleted = _everLoose && _ball.State == BallState.Held && _ball.StateMachine.HolderPeerId != 0;
        bool toucherCorrect = _toucherAtBlock == 2;
        bool scoreUnchanged = _gameManager.ScoreOf(1) == 0 && _gameManager.ScoreOf(2) == 0;
        bool velocityIsSwat = _velocityLatched && _velocityAtBlock.Z > 0f && _velocityAtBlock.Y < 0f;
        bool defenderSpentOnceLatched = _defenderRecoveryAtBlock;

        bool pass = turnoverCompleted && toucherCorrect && scoreUnchanged && velocityIsSwat && defenderSpentOnceLatched;

        if (pass)
        {
            GD.Print($"[layup] PASS — scenario=block-success, finalState={_ball.State}, " +
                     $"toucherAtBlock={_toucherAtBlock}, velocityAtBlock={_velocityAtBlock}, " +
                     $"defenderRecoveryAtBlock={_defenderRecoveryAtBlock}, score1={_gameManager.ScoreOf(1)}, " +
                     $"score2={_gameManager.ScoreOf(2)}.");
        }
        else
        {
            Fail($"scenario=block-success expected a completed turnover, toucherAtBlock=2, unchanged score, a " +
                 $"swat velocity (Z>0, Y<0), and defender Recovery at block time, but got " +
                 $"turnoverCompleted={turnoverCompleted}, toucherAtBlock={_toucherAtBlock}, " +
                 $"scoreUnchanged={scoreUnchanged} (score1={_gameManager.ScoreOf(1)}, score2={_gameManager.ScoreOf(2)}), " +
                 $"velocityAtBlock={_velocityAtBlock} (latched={_velocityLatched}), " +
                 $"defenderRecoveryAtBlock={_defenderRecoveryAtBlock}.");
        }
        Finish(pass ? 0 : 1);
    }

    private void TickBlockWhiff()
    {
        if (!_defenderBegun)
        {
            if (_elapsed > TimeoutSeconds)
            {
                Fail($"timed out at frame {_frame} before the defender's scheduled begin frame {_defenderBeginFrame}.");
                Finish();
            }
            return;
        }

        // The layup must be COMPLETELY unaffected — proceed to a made basket,
        // not merely "still InFlight past the defender's window" (a stronger
        // proof than BlockTurnoverTest's "whiff", which only needs to
        // outlast a natural flight far longer than the verdict margin —
        // here a made shot IS the natural, expected outcome, so waiting for
        // it directly is both simpler and stronger).
        if (_gameManager.ScoreOf(1) > 0)
        {
            VerdictBlockWhiff();
            return;
        }

        if (_elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame} waiting for the mistimed-block layup to score. " +
                 $"everLoose={_everLoose}, state={_ball.State}, score1={_gameManager.ScoreOf(1)}.");
            Finish();
        }
    }

    private void VerdictBlockWhiff()
    {
        bool neverBlocked = !_everLoose;
        bool scored = _gameManager.ScoreOf(1) == 1 && _gameManager.ScoreOf(2) == 0;

        // The defender still pays Recovery for the whiff — ADR-0018 §3's
        // reaction-tilt asymmetry applies to a miss regardless of which shot
        // it whiffed against.
        bool defenderPaidRecovery = _defender.PhaseForHarness == MovePhase.Recovery
            || _defender.PhaseForHarness == MovePhase.Inactive; // may have already expired by the time the shot scores

        bool pass = neverBlocked && scored && defenderPaidRecovery;

        if (pass)
        {
            GD.Print($"[layup] PASS — scenario=block-whiff, everLoose={_everLoose}, " +
                     $"score1={_gameManager.ScoreOf(1)}, score2={_gameManager.ScoreOf(2)}, " +
                     $"defenderPhase={_defender.PhaseForHarness}.");
        }
        else
        {
            Fail($"scenario=block-whiff expected the layup to score unaffected, but got everLoose={_everLoose}, " +
                 $"score1={_gameManager.ScoreOf(1)}, score2={_gameManager.ScoreOf(2)}, " +
                 $"defenderPhase={_defender.PhaseForHarness}.");
        }
        Finish(pass ? 0 : 1);
    }

    private void Fail(string message) => GD.PrintErr($"[layup] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[layup] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
