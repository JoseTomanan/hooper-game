using System;
using System.Collections.Generic;
using Godot;

/// <summary>Resource-level proof for #312's per-move Euro-step clip family.</summary>
public partial class EuroStepAnimTest : Node
{
    public override void _Ready()
    {
        var lib = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        bool pass = lib != null;
        string[] names = { "eurostepstartup", "eurostepactive", "eurosteprecovery" };
        int[] ticks = { 6, 14, 16 };
        for (int i = 0; i < names.Length; i++)
        {
            if (lib == null || !lib.HasAnimation(names[i])) { GD.PrintErr($"[eurostep-anim] missing {names[i]}"); pass = false; continue; }
            double got = lib.GetAnimation(names[i]).Length;
            double expected = ticks[i] / (double)Engine.PhysicsTicksPerSecond;
            GD.Print($"[eurostep-anim] {names[i]}={got:F6}s expected={expected:F6}s");
            if (Math.Abs(got - expected) > 1.0 / Engine.PhysicsTicksPerSecond) pass = false;
        }
        pass &= CheckTree();
        pass &= CheckMidlineTwice(lib);
        GD.Print($"[eurostep-anim] RESULT: {(pass ? "PASS" : "FAIL")}");
        GetTree().Quit(pass ? 0 : 1);
    }

    private static bool CheckTree()
    {
        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        var state = scene.GetState();
        AnimationNodeStateMachine machine = null;
        for (int i = 0; i < state.GetNodeCount(); i++)
            for (int p = 0; p < state.GetNodePropertyCount(i); p++)
                if (state.GetNodePropertyName(i, p) == "tree_root") machine = state.GetNodePropertyValue(i, p).As<AnimationNodeStateMachine>();
        if (machine == null) return false;
        var clips = new Dictionary<string, string> { ["EuroStepStartup"] = "locomotion/eurostepstartup", ["EuroStepActive"] = "locomotion/eurostepactive", ["EuroStepRecovery"] = "locomotion/eurosteprecovery" };
        bool ok = true;
        foreach (var pair in clips)
        {
            if (!machine.HasNode(pair.Key) || machine.GetNode(pair.Key) is not AnimationNodeAnimation node || node.Animation.ToString() != pair.Value)
            { GD.PrintErr($"[eurostep-anim] placeholder/wrong state {pair.Key}"); ok = false; }
        }
        var required = new[] { ("Locomotion","EuroStepStartup"), ("EuroStepStartup","EuroStepActive"), ("EuroStepActive","EuroStepRecovery"), ("EuroStepRecovery","Locomotion"), ("EuroStepStartup","EuroStepRecovery"), ("EuroStepStartup","Locomotion"), ("DribbleLeft","EuroStepStartup"), ("DribbleRight","EuroStepStartup"), ("EuroStepRecovery","DribbleLeft"), ("EuroStepRecovery","DribbleRight"), ("EuroStepStartup","DribbleLeft"), ("EuroStepStartup","DribbleRight") };
        var found = new HashSet<string>();
        for (int i = 0; i < machine.GetTransitionCount(); i++) found.Add($"{machine.GetTransitionFrom(i)}->{machine.GetTransitionTo(i)}");
        foreach (var edge in required) if (!found.Contains($"{edge.Item1}->{edge.Item2}")) ok = false;
        return ok;
    }

    private static bool CheckMidlineTwice(AnimationLibrary lib)
    {
        if (lib == null || !lib.HasAnimation("eurostepactive")) return false;
        var clip = lib.GetAnimation("eurostepactive");
        int track = -1;
        for (int i = 0; i < clip.GetTrackCount(); i++)
            if (clip.TrackGetType(i) == Animation.TrackType.Position3D && clip.TrackGetPath(i).GetSubNameCount() > 0 && clip.TrackGetPath(i).GetSubName(0).Contains("Hips")) track = i;
        if (track < 0) return false;
        int changes = 0, previous = 0;
        for (int tick = 0; tick <= 14; tick++)
        {
            float lateral = clip.PositionTrackInterpolate(track, tick / 60f).X;
            int sign = lateral > .01f ? 1 : lateral < -.01f ? -1 : 0;
            if (sign != 0 && previous != 0 && sign != previous) changes++;
            if (sign != 0) previous = sign;
        }
        GD.Print($"[eurostep-anim] active midline changes={changes}");
        return changes >= 2;
    }
}
