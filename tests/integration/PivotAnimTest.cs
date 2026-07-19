using System.Linq;
using Godot;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #242 (the AFK build half of #184):
// proves the AnimationTree's state machine is actually DRIVEN by
// IsPivotingInPlace — that a flick-180 pivot (the same mechanic
// PivotPlantTest already proves at the HeadingMath/Move() level) makes the
// live AnimationTree Travel() into the new "Pivot" state while the flag is
// true, and back to Locomotion once it clears, with a control scenario
// proving ordinary forward locomotion never enters Pivot at all.
//
// This is deliberately a SEPARATE file from PivotPlantTest, not an added
// scenario there: PivotPlantTest instances a bare PlayerController (no mesh/
// AnimationTree — the plant mechanic doesn't need one), where this harness
// needs the REAL Player.tscn (AnimationTreePath wired, a real rigged
// CharacterModel/Skeleton3D) to exercise ApplyAnimation's Travel() call at
// all. Single-concern: one file proves the mechanic's math, this one proves
// the presentation layer reads it correctly (ADR-0016).
//
//   godot --headless --path . res://tests/integration/PivotAnimTest.tscn -- --harness-scenario=pivot-enters-exits
//   godot --headless --path . res://tests/integration/PivotAnimTest.tscn -- --harness-scenario=control-locomotion
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//
// ── Why CurrentAnimStateForHarness, not a live AnimationTree bone-pose read ──
// Spike #87 found that headlessly driving AnimationTree.Advance() and
// sampling rendered bone poses needs a custom MainLoop frame pump — time-
// boxed out as a Tier-2 gap, left to the human's one-time visual confirm.
// This harness sidesteps that gap entirely: it does not need to prove the
// CLIP LOOKS RIGHT (that is #184/#173's irreducible feel judgment, explicitly
// out of scope per #242's acceptance) — only that ApplyAnimation's
// state-SELECTION decision (which the state machine is then handed via
// Travel()) tracks IsPivotingInPlace. CurrentAnimStateForHarness reads that
// decision directly, which is state-checkable today without solving spike
// #87's live-advance gap.
//
// ── Why a single offline instance is enough ─────────────────────────────────
// Same reasoning as PivotPlantTest: with no MultiplayerPeer assigned, Godot's
// OfflineMultiplayerPeer makes unique_id 1 both IsServer and IsLocalPlayer, so
// naming the instanced Player.tscn root "1" dispatches every tick through
// TickServerOwnPlayer — the same Move()/ApplyAnimation call chain a real
// listen-server host runs, with no seam needed for either scenario here.
public partial class PivotAnimTest : Node
{
    private const double TimeoutSeconds = 10.0;

    private string _scenario = "pivot-enters-exits";
    private PlayerController _player;
    private int _frame;
    private double _elapsed;
    private bool _finished;

    // pivot-enters-exits scenario state.
    private int _flickStartFrame = -1;
    private bool _pivotEnteredAnim;
    private bool _pivotEnteredAnimWhileFlagTrue;
    private bool _sawFlagGoFalseAfterPivotAnim;
    private int _postPivotSettleFrame = -1;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "pivot-enters-exits");
        GD.Print($"[pivot-anim] scenario={_scenario} booting headless…");

        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        var inst = scene.Instantiate<PlayerController>();
        inst.Name = "1";
        AddChild(inst);
        _player = inst;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        switch (_scenario)
        {
            case "pivot-enters-exits": TickPivotEntersExits(); break;
            case "control-locomotion": TickControlLocomotion(); break;
            default:
                Fail($"unknown scenario '{_scenario}'.");
                Finish();
                return;
        }

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame} without reaching a verdict.");
            Finish();
        }
    }

    // ── Scenario: ~180° flick → AnimationTree enters/exits Pivot in lockstep
    // with IsPivotingInPlace ───────────────────────────────────────────────
    private void TickPivotEntersExits()
    {
        if (_flickStartFrame < 0)
        {
            _flickStartFrame = _frame;
            Input.ActionPress("move_forward", 1.0f);
            GD.Print($"[pivot-anim] frame {_frame}: pressed move_forward (~180° flick)");
        }

        if (_frame == _flickStartFrame + 1)
        {
            Input.ActionRelease("move_forward");
        }

        bool pivoting = _player.IsPivotingInPlace;
        bool animIsPivot = _player.CurrentAnimStateForHarness == MoveAnimState.Pivot;

        if (animIsPivot)
        {
            _pivotEnteredAnim = true;
            if (pivoting) _pivotEnteredAnimWhileFlagTrue = true;
        }

        if (_pivotEnteredAnim && !pivoting && !animIsPivot && _postPivotSettleFrame < 0)
        {
            // The flag cleared AND the anim state already left Pivot — record
            // the frame so we can confirm it settled on Locomotion (not stuck
            // on some other state) and stayed there.
            _sawFlagGoFalseAfterPivotAnim = true;
            _postPivotSettleFrame = _frame;
        }

        if (_postPivotSettleFrame >= 0 && _frame == _postPivotSettleFrame + 5)
        {
            VerdictPivotEntersExits();
        }
    }

    private void VerdictPivotEntersExits()
    {
        bool animIsPivot = _player.CurrentAnimStateForHarness == MoveAnimState.Pivot;
        bool settledLocomotion = _player.CurrentAnimStateForHarness == MoveAnimState.Locomotion && !animIsPivot;

        bool pass = _pivotEnteredAnim && _pivotEnteredAnimWhileFlagTrue
                    && _sawFlagGoFalseAfterPivotAnim && settledLocomotion;

        if (pass)
        {
            GD.Print("[pivot-anim] PASS pivot-enters-exits — AnimationTree entered Pivot while " +
                     "IsPivotingInPlace was true, then settled back to Locomotion once it cleared.");
        }
        else
        {
            Fail($"pivot-enters-exits expected the anim state to enter Pivot while IsPivotingInPlace " +
                 $"was true, then return to Locomotion; got pivotEnteredAnim={_pivotEnteredAnim}, " +
                 $"pivotEnteredWhileFlagTrue={_pivotEnteredAnimWhileFlagTrue}, " +
                 $"sawFlagGoFalseAfterPivotAnim={_sawFlagGoFalseAfterPivotAnim}, " +
                 $"finalAnimState={_player.CurrentAnimStateForHarness}, " +
                 $"finalIsPivotingInPlace={_player.IsPivotingInPlace}.");
        }
        Finish(pass ? 0 : 1);
    }

    // ── Control scenario: plain forward locomotion never enters Pivot ──────
    private void TickControlLocomotion()
    {
        if (_flickStartFrame < 0)
        {
            _flickStartFrame = _frame;
            // Heading starts at 0 (property default) and forward is already
            // the desiredYaw a bare "move_forward" press produces — i.e. THIS
            // is the forward-ish, no-plant, no-reversal case (unlike
            // pivot-enters-exits' ~180° flick above).
            Input.ActionPress("move_backward", 1.0f);
            GD.Print($"[pivot-anim] frame {_frame}: pressed move_backward (forward-ish, no reversal)");
        }

        if (_player.CurrentAnimStateForHarness == MoveAnimState.Pivot)
        {
            Fail($"control-locomotion: anim state entered Pivot at frame {_frame} during plain " +
                 "forward-ish locomotion with no reversal — Pivot must only ever be reachable via " +
                 "an actual IsPivotingInPlace latch.");
            Finish();
            return;
        }

        if (_frame == _flickStartFrame + 30)
        {
            GD.Print("[pivot-anim] PASS control-locomotion — never entered Pivot over 30 ticks of " +
                     "plain forward-ish locomotion.");
            Finish(0);
        }
    }

    private void Fail(string message) => GD.PrintErr($"[pivot-anim] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[pivot-anim] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
