using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Player;
using Hooper.Moves;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #243 (ADR-0016), the build half of
// #185: proving the fadeaway/off-balance shot-animation TRIGGER end to end
// through the real engine glue. FadeawayTriggerResolverTests (xUnit) already
// pins the pure angle-threshold arithmetic; what it cannot reach is a REAL
// JumpShot, begun through the real BeginCommittedMove choke point, releasing
// through the real JustEnteredActive tick, with PlayerController.
// DisplayFadeaway() and MoveAnimResolver.Resolve reading the REAL
// (JumpShot.IsFadeaway, Phase) pair that TickCommittedMoveBehavior actually
// produces — not a hand-constructed pair asserted in isolation.
//
//   godot --headless --path . res://tests/integration/FadeawayTriggerTest.tscn -- --harness-scenario=mid-pivot
//   godot --headless --path . res://tests/integration/FadeawayTriggerTest.tscn -- --harness-scenario=squared-up
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "mid-pivot".
//
// ── Why a single offline instance is the server ───────────────────────────
// Same reasoning as ContestScatterTest/BlockTurnoverTest: with no
// MultiplayerPeer assigned, Godot uses OfflineMultiplayerPeer (is_server()
// hardcoded true), so PlayerController.IsServer is true and
// DisplayPhaseResolver.LocalMachineDrivesDisplay reads the shooter's own
// live _machine/CurrentMove — no second peer or broadcast round-trip needed
// to observe the classification (that per-role reconstruction is #69's own,
// already-tested territory, not this issue's subject).
//
// ── Why SetHeadingForHarness, not real stick input ────────────────────────
// This scenario's subject is "does the classification correctly key off
// Heading-at-release," not "can HeadingMath.RotateToward turn a player to a
// given yaw" (already covered by PivotPlantTest/DriveGatherTest). Forcing
// Heading directly is the same category of setup ContestScatterTest already
// uses for GlobalPosition.
//
// ── Scenario "mid-pivot" ─────────────────────────────────────────────────
// Shooter's Heading is forced to exactly 180° off the rim direction
// (back-to-basket — the same worst case ShotFacingTests exercises) BEFORE
// JumpShot begins, and never touched again (no movement input in this
// harness, so Heading cannot drift). At the verdict frame (past release +
// margin), asserts DisplayFadeaway() == true AND MoveAnimResolver.Resolve
// (with that flag) == FadeawayActive.
//
// ── Scenario "squared-up" ─────────────────────────────────────────────────
// The control this is checked against (mirrors ContestScatterTest's
// contest-active/no-contest pairing): identical shooter setup, but Heading
// is forced to point EXACTLY at the rim instead. Asserts DisplayFadeaway()
// == false and the resolved anim state is the ordinary Active, never
// FadeawayActive — proving "mid-pivot"'s true isn't a hardcoded/always-on
// default.
public partial class FadeawayTriggerTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int VerdictMarginFrames = 3; // ticks past release before reading the verdict

    // Shooter position mirrors ContestScatterTest/BlockTurnoverTest's
    // ShooterPosition (0,0,5) — a non-degenerate XZ distance from
    // RimCenter's (0,*,0), so AngleFromTarget's degenerate guard never fires.
    private static readonly Vector3 ShooterPosition = new(0f, 0f, 5f);
    private static readonly Vector3 RimCenter = new(0f, 3.05f, 0f);

    private string _scenario = "mid-pivot";
    private bool IsMidPivotScenario => _scenario == "mid-pivot";

    private BallController _ball;
    private PlayerController _shooter; // peer "1"
    private PlayerController _other;   // peer "2" — present only so the ball's tipoff has two players to award between

    private int _frame;
    private double _elapsed;
    private bool _finished;

    private bool _shooterBegun;
    private int _predictedReleaseFrame = -1;
    private int _verdictFrame = -1;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "mid-pivot");
        GD.Print($"[fadeaway-trigger] scenario={_scenario} booting headless…");

        // Code-built tree (avoids fragile .tscn ext_resource/uid wiring for a
        // throwaway harness — see StealTurnoverTest/ContestScatterTest's class docs).
        var players = new Node3D { Name = "Players" };
        _shooter = new PlayerController { Name = "1" };
        _other = new PlayerController { Name = "2" };
        players.AddChild(_shooter);
        players.AddChild(_other);

        _ball = new BallController { Name = "Ball", Players = players };

        AddChild(players);
        AddChild(_ball);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        // Once the tipoff has assigned the shooter as holder, force its
        // Heading per-scenario, position it, and begin a REAL JumpShot
        // through the same BeginCommittedMove path production input reaches.
        if (!_shooterBegun && _ball.StateMachine.HolderPeerId == 1)
        {
            _shooter.GlobalPosition = ShooterPosition;

            // Target yaw: direction from ShooterPosition to RimCenter — the
            // SAME Atan2(dx, dz) convention ShotFacing.AngleFromTarget uses
            // internally, kept explicit here rather than imported so this
            // harness proves the trigger against an independently-derived
            // yaw, not a value borrowed from the code under test.
            float dx = RimCenter.X - ShooterPosition.X;
            float dz = RimCenter.Z - ShooterPosition.Z;
            float targetYaw = Mathf.Atan2(dx, dz);

            float headingYaw = IsMidPivotScenario
                ? targetYaw + Mathf.Pi   // 180° off — worst-case mid-pivot
                : targetYaw;             // squared up
            _shooter.SetHeadingForHarness(headingYaw);

            bool began = _shooter.BeginJumpShotForHarness();
            _shooterBegun = true;
            if (!began)
            {
                Fail("BeginJumpShotForHarness returned false — shooter's machine was not Inactive at begin.");
                Finish();
                return;
            }

            // Predicted release frame, from THIS frame plus the LIVE JumpShot
            // frame data — not hardcoded, so it survives #104 retuning. Same
            // derivation ContestScatterTest/BlockTurnoverTest use.
            int jumpShotStartup = JumpShot.DefaultFrameData.StartupFrames;
            _predictedReleaseFrame = _frame + (jumpShotStartup - 1);
            _verdictFrame = _predictedReleaseFrame + VerdictMarginFrames;

            GD.Print($"[fadeaway-trigger] frame {_frame}: shooter begun JumpShot with heading={headingYaw:F4} " +
                     $"(targetYaw={targetYaw:F4}); predicted release frame {_predictedReleaseFrame}.");
        }

        if (_shooterBegun && _frame >= _verdictFrame)
        {
            Verdict();
            return;
        }

        if (_elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame} before reaching verdict frame {_verdictFrame}. shooterBegun={_shooterBegun}.");
            Finish();
        }
    }

    private void Verdict()
    {
        bool displayFadeaway = _shooter.DisplayFadeaway();
        MoveAnimState resolved = MoveAnimResolver.Resolve(_shooter.PhaseForHarness, displayFadeaway);
        bool pass;

        if (IsMidPivotScenario)
        {
            pass = displayFadeaway && resolved == MoveAnimState.FadeawayActive;
            if (pass)
            {
                GD.Print("[fadeaway-trigger] PASS — scenario=mid-pivot, DisplayFadeaway()=true, resolved=FadeawayActive.");
            }
            else
            {
                Fail($"scenario=mid-pivot expected DisplayFadeaway()==true and resolved==FadeawayActive, " +
                     $"got DisplayFadeaway()={displayFadeaway}, resolved={resolved}, phase={_shooter.PhaseForHarness}.");
            }
        }
        else
        {
            pass = !displayFadeaway && resolved == MoveAnimState.Active;
            if (pass)
            {
                GD.Print("[fadeaway-trigger] PASS — scenario=squared-up, DisplayFadeaway()=false, resolved=Active.");
            }
            else
            {
                Fail($"scenario=squared-up expected DisplayFadeaway()==false and resolved==Active (never FadeawayActive), " +
                     $"got DisplayFadeaway()={displayFadeaway}, resolved={resolved}, phase={_shooter.PhaseForHarness}.");
            }
        }

        Finish(pass ? 0 : 1);
    }

    private void Fail(string message) => GD.PrintErr($"[fadeaway-trigger] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[fadeaway-trigger] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
