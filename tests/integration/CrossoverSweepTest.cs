using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #195 (ADR-0016): the crossover's
// authoritative cross-body ball transit (no teleport). Unit tests already pin
// CrossoverBallSweep's pure curve; what they CANNOT reach is the live glue
// this issue rewired — BallController.AdvanceHandSweep actually triggering
// off a REAL PlayerController.HandSide flip produced by a REAL Crossover
// committed move, and actually moving BallController.GlobalPosition (not a
// mesh-only offset) through a mid-body position before landing on the new
// side.
//
//   godot --headless --path . res://tests/integration/CrossoverSweepTest.tscn -- --harness-scenario=crossover-sweep
//   godot --headless --path . res://tests/integration/CrossoverSweepTest.tscn -- --harness-scenario=possession-change-no-sweep
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "crossover-sweep".
//
// ── Why a single offline instance is the server ───────────────────────────
// Same reasoning as TripleThreatTest/StealTurnoverTest: with no
// MultiplayerPeer assigned, Godot uses OfflineMultiplayerPeer (unique_id 1,
// is_server() true), so BallController.IsServer is true and player "1" is
// IsLocalPlayer — its _machine.Tick() advances every physics frame via the
// free clock TickServerOwnPlayer's SampleMoveInput drives (no hardware input
// needed for a Crossover begun through the harness seam).
//
// ── Scenario "crossover-sweep": rule 1 — no teleport ──────────────────────
// 1. Await tipoff, then TryStartDribble to reach Dribbling (a Crossover
//    cannot legally Begin from Held — #193's dead-Held gate — so the
//    harness must put the holder in the same state real gameplay requires).
// 2. Record the ball's lateral offset from the holder BEFORE the crossover
//    (should already be at the full HandOffset magnitude, HandSide.Left).
// 3. BeginCrossoverForHarness (the real BeginCommittedMove path, same seam
//    #193's harness already uses) and wait for HandSide to actually flip —
//    Crossover's Startup is 6 ticks, so the flip lands on Active-entry a few
//    ticks later.
// 4. From the flip tick onward, sample the ball's lateral offset every tick
//    for a generous window and record the SMALLEST absolute offset seen.
//    Rule 1's acceptance criterion is exactly this: if the ball ever
//    teleported (old HandSign() read applied in one tick), every sample
//    would sit at ±HandOffset and never dip toward 0. A real sweep must, for
//    at least one tick, occupy the mid-body region — asserted here as
//    "smallest observed |offset| is well under HandOffset".
// 5. Confirm the sweep actually finishes on the NEW side (opposite sign from
//    step 2), not stuck mid-transit — proves the sweep terminates, not just
//    starts.
//
// ── Scenario "possession-change-no-sweep": rule 2 ─────────────────────────
// Walks the holder out of bounds (the already-proven OOB turnover, ADR-0008)
// and asserts the fresh possession's hand reset does NOT activate a sweep —
// BallController.SweepActiveForHarness must read false on every tick after
// the change, distinguishing "reset straight to the default hand" from "a
// sweep that happens to already be at its endpoint" (indistinguishable from
// position alone once settled).
public partial class CrossoverSweepTest : Node
{
    private const double TimeoutSeconds = 10.0;
    private const int ArmFrames = 2;           // ticks for TryAssignTipoffHolder to run
    private const int ActionMarginFrames = 3;  // ticks to let an action's effect settle
    private const int SweepObservationWindow = 30; // generous vs. the ~7-tick default sweep
    private const float FloorClipEpsilon = 0.001f; // float-math slack on the BallRadius floor

    // Same OOB point OobTurnoverTest/TripleThreatTest use (beyond CourtMax.X,
    // well inside the far-backstop walls).
    private static readonly Vector3 OobPositiveX = new(9.0f, 0f, 5f);

    private string _scenario = "crossover-sweep";

    private BallController _ball;
    private PlayerController _p1;
    private PlayerController _p2;

    private int _frame;
    private double _elapsed;
    private bool _finished;

    // ── "crossover-sweep" scenario state ─────────────────────────────────────
    private enum SweepStep
    {
        AwaitTipoff, DriveChecked, BeforeCaptured, CrossoverIssued,
        AwaitFlip, ObservingSweep, Done
    }
    private SweepStep _sweepStep = SweepStep.AwaitTipoff;
    private int _holderId;
    private float _offsetBefore;
    private HandSide _handSideBefore;
    private int _observeDeadlineFrame;
    private float _smallestAbsOffset = float.PositiveInfinity;
    private bool _sawSweepActive;

    // ── "possession-change-no-sweep" scenario state ──────────────────────────
    private enum ResetStep { AwaitTipoff, OobIssued, Observing, Done }
    private ResetStep _resetStep = ResetStep.AwaitTipoff;
    private int _resetHolderId;
    private int _resetOtherId;
    private int _resetObserveDeadlineFrame;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "crossover-sweep");
        GD.Print($"[crossover-sweep] scenario={_scenario} booting headless…");

        // Same code-built-tree pattern as TripleThreatTest — avoids fragile
        // .tscn ext_resource/uid wiring for a throwaway harness.
        var players = new Node3D { Name = "Players" };
        _p1 = new PlayerController { Name = "1" };
        _p2 = new PlayerController { Name = "2" };
        players.AddChild(_p1);
        players.AddChild(_p2);

        _ball = new BallController { Name = "Ball", Players = players };

        AddChild(players);
        AddChild(_ball);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        if (_scenario == "possession-change-no-sweep")
            TickPossessionChangeNoSweep();
        else
            TickCrossoverSweep();

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame}, sweepStep={_sweepStep}, resetStep={_resetStep}.");
            Finish();
        }
    }

    // ── Scenario: "crossover-sweep" ──────────────────────────────────────────
    private void TickCrossoverSweep()
    {
        switch (_sweepStep)
        {
            case SweepStep.AwaitTipoff:
                if (_frame < ArmFrames) break;
                if (_ball.StateMachine.HolderPeerId == 0)
                {
                    Fail("tipoff never assigned a holder.");
                    Finish();
                    return;
                }
                _holderId = _ball.StateMachine.HolderPeerId;

                _ball.TryStartDribble(_holderId);
                _sweepStep = SweepStep.DriveChecked;
                _observeDeadlineFrame = _frame + ActionMarginFrames;
                break;

            case SweepStep.DriveChecked:
                if (_frame < _observeDeadlineFrame) break;
                if (_ball.State != BallState.Dribbling)
                {
                    Fail($"expected TryStartDribble to reach Dribbling (a Crossover cannot Begin from Held, #193); got state={_ball.State}.");
                    Finish();
                    return;
                }

                PlayerController holderForBefore = NodeForPeer(_holderId);
                _handSideBefore = holderForBefore.HandSide;
                _offsetBefore = LateralOffset(_holderId);
                GD.Print($"[crossover-sweep] before: hand={_handSideBefore}, lateralOffset={_offsetBefore:F4}");

                bool began = holderForBefore.BeginCrossoverForHarness(1f);
                if (!began)
                {
                    Fail("BeginCrossoverForHarness returned false — machine was not Inactive.");
                    Finish();
                    return;
                }
                _sweepStep = SweepStep.AwaitFlip;
                break;

            case SweepStep.AwaitFlip:
                if (!AssertAboveFloor()) return;
                PlayerController holderForFlip = NodeForPeer(_holderId);
                if (holderForFlip.HandSide == _handSideBefore) break; // still in Startup

                GD.Print($"[crossover-sweep] flip observed at frame {_frame}: {_handSideBefore} -> {holderForFlip.HandSide}");
                _smallestAbsOffset = System.MathF.Abs(LateralOffset(_holderId));
                _sawSweepActive = _ball.SweepActiveForHarness;
                _sweepStep = SweepStep.ObservingSweep;
                _observeDeadlineFrame = _frame + SweepObservationWindow;
                break;

            case SweepStep.ObservingSweep:
                if (!AssertAboveFloor()) return;
                float offset = LateralOffset(_holderId);
                _smallestAbsOffset = System.Math.Min(_smallestAbsOffset, System.MathF.Abs(offset));
                _sawSweepActive |= _ball.SweepActiveForHarness;

                if (_frame < _observeDeadlineFrame) break;

                // Rule 1 acceptance: the sweep must have run (SweepActiveForHarness
                // observed true at least once) and the ball's authoritative lateral
                // offset must have dipped well below the full HandOffset magnitude
                // at some point — proof it occupied the mid-body region instead of
                // snapping straight from -HandOffset to +HandOffset in one tick.
                float fullMagnitude = System.MathF.Abs(_offsetBefore);
                bool noTeleport = _sawSweepActive && _smallestAbsOffset < fullMagnitude * 0.5f;
                if (!noTeleport)
                {
                    Fail($"expected the sweep to pass through the mid-body region; sawSweepActive={_sawSweepActive}, smallestAbsOffset={_smallestAbsOffset:F4}, fullMagnitude={fullMagnitude:F4}.");
                    Finish();
                    return;
                }
                GD.Print($"[crossover-sweep] PASS no-teleport — smallestAbsOffset={_smallestAbsOffset:F4} < half of {fullMagnitude:F4}, sweep observed active.");

                PlayerController holderAfter = NodeForPeer(_holderId);
                float offsetAfter = LateralOffset(_holderId);
                bool switchedSides = holderAfter.HandSide != _handSideBefore
                    && System.Math.Sign(offsetAfter) != System.Math.Sign(_offsetBefore)
                    && System.MathF.Abs(offsetAfter) > fullMagnitude * 0.5f;
                if (!switchedSides)
                {
                    Fail($"expected the sweep to finish on the OPPOSITE side; before(hand={_handSideBefore}, offset={_offsetBefore:F4}), after(hand={holderAfter.HandSide}, offset={offsetAfter:F4}).");
                    Finish();
                    return;
                }
                GD.Print($"[crossover-sweep] PASS sweep-completes — settled on {holderAfter.HandSide} at offset={offsetAfter:F4}.");
                GD.Print("[crossover-sweep] RESULT: PASS (exit 0)");
                Finish(0);
                return;
        }
    }

    // ── Scenario: "possession-change-no-sweep" ───────────────────────────────
    private void TickPossessionChangeNoSweep()
    {
        switch (_resetStep)
        {
            case ResetStep.AwaitTipoff:
                if (_frame < ArmFrames) break;
                if (_ball.StateMachine.HolderPeerId == 0)
                {
                    Fail("tipoff never assigned a holder.");
                    Finish();
                    return;
                }
                _resetHolderId = _ball.StateMachine.HolderPeerId;
                _resetOtherId = _resetHolderId == 1 ? 2 : 1;

                // Turn the ball over via the OOB rule (ADR-0008, already proven
                // by OobTurnoverTest/TripleThreatTest) — a possession change,
                // rule 2's trigger.
                NodeForPeer(_resetHolderId).GlobalPosition = OobPositiveX;
                _resetStep = ResetStep.OobIssued;
                _resetObserveDeadlineFrame = _frame + ActionMarginFrames;
                break;

            case ResetStep.OobIssued:
                if (_frame < _resetObserveDeadlineFrame) break;
                if (_ball.StateMachine.HolderPeerId != _resetOtherId)
                {
                    Fail($"expected the OOB turnover to award peer {_resetOtherId}; got holder={_ball.StateMachine.HolderPeerId}.");
                    Finish();
                    return;
                }
                GD.Print($"[crossover-sweep] possession changed to {_resetOtherId} at frame {_frame}; observing for a sweep…");
                _resetStep = ResetStep.Observing;
                _resetObserveDeadlineFrame = _frame + SweepObservationWindow;
                break;

            case ResetStep.Observing:
                // Rule 2 acceptance: NO sweep may ever activate off a possession
                // change alone — assert every single tick, not just at the end,
                // so a one-tick sweep flicker can't hide inside the window.
                if (_ball.SweepActiveForHarness)
                {
                    Fail($"a sweep activated after a pure possession-change hand reset at frame {_frame} — rule 2 violated.");
                    Finish();
                    return;
                }
                if (_frame < _resetObserveDeadlineFrame) break;

                PlayerController newHolder = NodeForPeer(_resetOtherId);
                if (newHolder.HandSide != HandSide.Left)
                {
                    Fail($"expected the fresh possession's holder to reset to HandSide.Left; got {newHolder.HandSide}.");
                    Finish();
                    return;
                }
                GD.Print("[crossover-sweep] PASS possession-change-no-sweep — hand reset to Left with SweepActiveForHarness false throughout.");
                GD.Print("[crossover-sweep] RESULT: PASS (exit 0)");
                Finish(0);
                return;
        }
    }

    // World-space X offset of the ball from the given peer's holder position —
    // the axis the crossover sweep moves along at this harness's default
    // heading (0, facing +Z per HeadingMath.Forward), since HandRight(forward)
    // puts the lateral axis entirely on world X at that heading (right.Z=0).
    private float LateralOffset(int peerId)
    {
        PlayerController holder = NodeForPeer(peerId);
        return _ball.GlobalPosition.X - holder.GlobalPosition.X;
    }

    private PlayerController NodeForPeer(int peerId) => peerId == 1 ? _p1 : _p2;

    // Code-review fix (#195 PR #208): the dribble bounce's floor-contact phase
    // and the sweep's mid-transit dip are uncorrelated timers, so they CAN
    // coincide and drive the ball's center under the court. Assert the floor
    // invariant every tick of the sweep, not just at the end, so a one-tick
    // clip can't hide inside the observation window.
    private bool AssertAboveFloor()
    {
        if (_ball.GlobalPosition.Y >= _ball.BallRadius - FloorClipEpsilon) return true;

        Fail($"ball clipped under the floor mid-sweep at frame {_frame}: GlobalPosition.Y={_ball.GlobalPosition.Y:F4} < BallRadius={_ball.BallRadius:F4}.");
        Finish();
        return false;
    }

    private void Fail(string message) => GD.PrintErr($"[crossover-sweep] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        if (code != 0)
            GD.Print($"[crossover-sweep] RESULT: FAIL (exit {code})");
        GetTree().Quit(code);
    }
}
