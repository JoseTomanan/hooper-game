using System.Collections.Generic;
using System.Linq;
using Godot;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// #367: structural evidence on the imported Y Bot in the real Player.tscn.
// independent-scaling checks every LOCAL bone pose scale against authored rest,
// using named expectations independent of RigScale.Classify. Spine scaling still
// propagates into arm world transforms; this does not prove visual independence.
// capsule-contract pins #170's unchanged gameplay capsule (ADR-0025 records its
// default dimensions). Numeric mesh fitting and proportion taste remain #178.
// Setter observations are immediate: animation SCALE tracks may overwrite poses
// later, so neither scenario claims persistence through animation playback.
// Run RigScaleHarnessTest.tscn with --harness-scenario=<name>; exit 0/1 = PASS/FAIL.
public partial class RigScaleHarnessTest : Node
{
    private const double TimeoutSeconds = 10.0;
    private const float Epsilon = 0.0001f;

    // Explicit imported names, not the production classifier's token sets.
    private static readonly HashSet<string> HeightBones = new()
    {
        "mixamorig_Spine", "mixamorig_Spine1", "mixamorig_Spine2", "mixamorig_Neck", "mixamorig_Head",
        "mixamorig_LeftUpLeg", "mixamorig_LeftLeg", "mixamorig_LeftFoot", "mixamorig_LeftToeBase",
        "mixamorig_RightUpLeg", "mixamorig_RightLeg", "mixamorig_RightFoot", "mixamorig_RightToeBase",
    };
    private static readonly HashSet<string> WingspanBones = new()
    {
        "mixamorig_LeftShoulder", "mixamorig_LeftArm", "mixamorig_LeftForeArm", "mixamorig_LeftHand",
        "mixamorig_RightShoulder", "mixamorig_RightArm", "mixamorig_RightForeArm", "mixamorig_RightHand",
    };
    // Samples of the finite-positive capability; not a tuned player-build range.
    private static readonly float[] Factors = { 0.8f, 1.2f, 2.0f };

    private string _scenario = "independent-scaling";
    private PlayerController _player;
    private PlayerRigScaler _scaler;
    private Skeleton3D _skeleton;
    private Vector3[] _restScales;
    private int _frame;
    private double _elapsed;
    private bool _finished;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "independent-scaling");
        GD.Print($"[rig-scale] scenario={_scenario} booting headless...");

        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        if (scene == null) { Fail("could not load Player.tscn."); Finish(); return; }
        _player = scene.Instantiate<PlayerController>();
        _player.Name = "1";
        AddChild(_player);
        _scaler = _player.GetNodeOrNull<PlayerRigScaler>("RigScaler");
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;
        if (_frame < 2) return;

        if (_scaler == null || !_scaler.SkeletonResolvedForHarness)
        { Fail("Player.tscn RigScaler is absent or resolved no skeleton."); Finish(); return; }

        if (_scenario == "independent-scaling") RunIndependentScaling();
        else if (_scenario == "capsule-contract") RunCapsuleContract();
        else { Fail($"unknown scenario '{_scenario}'."); Finish(); }

        if (!_finished && _elapsed > TimeoutSeconds)
        { Fail($"timed out at frame {_frame} without a verdict."); Finish(); }
    }

    private void RunIndependentScaling()
    {
        if (!AnimationHarnessResources.TryFindSkeleton(_player, out _skeleton, out string diagnostic))
        { Fail(diagnostic); Finish(); return; }

        int count = _skeleton.GetBoneCount();
        var names = new HashSet<string>();
        _restScales = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            string name = _skeleton.GetBoneName(i);
            names.Add(name);
            // Rest is independent of any scale the scaler already wrote at _Ready.
            // Sources: https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-method-get-bone-rest
            // https://docs.godotengine.org/en/4.7/classes/class_basis.html#class-basis-method-get-scale
            _restScales[i] = _skeleton.GetBoneRest(i).Basis.Scale;
            GD.Print($"[rig-scale] [{i}] '{name}' rest-scale={_restScales[i]}");
        }

        bool pass = Check(count == 65 && names.Count == 65,
            $"expected 65 unique imported Y Bot bones, got count={count}, unique={names.Count}.");
        foreach (string name in HeightBones.Concat(WingspanBones))
            pass &= Check(names.Contains(name), $"imported skeleton is missing expected bone '{name}'.");
        int neither = names.Count(name => !HeightBones.Contains(name) && !WingspanBones.Contains(name));
        pass &= Check(neither == 44, $"expected 44 unrelated bones, got {neither}.");
        pass &= Check(_scaler.HeightBonesScaledForHarness == 13 && _scaler.WingspanBonesScaledForHarness == 8,
            $"expected scaler counts height=13 wingspan=8, got {_scaler.HeightBonesScaledForHarness}/{_scaler.WingspanBonesScaledForHarness}.");
        pass &= Check(Approx(_scaler.Height, 1f) && Approx(_scaler.Wingspan, 1f),
            $"scene defaults must be height=1 wingspan=1, got {_scaler.Height}/{_scaler.Wingspan}.");
        pass &= CheckAllPoseScales(1f, 1f, "scene default identity");

        foreach (float factor in Factors)
        {
            _scaler.SetBuild(1f, 1f);
            _scaler.SetHeight(factor);
            pass &= CheckAllPoseScales(factor, 1f, $"height-only {factor}");
            _scaler.SetHeight(factor);
            pass &= CheckAllPoseScales(factor, 1f, $"height-only {factor} reapplied");
            _scaler.SetHeight(1f);
            pass &= CheckAllPoseScales(1f, 1f, $"height {factor} identity restored");

            _scaler.SetWingspan(factor);
            pass &= CheckAllPoseScales(1f, factor, $"wingspan-only {factor}");
            _scaler.SetWingspan(factor);
            pass &= CheckAllPoseScales(1f, factor, $"wingspan-only {factor} reapplied");
            _scaler.SetWingspan(1f);
            pass &= CheckAllPoseScales(1f, 1f, $"wingspan {factor} identity restored");
        }

        _scaler.SetBuild(0.8f, 1.2f);
        pass &= CheckAllPoseScales(0.8f, 1.2f, "combined build");
        _scaler.SetBuild(0.8f, 1.2f);
        pass &= CheckAllPoseScales(0.8f, 1.2f, "same combined build reapplied");
        _scaler.SetBuild(1f, 1f);
        pass &= CheckAllPoseScales(1f, 1f, "combined identity restored");

        if (pass) GD.Print("[rig-scale] PASS independent-scaling: all 65 bones, 13 height / 8 wingspan / 44 unrelated; immediate rest-based scaling and restoration.");
        Finish(pass ? 0 : 1);
    }

    private bool CheckAllPoseScales(float height, float wingspan, string step)
    {
        bool pass = true;
        for (int i = 0; i < _restScales.Length; i++)
        {
            string name = _skeleton.GetBoneName(i);
            float factor = HeightBones.Contains(name) ? height : WingspanBones.Contains(name) ? wingspan : 1f;
            Vector3 expected = _restScales[i] * factor;
            // Read local pose scale, not propagated skeleton-global/world scale.
            // Source: https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-method-get-bone-pose-scale
            Vector3 actual = _skeleton.GetBonePoseScale(i);
            pass &= Check(Approx(actual, expected), $"{step}: '{name}' expected {expected}, got {actual}.");
        }
        GD.Print($"[rig-scale] {step}: checked {_restScales.Length} local pose scales ({(pass ? "PASS" : "FAIL")}).");
        return pass;
    }

    private void RunCapsuleContract()
    {
        var collision = _player.GetNodeOrNull<CollisionShape3D>("CollisionShape3D");
        if (collision?.Shape is not CapsuleShape3D capsule)
        { Fail("Player.tscn's active CollisionShape3D must have a CapsuleShape3D."); Finish(); return; }

        bool pass = CheckCapsule(collision, capsule, "scene default");
        foreach (float factor in Factors)
        {
            _scaler.SetHeight(factor);
            pass &= CheckCapsule(collision, capsule, $"height setter {factor}");
            _scaler.SetHeight(1f);
            pass &= CheckCapsule(collision, capsule, "height restored");
            _scaler.SetWingspan(factor);
            pass &= CheckCapsule(collision, capsule, $"wingspan setter {factor}");
            _scaler.SetWingspan(1f);
            pass &= CheckCapsule(collision, capsule, "wingspan restored");
        }
        _scaler.SetBuild(0.8f, 1.2f);
        pass &= CheckCapsule(collision, capsule, "combined build");
        _scaler.SetBuild(0.8f, 1.2f);
        pass &= CheckCapsule(collision, capsule, "combined build reapplied");
        _scaler.SetBuild(1f, 1f);
        pass &= CheckCapsule(collision, capsule, "combined identity restored");

        if (pass) GD.Print("[rig-scale] PASS capsule-contract: enabled fixed radius=0.5m / height=2m capsule, unit local/global scales throughout setter sweep.");
        Finish(pass ? 0 : 1);
    }

    private bool CheckCapsule(CollisionShape3D collision, CapsuleShape3D capsule, string step)
    {
        // Capsule dimensions include both hemispheres; these are the accepted
        // unchanged shape values, not an invented mesh-fit tolerance.
        // Source: https://docs.godotengine.org/en/4.7/classes/class_capsuleshape3d.html#class-capsuleshape3d-property-height
        bool pass = Check(collision.Shape == capsule && Approx(capsule.Radius, 0.5f) && Approx(capsule.Height, 2f),
            $"{step}: fixed capsule changed (radius={capsule.Radius}, height={capsule.Height}).");
        // Source: https://docs.godotengine.org/en/4.7/classes/class_collisionshape3d.html#class-collisionshape3d-property-disabled
        pass &= Check(!collision.Disabled && collision.GetParent() == _player, $"{step}: capsule must be enabled directly under the player body.");

        // Non-uniform round-collider scaling is unsupported. Inspect ancestors
        // too: reciprocal ancestor scales could conceal an invalid local scale.
        // Sources: https://docs.godotengine.org/en/4.7/classes/class_collisionshape3d.html#description
        // https://docs.godotengine.org/en/4.7/classes/class_node3d.html#class-node3d-property-global-transform
        for (Node node = collision; node != null; node = node.GetParent())
        {
            if (node is not Node3D spatial) continue;
            pass &= Check(Approx(spatial.Transform.Basis.Scale, Vector3.One) &&
                          Approx(spatial.GlobalTransform.Basis.Scale, Vector3.One),
                $"{step}: '{spatial.GetPath()}' capsule ancestry must have unit local/global scale; got {spatial.Transform.Basis.Scale}/{spatial.GlobalTransform.Basis.Scale}.");
        }
        GD.Print($"[rig-scale] capsule {step}: {(pass ? "PASS" : "FAIL")}.");
        return pass;
    }

    private static bool Approx(float a, float b) => Mathf.Abs(a - b) < Epsilon;
    private static bool Approx(Vector3 a, Vector3 b) => Approx(a.X, b.X) && Approx(a.Y, b.Y) && Approx(a.Z, b.Z);
    private bool Check(bool condition, string diagnostic)
    {
        if (!condition) Fail(diagnostic);
        return condition;
    }
    private void Fail(string message) => GD.PrintErr($"[rig-scale] FAIL: {message}");
    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[rig-scale] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
