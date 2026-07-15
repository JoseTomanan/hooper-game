using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Player;
using Hooper.Moves;
using Hooper.Systems;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #98 (ADR-0016): the block-turnover
// mechanic, end to end. Unit tests already pin the pure DefensiveResolution
// predicate and BlockMove's frame-data contract; what they CANNOT reach is the
// live engine glue: BallController.ResolveBlockAttempts reading a REAL
// defender's committed-move Phase/FrameInPhase against a REAL shooter's
// InFlight release tick, the pre-switch-plus-release-tick-top-up call ordering
// that makes "a blocked shot cannot score" true, and the resulting swat
// velocity/last-toucher/Recovery side effects.
//
//   godot --headless --path . res://tests/integration/BlockTurnoverTest.tscn -- --harness-scenario=success
//   godot --headless --path . res://tests/integration/BlockTurnoverTest.tscn -- --harness-scenario=success-lastactive
//   godot --headless --path . res://tests/integration/BlockTurnoverTest.tscn -- --harness-scenario=whiff
//   godot --headless --path . res://tests/integration/BlockTurnoverTest.tscn -- --harness-scenario=control-make
//   godot --headless --path . res://tests/integration/BlockTurnoverTest.tscn -- --harness-scenario=control-make-default-geometry
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "success".
//
// ── Why a single offline instance is the server ───────────────────────────
// Same reasoning as StealTurnoverTest/TripleThreatTest: with no MultiplayerPeer
// assigned, Godot uses OfflineMultiplayerPeer (is_server() hardcoded true,
// unique_id 1), so BallController.IsServer is true and BOTH player nodes'
// _machine.Tick() advances every physics frame regardless of role (see
// PlayerController's class doc) — the free clock that walks the shooter's
// JumpShot and the defender's BlockMove through Startup->Active with no
// per-tick poking once each is begun via the harness seam.
//
// ── Scenario "success": defender's Active overlaps the release grace window ──
// The shooter (peer "1", the tipoff holder) begins a REAL JumpShot via the
// TripleThreatHarnessSeam's BeginJumpShotForHarness (the same BeginCommittedMove
// path production input reaches). The defender (peer "2") begins a REAL
// BlockMove via DefensiveMoveHarnessSeam's BeginMoveForHarness, timed via
// ComputeDefenderBeginFrame so its Active window's very first tick lands
// exactly on the shot's release tick (frame arithmetic derived from the LIVE
// JumpShot/BlockMove frame data and BallController.BlockGraceTicks, not
// hardcoded, so this survives #104 retuning). ADR-0018 §2's full interval
// form (DefensiveResolution.Succeeds) then reports an overlap on the very
// first tick BallController evaluates it — the release-tick top-up call
// (BallController._PhysicsProcess) is what makes that first tick observable
// at all, since the pre-switch call that same tick still saw Held/Dribbling.
//
// ── Scenario "success-lastactive": the boundary "success" doesn't reach ──────
// "success" places the defender's Active window's FIRST tick on the release
// tick T. This scenario instead places the window's LAST tick on T (Active
// entry at T - (ActiveFrames - 1)) — the exact case BallController.
// _PhysicsProcess's post-switch "release-tick top-up" call exists for: the
// PRE-switch ResolveBlockAttempts call on tick T still sees State !=
// InFlight (the switch that flips it hasn't run yet that same tick), so
// without the top-up call re-evaluating after the switch, this overlap would
// never be observed at all — by tick T+1 the defender has already rolled
// into Recovery. Deleting the top-up call must fail this scenario
// deterministically (it would pass "success" regardless, since that overlap
// is already visible on the pre-switch call of a LATER tick). Asserts only
// the core turnover outcome (Loose happened / fresh possession / defender
// Recovery) — the toucher/velocity/score checks are "success"'s job.
//
// ── Scenario "whiff": defender's Active begins at the vulnerable window's exclusive end ──
// ComputeDefenderBeginFrame places the Active window's entry tick EXACTLY on
// the vulnerable window's exclusive end (releaseFrame + BlockGraceTicks) —
// the first tick the half-open interval [vulnStart, vulnEnd) excludes — so
// DefensiveResolution.Succeeds' overlap check can never be true. Pinning the
// boundary tick itself (rather than a tick safely past it) catches a ±1 shift
// in the production tick arithmetic (e.g. a node-reorder skew) that a
// one-tick-past placement would silently absorb. The shot must proceed
// unblocked (state stays InFlight) and the defender still pays Recovery.
//
// ── Scenario "control-make": the counterfactual "success" is checked against ──
// Identical shooter setup to "success" (same ShooterPosition, same clean
// ShotScatterEnabled=false geometry), but the defender never begins a
// BlockMove at all. Asserts the shot SCORES — turning "success"'s "score
// unchanged" from "the scramble happened to settle before the shot could
// have scored anyway" into real proof that the block intercepted a shot that
// would otherwise have gone in. "success" derives a generous upper bound on
// this scenario's make tick (ComputeUnblockedMakeTicks, from ShotArc's own
// closed-form flight-time solve) and keeps ticking past it before asserting
// the score is still 0-0, rather than verdicting the instant the scramble
// settles.
//
// ── Scenario "control-make-default-geometry": proves the raw [Export] default ──
// Issue #216 finding 3. Identical to "control-make" (no defender, unblocked
// shot must score) EXCEPT it skips this class's explicit
// `_ball.BoardCenter = ...` assignment (see Ready()), so it is the ONLY
// scenario in this file exercising BallController.BoardCenter's raw code
// default rather than an explicitly-set value. Every other scenario here
// re-sets BoardCenter to a value identical to the live default, so none of
// them would notice a regression to the default itself — RimBackboardTests.cs
// can only pin the pure geometry RELATIONSHIP (RimBackboard.IsBoardBehindRim),
// since BallController is Node-derived and cannot be constructed in the plain
// xUnit project. This scenario is what actually proves the live [Export]
// default still produces a scoring shot: reverting BoardCenter's default to
// the pre-#217 value (0, 3.5, 0.3) makes this scenario time out at 0-0
// (verified — see the finding-3 evidence in PR #216's description).
//
// ── Self-block (dropped scenario) ──────────────────────────────────────────
// ADR-0018/BallController.ResolveBlockAttempts excludes the shooter from being
// their own blocker via the _lastShooterPeerId check. This is NOT reachable
// through the harness seam (or even a tampered production client) under the
// current single-CommittedMoveMachine-per-player model: BeginMoveForHarness
// is exactly BeginCommittedMove(new BlockMove()), which only succeeds from
// Phase == Inactive — but the shooter's OWN machine is occupied by their
// JumpShot's Active (4 ticks) then Recovery (20 ticks) for the shot's ENTIRE
// vulnerable window (BlockGraceTicks == 10 ticks after release), so it never
// returns to Inactive until long after the grace window has closed. Forcing
// this scenario would require bypassing _machine's phase graph entirely
// (e.g. a raw field write), which is reimplementing the guard rather than
// exercising real glue — the opposite of what this harness is for. Dropped;
// the exclusion is defensive/future-proofing under the current architecture,
// not a reachable live bug surface today.
public partial class BlockTurnoverTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int ArmFrames = 2; // ticks for TryAssignTipoffHolder to run

    // Extra ticks past a computed boundary before rendering a verdict, so the
    // check lands well clear of any one-tick read skew rather than pinned to
    // the exact frame something is expected to happen. Used by TickWhiff
    // (past the defender's Active window close) and by TickSuccess's
    // counterfactual wait for the "success" scenario (past the frame an
    // unblocked shot would have scored on, see ComputeUnblockedMakeTicks).
    // "success-lastactive" does not use it — it verdicts as soon as the
    // scramble settles, same as the pre-existing "success" behaviour.
    private const int VerdictMarginFrames = 6;

    // Holder position for the shot (issue #98): far enough from RimCenter's
    // (0, *, 0) XZ that the block's swat direction (computed from
    // GlobalPosition - RimCenter, see ResolveBlockAttempts) is unambiguously
    // non-degenerate — this discriminates a real "away from the rim" swat
    // from the original "toward the rim" shot velocity by the sign of Z
    // alone, without needing exact geometry.
    private static readonly Vector3 ShooterPosition = new(0f, 0f, 5f);

    private string _scenario = "success";

    // "control-make-default-geometry" (issue #216 finding 3) is a
    // control-make TWIN: same "no defender ever begins a BlockMove, the
    // unblocked shot must score" logic, differing ONLY in that it leaves
    // BallController.BoardCenter at its raw code [Export] default instead of
    // this class's explicit override (see Ready()). Grouping both scenario
    // names under one predicate keeps every OTHER decision point (defender
    // scheduling, tick dispatch, verdict dispatch) identical between them —
    // the board-placement default is the ONLY variable this scenario tests.
    private bool IsControlMakeScenario => _scenario == "control-make" || _scenario == "control-make-default-geometry";

    private BallController _ball;
    private GameManager _gameManager;
    private PlayerController _shooter; // peer "1" — always the tipoff holder in this code-built tree
    private PlayerController _defender; // peer "2"

    private int _frame;
    private double _elapsed;
    private bool _finished;

    private bool _shooterBegun;
    private bool _defenderBegun;
    private int _shooterBeginFrame;

    // The release frame DERIVED from the shooter's begin frame + JumpShot's
    // live Startup count — the same prediction the defender scheduling uses.
    // All frame MATH must use this, never the observed _releaseFrame below:
    // in "success"/"success-lastactive" the block resolves INSIDE the ball's
    // own processing of the release frame (Held -> InFlight -> Loose within
    // one frame), so this parent — which observes children one frame late —
    // never sees InFlight at all and the observed latch stays -1.
    private int _predictedReleaseFrame = -1;

    // Observed InFlight latch — logging/sanity only, NEVER frame math (see
    // _predictedReleaseFrame for why it provably stays -1 in the scenarios
    // whose block lands on the release tick).
    private int _releaseFrame = -1;

    private int _defenderBeginFrame;

    private bool _everLoose;
    private int _toucherAtBlock = -1;   // latched on the first Loose tick
    private Vector3 _velocityAtBlock;   // latched on the first Loose tick
    private bool _velocityLatched;

    // Latched on the SAME first-Loose observation tick as _toucherAtBlock:
    // whether the defender's machine was in Recovery right after the block
    // resolved. EndResolvedDefensiveMove enters Recovery on the resolution
    // tick itself, and this latch reads it one tick later (the harness parent
    // observes children one tick late) — 1-2 ticks into the 20-tick
    // RecoveryFrames, provably still Recovery. The success verdicts must
    // assert THIS latch, not the live phase at verdict time: "success"
    // deliberately outlasts the counterfactual make (~80 ticks past the
    // block), by which point the defender has LEGITIMATELY finished Recovery
    // and returned to Inactive — a live read there is stale, not wrong (the
    // CI failure that motivated this latch).
    private bool _defenderRecoveryAtBlock;

    // "success"/"success-lastactive": latched once the loose-ball scramble
    // has settled into a fresh Held possession, so TickSuccess only re-checks
    // the (possibly still-waiting) counterfactual condition afterward instead
    // of re-deriving "has it settled yet" every tick.
    private bool _settled;

    // "success" only: the frame past which it is safe to assert the score is
    // STILL 0-0 — computed lazily as _predictedReleaseFrame +
    // ComputeUnblockedMakeTicks() + VerdictMarginFrames. -1 means "not yet
    // computed". Built on the PREDICTED release frame: the observed
    // _releaseFrame is never latched in "success" (see its field doc), and
    // computing from its -1 sentinel would end this wait ~20 frames BEFORE
    // the counterfactual make tick — exactly the hole this wait exists to
    // close.
    private int _counterfactualVerdictFrame = -1;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "success");
        GD.Print($"[block-turnover] scenario={_scenario} booting headless…");

        // Same code-built-tree pattern as StealTurnoverTest/TripleThreatTest —
        // avoids fragile .tscn ext_resource/uid wiring for a throwaway harness.
        // Tree pre-order matches scenes/Main.tscn's declaration order (Players,
        // then Ball, then GameManager) so per-tick observation timing mirrors
        // production (see StealTurnoverTest's class doc for why this ordering
        // is load-bearing for the frame arithmetic below).
        var players = new Node3D { Name = "Players" };
        _shooter  = new PlayerController { Name = "1" };
        _defender = new PlayerController { Name = "2" };
        players.AddChild(_shooter);
        players.AddChild(_defender);

        _ball = new BallController { Name = "Ball", Players = players };

        // Distance-based shot scatter (ADR-0009) would otherwise draw a
        // seeded-RNG offset even for a "clean" aim — disabling it makes the
        // unblocked shot a GUARANTEED clean make (aimTarget == ShotTarget ==
        // RimCenter unconditionally), which is what makes "score unchanged"
        // in the success scenario a real proof that the block intercepted
        // the shot, not a lucky miss the block had nothing to do with.
        _ball.ShotScatterEnabled = false;

        // Redundant with BallController's own code default (#217/#218) — kept
        // explicit anyway so this scenario's dependence on the placement is
        // self-documenting and immune to future default drift. Full history:
        // issue #217, RimBackboard.IsBoardBehindRim's doc, RimBackboardTests.cs.
        //
        // "control-make-default-geometry" (issue #216 finding 3) is the ONE
        // exception: it skips this override so it alone exercises the raw
        // code default, closing the gap where every other scenario here
        // would never notice a regression to that default.
        if (_scenario != "control-make-default-geometry")
        {
            _ball.BoardCenter = new Vector3(0f, 3.205f, -0.27f);
        }

        _gameManager = new GameManager { Name = "GameManager" };

        AddChild(players);      // matches scenes/Main.tscn: Players before Ball
        AddChild(_ball);
        AddChild(_gameManager);

        // Live-scene frame-data invariant (ADR-0018 §3): a defensive Active
        // window can never be wider than the offensive vulnerable window it
        // must hit — read from the LIVE ball node's export (not the static
        // class constant alone), so a .tscn/export override that breaks this
        // invariant would be caught here even though the pure xUnit test
        // (hardcoded const) cannot see it.
        int liveBlockGraceTicks = _ball.BlockGraceTicks;
        int liveBlockActiveFrames = BlockMove.DefaultFrameData.ActiveFrames;
        if (liveBlockActiveFrames > liveBlockGraceTicks)
        {
            Fail($"ADR-0018 §3 invariant violated: BlockMove.ActiveFrames ({liveBlockActiveFrames}) " +
                 $"must be <= BallController.BlockGraceTicks ({liveBlockGraceTicks}).");
            Finish();
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        // Step 1: once the tipoff has assigned a holder, begin the shooter's
        // REAL JumpShot through the SAME BeginCommittedMove path production
        // input uses (TripleThreatHarnessSeam's seam, issue #193).
        if (!_shooterBegun && _frame >= ArmFrames)
        {
            if (_ball.StateMachine.HolderPeerId != 1)
            {
                Fail($"expected the tipoff to award peer 1 (this code-built tree's first child); got {_ball.StateMachine.HolderPeerId}.");
                Finish();
                return;
            }

            bool began = _shooter.BeginJumpShotForHarness();
            _shooterBegun = true;
            if (!began)
            {
                Fail("BeginJumpShotForHarness returned false — shooter's machine was not Inactive at begin.");
                Finish();
                return;
            }

            _shooterBeginFrame = _frame;

            // Every scoring-relevant conclusion in this harness rests on the
            // shot being CLEARED: an uncleared make routes to ADR-0008's
            // take-it-back branch (no points, turnover to the defender) —
            // which is indistinguishable from a block/miss by score alone.
            // The tipoff pre-clears the opening possession by design
            // (TryAssignTipoffHolder, ADR-0008: the tipoff is neither a
            // change of possession nor a made basket), and this harness
            // shoots on that very first possession — assert that premise
            // fails FAST and by name if a future rule change un-clears
            // tipoffs, instead of masquerading as a mechanic failure.
            // "whiff" is exempt: its assertions (no turnover, still
            // InFlight, Recovery paid) never consult the score.
            if (_scenario != "whiff" && !_ball.IsCleared)
            {
                Fail($"the tipoff possession is not cleared (IsCleared=false) at shot begin — the make " +
                     $"would be swallowed by the take-it-back rule (ADR-0008), so no scoring assertion in " +
                     $"this scenario can mean anything. The tipoff pre-clear (TryAssignTipoffHolder) " +
                     $"changed; fix the harness setup to clear the possession before the shot.");
                Finish();
                return;
            }

            // Position the shooter away from the rim's XZ (see ShooterPosition's
            // doc) BEFORE the release tick, so the eventual swat-direction
            // check is unambiguous. Positioning here (Startup, well before
            // Active/release) is harmless — TickHeld/TickDribbling re-centres
            // the ball on the holder's position every tick regardless.
            _shooter.GlobalPosition = ShooterPosition;

            // Predicted release frame, from the SHOOTER's actual begin frame
            // plus the LIVE JumpShot frame data — not hardcoded, so it
            // survives #104 retuning. Computed for EVERY scenario: it drives
            // the defender scheduling below AND the "success" scenario's
            // counterfactual wait (see _predictedReleaseFrame's field doc for
            // why the observed InFlight latch cannot serve either role).
            int jumpShotStartup = JumpShot.DefaultFrameData.StartupFrames;
            _predictedReleaseFrame = _shooterBeginFrame + (jumpShotStartup - 1);

            // "control-make"/"control-make-default-geometry" never begin a
            // BlockMove at all (that's the point — they're the counterfactual
            // "what would have happened" scenarios success/success-lastactive
            // are checked against), so there is nothing to schedule. Leave
            // _defenderBeginFrame at its default 0, which Step 2 below can
            // never reach (_frame starts at 1 and only increases).
            if (!IsControlMakeScenario)
            {
                _defenderBeginFrame = ComputeDefenderBeginFrame(_predictedReleaseFrame);

                // Fail-fast scheduling guard: if a legal frame-data retune (e.g.
                // #104 making BlockMove.Startup >= JumpShot.Startup) pushes the
                // computed begin frame to or before the CURRENT frame, Step 2
                // below can never fire (_frame == _defenderBeginFrame will never
                // become true again) and this test would otherwise just scramble
                // for the full 15s TimeoutSeconds before failing with a
                // misleading "timed out" message. Fail immediately instead, and
                // name the actual cause.
                if (_defenderBeginFrame <= _frame)
                {
                    Fail($"computed defender begin frame {_defenderBeginFrame} is not reachable from the " +
                         $"current frame {_frame} — a frame-data change (JumpShot/BlockMove Startup/Active or " +
                         $"BlockGraceTicks) made this scenario's placement unschedulable. Fix the harness's " +
                         $"frame arithmetic for the new tunables rather than chasing the resulting timeout.");
                    Finish();
                    return;
                }

                GD.Print($"[block-turnover] frame {_frame}: shooter begun JumpShot " +
                         $"(predicted release frame {_predictedReleaseFrame}); defender scheduled to begin at frame {_defenderBeginFrame}.");
            }
        }

        // Step 2: begin the defender's REAL BlockMove at the computed frame.
        if (_shooterBegun && !_defenderBegun && _frame == _defenderBeginFrame)
        {
            bool began = _defender.BeginMoveForHarness(new BlockMove());
            _defenderBegun = true;
            if (!began)
            {
                Fail("BeginMoveForHarness(BlockMove) returned false — defender's machine was not Inactive at begin.");
                Finish();
                return;
            }
            GD.Print($"[block-turnover] frame {_frame}: defender begun BlockMove.");
        }

        // Observed-InFlight latch — logging/sanity ONLY, never frame math
        // (that is _predictedReleaseFrame's job; see its field doc). In the
        // whiff/control-make scenarios this fires one frame after the true
        // release tick (parent observes children one frame late); in
        // "success"/"success-lastactive" it never fires at all — the block
        // resolves inside the ball's own release-frame processing, so the
        // ball is Held -> InFlight -> Loose within one frame and this parent
        // never observes InFlight.
        if (_releaseFrame < 0 && _ball.State == BallState.InFlight)
        {
            _releaseFrame = _frame;
            GD.Print($"[block-turnover] frame {_frame}: ball entered InFlight (shot released).");
        }

        // Latch the turnover: catch the Loose state even if the loose-ball
        // scramble re-awards possession a frame or two later. Mirrors
        // StealTurnoverTest's _everLoose/_toucherAtSteal latch discipline.
        if (_ball.State == BallState.Loose)
        {
            if (!_everLoose)
            {
                _toucherAtBlock = _ball.LastToucherPeerIdForHarness;
                // Read the defender's phase NOW, while Recovery is provably
                // in progress — not at verdict time, when it may have
                // legitimately expired back to Inactive (see the field's doc).
                _defenderRecoveryAtBlock = _defender.PhaseForHarness == MovePhase.Recovery;
                _everLoose = true;
            }
            if (!_velocityLatched)
            {
                _velocityAtBlock = _ball.VelocityForHarness;
                _velocityLatched = true;
            }
        }

        if (_scenario == "whiff")
        {
            TickWhiff();
            return;
        }

        if (IsControlMakeScenario)
        {
            TickControlMake();
            return;
        }

        TickSuccess(); // "success" and "success-lastactive"
    }

    // Where to begin the defender's BlockMove so its Active window lands in
    // the wanted relationship to the shot's vulnerable window
    // [releaseFrame, releaseFrame + BlockGraceTicks) (ADR-0018 §2). Derived
    // from LIVE tunables (JumpShot/BlockMove frame data, BlockGraceTicks), not
    // hardcoded, so this survives #104 retuning of any of them.
    //
    // CommittedMoveMachine.Tick() semantics (see that class's doc): Begin()
    // sets FrameInPhase=0 the SAME frame it's called; because Players ticks
    // BEFORE Ball each frame (matching production — see the class doc's
    // "Why a single offline instance is the server"), the FIRST Tick() call
    // also lands on that same begin frame, advancing FrameInPhase to 1. A
    // move's Active phase is therefore entered on frame
    // (beginFrame + StartupFrames - 1) with FrameInPhase reset to 0 there —
    // the same arithmetic StealTurnoverTest's ComputeBeginFrame doc derives
    // for the dribble-phase case, specialized here to a move's own Startup
    // count instead of a periodic dribble cycle.
    private int ComputeDefenderBeginFrame(int releaseFrame)
    {
        int blockStartup = BlockMove.DefaultFrameData.StartupFrames;
        int graceTicks = _ball.BlockGraceTicks;

        if (_scenario == "whiff")
            // Active entry lands EXACTLY on the vulnerable window's exclusive
            // end (releaseFrame + graceTicks) — the FIRST tick the half-open
            // interval [vulnStart, vulnEnd) EXCLUDES. DefensiveResolution.
            // Succeeds' overlap test is defActiveStart < vulnEnd && vulnStart
            // < defActiveEnd; with defActiveStart == vulnEnd the first half
            // (vulnEnd < vulnEnd) is false outright, so this pins the boundary
            // itself rather than a tick safely past it — catching an ±1 shift
            // in the production tick arithmetic (e.g. node-reorder skew) that
            // a one-tick-past placement would silently absorb.
            return (releaseFrame + graceTicks) - (blockStartup - 1);

        if (_scenario == "success-lastactive")
        {
            // Active entry lands so the defender's LAST Active tick (not the
            // first) coincides with the release tick T: defActiveStart =
            // T - (ActiveFrames - 1), i.e. beginFrame = T - (ActiveFrames-1)
            // - (StartupFrames-1). This is the exact case
            // BallController._PhysicsProcess's post-switch "release-tick
            // top-up" call exists for: the PRE-switch ResolveBlockAttempts
            // call on tick T still observes State != InFlight (the switch
            // that flips it to InFlight hasn't run yet that same tick), so
            // without the top-up call this overlap would never be evaluated
            // at all — by tick T+1 the defender has already rolled into
            // Recovery and BlockMoveActiveInterval is null. Deleting the
            // top-up call must fail this scenario deterministically.
            int blockActive = BlockMove.DefaultFrameData.ActiveFrames;
            return releaseFrame - (blockActive - 1) - (blockStartup - 1);
        }

        // "success": Active entry lands EXACTLY on the release tick itself —
        // guarantees an overlap (defActiveStart == vulnStart).
        return releaseFrame - (blockStartup - 1);
    }

    private void TickSuccess()
    {
        // Wait for the FULL turnover to complete — not merely that the ball
        // was ever observed Loose (see StealTurnoverTest's "anti-hollow-green"
        // rationale for why _everLoose alone is insufficient). Since #193,
        // AwardPossession settles the loose-ball scramble into a fresh, live
        // Held possession, not Dribbling.
        if (!_settled)
        {
            bool settled = _everLoose && _ball.State == BallState.Held && _ball.StateMachine.HolderPeerId != 0;
            if (settled)
            {
                _settled = true;
            }
            else
            {
                if (_elapsed > TimeoutSeconds)
                {
                    Fail($"timed out at frame {_frame} waiting for the block's loose-ball scramble to settle. " +
                         $"everLoose={_everLoose}, state={_ball.State}, defenderBegun={_defenderBegun}, " +
                         $"predictedReleaseFrame={_predictedReleaseFrame} (observed InFlight latch " +
                         $"{_releaseFrame}; -1 is EXPECTED here — a release-tick block never shows " +
                         $"this parent an InFlight frame).");
                    Finish();
                }
                return;
            }
        }

        // "success" alone must also outlast the frame an UNBLOCKED clean shot
        // from this exact setup would have scored on — the counterfactual
        // "control-make" scenario's make — before rendering its verdict. This
        // turns "score unchanged" from "the scramble merely settled quickly"
        // into "a blocked shot can never score, even generously past the
        // moment rim contact would have happened." "success-lastactive" skips
        // this: its point is pinning the release-tick top-up boundary, not
        // re-proving the counterfactual (already proven by "success").
        if (_scenario == "success")
        {
            // PREDICTED release frame, never the observed _releaseFrame: the
            // release-tick block means this parent never observes InFlight,
            // so the observed latch is still -1 here — computing from it
            // would end this wait ~20 frames before the counterfactual make
            // tick and let a same-tick-scoring regression slip through.
            if (_counterfactualVerdictFrame < 0)
                _counterfactualVerdictFrame = _predictedReleaseFrame + ComputeUnblockedMakeTicks() + VerdictMarginFrames;

            if (_frame < _counterfactualVerdictFrame)
            {
                if (_elapsed > TimeoutSeconds)
                {
                    Fail($"timed out at frame {_frame} waiting to safely outlast the counterfactual make frame " +
                         $"{_counterfactualVerdictFrame} (predictedReleaseFrame={_predictedReleaseFrame}) " +
                         $"before rendering the success verdict.");
                    Finish();
                }
                return;
            }
        }

        Verdict();
    }

    // A generous UPPER BOUND (not a guess) on how many ticks an UNBLOCKED
    // clean shot from this exact setup would take to reach the rim and
    // register a Make, calling ShotArc.ComputeFlightTime directly (issue
    // #216 original body row 6 — this used to hand-re-derive the same t_up +
    // t_down math, which could silently drift from ShotArc's own solve on a
    // future change) rather than hardcoding a tick count — so a tuning
    // change to any of these exports doesn't silently stale this bound out.
    // Release Y is DribbleHandHeight exactly: TickHeld sets the ball's
    // world-Y to DribbleHandHeight minus the crossover sweep's mid-transit
    // dip, and this shooter never dribbles or crosses over, so the dip term
    // is always 0 here. Target Y is RimCenter.Y (ShotTarget == RimCenter
    // with ShotScatterEnabled disabled, per this class's doc).
    private int ComputeUnblockedMakeTicks()
    {
        float tTotal = ShotArc.ComputeFlightTime(
            releaseY:   _ball.DribbleHandHeight,
            targetY:    _ball.RimCenter.Y,
            apexHeight: _ball.ShotApexHeight,
            gravity:    _ball.Gravity);
        return Mathf.CeilToInt(tTotal * Engine.PhysicsTicksPerSecond);
    }

    private void TickWhiff()
    {
        // No discrete "the shot is definitely safe" event exists to wait for
        // cheaply (the shot's natural flight is tens of ticks long) — instead
        // wait past the point the defender's own Active window closes (a
        // computable, non-guessed boundary: their Active started at
        // _defenderBeginFrame + StartupFrames - 1 and runs ActiveFrames
        // ticks), plus a small margin, then assert the ABSENCE of a block
        // this whole time. This is legitimate per ADR-0016 (asserting real
        // engine state), unlike guessing when a DIFFERENT event will occur.
        if (!_defenderBegun)
        {
            if (_elapsed > TimeoutSeconds)
            {
                Fail($"timed out at frame {_frame} before the defender's scheduled begin frame {_defenderBeginFrame}.");
                Finish();
            }
            return;
        }

        int blockStartup = BlockMove.DefaultFrameData.StartupFrames;
        int blockActive = BlockMove.DefaultFrameData.ActiveFrames;
        int blockRecovery = BlockMove.DefaultFrameData.RecoveryFrames;
        int defenderActiveEnd = _defenderBeginFrame + (blockStartup - 1) + blockActive;
        int verdictFrame = defenderActiveEnd + VerdictMarginFrames;

        // Unlike the success scenarios there is no block event to latch the
        // Recovery observation on here — the whiffed move expires NATURALLY —
        // so the verdict's live "defender pays Recovery" read must instead be
        // TIMED provably inside the Recovery window: Recovery is entered on
        // defenderActiveEnd (the Active->Recovery expiry tick) and is live
        // for [defenderActiveEnd, defenderActiveEnd + RecoveryFrames) — with
        // defaults, ticks 37..56 of a begin-at-20 whiff. The verdict fires at
        // defenderActiveEnd + VerdictMarginFrames (37 + 6 = 43), which reads
        // the defender's post-frame-42 state (the harness parent observes
        // children one tick late) — inside the window whenever
        // VerdictMarginFrames <= RecoveryFrames. Fail fast if a retune ever
        // breaks that inequality, instead of letting the verdict silently
        // read a legitimately-expired Inactive phase (the same live-read-
        // too-late bug class the success scenarios' latch fixes).
        if (VerdictMarginFrames > blockRecovery)
        {
            Fail($"VerdictMarginFrames ({VerdictMarginFrames}) exceeds BlockMove.RecoveryFrames " +
                 $"({blockRecovery}) — the whiff verdict's live Recovery read would land after Recovery " +
                 $"has legitimately expired. A frame-data retune broke this timing; re-derive the " +
                 $"whiff verdict frame rather than chasing the resulting phase-assert failure.");
            Finish();
            return;
        }

        if (_frame >= verdictFrame)
        {
            Verdict();
            return;
        }

        if (_elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame} before reaching verdict frame {verdictFrame}.");
            Finish();
        }
    }

    // "control-make": no BlockMove ever begins — this is the counterfactual
    // "what would have happened" run "success" is checked against (see
    // ComputeUnblockedMakeTicks / TickSuccess's counterfactual wait). Waits
    // for the shooter's own score to register — with ShotScatterEnabled
    // disabled and the same clean geometry "success" uses, an unblocked shot
    // from this setup is a guaranteed make.
    private void TickControlMake()
    {
        if (_gameManager.ScoreOf(1) > 0)
        {
            Verdict();
            return;
        }

        if (_elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame} waiting for the unblocked control shot to score. " +
                 $"state={_ball.State}, score1={_gameManager.ScoreOf(1)}.");
            Finish();
        }
    }

    private void Verdict()
    {
        if (_scenario == "whiff")
        {
            VerdictWhiff();
            return;
        }

        if (IsControlMakeScenario)
        {
            VerdictControlMake();
            return;
        }

        if (_scenario == "success-lastactive")
        {
            VerdictSuccessLastActive();
            return;
        }

        VerdictSuccess();
    }

    private void VerdictControlMake()
    {
        // The whole point of this scenario: with no block ever attempted, the
        // SAME clean setup "success" uses scores exactly once — the real proof
        // that "success"/"success-lastactive"'s "score unchanged" means the
        // block intercepted a shot that would otherwise have gone in, not that
        // the shot never had a chance to score in the first place.
        // "control-make-default-geometry" shares this exact assertion — its
        // only difference is which BoardCenter value produced the geometry
        // (see Ready()'s doc, issue #216 finding 3).
        bool pass = _gameManager.ScoreOf(1) == 1 && _gameManager.ScoreOf(2) == 0;

        if (pass)
        {
            GD.Print($"[block-turnover] PASS — scenario={_scenario}, score1={_gameManager.ScoreOf(1)}, " +
                     $"score2={_gameManager.ScoreOf(2)}.");
        }
        else
        {
            Fail($"scenario={_scenario} expected the unblocked shot to score exactly once, but got " +
                 $"score1={_gameManager.ScoreOf(1)}, score2={_gameManager.ScoreOf(2)}.");
        }
        Finish(pass ? 0 : 1);
    }

    private void VerdictSuccess()
    {
        bool turnoverCompleted = _everLoose && _ball.State == BallState.Held && _ball.StateMachine.HolderPeerId != 0;

        // Last toucher (#118 rule, mirrors the steal harness's discriminator):
        // the defender, not the shooter, must be charged as the last toucher
        // the instant the ball goes Loose — otherwise a swatted ball that
        // sails OOB before the scramble recovers it would wrongly hand the
        // turnover back to the offense.
        bool toucherCorrect = _toucherAtBlock == 2;

        // No basket registered for the blocked arc (make-detection path
        // unreachable): with ShotScatterEnabled disabled, an UNBLOCKED shot
        // from this setup is a guaranteed clean make — so both scores
        // remaining 0 after the scramble has fully settled is real proof the
        // block intercepted the shot before TickInFlight's make-detection
        // ever ran, not a lucky miss.
        bool scoreUnchanged = _gameManager.ScoreOf(1) == 0 && _gameManager.ScoreOf(2) == 0;

        // Velocity after the block is the swat (away from the rim / downward),
        // not the original shot velocity (toward the rim). ShooterPosition's Z
        // (+5) is far enough from RimCenter's Z (0) that the swat's Z
        // component must be POSITIVE (away from the rim, back toward the
        // shooter) — the original shot velocity would have had a NEGATIVE Z
        // component (toward the rim at Z=0). The swat is also DOWNWARD
        // (Y < 0), contrasting the shot's rising arc at this early point in
        // flight.
        bool velocityIsSwat = _velocityLatched && _velocityAtBlock.Z > 0f && _velocityAtBlock.Y < 0f;

        // Spent once: the block resolved the defender's Active phase early
        // (EndResolvedDefensiveMove -> CommittedMoveMachine.EndActiveEarly),
        // so the defender's machine must be in Recovery ON THE TICK THE
        // TURNOVER IS OBSERVED. LATCHED at block-observation time (like
        // _velocityAtBlock), NOT read live here: this verdict deliberately
        // runs ~80 ticks past the block (the counterfactual wait), by which
        // point the 20-tick Recovery has legitimately expired back to
        // Inactive — a live read would fail on correct behaviour.
        bool defenderSpentOnceLatched = _defenderRecoveryAtBlock;

        bool pass = turnoverCompleted && toucherCorrect && scoreUnchanged && velocityIsSwat && defenderSpentOnceLatched;

        if (pass)
        {
            GD.Print($"[block-turnover] PASS — scenario=success, finalState={_ball.State}, " +
                     $"holder={_ball.StateMachine.HolderPeerId}, toucherAtBlock={_toucherAtBlock}, " +
                     $"velocityAtBlock={_velocityAtBlock}, defenderRecoveryAtBlock={_defenderRecoveryAtBlock}, " +
                     $"score1={_gameManager.ScoreOf(1)}, score2={_gameManager.ScoreOf(2)}.");
        }
        else
        {
            Fail($"scenario=success expected a completed turnover, toucherAtBlock=2, unchanged score, a swat " +
                 $"velocity (Z>0, Y<0), and defender Recovery at block time, but got turnoverCompleted={turnoverCompleted}, " +
                 $"toucherAtBlock={_toucherAtBlock}, scoreUnchanged={scoreUnchanged} (score1={_gameManager.ScoreOf(1)}, " +
                 $"score2={_gameManager.ScoreOf(2)}), velocityAtBlock={_velocityAtBlock} " +
                 $"(latched={_velocityLatched}), defenderRecoveryAtBlock={_defenderRecoveryAtBlock} " +
                 $"(livePhaseNow={_defender.PhaseForHarness} — informational only; Inactive here is expected " +
                 $"long after the block).");
        }
        Finish(pass ? 0 : 1);
    }

    // "success-lastactive" pins the release-tick TOP-UP call specifically
    // (see ComputeDefenderBeginFrame's doc): the defender's Active window
    // overlaps the vulnerable window by exactly one tick, at its OWN last
    // tick rather than its first. Checks only the core turnover outcome
    // (Loose happened / fresh possession / defender Recovery) — the
    // toucher/velocity/score details are already the "success" scenario's
    // job; re-asserting them here would not add coverage of the boundary
    // this scenario exists to pin.
    private void VerdictSuccessLastActive()
    {
        bool turnoverCompleted = _everLoose && _ball.State == BallState.Held && _ball.StateMachine.HolderPeerId != 0;

        // Latched, not live (same reasoning as VerdictSuccess): this verdict
        // fires whenever the loose-ball scramble settles, and nothing bounds
        // that settle time to land inside the defender's 20-tick Recovery —
        // a slow scramble would flip a live read to Inactive on perfectly
        // correct behaviour.
        bool defenderSpentOnceLatched = _defenderRecoveryAtBlock;

        bool pass = turnoverCompleted && defenderSpentOnceLatched;

        if (pass)
        {
            GD.Print($"[block-turnover] PASS — scenario=success-lastactive, finalState={_ball.State}, " +
                     $"holder={_ball.StateMachine.HolderPeerId}, defenderRecoveryAtBlock={_defenderRecoveryAtBlock}.");
        }
        else
        {
            Fail($"scenario=success-lastactive expected a completed turnover and defender Recovery at block time, " +
                 $"but got turnoverCompleted={turnoverCompleted} (everLoose={_everLoose}, state={_ball.State}, " +
                 $"holder={_ball.StateMachine.HolderPeerId}), defenderRecoveryAtBlock={_defenderRecoveryAtBlock} " +
                 $"(livePhaseNow={_defender.PhaseForHarness} — informational only).");
        }
        Finish(pass ? 0 : 1);
    }

    private void VerdictWhiff()
    {
        // The shot must proceed unblocked: no block-induced early transition
        // to Loose ever happened, and the ball is still InFlight (its natural
        // flight is far longer than this verdict's margin — see TickWhiff).
        bool shotProceeded = !_everLoose && _ball.State == BallState.InFlight;

        // The last toucher must be unchanged from the tipoff (peer 1) — the
        // defender's whiffed attempt touched nothing.
        bool toucherUnchanged = _ball.LastToucherPeerIdForHarness == 1;

        // The defender still pays Recovery for the whiff (ADR-0018 §3's
        // reaction-tilt asymmetry applies to a miss just as much as a hit).
        // Live read, deliberately NOT latched: there is no block event to
        // latch on for a whiff — instead TickWhiff times this verdict to a
        // frame provably inside the natural Recovery window and fail-fasts
        // if a frame-data retune would break that timing (see the derivation
        // at TickWhiff's verdict-frame computation).
        bool defenderPaysRecovery = _defender.PhaseForHarness == MovePhase.Recovery;

        bool pass = shotProceeded && toucherUnchanged && defenderPaysRecovery;

        if (pass)
        {
            GD.Print($"[block-turnover] PASS — scenario=whiff, everLoose={_everLoose}, " +
                     $"finalState={_ball.State}, toucher={_ball.LastToucherPeerIdForHarness}, " +
                     $"defenderPhase={_defender.PhaseForHarness}.");
        }
        else
        {
            Fail($"scenario=whiff expected everLoose=false, state=InFlight, toucher=1, and defender Recovery, " +
                 $"but got everLoose={_everLoose}, state={_ball.State}, toucher={_ball.LastToucherPeerIdForHarness}, " +
                 $"defenderPhase={_defender.PhaseForHarness}.");
        }
        Finish(pass ? 0 : 1);
    }

    private void Fail(string message) => GD.PrintErr($"[block-turnover] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[block-turnover] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
