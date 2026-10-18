using System;
using System.Collections.Generic;
using Godot;
using Hooper.Moves;

namespace Hooper.Player;

/// <summary>Reauthors crossover endpoints inside their existing display phase windows.</summary>
public partial class CrossoverPose : SkeletonModifier3D
{
    public PlayerController Player { get; set; }
    public AnimationPlayer AnimationPlayer { get; set; }

    private Skeleton3D _skeleton;
    private AnimationLibrary _library;
    private CrossoverPoseBridge _bridge;
    private Transform3D[] _lastFinal;
    private Transform3D[] _startupAnchor;
    private Transform3D[] _recoveryAnchor;
    private MovePhase _previousPhase;
    private int _previousFrame;
    private int _sampleFrame;
    private ulong _samplePhysicsFrame;
    private HandSide _origin;
    private readonly Dictionary<string, Clip> _clips = new();

    public bool AppliedForHarness { get; private set; }
    public float MaxReachErrorForHarness { get; private set; }
    public int ClampCountForHarness { get; private set; }
    public int SampleFrameForHarness => _sampleFrame;

    private sealed record Clip(Animation Animation, int[] Rotation, int[] Position, int[] Scale);

    public override void _Ready()
    {
        _skeleton = GetSkeleton();
        if (_skeleton == null || AnimationPlayer == null || !AnimationPlayer.HasAnimationLibrary("locomotion")) return;
        _library = AnimationPlayer.GetAnimationLibrary("locomotion");
        int count = _skeleton.GetBoneCount();
        var parents = new int[count];
        for (int i = 0; i < count; i++) parents[i] = _skeleton.GetBoneParent(i);
        var chains = new List<CrossoverPoseBridge.LimbChain>();
        foreach (string side in new[] { "Left", "Right" })
        {
            AddChain(chains, side + "UpLeg", side + "Leg", side + "Foot", isSupport: true);
            AddChain(chains, side + "Arm", side + "ForeArm", side + "Hand");
        }
        _bridge = new CrossoverPoseBridge(parents, chains.ToArray());
        _lastFinal = CaptureCurrent();
        // This signal observes the final pose after every modifier, including the other
        // postures. A new move must begin from what was displayed, at any neutral phase.
        // https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-signal-skeleton-updated
        _skeleton.SkeletonUpdated += CacheFinalPose;
    }

    public override void _ExitTree()
    {
        if (GodotObject.IsInstanceValid(_skeleton)) _skeleton.SkeletonUpdated -= CacheFinalPose;
    }

    private void AddChain(List<CrossoverPoseBridge.LimbChain> chains, string root, string middle, string effector,
        bool isSupport = false)
    {
        int h = _skeleton.FindBone("mixamorig_" + root);
        int k = _skeleton.FindBone("mixamorig_" + middle);
        int a = _skeleton.FindBone("mixamorig_" + effector);
        if (h >= 0 && k >= 0 && a >= 0) chains.Add(new(h, k, a, isSupport));
    }

    private Transform3D[] CaptureCurrent()
    {
        var pose = new Transform3D[_skeleton.GetBoneCount()];
        for (int i = 0; i < pose.Length; i++)
            pose[i] = new Transform3D(new Basis(_skeleton.GetBonePoseRotation(i)).Scaled(_skeleton.GetBonePoseScale(i)),
                _skeleton.GetBonePosePosition(i));
        return pose;
    }

    private void CacheFinalPose() => _lastFinal = CaptureCurrent();

    public override void _ProcessModificationWithDelta(double delta)
    {
        AppliedForHarness = false;
        MaxReachErrorForHarness = 0f;
        ClampCountForHarness = 0;
        if (_bridge == null || Player == null) return;
        var display = Player.AnimationDisplaySnapshot;
        if (display.MoveId != "crossover" || display.Phase == MovePhase.Inactive)
        {
            _previousPhase = MovePhase.Inactive;
            _sampleFrame = 0;
            return;
        }
        MoveAnimState state = MoveAnimResolver.Resolve(display.Phase);
        HandSide origin = MoveAnimResolver.OriginHand(state, display.Hand);
        bool changed = display.Phase != _previousPhase || display.FrameInPhase < _previousFrame || origin != _origin;
        if (changed)
        {
            _bridge.ResetContinuity();
            if (display.Phase == MovePhase.Startup)
                _startupAnchor = (Transform3D[])_lastFinal.Clone();
            if (display.Phase == MovePhase.Recovery)
                _recoveryAnchor = null;
        }
        var data = Crossover.DefaultFrameData;
        int frames = display.Phase switch
        {
            MovePhase.Startup => data.StartupFrames,
            MovePhase.Active => data.ActiveFrames,
            _ => data.RecoveryFrames
        };
        ulong physicsFrame = Engine.GetPhysicsFrames();
        // Buffered remote display has the complete source timeline: sample its exact
        // counter, including held frames. Before history initializes, preserve the
        // legacy observed-phase clock; repeated Advance(0) calls consume no time.
        // https://docs.godotengine.org/en/4.7/classes/class_engine.html#class-engine-method-get-physics-frames
        ulong elapsed = physicsFrame >= _samplePhysicsFrame ? physicsFrame - _samplePhysicsFrame : 0;
        _sampleFrame = display.PresentationTick >= 0 ? display.FrameInPhase : CrossoverPoseClock.NextFrame(_sampleFrame, display.FrameInPhase, frames,
            display.SimulatedLocally, changed, elapsed);
        _samplePhysicsFrame = physicsFrame;
        _origin = origin;
        _previousPhase = display.Phase;
        _previousFrame = display.FrameInPhase;
        float progress = Math.Clamp(_sampleFrame / (float)Math.Max(1, frames - 1), 0f, 1f);
        string name = "crossover" + display.Phase.ToString().ToLowerInvariant() + origin.ToString().ToLowerInvariant();
        Clip clip = GetClip(name);
        if (clip == null) return;
        Transform3D[] animated = Sample(clip, progress * clip.Animation.Length);
        Transform3D[] result = animated;
        if (display.Phase != MovePhase.Active)
        {
            float smooth = progress * progress * (3f - 2f * progress);
            Transform3D[] anchor;
            float weight;
            float? supportWeight = null;
            if (display.Phase == MovePhase.Startup)
            {
                anchor = _startupAnchor ?? _lastFinal;
                // Ease the upper-body read, while distributing an arbitrary incoming
                // support-foot displacement evenly over the five Startup intervals.
                weight = smooth;
                supportWeight = progress;
            }
            else
            {
                string destination = (origin == HandSide.Left ? HandSide.Right : HandSide.Left).ToString().ToLowerInvariant();
                // A lost ball voids the payload without cancelling the committed move.
                // Land in the neutral family the resolver will actually select afterwards.
                if (_recoveryAnchor == null)
                {
                    // Freeze this arc for the whole phase. A late steal/OOB must
                    // not replace an almost fully weighted anchor in one frame.
                    Clip neutral = GetClip(Player.DisplayDribbling() ? "dribbleidle" + destination : "idle");
                    if (neutral == null) return;
                    _recoveryAnchor = Sample(neutral, 0.0);
                }
                anchor = _recoveryAnchor;
                weight = 1f - smooth;
            }
            var bridge = _bridge.Blend(anchor, animated, weight, supportWeight);
            result = bridge.Pose;
            MaxReachErrorForHarness = bridge.MaxReachError;
            ClampCountForHarness = bridge.ClampCount;
        }
        // Godot runs this after AnimationMixer and restores the base automatically.
        // No shared clip/resource is mutated, and no pose survives onto another move.
        // https://docs.godotengine.org/en/4.7/classes/class_skeletonmodifier3d.html#class-skeletonmodifier3d-method-process-modification-with-delta
        for (int i = 0; i < result.Length; i++)
        {
            _skeleton.SetBonePosePosition(i, result[i].Origin);
            _skeleton.SetBonePoseRotation(i, result[i].Basis.Orthonormalized().GetRotationQuaternion().Normalized());
            _skeleton.SetBonePoseScale(i, result[i].Basis.Scale);
        }
        AppliedForHarness = true;
    }

    private Clip GetClip(string name)
    {
        if (_clips.TryGetValue(name, out var cached)) return cached;
        if (!_library.HasAnimation(name)) return null;
        Animation animation = _library.GetAnimation(name);
        int count = _skeleton.GetBoneCount();
        var rotations = new int[count];
        var positions = new int[count];
        var scales = new int[count];
        Array.Fill(rotations, -1); Array.Fill(positions, -1); Array.Fill(scales, -1);
        for (int track = 0; track < animation.GetTrackCount(); track++)
        {
            string[] path = animation.TrackGetPath(track).ToString().Split(':');
            if (path.Length < 2) continue;
            int bone = _skeleton.FindBone(path[1]);
            if (bone < 0) continue;
            switch (animation.TrackGetType(track))
            {
                case Animation.TrackType.Rotation3D: rotations[bone] = track; break;
                case Animation.TrackType.Position3D: positions[bone] = track; break;
                case Animation.TrackType.Scale3D: scales[bone] = track; break;
            }
        }
        var clip = new Clip(animation, rotations, positions, scales);
        _clips.Add(name, clip);
        return clip;
    }

    private Transform3D[] Sample(Clip clip, double time)
    {
        var result = new Transform3D[_skeleton.GetBoneCount()];
        for (int i = 0; i < result.Length; i++)
        {
            var rest = _skeleton.GetBoneRest(i);
            // Missing scale tracks retain RigScaler's actual proportions, rather than
            // silently replacing a customized rig with the raw imported rest scale.
            Vector3 scale = clip.Scale[i] >= 0 ? clip.Animation.ScaleTrackInterpolate(clip.Scale[i], time) : _skeleton.GetBonePoseScale(i);
            Quaternion q = clip.Rotation[i] >= 0 ? clip.Animation.RotationTrackInterpolate(clip.Rotation[i], time) : rest.Basis.Orthonormalized().GetRotationQuaternion();
            Vector3 p = clip.Position[i] >= 0 ? clip.Animation.PositionTrackInterpolate(clip.Position[i], time) : rest.Origin;
            // https://docs.godotengine.org/en/4.7/classes/class_animation.html#class-animation-method-rotation-track-interpolate
            // https://docs.godotengine.org/en/4.7/classes/class_animation.html#class-animation-method-position-track-interpolate
            result[i] = new Transform3D(new Basis(q.Normalized()).Scaled(scale), p);
        }
        return result;
    }
}
