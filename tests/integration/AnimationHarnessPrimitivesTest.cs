using System;
using System.Collections.Generic;
using Godot;
namespace HOOPERGAME.Tests.Integration;

// Synthetic resources deliberately use qualified names unrelated to their local names.
// These checks characterize helper mechanics, not any move's pose or acceptance gate.
public partial class AnimationHarnessPrimitivesTest : Node
{
    public override void _Ready()
    {
        string scenario = HarnessArgs.ReadArg(OS.GetCmdlineUserArgs(), "--harness-scenario", "primitives-discovery");
        var report = new HarnessReport("animation-primitives", scenario);
        GD.Print($"[animation-primitives] scenario={scenario} booting headless...");
        int code = 0;
        try
        {
            switch (scenario)
            {
                case "primitives-discovery": CheckDiscovery(); break;
                case "primitives-resources": CheckResources(); break;
                case "primitives-state-clips": CheckStateClips(); break;
                case "primitives-report-failure":
                    GD.PrintErr(report.Failure("intentional reporting characterization"));
                    code = 7;
                    break;
                default: throw new InvalidOperationException($"Unknown scenario '{scenario}'.");
            }
        }
        catch (Exception error) { GD.PrintErr(report.Failure(error.Message)); code = 1; }
        GD.Print(report.Result(code));
        // Source: https://docs.godotengine.org/en/4.7/classes/class_scenetree.html#class-scenetree-method-quit
        GetTree().Quit(code);
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static void CheckDiscovery()
    {
        var root = new Node();
        try
        {
            var nested = new Node(); root.AddChild(nested);
            var skeleton = new Skeleton3D(); nested.AddChild(skeleton);
            var tree = new AnimationTree(); nested.AddChild(tree);
            Require(AnimationHarnessResources.TryFindSkeleton(root, out var found, out _) && found == skeleton,
                "nested Skeleton3D identity was not returned");
            Require(AnimationHarnessResources.TryFindAnimationTree(root, out var foundTree, out _) && foundTree == tree,
                "nested AnimationTree identity was not returned");
            Require(AnimationHarnessResources.TryFindSkeleton(skeleton, out found, out _) && found == skeleton,
                "root Skeleton3D was skipped");
        }
        finally { root.Free(); }
        var empty = new Node();
        try
        {
            Require(!AnimationHarnessResources.TryFindSkeleton(empty, out var skeleton, out var diagnostic) &&
                    skeleton == null && diagnostic.Contains("Skeleton3D"), "absent skeleton was silently accepted");
            Require(!AnimationHarnessResources.TryFindAnimationTree(empty, out var tree, out diagnostic) &&
                    tree == null && diagnostic.Contains("AnimationTree"), "absent tree was silently accepted");
        }
        finally { empty.Free(); }
    }
    // Source: https://docs.godotengine.org/en/4.7/classes/class_packedscene.html#class-packedscene-method-pack
    // Source: https://docs.godotengine.org/en/4.7/classes/class_node.html#class-node-property-owner
    private static PackedScene PackTree(AnimationRootNode machine)
    {
        var root = new Node();
        try
        {
            var nested = new Node { Name = "Nested" }; root.AddChild(nested); nested.Owner = root;
            var tree = new AnimationTree { Name = "Tree", TreeRoot = machine };
            nested.AddChild(tree); tree.Owner = root;
            var scene = new PackedScene();
            Require(scene.Pack(root) == Error.Ok, "synthetic scene failed to pack");
            return scene;
        }
        finally { root.Free(); }
    }
    private static void CheckResources()
    {
        using var machine = new AnimationNodeStateMachine();
        using var scene = PackTree(machine);
        Require(AnimationHarnessResources.TryFindStateMachine(scene.GetState(), out var found, out _) && found == machine,
            "serialized nested state machine identity was not returned");
        using var wrongScene = PackTree(new AnimationNodeBlendTree());
        Require(!AnimationHarnessResources.TryFindStateMachine(wrongScene.GetState(), out found, out var diagnostic) &&
                found == null && diagnostic.Contains("tree_root"), "non-state-machine tree root was accepted");
        var root = new Node();
        using var emptyScene = new PackedScene(); emptyScene.Pack(root); root.Free();
        Require(!AnimationHarnessResources.TryFindStateMachine(emptyScene.GetState(), out found, out diagnostic) &&
                diagnostic.Contains("AnimationTree"), "absent serialized tree was accepted");
        Require(!AnimationHarnessResources.TryLoadLibrary("res://tests/integration/StepBackAnimTest.tscn", out var wrong, out diagnostic) &&
                wrong == null && diagnostic.Contains("AnimationLibrary"), "wrong resource type was accepted as a library");
        Require(!AnimationHarnessResources.TryLoadLibrary("res://tests/integration/absent-issue390.res", out wrong, out diagnostic) &&
                wrong == null && diagnostic.Contains("absent-issue390"), "absent library was accepted");
        Require(!AnimationHarnessResources.TryLoadStateMachine("res://assets/locomotion.res", out found, out diagnostic) &&
                diagnostic.Contains("PackedScene"), "wrong resource type was accepted as a scene");
        using var library = new AnimationLibrary(); using var animation = new Animation { Length = 0.375 };
        double seconds = AnimationHarnessResources.ObserveDurationSeconds(animation);
        Require(seconds == 0.375 && ClipDuration.TicksForSeconds(seconds, 120) == 45 &&
                ClipDuration.SecondsForTicks(45, 120) == seconds, "observed duration lost seconds or explicit-rate conversion");
        library.AddAnimation("local", animation);
        Require(AnimationHarnessResources.TryGetAnimation(library, "local", out var actual, out _) && actual == animation,
            "named animation identity was not returned");
        Require(!AnimationHarnessResources.TryGetAnimation(library, "missing", out actual, out diagnostic) && actual == null &&
                diagnostic.Contains("missing"), "absent named animation was accepted");
        Require(!AnimationHarnessResources.TryGetAnimation(null, "local", out actual, out diagnostic) && actual == null &&
                diagnostic.Contains("local"), "absent library was accepted for named animation lookup");
    }
    private static void CheckStateClips()
    {
        using var library = new AnimationLibrary();
        foreach (string local in new[] { "expected-local", "wrong-local", "idle-local" })
            library.AddAnimation(local, new Animation());
        var clips = new Dictionary<string, AnimationHarnessResources.ClipSource>
        {
            ["custom/expected"] = new(library, "expected-local"),
            ["custom/wrong"] = new(library, "wrong-local"),
            ["custom/idle"] = new(library, "idle-local"),
            ["custom/missing"] = new(library, "not-in-library"),
        };
        string[] placeholders = { "custom/idle" };
        using var machine = new AnimationNodeStateMachine();
        using var node = new AnimationNodeAnimation { Animation = "custom/expected" };
        machine.AddNode("Move", node);
        void Check(AnimationHarnessResources.StateClipStatus expected, string clip, string expectation = "custom/expected")
        {
            node.Animation = clip;
            var result = AnimationHarnessResources.InspectStateClip(machine, "Move", expectation, placeholders, clips);
            Require(result.Status == expected && result.ActualClip == clip,
                $"mapping {clip}: got {result.Status}, expected {expected}");
            if (expected != AnimationHarnessResources.StateClipStatus.Correct)
                Require(result.Diagnostic.Contains("Move") && result.Diagnostic.Contains(clip), "mapping diagnostic lost identity");
        }
        Check(AnimationHarnessResources.StateClipStatus.Correct, "custom/expected");
        Check(AnimationHarnessResources.StateClipStatus.WrongClip, "custom/wrong");
        Check(AnimationHarnessResources.StateClipStatus.Placeholder, "custom/idle");
        Check(AnimationHarnessResources.StateClipStatus.Placeholder, "custom/idle", "custom/idle");
        Check(AnimationHarnessResources.StateClipStatus.MissingClip, "custom/unassociated");
        Check(AnimationHarnessResources.StateClipStatus.MissingClip, "custom/missing");
        Check(AnimationHarnessResources.StateClipStatus.MissingClip, "custom/missing", "custom/missing");
        var absent = AnimationHarnessResources.InspectStateClip(machine, "Absent", "custom/expected", placeholders, clips);
        Require(absent.Status == AnimationHarnessResources.StateClipStatus.MissingState && absent.Diagnostic.Contains("Absent"),
            "missing state was not distinguished");
        machine.AddNode("Blend", new AnimationNodeBlendTree());
        var blend = AnimationHarnessResources.InspectStateClip(machine, "Blend", "custom/expected", placeholders, clips);
        Require(blend.Status == AnimationHarnessResources.StateClipStatus.NonAnimationState && blend.Diagnostic.Contains("Blend"),
            "non-animation state was not distinguished");
    }
}
