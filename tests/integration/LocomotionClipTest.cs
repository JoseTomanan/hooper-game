using System.Collections.Generic;
using System.Linq;
using Godot;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #267 — proves the retargeted
// locomotion clips (idle/run/pivot in assets/locomotion.res) actually
// resolve against Y Bot's REAL Skeleton3D as it sits in the live
// scenes/Player.tscn (CharacterModel is an instance of Y Bot.fbx, whose own
// hierarchy is "Y Bot -> Skeleton3D" with NO "Root" wrapper — confirmed by
// dumping Y Bot.fbx's node tree headlessly during #267's Stage 1 spike), not
// just in an isolated FindBone check against a loaded-but-unattached
// skeleton. Before #267, EVERY bone track in every clip failed to resolve
// (the clips were authored against Kenney deform-bone names — Hips, Chest,
// LeftUpLeg, ... — while Y Bot's bones are all "mixamorig_"-prefixed), which
// is the literal T-pose bug. This harness is the RigScaleHarnessTest-style
// "don't guess, print the real names and assert against them" proof for that
// fix, generalized across all three clips.
//
// Deliberately does NOT assert the pose LOOKS right — per spike #87, driving
// AnimationTree.Advance() and sampling rendered bone poses headlessly needs a
// custom MainLoop frame pump, out of scope here. Only track *resolution* is
// state-checkable today; pose correctness (especially `pivot`, which has no
// rest-fixer pass — it was hand-remapped bone-name-only, see the #267 PR
// body) is the deferred human feel judgment (#178/#173, ADR-0021).
//
//   godot --headless --path . res://tests/integration/LocomotionClipTest.tscn
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
public partial class LocomotionClipTest : Node
{
    private const double TimeoutSeconds = 10.0;

    private PlayerController _player;
    private int _frame;
    private double _elapsed;
    private bool _finished;

    public override void _Ready()
    {
        GD.Print("[locomotion-clip] booting headless…");

        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        var inst = scene.Instantiate<PlayerController>();
        inst.Name = "1";
        AddChild(inst);
        _player = inst;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        // Give the instanced scene a couple of frames to run _Ready on every
        // node (mirrors RigScaleHarnessTest's own settle window) before
        // trusting the skeleton is fully resolved.
        if (_frame < 2) return;

        RunCheck();

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail("timed out without reaching a verdict.");
            Finish(1);
        }
    }

    private void RunCheck()
    {
        var skeleton = FindSkeleton(_player);
        if (skeleton == null)
        {
            Fail("could not locate a Skeleton3D in the instanced Player.tscn.");
            Finish(1);
            return;
        }

        GD.Print($"[locomotion-clip] live skeleton '{skeleton.Name}' has {skeleton.GetBoneCount()} bones.");

        var lib = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        if (lib == null)
        {
            Fail("assets/locomotion.res failed to load.");
            Finish(1);
            return;
        }

        var clipNames = lib.GetAnimationList();
        GD.Print($"[locomotion-clip] locomotion.res clips: {string.Join(", ", clipNames)}");

        // Vacuous-pass guard #1: the library itself must carry the three
        // clips Player.tscn's AnimationTree references (locomotion/idle,
        // locomotion/run, locomotion/pivot) — an empty or renamed library
        // would trivially "pass" a bare per-clip loop below.
        string[] expected = { "idle", "run", "pivot" };
        var missingClips = expected.Where(e => !clipNames.Contains(e)).ToArray();
        if (missingClips.Length > 0)
        {
            Fail($"locomotion.res is missing expected clip(s): {string.Join(", ", missingClips)}.");
            Finish(1);
            return;
        }

        bool allPass = true;
        foreach (var clipName in clipNames)
        {
            var anim = lib.GetAnimation(clipName);
            int total = 0;
            int resolved = 0;
            var unresolved = new List<string>();
            for (int i = 0; i < anim.GetTrackCount(); i++)
            {
                var path = anim.TrackGetPath(i);
                if (path.GetSubNameCount() == 0) continue; // not a bone track
                total++;
                string boneName = path.GetSubName(0);
                if (skeleton.FindBone(boneName) >= 0) resolved++;
                else unresolved.Add(boneName);
            }

            GD.Print($"[locomotion-clip]   '{clipName}': total_bone_tracks={total} resolved={resolved}");

            // Vacuous-pass guard #2: a clip with zero bone tracks would
            // trivially satisfy resolved==total without proving anything —
            // exactly RigScaleHarnessTest's "heightBones <= 0" style check.
            if (total <= 0)
            {
                Fail($"clip '{clipName}' has zero bone tracks — vacuous, not proof.");
                allPass = false;
                continue;
            }

            if (resolved != total)
            {
                Fail($"clip '{clipName}': {total - resolved}/{total} bone tracks did not resolve " +
                     $"(unresolved: {string.Join(", ", unresolved)}).");
                allPass = false;
            }
        }

        if (allPass)
        {
            GD.Print("[locomotion-clip] PASS — idle/run/pivot all fully resolve on the live Y Bot skeleton.");
        }

        // --- Issue #271 assertion family 1: loop mode -----------------------
        // idle/run are the two clips that actually loop during gameplay (a
        // BlendSpace1D between them drives locomotion); pivot already shipped
        // correctly and acts as the CONTROL that proves this assertion itself
        // is discriminating (if pivot ever failed here, the assertion logic —
        // not the fix — would be the suspect).
        string[] mustLoop = { "idle", "run", "pivot" };
        foreach (var clipName in mustLoop)
        {
            var anim = lib.GetAnimation(clipName);
            var mode = anim.LoopMode;
            GD.Print($"[locomotion-clip]   '{clipName}': loop_mode={mode}");
            if (mode != Animation.LoopModeEnum.Linear)
            {
                Fail($"clip '{clipName}' has loop_mode={mode}, expected Linear (issue #271 — " +
                     "the FBX importer's per-clip default is LOOP_NONE, so run visibly freezes " +
                     "after its first pass unless the import config or rebuild step sets it).");
                allPass = false;
            }
        }

        // --- Issue #271 assertion family 2: no T-pose anchor -----------------
        // The retarget's rest-fixer preserved delta-from-source-rest instead of
        // world pose: idle/run's frame-0 arm rotation landed EXACTLY on Y Bot's
        // raw import rest (0.000000 deg deviation) — and Y Bot's rest IS a
        // T-pose (arms-horizontal is baked into the Shoulder/Arm rest). Assert
        // the first key of each arm-chain rotation track deviates from that
        // same skeleton's rest by a clearly non-zero margin. Threshold is 10
        // degrees: the observed bug value is exactly 0.0 deg, and a corrected
        // arms-down idle/run pose is expected in the 40-90 deg range for
        // Arm/Shoulder — 10 deg leaves an order-of-magnitude margin against
        // both floating-point noise and a partial (but still visibly wrong)
        // fix, without being anywhere near tight enough to demand exact pose
        // correctness (that's the deferred human feel judgment, #178/#173).
        const double TposeAngleThresholdDeg = 10.0;
        string[] armBones = { "mixamorig_LeftArm", "mixamorig_RightArm" };
        string[] anchorClips = { "idle", "run" };
        foreach (var clipName in anchorClips)
        {
            var anim = lib.GetAnimation(clipName);
            foreach (var boneName in armBones)
            {
                int boneIdx = skeleton.FindBone(boneName);
                if (boneIdx < 0)
                {
                    Fail($"clip '{clipName}': skeleton has no bone '{boneName}' to check against — " +
                         "cannot evaluate the T-pose-anchor assertion.");
                    allPass = false;
                    continue;
                }
                Quaternion restRot = skeleton.GetBoneRest(boneIdx).Basis.GetRotationQuaternion();

                int trackIdx = FindRotationTrack(anim, boneName);
                if (trackIdx < 0)
                {
                    Fail($"clip '{clipName}': no rotation track for bone '{boneName}' — " +
                         "cannot evaluate the T-pose-anchor assertion.");
                    allPass = false;
                    continue;
                }
                if (anim.TrackGetKeyCount(trackIdx) <= 0)
                {
                    Fail($"clip '{clipName}': rotation track for '{boneName}' has zero keys — vacuous, not proof.");
                    allPass = false;
                    continue;
                }

                var firstKey = (Quaternion)anim.TrackGetKeyValue(trackIdx, 0);
                double deviationDeg = QuaternionAngleDeg(firstKey, restRot);
                GD.Print($"[locomotion-clip]   '{clipName}' first-key-vs-rest '{boneName}': {deviationDeg:F6} deg");

                if (deviationDeg < TposeAngleThresholdDeg)
                {
                    Fail($"clip '{clipName}': '{boneName}' first key is only {deviationDeg:F6} deg from Y Bot's " +
                         $"rest (T-pose) — expected >= {TposeAngleThresholdDeg} deg (issue #271 T-pose anchor bug).");
                    allPass = false;
                }
            }
        }

        Finish(allPass ? 0 : 1);
    }

    private static int FindRotationTrack(Animation anim, string boneName)
    {
        for (int i = 0; i < anim.GetTrackCount(); i++)
        {
            if (anim.TrackGetType(i) != Animation.TrackType.Rotation3D) continue;
            var path = anim.TrackGetPath(i);
            if (path.GetSubNameCount() == 0) continue;
            if (path.GetSubName(0) == boneName) return i;
        }
        return -1;
    }

    // Shortest-arc angle between two rotations, in degrees. Mirrors the
    // diagnosis probe's quat_angle_deg helper so the harness reproduces the
    // exact numbers already used to confirm the root cause.
    private static double QuaternionAngleDeg(Quaternion a, Quaternion b)
    {
        double dot = Mathf.Clamp(Mathf.Abs(a.Normalized().Dot(b.Normalized())), -1.0f, 1.0f);
        return Mathf.RadToDeg(2.0 * Mathf.Acos((float)dot));
    }

    private static Skeleton3D FindSkeleton(Node root)
    {
        if (root is Skeleton3D s) return s;
        var matches = root.FindChildren("*", nameof(Skeleton3D), recursive: true, owned: false);
        return matches.Count > 0 ? matches[0] as Skeleton3D : null;
    }

    private static void Fail(string message) => GD.PrintErr($"[locomotion-clip] FAIL: {message}");

    private void Finish(int code)
    {
        _finished = true;
        GD.Print($"[locomotion-clip] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
