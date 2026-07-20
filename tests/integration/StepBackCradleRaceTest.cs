using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #253 (ADR-0016): StepBack shares
// #225's cross-channel cradle race, but at its ACTIVE-ENTRY gather rather than
// Begin-time. RequestBeginMove (Reliable) and SubmitInput (UnreliableOrdered)
// have no cross-channel ordering: a client that drives then step-backs within
// ~1 tick can have the server process Begin(StepBack) BEFORE that drive's
// SubmitInput arrives. Unlike JumpShot (#225, which cradles synchronously at
// Begin), StepBack is deliberately exempt from the dead-dribble Held-gate and
// cradles LATER, at Active-entry (PlayerController.TickCommittedMoveBehavior's
// StepBack branch → BallController.CradleForShotStartup). If the drive's
// SubmitInput still hasn't arrived by that Active-entry tick, the server's ball
// copy is still Held, CradleForShotStartup no-ops, and HasDribbled stays false
// — a silent dead-dribble bypass, just with a longer (Startup-duration) window
// than JumpShot's.
//
//   godot --headless --path . res://tests/integration/StepBackCradleRaceTest.tscn -- --harness-scenario=out-of-order
//   godot --headless --path . res://tests/integration/StepBackCradleRaceTest.tscn -- --harness-scenario=in-order
//   godot --headless --path . res://tests/integration/StepBackCradleRaceTest.tscn -- --harness-scenario=no-drive-ever
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "out-of-order".
//
// ── Why a single offline instance is the server ───────────────────────────
// Same reasoning as CradleRaceTest/AwardFreshnessTest: OfflineMultiplayerPeer
// makes player "1" IsLocalPlayer (TickServerOwnPlayer) and player "2"
// IsServer && !IsLocalPlayer (TickServerRemotePlayer). This harness swaps the
// default child order (peer "2" added FIRST) so the tipoff awards peer 2 the
// fresh Held possession — the race is about the REMOTE holder specifically,
// the one role whose ball copy can lag its drive input across the wire, and
// the only role whose Active-entry cradle consults the boolean's no-op branch.
//
// ── Why this can't be a synchronous assertion like CradleRaceTest's ───────
// CradleRaceTest asserts convergence SYNCHRONOUSLY right after Begin because
// JumpShot cradles inside Begin(). StepBack cradles at ACTIVE-entry, several
// ticks later, so each scenario must TICK the real machine through Startup
// (TickServerRemotePlayer._machine.Tick() advances it every frame) and assert
// only AFTER the Active-entry gather has fired. This deferred timing is the
// whole reason #253 is a distinct exposure and not covered by #225's harness.
//
// ── The fix's mechanism (identical disambiguator to #225) ─────────────────
// The client tells the server (via clientWasAlreadyDribbling on the
// RequestBeginMove RPC) whether ITS OWN ball copy was already Dribbling when
// it fired this Begin. #253 carries that boolean on the StepBack INSTANCE
// (StepBack.ClientWasAlreadyDribbling) so it survives the Begin→Active-entry
// gap, then passes it to CradleForShotStartup at Active-entry. This harness's
// seam (ApplyRequestedMoveForHarness) supplies that boolean directly, exactly
// mirroring what a real client's RequestBeginMove RPC payload would carry and
// exercising the REAL ApplyRequestedMove("stepback", …) reconstruction path
// the fix modifies.
//
// ── Scenario "out-of-order": the race, reproduced ─────────────────────────
//   Begin(StepBack) processed while the SERVER's ball is still Held (no drive
//   delivered), but the client reports (accurately, per its own zero-latency
//   timeline) that its OWN ball was already Dribbling. Tick through Startup to
//   Active-entry, then assert the gather left the ball Held with
//   HasDribbled=TRUE — the boolean rescued the no-op branch. Pre-fix this
//   asserts false (the silent bypass).
//
// ── Scenario "in-order": the paired control ───────────────────────────────
// Proves the fix does not change the ALREADY-correct ordering: drive first
// (peer 2 reaches live Dribbling via the harness input seam, mirroring
// SubmitInput), THEN Begin(StepBack). At Active-entry the ball is genuinely
// Dribbling, so CradleForShotStartup's NORMAL (non-no-op) branch fires — the
// boolean is irrelevant. Final state must be IDENTICAL to "out-of-order"'s:
// Held, HasDribbled=true.
//
// ── Scenario "no-drive-ever": the triple-threat preservation control ──────
// The hard constraint the fix must not violate: a StepBack begun from a
// genuine Held possession with NO drive ever attempted must NOT fabricate a
// dribble to gather. Begins with clientWasAlreadyDribbling=false (nothing to
// report), ticks to Active-entry, and asserts the gather no-op'd cleanly —
// ball still Held, HasDribbled STILL false. This is StepBack's analogue of
// CradleRaceTest's shoot-from-triple-threat control.
public partial class StepBackCradleRaceTest : Node
{
    private const double TimeoutSeconds = 10.0;
    private const int ArmFrames = 2;          // ticks for TryAssignTipoffHolder to run
    private const int ActionMarginFrames = 4; // ticks to let the drive settle to Dribbling

    // StepBack's DefaultFrameData: startup=7. The Active-entry gather fires on
    // the tick Startup is exhausted; a margin of 12 comfortably clears it plus
    // several ticks past, proving the gather's effect persists (not a one-tick
    // fluke) and holds through the rest of the move's life.
    private const int StepBackActiveMarginFrames = 12;

    private string _scenario = "out-of-order";

    private BallController _ball;
    private PlayerController _p1;
    private PlayerController _p2;

    private int _frame;
    private double _elapsed;
    private bool _finished;

    private enum Step { AwaitTipoff, FirstEventIssued, BeginStepBack, AwaitGather }
    private Step _step = Step.AwaitTipoff;
    private int _deadlineFrame;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "out-of-order");
        GD.Print($"[stepback-cradle-race] scenario={_scenario} booting headless…");

        // Same code-built-tree pattern as CradleRaceTest — peer "2" is added
        // FIRST so TryAssignTipoffHolder's child-order walk awards the fresh
        // Held possession to peer 2, the REMOTE-player role this race is about.
        var players = new Node3D { Name = "Players" };
        _p2 = new PlayerController { Name = "2" };
        _p1 = new PlayerController { Name = "1" };
        players.AddChild(_p2);
        players.AddChild(_p1);

        _ball = new BallController { Name = "Ball", Players = players };

        AddChild(players);
        AddChild(_ball);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        Tick();

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame}, step={_step}.");
            Finish();
        }
    }

    private void Tick()
    {
        switch (_step)
        {
            case Step.AwaitTipoff:
                if (_frame < ArmFrames) break;
                if (_ball.StateMachine.HolderPeerId != 2)
                {
                    Fail($"await-tipoff: expected the tipoff to award peer 2 (swapped child order); got holder={_ball.StateMachine.HolderPeerId}.");
                    Finish();
                    return;
                }
                if (_ball.State != BallState.Held || _ball.HasDribbled)
                {
                    Fail($"await-tipoff: expected a fresh live Held possession; got state={_ball.State}, HasDribbled={_ball.HasDribbled}.");
                    Finish();
                    return;
                }
                GD.Print("[stepback-cradle-race] PASS await-tipoff — holder=2, state=Held, HasDribbled=false.");

                if (_scenario == "in-order")
                {
                    // The drive FIRST — production TryStartDribble via the
                    // harness input seam (mirrors SubmitInput). Peer 2 is the
                    // remote role, so TickServerRemotePlayer.Move +
                    // CheckAutoStartDribble applies this over the next few ticks.
                    // seq=5 clears the #224 award-stamp gate (same value
                    // CradleRaceTest's in-order uses).
                    _p2.SetPendingInputForHarness(5, new Vector2(0f, 1f));
                    _step = Step.FirstEventIssued;
                    _deadlineFrame = _frame + ActionMarginFrames;
                }
                else
                {
                    // out-of-order / no-drive-ever: no drive is delivered before
                    // the Begin — go straight to issuing the StepBack.
                    _step = Step.BeginStepBack;
                }
                break;

            case Step.FirstEventIssued: // in-order only
                if (_frame < _deadlineFrame) break;
                if (_ball.State != BallState.Dribbling)
                {
                    Fail($"drive-first: expected the drive to reach live Dribbling before Begin(StepBack); got state={_ball.State}.");
                    Finish();
                    return;
                }
                GD.Print("[stepback-cradle-race] PASS drive-first — the drive reached live Dribbling (in-order case).");
                _step = Step.BeginStepBack;
                break;

            case Step.BeginStepBack:
                // For "out-of-order" the client reports it WAS already dribbling
                // (true) even though the SERVER's ball is still Held — the race.
                // For "no-drive-ever" the client has nothing to report (false):
                // a genuine StepBack from triple threat. For "in-order" the ball
                // is already Dribbling server-side, so the boolean is inert (the
                // normal gather branch handles it) — pass true for parity with
                // out-of-order, proving it changes nothing there.
                bool clientWasAlreadyDribbling = _scenario != "no-drive-ever";

                _p2.ApplyRequestedMoveForHarness("stepback", 0f, clientWasAlreadyDribbling);
                if (_p2.MachinePhaseForHarness != MovePhase.Startup)
                {
                    Fail($"begin-stepback: expected StepBack to begin (Startup); got phase={_p2.MachinePhaseForHarness}.");
                    Finish();
                    return;
                }

                // StepBack cradles at ACTIVE-entry, not now — so at THIS instant
                // HasDribbled must still be whatever it was before the Begin
                // (false for out-of-order/no-drive-ever; true for in-order,
                // which already dribbled). Guard against a stray Begin-time
                // cradle sneaking in (it must not — that would be a different
                // bug: StepBack is exempt from the Begin-time cradle gate).
                if (_scenario != "in-order" && _ball.HasDribbled)
                {
                    Fail($"begin-stepback: StepBack must NOT cradle at Begin (only at Active-entry); HasDribbled already true right after Begin.");
                    Finish();
                    return;
                }
                GD.Print($"[stepback-cradle-race] PASS begin-stepback — StepBack begun (Startup), clientWasAlreadyDribbling={clientWasAlreadyDribbling}; gather deferred to Active-entry.");

                _step = Step.AwaitGather;
                _deadlineFrame = _frame + StepBackActiveMarginFrames;
                break;

            case Step.AwaitGather:
                if (_frame < _deadlineFrame) break;

                // After the Active-entry gather has fired and several ticks have
                // passed:
                //   out-of-order — the no-op branch honored the boolean:
                //                  Held, HasDribbled=TRUE (the #253 fix).
                //   in-order      — the normal branch cradled the live dribble:
                //                  Held, HasDribbled=true.
                //   no-drive-ever — the no-op branch did nothing (boolean false):
                //                  Held, HasDribbled=FALSE (triple-threat intact).
                bool expectHasDribbled = _scenario != "no-drive-ever";

                if (_ball.State != BallState.Held || _ball.HasDribbled != expectHasDribbled)
                {
                    Fail($"verdict ({_scenario}): expected state=Held, HasDribbled={expectHasDribbled} after the Active-entry gather; got state={_ball.State}, HasDribbled={_ball.HasDribbled}.");
                    Finish();
                    return;
                }
                GD.Print($"[stepback-cradle-race] PASS verdict ({_scenario}) — state=Held, HasDribbled={_ball.HasDribbled} after the Active-entry gather.");
                GD.Print("[stepback-cradle-race] RESULT: PASS (exit 0)");
                Finish(0);
                return;
        }
    }

    private void Fail(string message) => GD.PrintErr($"[stepback-cradle-race] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        if (code != 0)
            GD.Print($"[stepback-cradle-race] RESULT: FAIL (exit {code})");
        GetTree().Quit(code);
    }
}
