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
// recovery-reset: issue #176 regression. Runs the SAME steal as "success" through
//   to a completed scramble recovery, then proves the exact defect the issue
//   described no longer holds: the instant the ball lands back in Dribbling,
//   DribbleCycle.Phase must have been reset to 0 (not frozen at the in-band value
//   it held when the ball went Loose) AND the defender's own committed-move
//   Recovery (still running from the first steal) structurally refuses an
//   immediate second Begin(). Together these close the exploit at its root: even
//   once the defender's Recovery elapses and a genuine re-attempt becomes legal,
//   it starts reading a phase that begins at 0, not one already sitting inside
//   the steal-exposed band with no timing skill spent to get there.

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
    private int _toucherAtSteal = -1; // latched: last-toucher the FIRST tick the ball is Loose
    private double _elapsed;
    private bool _finished;

    // ── "recovery-reset" scenario state (issue #176) ────────────────────────
    private bool _recoveryChecked;
    private float _recoveryPhaseAtCheck = -1f;
    private bool _reattemptBeganAtRecovery;

    // ── #175 reconciliation proof ────────────────────────────────────────────
    // This single offline instance has no separate client process to receive a
    // ReceiveState broadcast and reconcile against (see the class doc's "single
    // offline instance is the server"), so this harness cannot directly boot a
    // second peer. Instead it builds a SHADOW CommittedMoveMachine that begins
    // the identical StealMove at the identical frame the real defender does —
    // exactly mirroring what a real client's local prediction would prevail as
    // — and, at the moment the REAL server-side machine resolves the steal
    // early (BallController.ResolveStealAttempts → PlayerController.
    // EndResolvedSteal, the exact code path #175 is about), feeds the shadow
    // the REAL broadcast values (via the ForHarness accessors) through the
    // actual CommittedMoveMachine.ShouldForceRecovery + ForceState production
    // logic. This proves the full server→signal→client-reconciliation chain
    // using real engine state end to end, without needing a second Godot
    // process — the one piece of the wire itself (RPC marshaling of the new
    // bool parameter) is covered separately by the build succeeding with the
    // [Rpc]-attributed signature change.
    private CommittedMoveMachine _shadowClient = new();
    private bool _shadowReconciled;
    private int _shadowRecoveryFrame = -1; // first frame shadow.Phase == Recovery
    private int _shadowInactiveFrame = -1; // first frame shadow.Phase == Inactive

    // (#175, doubt cycle post-CI-failure) The REAL server-side machine's own
    // early-Recovery entry frame, tracked independently of the shadow so the
    // "skew budget" claimed in Verdict()'s comments is an ACTUAL assertion,
    // not just print-string decoration. Reading _defender.PhaseForHarness
    // every tick (not only inside the reconciliation branch) means this is
    // captured even on a tick where the shadow's own reconciliation happens
    // to be delayed relative to the server.
    private int _realRecoveryFrame = -1; // first frame _defender.PhaseForHarness == Recovery

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
        AddChild(_ball);       // _ball._Ready() has now run, constructing StateMachine (Held)

        // #193 changed the tipoff to start Held-not-Dribbling (the triple-
        // threat rule). This harness tests STEAL mechanics, which are keyed on
        // an in-progress DRIBBLE (ResolveStealAttempts is a no-op unless the
        // ball is Dribbling) — and ComputeBeginFrame's phase math assumes
        // DribbleCycle.Phase has been advancing every tick since frame 1. Force
        // Dribbling here, once, before the first physics tick: TryAssignTipoffHolder's
        // ForceState(State, peerId) preserves whatever State already is, so this
        // survives the tipoff unchanged, reproducing the ball's pre-#193
        // "always dribbling" baseline this test's frame arithmetic was built on.
        // Unrelated to the dead-dribble rule itself, which this harness does not
        // exercise.
        _ball.StateMachine.StartDribble();

        _beginFrame = ComputeBeginFrame(_scenario);
        _verdictFrame = _beginFrame
            + StealMove.DefaultFrameData.StartupFrames
            + StealMove.DefaultFrameData.ActiveFrames
            + VerdictMarginFrames;

        // (#175, doubt cycle post-CI-failure) "success" ALSO needs the shadow
        // client to reach Inactive before Verdict() can assert reconciliationOk
        // (specifically shadowFinishedEarly) — but the shadow only starts its
        // Recovery countdown once the REAL server resolves the steal (as early
        // as one tick into Active) and then still owes the FULL RecoveryFrames
        // from there (EndActiveEarly shortens Active, never Recovery — see
        // CommittedMoveMachine.EndActiveEarly's doc). The base _verdictFrame
        // above was sized ONLY for the pre-#175 assertions (turnover completed,
        // correct toucher), which resolve within a few ticks of GoLoose() and
        // never needed to wait out a full Recovery. Extend it to the shadow's
        // worst-case finish: recovery entered no later than the natural
        // Active->Recovery boundary (StartupFrames + ActiveFrames), then a full
        // RecoveryFrames, then the existing margin. This was the actual root
        // cause of the CI failure this comment responds to: the harness was
        // rendering shadowInactiveFrame's verdict 9 frames before the shadow
        // could possibly reach Inactive (verdict fired at frame 25; the shadow
        // in that run could not reach Inactive before frame 34).
        if (_scenario == "success")
            _verdictFrame += StealMove.DefaultFrameData.RecoveryFrames;

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

            // (#175) Shadow client: begins the IDENTICAL StealMove the same
            // frame, mirroring what a real client's local prediction would be
            // running right now — see the field doc above for the full rationale.
            if (_scenario == "success")
                _shadowClient.Begin(new StealMove(HandSide.Left));
        }

        // Latch the turnover: catch the Loose state even if the loose-ball
        // scramble re-awards possession a frame or two later.
        if (_stealBegun && _ball.State == BallState.Loose)
        {
            // Capture last-toucher on the FIRST Loose tick only: the scramble's
            // eventual AwardPossession (whichever player recovers it) legitimately
            // overwrites this field afterward, which would mask the bug this
            // assertion targets — a knocked ball that goes OOB BEFORE recovery
            // must be attributed to the defender, not whoever the scramble later
            // hands the ball back to.
            if (!_everLoose)
                _toucherAtSteal = _ball.LastToucherPeerIdForHarness;
            _everLoose = true;
        }

        // (#175, doubt cycle post-CI-failure) Latch the REAL server-side
        // machine's own early-Recovery entry frame — independent of whether
        // the shadow has reconciled yet, so the skew check below compares
        // against ground truth, not against the shadow's own (possibly
        // delayed) read of it.
        if (_scenario == "success" && _stealBegun && _realRecoveryFrame < 0
            && _defender.PhaseForHarness == MovePhase.Recovery)
        {
            _realRecoveryFrame = _frame;
        }

        // (#175) Drive the shadow client one tick, mirroring TickClientOwnPlayer's
        // real order: reconcile against the latest "broadcast" (here, the REAL
        // defender's live values, read directly since there's no wire) BEFORE
        // advancing the shadow's own Tick(). Only runs after Begin() and only
        // for "success" — "whiff" never resolves early, so WasRecoveryEnteredEarly
        // never goes true and this block is inert (ShouldForceRecovery gates on it).
        // Scenario-exclusive with "recovery-reset" below (only one of the two
        // conditions can ever be true in a given run), so ordering between them
        // does not matter.
        if (_scenario == "success" && _stealBegun && _shadowClient.IsActive)
        {
            if (!_shadowReconciled && CommittedMoveMachine.ShouldForceRecovery(
                    _shadowClient.Phase, _defender.PhaseForHarness, _defender.WasRecoveryEnteredEarlyForHarness,
                    _shadowClient.CurrentMove?.Id, _defender.CurrentMoveIdForHarness))
            {
                _shadowClient.ForceState(MovePhase.Recovery, frameInPhase: 0, _shadowClient.CurrentMove, recoveryWasEarly: true);
                _shadowReconciled  = true;
                _shadowRecoveryFrame = _frame;
                GD.Print($"[steal-turnover] frame {_frame}: shadow client reconciled Active→Recovery (#175 fix engaged)");
            }

            _shadowClient.Tick();

            if (_shadowClient.Phase == MovePhase.Recovery && _shadowRecoveryFrame < 0)
                _shadowRecoveryFrame = _frame;
            if (_shadowClient.Phase == MovePhase.Inactive && _shadowInactiveFrame < 0)
                _shadowInactiveFrame = _frame;
        }

        // "recovery-reset" does not run to a fixed verdict frame — the ball's
        // scramble-recovery timing depends on the steal's knockback physics,
        // not a closed-form tick count, so this waits for the actual recovery
        // event (ADR-0016: assert real engine state, not a guessed frame) and
        // renders its verdict the instant that event is observed.
        if (_scenario == "recovery-reset")
        {
            // #193: AwardPossession no longer auto-chains into Dribbling — the
            // scramble recovery now lands the ball in a fresh, live Held
            // possession, so "the ball came back" is BallState.Held here, not
            // Dribbling (this harness never drives the auto-dribble input, so
            // the ball has no reason to leave Held after the recovery).
            if (_everLoose && !_recoveryChecked
                && _ball.State == BallState.Held
                && _ball.StateMachine.HolderPeerId != 0)
            {
                _recoveryChecked = true;
                // The moment the ball lands back in Held is the exact tick
                // AwardPossession runs — if DribbleCycle.Reset() were missing
                // (the #176 bug), Phase would still read whatever in-band value
                // it froze at when the ball went Loose, not 0.
                _recoveryPhaseAtCheck = _ball.DribblePhaseForHarness;

                // The defender's OWN committed-move machine is still paying
                // Recovery from the first steal (StealMove.DefaultFrameData:
                // 20 recovery ticks, far longer than the few ticks the ball
                // takes to settle back into pickup range) — so the earliest
                // possible re-attempt is structurally refused here, before the
                // phase-reset fix even has to matter for THIS particular
                // instant. The phase-reset assertion above is what protects
                // the moment Recovery DOES elapse and a genuine re-attempt
                // becomes legal.
                _reattemptBeganAtRecovery = _defender.BeginStealForHarness(HandSide.Left);

                Verdict();
                return;
            }

            if (_elapsed > TimeoutSeconds)
            {
                Fail($"timed out at frame {_frame} waiting for the scramble to recover the ball.");
                Finish();
            }
            return;
        }

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
        if (_scenario == "recovery-reset")
        {
            VerdictRecoveryReset();
            return;
        }

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
        // resolves a frame later. Requiring the ball to land back in a live
        // Held possession with a real holder by verdict time (VerdictMarginFrames
        // gives the scramble room to settle) makes that crash an explicit FAIL
        // instead of a silent PASS.
        //
        // Expected state is scenario-dependent since #193 (AwardPossession no
        // longer auto-chains into a dribble):
        //   - "success": the scramble recovery routes through AwardPossession,
        //     which now settles a fresh, live Held possession, not Dribbling.
        //   - "whiff": no possession change ever happens (the steal never
        //     connects), so the ball never leaves the Dribbling state THIS
        //     HARNESS forced it into at setup (see the StartDribble() call in
        //     _Ready — #193 also changed the tipoff to start Held, and this
        //     harness's frame arithmetic needs a live dribble from frame 1).
        bool ballStateSane = _scenario == "whiff"
            ? _ball.State == BallState.Dribbling
            : _ball.State == BallState.Held;
        bool turnoverCompleted = ballStateSane && _ball.StateMachine.HolderPeerId != 0;

        // On a real steal, the defender (peer "2") must become the last
        // toucher (#118 rule) THE INSTANT the ball goes Loose — otherwise a
        // knocked ball that sails OOB before the scramble recovers it would
        // charge the turnover back to the offense instead of the defender who
        // just touched it. Checked against _toucherAtSteal (latched on the
        // first Loose tick), not the current value: the scramble's own later
        // AwardPossession legitimately overwrites _lastToucherPeerId once
        // someone recovers the ball, which would otherwise mask this bug.
        // Only checked for "success": "whiff" never touches the ball, so
        // _toucherAtSteal is never latched (stays -1, the sentinel).
        bool toucherCorrect = _scenario == "whiff" ? _toucherAtSteal == -1 : _toucherAtSteal == 2;

        // (#175) The reconciliation fix, proven only on "success" (the only
        // scenario where EndActiveEarly ever fires):
        //   1. The shadow client must actually have been reconciled — a
        //      regression here means ShouldForceRecovery/the broadcast wiring
        //      silently stopped firing and the bug is back.
        //   2. Reconciliation must land the shadow's local Recovery entry
        //      within a couple of frames of the REAL server's early Recovery
        //      entry (bounded skew is expected/accepted — see
        //      ShouldForceRecovery's doc — but it must not be the FULL
        //      unshortened Active window the bug left it stuck predicting).
        //   3. The shadow must reach Inactive well before the "buggy" baseline
        //      timeline (begin + full Startup + full un-shortened ActiveFrames
        //      + full RecoveryFrames) it would have followed with no fix.
        bool reconciliationOk = true;
        string reconciliationDetail = "n/a (whiff)";
        if (_scenario == "success")
        {
            int buggyBaselineInactiveFrame = _beginFrame
                + StealMove.DefaultFrameData.StartupFrames
                + StealMove.DefaultFrameData.ActiveFrames
                + StealMove.DefaultFrameData.RecoveryFrames;
            const int MaxAcceptableRecoverySkewFrames = 3;

            bool shadowWasReconciled  = _shadowReconciled && _shadowRecoveryFrame >= 0;
            // (#175, doubt cycle post-CI-failure) This used to compare only
            // against ActiveFrames - 1 — a coarse "not the full unshortened
            // window" bound that never actually consumed
            // MaxAcceptableRecoverySkewFrames despite the constant and this
            // comment block claiming a "couple of frames" bound. Real finding:
            // the skew budget was decorative. Now compares the shadow's
            // reconciled Recovery frame against the REAL server's own
            // early-Recovery frame (_realRecoveryFrame, latched independently
            // above), which is the actual claim this test makes.
            bool shadowRecoveredOnTime = shadowWasReconciled && _realRecoveryFrame >= 0
                && System.Math.Abs(_shadowRecoveryFrame - _realRecoveryFrame) <= MaxAcceptableRecoverySkewFrames;
            bool shadowFinishedEarly  = _shadowInactiveFrame >= 0
                && _shadowInactiveFrame < buggyBaselineInactiveFrame;

            reconciliationOk = shadowWasReconciled && shadowRecoveredOnTime && shadowFinishedEarly;
            reconciliationDetail = $"reconciled={_shadowReconciled}, realRecoveryFrame={_realRecoveryFrame}, " +
                $"shadowRecoveryFrame={_shadowRecoveryFrame}, shadowInactiveFrame={_shadowInactiveFrame}, " +
                $"buggyBaselineInactiveFrame={buggyBaselineInactiveFrame}, skewBudget={MaxAcceptableRecoverySkewFrames}";
        }

        bool pass = (_scenario == "whiff" ? !_everLoose : _everLoose) && turnoverCompleted && toucherCorrect
            && reconciliationOk;

        if (pass)
        {
            GD.Print($"[steal-turnover] PASS — scenario={_scenario}, everLoose={_everLoose}, " +
                     $"finalState={_ball.State}, holder={_ball.StateMachine.HolderPeerId}, " +
                     $"toucherAtSteal={_toucherAtSteal}, {reconciliationDetail}.");
        }
        else
        {
            Fail($"scenario={_scenario} expected everLoose={(_scenario != "whiff")}, a completed " +
                 $"turnover, toucherAtSteal={(_scenario == "whiff" ? -1 : 2)}, and reconciliationOk=True, but got " +
                 $"everLoose={_everLoose}, finalState={_ball.State}, holder={_ball.StateMachine.HolderPeerId}, " +
                 $"toucherAtSteal={_toucherAtSteal}, reconciliationOk={reconciliationOk} ({reconciliationDetail}).");
        }
        Finish(pass ? 0 : 1);
    }

    // Issue #176 regression verdict: the ball must have gone Loose (a real
    // steal happened), the phase must have been reset to (approximately) 0 the
    // instant the scramble recovery re-caught it, that 0 must sit outside the
    // steal-exposed band, and the defender's structurally-still-Recovering
    // machine must refuse an immediate second Begin(). PhaseEpsilon is wider
    // than the unit tests' Epsilon because a few physics ticks may elapse
    // between AwardPossession and this check observing it (TickDribbling
    // advances Phase every tick the ball is Dribbling).
    private const float PhaseEpsilon = 0.05f;

    private void VerdictRecoveryReset()
    {
        bool phaseWasReset = _recoveryChecked && _recoveryPhaseAtCheck >= 0f
            && _recoveryPhaseAtCheck < PhaseEpsilon;
        bool phaseOutsideBand = _recoveryChecked
            && (_recoveryPhaseAtCheck < _ball.StealLoExposed || _recoveryPhaseAtCheck > _ball.StealHiExposed);
        bool reattemptRefused = !_reattemptBeganAtRecovery;

        bool pass = _everLoose && _recoveryChecked && phaseWasReset && phaseOutsideBand && reattemptRefused;

        if (pass)
        {
            GD.Print($"[steal-turnover] PASS — scenario={_scenario}, " +
                     $"phaseAtRecovery={_recoveryPhaseAtCheck:F3}, reattemptBegan={_reattemptBeganAtRecovery}.");
        }
        else
        {
            Fail($"scenario={_scenario} expected everLoose=true, a recovery observed with phase reset " +
                 $"near 0 (outside [{_ball.StealLoExposed:F2},{_ball.StealHiExposed:F2}]), and an immediate " +
                 $"reattempt refused, but got everLoose={_everLoose}, recoveryChecked={_recoveryChecked}, " +
                 $"phaseAtRecovery={_recoveryPhaseAtCheck:F3}, reattemptBegan={_reattemptBeganAtRecovery}.");
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
