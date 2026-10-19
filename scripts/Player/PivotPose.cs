using System;
using System.Collections.Generic;
using Godot;
using Hooper.Moves;

namespace Hooper.Player;

/// <summary>Pivot-specific landing, world toe contact and neutral handoff. Never moves the body collider.</summary>
public partial class PivotPose : SkeletonModifier3D
{
    public PlayerController Player { get; set; }
    public AnimationPlayer AnimationPlayer { get; set; }
    // Six intervals distribute the reproduced running/dribble endpoint gaps;
    // see docs/analysis/0416-pivot-continuity.md. This is not a move/heading timer.
    private const int BridgeTicks = 6;
    private const int LandingTicks = 4;
    private Skeleton3D _skeleton;
    private int[] _parents;
    private int _hips, _toe;
    private CrossoverPoseBridge.LimbChain _leg;
    private CrossoverPoseBridge _bridge;
    private CrossoverPoseBridge _neutralBridge;
    private Transform3D[] _last, _anchor, _held;
    private Transform3D[] _pivotTarget;
    private Transform3D _lastWorld;
    private Vector3 _contact, _landingStart;
    private bool _wasPivot, _exiting;
    private bool _correctDribbleBlend;
    private long _clock = -1;
    private int _age;
    private (int Epoch, int Resync) _reset;
    private bool _buffered;

    public bool AppliedForHarness { get; private set; }
    public string SupportToeForHarness => _toe >= 0 && _skeleton != null ? _skeleton.GetBoneName(_toe) : "";
    public int AgeForHarness => _age;
    public float ContactErrorForHarness { get; private set; }
    public float PelvisShiftForHarness { get; private set; }

    public override void _Ready()
    {
        _skeleton = GetSkeleton();
        if (_skeleton == null) return;
        _parents = new int[_skeleton.GetBoneCount()];
        for (int i = 0; i < _parents.Length; i++) _parents[i] = _skeleton.GetBoneParent(i);
        _hips = _skeleton.FindBone("mixamorig_Hips");
        _toe = -1;
        var chains = new List<CrossoverPoseBridge.LimbChain>();
        foreach (string side in new[] { "Left", "Right" })
        {
            chains.Add(new(_skeleton.FindBone("mixamorig_" + side + "UpLeg"),
                _skeleton.FindBone("mixamorig_" + side + "Leg"), _skeleton.FindBone("mixamorig_" + side + "Foot"), true));
            chains.Add(new(_skeleton.FindBone("mixamorig_" + side + "Arm"),
                _skeleton.FindBone("mixamorig_" + side + "ForeArm"), _skeleton.FindBone("mixamorig_" + side + "Hand")));
        }
        _bridge = new CrossoverPoseBridge(_parents, chains.ToArray());
        _neutralBridge = new CrossoverPoseBridge(_parents, chains.ToArray());
        Cache();
        // Snapshot the final evaluated pose, including other modifiers. Godot restores
        // their input afterwards; sampling in _Process would cache the wrong endpoint.
        // https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-signal-skeleton-updated
        _skeleton.SkeletonUpdated += Cache;
    }

    public override void _ExitTree()
    {
        if (GodotObject.IsInstanceValid(_skeleton)) _skeleton.SkeletonUpdated -= Cache;
    }

    private void Cache() { _last = Capture(); _lastWorld = _skeleton.GlobalTransform; }
    private Transform3D[] Capture()
    {
        var pose = new Transform3D[_parents.Length];
        for (int i = 0; i < pose.Length; i++)
            pose[i] = new Transform3D(new Basis(_skeleton.GetBonePoseRotation(i)).Scaled(_skeleton.GetBonePoseScale(i)),
                _skeleton.GetBonePosePosition(i));
        return pose;
    }

    public override void _ProcessModificationWithDelta(double delta)
    {
        AppliedForHarness = false;
        if (_bridge == null || Player == null || _hips < 0) return;
        var display = Player.AnimationDisplaySnapshot;
        bool buffered = display.PresentationTick >= 0;
        long clock = buffered ? display.PresentationTick : (long)Engine.GetPhysicsFrames();
        var reset = buffered ? Player.PivotPresentationReset : default;
        if (_clock >= 0 && (buffered != _buffered || reset != _reset || clock < _clock || clock - _clock > 1))
        {
            _wasPivot = _exiting = _correctDribbleBlend = false;
            _anchor = _held = null;
            Cache();
        }
        int step = _clock < 0 ? 0 : (int)Math.Clamp(clock - _clock, 0, 1);
        _clock = clock; _reset = reset; _buffered = buffered;
        // A committed clip/rebound owns its pose immediately. A raw pivot flag alone
        // cannot establish that the resolver actually selected Pivot.
        bool pivot = Player.DisplayPivoting && Player.ResolvedAnimationState == "Pivot";
        bool neutral = display.Phase == MovePhase.Inactive && Player.ResolvedAnimationState != "ReboundGrab";
        if (!neutral) { _wasPivot = _exiting = _correctDribbleBlend = false; _held = null; return; }
        bool plainDribble = Player.DisplayDribbling() && !Player.DisplayProtecting
            && !Player.DisplayPosting && !Player.DisplayDefensiveStance;
        float speed = new Vector2(Player.DisplayVelocity.X, Player.DisplayVelocity.Z).Length();
        bool intermediateDribble = plainDribble && speed > .001f && speed < Player.MoveSpeed - .001f;
        if (!intermediateDribble) _correctDribbleBlend = false;
        if (!pivot && !_wasPivot && !_exiting)
        {
            // After the bounded pivot handoff, retain only the neutral blend repair
            // until a clip endpoint. Postures retain their complete evaluated pose.
            if (_correctDribbleBlend) Write(DestinationPose(Capture()));
            return;
        }
        if (step == 0 && _held != null) { Write(_held); return; }
        ContactErrorForHarness = PelvisShiftForHarness = 0;
        var fresh = Capture();
        if (pivot && !_wasPivot)
        {
            _anchor = (Transform3D[])_last.Clone();
            _bridge.ResetContinuity(); _age = 0; _exiting = false;
            ChooseSupport(_anchor);
            _pivotTarget = SampleNeutral(fresh);
        }
        else if (!pivot && _wasPivot)
        {
            _anchor = (Transform3D[])_last.Clone();
            _bridge.ResetContinuity(); _age = 0; _exiting = true;
            _neutralBridge.ResetContinuity();
            _correctDribbleBlend = intermediateDribble;
        }
        else _age += step;
        _wasPivot = pivot;
        if (_exiting) fresh = DestinationPose(fresh);
        float progress = Math.Clamp(_age / (float)BridgeTicks, 0, 1);
        var result = _bridge.Blend(_anchor, pivot ? _pivotTarget : fresh, progress).Pose;
        if (pivot && _toe >= 0)
        {
            Plant(result, Math.Clamp(_age / (float)LandingTicks, 0, 1));
            KeepSwingAboveCourt(result);
        }
        Write(result);
        _held = result;
        if (_exiting && _age >= BridgeTicks) { _exiting = false; _held = null; }
    }

    private void ChooseSupport(Transform3D[] pose)
    {
        var globals = CrossoverPoseBridge.Forward(_parents, pose);
        int left = _skeleton.FindBone("mixamorig_LeftToe_End"), right = _skeleton.FindBone("mixamorig_RightToe_End");
        Vector3 root = Player.DisplayRootPosition;
        float FootprintDistance(int bone)
        {
            Vector3 position = _lastWorld * globals[bone].Origin;
            return new Vector2(position.X - root.X, position.Z - root.Z).LengthSquared();
        }
        // A low trailing dribble toe can be almost a metre behind the body.
        // The closer foot supports the turn without dragging the pelvis around it.
        _toe = left < 0 || right < 0 ? -1 : FootprintDistance(left) < FootprintDistance(right) ? left : right;
        if (_toe < 0) return;
        string side = _toe == left ? "Left" : "Right";
        _leg = new(_skeleton.FindBone("mixamorig_" + side + "UpLeg"),
            _skeleton.FindBone("mixamorig_" + side + "Leg"), _skeleton.FindBone("mixamorig_" + side + "Foot"));
        _landingStart = _lastWorld * globals[_toe].Origin;
        // Main's court is the world y=0 plane. An airborne incoming run foot lands
        // continuously; only its height changes, never its fixed world footprint.
        _contact = new Vector3(_landingStart.X, 0, _landingStart.Z);
    }

    private Transform3D[] SampleNeutral(Transform3D[] fallback)
    {
        string hand = Player.DisplayHandSide.ToString().ToLowerInvariant();
        string path = Player.DisplayHeld() ? "held/idle" + hand : "locomotion/" +
            (Player.DisplayDribbling() ? "dribbleidle" + hand : "idle");
        return SampleClip(path, 0, fallback);
    }

    private Transform3D[] DestinationPose(Transform3D[] fresh)
    {
        float speed = new Vector2(Player.DisplayVelocity.X, Player.DisplayVelocity.Z).Length();
        if (!Player.DisplayDribbling() || Player.DisplayProtecting || Player.DisplayPosting || Player.DisplayDefensiveStance
            || speed <= .001f || speed >= Player.MoveSpeed - .001f) return fresh;
        string hand = Player.DisplayHandSide.ToString().ToLowerInvariant();
        double time = Player.PivotAnimationTime;
        var idle = SampleClip("locomotion/dribbleidle" + hand, time, fresh);
        var moving = SampleClip("locomotion/dribblemove" + hand, time, fresh);
        // A held reversal accelerates into the dribble BlendSpace. Its intermediate
        // rest-relative accumulation can take the foot over the head (measured in
        // #416). During this handoff, bridge the actual clip endpoints in global
        // bone space. Return to the unchanged tree at the next blend endpoint.
        return _neutralBridge.Blend(idle, moving, Math.Clamp(speed / Player.MoveSpeed, 0, 1)).Pose;
    }

    private Transform3D[] SampleClip(string path, double time, Transform3D[] fallback)
    {
        if (AnimationPlayer == null || !AnimationPlayer.HasAnimation(path)) return fallback;
        Animation clip = AnimationPlayer.GetAnimation(path);
        time = clip.LoopMode == Animation.LoopModeEnum.Linear ? time % clip.Length : Math.Min(time, clip.Length);
        var pose = (Transform3D[])fallback.Clone();
        for (int i = 0; i < pose.Length; i++)
            pose[i] = new Transform3D(_skeleton.GetBoneRest(i).Basis.Orthonormalized().Scaled(fallback[i].Basis.Scale),
                _skeleton.GetBoneRest(i).Origin);
        for (int t = 0; t < clip.GetTrackCount(); t++)
        {
            NodePath track = clip.TrackGetPath(t);
            // 4.7 web C# examples spell this GetSubname, but the installed
            // GodotSharp 4.7.1 XML/compiler expose GetSubName (capital N).
            // https://docs.godotengine.org/en/4.7/classes/class_nodepath.html#class-nodepath-method-get-subname
            if (track.GetSubNameCount() != 1) continue;
            int bone = _skeleton.FindBone(track.GetSubName(0));
            if (bone < 0) continue;
            // Use immutable authored stance endpoints, preserving complete bone
            // coverage and live RigScaler proportions on unkeyed scale channels.
            // https://docs.godotengine.org/en/4.7/classes/class_animation.html#class-animation-method-rotation-track-interpolate
            if (clip.TrackGetType(t) == Animation.TrackType.Rotation3D)
                pose[bone].Basis = new Basis(clip.RotationTrackInterpolate(t, time)).Scaled(pose[bone].Basis.Scale);
            else if (clip.TrackGetType(t) == Animation.TrackType.Position3D)
                pose[bone].Origin = clip.PositionTrackInterpolate(t, time);
            else if (clip.TrackGetType(t) == Animation.TrackType.Scale3D)
                pose[bone].Basis = pose[bone].Basis.Orthonormalized().Scaled(clip.ScaleTrackInterpolate(t, time));
        }
        return pose;
    }

    private void Plant(Transform3D[] pose, float progress)
    {
        var globals = CrossoverPoseBridge.Forward(_parents, pose);
        Vector3 toe = _skeleton.ToLocal(_landingStart.Lerp(_contact, progress));
        Vector3 ankle = toe - (globals[_toe].Origin - globals[_leg.Effector].Origin);
        Vector3 h = globals[_leg.Root].Origin;
        float reach = globals[_leg.Root].Origin.DistanceTo(globals[_leg.Middle].Origin)
            + globals[_leg.Middle].Origin.DistanceTo(globals[_leg.Effector].Origin);
        Vector3 offset = ankle - h;
        // Minimal pelvis assistance only when the fixed contact is out of reach.
        // IK, not translating the whole rig around its toe, owns ordinary contact.
        if (offset.Length() > reach * .999f)
        {
            Vector3 shift = offset - offset.Normalized() * reach * .999f;
            pose[_hips].Origin += shift;
            PelvisShiftForHarness = (_skeleton.GlobalBasis * shift).Length();
        }
        var solved = _bridge.PlaceEffector(pose, _leg, ankle);
        Array.Copy(solved.Pose, pose, pose.Length);
        globals = CrossoverPoseBridge.Forward(_parents, pose);
        ContactErrorForHarness = _skeleton.ToGlobal(globals[_toe].Origin).DistanceTo(_landingStart.Lerp(_contact, progress));
    }

    private void Write(Transform3D[] pose)
    {
        // Modifiers run after AnimationMixer and Godot restores their base pose.
        // https://docs.godotengine.org/en/4.7/classes/class_skeletonmodifier3d.html
        for (int i = 0; i < pose.Length; i++)
        {
            _skeleton.SetBonePosePosition(i, pose[i].Origin);
            _skeleton.SetBonePoseRotation(i, pose[i].Basis.Orthonormalized().GetRotationQuaternion().Normalized());
            _skeleton.SetBonePoseScale(i, pose[i].Basis.Scale);
        }
        AppliedForHarness = true;
    }

    private void KeepSwingAboveCourt(Transform3D[] pose)
    {
        string side = _skeleton.GetBoneName(_toe).ToString().Contains("Left", StringComparison.Ordinal) ? "Right" : "Left";
        int toeIndex = _skeleton.FindBone("mixamorig_" + side + "Toe_End");
        var globals = CrossoverPoseBridge.Forward(_parents, pose);
        Vector3 world = _skeleton.ToGlobal(globals[toeIndex].Origin);
        if (world.Y >= 0) return;
        world.Y = 0;
        var leg = new CrossoverPoseBridge.LimbChain(_skeleton.FindBone("mixamorig_" + side + "UpLeg"),
            _skeleton.FindBone("mixamorig_" + side + "Leg"), _skeleton.FindBone("mixamorig_" + side + "Foot"));
        Vector3 ankle = _skeleton.ToLocal(world) - (globals[toeIndex].Origin - globals[leg.Effector].Origin);
        var solved = _bridge.PlaceEffector(pose, leg, ankle);
        Array.Copy(solved.Pose, pose, pose.Length);
    }
}
