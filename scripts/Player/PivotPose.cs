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
    // One short step per eight consumed source ticks, including a four-tick
    // landing and grounded stance. No wall-clock phase runs during remote holds.
    private const int StanceTicks = 8;
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
    private int _stanceAge;
    private bool _stepping;
    private int _swingToe;
    private CrossoverPoseBridge.LimbChain _swingLeg;
    private Vector3 _swingStart, _swingEnd;
    private (int Epoch, int Resync) _reset;
    private bool _buffered;

    public bool AppliedForHarness { get; private set; }
    public string SupportToeForHarness => _toe >= 0 && _skeleton != null ? _skeleton.GetBoneName(_toe) : "";
    public int AgeForHarness => _age;
    public int StanceAgeForHarness => _stanceAge;
    public float ContactErrorForHarness { get; private set; }
    public float SwingErrorForHarness { get; private set; }
    public bool SteppingForHarness => _wasPivot && _stepping;
    public string SwingToeForHarness => _swingToe >= 0 && _skeleton != null ? _skeleton.GetBoneName(_swingToe) : "";
    public Vector3 SupportTargetForHarness { get; private set; }
    public Vector3 SwingTargetForHarness { get; private set; }
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
        // Dedicated contact solves below own the legs during a turn. A second
        // endpoint IK solve in the generic bridge can choose a different knee
        // branch at mid-blend. Unconstrained exit legs use the continuous bone
        // rotation blend; only arms need generic endpoint interpolation here.
        _bridge = new CrossoverPoseBridge(_parents, chains.FindAll(chain => !chain.IsSupport).ToArray());
        _neutralBridge = new CrossoverPoseBridge(_parents, chains.ToArray(), followAnchorPose: true);
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
        ContactErrorForHarness = SwingErrorForHarness = PelvisShiftForHarness = 0;
        var fresh = Capture();
        if (pivot && !_wasPivot)
        {
            _anchor = (Transform3D[])_last.Clone();
            _bridge.ResetContinuity(); _age = 0; _exiting = false;
            _stanceAge = 0; _stepping = false;
            ChooseSupport(_anchor);
            BeginStep(_anchor);
            _pivotTarget = SampleNeutral(fresh);
        }
        else if (!pivot && _wasPivot)
        {
            _anchor = (Transform3D[])_last.Clone();
            _bridge.ResetContinuity(); _age = 0; _exiting = true;
            _neutralBridge.ResetContinuity();
            _correctDribbleBlend = intermediateDribble;
        }
        else { _age += step; if (pivot) _stanceAge += step; }
        _wasPivot = pivot;
        if (_exiting) fresh = DestinationPose(fresh);
        float progress = Math.Clamp(_age / (float)BridgeTicks, 0, 1);
        var result = _bridge.Blend(_anchor, pivot ? _pivotTarget : fresh, progress).Pose;
        if (pivot && _toe >= 0)
        {
            // A flick can stop before the first foot transfer. It retains the
            // stationary #416 contact instead of inventing latch-driven steps.
            if (speed > .05f && !_stepping)
            {
                // Motion can start after several blocked/stationary turn ticks.
                // Start a real swing from the last displayed feet, not halfway
                // through an old entry clock/trajectory.
                _stepping = true; _stanceAge = 0;
                var lastGlobal = CrossoverPoseBridge.Forward(_parents, _last);
                _landingStart = _lastWorld * lastGlobal[_toe].Origin;
                _contact = new Vector3(_landingStart.X, 0, _landingStart.Z);
                BeginStep(_last);
            }
            if (_stepping && speed > .05f && _stanceAge >= StanceTicks)
            {
                ChooseSupport(_last, transfer: true);
                BeginStep(_last);
                _stanceAge = 0;
            }
            float landing = Math.Clamp(_stanceAge / (float)LandingTicks, 0, 1);
            if (_stepping) StepFeet(result, landing);
            else Plant(result, landing);
            KeepSwingAboveCourt(result);
        }
        else if (_exiting)
        {
            // Unconstrained rotation blending can arc a toe below the flat
            // court between two valid poses. Minimal vertical pelvis clearance
            // avoids reintroducing the unstable leg IK branch during handoff.
            var globals = CrossoverPoseBridge.Forward(_parents, result);
            float lowest = Math.Min(
                _skeleton.ToGlobal(globals[_skeleton.FindBone("mixamorig_LeftToe_End")].Origin).Y,
                _skeleton.ToGlobal(globals[_skeleton.FindBone("mixamorig_RightToe_End")].Origin).Y);
            if (lowest < 0)
            {
                result[_hips].Origin += _skeleton.GlobalBasis.Inverse() * (Vector3.Up * -lowest);
                PelvisShiftForHarness = -lowest;
            }
        }
        Write(result);
        _held = result;
        if (_exiting && _age >= BridgeTicks) { _exiting = false; _held = null; }
    }

    private void ChooseSupport(Transform3D[] pose, bool transfer = false)
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
        _toe = left < 0 || right < 0 ? -1 : transfer ? (_toe == left ? right : left)
            : FootprintDistance(left) < FootprintDistance(right) ? left : right;
        if (_toe < 0) return;
        string side = _toe == left ? "Left" : "Right";
        _leg = new(_skeleton.FindBone("mixamorig_" + side + "UpLeg"),
            _skeleton.FindBone("mixamorig_" + side + "Leg"), _skeleton.FindBone("mixamorig_" + side + "Foot"));
        _landingStart = _lastWorld * globals[_toe].Origin;
        // Main's court is the world y=0 plane. An airborne incoming run foot lands
        // continuously; only its height changes, never its fixed world footprint.
        _contact = new Vector3(_landingStart.X, 0, _landingStart.Z);
    }

    private void BeginStep(Transform3D[] pose)
    {
        if (_toe < 0) return;
        string side = SupportToeForHarness.Contains("Left", StringComparison.Ordinal) ? "Right" : "Left";
        _swingToe = _skeleton.FindBone("mixamorig_" + side + "Toe_End");
        _swingLeg = new(_skeleton.FindBone("mixamorig_" + side + "UpLeg"),
            _skeleton.FindBone("mixamorig_" + side + "Leg"), _skeleton.FindBone("mixamorig_" + side + "Foot"));
        var globals = CrossoverPoseBridge.Forward(_parents, pose);
        _swingStart = _lastWorld * globals[_swingToe].Origin;
        Vector2 forward = HeadingMath.Forward(Player.DisplayHeading);
        Vector3 lateral = new Vector3(forward.Y, 0, -forward.X) * (side == "Left" ? .16f : -.16f);
        // Place the next foot under the travelling body, with 100 ms of measured
        // velocity lead. A fixed world destination prevents stance-foot skating.
        _swingEnd = Player.DisplayRootPosition + lateral + Player.DisplayVelocity * .10f;
        _swingEnd.Y = 0;
    }

    private void StepFeet(Transform3D[] pose, float landing)
    {
        float phase = Math.Clamp(_stanceAge / (float)(StanceTicks - 1), 0, 1);
        Vector3 world = _swingStart.Lerp(_swingEnd, phase);
        world.Y += .10f * MathF.Sin(MathF.PI * phase);
        var globals = CrossoverPoseBridge.Forward(_parents, pose);
        Vector3 supportWorld = _landingStart.Lerp(_contact, landing);
        SupportTargetForHarness = supportWorld;
        SwingTargetForHarness = world;
        Vector3 Ankle(CrossoverPoseBridge.LimbChain leg, int toe, Vector3 target) =>
            _skeleton.ToLocal(target) - (globals[toe].Origin - globals[leg.Effector].Origin);
        Vector3 support = Ankle(_leg, _toe, supportWorld);
        Vector3 swing = Ankle(_swingLeg, _swingToe, world);
        float Reach(CrossoverPoseBridge.LimbChain leg) =>
            (globals[leg.Root].Origin.DistanceTo(globals[leg.Middle].Origin)
            + globals[leg.Middle].Origin.DistanceTo(globals[leg.Effector].Origin)) * .999f;
        // Both legs share one pelvis. Solving the support first and silently
        // clamping the other leg can lift a grounded toe on transfer (#430).
        // Project into both reach spheres before either limb solve; target-error
        // proof rejects unreachable configurations rather than calling clamps contact.
        Vector3 shift = Vector3.Zero;
        void Assist(CrossoverPoseBridge.LimbChain leg, Vector3 ankle)
        {
            Vector3 offset = ankle - (globals[leg.Root].Origin + shift);
            float reach = Reach(leg);
            if (offset.Length() > reach) shift += offset - offset.Normalized() * reach;
        }
        for (int i = 0; i < 16; i++) { Assist(_leg, support); Assist(_swingLeg, swing); }
        pose[_hips].Origin += shift;
        PelvisShiftForHarness = (_skeleton.GlobalBasis * shift).Length();
        // A fresh blended knee can cross the hip-to-ankle axis during a short
        // turn and make IK choose the opposite bend (near-180-degree knee pop).
        // Start both solves from the last displayed world limb orientations,
        // including their twist, rather than reselecting that branch each tick.
        // The new foot orientations/targets still come from this tick's pose.
        var previous = CrossoverPoseBridge.Forward(_parents, _last);
        void SetRotation(int bone, Quaternion rotation)
        {
            var current = CrossoverPoseBridge.Forward(_parents, pose);
            Quaternion parent = current[_parents[bone]].Basis.Orthonormalized().GetRotationQuaternion();
            pose[bone].Basis = new Basis((parent.Inverse() * rotation).Normalized()).Scaled(pose[bone].Basis.Scale);
        }
        foreach (var leg in new[] { _leg, _swingLeg })
        {
            Quaternion foot = globals[leg.Effector].Basis.Orthonormalized().GetRotationQuaternion();
            foreach (int bone in new[] { leg.Root, leg.Middle })
                SetRotation(bone, (_skeleton.GlobalBasis.Inverse() * _lastWorld.Basis * previous[bone].Basis)
                    .Orthonormalized().GetRotationQuaternion());
            SetRotation(leg.Effector, foot);
        }
        foreach (var placement in new[] { (_leg, support), (_swingLeg, swing) })
        {
            var solved = _bridge.PlaceEffector(pose, placement.Item1, placement.Item2);
            Array.Copy(solved.Pose, pose, pose.Length);
        }
        globals = CrossoverPoseBridge.Forward(_parents, pose);
        ContactErrorForHarness = _skeleton.ToGlobal(globals[_toe].Origin).DistanceTo(supportWorld);
        SwingErrorForHarness = _skeleton.ToGlobal(globals[_swingToe].Origin).DistanceTo(world);
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
