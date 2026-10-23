using System;
using System.Collections.Generic;
using Godot;
using Hooper.Moves;

namespace Hooper.Player;

/// <summary>Possession-compatible catch presentation; never changes the ball or move machine.</summary>
public partial class ReboundPose : SkeletonModifier3D
{
    public PlayerController Player { get; set; }
    public AnimationPlayer AnimationPlayer { get; set; }

    // A presentation handoff, measured in consumed source ticks rather than renders.
    private const int BridgeTicks = 6;
    private Skeleton3D _skeleton;
    private int[] _parents;
    private CrossoverPoseBridge _bridge, _neutralBlend;
    private Transform3D[] _lastFinal, _anchor, _held;
    private bool _wasCatch, _exiting, _buffered, _discardIncoming;
    private long _clock = -1;
    private int _age;
    private double _neutralTime;
    private (int Epoch, int Resync) _reset;
    private readonly Dictionary<string, Clip> _clips = new();

    private sealed record Clip(Animation Animation, int[] Rotation, int[] Position, int[] Scale);

    public bool AppliedForHarness { get; private set; }
    public int AgeForHarness => _age;
    partial void AlterReboundClockForHarness(bool catchPose, int step);

    public override void _Ready()
    {
        _skeleton = GetSkeleton();
        if (_skeleton == null) return;
        _parents = new int[_skeleton.GetBoneCount()];
        for (int i = 0; i < _parents.Length; i++) _parents[i] = _skeleton.GetBoneParent(i);
        var chains = new List<CrossoverPoseBridge.LimbChain>();
        foreach (string side in new[] { "Left", "Right" })
        {
            AddChain(chains, side + "UpLeg", side + "Leg", side + "Foot", true);
            AddChain(chains, side + "Arm", side + "ForeArm", side + "Hand");
        }
        _bridge = new CrossoverPoseBridge(_parents, chains.ToArray());
        _neutralBlend = new CrossoverPoseBridge(_parents, chains.ToArray(), followAnchorPose: true);
        CacheFinalPose();
        // Read the final displayed result, after all modifiers, before Godot restores
        // the mixer input. A catch must start from the pose the viewer actually saw.
        // https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-signal-skeleton-updated
        _skeleton.SkeletonUpdated += CacheFinalPose;
    }

    public override void _ExitTree()
    {
        if (GodotObject.IsInstanceValid(_skeleton)) _skeleton.SkeletonUpdated -= CacheFinalPose;
    }

    private void AddChain(List<CrossoverPoseBridge.LimbChain> chains, string root, string middle,
        string end, bool support = false)
    {
        int a = _skeleton.FindBone("mixamorig_" + root);
        int b = _skeleton.FindBone("mixamorig_" + middle);
        int c = _skeleton.FindBone("mixamorig_" + end);
        if (a >= 0 && b >= 0 && c >= 0) chains.Add(new(a, b, c, support));
    }

    private Transform3D[] Capture()
    {
        var pose = new Transform3D[_parents.Length];
        for (int i = 0; i < pose.Length; i++)
            pose[i] = new Transform3D(new Basis(_skeleton.GetBonePoseRotation(i)).Scaled(_skeleton.GetBonePoseScale(i)),
                _skeleton.GetBonePosePosition(i));
        return pose;
    }

    private void CacheFinalPose() => _lastFinal = Capture();

    public override void _ProcessModificationWithDelta(double delta)
    {
        AppliedForHarness = false;
        if (_bridge == null || Player == null) return;
        var display = Player.AnimationDisplaySnapshot;
        bool catchPose = Player.ResolvedAnimationState == "ReboundGrab"
            && (Player.DisplayHeld() || Player.DisplayDribbling());
        bool buffered = display.PresentationTick >= 0;
        // The remote source can hold during refill. Neither neutral clip sampling
        // nor the bridge may consume local render time while that frame is held.
        // https://docs.godotengine.org/en/4.7/classes/class_engine.html#class-engine-method-get-physics-frames
        long clock = buffered ? display.PresentationTick : (long)Engine.GetPhysicsFrames();
        var reset = buffered ? Player.PivotPresentationReset : default;
        bool discontinuity = _clock >= 0 && (buffered != _buffered || reset != _reset
            || clock < _clock || clock - _clock > 1);
        int step = _clock < 0 || discontinuity ? 0 : (int)Math.Clamp(clock - _clock, 0, 1);
        _clock = clock; _buffered = buffered; _reset = reset;
        if (discontinuity)
        {
            bool interruptedCatch = _wasCatch && catchPose;
            Clear();
            // A resynchronized presentation explicitly permits a discontinuity;
            // do not blend a previous world/ownership context into the new one.
            _discardIncoming = interruptedCatch;
        }

        if (display.Phase != MovePhase.Inactive
            || (Player.ResolvedAnimationState == "ReboundGrab" && !catchPose))
        {
            Clear();
            return;
        }
        if (!catchPose && !_wasCatch && !_exiting)
        {
            _discardIncoming = false;
            // Preserve the incoming locomotion phase. Once travel selects Catch,
            // playback time is zero: using it would restart the moving dribble
            // on the opposite leg and make the entry bridge chase that phase jump.
            _neutralTime = Player.PivotAnimationTime;
            return;
        }
        if (step == 0 && _held != null) { Write(_held); return; }

        Transform3D[] fresh = Capture();
        if (catchPose && !_wasCatch)
        {
            _anchor = (Transform3D[])_lastFinal.Clone();
            _age = _discardIncoming ? BridgeTicks : 0;
            if (_discardIncoming) _neutralTime = Player.PivotAnimationTime;
            _discardIncoming = false;
            _exiting = false;
            _bridge.ResetContinuity();
            _neutralBlend.ResetContinuity();
        }
        else
        {
            // Keep this source clock continuous at the catch-to-neutral boundary.
            // The exit bridge targets the freshly evaluated neutral tree, rather
            // than abruptly substituting a neutral clip sampled at phase zero.
            // During entry the pose bridge already moves the limbs. Advancing
            // the destination gait as well can make it outrun that handoff when
            // the owner is simultaneously accelerating. Resume the authored
            // cycle from this phase once the incoming pose has reached it.
            if (!catchPose || _age >= BridgeTicks)
                _neutralTime += step / (double)Engine.PhysicsTicksPerSecond;
            if (!catchPose && _wasCatch)
            {
                _anchor = (Transform3D[])_lastFinal.Clone();
                _age = 0;
                _exiting = true;
                _bridge.ResetContinuity();
            }
            else _age = Math.Min(BridgeTicks, _age + step);
        }
        _wasCatch = catchPose;
        AlterReboundClockForHarness(catchPose, step);
        Transform3D[] target = catchPose ? SampleNeutral(fresh) : fresh;
        Transform3D[] result = _bridge.Blend(_anchor, target,
            Math.Clamp(_age / (float)BridgeTicks, 0, 1)).Pose;
        Write(result);
        _held = result;
        if (_exiting && _age >= BridgeTicks)
        {
            _exiting = false;
            _held = _anchor = null;
        }
    }

    private void Clear()
    {
        _wasCatch = _exiting = false;
        _anchor = _held = null;
        _age = 0;
        _bridge.ResetContinuity();
        _neutralBlend.ResetContinuity();
    }

    private Transform3D[] SampleNeutral(Transform3D[] fallback)
    {
        string hand = Player.DisplayHandSide.ToString().ToLowerInvariant();
        bool held = Player.DisplayHeld();
        string idle = held ? "held/idle" + hand : "locomotion/dribbleidle" + hand;
        string moving = held ? "held/moving" + hand : "locomotion/dribblemove" + hand;
        var idlePose = SampleClip(idle, fallback);
        var movingPose = SampleClip(moving, fallback);
        float speed = new Vector2(Player.DisplayVelocity.X, Player.DisplayVelocity.Z).Length();
        // Both authored endpoints move. Rest-relative BlendSpace accumulation can
        // collapse antipodal leg rotations; preserve the existing global-space
        // bridge's moving-anchor continuity and the contact-authored held palms.
        return _neutralBlend.Blend(idlePose, movingPose,
            Math.Clamp(speed / Math.Max(.001f, Player.MoveSpeed), 0, 1)).Pose;
    }

    private Transform3D[] SampleClip(string path, Transform3D[] fallback)
    {
        Clip clip = GetClip(path);
        if (clip == null) return fallback;
        double time = clip.Animation.LoopMode == Animation.LoopModeEnum.Linear && clip.Animation.Length > 0
            ? _neutralTime % clip.Animation.Length : Math.Min(_neutralTime, clip.Animation.Length);
        var result = new Transform3D[_parents.Length];
        for (int i = 0; i < result.Length; i++)
        {
            Transform3D rest = _skeleton.GetBoneRest(i);
            // Unkeyed scale channels retain the actual RigScaler proportions.
            Vector3 scale = clip.Scale[i] < 0 ? fallback[i].Basis.Scale : clip.Animation.ScaleTrackInterpolate(clip.Scale[i], time);
            Quaternion rotation = clip.Rotation[i] < 0 ? rest.Basis.Orthonormalized().GetRotationQuaternion()
                : clip.Animation.RotationTrackInterpolate(clip.Rotation[i], time);
            Vector3 position = clip.Position[i] < 0 ? rest.Origin : clip.Animation.PositionTrackInterpolate(clip.Position[i], time);
            // https://docs.godotengine.org/en/4.7/classes/class_animation.html#class-animation-method-rotation-track-interpolate
            // https://docs.godotengine.org/en/4.7/classes/class_animation.html#class-animation-method-position-track-interpolate
            result[i] = new Transform3D(new Basis(rotation.Normalized()).Scaled(scale), position);
        }
        return result;
    }

    private Clip GetClip(string path)
    {
        if (_clips.TryGetValue(path, out Clip cached)) return cached;
        if (AnimationPlayer == null || !AnimationPlayer.HasAnimation(path)) return null;
        Animation animation = AnimationPlayer.GetAnimation(path);
        var rotation = new int[_parents.Length];
        var position = new int[_parents.Length];
        var scale = new int[_parents.Length];
        Array.Fill(rotation, -1); Array.Fill(position, -1); Array.Fill(scale, -1);
        for (int track = 0; track < animation.GetTrackCount(); track++)
        {
            NodePath node = animation.TrackGetPath(track);
            // Installed GodotSharp 4.7.1 uses GetSubName (capital N).
            // https://docs.godotengine.org/en/4.7/classes/class_nodepath.html#class-nodepath-method-get-subname
            if (node.GetSubNameCount() != 1) continue;
            int bone = _skeleton.FindBone(node.GetSubName(0));
            if (bone < 0) continue;
            switch (animation.TrackGetType(track))
            {
                case Animation.TrackType.Rotation3D: rotation[bone] = track; break;
                case Animation.TrackType.Position3D: position[bone] = track; break;
                case Animation.TrackType.Scale3D: scale[bone] = track; break;
            }
        }
        var clip = new Clip(animation, rotation, position, scale);
        _clips.Add(path, clip);
        return clip;
    }

    private void Write(Transform3D[] pose)
    {
        // Modifiers run after AnimationMixer; Godot restores their input afterwards.
        // https://docs.godotengine.org/en/4.7/classes/class_skeletonmodifier3d.html
        for (int i = 0; i < pose.Length; i++)
        {
            _skeleton.SetBonePosePosition(i, pose[i].Origin);
            _skeleton.SetBonePoseRotation(i, pose[i].Basis.Orthonormalized().GetRotationQuaternion().Normalized());
            _skeleton.SetBonePoseScale(i, pose[i].Basis.Scale);
        }
        AppliedForHarness = true;
    }
}
