using System.Collections.Generic;
using Godot;
namespace HOOPERGAME.Tests.Integration;

// Resource mechanics only: callers retain their state names, expectations and verdicts.
public static class AnimationHarnessResources
{
    // Source: https://docs.godotengine.org/en/4.7/classes/class_node.html#class-node-method-get-children
    private static T Find<T>(Node root) where T : Node
    {
        if (root is T found) return found;
        if (root == null) return null;
        foreach (Node child in root.GetChildren())
        {
            T nested = Find<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }
    public static bool TryFindSkeleton(Node root, out Skeleton3D skeleton, out string diagnostic)
    {
        skeleton = Find<Skeleton3D>(root);
        diagnostic = skeleton == null ? "No Skeleton3D found below the supplied root." : "";
        return skeleton != null;
    }
    public static bool TryFindAnimationTree(Node root, out AnimationTree tree, out string diagnostic)
    {
        tree = Find<AnimationTree>(root);
        diagnostic = tree == null ? "No AnimationTree found below the supplied root." : "";
        return tree != null;
    }
    // Source: https://docs.godotengine.org/en/4.7/classes/class_resourceloader.html#class-resourceloader-method-load
    public static bool TryLoadLibrary(string path, out AnimationLibrary library, out string diagnostic)
    {
        // Source: https://docs.godotengine.org/en/4.7/classes/class_resourceloader.html#class-resourceloader-method-exists
        library = ResourceLoader.Exists(path) ? GD.Load(path) as AnimationLibrary : null;
        diagnostic = library == null ? $"Resource '{path}' is absent or is not an AnimationLibrary." : "";
        return library != null;
    }
    // The Variant read preserves the existing harness seconds precision across bindings.
    // Source: https://docs.godotengine.org/en/4.7/classes/class_animation.html#class-animation-property-length
    public static double ObserveDurationSeconds(Animation animation) => animation.Get("length").AsDouble();

    // Source: https://docs.godotengine.org/en/4.7/classes/class_animationlibrary.html#class-animationlibrary-method-has-animation
    public static bool TryGetAnimation(AnimationLibrary library, string name, out Animation animation, out string diagnostic)
    {
        animation = library != null && library.HasAnimation(name) ? library.GetAnimation(name) : null;
        diagnostic = animation == null ? $"AnimationLibrary has no animation '{name}'." : "";
        return animation != null;
    }
    // Source: https://docs.godotengine.org/en/4.7/classes/class_resourceloader.html#class-resourceloader-method-load
    public static bool TryLoadStateMachine(string path, out AnimationNodeStateMachine machine, out string diagnostic)
    {
        // Source: https://docs.godotengine.org/en/4.7/classes/class_resourceloader.html#class-resourceloader-method-exists
        var scene = ResourceLoader.Exists(path) ? GD.Load(path) as PackedScene : null;
        if (scene == null)
        { machine = null; diagnostic = $"Resource '{path}' is absent or is not a PackedScene."; return false; }
        return TryFindStateMachine(scene.GetState(), out machine, out diagnostic);
    }
    // Inspect serialized properties without instantiating gameplay nodes.
    // Source: https://docs.godotengine.org/en/4.7/classes/class_scenestate.html#class-scenestate-method-get-node-property-value
    public static bool TryFindStateMachine(SceneState state, out AnimationNodeStateMachine machine, out string diagnostic)
    {
        machine = null;
        if (state != null)
            for (int i = 0; i < state.GetNodeCount(); i++)
            {
                if (state.GetNodeType(i) != "AnimationTree") continue;
                for (int p = 0; p < state.GetNodePropertyCount(i); p++)
                {
                    if (state.GetNodePropertyName(i, p) != "tree_root") continue;
                    machine = state.GetNodePropertyValue(i, p).AsGodotObject() as AnimationNodeStateMachine;
                    if (machine != null) { diagnostic = ""; return true; }
                    diagnostic = "AnimationTree tree_root is not an AnimationNodeStateMachine.";
                    return false;
                }
            }
        diagnostic = "SceneState has no AnimationTree with an AnimationNodeStateMachine tree_root.";
        return false;
    }
    // Associations map qualified clip names to their explicit library AND local name.
    // This permits different naming layouts and never derives library identity from a slash.
    public readonly record struct ClipSource(AnimationLibrary Library, string AnimationName);
    public enum StateClipStatus { Correct, MissingState, NonAnimationState, MissingClip, Placeholder, WrongClip }
    public readonly record struct StateClipInspection(StateClipStatus Status, string ActualClip, string Diagnostic);

    // Source: https://docs.godotengine.org/en/4.7/classes/class_animationnodestatemachine.html#class-animationnodestatemachine-method-has-node
    public static StateClipInspection InspectStateClip(AnimationNodeStateMachine machine, string stateName,
        string expectedQualifiedClip, IReadOnlyCollection<string> placeholders,
        IReadOnlyDictionary<string, ClipSource> clips)
    {
        if (machine == null || !machine.HasNode(stateName))
            return new(StateClipStatus.MissingState, "", $"State machine has no state '{stateName}', expected '{expectedQualifiedClip}'.");
        if (machine.GetNode(stateName) is not AnimationNodeAnimation node)
            return new(StateClipStatus.NonAnimationState, "", $"State '{stateName}' is not an AnimationNodeAnimation, expected '{expectedQualifiedClip}'.");
        string actual = node.Animation.ToString();
        if (!clips.TryGetValue(actual, out var source) ||
            !TryGetAnimation(source.Library, source.AnimationName, out _, out string diagnostic))
            return new(StateClipStatus.MissingClip, actual, $"State '{stateName}' references missing clip '{actual}', expected '{expectedQualifiedClip}'.");
        foreach (string placeholder in placeholders)
            if (actual == placeholder)
                return new(StateClipStatus.Placeholder, actual, $"State '{stateName}' references placeholder '{actual}', expected '{expectedQualifiedClip}'.");
        if (actual == expectedQualifiedClip) return new(StateClipStatus.Correct, actual, "");
        return new(StateClipStatus.WrongClip, actual, $"State '{stateName}' references '{actual}', expected '{expectedQualifiedClip}'.");
    }
}
