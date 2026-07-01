using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Player;
using Hooper.Moves;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #96 remediation: the steal TURNOVER,
// end to end (ADR-0016). Unit tests already pin the pure DefensiveResolution
// predicate; what they CANNOT reach is the server-side glue that samples the
// live dribble phase against the defender's committed-move machine and flips the
// ball Dribbling→Loose. This scene proves exactly that glue in a real Godot
// engine, and — the point of the remediation — proves it resolves across the
// WHOLE Active window, not just the single entry tick (the merged #96 bug).
//
//   godot --headless --path . res://tests/integration/StealTurnoverTest.tscn -- --harness-scenario=success
//   godot --headless --path . res://tests/integration/StealTurnoverTest.tscn -- --harness-scenario=whiff
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//
// ── Why a single offline instance is the server ───────────────────────────
// With no MultiplayerPeer assigned, Godot uses OfflineMultiplayerPeer, whose
// is_server() is hardcoded true and unique_id is 1. So BallController.IsServer
// is true (ResolveStealAttempts runs), the player node named "1" runs the
// server-own-player path, and "2" runs the server-remote path whose
// _machine.Tick() advances every physics frame — the free clock that walks the
// steal through Startup→Active with no per-tick poking.
//
// ── The scenarios (see the frame math on ComputeBeginFrame) ───────────────
// success: the defender's Active window is placed so its ENTRY tick samples the
//   dribble phase JUST BELOW the exposed band and a LATER Active tick lands
//   INSIDE it. Entry-tick-only resolution (the bug) whiffs; per-Active-tick
//   resolution (the fix) steals. This is the exact old-vs-new discriminator, so
//   it fails RED on the merged code and passes GREEN once fixed.
// whiff: the Active window sits entirely ABOVE the band — no Active tick is ever
//   in band, so the ball must stay Dribbling under both old and new code. Guards
//   against a fix that over-triggers (steals when it should not).
public partial class StealTurnoverTest : Node
{
    private const double TimeoutSeconds = 15.0;

    // Extra frames to run past the end of the Active window before rendering a
    // verdict, so a legitimately-late GoLoose (and the one-frame read skew — Root
    // observes the ball state the tick after BallController set it) is caught.
    private const int VerdictMarginFrames = 4;

    private string _scenario = "success";

    private BallController _ball;
    private PlayerController _defender;

    private int _frame;
    private int _beginFrame;
    private int _verdictFrame;
    private bool _stealBegun;
    private bool _everLoose;   // latched: true once the ball is ever observed Loose after begin
    private double _elapsed;
    private bool _finished;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "success");
        GD.Print($"[steal-turnover] scenario={_scenario} booting headless…");

        // ── Build the minimal authoritative scene entirely in code ──────────
        // A code-built tree (like NetStateSyncTest) avoids the fragile .tscn
        // ext_resource/uid wiring for a throwaway harness. Bare PlayerControllers
        // (no mesh/AnimationTree) are fine: every such lookup is null-guarded and
        // only PrintErrs. Tree pre-order is Root → Players → "1" → "2" → Ball,
        // matching scenes/Main.tscn's declaration order (Players is declared
        // BEFORE Ball there) — an earlier version of this harness ticked Ball
        // before Players, which is backwards from production and was flagged by
        // review as making the "mirrors production timing" claim false. Under
        // this order: Root ticks first (so BeginStealForHarness lands before
        // anyone moves that frame), then the defender's committed-move machine
        // advances, THEN BallController reads the now-current-frame phase and
        // resolves the steal — see ComputeBeginFrame's doc for how this shifts
        // the frame arithmetic by one tick versus the old (wrong) order.
        var players = new Node3D { Name = "Players" };

        // Child order under Players decides the tipoff: BallController.
        // TryAssignTipoffHolder awards possession to the first child whose name
        // parses to a nonzero peer id. "1" first → holder; "2" second → defender.
        var holder = new PlayerController { Name = "1" };
        _defender = new PlayerController { Name = "2" };
        players.AddChild(holder);
        players.AddChild(_defender);

        _ball = new BallController { Name = "Ball", Players = players };

        AddChild(players);    // matches scenes/Main.tscn: Players before Ball
        AddChild(_ball);

        _beginFrame = ComputeBeginFrame(_scenario);
        _verdictFrame = _beginFrame
            + StealMove.DefaultFrameData.StartupFrames
            + StealMove.DefaultFrameData.ActiveFrames
            + VerdictMarginFrames;

        GD.Print($"[steal-turnover] beginFrame={_beginFrame} verdictFrame={_verdictFrame} " +
                 $"band=[{_ball.StealLoExposed:F2},{_ball.StealHiExposed:F2}]");
    }

    // Where to call BeginStealForHarness so the Active window lands in the wanted
    // relationship to the exposed band. Derived from tunables (not hardcoded) so
    // it survives #104 retuning of the band, period, or frame counts.
    //
    // Phase is a pure function of BallController physics ticks: it advances by
    // dt/DribblePeriod each tick from frame 1, so after frame N,
    //   phase(N) = (N / cycleTicks) mod 1,  cycleTicks = DribblePeriod * Hz.
    // With Players ticking BEFORE Ball each frame (matching production), the
    // defender's committed-move machine has ALREADY advanced this frame by the
    // time BallController reads it — so ResolveStealAttempts first sees Active
    // one frame EARLIER than it would under a Ball-before-Players order: on
    // frame (beginFrame + StartupFrames - 1), not (beginFrame + StartupFrames).
    // Equivalently, beginFrame must be one tick LATER than the Ball-first
    // ordering would need for the same entry-tick phase reading — hence the
    // "+ 1" in both branches below (absent when this harness ticked Ball first).
    private int ComputeBeginFrame(string scenario)
    {
        float cycleTicks = _ball.DribblePeriod * Engine.PhysicsTicksPerSecond;
        int startup = StealMove.DefaultFrameData.StartupFrames;

        // Smallest tick index whose phase is inside the band (inclusive lo).
        int firstInBandFrame = Mathf.CeilToInt(_ball.StealLoExposed * cycleTicks);
        // Largest tick index still inside the band (inclusive hi).
        int lastInBandFrame = Mathf.FloorToInt(_ball.StealHiExposed * cycleTicks);

        if (scenario == "whiff")
            // Entry read one tick ABOVE the band; the whole Active window then
            // stays above it (the band does not reopen until a full cycle later,
            // long after this move has left Active).
            return (lastInBandFrame + 1) - startup + 1;

        // success: entry read one tick BELOW the band (entry-tick-only code
        // whiffs here), with the very next Active tick inside it (per-tick code
        // steals).
        return (firstInBandFrame - 1) - startup + 1;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        // Fire the steal exactly once, from Root, BEFORE BallController and the
        // defender tick this frame (tree pre-order guarantees Root runs first).
        if (!_stealBegun && _frame == _beginFrame)
        {
            // The holder's HandSide is the invariant default HandSide.Left for a
            // plain in-place dribble (it only ever changes on a possession edge
            // or a crossover, neither of which the idle holder does), so aiming
            // the steal at Left guarantees the hand axis passes and the test
            // isolates the timing axis.
            bool begun = _defender.BeginStealForHarness(HandSide.Left);
            _stealBegun = true;
            if (!begun)
            {
                Fail("BeginStealForHarness returned false — machine was not Inactive at begin.");
                Finish();
                return;
            }
            GD.Print($"[steal-turnover] frame {_frame}: steal begun (target Left)");
        }

        // Latch the turnover: catch the Loose state even if the loose-ball
        // scramble re-awards possession a frame or two later.
        if (_stealBegun && _ball.State == BallState.Loose)
            _everLoose = true;

        if (_frame >= _verdictFrame)
        {
            Verdict();
            return;
        }

        if (_elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame} before reaching verdict frame {_verdictFrame}.");
            Finish();
        }
    }

    private void Verdict()
    {
        // "success" requires the FULL turnover to complete, not merely that the
        // ball was ever observed Loose. This is the anti-hollow-green fix: an
        // earlier version of this harness only checked _everLoose, which the
        // GoLoose() call sets synchronously — so it latched true and PASSed even
        // on a build where the very next tick's TickLoose crashed the server with
        // a NullReferenceException (issue #96 remediation, unseeded _arc on a
        // steal-induced Dribbling→Loose transition). That crash leaves the ball
        // stuck in BallState.Loose forever (TickLoose never runs to completion,
        // so ResolveLooseBallRecovery/AwardPossession never fire) — which
        // _everLoose alone cannot distinguish from a healthy scramble that
        // resolves a frame later. Requiring the ball to land back in Dribbling
        // with a real holder by verdict time (VerdictMarginFrames gives the
        // scramble room to settle) makes that crash an explicit FAIL instead of
        // a silent PASS.
        bool turnoverCompleted = _ball.State == BallState.Dribbling && _ball.StateMachine.HolderPeerId != 0;
        bool pass = _scenario == "whiff" ? !_everLoose && turnoverCompleted : _everLoose && turnoverCompleted;

        if (pass)
        {
            GD.Print($"[steal-turnover] PASS — scenario={_scenario}, everLoose={_everLoose}, " +
                     $"finalState={_ball.State}, holder={_ball.StateMachine.HolderPeerId}.");
        }
        else
        {
            Fail($"scenario={_scenario} expected everLoose={(_scenario != "whiff")} and a completed " +
                 $"turnover, but got everLoose={_everLoose}, finalState={_ball.State}, " +
                 $"holder={_ball.StateMachine.HolderPeerId}.");
        }
        Finish(pass ? 0 : 1);
    }

    private void Fail(string message) => GD.PrintErr($"[steal-turnover] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[steal-turnover] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
