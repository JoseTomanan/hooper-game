using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness that captures the defect #294 closes, measured
// directly (2026-07-28 session): the ball's in-hand position is NOT
// bone-attached. BallController.TickDribbling (scripts/Ball/BallController.cs)
// places the ball at holderPos + forward*offset + HandRight(forward)*HandOffset*
// HandSign(holder) — HandSign reads the server-authoritative PlayerController.
// HandSide (ADR-0012). But scenes/Player.tscn's Dribble AnimationTree state WAS
// a single BlendSpace1D with NO hand-side split, so the SAME clip played
// regardless of HandSide, and that clip was measured to animate the RIGHT hand
// (pump range L=0.0087 m vs R=0.3450 m at +0.5276 m lateral). Whenever HandSide
// was Left — every possession after a crossover — the ball sat at NEGATIVE
// lateral (HandSign(Left) = -1) while the animated hand pumped at POSITIVE
// lateral: opposite sides of the body.
//
// #294 splits that state into DribbleLeft/DribbleRight over a genuine mirrored
// clip pair, and this harness is the proof. It was written on its own branch
// BEFORE the fix, deliberately red, so the red output names the defect.
//
//   godot --headless --path . res://tests/integration/DribbleHandAlignmentTest.tscn -- --harness-scenario=hand-alignment-left
//   godot --headless --path . res://tests/integration/DribbleHandAlignmentTest.tscn -- --harness-scenario=hand-alignment-right
//   godot --headless --path . res://tests/integration/DribbleHandAlignmentTest.tscn -- --harness-scenario=hand-follows-authoritative-flip
//   godot --headless --path . res://tests/integration/DribbleHandAlignmentTest.tscn -- --harness-scenario=dribble-states-own-clips
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//
// ── Why every scenario FORCES the hand rather than relying on the default ───
// It used to be load-bearing that HandSide's initializer was Left. ADR-0012's
// 2026-07-28 amendment flipped the possession-reset default to Right, which
// silently inverted what "hand-alignment-left" was even testing — it began
// asserting the Right case under the Left name, and the flip scenario's
// window 1 premise (HandSide==Left) became unreachable. So no scenario here
// reads the default any more: each states the polarity it wants via
// SetHandSideForHarness and asserts it held. A test whose meaning depends on a
// default it does not set is a test that changes meaning without changing.
//
// ── Why HandRightForHarness/HolderForwardForHarness, not a hand-rolled axis ──
// This harness reads the lateral axis via BallController's own passthroughs
// rather than re-deriving Cross(forward, Up) or Heading->forward independently.
// Re-deriving it by hand is exactly the failure class that shipped bug #255 (a
// mirrored predicate whose only controls were left/right-symmetric, so it
// passed green while inverted) — a second, slightly-different formula in test
// code could quietly disagree with production. There is exactly one source of
// truth for "which way is right" here, and this harness reads it, it does not
// reimplement it.
//
// ── Why the dribbling hand is IDENTIFIED at runtime, not hardcoded ──────────
// The whole point of this harness is to not assume which hand the clip
// animates — that is the very fact under dispute. Each scenario tracks BOTH
// hands' vertical excursion (world Y minus Hips world Y) across the
// observation window and picks the one with the larger range as "the
// dribbling hand," asserting as a PREMISE that the winner's range is
// >= MinDribblingHandRangeMeters. A premise miss means no real dribbling hand
// was identified, so every downstream comparison would be meaningless — the
// scenario fails outright rather than silently comparing noise.
//
// ── Why Skeleton3D.GetBoneGlobalPose is valid HERE (inverted vs tools/*.gd) ──
// tools/*.gd rebuild scripts hand-roll FK because their skeletons are loaded
// standalone, never added to a live SceneTree. Here the Skeleton3D sits inside
// two real, added scenes/Player.tscn instances with a live AnimationTree
// (CallbackModeProcess=Physics, same as DribbleLoopTest) actually driving
// bone poses every physics tick — GetBoneGlobalPose is the correct, and only
// necessary, way to read a rendered bone position.
//
// ── Out of scope ─────────────────────────────────────────────────────────────
// Whether the dribble clip LOOKS right is #173's deferred human feel judgment
// (ADR-0021, per LocomotionClipTest's convention) — this harness only asserts
// spatial alignment between the ball and the hand the clip actually animates,
// never pose/clip content.
public partial class DribbleHandAlignmentTest : Node
{
    private const double TimeoutSeconds = 12.0;
    private const int ArmFrames = 2;              // ticks for TryAssignTipoffHolder to run
    private const int ActionMarginFrames = 2;      // ticks between tipoff-resolved and acting
    private const int ObserveFrames = 45;          // ticks a window watches before rendering a verdict
    // A SetHandSideForHarness call that differs from the ball's last-observed
    // HandSide is indistinguishable, to AdvanceHandSweep, from a real
    // in-game crossover — it starts the SAME ~7-tick lateral sweep
    // (CrossoverSweepDuration=0.12s @ 60 ticks/s, BallController.cs) rather
    // than snapping instantly. Sampling must not start until that sweep
    // settles, or the window's first few ticks would show the ball transiently
    // on the OLD side — a timing artifact, not the defect under test — and fail
    // the per-tick sign assertion for the wrong reason. 30 ticks is a large
    // safety margin over the ~7-tick sweep.
    //
    // Both polarities now force the hand, so exactly one of them agrees with
    // the tipoff's own Held-tick reset and sweeps nowhere while the other
    // genuinely sweeps. Which one is whichever way ADR-0012's reset default
    // currently points, and this harness deliberately does not care: the
    // WaitSweep step gates on SweepActiveForHarness, so a no-op force falls
    // straight through and a real one waits. That is what makes the pair
    // symmetric under a future change to that default.
    private const int SweepSettleFrames = 30;
    private const float MinDribblingHandRangeMeters = 0.15f;
    // See the non-symmetric-discriminator block in VerdictHandAlignment.
    private const float MinHandDominanceRatio = 5.0f;
    private const float MaxBallToHandDistanceMeters = 0.30f;

    private static readonly Vector3 HolderSpot = new(0f, 0f, 0f);
    private static readonly Vector3 FarSpot = new(12f, 0f, 12f); // keeps the other player out of PickupRadius

    private string _scenario = "hand-alignment-left";

    private BallController _ball;
    private PlayerController _p1;
    private PlayerController _p2;

    private int _frame;
    private double _elapsed;
    private bool _finished;

    private int _holderId;
    private Skeleton3D _holderSkeleton;
    private int _leftHandBoneIdx = -1;
    private int _rightHandBoneIdx = -1;
    private int _hipsBoneIdx = -1;

    private enum Step { AwaitTipoff, Act, WaitSweep, Settle1, FlipAct, Settle2 }
    private Step _step = Step.AwaitTipoff;
    private int _stepDeadlineFrame;

    private readonly List<Sample> _window1 = new();
    private readonly List<Sample> _window2 = new();

    // (#294) Every distinct AnimationTree node observed as ACTIVE while each
    // window was open, latched at observation time.
    //
    // The geometric assertions below are the real proof — they measure where
    // the ball and the animated hand physically are — but they cannot say WHY a
    // polarity was right, and a harness that only measures geometry would pass
    // on a build that reached the correct pose down some other path. This is the
    // #257 discipline: read what the state machine ACTUALLY entered
    // (AnimationNodeStateMachinePlayback.GetCurrentNode, via
    // ActiveAnimNodeForHarness), never what MoveAnimResolver merely returned.
    // Travel() to a missing or misnamed state only LOGS, so asserting the
    // resolver's own return value would keep passing against a tree that has no
    // such state at all.
    private readonly HashSet<string> _states1 = new();
    private readonly HashSet<string> _states2 = new();

    // One tick's worth of world-space observations, latched at event time
    // (not re-derived after the fact from a single end-of-window read).
    private readonly struct Sample
    {
        public readonly Vector3 BallPos;
        public readonly Vector3 LeftHandPos;
        public readonly Vector3 RightHandPos;
        public readonly Vector3 HipsPos;

        public Sample(Vector3 ballPos, Vector3 leftHandPos, Vector3 rightHandPos, Vector3 hipsPos)
        {
            BallPos = ballPos;
            LeftHandPos = leftHandPos;
            RightHandPos = rightHandPos;
            HipsPos = hipsPos;
        }
    }

    // Result of identifying which hand the live clip actually animates over a
    // window, plus the values needed to report a rich failure message.
    private readonly struct HandWinner
    {
        public readonly bool IsRight;
        public readonly float RangeMeters;
        public readonly float LeftRangeMeters;
        public readonly float RightRangeMeters;

        public HandWinner(bool isRight, float rangeMeters, float leftRangeMeters, float rightRangeMeters)
        {
            IsRight = isRight;
            RangeMeters = rangeMeters;
            LeftRangeMeters = leftRangeMeters;
            RightRangeMeters = rightRangeMeters;
        }

        public string HandName => IsRight ? "RightHand" : "LeftHand";
    }

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "hand-alignment-left");
        GD.Print($"[dribble-hand-alignment] scenario={_scenario} booting headless…");

        // Real Player.tscn instances (live AnimationTree + Skeleton3D), named
        // "1"/"2" so the OfflineMultiplayerPeer makes unique_id 1 both
        // IsServer and IsLocalPlayer (the full TickServerOwnPlayer ->
        // ApplyAnimation chain runs every tick), same as DribbleLoopTest.
        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        _p1 = scene.Instantiate<PlayerController>();
        _p1.Name = "1";
        _p2 = scene.Instantiate<PlayerController>();
        _p2.Name = "2";

        // Physics-callback lockstep so bone poses reflect the same-tick
        // Travel()/Advance() (the default Idle callback lags under
        // --headless — see DribbleLoopTest/MoveKindAnimTest's note).
        foreach (var p in new[] { _p1, _p2 })
            p.GetNode<AnimationTree>("AnimationTree").CallbackModeProcess =
                AnimationMixer.AnimationCallbackModeProcess.Physics;

        var players = new Node3D { Name = "Players" };
        players.AddChild(_p1);
        players.AddChild(_p2);

        _ball = new BallController { Name = "Ball", Players = players };

        AddChild(players); // matches scenes/Main.tscn: Players before Ball
        AddChild(_ball);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        switch (_scenario)
        {
            case "hand-alignment-left":  TickHandAlignment(forceRight: false); break;
            case "hand-alignment-right": TickHandAlignment(forceRight: true);  break;
            case "hand-follows-authoritative-flip": TickHandFollowsFlip(); break;
            case "dribble-states-own-clips": TickStatesOwnClips(); break;
            default:
                Fail($"unknown scenario '{_scenario}'.");
                Finish();
                return;
        }

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame}, scenario={_scenario}, step={_step}, ballState={_ball?.State}.");
            Finish();
        }
    }

    // Resolves the tipoff holder, separates the two players, and resolves the
    // holder's skeleton/bone indices once — mirrors DribbleLoopTest's
    // AwaitTipoffThenPosition, extended with the skeleton lookup this harness
    // additionally needs.
    private bool AwaitTipoffThenPosition()
    {
        if (_frame < ArmFrames) return false;
        if (_ball.StateMachine.HolderPeerId == 0)
        {
            Fail($"{_scenario}: tipoff never assigned a holder.");
            Finish();
            return false;
        }
        _holderId = _ball.StateMachine.HolderPeerId;
        var holder = NodeForPeer(_holderId);
        holder.GlobalPosition = HolderSpot;
        OtherNode(_holderId).GlobalPosition = FarSpot;

        var characterModel = holder.GetNodeOrNull<Node>("CharacterModel");
        _holderSkeleton = characterModel != null ? FindSkeleton(characterModel) : null;
        if (_holderSkeleton == null)
        {
            Fail($"{_scenario}: could not locate a Skeleton3D under the holder's CharacterModel.");
            Finish();
            return false;
        }

        _leftHandBoneIdx = _holderSkeleton.FindBone("mixamorig_LeftHand");
        _rightHandBoneIdx = _holderSkeleton.FindBone("mixamorig_RightHand");
        _hipsBoneIdx = _holderSkeleton.FindBone("mixamorig_Hips");
        if (_leftHandBoneIdx < 0 || _rightHandBoneIdx < 0 || _hipsBoneIdx < 0)
        {
            Fail($"{_scenario}: skeleton is missing one of mixamorig_LeftHand/RightHand/Hips " +
                 $"(found indices L={_leftHandBoneIdx}, R={_rightHandBoneIdx}, Hips={_hipsBoneIdx}).");
            Finish();
            return false;
        }
        return true;
    }

    private Sample SampleNow()
    {
        Vector3 leftHand = _holderSkeleton.GlobalTransform * _holderSkeleton.GetBoneGlobalPose(_leftHandBoneIdx).Origin;
        Vector3 rightHand = _holderSkeleton.GlobalTransform * _holderSkeleton.GetBoneGlobalPose(_rightHandBoneIdx).Origin;
        Vector3 hips = _holderSkeleton.GlobalTransform * _holderSkeleton.GetBoneGlobalPose(_hipsBoneIdx).Origin;
        return new Sample(_ball.GlobalPosition, leftHand, rightHand, hips);
    }

    // Identifies which hand the live clip actually animated over the given
    // window: the one with the larger world-Y excursion relative to Hips.
    private static HandWinner IdentifyDribblingHand(List<Sample> window)
    {
        float leftMin = window.Min(s => s.LeftHandPos.Y - s.HipsPos.Y);
        float leftMax = window.Max(s => s.LeftHandPos.Y - s.HipsPos.Y);
        float rightMin = window.Min(s => s.RightHandPos.Y - s.HipsPos.Y);
        float rightMax = window.Max(s => s.RightHandPos.Y - s.HipsPos.Y);
        float leftRange = leftMax - leftMin;
        float rightRange = rightMax - rightMin;
        bool isRight = rightRange >= leftRange;
        return new HandWinner(isRight, Math.Max(leftRange, rightRange), leftRange, rightRange);
    }

    private static Vector3 HandPos(Sample s, HandWinner winner) => winner.IsRight ? s.RightHandPos : s.LeftHandPos;

    private static Vector2 XZ(Vector3 v) => new(v.X, v.Z);

    // ── Scenarios: hand-alignment-left / hand-alignment-right ──────────────
    // Structurally identical: tipoff, force HandSide (right scenario only),
    // start a real dribble, then observe a single window. The right scenario
    // is the discriminating CONTROL — it proves the harness itself CAN pass,
    // so the left scenario's red is the real defect, not a broken assertion.
    private void TickHandAlignment(bool forceRight)
    {
        switch (_step)
        {
            case Step.AwaitTipoff:
                if (!AwaitTipoffThenPosition()) return;
                _step = Step.Act;
                _stepDeadlineFrame = _frame + ActionMarginFrames;
                return;

            case Step.Act:
                if (_frame < _stepDeadlineFrame) return;
                var holder = NodeForPeer(_holderId);
                holder.SetHandSideForHarness(forceRight ? HandSide.Right : HandSide.Left);
                // The real production entry point (PlayerController's
                // CheckAutoStartDribble calls exactly this), so the
                // DeadDribbleRule gate and the Held->Dribbling transition
                // both run genuinely rather than being simulated.
                _ball.TryStartDribble(_holderId);
                _step = Step.WaitSweep;
                _stepDeadlineFrame = _frame + SweepSettleFrames;
                return;

            case Step.WaitSweep:
                // See SweepSettleFrames' doc: forcing HandSide can trigger a
                // real ~7-tick lateral sweep on the ball. Don't start sampling
                // until it settles (or the safety margin elapses), or the
                // window would open on a mid-sweep transient.
                if (_ball.SweepActiveForHarness && _frame < _stepDeadlineFrame) return;
                _step = Step.Settle1;
                _stepDeadlineFrame = _frame + ObserveFrames;
                _window1.Clear();
                return;

            case Step.Settle1:
                {
                    var h = NodeForPeer(_holderId);
                    HandSide expected = forceRight ? HandSide.Right : HandSide.Left;
                    if (h.HandSide != expected)
                    {
                        Fail($"{_scenario}: premise broke — expected HandSide=={expected} throughout, " +
                             $"got {h.HandSide} at frame {_frame}.");
                        Finish();
                        return;
                    }
                    if (_ball.State != BallState.Dribbling)
                    {
                        Fail($"{_scenario}: premise broke — expected BallState.Dribbling throughout, " +
                             $"got {_ball.State} at frame {_frame}.");
                        Finish();
                        return;
                    }
                    _window1.Add(SampleNow());
                    _states1.Add(h.ActiveAnimNodeForHarness ?? "<null>");
                    if (_frame >= _stepDeadlineFrame) VerdictHandAlignment(expected);
                    return;
                }
        }
    }

    private void VerdictHandAlignment(HandSide expected)
    {
        if (_window1.Count == 0)
        {
            Fail($"{_scenario}: observation window is empty — nothing was sampled.");
            Finish();
            return;
        }

        var holder = NodeForPeer(_holderId);
        Vector3 forward = BallController.HolderForwardForHarness(holder);
        Vector3 right = BallController.HandRightForHarness(forward);
        Vector3 origin = holder.GlobalPosition;
        float Lat(Vector3 p) => (p - origin).Dot(right);

        var winner = IdentifyDribblingHand(_window1);
        GD.Print($"[dribble-hand-alignment] {_scenario}: identified dribbling hand={winner.HandName} " +
                 $"(leftRange={winner.LeftRangeMeters:F4} m, rightRange={winner.RightRangeMeters:F4} m, " +
                 $"winnerRange={winner.RangeMeters:F4} m).");

        if (winner.RangeMeters < MinDribblingHandRangeMeters)
        {
            Fail($"{_scenario}: PREMISE FAILED — no real dribbling hand identified. Winner range " +
                 $"{winner.RangeMeters:F4} m < required {MinDribblingHandRangeMeters} m " +
                 $"(leftRange={winner.LeftRangeMeters:F4} m, rightRange={winner.RightRangeMeters:F4} m). " +
                 "Every downstream assertion would be meaningless, so this fails rather than passes.");
            Finish();
            return;
        }

        bool signMismatch = false;
        int mismatchFrameIndex = -1;
        float mismatchBallLat = 0f, mismatchHandLat = 0f;
        float minDist = float.MaxValue;
        for (int i = 0; i < _window1.Count; i++)
        {
            var s = _window1[i];
            Vector3 handPos = HandPos(s, winner);
            float ballLat = Lat(s.BallPos);
            float handLat = Lat(handPos);
            if (!signMismatch && Math.Sign(ballLat) != Math.Sign(handLat))
            {
                signMismatch = true;
                mismatchFrameIndex = i;
                mismatchBallLat = ballLat;
                mismatchHandLat = handLat;
            }
            float dist = (XZ(s.BallPos) - XZ(handPos)).Length();
            if (dist < minDist) minDist = dist;
        }

        var lastSample = _window1[^1];
        float lastBallLat = Lat(lastSample.BallPos);
        float lastHandLat = Lat(HandPos(lastSample, winner));

        GD.Print($"[dribble-hand-alignment] {_scenario}: HandSide={expected}, dribblingHand={winner.HandName}, " +
                 $"lastTick ballLat={lastBallLat:F4} m (sign={Math.Sign(lastBallLat)}), " +
                 $"handLat={lastHandLat:F4} m (sign={Math.Sign(lastHandLat)}), " +
                 $"minBallToHandXZDistance={minDist:F4} m over {_window1.Count} ticks.");

        bool pass = true;
        if (!AssertOnlyState(_states1, "Dribble" + expected, "the observation window"))
            pass = false;

        // ── The non-symmetric discriminator ────────────────────────────────
        // Required, and this is the assertion the whole scenario turns on. The Y
        // Bot rig is mirror-symmetric across X=0 to 0.17 mm (measured, #294
        // triage), so ANY left/right-symmetric measurement passes on a broken
        // mirror — that is precisely how the #255 mirror bug shipped green.
        //
        // So assert the thing that genuinely differs: WHICH hand pumps, and by
        // how much more than the other. The source clip measures 0.3450 m on the
        // dribbling hand vs 0.0087 m on the idle one, a 39.58x ratio, so a real
        // mirror keeps that lopsidedness and merely swaps which side owns it. A
        // mirror that silently did nothing, or that swapped bone NAMES without
        // transforming the pose, lands the excursion on the wrong hand and dies
        // here — before the geometric sign check, which could otherwise be
        // satisfied by a ball that simply followed the wrong hand consistently.
        //
        // 5x, against a measured 39.58x, is deliberately loose: the claim is
        // "one hand plainly dominates," not a re-measurement of the clip. The
        // exact ratio is clip content and belongs to #173's deferred feel pass.
        bool expectRight = expected == HandSide.Right;
        float dominant = expectRight ? winner.RightRangeMeters : winner.LeftRangeMeters;
        float idle = expectRight ? winner.LeftRangeMeters : winner.RightRangeMeters;
        if (winner.IsRight != expectRight)
        {
            pass = false;
            Fail($"{_scenario}: HandSide={expected} but the clip animates the {winner.HandName} " +
                 $"(leftRange={winner.LeftRangeMeters:F4} m, rightRange={winner.RightRangeMeters:F4} m). " +
                 "The stance is playing the WRONG polarity's clip.");
        }
        else if (idle > 1e-6f && dominant / idle < MinHandDominanceRatio)
        {
            pass = false;
            Fail($"{_scenario}: HandSide={expected} — the {winner.HandName} does dominate, but only by " +
                 $"{dominant / idle:F2}x ({dominant:F4} m vs {idle:F4} m), under the required " +
                 $"{MinHandDominanceRatio}x. The source clip measures 39.58x, so a ratio this flat means the " +
                 "two hands are moving together: the mirror did not produce a genuinely one-handed dribble, " +
                 "and a symmetric clip would satisfy every other assertion here.");
        }
        else
        {
            GD.Print($"[dribble-hand-alignment] {_scenario}: hand dominance {dominant / Math.Max(idle, 1e-6f):F2}x " +
                     $"in favour of the {winner.HandName} ({dominant:F4} m vs {idle:F4} m) — non-symmetric, as required.");
        }
        if (signMismatch)
        {
            pass = false;
            Fail($"{_scenario}: lateral SIGN MISMATCH at window tick {mismatchFrameIndex} — " +
                 $"ball lateral={mismatchBallLat:F4} m (sign={Math.Sign(mismatchBallLat)}), " +
                 $"{winner.HandName} lateral={mismatchHandLat:F4} m (sign={Math.Sign(mismatchHandLat)}). " +
                 $"HandSide={expected}, dribblingHand={winner.HandName} (range={winner.RangeMeters:F4} m). " +
                 "The ball and the hand the clip actually animates are on OPPOSITE sides of the body.");
        }
        if (minDist >= MaxBallToHandDistanceMeters)
        {
            pass = false;
            Fail($"{_scenario}: min ball-to-hand XZ distance over the window is {minDist:F4} m, " +
                 $"expected < {MaxBallToHandDistanceMeters} m. HandSide={expected}, " +
                 $"dribblingHand={winner.HandName} (leftRange={winner.LeftRangeMeters:F4} m, " +
                 $"rightRange={winner.RightRangeMeters:F4} m).");
        }

        if (pass)
            GD.Print($"[dribble-hand-alignment] PASS {_scenario} — HandSide={expected}, the ball tracked " +
                     $"the {winner.HandName} the clip actually animates (min distance {minDist:F4} m, " +
                     "no lateral-sign mismatch over the window).");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: hand-follows-authoritative-flip (diagnostic) ─────────────
    // One run, two windows: dribble Left and latch signs, then flip to Right
    // (SetHandSideForHarness) and latch again after enough ticks for the ball's
    // hand-sweep (AdvanceHandSweep) to complete. The ball's sign is expected to
    // flip (it reads authoritative HandSide every tick — passes today); the
    // hand's sign is expected to ALSO flip (it does not, because the clip
    // itself carries no hand-side split) — this scenario names WHICH layer is
    // broken rather than just failing generically.
    private void TickHandFollowsFlip()
    {
        switch (_step)
        {
            case Step.AwaitTipoff:
                if (!AwaitTipoffThenPosition()) return;
                _step = Step.Act;
                _stepDeadlineFrame = _frame + ActionMarginFrames;
                return;

            case Step.Act:
                if (_frame < _stepDeadlineFrame) return;
                // Window 1 is the Left polarity, stated rather than inherited —
                // see the header note on why no scenario reads the default.
                NodeForPeer(_holderId).SetHandSideForHarness(HandSide.Left);
                _ball.TryStartDribble(_holderId);
                _step = Step.WaitSweep;
                _stepDeadlineFrame = _frame + SweepSettleFrames;
                return;

            case Step.WaitSweep:
                if (_ball.SweepActiveForHarness && _frame < _stepDeadlineFrame) return;
                _step = Step.Settle1;
                _stepDeadlineFrame = _frame + ObserveFrames;
                _window1.Clear();
                return;

            case Step.Settle1:
                {
                    var h = NodeForPeer(_holderId);
                    if (h.HandSide != HandSide.Left)
                    {
                        Fail($"{_scenario}: premise broke in window 1 — expected HandSide==Left, got " +
                             $"{h.HandSide} at frame {_frame}.");
                        Finish();
                        return;
                    }
                    if (_ball.State != BallState.Dribbling)
                    {
                        Fail($"{_scenario}: premise broke in window 1 — expected BallState.Dribbling, got " +
                             $"{_ball.State} at frame {_frame}.");
                        Finish();
                        return;
                    }
                    _window1.Add(SampleNow());
                    _states1.Add(h.ActiveAnimNodeForHarness ?? "<null>");
                    if (_frame >= _stepDeadlineFrame)
                    {
                        _step = Step.FlipAct;
                        _stepDeadlineFrame = _frame + 1;
                    }
                    return;
                }

            case Step.FlipAct:
                if (_frame < _stepDeadlineFrame) return;
                NodeForPeer(_holderId).SetHandSideForHarness(HandSide.Right);
                _step = Step.Settle2;
                // Long enough for AdvanceHandSweep's re-cross sweep
                // (~CrossoverSweepDuration, a handful of ticks) to complete
                // and settle, same ObserveFrames budget as window 1.
                _stepDeadlineFrame = _frame + ObserveFrames;
                _window2.Clear();
                return;

            case Step.Settle2:
                {
                    var h = NodeForPeer(_holderId);
                    if (h.HandSide != HandSide.Right)
                    {
                        Fail($"{_scenario}: premise broke in window 2 — expected HandSide==Right, got " +
                             $"{h.HandSide} at frame {_frame}.");
                        Finish();
                        return;
                    }
                    if (_ball.State != BallState.Dribbling)
                    {
                        Fail($"{_scenario}: premise broke in window 2 — expected BallState.Dribbling, got " +
                             $"{_ball.State} at frame {_frame}.");
                        Finish();
                        return;
                    }
                    _window2.Add(SampleNow());
                    _states2.Add(h.ActiveAnimNodeForHarness ?? "<null>");
                    if (_frame >= _stepDeadlineFrame) VerdictHandFollowsFlip();
                    return;
                }
        }
    }

    private void VerdictHandFollowsFlip()
    {
        if (_window1.Count == 0 || _window2.Count == 0)
        {
            Fail($"{_scenario}: one of the two observation windows is empty — nothing was sampled " +
                 $"(window1={_window1.Count}, window2={_window2.Count}).");
            Finish();
            return;
        }

        var holder = NodeForPeer(_holderId);
        Vector3 forward = BallController.HolderForwardForHarness(holder);
        Vector3 right = BallController.HandRightForHarness(forward);
        Vector3 origin = holder.GlobalPosition;
        float Lat(Vector3 p) => (p - origin).Dot(right);

        var winner1 = IdentifyDribblingHand(_window1);
        var winner2 = IdentifyDribblingHand(_window2);
        GD.Print($"[dribble-hand-alignment] {_scenario}: window1 dribbling hand={winner1.HandName} " +
                 $"(leftRange={winner1.LeftRangeMeters:F4} m, rightRange={winner1.RightRangeMeters:F4} m); " +
                 $"window2 dribbling hand={winner2.HandName} " +
                 $"(leftRange={winner2.LeftRangeMeters:F4} m, rightRange={winner2.RightRangeMeters:F4} m).");

        if (winner1.RangeMeters < MinDribblingHandRangeMeters || winner2.RangeMeters < MinDribblingHandRangeMeters)
        {
            Fail($"{_scenario}: PREMISE FAILED — no real dribbling hand identified in one of the windows. " +
                 $"window1Range={winner1.RangeMeters:F4} m, window2Range={winner2.RangeMeters:F4} m, " +
                 $"required >= {MinDribblingHandRangeMeters} m. Every downstream comparison would be " +
                 "meaningless, so this fails rather than passes.");
            Finish();
            return;
        }

        var lastSample1 = _window1[^1];
        var lastSample2 = _window2[^1];
        float ballLat1 = Lat(lastSample1.BallPos);
        float handLat1 = Lat(HandPos(lastSample1, winner1));
        float ballLat2 = Lat(lastSample2.BallPos);
        float handLat2 = Lat(HandPos(lastSample2, winner2));

        int ballSign1 = Math.Sign(ballLat1);
        int ballSign2 = Math.Sign(ballLat2);
        int handSign1 = Math.Sign(handLat1);
        int handSign2 = Math.Sign(handLat2);

        bool ballFlipped = ballSign1 != ballSign2 && ballSign1 != 0 && ballSign2 != 0;
        bool handFlipped = handSign1 != handSign2 && handSign1 != 0 && handSign2 != 0;

        GD.Print($"[dribble-hand-alignment] {_scenario}: BALL   lat1={ballLat1:F4} m (sign={ballSign1}) -> " +
                 $"lat2={ballLat2:F4} m (sign={ballSign2}), flipped={ballFlipped}");
        GD.Print($"[dribble-hand-alignment] {_scenario}: HAND   lat1={handLat1:F4} m (sign={handSign1}, " +
                 $"hand={winner1.HandName}) -> lat2={handLat2:F4} m (sign={handSign2}, hand={winner2.HandName}), " +
                 $"flipped={handFlipped}");

        // The state-machine half of the same claim the geometry makes below: the
        // tree must have been in DribbleLeft for window 1 and DribbleRight for
        // window 2, and in nothing else. Checked BEFORE the geometric verdict so
        // a build whose hands happen to line up while the tree never left one
        // state still fails, and fails with the reason named.
        bool statesOk = AssertOnlyState(_states1, "DribbleLeft", "window 1")
                        & AssertOnlyState(_states2, "DribbleRight", "window 2");

        bool pass = ballFlipped && handFlipped && statesOk;
        if (pass)
        {
            GD.Print($"[dribble-hand-alignment] PASS {_scenario} — BOTH the ball's lateral sign and the " +
                     "animated hand's lateral sign flipped when HandSide flipped Left->Right: the ball " +
                     "follows authoritative state AND the animation follows authoritative state.");
        }
        else
        {
            Fail($"{_scenario}: ballFlipped={ballFlipped} handFlipped={handFlipped} statesOk={statesOk} — expected ALL true. " +
                 $"ball: lat1={ballLat1:F4} (sign={ballSign1}) -> lat2={ballLat2:F4} (sign={ballSign2}); " +
                 $"hand: lat1={handLat1:F4} (sign={handSign1}, {winner1.HandName}) -> " +
                 $"lat2={handLat2:F4} (sign={handSign2}, {winner2.HandName}). " +
                 "If ballFlipped is false, the BALL layer is broken (unexpected — HandSign reads " +
                 "authoritative HandSide every tick). If handFlipped is false, the ANIMATION layer is " +
                 "broken — the Dribble BlendSpace1D has no hand-side split, so the SAME clip (and " +
                 "therefore the SAME physical hand) plays regardless of which hand is authoritative.");
        }

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: dribble-states-own-clips (structural) ────────────────────
    // Reads the AnimationNodeStateMachine straight off scenes/Player.tscn and
    // asserts the split's SHAPE, which no amount of runtime observation can
    // reach. Three claims:
    //
    //   1. Each polarity's BlendSpace1D points at ITS OWN clip pair. This is an
    //      ALLOWLIST, not a "not the other one" blocklist, because #294's split
    //      was generated — 72 transition sub-resources plus two blend surfaces —
    //      and the characteristic failure of generated wiring is a copy-paste
    //      that leaves DribbleLeft pointing at a real, valid, RIGHT-handed clip.
    //      That reads correct by state name alone and passes every blocklist.
    //
    //   2. The unsuffixed "Dribble" state is GONE. If it survived, Travel()'s
    //      pathfinder could route through it and the harness would never notice
    //      (Travel() silently routes around missing edges — proven by mutation
    //      in #279 — so edge-level assertions are not available to us here).
    //
    //   3. Every edge that touches one polarity has a twin touching the other.
    //      The split's whole rule was "each Dribble edge becomes two"; a
    //      polarity missing an edge is a stance the tree cannot leave or reach
    //      on that hand only, which in play looks like an intermittent freeze on
    //      one side of a crossover.
    private void TickStatesOwnClips()
    {
        // Load guard. Observed while building this scenario: with the import
        // cache cold, res://assets/Y Bot.fbx fails and Player.tscn logs a parse
        // error — yet GD.Load still hands back a PackedScene whose AnimationTree
        // sub-resources parsed fine, so this scenario read the right structure
        // and passed for the right reason. That is defensible for a structural
        // claim, but it is one step from a null that would NRE per tick until
        // the 12 s timeout and report a confusing failure. Name the condition.
        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        if (scene == null)
        {
            Fail("dribble-states-own-clips: res://scenes/Player.tscn did not load at all.");
            Finish();
            return;
        }
        var probe = scene.Instantiate<PlayerController>();
        var tree = probe.GetNode<AnimationTree>("AnimationTree");
        if (tree.TreeRoot is not AnimationNodeStateMachine sm)
        {
            Fail("dribble-states-own-clips: Player.tscn's AnimationTree root is not an AnimationNodeStateMachine.");
            probe.QueueFree();
            Finish();
            return;
        }

        bool pass = true;
        if (sm.HasNode("Dribble"))
        {
            Fail("dribble-states-own-clips: the unsuffixed \"Dribble\" state still exists. #294 replaced it " +
                 "with DribbleLeft/DribbleRight; leaving it behind gives Travel() somewhere hand-blind to land.");
            pass = false;
        }

        foreach ((string state, string idle, string move) in new[]
                 {
                     ("DribbleLeft",  "locomotion/dribbleidleleft",  "locomotion/dribblemoveleft"),
                     ("DribbleRight", "locomotion/dribbleidleright", "locomotion/dribblemoveright"),
                 })
        {
            if (!sm.HasNode(state))
            {
                Fail($"dribble-states-own-clips: state \"{state}\" is missing from Player.tscn.");
                pass = false;
                continue;
            }
            if (sm.GetNode(state) is not AnimationNodeBlendSpace1D bs)
            {
                Fail($"dribble-states-own-clips: state \"{state}\" is not an AnimationNodeBlendSpace1D — the " +
                     "stance blends idle<->moving on speed, so a single-clip state would freeze it at one endpoint.");
                pass = false;
                continue;
            }

            var actual = new List<string>();
            for (int i = 0; i < bs.GetBlendPointCount(); i++)
                actual.Add(bs.GetBlendPointNode(i) is AnimationNodeAnimation a ? a.Animation.ToString() : "<not-an-animation>");

            var expected = new List<string> { idle, move };
            if (!actual.SequenceEqual(expected))
            {
                Fail($"dribble-states-own-clips: \"{state}\" blend points are [{string.Join(", ", actual)}], " +
                     $"expected exactly [{string.Join(", ", expected)}] in that order (idle at pos 0, moving at " +
                     "pos 6). A valid-but-wrong-polarity clip here is the exact false read #294 closes.");
                pass = false;
            }
            else
            {
                GD.Print($"[dribble-hand-alignment] dribble-states-own-clips: \"{state}\" -> [{string.Join(", ", actual)}] OK.");
            }
        }

        // Claim 3: edge-for-edge symmetry between the two polarities.
        var left = new HashSet<string>();
        var right = new HashSet<string>();
        for (int i = 0; i < sm.GetTransitionCount(); i++)
        {
            string from = sm.GetTransitionFrom(i).ToString();
            string to = sm.GetTransitionTo(i).ToString();
            // The two cross-edges are the split's own invention and have no twin
            // by construction, so they are excluded from the symmetry set and
            // asserted separately below.
            if (from is "DribbleLeft" && to is "DribbleRight") continue;
            if (from is "DribbleRight" && to is "DribbleLeft") continue;
            if (from == "DribbleLeft" || to == "DribbleLeft") left.Add($"{Neutralise(from)}->{Neutralise(to)}");
            if (from == "DribbleRight" || to == "DribbleRight") right.Add($"{Neutralise(from)}->{Neutralise(to)}");
        }

        var onlyLeft = left.Except(right).OrderBy(s => s).ToList();
        var onlyRight = right.Except(left).OrderBy(s => s).ToList();
        if (onlyLeft.Count > 0 || onlyRight.Count > 0)
        {
            Fail($"dribble-states-own-clips: the two polarities are not edge-symmetric. Only on the LEFT: " +
                 $"[{string.Join(", ", onlyLeft)}]; only on the RIGHT: [{string.Join(", ", onlyRight)}]. " +
                 "Every pre-split Dribble edge was supposed to become exactly two, one per hand.");
            pass = false;
        }
        else
        {
            GD.Print($"[dribble-hand-alignment] dribble-states-own-clips: {left.Count} edges per polarity, " +
                     "identical modulo the suffix.");
        }

        bool l2r = false, r2l = false;
        for (int i = 0; i < sm.GetTransitionCount(); i++)
        {
            string from = sm.GetTransitionFrom(i).ToString(), to = sm.GetTransitionTo(i).ToString();
            if (from == "DribbleLeft" && to == "DribbleRight") l2r = true;
            if (from == "DribbleRight" && to == "DribbleLeft") r2l = true;
        }
        if (!l2r || !r2l)
        {
            Fail($"dribble-states-own-clips: the DribbleLeft<->DribbleRight pair is incomplete " +
                 $"(left->right={l2r}, right->left={r2l}). Without both, a crossover cannot move the stance " +
                 "to the other hand without detouring through Locomotion.");
            pass = false;
        }

        probe.QueueFree();
        if (pass)
            GD.Print("[dribble-hand-alignment] PASS dribble-states-own-clips — both stance states exist, own " +
                     "their own clip pair, are edge-symmetric, and are mutually reachable.");
        Finish(pass ? 0 : 1);
    }

    // Erases the polarity so the two edge sets are comparable as sets.
    private static string Neutralise(string state) =>
        state is "DribbleLeft" or "DribbleRight" ? "Dribble" : state;

    // (#294) Asserts the AnimationTree was in `expected` for the whole window and
    // never in anything else.
    //
    // "Only" rather than "at some point" is deliberate. The dribble stance is a
    // sustained neutral, so any second state observed while the ball is
    // continuously Dribbling and HandSide is held constant means something
    // Travel()ed away and back — most likely the OTHER polarity, which is the
    // exact defect. An "did we ever see it" assertion would pass on a tree that
    // flickered between DribbleLeft and DribbleRight every tick, and a flicker
    // is indistinguishable from correctness in a per-tick geometric sample
    // averaged over a window.
    private bool AssertOnlyState(HashSet<string> observed, string expected, string what)
    {
        if (observed.Count == 1 && observed.Contains(expected)) return true;
        Fail($"{_scenario}: over {what} the AnimationTree was expected to be in \"{expected}\" and nothing " +
             $"else; observed {{{string.Join(", ", observed)}}}. Read via ActiveAnimNodeForHarness " +
             "(GetCurrentNode), so this is what the state machine ACTUALLY entered, not what " +
             "MoveAnimResolver returned — Travel() to a missing state only logs (#257).");
        return false;
    }

    private PlayerController NodeForPeer(int peerId) => peerId == 1 ? _p1 : _p2;
    private PlayerController OtherNode(int peerId) => peerId == 1 ? _p2 : _p1;

    private static Skeleton3D FindSkeleton(Node root)
    {
        if (root is Skeleton3D s) return s;
        var matches = root.FindChildren("*", nameof(Skeleton3D), recursive: true, owned: false);
        return matches.Count > 0 ? matches[0] as Skeleton3D : null;
    }

    private void Fail(string message) => GD.PrintErr($"[dribble-hand-alignment] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[dribble-hand-alignment] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
