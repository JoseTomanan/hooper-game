using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

/// <summary>
/// #415: production Main/Player scene and real keyboard InputMap reproduction.
/// Baseline records the pre-fix presentation while asserting the gameplay premises;
/// held additionally requires the actual Held playback and its bound idle/moving clips.
/// This single-process fixture proves local gameplay, not RPC/remote presentation.
/// </summary>
public partial class HeldPossessionTest : Node
{
    private BallController _ball;
    private PlayerController _holder, _other;
    private AnimationTree _tree;
    private string _scenario;
    private int _tick, _stage, _stageTick;
    private bool _finished, _stageSawMove, _sawStartup, _sawActive, _sawRecovery, _sawFlight;
    private bool _heldIdle, _heldMoving, _freshHeld, _stepBackBurst;
    private Key _horizontal;
    private HandSide _tapStartHand;
    private float _tapNearestForward = float.PositiveInfinity;
    private Vector3 _heldMoveOrigin;
    private float _heldMoveHeading;
    private string _heldMoveAction = "";
    private readonly Dictionary<Key, int> _keys = new();
    private readonly List<string> _failures = new();
    private static readonly string[] StageNames = {
        "fresh-held", "start-dribble", "live-dribble", "short-k-behindtheback", "q-horizontal-first",
        "q-horizontal-second", "live-crossover", "live-hesitation", "held-k-stepback", "spent-idle",
        "spent-moving", "spent-settle", "refused-crossover", "refused-hesitation", "legal-shot",
        "new-possession", "new-drive", "reset-live-dribble" };

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "held");
        if (_scenario != "held" && _scenario != "baseline") { Finish("unknown scenario"); return; }
        var main = GetNode<Node3D>("Main");
        var players = main.GetNode<Node3D>("Players");
        _ball = main.GetNode<BallController>("Ball");
        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        _holder = scene.Instantiate<PlayerController>();
        _holder.Name = "1";
        _holder.Position = new Vector3(0, 0, 7);
        _other = scene.Instantiate<PlayerController>();
        _other.Name = "2";
        _other.Position = new Vector3(5, 0, 7);
        players.AddChild(_holder);
        players.AddChild(_other);
        _tree = _holder.GetNode<AnimationTree>("AnimationTree");
        // Match existing phase harnesses: make actual playback observable in physics lockstep.
        // https://docs.godotengine.org/en/4.7/classes/class_animationmixer.html#class-animationmixer-property-callback-mode-process
        _tree.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Physics;
        _other.GetNode<AnimationTree>("AnimationTree").CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Physics;
        ConfigureCapture(args, main);
        if (_finished) return;
        GD.Print($"[held-possession] boot scenario={_scenario} scene=Main.tscn input=physical-key-events local-peer=1");
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _tick++;
        if (_tick > 1400) { Finish("timeout"); return; }
        if (_tick < 5) return;
        if (_stageTick == 0) BeginStage();
        _stageTick++;
        Observe();
        if (_finished) return;
        switch (_stage)
        {
            case 0:
                if (_stageTick == 10 || _stageTick == 58) KeyEvent(Key.K, false);
                if (_stageTick == 40) KeyEvent(Key.K, true);
                if (_stageTick == 100)
                {
                    Require(!_stageSawMove && Held(false), "fresh Held refuses K tap and sustained K StepBack");
                    Advance();
                }
                break;
            case 1:
                if (_stageTick == 16) { Move(false); Advance(); }
                break;
            case 2:
                if (_stageTick == 30) { Require(LiveDribble(), "movement starts live handed dribble"); Advance(); }
                break;
            case 3:
                if (_stageTick == 10 || _stageTick == 75) KeyEvent(Key.K, false);
                if (_stageTick == 65)
                {
                    RequireMove("behindtheback");
                    Require(_tapNearestForward < -0.23f, "first K tap puts the full ball visibly behind the root plane");
                    Require(LiveDribble() && _holder.HandSide != _tapStartHand, "K tap switches to the opposite hand");
                    _observedIds.Clear(); _stageSawMove = false;
                    _tapNearestForward = float.PositiveInfinity;
                    KeyEvent(Key.K, true);
                }
                if (_stageTick == 130)
                {
                    RequireMove("behindtheback");
                    Require(_tapNearestForward < -0.23f, "opposite-hand K tap retains the full-ball rear margin");
                    Require(LiveDribble() && _holder.HandSide == _tapStartHand, "repressed K tap switches back from the other hand");
                    _observedIds.Clear(); _stageSawMove = false;
                    KeyEvent(Key.Q, true); KeyEvent(Key.K, true);
                }
                if (_stageTick == 140) KeyEvent(Key.K, false);
                if (_stageTick == 143) KeyEvent(Key.Q, false);
                if (_stageTick == 195) { RequireMove("retreatdribble"); Require(LiveDribble(), "Q+K tap retains live retreat dribble"); Advance(); }
                break;
            case 4:
            case 5:
                if (_stageTick == 10) { KeyEvent(_horizontal, false); KeyEvent(Key.Q, false); }
                if (_stageTick == 75) { RequireMove("behindtheback"); Require(LiveDribble(), "Q+horizontal preserves live dribble"); Advance(); }
                break;
            case 6:
            case 7:
                if (_stageTick == 10) KeyEvent(_horizontal, false);
                if (_stageTick == 75) { RequireMove(_stage == 6 ? "crossover" : "hesitation"); Require(LiveDribble(), "positive horizontal input control remains live"); Advance(); }
                break;
            case 8:
                if (_stageTick == 18) KeyEvent(Key.K, false);
                if (_stageTick == 85)
                {
                    RequireMove("stepback");
                    Require(_sawStartup && _sawActive && _sawRecovery, "StepBack runs all committed phases");
                    Require(_stepBackBurst, "live StepBack retains its committed Active separation burst");
                    Require(Held(true), "StepBack gathers into spent Held");
                    Advance();
                }
                break;
            case 9:
                if (_stageTick == 20) { Require(Held(true), "idle spent Held remains spent"); Advance(); }
                break;
            case 10:
                if (_stageTick == 40)
                {
                    Require(Held(true), "movement cannot restart spent dribble");
                    Require(MathF.Abs(MathF.Sin(_holder.Heading - _heldMoveHeading)) > 0.1f, "gathered holder can still pivot heading");
                    Input.ActionRelease(_heldMoveAction); _heldMoveAction = "";
                    Advance();
                }
                break;
            case 11:
                if (_stageTick == 10 || _stageTick == 58) KeyEvent(Key.K, false);
                if (_stageTick == 40) KeyEvent(Key.K, true);
                if (_stageTick == 90) { Require(!_stageSawMove && Held(true), "spent Held refuses K tap and repeated StepBack"); Advance(); }
                break;
            case 12:
            case 13:
                if (_stageTick == 10) KeyEvent(_horizontal, false);
                if (_stageTick == 35)
                {
                    Require(!_stageSawMove && Held(true), "spent dribble refuses " + (_stage == 12 ? "crossover" : "hesitation"));
                    Advance();
                }
                break;
            case 14:
                if (_stageTick == 2) KeyEvent(Key.Space, false);
                if (_stageTick == 65)
                {
                    Require(_observedIds.Contains("jumpshot") && _sawFlight, "legal shot after gather begins JumpShot and releases InFlight");
                    Advance();
                }
                break;
            case 15:
                if (_stageTick == 20) { Require(Held(false), "AwardPossession resets spent-dribble flag"); Advance(); }
                break;
            case 16:
                if (_stageTick == 16) { Move(false); Advance(); }
                break;
            case 17:
                if (_stageTick == 30)
                {
                    Require(LiveDribble(), "new possession restores legal movement-started dribble");
                    if (_scenario == "held") Require(_freshHeld && _heldIdle && _heldMoving, "actual Held idle/moving playback and bound clip endpoints");
                    Finish("");
                }
                break;
        }
    }

    private void BeginStage()
    {
        _stageSawMove = false;
        _sawStartup = _sawActive = _sawRecovery = false;
        switch (_stage)
        {
            case 0: case 11: KeyEvent(Key.K, true); break;
            case 1: case 16: Move(true); break;
            case 3: _tapStartHand = _holder.HandSide; KeyEvent(Key.K, true); break;
            case 8: KeyEvent(Key.K, true); break;
            case 10:
                _heldMoveHeading = _holder.Heading;
                var forward = HeadingMath.Forward(_heldMoveHeading);
                _heldMoveAction = MathF.Abs(forward.X) < MathF.Abs(forward.Y) ? "move_right" : "move_forward";
                Input.ActionPress(_heldMoveAction);
                break;
            case 4: case 5:
                _horizontal = EmptyHandKey(); KeyEvent(Key.Q, true); KeyEvent(_horizontal, true); break;
            case 6: case 12:
                _horizontal = EmptyHandKey(); KeyEvent(_horizontal, true); break;
            case 7: case 13:
                _horizontal = _holder.HandSide == HandSide.Left ? Key.J : Key.L; KeyEvent(_horizontal, true); break;
            case 14: KeyEvent(Key.Space, true); break;
            case 15:
                // Existing seam calls the genuine AwardPossession transition, including HasDribbled reset.
                _ball.PostAwardForHarness(1); break;
        }
    }

    private void Observe()
    {
        var (phase, _) = _holder.DisplayMove();
        string id = _holder.DisplayMoveId();
        if (_stage == 3 && id == "behindtheback" && _ball.SweepActiveForHarness)
        {
            var forward = HeadingMath.Forward(_holder.Heading);
            var offset = _ball.GlobalPosition - _holder.GlobalPosition;
            _tapNearestForward = MathF.Min(_tapNearestForward, offset.X * forward.X + offset.Z * forward.Y + _ball.BallRadius);
        }
        if (phase != MovePhase.Inactive) _observedIds.Add(id);
        if (phase != MovePhase.Inactive)
        {
            _stageSawMove = true;
            _sawStartup |= phase == MovePhase.Startup;
            _sawActive |= phase == MovePhase.Active;
            _sawRecovery |= phase == MovePhase.Recovery;
        }
        if (_stage == 14 && _ball.State == BallState.InFlight) _sawFlight = true;
        string current = _holder.ActiveAnimNodeForHarness;
        string clips = BoundClips(current);
        float speed = new Vector2(_holder.DisplayVelocity.X, _holder.DisplayVelocity.Z).Length();
        string suffix = _holder.HandSide.ToString().ToLowerInvariant();
        bool heldPlayback = current == "Held" + _holder.HandSide && clips == $"held/idle{suffix}|held/moving{suffix}" &&
            HeldEndpointsExist(current, suffix);
        var root = _tree.TreeRoot as AnimationNodeStateMachine;
        float blend = root != null && root.HasNode("Held" + _holder.HandSide)
            ? _tree.Get("parameters/Held" + _holder.HandSide + "/blend_position").AsSingle() : 0f;
        bool correctBlend = MathF.Abs(blend - MathF.Min(speed, _holder.MoveSpeed)) < 0.02f;
        if (_scenario == "held" && (_stage == 0 || _stage == 9 || _stage == 10) && _stageTick >= 5)
            Require(heldPlayback && correctBlend, "settled neutral Held window consistently uses the handed carry and speed blend");
        if (_stage == 0 && _stageTick >= 5 && Held(false) && heldPlayback && correctBlend && speed < 0.02f) _freshHeld = true;
        if (_stage == 9 && _stageTick >= 5 && Held(true) && heldPlayback && correctBlend && speed < 0.02f) _heldIdle = true;
        if (_stage == 10 && _stageTick >= 5)
        {
            Require(speed < 0.02f, "gathered holder cannot walk despite held movement input");
            if (_stageTick == 5) _heldMoveOrigin = _holder.GlobalPosition;
            // Keep the input pressed throughout this 35-tick observation. A
            // zero velocity report alone would miss direct position writes.
            Require((_holder.GlobalPosition - _heldMoveOrigin).Length() < 0.01f, "gathered holder position remains fixed while movement is held");
            if (Held(true) && heldPlayback && correctBlend && speed < 0.02f) _heldMoving = true;
        }
        if (_stage == 8 && phase != MovePhase.Inactive)
        {
            Require(id == "stepback" && current == "StepBack" + phase &&
                clips == "locomotion/stepback" + phase.ToString().ToLowerInvariant(),
                "actual committed StepBack phase and clip outrank neutral Held");
            if (phase == MovePhase.Active && speed > _holder.StepBackBurstSpeed * 0.9f) _stepBackBurst = true;
        }
        if (_stageTick >= 5 && _other.ActiveAnimNodeForHarness != "Locomotion")
            Require(false, "off-ball player uses locomotion");
        string keys = string.Join(",", _keys.Select(pair => $"{pair.Key}:{_tick - pair.Value}ticks"));
        GD.Print($"[held-possession] trace tick={_tick} stage={StageNames[_stage]} input=[{keys}] move={id} phase={phase} ball={_ball.State} holder={_ball.StateMachine.HolderPeerId} spent={_ball.HasDribbled} node={current} clips={clips} speed={speed:F3} heldBlend={blend:F3} hand={_holder.HandSide}");
    }

    private bool HeldEndpointsExist(string state, string suffix)
    {
        var machine = _tree.TreeRoot as AnimationNodeStateMachine;
        if (machine == null || !machine.HasNode(state) || machine.GetNode(state) is not AnimationNodeBlendSpace1D blend ||
            blend.GetBlendPointCount() != 2 || MathF.Abs(blend.GetBlendPointPosition(0)) > 0.001f ||
            MathF.Abs(blend.GetBlendPointPosition(1) - _holder.MoveSpeed) > 0.001f) return false;
        var player = _holder.GetNode<AnimationPlayer>("AnimationPlayer");
        return player.HasAnimation("held/idle" + suffix) && player.HasAnimation("held/moving" + suffix);
    }

    private string BoundClips(string current)
    {
        // Read the resource actually selected by GetCurrentNode, never a resolver-requested string.
        // https://docs.godotengine.org/en/4.7/classes/class_animationnodestatemachine.html#class-animationnodestatemachine-method-get-node
        var root = _tree.TreeRoot as AnimationNodeStateMachine;
        if (root == null || !root.HasNode(current)) return "<missing>";
        var node = root.GetNode(current);
        if (node is AnimationNodeAnimation animation) return animation.Animation;
        if (node is AnimationNodeBlendSpace1D blend)
        {
            var names = new List<string>();
            for (int i = 0; i < blend.GetBlendPointCount(); i++)
                names.Add(blend.GetBlendPointNode(i) is AnimationNodeAnimation clip ? clip.Animation.ToString() : "<non-animation>");
            return string.Join("|", names);
        }
        return "<non-animation>";
    }

    private bool Held(bool spent) => _ball.State == BallState.Held && _ball.StateMachine.HolderPeerId == 1 && _ball.HasDribbled == spent;
    private bool LiveDribble() => _ball.State == BallState.Dribbling && _ball.StateMachine.HolderPeerId == 1 &&
        _holder.ActiveAnimNodeForHarness == "Dribble" + _holder.HandSide &&
        BoundClips(_holder.ActiveAnimNodeForHarness) == (_holder.HandSide == HandSide.Left ? "locomotion/dribbleidleleft|locomotion/dribblemoveleft" : "locomotion/dribbleidleright|locomotion/dribblemoveright");
    private Key EmptyHandKey() => _holder.HandSide == HandSide.Left ? Key.L : Key.J;
    private void RequireMove(string id)
    {
        Require(_stageSawMove, "production input begins " + id);
        Require(_observedIds.Contains(id), "recognized exact move " + id);
    }
    private readonly HashSet<string> _observedIds = new();
    private void Advance()
    {
        _stage++; _stageTick = 0; _observedIds.Clear();
    }
    private void Require(bool condition, string message)
    {
        if (!condition && !_failures.Contains(message)) { _failures.Add(message); GD.PrintErr("[held-possession] assertion: " + message); }
    }
    private void Move(bool pressed)
    {
        // Documented state injection reaches production Input.GetVector/CheckAutoStartDribble.
        // https://docs.godotengine.org/en/4.7/classes/class_input.html#class-input-method-action-press
        if (pressed) Input.ActionPress("move_forward"); else Input.ActionRelease("move_forward");
        GD.Print($"[held-possession] movement tick={_tick} pressed={pressed}");
    }
    private void KeyEvent(Key key, bool pressed)
    {
        // ParseInputEvent feeds the shipped physical-key InputMap and recognizer, including K vs Q+horizontal.
        // https://docs.godotengine.org/en/4.7/classes/class_input.html#class-input-method-parse-input-event
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = pressed });
        if (pressed) _keys[key] = _tick; else _keys.Remove(key);
        GD.Print($"[held-possession] key tick={_tick} key={key} pressed={pressed}");
    }
    private void Finish(string failure)
    {
        if (_finished) return;
        CompleteCapture();
        _finished = true;
        if (failure.Length > 0) _failures.Add(failure);
        foreach (var key in _keys.Keys.ToArray()) KeyEvent(key, false);
        if (_heldMoveAction.Length > 0) Input.ActionRelease(_heldMoveAction);
        Move(false);
        GD.Print($"[held-possession] {(_failures.Count == 0 ? "PASS" : "FAIL")} scenario={_scenario} errors={string.Join("; ", _failures)} freshHeld={_freshHeld} spentIdle={_heldIdle} spentMoving={_heldMoving}");
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }
}
