using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Player;
using Hooper.Moves;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #206 (ADR-0018 Amendment
// 2026-07-19, human-decided Option A "pump-fake window"): the Held-ball
// steal check. Unit tests (DefensiveResolutionTests.HeldStealSucceeds_*)
// already pin the pure interval-overlap predicate; what they CANNOT reach is
// the live glue — PlayerController.HeldStealVulnerableWindow reading a REAL
// CommittedMoveMachine's Startup/feint-Recovery phase, and
// BallController.ResolveHeldStealAttempts composing it against a REAL
// defender's StealMove Active window inside the actual per-tick server
// pipeline. This scene proves that glue end to end (ADR-0016).
//
//   godot --headless --path . res://tests/integration/HeldStealTest.tscn -- --harness-scenario=held-vulnerable
//   godot --headless --path . res://tests/integration/HeldStealTest.tscn -- --harness-scenario=held-immune-outside-window
//   godot --headless --path . res://tests/integration/HeldStealTest.tscn -- --harness-scenario=pumpfake-now-exposed
//   Exit: 0 = PASS, 1 = FAIL (ADR-0016 exit-code contract).
//
// ── The scenarios ──────────────────────────────────────────────────────────
// held-vulnerable: holder begins a JumpShot; the defender's StealMove Active
//   window is scheduled generously inside the holder's Startup — the ball
//   must leave Held via GoLoose, with the defender as last toucher.
// held-immune-outside-window: CONTROL. Identical geometry/players, but the
//   defender's StealMove is scheduled to fully complete (including its own
//   Recovery) BEFORE the holder ever begins a JumpShot — HeldStealVulnerableWindow
//   is null for the entire duration the defender is Active, so the steal must
//   never connect and the ball must stay Held throughout (the "every
//   X-didn't-happen needs a control" law — without this, held-vulnerable's
//   PASS would be equally consistent with "the setup never worked at all").
// pumpfake-now-exposed: HEADLINE. Reproduces the Phase-0.5 degenerate exchange
//   this issue exists to kill: the ball starts DRIBBLING (the historical
//   live-dribble steal read this bug was originally described against); the
//   defender begins a StealMove reading that dribble; the HOLDER senses it
//   and begins a JumpShot BEFORE the defender's Active window opens, cradling
//   Dribbling->Held (the pre-#206 code's Dribbling-only guard would see a
//   Held ball by the time Active opens and whiff completely — exactly the
//   exploit), then pump-fakes (Feint()) the shot away. Under the fix, the
//   defender's Active window overlaps the holder's Startup/feint-Recovery
//   vulnerable window and the steal connects anyway — mashing the pump-fake
//   no longer saves the holder.

public partial class HeldStealTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int VerdictMarginFrames = 8;

    private string _scenario = "held-vulnerable";

    private BallController _ball;
    private PlayerController _holder;
    private PlayerController _defender;

    private int _frame;
    private double _elapsed;
    private bool _finished;

    // Scheduled action frames (scenario-dependent). int.MaxValue disables an
    // action for scenarios that don't need it.
    private int _jumpShotBeginFrame;
    private int _stealBeginFrame;
    private int _feintRetryStartFrame = int.MaxValue;
    private int _feintRetryEndFrame = int.MaxValue;
    private int _verdictFrame;

    private bool _jumpShotBegun;
    private bool _stealBegun;
    private bool _feinted;

    // Latched at the FIRST tick the ball is ever observed Loose — the
    // scramble's later AwardPossession legitimately overwrites
    // LastToucherPeerId once someone recovers the ball, which would mask
    // the exact bug this assertion targets (same discipline as
    // StealTurnoverTest's _toucherAtSteal).
    private bool _everLoose;
    private int _toucherAtSteal = -1;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "held-vulnerable");
        GD.Print($"[held-steal] scenario={_scenario} booting headless…");

        // Code-built tree, Players before Ball (hooper-architecture-contract
        // invariant #3 — see StealTurnoverTest/BlockTurnoverTest for the same
        // convention and why it's load-bearing for same-frame read/write
        // ordering between the defender's committed-move machine and the
        // ball's per-tick resolution).
        var players = new Node3D { Name = "Players" };
        _holder = new PlayerController { Name = "1" };
        _defender = new PlayerController { Name = "2" };
        players.AddChild(_holder);
        players.AddChild(_defender);

        _ball = new BallController { Name = "Ball", Players = players };

        AddChild(players);
        AddChild(_ball);

        // Force-set geometry rather than trust code-built-tree defaults
        // (the #217 trap) — a small separation so the steal's knock
        // direction is non-degenerate; not load-bearing for pass/fail
        // (DefensiveKnockDirection.SafeHorizontal falls back to Zero on
        // coincident positions with no crash either way).
        _holder.GlobalPosition = new Vector3(0, 0, 0);
        _defender.GlobalPosition = new Vector3(1, 0, 1);

        switch (_scenario)
        {
            case "held-vulnerable":
                // Ball stays Held (default post-#193 tipoff — no StartDribble
                // call). The defender's Active window is scheduled generously
                // inside the holder's 18-tick Startup so the overlap is
                // unambiguous regardless of the tree's one-tick read skew
                // (Players ticks before Ball each frame).
                _jumpShotBeginFrame = 5;
                _stealBeginFrame = 10; // Active opens ~18 — comfortably inside Startup [5, ~23)
                _verdictFrame = _stealBeginFrame
                    + StealMove.DefaultFrameData.StartupFrames
                    + StealMove.DefaultFrameData.ActiveFrames
                    + VerdictMarginFrames;
                break;

            case "held-immune-outside-window":
                // CONTROL: the defender's ENTIRE StealMove — Startup, Active,
                // AND its own Recovery — completes before the holder ever
                // begins a JumpShot. HeldStealVulnerableWindow is null for
                // every tick the defender's machine is Active, so the steal
                // must never connect.
                _stealBeginFrame = 5;
                _jumpShotBeginFrame = _stealBeginFrame
                    + StealMove.DefaultFrameData.StartupFrames
                    + StealMove.DefaultFrameData.ActiveFrames
                    + StealMove.DefaultFrameData.RecoveryFrames
                    + 10; // generous margin past the defender's own Recovery
                _verdictFrame = _jumpShotBeginFrame + 10;
                break;

            case "pumpfake-now-exposed":
                // Force the historical live-dribble premise the exploit was
                // originally described against (campaign Phase 0.5): a steal
                // timed to the exposed DRIBBLE band gets dodged because the
                // holder cradles into Held before the defender's Active
                // window opens.
                _ball.StateMachine.StartDribble();
                _jumpShotBeginFrame = 5; // cradles Dribbling->Held well before...
                _stealBeginFrame = 7;    // ...the defender's Active window opens (~15)
                _feintRetryStartFrame = _jumpShotBeginFrame + 4; // FrameInPhase ~3-4, inside the legal [3,12) feint window
                _feintRetryEndFrame = _jumpShotBeginFrame + 10;  // stay comfortably inside the window
                _verdictFrame = _stealBeginFrame
                    + StealMove.DefaultFrameData.StartupFrames
                    + StealMove.DefaultFrameData.ActiveFrames
                    + VerdictMarginFrames + 10;
                break;

            default:
                Fail($"unknown scenario '{_scenario}'.");
                Finish();
                return;
        }

        GD.Print($"[held-steal] jumpShotBeginFrame={_jumpShotBeginFrame}, " +
                 $"stealBeginFrame={_stealBeginFrame}, verdictFrame={_verdictFrame}");
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        // Fire scheduled actions from Root BEFORE Players/Ball tick this
        // frame (tree pre-order guarantees Root runs first — same
        // convention as StealTurnoverTest).
        if (!_jumpShotBegun && _frame == _jumpShotBeginFrame)
        {
            bool begun = _holder.BeginMoveForHarness(new JumpShot());
            _jumpShotBegun = true;
            if (!begun)
            {
                Fail("BeginMoveForHarness(JumpShot) returned false — holder machine was not Inactive.");
                Finish();
                return;
            }
            GD.Print($"[held-steal] frame {_frame}: holder begun JumpShot.");
        }

        if (!_stealBegun && _frame == _stealBeginFrame)
        {
            bool begun = _defender.BeginMoveForHarness(new StealMove(HandSide.Left));
            _stealBegun = true;
            if (!begun)
            {
                Fail("BeginMoveForHarness(StealMove) returned false — defender machine was not Inactive.");
                Finish();
                return;
            }
            GD.Print($"[held-steal] frame {_frame}: defender begun StealMove.");
        }

        if (!_feinted && _frame >= _feintRetryStartFrame && _frame <= _feintRetryEndFrame)
        {
            if (_holder.FeintForHarness())
            {
                _feinted = true;
                GD.Print($"[held-steal] frame {_frame}: holder pump-faked the JumpShot away.");
            }
        }

        // Latch the turnover the first tick it's observed, mirroring
        // StealTurnoverTest's discipline exactly (the scramble's own later
        // AwardPossession legitimately overwrites LastToucherPeerId once
        // someone recovers the ball).
        if (_ball.State == BallState.Loose)
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
        bool pass;
        string detail;

        if (_scenario == "held-immune-outside-window")
        {
            // CONTROL: the steal must NEVER connect — ball stays Held the
            // whole run, holder unchanged.
            pass = !_everLoose && _ball.State == BallState.Held
                && _ball.StateMachine.HolderPeerId == 1;
            detail = $"everLoose={_everLoose}, finalState={_ball.State}, holder={_ball.StateMachine.HolderPeerId}";
        }
        else
        {
            // held-vulnerable / pumpfake-now-exposed: the steal MUST connect
            // — the ball went Loose with the defender (peer 2) as the
            // toucher. Deliberately NOT asserting a final "settled back into
            // Held" state here (unlike StealTurnoverTest's Dribbling-path
            // verdict): ResolveStealSuccess reuses the EXACT SAME
            // GoLoose/_arc-seed/EndResolvedDefensiveMove code path the
            // already-proven Dribbling steal uses (extracted verbatim, not
            // reimplemented), so the #96 unseeded-_arc NRE regression is
            // already covered there and inherits identically here — no new
            // risk this scenario needs to re-prove. What this scenario's
            // "settles into" WOULD additionally exercise is issue #189
            // (phantom shot): the ORIGINAL shooter's CommittedMoveMachine is
            // never interrupted by a defensive turnover (only the
            // DEFENDER's move gets EndResolvedDefensiveMove'd), so if the
            // proximity-scramble happens to recover the loose ball back to
            // that same original holder — likely here, since they haven't
            // moved — their still-running JumpShot can reach Active on
            // schedule and re-release the ball, a KNOWN, separately-tracked,
            // deliberately out-of-scope gap (see this PR's body). Asserting
            // a final Held/holder state here would make this scenario's
            // pass/fail depend on that unrelated gap instead of on what
            // issue #206 actually fixes.
            pass = _everLoose && _toucherAtSteal == 2;
            detail = $"everLoose={_everLoose}, toucherAtSteal={_toucherAtSteal}, " +
                $"finalState={_ball.State}, holder={_ball.StateMachine.HolderPeerId}";
        }

        if (pass)
            GD.Print($"[held-steal] PASS — scenario={_scenario}, {detail}.");
        else
            Fail($"scenario={_scenario} — {detail}.");

        Finish(pass ? 0 : 1);
    }

    private void Fail(string message) => GD.PrintErr($"[held-steal] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[held-steal] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
