using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #280 (ADR-0016): proves the SIX
// hand-side crossover states scenes/Player.tscn now carries —
// Crossover{Startup,Active,Recovery}{Left,Right} — are actually ENTERED
// end-to-end through the real AnimationTree, and that the SUFFIX stays
// constant across a single crossover's whole Startup->Active->Recovery arc
// even though PlayerController.HandSide itself flips mid-move.
//
// MoveKindAnimTest (#277) already proves a clipped move's GENERIC per-move
// states are reachable; this is its narrower, hand-side-specific sibling for
// the ONE move MoveAnimResolver.HandedMoves currently lists ("crossover").
// What neither MoveKindAnimTest nor MoveAnimResolverTests (xUnit, pure/no
// AnimationTree) can catch is a PER-TICK hand read: OriginHand exists
// specifically because a crossover's Active-entry flips HandSide (Left<->Right)
// partway through the move (TickCommittedMoveBehavior's JustEnteredActive
// branch) — reading HandSide naively on every tick would show the Startup
// wind-up telegraphing one direction and the Active/Recovery burst playing the
// MIRROR of it, exactly the false read ADR-0003 forbids. OriginHand corrects
// for that by inverting the post-swap phases (see MoveAnimResolver.OriginHand's
// own doc for the full derivation). Only reading the LIVE
// AnimationNodeStateMachinePlayback proves the correction is actually wired
// into the real .tscn state machine, not just correct in the resolver's own
// unit tests.
//
//   godot --headless --path . res://tests/integration/CrossoverAnimTest.tscn -- --harness-scenario=crossover-left-origin
//   godot --headless --path . res://tests/integration/CrossoverAnimTest.tscn -- --harness-scenario=crossover-right-origin
//   godot --headless --path . res://tests/integration/CrossoverAnimTest.tscn -- --harness-scenario=crossover-single-polarity
//   godot --headless --path . res://tests/integration/CrossoverAnimTest.tscn -- --harness-scenario=no-unsuffixed-crossover-state
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "crossover-left-origin".
//
// ── Why ActiveAnimNodeForHarness, not the resolver's own decision (#257) ────
// Same discipline as every other anim harness in this repo: reading
// AnimationNodeStateMachinePlayback.GetCurrentNode() (via the live
// AnimationTree) asserts what the state machine ACTUALLY did, never what
// MoveAnimResolver.ResolveStateName merely DECIDED — calling that directly
// would keep passing even on a Player.tscn with none of the six handed states
// wired at all, since the resolver has no notion the .tscn even exists.
// Travel() to a missing/misnamed state only LOGS, it never throws (#257).
//
// ── Why a real scenes/Player.tscn instance, and a live BallController ───────
// A live AnimationTree only exists on the REAL scenes/Player.tscn (a bare
// `new PlayerController()` has no mesh/tree at all). Crossover is ALSO a
// dribble-family committed move: BeginCommittedMove's Held-holder
// dead-dribble gate (#193) refuses it outright unless the ball is genuinely
// Dribbling (see PlayerController.BeginCommittedMove's "#193 code-review fix"
// comment) — same reasoning MoveKindAnimTest's header already spells out for
// BehindTheBack/BetweenTheLegs, which Crossover shares verbatim.
//
// ── Why SetHandSideForHarness for crossover-right-origin ────────────────────
// A fresh tipoff possession always resets HandSide to the default (Left) —
// PlayerController.HandSide's own field doc, "reset to the default (Left)
// when the player gains possession". There is no production path that starts
// a holder in the RIGHT hand without first running an actual hand-swapping
// move, so the right-origin polarity needs a direct harness seam
// (SetHandSideForHarness, added alongside SetHeadingForHarness in
// PlayerHarnessSeam.cs for this issue) to force the precondition before
// Begin() — the same "direct setup, narrower than driving real input" pattern
// FadeawayTriggerTest/JumpshotAnimTest already use for Heading.
//
// ── Why crossover-single-polarity does NOT re-derive expected names ─────────
// The load-bearing scenario deliberately does not ask "does MoveAnimResolver
// think this should be CrossoverActiveLeft" — that would just be re-running
// the resolver's own (already unit-tested) formula and comparing it to
// itself. Instead it collects whatever DISTINCT "Crossover*" node names the
// LIVE tree actually reported over one full crossover and asserts they all
// share the same trailing suffix. A per-tick HandSide read (the exact bug
// OriginHand exists to prevent) would make this scenario observe BOTH
// "CrossoverStartupLeft" and "CrossoverActiveLeft"/"CrossoverRecoveryRight" (or
// the mirror), i.e. two distinct suffixes in the same run — this scenario
// fails on that mix, not on comparing against a resolver-derived expectation.
//
// ── What this harness CANNOT prove: individual transition EDGES ─────────────
// Same limitation JumpshotAnimTest's header documents at length (measured by
// mutation, #279): Travel() is a PATHFINDER over the transition graph, not a
// single-hop switch, so deleting a direct edge between two handed states does
// not redden anything observable through ActiveAnimNodeForHarness — the walk
// still arrives at the target via whatever other edges exist. Do not extend
// this file with an edge-level assertion; it will pass regardless of the
// edge's presence.
//
// ── Why the controls carry the real weight here ─────────────────────────────
// "crossover-single-polarity" and "no-unsuffixed-crossover-state" both assert
// their OWN PREMISE first — that a real crossover genuinely produced at least
// one "Crossover*" node observation — so neither can pass vacuously on a rig
// where nothing ever left Locomotion/Dribble (which would trivially satisfy
// "never showed a mixed/unsuffixed state" for any state).
//
// Whether either clip LOOKS right (correct limbs, no foot-sliding, reads as
// "crossover") is #279/#173's deferred human feel judgment (ADR-0021) — this
// harness only asserts state-machine REACHABILITY and suffix consistency,
// never clip content.
public partial class CrossoverAnimTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int ArmFrames = 2;           // ticks for TryAssignTipoffHolder to run
    private const int ActionMarginFrames = 3;  // ticks to let TryStartDribble's effect settle
    private const int SettleMarginFrames = 5;  // ticks after returning to Inactive before the final read

    private static readonly string[] KnownScenarios =
    {
        "crossover-left-origin", "crossover-right-origin", "crossover-single-polarity", "no-unsuffixed-crossover-state",
        "crossover-track-completeness", "crossover-active-distinct-from-siblings",
        "crossover-polarity-content",
    };

    private string _scenario = "crossover-left-origin";

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

    // ── Latches for crossover-left-origin / crossover-right-origin ─────────
    // Chained exactly like JumpshotAnimTest's phase latches: each guard
    // requires the previous one already latched, so "saw all three" IS
    // "saw them in order."
    private bool _sawStartup;
    private bool _sawActive;
    private bool _sawRecovery;

    // ── Observations for crossover-single-polarity / no-unsuffixed-crossover-state ─
    // Every DISTINCT "Crossover*" node name the live tree reported over the
    // whole run — collected at event time (every physics tick), not sampled
    // once at the end.
    private readonly HashSet<string> _distinctCrossoverStates = new();
    private bool _sawAnyCrossoverState;
    private bool _sawUnsuffixedCrossoverState;

    // Gate for "the move genuinely ran" (mirrors MoveKindAnimTest's
    // _sawActivePhase): only once the Active phase has actually been observed
    // does a later return to Inactive count as "the lifecycle finished."
    private bool _sawActivePhase;
    private int _returnedInactiveFrame = -1;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "crossover-left-origin");
        GD.Print($"[crossover-anim] scenario={_scenario} booting headless…");

        if (!KnownScenarios.Contains(_scenario))
        {
            Fail($"unknown scenario '{_scenario}'.");
            Finish();
            return;
        }

        if (_scenario is "crossover-track-completeness" or "crossover-active-distinct-from-siblings" or "crossover-polarity-content")
        {
            RunStaticCheck();
            return;
        }

        // Real Player.tscn instances (live AnimationTree), named "1"/"2" so the
        // OfflineMultiplayerPeer makes unique_id 1 both IsServer and
        // IsLocalPlayer (the full TickServerOwnPlayer -> ApplyAnimation chain
        // runs every tick), same as JumpshotAnimTest/MoveKindAnimTest.
        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        _p1 = scene.Instantiate<PlayerController>();
        _p1.Name = "1";
        _p2 = scene.Instantiate<PlayerController>();
        _p2.Name = "2";

        // Physics-callback lockstep so GetCurrentNode() reflects the same-tick
        // Travel() (the default Idle callback lags under --headless — see
        // MoveKindAnimTest's long note; harness-only observation fidelity).
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
                         $"(Crossover cannot Begin from Held, #193); got state={_ball.State}.");
                    Finish();
                    return;
                }

                var holder = HolderNode();

                // Both origin polarities are forced explicitly rather than
                // relying on either polarity being "the shipped default" —
                // ADR-0012's 2026-07-28 amendment moved the possession-reset
                // default from Left to Right, so a scenario that assumed the
                // tipoff's possession-award would leave the holder on a
                // PARTICULAR hand went stale the moment that default changed.
                // Forcing both explicitly makes this scenario immune to any
                // future default flip.
                if (_scenario == "crossover-left-origin")
                    holder.SetHandSideForHarness(HandSide.Left);
                else if (_scenario == "crossover-right-origin")
                    holder.SetHandSideForHarness(HandSide.Right);

                // The flick sign must point at the EMPTY hand, or this would not
                // be a crossover at all in the live input path — one flick
                // produces a crossover or a HESITATION depending on exactly this
                // (HandStateResolver.IsCrossover: Left,+1 and Right,-1 are the
                // only crossover rows). BeginCrossoverForHarness bypasses that
                // gate, so an inverted sign here would still "work"; it is
                // written correctly anyway so the scenario reproduces a real
                // crossover rather than an input the game could never produce,
                // and so nobody reads this and concludes the sign is arbitrary.
                //
                // Display does NOT depend on it. MoveAnimResolver deliberately
                // reads the authoritative HandSide rather than BurstDirection,
                // because the server reconstructs the move straight from the
                // client's payload with no IsCrossover re-validation, so the
                // burst sign is not a trustworthy witness to the origin hand.
                float flickSign = HandStateResolver.EmptyHandSign(holder.HandSide);
                bool began = holder.BeginCrossoverForHarness(flickSign);
                if (!began)
                {
                    Fail($"{_scenario}: BeginCrossoverForHarness returned false " +
                         "— machine was not Inactive or the dead-dribble gate refused it.");
                    Finish();
                    return;
                }
                GD.Print($"[crossover-anim] Crossover begun on holder={_holderId} " +
                         $"(startHand={holder.HandSide}).");
                _step = Step.Observing;
                break;

            case Step.Observing:
                Observe();
                if (_returnedInactiveFrame >= 0 && _frame == _returnedInactiveFrame + SettleMarginFrames)
                    RenderVerdict();
                break;
        }

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame}, scenario={_scenario}, step={_step}, " +
                 $"lastAnimNode={HolderNode()?.ActiveAnimNodeForHarness}, sawStartup={_sawStartup}, " +
                 $"sawActive={_sawActive}, sawRecovery={_sawRecovery}, sawActivePhase={_sawActivePhase}, " +
                 $"distinctCrossoverStates=[{string.Join(",", _distinctCrossoverStates)}], " +
                 $"sawUnsuffixed={_sawUnsuffixedCrossoverState}.");
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
            case "crossover-left-origin":
                if (!_sawStartup && node == "CrossoverStartupLeft") _sawStartup = true;
                if (_sawStartup && !_sawActive && node == "CrossoverActiveLeft") _sawActive = true;
                if (_sawActive && !_sawRecovery && node == "CrossoverRecoveryLeft") _sawRecovery = true;
                break;

            case "crossover-right-origin":
                if (!_sawStartup && node == "CrossoverStartupRight") _sawStartup = true;
                if (_sawStartup && !_sawActive && node == "CrossoverActiveRight") _sawActive = true;
                if (_sawActive && !_sawRecovery && node == "CrossoverRecoveryRight") _sawRecovery = true;
                break;

            case "crossover-single-polarity":
            case "no-unsuffixed-crossover-state":
                if (node.StartsWith("Crossover"))
                {
                    _sawAnyCrossoverState = true;
                    _distinctCrossoverStates.Add(node);
                    if (node == "CrossoverStartup" || node == "CrossoverActive" || node == "CrossoverRecovery")
                        _sawUnsuffixedCrossoverState = true;
                }
                break;
        }

        if (_sawActivePhase && phase == MovePhase.Inactive && _returnedInactiveFrame < 0)
            _returnedInactiveFrame = _frame;
    }

    private void RenderVerdict()
    {
        switch (_scenario)
        {
            case "crossover-left-origin":            VerdictOrigin("Left"); break;
            case "crossover-right-origin":            VerdictOrigin("Right"); break;
            case "crossover-single-polarity":         VerdictSinglePolarity(); break;
            case "no-unsuffixed-crossover-state":     VerdictNoUnsuffixed(); break;
        }
    }

    // ── Scenarios: crossover-left-origin / crossover-right-origin (positive) ──
    private void VerdictOrigin(string suffix)
    {
        bool pass = _sawStartup && _sawActive && _sawRecovery;
        if (pass)
            GD.Print($"[crossover-anim] PASS crossover-{suffix.ToLowerInvariant()}-origin — the tree was " +
                     $"observed on \"CrossoverStartup{suffix}\", then \"CrossoverActive{suffix}\", then " +
                     $"\"CrossoverRecovery{suffix}\", in that order.");
        else
            Fail($"crossover-{suffix.ToLowerInvariant()}-origin: expected CrossoverStartup{suffix} -> " +
                 $"CrossoverActive{suffix} -> CrossoverRecovery{suffix}, in order; got sawStartup={_sawStartup}, " +
                 $"sawActive={_sawActive}, sawRecovery={_sawRecovery}, " +
                 $"lastAnimNode={HolderNode().ActiveAnimNodeForHarness}.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: crossover-single-polarity (load-bearing) ─────────────────
    private void VerdictSinglePolarity()
    {
        // Premise check: the crossover must genuinely have shown at least one
        // "Crossover*" state, or "all observed suffixes agree" is vacuously
        // true over an empty set.
        var suffixes = _distinctCrossoverStates.Select(SuffixOf).Distinct().ToList();
        bool pass = _sawAnyCrossoverState && suffixes.Count == 1;

        if (pass)
            GD.Print($"[crossover-anim] PASS crossover-single-polarity — every distinct crossover state " +
                     $"observed ([{string.Join(",", _distinctCrossoverStates)}]) carried the SAME hand suffix " +
                     $"(\"{suffixes[0]}\"), across the whole Startup->Active->Recovery arc.");
        else
            Fail($"crossover-single-polarity: expected exactly one suffix across every observed crossover " +
                 $"state; got sawAnyCrossoverState={_sawAnyCrossoverState}, " +
                 $"distinctStates=[{string.Join(",", _distinctCrossoverStates)}], " +
                 $"distinctSuffixes=[{string.Join(",", suffixes)}]. If the premise broke, this proves nothing, " +
                 "so this fails rather than passes.");
        Finish(pass ? 0 : 1);
    }

    private static string SuffixOf(string node) =>
        node.EndsWith("Left") ? "Left" :
        node.EndsWith("Right") ? "Right" :
        "None";

    // â”€â”€ Static resource checks (#317) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // These checks deliberately read the AnimationLibrary and Player.tscn
    // resource graph directly. A live state-name observation proves routing,
    // but it cannot see a complete, correctly-named clip whose tracks bind to
    // nothing, nor can it distinguish two state nodes with swapped clip names.
    private void RunStaticCheck()
    {
        switch (_scenario)
        {
            case "crossover-track-completeness": RunTrackCompleteness(); break;
            case "crossover-active-distinct-from-siblings": RunActiveDistinctness(); break;
            case "crossover-polarity-content": RunPolarityContentMapping(); break;
        }
    }

    private static readonly string[] CrossoverClips =
    {
        "crossoverstartupleft", "crossoveractiveleft", "crossoverrecoveryleft",
        "crossoverstartupright", "crossoveractiveright", "crossoverrecoveryright",
    };

    private void RunTrackCompleteness()
    {
        AnimationLibrary library = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        Skeleton3D skeleton = FindSkeleton(GD.Load<PackedScene>("res://assets/Y Bot.fbx").Instantiate());
        if (library == null || skeleton == null)
        {
            Fail("crossover-track-completeness: could not load locomotion.res or Y Bot's Skeleton3D.");
            Finish(1);
            return;
        }

        var expected = new HashSet<string>();
        for (int i = 0; i < skeleton.GetBoneCount(); i++) expected.Add(CanonicalBone(skeleton.GetBoneName(i)));
        bool pass = true;
        foreach (string clipName in CrossoverClips)
        {
            if (!library.HasAnimation(clipName))
            {
                Fail($"crossover-track-completeness: locomotion.res has no '{clipName}'.");
                pass = false;
                continue;
            }

            Animation clip = library.GetAnimation(clipName);
            var tracks = new Dictionary<string, int>();
            var malformed = new List<string>();
            for (int i = 0; i < clip.GetTrackCount(); i++)
            {
                if (clip.TrackGetType(i) != Animation.TrackType.Rotation3D) continue;
                NodePath path = clip.TrackGetPath(i);
                if (path.GetSubNameCount() != 1 || !path.ToString().StartsWith("Skeleton3D:"))
                {
                    malformed.Add(path.ToString());
                    continue;
                }
                string bone = CanonicalBone(path.GetSubName(0));
                if (!tracks.TryAdd(bone, i)) malformed.Add($"duplicate:{bone}");
                else if (clip.TrackGetKeyCount(i) == 0) malformed.Add($"unkeyed:{bone}");
            }

            bool exact = tracks.Keys.ToHashSet().SetEquals(expected);
            GD.Print($"[crossover-anim]   {clipName}: rotation tracks={tracks.Count}, rig bones={expected.Count}, malformed=[{string.Join(",", malformed)}]");
            if (!exact || malformed.Count != 0)
            {
                var missing = expected.Except(tracks.Keys).OrderBy(x => x);
                var extra = tracks.Keys.Except(expected).OrderBy(x => x);
                Fail($"crossover-track-completeness: '{clipName}' must carry exactly one keyed, bindable Rotation3D track " +
                     $"for each of Y Bot's {expected.Count} bones; missing=[{string.Join(",", missing)}], " +
                     $"extra=[{string.Join(",", extra)}], malformed=[{string.Join(",", malformed)}].");
                pass = false;
            }
        }
        if (pass) GD.Print("[crossover-anim] PASS crossover-track-completeness â€” all six clips cover every one of Y Bot's 65 bones exactly once with bindable keyed rotation tracks.");
        Finish(pass ? 0 : 1);
    }

    private void RunActiveDistinctness()
    {
        AnimationLibrary library = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        if (library == null)
        {
            Fail("crossover-active-distinct-from-siblings: assets/locomotion.res failed to load.");
            Finish(1);
            return;
        }

        // Floor selected after measuring the shipped authored action: 33.011 deg
        // is its smallest counterpart separation; 20 deg preserves a 13-deg
        // margin while rejecting a sibling clip substituted wholesale.
        const float FloorDeg = 20f;
        bool pass = true;
        foreach (string side in new[] { "left", "right" })
        {
            string crossover = $"crossoveractive{side}";
            string[] siblings = { $"behindthebackactive{side}", "inandoutactive", $"betweenthelegsactive{side}" };
            foreach (string sibling in siblings)
            {
                if (!library.HasAnimation(crossover) || !library.HasAnimation(sibling))
                {
                    Fail($"crossover-active-distinct-from-siblings: required clip missing ({crossover}, {sibling}).");
                    pass = false;
                    continue;
                }
                float separation = WorstBoneSeparationDeg(library.GetAnimation(crossover), library.GetAnimation(sibling), out int compared);
                GD.Print($"[crossover-anim]   {crossover} vs {sibling}: max local-pose separation={separation:F3} deg over {compared} shared bones (floor={FloorDeg:F1})");
                if (!(compared == 65 && separation >= FloorDeg))
                {
                    Fail($"crossover-active-distinct-from-siblings: {crossover} vs {sibling} measured {separation:F3} deg " +
                         $"over {compared} shared bones; require 65 and >= {FloorDeg:F1} deg.");
                    pass = false;
                }
            }
        }
        if (pass) GD.Print("[crossover-anim] PASS crossover-active-distinct-from-siblings â€” both crossover polarities differ from behind-the-back, in-and-out, and between-the-legs Active poses.");
        Finish(pass ? 0 : 1);
    }

    private void RunPolarityContentMapping()
    {
        AnimationNodeStateMachine machine = LoadStateMachine();
        if (machine == null)
        {
            Fail("crossover-polarity-content: could not read scenes/Player.tscn's state machine.");
            Finish(1);
            return;
        }
        bool pass = true;
        foreach (string phase in new[] { "Startup", "Active", "Recovery" })
        {
            foreach (string side in new[] { "Left", "Right" })
            {
                string state = $"Crossover{phase}{side}";
                string expected = $"locomotion/crossover{phase.ToLowerInvariant()}{side.ToLowerInvariant()}";
                string actual = ClipOf(machine, state);
                GD.Print($"[crossover-anim]   {state} -> {actual}");
                if (actual != expected)
                {
                    Fail($"crossover-polarity-content: '{state}' points at '{actual}', expected '{expected}'. " +
                         "This is the non-vacuous witness for a swapped Left/Right clip pair.");
                    pass = false;
                }
            }
        }
        if (pass) GD.Print("[crossover-anim] PASS crossover-polarity-content â€” every handed state maps to its same-origin clip; swapping a pair reddens this check.");
        Finish(pass ? 0 : 1);
    }

    private static string CanonicalBone(string name) => name.Replace("mixamorig:", "mixamorig_");

    private static float WorstBoneSeparationDeg(Animation a, Animation b, out int compared)
    {
        var left = RotationTracks(a);
        var right = RotationTracks(b);
        compared = 0;
        float worst = 0f;
        foreach (var (bone, ia) in left)
        {
            if (!right.TryGetValue(bone, out int ib)) continue;
            compared++;
            Quaternion qa = a.RotationTrackInterpolate(ia, 0f).Normalized();
            Quaternion qb = b.RotationTrackInterpolate(ib, 0f).Normalized();
            worst = MathF.Max(worst, Mathf.RadToDeg(qa.AngleTo(qb)));
        }
        return compared == 0 ? float.NaN : worst;
    }

    private static Dictionary<string, int> RotationTracks(Animation animation)
    {
        var tracks = new Dictionary<string, int>();
        for (int i = 0; i < animation.GetTrackCount(); i++)
        {
            if (animation.TrackGetType(i) != Animation.TrackType.Rotation3D) continue;
            NodePath path = animation.TrackGetPath(i);
            if (path.GetSubNameCount() == 1) tracks[CanonicalBone(path.GetSubName(0))] = i;
        }
        return tracks;
    }

    private static Skeleton3D FindSkeleton(Node root)
    {
        if (root is Skeleton3D skeleton) return skeleton;
        foreach (Node child in root.GetChildren())
        {
            Skeleton3D found = FindSkeleton(child);
            if (found != null) return found;
        }
        return null;
    }

    private static AnimationNodeStateMachine LoadStateMachine()
    {
        PackedScene playerScene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        SceneState state = playerScene.GetState();
        for (int i = 0; i < state.GetNodeCount(); i++)
        {
            if (state.GetNodeType(i) != "AnimationTree") continue;
            for (int p = 0; p < state.GetNodePropertyCount(i); p++)
                if (state.GetNodePropertyName(i, p) == "tree_root")
                    return state.GetNodePropertyValue(i, p).As<AnimationNodeStateMachine>();
        }
        return null;
    }

    private static string ClipOf(AnimationNodeStateMachine machine, string stateName) =>
        machine != null && machine.HasNode(stateName) && machine.GetNode(stateName) is AnimationNodeAnimation animation
            ? animation.Animation.ToString()
            : null;

    // ── Scenario: no-unsuffixed-crossover-state (control) ───────────────────
    private void VerdictNoUnsuffixed()
    {
        // Premise check: a crossover must genuinely have run (shown SOME
        // "Crossover*" state) before "never showed a bare unsuffixed one"
        // means anything — otherwise a rig stuck on Locomotion/Dribble the
        // whole time would trivially satisfy "never saw it."
        bool pass = _sawAnyCrossoverState && !_sawUnsuffixedCrossoverState;
        if (pass)
            GD.Print("[crossover-anim] PASS no-unsuffixed-crossover-state — the crossover genuinely ran " +
                     $"(observed [{string.Join(",", _distinctCrossoverStates)}]) and the tree was never " +
                     "observed on a bare \"CrossoverStartup\"/\"CrossoverActive\"/\"CrossoverRecovery\".");
        else
            Fail($"no-unsuffixed-crossover-state: expected the crossover to run genuinely " +
                 $"(sawAnyCrossoverState={_sawAnyCrossoverState}) AND never observe a bare unsuffixed state; " +
                 $"sawUnsuffixedCrossoverState={_sawUnsuffixedCrossoverState}, " +
                 $"distinctStates=[{string.Join(",", _distinctCrossoverStates)}]. If the premise broke, " +
                 "'no unsuffixed state observed' proves nothing, so this fails rather than passes.");
        Finish(pass ? 0 : 1);
    }

    private void Fail(string message) => GD.PrintErr($"[crossover-anim] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[crossover-anim] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
