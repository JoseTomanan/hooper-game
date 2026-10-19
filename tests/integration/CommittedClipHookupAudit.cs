using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Godot;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Diagnostic forced-state hookup audit. This deliberately bypasses input, move
// legality and Travel routing; a pass establishes none of those gameplay paths
// and makes no claim about animation quality.
public partial class CommittedClipHookupAudit : Node
{
    private sealed record Family(string Id, string Prefix, string Clip, bool OriginHanded = false, bool ReachHanded = false);
    private static readonly Family[] Families =
    {
        new("jumpshot", "Jumpshot", "jumpshot"), new("turnaround", "Turnaround", "turnaround"),
        new("crossover", "Crossover", "crossover", true), new("behindtheback", "BehindTheBack", "behindtheback", true),
        new("betweenthelegs", "BetweenTheLegs", "betweenthelegs", true), new("steal", "Steal", "steal", false, true),
        new("block", "Block", "block"), new("contest", "Contest", "contest"), new("jab", "JabStep", "jabstep"),
        new("inandout", "InAndOut", "inandout"), new("retreatdribble", "RetreatDribble", "retreatdribble"),
        new("stepback", "StepBack", "stepback"), new("hesitation", "Hesitation", "hesitation"),
        new("spin", "Spin", "spin"), new("drivegather", "DriveGather", "drivegather"),
        new("eurostep", "EuroStep", "eurostep"), new("layup", "Layup", "layup"),
        new("__unclipped__", "", "generic")
    };
    private readonly List<object> _rows = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private string _output = "";
    private string _commit = "unknown";

    public override void _Ready()
    {
        try
        {
            string requested = HarnessArgs.ReadArg(OS.GetCmdlineUserArgs(), "--audit-output", ".godot/committed-clip-hookup-audit.json");
            _commit = HarnessArgs.ReadArg(OS.GetCmdlineUserArgs(), "--audit-commit", "unknown");
            string allowed = Path.GetFullPath(ProjectSettings.GlobalizePath("res://.godot")) + Path.DirectorySeparatorChar;
            _output = Path.GetFullPath(ProjectSettings.GlobalizePath(requested.StartsWith("res://", StringComparison.Ordinal) ? requested : "res://" + requested));
            if (!_output.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("--audit-output must be a file below .godot");

            var player = GD.Load<PackedScene>("res://scenes/Player.tscn").Instantiate<PlayerController>();
            player.Name = "1";
            AddChild(player);
            // Stop the production animation selector from overwriting forced states.
            // https://docs.godotengine.org/en/4.7/classes/class_node.html#class-node-method-set-process
            // https://docs.godotengine.org/en/4.7/classes/class_node.html#class-node-method-set-physics-process
            player.SetProcess(false);
            player.SetPhysicsProcess(false);
            var tree = player.GetNode<AnimationTree>("AnimationTree");
            var animationPlayer = player.GetNode<AnimationPlayer>("AnimationPlayer");
            if (!AnimationHarnessResources.TryFindSkeleton(player, out var skeleton, out string diagnostic)) throw new InvalidOperationException(diagnostic);
            // Manual mode requires explicit Advance calls.
            // https://docs.godotengine.org/en/4.7/classes/class_animationmixer.html#class-animationmixer-method-advance
            tree.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
            tree.Active = true;
            var playback = tree.Get("parameters/playback").As<AnimationNodeStateMachinePlayback>();
            var graph = (AnimationNodeStateMachine)tree.TreeRoot;
            var trackRoot = tree.GetNode(tree.RootNode);
            bool allPassed = true;
            foreach (var family in Families)
            {
                foreach (HandSide side in family.OriginHanded || family.ReachHanded ? new[] { HandSide.Left, HandSide.Right } : new[] { HandSide.Right })
                foreach (MovePhase phase in new[] { MovePhase.Startup, MovePhase.Active, MovePhase.Recovery })
                {
                    if (_clock.Elapsed.TotalSeconds > 20) throw new TimeoutException("Audit exceeded 20 seconds");
                    var currentHand = family.OriginHanded && phase != MovePhase.Startup ? Opposite(side) : side;
                    string suffix = family.OriginHanded || family.ReachHanded ? side.ToString() : "";
                    string expectedState = family.Prefix + phase + suffix;
                    string expectedClip = (family.Id == "turnaround" ? "turnaround/" : "locomotion/") + family.Clip + phase.ToString().ToLowerInvariant() + suffix.ToLowerInvariant();
                    allPassed &= Observe(family.Id, phase, side, currentHand, false, expectedState, expectedClip, tree, animationPlayer, graph, playback, trackRoot, skeleton);
                }
            }
            allPassed &= Observe("jumpshot", MovePhase.Active, HandSide.Right, HandSide.Right, true, "FadeawayActive", "locomotion/fadeawayactive", tree, animationPlayer, graph, playback, trackRoot, skeleton);
            WriteResult(allPassed, "");
            GD.Print($"[committed-clip-hookup-audit] {(allPassed ? "PASS" : "FAIL")} rows={_rows.Count} output={_output}; forced states only");
            GetTree().Quit(allPassed ? 0 : 1);
        }
        catch (Exception exception)
        {
            if (_output.Length > 0) WriteResult(false, exception.ToString());
            GD.PushError($"[committed-clip-hookup-audit] FAIL {exception}");
            GetTree().Quit(1);
        }
    }

    private bool Observe(string move, MovePhase phase, HandSide side, HandSide currentHand, bool fadeaway,
        string expectedState, string expectedClip, AnimationTree tree, AnimationPlayer animationPlayer,
        AnimationNodeStateMachine graph, AnimationNodeStateMachinePlayback playback, Node trackRoot, Skeleton3D skeleton)
    {
        string resolved = MoveAnimResolver.ResolveStateName(MoveAnimResolver.Resolve(phase, isFadeaway: fadeaway), move, currentHand, side);
        string enteredAtZero = "", enteredAtHalf = "", actualClip = "", resource = "";
        int tracks = 0, valid = 0;
        double halfSeconds = 0;
        var invalid = new List<string>();
        if (graph.HasNode(resolved) && graph.GetNode(resolved) is AnimationNodeAnimation node)
        {
            actualClip = node.Animation.ToString();
            if (animationPlayer.HasAnimation(node.Animation))
            {
                Animation animation = animationPlayer.GetAnimation(node.Animation);
                resource = animation.ResourcePath;
                // https://docs.godotengine.org/en/4.7/classes/class_animation.html#class-animation-method-get-track-count
                // https://docs.godotengine.org/en/4.7/classes/class_animation.html#class-animation-method-track-get-path
                // https://docs.godotengine.org/en/4.7/classes/class_node.html#class-node-method-get-node
                tracks = animation.GetTrackCount();
                for (int i = 0; i < tracks; i++)
                {
                    NodePath path = animation.TrackGetPath(i);
                    bool skeletal = animation.TrackGetType(i) is Animation.TrackType.Rotation3D or Animation.TrackType.Position3D or Animation.TrackType.Scale3D;
                    if (skeletal && path.GetSubNameCount() == 1 && trackRoot.GetNodeOrNull(new NodePath(path.GetConcatenatedNames())) == skeleton
                        && skeleton.FindBone(path.GetSubName(0)) >= 0 && animation.TrackGetKeyCount(i) > 0) valid++;
                    else invalid.Add(path.ToString());
                }
                // Start explicitly bypasses graph travel: this is a hookup audit.
                // https://docs.godotengine.org/en/4.7/classes/class_animationnodestatemachineplayback.html#class-animationnodestatemachineplayback-method-start
                playback.Start(resolved, true);
                tree.Advance(0);
                enteredAtZero = playback.GetCurrentNode().ToString();
                halfSeconds = animation.Length / 2;
                tree.Advance(halfSeconds);
                enteredAtHalf = playback.GetCurrentNode().ToString();
            }
        }
        bool pass = resolved == expectedState && enteredAtZero == expectedState && enteredAtHalf == expectedState
            && actualClip == expectedClip && tracks > 0 && valid > 0 && invalid.Count == 0;
        _rows.Add(new { move, phase = phase.ToString(), side = side.ToString(), currentHand = currentHand.ToString(), fadeaway,
            expectedState, resolvedState = resolved, enteredAtZero, enteredAtHalf, expectedClip, actualClip,
            animationResourcePath = resource, trackCount = tracks, validSkeletonTrackBindingCount = valid,
            invalidTrackPaths = invalid, halfSeconds, pass });
        return pass;
    }

    private void WriteResult(bool passed, string error)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_output)!);
        File.WriteAllText(_output, JsonSerializer.Serialize(new { audit = "forced-state-committed-clip-hookup", passed,
            inputCommit = _commit, engineVersion = Engine.GetVersionInfo()["string"].AsString(),
            scope = "Manual state entry and clip/skeleton binding; no gameplay input-route or visual-quality proof", error, rows = _rows }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static HandSide Opposite(HandSide side) => side == HandSide.Left ? HandSide.Right : HandSide.Left;
}
