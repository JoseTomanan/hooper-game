using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Player;
using Hooper.Moves;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #196 (ADR-0018 Amendment
// 2026-07-20): the transit (ball-hand-sweep) steal window, and its #261
// follow-up coverage for the NON-crossover sweep paths. Unit tests
// (DefensiveResolutionTests.WithinStealTransitReach_*) already pin the pure
// spatial predicate; what they CANNOT reach is the live glue —
// BallController.ResolveDribblingStealAttempts actually gating on a REAL
// #195-family sweep (_sweepActive, driven by AdvanceHandSweep for EVERY
// sweep move — Crossover, BehindTheBack, BetweenTheLegs, Spin's BodyShield)
// composed against a REAL defender's StealMove Active window and REAL
// GlobalPosition distance, inside the actual per-tick server pipeline. This
// scene proves that glue end to end (ADR-0016) for each sweep path that
// drives the SAME shared _sweepActive flag.
//
//   godot --headless --path . res://tests/integration/TransitStealTest.tscn -- --harness-scenario=transit-steal
//   godot --headless --path . res://tests/integration/TransitStealTest.tscn -- --harness-scenario=out-of-reach-recovery
//   godot --headless --path . res://tests/integration/TransitStealTest.tscn -- --harness-scenario=normal-window-unchanged
//   godot --headless --path . res://tests/integration/TransitStealTest.tscn -- --harness-scenario=transit-steal-behind-the-back
//   godot --headless --path . res://tests/integration/TransitStealTest.tscn -- --harness-scenario=out-of-reach-recovery-behind-the-back
//   godot --headless --path . res://tests/integration/TransitStealTest.tscn -- --harness-scenario=transit-steal-between-the-legs
//   godot --headless --path . res://tests/integration/TransitStealTest.tscn -- --harness-scenario=out-of-reach-recovery-between-the-legs
//   godot --headless --path . res://tests/integration/TransitStealTest.tscn -- --harness-scenario=transit-steal-spin
//   godot --headless --path . res://tests/integration/TransitStealTest.tscn -- --harness-scenario=out-of-reach-recovery-spin
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
//
// ── #261's non-crossover pairs ─────────────────────────────────────────────
// transit-steal-behind-the-back / out-of-reach-recovery-behind-the-back and
// transit-steal-between-the-legs / out-of-reach-recovery-between-the-legs
// are the SAME headline/control shape as transit-steal/out-of-reach-recovery,
// with the sweep move swapped for BehindTheBack (#194) / BetweenTheLegs
// (#199). This is a NON-vacuous re-derivation, not a copy-paste of the
// crossover trick: BehindTheBack and BetweenTheLegs both flip HandSide on
// JustEnteredActive — the FIRST Active tick — exactly like Crossover (see
// PlayerController.TickCommittedMoveBehavior's shared
// "Crossover or BehindTheBack or BetweenTheLegs or InAndOut" branch, which
// calls HandStateResolver.Opposite(HandSide) unconditionally for all three
// non-InAndOut members). Because they additionally share Crossover's exact
// Startup/Active frame counts (6/3 ticks — see each move's own
// DefaultFrameData), the SAME "old-hand-targeted, mistimed-for-the-normal-
// window" schedule discriminates them too: the flip completes several ticks
// before the defender's Active window opens, so the normal window's hand
// axis is unsatisfiable for the whole run, and only the transit window
// (which reads the sweep's spatial GlobalPosition, not the hand) can
// connect. ComputeStealBeginFrameAfterFlip derives the schedule from each
// move's LIVE DefaultFrameData rather than hardcoding "5" a second and third
// time, so this survives a future #238 retune that makes BehindTheBack's or
// BetweenTheLegs' Startup/Active diverge from Crossover's.
//
// transit-steal-spin / out-of-reach-recovery-spin: same headline/control
// shape again, but Spin (#201) needs a DIFFERENT discriminator derivation,
// not the crossover trick blindly reapplied — Spin's hand swap fires on the
// LAST Active tick (FrameInPhase == ActiveFrames - 1), not JustEnteredActive
// (see Spin's own class doc's "swap at the END" section and Spin's branch in
// TickCommittedMoveBehavior). ComputeStealBeginFrameAfterFlip's
// flipAtActiveEnd parameter re-derives the flip tick as
// sweepBeginFrame + StartupFrames + (ActiveFrames - 1) for Spin instead of
// + StartupFrames alone, then schedules the steal so its OWN Active window
// still opens comfortably after that later flip — the same "normal window's
// hand axis is unsatisfiable for the whole Active window" proof, just timed
// against Spin's real (later) flip point instead of assuming Crossover's.

public partial class TransitStealTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int VerdictMarginFrames = 10;
    private const int FlipToActiveOpenMarginTicks = 3; // matches transit-steal's own ~3-tick margin (crossover flip ~frame 10, steal Active opens frame 13)

    // Same defaults DefensiveResolutionTests/BallController ship —
    // StealReachRadius default is 2.2 m (ADR-0014 arm's-length anchor).
    private static readonly Vector3 NearDefenderOffset = new(0.3f, 0f, 0.3f);   // ~0.42 m from holder — well within reach
    private static readonly Vector3 FarDefenderOffset = new(20f, 0f, 20f);      // far outside any plausible reach radius

    // Which sweep move the scenario begins on the holder. Generalizes what
    // used to be a Crossover-only field so #261 can drive BehindTheBack/
    // BetweenTheLegs/Spin through the exact same schedule/verdict plumbing.
    private enum SweepMoveKind { Crossover, BehindTheBack, BetweenTheLegs, Spin }

    private string _scenario = "transit-steal";
    private SweepMoveKind _sweepMoveKind = SweepMoveKind.Crossover;

    private BallController _ball;
    private PlayerController _holder;
    private PlayerController _defender;

    private int _frame;
    private double _elapsed;
    private bool _finished;

    // Scheduled action frames.
    private int _sweepMoveBeginFrame = int.MaxValue; // disabled for normal-window-unchanged
    private int _stealBeginFrame;
    private int _verdictFrame;

    private bool _sweepMoveBegun;
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
                _sweepMoveKind = SweepMoveKind.Crossover;
                _defender.GlobalPosition = _holder.GlobalPosition + NearDefenderOffset;
                _sweepMoveBeginFrame = 4;
                _stealBeginFrame = 5;
                _verdictFrame = ComputeVerdictFrame();
                break;

            case "out-of-reach-recovery":
                _sweepMoveKind = SweepMoveKind.Crossover;
                _defender.GlobalPosition = _holder.GlobalPosition + FarDefenderOffset;
                _sweepMoveBeginFrame = 4;
                _stealBeginFrame = 5;
                _verdictFrame = ComputeVerdictFrame();
                break;

            case "normal-window-unchanged":
                // No crossover — a plain live-dribble steal, matching hand,
                // timed against the exposed phase band exactly like
                // StealTurnoverTest's "success" scenario. Defender position
                // is irrelevant here (the normal window carries no spatial
                // axis), so it's left at the near offset for simplicity.
                _defender.GlobalPosition = _holder.GlobalPosition + NearDefenderOffset;
                _stealBeginFrame = ComputeInBandBeginFrame();
                _verdictFrame = ComputeVerdictFrame();
                break;

            // ── #261: non-crossover sweep paths ─────────────────────────────
            // BehindTheBack/BetweenTheLegs share Crossover's exact Startup(6)/
            // Active(3) frame counts and flip HandSide on the SAME
            // JustEnteredActive tick (see class doc), so the SAME literal
            // sweepBeginFrame=4/stealBeginFrame=5 schedule discriminates them
            // too — re-derived below via ComputeStealBeginFrameAfterFlip
            // (flipAtActiveEnd: false) rather than re-hardcoded, so a future
            // retune that makes their timings diverge from Crossover's fails
            // loud (a too-early steal Active window) instead of silently
            // going vacuous.
            case "transit-steal-behind-the-back":
                _sweepMoveKind = SweepMoveKind.BehindTheBack;
                _defender.GlobalPosition = _holder.GlobalPosition + NearDefenderOffset;
                _sweepMoveBeginFrame = 4;
                _stealBeginFrame = ComputeStealBeginFrameAfterFlip(
                    _sweepMoveBeginFrame, BehindTheBack.DefaultFrameData, flipAtActiveEnd: false);
                _verdictFrame = ComputeVerdictFrame();
                break;

            case "out-of-reach-recovery-behind-the-back":
                _sweepMoveKind = SweepMoveKind.BehindTheBack;
                _defender.GlobalPosition = _holder.GlobalPosition + FarDefenderOffset;
                _sweepMoveBeginFrame = 4;
                _stealBeginFrame = ComputeStealBeginFrameAfterFlip(
                    _sweepMoveBeginFrame, BehindTheBack.DefaultFrameData, flipAtActiveEnd: false);
                _verdictFrame = ComputeVerdictFrame();
                break;

            case "transit-steal-between-the-legs":
                _sweepMoveKind = SweepMoveKind.BetweenTheLegs;
                _defender.GlobalPosition = _holder.GlobalPosition + NearDefenderOffset;
                _sweepMoveBeginFrame = 4;
                _stealBeginFrame = ComputeStealBeginFrameAfterFlip(
                    _sweepMoveBeginFrame, BetweenTheLegs.DefaultFrameData, flipAtActiveEnd: false);
                _verdictFrame = ComputeVerdictFrame();
                break;

            case "out-of-reach-recovery-between-the-legs":
                _sweepMoveKind = SweepMoveKind.BetweenTheLegs;
                _defender.GlobalPosition = _holder.GlobalPosition + FarDefenderOffset;
                _sweepMoveBeginFrame = 4;
                _stealBeginFrame = ComputeStealBeginFrameAfterFlip(
                    _sweepMoveBeginFrame, BetweenTheLegs.DefaultFrameData, flipAtActiveEnd: false);
                _verdictFrame = ComputeVerdictFrame();
                break;

            // Spin (#201) flips HandSide on its LAST Active tick, not its
            // first (see class doc's "swap at the END" section) — a
            // genuinely later, differently-derived flip point, not the
            // family default. flipAtActiveEnd: true re-derives the schedule
            // around that later flip instead of assuming Crossover's timing.
            case "transit-steal-spin":
                _sweepMoveKind = SweepMoveKind.Spin;
                _defender.GlobalPosition = _holder.GlobalPosition + NearDefenderOffset;
                _sweepMoveBeginFrame = 4;
                _stealBeginFrame = ComputeStealBeginFrameAfterFlip(
                    _sweepMoveBeginFrame, Spin.DefaultFrameData, flipAtActiveEnd: true);
                _verdictFrame = ComputeVerdictFrame();
                break;

            case "out-of-reach-recovery-spin":
                _sweepMoveKind = SweepMoveKind.Spin;
                _defender.GlobalPosition = _holder.GlobalPosition + FarDefenderOffset;
                _sweepMoveBeginFrame = 4;
                _stealBeginFrame = ComputeStealBeginFrameAfterFlip(
                    _sweepMoveBeginFrame, Spin.DefaultFrameData, flipAtActiveEnd: true);
                _verdictFrame = ComputeVerdictFrame();
                break;

            default:
                Fail($"unknown scenario '{_scenario}'.");
                Finish();
                return;
        }

        GD.Print($"[transit-steal] scenario={_scenario}, sweepMoveKind={_sweepMoveKind}, " +
                 $"sweepMoveBeginFrame={_sweepMoveBeginFrame}, stealBeginFrame={_stealBeginFrame}, " +
                 $"verdictFrame={_verdictFrame}, reachRadius={_ball.StealReachRadius:F2}");
    }

    /// <summary>
    /// The steal's own Active window is what has to open AFTER the sweep
    /// move's HandSide flip for the "normal window's hand axis is
    /// unsatisfiable throughout" proof to hold. Re-derived from the LIVE
    /// MoveFrameData of whichever move is under test (never hardcoded) so
    /// this survives a future #238 retune of any move's Startup/Active
    /// counts. flipAtActiveEnd selects which timing rule the move follows:
    /// false for the JustEnteredActive family (Crossover/BehindTheBack/
    /// BetweenTheLegs), true for Spin's "swap at the END of Active" rule.
    /// </summary>
    private static int ComputeStealBeginFrameAfterFlip(int sweepBeginFrame, MoveFrameData sweepFrameData, bool flipAtActiveEnd)
    {
        int flipFrame = sweepBeginFrame + sweepFrameData.StartupFrames
            + (flipAtActiveEnd ? sweepFrameData.ActiveFrames - 1 : 0);
        return flipFrame + FlipToActiveOpenMarginTicks - StealMove.DefaultFrameData.StartupFrames;
    }

    private int ComputeVerdictFrame() =>
        _stealBeginFrame
            + StealMove.DefaultFrameData.StartupFrames
            + StealMove.DefaultFrameData.ActiveFrames
            + VerdictMarginFrames;

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
        if (!_sweepMoveBegun && _frame == _sweepMoveBeginFrame)
        {
            // BurstDirection/SpinDirection sign is irrelevant to the
            // discriminator (the HandSide flip always goes Left->Right from
            // the holder's default starting hand, regardless of which way
            // the burst/rotation points) — 1f for every move, matching the
            // original Crossover(1f).
            CommittedMove sweepMove = _sweepMoveKind switch
            {
                SweepMoveKind.BehindTheBack  => new BehindTheBack(1f),
                SweepMoveKind.BetweenTheLegs => new BetweenTheLegs(1f),
                SweepMoveKind.Spin           => new Spin(1f),
                _                            => new Crossover(1f),
            };
            bool began = _holder.BeginMoveForHarness(sweepMove);
            _sweepMoveBegun = true;
            if (!began)
            {
                Fail($"BeginMoveForHarness({_sweepMoveKind}) returned false — holder machine was not Inactive.");
                Finish();
                return;
            }
            GD.Print($"[transit-steal] frame {_frame}: holder begun {_sweepMoveKind}.");
        }

        if (!_stealBegun && _frame == _stealBeginFrame)
        {
            // TargetHand.Left is the holder's STARTING hand (HandSide
            // defaults Left). For every sweep-move scenario this is
            // deliberately the OLD hand — the sweep move flips
            // holder.HandSide to Right several ticks before this steal's
            // Active window even opens (see class doc), so the normal
            // window's side axis is guaranteed to fail for the WHOLE Active
            // window regardless of dribble phase. For normal-window-unchanged
            // there is no sweep move at all, so Left stays correct
            // throughout — the union's window (a) branch.
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

        // Matched by PREFIX, not exact scenario name: every "transit-steal*"
        // scenario (crossover, behind-the-back, between-the-legs, spin)
        // shares the exact same headline claim, and every
        // "out-of-reach-recovery*" scenario shares the exact same control
        // claim — the sweep move under test differs, but what "success"
        // means does not, so one verdict body serves the whole family
        // instead of four near-identical copies.
        if (_scenario.StartsWith("transit-steal"))
        {
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
        }
        else if (_scenario.StartsWith("out-of-reach-recovery"))
        {
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
        }
        else // "normal-window-unchanged"
        {
            pass = _everLoose && _toucherAtSteal == 2;
            detail = $"everLoose={_everLoose}, toucherAtSteal={_toucherAtSteal}, finalState={_ball.State}";
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
