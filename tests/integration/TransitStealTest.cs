using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Player;
using Hooper.Moves;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #196 (ADR-0018 Amendment
// 2026-07-20): the transit (crossover-sweep) steal window. Unit tests
// (DefensiveResolutionTests.WithinStealTransitReach_*) already pin the pure
// spatial predicate; what they CANNOT reach is the live glue —
// BallController.ResolveDribblingStealAttempts actually gating on a REAL
// #195 sweep (_sweepActive, driven by a real Crossover) composed against a
// REAL defender's StealMove Active window and REAL GlobalPosition distance,
// inside the actual per-tick server pipeline. This scene proves that glue
// end to end (ADR-0016).
//
//   godot --headless --path . res://tests/integration/TransitStealTest.tscn -- --harness-scenario=transit-steal
//   godot --headless --path . res://tests/integration/TransitStealTest.tscn -- --harness-scenario=out-of-reach-recovery
//   godot --headless --path . res://tests/integration/TransitStealTest.tscn -- --harness-scenario=normal-window-unchanged
//   Exit: 0 = PASS, 1 = FAIL (ADR-0016 exit-code contract).
//
// ── The scenarios ──────────────────────────────────────────────────────────
// transit-steal: HEADLINE. The holder starts Dribbling (default HandSide.Left)
//   and begins a Crossover; the defender begins a StealMove TARGETING THE OLD
//   HAND (Left) — scheduled so the crossover's hand-flip (Left->Right, #195)
//   completes several ticks BEFORE the defender's Active window even opens,
//   so for the ENTIRE Active window holder.HandSide reports Right while
//   targetHand is Left: the NORMAL window's side axis is guaranteed to fail
//   throughout, regardless of dribble phase. The defender is positioned well
//   within StealReachRadius of the holder (and therefore of the swept ball).
//   The turnover must still connect — proving the transit window (spatial +
//   relaxed hand), not the normal window, resolved it.
// out-of-reach-recovery: CONTROL for the whiff/blow-by half. Identical setup
//   (same crossover, same mistimed-for-normal-window steal), but the
//   defender is placed far outside StealReachRadius. The transit window must
//   NOT connect either (the ball must stay Dribbling for the WHOLE run — the
//   "every X-didn't-happen needs a control" law: without this, transit-steal
//   passing would be equally consistent with "any steal always connects
//   regardless of reach"), the defender's StealMove must expire naturally
//   into Recovery, and the resulting whiff must grant the #100 blow-by
//   beaten window — proving the risk half of the gamble AND that this exact
//   setup COULD have produced a steal (it does, in transit-steal).
// normal-window-unchanged: CONTROL for the union, not the transit axis. No
//   crossover at all — a plain live-dribble steal timed against the exposed
//   phase band with a MATCHING hand (the union's window (a) branch) must
//   still connect exactly as it did before #196 added window (b). Guards
//   against a regression where adding the `||` accidentally short-circuited
//   or otherwise broke the original #96 path.

public partial class TransitStealTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int VerdictMarginFrames = 10;

    // Same defaults DefensiveResolutionTests/BallController ship —
    // StealReachRadius default is 2.2 m (ADR-0014 arm's-length anchor).
    private static readonly Vector3 NearDefenderOffset = new(0.3f, 0f, 0.3f);   // ~0.42 m from holder — well within reach
    private static readonly Vector3 FarDefenderOffset = new(20f, 0f, 20f);      // far outside any plausible reach radius

    private string _scenario = "transit-steal";

    private BallController _ball;
    private PlayerController _holder;
    private PlayerController _defender;

    private int _frame;
    private double _elapsed;
    private bool _finished;

    // Scheduled action frames.
    private int _crossoverBeginFrame = int.MaxValue; // disabled for normal-window-unchanged
    private int _stealBeginFrame;
    private int _verdictFrame;

    private bool _crossoverBegun;
    private bool _stealBegun;

    // Latched at the FIRST tick the ball is ever observed Loose (the
    // scramble's later AwardPossession legitimately overwrites
    // LastToucherPeerId once someone recovers the ball — same discipline as
    // StealTurnoverTest/HeldStealTest).
    private bool _everLoose;
    private int _toucherAtSteal = -1;
    private HandSide? _holderHandAtSteal;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "transit-steal");
        GD.Print($"[transit-steal] scenario={_scenario} booting headless…");

        // Code-built tree, Players before Ball (hooper-architecture-contract
        // invariant #3 — matches StealTurnoverTest/HeldStealTest/CrossoverSweepTest).
        var players = new Node3D { Name = "Players" };
        _holder = new PlayerController { Name = "1" };
        _defender = new PlayerController { Name = "2" };
        players.AddChild(_holder);
        players.AddChild(_defender);

        _ball = new BallController { Name = "Ball", Players = players };

        AddChild(players);
        AddChild(_ball);

        // Force Dribbling BEFORE the first physics tick — TryAssignTipoffHolder's
        // ForceState(State, peerId) preserves whatever State already is (same
        // pattern as StealTurnoverTest), so the auto-tipoff (which fires on
        // tick 1 because HolderPeerId starts 0) assigns holder "1" without
        // reverting to Held. A Crossover cannot legally Begin from Held
        // (#193's dead-dribble gate), and the normal steal window is only
        // ever checked while Dribbling.
        _ball.StateMachine.StartDribble();

        _holder.GlobalPosition = Vector3.Zero;

        switch (_scenario)
        {
            case "transit-steal":
                _defender.GlobalPosition = _holder.GlobalPosition + NearDefenderOffset;
                _crossoverBeginFrame = 4;
                _stealBeginFrame = 5;
                _verdictFrame = _stealBeginFrame
                    + StealMove.DefaultFrameData.StartupFrames
                    + StealMove.DefaultFrameData.ActiveFrames
                    + VerdictMarginFrames;
                break;

            case "out-of-reach-recovery":
                _defender.GlobalPosition = _holder.GlobalPosition + FarDefenderOffset;
                _crossoverBeginFrame = 4;
                _stealBeginFrame = 5;
                _verdictFrame = _stealBeginFrame
                    + StealMove.DefaultFrameData.StartupFrames
                    + StealMove.DefaultFrameData.ActiveFrames
                    + VerdictMarginFrames;
                break;

            case "normal-window-unchanged":
                // No crossover — a plain live-dribble steal, matching hand,
                // timed against the exposed phase band exactly like
                // StealTurnoverTest's "success" scenario. Defender position
                // is irrelevant here (the normal window carries no spatial
                // axis), so it's left at the near offset for simplicity.
                _defender.GlobalPosition = _holder.GlobalPosition + NearDefenderOffset;
                _stealBeginFrame = ComputeInBandBeginFrame();
                _verdictFrame = _stealBeginFrame
                    + StealMove.DefaultFrameData.StartupFrames
                    + StealMove.DefaultFrameData.ActiveFrames
                    + VerdictMarginFrames;
                break;

            default:
                Fail($"unknown scenario '{_scenario}'.");
                Finish();
                return;
        }

        GD.Print($"[transit-steal] crossoverBeginFrame={_crossoverBeginFrame}, " +
                 $"stealBeginFrame={_stealBeginFrame}, verdictFrame={_verdictFrame}, " +
                 $"reachRadius={_ball.StealReachRadius:F2}");
    }

    // Smallest tick whose live-dribble phase sits inside the exposed band —
    // same closed-form math as StealTurnoverTest.ComputeBeginFrame's
    // "success" branch, reused here (not copied blindly — re-derived from
    // the SAME exported tunables) so this scenario survives a #238 retune of
    // the band or the frame counts. A generous +2 margin (vs. that test's
    // razor-precise "-1 then re-check next tick" framing) is used here
    // because this scenario only needs ONE in-band Active tick to land
    // somewhere inside the window, not to discriminate an entry-tick-only
    // regression the way StealTurnoverTest's "success" scenario does.
    private int ComputeInBandBeginFrame()
    {
        float cycleTicks = _ball.DribblePeriod * Engine.PhysicsTicksPerSecond;
        int startup = StealMove.DefaultFrameData.StartupFrames;
        int firstInBandFrame = Mathf.CeilToInt(_ball.StealLoExposed * cycleTicks);
        return (firstInBandFrame + 2) - startup;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        // Fire scheduled actions from Root BEFORE Players/Ball tick this
        // frame (tree pre-order guarantees Root runs first).
        if (!_crossoverBegun && _frame == _crossoverBeginFrame)
        {
            bool began = _holder.BeginMoveForHarness(new Crossover(1f));
            _crossoverBegun = true;
            if (!began)
            {
                Fail("BeginMoveForHarness(Crossover) returned false — holder machine was not Inactive.");
                Finish();
                return;
            }
            GD.Print($"[transit-steal] frame {_frame}: holder begun Crossover.");
        }

        if (!_stealBegun && _frame == _stealBeginFrame)
        {
            // TargetHand.Left is the holder's STARTING hand (HandSide
            // defaults Left). For the two crossover scenarios this is
            // deliberately the OLD hand — the crossover flips holder.HandSide
            // to Right several ticks before this steal's Active window even
            // opens (see class doc), so the normal window's side axis is
            // guaranteed to fail for the WHOLE Active window regardless of
            // dribble phase. For normal-window-unchanged there is no
            // crossover, so Left stays correct throughout — the union's
            // window (a) branch.
            bool began = _defender.BeginMoveForHarness(new StealMove(HandSide.Left));
            _stealBegun = true;
            if (!began)
            {
                Fail("BeginMoveForHarness(StealMove) returned false — defender machine was not Inactive.");
                Finish();
                return;
            }
            GD.Print($"[transit-steal] frame {_frame}: defender begun StealMove (target Left).");
        }

        // Latch the turnover the first tick it's observed (StealTurnoverTest/
        // HeldStealTest discipline — the scramble's own later AwardPossession
        // legitimately overwrites LastToucherPeerId once someone recovers the
        // ball, which would mask the exact bug these assertions target).
        if (_ball.State == BallState.Loose)
        {
            if (!_everLoose)
            {
                _toucherAtSteal = _ball.LastToucherPeerIdForHarness;
                _holderHandAtSteal = _holder.HandSide;
            }
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
        bool pass;
        string detail;

        switch (_scenario)
        {
            case "transit-steal":
                // The steal MUST connect (defender is peer "2"), and — the
                // headline claim — it must have connected while the holder's
                // authoritative HandSide already reported the NEW hand
                // (Right), even though the defender targeted the OLD hand
                // (Left). That combination is only possible via the transit
                // window; the normal window's hand axis would have refused
                // every tick of this run.
                pass = _everLoose && _toucherAtSteal == 2 && _holderHandAtSteal == HandSide.Right;
                detail = $"everLoose={_everLoose}, toucherAtSteal={_toucherAtSteal}, " +
                    $"holderHandAtSteal={_holderHandAtSteal}, finalState={_ball.State}";
                break;

            case "out-of-reach-recovery":
                // CONTROL: must NEVER connect (out of reach), the ball must
                // stay Dribbling for the whole run, the defender's own
                // StealMove must have expired naturally into Recovery, and
                // the generic #100 whiff-punish lane must have granted a
                // beaten window (proving ResolveBeatenWindowTriggers still
                // catches a transit-steal whiff with no #196-specific code).
                bool neverConnected = !_everLoose && _ball.State == BallState.Dribbling;
                bool naturalRecovery = _defender.PhaseForHarness == MovePhase.Recovery;
                bool beatenWindowGranted = _defender.BeatenUntilTickForHarness > 0;
                pass = neverConnected && naturalRecovery && beatenWindowGranted;
                detail = $"everLoose={_everLoose}, finalState={_ball.State}, " +
                    $"defenderPhase={_defender.PhaseForHarness}, beatenUntilTick={_defender.BeatenUntilTickForHarness}";
                break;

            default: // "normal-window-unchanged"
                pass = _everLoose && _toucherAtSteal == 2;
                detail = $"everLoose={_everLoose}, toucherAtSteal={_toucherAtSteal}, finalState={_ball.State}";
                break;
        }

        if (pass)
            GD.Print($"[transit-steal] PASS — scenario={_scenario}, {detail}.");
        else
            Fail($"scenario={_scenario} — {detail}.");

        Finish(pass ? 0 : 1);
    }

    private void Fail(string message) => GD.PrintErr($"[transit-steal] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[transit-steal] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
