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
//   5. The spin's ball-sweep path resolves to BodyShield (#201's "third
//      option on #195's sweep geometry" — the ball pulled in tight against
//      the rotating body, not extended in front and not swung fully behind)
//      — CONTROL: a real Crossover resolves to InFront, the pre-existing
//      default every non-special-cased move-id already gets.
//   6. A real player CAN trigger a spin: holding BOTH "move_size_up" and
//      "move_finesse" together during a held crossover flick begins a real
//      Spin through the ACTUAL RightStickGestureRecognizer -> SampleMoveInput
//      dispatch (real synthetic Input.ActionPress, no harness seam) — CONTROL:
//      "move_size_up" alone under the SAME flick still begins BehindTheBack,
//      proving the modifier-combo is additive, not a silent reassignment of
//      the existing single-modifier dispatch.
//   7. A client-initiated "spin" RequestBeginMove payload reconstructs
//      correctly server-side through the SAME ApplyRequestedMove dispatch a
//      real RPC would drive (the flick sign survives as SpinDirection).
//
//   godot --headless --path . res://tests/integration/SpinTest.tscn -- --harness-scenario=rotation
//   godot --headless --path . res://tests/integration/SpinTest.tscn -- --harness-scenario=handside-swap-timing
//   godot --headless --path . res://tests/integration/SpinTest.tscn -- --harness-scenario=dead-dribble-gate
//   godot --headless --path . res://tests/integration/SpinTest.tscn -- --harness-scenario=exit-burst-continues-entry-line
//   godot --headless --path . res://tests/integration/SpinTest.tscn -- --harness-scenario=sweep-path
//   godot --headless --path . res://tests/integration/SpinTest.tscn -- --harness-scenario=real-input-trigger
//   godot --headless --path . res://tests/integration/SpinTest.tscn -- --harness-scenario=reconstruct
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
// ── Why most scenarios use BeginMoveForHarness, not real gesture/RPC dispatch ──
// Scenarios 1-5 above use BeginMoveForHarness — same as every sibling move's
// mechanics harness (DefensiveMoveHarnessSeam's own doc) — because the thing
// under test there is what Spin DOES once begun, not how it gets begun.
// Scenarios 6-7 exist specifically to prove Spin CAN be begun through the real
// input/RPC surface (move_size_up + move_finesse held together during a
// crossover flick — see SampleMoveInput's Spin branch for the full ADR-0014
// tier-3 citation and the circuit-breaker reasoning for NOT building a new
// stick-rotation gesture primitive) and through ApplyRequestedMove's "spin"
// reconstruction case.
public partial class SpinTest : Node
{
    private const double TimeoutSeconds = 10.0;
    private const int ArmFrames = 2;          // ticks for TryAssignTipoffHolder to run
    private const int ActionMarginFrames = 3; // ticks to let an action's effect settle
    private const float HeadingTolerance = 0.01f;
    private const float VelocityTolerance = 0.05f;
    private const int FlickHoldTicks = 7;     // > the recognizer's feint window — commits the HELD gesture (mirrors InAndOutTest)

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

        if (_scenario is "dead-dribble-gate" or "sweep-path")
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
            //
            // "remote-exit-vector-preferred" (issue #210) names the node "2"
            // instead of "1" — with no MultiplayerPeer assigned,
            // OfflineMultiplayerPeer hardcodes unique_id 1, so "2" makes
            // IsServer true but IsLocalPlayer FALSE: the TickServerRemotePlayer
            // role, exactly like MovingCrossoverTest's "remote-pending-stick"
            // scenario targets for Crossover. Every other Spin scenario stays
            // named "1" (TickServerRemotePlayer's own-player role), which
            // never reads _authoritativeExitVector/_pendingRawStick at all.
            _p1 = new PlayerController { Name = _scenario == "remote-exit-vector-preferred" ? "2" : "1" };
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
            case "sweep-path":                          TickSweepPath();                   break;
            case "real-input-trigger":                   TickRealInputTrigger();            break;
            case "reconstruct":                          TickReconstruct();                 break;
            case "remote-exit-vector-preferred":          TickRemoteExitVectorPreferred();   break;
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
        SweepAwaitDribbling, SweepAwaitSwapped, SweepAwaitInactive, SweepControlAwaitSwapped,
        RealInputFlickStarted, RealInputAwaitInactive, RealInputControlFlickStarted,
        RemoteExitVectorAwaitLastActive,
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

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario: "sweep-path"
    // The spin's ball-sweep path resolves to BodyShield (#201's "third option
    // on #195's sweep geometry") — pulled tight to the rotating body, not
    // InFront's default. Control: a real Crossover under identical
    // (Dribbling, then a hand-swap) conditions resolves to InFront, the
    // pre-existing default every non-special-cased move-id already gets.
    // Needs a real BallController + tipoff + a live Dribbling possession —
    // the ball-sweep machinery (AdvanceHandSweep) only runs from
    // TickHeld/TickDribbling, and Spin itself is refused from Held (see the
    // "dead-dribble-gate" scenario), so the holder must actually be
    // Dribbling before either move can begin.
    // ═══════════════════════════════════════════════════════════════════════
    private void TickSweepPath()
    {
        switch (_step)
        {
            case Step.AwaitTipoff:
                if (_frame < ArmFrames) return;
                if (_ball.StateMachine.HolderPeerId == 0)
                {
                    Fail("sweep-path: tipoff never assigned a holder.");
                    Finish();
                    return;
                }
                _holderId = _ball.StateMachine.HolderPeerId;
                _ball.TryStartDribble(_holderId);
                _step = Step.SweepAwaitDribbling;
                _stepDeadlineFrame = _frame + ActionMarginFrames;
                return;

            case Step.SweepAwaitDribbling:
            {
                if (_frame < _stepDeadlineFrame) return;
                if (_ball.State != BallState.Dribbling)
                {
                    Fail($"sweep-path: expected TryStartDribble to reach Dribbling; got state={_ball.State}.");
                    Finish();
                    return;
                }
                PlayerController holder = NodeForPeer(_holderId);
                bool began = holder.BeginMoveForHarness(new Spin(spinDirection: 1f));
                if (!began)
                {
                    Fail("sweep-path: BeginMoveForHarness(Spin) returned false.");
                    Finish();
                    return;
                }
                _step = Step.SweepAwaitSwapped;
                return;
            }

            case Step.SweepAwaitSwapped:
            {
                // Wait until at least Recovery — the HandSide flip (and the
                // AdvanceHandSweep-driven _sweepPath resolution it triggers)
                // fires on the LAST Active tick (Spin's own timing contract,
                // see "handside-swap-timing"), one tick before Phase leaves
                // Active, so Recovery guarantees the flip has already been
                // observed by the ball.
                PlayerController holder = NodeForPeer(_holderId);
                if (holder.PhaseForHarness != MovePhase.Recovery && holder.PhaseForHarness != MovePhase.Inactive) return;

                if (_ball.SweepPathForHarness != BallSweepPath.BodyShield)
                {
                    Fail($"sweep-path: expected Spin's sweep to resolve to BallSweepPath.BodyShield, got {_ball.SweepPathForHarness}.");
                    Finish();
                    return;
                }
                GD.Print("[spin] PASS sweep-path — Spin's ball sweep resolved to BodyShield.");
                _step = Step.SweepAwaitInactive;
                return;
            }

            case Step.SweepAwaitInactive:
            {
                if (NodeForPeer(_holderId).PhaseForHarness != MovePhase.Inactive) return;
                if (_ball.State != BallState.Dribbling)
                {
                    Fail($"sweep-path: expected the ball to still be Dribbling after Spin's full lifecycle; got state={_ball.State}.");
                    Finish();
                    return;
                }

                // Control: a REAL Crossover under identical (Dribbling)
                // conditions resolves to InFront — proving BodyShield above
                // is spin-specific, not some blanket effect of a hand swap.
                PlayerController holder = NodeForPeer(_holderId);
                bool controlBegan = holder.BeginMoveForHarness(new Crossover(1f));
                if (!controlBegan)
                {
                    Fail("sweep-path: control BeginMoveForHarness(Crossover) returned false.");
                    Finish();
                    return;
                }
                _step = Step.SweepControlAwaitSwapped;
                return;
            }

            case Step.SweepControlAwaitSwapped:
            {
                PlayerController holder = NodeForPeer(_holderId);
                // Crossover's hand swap fires on JustEnteredActive (its FIRST
                // Active tick) — Active itself is enough to have observed it.
                if (holder.PhaseForHarness != MovePhase.Active
                    && holder.PhaseForHarness != MovePhase.Recovery
                    && holder.PhaseForHarness != MovePhase.Inactive) return;

                if (_ball.SweepPathForHarness != BallSweepPath.InFront)
                {
                    Fail($"sweep-path: control Crossover expected BallSweepPath.InFront, got {_ball.SweepPathForHarness}.");
                    Finish();
                    return;
                }
                GD.Print("[spin] PASS control — a real Crossover resolves to InFront, proving BodyShield is spin-specific.");
                GD.Print("[spin] RESULT: PASS (exit 0)");
                Finish(0);
                return;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario: "real-input-trigger"
    // A real player CAN trigger a spin: holding BOTH "move_size_up" and
    // "move_finesse" during a held crossover flick begins a real Spin through
    // the ACTUAL RightStickGestureRecognizer -> SampleMoveInput dispatch (real
    // synthetic Input.ActionPress, no harness seam). Control: "move_size_up"
    // ALONE under the SAME flick still begins BehindTheBack — proving the
    // modifier-combo is additive (a NEW selection), not a silent reassignment
    // of the pre-existing single-modifier dispatch.
    // ═══════════════════════════════════════════════════════════════════════
    private int _flickStartFrame = -1;

    private void TickRealInputTrigger()
    {
        switch (_step)
        {
            case Step.Start:
                Input.ActionPress("move_size_up", 1.0f);
                Input.ActionPress("move_finesse", 1.0f);
                Input.ActionPress("aim_right", 1.0f); // flickSign +1: empty hand (HandSide.Left default)
                _flickStartFrame = _frame;
                _step = Step.RealInputFlickStarted;
                return;

            case Step.RealInputFlickStarted:
                if (_p1.CurrentMoveIdForHarness == "spin")
                {
                    // Release NOW, not on a fixed frame offset — the gesture
                    // may commit (FeintWindowTicks+1 ticks) well before a
                    // fixed FlickHoldTicks deadline, and this case is left the
                    // very same tick the move commits, so a frame-counted
                    // release scheduled for later would never fire (the bug
                    // an earlier draft of this scenario had: aim_right stayed
                    // HELD through the whole rest of the scenario, corrupting
                    // the later control flick's stick reading).
                    Input.ActionRelease("aim_right");
                    GD.Print($"[spin] PASS real-input-trigger — holding move_size_up+move_finesse during a real flick began Spin at frame {_frame}.");
                    _step = Step.RealInputAwaitInactive;
                }
                else if (_p1.CurrentMoveIdForHarness is "behindtheback" or "betweenthelegs" or "crossover")
                {
                    Input.ActionRelease("aim_right");
                    Fail($"real-input-trigger: expected Spin, got '{_p1.CurrentMoveIdForHarness}' — the modifier-combo dispatch did not fire correctly.");
                    Finish();
                    return;
                }
                else if (_frame > _flickStartFrame + FlickHoldTicks + ActionMarginFrames)
                {
                    // Safety valve: the gesture never committed at all within
                    // a generous margin past FlickHoldTicks — fail loudly
                    // instead of relying on the outer timeout to explain why.
                    Input.ActionRelease("aim_right");
                    Fail($"real-input-trigger: the flick never committed to any move within {FlickHoldTicks + ActionMarginFrames} ticks (moveId='{_p1.CurrentMoveIdForHarness}').");
                    Finish();
                    return;
                }
                return;

            case Step.RealInputAwaitInactive:
                if (_p1.PhaseForHarness != MovePhase.Inactive) return;
                GD.Print("[spin] PASS lifecycle — the real-input Spin ran its full Startup->Active->Recovery->Inactive cycle.");
                Input.ActionRelease("move_size_up");
                Input.ActionRelease("move_finesse");

                // Control: the SAME kind of flick with ONLY move_size_up held
                // must still begin a BehindTheBack — the pre-existing
                // single-modifier dispatch is unaffected by adding the
                // modifier-combo branch above it. Spin's own effect swapped
                // HandSide (Left -> Right), so the EMPTY hand is now on the
                // LEFT (HandStateResolver.EmptyHandSign(Right) == -1) — the
                // "toward the empty hand" flick that reads as a held crossover
                // gesture is therefore aim_LEFT this time, not aim_right
                // (using the stale sign here would silently resolve to
                // Hesitation instead, since the flick would read as "toward
                // the ball hand" against the NEW HandSide).
                Input.ActionPress("move_size_up", 1.0f);
                Input.ActionPress("aim_left", 1.0f);
                _flickStartFrame = _frame;
                _step = Step.RealInputControlFlickStarted;
                return;

            case Step.RealInputControlFlickStarted:
                if (_p1.CurrentMoveIdForHarness == "behindtheback")
                {
                    // Release NOW — see RealInputFlickStarted's identical
                    // reasoning: a frame-counted release scheduled past the
                    // tick this case is left on would never fire.
                    Input.ActionRelease("aim_left");
                    Input.ActionRelease("move_size_up");
                    GD.Print("[spin] PASS control — move_size_up ALONE under the same flick still begins BehindTheBack, unaffected by the new modifier-combo branch.");
                    GD.Print("[spin] RESULT: PASS (exit 0)");
                    Finish(0);
                }
                else if (_p1.CurrentMoveIdForHarness == "spin")
                {
                    Input.ActionRelease("aim_left");
                    Fail("real-input-trigger: control move_size_up-alone wrongly began Spin instead of BehindTheBack.");
                    Finish();
                    return;
                }
                else if (_frame > _flickStartFrame + FlickHoldTicks + ActionMarginFrames)
                {
                    Input.ActionRelease("aim_left");
                    Fail($"real-input-trigger: control flick never committed to any move within {FlickHoldTicks + ActionMarginFrames} ticks (moveId='{_p1.CurrentMoveIdForHarness}').");
                    Finish();
                    return;
                }
                return;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario: "reconstruct"
    // A client-initiated "spin" RequestBeginMove payload reconstructs
    // correctly server-side (SpinDirection intact) through the SAME
    // ApplyRequestedMove dispatch a real RPC would drive — bypassing only the
    // sender-authorization/RPC layer headless cannot exercise (see
    // RequestMoveForHarness/LayupRangeHarnessSeam's identical rationale).
    // ═══════════════════════════════════════════════════════════════════════
    private void TickReconstruct()
    {
        switch (_step)
        {
            case Step.Start:
                _p1.RequestMoveForHarness("spin", 1f);
                if (_p1.CurrentMoveIdForHarness != "spin")
                {
                    Fail($"reconstruct: expected 'spin' to reconstruct and begin; got CurrentMoveIdForHarness='{_p1.CurrentMoveIdForHarness}'.");
                    Finish();
                    return;
                }
                GD.Print("[spin] PASS reconstruct-begin — the 'spin' wire payload reconstructed and began through ApplyRequestedMove.");
                _step = Step.AwaitInactive;
                return;

            case Step.AwaitInactive:
                if (_p1.PhaseForHarness != MovePhase.Inactive) return;
                GD.Print("[spin] PASS reconstruct-lifecycle — the reconstructed Spin ran to Inactive.");
                GD.Print("[spin] RESULT: PASS (exit 0)");
                Finish(0);
                return;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario: "remote-exit-vector-preferred" (issue #210)
    // Proves the fix's shared seam genuinely REACHES Spin, not just the
    // Crossover family: the SERVER's copy of a REMOTE player's
    // _pendingRawStick is deliberately poisoned to a WRONG decoy direction
    // (SetPendingRawStickForHarness, the same seam MovingCrossoverTest's
    // "remote-pending-stick" control already uses for Crossover), but
    // _authoritativeExitVector is ALSO set (SetAuthoritativeExitVectorForHarness,
    // standing in for a real RequestExitVector RPC's arrival) to a DIFFERENT,
    // TRUE direction. Spin's own composition (_spinEntryExitVector, captured
    // once at JustEnteredActive and applied on the LAST Active tick) must
    // reflect the TRUE value, not the poisoned cache — proving
    // TickServerRemotePlayer's shared `_authoritativeExitVector ??
    // _pendingRawStick` expression is what every burst-family move's
    // exitVectorSample parameter ultimately traces back to, Spin included.
    // The FALLBACK half of that expression (no RPC value received) is not
    // re-proven here — it is the SAME non-move-specific ternary
    // MovingCrossoverTest's "remote-pending-stick" scenario already exercises
    // for Crossover, so proving it twice would test the same line of code
    // twice, not two different behaviors.
    // ═══════════════════════════════════════════════════════════════════════
    private static readonly Vector2 RemoteExitVectorTrue  = new(1f, 0f);
    private static readonly Vector2 RemoteExitVectorDecoy = new(-1f, 0f);

    private void TickRemoteExitVectorPreferred()
    {
        switch (_step)
        {
            case Step.Start:
            {
                _p1.SetPendingRawStickForHarness(RemoteExitVectorDecoy);
                bool began = _p1.BeginMoveForHarness(new Spin(spinDirection: 1f));
                if (!began)
                {
                    Fail("remote-exit-vector-preferred: BeginMoveForHarness(Spin) returned false.");
                    Finish();
                    return;
                }
                // Set AFTER Begin() — BeginCommittedMove unconditionally
                // clears _authoritativeExitVector at the START of every
                // attempt (the #210 stale-echo guard), so setting it before
                // would just be immediately wiped. This mirrors production
                // timing too: RequestBeginMove is always processed before
                // RequestExitVector for the SAME move (see RequestExitVector's
                // own doc on ENet's same-channel ordering guarantee).
                _p1.SetAuthoritativeExitVectorForHarness(RemoteExitVectorTrue);
                _step = Step.RemoteExitVectorAwaitLastActive;
                return;
            }

            case Step.RemoteExitVectorAwaitLastActive:
            {
                if (_p1.PhaseForHarness != MovePhase.Active) return;
                if (_p1.FrameInPhaseForHarness < ActiveFrames - 1) return; // Spin's exit burst fires on the LAST Active tick only

                Vector3 expected = CrossoverBurstMath.ComposeActiveVelocity(
                    Vector3.Zero, 0f, 1, RemoteExitVectorTrue,
                    _p1.SpinBurstSpeed, _p1.SpinForwardBurstScale, _p1.ExitDeadzone);
                float diff = (expected - _p1.Velocity).Length();
                if (diff > VelocityTolerance)
                {
                    Fail($"remote-exit-vector-preferred: expected the TRUE exit vector {RemoteExitVectorTrue} to win over the poisoned _pendingRawStick {RemoteExitVectorDecoy} — expected velocity={expected}, actual={_p1.Velocity}, diff={diff:F4}.");
                    Finish();
                    return;
                }
                GD.Print($"[spin] PASS remote-exit-vector-preferred — Spin's composed exit burst used the RPC'd TRUE exit vector {RemoteExitVectorTrue}, not the poisoned _pendingRawStick {RemoteExitVectorDecoy} (diff={diff:F4}); the #210 fix's shared seam reaches Spin.");
                GD.Print("[spin] RESULT: PASS (exit 0)");
                Finish(0);
                return;
            }
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
