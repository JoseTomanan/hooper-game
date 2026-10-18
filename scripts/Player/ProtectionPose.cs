using Godot;
using Hooper.Moves;

namespace Hooper.Player;

/// <summary>Cosmetic shield layered over the fresh authored pose, never gameplay state.</summary>
public partial class ProtectionPose : SkeletonModifier3D
{
    public PlayerController Player { get; set; }

    public override void _ProcessModificationWithDelta(double delta)
    {
        // Godot applies modifiers after AnimationMixer and restores the underlying pose.
        // Each write starts from this frame's authored rotation; no cached pose accumulates.
        // Source: https://docs.godotengine.org/en/4.7/classes/class_skeletonmodifier3d.html
        if (Player == null || !Player.DisplayProtecting || Player.DisplayPivoting
            || Player.DisplayMove().phase != MovePhase.Inactive) return;
        var skeleton = GetSkeleton();
        bool rightBall = Player.DisplayHandSide == HandSide.Right;
        string off = rightBall ? "Left" : "Right";
        float mirror = rightBall ? 1f : -1f;
        Rotate(skeleton, "mixamorig_" + off + "Arm", new Vector3(.35f, 0, .45f * mirror));
        // Mixamo forearm Y is longitudinal: Z bends the elbow instead of twisting the wrist.
        Rotate(skeleton, "mixamorig_" + off + "ForeArm", new Vector3(0, 0, 1.3f * mirror));
        Rotate(skeleton, "mixamorig_Spine", new Vector3(.12f, .10f * mirror, 0));
        int hips = skeleton.FindBone("mixamorig_Hips");
        if (hips >= 0) skeleton.SetBonePosePosition(hips, skeleton.GetBonePosePosition(hips) + new Vector3(0, -.035f, 0));
    }

    private static void Rotate(Skeleton3D skeleton, string name, Vector3 euler)
    {
        int bone = skeleton.FindBone(name);
        if (bone >= 0) skeleton.SetBonePoseRotation(bone,
            skeleton.GetBonePoseRotation(bone) * Quaternion.FromEuler(euler));
    }
}
