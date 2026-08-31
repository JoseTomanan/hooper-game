using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #277: proves the per-move
// AnimationTree display layer (MoveAnimResolver.ResolveStateName +
// PlayerController.ApplyAnimation's Travel() call, scenes/Player.tscn's new
// per-move states) is actually REACHED end-to-end for a clipped move, and
// that an unclipped move genuinely falls back to the shared generic state
// rather than either state name being asserted in isolation.
//
//   godot --headless --path . res://tests/integration/MoveKindAnimTest.tscn -- --harness-scenario=clipped-reaches-permove
//   godot --headless --path . res://tests/integration/MoveKindAnimTest.tscn -- --harness-scenario=unclipped-stays-generic
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "clipped-reaches-permove".
//
// ── Why ActiveAnimNodeForHarness, not CurrentAnimStateForHarness (#257) ─────
// Travel() to a missing/misnamed AnimationTree state only LOGS a Godot error
// — it never throws or rolls the field back. PivotAnimTest's own header
// comment documents the exact same trap: an earlier version of that file
// asserted CurrentAnimStateForHarness (ApplyAnimation's own state-SELECTION
// decision — the string it hands to Travel()) and would have PASSED even if
// scenes/Player.tscn's per-move states/transitions were totally broken,
// because that field is just re-proving the already-unit-tested resolver,
// never the .tscn wiring. Reading ActiveAnimNodeForHarness —
// AnimationNodeStateMachinePlayback.GetCurrentNode() via the live
// AnimationTree — asserts what the state machine ACTUALLY did. That is the
// only honest proof scenes/Player.tscn's BehindTheBackStartup/Active/
// Recovery states (and the transitions reaching them) are really wired, not
// just that ResolveStateName's dictionary lookup is correct in isolation
// (already covered by MoveAnimResolverTests).
//
// ── Why Player.tscn instances AND a live ball are both needed ──────────────
// A live AnimationTree only exists on the REAL scenes/Player.tscn (bare
// `new PlayerController()`, as most non-anim harnesses use, has no mesh/
// AnimationTree at all — ApplyAnimation's Travel() call would be a no-op
// against a null tree). But BehindTheBack/EuroStep are DRIBBLE-family committed
// moves: BeginCommittedMove's Held-holder dead-dribble gate (#193) refuses
// them outright unless the ball is actually Dribbling — see
// BehindTheBackTest.cs's "dead-dribble-gate" scenario and its "cannot Begin
// from Held" comment. So this harness needs BOTH pieces at once: two
// Player.tscn instances (for a live AnimationTree) under a BallController
// (for a real Dribbling possession) — bare PlayerControllers would fail to
// even Begin the move; a Ball-less setup (as e.g. MovingCrossoverTest uses to
// isolate pure burst math) would make GetBall() null and the dead-dribble
// gate a no-op, but that also means BeginCommittedMove's ordinary legality
// checks never see a real Dribbling state, which is not what a real client
// ever does for these two moves.
//
// ── Why BehindTheBack (clipped) / EuroStep (unclipped) specifically ────────
// Both gate identically (Dribbling-only, #193) and — the reason they make a
// meaningful control PAIR here — MoveAnimResolver.ClippedMovePrefixes lists
// "behindtheback" (mapping to the real per-move BehindTheBack states
// scenes/Player.tscn has) but does NOT list "eurostep". So scenario 1 proves the
// per-move path is really wired; scenario 2 proves the SAME harness setup,
// unchanged except for which move begins, stays on the generic "Active" node
// and never drifts onto a per-move state that doesn't exist for it (there is
// no "EuroStepActive" node in the tree at all) — which is what makes scenario 1's
// pass meaningful rather than a harness premise that could never have failed.
//
// Scenario 2's move is load-bearing but NOT permanent: it is whichever
// dribble-family move is currently unclipped, and it moved from
// BetweenTheLegs to Spin (#309) to DriveGather (#310) to EuroStep (#311).
// See that scenario's own comment for the three criteria a successor has to
// meet — and note that EuroStep is the LAST real move that meets them.
//
// ── Out of scope ────────────────────────────────────────────────────────────
// Whether BehindTheBackActive{Left,Right}'s clip LOOKS right (correct limbs,
// no foot-sliding, reads as "behind the back") is #279/#173's deferred human
// feel judgment (ADR-0021) — this harness only asserts state-machine
// REACHABILITY, never clip content.
public partial class MoveKindAnimTest : Node
{
    // All production moves are now clipped (#312), so the fallback control must
    // use a synthetic committed move rather than pretending a real move is not.
    private sealed class UnclippedMove : CommittedMove
    {
        public UnclippedMove() : base("__unclipped__", "Unclipped", new MoveFrameData(3, 3, 3, 0)) { }
    }

    private const double TimeoutSeconds = 10.0;
    private const int ArmFrames = 2;          // ticks for TryAssignTipoffHolder to run
    private const int ActionMarginFrames = 3; // ticks to let TryStartDribble's effect settle
    private const int SettleMarginFrames = 5; // ticks after returning to Inactive before the final read

    private string _scenario = "clipped-reaches-permove";

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

    // Latched (event-time, not end-of-run) observations.
    private bool _sawActivePhase;
    private bool _latchedTargetAnimState;
    private string _observedPerMoveState = "(none)";
    private int _returnedInactiveFrame = -1;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "clipped-reaches-permove");
        GD.Print($"[movekind-anim] scenario={_scenario} booting headless…");

        // Real Player.tscn instances (not bare PlayerController) — needed for
        // a live AnimationTree, see file header. Named "1"/"2" so, with no
        // MultiplayerPeer assigned, Godot's OfflineMultiplayerPeer makes
        // unique_id 1 both IsServer and IsLocalPlayer: node "1" runs the full
        // TickServerOwnPlayer -> Move() -> ApplyAnimation chain every tick,
        // same as PivotAnimTest and every offline-server harness in this repo.
        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        var p1 = scene.Instantiate<PlayerController>();
        p1.Name = "1";
        var p2 = scene.Instantiate<PlayerController>();
        p2.Name = "2";

        // Drive each AnimationTree on the PHYSICS step, not the default Idle
        // step. ApplyAnimation calls Travel() from _PhysicsProcess every tick,
        // but under the default Idle callback the tree only WALKS that queued
        // path on idle frames, which do not advance in lockstep with the
        // physics ticks under --headless — so GetCurrentNode() lags several
        // ticks behind the Travel() call and a short committed-move Active
        // window can elapse before the tree ever reaches the per-move Active
        // node (and a legit Startup->Recovery pump-fake shortcut edge then lets
        // the lagging walk skip Active entirely). Physics callback makes tree
        // advancement lockstep with the sim so GetCurrentNode() reflects what
        // was Travelled within the same tick cadence. It does not change WHICH
        // states the graph can reach, only when the reach becomes observable
        // (same reason LocomotionClipTest takes manual control of Advance()).
        //
        // This used to add "production keeps the shipped Idle callback and keeps
        // up at 60fps", framing the override as a harness-only fidelity choice.
        // That is no longer true: #280 set callback_mode_process=0 on
        // scenes/Player.tscn precisely BECAUSE the harness was proving tick
        // alignment under a mode the game did not use. Verified against Godot
        // 4.7.1: PHYSICS=0, IDLE=1, and a fresh AnimationTree reports 1. So this
        // line now agrees with the shipped scene rather than diverging from it.
        // It is kept explicit anyway, matching every sibling anim harness, so a
        // scenario never silently depends on a scene-level default.
        foreach (var p in new[] { p1, p2 })
            p.GetNode<AnimationTree>("AnimationTree").CallbackModeProcess =
                AnimationMixer.AnimationCallbackModeProcess.Physics;

        var players = new Node3D { Name = "Players" };
        players.AddChild(p1);
        players.AddChild(p2);
        _p1 = p1;
        _p2 = p2;

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
            case "clipped-reaches-permove":   TickClippedReachesPerMove();  break;
            case "unclipped-stays-generic":   TickUnclippedStaysGeneric();  break;
            default:
                Fail($"unknown scenario '{_scenario}'.");
                Finish();
                return;
        }

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame}, scenario={_scenario}, step={_step}, " +
                 $"sawActivePhase={_sawActivePhase}, latchedTargetAnimState={_latchedTargetAnimState}, " +
                 $"lastActiveAnimNode={NodeForPeer(_holderId)?.ActiveAnimNodeForHarness}.");
            Finish();
        }
    }

    // ── Scenario: clipped-reaches-permove ───────────────────────────────────
    // BehindTheBack IS in MoveAnimResolver.ClippedMovePrefixes ("behindtheback"
    // -> "BehindTheBack"). Asserts the AnimationTree's state machine really
    // Travels into a "BehindTheBackActive*" state while MovePhase.Active is
    // live (event-time latch, not an end-of-run check — matches PivotAnimTest's
    // discipline), then settles back onto the possession-correct NEUTRAL once
    // the move's full lifecycle returns to Inactive — "Dribble" + the holder's
    // HandSide here (#294 split the single Dribble state into DribbleLeft/
    // DribbleRight), since the scenario starts a live dribble first and
    // BehindTheBack does not end it (see ExpectedNeutralAnimNode; pre-#285 this
    // was always "Locomotion").
    //
    // The trailing wildcard is load-bearing (#281): behind-the-back is now
    // HANDED, so the state is "BehindTheBackActiveLeft"/"...Right" and the bare
    // "BehindTheBackActive" no longer exists at all. The scenario's claim is
    // unchanged — a CLIPPED move must reach its own per-move state rather than
    // the shared generic "Active", which the prefix still excludes.
    private void TickClippedReachesPerMove()
    {
        PlayerController holder;
        switch (_step)
        {
            case Step.AwaitTipoff:
                if (_frame < ArmFrames) return;
                if (_ball.StateMachine.HolderPeerId == 0)
                {
                    Fail("clipped-reaches-permove: tipoff never assigned a holder.");
                    Finish();
                    return;
                }
                _holderId = _ball.StateMachine.HolderPeerId;
                _ball.TryStartDribble(_holderId);
                _step = Step.DriveChecked;
                _stepDeadlineFrame = _frame + ActionMarginFrames;
                return;

            case Step.DriveChecked:
                if (_frame < _stepDeadlineFrame) return;
                if (_ball.State != BallState.Dribbling)
                {
                    Fail($"clipped-reaches-permove: expected TryStartDribble to reach Dribbling " +
                         $"(BehindTheBack cannot Begin from Held, #193); got state={_ball.State}.");
                    Finish();
                    return;
                }
                holder = NodeForPeer(_holderId);
                bool began = holder.BeginBehindTheBackForHarness(1f);
                if (!began)
                {
                    Fail("clipped-reaches-permove: BeginBehindTheBackForHarness returned false " +
                         "— machine was not Inactive or the dead-dribble gate refused it.");
                    Finish();
                    return;
                }
                GD.Print($"[movekind-anim] BehindTheBack begun on holder={_holderId}.");
                _step = Step.Observing;
                return;

            case Step.Observing:
                holder = NodeForPeer(_holderId);
                MovePhase phase = holder.PhaseForHarness;
                string animNode = holder.ActiveAnimNodeForHarness;

                if (phase == MovePhase.Active)
                {
                    _sawActivePhase = true;
                    // (#281) Prefix match, not equality. Behind-the-back is now
                    // HANDED as well as clipped, so its Active state is
                    // "BehindTheBackActiveLeft" or "...Right" and the
                    // unsuffixed "BehindTheBackActive" no longer exists. This
                    // scenario's claim is unchanged and still precisely tested:
                    // a CLIPPED move must reach its own per-move state rather
                    // than the shared generic "Active" — which the prefix
                    // excludes. Which polarity comes up depends on the tipoff's
                    // starting hand and is CrossoverAnimTest/
                    // BehindTheBackAnimTest's business, not this file's.
                    if (animNode.StartsWith("BehindTheBackActive"))
                    {
                        _latchedTargetAnimState = true;
                        _observedPerMoveState = animNode;
                    }
                }

                if (_sawActivePhase && phase == MovePhase.Inactive && _returnedInactiveFrame < 0)
                {
                    _returnedInactiveFrame = _frame;
                }

                if (_returnedInactiveFrame >= 0 && _frame == _returnedInactiveFrame + SettleMarginFrames)
                {
                    VerdictClippedReachesPerMove();
                }
                return;
        }
    }

    private void VerdictClippedReachesPerMove()
    {
        PlayerController holder = NodeForPeer(_holderId);
        string finalAnimNode = holder.ActiveAnimNodeForHarness;
        string expectedNeutral = ExpectedNeutralAnimNode();
        bool settledNeutral = finalAnimNode == expectedNeutral;

        bool pass = _sawActivePhase && _latchedTargetAnimState && settledNeutral;

        if (pass)
        {
            GD.Print($"[movekind-anim] PASS clipped-reaches-permove — the AnimationTree state " +
                     $"machine actually entered \"{_observedPerMoveState}\" (a per-move state, not the " +
                     $"shared generic \"Active\") while MovePhase.Active was live, then settled back onto " +
                     $"\"{expectedNeutral}\" once the move's lifecycle finished.");
        }
        else
        {
            Fail($"clipped-reaches-permove: expected the tree to enter a \"BehindTheBackActive*\" state " +
                 $"during Active and settle on \"{expectedNeutral}\" after; got sawActivePhase={_sawActivePhase}, " +
                 $"latchedTargetAnimState={_latchedTargetAnimState}, finalAnimNode={finalAnimNode}, " +
                 $"ballState={_ball.State}.");
        }
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: unclipped-stays-generic (the control) ────────────────────
    // EuroStep is deliberately NOT in ClippedMovePrefixes — the same gate (#193),
    // the same live-Dribbling setup, but a different move. Asserts the generic
    // "Active" node is actually reached during MovePhase.Active (proving the
    // fallback path itself is live, not merely "nothing broke"), AND that the
    // per-move state name it would have gotten had it wrongly been clipped
    // ("EuroStepActive" — a node that does not exist in the tree at all) is never
    // observed, every single frame of the run. This is the control that makes
    // scenario 1's pass meaningful: it proves the harness COULD have caught a
    // per-move state wrongly appearing.
    //
    // ── This scenario's subject keeps graduating, and #311 moves it for the last time
    // A control whose whole premise is "this move is UNCLIPPED" has a shelf
    // life: it expires the moment that move gets its clip. #309 clipped
    // betweenthelegs, #310 clipped spin, #311 clipped drivegather — each would
    // have turned this green control red, not because anything regressed but
    // because its subject graduated. #310's full local sweep caught exactly
    // that: 232 scenarios passed and this one failed on "SpinActive" at frame
    // 13, which is the control working as designed rather than a defect in it.
    //
    // A successor has to share this scenario's setup EXACTLY, so the only
    // variable between the two scenarios stays "which move began": dribble-
    // family (so the live-Dribbling setup is required, not incidental), gated
    // identically by BeginCommittedMove's #193 dead-dribble list, and still
    // absent from ClippedMovePrefixes.
    //
    // EuroStep is now the ONLY remaining move meeting all three, so it is the
    // successor by exhaustion rather than by preference. #310 explicitly passed
    // it over in favour of DriveGather, and that reservation still stands and is
    // worth carrying forward: EuroStep also carries ADR-0023's rim-range gate on
    // top of the shared dead-dribble gate, so a future tuning of that range
    // could stop the move beginning from this harness's spot and redden this
    // control for a reason that has nothing to do with what it asserts. If that
    // happens the fix is the synthetic id described below — NOT widening the
    // range or moving the actor, either of which would be editing the subject to
    // suit the test.
    //
    // Like DriveGather before it, EuroStep DOES end the dribble (its beat 1 IS
    // the gather). That is fine here and deliberately so: the settle assertion
    // derives its expected state from live ball state rather than hardcoding
    // "Dribble", exactly so it keeps its meaning for such a move — see
    // ExpectedSettledNode's own comment.
    //
    // It goes through DefensiveMoveHarnessSeam's generic BeginMoveForHarness
    // rather than a move-specific seam because no such seam exists and one
    // move-typed passthrough is not worth a new file; that seam reaches the same
    // private BeginCommittedMove, so the production gates still run.
    //
    // When #312 clips the euro-step the dribble family is EXHAUSTED and this
    // control can no longer be expressed with a real move. Do NOT delete it at
    // that point — the fallback branch it guards still exists. Give it a
    // synthetic unclipped moveId instead. (This reasoning goes stale every time
    // a clip lands; check MoveAnimResolver.ClippedMovePrefixes, not this
    // comment.)
    private void TickUnclippedStaysGeneric()
    {
        PlayerController holder;
        switch (_step)
        {
            case Step.AwaitTipoff:
                if (_frame < ArmFrames) return;
                if (_ball.StateMachine.HolderPeerId == 0)
                {
                    Fail("unclipped-stays-generic: tipoff never assigned a holder.");
                    Finish();
                    return;
                }
                _holderId = _ball.StateMachine.HolderPeerId;
                _ball.TryStartDribble(_holderId);
                _step = Step.DriveChecked;
                _stepDeadlineFrame = _frame + ActionMarginFrames;
                return;

            case Step.DriveChecked:
                if (_frame < _stepDeadlineFrame) return;
                if (_ball.State != BallState.Dribbling)
                {
                    Fail($"unclipped-stays-generic: expected TryStartDribble to reach Dribbling " +
                         $"(EuroStep cannot Begin from Held, #193); got state={_ball.State}.");
                    Finish();
                    return;
                }
                holder = NodeForPeer(_holderId);
                // lateralDirection +1 = step right. The sign is inert for this
                // scenario — it shapes the Active-entry burst, not which anim
                // state resolves — but the constructor requires one.
                bool began = holder.BeginMoveForHarness(new UnclippedMove());
                if (!began)
                {
                    Fail("unclipped-stays-generic: BeginMoveForHarness(new EuroStep(1f)) returned false " +
                         "— machine was not Inactive, the dead-dribble gate refused it, or ADR-0023's " +
                         "rim-range gate did. See this scenario's comment: widen nothing, switch to a " +
                         "synthetic unclipped moveId instead.");
                    Finish();
                    return;
                }
                GD.Print($"[movekind-anim] EuroStep begun on holder={_holderId}.");
                _step = Step.Observing;
                return;

            case Step.Observing:
                holder = NodeForPeer(_holderId);
                MovePhase phase = holder.PhaseForHarness;
                string animNode = holder.ActiveAnimNodeForHarness;

                // The control assertion: this per-move state must NEVER
                // appear, at any point in the run — not just during Active.
                // It does not exist as a node in scenes/Player.tscn at all,
                // so this also stands as a sanity check on the harness itself.
                if (animNode == "EuroStepActive")
                {
                    Fail($"unclipped-stays-generic: ActiveAnimNodeForHarness was " +
                         $"\"EuroStepActive\" at frame {_frame} — that state does not exist " +
                         "in scenes/Player.tscn and must never be reachable for an unclipped move.");
                    Finish();
                    return;
                }

                if (phase == MovePhase.Active)
                {
                    _sawActivePhase = true;
                    if (animNode == "Active")
                    {
                        _latchedTargetAnimState = true;
                    }
                }

                if (_sawActivePhase && phase == MovePhase.Inactive && _returnedInactiveFrame < 0)
                {
                    _returnedInactiveFrame = _frame;
                }

                if (_returnedInactiveFrame >= 0 && _frame == _returnedInactiveFrame + SettleMarginFrames)
                {
                    VerdictUnclippedStaysGeneric();
                }
                return;
        }
    }

    private void VerdictUnclippedStaysGeneric()
    {
        PlayerController holder = NodeForPeer(_holderId);
        string finalAnimNode = holder.ActiveAnimNodeForHarness;
        string expectedNeutral = ExpectedNeutralAnimNode();
        bool settledNeutral = finalAnimNode == expectedNeutral;

        bool pass = _sawActivePhase && _latchedTargetAnimState && settledNeutral;

        if (pass)
        {
            GD.Print("[movekind-anim] PASS unclipped-stays-generic — the AnimationTree state " +
                     "machine reached the generic \"Active\" node during MovePhase.Active (never a " +
                     $"per-move state — \"EuroStepActive\" never appeared), then settled back " +
                     $"onto \"{expectedNeutral}\" once the move's lifecycle finished.");
        }
        else
        {
            Fail($"unclipped-stays-generic: expected the tree to enter generic \"Active\" during " +
                 $"Active and settle on \"{expectedNeutral}\" after; got sawActivePhase={_sawActivePhase}, " +
                 $"latchedTargetAnimState={_latchedTargetAnimState}, finalAnimNode={finalAnimNode}, " +
                 $"ballState={_ball.State}.");
        }
        Finish(pass ? 0 : 1);
    }

    private PlayerController NodeForPeer(int peerId) => peerId == 1 ? _p1 : _p2;

    // The neutral display state the holder SHOULD settle onto once the move's
    // lifecycle returns to Inactive.
    //
    // Before #285 this was unconditionally "Locomotion". It is now possession-
    // dependent: a live-dribbling holder settles onto the Dribble stance
    // instead (MoveAnimResolver's Inactive branch). Both scenarios here call
    // TryStartDribble before their move — BehindTheBack and EuroStep cannot Begin
    // from Held (#193).
    //
    // They then diverge, which is exactly the case this helper was written for:
    // BehindTheBack keeps the dribble alive and settles on "Dribble" + the
    // holder's HandSide (#294), while EuroStep CRADLES at Startup-begin (its
    // beat 1 is the gather) and so settles on "Locomotion". Deriving the
    // expectation from live ball state is what lets one assertion cover both
    // without either scenario hardcoding an answer.
    //
    // Derived from live ball state rather than hardcoded to "Dribble" so the
    // assertion keeps its meaning if a scenario is ever pointed at a move that
    // DOES end the dribble (a JumpShot leaves the ball InFlight, so its holder
    // would correctly settle back onto "Locomotion") — and so a bug that
    // silently killed the dribble mid-move would still be caught here rather
    // than absorbed by a loosened "either name is fine" check.
    //
    // (#294) "Dribble" alone is no longer a real state name — the tree now has
    // "DribbleLeft"/"DribbleRight", and the holder's own HandSide (server-
    // authoritative, ADR-0012) decides which. Appending it here keeps this an
    // exact-name check rather than a weakened prefix test, same discipline as
    // DribbleLoopTest's positive sites.
    private string ExpectedNeutralAnimNode() =>
        _ball.State == BallState.Dribbling && _ball.StateMachine.HolderPeerId == _holderId
            ? "Dribble" + NodeForPeer(_holderId).HandSide
            : "Locomotion";

    private void Fail(string message) => GD.PrintErr($"[movekind-anim] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[movekind-anim] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
