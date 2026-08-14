using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #309 (ADR-0016): proves the SIX
// hand-side between-the-legs states scenes/Player.tscn now carries —
// BetweenTheLegs{Startup,Active,Recovery}{Left,Right} — are actually ENTERED
// end-to-end through the real AnimationTree, that the SUFFIX stays constant
// across a single between-the-legs' whole Startup->Active->Recovery arc even
// though PlayerController.HandSide itself flips mid-move, that no unsuffixed
// state exists, that each clip's duration matches BetweenTheLegs.DefaultFrameData's
// tick windows, that none of the six states still points at the #296
// placeholder, that the clips physically drive the rig, and — #296's actual
// complaint — that Startup and Recovery are not the same thing to look at.
//
// Structurally this is #281's shipped BehindTheBackAnimTest, the closest
// precedent: between-the-legs is the third HANDED dribble-family move, so it
// has the same six-state shape, the same mid-move swap at Active-entry, and the
// same #193 dead-dribble begin gate. Where this file diverges from that one is
// the startup-vs-recovery pair, which behind-the-back did not carry.
//
//   godot --headless --path . res://tests/integration/BetweenTheLegsAnimTest.tscn -- --harness-scenario=betweenthelegs-phases
//   godot --headless --path . res://tests/integration/BetweenTheLegsAnimTest.tscn -- --harness-scenario=btl-right-origin
//   godot --headless --path . res://tests/integration/BetweenTheLegsAnimTest.tscn -- --harness-scenario=btl-single-polarity
//   godot --headless --path . res://tests/integration/BetweenTheLegsAnimTest.tscn -- --harness-scenario=no-unsuffixed-btl-state
//   godot --headless --path . res://tests/integration/BetweenTheLegsAnimTest.tscn -- --harness-scenario=control-unsuffixed-probe
//   godot --headless --path . res://tests/integration/BetweenTheLegsAnimTest.tscn -- --harness-scenario=betweenthelegs-segment-lengths
//   godot --headless --path . res://tests/integration/BetweenTheLegsAnimTest.tscn -- --harness-scenario=betweenthelegs-no-placeholder-leak
//   godot --headless --path . res://tests/integration/BetweenTheLegsAnimTest.tscn -- --harness-scenario=betweenthelegs-startup-differs-from-recovery
//   godot --headless --path . res://tests/integration/BetweenTheLegsAnimTest.tscn -- --harness-scenario=control-identical-clips-read-as-identical
//   godot --headless --path . res://tests/integration/BetweenTheLegsAnimTest.tscn -- --harness-scenario=btl-poses-the-skeleton
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "betweenthelegs-phases".
//
// ── File name ────────────────────────────────────────────────────────────────
// #309's acceptance list spells this "BetweenthelegsAnimTest.cs", which is the
// move's ID string ("betweenthelegs") with one capital rather than the type
// name. Every other file in this folder — including this move's own
// BetweenTheLegsTest.cs and BetweenTheLegsHarnessSeam.cs — uses the C# type
// spelling, so this follows those. The scenario NAMES the issue specifies are
// reproduced verbatim; only the filename differs.
//
// ── Why ActiveAnimNodeForHarness, not the resolver's own decision (#257) ────
// Same discipline as every other anim harness in this repo: reading
// AnimationNodeStateMachinePlayback.GetCurrentNode() (via the live
// AnimationTree) asserts what the state machine ACTUALLY did, never what
// MoveAnimResolver.ResolveStateName merely DECIDED — calling that directly
// would keep passing on a Player.tscn with none of the six handed states
// wired at all. Travel() to a missing/misnamed state only LOGS (#257).
//
// ── Why a real scenes/Player.tscn instance ───────────────────────────────
// A live AnimationTree only exists on the REAL scenes/Player.tscn. Between-the-
// legs is a dribble-family committed move: BeginCommittedMove's Held-holder
// dead-dribble gate (#193) refuses it outright unless the ball is genuinely
// Dribbling, which is why every live scenario below runs TryStartDribble first.
//
// ── Why btl-single-polarity does NOT re-derive expected names ───────────────
// Same reasoning as behindtheback-single-polarity: it does not ask "does
// MoveAnimResolver think this should be BetweenTheLegsActiveLeft" — that would
// re-run the resolver's own (already unit-tested) formula against itself. It
// collects whatever DISTINCT "BetweenTheLegs*" node names the LIVE tree
// reported over one full move and asserts they all share the same trailing
// suffix. This is the scenario that would catch the specific defect
// HandedMoves exists to prevent: betweenthelegs swaps the ball hand at
// Active-entry, so a resolver reading live HandSide per tick would emit
// "BetweenTheLegsStartupLeft" then "BetweenTheLegsActiveRight" in the same run
// and Recovery would play the mirrored clip.
//
// ── Why two probe scenarios ────────────────────────────────────────────────
// Lifted wholesale from BehindTheBackAnimTest, including its reasoning. The
// two share IDENTICAL setup and differ only in the Travel() target:
//   - "no-unsuffixed-btl-state" travels to "BetweenTheLegsActive" — a node that
//     must not exist (this move was never wired with unsuffixed states, so this
//     asserts the #309 wiring did not accidentally add one as a fallback).
//     GetCurrentNode() can never report a name that names no node.
//   - "control-unsuffixed-probe" travels to "BetweenTheLegsActiveLeft" — a node
//     that DOES exist and IS reachable from DribbleLeft/DribbleRight via the
//     edge chain #309 adds. This is the PREMISE for the scenario above: without
//     it, "never reached BetweenTheLegsActive" would pass just as happily on a
//     completely broken probe mechanism.
// This does NOT attempt an edge-level assertion — #279 mutation-proved that
// unreliable, because Travel() is a pathfinder that routes around a missing
// EDGE. A missing NODE is a different failure shape: there is no path to route
// around when the destination does not exist.
//
// ── What this harness CANNOT prove ───────────────────────────────────────
// Whether the clip LOOKS right — knees genuinely apart, hands genuinely between
// them, ball reading at ~0.30 m off the floor — is #173's deferred human feel
// judgment (ADR-0021). The GEOMETRIC content gates (stance widening, wrists
// inside the narrower knee, the handedness oracle) live in
// tools/rebuild_betweenthelegs_clips.gd, which is the tool of record for
// regenerating these clips and re-proves them on every regeneration. This
// harness asserts state-machine reachability, suffix consistency, clip
// duration, the state->clip mapping, that the clips drive the rig, and that
// Startup and Recovery are distinguishable — never whether the pose is good.
public partial class BetweenTheLegsAnimTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int ArmFrames = 2;           // ticks for TryAssignTipoffHolder to run
    private const int ActionMarginFrames = 3;  // ticks to let TryStartDribble's effect settle
    private const int SettleMarginFrames = 5;  // ticks after returning to Inactive before the final read
    private const int ProbeSettleFrames = 40;  // ticks given to a direct Travel() probe to land (2/3 s @ 60 Hz)

    private static readonly string[] KnownScenarios =
    {
        "betweenthelegs-phases", "btl-right-origin", "btl-single-polarity",
        "no-unsuffixed-btl-state", "control-unsuffixed-probe",
        "betweenthelegs-segment-lengths", "betweenthelegs-no-placeholder-leak",
        "betweenthelegs-startup-differs-from-recovery",
        "control-identical-clips-read-as-identical",
        "btl-poses-the-skeleton",
    };

    // The scenarios that need no tipoff/dribble/move setup at all — pure
    // resource/scene inspection, run once and finished.
    private static readonly string[] StaticScenarios =
    {
        "betweenthelegs-segment-lengths", "betweenthelegs-no-placeholder-leak",
        "betweenthelegs-startup-differs-from-recovery",
        "control-identical-clips-read-as-identical",
    };

    // Upper-body bones the move must visibly drive. Deliberately NOT the legs:
    // the source dribble's own crouch already moves those, so a leg-only check
    // would pass on a clip that never touched the arms — and the arms are
    // where this move's entire handedness lives (the stance is symmetric by
    // design, since "knees APART" is the read).
    private static readonly string[] UpperBodyBones =
    {
        "mixamorig_LeftArm", "mixamorig_RightArm",
        "mixamorig_LeftForeArm", "mixamorig_RightForeArm",
        "mixamorig_Spine",
    };

    // Degrees the upper body must still sit off REST on the final
    // between-the-legs tick. Same instrument and same reasoning as
    // BehindTheBackAnimTest's PosedMinDeg, which arrived at this bar by
    // mutation rather than taste: an UNBOUND clip (the "Armature/" prefix trap,
    // #313) reads ~0 deg there because the bones have collapsed to rest, while
    // a bound one reads well over 100. 30 sits in the empty middle. It pins no
    // particular pose — which pose is right is #173's call.
    private const float PosedMinDeg = 30.0f;

    private string _scenario = "betweenthelegs-phases";

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

    // ── Latches for betweenthelegs-phases / btl-right-origin ────────────────
    // Chained: each guard requires the previous one already latched, so "saw
    // all three" IS "saw them in order."
    private bool _sawStartup;
    private bool _sawActive;
    private bool _sawRecovery;

    // ── Observations for btl-single-polarity ────────────────────────────────
    // Every DISTINCT "BetweenTheLegs*" node name the live tree reported over
    // the whole run — collected at event time (every physics tick), not
    // sampled once at the end.
    private readonly HashSet<string> _distinctBtlStates = new();
    private bool _sawAnyBtlState;

    // ── Latch for no-unsuffixed-btl-state / control-unsuffixed-probe ────────
    // Shared field: only one scenario runs per process invocation, so no
    // cross-talk. Set at event time the tick the probed node is observed.
    private bool _sawProbeTargetNode;

    // ── Observation for btl-poses-the-skeleton ──────────────────────────────
    private float _posedDegOnLastBtlTick;

    // Gate for "the move genuinely ran" (real-move scenarios only): only once
    // the Active phase has actually been observed does a later return to
    // Inactive count as "the lifecycle finished."
    private bool _sawActivePhase;
    private int _returnedInactiveFrame = -1;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "betweenthelegs-phases");
        GD.Print($"[betweenthelegs-anim] scenario={_scenario} booting headless…");

        if (!KnownScenarios.Contains(_scenario))
        {
            Fail($"unknown scenario '{_scenario}'.");
            Finish();
            return;
        }

        // Real Player.tscn instances (live AnimationTree), named "1"/"2" so the
        // OfflineMultiplayerPeer makes unique_id 1 both IsServer and
        // IsLocalPlayer (the full TickServerOwnPlayer -> ApplyAnimation chain
        // runs every tick).
        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        _p1 = scene.Instantiate<PlayerController>();
        _p1.Name = "1";
        _p2 = scene.Instantiate<PlayerController>();
        _p2.Name = "2";

        // Physics-callback lockstep so GetCurrentNode() reflects the same-tick
        // Travel() (the default Idle callback lags under --headless — trap
        // #6/README). Unused by the static scenarios but harmless to set.
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
                         $"(BetweenTheLegs cannot Begin from Held, #193); got state={_ball.State}.");
                    Finish();
                    return;
                }

                var holder = HolderNode();

                if (_scenario == "betweenthelegs-phases")
                    holder.SetHandSideForHarness(HandSide.Left);
                else if (_scenario == "btl-right-origin")
                    holder.SetHandSideForHarness(HandSide.Right);

                if (_scenario is "no-unsuffixed-btl-state" or "control-unsuffixed-probe")
                {
                    // Direct AnimationTree access — the same "parameters/playback"
                    // path PlayerController's own _Ready resolves internally,
                    // reached here via ordinary Godot node/property API.
                    var tree = holder.GetNode<AnimationTree>("AnimationTree");
                    var playback = tree.Get("parameters/playback").As<AnimationNodeStateMachinePlayback>();
                    if (playback == null)
                    {
                        Fail($"{_scenario}: could not resolve 'parameters/playback' off the holder's AnimationTree.");
                        Finish();
                        return;
                    }
                    string target = _scenario == "no-unsuffixed-btl-state"
                        ? "BetweenTheLegsActive"
                        : "BetweenTheLegsActiveLeft";
                    GD.Print($"[betweenthelegs-anim] issuing Travel(\"{target}\") directly on holder={_holderId}, " +
                             $"current node before call = \"{playback.GetCurrentNode()}\".");
                    playback.Travel(target);
                    _step = Step.Observing;
                    _stepDeadlineFrame = _frame + ProbeSettleFrames;
                    break;
                }

                // The flick sign must point at the EMPTY hand, or this would not
                // be a genuine between-the-legs in the live input path:
                // HandStateResolver classifies a flick toward the empty hand as
                // the crossover family, and BetweenTheLegs is that same branch
                // taken with the move_finesse modifier held (see
                // BetweenTheLegsHarnessSeam's doc — headless has no modifier
                // hardware, which is why the seam exists at all).
                // BeginBetweenTheLegsForHarness bypasses the classifier itself,
                // but the sign is still written correctly so this scenario
                // reproduces a real between-the-legs rather than an input the
                // game could never produce.
                float flickSign = HandStateResolver.EmptyHandSign(holder.HandSide);
                bool began = holder.BeginBetweenTheLegsForHarness(flickSign);
                if (!began)
                {
                    Fail($"{_scenario}: BeginBetweenTheLegsForHarness returned false " +
                         "— machine was not Inactive or the dead-dribble gate refused it.");
                    Finish();
                    return;
                }
                GD.Print($"[betweenthelegs-anim] BetweenTheLegs begun on holder={_holderId} " +
                         $"(startHand={holder.HandSide}, flickSign={flickSign}).");
                _step = Step.Observing;
                break;

            case Step.Observing:
                Observe();
                bool isProbeScenario = _scenario is "no-unsuffixed-btl-state" or "control-unsuffixed-probe";
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
                 $"distinctBtlStates=[{string.Join(",", _distinctBtlStates)}], " +
                 $"sawProbeTargetNode={_sawProbeTargetNode}.");
            Finish();
        }
    }

    // null until the tipoff actually assigns a holder. The obvious
    // `_holderId == 1 ? _p1 : _p2` silently returns PLAYER 2 while _holderId is
    // still 0, which matters in exactly one place and it is the worst place: the
    // timeout diagnostic. A run that times out in Step.AwaitTipoff is a run
    // where no holder was ever assigned, and that message would have printed
    // player 2's anim node labelled "lastAnimNode", pointing whoever debugs it
    // at the wrong node. The `?.` at the timeout site is what makes this
    // readable — it prints empty rather than a confident wrong answer.
    private PlayerController HolderNode() =>
        _holderId == 0 ? null : (_holderId == 1 ? _p1 : _p2);

    private void Observe()
    {
        PlayerController holder = HolderNode();
        MovePhase phase = holder.PhaseForHarness;
        string node = holder.ActiveAnimNodeForHarness;

        if (phase == MovePhase.Active) _sawActivePhase = true;

        switch (_scenario)
        {
            case "betweenthelegs-phases":
                if (!_sawStartup && node == "BetweenTheLegsStartupLeft") _sawStartup = true;
                if (_sawStartup && !_sawActive && node == "BetweenTheLegsActiveLeft") _sawActive = true;
                if (_sawActive && !_sawRecovery && node == "BetweenTheLegsRecoveryLeft") _sawRecovery = true;
                break;

            case "btl-right-origin":
                if (!_sawStartup && node == "BetweenTheLegsStartupRight") _sawStartup = true;
                if (_sawStartup && !_sawActive && node == "BetweenTheLegsActiveRight") _sawActive = true;
                if (_sawActive && !_sawRecovery && node == "BetweenTheLegsRecoveryRight") _sawRecovery = true;
                break;

            case "btl-single-polarity":
                if (node.StartsWith("BetweenTheLegs"))
                {
                    _sawAnyBtlState = true;
                    _distinctBtlStates.Add(node);
                }
                break;

            case "btl-poses-the-skeleton":
                // Latched at event time, on every tick a between-the-legs state
                // is the ACTIVE node — sampling afterwards would read whatever
                // the tree settled back into. OVERWRITE, not Max: the verdict
                // wants the departure on the LAST such tick. See the verdict's
                // comment for why the max is the one number here that cannot
                // discriminate.
                if (node.StartsWith("BetweenTheLegs"))
                {
                    _sawAnyBtlState = true;
                    _posedDegOnLastBtlTick = UpperBodyDepartureFromRest(holder);
                }
                break;

            case "no-unsuffixed-btl-state":
                if (node == "BetweenTheLegsActive") _sawProbeTargetNode = true;
                break;

            case "control-unsuffixed-probe":
                if (node == "BetweenTheLegsActiveLeft") _sawProbeTargetNode = true;
                break;
        }

        if (_sawActivePhase && phase == MovePhase.Inactive && _returnedInactiveFrame < 0)
            _returnedInactiveFrame = _frame;
    }

    private void RenderVerdict()
    {
        switch (_scenario)
        {
            case "betweenthelegs-phases":    VerdictOrigin("Left", "betweenthelegs-phases"); break;
            case "btl-right-origin":         VerdictOrigin("Right", "btl-right-origin"); break;
            case "btl-single-polarity":      VerdictSinglePolarity(); break;
            case "btl-poses-the-skeleton":   VerdictPosesTheSkeleton(); break;
            case "no-unsuffixed-btl-state":  VerdictProbeUnsuffixed(); break;
            case "control-unsuffixed-probe": VerdictProbeControl(); break;
        }
    }

    // ── Scenarios: betweenthelegs-phases / btl-right-origin (positive) ──────
    private void VerdictOrigin(string suffix, string label)
    {
        bool pass = _sawStartup && _sawActive && _sawRecovery;
        if (pass)
            GD.Print($"[betweenthelegs-anim] PASS {label} — the tree was observed on " +
                     $"\"BetweenTheLegsStartup{suffix}\", then \"BetweenTheLegsActive{suffix}\", then " +
                     $"\"BetweenTheLegsRecovery{suffix}\", in that order.");
        else
            Fail($"{label}: expected BetweenTheLegsStartup{suffix} -> BetweenTheLegsActive{suffix} -> " +
                 $"BetweenTheLegsRecovery{suffix}, in order; got sawStartup={_sawStartup}, " +
                 $"sawActive={_sawActive}, sawRecovery={_sawRecovery}, " +
                 $"lastAnimNode={HolderNode().ActiveAnimNodeForHarness}.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: btl-single-polarity (load-bearing) ────────────────────────
    private void VerdictSinglePolarity()
    {
        // Premise check, in two parts. "All observed suffixes agree" is
        // vacuously true over an EMPTY set — and nearly vacuous over a set of
        // ONE, which is exactly what a run that reached Startup and then
        // stalled would produce. Since the whole point of this scenario is that
        // the suffix survives the mid-move HandSide flip at Active-entry, a run
        // that never got past Startup has not tested the flip at all and must
        // not report success. So require all THREE phases observed, then
        // require their suffixes to agree.
        var suffixes = _distinctBtlStates.Select(SuffixOf).Distinct().ToList();
        var phases = _distinctBtlStates.Select(PhaseOf).Distinct().ToList();
        bool sawWholeArc = phases.Count == 3;
        bool pass = _sawAnyBtlState && sawWholeArc && suffixes.Count == 1;

        if (pass)
            GD.Print($"[betweenthelegs-anim] PASS btl-single-polarity — every distinct between-the-legs state " +
                     $"observed ([{string.Join(",", _distinctBtlStates)}]) carried the SAME hand suffix " +
                     $"(\"{suffixes[0]}\"), across the whole Startup->Active->Recovery arc, even though the " +
                     "ball swapped hands at Active-entry.");
        else
            Fail($"btl-single-polarity: expected all three phases observed and exactly one suffix across " +
                 $"every observed between-the-legs state; got sawAnyBtlState={_sawAnyBtlState}, " +
                 $"sawWholeArc={sawWholeArc}, distinctStates=[{string.Join(",", _distinctBtlStates)}], " +
                 $"distinctPhases=[{string.Join(",", phases)}], " +
                 $"distinctSuffixes=[{string.Join(",", suffixes)}]. A MIX of suffixes means the resolver read " +
                 "live HandSide per tick instead of OriginHand — i.e. \"betweenthelegs\" is missing from " +
                 "MoveAnimResolver.HandedMoves. If the premise broke instead, this proves nothing, so it " +
                 "fails rather than passes.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: btl-poses-the-skeleton ────────────────────────────────────
    // The gap every reachability/duration/mapping scenario leaves open. #313
    // shipped six clips whose track paths carried an "Armature/" prefix — one
    // level deeper than scenes/Player.tscn's rig — so every track bound to
    // nothing and the clips were SILENT no-ops. The state machine still entered
    // the right state, the durations still matched, the state->clip mapping was
    // still correct, and the mesh never moved. Godot logs "couldn't resolve
    // track" and carries on; it does not fail.
    //
    // Sampled on the LAST between-the-legs tick, against REST. Both halves are
    // load-bearing and both were established by mutation on the behind-the-back
    // family rather than by taste — two earlier metrics were tried against
    // deliberately-corrupted clips and BOTH passed on them:
    //
    //   max departure from rest    the Y Bot's rest is a T-pose and the arms are
    //   (over all ticks)           held off it by other machinery, so "far from
    //                              rest" is true either way.
    //   max change across the arc  a clip binding to nothing makes the bones
    //                              COLLAPSE to rest, and that collapse is itself
    //                              a large change. "It moved" does not imply
    //                              "this clip moved it".
    //
    // What separates them is WHERE the pose ends up. A bound clip holds the
    // upper body posed through Recovery; an unbound one has fully collapsed to
    // rest within a tick of entry (there is no xfade on any edge — `grep -c
    // xfade_time scenes/Player.tscn` is 0 — so the collapse is immediate) and
    // stays there. The final tick therefore reads ~0 deg when inert.
    private void VerdictPosesTheSkeleton()
    {
        bool pass = _sawAnyBtlState && _posedDegOnLastBtlTick >= PosedMinDeg;
        if (pass)
            GD.Print($"[betweenthelegs-anim] PASS btl-poses-the-skeleton — the upper body was still posed " +
                     $"{_posedDegOnLastBtlTick:F2} deg off rest (floor {PosedMinDeg:F1}) on the LAST " +
                     "between-the-legs tick, so the clips' tracks bind to this rig and hold it — rather than " +
                     "collapsing it to rest, which is what an unbound clip does.");
        else
            Fail($"btl-poses-the-skeleton: the clip did not move the rig. sawAnyBtlState={_sawAnyBtlState}, " +
                 $"upperBodyDepartureFromRestOnLastBtlTick={_posedDegOnLastBtlTick:F4} deg " +
                 $"(need >= {PosedMinDeg:F1}). Most likely the clips' track NODE PATHS do not bind on " +
                 "scenes/Player.tscn (check for an 'Armature/' prefix — Blender's FBX export adds an Armature " +
                 "object wrapper this rig does not have; tools/rebuild_betweenthelegs_clips.gd strips it), or " +
                 "the clip omits the arm tracks entirely and they are sitting at rest.");
        Finish(pass ? 0 : 1);
    }

    // Worst upper-body bone rotation off REST on the holder's live Skeleton3D,
    // this tick. Returns NaN — not 0 — when the skeleton cannot be found, so a
    // resolution failure fails the gate closed instead of printing a confident
    // "0.0000 deg" that reads as a real measurement of a real defect (#305).
    private static float UpperBodyDepartureFromRest(PlayerController holder)
    {
        var skel = FindSkeleton(holder);
        if (skel == null) return float.NaN;

        float worst = 0f;
        int measured = 0;
        foreach (string boneName in UpperBodyBones)
        {
            int idx = skel.FindBone(boneName);
            if (idx < 0) continue;
            measured++;
            Quaternion rest = skel.GetBoneRest(idx).Basis.GetRotationQuaternion().Normalized();
            Quaternion pose = skel.GetBonePose(idx).Basis.GetRotationQuaternion().Normalized();
            worst = Math.Max(worst, Mathf.RadToDeg(rest.AngleTo(pose)));
        }
        // Zero bones resolved is not "the rig is at rest", it is "this function
        // measured nothing" — poison rather than report a flattering 0.
        return measured == 0 ? float.NaN : worst;
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

    // The phase half of a "BetweenTheLegs<Phase><Suffix>" node name, used only
    // to prove the whole Startup->Active->Recovery arc was actually walked.
    private static string PhaseOf(string node) =>
        node.Contains("Startup") ? "Startup" :
        node.Contains("Active") ? "Active" :
        node.Contains("Recovery") ? "Recovery" :
        "None";

    // ── Scenario: no-unsuffixed-btl-state (negative) ────────────────────────
    // The premise this depends on — that a Travel() call under these identical
    // conditions CAN succeed at all — is proven by the companion scenario
    // below, not re-derived here.
    private void VerdictProbeUnsuffixed()
    {
        bool pass = !_sawProbeTargetNode;
        if (pass)
            GD.Print("[betweenthelegs-anim] PASS no-unsuffixed-btl-state — Travel(\"BetweenTheLegsActive\") " +
                     $"never reached that node across {ProbeSettleFrames} ticks; the state machine has no such " +
                     "node to travel to. See control-unsuffixed-probe for the premise proof that Travel() " +
                     "itself works under these identical conditions.");
        else
            Fail("no-unsuffixed-btl-state: Travel(\"BetweenTheLegsActive\") reached a node reporting that " +
                 "exact name — an unsuffixed state exists in scenes/Player.tscn and must be removed. #309's " +
                 "premise is that HandSide is two-valued and OriginHand is total over it, so there is nothing " +
                 "for an unsuffixed fallback to catch; one existing means some path can silently land on it.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: control-unsuffixed-probe (premise control) ────────────────
    private void VerdictProbeControl()
    {
        bool pass = _sawProbeTargetNode;
        if (pass)
            GD.Print("[betweenthelegs-anim] PASS control-unsuffixed-probe — Travel(\"BetweenTheLegsActiveLeft\") " +
                     $"reached that node within {ProbeSettleFrames} ticks, under the SAME setup " +
                     "no-unsuffixed-btl-state uses. This proves the Travel()-and-observe mechanism itself is " +
                     "sound, which is the premise that scenario's pass depends on.");
        else
            Fail($"control-unsuffixed-probe: Travel(\"BetweenTheLegsActiveLeft\") never reached that node " +
                 $"within {ProbeSettleFrames} ticks — either the state is missing/misnamed in " +
                 "scenes/Player.tscn, or the DribbleLeft/DribbleRight -> BetweenTheLegsStartupLeft -> " +
                 "BetweenTheLegsActiveLeft edge chain is broken, or the probe mechanism itself is broken — in " +
                 "which case no-unsuffixed-btl-state's pass would prove nothing.");
        Finish(pass ? 0 : 1);
    }

    // ── Static scenarios: no live tree, pure resource/scene inspection ──────
    private void RunStaticCheck()
    {
        switch (_scenario)
        {
            case "betweenthelegs-segment-lengths":              RunSegmentLengthsCheck(); break;
            case "betweenthelegs-no-placeholder-leak":          RunNoPlaceholderLeakCheck(); break;
            case "betweenthelegs-startup-differs-from-recovery": RunStartupDiffersFromRecoveryCheck(); break;
            case "control-identical-clips-read-as-identical":   RunIdenticalClipsControl(); break;
        }
    }

    // ── Scenario: betweenthelegs-segment-lengths ─────────────────────────────
    // Reads the move's real tick windows from BetweenTheLegs.DefaultFrameData
    // (not hardcoded), so a future #238 retune that forgets to re-run
    // tools/rebuild_betweenthelegs_clips.gd goes red here and names the tool.
    //
    // The tolerance is a float-noise band, NOT the "one tick" #309's acceptance
    // list allows. Those answer different questions. One-tick slack satisfies
    // "a clip a hair off the ideal is still legible" but silently voids "a
    // retune that forgot the rebuild tool goes red here" — bumping
    // StartupFrames 6 -> 7 deviates by exactly 1/60 s, slips under a one-tick
    // bar, and reports green while the clip is still cut to 6 ticks. Measured
    // deviation on all six clips is 0.000000s (re-printed per clip on every run,
    // pass or fail), so nothing is "a hair off" and the tight band costs
    // legibility nothing. Same call #314's review made for behind-the-back.
    private void RunSegmentLengthsCheck()
    {
        var lib = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        if (lib == null)
        {
            Fail("assets/locomotion.res failed to load — cannot evaluate betweenthelegs-segment-lengths.");
            Finish(1);
            return;
        }

        double tps = Engine.PhysicsTicksPerSecond;
        var frames = BetweenTheLegs.DefaultFrameData;
        const double ToleranceSeconds = 1e-3;

        (string Clip, int Ticks)[] windows =
        {
            ("betweenthelegsstartupleft",   frames.StartupFrames),
            ("betweenthelegsactiveleft",    frames.ActiveFrames),
            ("betweenthelegsrecoveryleft",  frames.RecoveryFrames),
            ("betweenthelegsstartupright",  frames.StartupFrames),
            ("betweenthelegsactiveright",   frames.ActiveFrames),
            ("betweenthelegsrecoveryright", frames.RecoveryFrames),
        };

        bool pass = true;
        foreach (var (clipName, ticks) in windows)
        {
            if (!lib.HasAnimation(clipName))
            {
                Fail($"AnimationLibrary has no clip '{clipName}' — run tools/rebuild_betweenthelegs_clips.gd.");
                pass = false;
                continue;
            }

            double expectedSeconds = ticks / tps;
            // Variant accessor, NOT .Length: that property is `float` in Godot
            // 4.6.x and `double` in 4.7, so a 4.7.1-built assembly throws
            // MissingMethodException under a stale 4.6 binary — and it throws
            // inside _PhysicsProcess, BEFORE the timeout check, so the scenario
            // HANGS instead of failing (#339). The Variant accessor binds under
            // both.
            double actualSeconds = lib.GetAnimation(clipName).Get("length").AsDouble();
            double deviationSeconds = Math.Abs(actualSeconds - expectedSeconds);
            GD.Print($"[betweenthelegs-anim]   '{clipName}': length={actualSeconds:F6}s " +
                     $"expected={expectedSeconds:F6}s ({ticks} ticks @ {tps} tps), deviation={deviationSeconds:F6}s");

            if (deviationSeconds > ToleranceSeconds)
            {
                Fail($"clip '{clipName}' is {actualSeconds:F6}s, expected {expectedSeconds:F6}s " +
                     $"({ticks} ticks at {tps} tps — BetweenTheLegs.DefaultFrameData), a deviation of " +
                     $"{deviationSeconds:F6}s exceeds the float-noise tolerance ({ToleranceSeconds:F6}s). " +
                     "Re-run tools/rebuild_betweenthelegs_clips.gd after retuning the move's frame data.");
                pass = false;
            }
        }

        if (pass)
            GD.Print("[betweenthelegs-anim] PASS betweenthelegs-segment-lengths — all six clips' durations are " +
                     "within the float-noise band of BetweenTheLegs.DefaultFrameData's Startup/Active/Recovery " +
                     "tick windows.");
        else
            GD.PrintErr("[betweenthelegs-anim] FAIL betweenthelegs-segment-lengths — see per-clip deviations above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: betweenthelegs-no-placeholder-leak ─────────────────────────
    // The direct statement that #296 is closed for this move: before #309,
    // betweenthelegs fell through MoveAnimResolver's default case onto the
    // shared generic states, which resolve to locomotion/idle (Startup and
    // Recovery) and a looping locomotion/run (Active). Reads the
    // AnimationNodeStateMachine directly off scenes/Player.tscn's SceneState,
    // which a state-NAME-only assertion (betweenthelegs-phases etc.) cannot do:
    // GetCurrentNode() would still report "BetweenTheLegsActiveLeft" even if
    // that state's own AnimationNodeAnimation pointed at the wrong clip.
    private void RunNoPlaceholderLeakCheck()
    {
        AnimationNodeStateMachine stateMachine = LoadStateMachine();
        if (stateMachine == null)
        {
            Fail("could not read an AnimationNodeStateMachine off scenes/Player.tscn's AnimationTree " +
                 "tree_root — the state<->clip mapping is unverified.");
            Finish(1);
            return;
        }

        // An ALLOWLIST, not a placeholder blocklist. A blocklist ("is it
        // locomotion/idle or locomotion/run?") closes #296 but leaves the more
        // likely defect open: these six AnimationNodeAnimation sub-resources
        // were spliced into scenes/Player.tscn directly beneath the six
        // behind-the-back ones, so the realistic slip is a state pointing at
        // locomotion/behindthebackactiveleft — a real, non-placeholder clip a
        // blocklist waves through and that GetCurrentNode() cannot see either.
        // Pinning the exact expected clip per state subsumes the placeholder
        // check and catches the copy-paste too.
        (string State, string Clip)[] states =
        {
            ("BetweenTheLegsStartupLeft",   "locomotion/betweenthelegsstartupleft"),
            ("BetweenTheLegsActiveLeft",    "locomotion/betweenthelegsactiveleft"),
            ("BetweenTheLegsRecoveryLeft",  "locomotion/betweenthelegsrecoveryleft"),
            ("BetweenTheLegsStartupRight",  "locomotion/betweenthelegsstartupright"),
            ("BetweenTheLegsActiveRight",   "locomotion/betweenthelegsactiveright"),
            ("BetweenTheLegsRecoveryRight", "locomotion/betweenthelegsrecoveryright"),
        };
        string[] placeholderClips = { "locomotion/idle", "locomotion/run" };

        bool pass = true;
        foreach (var (stateName, expectedClip) in states)
        {
            if (!stateMachine.HasNode(stateName))
            {
                Fail($"scenes/Player.tscn's state machine has no state '{stateName}' — cannot evaluate " +
                     "betweenthelegs-no-placeholder-leak for it.");
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
            GD.Print($"[betweenthelegs-anim]   state '{stateName}' -> clip '{clip}' (expected '{expectedClip}')");
            if (placeholderClips.Contains(clip))
            {
                Fail($"state '{stateName}' still points at the placeholder clip '{clip}' — #296 is not closed " +
                     "for this state. Re-point it at its own locomotion/betweenthelegs... clip.");
                pass = false;
            }
            else if (clip != expectedClip)
            {
                Fail($"state '{stateName}' points at '{clip}', not its own '{expectedClip}'. The clip is real, " +
                     "so this is not #296 — it is a mis-wired sub-resource (most likely a copy-paste off the " +
                     "adjacent behind-the-back block). The state name alone reads correct, so no reachability " +
                     "assertion can catch this.");
                pass = false;
            }
        }

        if (pass)
            GD.Print("[betweenthelegs-anim] PASS betweenthelegs-no-placeholder-leak — each of the six " +
                     "between-the-legs states points at its OWN clip, not at locomotion/idle, locomotion/run, " +
                     "or a neighbouring move's clip (#296 closed for this move).");
        else
            GD.PrintErr("[betweenthelegs-anim] FAIL betweenthelegs-no-placeholder-leak — see per-state clips above.");

        Finish(pass ? 0 : 1);
    }

    private static AnimationNodeStateMachine LoadStateMachine()
    {
        var playerScene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        var sceneState = playerScene.GetState();
        for (int i = 0; i < sceneState.GetNodeCount(); i++)
        {
            if (sceneState.GetNodeType(i) != "AnimationTree") continue;
            for (int p = 0; p < sceneState.GetNodePropertyCount(i); p++)
            {
                if (sceneState.GetNodePropertyName(i, p) != "tree_root") continue;
                return sceneState.GetNodePropertyValue(i, p).As<AnimationNodeStateMachine>();
            }
        }
        return null;
    }

    // ── Startup-vs-Recovery: the pair ────────────────────────────────────────
    //
    // Degrees of worst-bone disagreement two clips must show to count as
    // visibly different poses, and the ceiling below which two clips count as
    // the same thing to look at. Both set from measurement, and both numbers
    // are re-printed on every run (pass or fail) so the next person can see
    // whether the bar still sits where this comment claims:
    //
    //   Startup vs Recovery   39.02 / 42.59 / 43.97 / 47.85 deg — the four
    //                         (polarity x sample-point) pairs. Closest is 39.02.
    //   idle vs ITSELF         0.0560 deg, at both sample points.
    //
    // The 0.056 is NOT a content difference — comparing a clip against itself
    // is bit-identical input. It is conditioning noise in Quaternion.AngleTo,
    // which evaluates acos(2d^2 - 1); acos has an infinite derivative at 1, so
    // a dot product one ulp below 1.0 lands tens of millidegrees off zero. That
    // is why the ceiling is 0.5 rather than 0.0: an exact-zero bar would fail
    // on arithmetic, not on content.
    //
    // 8 and 0.5 leave the whole 0.06 .. 39 range empty, so neither bar sits
    // near a real value — ~5x headroom below the tightest real separation and
    // ~9x above the noise floor.
    private const float StartupVsRecoveryMinDeg = 8.0f;
    private const float IdenticalMaxDeg = 0.5f;

    // Degrees the control's time-sensitivity leg must see between ONE clip
    // sampled at two DIFFERENT instants. Guards a comparator that honoured only
    // its first time argument: with a == b that bug is invisible, and with
    // ta == tb it is invisible too, so the identity legs alone cannot see it.
    // Measured on locomotion/idle: 8.6643 deg at t=0 vs t=L/2. Note this is
    // deliberately NOT t=0 vs t=end -- idle is a perfect loop, so the seam reads
    // 0.0560 deg and would make this leg fail on a healthy comparator.
    private const float TimeSensitivityMinDeg = 3.0f;

    // ── Scenario: betweenthelegs-startup-differs-from-recovery ───────────────
    // #296's actual complaint, stated directly. Under the pre-#309 fallback,
    // Startup and Recovery both resolved to locomotion/idle and were therefore
    // PIXEL-IDENTICAL — an opponent could not tell "committing" from "in the
    // punish window", which is a competitive defect under ADR-0003's legibility
    // requirement, not a cosmetic one.
    //
    // Compares CLIP CONTENT, not the live tree. betweenthelegs-no-placeholder-leak
    // already pins state->clip by name, but two DIFFERENTLY-NAMED clips can
    // still be byte-identical — a slice tool that cut the same source window
    // twice produces exactly that, and every name-based assertion in this file
    // waves it through. This samples both Animation resources directly and
    // compares per-bone local rotations, so identical content reads as 0 no
    // matter what the two clips are called.
    //
    // Two sample points, min-reduced (trap #17: a "both differ" claim asserted
    // with max() passes when only one of them does):
    //   t=0    the pose an opponent sees the instant each phase BEGINS — for
    //          Startup a near-neutral dribble stance, for Recovery the deep
    //          crouch Active ended in.
    //   t=end  the pose each phase settles into — for Startup that same deep
    //          crouch, for Recovery a return toward neutral.
    // Both polarities are checked, so a right-hand-only regression cannot hide
    // behind a healthy left.
    //
    // The instrument's own soundness — that it can report "these are the same"
    // at all, rather than returning a large number regardless of input — is the
    // premise, and is proven by control-identical-clips-read-as-identical.
    private void RunStartupDiffersFromRecoveryCheck()
    {
        var lib = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        if (lib == null)
        {
            Fail("assets/locomotion.res failed to load — cannot evaluate " +
                 "betweenthelegs-startup-differs-from-recovery.");
            Finish(1);
            return;
        }

        bool pass = true;
        float worstOverall = float.PositiveInfinity;

        foreach (string pol in new[] { "left", "right" })
        {
            var startup = LoadClip(lib, $"betweenthelegsstartup{pol}");
            var recovery = LoadClip(lib, $"betweenthelegsrecovery{pol}");
            if (startup == null || recovery == null)
            {
                Fail($"missing clip for polarity '{pol}' — run tools/rebuild_betweenthelegs_clips.gd.");
                pass = false;
                continue;
            }

            float sEnd = (float)startup.Get("length").AsDouble();
            float rEnd = (float)recovery.Get("length").AsDouble();

            (string Label, float Ta, float Tb)[] samples =
            {
                ("phase-entry (t=0)", 0f, 0f),
                ("phase-exit (t=end)", sEnd, rEnd),
            };

            foreach (var (label, ta, tb) in samples)
            {
                float deg = WorstBoneSeparationDeg(startup, recovery, ta, tb,
                                                   out int compared, out string worstBone);
                GD.Print($"[betweenthelegs-anim]   {pol}/{label}: worst-bone separation = {deg:F4} deg " +
                         $"({worstBone}), bones compared = {compared}");

                // NaN propagates through `>=` as false, so a poisoned
                // measurement fails closed rather than reporting a confident 0.
                if (!(deg >= StartupVsRecoveryMinDeg))
                {
                    Fail($"betweenthelegs-startup-differs-from-recovery: {pol} Startup and Recovery are only " +
                         $"{deg:F4} deg apart at {label} (need >= {StartupVsRecoveryMinDeg:F1}), over " +
                         $"{compared} shared bones. If that is 0.0000 the two states are showing the SAME " +
                         "content and #296 is NOT closed for this move — the opponent cannot tell " +
                         "\"committing\" from \"punish window\". If `compared` is 0 the two clips share no " +
                         "bone tracks at all, which is a different defect in the slice tool.");
                    pass = false;
                }
                worstOverall = Math.Min(worstOverall, deg);
            }
        }

        if (pass)
            GD.Print($"[betweenthelegs-anim] PASS betweenthelegs-startup-differs-from-recovery — across both " +
                     $"polarities and both sample points, the closest Startup/Recovery pair still separates by " +
                     $"{worstOverall:F4} deg (floor {StartupVsRecoveryMinDeg:F1}). The two phases are " +
                     "distinguishable poses, not the pixel-identical locomotion/idle pair #296 reported.");
        else
            GD.PrintErr("[betweenthelegs-anim] FAIL betweenthelegs-startup-differs-from-recovery — see " +
                        "per-sample separations above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: control-identical-clips-read-as-identical (premise) ────────
    // The control for the scenario above, and the reason that one is not
    // vacuous. Its pass shape is "the number came out LARGE", which a broken
    // comparator — one that silently ignored its second argument, or compared
    // each clip against the bone REST instead of against each other — would
    // also produce, on every input, forever.
    //
    // So run the SAME instrument on the exact defect #296 reported: the
    // pre-#309 configuration, where Startup and Recovery both resolved to
    // locomotion/idle. That is a real clip in the shipped library, so this
    // reproduces the historical failure rather than simulating it, and asserts
    // the comparator reports it as ~0. `compared > 0` is asserted explicitly:
    // a comparator matching NO bones would also return a small number, and
    // "small" is this scenario's PASS condition — the one place in this file
    // where an empty measurement could be mistaken for success.
    private void RunIdenticalClipsControl()
    {
        var lib = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        if (lib == null)
        {
            Fail("assets/locomotion.res failed to load — cannot evaluate " +
                 "control-identical-clips-read-as-identical.");
            Finish(1);
            return;
        }

        var idle = LoadClip(lib, "idle");
        if (idle == null)
        {
            Fail("assets/locomotion.res has no 'idle' clip — this control needs the very clip #296's " +
                 "fallback resolved to.");
            Finish(1);
            return;
        }

        float end = (float)idle.Get("length").AsDouble();
        (string Label, float Ta, float Tb)[] samples =
        {
            ("t=0", 0f, 0f),
            ("t=end", end, end),
        };

        bool pass = true;
        foreach (var (label, ta, tb) in samples)
        {
            float deg = WorstBoneSeparationDeg(idle, idle, ta, tb, out int compared, out string worstBone);
            GD.Print($"[betweenthelegs-anim]   idle-vs-idle/{label}: worst-bone separation = {deg:F6} deg " +
                     $"({worstBone}), bones compared = {compared}");

            if (compared <= 0)
            {
                Fail($"control-identical-clips-read-as-identical: compared {compared} bones at {label}. A " +
                     "separation near zero over ZERO bones is not evidence the comparator can detect " +
                     "sameness — it is evidence it measured nothing, and this scenario's pass condition " +
                     "would swallow that silently.");
                pass = false;
                continue;
            }
            // NaN fails this closed too: !(NaN <= x) is true.
            if (!(deg <= IdenticalMaxDeg))
            {
                Fail($"control-identical-clips-read-as-identical: a clip compared against ITSELF reported " +
                     $"{deg:F6} deg of separation at {label} (ceiling {IdenticalMaxDeg:F1}) over {compared} " +
                     "bones. The comparator returns large numbers regardless of input, so " +
                     "betweenthelegs-startup-differs-from-recovery's pass proves nothing and must not be " +
                     "trusted until this is fixed.");
                pass = false;
            }
        }

        // ── Time-sensitivity leg ────────────────────────────────────────────
        // The two legs above hold `a == b` AND `ta == tb`, so between them they
        // cannot see a comparator that honoured only its FIRST time argument.
        // That bug is not hypothetical in shape: under it, the main scenario's
        // phase-exit sample would compare startup@0.100 against recovery@0.100
        // instead of recovery@0.183 — a different, wronger question that still
        // reports tens of degrees and still passes.
        //
        // So sample ONE clip at two DIFFERENT instants and require a real
        // separation. Deliberately t=0 vs t=L/2, not t=0 vs t=end: idle is a
        // perfect loop, so its seam reads 0.0560 deg — the same as the identity
        // legs — and would fail this on a perfectly healthy comparator.
        {
            float half = (float)(idle.Get("length").AsDouble() * 0.5);
            float deg = WorstBoneSeparationDeg(idle, idle, 0f, half, out int compared, out string worstBone);
            GD.Print($"[betweenthelegs-anim]   idle-vs-idle/t=0-vs-t=L/2: worst-bone separation = {deg:F4} deg " +
                     $"({worstBone}), bones compared = {compared}");
            if (!(deg >= TimeSensitivityMinDeg))
            {
                Fail($"control-identical-clips-read-as-identical: one clip sampled at t=0 and t={half:F4} " +
                     $"reported only {deg:F4} deg of separation (need >= {TimeSensitivityMinDeg:F1}) over " +
                     $"{compared} bones. The comparator is not honouring its SECOND time argument — most " +
                     "likely both samples are being taken at `ta`. betweenthelegs-startup-differs-from-recovery " +
                     "would still pass under that bug while comparing the wrong pair of instants, so its " +
                     "result must not be trusted until this is fixed.");
                pass = false;
            }
        }

        if (pass)
            GD.Print("[betweenthelegs-anim] PASS control-identical-clips-read-as-identical — the same " +
                     "comparator betweenthelegs-startup-differs-from-recovery uses reports ~0 deg when handed " +
                     "two identical clips (locomotion/idle, the very clip #296's fallback resolved to), over a " +
                     "non-empty bone set, AND reports a real separation when handed one clip at two different " +
                     "instants. It therefore distinguishes 'different' from 'the same' and honours both time " +
                     "arguments — the two premises that scenario's pass depends on.");
        else
            GD.PrintErr("[betweenthelegs-anim] FAIL control-identical-clips-read-as-identical — see above.");

        Finish(pass ? 0 : 1);
    }

    private static Animation LoadClip(AnimationLibrary lib, string name) =>
        lib.HasAnimation(name) ? lib.GetAnimation(name) : null;

    // Worst per-bone local-rotation disagreement between two clips, sampled at
    // `ta` in `a` and `tb` in `b`, in degrees.
    //
    // Deliberately compares LOCAL bone rotations rather than FK'd world
    // origins: the question here is "is this the same animation content", and
    // local rotation is what the clip literally stores. It needs no Skeleton3D,
    // so it cannot be confounded by rest-pose geometry (see the
    // BlendRestAnchor lesson — global rests on this rig inherit a mutated
    // UpLeg rest and are not a trustworthy reference frame).
    //
    // Bones present in only ONE clip are skipped rather than counted as
    // disagreement: an absent track is a coverage question, which
    // btl-poses-the-skeleton and the rebuild tool's own coverage gate own.
    //
    // Returns NaN when nothing was compared. The `compared` out-param is
    // returned rather than folded into the result so the CONTROL — whose pass
    // condition is a SMALL number — can assert it independently; there, and
    // only there, an empty comparison would otherwise look like success.
    private static float WorstBoneSeparationDeg(Animation a, Animation b, float ta, float tb,
                                                out int compared, out string worstBone)
    {
        Dictionary<string, int> rotTracks(Animation anim)
        {
            var map = new Dictionary<string, int>();
            for (int i = 0; i < anim.GetTrackCount(); i++)
            {
                if (anim.TrackGetType(i) != Animation.TrackType.Rotation3D) continue;
                NodePath path = anim.TrackGetPath(i);
                if (path.GetSubNameCount() == 0) continue;
                map[path.GetSubName(0)] = i;
            }
            return map;
        }

        Dictionary<string, int> ma = rotTracks(a);
        Dictionary<string, int> mb = rotTracks(b);

        compared = 0;
        worstBone = "(none)";
        float worst = 0f;
        foreach (var (bone, ia) in ma)
        {
            if (!mb.TryGetValue(bone, out int ib)) continue;
            compared++;
            Quaternion qa = a.RotationTrackInterpolate(ia, ta).Normalized();
            Quaternion qb = b.RotationTrackInterpolate(ib, tb).Normalized();
            float deg = Mathf.RadToDeg(qa.AngleTo(qb));
            if (deg > worst)
            {
                worst = deg;
                worstBone = bone;
            }
        }
        return compared == 0 ? float.NaN : worst;
    }

    private void Fail(string message) => GD.PrintErr($"[betweenthelegs-anim] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[betweenthelegs-anim] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
