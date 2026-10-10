using System;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

/// <summary>Real capsules and production paired authority tick, with independently obstructed controls.</summary>
public partial class ProductionContactTest : Node3D
{
    private const float Tolerance = 0.0002f;
    private ContactHarnessSeam _fixture, _control;
    private string _scenario;
    private int _ticks, _moveIndex;
    private float _radius;
    private bool _finished, _testedTarget;
    private double _elapsed;
    private readonly bool[] _phases = new bool[4];
    private CommittedMoveMachine _timeline;
    private BallController _ball, _controlBall;
    private static readonly Func<CommittedMove>[] Moves =
    {
        () => new Crossover(1), () => new BehindTheBack(1), () => new BetweenTheLegs(1),
        () => new Spin(1), () => new Hesitation(), () => new InAndOut(1),
        () => new StepBack(), () => new RetreatDribble(), () => new DriveGather(),
        () => new EuroStep(1), () => new JabStep(), () => new JumpShot(), () => new Layup(),
        () => new StealMove(HandSide.Left, 1), () => new BlockMove(), () => new ContestMove()
    };

    public override void _Ready()
    {
        try
        {
            _scenario = HarnessArgs.ReadArg(OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray(),
                "--harness-scenario", "set-drive");
            Require(new[] { "set-drive", "unset-drive", "disabled-contact", "overlap", "coincident",
                "crossing", "floor", "wall", "corner", "startup", "active", "recovery", "timeline",
                "box-out", "pickup-tie", "solver-mask", "immutable-crossing", "reconcile-overlap" }.Contains(_scenario), "unknown scenario");
            _fixture = CreateFixture("Authority", false);
            _control = CreateFixture("Control", true);
            _radius = Radius(_fixture.First) + Radius(_fixture.Second);
            Require(Mathf.Abs(_radius - 1) < Tolerance, "shipped capsule radius premise changed");
            Reset(_fixture, 3, _scenario == "unset-drive" ? Mathf.Pi : 0, -6);
            Reset(_control, 3, _scenario == "unset-drive" ? Mathf.Pi : 0, -6);
            if (_scenario == "disabled-contact") DisableContact(_fixture);
            if (_scenario is "overlap" or "coincident") Reset(_fixture, _scenario == "overlap" ? .25f : 0, 0, 0);
            if (_scenario is "crossing" or "immutable-crossing" or "startup" or "active" or "recovery")
            {
                Stress(_fixture.First); Stress(_control.First);
                Reset(_fixture, 3, 0, -1200); Reset(_control, 3, 0, -1200);
                if (_scenario is not "crossing" and not "immutable-crossing") BeginMove(() => new DriveGather());
            }
            if (_scenario == "timeline") BeginMove(Moves[0]);
            if (_scenario is "floor" or "wall" or "corner")
            {
                AddWorldBox("Floor", new Vector3(0, -1.25f, 0), new Vector3(30, .5f, 30));
                if (_scenario != "floor")
                {
                    AddWorldBox("Wall", new Vector3(0, 0, -.75f), new Vector3(20, 6, .5f));
                    if (_scenario == "corner") AddWorldBox("Corner", new Vector3(-.75f, 0, 0), new Vector3(.5f, 6, 20));
                    Reset(_fixture, 1.05f, Mathf.Pi, -6);
                }
            }
            if (_scenario is "box-out" or "pickup-tie")
            {
                _ball = AddBall(_fixture); _controlBall = AddBall(_control);
            }
        }
        catch (Exception error) { Finish(false, error.Message); }
    }

    private ContactHarnessSeam CreateFixture(string name, bool control)
    {
        var fixture = new ContactHarnessSeam { Name = name, FirstName = "2", SecondName = "3" };
        AddChild(fixture);
        Require(!fixture.First.IsPhysicsProcessing() && !fixture.Second.IsPhysicsProcessing(), "automatic tick disabled");
        if (control) DisableContact(fixture);
        return fixture;
    }

    private static float Radius(PlayerController player) =>
        ((CapsuleShape3D)player.GetNode<CollisionShape3D>("CollisionShape3D").Shape).Radius;
    private static void DisableContact(ContactHarnessSeam f)
    {
        // Both sides must be absent from collision discovery, as in #356's control.
        // Source: https://docs.godotengine.org/en/4.7/classes/class_collisionobject3d.html#class-collisionobject3d-property-collision-mask
        f.First.CollisionLayer = f.First.CollisionMask = 0;
        f.Second.CollisionLayer = f.Second.CollisionMask = 0;
    }
    private static void Reset(ContactHarnessSeam f, float z, float defenderHeading, float velocity) =>
        f.Reset(new Vector3(0, 0, z), Mathf.Pi, new Vector3(0, 0, velocity), Vector3.Zero, defenderHeading, Vector3.Zero);
    private static void Stress(PlayerController p)
    {
        // Fixture-only swept-crossing stress; production scene tuning remains untouched.
        p.MoveSpeed = p.DriveGatherBurstSpeed = 1200;
        p.Accel = p.Decel = p.DriveGatherDecel = 0;
    }
    private void AddWorldBox(string name, Vector3 position, Vector3 size)
    {
        // Static world remains in the engine solver, unlike the opposing player.
        // Source: https://docs.godotengine.org/en/4.7/classes/class_staticbody3d.html
        var body = new StaticBody3D { Name = name, Position = position, CollisionLayer = 1, CollisionMask = 0 };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        AddChild(body);
    }
    private BallController AddBall(ContactHarnessSeam f)
    {
        var ball = new BallController { Name = "Ball", Players = f.First.GetParent(), Position = new Vector3(0, 0, -.5f) };
        f.AddChild(ball); ball.SetPhysicsProcess(false); ball.SetProcess(false);
        return ball;
    }
    private void BeginMove(Func<CommittedMove> create)
    {
        Require(_fixture.First.BeginProductionContactMoveForHarness(create()), "real BeginCommittedMove rejected fixture move");
        Require(_control.First.BeginProductionContactMoveForHarness(create()), "real control BeginCommittedMove rejected");
        _timeline = new CommittedMoveMachine(); Require(_timeline.Begin(create()), "timeline begin rejected");
        Array.Clear(_phases); _testedTarget = false;
    }

    public override void _Process(double delta)
    {
        _elapsed += delta;
        if (!_finished && _elapsed > 30) Finish(false, "timed out awaiting physics evidence");
    }
    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        try
        {
            Require(Math.Abs(delta - _fixture.FixedDelta) < 1e-6, "fixed physics delta premise");
            if (_scenario == "reconcile-overlap") { ReconcileOverlap(delta); return; }
            if (_scenario == "solver-mask")
            {
                // Deliberately omit the contact sample: the engine must not backstop the kernel.
                _fixture.First.Move(new Vector2(0, -1), delta);
                _ticks++;
                Require(NoPlayerSlide(_fixture.First), "player solver mask bypass failed");
                if (_ticks == 30)
                {
                    Require(Mathf.Abs(_fixture.First.GlobalPosition.Z) < Tolerance,
                        "absent kernel probe did not reach independent free endpoint");
                    Finish(true, "shipped masks leave absent-kernel probe unobstructed");
                }
                return;
            }
            if (_scenario == "immutable-crossing")
            {
                var untouched = _fixture.Second.GlobalPosition;
                var shape = _fixture.Second.GetNode<CollisionShape3D>("CollisionShape3D");
                var capsule = (CapsuleShape3D)shape.Shape;
                // An unset immutable opponent cannot be pushed: absorb its proposed share here too.
                var sample = new ContactOpponent(untouched, Vector3.Zero, Mathf.Pi,
                    capsule.Radius, capsule.Height, shape.Position.Y, 3);
                _fixture.First.Move(new Vector2(0, -1), delta, sample);
                _control.First.Move(new Vector2(0, -1), delta);
                Require(_fixture.Second.GlobalPosition == untouched, "immutable crossing advanced opponent body");
                Require(_control.First.GlobalPosition.Z < 0, "immutable crossing free control did not cross");
                Require(_fixture.First.GlobalPosition.Z >= untouched.Z + _radius - Tolerance,
                    "immutable crossing emerged behind sample");
                Require(NoPlayerSlide(_fixture.First), "immutable crossing delegated to player solver");
                Finish(true, "immutable sample swept crossing and free control");
                return;
            }
            if (_scenario == "pickup-tie") { PickupTie(); return; }
            if (_scenario == "timeline")
            {
                // Reset geometry and momentum, never the machine. Every phase must repair actual overlap.
                Reset(_fixture, .25f, 0, 0); Reset(_control, .25f, 0, 0);
            }
            if (_scenario is "startup" or "active" or "recovery")
            {
                var target = _scenario == "startup" ? MovePhase.Startup : _scenario == "active" ? MovePhase.Active : MovePhase.Recovery;
                // Tick advances the phase before motion: choose the independent next-tick phase.
                _timeline.Tick();
                bool targetTick = _timeline.Phase == target;
                Reset(_fixture, targetTick ? 3 : 100, 0, -1200);
                Reset(_control, targetTick ? 3 : 100, 0, -1200);
                _testedTarget = targetTick;
            }
            int pairBefore = _fixture.First.ProductionContactPairStepsForHarness;
            int aBefore = _fixture.First.ProductionContactMotionStepsForHarness;
            int bBefore = _fixture.Second.ProductionContactMotionStepsForHarness;
            _fixture.First.StepProductionContactPairForHarness(_fixture.Second, delta, new Vector2(0, -1), Vector2.Zero);
            _control.First.StepProductionContactPairForHarness(_control.Second, delta, new Vector2(0, -1), Vector2.Zero);
            _ticks++;
            Require(_fixture.First.ProductionContactPairStepsForHarness == pairBefore + 1
                && _fixture.First.ProductionContactMotionStepsForHarness == aBefore + 1
                && _fixture.Second.ProductionContactMotionStepsForHarness == bBefore + 1, "pair must integrate each body exactly once");
            var state = _fixture.Snapshot;
            bool absorbed = state.HorizontalSeparation >= _radius - Tolerance;
            if (_scenario != "disabled-contact") Require(absorbed, "production contact separation failed");
            Require(NoPlayerSlide(_fixture.First) && NoPlayerSlide(_fixture.Second), "player contact delegated to engine slide solver");

            if (_scenario == "timeline") { Timeline(); return; }
            if (_scenario is "startup" or "active" or "recovery")
            {
                Require(_fixture.First.PhaseForHarness == _timeline.Phase, "stress phase departed independent timeline");
                if (!_testedTarget) return;
                Require(_fixture.First.ProductionContactCommittedForHarness, "committed path observer not reached");
                Require(_control.First.GlobalPosition.Z < 0, "committed crossing control did not cross: invalid premise");
                Require(state.First.Position.Z > state.Second.Position.Z, "committed sweep emerged behind set body");
                Finish(true, "committed phase crossing and independent unobstructed control"); return;
            }
            if (_scenario is "overlap" or "coincident" or "crossing")
            {
                if (_scenario == "crossing")
                {
                    Require(_control.First.GlobalPosition.Z < 0, "highspeed free control must cross");
                    Require(state.First.Position.Z >= state.Second.Position.Z + _radius - Tolerance, "highspeed sweep emerged behind set body");
                }
                Finish(true, "first-tick correction/sweep"); return;
            }
            if (_scenario is "floor" or "wall" or "corner")
            {
                Require(Mathf.Abs(state.First.Position.Y) < Tolerance && Mathf.Abs(state.Second.Position.Y) < Tolerance,
                    "world floor support changed capsule Y");
                VerifyFloorProbe(_fixture.First);
                VerifyFloorProbe(_fixture.Second);
                if (_scenario != "floor")
                {
                    Require(state.Second.Position.Z >= -Tolerance, "unset defender phased through world wall");
                    Require(state.First.Position.Z >= state.Second.Position.Z + _radius - Tolerance,
                        "world-blocked yield broke contact separation");
                    if (_scenario == "corner") Require(state.Second.Position.X >= -Tolerance, "defender phased through corner");
                }
            }
            if (_ticks < 30) return;
            if (_scenario == "disabled-contact")
                Require(!absorbed && Mathf.Abs(state.First.Position.Z) < Tolerance, "disabled contact must fail ordinary absorption gate at free endpoint");
            else if (_scenario == "unset-drive")
                Require(state.Second.Position.Z < -.2f, "unset defender did not yield in driving direction");
            else if (_scenario is "set-drive" or "box-out" or "floor")
            {
                Require(state.Second.Position.DistanceTo(Vector3.Zero) < Tolerance, "set defender displaced");
                Require(state.First.Position.Z <= _radius + Tolerance && 3 - state.First.Position.Z > 1.5f,
                    "set-drive did not meaningfully approach and stop at radius sum");
            }
            Require(Mathf.Abs(_control.First.GlobalPosition.Z) < Tolerance, "unobstructed free endpoint control failed");
            if (_scenario == "box-out")
            {
                Require(_ball.GlobalPosition.DistanceTo(_fixture.Second.GlobalPosition) < _ball.PickupRadius,
                    "box-out blocker outside pickup radius");
                Require(_controlBall.GlobalPosition.DistanceTo(_control.First.GlobalPosition) < _controlBall.PickupRadius,
                    "free driver outside pickup radius");
                // Free driver at Z=0 ties blocker at Z=0; shift ball-side driver by its last free step
                // using one MORE production tick, so distance advantage is physical approach evidence.
                _control.First.StepProductionContactPairForHarness(_control.Second, delta, new Vector2(0,-1), Vector2.Zero);
                Require(_control.First.GlobalPosition.DistanceTo(_controlBall.GlobalPosition)
                    < _control.Second.GlobalPosition.DistanceTo(_controlBall.GlobalPosition), "free control driver must be nearer");
                Require(_ball.ResolveProductionContactRecoveryForHarness() == 3, "box-out did not award nearer blocker");
                Require(_controlBall.ResolveProductionContactRecoveryForHarness() == 2, "unobstructed control did not award nearer driver");
            }
            Finish(true, $"ticks={_ticks} separation={state.HorizontalSeparation:F6}");
        }
        catch (Exception error)
        {
            var state = _fixture.Snapshot;
            Finish(false, $"{error.Message}; tick={_ticks} first={state.First.Position} second={state.Second.Position} "
                + $"control={_control.First.GlobalPosition} phase={_fixture.First.PhaseForHarness}");
        }
    }

    private void Timeline()
    {
        _timeline.Tick();
        var p = _fixture.First;
        Require(p.PhaseForHarness == _timeline.Phase && p.FrameInPhaseForHarness == _timeline.FrameInPhase
            && p.PhaseForHarness == _control.First.PhaseForHarness
            && p.FrameInPhaseForHarness == _control.First.FrameInPhaseForHarness, "contact altered committed frame timeline");
        if (p.PhaseForHarness != MovePhase.Inactive)
        {
            _phases[(int)p.PhaseForHarness] = true;
            Require(p.ProductionContactCommittedForHarness, "committed timeline skipped production contact path");
            return;
        }
        Require(_phases[(int)MovePhase.Startup] && _phases[(int)MovePhase.Active] && _phases[(int)MovePhase.Recovery],
            "timeline omitted a committed phase");
        _moveIndex++;
        if (_moveIndex == Moves.Length) Finish(true, $"all {Moves.Length} shipped moves retain natural phase durations");
        else BeginMove(Moves[_moveIndex]);
    }
    private void PickupTie()
    {
        _ball.GlobalPosition = Vector3.Zero;
        _fixture.Reset(new Vector3(0,0,1), 0, Vector3.Zero, new Vector3(0,0,-1), 0, Vector3.Zero);
        Require(_ball.ResolveProductionContactRecoveryForHarness() == 2, "exact-distance tie must choose lower peer id");
        _fixture.First.GetParent().MoveChild(_fixture.Second, 0);
        Require(_ball.ResolveProductionContactRecoveryForHarness() == 2, "pickup tie depends on child enumeration order");
        Finish(true, "real pickup exact-distance tie stable under reversed enumeration");
    }
    private void ReconcileOverlap(double delta)
    {
        var self = _fixture.First;
        var other = _fixture.Second;
        var display = new Vector3(8, 0, 0);
        other.GlobalPosition = display;
        other.SeedProductionContactRawForHarness(Vector3.Zero, 0);
        Require(self.ProductionContactBufferedInputsForHarness == 0, "reconcile probe requires empty replay buffer");
        self.ReconcileProductionContactEmptyForHarness(delta);
        Require(self.GlobalPosition.IsFinite() && self.Velocity.IsFinite() && float.IsFinite(self.Heading),
            "reconcile correction produced nonfinite state");
        Require(new Vector2(self.GlobalPosition.X, self.GlobalPosition.Z).Length() >= _radius - Tolerance,
            "empty-buffer reconcile did not separate from raw opponent");
        Require(other.GlobalPosition == display, "reconcile mutated opponent display body");
        Require(self.ProductionContactBufferedInputsForHarness == 0, "reconcile changed empty replay buffer");
        other.ClearProductionContactRawForHarness();
        self.ReconcileProductionContactEmptyForHarness(delta);
        Require(self.GlobalPosition.DistanceTo(Vector3.Zero) < Tolerance,
            "unavailable raw sample caused phantom reconcile contact");
        Require(new Vector2(self.GlobalPosition.X, self.GlobalPosition.Z).Length() < _radius - Tolerance,
            "unavailable-sample control did not fail ordinary raw-separation gate");
        Require(other.GlobalPosition == display && self.ProductionContactBufferedInputsForHarness == 0,
            "unavailable-sample control changed opponent or replay buffer");
        Finish(true, "actual empty-buffer reconcile repairs raw overlap; unavailable-sample control stays at authoritative snap");
    }
    private static bool NoPlayerSlide(PlayerController p)
    {
        // Source: https://docs.godotengine.org/en/4.7/classes/class_characterbody3d.html#class-characterbody3d-method-get-slide-collision
        for (int i = 0; i < p.GetSlideCollisionCount(); i++)
            if (p.GetSlideCollision(i).GetCollider() is PlayerController) return false;
        return true;
    }
    private static void VerifyFloorProbe(PlayerController player)
    {
        // TestMove queries the swept body without advancing it. An upward normal proves
        // actual floor support rather than Y invariance in a controller without gravity.
        // Source: https://docs.godotengine.org/en/4.7/classes/class_physicsbody3d.html#class-physicsbody3d-method-test-move
        var support = new KinematicCollision3D();
        Require(player.TestMove(player.GlobalTransform, Vector3.Down * .1f, support),
            "world floor probe did not collide");
        Require(support.GetNormal().Y > .99f && support.GetCollider() is StaticBody3D floor
            && floor.Name == "Floor", "world floor probe lacks upward floor support");
        uint mask = player.CollisionMask;
        try
        {
            player.CollisionMask = 0;
            Require(!player.TestMove(player.GlobalTransform, Vector3.Down * .1f),
                "disabled world mask control still detects floor");
        }
        finally { player.CollisionMask = mask; }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private void Finish(bool passed, string detail)
    {
        if (_finished) return;
        _finished = true;
        GD.Print($"[Harness] {(passed ? "PASS" : "FAIL")} production-contact/{_scenario}: {detail}");
        GetTree().Quit(passed ? 0 : 1);
    }
}
