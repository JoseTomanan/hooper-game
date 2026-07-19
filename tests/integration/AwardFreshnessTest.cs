using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #224 (ADR-0016): stale-input
// auto-dribble on possession award (Race 1 of #207). TickServerRemotePlayer
// calls CheckAutoStartDribble(_pendingInput) on every non-Active tick — but
// _pendingInput can be up to ~1 RTT stale (SubmitInput is UnreliableOrdered;
// a dropped release-packet widens the window). If a possession award lands
// while the server still holds a stale NONZERO stick for the new holder, the
// pre-fix code auto-fired Held->Dribbling against the player's actual
// (released) intent, silently costing them the triple-threat beat.
//
//   godot --headless --path . res://tests/integration/AwardFreshnessTest.tscn -- --harness-scenario=stale-blocked-fresh-works
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//
// ── Why a single offline instance is the server ───────────────────────────
// Same reasoning as TripleThreatTest/StealTurnoverTest: OfflineMultiplayerPeer
// makes BallController.IsServer true and player "1" IsLocalPlayer
// (TickServerOwnPlayer); player "2" is IsServer && !IsLocalPlayer
// (TickServerRemotePlayer) — the ONE role the #224 fix's gate applies to.
//
// ── Scenario "stale-blocked-fresh-works": the paired-control AC verbatim ──
//   1. await-tipoff:     tipoff lands peer "1" in a fresh Held possession
//                        (default child order — unchanged from TripleThreatTest).
//   2. prime-stale:      peer "2" (NOT the holder) is given a stale nonzero
//                        cached input via SetPendingInputForHarness — this
//                        simulates "peer 2 pushed the stick a while ago, then
//                        released it, but the release packet never arrived
//                        (or hasn't yet)" — production code has no way to
//                        distinguish this from a genuinely still-held stick
//                        without the #224 fix.
//   3. trigger-award:    peer "1" (the current holder) is walked OOB — the
//                        ALREADY-PROVEN OobTurnoverTest/TripleThreatTest
//                        "reset-on-turnover" path — which awards possession
//                        to peer "2" via the REAL BallController.AwardPossession,
//                        with peer 2's stale _pendingInput/_serverAckedSeq
//                        untouched since step 2 (the award-time stamp
//                        captures exactly that stale value as the baseline —
//                        see PlayerController._awardStampSeq's doc).
//   4. stale-blocked:    the core #224 assertion — several ticks after the
//                        award, the ball must still be Held, NOT auto-fired
//                        into Dribbling, despite peer 2's cached input
//                        remaining nonzero the entire time.
//   5. fresh-input:      a genuinely NEWER packet is simulated (a higher seq,
//                        same nonzero input — "the still-held stick's next
//                        real packet finally arrived").
//   6. fresh-works:      the paired control — the held drive DOES start,
//                        proving the fix is a freshness gate, not a
//                        blanket suppression of remote auto-dribble.
public partial class AwardFreshnessTest : Node
{
    private const double TimeoutSeconds = 10.0;
    private const int ArmFrames = 2;          // ticks for TryAssignTipoffHolder to run
    private const int ActionMarginFrames = 4; // ticks to let an action's effect settle

    // Same OOB point TripleThreatTest/OobTurnoverTest use (beyond CourtMax.X,
    // well inside the far-backstop walls) — reused rather than re-derived so
    // this test's OOB step exercises the identical, already-proven geometry.
    private static readonly Vector3 OobPositiveX = new(9.0f, 0f, 5f);

    private string _scenario = "stale-blocked-fresh-works";

    private BallController _ball;
    private PlayerController _p1;
    private PlayerController _p2;

    private int _frame;
    private double _elapsed;
    private bool _finished;

    private enum Step
    {
        AwaitTipoff, StaleInputPrimed, AwardTriggered, StaleBlockedChecked,
        FreshInputIssued, FreshWorksChecked
    }

    private Step _step = Step.AwaitTipoff;
    private int _stepDeadlineFrame;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "stale-blocked-fresh-works");
        GD.Print($"[award-freshness] scenario={_scenario} booting headless…");

        // Same code-built-tree pattern as TripleThreatTest/StealTurnoverTest —
        // avoids fragile .tscn ext_resource/uid wiring for a throwaway harness.
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

        switch (_step)
        {
            case Step.AwaitTipoff:
                if (_frame < ArmFrames) break;
                if (_ball.StateMachine.HolderPeerId != 1)
                {
                    Fail($"await-tipoff: expected the tipoff to award peer 1 (default child order); got holder={_ball.StateMachine.HolderPeerId}.");
                    Finish();
                    return;
                }
                if (_ball.State != BallState.Held || _ball.HasDribbled)
                {
                    Fail($"await-tipoff: expected a fresh live Held possession; got state={_ball.State}, HasDribbled={_ball.HasDribbled}.");
                    Finish();
                    return;
                }
                GD.Print("[award-freshness] PASS await-tipoff — holder=1, state=Held, HasDribbled=false.");

                // Step 2: prime peer 2's cached input as stale-nonzero BEFORE
                // any award — this seq (5) becomes the award-time baseline in
                // step 3, matching the real race (a stick pushed a while ago,
                // its release never confirmed).
                _p2.SetPendingInputForHarness(5, new Vector2(0f, 1f));
                _step = Step.StaleInputPrimed;
                _stepDeadlineFrame = _frame + ActionMarginFrames;
                break;

            case Step.StaleInputPrimed:
                if (_frame < _stepDeadlineFrame) break;
                GD.Print("[award-freshness] PASS prime-stale — peer 2 carries a stale nonzero cached input, not yet the holder.");

                // Step 3: walk the CURRENT holder (peer 1) OOB — the
                // already-proven OOB turnover path awards possession to
                // peer 2 via the REAL AwardPossession, with peer 2's stale
                // input untouched since step 2.
                _p1.GlobalPosition = OobPositiveX;
                _step = Step.AwardTriggered;
                _stepDeadlineFrame = _frame + ActionMarginFrames;
                break;

            case Step.AwardTriggered:
                if (_frame < _stepDeadlineFrame) break;
                if (_ball.StateMachine.HolderPeerId != 2)
                {
                    Fail($"trigger-award: expected the OOB turnover to award peer 2; got holder={_ball.StateMachine.HolderPeerId}.");
                    Finish();
                    return;
                }
                GD.Print("[award-freshness] PASS trigger-award — possession awarded to peer 2 (stale-input holder).");

                _step = Step.StaleBlockedChecked;
                // Extra settle window: the core assertion needs several MORE
                // ticks to elapse with the stale input still cached, to prove
                // this isn't a one-tick fluke — not just the same margin used
                // to observe the award itself.
                _stepDeadlineFrame = _frame + ActionMarginFrames;
                break;

            case Step.StaleBlockedChecked:
                if (_frame < _stepDeadlineFrame) break;
                // The core #224 assertion: despite peer 2's cached input
                // being nonzero this entire time, the freshness gate must
                // have kept CheckAutoStartDribble from ever firing on it —
                // the ball stays Held.
                if (_ball.State != BallState.Held)
                {
                    Fail($"stale-blocked: expected the ball to stay Held (stale input must NOT auto-dribble); got state={_ball.State}.");
                    Finish();
                    return;
                }
                GD.Print("[award-freshness] PASS stale-blocked — stale nonzero input did NOT auto-transition Held -> Dribbling.");

                // Step 5: simulate a genuinely FRESH post-award packet — a
                // higher seq than the award-time baseline, same nonzero
                // input ("the still-held stick's next real packet finally
                // arrived").
                _p2.SetPendingInputForHarness(6, new Vector2(0f, 1f));
                _step = Step.FreshInputIssued;
                _stepDeadlineFrame = _frame + ActionMarginFrames;
                break;

            case Step.FreshInputIssued:
                if (_frame < _stepDeadlineFrame) break;
                GD.Print("[award-freshness] PASS fresh-input — a newer-seq nonzero packet was delivered for peer 2.");

                _step = Step.FreshWorksChecked;
                _stepDeadlineFrame = _frame + ActionMarginFrames;
                break;

            case Step.FreshWorksChecked:
                if (_frame < _stepDeadlineFrame) break;
                // The paired control: a genuinely held drive still starts
                // once a fresh post-award packet confirms it — the fix is a
                // freshness gate, not a blanket suppression.
                if (_ball.State != BallState.Dribbling)
                {
                    Fail($"fresh-works: expected the fresh post-award input to start Dribbling; got state={_ball.State}.");
                    Finish();
                    return;
                }
                GD.Print("[award-freshness] PASS fresh-works — a fresh post-award input DID start the dribble (control).");
                GD.Print("[award-freshness] RESULT: PASS (exit 0)");
                Finish(0);
                return;
        }

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame}, step {_step}.");
            Finish();
        }
    }

    private void Fail(string message) => GD.PrintErr($"[award-freshness] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        if (code != 0)
            GD.Print($"[award-freshness] RESULT: FAIL (exit {code})");
        GetTree().Quit(code);
    }
}
