using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Player;
using Hooper.Moves;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #100 (ADR-0016, ADR-0018 Amendment
// 2026-07-16): the whiff-punish blow-by lane, end to end. Unit tests already
// pin the pure BeatenWindow clock (BeatenWindowTests.cs) and the natural-
// whiff bit it's built from (CommittedMoveMachineTests' WasRecoveryEnteredEarly
// suite). What they CANNOT reach is the live engine glue: a REAL failed
// StealMove actually calling BallController.ResolveBeatenWindowTriggers, and
// a REAL shot resolution actually reading PlayerController.IsBeaten to
// suppress BOTH accuracy terms in ApplyShootLocally.
//
//   godot --headless --path . res://tests/integration/BlowByWindowTest.tscn -- --harness-scenario=whiff-triggers
//   godot --headless --path . res://tests/integration/BlowByWindowTest.tscn -- --harness-scenario=suppressed
//   godot --headless --path . res://tests/integration/BlowByWindowTest.tscn -- --harness-scenario=control
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//
// ── Why a single offline instance is the server ───────────────────────────
// Same reasoning as StealTurnoverTest/ContestScatterTest: with no
// MultiplayerPeer assigned, Godot uses OfflineMultiplayerPeer (is_server()
// hardcoded true), so BallController.IsServer is true and both player nodes'
// _machine.Tick() advances every physics frame regardless of role.
//
// ── Scenario "whiff-triggers": a REAL failed steal grants the beaten window ──
// Reuses StealTurnoverTest's "whiff" placement (the defender's Active window
// sits entirely above the steal-exposed band, so DefensiveResolution.
// StealSucceeds never fires and the move runs Active to its natural
// frame-count expiry, not an EndActiveEarly() success). The instant the
// defender's real machine naturally enters Recovery (WasRecoveryEnteredEarlyForHarness
// == false at that transition — the discriminator vs. a resolved steal),
// BallController.ResolveBeatenWindowTriggers must have already fired
// TriggerBeatenWindow this same physics tick. Asserted against the LIVE
// BlowByWindowTicks export (not a hardcoded literal), with the well-established
// "-1" correction this codebase's harnesses already use for parent-observes-
// child-one-tick-later tree-order skew (see StealTurnoverTest's
// ComputeBeginFrame doc) — Root's own _PhysicsProcess runs BEFORE Players/Ball
// each tick (tree pre-order), so the tick Root FIRST observes the defender's
// Recovery is one engine tick AFTER the tick BallController actually computed
// PhysicsTick and triggered the window.
//
// ── Scenarios "suppressed" / "control": the suppression itself ─────────────
// Both place a REAL shooter (JumpShot) and a REAL defender ContestMove exactly
// as ContestScatterTest's "contest-active" does (same frame-placement math),
// with the defender inside ContestRange so the PASSIVE proximity term would
// also be non-1 absent suppression. "suppressed" additionally calls the SAME
// public TriggerBeatenWindow API ResolveBeatenWindowTriggers itself calls —
// this is not a seam bypassing a choke point, it IS the production choke
// point, exercised directly so the suppression proof doesn't depend on
// re-deriving the exact whiff-timing choreography a second time (and, as the
// BlowByWindowTicks doc explains, a real whiff's own Recovery would otherwise
// consume the entire default window before a fresh ContestMove could ever be
// begun — see that export's doc for why the +14 margin exists at all).
// "control" never calls it — the identical setup must then show BOTH terms
// at their normal (non-1) values, proving "suppressed"'s 1.0/1.0 is a real
// effect of the beaten state, not a setup that happened to always read 1.0
// (the same anti-vacuous-pass discipline ContestScatterTest's own "no-contest"
// control follows).
public partial class BlowByWindowTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int ArmFrames = 2; // ticks for TryAssignTipoffHolder to run
    private const int VerdictMarginFrames = 4;

    // Mirrors ContestScatterTest's placements: close enough that the passive
    // proximity term is meaningfully non-1 absent suppression.
    private static readonly Vector3 ShooterPosition  = new(0f, 0f, 5f);
    private static readonly Vector3 DefenderPosition = new(1f, 0f, 5f);

    private string _scenario = "whiff-triggers";

    private BallController _ball;
    private PlayerController _holder;   // peer "1"
    private PlayerController _defender; // peer "2"

    private int _frame;
    private double _elapsed;
    private bool _finished;

    // ── "whiff-triggers" state ──────────────────────────────────────────────
    private int _beginFrame;
    private bool _stealBegun;
    private bool _recoveryObserved;
    private int _recoveryObservedEngineTick = -1;
    private bool _recoveryWasEarlyAtObservation;
    private int _beatenUntilAtObservation = int.MinValue;
    private bool _everWentLoose;

    // ── "suppressed" / "control" state ──────────────────────────────────────
    private bool _shooterBegun;
    private bool _defenderBegun;
    private int _shooterBeginFrame;
    private int _predictedReleaseFrame = -1;
    private int _defenderBeginFrame;
    private int _verdictFrame = -1;
    private bool _beatenTriggered;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "whiff-triggers");
        GD.Print($"[blowby-window] scenario={_scenario} booting headless…");

        // Code-built tree (avoids fragile .tscn ext_resource/uid wiring for a
        // throwaway harness). Sibling order matches scenes/Main.tscn: Players
        // before Ball.
        var players = new Node3D { Name = "Players" };
        _holder   = new PlayerController { Name = "1" };
        _defender = new PlayerController { Name = "2" };
        players.AddChild(_holder);
        players.AddChild(_defender);

        _ball = new BallController { Name = "Ball", Players = players };

        AddChild(players);
        AddChild(_ball);

        if (_scenario == "whiff-triggers")
        {
            // #193 tipoff starts Held, not Dribbling; steal mechanics need a
            // live dribble from frame 1 — same forcing StealTurnoverTest does.
            _ball.StateMachine.StartDribble();
            _beginFrame = ComputeWhiffBeginFrame();
            GD.Print($"[blowby-window] beginFrame={_beginFrame} " +
                     $"band=[{_ball.StealLoExposed:F2},{_ball.StealHiExposed:F2}] " +
                     $"blowByWindowTicks={_ball.BlowByWindowTicks}");
        }
        else
        {
            _defender.GlobalPosition = DefenderPosition;
        }
    }

    // Copy of StealTurnoverTest.ComputeBeginFrame's "whiff" branch — places the
    // defender's Active window entirely ABOVE the exposed band so no Active
    // tick is ever in-band and the move is guaranteed to whiff, not succeed.
    // See that method's doc for the full "+1" tree-order derivation this
    // shares (Players ticks before Ball, matching production).
    private int ComputeWhiffBeginFrame()
    {
        float cycleTicks = _ball.DribblePeriod * Engine.PhysicsTicksPerSecond;
        int startup = StealMove.DefaultFrameData.StartupFrames;
        int lastInBandFrame = Mathf.FloorToInt(_ball.StealHiExposed * cycleTicks);
        return (lastInBandFrame + 1) - startup + 1;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;
        int engineTick = (int)Engine.GetPhysicsFrames();

        if (_scenario == "whiff-triggers")
        {
            TickWhiffTriggers(engineTick);
            return;
        }

        TickSuppression(engineTick);
    }

    private void TickWhiffTriggers(int engineTick)
    {
        if (!_stealBegun && _frame == _beginFrame)
        {
            bool began = _defender.BeginMoveForHarness(new StealMove(HandSide.Left));
            _stealBegun = true;
            if (!began)
            {
                Fail("BeginMoveForHarness(StealMove) returned false — machine was not Inactive at begin.");
                Finish();
                return;
            }
            GD.Print($"[blowby-window] frame {_frame}: whiff steal begun (target Left)");
        }

        if (_stealBegun && _ball.State == BallState.Loose)
            _everWentLoose = true;

        if (_stealBegun && !_recoveryObserved && _defender.PhaseForHarness == MovePhase.Recovery)
        {
            _recoveryObserved = true;
            _recoveryObservedEngineTick = engineTick;
            _recoveryWasEarlyAtObservation = _defender.WasRecoveryEnteredEarlyForHarness;
            _beatenUntilAtObservation = _defender.BeatenUntilTickForHarness;

            // A couple more frames so any late-arriving write settles before
            // the verdict reads it (matches StealTurnoverTest's VerdictMarginFrames use).
            Verdict();
            return;
        }

        if (_elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame} waiting for the defender's whiffed StealMove to reach Recovery.");
            Finish();
        }
    }

    private void TickSuppression(int engineTick)
    {
        if (!_shooterBegun && _frame >= ArmFrames)
        {
            if (_ball.StateMachine.HolderPeerId != 1)
            {
                Fail($"expected the tipoff to award peer 1; got {_ball.StateMachine.HolderPeerId}.");
                Finish();
                return;
            }

            bool began = _holder.BeginJumpShotForHarness();
            _shooterBegun = true;
            if (!began)
            {
                Fail("BeginJumpShotForHarness returned false — shooter's machine was not Inactive at begin.");
                Finish();
                return;
            }

            _shooterBeginFrame = _frame;
            _holder.GlobalPosition = ShooterPosition;

            int jumpShotStartup = JumpShot.DefaultFrameData.StartupFrames;
            _predictedReleaseFrame = _shooterBeginFrame + (jumpShotStartup - 1);
            _verdictFrame = _predictedReleaseFrame + VerdictMarginFrames;

            _defenderBeginFrame = ComputeDefenderBeginFrame(_predictedReleaseFrame);

            if (_defenderBeginFrame <= _frame)
            {
                Fail($"computed defender begin frame {_defenderBeginFrame} is not reachable from the " +
                     $"current frame {_frame} — a frame-data change made this scenario's placement unschedulable.");
                Finish();
                return;
            }

            GD.Print($"[blowby-window] frame {_frame}: shooter begun JumpShot " +
                     $"(predicted release frame {_predictedReleaseFrame}); defender scheduled to begin at frame {_defenderBeginFrame}.");
        }

        // "suppressed" only: trigger the real beaten window directly through
        // the same public API ResolveBeatenWindowTriggers itself calls — see
        // the class doc for why calling it directly here is legitimate (it IS
        // the production choke point, not a bypass of one). Fires once,
        // comfortably before the release tick, well within the default
        // BlowByWindowTicks margin.
        if (_scenario == "suppressed" && _shooterBegun && !_beatenTriggered && _frame == ArmFrames + 1)
        {
            _defender.TriggerBeatenWindow(engineTick, _ball.BlowByWindowTicks);
            _beatenTriggered = true;
            GD.Print($"[blowby-window] frame {_frame}: defender directly beaten (tick {engineTick}, window {_ball.BlowByWindowTicks}).");
        }

        if (_shooterBegun && !_defenderBegun && _frame == _defenderBeginFrame)
        {
            bool began = _defender.BeginMoveForHarness(new ContestMove());
            _defenderBegun = true;
            if (!began)
            {
                Fail("BeginMoveForHarness(ContestMove) returned false — defender's machine was not Inactive at begin.");
                Finish();
                return;
            }
            GD.Print($"[blowby-window] frame {_frame}: defender begun ContestMove.");
        }

        if (_shooterBegun && _frame >= _verdictFrame)
        {
            Verdict();
            return;
        }

        if (_elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame} before reaching verdict frame {_verdictFrame}. " +
                 $"shooterBegun={_shooterBegun}, defenderBegun={_defenderBegun}.");
            Finish();
        }
    }

    // Same placement strategy as ContestScatterTest.ComputeDefenderBeginFrame:
    // the defender's ContestMove Active window's FIRST tick lands exactly on
    // the release tick.
    private int ComputeDefenderBeginFrame(int releaseFrame)
    {
        int contestStartup = ContestMove.DefaultFrameData.StartupFrames;
        return releaseFrame - (contestStartup - 1);
    }

    private void Verdict()
    {
        bool pass;

        if (_scenario == "whiff-triggers")
        {
            // The steal must have genuinely whiffed (never gone Loose, and
            // Recovery entered NATURALLY — not via EndActiveEarly, which
            // would mean the steal actually succeeded and this proves
            // nothing about the whiff path).
            bool genuineWhiff = !_everWentLoose && !_recoveryWasEarlyAtObservation;

            // The "-1" correction: Root observes the defender's Recovery
            // transition one engine tick AFTER BallController computed
            // PhysicsTick and called TriggerBeatenWindow this same tick (tree
            // pre-order — Root ticks before Players/Ball every frame). See
            // the class doc's "whiff-triggers" section.
            int expectedUntilTick = _recoveryObservedEngineTick - 1 + _ball.BlowByWindowTicks;
            bool windowSetCorrectly = _beatenUntilAtObservation == expectedUntilTick;

            pass = _recoveryObserved && genuineWhiff && windowSetCorrectly;

            if (pass)
            {
                GD.Print($"[blowby-window] PASS — scenario=whiff-triggers, " +
                         $"recoveryObservedEngineTick={_recoveryObservedEngineTick}, " +
                         $"beatenUntilTick={_beatenUntilAtObservation} (expected {expectedUntilTick}).");
            }
            else
            {
                Fail($"scenario=whiff-triggers expected a genuine natural whiff (everWentLoose=False, " +
                     $"recoveryWasEarly=False) with BeatenUntilTickForHarness == {expectedUntilTick}, but got " +
                     $"everWentLoose={_everWentLoose}, recoveryWasEarly={_recoveryWasEarlyAtObservation}, " +
                     $"beatenUntilTick={_beatenUntilAtObservation}, recoveryObserved={_recoveryObserved}.");
            }
        }
        else
        {
            float contestFactor     = _ball.LastContestFactorForHarness;
            float contestMoveFactor = _ball.LastContestMoveFactorForHarness;
            float k                = _ball.ContestMoveScatterK; // live export

            if (_scenario == "suppressed")
            {
                bool contestFactorSuppressed     = Mathf.IsEqualApprox(contestFactor, 1f, 1e-4f);
                bool contestMoveFactorSuppressed = Mathf.IsEqualApprox(contestMoveFactor, 1f, 1e-4f);
                pass = contestFactorSuppressed && contestMoveFactorSuppressed;

                if (pass)
                {
                    GD.Print($"[blowby-window] PASS — scenario=suppressed, " +
                             $"contestFactor={contestFactor} (expected 1.0), " +
                             $"contestMoveFactor={contestMoveFactor} (expected 1.0).");
                }
                else
                {
                    Fail($"scenario=suppressed expected BOTH contestFactor==1.0 and contestMoveFactor==1.0 " +
                         $"(beaten window suppresses both terms per issue #100), but got " +
                         $"contestFactor={contestFactor}, contestMoveFactor={contestMoveFactor}.");
                }
            }
            else // "control"
            {
                bool contestFactorNormal     = contestFactor > 1.0001f;
                bool contestMoveFactorNormal = Mathf.IsEqualApprox(contestMoveFactor, 1f + k, 1e-4f);
                pass = contestFactorNormal && contestMoveFactorNormal;

                if (pass)
                {
                    GD.Print($"[blowby-window] PASS — scenario=control, " +
                             $"contestFactor={contestFactor} (expected > 1.0), " +
                             $"contestMoveFactor={contestMoveFactor} (expected {1f + k}).");
                }
                else
                {
                    Fail($"scenario=control (no beaten window) expected contestFactor > 1.0 and " +
                         $"contestMoveFactor == 1 + ContestMoveScatterK ({1f + k}) — proving 'suppressed''s " +
                         $"1.0/1.0 is a real effect, not a setup that always reads 1.0 — but got " +
                         $"contestFactor={contestFactor}, contestMoveFactor={contestMoveFactor}.");
                }
            }
        }

        Finish(pass ? 0 : 1);
    }

    private void Fail(string message) => GD.PrintErr($"[blowby-window] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[blowby-window] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
