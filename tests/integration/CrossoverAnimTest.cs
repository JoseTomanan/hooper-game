using System.Collections.Generic;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #280 (ADR-0016): proves the SIX
// hand-side crossover states scenes/Player.tscn now carries —
// Crossover{Startup,Active,Recovery}{Left,Right} — are actually ENTERED
// end-to-end through the real AnimationTree, and that the SUFFIX stays
// constant across a single crossover's whole Startup->Active->Recovery arc
// even though PlayerController.HandSide itself flips mid-move.
//
// MoveKindAnimTest (#277) already proves a clipped move's GENERIC per-move
// states are reachable; this is its narrower, hand-side-specific sibling for
// the ONE move MoveAnimResolver.HandedMoves currently lists ("crossover").
// What neither MoveKindAnimTest nor MoveAnimResolverTests (xUnit, pure/no
// AnimationTree) can catch is a PER-TICK hand read: OriginHand exists
// specifically because a crossover's Active-entry flips HandSide (Left<->Right)
// partway through the move (TickCommittedMoveBehavior's JustEnteredActive
// branch) — reading HandSide naively on every tick would show the Startup
// wind-up telegraphing one direction and the Active/Recovery burst playing the
// MIRROR of it, exactly the false read ADR-0003 forbids. OriginHand corrects
// for that by inverting the post-swap phases (see MoveAnimResolver.OriginHand's
// own doc for the full derivation). Only reading the LIVE
// AnimationNodeStateMachinePlayback proves the correction is actually wired
// into the real .tscn state machine, not just correct in the resolver's own
// unit tests.
//
//   godot --headless --path . res://tests/integration/CrossoverAnimTest.tscn -- --harness-scenario=crossover-left-origin
//   godot --headless --path . res://tests/integration/CrossoverAnimTest.tscn -- --harness-scenario=crossover-right-origin
//   godot --headless --path . res://tests/integration/CrossoverAnimTest.tscn -- --harness-scenario=crossover-single-polarity
//   godot --headless --path . res://tests/integration/CrossoverAnimTest.tscn -- --harness-scenario=no-unsuffixed-crossover-state
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "crossover-left-origin".
//
// ── Why ActiveAnimNodeForHarness, not the resolver's own decision (#257) ────
// Same discipline as every other anim harness in this repo: reading
// AnimationNodeStateMachinePlayback.GetCurrentNode() (via the live
// AnimationTree) asserts what the state machine ACTUALLY did, never what
// MoveAnimResolver.ResolveStateName merely DECIDED — calling that directly
// would keep passing even on a Player.tscn with none of the six handed states
// wired at all, since the resolver has no notion the .tscn even exists.
// Travel() to a missing/misnamed state only LOGS, it never throws (#257).
//
// ── Why a real scenes/Player.tscn instance, and a live BallController ───────
// A live AnimationTree only exists on the REAL scenes/Player.tscn (a bare
// `new PlayerController()` has no mesh/tree at all). Crossover is ALSO a
// dribble-family committed move: BeginCommittedMove's Held-holder
// dead-dribble gate (#193) refuses it outright unless the ball is genuinely
// Dribbling (see PlayerController.BeginCommittedMove's "#193 code-review fix"
// comment) — same reasoning MoveKindAnimTest's header already spells out for
// BehindTheBack/BetweenTheLegs, which Crossover shares verbatim.
//
// ── Why SetHandSideForHarness for crossover-right-origin ────────────────────
// A fresh tipoff possession always resets HandSide to the default (Left) —
// PlayerController.HandSide's own field doc, "reset to the default (Left)
// when the player gains possession". There is no production path that starts
// a holder in the RIGHT hand without first running an actual hand-swapping
// move, so the right-origin polarity needs a direct harness seam
// (SetHandSideForHarness, added alongside SetHeadingForHarness in
// PlayerHarnessSeam.cs for this issue) to force the precondition before
// Begin() — the same "direct setup, narrower than driving real input" pattern
// FadeawayTriggerTest/JumpshotAnimTest already use for Heading.
//
// ── Why crossover-single-polarity does NOT re-derive expected names ─────────
// The load-bearing scenario deliberately does not ask "does MoveAnimResolver
// think this should be CrossoverActiveLeft" — that would just be re-running
// the resolver's own (already unit-tested) formula and comparing it to
// itself. Instead it collects whatever DISTINCT "Crossover*" node names the
// LIVE tree actually reported over one full crossover and asserts they all
// share the same trailing suffix. A per-tick HandSide read (the exact bug
// OriginHand exists to prevent) would make this scenario observe BOTH
// "CrossoverStartupLeft" and "CrossoverActiveLeft"/"CrossoverRecoveryRight" (or
// the mirror), i.e. two distinct suffixes in the same run — this scenario
// fails on that mix, not on comparing against a resolver-derived expectation.
//
// ── What this harness CANNOT prove: individual transition EDGES ─────────────
// Same limitation JumpshotAnimTest's header documents at length (measured by
// mutation, #279): Travel() is a PATHFINDER over the transition graph, not a
// single-hop switch, so deleting a direct edge between two handed states does
// not redden anything observable through ActiveAnimNodeForHarness — the walk
// still arrives at the target via whatever other edges exist. Do not extend
// this file with an edge-level assertion; it will pass regardless of the
// edge's presence.
//
// ── Why the controls carry the real weight here ─────────────────────────────
// "crossover-single-polarity" and "no-unsuffixed-crossover-state" both assert
// their OWN PREMISE first — that a real crossover genuinely produced at least
// one "Crossover*" node observation — so neither can pass vacuously on a rig
// where nothing ever left Locomotion/Dribble (which would trivially satisfy
// "never showed a mixed/unsuffixed state" for any state).
//
// Whether either clip LOOKS right (correct limbs, no foot-sliding, reads as
// "crossover") is #279/#173's deferred human feel judgment (ADR-0021) — this
// harness only asserts state-machine REACHABILITY and suffix consistency,
// never clip content.
public partial class CrossoverAnimTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int ArmFrames = 2;           // ticks for TryAssignTipoffHolder to run
    private const int ActionMarginFrames = 3;  // ticks to let TryStartDribble's effect settle
    private const int SettleMarginFrames = 5;  // ticks after returning to Inactive before the final read

    private static readonly string[] KnownScenarios =
    {
        "crossover-left-origin", "crossover-right-origin", "crossover-single-polarity", "no-unsuffixed-crossover-state"
    };

    private string _scenario = "crossover-left-origin";

    private BallController _ball;
    private PlayerController _p1;
    private PlayerController _p2;

    private int _frame;
    private double _elapsed;
    private bool _finished;

    private int _holderId;

    private enum Step { AwaitTipoff, DriveChecked, Observing }
    private Step _step = Step.AwaitTipoff;
    private int _stepDeadlineFrame;

    // ── Latches for crossover-left-origin / crossover-right-origin ─────────
    // Chained exactly like JumpshotAnimTest's phase latches: each guard
    // requires the previous one already latched, so "saw all three" IS
    // "saw them in order."
    private bool _sawStartup;
    private bool _sawActive;
    private bool _sawRecovery;

    // ── Observations for crossover-single-polarity / no-unsuffixed-crossover-state ─
    // Every DISTINCT "Crossover*" node name the live tree reported over the
    // whole run — collected at event time (every physics tick), not sampled
    // once at the end.
    private readonly HashSet<string> _distinctCrossoverStates = new();
    private bool _sawAnyCrossoverState;
    private bool _sawUnsuffixedCrossoverState;

    // Gate for "the move genuinely ran" (mirrors MoveKindAnimTest's
    // _sawActivePhase): only once the Active phase has actually been observed
    // does a later return to Inactive count as "the lifecycle finished."
    private bool _sawActivePhase;
    private int _returnedInactiveFrame = -1;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "crossover-left-origin");
        GD.Print($"[crossover-anim] scenario={_scenario} booting headless…");

        if (!KnownScenarios.Contains(_scenario))
        {
            Fail($"unknown scenario '{_scenario}'.");
            Finish();
            return;
        }

        // Real Player.tscn instances (live AnimationTree), named "1"/"2" so the
        // OfflineMultiplayerPeer makes unique_id 1 both IsServer and
        // IsLocalPlayer (the full TickServerOwnPlayer -> ApplyAnimation chain
        // runs every tick), same as JumpshotAnimTest/MoveKindAnimTest.
        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        _p1 = scene.Instantiate<PlayerController>();
        _p1.Name = "1";
        _p2 = scene.Instantiate<PlayerController>();
        _p2.Name = "2";

        // Physics-callback lockstep so GetCurrentNode() reflects the same-tick
        // Travel() (the default Idle callback lags under --headless — see
        // MoveKindAnimTest's long note; harness-only observation fidelity).
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

        switch (_step)
        {
            case Step.AwaitTipoff:
                if (_frame < ArmFrames) break;
                if (_ball.StateMachine.HolderPeerId == 0)
                {
                    Fail($"{_scenario}: tipoff never assigned a holder.");
                    Finish();
                    return;
                }
                _holderId = _ball.StateMachine.HolderPeerId;
                _ball.TryStartDribble(_holderId);
                _step = Step.DriveChecked;
                _stepDeadlineFrame = _frame + ActionMarginFrames;
                break;

            case Step.DriveChecked:
                if (_frame < _stepDeadlineFrame) break;
                if (_ball.State != BallState.Dribbling)
                {
                    Fail($"{_scenario}: expected TryStartDribble to reach Dribbling " +
                         $"(Crossover cannot Begin from Held, #193); got state={_ball.State}.");
                    Finish();
                    return;
                }

                var holder = HolderNode();

                // Only crossover-right-origin needs a forced precondition —
                // every other scenario runs the shipped default (Left), which
                // the tipoff's possession-award already leaves the holder in
                // (see PlayerController.HandSide's field doc).
                if (_scenario == "crossover-right-origin")
                    holder.SetHandSideForHarness(HandSide.Right);

                // The flick sign must point at the EMPTY hand, or this would not
                // be a crossover at all in the live input path — one flick
                // produces a crossover or a HESITATION depending on exactly this
                // (HandStateResolver.IsCrossover: Left,+1 and Right,-1 are the
                // only crossover rows). BeginCrossoverForHarness bypasses that
                // gate, so an inverted sign here would still "work"; it is
                // written correctly anyway so the scenario reproduces a real
                // crossover rather than an input the game could never produce,
                // and so nobody reads this and concludes the sign is arbitrary.
                //
                // Display does NOT depend on it. MoveAnimResolver deliberately
                // reads the authoritative HandSide rather than BurstDirection,
                // because the server reconstructs the move straight from the
                // client's payload with no IsCrossover re-validation, so the
                // burst sign is not a trustworthy witness to the origin hand.
                float flickSign = HandStateResolver.EmptyHandSign(holder.HandSide);
                bool began = holder.BeginCrossoverForHarness(flickSign);
                if (!began)
                {
                    Fail($"{_scenario}: BeginCrossoverForHarness returned false " +
                         "— machine was not Inactive or the dead-dribble gate refused it.");
                    Finish();
                    return;
                }
                GD.Print($"[crossover-anim] Crossover begun on holder={_holderId} " +
                         $"(startHand={holder.HandSide}).");
                _step = Step.Observing;
                break;

            case Step.Observing:
                Observe();
                if (_returnedInactiveFrame >= 0 && _frame == _returnedInactiveFrame + SettleMarginFrames)
                    RenderVerdict();
                break;
        }

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame}, scenario={_scenario}, step={_step}, " +
                 $"lastAnimNode={HolderNode()?.ActiveAnimNodeForHarness}, sawStartup={_sawStartup}, " +
                 $"sawActive={_sawActive}, sawRecovery={_sawRecovery}, sawActivePhase={_sawActivePhase}, " +
                 $"distinctCrossoverStates=[{string.Join(",", _distinctCrossoverStates)}], " +
                 $"sawUnsuffixed={_sawUnsuffixedCrossoverState}.");
            Finish();
        }
    }

    private PlayerController HolderNode() => _holderId == 1 ? _p1 : _p2;

    private void Observe()
    {
        PlayerController holder = HolderNode();
        MovePhase phase = holder.PhaseForHarness;
        string node = holder.ActiveAnimNodeForHarness;

        if (phase == MovePhase.Active) _sawActivePhase = true;

        switch (_scenario)
        {
            case "crossover-left-origin":
                if (!_sawStartup && node == "CrossoverStartupLeft") _sawStartup = true;
                if (_sawStartup && !_sawActive && node == "CrossoverActiveLeft") _sawActive = true;
                if (_sawActive && !_sawRecovery && node == "CrossoverRecoveryLeft") _sawRecovery = true;
                break;

            case "crossover-right-origin":
                if (!_sawStartup && node == "CrossoverStartupRight") _sawStartup = true;
                if (_sawStartup && !_sawActive && node == "CrossoverActiveRight") _sawActive = true;
                if (_sawActive && !_sawRecovery && node == "CrossoverRecoveryRight") _sawRecovery = true;
                break;

            case "crossover-single-polarity":
            case "no-unsuffixed-crossover-state":
                if (node.StartsWith("Crossover"))
                {
                    _sawAnyCrossoverState = true;
                    _distinctCrossoverStates.Add(node);
                    if (node == "CrossoverStartup" || node == "CrossoverActive" || node == "CrossoverRecovery")
                        _sawUnsuffixedCrossoverState = true;
                }
                break;
        }

        if (_sawActivePhase && phase == MovePhase.Inactive && _returnedInactiveFrame < 0)
            _returnedInactiveFrame = _frame;
    }

    private void RenderVerdict()
    {
        switch (_scenario)
        {
            case "crossover-left-origin":            VerdictOrigin("Left"); break;
            case "crossover-right-origin":            VerdictOrigin("Right"); break;
            case "crossover-single-polarity":         VerdictSinglePolarity(); break;
            case "no-unsuffixed-crossover-state":     VerdictNoUnsuffixed(); break;
        }
    }

    // ── Scenarios: crossover-left-origin / crossover-right-origin (positive) ──
    private void VerdictOrigin(string suffix)
    {
        bool pass = _sawStartup && _sawActive && _sawRecovery;
        if (pass)
            GD.Print($"[crossover-anim] PASS crossover-{suffix.ToLowerInvariant()}-origin — the tree was " +
                     $"observed on \"CrossoverStartup{suffix}\", then \"CrossoverActive{suffix}\", then " +
                     $"\"CrossoverRecovery{suffix}\", in that order.");
        else
            Fail($"crossover-{suffix.ToLowerInvariant()}-origin: expected CrossoverStartup{suffix} -> " +
                 $"CrossoverActive{suffix} -> CrossoverRecovery{suffix}, in order; got sawStartup={_sawStartup}, " +
                 $"sawActive={_sawActive}, sawRecovery={_sawRecovery}, " +
                 $"lastAnimNode={HolderNode().ActiveAnimNodeForHarness}.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: crossover-single-polarity (load-bearing) ─────────────────
    private void VerdictSinglePolarity()
    {
        // Premise check: the crossover must genuinely have shown at least one
        // "Crossover*" state, or "all observed suffixes agree" is vacuously
        // true over an empty set.
        var suffixes = _distinctCrossoverStates.Select(SuffixOf).Distinct().ToList();
        bool pass = _sawAnyCrossoverState && suffixes.Count == 1;

        if (pass)
            GD.Print($"[crossover-anim] PASS crossover-single-polarity — every distinct crossover state " +
                     $"observed ([{string.Join(",", _distinctCrossoverStates)}]) carried the SAME hand suffix " +
                     $"(\"{suffixes[0]}\"), across the whole Startup->Active->Recovery arc.");
        else
            Fail($"crossover-single-polarity: expected exactly one suffix across every observed crossover " +
                 $"state; got sawAnyCrossoverState={_sawAnyCrossoverState}, " +
                 $"distinctStates=[{string.Join(",", _distinctCrossoverStates)}], " +
                 $"distinctSuffixes=[{string.Join(",", suffixes)}]. If the premise broke, this proves nothing, " +
                 "so this fails rather than passes.");
        Finish(pass ? 0 : 1);
    }

    private static string SuffixOf(string node) =>
        node.EndsWith("Left") ? "Left" :
        node.EndsWith("Right") ? "Right" :
        "None";

    // ── Scenario: no-unsuffixed-crossover-state (control) ───────────────────
    private void VerdictNoUnsuffixed()
    {
        // Premise check: a crossover must genuinely have run (shown SOME
        // "Crossover*" state) before "never showed a bare unsuffixed one"
        // means anything — otherwise a rig stuck on Locomotion/Dribble the
        // whole time would trivially satisfy "never saw it."
        bool pass = _sawAnyCrossoverState && !_sawUnsuffixedCrossoverState;
        if (pass)
            GD.Print("[crossover-anim] PASS no-unsuffixed-crossover-state — the crossover genuinely ran " +
                     $"(observed [{string.Join(",", _distinctCrossoverStates)}]) and the tree was never " +
                     "observed on a bare \"CrossoverStartup\"/\"CrossoverActive\"/\"CrossoverRecovery\".");
        else
            Fail($"no-unsuffixed-crossover-state: expected the crossover to run genuinely " +
                 $"(sawAnyCrossoverState={_sawAnyCrossoverState}) AND never observe a bare unsuffixed state; " +
                 $"sawUnsuffixedCrossoverState={_sawUnsuffixedCrossoverState}, " +
                 $"distinctStates=[{string.Join(",", _distinctCrossoverStates)}]. If the premise broke, " +
                 "'no unsuffixed state observed' proves nothing, so this fails rather than passes.");
        Finish(pass ? 0 : 1);
    }

    private void Fail(string message) => GD.PrintErr($"[crossover-anim] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[crossover-anim] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
