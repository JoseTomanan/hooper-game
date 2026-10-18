using Godot;
using Hooper.Moves;

namespace Hooper.Player;

/// <summary>Cosmetic low, wide defensive posture over this frame's authored animation.</summary>
public partial class DefensiveStancePose : SkeletonModifier3D
{
    public PlayerController Player { get; set; }

    public override void _ProcessModificationWithDelta(double delta)
    {
        if (Player == null || !Player.DisplayDefensiveStance || Player.DisplayPivoting
            || Player.DisplayMove().phase != MovePhase.Inactive) return;
        var skeleton = GetSkeleton();
        if (skeleton == null) return;
        // The modifier runs after AnimationMixer; Godot restores its fresh base afterwards.
        // https://docs.godotengine.org/en/4.7/classes/class_skeletonmodifier3d.html#class-skeletonmodifier3d-method-process-modification-with-delta
        // These provisional visual magnitudes belong to the deferred #173 feel pass.
        int hips = skeleton.FindBone("mixamorig_Hips");
        if (hips < 0) return;
        skeleton.SetBonePosePosition(hips,
            skeleton.GetBonePosePosition(hips) + new Vector3(0, -.12f, 0));
        foreach (string side in new[] { "Left", "Right" })
        {
            int thigh = skeleton.FindBone("mixamorig_" + side + "UpLeg");
            if (thigh >= 0)
            {
                float outward = Mathf.Sign(skeleton.GetBoneGlobalPose(thigh).Origin.X
                    - skeleton.GetBoneGlobalPose(hips).Origin.X);
                // Bend/spread about character-space axes so imported bone roll cannot
                // turn the knee bend into a twist. This writes only the cosmetic pose.
                RotateCharacterSpace(skeleton, thigh, new Vector3(-.35f, 0, .18f * outward));
            }
            RotateCharacterSpace(skeleton, skeleton.FindBone("mixamorig_" + side + "Leg"), new Vector3(.7f, 0, 0));
            float mirror = side == "Left" ? 1f : -1f;
            RotateLocal(skeleton, "mixamorig_" + side + "Arm", new Vector3(.35f, 0, .45f * mirror));
            RotateLocal(skeleton, "mixamorig_" + side + "ForeArm", new Vector3(0, 0, 1.3f * mirror));
        }
    }

    private static void RotateCharacterSpace(Skeleton3D skeleton, int bone, Vector3 euler)
    {
        if (bone < 0) return;
        int parent = skeleton.GetBoneParent(bone);
        var parentRotation = parent >= 0
            ? skeleton.GetBoneGlobalPose(parent).Basis.GetRotationQuaternion() : Quaternion.Identity;
        var rotation = parentRotation.Inverse() * Quaternion.FromEuler(euler) * parentRotation;
        // Global pose is relative to Skeleton3D; local pose rotation is relative to its parent.
        // https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-method-get-bone-global-pose
        // https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-method-set-bone-pose-rotation
        skeleton.SetBonePoseRotation(bone, rotation * skeleton.GetBonePoseRotation(bone));
    }

    private static void RotateLocal(Skeleton3D skeleton, string name, Vector3 euler)
    {
        int bone = skeleton.FindBone(name);
        if (bone >= 0) skeleton.SetBonePoseRotation(bone,
            skeleton.GetBonePoseRotation(bone) * Quaternion.FromEuler(euler));
    }
}
