using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #201 (ADR-0016): the spin — the
// dribble-move family's last leaf, a full-body rotation that shields the
// ball with a hand swap at the END of the rotation. Unit tests already pin
// the pure heading-arc contract (SpinHeadingMathTests.cs — progress reaches
// exactly 1.0 on the last Active tick, direction sign, [-pi, pi]
// normalization, the activeFrames<=0 guard). What they CANNOT reach is the
// live engine glue this harness proves:
//   1. A real spin rotates the holder's AUTHORITATIVE Heading ~180 degrees
//      from its entry value — CONTROL: a real (non-spin) Crossover leaves
//      Heading completely unrotated across its own full lifecycle.
//   2. HandSide swaps EXACTLY ONCE, on the LAST Active tick (FrameInPhase ==
//      ActiveFrames - 1) — the paired control is baked into the same
//      lifecycle: HandSide is provably UNCHANGED on the FIRST Active tick
//      (FrameInPhase == 0) and every tick before the last one.
//   3. Spin is refused while dead-Held (HasDribbled=true, post-cradle) AND
//      from a fresh LIVE Held possession (the ball is not yet a dribble to
//      shield) — CONTROL: permitted from a live Dribbling possession — the
//      specific check that catches a hardcoded-type-list omission (the same
//      #193 bug class every sibling move's dead-dribble gate guards against).
//   4. The exit burst composes against the ENTRY exit-vector snapshot, not
//      whatever the left stick reads on the LAST Active tick — proven by
//      comparing two independent trials (stick held constant throughout vs.
//      stick switched to a DIFFERENT direction right after Active begins):
//      the two trials' burst velocities must match, which could only be true
//      if the burst is composed from the ENTRY snapshot in both cases. This
//      is the doubt-driven-development finding an earlier draft got wrong
//      (see Spin's/SpinHeadingMath's class docs) — RELATIVE direction only;
//      the burst MAGNITUDE is feel, deferred to #173 (ADR-0021).
//
//   godot --headless --path . res://tests/integration/SpinTest.tscn -- --harness-scenario=rotation
//   godot --headless --path . res://tests/integration/SpinTest.tscn -- --harness-scenario=handside-swap-timing
//   godot --headless --path . res://tests/integration/SpinTest.tscn -- --harness-scenario=dead-dribble-gate
//   godot --headless --path . res://tests/integration/SpinTest.tscn -- --harness-scenario=exit-burst-continues-entry-line
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "rotation".
//
// ── Why a single offline instance is the server ───────────────────────────
// Same reasoning as InAndOutTest/JabStepTest/BehindTheBackTest: with no
// MultiplayerPeer assigned, Godot uses OfflineMultiplayerPeer (is_server()
// hardcoded true, unique_id 1), so player "1" is both IsServer and
// IsLocalPlayer — its _machine.Tick()/TickCommittedMoveBehavior advance every
// physics frame, the free clock every other Begin*ForHarness-driven harness
// in this repo relies on.
//
// ── Why BeginMoveForHarness, not real gesture/RPC dispatch ────────────────
// Spin has no assigned right-stick gesture or input action yet (a real input
// trigger for it is untouched — flagged as adjacent, out-of-scope follow-up
// work for this issue, not silently done here). BeginMoveForHarness routes
// through the SAME production choke point (BeginCommittedMove) every gesture-
// driven move already uses (DefensiveMoveHarnessSeam's own doc), so every
// assertion below proves the real move machinery — only the input-selection
// layer is bypassed, exactly like every other harness in this repo that
// exercises a move via this seam.
public partial class SpinTest : Node
{
    private const double TimeoutSeconds = 10.0;
    private const int ArmFrames = 2;          // ticks for TryAssignTipoffHolder to run
    private const int ActionMarginFrames = 3; // ticks to let an action's effect settle
    private const float HeadingTolerance = 0.01f;
    private const float VelocityTolerance = 0.05f;

    private static readonly int ActiveFrames = Spin.DefaultFrameData.ActiveFrames;

    private string _scenario = "rotation";

    private BallController _ball;
    private PlayerController _p1;

    private int _frame;
    private double _elapsed;
    private bool _finished;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "rotation");
        GD.Print($"[spin] scenario={_scenario} booting headless…");

        if (_scenario is "dead-dribble-gate")
        {
            // Needs a real BallController + tipoff for possession-state
            // scenarios (mirrors InAndOutTest/JabStepTest/BehindTheBackTest's
            // ball setup).
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
            // No Ball wrapper needed — mirrors InAndOutTest's non-ball
            // scenarios: BeginCommittedMove's dead-dribble gate is
            // IsBallHolder-gated, and GetBall() returning null makes that
            // gate a no-op, isolating the behavior under test from
            // possession state.
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
            case "rotation":                          TickRotation();                    break;
            case "handside-swap-timing":               TickHandSideSwapTiming();          break;
            case "dead-dribble-gate":                   TickDeadDribbleGate();             break;
            case "exit-burst-continues-entry-line":     TickExitBurstContinuesEntryLine(); break;
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

    // ── Shared step machine (mirrors InAndOutTest/JabStepTest) ──────────────
    private enum Step
    {
        Start, AwaitInactive, ControlAwaitInactive,
        AwaitActive, AwaitFirstActive, AwaitLastActive,
        AwaitTipoff, RefusedFromLiveHeld, DriveIssued, PermittedFromDribbling,
        CradleIssued, AwaitDeadHeld, RefusedFromDeadHeld,
        BaselineAwaitFirstActive, BaselineAwaitRecovery, BaselineAwaitInactive,
        ReplacePlayerAwait, SwitchedAwaitFirstActive, SwitchedAwaitRecovery,
    }
    private Step _step = Step.Start;
    private int _stepDeadlineFrame;

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario: "rotation"
    // A real spin rotates the holder's authoritative Heading ~180 degrees
    // (exactly pi radians, entry heading 0) from entry to Inactive. Control:
    // a real Crossover leaves Heading completely unrotated across its own
    // full lifecycle.
    // ═══════════════════════════════════════════════════════════════════════
    private float _entryHeading;

    private void TickRotation()
    {
        switch (_step)
        {
            case Step.Start:
            {
                _entryHeading = _p1.Heading;
                bool began = _p1.BeginMoveForHarness(new Spin(spinDirection: 1f));
                if (!began)
                {
                    Fail("rotation: BeginMoveForHarness(Spin) returned false.");
                    Finish();
                    return;
                }
                _step = Step.AwaitInactive;
                return;
            }

            case Step.AwaitInactive:
            {
                if (_p1.PhaseForHarness != MovePhase.Inactive) return;

                float expected = NormalizeAngle(_entryHeading + Mathf.Pi);
                float diff = Mathf.Abs(NormalizeAngle(_p1.Heading - expected));
                if (diff > HeadingTolerance)
                {
                    Fail($"rotation: expected Heading ~= {expected:F4} (entry {_entryHeading:F4} + pi) after a full spin, got {_p1.Heading:F4} (diff {diff:F4}).");
                    Finish();
                    return;
                }
                GD.Print($"[spin] PASS rotation — Heading went {_entryHeading:F4} -> {_p1.Heading:F4}, a ~180 degree rotation.");

                // Control: a REAL Crossover leaves Heading completely
                // unrotated across its own full lifecycle — proving the
                // rotation above is spin-specific, not some incidental
                // side effect of BeginCommittedMove/committed-move ticking.
                _entryHeading = _p1.Heading;
                bool controlBegan = _p1.BeginMoveForHarness(new Crossover(1f));
                if (!controlBegan)
                {
                    Fail("rotation: control BeginMoveForHarness(Crossover) returned false.");
                    Finish();
                    return;
                }
                _step = Step.ControlAwaitInactive;
                return;
            }

            case Step.ControlAwaitInactive:
            {
                if (_p1.PhaseForHarness != MovePhase.Inactive) return;
                float diff = Mathf.Abs(NormalizeAngle(_p1.Heading - _entryHeading));
                if (diff > HeadingTolerance)
                {
                    Fail($"rotation: control Crossover expected Heading UNCHANGED at {_entryHeading:F4}, got {_p1.Heading:F4} (diff {diff:F4}).");
                    Finish();
                    return;
                }
                GD.Print($"[spin] PASS control — a real Crossover leaves Heading unrotated ({_p1.Heading:F4}), proving Spin's rotation is move-specific.");
                GD.Print("[spin] RESULT: PASS (exit 0)");
                Finish(0);
                return;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario: "handside-swap-timing"
    // HandSide swaps EXACTLY ONCE, on the LAST Active tick (FrameInPhase ==
    // ActiveFrames - 1). The paired control lives in the SAME lifecycle:
    // HandSide is UNCHANGED on the FIRST Active tick (FrameInPhase == 0) and
    // every tick strictly before the last one.
    // ═══════════════════════════════════════════════════════════════════════
    private HandSide _handSideBefore;
    private bool _sawFirstActiveTick;
    private bool _sawLastActiveTick;

    private void TickHandSideSwapTiming()
    {
        switch (_step)
        {
            case Step.Start:
            {
                _handSideBefore = _p1.HandSide;
                bool began = _p1.BeginMoveForHarness(new Spin(spinDirection: 1f));
                if (!began)
                {
                    Fail("handside-swap-timing: BeginMoveForHarness(Spin) returned false.");
                    Finish();
                    return;
                }
                _sawFirstActiveTick = false;
                _sawLastActiveTick = false;
                _step = Step.AwaitActive;
                return;
            }

            case Step.AwaitActive:
            {
                if (_p1.PhaseForHarness == MovePhase.Active)
                {
                    int fip = _p1.FrameInPhaseForHarness;

                    if (fip == 0)
                    {
                        _sawFirstActiveTick = true;
                        if (_p1.HandSide != _handSideBefore)
                        {
                            Fail($"handside-swap-timing: expected HandSide UNCHANGED ({_handSideBefore}) on the FIRST Active tick, got {_p1.HandSide}.");
                            Finish();
                            return;
                        }
                        GD.Print($"[spin] PASS control — HandSide stayed {_handSideBefore} on the FIRST Active tick (FrameInPhase=0).");
                    }
                    else if (fip < ActiveFrames - 1)
                    {
                        if (_p1.HandSide != _handSideBefore)
                        {
                            Fail($"handside-swap-timing: HandSide swapped too early, at FrameInPhase={fip} (expected the swap only on FrameInPhase={ActiveFrames - 1}).");
                            Finish();
                            return;
                        }
                    }
                    else if (fip == ActiveFrames - 1)
                    {
                        _sawLastActiveTick = true;
                        if (_p1.HandSide == _handSideBefore)
                        {
                            Fail($"handside-swap-timing: expected HandSide to have swapped away from {_handSideBefore} by the LAST Active tick (FrameInPhase={fip}), but it did not.");
                            Finish();
                            return;
                        }
                        GD.Print($"[spin] PASS rotation-swap — HandSide swapped {_handSideBefore} -> {_p1.HandSide} on the LAST Active tick (FrameInPhase={fip}).");
                    }
                    return;
                }

                if (_p1.PhaseForHarness == MovePhase.Recovery || _p1.PhaseForHarness == MovePhase.Inactive)
                {
                    if (!_sawFirstActiveTick || !_sawLastActiveTick)
                    {
                        Fail($"handside-swap-timing: never observed both the first ({_sawFirstActiveTick}) and last ({_sawLastActiveTick}) Active ticks before leaving Active.");
                        Finish();
                        return;
                    }
                    GD.Print("[spin] RESULT: PASS (exit 0)");
                    Finish(0);
                }
                return;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario: "dead-dribble-gate"
    // Spin is refused from a fresh LIVE Held possession AND from a DEAD Held
    // possession (post-cradle) — the same #193 bug class every sibling move's
    // dead-dribble gate guards against. Control: permitted from a live
    // Dribbling possession — the specific check that catches a
    // hardcoded-type-list omission.
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
                bool began = holder.BeginMoveForHarness(new Spin(spinDirection: 1f));
                bool refused = !began && holder.PhaseForHarness == MovePhase.Inactive;
                if (!refused)
                {
                    Fail($"dead-dribble-gate: expected Spin refused from a LIVE Held possession; began={began}, phase={holder.PhaseForHarness}.");
                    Finish();
                    return;
                }
                GD.Print("[spin] PASS refused-live-held — Spin refused from a fresh live Held possession.");

                // Start a LIVE dribble. The dead-dribble rule (#193) makes
                // HasDribbled a ONE-WAY latch for this possession, so the
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
                // The required control: Spin IS permitted from a LIVE
                // Dribbling possession — the specific check that catches a
                // hardcoded-type-list omission. Begun and run to completion
                // here; Spin never cradles, so the ball stays Dribbling
                // throughout and HasDribbled stays false, leaving the
                // possession free to cradle afterward.
                PlayerController holder = NodeForPeer(_holderId);
                bool began = holder.BeginMoveForHarness(new Spin(spinDirection: 1f));
                if (!began)
                {
                    Fail($"dead-dribble-gate: expected Spin PERMITTED from a live Dribbling possession (control); began={began}, phase={holder.PhaseForHarness}.");
                    Finish();
                    return;
                }
                GD.Print("[spin] PASS control — Spin IS permitted from a live Dribbling possession.");
                _step = Step.AwaitDeadHeld; // reused: "wait for this control Spin to finish, then cradle"
                return;
            }

            case Step.AwaitDeadHeld:
            {
                if (NodeForPeer(_holderId).PhaseForHarness != MovePhase.Inactive) return;
                if (_ball.State != BallState.Dribbling || _ball.HasDribbled)
                {
                    Fail($"dead-dribble-gate: expected the ball to STILL be live Dribbling after the control Spin's full lifecycle; got state={_ball.State}, HasDribbled={_ball.HasDribbled}.");
                    Finish();
                    return;
                }

                // Now cradle a dribble (JumpShot begin -> feint away -> wait
                // for Recovery) to reach DEAD Held (HasDribbled=true), same
                // setup InAndOutTest/JabStepTest/TripleThreatTest use.
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
                _step = Step.RefusedFromDeadHeld; // reused: "await Inactive, then refuse"
                return;

            case Step.RefusedFromDeadHeld:
            {
                if (NodeForPeer(_holderId).PhaseForHarness != MovePhase.Inactive) return;
                if (!(_ball.State == BallState.Held && _ball.HasDribbled))
                {
                    Fail($"dead-dribble-gate: expected DEAD Held once the feinted shot's Recovery elapsed; got state={_ball.State}, HasDribbled={_ball.HasDribbled}.");
                    Finish();
                    return;
                }

                PlayerController holder = NodeForPeer(_holderId);
                bool began = holder.BeginMoveForHarness(new Spin(spinDirection: 1f));
                bool refused = !began && holder.PhaseForHarness == MovePhase.Inactive;
                if (!refused)
                {
                    Fail($"dead-dribble-gate: expected Spin refused from a DEAD Held possession; began={began}, phase={holder.PhaseForHarness}.");
                    Finish();
                    return;
                }
                GD.Print("[spin] PASS refused-dead-held — Spin refused from a dead Held possession (HasDribbled=true).");
                GD.Print("[spin] RESULT: PASS (exit 0)");
                Finish(0);
                return;
            }
        }
    }

    private PlayerController NodeForPeer(int peerId) => peerId == 1 ? _p1 : (PlayerController)_ball.Players.GetChild(peerId - 1);

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario: "exit-burst-continues-entry-line"
    // The exit burst composes against the ENTRY exit-vector snapshot (the
    // left stick reading captured ONCE at JustEnteredActive), not whatever
    // the stick reads on the LAST Active tick. Proven by comparing two
    // trials: a baseline (stick held constant throughout Active) vs. a
    // switched trial (stick changes to a DIFFERENT direction right after
    // Active begins). If the burst read the LIVE stick on the final tick (the
    // doubt-driven-development bug an earlier draft had), the switched
    // trial's velocity would diverge from the baseline's; since the entry
    // snapshot is locked in both cases, the two must match. RELATIVE
    // direction/consistency only — the burst MAGNITUDE is feel, deferred to
    // the consolidated human pass #173 (ADR-0021).
    //
    // The switched trial runs on a FRESH PlayerController, not the same
    // instance the baseline used: the burst composition is heading-relative
    // (world burst direction derives from the entry heading — see Spin's
    // "Exit composition" doc), and the baseline trial's own spin has already
    // rotated that instance's Heading ~180 degrees. Reusing it would confound
    // "did the burst re-read live input" with "the second trial started from
    // a different heading" — two different world-frame outcomes that would
    // look identical to a bug. A fresh instance (Heading back at its default
    // 0) isolates the ONE variable this scenario is actually testing.
    // ═══════════════════════════════════════════════════════════════════════
    private Vector3 _baselineVelocity;

    private void TickExitBurstContinuesEntryLine()
    {
        switch (_step)
        {
            case Step.Start:
                Input.ActionPress("move_forward", 1.0f);
                bool began = _p1.BeginMoveForHarness(new Spin(spinDirection: 1f));
                if (!began)
                {
                    Fail("exit-burst-continues-entry-line: BeginMoveForHarness(Spin) returned false (baseline).");
                    Finish();
                    return;
                }
                _step = Step.BaselineAwaitFirstActive;
                return;

            case Step.BaselineAwaitFirstActive:
                // Baseline: stick held constant (move_forward) throughout —
                // never switched — so this trial's result is the reference
                // "entry direction" outcome.
                if (_p1.PhaseForHarness != MovePhase.Active || _p1.FrameInPhaseForHarness != 0) return;
                _step = Step.BaselineAwaitRecovery;
                return;

            case Step.BaselineAwaitRecovery:
                if (_p1.PhaseForHarness != MovePhase.Recovery) return;
                _baselineVelocity = _p1.Velocity;
                Input.ActionRelease("move_forward");
                GD.Print($"[spin] baseline burst velocity = {_baselineVelocity} (stick held constant throughout Active).");
                _step = Step.BaselineAwaitInactive;
                return;

            case Step.BaselineAwaitInactive:
                if (_p1.PhaseForHarness != MovePhase.Inactive) return;

                // Swap in a fresh PlayerController for the switched trial —
                // see this scenario's class doc for why reusing the
                // baseline's own (now ~180-degree-rotated) instance would
                // confound the comparison.
                _p1.QueueFree();
                _p1 = new PlayerController { Name = "1" };
                AddChild(_p1);
                _step = Step.ReplacePlayerAwait;
                _stepDeadlineFrame = _frame + ArmFrames;
                return;

            case Step.ReplacePlayerAwait:
                if (_frame < _stepDeadlineFrame) return;

                Input.ActionPress("move_forward", 1.0f);
                bool switchedBegan = _p1.BeginMoveForHarness(new Spin(spinDirection: 1f));
                if (!switchedBegan)
                {
                    Fail("exit-burst-continues-entry-line: BeginMoveForHarness(Spin) returned false (switched trial).");
                    Finish();
                    return;
                }
                _step = Step.SwitchedAwaitFirstActive;
                return;

            case Step.SwitchedAwaitFirstActive:
                if (_p1.PhaseForHarness != MovePhase.Active || _p1.FrameInPhaseForHarness != 0) return;

                // The entry snapshot has already been captured THIS tick
                // (TickCommittedMoveBehavior runs synchronously within the
                // same _PhysicsProcess call that advanced FrameInPhase to
                // 0). Switching the stick NOW, for every remaining Active
                // tick, isolates whether the burst re-reads it live.
                Input.ActionRelease("move_forward");
                Input.ActionPress("move_backward", 1.0f);
                _step = Step.SwitchedAwaitRecovery;
                return;

            case Step.SwitchedAwaitRecovery:
                if (_p1.PhaseForHarness != MovePhase.Recovery) return;
                Vector3 switchedVelocity = _p1.Velocity;
                Input.ActionRelease("move_backward");

                float diffX = Mathf.Abs(switchedVelocity.X - _baselineVelocity.X);
                float diffZ = Mathf.Abs(switchedVelocity.Z - _baselineVelocity.Z);
                if (diffX > VelocityTolerance || diffZ > VelocityTolerance)
                {
                    Fail($"exit-burst-continues-entry-line: expected the switched trial's burst ({switchedVelocity}) to MATCH the baseline ({_baselineVelocity}) — the entry stick reading should be locked in at JustEnteredActive, not re-read live on the last Active tick. diffX={diffX:F4}, diffZ={diffZ:F4}.");
                    Finish();
                    return;
                }
                GD.Print($"[spin] PASS exit-burst-continues-entry-line — switched-trial burst ({switchedVelocity}) matches the baseline ({_baselineVelocity}) within tolerance, proving the ENTRY exit-vector snapshot drives the composition, not a live re-read.");
                GD.Print("[spin] RESULT: PASS (exit 0)");
                Finish(0);
                return;
        }
    }

    private static float NormalizeAngle(float angle)
    {
        while (angle > Mathf.Pi) angle -= 2f * Mathf.Pi;
        while (angle < -Mathf.Pi) angle += 2f * Mathf.Pi;
        return angle;
    }

    private void Fail(string message) => GD.PrintErr($"[spin] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[spin] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
