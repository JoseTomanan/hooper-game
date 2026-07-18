using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #202 (ADR-0016): the in-and-out —
// the crossover's twin, same telegraph, no hand swap. Unit tests already pin
// the pure frame-data/identity contract (InAndOutTests.cs), the gesture-
// meaning composition (InAndOutTests' AC-1/AC-8 pair), and the rename
// (RightStickGestureRecognizerTests.cs). What they CANNOT reach is the live
// engine glue this harness proves:
//   1. The REAL RightStickGestureRecognizer -> SampleMoveInput dispatch
//      actually retargets the quick-return gesture to InAndOut/Hesitation
//      through real synthetic input (Input.ActionPress), not a harness seam
//      bypass — proving the WIRING, not just the pure decision.
//   2. InAndOut leaves HandSide untouched across its full lifecycle,
//      contrasted against a REAL Crossover which flips it (AC-2).
//   3. The stationary+neutral-exit fallback burst travels toward the BALL
//      hand (the negated flick sign), contrasted against a REAL Crossover
//      under identical conditions, which bursts toward the empty hand (AC-4).
//   4. The dead-dribble gate refuses InAndOut from both live AND dead Held,
//      but permits it from live Dribbling — the control that catches the
//      #202 brief's "hardcoded type list" trap (AC-5).
//   5. Crossover/BehindTheBack are now structurally unfeintable (the ADR-0003
//      amendment), contrasted against JumpShot's pump-fake, which still works
//      (AC-6).
//   6. A client-initiated "inandout" RPC payload reconstructs correctly
//      server-side (AC-7).
//
//   godot --headless --path . res://tests/integration/InAndOutTest.tscn -- --harness-scenario=quick-return-empty-hand
//   godot --headless --path . res://tests/integration/InAndOutTest.tscn -- --harness-scenario=quick-return-ball-hand
//   godot --headless --path . res://tests/integration/InAndOutTest.tscn -- --harness-scenario=handside-unchanged
//   godot --headless --path . res://tests/integration/InAndOutTest.tscn -- --harness-scenario=fallback-toward-ball-hand
//   godot --headless --path . res://tests/integration/InAndOutTest.tscn -- --harness-scenario=dead-dribble-gate
//   godot --headless --path . res://tests/integration/InAndOutTest.tscn -- --harness-scenario=feint-refused
//   godot --headless --path . res://tests/integration/InAndOutTest.tscn -- --harness-scenario=reconstruct
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "quick-return-empty-hand".
//
// ── Why a single offline instance is the server ───────────────────────────
// Same reasoning as JabStepTest/BehindTheBackTest: with no MultiplayerPeer
// assigned, Godot uses OfflineMultiplayerPeer (is_server() hardcoded true,
// unique_id 1), so player "1" is both IsServer and IsLocalPlayer — its
// _machine.Tick()/SampleMoveInput advance every physics frame, the free
// clock every other Begin*ForHarness-driven harness in this repo relies on.
public partial class InAndOutTest : Node
{
    private const double TimeoutSeconds = 10.0;
    private const int ArmFrames = 2;          // ticks for TryAssignTipoffHolder to run
    private const int ActionMarginFrames = 3; // ticks to let an action's effect settle
    private const int FlickHoldTicks = 7;     // > FeintWindowTicks(4)+1 — commits the HELD gesture

    private string _scenario = "quick-return-empty-hand";

    private BallController _ball;
    private PlayerController _p1;

    private int _frame;
    private double _elapsed;
    private bool _finished;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "quick-return-empty-hand");
        GD.Print($"[in-and-out] scenario={_scenario} booting headless…");

        if (_scenario is "dead-dribble-gate")
        {
            // Needs a real BallController + tipoff for possession-state
            // scenarios (mirrors JabStepTest/BehindTheBackTest's ball setup).
            var players = new Node3D { Name = "Players" };
            _p1 = new PlayerController { Name = "1" };
            var p2 = new PlayerController { Name = "2" };
            players.AddChild(_p1);
            players.AddChild(p2);
            _ball = new BallController { Name = "Ball", Players = players };
            AddChild(players);
            AddChild(_ball);
            _step = Step.AwaitTipoff; // this scenario's step machine starts here, not Step.Start
        }
        else
        {
            // No Ball wrapper needed — mirrors BehindTheBackTest's
            // "narrower-exit-cone"/MovingCrossoverTest pattern: the gesture
            // branch and BeginMoveForHarness's dead-dribble gate are both
            // IsBallHolder-gated, and GetBall() returning null makes that gate
            // a no-op, isolating the behavior under test from possession state.
            _p1 = new PlayerController { Name = "1" };
            AddChild(_p1);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        switch (_scenario)
        {
            case "quick-return-empty-hand":     TickQuickReturnEmptyHand();     break;
            case "quick-return-ball-hand":       TickQuickReturnBallHand();      break;
            case "handside-unchanged":           TickHandSideUnchanged();        break;
            case "fallback-toward-ball-hand":    TickFallbackTowardBallHand();   break;
            case "dead-dribble-gate":            TickDeadDribbleGate();          break;
            case "feint-refused":                TickFeintRefused();             break;
            case "reconstruct":                  TickReconstruct();              break;
            default:
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

    // ── Shared step machine (simple linear sequence, mirrors JabStepTest) ────
    private enum Step
    {
        Start, FlickStarted, AwaitBegin, AwaitLifecycleDone,
        HeldFlickStarted, AwaitControlBegin, AwaitControlDone,
        AwaitTipoff, RefusedFromLiveHeld, CradleIssued, FeintIssued, AwaitDeadHeld,
        RefusedFromDeadHeld, DriveIssued, PermittedFromDribbling,
        AwaitActive, ControlAwaitActive, Done
    }
    private Step _step = Step.Start;
    private int _stepDeadlineFrame;
    private int _flickStartFrame = -1;

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario: "quick-return-empty-hand" (AC-1 + control)
    // Flick toward the EMPTY hand (default HandSide.Left -> aim_right),
    // released to deadzone WITHIN the recognizer's window -> InAndOut begins.
    // Control: the SAME flick HELD past the window -> Crossover begins.
    // ═══════════════════════════════════════════════════════════════════════
    private void TickQuickReturnEmptyHand()
    {
        switch (_step)
        {
            case Step.Start:
                Input.ActionPress("aim_right", 1.0f); // flickSign +1: empty hand (HandSide.Left default)
                _flickStartFrame = _frame;
                _step = Step.FlickStarted;
                return;

            case Step.FlickStarted:
                // Release WELL within the window (FeintWindowTicks=4 default) —
                // one tick above threshold is enough to start timing, then back
                // to the deadzone commits the "quick" gesture.
                if (_frame == _flickStartFrame + 1)
                    Input.ActionRelease("aim_right");
                if (_p1.CurrentMoveIdForHarness == "inandout")
                {
                    GD.Print($"[in-and-out] PASS quick-return-empty-hand — real gesture input began InAndOut at frame {_frame}.");
                    _step = Step.AwaitLifecycleDone;
                }
                else if (_p1.CurrentMoveIdForHarness == "crossover" || _p1.CurrentMoveIdForHarness == "hesitation")
                {
                    Fail($"quick-return-empty-hand: expected InAndOut, got '{_p1.CurrentMoveIdForHarness}' — gesture retarget did not fire correctly.");
                    Finish();
                }
                return;

            case Step.AwaitLifecycleDone:
                if (_p1.PhaseForHarness != MovePhase.Inactive) return;
                GD.Print("[in-and-out] PASS lifecycle — InAndOut ran its full Startup->Active->Recovery->Inactive cycle.");

                // Control: the SAME flick, HELD past the window, must begin a
                // Crossover instead — proving the retarget is a QUICK-RETURN-
                // specific effect, not a change to what the hold gesture does.
                Input.ActionPress("aim_right", 1.0f);
                _flickStartFrame = _frame;
                _step = Step.HeldFlickStarted;
                return;

            case Step.HeldFlickStarted:
                if (_frame == _flickStartFrame + FlickHoldTicks)
                    Input.ActionRelease("aim_right");
                if (_p1.CurrentMoveIdForHarness == "crossover")
                {
                    GD.Print("[in-and-out] PASS control — the SAME flick HELD past the window begins a Crossover.");
                    GD.Print("[in-and-out] RESULT: PASS (exit 0)");
                    Finish(0);
                }
                else if (_p1.CurrentMoveIdForHarness == "inandout")
                {
                    Fail("quick-return-empty-hand: control held-flick wrongly began InAndOut instead of Crossover.");
                    Finish();
                }
                return;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario: "quick-return-ball-hand" (AC-8 + control)
    // Flick toward the BALL hand (default HandSide.Left -> aim_left), quick
    // return -> Hesitation, IDENTICAL to the held gesture (the control).
    // ═══════════════════════════════════════════════════════════════════════
    private void TickQuickReturnBallHand()
    {
        switch (_step)
        {
            case Step.Start:
                Input.ActionPress("aim_left", 1.0f); // flickSign -1: ball hand (HandSide.Left default)
                _flickStartFrame = _frame;
                _step = Step.FlickStarted;
                return;

            case Step.FlickStarted:
                if (_frame == _flickStartFrame + 1)
                    Input.ActionRelease("aim_left");
                if (_p1.CurrentMoveIdForHarness == "hesitation")
                {
                    GD.Print($"[in-and-out] PASS quick-return-ball-hand — real gesture input began Hesitation at frame {_frame}.");
                    _step = Step.AwaitLifecycleDone;
                }
                else if (_p1.CurrentMoveIdForHarness == "inandout")
                {
                    Fail("quick-return-ball-hand: expected Hesitation, got InAndOut — ball-hand quick-return must not begin InAndOut.");
                    Finish();
                }
                return;

            case Step.AwaitLifecycleDone:
                if (_p1.PhaseForHarness != MovePhase.Inactive) return;

                // Control: the SAME flick HELD past the window must ALSO begin
                // a Hesitation (AC-8) — flick direction picks the family; hold
                // duration only disambiguates the empty-hand column.
                Input.ActionPress("aim_left", 1.0f);
                _flickStartFrame = _frame;
                _step = Step.HeldFlickStarted;
                return;

            case Step.HeldFlickStarted:
                if (_frame == _flickStartFrame + FlickHoldTicks)
                    Input.ActionRelease("aim_left");
                if (_p1.CurrentMoveIdForHarness == "hesitation")
                {
                    GD.Print("[in-and-out] PASS control — the SAME flick HELD past the window ALSO begins a Hesitation.");
                    GD.Print("[in-and-out] RESULT: PASS (exit 0)");
                    Finish(0);
                }
                return;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario: "handside-unchanged" (AC-2 + control)
    // ═══════════════════════════════════════════════════════════════════════
    private HandSide _handSideBefore;

    private void TickHandSideUnchanged()
    {
        switch (_step)
        {
            case Step.Start:
                _handSideBefore = _p1.HandSide;
                bool began = _p1.BeginMoveForHarness(new InAndOut(1f));
                if (!began)
                {
                    Fail("handside-unchanged: BeginMoveForHarness(InAndOut) returned false.");
                    Finish();
                    return;
                }
                _step = Step.AwaitLifecycleDone;
                return;

            case Step.AwaitLifecycleDone:
                if (_p1.PhaseForHarness != MovePhase.Inactive) return;
                if (_p1.HandSide != _handSideBefore)
                {
                    Fail($"handside-unchanged: expected HandSide to stay {_handSideBefore}, got {_p1.HandSide}.");
                    Finish();
                    return;
                }
                GD.Print($"[in-and-out] PASS handside-unchanged — HandSide stayed {_handSideBefore} across InAndOut's full lifecycle.");

                // Control: a REAL Crossover from the same starting hand DOES
                // flip it — without this control the assertion above would
                // pass vacuously if the move never actually began/ran.
                _handSideBefore = _p1.HandSide;
                bool controlBegan = _p1.BeginMoveForHarness(new Crossover(1f));
                if (!controlBegan)
                {
                    Fail("handside-unchanged: control BeginMoveForHarness(Crossover) returned false.");
                    Finish();
                    return;
                }
                _step = Step.AwaitControlDone;
                return;

            case Step.AwaitControlDone:
                if (_p1.PhaseForHarness != MovePhase.Inactive) return;
                if (_p1.HandSide == _handSideBefore)
                {
                    Fail($"handside-unchanged: control Crossover should have flipped HandSide away from {_handSideBefore}, but it stayed.");
                    Finish();
                    return;
                }
                GD.Print($"[in-and-out] PASS control — a Crossover DOES flip HandSide ({_handSideBefore} -> {_p1.HandSide}), proving InAndOut's non-effect is a real observation.");
                GD.Print("[in-and-out] RESULT: PASS (exit 0)");
                Finish(0);
                return;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario: "fallback-toward-ball-hand" (AC-4 + control)
    // Stationary + neutral left stick: InAndOut's fallback burst must travel
    // toward the BALL hand (negated flick sign); a Crossover under identical
    // conditions bursts toward the EMPTY hand (the control).
    // ═══════════════════════════════════════════════════════════════════════
    private void TickFallbackTowardBallHand()
    {
        switch (_step)
        {
            case Step.Start:
                // flickSign +1 = flick toward the empty (right) hand, same
                // convention Crossover's BurstDirection uses — HandSide.Left default.
                bool began = _p1.BeginMoveForHarness(new InAndOut(1f));
                if (!began)
                {
                    Fail("fallback-toward-ball-hand: BeginMoveForHarness(InAndOut) returned false.");
                    Finish();
                    return;
                }
                _step = Step.AwaitActive;
                return;

            case Step.AwaitActive:
                if (!(_p1.PhaseForHarness == MovePhase.Active)) return;
                // Heading is 0 (fresh player, no movement) so world X is the
                // lateral axis. InAndOut negates the sign before composing, so
                // the fallback impulse points toward the BALL hand: at
                // heading 0, HandStateResolver.BurstWorldDir(0, -1) = (+1, 0) —
                // a POSITIVE X velocity (see InAndOutTests' fallback contract
                // and CrossoverBurstMathTests' fallback pin for the sign math).
                if (_p1.Velocity.X <= 0f)
                {
                    Fail($"fallback-toward-ball-hand: expected a POSITIVE X burst (toward the ball hand), got Velocity.X={_p1.Velocity.X:F3}.");
                    Finish();
                    return;
                }
                GD.Print($"[in-and-out] PASS fallback-toward-ball-hand — Velocity.X={_p1.Velocity.X:F3} > 0 (toward the ball hand).");
                _step = Step.AwaitLifecycleDone;
                return;

            case Step.AwaitLifecycleDone:
                if (_p1.PhaseForHarness != MovePhase.Inactive) return;

                // Control: a REAL Crossover under IDENTICAL conditions bursts
                // toward the EMPTY hand — the opposite sign.
                bool controlBegan = _p1.BeginMoveForHarness(new Crossover(1f));
                if (!controlBegan)
                {
                    Fail("fallback-toward-ball-hand: control BeginMoveForHarness(Crossover) returned false.");
                    Finish();
                    return;
                }
                _step = Step.ControlAwaitActive;
                return;

            case Step.ControlAwaitActive:
                if (!(_p1.PhaseForHarness == MovePhase.Active)) return;
                if (_p1.Velocity.X >= 0f)
                {
                    Fail($"fallback-toward-ball-hand: control Crossover expected a NEGATIVE X burst (toward the empty hand), got Velocity.X={_p1.Velocity.X:F3}.");
                    Finish();
                    return;
                }
                GD.Print($"[in-and-out] PASS control — a Crossover's fallback bursts the OPPOSITE way (Velocity.X={_p1.Velocity.X:F3} < 0), proving the negated sign is InAndOut-specific.");
                GD.Print("[in-and-out] RESULT: PASS (exit 0)");
                Finish(0);
                return;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario: "dead-dribble-gate" (AC-5 + required control)
    // Refused from a fresh (live) Held possession AND from a dead Held
    // possession (post-cradle). Control: permitted from a live Dribbling
    // possession — the specific check that catches a hardcoded-type-list
    // omission (the #202 brief's named single most likely defect).
    // ═══════════════════════════════════════════════════════════════════════
    private int _holderId;

    private void TickDeadDribbleGate()
    {
        switch (_step)
        {
            case Step.AwaitTipoff:
                if (_frame < ArmFrames) return;
                if (_ball.StateMachine.HolderPeerId == 0)
                {
                    Fail("dead-dribble-gate: tipoff never assigned a holder.");
                    Finish();
                    return;
                }
                _holderId = _ball.StateMachine.HolderPeerId;
                if (_ball.State != BallState.Held || _ball.HasDribbled)
                {
                    Fail($"dead-dribble-gate: expected a fresh live Held possession at tipoff; got state={_ball.State}, HasDribbled={_ball.HasDribbled}.");
                    Finish();
                    return;
                }
                _step = Step.RefusedFromLiveHeld;
                return;

            case Step.RefusedFromLiveHeld:
            {
                PlayerController holder = NodeForPeer(_holderId);
                bool began = holder.BeginMoveForHarness(new InAndOut(1f));
                bool refused = !began && holder.PhaseForHarness == MovePhase.Inactive;
                if (!refused)
                {
                    Fail($"dead-dribble-gate: expected InAndOut refused from a LIVE Held possession; began={began}, phase={holder.PhaseForHarness}.");
                    Finish();
                    return;
                }
                GD.Print("[in-and-out] PASS refused-live-held — InAndOut refused from a fresh live Held possession.");

                // Start a LIVE dribble. The dead-dribble rule (#193) makes
                // HasDribbled a ONE-WAY latch for this possession — once dead
                // Held, TryStartDribble can never revive it — so the
                // permitted-from-Dribbling control MUST run now, before the
                // possession is cradled, not after.
                _ball.TryStartDribble(_holderId);
                _step = Step.DriveIssued;
                _stepDeadlineFrame = _frame + ActionMarginFrames;
                return;
            }

            case Step.DriveIssued:
                if (_frame < _stepDeadlineFrame) return;
                if (_ball.State != BallState.Dribbling)
                {
                    Fail($"dead-dribble-gate: expected TryStartDribble to reach Dribbling; got state={_ball.State}.");
                    Finish();
                    return;
                }
                _step = Step.PermittedFromDribbling;
                return;

            case Step.PermittedFromDribbling:
            {
                // The required control (AC-5): InAndOut IS permitted from a
                // LIVE Dribbling possession — the specific check that catches
                // a hardcoded-type-list omission (the #202 brief's named
                // single most likely defect). Begun and run to completion
                // here; InAndOut never cradles (unlike JumpShot/Layup/
                // DriveGather/EuroStep), so the ball stays Dribbling
                // throughout and HasDribbled stays false, leaving the
                // possession free to cradle afterward.
                PlayerController holder = NodeForPeer(_holderId);
                bool began = holder.BeginMoveForHarness(new InAndOut(1f));
                if (!began)
                {
                    Fail($"dead-dribble-gate: expected InAndOut PERMITTED from a live Dribbling possession (control); began={began}, phase={holder.PhaseForHarness}.");
                    Finish();
                    return;
                }
                GD.Print("[in-and-out] PASS control — InAndOut IS permitted from a live Dribbling possession.");
                _step = Step.AwaitControlDone;
                return;
            }

            case Step.AwaitControlDone:
            {
                if (NodeForPeer(_holderId).PhaseForHarness != MovePhase.Inactive) return;
                if (_ball.State != BallState.Dribbling || _ball.HasDribbled)
                {
                    Fail($"dead-dribble-gate: expected the ball to STILL be live Dribbling after the control InAndOut's full lifecycle; got state={_ball.State}, HasDribbled={_ball.HasDribbled}.");
                    Finish();
                    return;
                }

                // Now cradle a dribble (JumpShot begin -> feint away -> wait
                // for Recovery) to reach DEAD Held (HasDribbled=true), same
                // setup JabStepTest/TripleThreatTest use.
                PlayerController holder = NodeForPeer(_holderId);
                bool cradleBegan = holder.BeginJumpShotForHarness();
                if (!cradleBegan || !(_ball.State == BallState.Held && _ball.HasDribbled))
                {
                    Fail($"dead-dribble-gate: expected the JumpShot begin to cradle synchronously; began={cradleBegan}, state={_ball.State}, HasDribbled={_ball.HasDribbled}.");
                    Finish();
                    return;
                }
                _step = Step.CradleIssued;
                _stepDeadlineFrame = _frame + ActionMarginFrames;
                return;
            }

            case Step.CradleIssued:
                if (_frame < _stepDeadlineFrame) return;
                if (!NodeForPeer(_holderId).FeintForHarness())
                {
                    Fail("dead-dribble-gate: FeintForHarness() returned false — outside JumpShot's feint window.");
                    Finish();
                    return;
                }
                _step = Step.AwaitDeadHeld;
                return;

            case Step.AwaitDeadHeld:
                if (NodeForPeer(_holderId).PhaseForHarness != MovePhase.Inactive) return;
                if (!(_ball.State == BallState.Held && _ball.HasDribbled))
                {
                    Fail($"dead-dribble-gate: expected DEAD Held once the feinted shot's Recovery elapsed; got state={_ball.State}, HasDribbled={_ball.HasDribbled}.");
                    Finish();
                    return;
                }
                _step = Step.RefusedFromDeadHeld;
                return;

            case Step.RefusedFromDeadHeld:
            {
                PlayerController holder = NodeForPeer(_holderId);
                bool began = holder.BeginMoveForHarness(new InAndOut(1f));
                bool refused = !began && holder.PhaseForHarness == MovePhase.Inactive;
                if (!refused)
                {
                    Fail($"dead-dribble-gate: expected InAndOut refused from a DEAD Held possession; began={began}, phase={holder.PhaseForHarness}.");
                    Finish();
                    return;
                }
                GD.Print("[in-and-out] PASS refused-dead-held — InAndOut refused from a dead Held possession (HasDribbled=true).");
                GD.Print("[in-and-out] RESULT: PASS (exit 0)");
                Finish(0);
                return;
            }
        }
    }

    private PlayerController NodeForPeer(int peerId) => peerId == 1 ? _p1 : (PlayerController)_ball.Players.GetChild(peerId - 1);

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario: "feint-refused" (AC-6 + required control)
    // Crossover/BehindTheBack are now structurally unfeintable at every
    // Startup frame (feintWindowFrames: 0, ADR-0003 amendment). Control:
    // JumpShot's pump-fake still succeeds inside its legal [3, 12) window and
    // still consumes the shot WITHOUT ever reaching Active (no release).
    // ═══════════════════════════════════════════════════════════════════════
    private bool _everEnteredActive;

    private void TickFeintRefused()
    {
        switch (_step)
        {
            case Step.Start:
            {
                bool began = _p1.BeginMoveForHarness(new Crossover(1f));
                if (!began)
                {
                    Fail("feint-refused: BeginMoveForHarness(Crossover) returned false.");
                    Finish();
                    return;
                }
                _step = Step.FlickStarted; // reuse as "crossover-begun, tick 1 then feint"
                return;
            }

            case Step.FlickStarted:
            {
                // At FrameInPhase == 0 (frame this begun) a feint attempt must
                // already fail — feintWindowFrames == 0 refuses at EVERY frame.
                bool feinted = _p1.FeintForHarness();
                if (feinted || _p1.PhaseForHarness != MovePhase.Startup)
                {
                    Fail($"feint-refused: expected Crossover's feint refused at frame 0 of Startup; feinted={feinted}, phase={_p1.PhaseForHarness}.");
                    Finish();
                    return;
                }
                GD.Print("[in-and-out] PASS crossover-unfeintable — Feint() refused at Startup frame 0 (feintWindowFrames=0).");

                // Let Crossover's lifecycle finish, then try BehindTheBack.
                _step = Step.AwaitLifecycleDone;
                return;
            }

            case Step.AwaitLifecycleDone:
                if (_p1.PhaseForHarness != MovePhase.Inactive) return;

                bool btbBegan = _p1.BeginMoveForHarness(new BehindTheBack(1f));
                if (!btbBegan)
                {
                    Fail("feint-refused: BeginMoveForHarness(BehindTheBack) returned false.");
                    Finish();
                    return;
                }
                _step = Step.AwaitControlBegin; // reused: "BehindTheBack begun, now feint it"
                return;

            case Step.AwaitControlBegin:
            {
                bool feinted = _p1.FeintForHarness();
                if (feinted || _p1.PhaseForHarness != MovePhase.Startup)
                {
                    Fail($"feint-refused: expected BehindTheBack's feint refused at frame 0 of Startup; feinted={feinted}, phase={_p1.PhaseForHarness}.");
                    Finish();
                    return;
                }
                GD.Print("[in-and-out] PASS behindtheback-unfeintable — Feint() refused at Startup frame 0 (feintWindowFrames=0).");
                _step = Step.AwaitControlDone; // reused: "wait for BehindTheBack to finish, then run the JumpShot control"
                return;
            }

            case Step.AwaitControlDone:
                if (_p1.PhaseForHarness != MovePhase.Inactive) return;

                bool shotBegan = _p1.BeginJumpShotForHarness();
                if (!shotBegan)
                {
                    Fail("feint-refused: control BeginJumpShotForHarness() returned false.");
                    Finish();
                    return;
                }
                _everEnteredActive = false;
                _stepDeadlineFrame = _frame + ActionMarginFrames; // past FeintMinStartupFrames (3)
                _step = Step.RefusedFromLiveHeld; // reused: "await the feint-legal window"
                return;

            case Step.RefusedFromLiveHeld:
                if ((_p1.PhaseForHarness == MovePhase.Active)) _everEnteredActive = true;
                if (_frame < _stepDeadlineFrame) return;

                bool controlFeinted = _p1.FeintForHarness();
                if (!controlFeinted)
                {
                    Fail("feint-refused: control JumpShot pump-fake returned false — inside its legal [3, 12) window, expected true.");
                    Finish();
                    return;
                }
                if (_p1.PhaseForHarness != MovePhase.Recovery)
                {
                    Fail($"feint-refused: control JumpShot pump-fake expected to land in Recovery; got phase={_p1.PhaseForHarness}.");
                    Finish();
                    return;
                }
                _step = Step.RefusedFromDeadHeld; // reused: "await Inactive, then verify no release ever fired"
                return;

            case Step.RefusedFromDeadHeld:
                if ((_p1.PhaseForHarness == MovePhase.Active)) _everEnteredActive = true;
                if (_p1.PhaseForHarness != MovePhase.Inactive) return;

                if (_everEnteredActive)
                {
                    Fail("feint-refused: control JumpShot's pump-fake must NEVER reach Active (that is what gates the ball release) — JustEnteredActive fired during the pump-fake sequence.");
                    Finish();
                    return;
                }
                GD.Print("[in-and-out] PASS control — JumpShot's pump-fake still succeeds inside its legal window and NEVER reaches Active (no release), proving Crossover/BehindTheBack's refusal is move-specific, not a global feint break.");
                GD.Print("[in-and-out] RESULT: PASS (exit 0)");
                Finish(0);
                return;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario: "reconstruct" (AC-7)
    // A client-initiated "inandout" RequestBeginMove payload reconstructs
    // correctly server-side (flick sign intact, burst direction correct,
    // HandSide unchanged) through the SAME ApplyRequestedMove dispatch a real
    // RPC would drive — bypassing only the sender-authorization/RPC layer
    // headless cannot exercise (see RequestMoveForHarness/
    // LayupRangeHarnessSeam's identical rationale).
    // ═══════════════════════════════════════════════════════════════════════
    private void TickReconstruct()
    {
        switch (_step)
        {
            case Step.Start:
                _p1.RequestMoveForHarness("inandout", 1f);
                if (_p1.CurrentMoveIdForHarness != "inandout")
                {
                    Fail($"reconstruct: expected 'inandout' to reconstruct and begin; got CurrentMoveIdForHarness='{_p1.CurrentMoveIdForHarness}'.");
                    Finish();
                    return;
                }
                GD.Print("[in-and-out] PASS reconstruct-begin — the 'inandout' wire payload reconstructed and began through ApplyRequestedMove.");
                _step = Step.AwaitActive;
                return;

            case Step.AwaitActive:
                if (!(_p1.PhaseForHarness == MovePhase.Active)) return;
                // Same fallback-direction contract as "fallback-toward-ball-hand":
                // param=+1 (empty-hand flick sign) composes with the NEGATED
                // sign, so the fallback burst points toward the ball hand
                // (positive X at heading 0) — proving the flick sign survived
                // the wire reconstruction intact, not just defaulted to 0.
                if (_p1.Velocity.X <= 0f)
                {
                    Fail($"reconstruct: expected the reconstructed flick sign (+1) to compose a POSITIVE X burst; got Velocity.X={_p1.Velocity.X:F3}.");
                    Finish();
                    return;
                }
                GD.Print($"[in-and-out] PASS reconstruct-payload — flick sign survived reconstruction (Velocity.X={_p1.Velocity.X:F3} > 0).");
                _step = Step.AwaitLifecycleDone;
                return;

            case Step.AwaitLifecycleDone:
                if (_p1.PhaseForHarness != MovePhase.Inactive) return;
                if (_p1.HandSide != HandSide.Left)
                {
                    Fail($"reconstruct: expected HandSide to stay Left (InAndOut never swaps, AC-2) after a reconstructed lifecycle; got {_p1.HandSide}.");
                    Finish();
                    return;
                }
                GD.Print("[in-and-out] PASS reconstruct-lifecycle — the reconstructed InAndOut ran to Inactive with HandSide untouched.");
                GD.Print("[in-and-out] RESULT: PASS (exit 0)");
                Finish(0);
                return;
        }
    }

    private void Fail(string message) => GD.PrintErr($"[in-and-out] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[in-and-out] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
