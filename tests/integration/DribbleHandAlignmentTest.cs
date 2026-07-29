using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness that captures a defect measured directly
// against this branch (2026-07-28 session): the ball's in-hand position is
// NOT bone-attached. BallController.TickDribbling (scripts/Ball/BallController.cs)
// places the ball at holderPos + forward*offset + HandRight(forward)*HandOffset*
// HandSign(holder) — HandSign reads the server-authoritative PlayerController.
// HandSide (ADR-0012). But scenes/Player.tscn's Dribble AnimationTree state is a
// single BlendSpace1D with NO hand-side split, so the SAME clip plays regardless
// of HandSide, and that clip was measured to animate the RIGHT hand (pump range
// L=0.0087 m vs R=0.3450 m at +0.5276 m lateral). HandSide defaults to Left
// (PlayerController.cs, HandSide property initializer), so on a fresh possession
// the ball is placed at NEGATIVE lateral (HandSign(Left) = -1) while the
// animated hand pumps at POSITIVE lateral — opposite sides of the body.
//
//   godot --headless --path . res://tests/integration/DribbleHandAlignmentTest.tscn -- --harness-scenario=hand-alignment-left
//   godot --headless --path . res://tests/integration/DribbleHandAlignmentTest.tscn -- --harness-scenario=hand-alignment-right
//   godot --headless --path . res://tests/integration/DribbleHandAlignmentTest.tscn -- --harness-scenario=hand-follows-authoritative-flip
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
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
    // than snapping instantly. hand-alignment-right forces HandSide AFTER the
    // tipoff's own automatic Held-tick reset already cached "Left" as the
    // last-observed value, so that force IS seen as a flip and DOES trigger a
    // sweep. Sampling must not start until the sweep settles, or the window's
    // first few ticks would show the ball transiently on the OLD (left) side
    // — a timing artifact, not the defect under test — and fail the per-tick
    // sign assertion for the wrong reason. 30 ticks is a large safety margin
    // over the ~7-tick sweep.
    private const int SweepSettleFrames = 30;
    private const float MinDribblingHandRangeMeters = 0.15f;
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
                if (forceRight) holder.SetHandSideForHarness(HandSide.Right);
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
                _ball.TryStartDribble(_holderId);
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

        bool pass = ballFlipped && handFlipped;
        if (pass)
        {
            GD.Print($"[dribble-hand-alignment] PASS {_scenario} — BOTH the ball's lateral sign and the " +
                     "animated hand's lateral sign flipped when HandSide flipped Left->Right: the ball " +
                     "follows authoritative state AND the animation follows authoritative state.");
        }
        else
        {
            Fail($"{_scenario}: ballFlipped={ballFlipped} handFlipped={handFlipped} — expected BOTH true. " +
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
