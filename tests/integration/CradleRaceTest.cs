using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #225 (ADR-0016): cradle no-op —
// Reliable Begin(JumpShot) overtaking UnreliableOrdered dribble input,
// bypassing dead-dribble (Race 2 of #207). RequestBeginMove (Reliable) and
// SubmitInput (UnreliableOrdered) have no cross-channel ordering: a client
// that drives then pump-fakes within ~1 tick can have the server process
// Begin(JumpShot) BEFORE that drive's SubmitInput arrives. Pre-fix,
// CradleForShotStartup's guard silently no-op'd (ball still Held), so
// HasDribbled stayed false and the LATE drive input then legally started a
// live dribble the shot's gather should have already cradled.
//
//   godot --headless --path . res://tests/integration/CradleRaceTest.tscn -- --harness-scenario=out-of-order
//   godot --headless --path . res://tests/integration/CradleRaceTest.tscn -- --harness-scenario=in-order
//   godot --headless --path . res://tests/integration/CradleRaceTest.tscn -- --harness-scenario=later-legit-drive
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//
// ── Why a single offline instance is the server ───────────────────────────
// Same reasoning as TripleThreatTest/AwardFreshnessTest: OfflineMultiplayerPeer
// makes player "1" IsLocalPlayer (TickServerOwnPlayer) and player "2"
// IsServer && !IsLocalPlayer (TickServerRemotePlayer). This harness swaps the
// default child order (peer "2" added FIRST) so the tipoff awards peer 2 the
// fresh Held possession — the race is about the REMOTE holder specifically.
//
// ── The fix's actual mechanism (redesigned after an empirical dead end) ──
// A first design attempted to resolve the race lazily: remember a "pending
// cradle" seq ceiling at the no-op, and let a LATER TryStartDribble call
// (once the delayed drive packet arrives) decide by comparing seqs. Running
// that design against the REAL production tick loop (not a synthetic seam)
// disproved it: CommittedMoveMachine.IsActive means "Phase != Inactive" —
// true for the WHOLE Startup+Active+Recovery span — so
// PlayerController.TickServerRemotePlayer's CheckAutoStartDribble call is
// skipped for the entire life of the shot; the delayed packet is (almost
// always, per the client zeroing moveInput while IsActive) long since
// superseded by later in-order zero packets by the time the machine returns
// to Inactive and CheckAutoStartDribble runs again. The SHIPPED fix instead
// resolves the race IMMEDIATELY at Begin-time: the client tells the server
// (via a boolean, clientWasAlreadyDribbling) whether ITS OWN ball copy was
// already Dribbling when it fired this Begin — zero-latency, always
// accurate from the client's own point of view. This harness's seam
// (ApplyRequestedMoveForHarness) supplies that boolean directly, exactly
// mirroring what a real client's RequestBeginMove RPC payload would carry.
//
// ── Scenario "out-of-order": the race, reproduced ─────────────────────────
//   1. await-tipoff:  tipoff lands peer 2 in a fresh Held possession.
//   2. begin-race:    ApplyRequestedMoveForHarness("jumpshot", 0f,
//                     clientWasAlreadyDribbling: true) reproduces "Begin
//                     (JumpShot) processed while the SERVER's ball is still
//                     Held, but the CLIENT's own ball was already Dribbling"
//                     — the out-of-order case. Asserts SYNCHRONOUSLY (no
//                     tick delay — the fix resolves entirely at Begin-time,
//                     unlike the disproven lazy design) that the ball
//                     converges to Held, HasDribbled=true — matching the
//                     in-order case's own final state exactly.
//
// ── Scenario "in-order": the paired control ───────────────────────────────
// Proves the fix does not change the ALREADY-correct ordering: drive first
// (peer 2 reaches live Dribbling via the harness input seam, mirroring
// SubmitInput), THEN Begin(JumpShot) — CradleForShotStartup's NORMAL
// (non-no-op) branch fires synchronously, exactly as before this fix. Final
// state must be IDENTICAL to "out-of-order"'s: Held, HasDribbled=true.
//
// ── Scenario "later-legit-drive": the Case-1 preservation control ────────
// The hard constraint the fix must not violate (per #225's own doc and the
// orchestrator's CRITICAL TRAP): a shot-fake from Held with NO drive ever
// attempted must leave a LATER, genuinely new drive attempt in the SAME
// possession completely unaffected. Begins a JumpShot with
// clientWasAlreadyDribbling=false (nothing to report — no drive in flight),
// feints it away, waits for the machine to return fully to Inactive (so
// this is unambiguously "well after", not adjacent to the no-op), THEN
// issues a drive. Must succeed normally: Dribbling, HasDribbled still false.
public partial class CradleRaceTest : Node
{
    private const double TimeoutSeconds = 10.0;
    private const int ArmFrames = 2;          // ticks for TryAssignTipoffHolder to run
    private const int ActionMarginFrames = 4; // ticks to let an action's effect settle

    private string _scenario = "out-of-order";

    private BallController _ball;
    private PlayerController _p1;
    private PlayerController _p2;

    private int _frame;
    private double _elapsed;
    private bool _finished;

    // ── "out-of-order" / "in-order" shared step machine ─────────────────────
    private enum RaceStep { AwaitTipoff, FirstEventIssued, Verdict }
    private RaceStep _raceStep = RaceStep.AwaitTipoff;
    private int _raceDeadlineFrame;

    // ── "later-legit-drive" step machine ────────────────────────────────────
    private enum LegitStep { AwaitTipoff, BeginIssued, FeintIssued, AwaitInactive, DriveIssued, Verdict }
    private LegitStep _legitStep = LegitStep.AwaitTipoff;
    private int _legitDeadlineFrame;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "out-of-order");
        GD.Print($"[cradle-race] scenario={_scenario} booting headless…");

        // Same code-built-tree pattern as TripleThreatTest — but peer "2" is
        // added FIRST so TryAssignTipoffHolder's child-order walk awards the
        // fresh Held possession to peer 2, the REMOTE-player role this race
        // is about (see class doc).
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

        if (_scenario == "later-legit-drive")
            TickLegit();
        else
            TickRace();

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame}, raceStep={_raceStep}, legitStep={_legitStep}.");
            Finish();
        }
    }

    // ── "out-of-order" (default) / "in-order" ───────────────────────────────
    private void TickRace()
    {
        switch (_raceStep)
        {
            case RaceStep.AwaitTipoff:
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
                GD.Print("[cradle-race] PASS await-tipoff — holder=2, state=Held, HasDribbled=false.");

                if (_scenario == "out-of-order")
                {
                    // The race: Begin(JumpShot) processed while the SERVER's
                    // ball is still Held, but the client reports (accurately,
                    // per its own zero-latency timeline) that its OWN ball
                    // was already Dribbling. The fix resolves this
                    // SYNCHRONOUSLY — no tick delay needed at all.
                    _p2.ApplyRequestedMoveForHarness("jumpshot", 0f, clientWasAlreadyDribbling: true);
                    if (_p2.MachinePhaseForHarness != MovePhase.Startup)
                    {
                        Fail($"begin-race: expected JumpShot to begin (Startup); got phase={_p2.MachinePhaseForHarness}.");
                        Finish();
                        return;
                    }
                    bool convergedSynchronously = _ball.State == BallState.Held && _ball.HasDribbled;
                    if (!convergedSynchronously)
                    {
                        Fail($"begin-race: expected SYNCHRONOUS convergence to state=Held, HasDribbled=true; got state={_ball.State}, HasDribbled={_ball.HasDribbled}.");
                        Finish();
                        return;
                    }
                    GD.Print("[cradle-race] PASS begin-race — Begin(JumpShot) processed with clientWasAlreadyDribbling=true while the SERVER's ball was still Held; resolved synchronously to Held, HasDribbled=true.");
                }
                else // "in-order"
                {
                    // The drive FIRST — production TryStartDribble via the
                    // harness input seam (mirrors SubmitInput).
                    _p2.SetPendingInputForHarness(5, new Vector2(0f, 1f));
                }

                _raceStep = RaceStep.FirstEventIssued;
                _raceDeadlineFrame = _frame + ActionMarginFrames;
                break;

            case RaceStep.FirstEventIssued:
                if (_frame < _raceDeadlineFrame) break;

                if (_scenario == "in-order")
                {
                    if (_ball.State != BallState.Dribbling)
                    {
                        Fail($"drive-first: expected the drive to reach live Dribbling before Begin(JumpShot); got state={_ball.State}.");
                        Finish();
                        return;
                    }
                    GD.Print("[cradle-race] PASS drive-first — the drive reached live Dribbling (in-order case).");

                    // Begin(JumpShot) AFTER the ball is already Dribbling —
                    // CradleForShotStartup's NORMAL branch fires
                    // synchronously (clientWasAlreadyDribbling is irrelevant
                    // here — the ordinary path handles it, exactly as
                    // #193 shipped it, unmodified by this fix).
                    _p2.ApplyRequestedMoveForHarness("jumpshot", 0f, clientWasAlreadyDribbling: true);
                    bool cradledSynchronously = _ball.State == BallState.Held && _ball.HasDribbled;
                    if (!cradledSynchronously)
                    {
                        Fail($"begin-second: expected the NORMAL cradle path to fire synchronously (state=Held, HasDribbled=true); got state={_ball.State}, HasDribbled={_ball.HasDribbled}.");
                        Finish();
                        return;
                    }
                    GD.Print("[cradle-race] PASS begin-second — Begin(JumpShot) cradled the live dribble synchronously (in-order case).");
                }

                _raceStep = RaceStep.Verdict;
                _raceDeadlineFrame = _frame + ActionMarginFrames;
                break;

            case RaceStep.Verdict:
                if (_frame < _raceDeadlineFrame) break;
                // The core #225 assertion for BOTH scenarios: whichever order
                // the two events landed in, the final state converges —
                // Held, HasDribbled=true. Never a live Dribbling with
                // HasDribbled=false (the silent dead-dribble bypass). Waited
                // several MORE ticks past the synchronous check above to
                // prove the convergence holds, not just a one-tick fluke.
                bool converged = _ball.State == BallState.Held && _ball.HasDribbled;
                if (!converged)
                {
                    Fail($"verdict ({_scenario}): expected convergence to state=Held, HasDribbled=true; got state={_ball.State}, HasDribbled={_ball.HasDribbled}.");
                    Finish();
                    return;
                }
                GD.Print($"[cradle-race] PASS verdict ({_scenario}) — converged to Held, HasDribbled=true.");
                GD.Print("[cradle-race] RESULT: PASS (exit 0)");
                Finish(0);
                return;
        }
    }

    // ── "later-legit-drive" ──────────────────────────────────────────────────
    private void TickLegit()
    {
        switch (_legitStep)
        {
            case LegitStep.AwaitTipoff:
                if (_frame < ArmFrames) break;
                if (_ball.StateMachine.HolderPeerId != 2)
                {
                    Fail($"await-tipoff: expected the tipoff to award peer 2 (swapped child order); got holder={_ball.StateMachine.HolderPeerId}.");
                    Finish();
                    return;
                }
                GD.Print("[cradle-race] PASS await-tipoff — holder=2, state=Held, HasDribbled=false.");

                // Begin(JumpShot) with clientWasAlreadyDribbling=false — the
                // client has NOTHING to report (no drive in flight at all).
                // This is Case 1: a shoot-from-triple-threat with no drive
                // ever attempted.
                _p2.ApplyRequestedMoveForHarness("jumpshot", 0f, clientWasAlreadyDribbling: false);
                if (_p2.MachinePhaseForHarness != MovePhase.Startup)
                {
                    Fail($"begin-no-drive: expected JumpShot to begin (Startup); got phase={_p2.MachinePhaseForHarness}.");
                    Finish();
                    return;
                }
                if (_ball.State != BallState.Held || _ball.HasDribbled)
                {
                    Fail($"begin-no-drive: expected state=Held, HasDribbled=false (Case 1 — no drive ever) immediately after Begin; got state={_ball.State}, HasDribbled={_ball.HasDribbled}.");
                    Finish();
                    return;
                }
                GD.Print("[cradle-race] PASS begin-no-drive — JumpShot begun from Held with no drive ever attempted (Case 1).");

                _legitStep = LegitStep.BeginIssued;
                _legitDeadlineFrame = _frame + ActionMarginFrames;
                break;

            case LegitStep.BeginIssued:
                if (_frame < _legitDeadlineFrame) break;

                // Feint the shot away — JumpShot's feint window opens at
                // frame 3 of Startup (see JumpShot.cs DefaultFrameData); the
                // margin above already ticked the machine well past that.
                bool feinted = _p2.FeintForHarness();
                if (!feinted)
                {
                    Fail("feint: FeintForHarness returned false (outside its window) — increase ActionMarginFrames.");
                    Finish();
                    return;
                }
                GD.Print("[cradle-race] PASS feint — the shot-fake was feinted away.");

                _legitStep = LegitStep.AwaitInactive;
                break;

            case LegitStep.AwaitInactive:
                // Wait for the feint's own (cheaper) Recovery to fully
                // elapse — the machine must be genuinely Inactive, not just
                // "not Startup", so this drive attempt is unambiguously
                // "well after" the no-op'd Begin, not adjacent to it.
                if (_p2.MachinePhaseForHarness != MovePhase.Inactive) break;
                GD.Print("[cradle-race] PASS await-inactive — the feinted JumpShot's Recovery fully elapsed.");

                // A genuinely NEW, later drive. Must succeed as an ORDINARY
                // drive, not collapse into a cradle.
                _p2.SetPendingInputForHarness(999, new Vector2(0f, 1f));
                _legitStep = LegitStep.DriveIssued;
                _legitDeadlineFrame = _frame + ActionMarginFrames;
                break;

            case LegitStep.DriveIssued:
                if (_frame < _legitDeadlineFrame) break;
                _legitStep = LegitStep.Verdict;
                _legitDeadlineFrame = _frame + ActionMarginFrames;
                break;

            case LegitStep.Verdict:
                if (_frame < _legitDeadlineFrame) break;
                // The Case-1 preservation assertion: a later, genuinely new
                // drive attempt must succeed NORMALLY — live Dribbling, and
                // HasDribbled must STILL be false (nothing was ever cradled;
                // this is the FIRST real dribble this possession).
                bool legitDriveSucceeded = _ball.State == BallState.Dribbling && !_ball.HasDribbled;
                if (!legitDriveSucceeded)
                {
                    Fail($"verdict (later-legit-drive): expected the later drive to succeed normally (state=Dribbling, HasDribbled=false); got state={_ball.State}, HasDribbled={_ball.HasDribbled}.");
                    Finish();
                    return;
                }
                GD.Print("[cradle-race] PASS verdict (later-legit-drive) — a genuinely later drive succeeded normally; the shoot-from-triple-threat rule is intact.");
                GD.Print("[cradle-race] RESULT: PASS (exit 0)");
                Finish(0);
                return;
        }
    }

    private void Fail(string message) => GD.PrintErr($"[cradle-race] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        if (code != 0)
            GD.Print($"[cradle-race] RESULT: FAIL (exit {code})");
        GetTree().Quit(code);
    }
}
