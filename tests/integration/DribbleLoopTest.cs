using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #285: proves the live-dribble neutral
// stance (MoveAnimState.Dribble, PlayerController.DisplayDribbling, the
// scenes/Player.tscn Dribble BlendSpace1D state) is REACHED end-to-end when a
// holder starts a real dribble — and, the load-bearing controls, that it does
// NOT appear for a player without the ball or for a holder whose ball is merely
// Held.
//
//   godot --headless --path . res://tests/integration/DribbleLoopTest.tscn -- --harness-scenario=dribble-entered
//   godot --headless --path . res://tests/integration/DribbleLoopTest.tscn -- --harness-scenario=no-ball-locomotion
//   godot --headless --path . res://tests/integration/DribbleLoopTest.tscn -- --harness-scenario=held-no-dribble
//   godot --headless --path . res://tests/integration/DribbleLoopTest.tscn -- --harness-scenario=move-outranks-dribble
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "dribble-entered".
//
// ── Why ActiveAnimNodeForHarness, not the resolver's own decision (#257) ────
// Same discipline as ReboundGrabTest/MoveKindAnimTest/PivotAnimTest: reading
// AnimationNodeStateMachinePlayback.GetCurrentNode() (via the live AnimationTree)
// asserts what the state machine ACTUALLY did, not merely that Resolve returned
// MoveAnimState.Dribble in isolation — MoveAnimResolverTests already pins that,
// and it would keep passing even if Player.tscn had no Dribble state at all.
// Travel() to a missing/misnamed state only LOGS, so this is the only honest
// proof the .tscn state and its transitions are wired. Post-#294 split, that
// count is 72 transition edges: 35 per polarity (DribbleLeft and DribbleRight
// each mirror the single pre-split Dribble state's 35-edge set) plus the
// DribbleLeft<->DribbleRight pair connecting the two polarities directly.
//
// ── Why the controls carry the real weight here ─────────────────────────────
// The whole design decision #285 records (ADR-0014, self-resolved on the issue)
// is that the stance is gated on BallState.Dribbling specifically and NOT on
// "holds the ball": showing a live-dribble loop once the dribble is dead
// advertises a drive the holder can no longer legally make — an actively FALSE
// read, worse than no signal. "held-no-dribble" is the assertion that encodes
// that call, and it is only meaningful next to "dribble-entered" proving the
// SAME rig does reach Dribble when the ball really is bouncing. Each control
// therefore also asserts its own premise (the ball really is Held; the control
// player really has no possession) rather than passing on a rig that could
// never have fired.
//
// ── Why Player.tscn instances (a live AnimationTree) + a live ball ──────────
// A live AnimationTree only exists on the REAL scenes/Player.tscn (bare
// `new PlayerController()` has no mesh/tree — Travel() would no-op). The ball is
// a real BallController so the tipoff and TryStartDribble run their genuine
// possession transitions, including the DeadDribbleRule gate.
//
// ── Out of scope ────────────────────────────────────────────────────────────
// Whether the dribble clip LOOKS right is #173's deferred human feel judgment
// (ADR-0021) — this harness only asserts state-machine reachability, never clip
// content. Clip track binding, loop mode, and the Dribble blend surface's #287
// corridor behaviour are LocomotionClipTest's job.
public partial class DribbleLoopTest : Node
{
    private const double TimeoutSeconds = 12.0;
    private const int ArmFrames = 2;            // ticks for TryAssignTipoffHolder to run
    private const int ObserveFrames = 45;       // ticks a control watches before rendering a verdict

    private static readonly Vector3 HolderSpot = new(0f, 0f, 0f);
    private static readonly Vector3 FarSpot = new(12f, 0f, 12f); // keeps the other player out of PickupRadius

    private string _scenario = "dribble-entered";

    private BallController _ball;
    private PlayerController _p1;
    private PlayerController _p2;

    private int _frame;
    private double _elapsed;
    private bool _finished;

    private int _holderId;

    private enum Step { AwaitTipoff, Act, Observe }
    private Step _step = Step.AwaitTipoff;
    private int _stepDeadlineFrame;

    // Latched (event-time) observations.
    private bool _sawDribbleState;      // the ball genuinely reached BallState.Dribbling
    private bool _sawDribbleAnim;       // the tree entered "Dribble" + the holder's authoritative HandSide (#294)
    private bool _sawLocomotionOnOther; // the off-ball control player was observed on "Locomotion"
    private bool _sawHeldThroughout;    // held-no-dribble: the ball stayed Held for the window
    private bool _sawMoveStartup;       // move-outranks-dribble: the tree left Dribble for the move's Startup

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "dribble-entered");
        GD.Print($"[dribble-loop] scenario={_scenario} booting headless…");

        // Real Player.tscn instances (live AnimationTree), named "1"/"2" so the
        // OfflineMultiplayerPeer makes unique_id 1 both IsServer and
        // IsLocalPlayer (the full TickServerOwnPlayer -> ApplyAnimation chain
        // runs every tick), same as ReboundGrabTest/MoveKindAnimTest.
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

        switch (_scenario)
        {
            case "dribble-entered":       TickDribbleEntered();      break;
            case "no-ball-locomotion":    TickNoBallLocomotion();    break;
            case "held-no-dribble":       TickHeldNoDribble();       break;
            case "move-outranks-dribble": TickMoveOutranksDribble(); break;
            default:
                Fail($"unknown scenario '{_scenario}'.");
                Finish();
                return;
        }

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame}, scenario={_scenario}, step={_step}, " +
                 $"ballState={_ball?.State}, sawDribbleState={_sawDribbleState}, " +
                 $"sawDribbleAnim={_sawDribbleAnim}, lastAnimNode={NodeForPeer(_holderId)?.ActiveAnimNodeForHarness}.");
            Finish();
        }
    }

    // Resolves the tipoff holder and separates the two players, so the holder is
    // unambiguous and the other player is nowhere near a pickup.
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
        NodeForPeer(_holderId).GlobalPosition = HolderSpot;
        OtherNode(_holderId).GlobalPosition = FarSpot;
        return true;
    }

    // ── Scenario: dribble-entered (positive) ────────────────────────────────
    private void TickDribbleEntered()
    {
        switch (_step)
        {
            case Step.AwaitTipoff:
                if (!AwaitTipoffThenPosition()) return;
                _step = Step.Act;
                _stepDeadlineFrame = _frame + 2;
                return;

            case Step.Act:
                if (_frame < _stepDeadlineFrame) return;
                // The REAL production entry point (PlayerController's
                // CheckAutoStartDribble calls exactly this), so the
                // DeadDribbleRule gate and the Held->Dribbling transition both
                // run genuinely rather than being simulated.
                _ball.TryStartDribble(_holderId);
                _step = Step.Observe;
                _stepDeadlineFrame = _frame + ObserveFrames;
                return;

            case Step.Observe:
                if (_ball.State == BallState.Dribbling) _sawDribbleState = true;
                // (#294) Exact-handed assertion, not a "Dribble" prefix: the point
                // of the split is proving the tree entered the SPECIFIC polarity
                // the authoritative HandSide demands, not merely some dribble
                // stance. A bug that entered the wrong-handed state would pass a
                // prefix check but must fail this one.
                var holder = NodeForPeer(_holderId);
                if (holder.ActiveAnimNodeForHarness == "Dribble" + holder.HandSide) _sawDribbleAnim = true;
                if (_sawDribbleState && _sawDribbleAnim) VerdictDribbleEntered();
                else if (_frame >= _stepDeadlineFrame) VerdictDribbleEntered();
                return;
        }
    }

    private void VerdictDribbleEntered()
    {
        bool pass = _sawDribbleState && _sawDribbleAnim;
        if (pass)
            GD.Print("[dribble-loop] PASS dribble-entered — the ball reached BallState.Dribbling and the " +
                     "holder's AnimationTree entered \"Dribble\" + their authoritative HandSide (the .tscn states " +
                     "and their transitions are live).");
        else
            Fail($"dribble-entered: expected the ball to reach Dribbling and the tree to enter \"Dribble\" + the " +
                 $"holder's HandSide; got sawDribbleState={_sawDribbleState}, sawDribbleAnim={_sawDribbleAnim}, " +
                 $"ballState={_ball.State}, handSide={NodeForPeer(_holderId).HandSide}, " +
                 $"lastAnimNode={NodeForPeer(_holderId).ActiveAnimNodeForHarness}.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: no-ball-locomotion (control) ──────────────────────────────
    // The off-ball player must stay on "Locomotion" for the whole run WHILE the
    // holder is genuinely dribbling — so this is a co-occurring control, not a
    // separate run that could pass because nothing happened at all.
    private void TickNoBallLocomotion()
    {
        var other = _holderId == 0 ? null : OtherNode(_holderId);
        // (#294) Prefix test, not exact-handed: the claim here is "no dribble
        // stance of EITHER polarity", so StartsWith("Dribble") is the correct
        // (and stronger) check — no other state in the tree begins with
        // "Dribble", so the prefix is exact. Using the exact-handed form here
        // would let a bug that entered the WRONG polarity slip past this control.
        if (other != null && other.ActiveAnimNodeForHarness.StartsWith("Dribble"))
        {
            Fail($"no-ball-locomotion: the player WITHOUT the ball entered \"{other.ActiveAnimNodeForHarness}\" at " +
                 $"frame {_frame} — the stance must be gated on being the holder, not merely on the ball existing.");
            Finish();
            return;
        }

        switch (_step)
        {
            case Step.AwaitTipoff:
                if (!AwaitTipoffThenPosition()) return;
                _step = Step.Act;
                _stepDeadlineFrame = _frame + 2;
                return;

            case Step.Act:
                if (_frame < _stepDeadlineFrame) return;
                _ball.TryStartDribble(_holderId);
                _step = Step.Observe;
                _stepDeadlineFrame = _frame + ObserveFrames;
                return;

            case Step.Observe:
                if (_ball.State == BallState.Dribbling) _sawDribbleState = true;
                // (#294) Exact-handed, same reasoning as the dribble-entered site:
                // this is the holder's own positive, so it must prove the SPECIFIC
                // polarity HandSide demands, not merely "some Dribble state".
                var holder = NodeForPeer(_holderId);
                if (holder.ActiveAnimNodeForHarness == "Dribble" + holder.HandSide) _sawDribbleAnim = true;
                if (other.ActiveAnimNodeForHarness == "Locomotion") _sawLocomotionOnOther = true;
                if (_frame >= _stepDeadlineFrame) VerdictNoBallLocomotion();
                return;
        }
    }

    private void VerdictNoBallLocomotion()
    {
        // Non-vacuous: the holder MUST have genuinely reached the Dribble stance
        // in this same run, else "the other player stayed on Locomotion" proves
        // nothing (a rig where nobody ever dribbles trivially satisfies it).
        bool pass = _sawDribbleState && _sawDribbleAnim && _sawLocomotionOnOther;
        if (pass)
            GD.Print("[dribble-loop] PASS no-ball-locomotion — while the holder was live-dribbling and showing " +
                     "\"Dribble\" + their HandSide, the off-ball player stayed on \"Locomotion\" and never entered " +
                     "any \"Dribble*\" state.");
        else
            Fail($"no-ball-locomotion: expected the holder to reach \"Dribble\" + their HandSide AND the off-ball " +
                 $"player to sit on \"Locomotion\"; got sawDribbleState={_sawDribbleState}, holderSawDribble={_sawDribbleAnim}, " +
                 $"otherSawLocomotion={_sawLocomotionOnOther}, otherAnimNode={OtherNode(_holderId).ActiveAnimNodeForHarness}. " +
                 "Without the holder's positive, this control would be vacuous, so it fails rather than passes.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: held-no-dribble (control) ─────────────────────────────────
    // The ADR-0014 design call #285 recorded, made assertable: a holder whose
    // ball is HELD (a cradle / dead dribble) keeps Locomotion. TryStartDribble
    // is deliberately never called, so the tipoff possession stays Held.
    private void TickHeldNoDribble()
    {
        // (#294) Prefix test: the claim is "no dribble stance of EITHER polarity"
        // while the ball is Held, so StartsWith("Dribble") is correct here too —
        // no other state begins with "Dribble", so the prefix is exact, and it
        // stays a control against BOTH DribbleLeft and DribbleRight rather than
        // just the one polarity HandSide happens to be defaulted to.
        if (_holderId != 0 && NodeForPeer(_holderId).ActiveAnimNodeForHarness.StartsWith("Dribble"))
        {
            Fail($"held-no-dribble: \"{NodeForPeer(_holderId).ActiveAnimNodeForHarness}\" appeared at frame {_frame} " +
                 $"while the ball was {_ball.State} — the stance must be gated on BallState.Dribbling, not on " +
                 "holding the ball. Showing a live-dribble loop on a Held/dead ball advertises a drive the holder " +
                 "cannot legally make.");
            Finish();
            return;
        }

        switch (_step)
        {
            case Step.AwaitTipoff:
                if (!AwaitTipoffThenPosition()) return;
                _step = Step.Observe;
                _stepDeadlineFrame = _frame + ObserveFrames;
                _sawHeldThroughout = true;
                return;

            case Step.Observe:
                // Premise check, every frame: this control only means anything
                // while the ball really is Held on this player.
                if (_ball.State != BallState.Held || _ball.StateMachine.HolderPeerId != _holderId)
                    _sawHeldThroughout = false;
                if (NodeForPeer(_holderId).ActiveAnimNodeForHarness == "Locomotion")
                    _sawLocomotionOnOther = true; // reused latch: "the holder was seen on Locomotion"
                if (_frame >= _stepDeadlineFrame) VerdictHeldNoDribble();
                return;
        }
    }

    private void VerdictHeldNoDribble()
    {
        bool pass = _sawHeldThroughout && _sawLocomotionOnOther;
        if (pass)
            GD.Print("[dribble-loop] PASS held-no-dribble — the holder kept a HELD ball for the whole window, " +
                     "displayed \"Locomotion\", and never entered \"Dribble\" (the stance is gated on " +
                     "BallState.Dribbling, not on possession).");
        else
            Fail($"held-no-dribble: expected the ball to stay Held on the holder for the whole window AND the " +
                 $"holder to display \"Locomotion\"; got heldThroughout={_sawHeldThroughout}, " +
                 $"sawLocomotion={_sawLocomotionOnOther}, ballState={_ball.State}, " +
                 $"holder={_ball.StateMachine.HolderPeerId}, animNode={NodeForPeer(_holderId).ActiveAnimNodeForHarness}. " +
                 "If the premise broke, 'no Dribble' proves nothing, so this fails rather than passes.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: move-outranks-dribble (precedence) ────────────────────────
    // A committed move ALWAYS beats the neutral stance (MoveAnimResolver only
    // consults isDribbling during MovePhase.Inactive). Proves the .tscn actually
    // carries the Dribble -> <move>Startup edge — without it, Travel() would
    // route through Locomotion or no-op, which is precisely the silent failure
    // a uniform transition mirror exists to prevent.
    private void TickMoveOutranksDribble()
    {
        switch (_step)
        {
            case Step.AwaitTipoff:
                if (!AwaitTipoffThenPosition()) return;
                _step = Step.Act;
                _stepDeadlineFrame = _frame + 2;
                return;

            case Step.Act:
                if (_frame < _stepDeadlineFrame) return;
                _ball.TryStartDribble(_holderId);
                if (_ball.State == BallState.Dribbling) _sawDribbleState = true;
                // (#294) Exact-handed, same reasoning as the other positives:
                // this proves the specific polarity HandSide demands actually
                // played before the move's Startup takes over.
                var holder = NodeForPeer(_holderId);
                if (holder.ActiveAnimNodeForHarness == "Dribble" + holder.HandSide) _sawDribbleAnim = true;
                // Only begin the move once the stance is genuinely on screen —
                // otherwise "the move won" would be trivially true.
                if (!_sawDribbleState || !_sawDribbleAnim) return;
                if (!NodeForPeer(_holderId).BeginJumpShotForHarness())
                {
                    Fail("move-outranks-dribble: BeginJumpShotForHarness returned false — the holder's machine " +
                         "was not Inactive at begin.");
                    Finish();
                    return;
                }
                _step = Step.Observe;
                _stepDeadlineFrame = _frame + ObserveFrames;
                return;

            case Step.Observe:
                {
                    string animNode = NodeForPeer(_holderId).ActiveAnimNodeForHarness;
                    // "jumpshot" is in MoveAnimResolver's clipped-move table, so
                    // the resolved state name is the per-move one (#277).
                    if (animNode == "JumpshotStartup") _sawMoveStartup = true;
                    if (_sawMoveStartup || _frame >= _stepDeadlineFrame) VerdictMoveOutranksDribble();
                }
                return;
        }
    }

    private void VerdictMoveOutranksDribble()
    {
        bool pass = _sawDribbleState && _sawDribbleAnim && _sawMoveStartup;
        if (pass)
            GD.Print("[dribble-loop] PASS move-outranks-dribble — the tree was showing \"Dribble\" + the holder's " +
                     "HandSide and a committed JumpShot moved it straight to \"JumpshotStartup\" (the handed Dribble " +
                     "-> per-move Startup edge is wired, and the neutral stance never overrides a move in flight).");
        else
            Fail($"move-outranks-dribble: expected \"Dribble\" + HandSide then \"JumpshotStartup\"; got " +
                 $"sawDribbleState={_sawDribbleState}, sawDribbleAnim={_sawDribbleAnim}, " +
                 $"sawMoveStartup={_sawMoveStartup}, lastAnimNode={NodeForPeer(_holderId).ActiveAnimNodeForHarness}.");
        Finish(pass ? 0 : 1);
    }

    private PlayerController NodeForPeer(int peerId) => peerId == 1 ? _p1 : _p2;
    private PlayerController OtherNode(int peerId) => peerId == 1 ? _p2 : _p1;

    private void Fail(string message) => GD.PrintErr($"[dribble-loop] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[dribble-loop] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
