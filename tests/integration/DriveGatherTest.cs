using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #230 (ADR-0022, ADR-0016): the
// drive-gather committed move, end to end. Unit tests already pin the pure
// frame-data contract (DriveGatherTests.cs) and the steering/burst math
// (DriveGatherMathTests.cs). What they CANNOT reach is the live engine glue
// this harness proves:
//   1. Startup bleeds pre-existing LATERAL velocity via the SAME hybrid-gather
//      model the burst family already uses (#198's GatherDecel-style
//      tunable), NOT an instant stop — a REAL harness-asserted momentum
//      profile, not just the pure-math formula.
//   2. Startup resolves the drive line toward the rim via the bounded
//      turn-rate HeadingMath.RotateToward path (ADR-0010) — NOT an instant
//      snap. Proven against the SAME production function, not a re-derived
//      approximation.
//   3. The completed drive-gather (Startup->Active->Recovery->Inactive) plants
//      the player on a forward line that brings them into #229's Layup range
//      — and a REAL Layup, begun through the SAME BeginCommittedMove choke
//      point production input reaches, actually launches from there.
//   4. DriveGather respects the SAME #193 dead-dribble gate Crossover/
//      Hesitation/BehindTheBack/BetweenTheLegs/RetreatDribble already do
//      (code-review fix): it cannot legally begin from a dead Held
//      possession, since "the gather" IS the act of picking up a live
//      dribble (ADR-0014 tier 1) — there is nothing left to pick up twice.
//
//   godot --headless --path . res://tests/integration/DriveGatherTest.tscn -- --harness-scenario=gather-bleed
//   godot --headless --path . res://tests/integration/DriveGatherTest.tscn -- --harness-scenario=heading-turn
//   godot --headless --path . res://tests/integration/DriveGatherTest.tscn -- --harness-scenario=gather-to-layup-chain
//   godot --headless --path . res://tests/integration/DriveGatherTest.tscn -- --harness-scenario=dead-dribble-gate
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "gather-bleed".
//
// ── Why a single offline instance is the server ───────────────────────────
// Same reasoning as LayupTest/BlockTurnoverTest/BetweenTheLegsTest: with no
// MultiplayerPeer assigned, Godot uses OfflineMultiplayerPeer
// (is_server() hardcoded true, unique_id 1), so BallController.IsServer is
// true and player "1"'s _machine.Tick() advances every physics frame as the
// HOST's own player (TickServerOwnPlayer).
//
// ── Scenario "gather-bleed" ────────────────────────────────────────────────
// The holder starts with a PURELY LATERAL Velocity (5,0,0) — orthogonal to
// the drive line toward the rim — then begins a REAL DriveGather via
// BeginMoveForHarness. The holder is positioned already facing the rim
// (default Heading=0 faces +Z; RimCenter's default XZ is (0,0), holder at
// (0,0,-6)), which isolates this scenario's assertions from the heading-turn
// mechanic (the "heading-turn" scenario below covers that separately).
// Asserts the FIRST Startup tick's velocity: (a) is NOT the original 5 m/s
// (proves bleeding happened) (b) is NOT zero (proves it did not instant-stop)
// (c) matches Godot's own Vector3.MoveToward(original, Vector3.Zero,
// DriveGatherDecel*dt) within tolerance — a real profile match against the
// SAME hybrid-gather formula the burst family already uses, not merely "some
// decrease happened."
//
// ── Scenario "heading-turn" ─────────────────────────────────────────────────
// RimCenter is overridden to sit 90 degrees to the holder's right of their
// default facing (rather than moving the holder off default Heading=0, which
// has no public setter — RimCenter IS an [Export], so this is the minimal
// way to force a genuine turn without adding test-only surface to
// PlayerController). Asserts the FIRST Startup tick's Heading: (a) is NOT
// the full 90-degree target (proves it is not an instant snap) (b) exactly
// matches HeadingMath.RotateToward(0, wishDir, dt, MaxTurnRateDeg,
// BackTurnSlowFactor) — the SAME production function Move() itself calls —
// within tolerance, proving the resolution genuinely rides the bounded
// turn-rate path (ADR-0010), not a second hand-rolled scheme.
//
// ── Scenario "gather-to-layup-chain" ────────────────────────────────────────
// The holder starts outside LayupRange (default 4.0m) but close enough that
// the drive-gather's full Startup+Active+Recovery lifecycle closes the gap.
// Once the machine returns to Inactive, asserts distanceToRim < LayupRange,
// then begins a REAL Layup via BeginMoveForHarness and asserts it succeeds
// (Begin() returns true, the machine reports Startup/"layup") — proving the
// gather is a genuine launch point for #229's finish, not merely that the
// gather itself runs.
//
// ── Scenario "dead-dribble-gate" ─────────────────────────────────────────
// Mirrors BetweenTheLegsTest's/LayupTest's own "the SAME Held-holder gate
// Crossover/Hesitation/BehindTheBack use" scenario, applied to DriveGather.
// A fresh tipoff lands the holder in dead Held (#193) — no drive, no live
// dribble. Attempting BeginMoveForHarness(DriveGather) here must be refused
// by BeginCommittedMove's dead-dribble gate: asserts began==false AND the
// machine stayed Inactive (never even entered Startup).
public partial class DriveGatherTest : Node
{
    private const double TimeoutSeconds = 10.0;
    private const int ArmFrames = 5;

    private string _scenario = "gather-bleed";

    private BallController _ball;
    private PlayerController _holder;

    private int _frame;
    private double _elapsed;
    private bool _finished;

    // ── "gather-bleed" state ──────────────────────────────────────────────
    private static readonly Vector3 LateralVelocity = new(5f, 0f, 0f);
    private bool _gatherBegun;

    // ── "heading-turn" state ─────────────────────────────────────────────
    private static readonly Vector3 HeadingTurnHolderPosition = new(0f, 0f, -6f);
    private static readonly Vector3 HeadingTurnRimCenter = new(6f, 3.05f, -6f); // 90 deg to the holder's right
    private bool _headingTurnBegun;

    // ── "gather-to-layup-chain" state ────────────────────────────────────
    private static readonly Vector3 ChainStartPosition = new(0f, 0f, -5.3f); // 5.3m, outside default 4.0m LayupRange
    private enum ChainStep { AwaitTipoff, DriveBegun, AwaitInactive, LayupAttempted }
    private ChainStep _chainStep = ChainStep.AwaitTipoff;

    // ── "dead-dribble-gate" state ────────────────────────────────────────
    private enum GateStep { AwaitTipoff, Attempted }
    private GateStep _gateStep = GateStep.AwaitTipoff;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "gather-bleed");
        GD.Print($"[drive-gather] scenario={_scenario} booting headless…");

        var players = new Node3D { Name = "Players" };
        _holder = new PlayerController { Name = "1" };
        var other = new PlayerController { Name = "2" };
        players.AddChild(_holder);
        players.AddChild(other);

        _ball = new BallController { Name = "Ball", Players = players };
        if (_scenario == "heading-turn")
            _ball.RimCenter = HeadingTurnRimCenter;

        AddChild(players);
        AddChild(_ball);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        switch (_scenario)
        {
            case "heading-turn":            TickHeadingTurn(); break;
            case "gather-to-layup-chain":   TickChain(); break;
            case "dead-dribble-gate":       TickDeadDribbleGate(); break;
            default:                        TickGatherBleed(); break;
        }

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame}, scenario={_scenario}.");
            Finish();
        }
    }

    // ── Scenario: "gather-bleed" ─────────────────────────────────────────────
    private void TickGatherBleed()
    {
        if (_frame < ArmFrames) return;

        if (!_gatherBegun)
        {
            // DriveGather now respects the #193 dead-dribble gate
            // (code-review fix) — a fresh tipoff lands the holder in dead
            // Held, so a live dribble must be started first, exactly like
            // every other gated burst-family move's own harness setup.
            _ball.TryStartDribble(1);
            _holder.GlobalPosition = new Vector3(0f, 0f, -6f); // faces rim by default Heading=0 -> no confounding turn
            _holder.Velocity = LateralVelocity;

            bool began = _holder.BeginMoveForHarness(new DriveGather());
            _gatherBegun = true;
            if (!began)
            {
                Fail("BeginMoveForHarness(DriveGather) returned false — machine was not Inactive at begin.");
                Finish();
                return;
            }
            GD.Print($"[drive-gather] frame {_frame}: begun DriveGather with lateral velocity {LateralVelocity}.");
            return;
        }

        // The tick immediately after begin is the FIRST Startup tick's
        // TickCommittedMoveBehavior application.
        if (_holder.PhaseForHarness != MovePhase.Startup) return;

        Vector3 actual = _holder.Velocity;
        Vector3 expected = LateralVelocity.MoveToward(
            Vector3.Zero, _holder.DriveGatherDecel * (float)GetPhysicsProcessDeltaTime());

        bool notInstantStop = actual.Length() > 0.05f;
        bool didBleed = actual.Length() < LateralVelocity.Length() - 0.05f;
        bool matchesProfile = actual.DistanceTo(expected) < 0.05f;

        GD.Print($"[drive-gather] frame {_frame}: post-Startup-tick velocity={actual}, expected={expected}.");

        bool pass = notInstantStop && didBleed && matchesProfile;
        if (pass)
        {
            GD.Print($"[drive-gather] PASS gather-bleed — velocity bled from {LateralVelocity} toward " +
                     $"{expected} in one tick, matching the hybrid-gather model (not instant-stop).");
            GD.Print("[drive-gather] RESULT: PASS (exit 0)");
            Finish(0);
        }
        else
        {
            Fail($"expected a gradual gather-model bleed; notInstantStop={notInstantStop}, didBleed={didBleed}, " +
                 $"matchesProfile={matchesProfile} (actual={actual}, expected={expected}).");
            Finish();
        }
    }

    // ── Scenario: "heading-turn" ─────────────────────────────────────────────
    private void TickHeadingTurn()
    {
        if (_frame < ArmFrames) return;

        if (!_headingTurnBegun)
        {
            // Same dead-dribble gate as "gather-bleed" above — start a live
            // dribble before attempting Begin.
            _ball.TryStartDribble(1);
            _holder.GlobalPosition = HeadingTurnHolderPosition;
            bool began = _holder.BeginMoveForHarness(new DriveGather());
            _headingTurnBegun = true;
            if (!began)
            {
                Fail("BeginMoveForHarness(DriveGather) returned false — machine was not Inactive at begin.");
                Finish();
                return;
            }
            GD.Print($"[drive-gather] frame {_frame}: begun DriveGather at {HeadingTurnHolderPosition}, " +
                     $"RimCenter={HeadingTurnRimCenter} (90 deg to the right).");
            return;
        }

        if (_holder.PhaseForHarness != MovePhase.Startup) return;

        Vector2 fromXZ = new(HeadingTurnHolderPosition.X, HeadingTurnHolderPosition.Z);
        Vector2 targetXZ = new(HeadingTurnRimCenter.X, HeadingTurnRimCenter.Z);
        Vector2 wishDir = DriveGatherMath.WishDirToward(fromXZ, targetXZ);
        float targetYaw = Mathf.Atan2(wishDir.X, wishDir.Y); // matches HeadingMath's own convention

        float expected = HeadingMath.RotateToward(
            0f, wishDir, GetPhysicsProcessDeltaTime(), _holder.MaxTurnRateDeg, _holder.BackTurnSlowFactor);
        float actual = _holder.Heading;

        bool notInstantSnap = Mathf.Abs(Mathf.Wrap(actual - targetYaw, -Mathf.Pi, Mathf.Pi)) > 0.05f;
        bool turnedAtAll = Mathf.Abs(Mathf.Wrap(actual - 0f, -Mathf.Pi, Mathf.Pi)) > 0.001f;
        bool matchesRotateToward = Mathf.Abs(Mathf.Wrap(actual - expected, -Mathf.Pi, Mathf.Pi)) < 0.01f;

        GD.Print($"[drive-gather] frame {_frame}: post-Startup-tick Heading={actual:F4} rad, " +
                 $"expected={expected:F4} rad, target={targetYaw:F4} rad.");

        bool pass = notInstantSnap && turnedAtAll && matchesRotateToward;
        if (pass)
        {
            GD.Print("[drive-gather] PASS heading-turn — Heading advanced toward the rim by exactly one " +
                     "HeadingMath.RotateToward step (ADR-0010), not an instant snap.");
            GD.Print("[drive-gather] RESULT: PASS (exit 0)");
            Finish(0);
        }
        else
        {
            Fail($"expected a bounded turn-rate step, not an instant snap; notInstantSnap={notInstantSnap}, " +
                 $"turnedAtAll={turnedAtAll}, matchesRotateToward={matchesRotateToward} " +
                 $"(actual={actual:F4}, expected={expected:F4}, target={targetYaw:F4}).");
            Finish();
        }
    }

    // ── Scenario: "gather-to-layup-chain" ────────────────────────────────────
    private void TickChain()
    {
        switch (_chainStep)
        {
            case ChainStep.AwaitTipoff:
                if (_frame < ArmFrames) break;
                if (_ball.StateMachine.HolderPeerId != 1)
                {
                    Fail($"expected the tipoff to award peer 1 (this code-built tree's first child); got {_ball.StateMachine.HolderPeerId}.");
                    Finish();
                    return;
                }

                _ball.TryStartDribble(1);
                _holder.GlobalPosition = ChainStartPosition;

                float dxBefore = _holder.GlobalPosition.X - _ball.RimCenter.X;
                float dzBefore = _holder.GlobalPosition.Z - _ball.RimCenter.Z;
                float distBefore = Mathf.Sqrt(dxBefore * dxBefore + dzBefore * dzBefore);
                if (!(distBefore >= _ball.LayupRange))
                {
                    Fail($"expected the chain scenario's start position (dist={distBefore:F2}) to be OUTSIDE " +
                         $"LayupRange ({_ball.LayupRange}) — the premise this scenario proves would be vacuous otherwise.");
                    Finish();
                    return;
                }

                bool began = _holder.BeginMoveForHarness(new DriveGather());
                if (!began)
                {
                    Fail("BeginMoveForHarness(DriveGather) returned false — machine was not Inactive at begin.");
                    Finish();
                    return;
                }
                GD.Print($"[drive-gather] frame {_frame}: begun DriveGather at dist={distBefore:F2}m " +
                         $"(LayupRange={_ball.LayupRange}).");
                _chainStep = ChainStep.AwaitInactive;
                break;

            case ChainStep.AwaitInactive:
                if (_holder.PhaseForHarness != MovePhase.Inactive) break;

                float dx = _holder.GlobalPosition.X - _ball.RimCenter.X;
                float dz = _holder.GlobalPosition.Z - _ball.RimCenter.Z;
                float distAfter = Mathf.Sqrt(dx * dx + dz * dz);
                GD.Print($"[drive-gather] frame {_frame}: DriveGather completed (machine Inactive), " +
                         $"dist now {distAfter:F2}m.");

                if (!(distAfter < _ball.LayupRange))
                {
                    Fail($"expected the completed drive-gather to plant the holder INSIDE LayupRange " +
                         $"({_ball.LayupRange}); got distAfter={distAfter:F2}m.");
                    Finish();
                    return;
                }

                bool layupBegan = _holder.BeginMoveForHarness(new Layup());
                bool phaseCorrect = _holder.PhaseForHarness == MovePhase.Startup;
                bool idCorrect = _holder.CurrentMoveIdForHarness == "layup";

                bool pass = layupBegan && phaseCorrect && idCorrect;
                if (pass)
                {
                    GD.Print($"[drive-gather] PASS gather-to-layup-chain — Layup launched from the " +
                             $"drive-gather's plant (dist={distAfter:F2}m < LayupRange={_ball.LayupRange}).");
                    GD.Print("[drive-gather] RESULT: PASS (exit 0)");
                    Finish(0);
                }
                else
                {
                    Fail($"expected the Layup to launch from the drive-gather's plant; layupBegan={layupBegan}, " +
                         $"phaseCorrect={phaseCorrect} (phase={_holder.PhaseForHarness}), " +
                         $"idCorrect={idCorrect} (id={_holder.CurrentMoveIdForHarness}).");
                    Finish();
                }
                _chainStep = ChainStep.LayupAttempted;
                return;
        }
    }

    // ── Scenario: "dead-dribble-gate" ────────────────────────────────────────
    private void TickDeadDribbleGate()
    {
        switch (_gateStep)
        {
            case GateStep.AwaitTipoff:
                if (_frame < ArmFrames) break;
                if (_ball.StateMachine.HolderPeerId != 1)
                {
                    Fail($"expected the tipoff to award peer 1 (this code-built tree's first child); got {_ball.StateMachine.HolderPeerId}.");
                    Finish();
                    return;
                }

                // Fresh tipoff already lands the holder in dead Held (#193) —
                // no drive, no dribble. Attempting DriveGather here must be
                // refused by the SAME Held-holder gate Crossover/Hesitation/
                // BehindTheBack/BetweenTheLegs/RetreatDribble use
                // (PlayerController.BeginCommittedMove).
                if (_ball.State != BallState.Held)
                {
                    Fail($"expected a fresh tipoff to land the holder in Held; got state={_ball.State}.");
                    Finish();
                    return;
                }

                bool began = _holder.BeginMoveForHarness(new DriveGather());
                bool stayedInactive = _holder.PhaseForHarness == MovePhase.Inactive;

                if (began || !stayedInactive)
                {
                    Fail($"expected BeginMoveForHarness(DriveGather) to be REFUSED from a dead-Held possession; began={began}, phase={_holder.PhaseForHarness}.");
                    Finish();
                    return;
                }
                GD.Print("[drive-gather] PASS dead-dribble-gate — DriveGather refused from a dead-Held possession, machine stayed Inactive.");
                GD.Print("[drive-gather] RESULT: PASS (exit 0)");
                Finish(0);
                _gateStep = GateStep.Attempted;
                return;
        }
    }

    private void Fail(string message) => GD.PrintErr($"[drive-gather] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        if (code != 0)
            GD.Print($"[drive-gather] RESULT: FAIL (exit {code})");
        GetTree().Quit(code);
    }
}
