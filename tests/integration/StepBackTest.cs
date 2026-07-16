using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #197 (ADR-0016): step-back +
// retreat dribble, the vertical right-stick gesture pair. RightStickGesture-
// RecognizerTests / StepBackBurstMathTests / StepBackTests / RetreatDribble-
// Tests (xUnit) already pin the pure recognizer, burst math, and frame-data
// contracts; what they CANNOT reach is the real glue this issue wires up:
// PlayerController.BeginCommittedMove's dead-dribble gate for RetreatDribble,
// and StepBack's Active-entry gather (BallController.CradleForShotStartup)
// actually firing end to end.
//
//   godot --headless --path . res://tests/integration/StepBackTest.tscn -- --harness-scenario=step-back-gathers
//   godot --headless --path . res://tests/integration/StepBackTest.tscn -- --harness-scenario=retreat-dribble-no-gather
//   godot --headless --path . res://tests/integration/StepBackTest.tscn -- --harness-scenario=retreat-dribble-dead-dribble-gate
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "step-back-gathers".
//
// ── Scenario "step-back-gathers": the risk/reward core of #197 ────────────
// A live dribble, then a real StepBack via BeginCommittedMove. Asserts the
// Active-entry gather actually fires: the ball transitions Dribbling -> Held
// AND HasDribbled becomes true (#193's dead-Held pattern), and the holder's
// Velocity picked up a nonzero backward burst.
//
// ── Scenario "retreat-dribble-no-gather": the CONTROL for the scenario above ─
// SAME live-dribble setup, but a RetreatDribble instead. This is the
// "didn't happen" assertion's control: without this scenario, a bug that
// made EVERY committed move gather (e.g. an accidental unconditional
// CradleForShotStartup call) would pass "step-back-gathers" trivially. This
// proves the OTHER half of the vertical gesture pair does NOT gather — ball
// stays Dribbling, HasDribbled stays false — throughout its full lifecycle.
//
// ── Scenario "retreat-dribble-dead-dribble-gate": #193's gate applies here too ─
// Mirrors TripleThreatTest's dead-Held-crossover-refused step and
// BehindTheBackTest's "dead-dribble-gate" scenario for RetreatDribble
// specifically: a fresh tipoff lands the holder in Held (no dribble yet);
// attempting a RetreatDribble must be refused by the SAME Held-holder gate
// Crossover/Hesitation/BehindTheBack use.
public partial class StepBackTest : Node
{
    private const double TimeoutSeconds = 10.0;
    private const int ArmFrames = 2;
    private const int ActionMarginFrames = 3;

    // StepBack's DefaultFrameData: startup=7. Active fires on the 7th
    // Startup-exhausting tick; a margin of 3 comfortably clears it.
    private const int StepBackActiveMarginFrames = 10;

    // RetreatDribble's DefaultFrameData: startup=3, active=2, recovery=4 —
    // 9 ticks total. A margin of 4 comfortably clears the FULL lifecycle
    // back to Inactive, so the control's "never gathers" check covers the
    // whole move, not just its Active tick.
    private const int RetreatDribbleFullLifecycleMarginFrames = 13;

    private string _scenario = "step-back-gathers";

    private BallController _ball;
    private PlayerController _p1;
    private PlayerController _p2;

    private int _frame;
    private double _elapsed;
    private bool _finished;

    // ── "step-back-gathers" state ────────────────────────────────────────────
    private enum GatherStep { AwaitTipoff, DriveChecked, AwaitGather }
    private GatherStep _gatherStep = GatherStep.AwaitTipoff;
    private int _holderId;
    private int _stepDeadlineFrame;

    // ── "retreat-dribble-no-gather" state ────────────────────────────────────
    private enum NoGatherStep { AwaitTipoff, DriveChecked, AwaitLifecycleEnd }
    private NoGatherStep _noGatherStep = NoGatherStep.AwaitTipoff;

    // ── "retreat-dribble-dead-dribble-gate" state ────────────────────────────
    private enum GateStep { AwaitTipoff, Attempted }
    private GateStep _gateStep = GateStep.AwaitTipoff;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "step-back-gathers");
        GD.Print($"[step-back] scenario={_scenario} booting headless…");

        var players = new Node3D { Name = "Players" };
        _p1 = new PlayerController { Name = "1" };
        _p2 = new PlayerController { Name = "2" };
        players.AddChild(_p1);
        players.AddChild(_p2);

        _ball = new BallController { Name = "Ball", Players = players };

        AddChild(players);
        AddChild(_ball);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        switch (_scenario)
        {
            case "retreat-dribble-no-gather":            TickNoGather(); break;
            case "retreat-dribble-dead-dribble-gate":     TickDeadDribbleGate(); break;
            default:                                      TickStepBackGathers(); break;
        }

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame}, scenario={_scenario}.");
            Finish();
        }
    }

    // ── Scenario: "step-back-gathers" ────────────────────────────────────────
    private void TickStepBackGathers()
    {
        switch (_gatherStep)
        {
            case GatherStep.AwaitTipoff:
                if (_frame < ArmFrames) break;
                if (_ball.StateMachine.HolderPeerId == 0)
                {
                    Fail("tipoff never assigned a holder.");
                    Finish();
                    return;
                }
                _holderId = _ball.StateMachine.HolderPeerId;
                _ball.TryStartDribble(_holderId);
                _gatherStep = GatherStep.DriveChecked;
                _stepDeadlineFrame = _frame + ActionMarginFrames;
                break;

            case GatherStep.DriveChecked:
                if (_frame < _stepDeadlineFrame) break;
                if (_ball.State != BallState.Dribbling)
                {
                    Fail($"expected TryStartDribble to reach Dribbling; got state={_ball.State}.");
                    Finish();
                    return;
                }

                PlayerController holder = NodeForPeer(_holderId);
                bool began = holder.BeginStepBackForHarness();
                if (!began)
                {
                    Fail("BeginStepBackForHarness returned false — machine was not Inactive.");
                    Finish();
                    return;
                }
                GD.Print($"[step-back] StepBack begun for holder {_holderId} at frame {_frame}.");
                _gatherStep = GatherStep.AwaitGather;
                _stepDeadlineFrame = _frame + StepBackActiveMarginFrames;
                break;

            case GatherStep.AwaitGather:
                if (_frame < _stepDeadlineFrame) break;

                bool gathered = _ball.State == BallState.Held && _ball.HasDribbled;
                if (!gathered)
                {
                    Fail($"expected the step-back's Active-entry gather to leave state=Held, HasDribbled=true; got state={_ball.State}, HasDribbled={_ball.HasDribbled}.");
                    Finish();
                    return;
                }
                GD.Print("[step-back] PASS step-back-gathers — Active-entry gather reached dead Held (state=Held, HasDribbled=true).");

                PlayerController holderAfter = NodeForPeer(_holderId);
                float speed = new Vector2(holderAfter.Velocity.X, holderAfter.Velocity.Z).Length();
                bool burstApplied = speed > 0.5f;
                if (!burstApplied)
                {
                    Fail($"expected a nonzero backward burst on the holder's Velocity; got speed={speed:F4}.");
                    Finish();
                    return;
                }
                GD.Print($"[step-back] PASS step-back-burst — holder speed={speed:F4} m/s during the burst window.");
                GD.Print("[step-back] RESULT: PASS (exit 0)");
                Finish(0);
                return;
        }
    }

    // ── Scenario: "retreat-dribble-no-gather" (control) ──────────────────────
    private void TickNoGather()
    {
        switch (_noGatherStep)
        {
            case NoGatherStep.AwaitTipoff:
                if (_frame < ArmFrames) break;
                if (_ball.StateMachine.HolderPeerId == 0)
                {
                    Fail("tipoff never assigned a holder.");
                    Finish();
                    return;
                }
                _holderId = _ball.StateMachine.HolderPeerId;
                _ball.TryStartDribble(_holderId);
                _noGatherStep = NoGatherStep.DriveChecked;
                _stepDeadlineFrame = _frame + ActionMarginFrames;
                break;

            case NoGatherStep.DriveChecked:
                if (_frame < _stepDeadlineFrame) break;
                if (_ball.State != BallState.Dribbling)
                {
                    Fail($"expected TryStartDribble to reach Dribbling; got state={_ball.State}.");
                    Finish();
                    return;
                }

                PlayerController holder = NodeForPeer(_holderId);
                bool began = holder.BeginRetreatDribbleForHarness();
                if (!began)
                {
                    Fail("BeginRetreatDribbleForHarness returned false — machine was not Inactive, or the dead-dribble gate misfired against a live dribble.");
                    Finish();
                    return;
                }
                GD.Print($"[step-back] RetreatDribble begun for holder {_holderId} at frame {_frame}.");
                _noGatherStep = NoGatherStep.AwaitLifecycleEnd;
                _stepDeadlineFrame = _frame + RetreatDribbleFullLifecycleMarginFrames;
                break;

            case NoGatherStep.AwaitLifecycleEnd:
                // Poll every tick through the move's whole lifecycle — the
                // control's whole point is that gather NEVER happens, not
                // just that it hasn't happened yet at one sampled instant.
                if (_ball.State != BallState.Dribbling || _ball.HasDribbled)
                {
                    Fail($"CONTROL VIOLATION: RetreatDribble must never gather — expected state=Dribbling, HasDribbled=false throughout; got state={_ball.State}, HasDribbled={_ball.HasDribbled} at frame {_frame}.");
                    Finish();
                    return;
                }
                if (_frame < _stepDeadlineFrame) break;

                PlayerController holder2 = NodeForPeer(_holderId);
                if (holder2.MachinePhaseForHarness != MovePhase.Inactive)
                {
                    Fail($"expected the RetreatDribble lifecycle to have returned to Inactive by frame {_frame}; got phase={holder2.MachinePhaseForHarness}. Increase RetreatDribbleFullLifecycleMarginFrames.");
                    Finish();
                    return;
                }

                GD.Print("[step-back] PASS retreat-dribble-no-gather — ball stayed Dribbling, HasDribbled stayed false through the FULL RetreatDribble lifecycle (control for step-back-gathers).");
                GD.Print("[step-back] RESULT: PASS (exit 0)");
                Finish(0);
                return;
        }
    }

    // ── Scenario: "retreat-dribble-dead-dribble-gate" ────────────────────────
    private void TickDeadDribbleGate()
    {
        switch (_gateStep)
        {
            case GateStep.AwaitTipoff:
                if (_frame < ArmFrames) break;
                if (_ball.StateMachine.HolderPeerId == 0)
                {
                    Fail("tipoff never assigned a holder.");
                    Finish();
                    return;
                }

                // Fresh tipoff already lands the holder in (live) Held (#193)
                // — no drive, no dribble yet. Attempting RetreatDribble here
                // must be refused by the SAME Held-holder gate Crossover/
                // Hesitation/BehindTheBack use (PlayerController.
                // BeginCommittedMove) — the gate keys on ball STATE (Held),
                // not on HasDribbled, so this fires even on a live (not yet
                // dead) Held possession.
                if (_ball.State != BallState.Held)
                {
                    Fail($"expected a fresh tipoff to land the holder in Held; got state={_ball.State}.");
                    Finish();
                    return;
                }

                PlayerController holder = NodeForPeer(_ball.StateMachine.HolderPeerId);
                bool began = holder.BeginRetreatDribbleForHarness();
                bool stayedInactive = holder.MachinePhaseForHarness == MovePhase.Inactive;

                if (began || !stayedInactive)
                {
                    Fail($"expected BeginRetreatDribbleForHarness to be REFUSED from a Held possession; began={began}, phase={holder.MachinePhaseForHarness}.");
                    Finish();
                    return;
                }
                GD.Print("[step-back] PASS retreat-dribble-dead-dribble-gate — RetreatDribble refused from a Held possession, machine stayed Inactive.");
                GD.Print("[step-back] RESULT: PASS (exit 0)");
                Finish(0);
                _gateStep = GateStep.Attempted;
                return;
        }
    }

    private PlayerController NodeForPeer(int peerId) => peerId == 1 ? _p1 : _p2;

    private void Fail(string message) => GD.PrintErr($"[step-back] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        if (code != 0)
            GD.Print($"[step-back] RESULT: FAIL (exit {code})");
        GetTree().Quit(code);
    }
}
