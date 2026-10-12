using Godot;
using Hooper.Moves;

namespace Hooper.Player;

/// <summary>Cosmetic planted post guard, layered over the handed dribble pose.</summary>
public partial class PostPose : SkeletonModifier3D
{
    public PlayerController Player { get; set; }

    public override void _ProcessModificationWithDelta(double delta)
    {
        if (Player == null || !Player.DisplayPosting || Player.IsPivotingInPlace
            || Player.DisplayMove().phase != MovePhase.Inactive) return;
        var skeleton = GetSkeleton();
        if (skeleton == null) return;
        // Godot restores the authored base after modifiers; never cache the adjusted pose.
        // https://docs.godotengine.org/en/4.7/classes/class_skeletonmodifier3d.html
        int hips = skeleton.FindBone("mixamorig_Hips");
        if (hips >= 0) skeleton.SetBonePosePosition(hips,
            skeleton.GetBonePosePosition(hips) + new Vector3(0, -.06f, 0));
        foreach (string side in new[] { "Left", "Right" })
        {
            RotateCharacterSpace(skeleton, "mixamorig_" + side + "UpLeg", new Vector3(-.22f, 0, 0));
            RotateCharacterSpace(skeleton, "mixamorig_" + side + "Leg", new Vector3(.44f, 0, 0));
            RotateCharacterSpace(skeleton, "mixamorig_" + side + "Foot", new Vector3(-.22f, 0, 0));
        }
        bool rightBall = Player.HandSide == HandSide.Right;
        string off = rightBall ? "Left" : "Right";
        float mirror = rightBall ? 1f : -1f;
        RotateLocal(skeleton, "mixamorig_" + off + "Arm", new Vector3(.55f, -.15f * mirror, .65f * mirror));
        RotateLocal(skeleton, "mixamorig_" + off + "ForeArm", new Vector3(0, 0, 1.05f * mirror));
        RotateLocal(skeleton, "mixamorig_Spine", new Vector3(.18f, .18f * mirror, 0));
    }

    private static void RotateCharacterSpace(Skeleton3D skeleton, string name, Vector3 euler)
    {
        int bone = skeleton.FindBone(name);
        if (bone < 0) return;
        int parent = skeleton.GetBoneParent(bone);
        var parentRotation = parent >= 0
            ? skeleton.GetBoneGlobalPose(parent).Basis.GetRotationQuaternion() : Quaternion.Identity;
        // Bone global poses use Skeleton3D space; convert the bend into parent space.
        // https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-method-get-bone-global-pose
        var rotation = parentRotation.Inverse() * Quaternion.FromEuler(euler) * parentRotation;
        skeleton.SetBonePoseRotation(bone, rotation * skeleton.GetBonePoseRotation(bone));
    }

    private static void RotateLocal(Skeleton3D skeleton, string name, Vector3 euler)
    {
        int bone = skeleton.FindBone(name);
        if (bone >= 0) skeleton.SetBonePoseRotation(bone,
            skeleton.GetBonePoseRotation(bone) * Quaternion.FromEuler(euler));
    }
}
