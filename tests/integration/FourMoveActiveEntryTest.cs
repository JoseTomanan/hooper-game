using System;
using System.Linq;
using Godot;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// One scenario per process. Existing math owns numerical correctness; these
// literal expectations pin production dispatch, tunables and event timing.
public partial class FourMoveActiveEntryTest : Node
{
    private const float Epsilon = 0.00001f;
    private const double TimeoutSeconds = 5;
    private sealed record Spec(CommittedMove Move, HandSide InitialHand, float Heading,
        Vector3 InitialVelocity, Vector2 Exit, float Gather, HandSide EntryHand,
        Func<Vector3, float, Vector2, Vector3> Compose, string Boundary = "");

    private string _scenario;
    private Spec _spec;
    private PlayerController _player;
    private ulong _started;
    private double _elapsed;
    private int _ticks, _startupTicks, _activeTicks, _laterActiveTicks, _recoveryTicks, _entries;
    private bool _finished;
    private Vector3 _previousVelocity, _entryVelocity, _expectedVelocity;
    private HandSide _entryHand, _expectedHand;

    public override void _Ready()
    {
        _started = Time.GetTicksMsec();
        var args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "crossover-neutral");
        _spec = Scenario(_scenario);
        if (!Require(_spec != null, "unknown scenario")) return;

        // Lower physics priority runs first; observe after the player's real tick.
        // https://docs.godotengine.org/en/4.7/classes/class_node.html#class-node-property-process-physics-priority
        ProcessPhysicsPriority = 100;
        _player = new PlayerController
        {
            Name = "2", ProcessPhysicsPriority = 0,
            BurstSpeed = 7.25f, ForwardBurstScale = 8.75f, ExitDeadzone = .12f,
            BehindTheBackBurstSpeed = 4.25f, BehindTheBackForwardBurstScale = 5.75f,
            BehindTheBackExitConeDegrees = 31f,
            BetweenTheLegsBurstSpeed = 6.25f, BetweenTheLegsForwardBurstScale = 7.75f,
            BetweenTheLegsExitConeDegrees = 57f,
            InAndOutBurstSpeed = 2.25f, InAndOutForwardBurstScale = 3.75f,
            GatherDecel = 2f, BehindTheBackGatherDecel = 3f,
            BetweenTheLegsGatherDecel = 4f, Decel = 6f,
        };
        AddChild(_player);
        _player.SetHeadingForHarness(_spec.Heading);
        _player.SetHandSideForHarness(_spec.InitialHand);
        _player.Velocity = _spec.InitialVelocity;
        _player.SetPendingRawStickForHarness(_spec.Exit);
        _previousVelocity = _expectedVelocity = _spec.InitialVelocity;
        _expectedHand = _spec.InitialHand;
        if (!Require(_player.BeginFourMoveEntryForHarness(_spec.Move), "production begin rejected")) return;
        if (!Require(_player.PhaseForHarness == MovePhase.Startup
            && _player.FrameInPhaseForHarness == 0 && !_player.FourMoveJustEnteredActiveForHarness,
            "begin must expose Startup frame 0 without Active entry")) return;
        if (!Require(ReferenceEquals(_player.FourMoveCurrentMoveForHarness, _spec.Move), "begin changed move identity")) return;
        Require(Near(_player.Velocity, _spec.InitialVelocity) && _player.HandSide == _spec.InitialHand
            && _player.Heading == _spec.Heading, "begin changed velocity, hand or heading");
    }

    public override void _Process(double delta)
    {
        if (!_finished && Time.GetTicksMsec() - _started > TimeoutSeconds * 1000)
            Require(false, "wall timeout");
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _ticks++;
        _elapsed += delta;
        if (!Require(_ticks <= 300 && _elapsed <= TimeoutSeconds, "physics timeout")) return;
        if (!Require(Math.Abs(delta - 1.0 / Engine.PhysicsTicksPerSecond) <= 1e-8, "unexpected fixed delta")) return;
        if (!Require(_player.Heading == _spec.Heading, "committed heading changed")) return;
        var phase = _player.PhaseForHarness;
        int frame = _player.FrameInPhaseForHarness;
        bool entered = _player.FourMoveJustEnteredActiveForHarness;
        if (entered) _entries++;
        if (phase != MovePhase.Inactive
            && !Require(ReferenceEquals(_player.FourMoveCurrentMoveForHarness, _spec.Move), "move identity changed")) return;

        switch (phase)
        {
            case MovePhase.Startup:
                _expectedVelocity = _previousVelocity.MoveToward(Vector3.Zero, _spec.Gather * (float)delta);
                _expectedHand = _spec.InitialHand;
                if (!Require(_activeTicks == 0 && frame == _startupTicks + 1 && !entered,
                    "Startup timeline/entry flag changed")) return;
                if (!Require(Near(_player.Velocity, _expectedVelocity) && _player.HandSide == _expectedHand,
                    "Startup gather or hand changed")) return;
                _startupTicks++;
                break;

            case MovePhase.Active:
                _expectedHand = _spec.EntryHand;
                if (_activeTicks == 0)
                {
                    if (!Require(frame == 0 && entered && _entries == 1
                        && _startupTicks == _spec.Move.FrameData.StartupFrames - 1,
                        "first Active must be the single entry after all Startup ticks")) return;
                    if (_spec.InitialVelocity.LengthSquared() > 0
                        && !Require(_previousVelocity.Length() > 0, "moving premise lost all gather momentum")) return;
                    _expectedVelocity = _spec.Compose(_previousVelocity, _spec.Heading, _spec.Exit);
                    if (!Require((_expectedVelocity - _previousVelocity).Length() > .1f,
                        "scenario must discriminate entry from no burst")) return;
                    if (!Require(Near(_player.Velocity, _expectedVelocity) && _player.HandSide == _expectedHand,
                        "entry composition or event-time hand differs")) return;
                    _entryVelocity = _player.Velocity;
                    _entryHand = _player.HandSide;

                    Vector2 changedExit = _spec.Exit.Length() <= .12f
                        ? Stick(_spec.Heading, 1f, 0f) : -_spec.Exit;
                    Vector3 changedComposition = _spec.Compose(_entryVelocity, _spec.Heading, changedExit);
                    if (!Require((changedComposition - _entryVelocity).Length() > .1f,
                        "changed pending exit must discriminate recomposition")) return;
                    _player.SetPendingRawStickForHarness(changedExit);
                }
                else
                {
                    _expectedVelocity = _entryVelocity;
                    if (!Require(frame == _activeTicks && !entered && _entries == 1,
                        "later Active replayed entry or changed frame")) return;
                    if (!Require(Near(_player.Velocity, _entryVelocity) && _player.HandSide == _entryHand,
                        "later Active changed latched entry velocity or hand")) return;
                    _laterActiveTicks++;
                }
                _activeTicks++;
                break;

            case MovePhase.Recovery:
                _expectedVelocity = _previousVelocity.MoveToward(Vector3.Zero, 6f * (float)delta);
                _expectedHand = _entryHand;
                if (!Require(_activeTicks == _spec.Move.FrameData.ActiveFrames && _laterActiveTicks >= 1
                    && frame == _recoveryTicks && !entered && _entries == 1,
                    "Recovery timeline or entry count changed")) return;
                if (!Require(Near(_player.Velocity, _expectedVelocity) && _player.HandSide == _entryHand,
                    "Recovery deceleration or hand differs")) return;
                if (_recoveryTicks == 0 && !Require(_player.Velocity.Length() > 0
                    && _player.Velocity.Length() < _entryVelocity.Length(),
                    "first Recovery tick must measurably slow a nonzero burst")) return;
                _recoveryTicks++;
                break;

            case MovePhase.Inactive:
                if (!Require(_recoveryTicks == _spec.Move.FrameData.RecoveryFrames
                    && _activeTicks == _spec.Move.FrameData.ActiveFrames && _entries == 1 && !entered
                    && _player.FourMoveCurrentMoveForHarness == null && _player.HandSide == _entryHand,
                    "incomplete lifecycle or repeated entry")) return;
                Finish(true, "complete lifecycle");
                return;
            default:
                Require(false, "unexpected phase");
                return;
        }
        _previousVelocity = _player.Velocity;
    }

    private bool Require(bool pass, string reason)
    {
        if (!pass) Finish(false, reason);
        return pass;
    }

    private void Finish(bool pass, string reason)
    {
        if (_finished) return;
        _finished = true;
        string detail = $"[four-move-entry] {(pass ? "PASS" : "FAIL")} scenario={_scenario} "
            + $"phase={_player?.PhaseForHarness} frame={_player?.FrameInPhaseForHarness} "
            + $"startup={_startupTicks} entries={_entries} active={_activeTicks} later={_laterActiveTicks} recovery={_recoveryTicks} "
            + $"expectedVelocity={_expectedVelocity} actualVelocity={_player?.Velocity} "
            + $"expectedHand={_expectedHand} actualHand={_player?.HandSide} entryHand={_entryHand} "
            + $"boundary={_spec?.Boundary} reason={reason}";
        if (pass) GD.Print(detail); else GD.PrintErr(detail);
        GetTree().Quit(pass ? 0 : 1);
    }

    private static bool Near(Vector3 actual, Vector3 expected) => actual.DistanceTo(expected) <= Epsilon;
    private static Vector2 Stick(float heading, float forward, float lateral) =>
        new Vector2(MathF.Sin(heading), MathF.Cos(heading)) * forward
        + new Vector2(-MathF.Cos(heading), MathF.Sin(heading)) * lateral;
    private static Vector3 Motion(float heading, float lateral)
    {
        Vector2 v = Stick(heading, 3.5f, lateral);
        return new Vector3(v.X, 0, v.Y);
    }
    private static Vector2 ConeExit(float heading, float signedAngle) =>
        Stick(heading, MathF.Cos(signedAngle), MathF.Sin(signedAngle));

    private static Spec Scenario(string name) => name switch
    {
        "crossover-neutral" => new(new Crossover(-.35f), HandSide.Right, 0f, Vector3.Zero,
            Vector2.Zero, 2f, HandSide.Left,
            (v, h, e) => CrossoverBurstMath.ComposeCrossoverActiveVelocity(v, h, -1, e, 7.25f, 8.75f, .12f)),
        "crossover-moving" => new(new Crossover(.7f), HandSide.Left, .7f, Motion(.7f, .4f),
            Vector2.Zero, 2f, HandSide.Right,
            (v, h, e) => CrossoverBurstMath.ComposeCrossoverActiveVelocity(v, h, 1, e, 7.25f, 8.75f, .12f)),
        "crossover-forward" => new(new Crossover(-.35f), HandSide.Right, -1.2f, Motion(-1.2f, -.4f),
            Stick(-1.2f, 1f, 0f), 2f, HandSide.Left,
            (v, h, e) => CrossoverBurstMath.ComposeCrossoverActiveVelocity(v, h, -1, e, 7.25f, 8.75f, .12f)),
        "crossover-backward" => new(new Crossover(.7f), HandSide.Left, 0f, Vector3.Zero,
            Stick(0f, -1f, 0f), 2f, HandSide.Right,
            (v, h, e) => CrossoverBurstMath.ComposeCrossoverActiveVelocity(v, h, 1, e, 7.25f, 8.75f, .12f)),
        "behind-neutral" => new(new BehindTheBack(.7f), HandSide.Left, .7f, Vector3.Zero,
            Vector2.Zero, 3f, HandSide.Right,
            (v, h, e) => CrossoverBurstMath.ComposeActiveVelocity(v, h, 1, e, 4.25f, 5.75f, .12f, Mathf.DegToRad(31f))),
        "behind-lateral" => new(new BehindTheBack(-.35f), HandSide.Right, -1.2f, Motion(-1.2f, -.4f),
            Stick(-1.2f, 0f, 1f), 3f, HandSide.Left,
            (v, h, e) => CrossoverBurstMath.ComposeActiveVelocity(v, h, -1, e, 4.25f, 5.75f, .12f, Mathf.DegToRad(31f))),
        "behind-cone-edge" => new(new BehindTheBack(.7f), HandSide.Left, .7f, Motion(.7f, .4f),
            ConeExit(.7f, Mathf.DegToRad(31f) + .01f), 3f, HandSide.Right,
            (v, h, e) => CrossoverBurstMath.ComposeActiveVelocity(v, h, 1, e, 4.25f, 5.75f, .12f, Mathf.DegToRad(31f)), "outside:31deg+.01rad"),
        "behind-backward" => new(new BehindTheBack(-.35f), HandSide.Right, 0f, Vector3.Zero,
            Stick(0f, -1f, 0f), 3f, HandSide.Left,
            (v, h, e) => CrossoverBurstMath.ComposeActiveVelocity(v, h, -1, e, 4.25f, 5.75f, .12f, Mathf.DegToRad(31f))),
        "between-neutral" => new(new BetweenTheLegs(.7f), HandSide.Left, -1.2f, Vector3.Zero,
            Vector2.Zero, 4f, HandSide.Right,
            (v, h, e) => CrossoverBurstMath.ComposeActiveVelocity(v, h, 1, e, 6.25f, 7.75f, .12f, Mathf.DegToRad(57f))),
        "between-forward" => new(new BetweenTheLegs(-.35f), HandSide.Right, .7f, Motion(.7f, -.4f),
            Stick(.7f, 1f, 0f), 4f, HandSide.Left,
            (v, h, e) => CrossoverBurstMath.ComposeActiveVelocity(v, h, -1, e, 6.25f, 7.75f, .12f, Mathf.DegToRad(57f))),
        "between-lateral" => new(new BetweenTheLegs(.7f), HandSide.Left, -1.2f, Motion(-1.2f, .4f),
            Stick(-1.2f, 0f, -1f), 4f, HandSide.Right,
            (v, h, e) => CrossoverBurstMath.ComposeActiveVelocity(v, h, 1, e, 6.25f, 7.75f, .12f, Mathf.DegToRad(57f))),
        "between-cone-edge" => new(new BetweenTheLegs(-.35f), HandSide.Right, .7f, Motion(.7f, -.4f),
            ConeExit(.7f, -(Mathf.DegToRad(57f) - .01f)), 4f, HandSide.Left,
            (v, h, e) => CrossoverBurstMath.ComposeActiveVelocity(v, h, -1, e, 6.25f, 7.75f, .12f, Mathf.DegToRad(57f)), "inside:-(57deg-.01rad)"),
        "inout-neutral" => new(new InAndOut(.7f), HandSide.Left, -1.2f, Vector3.Zero,
            Vector2.Zero, 2f, HandSide.Left,
            (v, h, e) => CrossoverBurstMath.ComposeActiveVelocity(v, h, -1, e, 2.25f, 3.75f, .12f, Mathf.DegToRad(180f))),
        "inout-right-neutral" => new(new InAndOut(-.35f), HandSide.Right, 0f, Vector3.Zero,
            Vector2.Zero, 2f, HandSide.Right,
            (v, h, e) => CrossoverBurstMath.ComposeActiveVelocity(v, h, 1, e, 2.25f, 3.75f, .12f, Mathf.DegToRad(180f))),
        "inout-forward" => new(new InAndOut(-.35f), HandSide.Right, -1.2f, Motion(-1.2f, -.4f),
            Stick(-1.2f, 1f, 0f), 2f, HandSide.Right,
            (v, h, e) => CrossoverBurstMath.ComposeActiveVelocity(v, h, 1, e, 2.25f, 3.75f, .12f, Mathf.DegToRad(180f))),
        "inout-backward" => new(new InAndOut(.7f), HandSide.Left, 0f, Vector3.Zero,
            Stick(0f, -1f, 0f), 2f, HandSide.Left,
            (v, h, e) => CrossoverBurstMath.ComposeActiveVelocity(v, h, -1, e, 2.25f, 3.75f, .12f, Mathf.DegToRad(180f))),
        "crossover-exit-below-deadzone" => new(new Crossover(-.35f), HandSide.Right, .7f, Vector3.Zero,
            Stick(.7f, .11f, 0f), 2f, HandSide.Left,
            (v, h, e) => CrossoverBurstMath.ComposeCrossoverActiveVelocity(v, h, -1, e, 7.25f, 8.75f, .12f)),
        "crossover-exit-above-deadzone" => new(new Crossover(.7f), HandSide.Left, -1.2f, Vector3.Zero,
            Stick(-1.2f, .13f, 0f), 2f, HandSide.Right,
            (v, h, e) => CrossoverBurstMath.ComposeCrossoverActiveVelocity(v, h, 1, e, 7.25f, 8.75f, .12f)),
        _ => null,
    };
}
