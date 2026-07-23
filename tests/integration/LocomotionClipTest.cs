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
// custom MainLoop frame pump, out of scope here. Three bounded clip-property
// assertion families sit on top of track resolution:
//   1. loop_mode (#271 — the import default LOOP_NONE shipped once, freezing
//      run after a single pass);
//   2. a T-pose-anchor guard for idle/run (#271 — each arm chain's first
//      rotation key must sit well off the skeleton's rest, because the rest
//      fixer without fix_silhouette anchors clips at the target rest);
//   3. a rest-delta guard for pivot (#273 — every rotation key on pivot's 4
//      tracks must sit NEAR Y Bot's rest instead of far from it — the
//      OPPOSITE polarity of (2) — because pivot's hand-authored keys were
//      Kenney-rest-relative and Godot's absolute ROTATION_3D tracks handed
//      Y Bot's bones the raw Kenney rest orientations verbatim).
// Whether the corrected pose actually looks RIGHT remains the deferred human
// feel judgment (#178/#173, ADR-0021) — but as of #273, pivot's pose is now
// numerically anchored to Y Bot's own rests via the rest-delta correction,
// the same footing as idle/run's rest-fixer pass; only visual pose quality
// is still unverified, not track-level correctness.
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

        // --- Issue #273 assertion family: pivot rest-delta correction -------
        // pivot (authored in #242 against the KENNEY characterMedium.fbx rig,
        // then bone-name-only remapped in #267) carries rotation keys
        // expressed against KENNEY's rest orientations, not Y Bot's. Godot
        // ROTATION_3D tracks are absolute local rotations, so unlike idle/run
        // (which went through the importer's rest-fixer), pivot's raw Kenney
        // rest quats get handed to Y Bot's bones verbatim — Hips/LeftUpLeg/
        // RightUpLeg land 177-180 deg off Y Bot's rest (confirmed exact in the
        // issue's fact table; Spine happens to coincide across rigs at ~0deg,
        // which is why the pose reads "collapsed", not uniformly rotated).
        //
        // Polarity is the OPPOSITE of the #271 T-pose-anchor guard above:
        // pivot's CORRECT keys sit NEAR Y Bot rest (small authored deltas
        // around a live stance), not far from it. Threshold 15 deg: the
        // observed bug values are 177-180 deg (an order of magnitude beyond),
        // and the authored inter-key motion is only 6-10 deg per track, so 15
        // deg comfortably separates "still broken" from "corrected" without
        // demanding exact pose correctness (that stays the deferred human
        // feel judgment, #178/#173).
        const double PivotRestDeltaThresholdDeg = 15.0;
        // The correction is a left-multiplication by a unit quaternion (an
        // isometry) — it must preserve the authored inter-key motion exactly.
        // Guard against a "fix" that collapses pivot to static rests instead
        // of correcting them: every track's keys must still span at least
        // this much pairwise deviation. Observed authored motion is 6-10 deg;
        // 3 deg leaves margin against floating-point noise while still ruling
        // out a degenerate all-keys-equal "fix".
        const double PivotMinPairwiseMotionDeg = 3.0;
        // Guards against a "fix" that silently drops/merges tracks — pivot is
        // documented (root-cause table) as exactly 4 rotation tracks.
        const int PivotExpectedRotationTrackCount = 4;

        var pivotAnim = lib.GetAnimation("pivot");
        var pivotRotationTracks = new List<(int TrackIdx, string BoneName)>();
        for (int i = 0; i < pivotAnim.GetTrackCount(); i++)
        {
            if (pivotAnim.TrackGetType(i) != Animation.TrackType.Rotation3D) continue;
            var path = pivotAnim.TrackGetPath(i);
            if (path.GetSubNameCount() == 0) continue;
            pivotRotationTracks.Add((i, path.GetSubName(0)));
        }

        GD.Print($"[locomotion-clip]   'pivot': rotation_track_count={pivotRotationTracks.Count}");
        if (pivotRotationTracks.Count != PivotExpectedRotationTrackCount)
        {
            Fail($"clip 'pivot': expected {PivotExpectedRotationTrackCount} rotation tracks, " +
                 $"found {pivotRotationTracks.Count}.");
            allPass = false;
        }

        foreach (var (trackIdx, boneName) in pivotRotationTracks)
        {
            int boneIdx = skeleton.FindBone(boneName);
            if (boneIdx < 0)
            {
                Fail($"clip 'pivot': skeleton has no bone '{boneName}' to check against — " +
                     "cannot evaluate the rest-delta assertion.");
                allPass = false;
                continue;
            }
            Quaternion restRot = skeleton.GetBoneRest(boneIdx).Basis.GetRotationQuaternion();

            int keyCount = pivotAnim.TrackGetKeyCount(trackIdx);
            if (keyCount <= 0)
            {
                Fail($"clip 'pivot': rotation track for '{boneName}' has zero keys — vacuous, not proof.");
                allPass = false;
                continue;
            }

            var keys = new List<Quaternion>(keyCount);
            for (int k = 0; k < keyCount; k++)
            {
                keys.Add((Quaternion)pivotAnim.TrackGetKeyValue(trackIdx, k));
            }

            double maxRestDeviationDeg = 0.0;
            foreach (var key in keys)
            {
                double deviationDeg = QuaternionAngleDeg(key, restRot);
                if (deviationDeg > maxRestDeviationDeg) maxRestDeviationDeg = deviationDeg;
            }

            double maxPairwiseDeviationDeg = 0.0;
            for (int a = 0; a < keys.Count; a++)
            {
                for (int b = a + 1; b < keys.Count; b++)
                {
                    double devDeg = QuaternionAngleDeg(keys[a], keys[b]);
                    if (devDeg > maxPairwiseDeviationDeg) maxPairwiseDeviationDeg = devDeg;
                }
            }

            GD.Print($"[locomotion-clip]   'pivot' '{boneName}': max_vs_ybot_rest={maxRestDeviationDeg:F6} deg, " +
                      $"max_pairwise_key_deviation={maxPairwiseDeviationDeg:F6} deg");

            if (maxRestDeviationDeg >= PivotRestDeltaThresholdDeg)
            {
                Fail($"clip 'pivot': '{boneName}' has a key {maxRestDeviationDeg:F6} deg from Y Bot's rest — " +
                     $"expected < {PivotRestDeltaThresholdDeg} deg (issue #273 Kenney-rest-relative bug).");
                allPass = false;
            }

            if (maxPairwiseDeviationDeg < PivotMinPairwiseMotionDeg)
            {
                Fail($"clip 'pivot': '{boneName}' keys only span {maxPairwiseDeviationDeg:F6} deg pairwise — " +
                     $"expected >= {PivotMinPairwiseMotionDeg} deg (clip must still actually animate, not " +
                     "collapse to static rests).");
                allPass = false;
            }
        }

        // --- Issue #275 assertion family: idle<->run blend-compatibility ----
        // The Locomotion BlendSpace1D (idle@0.0 <-> run@6.0) transits an
        // out-of-corridor pose at intermediate blend weights on the upper-leg
        // bones (the human-visible start/stop-run twitch). Root cause,
        // confirmed by a headless empirical probe (Godot 4.7.1, real Y Bot
        // skeleton, code-built blend space mirroring this scene's config):
        //   - mixamorig_LeftUpLeg: min signed dot between idle/run keys was
        //     -0.962 (near-antipodal REPRESENTATION for orientations only
        //     ~32 deg apart physically -- a hemisphere-flip data defect).
        //   - mixamorig_RightUpLeg: min signed dot was -0.215; worst-pair
        //     physical angle 162 deg -- anatomically absurd for a thigh
        //     between an idle stance and a running stride, pointing at a
        //     genuine ~180 deg twist about the bone's own axis (silhouette-
        //     disambiguation picked the wrong branch for this one bone during
        //     #267's retarget), not just a representation artifact.
        //   - Every OTHER shared rotation track already has a positive min
        //     signed dot (Hips 0.997, Spine 0.994, LeftLeg 0.656, ...) --
        //     these two UpLeg bones are the only violators.
        //
        // Two independent invariants pin what the fix must establish:
        //   (a) signed-dot continuity: every cross pair of (idle key, run
        //       key) on a shared bone must have a non-negative dot product
        //       (no antipodal-quaternion / hemisphere-flip data defect);
        //   (b) anatomical bound: on the UpLeg bones specifically, the worst
        //       cross-pair PHYSICAL angle (dot-sign-independent, via the
        //       existing QuaternionAngleDeg helper) must stay <= 90 deg --
        //       comfortably above every OTHER bone's own worst-pair angle
        //       once the twist is corrected (RightUpLeg drops from 162 deg to
        //       65 deg), while still an order of magnitude under the observed
        //       162 deg bug value, so this threshold cleanly separates
        //       "genuinely close" from "twisted" without demanding exact pose
        //       correctness (pose *quality* stays the deferred human feel
        //       judgment, #178/#173, ADR-0021 -- this pins data-level
        //       correctness only).
        // A third, weaker invariant (c) pins ordinary interpolation hygiene
        // within each clip on its own: consecutive keys in the SAME track
        // must already have a non-negative dot (no internal hemisphere hop),
        // which every OTHER assertion family above implicitly assumes holds.
        const double SignedDotFloor = 0.0;
        const double UpLegAngleThresholdDeg = 90.0;
        string[] upLegBones = { "mixamorig_LeftUpLeg", "mixamorig_RightUpLeg" };

        var idleAnim = lib.GetAnimation("idle");
        var runAnim = lib.GetAnimation("run");

        var idleRotationBones = new List<string>();
        for (int i = 0; i < idleAnim.GetTrackCount(); i++)
        {
            if (idleAnim.TrackGetType(i) != Animation.TrackType.Rotation3D) continue;
            var path = idleAnim.TrackGetPath(i);
            if (path.GetSubNameCount() == 0) continue;
            idleRotationBones.Add(path.GetSubName(0));
        }

        var sharedBones = new List<string>();
        for (int i = 0; i < runAnim.GetTrackCount(); i++)
        {
            if (runAnim.TrackGetType(i) != Animation.TrackType.Rotation3D) continue;
            var path = runAnim.TrackGetPath(i);
            if (path.GetSubNameCount() == 0) continue;
            var bone = path.GetSubName(0);
            if (idleRotationBones.Contains(bone)) sharedBones.Add(bone);
        }

        // Vacuous-pass guard: idle/run share plenty of skeletal rotation
        // tracks (23 in the pre-fix asset) -- a near-empty overlap would mean
        // this whole assertion family isn't actually exercising anything.
        if (sharedBones.Count < 10)
        {
            Fail($"clip 'idle'/'run': only {sharedBones.Count} shared ROTATION_3D bone tracks found -- " +
                 "expected >= 10; the #275 blend-compatibility assertion family would be vacuous.");
            allPass = false;
        }

        foreach (var bone in sharedBones)
        {
            int idleTrack = FindRotationTrack(idleAnim, bone);
            int runTrack = FindRotationTrack(runAnim, bone);
            int idleKeyCount = idleAnim.TrackGetKeyCount(idleTrack);
            int runKeyCount = runAnim.TrackGetKeyCount(runTrack);
            if (idleKeyCount <= 0 || runKeyCount <= 0)
            {
                Fail($"clip 'idle'/'run': bone '{bone}' has a zero-key rotation track -- vacuous, not proof.");
                allPass = false;
                continue;
            }

            double minCrossDot = double.PositiveInfinity;
            double maxCrossAngleDeg = 0.0;
            for (int ik = 0; ik < idleKeyCount; ik++)
            {
                var idleKey = (Quaternion)idleAnim.TrackGetKeyValue(idleTrack, ik);
                for (int rk = 0; rk < runKeyCount; rk++)
                {
                    var runKey = (Quaternion)runAnim.TrackGetKeyValue(runTrack, rk);
                    double dot = idleKey.Normalized().Dot(runKey.Normalized());
                    if (dot < minCrossDot) minCrossDot = dot;
                    double angleDeg = QuaternionAngleDeg(idleKey, runKey);
                    if (angleDeg > maxCrossAngleDeg) maxCrossAngleDeg = angleDeg;
                }
            }

            bool isUpLeg = System.Array.IndexOf(upLegBones, bone) >= 0;
            GD.Print($"[locomotion-clip]   #275 '{bone}': min_cross_signed_dot={minCrossDot:F6} " +
                      $"max_cross_angle_deg={maxCrossAngleDeg:F6} is_upleg={isUpLeg}");

            if (minCrossDot < SignedDotFloor)
            {
                Fail($"clip 'idle'/'run': bone '{bone}' has a cross-clip key pair with signed dot " +
                     $"{minCrossDot:F6} (< {SignedDotFloor}) -- antipodal-quaternion / hemisphere-flip " +
                     "defect (issue #275): the idle<->run blend will transit the long way around the " +
                     "sphere at intermediate weights instead of staying in the idle/run pose corridor.");
                allPass = false;
            }

            if (isUpLeg && maxCrossAngleDeg > UpLegAngleThresholdDeg)
            {
                Fail($"clip 'idle'/'run': UpLeg bone '{bone}' has a cross-clip key pair {maxCrossAngleDeg:F6} " +
                     $"deg apart (> {UpLegAngleThresholdDeg} deg) -- anatomically implausible for a thigh " +
                     "between an idle stance and a running stride; points at an uncorrected twist about the " +
                     "bone's own axis (issue #275).");
                allPass = false;
            }
        }

        // Intra-track continuity (c): within EACH clip on its own, every
        // consecutive key pair on every rotation track must already share a
        // non-negative dot, or that clip's own key-to-key interpolation
        // (independent of any cross-clip blending) already hops hemispheres.
        foreach (var (clipName, anim) in new[] { ("idle", idleAnim), ("run", runAnim) })
        {
            for (int i = 0; i < anim.GetTrackCount(); i++)
            {
                if (anim.TrackGetType(i) != Animation.TrackType.Rotation3D) continue;
                int keyCount = anim.TrackGetKeyCount(i);
                if (keyCount < 2) continue;

                var prev = (Quaternion)anim.TrackGetKeyValue(i, 0);
                for (int k = 1; k < keyCount; k++)
                {
                    var cur = (Quaternion)anim.TrackGetKeyValue(i, k);
                    double dot = prev.Normalized().Dot(cur.Normalized());
                    if (dot < SignedDotFloor)
                    {
                        var path = anim.TrackGetPath(i);
                        string boneName = path.GetSubNameCount() > 0 ? path.GetSubName(0) : $"track[{i}]";
                        Fail($"clip '{clipName}': bone '{boneName}' has consecutive keys {k - 1}->{k} with " +
                             $"signed dot {dot:F6} (< {SignedDotFloor}) -- intra-track hemisphere hop " +
                             "(issue #275 continuity invariant).");
                        allPass = false;
                    }
                    prev = cur;
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
