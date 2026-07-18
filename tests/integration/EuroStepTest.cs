using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;
using Hooper.Systems;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #231 (ADR-0022, ADR-0016): the
// euro-step — a lateral evasive gather-step that displaces the finish's launch
// past an on-ball defender. Unit tests already pin the pure lateral-composition
// math (EuroStepMathTests.cs), the input read (EuroStepReadResolverTests.cs),
// and the frame-data contract (via EuroStep.DefaultFrameData). What they CANNOT
// reach is the live engine glue this harness proves:
//   1. A REAL euro-step, begun through the SAME BeginCommittedMove choke point
//      production input reaches, physically displaces the ball-handler LATERALLY
//      off the straight drive line (the evasion actually happens), and then
//      chains into #229's Layup from that displaced spot (the proven
//      gather-to-layup-chain pattern, applied to the lateral variant).
//   2. A euro-step whose displacement carries the finish OUTSIDE a committed
//      (planted) defender's reach finishes uncontested — the evasion beats a
//      defender who committed to the original drive line.
//   3. CONTROL: a defender who READS the step — repositions to the displaced
//      launch — still contests it (ADR-0018 block, reused verbatim). Without
//      this, "the euro-step scored" would pass even if euro-step finishes were
//      simply unblockable; the control proves the make is the EVASION, not a
//      broken block. (The issue bakes this in: "not a free auto-evade.")
//   4. A whiffed euro-step (no finish taken) leaves the player in a Recovery
//      window — an attempted finish DURING recovery is refused — not a free
//      cancel (ADR-0003 commitment cost).
//
//   godot --headless --path . res://tests/integration/EuroStepTest.tscn -- --harness-scenario=euro-step-beats-committed-defender
//   godot --headless --path . res://tests/integration/EuroStepTest.tscn -- --harness-scenario=euro-step-read-still-contests
//   godot --headless --path . res://tests/integration/EuroStepTest.tscn -- --harness-scenario=euro-step-whiff-recovery
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "euro-step-beats-committed-defender".
//
// ── Why a single offline instance is the server ───────────────────────────
// Same reasoning as LayupTest/DriveGatherTest/BlockTurnoverTest: with no
// MultiplayerPeer assigned, Godot uses OfflineMultiplayerPeer (is_server()
// hardcoded true, unique_id 1), so BallController.IsServer is true and peer
// "1"'s _machine.Tick() advances every physics frame as the HOST's own player
// (TickServerOwnPlayer) — which is what physically drives the euro-step's
// Active-window displacement through MoveAndSlide (the same path DriveGatherTest
// relies on to prove the straight drive plants the holder forward).
//
// ── Why the committed defender is PLANTED and the reading one REPOSITIONS ──
// The block resolves on BOTH a timing overlap (ADR-0018 DefensiveResolution.
// Succeeds) AND a spatial reach gate (#214, BlockReachRadius, default 2.2m). A
// realistic single lateral hop (~1m) is SMALLER than that reach radius, so a
// defender who TRACKS the ball-handler is always within reach — the euro-step
// cannot beat a tracking defender spatially, and its per-move timing shift is
// smaller than the block's Active+grace forgiveness window, so it cannot beat
// one temporally either. The faithful model of "beats a defender committed to
// the original drive line" is therefore a defender who PLANTED (committed their
// block to a fixed spot on the original line and did not follow the step): the
// euro-step's displacement carries the launch beyond that planted defender's
// reach. The CONTROL defender READS the step and repositions to the displaced
// launch, landing back inside reach — and still contests. Timing is held
// IDENTICAL (both blocks overlap the actual release), so the SOLE discriminator
// is whether the defender followed the step, which is exactly the issue's
// "committed to the original line" vs "reads the step."
//
// ── Anti-vacuous discipline (harness-code-defaults / #217 lesson) ─────────
// The committed defender's spot is DERIVED at runtime from the MEASURED
// displaced launch and the LIVE BlockReachRadius (never a hardcoded coordinate),
// and BOTH reach premises are asserted: the committed defender must be OUTSIDE
// reach of the displaced launch (else the whiff is not attributable to the
// evasion) yet INSIDE reach of the original drive line (else it never committed
// to anything). A #238 retune of EuroStepLateralHopSpeed re-derives this
// geometry instead of silently invalidating the scenario. ShotScatterEnabled is
// disabled so an uncontested finish is a GUARANTEED clean make (as in
// LayupTest), making "scores" a real verdict rather than luck.
public partial class EuroStepTest : Node
{
    private const double TimeoutSeconds = 20.0;
    private const int ArmFrames = 15;

    // The euro-step's body-relative step direction under test: +1 = to the
    // player's right. With the holder facing the rim at default Heading 0, the
    // player's right is world -X (HandStateResolver.BurstWorldDir(0,+1)==(-1,0)),
    // so the euro-step displaces the finish toward -X.
    private const int LateralSign = +1;

    // 2 ticks after the defender's block begins, we begin the layup, so the
    // layup's release (Layup.Startup-1 ticks later) lands on the block's
    // Active-entry — an overlap held IDENTICAL across the committed and reading
    // scenarios, so position is the only discriminator. Derived from live frame
    // data at use, not hardcoded (see BeginFinishAndBlock).
    private const int SmallInReachOffset = 1; // metres, defender-to-launch for the reading/tracking defender

    private string _scenario = "euro-step-beats-committed-defender";
    private bool IsContestScenario =>
        _scenario == "euro-step-beats-committed-defender"
        || _scenario == "euro-step-read-still-contests";

    private BallController _ball;
    private GameManager _gameManager;
    private PlayerController _shooter;  // peer "1" — the tipoff holder / ball-handler
    private PlayerController _defender; // peer "2"

    private int _frame;
    private double _elapsed;
    private bool _finished;

    private Vector3 _startPosition;      // the shooter's position before the euro-step (the original drive line)
    private Vector3 _displacedLaunch;    // measured once the euro-step returns to Inactive
    private bool _euroBegun;
    private int _euroBeginFrame = -1;

    private enum Step { AwaitTipoff, EuroRunning, FinishScheduled, Resolving }
    private Step _step = Step.AwaitTipoff;

    private int _blockBeginFrame = -1;
    private int _layupBeginFrame = -1;
    private bool _layupBegun;
    private bool _defenderBegun;

    // Turnover latches (contest scenarios).
    private bool _everLoose;
    private int _toucherAtBlock = -1;

    // Whiff-recovery latches.
    private bool _recoveryObserved;
    private bool _cancelRefusedDuringRecovery;
    private bool _cancelAttempted;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "euro-step-beats-committed-defender");
        GD.Print($"[euro-step] scenario={_scenario} booting headless…");

        var players = new Node3D { Name = "Players" };
        _shooter = new PlayerController { Name = "1" };
        _defender = new PlayerController { Name = "2" };
        players.AddChild(_shooter);
        players.AddChild(_defender);

        _ball = new BallController { Name = "Ball", Players = players };
        // Deterministic clean make when uncontested (ADR-0009 scatter off), same
        // as LayupTest — so a "scores" verdict is proof, not luck.
        _ball.ShotScatterEnabled = false;
        _ball.BoardCenter = new Vector3(0f, 3.205f, -0.27f); // matches LayupTest/BlockTurnoverTest override

        _gameManager = new GameManager { Name = "GameManager" };

        AddChild(players);   // matches scenes/Main.tscn: Players before Ball
        AddChild(_ball);
        AddChild(_gameManager);

        // Park the defender far away initially so it can never incidentally be
        // in reach before it is deliberately positioned per scenario. Must run
        // AFTER AddChild: GlobalPosition derives from the global transform, which
        // only exists once the node is inside the tree — set before parenting it
        // is silently dropped (Godot's !is_inside_tree() guard).
        _defender.GlobalPosition = new Vector3(50f, 0f, 50f);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        if (_scenario == "euro-step-whiff-recovery")
        {
            TickWhiffRecovery();
        }
        else if (IsContestScenario)
        {
            TickContest();
        }
        else
        {
            Fail($"unknown scenario '{_scenario}'.");
            Finish();
            return;
        }

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame}, scenario={_scenario}, step={_step}.");
            Finish();
        }
    }

    // ── Begin the euro-step (shared by all scenarios) ─────────────────────────
    // Returns true once begun. Positions the holder on the straight line to the
    // rim, facing it (default Heading 0), with a live dribble (the euro-step
    // shares DriveGather's dead-dribble gate, so a fresh dead-Held tipoff must
    // be turned into a live dribble first — same setup every gated move's harness
    // uses).
    private bool TryBeginEuroStep()
    {
        if (_ball.StateMachine.HolderPeerId != 1)
        {
            Fail($"expected the tipoff to award peer 1 (this code-built tree's first child); got {_ball.StateMachine.HolderPeerId}.");
            Finish();
            return false;
        }

        _ball.TryStartDribble(1);
        // 3m out on the straight line to the rim (+Z). Comfortably inside the
        // default LayupRange so the chained finish is unambiguously a layup; the
        // euro-step's lateral displacement — not the range gate (LayupTest's job)
        // — is what this harness proves.
        _startPosition = new Vector3(_ball.RimCenter.X, 0f, _ball.RimCenter.Z - 3f);
        _shooter.GlobalPosition = _startPosition;

        bool began = _shooter.BeginMoveForHarness(new EuroStep(LateralSign));
        if (!began)
        {
            Fail("BeginMoveForHarness(EuroStep) returned false — machine was not Inactive at begin.");
            Finish();
            return false;
        }
        _euroBeginFrame = _frame;
        GD.Print($"[euro-step] frame {_frame}: begun EuroStep(sign={LateralSign}) at {_startPosition}.");
        return true;
    }

    // ── Scenarios: beats-committed-defender / read-still-contests ─────────────
    private void TickContest()
    {
        switch (_step)
        {
            case Step.AwaitTipoff:
                if (_frame < ArmFrames) return;
                if (!TryBeginEuroStep()) return;
                _euroBegun = true;
                _step = Step.EuroRunning;
                return;

            case Step.EuroRunning:
                // Wait for the euro-step's full Startup->Active->Recovery lifecycle
                // to complete. Its Active window has by then physically displaced
                // the holder laterally off the original line.
                if (_shooter.PhaseForHarness != MovePhase.Inactive) return;

                _displacedLaunch = _shooter.GlobalPosition;
                float lateral = Mathf.Abs(_displacedLaunch.X - _startPosition.X);
                GD.Print($"[euro-step] frame {_frame}: EuroStep complete — displaced launch {_displacedLaunch} " +
                         $"(lateral offset {lateral:F2}m from the original line).");

                // Premise: the euro-step actually ran its lateral beat. A zero
                // lateral offset would mean the displacement never happened and
                // every downstream reach comparison would be meaningless.
                if (lateral < 0.1f)
                {
                    Fail($"the euro-step produced no lateral displacement ({lateral:F2}m) — its Active beat did not " +
                         $"run, so the evasion under test never occurred.");
                    Finish();
                    return;
                }

                // Premise: the finish is still a layup from the displaced spot.
                float distToRim = Mathf.Sqrt(DefensiveResolution.DistanceXZSquared(_displacedLaunch, _ball.RimCenter));
                if (!(distToRim < _ball.LayupRange))
                {
                    Fail($"the displaced launch is {distToRim:F2}m from the rim, outside LayupRange " +
                         $"({_ball.LayupRange}) — the chained finish would not be a layup. Re-derive the start " +
                         $"distance for the tuned frame data (#238).");
                    Finish();
                    return;
                }

                PositionDefenderForScenario();
                BeginFinishAndBlock();
                _step = Step.FinishScheduled;
                return;

            case Step.FinishScheduled:
                // Begin the layup a fixed, live-frame-data-derived offset after
                // the block, so its release overlaps the block's Active window.
                if (!_layupBegun && _frame >= _layupBeginFrame)
                {
                    bool layupBegan = _shooter.BeginMoveForHarness(new Layup());
                    _layupBegun = true;
                    if (!layupBegan)
                    {
                        Fail("BeginMoveForHarness(Layup) returned false — the chained finish could not begin from the displaced spot.");
                        Finish();
                        return;
                    }
                    GD.Print($"[euro-step] frame {_frame}: chained Layup begun from the displaced launch.");
                    _step = Step.Resolving;
                }
                return;

            case Step.Resolving:
                LatchLoose();
                ResolveContest();
                return;
        }
    }

    // Position the defender per scenario, deriving everything from the MEASURED
    // displaced launch and the LIVE BlockReachRadius so a #238 hop retune
    // re-derives the geometry (never a hardcoded coordinate).
    private void PositionDefenderForScenario()
    {
        float reach = _ball.BlockReachRadius;

        if (_scenario == "euro-step-read-still-contests")
        {
            // The reading defender repositions to the displaced launch — a small
            // fixed offset toward +X keeps it comfortably inside reach.
            _defender.GlobalPosition = _displacedLaunch + new Vector3(SmallInReachOffset, 0f, 0f);
            float d = Mathf.Sqrt(DefensiveResolution.DistanceXZSquared(_defender.GlobalPosition, _displacedLaunch));
            GD.Print($"[euro-step] reading defender placed {d:F2}m from the displaced launch (reach {reach}m).");
            return;
        }

        // "euro-step-beats-committed-defender": a PLANTED defender committed to
        // the original drive line. Place it along the direction from the
        // displaced launch back toward the original-line finish, at (reach +
        // margin) from the displaced launch — guaranteeing it is OUTSIDE reach
        // of where the euro-step actually finished, while landing back near the
        // original line it committed to.
        Vector3 straightFinish = new(_startPosition.X, 0f, _displacedLaunch.Z); // same forward progress, no lateral step
        Vector3 towardOriginal = (straightFinish - _displacedLaunch);
        towardOriginal.Y = 0f;
        towardOriginal = towardOriginal.LengthSquared() > 0.0001f ? towardOriginal.Normalized() : new Vector3(1f, 0f, 0f);
        _defender.GlobalPosition = _displacedLaunch + towardOriginal * (reach + 0.4f);
    }

    // Begin the defender's block now and schedule the layup so its release
    // overlaps the block's Active window (timing held identical across both
    // contest scenarios — see class doc). Frame offsets are derived from LIVE
    // frame data so a retune re-derives the schedule.
    private void BeginFinishAndBlock()
    {
        bool defenderBegan = _defender.BeginMoveForHarness(new BlockMove());
        _defenderBegun = true;
        if (!defenderBegan)
        {
            Fail("BeginMoveForHarness(BlockMove) returned false — defender's machine was not Inactive at begin.");
            Finish();
            return;
        }
        _blockBeginFrame = _frame;

        // Block Active-entry lands at _blockBeginFrame + (BlockStartup-1); the
        // layup's release lands at layupBegin + (LayupStartup-1). Choosing
        // layupBegin so those coincide overlaps the block's Active window with
        // the release, guaranteeing a timing overlap (ADR-0018) — leaving the
        // defender's POSITION as the sole discriminator between the two contest
        // scenarios.
        int blockActiveEntry = _blockBeginFrame + (BlockMove.DefaultFrameData.StartupFrames - 1);
        _layupBeginFrame = blockActiveEntry - (Layup.DefaultFrameData.StartupFrames - 1);
        if (_layupBeginFrame <= _frame)
            _layupBeginFrame = _frame + 1; // never schedule the finish in the past

        GD.Print($"[euro-step] frame {_frame}: defender begun BlockMove; layup scheduled for frame {_layupBeginFrame} " +
                 $"(block Active-entry ~{blockActiveEntry}).");
    }

    private void LatchLoose()
    {
        if (_ball.State == BallState.Loose && !_everLoose)
        {
            _toucherAtBlock = _ball.LastToucherPeerIdForHarness;
            _everLoose = true;
        }
    }

    private void ResolveContest()
    {
        if (_scenario == "euro-step-beats-committed-defender")
        {
            // The committed (planted, out-of-reach) defender cannot contest the
            // displaced finish: it scores, and the ball never went loose from a
            // block. The premise assertions below make the make attributable to
            // the EVASION (out of reach) rather than to a mistimed block.
            if (_gameManager.ScoreOf(1) > 0)
            {
                VerdictBeatsCommitted();
                return;
            }
            if (_everLoose)
            {
                Fail($"the committed defender blocked the euro-step finish (ball went loose, toucher " +
                     $"{_toucherAtBlock}) — the displacement did not evade its reach. displacedLaunch=" +
                     $"{_displacedLaunch}, defender={_defender.GlobalPosition}, reach={_ball.BlockReachRadius}.");
                Finish();
            }
            return;
        }

        // "euro-step-read-still-contests": the reading defender's in-reach,
        // overlapping block turns the finish over — proving the euro-step is not
        // a free auto-evade.
        bool settled = _everLoose && _ball.State == BallState.Held && _ball.StateMachine.HolderPeerId != 0;
        if (settled)
        {
            VerdictReadContests();
            return;
        }
        if (_gameManager.ScoreOf(1) > 0)
        {
            Fail($"the reading defender FAILED to contest — the euro-step finish scored despite the defender " +
                 $"repositioning to the displaced launch and timing the block to the release. This is the " +
                 $"'free auto-evade' the control exists to rule out. defender={_defender.GlobalPosition}, " +
                 $"displacedLaunch={_displacedLaunch}, reach={_ball.BlockReachRadius}.");
            Finish();
        }
    }

    private void VerdictBeatsCommitted()
    {
        float distCommittedToDisplaced = Mathf.Sqrt(
            DefensiveResolution.DistanceXZSquared(_defender.GlobalPosition, _displacedLaunch));
        Vector3 straightFinish = new(_startPosition.X, 0f, _displacedLaunch.Z);
        float distCommittedToOriginal = Mathf.Sqrt(
            DefensiveResolution.DistanceXZSquared(_defender.GlobalPosition, straightFinish));
        float reach = _ball.BlockReachRadius;

        bool scored = _gameManager.ScoreOf(1) == 1 && _gameManager.ScoreOf(2) == 0;
        bool neverBlocked = !_everLoose;
        bool evadedReach = distCommittedToDisplaced > reach;          // out of reach of the actual finish
        bool committedToLine = distCommittedToOriginal <= reach;      // yet in reach of the original line

        bool pass = scored && neverBlocked && evadedReach && committedToLine;
        if (pass)
        {
            GD.Print($"[euro-step] PASS — scenario=euro-step-beats-committed-defender: the displaced finish " +
                     $"scored (score1={_gameManager.ScoreOf(1)}), never blocked. Committed defender was " +
                     $"{distCommittedToDisplaced:F2}m from the displaced launch (>reach {reach}m: evaded) but " +
                     $"{distCommittedToOriginal:F2}m from the original line (<=reach: genuinely committed).");
            Finish(0);
        }
        else
        {
            Fail($"expected an uncontested make by a committed-but-evaded defender; scored={scored} " +
                 $"(score1={_gameManager.ScoreOf(1)}, score2={_gameManager.ScoreOf(2)}), neverBlocked={neverBlocked}, " +
                 $"evadedReach={evadedReach} (distToDisplaced={distCommittedToDisplaced:F2}m), " +
                 $"committedToLine={committedToLine} (distToOriginal={distCommittedToOriginal:F2}m), reach={reach}m.");
            Finish();
        }
    }

    private void VerdictReadContests()
    {
        float distToDisplaced = Mathf.Sqrt(
            DefensiveResolution.DistanceXZSquared(_defender.GlobalPosition, _displacedLaunch));
        bool turnoverCompleted = _everLoose && _ball.State == BallState.Held && _ball.StateMachine.HolderPeerId != 0;
        bool toucherCorrect = _toucherAtBlock == 2;
        bool scoreUnchanged = _gameManager.ScoreOf(1) == 0 && _gameManager.ScoreOf(2) == 0;
        bool inReach = distToDisplaced <= _ball.BlockReachRadius;

        bool pass = turnoverCompleted && toucherCorrect && scoreUnchanged && inReach;
        if (pass)
        {
            GD.Print($"[euro-step] PASS — scenario=euro-step-read-still-contests: a defender that read the step " +
                     $"(repositioned {distToDisplaced:F2}m from the displaced launch, <=reach " +
                     $"{_ball.BlockReachRadius}m) contested it — turnover to peer {_toucherAtBlock}, score " +
                     $"unchanged. The euro-step is not a free auto-evade.");
            Finish(0);
        }
        else
        {
            Fail($"expected the reading defender to force a turnover; turnoverCompleted={turnoverCompleted}, " +
                 $"toucherCorrect={toucherCorrect} (toucher={_toucherAtBlock}), scoreUnchanged={scoreUnchanged} " +
                 $"(score1={_gameManager.ScoreOf(1)}, score2={_gameManager.ScoreOf(2)}), inReach={inReach} " +
                 $"(distToDisplaced={distToDisplaced:F2}m, reach={_ball.BlockReachRadius}m).");
            Finish();
        }
    }

    // ── Scenario: euro-step-whiff-recovery ───────────────────────────────────
    // A euro-step taken with no finish available must still cost a Recovery
    // window — the player cannot instantly cancel out of it into another action.
    private void TickWhiffRecovery()
    {
        if (!_euroBegun)
        {
            if (_frame < ArmFrames) return;
            if (!TryBeginEuroStep()) return;
            _euroBegun = true;
            _startPosition = _shooter.GlobalPosition;
            return;
        }

        MovePhase phase = _shooter.PhaseForHarness;

        // Prove "not a free cancel": the FIRST time we observe Recovery, attempt
        // to begin a finish (a Layup — not dead-dribble-gated, so a refusal here
        // is attributable to the Recovery phase, not to ball state). It must be
        // refused because the machine is not Inactive.
        if (phase == MovePhase.Recovery)
        {
            _recoveryObserved = true;
            if (!_cancelAttempted)
            {
                bool finishBegan = _shooter.BeginMoveForHarness(new Layup());
                _cancelRefusedDuringRecovery = !finishBegan;
                _cancelAttempted = true;
                GD.Print($"[euro-step] frame {_frame}: mid-Recovery finish attempt " +
                         $"{(finishBegan ? "BEGAN (BUG — free cancel)" : "refused (correct)")}.");
            }
            return;
        }

        // Once the euro-step has fully returned to Inactive, render the verdict.
        if (_recoveryObserved && phase == MovePhase.Inactive)
        {
            float lateral = Mathf.Abs(_shooter.GlobalPosition.X - _startPosition.X);
            bool pass = _recoveryObserved && _cancelRefusedDuringRecovery && lateral >= 0.1f;
            if (pass)
            {
                GD.Print($"[euro-step] PASS — scenario=euro-step-whiff-recovery: the whiffed euro-step passed " +
                         $"through a Recovery window (lateral commitment {lateral:F2}m), and a finish attempted " +
                         $"during Recovery was refused — not a free cancel.");
                Finish(0);
            }
            else
            {
                Fail($"expected a Recovery window with no free cancel; recoveryObserved={_recoveryObserved}, " +
                     $"cancelRefusedDuringRecovery={_cancelRefusedDuringRecovery}, lateralCommitment={lateral:F2}m.");
                Finish();
            }
        }
    }

    private void Fail(string message) => GD.PrintErr($"[euro-step] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[euro-step] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
