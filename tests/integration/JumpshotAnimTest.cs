using System;
using System.Collections.Generic;
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
//   godot --headless --path . res://tests/integration/JumpshotAnimTest.tscn -- --harness-scenario=jumpshot-airborne-active
//   godot --headless --path . res://tests/integration/JumpshotAnimTest.tscn -- --harness-scenario=control-jumpshot-grounded-startup
//   godot --headless --path . res://tests/integration/JumpshotAnimTest.tscn -- --harness-scenario=jumpshot-track-completeness
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "jumpshot-phases".
//
// ── Why the last three scenarios exist: the #316 measurement ────────────────
// #316 proposed RE-AUTHORING these clips in headless Blender, on three stated
// grounds. All three were measured against the shipped assets/locomotion.res
// before any work started (tools/measure_clip_completeness.gd and
// tools/measure_jumpshot_vertical.gd, both committed alongside this), and all
// three turned out to be false:
//
//   1. "Hand-keyed in GDScript, so it leaves track gaps by construction."
//      It is not hand-keyed. tools/rebuild_jumpshot_clips.gd SLICES a real
//      mocap clip (`Goalkeeper Catch Stationary`) and resamples it at one key
//      per gameplay tick, and its _assert_complete() already fails the BUILD if
//      a slice loses coverage.
//   2. "80% bone coverage (52/65) leaves 13 bones at rest."
//      True as a count, inert as a defect: all 13 are Mixamo LEAF terminators
//      (`*4` fingertips, HeadTop_End, `*Toe_End`). The a45bd1d T-pose trap
//      needs an untracked bone whose LOCAL REST differs from its correct pose —
//      an arm bone's rest is horizontal, so omitting it snaps the arm sideways.
//      A fingertip's rest RELATIVE TO ITS ANIMATED PARENT is already correct,
//      and being a leaf it can drag no subtree with it. That is what
//      "jumpshot-track-completeness" pins, and it is why that scenario asserts
//      "every NON-LEAF bone is animated" rather than a coverage percentage: the
//      percentage is the wrong metric and would have to be lowered to 80% to
//      pass, which would encode the wrong guarantee.
//   3. "The startup ends with a jump — grounding was never proven."
//      The grounding was already TRUE, just unasserted. Measured lowest-toe
//      height across the shipped family: +0.0002 at Startup entry, +0.399 at
//      Startup exit, +0.406 peak during Active, +0.005 at Recovery end. Hips
//      likewise run -0.230 (gather) -> +0.431 (release) -> -0.291 (landing
//      absorb), which MEETS OR EXCEEDS every magnitude #316's own motion spec
//      asked a re-author to produce (-0.12 / +0.30 / -0.10).
//
// ── The two measurement spaces are NOT comparable — do not "reconcile" them ──
// tools/measure_jumpshot_vertical.gd reports +0.406 of Active toe rise; this
// harness reports +0.2240 for the same clips, and BOTH are correct. They measure
// different things:
//   * the tool does manual FK on the RAW assets/Y Bot.fbx rig, in resource space,
//     against the skeleton's REST pose;
//   * this harness reads the LIVE Skeleton3D on scenes/Player.tscn, against the
//     pre-move idle stance — and that skeleton has been rewritten by
//     PlayerRigScaler (per-bone SetBonePoseScale on the leg/spine chain) and
//     BlendRestAnchor before a single tick runs.
// Different baseline AND different scale. Only the harness number is the one any
// threshold here may be set from; the tool's numbers are for reasoning about the
// CLIP, not about the rendered player.
//
// So the re-author was dropped (it would have spent a merge-train slot on a
// BINARY resource to make the motion no better, and arguably worse — programmatic
// Blender keyframing interpolates between authored poses, which is precisely the
// "sliding between two poses" #316 wanted to avoid, whereas the shipped clip is
// resampled motion capture). What was genuinely missing was CI proof, which is
// what these three scenarios are. The measurement is recorded here rather than
// only in the issue so the next reader does not re-open a settled question.
//
// ── Mutation evidence for the three #316 scenarios ──────────────────────────
// Every threshold below is set from a measured working value AND a measured
// broken one. Run locally against Godot 4.7.1 on 2026-08-03; "-" = unaffected.
//
// Shipped tree reads: startup min=-0.1876 max=+0.1541 / active min=+0.2174
// max=+0.2240 / completeness 52 of 52 non-leaf on all four clips.
//
//   mutation                                        airborne  grounded  complete
//   (none — shipped tree)                             PASS      PASS      PASS
//   A: JumpshotActive  -> "locomotion/idle"           FAIL*     FAIL†     -
//   B: JumpshotStartup -> "locomotion/jumpshotactive" PASS‡     FAIL§     -
//   D: JumpshotStartup -> "locomotion/idle"           PASS‡     FAIL¶     -
//   C: add "pivot" to the completeness clip list      -         -         FAIL#
//
//   * min=-0.0002 / max=+0.1812. Under the ORIGINAL Math.Max reduction this
//     mutation PASSED at +0.1812 — see the note below; that is why the gate
//     reduces with min.
//   † correctly fails on BROKEN PREMISE rather than passing: with Active grounded
//     the toe instrument is no longer demonstrably live, so a grounded Startup
//     reading proves nothing.
//   ‡ correctly UNAFFECTED — B and D break only the wind-up, and the Active clip
//     is still genuinely airborne. The two scenarios detect different defects,
//     which is what makes keeping both worthwhile.
//   § the GROUNDED half: startup min=+0.2080 > ceiling 0.08 — the shot began in
//     the air.
//   ¶ the TAKEOFF half: startup max=+0.0112 < floor 0.10 — the shot never left
//     the floor during its wind-up. B and D together are what prove the two-sided
//     gate; either half alone is satisfied by one of these two defects.
//   # 'pivot' animates 29/52 non-leaf bones, leaving 23 (including
//     mixamorig_LeftToeBase) pinned to rest.
//
//   Not covered by this table: no mutation makes "complete" go red while the two
//   geometric gates stay green from a CLIP-CONTENT change, because C mutates the
//   assertion's input list rather than locomotion.res (a binary resource). If a
//   future change strips tracks from a shipped clip, re-run this table.
//
// ── Why "airborne" reduces with MIN across Active (README trap 17) ──────────
// Written first with Math.Max and MUTATION-PROVEN INADEQUATE, so it is recorded
// here rather than silently fixed. Mutation: point the JumpshotActive state's
// AnimationNodeAnimation at "locomotion/idle" — a grounded clip, i.e. a jump shot
// released with both feet planted, exactly the defect this gate exists to catch.
// Under Math.Max it read +0.1812 and PASSED (floor 0.15). Under Math.Min the same
// run reads min=-0.0002 / max=+0.1812 and FAILS — the split between the two bounds
// IS the diagnosis, which is why both are printed.
//
// The cause is the 4-tick Active window plus the ~1-tick lag between what
// GetCurrentNode() reports and the pose the Skeleton3D has actually been given.
// The first sample labelled "JumpshotActive" therefore still carries STARTUP's
// final pose — which is fully airborne by design — so a max-reduction latches
// Startup's tail and never looks at the Active clip at all.
//
// Trap 17's rule states the general form: the CLAIM here is "the shooter is
// airborne THROUGHOUT the release", a statement about EVERY Active tick, and a
// statement about every tick can only be reduced with min. Max asks whether ANY
// tick complied, which the borrowed Startup pose satisfies for free. Note the
// gate is green under both reductions on a correct clip (min == max to within
// 0.01), which is precisely why this was invisible until mutated.
//
// ── Why "grounded startup" is TWO-SIDED rather than a single entry sample ───
// LayupAnimTest's control-layup-grounded-startup gates
// maxHipRiseDuringStartup <= 0.08 across the WHOLE Startup phase. Copying that
// shape here would be wrong and would FAIL: jumpshot's Startup is 18 ticks and
// deliberately ENDS AIRBORNE — "heels leave the floor at the end" is the motion
// spec.
//
// The obvious repair — sample only the FIRST Startup tick — was written, and
// MUTATION-PROVED VACUOUS. Mutation: point JumpshotStartup's
// AnimationNodeAnimation at "locomotion/jumpshotactive", so the shot begins
// fully airborne. The entry sample still read +0.0003 and PASSED, because of the
// one-tick reporting lag documented on ObserveGeometry: the first tick named
// "JumpshotStartup" still carries the pre-move idle pose — which is the very
// pose _toeBaselineY was latched from. The gate was asserting that the baseline
// equals itself and could not fail for ANY clip.
//
// What is asserted instead is that Startup CONTAINS THE TAKEOFF: its lowest
// reading is grounded (the gather) and its highest is airborne (the extension),
// with the first tick dropped. Each half alone is satisfiable by a defect — only
// the grounded half by a shot that never jumps, only the airborne half by one
// that begins mid-air — so the pair is the claim. It is also the stronger
// legibility statement: the rise is required to happen inside the 18 ticks an
// opponent reads the feint window off, not somewhere off-screen.
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

    // Thresholds are MEASURED, not assumed. Observed on the shipped clips through
    // THIS harness (see the note below on why that qualifier matters):
    //   toe rise during Active      = +0.2240  vs floor   0.15  -> ~1.5x margin
    //   toe rise at Startup entry   = +0.0003  vs ceiling 0.08  -> ~270x margin
    // Both numbers are the same two ContestAnimTest/LayupAnimTest use, deliberately:
    // a shared scale keeps "airborne" meaning the same thing across the per-move
    // harnesses. The airborne margin is the tighter of the two, but the broken
    // value it must separate from is a grounded clip reading ~0.01-0.05, so 0.15
    // still sits cleanly between them.
    private const float AirborneMinToeRise = 0.15f;
    private const float GroundedMaxToeRise = 0.08f;

    // The takeoff half of control-jumpshot-grounded-startup gets its OWN, LOWER
    // floor, and reusing AirborneMinToeRise here would be a latent flake rather
    // than a stricter test. Startup's highest observable reading is +0.1541 —
    // only 0.0041 above 0.15 — because the one-tick reporting lag means Startup's
    // FINAL and highest pose (~0.22) is never sampled under the "JumpshotStartup"
    // label at all; it lands on Active's first tick. A threshold 2.7% below the
    // measured value would redden on any minor retune of the wind-up.
    //
    // 0.10 keeps a 1.5x margin against the measured +0.1541 while still sitting
    // clearly ABOVE GroundedMaxToeRise (0.08), which is what keeps the two-sided
    // claim non-degenerate: a clip cannot satisfy both halves by standing still.
    private const float StartupTakeoffMinToeRise = 0.10f;

    private static readonly string[] KnownScenarios =
    {
        "jumpshot-phases", "fadeaway-active", "no-fadeaway-when-squared-up", "no-placeholder-leak",
        "jumpshot-airborne-active", "control-jumpshot-grounded-startup", "jumpshot-track-completeness",
    };

    // Pure resource inspection — needs no live tree, no tipoff, no move.
    private static readonly string[] StaticScenarios = { "jumpshot-track-completeness" };

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

    // Geometry, latched at EVENT TIME. Never recomputed at verdict time: by then
    // the move is long over and the rig has settled back onto Locomotion, so a
    // verdict-time read would measure a standing player and report a confident,
    // meaningless "grounded" for every scenario.
    private Skeleton3D _shooterSkel;
    private bool _haveToeBaseline;
    private float _toeBaselineY;             // lowest toe BEFORE the move — the standing stance
    // Startup is asserted TWO-SIDED (grounded early, airborne late) rather than at
    // a single entry sample — see the header note. Both bounds are kept for the
    // same reason Active keeps both: the pair is the diagnosis.
    private float _minToeRiseDuringStartup = float.PositiveInfinity;
    private float _maxToeRiseDuringStartup = float.NegativeInfinity;
    private int _startupTicksObserved;
    // BOTH bounds across Active are kept, and the gate reduces with the MIN — see
    // the header note "why airborne reduces with min". Both are printed so a clip
    // that is airborne for only part of its release is legible in the log rather
    // than failing anonymously (README trap 17's preferred shape, copied from
    // LocomotionClipTest's #298 stride gate).
    //
    // Seeds are the infinities, not 0: a toe BELOW the baseline is a legitimate
    // reading (the gather crouch settles the feet), and seeding at 0 would clamp
    // one bound to a value the rig never produced.
    private float _minToeRiseDuringActive = float.PositiveInfinity;
    private float _maxToeRiseDuringActive = float.NegativeInfinity;
    private int _activeTicksObserved;

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

        if (StaticScenarios.Contains(_scenario))
        {
            VerdictTrackCompleteness();
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
                // The standing stance, latched on the LAST tick before the shot
                // begins — while the shooter is still on Locomotion with both
                // feet down. Every geometric reading below is a DIFFERENCE from
                // this, so the gates measure "how far from standing" rather than
                // raw Skeleton3D-space coordinates, which carry the rig's own
                // offsets and would make the thresholds rig-specific magic.
                LatchToeBaseline();
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

        ObserveGeometry(node);
    }

    // Reads the LIVE Skeleton3D mid-move. This is deliberately not a re-read of
    // what rebuild_jumpshot_clips.gd already checks on the resource: a clip whose
    // track paths fail to bind on scenes/Player.tscn is a SILENT no-op (#281's
    // "Armature/" prefix), and every resource-side check still passes on it. The
    // live skeleton is the only place that failure is visible.
    private void ObserveGeometry(string node)
    {
        if (!_haveToeBaseline) return;

        float toe = MeasureLowestToe();
        // NaN propagates rather than defaulting to 0 — a Skeleton3D whose toe
        // bones could not be found must leave the latches unset (and the verdicts
        // failing) instead of reporting a flawless 0.0000 for every tick.
        if (float.IsNaN(toe)) return;
        float rise = toe - _toeBaselineY;

        // The FIRST tick on which GetCurrentNode() names a phase still carries the
        // PREVIOUS phase's pose — the AnimationTree reports the state it travelled
        // to before the mixer has written that state's clip to the Skeleton3D.
        // Measured, not assumed: with JumpshotStartup pointed at an airborne clip
        // the entry sample still read +0.0003 (the idle stance), and with
        // JumpshotActive pointed at a grounded clip the first Active sample still
        // read Startup's airborne +0.18. Both phases therefore drop their first
        // observed tick, or the gate measures the phase BEFORE the one it names.
        //
        // Startup has 18 ticks and Active 4, so dropping one leaves 17 and 3 —
        // ample in both cases. If a future retune shortens Active to 1 tick this
        // stops being viable and the scenario should fail loudly rather than
        // silently measure nothing, which is what _activeTicksObserved > 0 in the
        // verdict is for.
        if (node == "JumpshotStartup")
        {
            _startupTicksObserved++;
            if (_startupTicksObserved > 1)
            {
                _minToeRiseDuringStartup = Math.Min(_minToeRiseDuringStartup, rise);
                _maxToeRiseDuringStartup = Math.Max(_maxToeRiseDuringStartup, rise);
            }
        }

        if (node == "JumpshotActive")
        {
            _activeTicksObserved++;
            if (_activeTicksObserved > 1)
            {
                _minToeRiseDuringActive = Math.Min(_minToeRiseDuringActive, rise);
                _maxToeRiseDuringActive = Math.Max(_maxToeRiseDuringActive, rise);
            }
        }
    }

    private void LatchToeBaseline()
    {
        float toe = MeasureLowestToe();
        if (float.IsNaN(toe)) return; // leaves _haveToeBaseline false -> verdicts fail loudly
        _toeBaselineY = toe;
        _haveToeBaseline = true;
    }

    // Lowest of the two toes, in the Skeleton3D's own space. Absolute values are
    // meaningless here (they carry the rig's offsets); only differences from
    // _toeBaselineY are ever asserted. Mirrors ContestAnimTest.MeasureLowestToe.
    private float MeasureLowestToe()
    {
        _shooterSkel ??= FindSkeleton(_shooter);
        if (_shooterSkel == null) return float.NaN;

        float lowest = float.PositiveInfinity;
        foreach (string toe in new[] { "mixamorig_LeftToeBase", "mixamorig_RightToeBase" })
        {
            int idx = _shooterSkel.FindBone(toe);
            if (idx < 0) continue;
            lowest = Math.Min(lowest, _shooterSkel.GetBoneGlobalPose(idx).Origin.Y);
        }
        return float.IsPositiveInfinity(lowest) ? float.NaN : lowest;
    }

    private static Skeleton3D FindSkeleton(Node root)
    {
        if (root is Skeleton3D s) return s;
        foreach (Node child in root.GetChildren())
        {
            Skeleton3D found = FindSkeleton(child);
            if (found != null) return found;
        }
        return null;
    }

    private void RenderVerdict()
    {
        switch (_scenario)
        {
            case "jumpshot-phases":             VerdictJumpshotPhases(); break;
            case "fadeaway-active":              VerdictFadeawayActive(); break;
            case "no-fadeaway-when-squared-up":  VerdictNoFadeawayWhenSquaredUp(); break;
            case "no-placeholder-leak":          VerdictNoPlaceholderLeak(); break;
            case "jumpshot-airborne-active":     VerdictAirborneActive(); break;
            case "control-jumpshot-grounded-startup": VerdictGroundedStartup(); break;
        }
    }

    // ── Scenario: jumpshot-airborne-active (positive) ───────────────────────
    // A jump shot whose feet never leave the floor is not a jump shot — it is a
    // set shot, and it reads as one. This is the gate that would catch a clip
    // re-slice landing on the wrong part of the source arc, or a re-authored
    // clip that lost its root translation.
    private void VerdictAirborneActive()
    {
        GD.Print($"[jumpshot-anim]   toe rise during Active: min={_minToeRiseDuringActive:F4} " +
                 $"max={_maxToeRiseDuringActive:F4} over {_activeTicksObserved} tick(s) " +
                 $"(floor {AirborneMinToeRise:F2} applies to MIN), baseline latched = {_haveToeBaseline}");

        // The min is the gate; the max is printed for legibility. See the header
        // for the mutation that proves max alone is not enough.
        bool pass = _haveToeBaseline
                    && _sawJumpshotActive
                    && _activeTicksObserved > 0
                    && _minToeRiseDuringActive >= AirborneMinToeRise;
        if (pass)
            GD.Print($"[jumpshot-anim] PASS jumpshot-airborne-active — across ALL {_activeTicksObserved} " +
                     $"\"JumpshotActive\" tick(s) the lowest toe stayed at least {_minToeRiseDuringActive:F4} " +
                     $"above the stance the shot began in (floor {AirborneMinToeRise:F2}, peak " +
                     $"{_maxToeRiseDuringActive:F4}). The shooter is off the floor for the whole release.");
        else
            Fail($"jumpshot-airborne-active: toe rise during Active was min={_minToeRiseDuringActive:F4} " +
                 $"max={_maxToeRiseDuringActive:F4} over {_activeTicksObserved} tick(s), need MIN >= " +
                 $"{AirborneMinToeRise:F2} (sawActive={_sawJumpshotActive}, baseline={_haveToeBaseline}). " +
                 "If min is low while max is high, the shooter touched down DURING the release — the Active " +
                 "clip is grounded and only its first tick inherited Startup's airborne pose. If both are " +
                 "low, the clip lost its root translation or its tracks are not binding on Player.tscn (#281).");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: control-jumpshot-grounded-startup (control) ───────────────
    // Two jobs, and the second is the one that makes it a control rather than a
    // second positive:
    //
    //   (a) it pins the CONTRAST — grounded at Startup entry, airborne by Active
    //       — which is what makes the motion a RISE. "Airborne during Active"
    //       alone is equally satisfied by a clip that begins airborne and stays
    //       there, i.e. a player who never jumps because he was already floating.
    //   (b) it asserts its own premise. A dead toe instrument — a Skeleton3D the
    //       harness failed to find, bones renamed by a rig change, or a clip
    //       bound to nothing — reports a constant value, which satisfies
    //       "grounded at entry" PERFECTLY. So this scenario requires the SAME
    //       run's Active reading to have gone airborne first. A frozen instrument
    //       cannot produce both readings.
    private void VerdictGroundedStartup()
    {
        GD.Print($"[jumpshot-anim]   toe rise across Startup: min={_minToeRiseDuringStartup:F4} " +
                 $"(ceiling {GroundedMaxToeRise:F2}) max={_maxToeRiseDuringStartup:F4} " +
                 $"(floor {StartupTakeoffMinToeRise:F2}) over {_startupTicksObserved} tick(s); " +
                 $"premise min during Active = {_minToeRiseDuringActive:F4}");

        // The premise uses the SAME min-reduction the positive scenario gates on,
        // not max — a premise weaker than the claim it underwrites would let this
        // control pass on exactly the runs the positive scenario rejects.
        bool premise = _haveToeBaseline
                       && _sawJumpshotStartup
                       && _startupTicksObserved > 1
                       && _minToeRiseDuringActive >= AirborneMinToeRise;

        // Two-sided: Startup must CONTAIN the takeoff. Grounded at its low point
        // (the gather) and airborne at its high point (the extension). Asserting
        // only the grounded half would be satisfied by a Startup that never leaves
        // the floor; asserting only the airborne half would be satisfied by one
        // that begins in the air. Together they say "the rise happens HERE",
        // inside the 18 ticks the feint window is read off.
        bool wasGrounded = _minToeRiseDuringStartup <= GroundedMaxToeRise;
        bool leftFloor = _maxToeRiseDuringStartup >= StartupTakeoffMinToeRise;
        bool pass = premise && wasGrounded && leftFloor;

        if (pass)
            GD.Print($"[jumpshot-anim] PASS control-jumpshot-grounded-startup — across \"JumpshotStartup\" the " +
                     $"lowest toe went from {_minToeRiseDuringStartup:F4} (grounded, ceiling " +
                     $"{GroundedMaxToeRise:F2}) up to {_maxToeRiseDuringStartup:F4} (airborne, floor " +
                     $"{AirborneMinToeRise:F2}), and the same run held {_minToeRiseDuringActive:F4} throughout " +
                     "Active. The takeoff happens INSIDE the wind-up: the shot rises from the floor rather " +
                     "than beginning airborne, and the toe instrument is demonstrably live.");
        else
            Fail($"control-jumpshot-grounded-startup: startup min={_minToeRiseDuringStartup:F4} " +
                 $"(need <= {GroundedMaxToeRise:F2}) max={_maxToeRiseDuringStartup:F4} " +
                 $"(need >= {StartupTakeoffMinToeRise:F2}) over {_startupTicksObserved} tick(s); premise " +
                 $"minActiveRise={_minToeRiseDuringActive:F4} (floor {AirborneMinToeRise:F2}), " +
                 $"sawStartup={_sawJumpshotStartup}, baseline={_haveToeBaseline}. A high MIN means the shot " +
                 "began airborne; a low MAX means it never left the floor during the wind-up. If the PREMISE " +
                 "broke, a grounded reading proves nothing — a frozen instrument is grounded too — so this " +
                 "fails rather than passes.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: jumpshot-track-completeness (static) ──────────────────────
    // Encodes the #316 measurement (see the header). The assertion is NOT a
    // coverage percentage — the shipped clips are 52/65 = 80% and that is
    // CORRECT, because the 13 untracked bones are all leaf terminators. A
    // percentage floor would have to be set at 80% to pass, which asserts nothing
    // about the property that actually matters.
    //
    // What matters is the a45bd1d trap's real precondition: an untracked bone
    // pins its whole SUBTREE to rest. A leaf has no subtree, and its own rest is
    // measured relative to a parent the clip DOES animate, so it follows correctly.
    // Hence: every NON-LEAF bone must be animated. That is rig-derived rather than
    // a magic number, so a rig change moves the requirement instead of silently
    // invalidating it.
    private void VerdictTrackCompleteness()
    {
        var lib = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        if (lib == null)
        {
            Fail("jumpshot-track-completeness: could not load res://assets/locomotion.res.");
            Finish(1);
            return;
        }

        // The RAW rig, never scenes/Player.tscn: BlendRestAnchor rewrites bone
        // rests at _Ready. Only the HIERARCHY is read here so that would not
        // actually bite, but the raw-FBX rule is cheap to honour and the next
        // person to extend this may well reach for a rest transform.
        var rigScene = GD.Load<PackedScene>("res://assets/Y Bot.fbx");
        Skeleton3D rig = rigScene == null ? null : FindSkeleton(rigScene.Instantiate());
        if (rig == null)
        {
            Fail("jumpshot-track-completeness: could not find a Skeleton3D in res://assets/Y Bot.fbx.");
            Finish(1);
            return;
        }

        var nonLeaf = new List<string>();
        for (int i = 0; i < rig.GetBoneCount(); i++)
            if (rig.GetBoneChildren(i).Length > 0)
                nonLeaf.Add(rig.GetBoneName(i));

        // A rig that reported no non-leaf bones would make this scenario vacuous
        // — every clip would trivially satisfy an empty requirement.
        if (nonLeaf.Count == 0)
        {
            Fail($"jumpshot-track-completeness: the rig reported {rig.GetBoneCount()} bones but NONE with " +
                 "children, so 'every non-leaf bone is animated' would be vacuously true. The hierarchy " +
                 "lookup is broken.");
            Finish(1);
            return;
        }

        string[] clips = { "jumpshotstartup", "jumpshotactive", "jumpshotrecovery", "fadeawayactive" };
        bool pass = true;

        foreach (string clipName in clips)
        {
            if (!lib.HasAnimation(clipName))
            {
                Fail($"jumpshot-track-completeness: '{clipName}' is missing from locomotion.res.");
                pass = false;
                continue;
            }

            Animation anim = lib.GetAnimation(clipName);
            var tracked = new HashSet<string>();
            for (int t = 0; t < anim.GetTrackCount(); t++)
            {
                NodePath path = anim.TrackGetPath(t);
                // Skipping subname-less paths here is safe in a way README trap 15
                // warns it usually is NOT: that trap is about a gate that reports
                // "unresolved=[]" by exempting the very tracks that failed to bind.
                // This gate runs the other direction — it iterates the RIG's bones
                // and demands each one appear — so a path contributing no bone name
                // simply fails to satisfy anything and can mask nothing.
                if (path.GetSubNameCount() == 0) continue;
                tracked.Add(path.GetSubName(0));
            }

            var missing = nonLeaf.Where(b => !tracked.Contains(b)).ToList();
            GD.Print($"[jumpshot-anim]   '{clipName}': {tracked.Count}/{rig.GetBoneCount()} bones animated, " +
                     $"{nonLeaf.Count - missing.Count}/{nonLeaf.Count} non-leaf bones animated");

            if (missing.Count > 0)
            {
                Fail($"jumpshot-track-completeness: '{clipName}' leaves {missing.Count} NON-LEAF bone(s) " +
                     $"untracked: [{string.Join(", ", missing)}]. Each pins its whole subtree to the rig's " +
                     "rest pose (a Mixamo T-pose) for the clip's duration — the a45bd1d trap. Re-run " +
                     "tools/rebuild_jumpshot_clips.gd, whose _assert_complete() should have caught this at " +
                     "build time.");
                pass = false;
            }
        }

        if (pass)
            GD.Print("[jumpshot-anim] PASS jumpshot-track-completeness — all four jumpshot clips animate every " +
                     $"one of the rig's {nonLeaf.Count} non-leaf bones, so no bone can drag a subtree to the " +
                     "T-pose rest when its state is entered at full weight.");
        Finish(pass ? 0 : 1);
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
