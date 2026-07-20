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
//   godot --headless --path . res://tests/integration/HeldStealTest.tscn -- --harness-scenario=held-static-vulnerable
//   godot --headless --path . res://tests/integration/HeldStealTest.tscn -- --harness-scenario=held-static-immune-out-of-reach
//   godot --headless --path . res://tests/integration/HeldStealTest.tscn -- --harness-scenario=held-static-immune-shielded
//   godot --headless --path . res://tests/integration/HeldStealTest.tscn -- --harness-scenario=held-static-immune-wrong-side
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
//
// ── Issue #255 (ADR-0018 Amendment 2026-07-20, Route A static exposure) ────
// held-static-vulnerable: HEADLINE for #255. The holder never begins ANY
//   committed move at all — a plain, idle "dead-Held staller" possession,
//   the exact case #206 left untouched (HeldStealVulnerableWindow stays null
//   the entire run). The defender is positioned within HeldStealReachRadius
//   AND on the holder's exposed hand-side cone. Exposed-side geometry is
//   LOCKED to BallController.HandRight/HandSign, the same formula the ball
//   render itself uses (NOT a naive "Right hand = world +X" assumption —
//   Godot's +Z-forward, right-handed convention makes a facing-+Z player's
//   actual right point toward world -X; this file's first version had this
//   backwards and code review caught it before merge): default HandSide.Left
//   at heading 0 carries the ball toward world +X. Times a real StealMove
//   Active window; the turnover must connect via the NEW static term alone —
//   proving the previously-untouchable staller now loses the ball.
// held-static-immune-out-of-reach: CONTROL. Identical on-axis geometry and
//   timing, but the defender is placed well beyond HeldStealReachRadius —
//   the steal must NOT connect, ball stays Held (without this control,
//   held-static-vulnerable's PASS would be equally consistent with "any
//   defender near a StealMove connects regardless of reach").
// held-static-immune-shielded: CONTROL. Identical in-reach, on-original-axis
//   geometry, but the holder's Heading is force-set 180 degrees away
//   (SetHeadingForHarness) BEFORE the steal resolves — rotating the exposed
//   cone off the defender entirely (the "turn the body to shield the ball"
//   counter the issue's design contract requires). The steal must NOT
//   connect (without this control, held-static-vulnerable's PASS would be
//   equally consistent with "proximity alone is sufficient, facing does
//   nothing"). NOTE: this control is left/right SYMMETRIC — a mirrored
//   hand-side convention would still pass it, which is exactly how the
//   mirror bug above survived this control alone; see
//   held-static-immune-wrong-side below for the control that actually
//   discriminates handedness.
// held-static-immune-wrong-side: CONTROL (code-review-added). Identical
//   heading/timing to held-static-vulnerable, defender within
//   HeldStealReachRadius, but on the PROTECTED off-hand side (world -X, the
//   RIGHT-hand axis) rather than the exposed side (the ball is actually
//   carried Left/+X). This is NOT axis-symmetric like the shielded control
//   above — it fails to discriminate a mirrored convention, which is exactly
//   the bug this scenario was added to pin. The steal must NOT connect.

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
                //
                // Defender placed directly in front (+Z) — deliberately OFF
                // #255's static exposure cone (90 degrees off either hand
                // axis) — so this scenario isolates the #206 pump-fake
                // window ALONE. HeldStealSucceeds is timing-only (no reach/
                // facing term), so the pump-fake steal still connects from
                // here; without this, the shared default geometry (1,0,1) is
                // ALSO inside the static cone for the holder's default
                // Left-hand carry, so a regression that broke
                // HeldStealSucceeds could be masked by the static term
                // resolving the same steal for the wrong reason (the exact
                // "a scenario must isolate its axis" failure the #255 mirror
                // bug taught — see held-immune-outside-window below).
                _defender.GlobalPosition = new Vector3(0, 0, 1);
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
                // every tick the defender's machine is Active, so the
                // PUMP-FAKE path must never connect. This scenario predates
                // #255 and isolates ONLY the pump-fake timing axis — since
                // #255 added a SECOND, independent way to steal a Held ball
                // (the static reach+facing term, exercised by its own
                // dedicated held-static-* scenarios below), the shared
                // default defender geometry (1,0,1) — which happens to sit
                // inside the default HeldStealReachRadius/cone for the
                // holder's default Left-hand carry — must be moved OFF that
                // static cone here, or this control would spuriously connect
                // via the static term and no longer isolate the axis it was
                // written to test. Directly in front (+Z) is 90 degrees off
                // either hand-side axis, defeating the facing term
                // regardless of HandSide.
                _defender.GlobalPosition = new Vector3(0, 0, 1);
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
                //
                // Defender placed directly in front (+Z) — same rationale as
                // held-vulnerable above: deliberately off #255's static
                // exposure cone so this scenario isolates the #206 pump-fake
                // window alone (the shared default geometry (1,0,1) is
                // inside that cone for the holder's default Left-hand carry
                // and would let the static term mask a pump-fake-window
                // regression here too).
                _defender.GlobalPosition = new Vector3(0, 0, 1);
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

            case "held-static-vulnerable":
                // Issue #255 headline: NO JumpShot ever begins (a plain idle
                // "dead-Held staller" — HeldStealVulnerableWindow stays null
                // for the whole run, so success can ONLY come from the new
                // static term). Holder's default HandSide is Left and
                // Heading is 0 (Forward == +Z); the exposed hand-side
                // direction is LOCKED to the same formula the ball render
                // uses (BallController.HandRight/HandSign, code-review-
                // caught: an earlier version of this predicate/scenario had
                // this mirrored) — forward==(0,1) -> right==(-1,0) -> Left's
                // handSign(-1) flips it to world +X. Place the defender
                // squarely on that axis, well inside the default
                // HeldStealReachRadius (2.2m).
                _jumpShotBeginFrame = int.MaxValue; // disabled
                _defender.GlobalPosition = new Vector3(1, 0, 0);
                _stealBeginFrame = 5;
                _verdictFrame = _stealBeginFrame
                    + StealMove.DefaultFrameData.StartupFrames
                    + StealMove.DefaultFrameData.ActiveFrames
                    + VerdictMarginFrames;
                break;

            case "held-static-immune-out-of-reach":
                // CONTROL: identical on-axis geometry/timing to
                // held-static-vulnerable, but the defender sits well beyond
                // the default HeldStealReachRadius (2.2m) — the reach axis
                // alone must refuse the steal regardless of facing.
                _jumpShotBeginFrame = int.MaxValue; // disabled
                _defender.GlobalPosition = new Vector3(10, 0, 0);
                _stealBeginFrame = 5;
                _verdictFrame = _stealBeginFrame
                    + StealMove.DefaultFrameData.StartupFrames
                    + StealMove.DefaultFrameData.ActiveFrames
                    + VerdictMarginFrames;
                break;

            case "held-static-immune-shielded":
                // CONTROL: identical in-reach, on-original-axis geometry to
                // held-static-vulnerable (defender at world +X, the holder's
                // actual Left-hand carry side at heading 0), but the
                // holder's Heading is force-rotated 180 degrees BEFORE the
                // steal resolves (SetHeadingForHarness — a bare headless
                // second node has no input path that would ever advance
                // Heading via Move()->HeadingMath.Step, so setup must force
                // it directly, same rationale as FadeawayTriggerTest's use
                // of the same seam). The exposed hand-side direction rotates
                // WITH Heading (forward(pi)==(0,-1) -> right==(1,0) ->
                // Left's handSign(-1) flips it to world -X), so the SAME
                // defender position that was on-axis is now squarely
                // off-cone — the "turn the body to shield the ball" counter.
                _jumpShotBeginFrame = int.MaxValue; // disabled
                _defender.GlobalPosition = new Vector3(1, 0, 0);
                _holder.SetHeadingForHarness(Mathf.Pi);
                _stealBeginFrame = 5;
                _verdictFrame = _stealBeginFrame
                    + StealMove.DefaultFrameData.StartupFrames
                    + StealMove.DefaultFrameData.ActiveFrames
                    + VerdictMarginFrames;
                break;

            case "held-static-immune-wrong-side":
                // CONTROL (code-review-added, catches the exact mirror bug
                // this scenario file's first version had): identical
                // heading/timing to held-static-vulnerable, defender WITHIN
                // HeldStealReachRadius, but on the PROTECTED off-hand side
                // (world -X — the RIGHT-hand axis, since the ball is
                // actually carried Left/+X) rather than the exposed side.
                // Unlike held-static-immune-shielded (which is symmetric
                // under a left/right mirror-up — a flipped convention would
                // still pass it), this control is NOT axis-symmetric: it
                // fails to discriminate only if the facing term is dropped
                // entirely, and it fails to PASS if the exposed axis is
                // computed on the wrong side. The steal must NOT connect.
                _jumpShotBeginFrame = int.MaxValue; // disabled
                _defender.GlobalPosition = new Vector3(-1, 0, 0);
                _stealBeginFrame = 5;
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

        bool isControlScenario = _scenario == "held-immune-outside-window"
            || _scenario == "held-static-immune-out-of-reach"
            || _scenario == "held-static-immune-shielded"
            || _scenario == "held-static-immune-wrong-side";

        if (isControlScenario)
        {
            // CONTROL: the steal must NEVER connect — ball stays Held the
            // whole run, holder unchanged.
            pass = !_everLoose && _ball.State == BallState.Held
                && _ball.StateMachine.HolderPeerId == 1;
            detail = $"everLoose={_everLoose}, finalState={_ball.State}, holder={_ball.StateMachine.HolderPeerId}";
        }
        else
        {
            // held-vulnerable / pumpfake-now-exposed / held-static-vulnerable:
            // the steal MUST connect — the ball went Loose with the defender
            // (peer 2) as the
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
