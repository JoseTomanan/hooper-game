using System;
using Godot;
using Hooper.Ball;
using Hooper.Moves;

namespace Hooper.Player;

/// <summary>Final skin binding to the displayed simulation ball; never writes gameplay.</summary>
public partial class ShotHandPose : SkeletonModifier3D
{
    public PlayerController Player { get; set; }
    private Skeleton3D _skeleton;
    private int[] _parents;
    private CrossoverPoseBridge _solver;
    private CrossoverPoseBridge.LimbChain _right, _left;
    private MovePhase _previousPhase;
    private float _rightWeight, _leftWeight, _fakeRight, _fakeLeft;
    private long _clock = -1;
    private (int Epoch, int Resync) _reset;
    public float MaxReachErrorForHarness { get; private set; }

    public override void _Ready()
    {
        _skeleton = GetSkeleton();
        if (_skeleton == null) return;
        _parents = new int[_skeleton.GetBoneCount()];
        for (int i = 0; i < _parents.Length; i++) _parents[i] = _skeleton.GetBoneParent(i);
        _right = Chain("Right"); _left = Chain("Left");
        if (_right.Root < 0 || _left.Root < 0) return;
        _solver = new CrossoverPoseBridge(_parents, new[] { _right, _left });
    }

    private CrossoverPoseBridge.LimbChain Chain(string side) => new(
        _skeleton.FindBone("mixamorig_" + side + "Arm"), _skeleton.FindBone("mixamorig_" + side + "ForeArm"),
        _skeleton.FindBone("mixamorig_" + side + "Hand"));

    public override void _ProcessModificationWithDelta(double delta)
    {
        MaxReachErrorForHarness = 0;
        if (_solver == null || Player == null || !Player.TryShotBallTarget(out var ball)) return;
        var display = Player.AnimationDisplaySnapshot;
        long clock = display.PresentationTick >= 0 ? display.PresentationTick : (long)Engine.GetPhysicsFrames();
        var reset = display.PresentationTick >= 0 ? Player.PivotPresentationReset : default;
        if (_clock < 0 || clock < _clock || clock - _clock > 1 || reset != _reset)
        {
            _previousPhase = MovePhase.Inactive;
            _rightWeight = _leftWeight = _fakeRight = _fakeLeft = 0;
        }
        _clock = clock; _reset = reset;
        float right = 1f, left = 0f;
        if (display.Phase == MovePhase.Startup)
        {
            // A bouncing entry must first gather the actual low incoming ball.
            // Reach gradually; the right palm has full control at the set frame.
            right = Smooth((display.FrameInPhase - 1) / 7f);
            left = display.Hand == HandSide.Left ? 1f - Smooth((display.FrameInPhase - 5) / 3f) : 0f;
            if (display.Hand == HandSide.Right && display.FrameInPhase < 8)
                right = Smooth((display.FrameInPhase - 1) / 5f);
        }
        else if (display.Phase == MovePhase.Recovery)
        {
            if (_previousPhase == MovePhase.Startup)
            { _fakeRight = _rightWeight; _fakeLeft = _leftWeight; }
            float returnWeight = Smooth((display.FrameInPhase - 13) / 6f);
            // As the retained left hand takes the ball, relinquish the right
            // shooting-hand binding weight with zero terminal acceleration. Squaring
            // the remaining weight removes its tail correction before unbinding.
            float remaining = 1f - returnWeight;
            right = display.Hand == HandSide.Left ? _fakeRight * remaining * remaining
                : Mathf.Lerp(_fakeRight, 1f, returnWeight);
            left = Mathf.Lerp(_fakeLeft, display.Hand == HandSide.Left ? 1f : 0f, returnWeight);
        }
        _previousPhase = display.Phase; _rightWeight = right; _leftWeight = left;
        var pose = new Transform3D[_parents.Length];
        for (int i = 0; i < pose.Length; i++)
            pose[i] = new(new Basis(_skeleton.GetBonePoseRotation(i)).Scaled(_skeleton.GetBonePoseScale(i)),
                _skeleton.GetBonePosePosition(i));
        Vector3 target = _skeleton.GlobalTransform.AffineInverse() * ball;
        Bind(_right, ShotGatherProfile.RightPalm, ShotGatherProfile.RightNormal, right);
        Bind(_left, ShotGatherProfile.LeftPalm, ShotGatherProfile.LeftNormal, left);
        // The modifier executes after mixer + body/entry/fake modifiers. The
        // engine applies these final local rotations to the skin afterwards.
        // https://docs.godotengine.org/en/4.7/classes/class_skeletonmodifier3d.html
        for (int i = 0; i < pose.Length; i++)
            _skeleton.SetBonePoseRotation(i, pose[i].Basis.Orthonormalized().GetRotationQuaternion().Normalized());

        void Bind(CrossoverPoseBridge.LimbChain chain, Vector3 palm, Vector3 normal, float weight)
        {
            if (weight <= 0f) return;
            var globals = CrossoverPoseBridge.Forward(_parents, pose);
            Vector3 wanted = target - globals[chain.Effector].Basis * (palm + normal * .12f);
            wanted = globals[chain.Effector].Origin.Lerp(wanted, weight);
            var result = _solver.PlaceEffector(pose, chain, wanted);
            MaxReachErrorForHarness = Math.Max(MaxReachErrorForHarness, result.MaxReachError);
            pose = result.Pose;
        }
    }

    private static float Smooth(float t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }
}
