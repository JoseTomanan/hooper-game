using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #281 (ADR-0016): proves the SIX
// hand-side behind-the-back states scenes/Player.tscn now carries —
// BehindTheBack{Startup,Active,Recovery}{Left,Right} — are actually ENTERED
// end-to-end through the real AnimationTree, that the SUFFIX stays constant
// across a single behind-the-back's whole Startup->Active->Recovery arc even
// though PlayerController.HandSide itself flips mid-move, that the deleted
// unsuffixed states are genuinely gone (not merely renamed-and-forgotten),
// that each clip's duration matches BehindTheBack.DefaultFrameData's tick
// windows, and that none of the six states still points at the #296
// placeholder (locomotion/idle, which all three original unsuffixed states
// resolved to before this issue).
//
// This is #280's shipped CrossoverAnimTest pattern, copied structurally and
// extended with two scenarios crossover's proof didn't need (see "Why two
// probe scenarios" below) plus a segment-length and a placeholder-leak check
// that live here instead of LocomotionClipTest.cs (this issue does not own
// that file).
//
//   godot --headless --path . res://tests/integration/BehindTheBackAnimTest.tscn -- --harness-scenario=btb-left-origin
//   godot --headless --path . res://tests/integration/BehindTheBackAnimTest.tscn -- --harness-scenario=btb-right-origin
//   godot --headless --path . res://tests/integration/BehindTheBackAnimTest.tscn -- --harness-scenario=btb-single-polarity
//   godot --headless --path . res://tests/integration/BehindTheBackAnimTest.tscn -- --harness-scenario=no-unsuffixed-btb-state
//   godot --headless --path . res://tests/integration/BehindTheBackAnimTest.tscn -- --harness-scenario=control-unsuffixed-probe
//   godot --headless --path . res://tests/integration/BehindTheBackAnimTest.tscn -- --harness-scenario=btb-segment-lengths
//   godot --headless --path . res://tests/integration/BehindTheBackAnimTest.tscn -- --harness-scenario=btb-no-placeholder-leak
//   godot --headless --path . res://tests/integration/BehindTheBackAnimTest.tscn -- --harness-scenario=btb-poses-the-skeleton
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "btb-left-origin".
//
// ── Why ActiveAnimNodeForHarness, not the resolver's own decision (#257) ────
// Same discipline as every other anim harness in this repo: reading
// AnimationNodeStateMachinePlayback.GetCurrentNode() (via the live
// AnimationTree) asserts what the state machine ACTUALLY did, never what
// MoveAnimResolver.ResolveStateName merely DECIDED — calling that directly
// would keep passing even on a Player.tscn with none of the six handed
// states wired at all. Travel() to a missing/misnamed state only LOGS, it
// never throws (#257).
//
// ── Why a real scenes/Player.tscn instance ───────────────────────────────
// A live AnimationTree only exists on the REAL scenes/Player.tscn. Behind-
// the-back is ALSO a dribble-family committed move: BeginCommittedMove's
// Held-holder dead-dribble gate (#193) refuses it outright unless the ball
// is genuinely Dribbling — same reasoning CrossoverAnimTest's header spells
// out, since BehindTheBack shares Crossover's begin-path gating verbatim.
//
// ── Why BeginBehindTheBackForHarness, SetHandSideForHarness ─────────────────
// Both seams already existed before this issue (BehindTheBackHarnessSeam.cs
// for #194, PlayerHarnessSeam.cs for #280) — nothing new needed adding to
// PlayerController for the origin/single-polarity scenarios. Right-origin
// forces HandSide directly because a fresh tipoff possession always resets
// it to the default and there is no production path that starts a holder in
// the OTHER polarity without first running a real hand-swapping move.
//
// ── Why btb-single-polarity does NOT re-derive expected names ───────────────
// Same reasoning as crossover-single-polarity: it does not ask "does
// MoveAnimResolver think this should be BehindTheBackActiveLeft" — that
// would just re-run the resolver's own (already unit-tested) formula against
// itself. It collects whatever DISTINCT "BehindTheBack*" node names the LIVE
// tree actually reported over one full move and asserts they all share the
// same trailing suffix. A per-tick HandSide read (the exact bug OriginHand
// exists to prevent) would make this scenario observe BOTH
// "BehindTheBackStartupLeft" and "BehindTheBackActiveLeft"/
// "BehindTheBackRecoveryRight" (or the mirror) in the same run — this
// scenario fails on that mix, not on comparing against a resolver-derived
// expectation.
//
// ── Why two probe scenarios instead of crossover's single control ──────────
// CrossoverAnimTest's "no-unsuffixed-crossover-state" drives a REAL crossover
// and asserts the SET of observed states never includes a bare unsuffixed
// name — a statistical control over an actually-running move. That works for
// crossover, but here the brief calls for a more direct instrument: issue
// AnimationNodeStateMachinePlayback.Travel() DIRECTLY at a literal state
// name and observe whether the tree ever reports having arrived, bypassing
// CommittedMoveMachine and MoveAnimResolver entirely.
//
// The two scenarios share IDENTICAL setup (a dribbling holder, same starting
// node "Dribble" + the holder's HandSide — #294 split the single Dribble
// state into DribbleLeft/DribbleRight, but both still transition into the
// same BehindTheBack states below) and differ only in the Travel() target:
//   - "no-unsuffixed-btb-state" travels to "BehindTheBackActive" — a node
//     that must not exist at all post-#281 (the three unsuffixed states are
//     DELETED, not merely unreachable). GetCurrentNode() can never report a
//     name that names no node, so this is a direct existence probe.
//   - "control-unsuffixed-probe" travels to "BehindTheBackActiveLeft" — a
//     node that DOES exist and IS reachable from Dribble via the
//     Dribble->BehindTheBackStartupLeft->BehindTheBackActiveLeft edge chain
//     the #281 wiring adds. This is the PREMISE for the scenario above: it
//     proves that under these exact conditions, a Travel() call that SHOULD
//     succeed actually does — so "no-unsuffixed-btb-state" passing means the
//     unsuffixed name is unreachable, not that Travel()/observation itself
//     is broken. Without this control, "never reached BehindTheBackActive"
//     would pass vacuously on a completely broken probe mechanism too.
//
// This does NOT attempt an edge-level assertion (trap #279 proved
// unreliable via mutation: Travel() is a pathfinder that routes around a
// missing EDGE). A missing NODE is a different failure shape entirely —
// there is no path to route around when the destination doesn't exist — so
// this probe is sound where an edge-deletion probe would not be.
//
// ── Why btb-segment-lengths and btb-no-placeholder-leak need no live tree ───
// Both are pure resource/scene inspections (AnimationLibrary clip lengths;
// the AnimationNodeStateMachine's state->clip mapping read directly off
// scenes/Player.tscn's SceneState) — same instrument LocomotionClipTest uses
// for the equivalent crossover/jumpshot families, reproduced here rather
// than added to that file because this issue does not own it. Neither
// scenario drives BallController/PlayerController at all; the shared
// _Ready() setup below still instantiates them for symmetry with the other
// five scenarios, but they sit idle for these two.
//
// ── Why btb-poses-the-skeleton exists ────────────────────────────────────
// Because every OTHER scenario here is upstream of the thing that matters.
// Reachability, duration and the state->clip mapping all hold perfectly well
// for a clip whose tracks bind to NOTHING: the seven scenarios above were all
// green while the six clips carried "Armature/Skeleton3D:mixamorig_Hips"
// paths — one level deeper than this rig, whose skeleton is at "Skeleton3D" —
// so the clips were silent no-ops and the mesh never moved. Godot logs
// "couldn't resolve track" and carries on. See that scenario's own comment for
// why the naive metrics (departure-from-rest, change-across-the-arc) both
// FAILED to catch it under mutation, and why the final-tick reading does.
//
// ── What this harness CANNOT prove ───────────────────────────────────────
// Whether either clip LOOKS right (correct limbs, no foot-sliding, reads as
// "behind the back") is #173's deferred human feel judgment (ADR-0021) —
// this harness asserts state-machine reachability, suffix consistency, clip
// duration, the absence of the #296 placeholder, and that the clip physically
// drives the rig; never whether the resulting pose is any good.
public partial class BehindTheBackAnimTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int ArmFrames = 2;           // ticks for TryAssignTipoffHolder to run
    private const int ActionMarginFrames = 3;  // ticks to let TryStartDribble's effect settle
    private const int SettleMarginFrames = 5;  // ticks after returning to Inactive before the final read
    private const int ProbeSettleFrames = 40;  // ticks given to a direct Travel() probe to land (2/3 s @ 60 Hz)

    private static readonly string[] KnownScenarios =
    {
        "btb-left-origin", "btb-right-origin", "btb-single-polarity",
        "no-unsuffixed-btb-state", "control-unsuffixed-probe",
        "btb-segment-lengths", "btb-no-placeholder-leak",
        "btb-poses-the-skeleton",
    };

    // Upper-body bones the wrap must visibly move. Deliberately NOT the legs:
    // the source clip's own crouch already moves those, so a leg-only check
    // would pass on a clip that never touched the arms at all.
    private static readonly string[] UpperBodyBones =
    {
        "mixamorig_LeftArm", "mixamorig_RightArm",
        "mixamorig_LeftForeArm", "mixamorig_RightForeArm",
        "mixamorig_Spine",
    };

    // Degrees the upper body must still sit off REST on the final
    // behind-the-back tick. Set from the measured separation, not by taste: an
    // unbound clip reads ~0 deg there (the bones have collapsed to rest) and a
    // bound one reads ~179 deg, so 30 sits in the empty middle with an order of
    // magnitude of headroom on both sides. It pins no particular pose — which
    // pose is right is #173's deferred feel call, not this harness's business.
    private const float PosedMinDeg = 30.0f;

    // The two scenarios that need no tipoff/dribble/move setup at all — pure
    // resource/scene inspection, run once and finished.
    private static readonly string[] StaticScenarios =
    {
        "btb-segment-lengths", "btb-no-placeholder-leak",
    };

    private string _scenario = "btb-left-origin";

    private BallController _ball;
    private PlayerController _p1;
    private PlayerController _p2;

    private int _frame;
    private double _elapsed;
    private bool _finished;

    private int _holderId;

    private enum Step { AwaitTipoff, DriveChecked, Observing }
    private Step _step = Step.AwaitTipoff;
    private int _stepDeadlineFrame;

    // ── Latches for btb-left-origin / btb-right-origin ──────────────────────
    // Chained exactly like CrossoverAnimTest's phase latches: each guard
    // requires the previous one already latched, so "saw all three" IS
    // "saw them in order."
    private bool _sawStartup;
    private bool _sawActive;
    private bool _sawRecovery;

    // ── Observations for btb-single-polarity ─────────────────────────────
    // Every DISTINCT "BehindTheBack*" node name the live tree reported over
    // the whole run — collected at event time (every physics tick), not
    // sampled once at the end.
    private readonly HashSet<string> _distinctBtbStates = new();
    private bool _sawAnyBtbState;

    // ── Latch for no-unsuffixed-btb-state / control-unsuffixed-probe ────────
    // Shared field: only one scenario runs per process invocation, so no
    // cross-talk. Set at event time the tick the probed node is observed.
    private bool _sawProbeTargetNode;

    // ── Observation for btb-poses-the-skeleton ──────────────────────────────
    // Largest upper-body departure from rest seen on any tick a behind-the-back
    // state was the tree's active node.
    private float _worstPosedDeg;

    // Gate for "the move genuinely ran" (real-move scenarios only): only once
    // the Active phase has actually been observed does a later return to
    // Inactive count as "the lifecycle finished."
    private bool _sawActivePhase;
    private int _returnedInactiveFrame = -1;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "btb-left-origin");
        GD.Print($"[behindtheback-anim] scenario={_scenario} booting headless…");

        if (!KnownScenarios.Contains(_scenario))
        {
            Fail($"unknown scenario '{_scenario}'.");
            Finish();
            return;
        }

        // Real Player.tscn instances (live AnimationTree), named "1"/"2" so the
        // OfflineMultiplayerPeer makes unique_id 1 both IsServer and
        // IsLocalPlayer (the full TickServerOwnPlayer -> ApplyAnimation chain
        // runs every tick), same as CrossoverAnimTest/JumpshotAnimTest.
        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        _p1 = scene.Instantiate<PlayerController>();
        _p1.Name = "1";
        _p2 = scene.Instantiate<PlayerController>();
        _p2.Name = "2";

        // Physics-callback lockstep so GetCurrentNode() reflects the same-tick
        // Travel() (the default Idle callback lags under --headless — trap
        // #6/README). Harness-only observation fidelity; unused by the two
        // static scenarios but harmless to set unconditionally.
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
                _ball.TryStartDribble(_holderId);
                _step = Step.DriveChecked;
                _stepDeadlineFrame = _frame + ActionMarginFrames;
                break;

            case Step.DriveChecked:
                if (_frame < _stepDeadlineFrame) break;
                if (_ball.State != BallState.Dribbling)
                {
                    Fail($"{_scenario}: expected TryStartDribble to reach Dribbling " +
                         $"(BehindTheBack cannot Begin from Held, #193); got state={_ball.State}.");
                    Finish();
                    return;
                }

                var holder = HolderNode();

                if (_scenario == "btb-left-origin")
                    holder.SetHandSideForHarness(HandSide.Left);
                else if (_scenario == "btb-right-origin")
                    holder.SetHandSideForHarness(HandSide.Right);

                if (_scenario is "no-unsuffixed-btb-state" or "control-unsuffixed-probe")
                {
                    // Direct AnimationTree access — no seam needed, this is
                    // exactly the same "parameters/playback" path
                    // PlayerController's own _Ready resolves internally
                    // (scripts/Player/PlayerController.cs), reached here via
                    // ordinary Godot node/property API on the exported
                    // AnimationTree child.
                    var tree = holder.GetNode<AnimationTree>("AnimationTree");
                    var playback = tree.Get("parameters/playback").As<AnimationNodeStateMachinePlayback>();
                    if (playback == null)
                    {
                        Fail($"{_scenario}: could not resolve 'parameters/playback' off the holder's AnimationTree.");
                        Finish();
                        return;
                    }
                    string target = _scenario == "no-unsuffixed-btb-state"
                        ? "BehindTheBackActive"
                        : "BehindTheBackActiveLeft";
                    GD.Print($"[behindtheback-anim] issuing Travel(\"{target}\") directly on holder={_holderId}, " +
                             $"current node before call = \"{playback.GetCurrentNode()}\".");
                    playback.Travel(target);
                    _step = Step.Observing;
                    _stepDeadlineFrame = _frame + ProbeSettleFrames;
                    break;
                }

                // The flick sign must point at the EMPTY hand, or this would not
                // be a genuine behind-the-back at all in the live input path —
                // same discipline as CrossoverAnimTest (HandStateResolver shares
                // its crossover-direction gate with BehindTheBack's begin path,
                // see PlayerController's "BehindTheBack (#194) reuses this SAME
                // flick-toward-the-empty-hand" comment). BeginBehindTheBackForHarness
                // bypasses that gate itself, but the sign is still written
                // correctly so this scenario reproduces a real behind-the-back
                // rather than an input the game could never produce.
                float flickSign = HandStateResolver.EmptyHandSign(holder.HandSide);
                bool began = holder.BeginBehindTheBackForHarness(flickSign);
                if (!began)
                {
                    Fail($"{_scenario}: BeginBehindTheBackForHarness returned false " +
                         "— machine was not Inactive or the dead-dribble gate refused it.");
                    Finish();
                    return;
                }
                GD.Print($"[behindtheback-anim] BehindTheBack begun on holder={_holderId} " +
                         $"(startHand={holder.HandSide}).");
                _step = Step.Observing;
                break;

            case Step.Observing:
                Observe();
                bool isProbeScenario = _scenario is "no-unsuffixed-btb-state" or "control-unsuffixed-probe";
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
                 $"lastAnimNode={HolderNode()?.ActiveAnimNodeForHarness}, sawStartup={_sawStartup}, " +
                 $"sawActive={_sawActive}, sawRecovery={_sawRecovery}, sawActivePhase={_sawActivePhase}, " +
                 $"distinctBtbStates=[{string.Join(",", _distinctBtbStates)}], " +
                 $"sawProbeTargetNode={_sawProbeTargetNode}.");
            Finish();
        }
    }

    private PlayerController HolderNode() => _holderId == 1 ? _p1 : _p2;

    private void Observe()
    {
        PlayerController holder = HolderNode();
        MovePhase phase = holder.PhaseForHarness;
        string node = holder.ActiveAnimNodeForHarness;

        if (phase == MovePhase.Active) _sawActivePhase = true;

        switch (_scenario)
        {
            case "btb-left-origin":
                if (!_sawStartup && node == "BehindTheBackStartupLeft") _sawStartup = true;
                if (_sawStartup && !_sawActive && node == "BehindTheBackActiveLeft") _sawActive = true;
                if (_sawActive && !_sawRecovery && node == "BehindTheBackRecoveryLeft") _sawRecovery = true;
                break;

            case "btb-right-origin":
                if (!_sawStartup && node == "BehindTheBackStartupRight") _sawStartup = true;
                if (_sawStartup && !_sawActive && node == "BehindTheBackActiveRight") _sawActive = true;
                if (_sawActive && !_sawRecovery && node == "BehindTheBackRecoveryRight") _sawRecovery = true;
                break;

            case "btb-single-polarity":
                if (node.StartsWith("BehindTheBack"))
                {
                    _sawAnyBtbState = true;
                    _distinctBtbStates.Add(node);
                }
                break;

            case "btb-poses-the-skeleton":
                // Latched at event time, on every tick a behind-the-back state
                // is the ACTIVE node -- sampling afterwards would read whatever
                // the tree settled back into.
                if (node.StartsWith("BehindTheBack"))
                {
                    _sawAnyBtbState = true;
                    // OVERWRITE, not Max: the verdict wants the departure on
                    // the LAST behind-the-back tick, not the largest seen. See
                    // UpperBodyDepartureFromRest for why the max is the one
                    // number here that cannot discriminate.
                    _worstPosedDeg = UpperBodyDepartureFromRest(holder);
                }
                break;

            case "no-unsuffixed-btb-state":
                if (node == "BehindTheBackActive") _sawProbeTargetNode = true;
                break;

            case "control-unsuffixed-probe":
                if (node == "BehindTheBackActiveLeft") _sawProbeTargetNode = true;
                break;
        }

        if (_sawActivePhase && phase == MovePhase.Inactive && _returnedInactiveFrame < 0)
            _returnedInactiveFrame = _frame;
    }

    private void RenderVerdict()
    {
        switch (_scenario)
        {
            case "btb-left-origin":          VerdictOrigin("Left"); break;
            case "btb-right-origin":         VerdictOrigin("Right"); break;
            case "btb-single-polarity":      VerdictSinglePolarity(); break;
            case "btb-poses-the-skeleton":   VerdictPosesTheSkeleton(); break;
            case "no-unsuffixed-btb-state":  VerdictProbeUnsuffixed(); break;
            case "control-unsuffixed-probe": VerdictProbeControl(); break;
        }
    }

    // ── Scenarios: btb-left-origin / btb-right-origin (positive) ───────────
    private void VerdictOrigin(string suffix)
    {
        bool pass = _sawStartup && _sawActive && _sawRecovery;
        if (pass)
            GD.Print($"[behindtheback-anim] PASS btb-{suffix.ToLowerInvariant()}-origin — the tree was " +
                     $"observed on \"BehindTheBackStartup{suffix}\", then \"BehindTheBackActive{suffix}\", then " +
                     $"\"BehindTheBackRecovery{suffix}\", in that order.");
        else
            Fail($"btb-{suffix.ToLowerInvariant()}-origin: expected BehindTheBackStartup{suffix} -> " +
                 $"BehindTheBackActive{suffix} -> BehindTheBackRecovery{suffix}, in order; got sawStartup={_sawStartup}, " +
                 $"sawActive={_sawActive}, sawRecovery={_sawRecovery}, " +
                 $"lastAnimNode={HolderNode().ActiveAnimNodeForHarness}.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: btb-single-polarity (load-bearing) ────────────────────────
    private void VerdictSinglePolarity()
    {
        // Premise check, in two parts. "All observed suffixes agree" is
        // vacuously true over an EMPTY set — but it is also nearly vacuous over
        // a set of ONE, and one is exactly what a run that reached Startup and
        // then stalled would produce. Since the whole point of this scenario is
        // that the suffix survives the mid-move HandSide flip at Active-entry,
        // a run that never got past Startup has not tested the flip at all and
        // must not be allowed to report success. So require all THREE phases
        // observed, then require their suffixes to agree.
        var suffixes = _distinctBtbStates.Select(SuffixOf).Distinct().ToList();
        var phases = _distinctBtbStates.Select(PhaseOf).Distinct().ToList();
        bool sawWholeArc = phases.Count == 3;
        bool pass = _sawAnyBtbState && sawWholeArc && suffixes.Count == 1;

        if (pass)
            GD.Print($"[behindtheback-anim] PASS btb-single-polarity — every distinct behind-the-back state " +
                     $"observed ([{string.Join(",", _distinctBtbStates)}]) carried the SAME hand suffix " +
                     $"(\"{suffixes[0]}\"), across the whole Startup->Active->Recovery arc.");
        else
            Fail($"btb-single-polarity: expected all three phases observed and exactly one suffix across " +
                 $"every observed behind-the-back state; got sawAnyBtbState={_sawAnyBtbState}, " +
                 $"sawWholeArc={sawWholeArc}, distinctStates=[{string.Join(",", _distinctBtbStates)}], " +
                 $"distinctPhases=[{string.Join(",", phases)}], " +
                 $"distinctSuffixes=[{string.Join(",", suffixes)}]. If the premise broke, this proves nothing, " +
                 "so this fails rather than passes.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: btb-poses-the-skeleton ────────────────────────────────────
    // The gap every other scenario in this file leaves open. All seven of them
    // passed green while all six clips were built with track paths of the form
    // "Armature/Skeleton3D:mixamorig_Hips" -- one level deeper than
    // scenes/Player.tscn's rig, whose skeleton sits at "Skeleton3D". Every
    // track bound to nothing, so the clips were silent no-ops: the state
    // machine still entered the right state, the durations still matched, the
    // state->clip mapping was still correct, and the mesh never moved. Godot
    // logs "couldn't resolve track" and carries on; it does not fail.
    //
    // Reachability, duration and mapping are all upstream of the thing that
    // actually matters, which is that the clip MOVES THE RIG. This reads the
    // live Skeleton3D and asserts an upper-body bone departs from its rest
    // rotation. It also happens to cover the a45bd1d trap from the other side:
    // a clip that omitted the arm tracks entirely would leave them AT rest,
    // which is exactly what this refuses to accept.
    private void VerdictPosesTheSkeleton()
    {
        bool pass = _sawAnyBtbState && _worstPosedDeg >= PosedMinDeg;
        if (pass)
            GD.Print($"[behindtheback-anim] PASS btb-poses-the-skeleton — the upper body was still posed {_worstPosedDeg:F2} deg off rest " +
                     $"(floor {PosedMinDeg:F1}) on the LAST behind-the-back tick, so the clip's tracks bind to this " +
                     "rig and hold it — rather than collapsing it to rest, which is what an unbound clip does.");
        else
            Fail($"btb-poses-the-skeleton: the clip did not move the rig. sawAnyBtbState={_sawAnyBtbState}, " +
                 $"upperBodyDepartureFromRestOnLastBtbTick={_worstPosedDeg:F4} deg (need >= {PosedMinDeg:F1}). " +
                 "Most likely the clips' track NODE PATHS do not bind on scenes/Player.tscn (check for an " +
                 "'Armature/' prefix — Blender's FBX export adds an Armature object wrapper the rig does not " +
                 "have), or the clip omits the arm tracks entirely and they are sitting at rest.");
        Finish(pass ? 0 : 1);
    }

    // Upper-body bone rotations off the holder's live Skeleton3D, this tick.
    //
    // Compared against a BASELINE taken on the first behind-the-back tick, not
    // against the bones' REST. Departure-from-rest was tried first and is NOT a
    // discriminating measure here: with the clips deliberately corrupted to the
    // unbindable "Armature/..." paths, the upper body still measured 110 deg
    // from rest (vs 179 working), because the Y Bot's rest is a T-pose and the
    // arms are held away from it by other machinery regardless of whether this
    // clip contributes anything. Both numbers are large, so the gate could not
    // tell inert from working.
    //
    // Pose CHANGE across the move is the honest question: a clip that drives
    // the rig moves the upper body between Startup and Recovery (the Blender
    // side authored 62 deg of travel), while an inert clip leaves it frozen at
    // whatever it was already showing.
    // Largest upper-body departure from REST, sampled on the LAST tick a
    // behind-the-back state was active. Both halves of that sentence are
    // load-bearing, and both were arrived at by mutation rather than by taste
    // — two earlier metrics were tried against clips deliberately corrupted to
    // the unbindable "Armature/..." paths, and BOTH passed on them:
    //
    //   max departure from rest   inert 110 deg vs working 179 deg — the Y Bot
    //                             rest is a T-pose and the arms are held off it
    //                             by other machinery, so "far from rest" is
    //                             true either way.
    //   max change across the arc inert 110 deg vs working 179 deg — because a
    //                             clip whose tracks bind to nothing makes the
    //                             bones COLLAPSE to rest, and that collapse is
    //                             itself a large change. "It moved" does not
    //                             imply "this clip moved it".
    //
    // What actually separates the two is WHERE the pose ends up. A bound clip
    // holds the upper body posed all the way through Recovery; an unbound one
    // has fully collapsed to rest within a tick of entry (there is no xfade on
    // any edge, so the collapse is immediate) and stays there. Sampling the
    // final behind-the-back tick therefore reads ~0 deg when inert and ~179 deg
    // when driving — a real separation rather than two large numbers.
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

    // The phase half of a "BehindTheBack<Phase><Suffix>" node name, used only
    // to prove the whole Startup->Active->Recovery arc was actually walked.
    private static string PhaseOf(string node) =>
        node.Contains("Startup") ? "Startup" :
        node.Contains("Active") ? "Active" :
        node.Contains("Recovery") ? "Recovery" :
        "None";

    // ── Scenario: no-unsuffixed-btb-state (control) ─────────────────────────
    // Direct probe, not a statistical observation over a real move: see the
    // header's "Why two probe scenarios" for why this differs from
    // CrossoverAnimTest's approach. The premise this depends on —
    // that a Travel() call under these identical conditions CAN succeed at
    // all — is proven by the companion scenario below, not re-derived here.
    private void VerdictProbeUnsuffixed()
    {
        bool pass = !_sawProbeTargetNode;
        if (pass)
            GD.Print("[behindtheback-anim] PASS no-unsuffixed-btb-state — Travel(\"BehindTheBackActive\") " +
                     $"never reached that node across {ProbeSettleFrames} ticks; the state machine has no " +
                     "such node to travel to (the three unsuffixed states were deleted per #281). See " +
                     "control-unsuffixed-probe for the premise proof that Travel() itself works under these " +
                     "identical conditions.");
        else
            Fail("no-unsuffixed-btb-state: Travel(\"BehindTheBackActive\") reached a node reporting that " +
                 "exact name — the unsuffixed state still exists in scenes/Player.tscn and must be deleted " +
                 "(#281's premise: HandSide is two-valued and OriginHand is total over it, so no unsuffixed " +
                 "fallback should exist).");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: control-unsuffixed-probe (premise control) ────────────────
    private void VerdictProbeControl()
    {
        bool pass = _sawProbeTargetNode;
        if (pass)
            GD.Print("[behindtheback-anim] PASS control-unsuffixed-probe — Travel(\"BehindTheBackActiveLeft\") " +
                     $"reached that node within {ProbeSettleFrames} ticks, under the SAME setup " +
                     "no-unsuffixed-btb-state uses. This proves the Travel()-and-observe mechanism itself is " +
                     "sound, which is the premise no-unsuffixed-btb-state's pass depends on.");
        else
            Fail($"control-unsuffixed-probe: Travel(\"BehindTheBackActiveLeft\") never reached that node " +
                 $"within {ProbeSettleFrames} ticks — either the state is missing/misnamed in scenes/Player.tscn, " +
                 "or the Dribble->BehindTheBackStartupLeft->BehindTheBackActiveLeft edge chain is broken, or the " +
                 "probe mechanism itself is broken — in which case no-unsuffixed-btb-state's pass would prove " +
                 "nothing.");
        Finish(pass ? 0 : 1);
    }

    // ── Static scenarios: no live tree, pure resource/scene inspection ──────
    private void RunStaticCheck()
    {
        switch (_scenario)
        {
            case "btb-segment-lengths":     RunSegmentLengthsCheck(); break;
            case "btb-no-placeholder-leak": RunNoPlaceholderLeakCheck(); break;
        }
    }

    // ── Scenario: btb-segment-lengths ────────────────────────────────────────
    // Same instrument as LocomotionClipTest's jumpshot/crossover segment-length
    // families (reproduced here — this issue does not own that file): read the
    // move's real tick windows from BehindTheBack.DefaultFrameData (not
    // hardcoded), so a future #238 retune that forgets to re-run
    // tools/rebuild_behindtheback_clips.gd goes red here and names the tool.
    // Tolerance is ONE TICK (1/60 s) per the brief — looser than
    // LocomotionClipTest's ~1e-4s "should be exact" tolerance for an
    // already-built asset, because a clip built to a slightly different frame
    // count than the exact ideal but still within a tick's worth of the
    // window is not a false read an opponent could perceive.
    private void RunSegmentLengthsCheck()
    {
        var lib = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        if (lib == null)
        {
            Fail("assets/locomotion.res failed to load — cannot evaluate btb-segment-lengths.");
            Finish(1);
            return;
        }

        double tps = Engine.PhysicsTicksPerSecond;
        var frames = BehindTheBack.DefaultFrameData;
        const double ToleranceSeconds = 1.0 / 60.0 + 1e-6; // "within one tick" (brief), tiny float-noise margin

        (string Clip, int Ticks)[] windows =
        {
            ("behindthebackstartupleft",   frames.StartupFrames),
            ("behindthebackactiveleft",    frames.ActiveFrames),
            ("behindthebackrecoveryleft",  frames.RecoveryFrames),
            ("behindthebackstartupright",  frames.StartupFrames),
            ("behindthebackactiveright",   frames.ActiveFrames),
            ("behindthebackrecoveryright", frames.RecoveryFrames),
        };

        bool pass = true;
        foreach (var (clipName, ticks) in windows)
        {
            if (!lib.HasAnimation(clipName))
            {
                Fail($"AnimationLibrary has no clip '{clipName}' — run tools/rebuild_behindtheback_clips.gd.");
                pass = false;
                continue;
            }

            double expectedSeconds = ticks / tps;
            double actualSeconds = lib.GetAnimation(clipName).Length;
            double deviationSeconds = Math.Abs(actualSeconds - expectedSeconds);
            GD.Print($"[behindtheback-anim]   '{clipName}': length={actualSeconds:F6}s " +
                     $"expected={expectedSeconds:F6}s ({ticks} ticks @ {tps} tps), deviation={deviationSeconds:F6}s");

            if (deviationSeconds > ToleranceSeconds)
            {
                Fail($"clip '{clipName}' is {actualSeconds:F6}s, expected {expectedSeconds:F6}s " +
                     $"({ticks} ticks at {tps} tps — BehindTheBack.DefaultFrameData), a deviation of " +
                     $"{deviationSeconds:F6}s exceeds the one-tick tolerance ({ToleranceSeconds:F6}s). Re-run " +
                     "tools/rebuild_behindtheback_clips.gd after retuning the move's frame data.");
                pass = false;
            }
        }

        if (pass)
            GD.Print("[behindtheback-anim] PASS btb-segment-lengths — all six clips' durations are within " +
                     "one tick of BehindTheBack.DefaultFrameData's Startup/Active/Recovery tick windows.");
        else
            GD.PrintErr("[behindtheback-anim] FAIL btb-segment-lengths — see per-clip deviations above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: btb-no-placeholder-leak ────────────────────────────────────
    // The direct statement that #296 is closed for this move: before #281,
    // all three (unsuffixed) BehindTheBack states pointed at
    // SubResource("AnimationNodeAnimation_mv277ph") = locomotion/idle. Reads
    // the AnimationNodeStateMachine resource directly off scenes/Player.tscn's
    // SceneState — same instrument LocomotionClipTest's crossover family (e)
    // uses to catch a copy-pasted SubResource id, which a state-NAME-only
    // assertion (btb-left-origin etc.) cannot see: GetCurrentNode() would
    // still report "BehindTheBackActiveLeft" even if that state's own
    // AnimationNodeAnimation resource pointed at the wrong clip.
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

        // An ALLOWLIST, not a placeholder blocklist. A blocklist ("is it
        // locomotion/idle or locomotion/run?") closes #296 but leaves the more
        // likely defect wide open: these six AnimationNodeAnimation
        // sub-resources were hand-authored directly beneath the six crossover
        // ones in scenes/Player.tscn, so the realistic slip is a state pointing
        // at locomotion/crossoveractiveleft — a real, non-placeholder clip that
        // a blocklist waves through and that GetCurrentNode() cannot see either
        // (the STATE name would still read "BehindTheBackActiveLeft"). Pinning
        // the exact expected clip per state subsumes the placeholder check and
        // catches the copy-paste too.
        (string State, string Clip)[] states =
        {
            ("BehindTheBackStartupLeft",   "locomotion/behindthebackstartupleft"),
            ("BehindTheBackActiveLeft",    "locomotion/behindthebackactiveleft"),
            ("BehindTheBackRecoveryLeft",  "locomotion/behindthebackrecoveryleft"),
            ("BehindTheBackStartupRight",  "locomotion/behindthebackstartupright"),
            ("BehindTheBackActiveRight",   "locomotion/behindthebackactiveright"),
            ("BehindTheBackRecoveryRight", "locomotion/behindthebackrecoveryright"),
        };
        string[] placeholderClips = { "locomotion/idle", "locomotion/run" };

        bool pass = true;
        foreach (var (stateName, expectedClip) in states)
        {
            if (!stateMachine.HasNode(stateName))
            {
                Fail($"scenes/Player.tscn's state machine has no state '{stateName}' — cannot evaluate " +
                     "btb-no-placeholder-leak for it.");
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
            GD.Print($"[behindtheback-anim]   state '{stateName}' -> clip '{clip}' (expected '{expectedClip}')");
            if (placeholderClips.Contains(clip))
            {
                Fail($"state '{stateName}' still points at the placeholder clip '{clip}' — #296 is not " +
                     "closed for this state. Re-point it at its own locomotion/behindtheback... clip.");
                pass = false;
            }
            else if (clip != expectedClip)
            {
                Fail($"state '{stateName}' points at '{clip}', not its own '{expectedClip}'. The clip is real, " +
                     "so this is not #296 — it is a mis-wired sub-resource (most likely a copy-paste off the " +
                     "adjacent crossover block). The state name alone reads correct, so no reachability " +
                     "assertion can catch this.");
                pass = false;
            }
        }

        if (pass)
            GD.Print("[behindtheback-anim] PASS btb-no-placeholder-leak — none of the six behind-the-back " +
                     "states points at locomotion/idle or locomotion/run (#296 closed for this move).");
        else
            GD.PrintErr("[behindtheback-anim] FAIL btb-no-placeholder-leak — see per-state clips above.");

        Finish(pass ? 0 : 1);
    }

    private void Fail(string message) => GD.PrintErr($"[behindtheback-anim] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[behindtheback-anim] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
