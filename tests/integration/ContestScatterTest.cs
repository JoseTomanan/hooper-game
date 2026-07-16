using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Player;
using Hooper.Moves;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #99 (ADR-0016, ADR-0018 §2): the
// on-ball contest's ADDITIONAL accuracy factor, composed on top of the
// existing passive proximity scatter, end to end. Unit tests already pin the
// pure composition arithmetic (DefensiveResolutionTests.ContestAppliesAt /
// ContestMoveFactor) and ContestMove's frame-data contract (ContestMoveTests).
// What they CANNOT reach is the live engine glue: BallController.
// ApplyShootLocally reading a REAL defender's committed-move Phase/
// FrameInPhase (via PlayerController.ActiveMove<ContestMove>()) against a
// REAL shooter's release tick, computed with real PhysicsTick arithmetic.
//
//   godot --headless --path . res://tests/integration/ContestScatterTest.tscn -- --harness-scenario=contest-active
//   godot --headless --path . res://tests/integration/ContestScatterTest.tscn -- --harness-scenario=no-contest
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "contest-active".
//
// ── Why a single offline instance is the server ───────────────────────────
// Same reasoning as BlockTurnoverTest/StealTurnoverTest: with no
// MultiplayerPeer assigned, Godot uses OfflineMultiplayerPeer (is_server()
// hardcoded true), so BallController.IsServer is true and both player nodes'
// _machine.Tick() advances every physics frame regardless of role.
//
// ── Why LastContestMoveFactorForHarness, not the shot's landing spot ──────
// The composed factor is otherwise only observable indirectly through the
// scattered shot's (RNG-influenced) landing position — proving THAT would
// mean re-deriving ShotScatter's uniform-disc math inside this harness just
// to invert it, which duplicates ADR-0009's own tests rather than proving
// #99's NEW composition. Exposing the live-computed factor directly is the
// same *ForHarness pattern this file already uses for LastToucherPeerId/
// Velocity (hooper-architecture-contract's harness-seam discipline) — it
// proves the REAL engine glue (a real committed ContestMove, begun through
// the real BeginCommittedMove choke point, read via the real
// ActiveMove<ContestMove>() against the real release tick) produced the
// right number, without re-implementing ShotScatter's geometry here.
//
// ── Scenario "contest-active": defender's Active overlaps the release tick ──
// The shooter (peer "1", the tipoff holder) begins a REAL JumpShot via
// TripleThreatHarnessSeam.BeginJumpShotForHarness (the same BeginCommittedMove
// path production input reaches). The defender (peer "2") begins a REAL
// ContestMove via DefensiveMoveHarnessSeam.BeginMoveForHarness, timed via
// ComputeDefenderBeginFrame so its Active window's very first tick lands
// exactly on the shot's release tick — the same placement strategy
// BlockTurnoverTest's "success" scenario uses for BlockMove. Asserts
// LastContestMoveFactorForHarness == 1 + ContestMoveScatterK (the live
// export, not a hardcoded literal, so this survives #104 retuning).
//
// ── Scenario "no-contest": the control this is checked against ─────────────
// Identical shooter setup, but the defender never begins a ContestMove at
// all. Asserts LastContestMoveFactorForHarness == 1.0 exactly — proving
// "contest-active"'s 1.5 isn't a hardcoded/always-on constant but genuinely
// depends on the committed move actually being Active at release. Without
// this control, "contest-active" alone could pass even if the composition
// wiring were entirely deleted and the property just defaulted to some other
// constant.
public partial class ContestScatterTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int ArmFrames = 2; // ticks for TryAssignTipoffHolder to run
    private const int VerdictMarginFrames = 3; // ticks past release before reading the verdict

    // Shooter position mirrors BlockTurnoverTest's ShooterPosition (0,0,5) —
    // an arbitrary but non-degenerate distance from RimCenter's (0,*,0) XZ.
    private static readonly Vector3 ShooterPosition = new(0f, 0f, 5f);

    // Contest carries no reach gate (ADR-0018 §2 is timing-only for #99,
    // unlike block's #214 amendment — see ContestMove's class doc) so the
    // defender's exact position does not matter to LastContestMoveFactorForHarness.
    // Placed near the shooter anyway so the setup reads naturally.
    private static readonly Vector3 DefenderPosition = new(1f, 0f, 5f);

    private string _scenario = "contest-active";
    private bool IsContestActiveScenario => _scenario == "contest-active";

    private BallController _ball;
    private PlayerController _shooter; // peer "1" — tipoff holder in this code-built tree
    private PlayerController _defender; // peer "2"

    private int _frame;
    private double _elapsed;
    private bool _finished;

    private bool _shooterBegun;
    private bool _defenderBegun;
    private int _shooterBeginFrame;
    private int _predictedReleaseFrame = -1;
    private int _defenderBeginFrame;
    private int _verdictFrame = -1;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "contest-active");
        GD.Print($"[contest-scatter] scenario={_scenario} booting headless…");

        // Code-built tree (avoids fragile .tscn ext_resource/uid wiring for a
        // throwaway harness — see StealTurnoverTest's class doc). Sibling
        // order matches scenes/Main.tscn: Players before Ball.
        var players = new Node3D { Name = "Players" };
        _shooter = new PlayerController { Name = "1" };
        _defender = new PlayerController { Name = "2" };
        players.AddChild(_shooter);
        players.AddChild(_defender);
        _defender.GlobalPosition = DefenderPosition;

        _ball = new BallController { Name = "Ball", Players = players };
        // ShotScatterEnabled stays at its true default — this harness exists
        // specifically to exercise the IsServer && ShotScatterEnabled block
        // that composes contestMoveFactor (contrast BlockTurnoverTest, which
        // disables it to get a clean-geometry counterfactual make).

        AddChild(players);
        AddChild(_ball);
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
            _shooter.GlobalPosition = ShooterPosition;

            // Predicted release frame, from the SHOOTER's actual begin frame
            // plus the LIVE JumpShot frame data — not hardcoded, so it
            // survives #104 retuning. Same derivation BlockTurnoverTest uses.
            int jumpShotStartup = JumpShot.DefaultFrameData.StartupFrames;
            _predictedReleaseFrame = _shooterBeginFrame + (jumpShotStartup - 1);
            _verdictFrame = _predictedReleaseFrame + VerdictMarginFrames;

            if (IsContestActiveScenario)
            {
                _defenderBeginFrame = ComputeDefenderBeginFrame(_predictedReleaseFrame);

                if (_defenderBeginFrame <= _frame)
                {
                    Fail($"computed defender begin frame {_defenderBeginFrame} is not reachable from the " +
                         $"current frame {_frame} — a frame-data change (JumpShot/ContestMove Startup) made " +
                         "this scenario's placement unschedulable. Re-derive the harness's frame arithmetic " +
                         "for the new tunables rather than chasing the resulting timeout.");
                    Finish();
                    return;
                }

                GD.Print($"[contest-scatter] frame {_frame}: shooter begun JumpShot " +
                         $"(predicted release frame {_predictedReleaseFrame}); defender scheduled to begin at frame {_defenderBeginFrame}.");
            }
        }

        // Step 2 ("contest-active" only): begin the defender's REAL
        // ContestMove at the computed frame. "no-contest" never reaches
        // this — _defenderBeginFrame stays at its default 0, which _frame
        // (starting at 1, only increasing) can never equal.
        if (IsContestActiveScenario && _shooterBegun && !_defenderBegun && _frame == _defenderBeginFrame)
        {
            bool began = _defender.BeginMoveForHarness(new ContestMove());
            _defenderBegun = true;
            if (!began)
            {
                Fail("BeginMoveForHarness(ContestMove) returned false — defender's machine was not Inactive at begin.");
                Finish();
                return;
            }
            GD.Print($"[contest-scatter] frame {_frame}: defender begun ContestMove.");
        }

        if (_shooterBegun && _frame >= _verdictFrame)
        {
            Verdict();
            return;
        }

        if (_elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame} before reaching verdict frame {_verdictFrame}. " +
                 $"shooterBegun={_shooterBegun}, defenderBegun={_defenderBegun}.");
            Finish();
        }
    }

    // Where to begin the defender's ContestMove so its Active window's FIRST
    // tick lands exactly on the release tick — guarantees
    // DefensiveResolution.ContestAppliesAt reports true (the release "window"
    // collapses to that single tick; see its own doc). Same CommittedMoveMachine
    // Tick()/Begin() arithmetic BlockTurnoverTest.ComputeDefenderBeginFrame
    // derives for its "success" scenario, specialized to ContestMove's own
    // Startup count.
    private int ComputeDefenderBeginFrame(int releaseFrame)
    {
        int contestStartup = ContestMove.DefaultFrameData.StartupFrames;
        return releaseFrame - (contestStartup - 1);
    }

    private void Verdict()
    {
        float observed = _ball.LastContestMoveFactorForHarness;
        float k = _ball.ContestMoveScatterK; // live export — survives #104 retuning
        bool pass;

        if (IsContestActiveScenario)
        {
            float expected = 1f + k;
            pass = Mathf.IsEqualApprox(observed, expected, 1e-4f);
            if (pass)
            {
                GD.Print($"[contest-scatter] PASS — scenario=contest-active, " +
                         $"LastContestMoveFactorForHarness={observed} (expected {expected}).");
            }
            else
            {
                Fail($"scenario=contest-active expected LastContestMoveFactorForHarness == 1 + ContestMoveScatterK " +
                     $"({expected}), but got {observed}. defenderBegun={_defenderBegun}, " +
                     $"predictedReleaseFrame={_predictedReleaseFrame}, defenderBeginFrame={_defenderBeginFrame}.");
            }
        }
        else
        {
            pass = Mathf.IsEqualApprox(observed, 1f, 1e-4f);
            if (pass)
            {
                GD.Print($"[contest-scatter] PASS — scenario=no-contest, " +
                         $"LastContestMoveFactorForHarness={observed} (expected 1.0).");
            }
            else
            {
                Fail($"scenario=no-contest expected LastContestMoveFactorForHarness == 1.0 (no committed contest " +
                     $"ever began), but got {observed} — the passive-only shot must not pick up the additional " +
                     "committed-contest factor.");
            }
        }

        Finish(pass ? 0 : 1);
    }

    private void Fail(string message) => GD.PrintErr($"[contest-scatter] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[contest-scatter] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
