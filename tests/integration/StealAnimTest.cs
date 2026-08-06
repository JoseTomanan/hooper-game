using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #282 (ADR-0016): proves the SIX
// hand-side steal states scenes/Player.tscn now carries —
// Steal{Startup,Active,Recovery}{Left,Right} — are actually ENTERED end-to-end
// through the real AnimationTree, that the suffix is the DEFENDER's own
// ReachSide (never the ball-holder's TargetHand, never a per-tick HandSide
// read), that the deleted unsuffixed states are genuinely gone, that each
// clip's duration matches StealMove.DefaultFrameData's tick windows (with the
// ADR-0018 telegraph requirement pinned separately for Startup), and that none
// of the six states still points at the #296 placeholder.
//
// This is #281's shipped BehindTheBackAnimTest pattern, copied structurally.
// The one structural difference: behind-the-back poses the BALL-HANDLER
// (one role, one node), while a steal poses the DEFENDER of a two-player duel
// (holder + defender, two roles) — so this harness always instantiates BOTH
// and tracks _holderId/_defenderId separately, driving BeginStealFromAimForHarness
// on the DEFENDER while the HOLDER exists only to give ResolveStealTargetHand
// a real body to read a Heading off (needed for the face-to-face scenario).
//
//   godot --headless --path . res://tests/integration/StealAnimTest.tscn -- --harness-scenario=steal-left-reach
//   godot --headless --path . res://tests/integration/StealAnimTest.tscn -- --harness-scenario=steal-right-reach
//   godot --headless --path . res://tests/integration/StealAnimTest.tscn -- --harness-scenario=steal-constant-polarity
//   godot --headless --path . res://tests/integration/StealAnimTest.tscn -- --harness-scenario=steal-face-to-face-reads-true
//   godot --headless --path . res://tests/integration/StealAnimTest.tscn -- --harness-scenario=steal-segment-lengths
//   godot --headless --path . res://tests/integration/StealAnimTest.tscn -- --harness-scenario=steal-startup-fills-window
//   godot --headless --path . res://tests/integration/StealAnimTest.tscn -- --harness-scenario=steal-no-placeholder-leak
//   godot --headless --path . res://tests/integration/StealAnimTest.tscn -- --harness-scenario=steal-poses-the-skeleton
//   godot --headless --path . res://tests/integration/StealAnimTest.tscn -- --harness-scenario=no-unsuffixed-steal-state
//   godot --headless --path . res://tests/integration/StealAnimTest.tscn -- --harness-scenario=control-unsuffixed-probe
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "steal-left-reach".
//
// ── Why ActiveAnimNodeForHarness, not the resolver's own decision (#257) ────
// Same discipline as every other anim harness in this repo: reading
// AnimationNodeStateMachinePlayback.GetCurrentNode() (via the live
// AnimationTree) asserts what the state machine ACTUALLY did, never what
// MoveAnimResolver.ResolveStateName merely DECIDED. Travel() to a
// missing/misnamed state only LOGS, it never throws (#257).
//
// ── Why TWO real scenes/Player.tscn instances, and why both matter ─────────
// A steal has no ball-hand to swap and no dead-dribble gate (DefensiveMoveHarnessSeam's
// own header: "neither StealMove nor BlockMove is subject to that gate") — so,
// unlike BehindTheBackAnimTest, this harness does not NEED the holder to be
// dribbling to legally Begin() the move. It still puts the ball in Dribbling
// on the holder anyway, for two reasons: (1) it reproduces the realistic game
// state a steal is actually thrown in, and (2) BeginStealFromAimForHarness
// resolves TargetHand via ResolveStealTargetHand, which looks up the holder
// node BY ball.StateMachine.HolderPeerId — a real assigned holder is required
// for that lookup to find anyone at all (the "no live holder" fallback branch
// in ResolveStealTargetHand's own doc would otherwise silently skip the #254
// facing transform this file's face-to-face scenario exists to exercise).
//
// ── Why BeginStealFromAimForHarness, not `new StealMove(hand, sign)` directly ──
// BeginStealFromAimForHarness (tests/integration/DefensiveMoveHarnessSeam.cs)
// routes the aim sign through the REAL production mapping
// (ResolveStealTargetHand -> HandStateResolver.TargetHandFromAim) exactly as
// ApplyRequestedMove's "steal" branch does — the #254 facing transform is
// exactly what steal-face-to-face-reads-true depends on being genuinely
// exercised, not hand-picked around.
//
// ── Why steal-constant-polarity continuously TOGGLES the defender's own
//    HandSide, rather than flipping it once ─────────────────────────────────
// The obvious wrong implementation the brief calls out is suffixing by
// MoveAnimResolver.OriginHand(generic, ballHand) instead of ReachSide.
// OriginHand's formula is `generic == Startup ? ballHand : Opposite(ballHand)`
// — it is PHASE-conditioned, not ballHand-conditioned, so a single flip timed
// to land exactly at Active-entry (the timing every OTHER handed move's real
// hand-swap uses) would accidentally cancel out and still read constant. A
// steal never swaps ball-hand at Active-entry in real play (the defender
// isn't the holder), so this harness cannot borrow that natural timing at
// all — it must manufacture disagreement instead. Toggling HandSide every
// few ticks for the WHOLE run guarantees at least one read lands off any
// particular phase boundary, so OriginHand's phase-conditioned output (and a
// naive raw-ballHand-suffix output) is forced to show more than one suffix
// somewhere in the run, while the correct ReachSide-only implementation is
// provably unaffected by anything this scenario does to HandSide at all.
//
// ── Why steal-face-to-face-reads-true is the highest-value scenario ────────
// StealMove.TargetHand (the ball-HOLDER's hand under attack) and
// StealMove.ReachSide (the DEFENDER's own body-relative reach side) are
// related by the #254 facing transform,
// `TargetHand == AimSign * sign(cos(defenderHeading - holderHeading))`, so
// they are EQUAL when both players face the same way and OPPOSITE
// face-to-face — the overwhelmingly common defensive stance. Every other
// scenario in this file uses the DEFAULT (same-facing, cos ~= 1) heading pair,
// under which TargetHand and ReachSide happen to agree — so a build that
// suffixes by TargetHand instead of ReachSide (the wrong-but-plausible
// implementation MoveAnimResolver's own doc warns was the ORIGINAL #282
// handoff spec) would still pass steal-left-reach/steal-right-reach/
// steal-constant-polarity. Only a genuine face-to-face geometry, where the
// two values provably diverge, can catch it. See VerdictFaceToFace for the
// exact numbers.
//
// ── Why steal-segment-lengths and steal-startup-fills-window need no live tree ──
// Both are pure resource/scene inspections (AnimationLibrary clip lengths),
// same instrument BehindTheBackAnimTest's btb-segment-lengths uses. Split into
// two scenarios per the brief: steal-segment-lengths covers all six clips
// (float-noise tolerance — NOT the brief's one tick, which could not catch a
// one-tick retune; see that scenario's comment), steal-startup-fills-window is the SAME
// instrument scoped to just the Startup pair, asserted as its OWN scenario so
// the ADR-0018 telegraph requirement (Startup must fill its whole visible
// wind-up window) is pinned explicitly rather than merely implied by the
// all-six check passing.
//
// ── Why steal-poses-the-skeleton exists, and why it reads the LAST tick ────
// Every OTHER scenario here is upstream of the thing that actually matters.
// Reachability, duration and the state->clip mapping all hold perfectly well
// for a clip whose tracks bind to NOTHING — Godot logs "couldn't resolve
// track" and carries on; it does not fail. This reads the live Skeleton3D and
// asserts the upper body is still posed off REST on the LAST steal tick —
// see UpperBodyDepartureFromRest's own comment (copied verbatim from #281)
// for why that specific metric, and not "max departure from rest" or "max
// change across the arc", is the one that actually discriminates a bound clip
// from an unbound one on this rig.
//
// ── What this harness CANNOT prove ───────────────────────────────────────
// Whether the clip LOOKS right (correct limbs, correct silhouette, reads as
// "a steal") is #173's deferred human feel judgment (ADR-0021) — this harness
// asserts state-machine reachability, suffix correctness (ReachSide, not
// TargetHand, not a per-tick read), clip duration, the absence of the #296
// placeholder, and that the clip physically drives the rig; never whether the
// resulting pose is any good.
public partial class StealAnimTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int ArmFrames = 2;           // ticks for TryAssignTipoffHolder to run
    private const int ActionMarginFrames = 3;  // ticks to let TryStartDribble's effect settle
    private const int SettleMarginFrames = 5;  // ticks after returning to Inactive before the final read
    private const int ProbeSettleFrames = 40;  // ticks given to a direct Travel() probe to land (2/3 s @ 60 Hz)

    // How often (in physics ticks) steal-constant-polarity flips the
    // defender's own HandSide. Short enough that several flips land inside
    // even the shortest phase (Startup/Active are 8 ticks each) — see the
    // class doc's "Why steal-constant-polarity continuously TOGGLES" section.
    private const int ToggleHandSidePeriodFrames = 3;

    private static readonly string[] KnownScenarios =
    {
        "steal-left-reach", "steal-right-reach", "steal-constant-polarity",
        "steal-face-to-face-reads-true",
        "steal-segment-lengths", "steal-startup-fills-window", "steal-no-placeholder-leak",
        "steal-poses-the-skeleton",
        "no-unsuffixed-steal-state", "control-unsuffixed-probe",
    };

    // Upper-body bones the swipe must visibly move. Same set BehindTheBackAnimTest
    // uses — deliberately NOT the legs, matching that file's reasoning.
    private static readonly string[] UpperBodyBones =
    {
        "mixamorig_LeftArm", "mixamorig_RightArm",
        "mixamorig_LeftForeArm", "mixamorig_RightForeArm",
        "mixamorig_Spine",
    };

    // Degrees the upper body must still sit off REST on the final steal tick.
    // Copied verbatim from BehindTheBackAnimTest's PosedMinDeg — see that
    // file's comment for the mutation-derived reasoning (inert ~0 deg vs
    // bound ~179 deg on this rig, so 30 sits in the empty middle with an
    // order of magnitude of headroom on both sides).
    //
    // KNOWN LIMIT, established by mutation during #282 — do not over-trust this
    // scenario. It catches the defect it was BUILT for (unbound tracks: the
    // Armature/ path prefix, where the clip binds to nothing and the pose
    // collapses to rest within a tick of entry). It does NOT catch a MISSPELLED
    // clip name: a state whose `animation` names a nonexistent clip makes the
    // mixer HOLD the last valid pose from Startup rather than collapse, so the
    // final-tick reading still measured 157.98 deg and stayed green.
    // That defect is covered instead by steal-no-placeholder-leak (an allowlist
    // over the .tscn's declared clip names) and by steal-segment-lengths (which
    // must actually LOAD each clip out of locomotion.res to measure it).
    // The three scenarios are complementary; none of them subsumes another.
    private const float PosedMinDeg = 30.0f;

    // Face-to-face geometry (steal-face-to-face-reads-true). Exact 180 degrees
    // so relativeCos lands at exactly -1.0 — no floating-point boundary risk,
    // since HandStateResolver.TargetHandFromAim's own boundary is `> 0f`, not
    // `>= 0f` or `~= 0f`.
    private const float FaceToFaceHolderHeading = 0f;
    private static readonly float FaceToFaceDefenderHeading = Mathf.Pi;
    private const float FaceToFaceAimSign = 1f; // defender's own body-RIGHT

    // The two scenarios that need no tipoff/dribble/move setup at all — pure
    // resource/scene inspection, run once and finished.
    private static readonly string[] StaticScenarios =
    {
        "steal-segment-lengths", "steal-startup-fills-window", "steal-no-placeholder-leak",
    };

    private string _scenario = "steal-left-reach";

    private BallController _ball;
    private PlayerController _p1;
    private PlayerController _p2;

    private int _frame;
    private double _elapsed;
    private bool _finished;

    private int _holderId;
    private int _defenderId;

    private enum Step { AwaitTipoff, DriveChecked, Observing }
    private Step _step = Step.AwaitTipoff;
    private int _stepDeadlineFrame;

    // ── Latches for steal-left-reach / steal-right-reach / steal-face-to-face-reads-true ──
    // Chained exactly like BehindTheBackAnimTest's phase latches: each guard
    // requires the previous one already latched, so "saw all three" IS
    // "saw them in order." Shared across all three scenarios (only one runs
    // per process invocation) since each just asserts arrival at a single,
    // scenario-specific suffix.
    private bool _sawStartup;
    private bool _sawActive;
    private bool _sawRecovery;

    // ── Observations for steal-constant-polarity ─────────────────────────
    // Every DISTINCT "Steal*" node name the live tree reported over the whole
    // run — collected at event time (every physics tick), not sampled once at
    // the end.
    private readonly HashSet<string> _distinctStealStates = new();
    private bool _sawAnyStealState;

    // ── Latch for no-unsuffixed-steal-state / control-unsuffixed-probe ──────
    // Shared field: only one scenario runs per process invocation, so no
    // cross-talk. Set at event time the tick the probed node is observed.
    private bool _sawProbeTargetNode;

    // ── Observation for steal-poses-the-skeleton ──────────────────────────
    // Departure-from-rest on the LAST tick a steal state was the tree's
    // active node — OVERWRITE, not Max; see UpperBodyDepartureFromRest.
    private float _worstPosedDeg;

    // Gate for "the move genuinely ran" (real-move scenarios only): only once
    // the Active phase has actually been observed does a later return to
    // Inactive count as "the lifecycle finished."
    private bool _sawActivePhase;
    private int _returnedInactiveFrame = -1;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "steal-left-reach");
        GD.Print($"[steal-anim] scenario={_scenario} booting headless…");

        if (!KnownScenarios.Contains(_scenario))
        {
            Fail($"unknown scenario '{_scenario}'.");
            Finish();
            return;
        }

        // Real Player.tscn instances (live AnimationTree), named "1"/"2" so the
        // OfflineMultiplayerPeer makes unique_id 1 both IsServer and
        // IsLocalPlayer — and, since DisplayStealReachSide/DisplayMoveId key off
        // DisplayPhaseResolver.LocalMachineDrivesDisplay(IsServer, IsLocalPlayer)
        // = isServer || isLocalPlayer, node "2" ALSO drives its own display
        // locally here (both are "the server"), so ApplyAnimation reads each
        // node's own live CommittedMoveMachine regardless of which one is the
        // defender this run. Same instance shape as BehindTheBackAnimTest/
        // CrossoverAnimTest.
        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        _p1 = scene.Instantiate<PlayerController>();
        _p1.Name = "1";
        _p2 = scene.Instantiate<PlayerController>();
        _p2.Name = "2";

        // Physics-callback lockstep so GetCurrentNode() reflects the same-tick
        // Travel() (the default Idle callback lags under --headless — trap
        // #6/README).
        foreach (var p in new[] { _p1, _p2 })
            p.GetNode<AnimationTree>("AnimationTree").CallbackModeProcess =
                AnimationMixer.AnimationCallbackModeProcess.Physics;

        var players = new Node3D { Name = "Players" };
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

        if (StaticScenarios.Contains(_scenario))
        {
            if (_frame < 2) return; // let the just-instanced scene finish its own _Ready
            RunStaticCheck();
            return;
        }

        switch (_step)
        {
            case Step.AwaitTipoff:
                if (_frame < ArmFrames) break;
                if (_ball.StateMachine.HolderPeerId == 0)
                {
                    Fail($"{_scenario}: tipoff never assigned a holder.");
                    Finish();
                    return;
                }
                _holderId = _ball.StateMachine.HolderPeerId;
                _defenderId = _holderId == 1 ? 2 : 1;
                _ball.TryStartDribble(_holderId);
                _step = Step.DriveChecked;
                _stepDeadlineFrame = _frame + ActionMarginFrames;
                break;

            case Step.DriveChecked:
                if (_frame < _stepDeadlineFrame) break;
                if (_ball.State != BallState.Dribbling)
                {
                    Fail($"{_scenario}: expected TryStartDribble to reach Dribbling; got state={_ball.State}.");
                    Finish();
                    return;
                }

                var defender = DefenderNode();
                var holder = HolderNode();

                if (_scenario is "no-unsuffixed-steal-state" or "control-unsuffixed-probe")
                {
                    // Direct AnimationTree access on the DEFENDER — no seam
                    // needed, same "parameters/playback" path
                    // PlayerController's own _Ready resolves internally.
                    var tree = defender.GetNode<AnimationTree>("AnimationTree");
                    var playback = tree.Get("parameters/playback").As<AnimationNodeStateMachinePlayback>();
                    if (playback == null)
                    {
                        Fail($"{_scenario}: could not resolve 'parameters/playback' off the defender's AnimationTree.");
                        Finish();
                        return;
                    }
                    string target = _scenario == "no-unsuffixed-steal-state"
                        ? "StealActive"
                        : "StealActiveLeft";
                    GD.Print($"[steal-anim] issuing Travel(\"{target}\") directly on defender={_defenderId}, " +
                             $"current node before call = \"{playback.GetCurrentNode()}\".");
                    playback.Travel(target);
                    _step = Step.Observing;
                    _stepDeadlineFrame = _frame + ProbeSettleFrames;
                    break;
                }

                float aimSign = _scenario switch
                {
                    "steal-left-reach" => -1f,             // ReachSide = Left
                    "steal-right-reach" => 1f,              // ReachSide = Right
                    "steal-constant-polarity" => 1f,        // ReachSide = Right; HandSide is toggled continuously in Observe()
                    "steal-face-to-face-reads-true" => FaceToFaceAimSign, // ReachSide = Right, TargetHand = Left (see class doc)
                    "steal-poses-the-skeleton" => -1f,      // ReachSide = Left; arbitrary, just needs a real move
                    _ => throw new InvalidOperationException($"unhandled real-move scenario '{_scenario}'"),
                };

                if (_scenario == "steal-face-to-face-reads-true")
                {
                    // The #254 facing transform's whole point: the two players'
                    // AUTHORITATIVE headings, not their positions, decide whether
                    // TargetHand and ReachSide agree or diverge. Forced directly
                    // via the shared PlayerHarnessSeam.SetHeadingForHarness — a
                    // bare headless second node has no input path that would
                    // ever advance Heading otherwise.
                    holder.SetHeadingForHarness(FaceToFaceHolderHeading);
                    defender.SetHeadingForHarness(FaceToFaceDefenderHeading);

                    // (Doubt-cycle finding, verified by mutation) PlayerController.HandSide's
                    // FIELD DEFAULT is actually HandSide.Right — despite that
                    // property's own doc comments ("Reset to the default (Left)")
                    // claiming Left; ResetHandSide() shares the same mismatch. The
                    // defender never dribbles in this scenario, so nothing ever
                    // writes its HandSide, and it sits at that Right default for
                    // the whole run. FaceToFaceAimSign=+1 makes expectedReachSide
                    // Right too, so a mutant reading ballHand (this player's own
                    // HandSide) instead of reachSide would coincidentally read
                    // "Right" here and pass VACUOUSLY — proven by mutation: the
                    // `prefix + generic + ballHand` mutation (simulating "use
                    // TargetHand instead of ReachSide") passed this scenario
                    // green until this line was added. Forcing HandSide to the
                    // OPPOSITE of expectedReachSide removes the coincidence and
                    // makes the mutant's wrong answer visibly wrong.
                    defender.SetHandSideForHarness(HandSide.Left);
                }

                bool began = defender.BeginStealFromAimForHarness(aimSign, _ball);
                if (!began)
                {
                    Fail($"{_scenario}: BeginStealFromAimForHarness returned false " +
                         "— machine was not Inactive.");
                    Finish();
                    return;
                }
                GD.Print($"[steal-anim] Steal begun on defender={_defenderId} against holder={_holderId} " +
                         $"(aimSign={aimSign}, defenderHeading={defender.Heading}, holderHeading={holder.Heading}).");
                _step = Step.Observing;
                break;

            case Step.Observing:
                Observe();
                bool isProbeScenario = _scenario is "no-unsuffixed-steal-state" or "control-unsuffixed-probe";
                if (isProbeScenario)
                {
                    if (_frame >= _stepDeadlineFrame) RenderVerdict();
                }
                else if (_sawActivePhase && _returnedInactiveFrame >= 0 && _frame == _returnedInactiveFrame + SettleMarginFrames)
                {
                    RenderVerdict();
                }
                break;
        }

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame}, scenario={_scenario}, step={_step}, " +
                 $"lastAnimNode={DefenderNode()?.ActiveAnimNodeForHarness}, sawStartup={_sawStartup}, " +
                 $"sawActive={_sawActive}, sawRecovery={_sawRecovery}, sawActivePhase={_sawActivePhase}, " +
                 $"distinctStealStates=[{string.Join(",", _distinctStealStates)}], " +
                 $"sawProbeTargetNode={_sawProbeTargetNode}.");
            Finish();
        }
    }

    private PlayerController HolderNode() => _holderId == 1 ? _p1 : _p2;
    private PlayerController DefenderNode() => _defenderId == 1 ? _p1 : _p2;

    private void Observe()
    {
        PlayerController defender = DefenderNode();

        // steal-constant-polarity: flip the defender's own HandSide every
        // ToggleHandSidePeriodFrames ticks, for the WHOLE observing window —
        // see the class doc's "Why steal-constant-polarity continuously
        // TOGGLES" for why this must not be a single flip. Harmless for every
        // other scenario (this branch only runs for this one).
        if (_scenario == "steal-constant-polarity")
        {
            HandSide toggled = (_frame / ToggleHandSidePeriodFrames) % 2 == 0 ? HandSide.Left : HandSide.Right;
            defender.SetHandSideForHarness(toggled);
        }

        MovePhase phase = defender.PhaseForHarness;
        string node = defender.ActiveAnimNodeForHarness;

        if (phase == MovePhase.Active) _sawActivePhase = true;

        switch (_scenario)
        {
            case "steal-left-reach":
                if (!_sawStartup && node == "StealStartupLeft") _sawStartup = true;
                if (_sawStartup && !_sawActive && node == "StealActiveLeft") _sawActive = true;
                if (_sawActive && !_sawRecovery && node == "StealRecoveryLeft") _sawRecovery = true;
                break;

            case "steal-right-reach":
                if (!_sawStartup && node == "StealStartupRight") _sawStartup = true;
                if (_sawStartup && !_sawActive && node == "StealActiveRight") _sawActive = true;
                if (_sawActive && !_sawRecovery && node == "StealRecoveryRight") _sawRecovery = true;
                break;

            case "steal-face-to-face-reads-true":
                // FaceToFaceAimSign = +1 -> ReachSide = Right (see class doc).
                // A build that suffixed by TargetHand (Left here, opposite
                // geometry) would never latch any of these three.
                if (!_sawStartup && node == "StealStartupRight") _sawStartup = true;
                if (_sawStartup && !_sawActive && node == "StealActiveRight") _sawActive = true;
                if (_sawActive && !_sawRecovery && node == "StealRecoveryRight") _sawRecovery = true;
                break;

            case "steal-constant-polarity":
                if (node.StartsWith("Steal"))
                {
                    _sawAnyStealState = true;
                    _distinctStealStates.Add(node);
                }
                break;

            case "steal-poses-the-skeleton":
                // Latched at event time, on every tick a steal state is the
                // ACTIVE node — sampling afterwards would read whatever the
                // tree settled back into.
                if (node.StartsWith("Steal"))
                {
                    _sawAnyStealState = true;
                    // OVERWRITE, not Max: the verdict wants the departure on
                    // the LAST steal tick, not the largest seen.
                    _worstPosedDeg = UpperBodyDepartureFromRest(defender);
                }
                break;

            case "no-unsuffixed-steal-state":
                if (node == "StealActive") _sawProbeTargetNode = true;
                break;

            case "control-unsuffixed-probe":
                if (node == "StealActiveLeft") _sawProbeTargetNode = true;
                break;
        }

        if (_sawActivePhase && phase == MovePhase.Inactive && _returnedInactiveFrame < 0)
            _returnedInactiveFrame = _frame;
    }

    private void RenderVerdict()
    {
        switch (_scenario)
        {
            case "steal-left-reach":                VerdictOrigin("Left"); break;
            case "steal-right-reach":                VerdictOrigin("Right"); break;
            case "steal-face-to-face-reads-true":    VerdictFaceToFace(); break;
            case "steal-constant-polarity":          VerdictConstantPolarity(); break;
            case "steal-poses-the-skeleton":         VerdictPosesTheSkeleton(); break;
            case "no-unsuffixed-steal-state":        VerdictProbeUnsuffixed(); break;
            case "control-unsuffixed-probe":         VerdictProbeControl(); break;
        }
    }

    // ── Scenarios: steal-left-reach / steal-right-reach (positive) ─────────
    private void VerdictOrigin(string suffix)
    {
        bool pass = _sawStartup && _sawActive && _sawRecovery;
        if (pass)
            GD.Print($"[steal-anim] PASS steal-{suffix.ToLowerInvariant()}-reach — the tree was " +
                     $"observed on \"StealStartup{suffix}\", then \"StealActive{suffix}\", then " +
                     $"\"StealRecovery{suffix}\", in that order.");
        else
            Fail($"steal-{suffix.ToLowerInvariant()}-reach: expected StealStartup{suffix} -> " +
                 $"StealActive{suffix} -> StealRecovery{suffix}, in order; got sawStartup={_sawStartup}, " +
                 $"sawActive={_sawActive}, sawRecovery={_sawRecovery}, " +
                 $"lastAnimNode={DefenderNode().ActiveAnimNodeForHarness}.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: steal-face-to-face-reads-true (load-bearing) ─────────────
    // Same arrival check as VerdictOrigin("Right"), but under FACE-TO-FACE
    // geometry rather than the default same-facing headings every other
    // scenario in this file uses. Computes StealMove.TargetHand independently
    // (a pure static call, not read off any live move instance) purely to
    // print the discriminating numbers — the pass/fail verdict itself only
    // depends on which suffix the tree actually entered.
    private void VerdictFaceToFace()
    {
        HandSide expectedReachSide = FaceToFaceAimSign > 0f ? HandSide.Right : HandSide.Left;
        HandSide expectedTargetHand = HandStateResolver.TargetHandFromAim(
            FaceToFaceAimSign, FaceToFaceDefenderHeading, FaceToFaceHolderHeading);

        bool pass = _sawStartup && _sawActive && _sawRecovery;
        if (pass)
            GD.Print($"[steal-anim] PASS steal-face-to-face-reads-true — with the defender facing the " +
                     $"holder ({FaceToFaceDefenderHeading:F4} rad vs {FaceToFaceHolderHeading:F4} rad, " +
                     $"relativeCos={Mathf.Cos(FaceToFaceDefenderHeading - FaceToFaceHolderHeading):F1}), " +
                     $"the tree was observed on StealStartup{expectedReachSide} -> " +
                     $"StealActive{expectedReachSide} -> StealRecovery{expectedReachSide} — the DEFENDER's " +
                     $"own ReachSide ({expectedReachSide}), which is the OPPOSITE of StealMove.TargetHand " +
                     $"({expectedTargetHand}, the ball-holder's hand under attack) under this exact geometry. " +
                     "A build suffixing by TargetHand instead of ReachSide would have entered the " +
                     $"{expectedTargetHand} states here instead and this scenario would be the only one " +
                     "in this file to catch it.");
        else
            Fail($"steal-face-to-face-reads-true: expected StealStartup{expectedReachSide} -> " +
                 $"StealActive{expectedReachSide} -> StealRecovery{expectedReachSide} (the defender's " +
                 $"ReachSide); got sawStartup={_sawStartup}, sawActive={_sawActive}, sawRecovery={_sawRecovery}, " +
                 $"lastAnimNode={DefenderNode().ActiveAnimNodeForHarness}. expectedTargetHand={expectedTargetHand} " +
                 $"(opposite of expectedReachSide={expectedReachSide}) — if the tree instead entered the " +
                 $"{expectedTargetHand} states, the resolver is suffixing by TargetHand, not ReachSide.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: steal-constant-polarity (load-bearing) ────────────────────
    private void VerdictConstantPolarity()
    {
        // Premise check, in two parts — same reasoning as BehindTheBackAnimTest's
        // btb-single-polarity: "all observed suffixes agree" is vacuously true
        // over an empty set and nearly vacuous over a set of one, so require
        // all THREE phases observed before trusting a single-suffix result.
        var suffixes = _distinctStealStates.Select(SuffixOf).Distinct().ToList();
        var phases = _distinctStealStates.Select(PhaseOf).Distinct().ToList();
        bool sawWholeArc = phases.Count == 3;
        bool pass = _sawAnyStealState && sawWholeArc && suffixes.Count == 1;

        if (pass)
            GD.Print($"[steal-anim] PASS steal-constant-polarity — every distinct steal state observed " +
                     $"([{string.Join(",", _distinctStealStates)}]) carried the SAME suffix (\"{suffixes[0]}\") " +
                     "across the whole Startup->Active->Recovery arc, even while the defender's own HandSide " +
                     $"was toggled every {ToggleHandSidePeriodFrames} ticks throughout.");
        else
            Fail($"steal-constant-polarity: expected all three phases observed and exactly one suffix across " +
                 $"every observed steal state; got sawAnyStealState={_sawAnyStealState}, " +
                 $"sawWholeArc={sawWholeArc}, distinctStates=[{string.Join(",", _distinctStealStates)}], " +
                 $"distinctPhases=[{string.Join(",", phases)}], " +
                 $"distinctSuffixes=[{string.Join(",", suffixes)}]. If the premise broke, this proves nothing, " +
                 "so this fails rather than passes.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: steal-poses-the-skeleton ──────────────────────────────────
    // Same gap BehindTheBackAnimTest's own btb-poses-the-skeleton closes, and
    // the same metric (copied verbatim, see UpperBodyDepartureFromRest below).
    private void VerdictPosesTheSkeleton()
    {
        bool pass = _sawAnyStealState && _worstPosedDeg >= PosedMinDeg;
        if (pass)
            GD.Print($"[steal-anim] PASS steal-poses-the-skeleton — the upper body was still posed {_worstPosedDeg:F2} deg off rest " +
                     $"(floor {PosedMinDeg:F1}) on the LAST steal tick, so the clip's tracks bind to this " +
                     "rig and hold it — rather than collapsing it to rest, which is what an unbound clip does.");
        else
            Fail($"steal-poses-the-skeleton: the clip did not move the rig. sawAnyStealState={_sawAnyStealState}, " +
                 $"upperBodyDepartureFromRestOnLastStealTick={_worstPosedDeg:F4} deg (need >= {PosedMinDeg:F1}). " +
                 "Most likely the clips' track NODE PATHS do not bind on scenes/Player.tscn (check for an " +
                 "'Armature/' prefix), or the clip omits the arm tracks entirely and they are sitting at rest.");
        Finish(pass ? 0 : 1);
    }

    // Upper-body bone rotations off the defender's live Skeleton3D, this tick.
    //
    // Largest upper-body departure from REST, sampled on the LAST tick a steal
    // state was active. Copied verbatim from BehindTheBackAnimTest's own
    // UpperBodyDepartureFromRest (#281) — that file's comment records the
    // mutation history this metric survived: "max departure from rest" and
    // "max change across the arc" BOTH passed on deliberately-corrupted
    // (unbindable "Armature/..." track path) clips, because the Y Bot rest is
    // a T-pose (so "far from rest" is true either way) and an unbound clip's
    // COLLAPSE to rest is itself a large change ("it moved" does not imply
    // "this clip moved it"). Only the final-tick reading separates the two:
    // a bound clip holds its pose all the way through Recovery, an unbound one
    // collapses to rest within a tick of entry (no xfade on any edge) and
    // stays there.
    private static float UpperBodyDepartureFromRest(PlayerController holder)
    {
        var skel = FindSkeleton(holder);
        if (skel == null) return 0f;

        float worst = 0f;
        foreach (string boneName in UpperBodyBones)
        {
            int idx = skel.FindBone(boneName);
            if (idx < 0) continue;
            Quaternion rest = skel.GetBoneRest(idx).Basis.GetRotationQuaternion().Normalized();
            Quaternion pose = skel.GetBonePose(idx).Basis.GetRotationQuaternion().Normalized();
            worst = Math.Max(worst, Mathf.RadToDeg(rest.AngleTo(pose)));
        }
        return worst;
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

    private static string SuffixOf(string node) =>
        node.EndsWith("Left") ? "Left" :
        node.EndsWith("Right") ? "Right" :
        "None";

    // The phase half of a "Steal<Phase><Suffix>" node name, used only to prove
    // the whole Startup->Active->Recovery arc was actually walked.
    private static string PhaseOf(string node) =>
        node.Contains("Startup") ? "Startup" :
        node.Contains("Active") ? "Active" :
        node.Contains("Recovery") ? "Recovery" :
        "None";

    // ── Scenario: no-unsuffixed-steal-state (control) ───────────────────────
    // Direct probe, not a statistical observation over a real move — same
    // reasoning as BehindTheBackAnimTest's own pair. The premise this depends
    // on — that a Travel() call under these identical conditions CAN succeed
    // at all — is proven by the companion scenario below, not re-derived here.
    private void VerdictProbeUnsuffixed()
    {
        bool pass = !_sawProbeTargetNode;
        if (pass)
            GD.Print("[steal-anim] PASS no-unsuffixed-steal-state — Travel(\"StealActive\") " +
                     $"never reached that node across {ProbeSettleFrames} ticks; the state machine has no " +
                     "such node to travel to (the unsuffixed state was never wired for this issue). See " +
                     "control-unsuffixed-probe for the premise proof that Travel() itself works under these " +
                     "identical conditions.");
        else
            Fail("no-unsuffixed-steal-state: Travel(\"StealActive\") reached a node reporting that " +
                 "exact name — an unsuffixed steal state exists in scenes/Player.tscn and must not " +
                 "(#282's premise: the suffix is total over the defender's two-valued ReachSide, so no " +
                 "unsuffixed fallback should exist).");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: control-unsuffixed-probe (premise control) ────────────────
    private void VerdictProbeControl()
    {
        bool pass = _sawProbeTargetNode;
        if (pass)
            GD.Print("[steal-anim] PASS control-unsuffixed-probe — Travel(\"StealActiveLeft\") " +
                     $"reached that node within {ProbeSettleFrames} ticks, under the SAME setup " +
                     "no-unsuffixed-steal-state uses. This proves the Travel()-and-observe mechanism itself " +
                     "is sound, which is the premise no-unsuffixed-steal-state's pass depends on.");
        else
            Fail($"control-unsuffixed-probe: Travel(\"StealActiveLeft\") never reached that node " +
                 $"within {ProbeSettleFrames} ticks — either the state is missing/misnamed in scenes/Player.tscn, " +
                 "or the Locomotion->StealStartupLeft->StealActiveLeft edge chain is broken, or the probe " +
                 "mechanism itself is broken — in which case no-unsuffixed-steal-state's pass would prove " +
                 "nothing.");
        Finish(pass ? 0 : 1);
    }

    // ── Static scenarios: no live tree, pure resource/scene inspection ──────
    private void RunStaticCheck()
    {
        switch (_scenario)
        {
            case "steal-segment-lengths":       RunSegmentLengthsCheck(); break;
            case "steal-startup-fills-window":  RunStartupFillsWindowCheck(); break;
            case "steal-no-placeholder-leak":   RunNoPlaceholderLeakCheck(); break;
        }
    }

    // ── Scenario: steal-segment-lengths ──────────────────────────────────────
    // Same instrument as BehindTheBackAnimTest's btb-segment-lengths: read the
    // move's real tick windows from StealMove.DefaultFrameData (not
    // hardcoded), so a future #238 retune that forgets to re-run
    // tools/rebuild_steal_clips.gd goes red here and names the tool. Tolerance
    // is ONE TICK (1/60 s) per the brief.
    private void RunSegmentLengthsCheck()
    {
        var lib = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        if (lib == null)
        {
            Fail("assets/locomotion.res failed to load — cannot evaluate steal-segment-lengths.");
            Finish(1);
            return;
        }

        double tps = Engine.PhysicsTicksPerSecond;
        var frames = StealMove.DefaultFrameData;
        // NOT one tick, despite the brief. A one-tick bar cannot catch a one-tick
        // retune — bumping StartupFrames 8 → 9 deviates by exactly 1/60 s, slips
        // under, and reports green while stealstartup* is still cut to 8 ticks and
        // no longer covers the move's Startup window. That is the staleness this
        // scenario exists to catch, so the loose bar voided its own purpose (#314
        // review). Measured deviation on all six steal clips is 0.000000s (re-checkable:
        // the scenario prints deviation= per clip on every run, pass or fail) — the
        // slice is exact and the tolerance only absorbs float32 `Animation.Length`
        // representation noise (~5e-9 s here). A NOISE BAND, not a drift allowance:
        // 1e-3 s is ~17x tighter than the smallest possible retune. A clip landing
        // genuinely near a tick is a slice bug to fix, not a bar to widen back.
        const double ToleranceSeconds = 1e-3;

        (string Clip, int Ticks)[] windows =
        {
            ("stealstartupleft",   frames.StartupFrames),
            ("stealactiveleft",    frames.ActiveFrames),
            ("stealrecoveryleft",  frames.RecoveryFrames),
            ("stealstartupright",  frames.StartupFrames),
            ("stealactiveright",   frames.ActiveFrames),
            ("stealrecoveryright", frames.RecoveryFrames),
        };

        bool pass = CheckClipWindows(lib, windows, tps, ToleranceSeconds, "steal-segment-lengths",
            "tools/rebuild_steal_clips.gd");

        if (pass)
            GD.Print("[steal-anim] PASS steal-segment-lengths — all six clips' durations are within " +
                     "the float-noise band of StealMove.DefaultFrameData's Startup/Active/Recovery tick windows.");
        else
            GD.PrintErr("[steal-anim] FAIL steal-segment-lengths — see per-clip deviations above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: steal-startup-fills-window ─────────────────────────────────
    // The ADR-0018 telegraph requirement, asserted rather than assumed: a
    // steal's Startup must fill its whole visible wind-up window (8 ticks,
    // StealMove.DefaultFrameData.StartupFrames), the same instrument as
    // steal-segment-lengths but scoped to just the Startup pair and reported
    // as its OWN scenario per the brief, rather than folded silently into the
    // all-six check.
    private void RunStartupFillsWindowCheck()
    {
        var lib = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        if (lib == null)
        {
            Fail("assets/locomotion.res failed to load — cannot evaluate steal-startup-fills-window.");
            Finish(1);
            return;
        }

        double tps = Engine.PhysicsTicksPerSecond;
        var frames = StealMove.DefaultFrameData;
        // Same 1e-3 noise band as steal-segment-lengths, but note this scenario's
        // claim is NOT staleness — it is the ADR-0018 TELEGRAPH requirement, that
        // the wind-up actually fills the 8 ticks a defender is given to read. A
        // one-tick tolerance is worse here than it is above: it would accept a
        // Startup clip a full tick (12.5% of the telegraph) short of the window
        // this scenario's own name promises it "fills". Measured deviation on both
        // Startup clips is 0.000000s, so the band costs nothing.
        const double ToleranceSeconds = 1e-3;

        (string Clip, int Ticks)[] windows =
        {
            ("stealstartupleft",  frames.StartupFrames),
            ("stealstartupright", frames.StartupFrames),
        };

        bool pass = CheckClipWindows(lib, windows, tps, ToleranceSeconds, "steal-startup-fills-window",
            "tools/rebuild_steal_clips.gd");

        if (pass)
            GD.Print($"[steal-anim] PASS steal-startup-fills-window — both Startup clips fill the " +
                     $"{frames.StartupFrames}-tick ADR-0018 telegraph window to within float noise " +
                     $"({ToleranceSeconds:F6}s, StealMove.DefaultFrameData.StartupFrames). A clip even " +
                     "one tick short of the telegraph goes red here.");
        else
            GD.PrintErr("[steal-anim] FAIL steal-startup-fills-window — see per-clip deviations above.");

        Finish(pass ? 0 : 1);
    }

    // Shared clip-duration-vs-tick-window check used by both static duration
    // scenarios above. Returns true iff every named clip exists and is within
    // toleranceSeconds of ticks/tps.
    private bool CheckClipWindows(AnimationLibrary lib, (string Clip, int Ticks)[] windows, double tps,
        double toleranceSeconds, string scenarioTag, string rebuildTool)
    {
        bool pass = true;
        foreach (var (clipName, ticks) in windows)
        {
            if (!lib.HasAnimation(clipName))
            {
                Fail($"AnimationLibrary has no clip '{clipName}' — run {rebuildTool}.");
                pass = false;
                continue;
            }

            double expectedSeconds = ticks / tps;
            // Variant accessor, NOT .Length: that property is `float` in Godot 4.6.x
            // and `double` in 4.7, so a 4.7.1-built assembly throws
            // MissingMethodException under a stale 4.6 binary — and it throws
            // inside _PhysicsProcess, BEFORE the timeout check, so the scenario
            // HANGS instead of failing (#339 measured all 8 of these). The
            // Variant accessor binds correctly under both. See AuthoredClipMcpProbe.
            double actualSeconds = lib.GetAnimation(clipName).Get("length").AsDouble();
            double deviationSeconds = Math.Abs(actualSeconds - expectedSeconds);
            GD.Print($"[steal-anim]   '{clipName}': length={actualSeconds:F6}s " +
                     $"expected={expectedSeconds:F6}s ({ticks} ticks @ {tps} tps), deviation={deviationSeconds:F6}s");

            if (deviationSeconds > toleranceSeconds)
            {
                Fail($"clip '{clipName}' is {actualSeconds:F6}s, expected {expectedSeconds:F6}s " +
                     $"({ticks} ticks at {tps} tps — StealMove.DefaultFrameData), a deviation of " +
                     $"{deviationSeconds:F6}s exceeds the float-noise tolerance ({toleranceSeconds:F6}s). Re-run " +
                     $"{rebuildTool} after retuning the move's frame data. [{scenarioTag}]");
                pass = false;
            }
        }
        return pass;
    }

    // ── Scenario: steal-no-placeholder-leak ──────────────────────────────────
    // The direct statement that #296 is closed for steal: reads the
    // AnimationNodeStateMachine resource directly off scenes/Player.tscn's
    // SceneState, same instrument BehindTheBackAnimTest's btb-no-placeholder-leak
    // uses.
    private void RunNoPlaceholderLeakCheck()
    {
        var playerScene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        var sceneState = playerScene.GetState();
        AnimationNodeStateMachine stateMachine = null;
        for (int i = 0; i < sceneState.GetNodeCount(); i++)
        {
            if (sceneState.GetNodeType(i) != "AnimationTree") continue;
            for (int p = 0; p < sceneState.GetNodePropertyCount(i); p++)
            {
                if (sceneState.GetNodePropertyName(i, p) != "tree_root") continue;
                stateMachine = sceneState.GetNodePropertyValue(i, p).As<AnimationNodeStateMachine>();
            }
        }

        if (stateMachine == null)
        {
            Fail("could not read an AnimationNodeStateMachine off scenes/Player.tscn's AnimationTree " +
                 "tree_root — the state<->clip mapping is unverified.");
            Finish(1);
            return;
        }

        // An ALLOWLIST, not a placeholder blocklist — same reasoning as
        // BehindTheBackAnimTest's own check: the realistic slip is a state
        // pointing at a real-but-wrong clip (e.g. locomotion/stealactiveright
        // on the Left state), which a blocklist waves through and which
        // GetCurrentNode() cannot see either (the STATE name would still read
        // correctly).
        (string State, string Clip)[] states =
        {
            ("StealStartupLeft",   "locomotion/stealstartupleft"),
            ("StealActiveLeft",    "locomotion/stealactiveleft"),
            ("StealRecoveryLeft",  "locomotion/stealrecoveryleft"),
            ("StealStartupRight",  "locomotion/stealstartupright"),
            ("StealActiveRight",   "locomotion/stealactiveright"),
            ("StealRecoveryRight", "locomotion/stealrecoveryright"),
        };
        string[] placeholderClips = { "locomotion/idle", "locomotion/run" };

        bool pass = true;
        foreach (var (stateName, expectedClip) in states)
        {
            if (!stateMachine.HasNode(stateName))
            {
                Fail($"scenes/Player.tscn's state machine has no state '{stateName}' — cannot evaluate " +
                     "steal-no-placeholder-leak for it.");
                pass = false;
                continue;
            }
            var animNode = stateMachine.GetNode(stateName) as AnimationNodeAnimation;
            if (animNode == null)
            {
                Fail($"state '{stateName}' is not an AnimationNodeAnimation — a per-move state must be a " +
                     "single full-weight clip, never a blend (#287).");
                pass = false;
                continue;
            }
            string clip = animNode.Animation;
            GD.Print($"[steal-anim]   state '{stateName}' -> clip '{clip}' (expected '{expectedClip}')");
            if (placeholderClips.Contains(clip))
            {
                Fail($"state '{stateName}' still points at the placeholder clip '{clip}' — #296 is not " +
                     "closed for this state. Re-point it at its own locomotion/steal... clip.");
                pass = false;
            }
            else if (clip != expectedClip)
            {
                Fail($"state '{stateName}' points at '{clip}', not its own '{expectedClip}'. The clip is real, " +
                     "so this is not #296 — it is a mis-wired sub-resource. The state name alone reads correct, " +
                     "so no reachability assertion can catch this.");
                pass = false;
            }
        }

        if (pass)
            GD.Print("[steal-anim] PASS steal-no-placeholder-leak — none of the six steal states points at " +
                     "locomotion/idle or locomotion/run (#296 closed for this move).");
        else
            GD.PrintErr("[steal-anim] FAIL steal-no-placeholder-leak — see per-state clips above.");

        Finish(pass ? 0 : 1);
    }

    private void Fail(string message) => GD.PrintErr($"[steal-anim] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[steal-anim] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
