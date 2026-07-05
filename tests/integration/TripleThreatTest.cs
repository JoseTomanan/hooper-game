using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #193 (ADR-0016): triple-threat
// stance — Held-start possessions + the dead-dribble rule. Unit tests already
// pin DeadDribbleRule's pure predicate; what they CANNOT reach is the real
// glue this issue rewired: BallController.AwardPossession no longer
// auto-chaining into Dribbling, BallController.CradleForShotStartup firing as
// a side effect of PlayerController.BeginCommittedMove, and
// BallController.TryStartDribble's refusal actually blocking a StartDribble
// attempt end to end.
//
//   godot --headless --path . res://tests/integration/TripleThreatTest.tscn -- --harness-scenario=dead-dribble
//   godot --headless --path . res://tests/integration/TripleThreatTest.tscn -- --harness-scenario=production-drive
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "dead-dribble" (unchanged CI invocation).
//
// ── Why a single offline instance is the server ───────────────────────────
// Same reasoning as StealTurnoverTest/OobTurnoverTest: with no MultiplayerPeer
// assigned, Godot uses OfflineMultiplayerPeer (is_server() hardcoded true,
// unique_id 1), so BallController.IsServer is true and player "1" is
// IsLocalPlayer (TickServerOwnPlayer) — its _machine.Tick() advances every
// physics frame via SampleMoveInput, the same free clock StealTurnoverTest
// relies on for its defender.
//
// ── Scenario "dead-dribble": a single sequence of steps ────────────────────
// This test does not need frame-precise phase math (unlike the steal timing
// harness) — each step fires an action, waits a fixed small margin, and
// asserts, failing fast with a labelled step name on the first violation:
//   1. fresh-Held:       tipoff lands holder "1" in BallState.Held with a
//                        live dribble (HasDribbled == false) — NOT Dribbling
//                        (the pre-#193 behaviour this issue removed).
//   2. drive:            TryStartDribble succeeds from live Held -> Dribbling.
//   3. cradle:           BeginJumpShotForHarness (real BeginCommittedMove path)
//                        cradles the dribble as a side effect: Dribbling ->
//                        Held, HasDribbled -> true, SYNCHRONOUSLY (no tick
//                        delay — CradleForShotStartup runs inside Begin()).
//   4. dead-refusal:     TryStartDribble is now REFUSED — state stays Held,
//                        not Dribbling. This is #193's core acceptance
//                        criterion.
//   5. feint-still-dead: feinting the jump shot away (within its window)
//                        aborts the SHOT, but HasDribbled stays true and
//                        TryStartDribble is still refused — "a feinted
//                        pump-fake still leaves the player in dead Held" is
//                        called out explicitly as intentional in the issue.
//   6. dead-Held-crossover-refused: (#193 code-review fix) once the machine
//                        returns to Inactive (the feint's Recovery has fully
//                        elapsed) but the ball is STILL dead Held, attempting
//                        a Crossover through the real BeginCommittedMove path
//                        must be refused — a dribble move cannot begin while
//                        this player holds a Held ball, dead or live. Pins
//                        that the refusal actually blocks BOTH the burst
//                        (machine never leaves Inactive) and the HandSide
//                        flip (HandSide is unchanged), not just that the
//                        return value happens to be false.
//   7. reset-on-turnover: walking the holder out of bounds (the already-proven
//                        OOB turnover, ADR-0008) awards the ball to the
//                        opponent as a fresh, live Held possession —
//                        HasDribbled resets to false, and the NEW holder can
//                        immediately dribble.
//
// ── Scenario "production-drive": the input path, not the ball's contract ──
// (Code-review finding on #204.) "dead-dribble" above proves BallController's
// TryStartDribble CONTRACT by calling it directly — it never actually drives
// PlayerController.CheckAutoStartDribble, the production entry point real
// gameplay input reaches. A regression there (GetBall() returning null, the
// Name-parse guard rejecting a valid peer, or a tick path that stops calling
// it) would ship green under "dead-dribble" alone. This scenario instead
// presses REAL movement input (Input.ActionPress, the same Godot-supported
// automated-input mechanism PivotPlantTest already uses) on a fresh Held
// possession and asserts the Held -> Dribbling transition happens via the
// UNMODIFIED TickServerOwnPlayer -> SampleMoveInput -> ReadInput ->
// CheckAutoStartDribble -> BallController.TryStartDribble chain, with no
// direct ball call from the test at all.
public partial class TripleThreatTest : Node
{
    private const double TimeoutSeconds = 10.0;
    private const int ArmFrames = 2;      // ticks for TryAssignTipoffHolder to run
    private const int ActionMarginFrames = 3; // ticks to let an action's effect settle

    // Same OOB point OobTurnoverTest uses (beyond CourtMax.X, well inside the
    // far-backstop walls) — reused rather than re-derived so this test's OOB
    // step exercises the identical, already-proven geometry.
    private static readonly Vector3 OobPositiveX = new(9.0f, 0f, 5f);

    private string _scenario = "dead-dribble";

    private BallController _ball;
    private PlayerController _p1;
    private PlayerController _p2;

    private int _frame;
    private double _elapsed;
    private bool _finished;

    private enum Step
    {
        AwaitTipoff, FreshHeldChecked, DriveIssued, DriveChecked,
        CradleIssued, CradleChecked, DeadRefusalChecked,
        FeintIssued, FeintChecked, AwaitInactiveForCrossoverCheck,
        OobIssued, Done
    }

    private Step _step = Step.AwaitTipoff;
    private int _stepDeadlineFrame;
    private int _holderId;
    private int _otherId;

    // ── "production-drive" scenario state ───────────────────────────────────
    private enum DriveStep { AwaitTipoff, InputPressed, Verdict }
    private DriveStep _driveStep = DriveStep.AwaitTipoff;
    private int _driveHolderId;
    private int _driveVerdictFrame;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "dead-dribble");
        GD.Print($"[triple-threat] scenario={_scenario} booting headless…");

        // Same code-built-tree pattern as StealTurnoverTest/OobTurnoverTest —
        // avoids fragile .tscn ext_resource/uid wiring for a throwaway harness.
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

        if (_scenario == "production-drive")
        {
            TickProductionDrive();
            if (!_finished && _elapsed > TimeoutSeconds)
            {
                Fail($"timed out at frame {_frame}, driveStep {_driveStep}.");
                Finish();
            }
            return;
        }

        switch (_step)
        {
            case Step.AwaitTipoff:
                if (_frame < ArmFrames) break;
                if (_ball.StateMachine.HolderPeerId == 0)
                {
                    Fail("fresh-Held: tipoff never assigned a holder.");
                    Finish();
                    return;
                }

                _holderId = _ball.StateMachine.HolderPeerId;
                _otherId = _holderId == 1 ? 2 : 1;

                bool freshHeld = _ball.State == BallState.Held;
                bool liveDribble = !_ball.HasDribbled;
                if (!(freshHeld && liveDribble))
                {
                    Fail($"fresh-Held: expected state=Held, HasDribbled=false at tipoff; got state={_ball.State}, HasDribbled={_ball.HasDribbled}.");
                    Finish();
                    return;
                }
                GD.Print($"[triple-threat] PASS fresh-Held — holder={_holderId}, state=Held, HasDribbled=false.");

                // Step 2: issue the drive.
                _ball.TryStartDribble(_holderId);
                _step = Step.DriveChecked;
                _stepDeadlineFrame = _frame + ActionMarginFrames;
                break;

            case Step.DriveChecked:
                if (_frame < _stepDeadlineFrame) break;
                if (_ball.State != BallState.Dribbling)
                {
                    Fail($"drive: expected TryStartDribble to move a live Held possession to Dribbling; got state={_ball.State}.");
                    Finish();
                    return;
                }
                GD.Print("[triple-threat] PASS drive — TryStartDribble resumed Dribbling from live Held.");

                // Step 3: cradle via the real JumpShot begin path. Runs
                // SYNCHRONOUSLY (CradleForShotStartup fires inside Begin()),
                // so we can assert immediately without waiting a tick.
                PlayerController holder = NodeForPeer(_holderId);
                bool began = holder.BeginJumpShotForHarness();
                if (!began)
                {
                    Fail("cradle: BeginJumpShotForHarness returned false — machine was not Inactive.");
                    Finish();
                    return;
                }
                bool cradled = _ball.State == BallState.Held && _ball.HasDribbled;
                if (!cradled)
                {
                    Fail($"cradle: expected state=Held, HasDribbled=true immediately after JumpShot begin; got state={_ball.State}, HasDribbled={_ball.HasDribbled}.");
                    Finish();
                    return;
                }
                GD.Print("[triple-threat] PASS cradle — JumpShot Startup cradled the dribble synchronously (Held, HasDribbled=true).");

                // Step 4: the dead-dribble refusal — the core #193 acceptance
                // criterion. Attempt to redribble; it must be refused.
                _ball.TryStartDribble(_holderId);
                _step = Step.DeadRefusalChecked;
                _stepDeadlineFrame = _frame + ActionMarginFrames;
                break;

            case Step.DeadRefusalChecked:
                if (_frame < _stepDeadlineFrame) break;
                if (_ball.State != BallState.Held)
                {
                    Fail($"dead-refusal: TryStartDribble should have been refused (dead dribble) but ball is state={_ball.State}.");
                    Finish();
                    return;
                }
                GD.Print("[triple-threat] PASS dead-refusal — TryStartDribble was refused while HasDribbled is set.");

                // Step 5: feint the shot away within its window (JumpShot's
                // feint window starts at frame 3 of Startup — see JumpShot.cs
                // DefaultFrameData — the machine has already ticked past that
                // by now via TickServerOwnPlayer's automatic per-frame
                // SampleMoveInput -> _machine.Tick()).
                PlayerController holder2 = NodeForPeer(_holderId);
                bool feinted = holder2.FeintForHarness();
                if (!feinted)
                {
                    Fail($"feint-still-dead: FeintForHarness returned false (outside its window) — increase ActionMarginFrames or re-check JumpShot's feint window.");
                    Finish();
                    return;
                }
                _step = Step.FeintChecked;
                _stepDeadlineFrame = _frame + ActionMarginFrames;
                break;

            case Step.FeintChecked:
                if (_frame < _stepDeadlineFrame) break;
                bool stillDeadAfterFeint = _ball.State == BallState.Held && _ball.HasDribbled;
                if (!stillDeadAfterFeint)
                {
                    Fail($"feint-still-dead: expected the canceled pump-fake to leave the player in dead Held (state=Held, HasDribbled=true); got state={_ball.State}, HasDribbled={_ball.HasDribbled}.");
                    Finish();
                    return;
                }
                // A redribble attempt right after the feint must ALSO still be
                // refused — the dead-dribble flag is possession-scoped, not
                // move-scoped, so it must outlive the aborted shot itself.
                _ball.TryStartDribble(_holderId);
                if (_ball.State != BallState.Held)
                {
                    Fail($"feint-still-dead: TryStartDribble should still be refused right after the feint; got state={_ball.State}.");
                    Finish();
                    return;
                }
                GD.Print("[triple-threat] PASS feint-still-dead — a canceled pump-fake still leaves the player in dead Held.");

                // Step 6: wait for the feint's Recovery to fully elapse (machine
                // -> Inactive) before attempting the crossover-refusal check —
                // otherwise a refusal would be indistinguishable from the
                // ordinary "already in a move" Begin() guard, not the NEW
                // Held-holder gate this step exists to pin.
                _step = Step.AwaitInactiveForCrossoverCheck;
                break;

            case Step.AwaitInactiveForCrossoverCheck:
                PlayerController holder3 = NodeForPeer(_holderId);
                if (holder3.MachinePhaseForHarness != MovePhase.Inactive) break;

                HandSide handSideBefore = holder3.HandSide;
                bool crossoverBegan = holder3.BeginCrossoverForHarness(1f);
                bool refused = !crossoverBegan
                    && holder3.MachinePhaseForHarness == MovePhase.Inactive
                    && holder3.HandSide == handSideBefore
                    && _ball.State == BallState.Held;
                if (!refused)
                {
                    Fail($"dead-Held-crossover-refused: expected a dribble move to be refused while holding a Held ball; began={crossoverBegan}, phase={holder3.MachinePhaseForHarness}, handSide {handSideBefore}->{holder3.HandSide}, ballState={_ball.State}.");
                    Finish();
                    return;
                }
                GD.Print("[triple-threat] PASS dead-Held-crossover-refused — Crossover Begin was refused (no burst, no HandSide flip) while holding a Held ball.");

                // Step 7: turn the ball over via the OOB rule (ADR-0008,
                // already proven by OobTurnoverTest) and confirm the fresh
                // possession resets HasDribbled and starts live.
                NodeForPeer(_holderId).GlobalPosition = OobPositiveX;
                _step = Step.OobIssued;
                _stepDeadlineFrame = _frame + ActionMarginFrames;
                break;

            case Step.OobIssued:
                if (_frame < _stepDeadlineFrame) break;
                bool flippedToOther = _ball.StateMachine.HolderPeerId == _otherId;
                bool freshOnTurnover = _ball.State == BallState.Held && !_ball.HasDribbled;
                if (!(flippedToOther && freshOnTurnover))
                {
                    Fail($"reset-on-turnover: expected holder={_otherId}, state=Held, HasDribbled=false after the OOB turnover; got holder={_ball.StateMachine.HolderPeerId}, state={_ball.State}, HasDribbled={_ball.HasDribbled}.");
                    Finish();
                    return;
                }

                // The new holder must be able to dribble immediately — proves
                // the reset actually un-gates TryStartDribble, not merely that
                // the flag reads false.
                _ball.TryStartDribble(_otherId);
                if (_ball.State != BallState.Dribbling)
                {
                    Fail($"reset-on-turnover: new holder {_otherId} could not resume a live dribble after the reset; state={_ball.State}.");
                    Finish();
                    return;
                }

                GD.Print("[triple-threat] PASS reset-on-turnover — the OOB turnover started a fresh, live Held possession for the new holder.");
                GD.Print("[triple-threat] RESULT: PASS (exit 0)");
                Finish(0);
                return;
        }

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame}, step {_step}.");
            Finish();
        }
    }

    // ── Scenario: "production-drive" (#204 code-review fix) ─────────────────
    private void TickProductionDrive()
    {
        switch (_driveStep)
        {
            case DriveStep.AwaitTipoff:
                if (_frame < ArmFrames) break;
                if (_ball.StateMachine.HolderPeerId == 0)
                {
                    Fail("production-drive: tipoff never assigned a holder.");
                    Finish();
                    return;
                }

                // This offline harness only has real HARDWARE input on peer
                // "1" (OfflineMultiplayerPeer's unique_id is 1, so "1" is both
                // IsServer and IsLocalPlayer -> TickServerOwnPlayer reads
                // Input.GetVector for real). The tipoff always awards peer "1"
                // first in this code-built tree's child order (see
                // BallController.TryAssignTipoffHolder), but assert it rather
                // than assume it, so a future change to spawn order fails
                // loudly here instead of silently testing nothing.
                _driveHolderId = _ball.StateMachine.HolderPeerId;
                if (_driveHolderId != 1)
                {
                    Fail($"production-drive: expected the tipoff to award peer 1 (the only peer with real hardware input in this harness), got {_driveHolderId}.");
                    Finish();
                    return;
                }
                if (_ball.State != BallState.Held || _ball.HasDribbled)
                {
                    Fail($"production-drive: expected a fresh live Held possession before pressing input; got state={_ball.State}, HasDribbled={_ball.HasDribbled}.");
                    Finish();
                    return;
                }

                // Press REAL movement input — Input.ActionPress is Godot's own
                // supported mechanism for driving the Input singleton in
                // automated tests (no display/window needed), the same
                // mechanism PivotPlantTest already uses. This exercises the
                // UNMODIFIED TickServerOwnPlayer -> SampleMoveInput ->
                // ReadInput -> CheckAutoStartDribble -> BallController.
                // TryStartDribble chain — no test-only seam for this step.
                Input.ActionPress("move_left", 1.0f);
                GD.Print($"[triple-threat] production-drive: frame {_frame}: pressed move_left for holder {_driveHolderId}");

                // Frame-stamp gotcha (empirically pinned by PivotPlantTest's
                // #172 CI repro, and reused verbatim here): ActionPress sets
                // the pressed state immediately, but this test (the PARENT of
                // the PlayerController it drives) ticks BEFORE its children
                // each frame — so the earliest frame this Root can OBSERVE an
                // effect the CHILD's SAME-frame _PhysicsProcess produced is
                // one frame later. ActionMarginFrames (3 ticks) covers that
                // with room to spare rather than relying on an exact off-by-one.
                _driveStep = DriveStep.InputPressed;
                _driveVerdictFrame = _frame + ActionMarginFrames;
                break;

            case DriveStep.InputPressed:
                if (_frame < _driveVerdictFrame) break;
                Input.ActionRelease("move_left"); // hygiene — not load-bearing in a one-shot headless process

                if (_ball.State != BallState.Dribbling)
                {
                    Fail($"production-drive: expected real move_left input to drive Held -> Dribbling via CheckAutoStartDribble -> TryStartDribble; got state={_ball.State}.");
                    Finish();
                    return;
                }

                GD.Print("[triple-threat] PASS production-drive — real movement input drove Held -> Dribbling through the unmodified production input path.");
                GD.Print("[triple-threat] RESULT: PASS (exit 0)");
                Finish(0);
                return;
        }
    }

    private PlayerController NodeForPeer(int peerId) => peerId == 1 ? _p1 : _p2;

    private void Fail(string message) => GD.PrintErr($"[triple-threat] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        if (code != 0)
            GD.Print($"[triple-threat] RESULT: FAIL (exit {code})");
        GetTree().Quit(code);
    }
}
