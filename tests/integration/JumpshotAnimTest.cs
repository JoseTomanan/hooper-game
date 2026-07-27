using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #279 (ADR-0016): proves the four
// JUMPSHOT ANIMATION STATES the #279 clip work wired into scenes/Player.tscn
// (JumpshotStartup / JumpshotActive / JumpshotRecovery / FadeawayActive) are
// actually ENTERED end-to-end when a real JumpShot runs through the real
// AnimationTree.
//
// Two lower-level proofs already exist and this harness deliberately does
// NOT re-litigate either of them:
//   - LocomotionClipTest pins the four clips' own PROPERTIES (length, track
//     coverage, loop mode, pose-off-rest, fadeaway distinctness) by reading
//     the .res resource directly — it never runs a state machine.
//   - FadeawayTriggerTest (#243) pins the pure CLASSIFICATION decision
//     (DisplayFadeaway()/MoveAnimResolver.Resolve) against a code-built tree
//     with NO AnimationTree at all — sufficient for that issue's own subject
//     (the boolean gate), but blind to whether the real .tscn state machine
//     actually reaches FadeawayActive.
// What neither can catch is a broken WIRE: a renamed state key, a missing
// transition edge, or a #277 ClippedMovePrefixes table entry that silently
// falls back to the shared placeholder Startup/Active/Recovery clip (see
// "no-placeholder-leak" below). Travel() to a missing/misnamed state only
// LOGS, it never throws (#257) — so only reading the live
// AnimationNodeStateMachinePlayback proves the wiring is real.
//
//   godot --headless --path . res://tests/integration/JumpshotAnimTest.tscn -- --harness-scenario=jumpshot-phases
//   godot --headless --path . res://tests/integration/JumpshotAnimTest.tscn -- --harness-scenario=fadeaway-active
//   godot --headless --path . res://tests/integration/JumpshotAnimTest.tscn -- --harness-scenario=no-fadeaway-when-squared-up
//   godot --headless --path . res://tests/integration/JumpshotAnimTest.tscn -- --harness-scenario=no-placeholder-leak
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "jumpshot-phases".
//
// ── Why ActiveAnimNodeForHarness, not the resolver's own decision (#257) ────
// Same discipline as DribbleLoopTest/PivotAnimTest/MoveKindAnimTest: reading
// AnimationNodeStateMachinePlayback.GetCurrentNode() (via the live
// AnimationTree) asserts what the state machine ACTUALLY did, never what
// MoveAnimResolver/ResolveStateName merely DECIDED — calling those directly
// would keep passing even on a Player.tscn with no Jumpshot*/FadeawayActive
// state at all, since the resolver has no notion the .tscn even exists.
//
// ── Why real scenes/Player.tscn, not a code-built tree ──────────────────────
// A live AnimationTree only exists on the REAL scenes/Player.tscn (bare
// `new PlayerController()`, which FadeawayTriggerTest deliberately uses for
// ITS narrower subject, has no mesh/tree — Travel() would no-op silently).
// This harness is the missing end-to-end half FadeawayTriggerTest cannot be.
//
// ── Why BeginJumpShotForHarness, not real stick+button input ────────────────
// JumpShot's Begin() carries no possession precondition (unlike the
// dribble-move family gated by the dead-Held rule — see
// PlayerController.BeginCommittedMove's doc), so TripleThreatHarnessSeam's
// BeginJumpShotForHarness — which already routes through the SAME
// BeginCommittedMove choke point production input reaches (SampleMoveInput's
// shoot branch) — is the correct seam here too: it bypasses only the
// input/RPC layer headless cannot drive, exactly like BlockTurnoverTest/
// ContestScatterTest/FadeawayTriggerTest already do for the same move.
//
// ── Why SetHeadingForHarness for the fadeaway scenarios ─────────────────────
// Same reasoning as FadeawayTriggerTest: forcing Heading directly isolates
// "does the classification correctly key off Heading-at-release" from "can
// HeadingMath.RotateToward turn a player to a given yaw" (already proven
// elsewhere by PivotPlantTest/DriveGatherTest). A Heading forced before Begin
// cannot drift mid-shot HERE because (a) no stick input reaches this harness, so
// Move() has nothing to turn toward, and (b) JumpShot's own branch in
// TickCommittedMoveBehavior never writes Heading.
//
// Stated deliberately narrowly: "a committed move freezes Heading" is NOT a
// general rule of this codebase. Spin's Active phase overwrites Heading every
// tick via SpinHeadingMath.ArcHeading — an explicit ADR-0010 SANCTIONED
// EXCEPTION in PlayerController — so any future harness that reuses this setup
// for a different move must re-check that move's own branch rather than
// inheriting this guarantee.
//
// ── What this harness CANNOT prove: individual transition EDGES ─────────────
// Measured, not assumed (#279). Deleting the FadeawayActive -> JumpshotRecovery
// edge (tr_js_fdw2re) from Player.tscn's `transitions` array does NOT redden any
// scenario here, and is not observable through ActiveAnimNodeForHarness at all:
// AnimationNodeStateMachinePlayback.Travel() is a PATHFINDER over the transition
// graph, not a single-hop switch, so with a direct edge gone it still arrives at
// the target — and it does so without ever reporting an intermediate state on the
// tick boundaries this harness samples. Two stronger assertions were written and
// BOTH stayed green under that mutation: "JumpshotRecovery was reached after
// FadeawayActive", and "…with no other state observed in between". Neither was
// kept, because an assertion that cannot be made to fail is exactly the vacuous
// kind this repo forbids.
//
// So: what IS proven here is that each state is REACHABLE and that the resolver
// asks for the right one. What is NOT proven is the shape of the graph that gets
// it there. Anyone extending this pattern to the remaining per-move families
// (#280-#283) should not spend effort on edge-level assertions via
// GetCurrentNode() — they will pass regardless. Proving an edge needs a different
// instrument (inspecting the AnimationNodeStateMachine resource's transition list
// directly, the way LocomotionClipTest inspects clip properties).
//
// ── Why the controls carry the real weight here ─────────────────────────────
// "no-fadeaway-when-squared-up" and "no-placeholder-leak" both assert their
// OWN PREMISE first — that a real JumpShot genuinely ran and reached its
// Active phase — so neither can pass vacuously on a rig where nothing ever
// left Locomotion (which would trivially satisfy "never showed X" for any X).
public partial class JumpshotAnimTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int ArmFrames = 2;      // ticks for TryAssignTipoffHolder to run
    private const int ActFrames = 2;      // ticks after tipoff before Begin (lets position/heading settle)
    private const int ObserveFrames = 70; // > startup(18)+active(4)+recovery(20)=42, with margin for slack ticks

    // Non-degenerate XZ distance from RimCenter (matches ContestScatterTest/
    // BlockTurnoverTest/FadeawayTriggerTest's ShooterPosition convention), so
    // ShotFacing.AngleFromTarget's degenerate "standing on the rim" guard
    // never fires.
    private static readonly Vector3 ShooterSpot = new(0f, 0f, 5f);
    private static readonly Vector3 FarSpot = new(12f, 0f, 12f); // keeps the other player out of PickupRadius
    private static readonly Vector3 RimCenter = new(0f, 3.05f, 0f); // matches BallController.DefaultRimCenter

    private static readonly string[] KnownScenarios =
    {
        "jumpshot-phases", "fadeaway-active", "no-fadeaway-when-squared-up", "no-placeholder-leak"
    };

    private string _scenario = "jumpshot-phases";

    private BallController _ball;
    private PlayerController _shooter; // peer "1" — always the tipoff holder (ADR-0007: deterministic first-present-node assignment)
    private PlayerController _other;   // peer "2"

    private int _frame;
    private double _elapsed;
    private bool _finished;

    private enum Step { AwaitTipoff, Act, Observe }
    private Step _step = Step.AwaitTipoff;
    private int _stepDeadlineFrame;

    // Latched (event-time) observations. The three Jumpshot-phase latches can
    // only turn true in order — each one's own guard requires the previous
    // one already latched — so "saw all three" IS "saw them in order."
    private bool _sawJumpshotStartup;
    private bool _sawJumpshotActive;
    private bool _sawJumpshotRecovery;
    private bool _sawFadeawayActive;
    private bool _sawGenericPlaceholder; // the shared "Startup"/"Active"/"Recovery" leaked through

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "jumpshot-phases");
        GD.Print($"[jumpshot-anim] scenario={_scenario} booting headless…");

        if (!KnownScenarios.Contains(_scenario))
        {
            Fail($"unknown scenario '{_scenario}'.");
            Finish();
            return;
        }

        // Real Player.tscn instances (live AnimationTree), named "1"/"2" so the
        // OfflineMultiplayerPeer makes unique_id 1 both IsServer and
        // IsLocalPlayer (the full TickServerOwnPlayer -> ApplyAnimation chain
        // runs every tick), same as DribbleLoopTest/ReboundGrabTest.
        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        _shooter = scene.Instantiate<PlayerController>();
        _shooter.Name = "1";
        _other = scene.Instantiate<PlayerController>();
        _other.Name = "2";

        // Physics-callback lockstep so GetCurrentNode() reflects the same-tick
        // Travel() (the default Idle callback lags under --headless — see
        // MoveKindAnimTest's long note; harness-only observation fidelity).
        foreach (var p in new[] { _shooter, _other })
            p.GetNode<AnimationTree>("AnimationTree").CallbackModeProcess =
                AnimationMixer.AnimationCallbackModeProcess.Physics;

        var players = new Node3D { Name = "Players" };
        players.AddChild(_shooter);
        players.AddChild(_other);

        _ball = new BallController { Name = "Ball", Players = players };

        AddChild(players); // matches scenes/Main.tscn: Players before Ball
        AddChild(_ball);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        switch (_step)
        {
            case Step.AwaitTipoff:
                if (_frame < ArmFrames) break;
                if (_ball.StateMachine.HolderPeerId != 1)
                {
                    // Tipoff assignment is deterministic — the server awards it
                    // to the first present player node (ADR-0007) — but fail
                    // loudly rather than let a changed default silently hang
                    // this harness at the 15s timeout with no diagnosis.
                    Fail($"{_scenario}: tipoff did not assign holder 1 (got {_ball.StateMachine.HolderPeerId}).");
                    Finish();
                    return;
                }
                _shooter.GlobalPosition = ShooterSpot;
                _other.GlobalPosition = FarSpot;
                ApplyScenarioHeading();
                _step = Step.Act;
                _stepDeadlineFrame = _frame + ActFrames;
                break;

            case Step.Act:
                if (_frame < _stepDeadlineFrame) break;
                // The REAL production entry point for a shot (TripleThreatHarnessSeam's
                // BeginJumpShotForHarness calls the same BeginCommittedMove
                // choke point SampleMoveInput's shoot branch does).
                if (!_shooter.BeginJumpShotForHarness())
                {
                    Fail($"{_scenario}: BeginJumpShotForHarness returned false — shooter's machine was not Inactive at begin.");
                    Finish();
                    return;
                }
                _step = Step.Observe;
                _stepDeadlineFrame = _frame + ObserveFrames;
                break;

            case Step.Observe:
                Observe();
                if (_frame >= _stepDeadlineFrame) RenderVerdict();
                break;
        }

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame}, scenario={_scenario}, step={_step}, " +
                 $"lastAnimNode={_shooter?.ActiveAnimNodeForHarness}, sawStartup={_sawJumpshotStartup}, " +
                 $"sawActive={_sawJumpshotActive}, sawRecovery={_sawJumpshotRecovery}, " +
                 $"sawFadeawayActive={_sawFadeawayActive}.");
            Finish();
        }
    }

    // Heading forced BEFORE Begin, exactly like FadeawayTriggerTest — the shooter
    // never moves after this (no stick input reaches CheckAutoStartDribble in this
    // harness, and JumpShot's own TickCommittedMoveBehavior branch never writes
    // Heading), so Heading cannot drift mid-shot. See the header for why that is
    // scoped to JumpShot rather than to committed moves in general.
    private void ApplyScenarioHeading()
    {
        // Target yaw: direction from shooter to rim — the SAME Atan2(dx, dz)
        // convention ShotFacing.AngleFromTarget uses internally, kept explicit
        // here rather than imported so this harness proves the trigger against
        // an independently-derived yaw, not a value borrowed from the code
        // under test (same discipline as FadeawayTriggerTest).
        float dx = RimCenter.X - ShooterSpot.X;
        float dz = RimCenter.Z - ShooterSpot.Z;
        float squaredUpYaw = Mathf.Atan2(dx, dz);

        // Only "fadeaway-active" needs the mid-pivot (180° off) heading; the
        // other three scenarios are squared-up — "jumpshot-phases" and
        // "no-placeholder-leak" don't care about the fadeaway axis at all, and
        // "no-fadeaway-when-squared-up" specifically needs squared-up.
        float headingYaw = _scenario == "fadeaway-active"
            ? squaredUpYaw + Mathf.Pi
            : squaredUpYaw;
        _shooter.SetHeadingForHarness(headingYaw);
    }

    private void Observe()
    {
        string node = _shooter.ActiveAnimNodeForHarness;

        if (!_sawJumpshotStartup && node == "JumpshotStartup") _sawJumpshotStartup = true;
        if (_sawJumpshotStartup && !_sawJumpshotActive && node == "JumpshotActive") _sawJumpshotActive = true;
        if (_sawJumpshotActive && !_sawJumpshotRecovery && node == "JumpshotRecovery") _sawJumpshotRecovery = true;
        // Chained behind Startup for the same reason the three above are: it makes
        // the verdict's "reached JumpshotStartup THEN showed FadeawayActive"
        // literally what was observed rather than an inference from JumpShot's
        // phase order. It also guards a real edge — Locomotion -> FadeawayActive
        // exists in the .tscn (fdw01), so a spurious entry that skipped the
        // wind-up entirely would otherwise still satisfy this latch.
        if (_sawJumpshotStartup && node == "FadeawayActive") _sawFadeawayActive = true;
        if (node == "Startup" || node == "Active" || node == "Recovery") _sawGenericPlaceholder = true;
    }

    private void RenderVerdict()
    {
        switch (_scenario)
        {
            case "jumpshot-phases":             VerdictJumpshotPhases(); break;
            case "fadeaway-active":              VerdictFadeawayActive(); break;
            case "no-fadeaway-when-squared-up":  VerdictNoFadeawayWhenSquaredUp(); break;
            case "no-placeholder-leak":          VerdictNoPlaceholderLeak(); break;
        }
    }

    // ── Scenario: jumpshot-phases (positive) ────────────────────────────────
    private void VerdictJumpshotPhases()
    {
        bool pass = _sawJumpshotStartup && _sawJumpshotActive && _sawJumpshotRecovery;
        if (pass)
            GD.Print("[jumpshot-anim] PASS jumpshot-phases — the tree was observed on \"JumpshotStartup\", then " +
                     "\"JumpshotActive\", then \"JumpshotRecovery\", in that order (the .tscn state machine and " +
                     "its transitions are live).");
        else
            Fail($"jumpshot-phases: expected JumpshotStartup -> JumpshotActive -> JumpshotRecovery, in order; got " +
                 $"sawStartup={_sawJumpshotStartup}, sawActive={_sawJumpshotActive}, sawRecovery={_sawJumpshotRecovery}, " +
                 $"lastAnimNode={_shooter.ActiveAnimNodeForHarness}.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: fadeaway-active (positive) ────────────────────────────────
    private void VerdictFadeawayActive()
    {
        bool pass = _sawJumpshotStartup && _sawFadeawayActive && !_sawJumpshotActive;
        if (pass)
            GD.Print("[jumpshot-anim] PASS fadeaway-active — a mid-pivot JumpShot reached \"JumpshotStartup\" then " +
                     "showed \"FadeawayActive\" during its Active phase, and never showed the squared-up " +
                     "\"JumpshotActive\" clip.");
        else
            Fail($"fadeaway-active: expected JumpshotStartup then FadeawayActive (never JumpshotActive); got " +
                 $"sawStartup={_sawJumpshotStartup}, sawFadeawayActive={_sawFadeawayActive}, " +
                 $"sawJumpshotActive={_sawJumpshotActive}, lastAnimNode={_shooter.ActiveAnimNodeForHarness}.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: no-fadeaway-when-squared-up (control) ─────────────────────
    private void VerdictNoFadeawayWhenSquaredUp()
    {
        // Premise check: this control only means anything if a squared-up
        // JumpShot genuinely reached JumpshotActive in THIS run — a rig where
        // nothing happened would trivially satisfy "never showed FadeawayActive."
        bool pass = _sawJumpshotStartup && _sawJumpshotActive && !_sawFadeawayActive;
        if (pass)
            GD.Print("[jumpshot-anim] PASS no-fadeaway-when-squared-up — a squared-up JumpShot reached " +
                     "\"JumpshotActive\" and never showed \"FadeawayActive\".");
        else
            Fail($"no-fadeaway-when-squared-up: expected JumpshotStartup then JumpshotActive AND never " +
                 $"FadeawayActive; got sawStartup={_sawJumpshotStartup}, sawJumpshotActive={_sawJumpshotActive}, " +
                 $"sawFadeawayActive={_sawFadeawayActive}, lastAnimNode={_shooter.ActiveAnimNodeForHarness}. " +
                 "If the premise broke, 'never FadeawayActive' proves nothing, so this fails rather than passes.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: no-placeholder-leak (control) ─────────────────────────────
    private void VerdictNoPlaceholderLeak()
    {
        // Premise check: the jumpshot must genuinely have run its full arc
        // (reached all three of its OWN states) before "no generic placeholder
        // observed" means anything — otherwise a rig stuck on Locomotion the
        // whole time would trivially satisfy "never saw the placeholder."
        bool ranGenuinely = _sawJumpshotStartup && _sawJumpshotActive && _sawJumpshotRecovery;
        bool pass = ranGenuinely && !_sawGenericPlaceholder;
        if (pass)
            GD.Print("[jumpshot-anim] PASS no-placeholder-leak — the jumpshot ran its full Startup/Active/" +
                     "Recovery arc on its OWN per-move states and never showed the shared generic " +
                     "\"Startup\"/\"Active\"/\"Recovery\" placeholder #279 moved it off of.");
        else
            Fail($"no-placeholder-leak: expected the jumpshot to run genuinely (sawStartup={_sawJumpshotStartup}, " +
                 $"sawActive={_sawJumpshotActive}, sawRecovery={_sawJumpshotRecovery}) AND never observe the " +
                 $"generic placeholder; sawGenericPlaceholder={_sawGenericPlaceholder}, " +
                 $"lastAnimNode={_shooter.ActiveAnimNodeForHarness}. If the premise broke, 'no leak observed' " +
                 "proves nothing, so this fails rather than passes.");
        Finish(pass ? 0 : 1);
    }

    private void Fail(string message) => GD.PrintErr($"[jumpshot-anim] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[jumpshot-anim] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
