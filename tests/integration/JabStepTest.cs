using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #200 (ADR-0016): the jab step —
// triple threat's stance bait. Unit tests already pin the pure legality gate
// (JabStepLegalityResolverTests.cs) and the frame-data/lifecycle contract
// (JabStepTests.cs). What they CANNOT reach is the live engine glue this
// harness proves:
//   1. A jab step actually BEGINS through the real BeginCommittedMove choke
//      point from a live Held possession, runs its full lifecycle, and
//      leaves HasDribbled/BallState/HandSide/GlobalPosition untouched — "no
//      state change, no ball movement, no burst" is a claim about the LIVE
//      glue (TickCommittedMoveBehavior's Startup/Active switches), not just
//      the pure frame-data numbers.
//   2. The SAME is true from a DEAD Held possession (post-cradle) — the jab
//      does not care about HasDribbled, and does not reset or further alter
//      it.
//   3. A jab step is REFUSED outright while Dribbling — BeginCommittedMove's
//      new inverse gate (JabStepLegalityResolver) actually blocks the real
//      choke point, not just the pure predicate in isolation.
//
// Every "did not happen" assertion here is paired with a control that proves
// the harness COULD have detected the opposite (the project's own harness
// discipline — see hooper-verification-and-qa's "control scenario" rule):
// scenario "legal-held" pairs its "HasDribbled stays false" assertion with a
// control JumpShot that DOES flip HasDribbled true afterward; scenario
// "illegal-dribbling" pairs its refusal with a control Hesitation that DOES
// begin from the SAME Dribbling state, proving the refusal is jab-specific,
// not "nothing can begin while Dribbling."
//
//   godot --headless --path . res://tests/integration/JabStepTest.tscn -- --harness-scenario=legal-held
//   godot --headless --path . res://tests/integration/JabStepTest.tscn -- --harness-scenario=legal-dead-held
//   godot --headless --path . res://tests/integration/JabStepTest.tscn -- --harness-scenario=illegal-dribbling
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "legal-held".
//
// ── Why a single offline instance is the server ───────────────────────────
// Same reasoning as TripleThreatTest/EuroStepTest: with no MultiplayerPeer
// assigned, Godot uses OfflineMultiplayerPeer (is_server() hardcoded true,
// unique_id 1), so BallController.IsServer is true and player "1" is
// IsLocalPlayer — its _machine.Tick() advances every physics frame via
// SampleMoveInput, the free clock every other Begin*ForHarness-driven
// harness in this repo relies on.
public partial class JabStepTest : Node
{
    private const double TimeoutSeconds = 10.0;
    private const int ArmFrames = 2;          // ticks for TryAssignTipoffHolder to run
    private const int ActionMarginFrames = 3; // ticks to let an action's effect settle

    private string _scenario = "legal-held";

    private BallController _ball;
    private PlayerController _p1;
    private PlayerController _p2;

    private int _frame;
    private double _elapsed;
    private bool _finished;

    private int _holderId;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "legal-held");
        GD.Print($"[jab-step] scenario={_scenario} booting headless…");

        // Same code-built-tree pattern as TripleThreatTest/EuroStepTest —
        // avoids fragile .tscn ext_resource/uid wiring for a throwaway harness.
        var players = new Node3D { Name = "Players" };
        _p1 = new PlayerController { Name = "1" };
        _p2 = new PlayerController { Name = "2" };
        players.AddChild(_p1);
        players.AddChild(_p2);

        _ball = new BallController { Name = "Ball", Players = players };

        AddChild(players); // matches scenes/Main.tscn: Players before Ball
        AddChild(_ball);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        switch (_scenario)
        {
            case "legal-held":        TickLegalHeld();        break;
            case "legal-dead-held":   TickLegalDeadHeld();     break;
            case "illegal-dribbling": TickIllegalDribbling();  break;
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

    // ── Shared step machine (simple linear sequence, mirrors TripleThreatTest) ──
    private enum Step
    {
        AwaitTipoff, BeginIssued, LifecycleAwaited, CradleIssued, FeintIssued,
        AwaitInactiveForJab, DriveIssued, DribblingChecked, Done
    }
    private Step _step = Step.AwaitTipoff;
    private int _stepDeadlineFrame;

    private Vector3 _startPosition;
    private HandSide _startHandSide;

    // ── Scenario: legal-held ──────────────────────────────────────────────────
    // Jab begins from a FRESH live Held possession (HasDribbled == false),
    // runs its full Startup->Active->Recovery->Inactive lifecycle, and leaves
    // HasDribbled/BallState/HandSide/GlobalPosition untouched. Then, as the
    // control, a JumpShot begins from the same (now-Inactive) state and DOES
    // flip HasDribbled true — proving the earlier "still false" was a real
    // observation, not a harness premise that could never fail.
    private void TickLegalHeld()
    {
        switch (_step)
        {
            case Step.AwaitTipoff:
                if (_frame < ArmFrames) return;
                if (_ball.StateMachine.HolderPeerId == 0)
                {
                    Fail("legal-held: tipoff never assigned a holder.");
                    Finish();
                    return;
                }
                _holderId = _ball.StateMachine.HolderPeerId;

                if (_ball.State != BallState.Held || _ball.HasDribbled)
                {
                    Fail($"legal-held: expected a fresh live Held possession at tipoff; got state={_ball.State}, HasDribbled={_ball.HasDribbled}.");
                    Finish();
                    return;
                }

                PlayerController holder = NodeForPeer(_holderId);
                _startPosition = holder.GlobalPosition;
                _startHandSide = holder.HandSide;

                bool began = holder.BeginMoveForHarness(new JabStep());
                if (!began)
                {
                    Fail("legal-held: BeginMoveForHarness(JabStep) returned false from a live Held possession — expected legal.");
                    Finish();
                    return;
                }
                GD.Print($"[jab-step] PASS begin — JabStep began from a fresh live Held possession (holder={_holderId}).");
                _step = Step.LifecycleAwaited;
                return;

            case Step.LifecycleAwaited:
                if (NodeForPeer(_holderId).PhaseForHarness != MovePhase.Inactive) return;

                PlayerController h = NodeForPeer(_holderId);
                bool noStateChange = _ball.State == BallState.Held && !_ball.HasDribbled;
                bool noHandSwap = h.HandSide == _startHandSide;
                bool noBurst = h.GlobalPosition.DistanceTo(_startPosition) < 0.01f;

                if (!(noStateChange && noHandSwap && noBurst))
                {
                    Fail($"legal-held: expected no state change / no hand swap / no burst after the jab's full lifecycle; " +
                         $"state={_ball.State}, HasDribbled={_ball.HasDribbled}, handSide {_startHandSide}->{h.HandSide}, " +
                         $"positionDelta={h.GlobalPosition.DistanceTo(_startPosition):F3}m.");
                    Finish();
                    return;
                }
                GD.Print("[jab-step] PASS no-state-change — HasDribbled stayed false, ball stayed Held, HandSide unchanged, no burst displacement.");

                // Control: a live dribble + a JumpShot begun from THIS SAME
                // state DOES cradle (CradleForShotStartup only fires while
                // Dribbling — see its own doc — so the dribble must be
                // started first, same setup TripleThreatTest's "cradle" step
                // uses) — proving the "HasDribbled stayed false" assertion
                // above could have failed had the jab (wrongly) touched it.
                _ball.TryStartDribble(_holderId);
                bool jumpBegan = h.BeginJumpShotForHarness();
                if (!jumpBegan)
                {
                    Fail("legal-held: control BeginJumpShotForHarness() returned false — machine was not Inactive.");
                    Finish();
                    return;
                }
                bool controlCradled = _ball.State == BallState.Held && _ball.HasDribbled;
                if (!controlCradled)
                {
                    Fail($"legal-held: control JumpShot should have cradled the dribble (HasDribbled -> true) synchronously; got state={_ball.State}, HasDribbled={_ball.HasDribbled}.");
                    Finish();
                    return;
                }
                GD.Print("[jab-step] PASS control — a JumpShot from the same state DOES flip HasDribbled true, proving the jab's non-effect is a real observation.");
                GD.Print("[jab-step] RESULT: PASS (exit 0)");
                Finish(0);
                return;
        }
    }

    // ── Scenario: legal-dead-held ─────────────────────────────────────────────
    // Cradles a dribble (JumpShot begin -> feint away -> wait for Recovery to
    // fully elapse) to reach DEAD Held (HasDribbled == true, machine Inactive),
    // then proves a jab begins from there too, and leaves HasDribbled STILL
    // true (untouched, not reset) and the ball STILL Held.
    private void TickLegalDeadHeld()
    {
        switch (_step)
        {
            case Step.AwaitTipoff:
                if (_frame < ArmFrames) return;
                if (_ball.StateMachine.HolderPeerId == 0)
                {
                    Fail("legal-dead-held: tipoff never assigned a holder.");
                    Finish();
                    return;
                }
                _holderId = _ball.StateMachine.HolderPeerId;

                // CradleForShotStartup only fires while Dribbling (its own
                // no-op-if-not-Dribbling guard) — a fresh tipoff starts live
                // Held, not Dribbling, so the dribble must be started first
                // (same setup TripleThreatTest's "cradle" step uses).
                _ball.TryStartDribble(_holderId);
                bool cradleBegan = NodeForPeer(_holderId).BeginJumpShotForHarness();
                if (!cradleBegan || !(_ball.State == BallState.Held && _ball.HasDribbled))
                {
                    Fail($"legal-dead-held: expected the JumpShot begin to cradle synchronously; began={cradleBegan}, state={_ball.State}, HasDribbled={_ball.HasDribbled}.");
                    Finish();
                    return;
                }
                _step = Step.CradleIssued;
                _stepDeadlineFrame = _frame + ActionMarginFrames;
                return;

            case Step.CradleIssued:
                // Wait so the JumpShot's Startup has advanced past its
                // FeintMinStartupFrames floor (default 3) before feinting —
                // same margin TripleThreatTest uses for the identical setup.
                if (_frame < _stepDeadlineFrame) return;

                bool feinted = NodeForPeer(_holderId).FeintForHarness();
                if (!feinted)
                {
                    Fail("legal-dead-held: FeintForHarness() returned false — outside JumpShot's feint window.");
                    Finish();
                    return;
                }
                _step = Step.AwaitInactiveForJab;
                return;

            case Step.AwaitInactiveForJab:
                // Wait for the feint's shortened Recovery to fully elapse
                // (machine -> Inactive) while HasDribbled stays true the
                // whole time (the dead-dribble rule, #193) — mirrors
                // TripleThreatTest's identical wait.
                if (NodeForPeer(_holderId).PhaseForHarness != MovePhase.Inactive) return;

                if (!(_ball.State == BallState.Held && _ball.HasDribbled))
                {
                    Fail($"legal-dead-held: expected to still be in DEAD Held once the feinted shot's Recovery elapsed; got state={_ball.State}, HasDribbled={_ball.HasDribbled}.");
                    Finish();
                    return;
                }

                PlayerController holder = NodeForPeer(_holderId);
                _startPosition = holder.GlobalPosition;
                bool jabBegan = holder.BeginMoveForHarness(new JabStep());
                if (!jabBegan)
                {
                    Fail("legal-dead-held: BeginMoveForHarness(JabStep) returned false from a DEAD Held possession — expected legal (the jab does not care about HasDribbled).");
                    Finish();
                    return;
                }
                GD.Print($"[jab-step] PASS begin — JabStep began from a DEAD Held possession (holder={_holderId}, HasDribbled=true).");
                _step = Step.LifecycleAwaited;
                return;

            case Step.LifecycleAwaited:
                if (NodeForPeer(_holderId).PhaseForHarness != MovePhase.Inactive) return;

                PlayerController h = NodeForPeer(_holderId);
                bool stillDead = _ball.State == BallState.Held && _ball.HasDribbled;
                bool noBurst = h.GlobalPosition.DistanceTo(_startPosition) < 0.01f;
                if (!(stillDead && noBurst))
                {
                    Fail($"legal-dead-held: expected HasDribbled to REMAIN true (untouched by the jab) and no burst displacement; " +
                         $"state={_ball.State}, HasDribbled={_ball.HasDribbled}, positionDelta={h.GlobalPosition.DistanceTo(_startPosition):F3}m.");
                    Finish();
                    return;
                }
                GD.Print("[jab-step] PASS still-dead — the jab left a DEAD Held possession exactly as it found it (HasDribbled still true), no burst.");
                GD.Print("[jab-step] RESULT: PASS (exit 0)");
                Finish(0);
                return;
        }
    }

    // ── Scenario: illegal-dribbling ───────────────────────────────────────────
    // From a LIVE Dribbling possession, a jab must be REFUSED outright. As the
    // control, a Hesitation (which IS legal from Dribbling — #86 already
    // covers the equivalent bait there) DOES begin from the identical state,
    // proving the refusal is jab-specific, not "nothing can begin while
    // Dribbling."
    private void TickIllegalDribbling()
    {
        switch (_step)
        {
            case Step.AwaitTipoff:
                if (_frame < ArmFrames) return;
                if (_ball.StateMachine.HolderPeerId == 0)
                {
                    Fail("illegal-dribbling: tipoff never assigned a holder.");
                    Finish();
                    return;
                }
                _holderId = _ball.StateMachine.HolderPeerId;

                _ball.TryStartDribble(_holderId);
                _step = Step.DriveIssued;
                _stepDeadlineFrame = _frame + ActionMarginFrames;
                return;

            case Step.DriveIssued:
                if (_frame < _stepDeadlineFrame) return;
                if (_ball.State != BallState.Dribbling)
                {
                    Fail($"illegal-dribbling: expected TryStartDribble to reach Dribbling; got state={_ball.State}.");
                    Finish();
                    return;
                }
                _step = Step.DribblingChecked;
                return;

            case Step.DribblingChecked:
                PlayerController holder = NodeForPeer(_holderId);
                bool jabBegan = holder.BeginMoveForHarness(new JabStep());
                bool refused = !jabBegan
                    && holder.PhaseForHarness == MovePhase.Inactive
                    && _ball.State == BallState.Dribbling;
                if (!refused)
                {
                    Fail($"illegal-dribbling: expected a jab to be refused while Dribbling; began={jabBegan}, phase={holder.PhaseForHarness}, ballState={_ball.State}.");
                    Finish();
                    return;
                }
                GD.Print("[jab-step] PASS refused — JabStep Begin was refused while Dribbling (no lifecycle entered, ball state unchanged).");

                // Control: a Hesitation (a real dribble-family move, legal
                // from Dribbling) DOES begin from the SAME Dribbling state —
                // proving the refusal above is jab-specific, not a broken
                // harness premise where nothing can begin while Dribbling.
                bool hesiBegan = holder.BeginMoveForHarness(new Hesitation());
                if (!hesiBegan)
                {
                    Fail("illegal-dribbling: control BeginMoveForHarness(Hesitation) returned false from Dribbling — expected legal, invalidating this refusal as a proof.");
                    Finish();
                    return;
                }
                GD.Print("[jab-step] PASS control — a Hesitation DOES begin from the same Dribbling state, proving the jab's refusal is move-specific.");
                GD.Print("[jab-step] RESULT: PASS (exit 0)");
                Finish(0);
                return;
        }
    }

    private PlayerController NodeForPeer(int peerId) => peerId == 1 ? _p1 : _p2;

    private void Fail(string message) => GD.PrintErr($"[jab-step] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[jab-step] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
