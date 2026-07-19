using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Player;
using Hooper.Moves;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #254: the steal aim→hand mapping
// must be TRANSFORMED by the relative heading between the defender and the
// ball-holder, not compared to the holder's body-relative HandSide raw
// (ADR-0010/0012). StealTurnoverTest already proves the TIMING axis of the
// two-axis steal read (ADR-0018 §2) end to end; this harness isolates the
// HAND axis specifically under a non-zero relative heading, which
// StealTurnoverTest's own scenarios never vary (both its players sit at the
// spawn-default Heading 0 — same-heading throughout).
//
//   godot --headless --path . res://tests/integration/StealFacingMappingTest.tscn -- --harness-scenario=face-to-face
//   godot --headless --path . res://tests/integration/StealFacingMappingTest.tscn -- --harness-scenario=side-by-side
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//
// ── Why this test derives TargetHand from AIM INPUT, not `new StealMove(HandSide.X)` ──
// The #254 root cause shipped for as long as it did because StealTurnoverTest
// constructs `new StealMove(HandSide.Left)` DIRECTLY, bypassing the aim.X →
// HandSide mapping entirely — a regression in the mapping itself could never
// fail that test. This harness instead calls
// PlayerController.BeginStealFromAimForHarness(aimSign, ball)
// (DefensiveMoveHarnessSeam.cs), which routes through the SAME
// ResolveStealTargetHand → HandStateResolver.TargetHandFromAim production
// code SampleMoveInput and ApplyRequestedMove both call — the actual shipped
// mapping, not a copy of it.
//
// ── Why the defender's Heading needs a setup seam ──────────────────────────
// A single offline instance makes player "1" the server-own-player (reads
// real hardware input) and "2" the server-remote-player (only ticked via
// _machine.Tick(), never driven by Move()/HeadingMath — see
// PlayerHarnessSeam.cs and PivotPlantTest's class doc for the identical
// problem with movement). There is no real input path that would ever turn
// "2" in this harness, so DefensiveMoveHarnessSeam.SetHeadingForHarness
// force-sets it once at setup — a scenario INPUT, not something asserted
// against; the geometry under test is exercised for real via
// BeginStealFromAimForHarness's call into HandStateResolver.TargetHandFromAim.
//
// ── The two scenarios ───────────────────────────────────────────────────────
// face-to-face: defender Heading = π, holder Heading = 0 (holder's default) —
//   ~180° relative, the exact repro geometry from #254's dual-instance session.
//   Holder's ball stays on the spawn-default HandSide.Left. aimSign = +1
//   (aim-right, the defender's own body-right) is chosen so the OLD naive
//   mapping (`aim.X > 0 ? Right : Left`) would resolve Right — WRONG, since
//   the ball is on Left, so the pre-fix code would whiff here. The FIX
//   (cos(defenderHeading - holderHeading) = cos(π) = -1) flips it to Left —
//   CORRECT, so the steal must succeed. This is the actual regression
//   discriminator: a reverted/naive mapping fails this scenario RED.
// side-by-side: defender and holder both sit at the spawn-default Heading 0 —
//   ~0° relative, the "no inversion" control. aimSign = -1 (aim-left) is
//   chosen to match the actual Left hand under the UNCHANGED naive mapping —
//   proving the fix is a no-op at 0° relative, exactly the requirement that
//   rules out a blanket unconditional inversion (which would flip this
//   control's result too, and fail it).
public partial class StealFacingMappingTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int VerdictMarginFrames = 4;

    private string _scenario = "face-to-face";

    private BallController _ball;
    private PlayerController _defender;

    private int _frame;
    private int _beginFrame;
    private int _verdictFrame;
    private bool _stealBegun;
    private bool _everLoose;
    private int _toucherAtSteal = -1;
    private double _elapsed;
    private bool _finished;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "face-to-face");
        GD.Print($"[steal-facing] scenario={_scenario} booting headless…");

        // Same code-built tree shape as StealTurnoverTest (Players before Ball,
        // matching scenes/Main.tscn's declaration order — see that class doc
        // for why tick order matters to the frame arithmetic below).
        var players = new Node3D { Name = "Players" };
        var holder = new PlayerController { Name = "1" };
        _defender = new PlayerController { Name = "2" };
        players.AddChild(holder);
        players.AddChild(_defender);

        _ball = new BallController { Name = "Ball", Players = players };

        AddChild(players);
        AddChild(_ball);

        // Force a live dribble the same way StealTurnoverTest does — steal
        // resolution (ResolveStealAttempts) is a no-op unless the ball is
        // Dribbling, and the frame arithmetic below assumes DribbleCycle.Phase
        // has been advancing every tick since frame 1 (see ComputeBeginFrame).
        _ball.StateMachine.StartDribble();

        // ── Setup-only heading seam (issue #254) ────────────────────────────
        // Holder is left at the spawn-default Heading 0 in BOTH scenarios —
        // only the defender's facing varies between them.
        if (_scenario == "face-to-face")
            _defender.SetHeadingForHarness(Mathf.Pi);
        // "side-by-side": no-op, defender stays at the default Heading 0.

        _beginFrame = ComputeBeginFrame();
        _verdictFrame = _beginFrame
            + StealMove.DefaultFrameData.StartupFrames
            + StealMove.DefaultFrameData.ActiveFrames
            + VerdictMarginFrames;

        GD.Print($"[steal-facing] beginFrame={_beginFrame} verdictFrame={_verdictFrame} " +
                 $"band=[{_ball.StealLoExposed:F2},{_ball.StealHiExposed:F2}]");
    }

    // Identical derivation to StealTurnoverTest's "success" scenario — entry
    // tick lands one tick BELOW the exposed band, with the very next Active
    // tick landing INSIDE it, so the ball-hand axis (what this harness is
    // isolating) is the only thing that can make the steal whiff, not timing.
    private int ComputeBeginFrame()
    {
        float cycleTicks = _ball.DribblePeriod * Engine.PhysicsTicksPerSecond;
        int startup = StealMove.DefaultFrameData.StartupFrames;
        int firstInBandFrame = Mathf.CeilToInt(_ball.StealLoExposed * cycleTicks);
        return (firstInBandFrame - 1) - startup + 1;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        if (!_stealBegun && _frame == _beginFrame)
        {
            // aimSign chosen per scenario — see class doc for the derivation
            // of why each value discriminates the fix from the bug/a blanket
            // flip. TargetHand is DERIVED here, not hand-picked — the point
            // of this harness (issue #254's "required harness coverage").
            float aimSign = _scenario == "face-to-face" ? +1f : -1f;
            bool begun = _defender.BeginStealFromAimForHarness(aimSign, _ball);
            _stealBegun = true;
            if (!begun)
            {
                Fail("BeginStealFromAimForHarness returned false — machine was not Inactive at begin.");
                Finish();
                return;
            }
            GD.Print($"[steal-facing] frame {_frame}: steal begun (scenario={_scenario}, aimSign={aimSign})");
        }

        if (_stealBegun && _ball.State == BallState.Loose)
        {
            if (!_everLoose)
                _toucherAtSteal = _ball.LastToucherPeerIdForHarness;
            _everLoose = true;
        }

        if (_frame >= _verdictFrame)
        {
            Verdict();
            return;
        }

        if (_elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame} before reaching verdict frame {_verdictFrame}.");
            Finish();
        }
    }

    private void Verdict()
    {
        // Both scenarios expect a SUCCESSFUL steal: the whole point of #254's
        // required coverage is a face-to-face aim-at-the-visible-ball steal
        // that connects, plus a side-by-side control proving the mapping is
        // unchanged (not merely "still succeeds by coincidence") there too.
        bool turnoverCompleted = _ball.State == BallState.Held && _ball.StateMachine.HolderPeerId != 0;
        bool toucherCorrect = _toucherAtSteal == 2;

        bool pass = _everLoose && turnoverCompleted && toucherCorrect;

        if (pass)
        {
            GD.Print($"[steal-facing] PASS — scenario={_scenario}, everLoose={_everLoose}, " +
                     $"finalState={_ball.State}, holder={_ball.StateMachine.HolderPeerId}, " +
                     $"toucherAtSteal={_toucherAtSteal}.");
        }
        else
        {
            Fail($"scenario={_scenario} expected everLoose=true, a completed turnover, and " +
                 $"toucherAtSteal=2, but got everLoose={_everLoose}, finalState={_ball.State}, " +
                 $"holder={_ball.StateMachine.HolderPeerId}, toucherAtSteal={_toucherAtSteal}.");
        }
        Finish(pass ? 0 : 1);
    }

    private void Fail(string message) => GD.PrintErr($"[steal-facing] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[steal-facing] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
