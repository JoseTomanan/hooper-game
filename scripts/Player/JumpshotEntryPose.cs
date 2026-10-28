using System;
using System.Collections.Generic;
using Godot;
using Hooper.Moves;

namespace Hooper.Player;

/// <summary>Connects the displayed neutral pose to the normal shot's existing gather.</summary>
public partial class JumpshotEntryPose : SkeletonModifier3D
{
    public PlayerController Player { get; set; }
    private Skeleton3D _skeleton;
    private CrossoverPoseBridge _bridge;
    private Transform3D[] _lastFinal, _anchor;
    private int _previousFrame = -1;

    public override void _Ready()
    {
        _skeleton = GetSkeleton();
        if (_skeleton == null) return;
        var parents = new int[_skeleton.GetBoneCount()];
        for (int i = 0; i < parents.Length; i++) parents[i] = _skeleton.GetBoneParent(i);
        var chains = new List<CrossoverPoseBridge.LimbChain>();
        foreach (string side in new[] { "Left", "Right" })
        {
            AddChain(side + "UpLeg", side + "Leg", side + "Foot", true);
            AddChain(side + "Arm", side + "ForeArm", side + "Hand", false);
        }
        _bridge = new CrossoverPoseBridge(parents, chains.ToArray());
        _lastFinal = Capture();
        // Anchor to the pose actually skinned after every other modifier.
        // https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-signal-skeleton-updated
        _skeleton.SkeletonUpdated += CacheFinal;

        void AddChain(string root, string middle, string end, bool support)
        {
            int a = _skeleton.FindBone("mixamorig_" + root);
            int b = _skeleton.FindBone("mixamorig_" + middle);
            int c = _skeleton.FindBone("mixamorig_" + end);
            if (a >= 0 && b >= 0 && c >= 0) chains.Add(new(a, b, c, support));
        }
    }

    public override void _ExitTree()
    {
        if (GodotObject.IsInstanceValid(_skeleton)) _skeleton.SkeletonUpdated -= CacheFinal;
    }

    private Transform3D[] Capture()
    {
        var pose = new Transform3D[_skeleton.GetBoneCount()];
        for (int i = 0; i < pose.Length; i++)
            pose[i] = new Transform3D(new Basis(_skeleton.GetBonePoseRotation(i)).Scaled(_skeleton.GetBonePoseScale(i)),
                _skeleton.GetBonePosePosition(i));
        return pose;
    }

    private void CacheFinal() => _lastFinal = Capture();

    public override void _ProcessModificationWithDelta(double delta)
    {
        if (_bridge == null || Player == null) return;
        var display = Player.AnimationDisplaySnapshot;
        if (display.MoveId != "jumpshot" || display.Phase != MovePhase.Startup)
        {
            _anchor = null; _previousFrame = -1;
            return;
        }
        if (_anchor == null || display.FrameInPhase < _previousFrame)
        {
            _anchor = (Transform3D[])_lastFinal.Clone();
            _bridge.ResetContinuity();
        }
        _previousFrame = display.FrameInPhase;
        // Four intervals within Startup, independent of render frequency or
        // held remote ticks. Preserve the actual incoming feet/hands and use
        // the existing limb bridge to avoid rest-relative mixer antipodes.
        float progress = Math.Clamp((display.FrameInPhase - 1) / 4f, 0f, 1f);
        if (progress >= 1f) return;
        float smooth = progress * progress * (3f - 2f * progress);
        var result = _bridge.Blend(_anchor, Capture(), smooth, progress).Pose;
        // Godot restores the unmodified animation pose automatically.
        // https://docs.godotengine.org/en/4.7/classes/class_skeletonmodifier3d.html#class-skeletonmodifier3d-method-process-modification-with-delta
        for (int i = 0; i < result.Length; i++)
        {
            _skeleton.SetBonePosePosition(i, result[i].Origin);
            _skeleton.SetBonePoseRotation(i, result[i].Basis.Orthonormalized().GetRotationQuaternion().Normalized());
            _skeleton.SetBonePoseScale(i, result[i].Basis.Scale);
        }
    }
}
