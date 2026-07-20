using System.Linq;
using Godot;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #170 — the engine-glue half of the
// independent height/wingspan rig scaling. RigScaleTests already pins the pure
// RigScale classifier against a hand-authored Mixamo bone list; this harness
// proves the glue (PlayerRigScaler) actually runs against the REAL imported
// Y Bot Skeleton3D, exactly as PR #257 established for engine-glue that mutates
// live engine state (a pure-resolver unit test alone is not proof the .tscn +
// live skeleton are wired right).
//
//   godot --headless --path . res://tests/integration/RigScaleHarnessTest.tscn -- --harness-scenario=independent-scaling
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//
// ── Why this is the test that catches the real risk ──────────────────────────
// The doubt-review of RigScale flagged that a rig whose bone names don't match
// the Mixamo convention would classify EVERY bone as Neither and silently scale
// nothing — a vacuous "pass." This harness closes that: it asserts the classifier
// matched a NON-ZERO number of real height and wingspan bones on the actually-
// imported skeleton, and that a height-only scale visibly changes the height
// bones' pose while leaving the wingspan chain and the Hips root at their
// baseline. It also PRINTS every imported bone name first, so if the assumption
// about Mixamo naming (mixamorig: prefix, etc.) is ever wrong, the CI log names
// the real bones rather than leaving a silent no-op.
//
// Single offline instance is enough (same reasoning as PivotAnimTest): no
// MultiplayerPeer means unique_id 1 is both server and local, and the scaler is
// cosmetic/offline anyway — it never touches replicated state.
public partial class RigScaleHarnessTest : Node
{
    private const double TimeoutSeconds = 10.0;
    private const float Epsilon = 0.01f;

    private string _scenario = "independent-scaling";
    private PlayerController _player;
    private PlayerRigScaler _scaler;
    private Skeleton3D _skeleton;
    private int _frame;
    private double _elapsed;
    private bool _finished;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "independent-scaling");
        GD.Print($"[rig-scale] scenario={_scenario} booting headless…");

        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        var inst = scene.Instantiate<PlayerController>();
        inst.Name = "1";
        AddChild(inst);
        _player = inst;
        _scaler = inst.GetNodeOrNull<PlayerRigScaler>("RigScaler");
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        // Give the instanced scene a couple of frames to run _Ready on every node
        // (the scaler resolves its skeleton and applies the identity 1.0/1.0 in
        // its own _Ready).
        if (_frame < 2) return;

        if (_scenario == "independent-scaling") RunIndependentScaling();
        else { Fail($"unknown scenario '{_scenario}'."); Finish(); }

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame} without a verdict.");
            Finish();
        }
    }

    private void RunIndependentScaling()
    {
        if (_scaler == null) { Fail("Player.tscn has no RigScaler node (PlayerRigScaler)."); Finish(); return; }
        if (!_scaler.SkeletonResolvedForHarness) { Fail("RigScaler resolved no Skeleton3D under the player root."); Finish(); return; }

        _skeleton = FindSkeleton(_player);
        if (_skeleton == null) { Fail("could not locate a Skeleton3D in the instanced Player.tscn."); Finish(); return; }

        // Diagnostic: dump the real imported bone names + how RigScale classified
        // each. If a naming assumption is wrong, this is what tells us the truth.
        int count = _skeleton.GetBoneCount();
        GD.Print($"[rig-scale] imported skeleton has {count} bones:");
        for (int i = 0; i < count; i++)
        {
            string name = _skeleton.GetBoneName(i);
            GD.Print($"[rig-scale]   [{i}] '{name}' -> {RigScale.Classify(name)}");
        }
        GD.Print($"[rig-scale] classified height={_scaler.HeightBonesScaledForHarness} " +
                 $"wingspan={_scaler.WingspanBonesScaledForHarness}");

        // (1) The classifier must have matched real bones — the silent-no-op guard.
        if (_scaler.HeightBonesScaledForHarness <= 0 || _scaler.WingspanBonesScaledForHarness <= 0)
        {
            Fail($"expected >0 height AND >0 wingspan bones on the real skeleton, got " +
                 $"height={_scaler.HeightBonesScaledForHarness}, wingspan={_scaler.WingspanBonesScaledForHarness} " +
                 "— the rig's bone names likely don't match RigScale's Mixamo convention (see dump above).");
            Finish();
            return;
        }

        // Pick a representative bone from each band on the ACTUAL skeleton.
        int heightBone = FirstBoneWhere(count, RigChain.Height);
        int wingspanBone = FirstBoneWhere(count, RigChain.Wingspan);
        int rootBone = FirstRootNeitherBone(count); // the Hips pelvis root

        if (heightBone < 0 || wingspanBone < 0 || rootBone < 0)
        {
            Fail($"missing a representative bone (height={heightBone}, wingspan={wingspanBone}, root={rootBone}).");
            Finish();
            return;
        }

        // After the scaler's identity _Ready apply, current pose scale == baseline.
        Vector3 heightBefore = _skeleton.GetBonePoseScale(heightBone);
        Vector3 wingspanBefore = _skeleton.GetBonePoseScale(wingspanBone);
        Vector3 rootBefore = _skeleton.GetBonePoseScale(rootBone);

        // (2) Height-ONLY scale: height chain grows 2x; wingspan chain and the
        // root stay at baseline. This is the write-set independence, proven on the
        // real skeleton (not just the classifier's token sets).
        _scaler.SetHeight(2.0f);

        Vector3 heightAfter = _skeleton.GetBonePoseScale(heightBone);
        Vector3 wingspanAfter = _skeleton.GetBonePoseScale(wingspanBone);
        Vector3 rootAfter = _skeleton.GetBonePoseScale(rootBone);

        bool heightScaled = Approx(heightAfter, heightBefore * 2.0f);
        bool wingspanUnchanged = Approx(wingspanAfter, wingspanBefore);
        bool rootUnchanged = Approx(rootAfter, rootBefore);

        // (3) Default identity round-trip: back to 1.0 restores the baseline.
        _scaler.SetHeight(1.0f);
        bool identityRestored = Approx(_skeleton.GetBonePoseScale(heightBone), heightBefore);

        bool pass = heightScaled && wingspanUnchanged && rootUnchanged && identityRestored;
        if (pass)
        {
            GD.Print("[rig-scale] PASS independent-scaling — height-only scale grew the height " +
                     "chain, left the wingspan chain and the Hips root untouched, and 1.0 restored baseline.");
        }
        else
        {
            Fail($"independent-scaling: heightScaled={heightScaled} (before {heightBefore} after {heightAfter}), " +
                 $"wingspanUnchanged={wingspanUnchanged} (before {wingspanBefore} after {wingspanAfter}), " +
                 $"rootUnchanged={rootUnchanged} (before {rootBefore} after {rootAfter}), " +
                 $"identityRestored={identityRestored}.");
        }
        Finish(pass ? 0 : 1);
    }

    private int FirstBoneWhere(int count, RigChain chain)
    {
        for (int i = 0; i < count; i++)
            if (RigScale.Classify(_skeleton.GetBoneName(i)) == chain) return i;
        return -1;
    }

    // The Hips pelvis: a Neither-classified bone with no parent (the skeleton root).
    private int FirstRootNeitherBone(int count)
    {
        for (int i = 0; i < count; i++)
            if (_skeleton.GetBoneParent(i) < 0 && RigScale.Classify(_skeleton.GetBoneName(i)) == RigChain.Neither)
                return i;
        return -1;
    }

    private static Skeleton3D FindSkeleton(Node root)
    {
        if (root is Skeleton3D s) return s;
        var matches = root.FindChildren("*", nameof(Skeleton3D), recursive: true, owned: false);
        return matches.Count > 0 ? matches[0] as Skeleton3D : null;
    }

    private static bool Approx(Vector3 a, Vector3 b) =>
        Mathf.Abs(a.X - b.X) < Epsilon && Mathf.Abs(a.Y - b.Y) < Epsilon && Mathf.Abs(a.Z - b.Z) < Epsilon;

    private void Fail(string message) => GD.PrintErr($"[rig-scale] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[rig-scale] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
