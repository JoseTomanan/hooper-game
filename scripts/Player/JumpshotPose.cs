using System;
using System.Collections.Generic;
using Godot;
using Hooper.Moves;

namespace Hooper.Player;

/// <summary>Connects actual displayed poses within the shot's existing gather and fake windows.</summary>
public partial class JumpshotPose : SkeletonModifier3D
{
    public PlayerController Player { get; set; }
    private Skeleton3D _skeleton;
    private CrossoverPoseBridge _bridge;
    private Transform3D[] _lastFinal, _anchor;
    private int _previousFrame = -1;
    private MovePhase _previousPhase = MovePhase.Inactive;
    private bool _fakeRecovery;
    private long _clock = -1;
    private bool _buffered;
    private (int Epoch, int Resync) _reset;
    public bool AppliedForHarness { get; private set; }

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
        AppliedForHarness = false;
        if (_bridge == null || Player == null) return;
        var display = Player.AnimationDisplaySnapshot;
        bool buffered = display.PresentationTick >= 0;
        long clock = buffered ? display.PresentationTick : (long)Engine.GetPhysicsFrames();
        var reset = buffered ? Player.PivotPresentationReset : default;
        if (_clock >= 0 && (buffered != _buffered || reset != _reset || clock < _clock || clock - _clock > 1))
        {
            // A history reset/gap cannot establish a Startup-to-fake seam.
            // Rebase on this sampled pose, never carry a pre-reset anchor into
            // what may be the late landing of an ordinary shot (ADR-0027).
            _anchor = null; _fakeRecovery = false; _previousPhase = MovePhase.Inactive;
            _lastFinal = Capture();
            _bridge.ResetContinuity();
        }
        _clock = clock; _buffered = buffered; _reset = reset;
        bool startup = display.MoveId == "jumpshot" && display.Phase == MovePhase.Startup;
        bool recovery = display.MoveId == "jumpshot" && display.Phase == MovePhase.Recovery;
        bool newFake = recovery && _previousPhase == MovePhase.Startup && display.FrameInPhase >= 12;
        bool newStartup = startup && (_previousPhase != MovePhase.Startup || display.FrameInPhase < _previousFrame);
        if (newStartup || newFake)
        {
            _fakeRecovery = newFake;
            _anchor = (Transform3D[])_lastFinal.Clone();
            _bridge.ResetContinuity();
        }
        _previousPhase = display.MoveId == "jumpshot" ? display.Phase : MovePhase.Inactive;
        _previousFrame = display.FrameInPhase;
        if (!startup && !(recovery && _fakeRecovery))
        {
            _anchor = null; _fakeRecovery = false;
            return;
        }
        if (_anchor == null) return;
        // Spread the rotational gather and arm goals over eight Startup
        // intervals: the four-interval bridge concentrated finger-tip travel
        // into Startup2->3. Body origins and support goals keep their existing
        // four-interval settling so centering is complete before frame6.
        // All entry channels reach the authored pose by frame9, within Startup.
        // A fake skips Recovery's airborne head. Join its grounded tail from
        // the actual last Startup pose instead of snapping through a mixer fade.
        // Frame13 is the first ordinary post-Feint tick; frame19 must already
        // match neutral before the unchanged eight-tick Recovery finishes.
        float progress = startup ? Math.Clamp((display.FrameInPhase - 1) / 8f, 0f, 1f)
            : Math.Clamp((display.FrameInPhase - 13) / 6f, 0f, 1f);
        if (progress >= 1f) return;
        float support = startup ? Math.Clamp((display.FrameInPhase - 1) / 4f, 0f, 1f) : progress;
        float weight = progress * progress * (3f - 2f * progress);
        float positionWeight = startup ? support * support * (3f - 2f * support) : weight;
        var result = _bridge.Blend(_anchor, Capture(), weight, support, positionWeight).Pose;
        // Godot restores the unmodified animation pose automatically.
        // https://docs.godotengine.org/en/4.7/classes/class_skeletonmodifier3d.html#class-skeletonmodifier3d-method-process-modification-with-delta
        for (int i = 0; i < result.Length; i++)
        {
            _skeleton.SetBonePosePosition(i, result[i].Origin);
            _skeleton.SetBonePoseRotation(i, result[i].Basis.Orthonormalized().GetRotationQuaternion().Normalized());
            _skeleton.SetBonePoseScale(i, result[i].Basis.Scale);
        }
        AppliedForHarness = true;
    }
}
