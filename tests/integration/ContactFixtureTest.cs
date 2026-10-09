using System;
using System.Linq;
using Godot;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

/// <summary>Baseline engine-collision proof and its deliberately non-contact control (#356).</summary>
public partial class ContactFixtureTest : Node3D
{
    private const int TargetTicks = 30;
    private const float GeometryTolerance = 0.01f;
    private ContactHarnessSeam _fixture;
    private string _scenario = "contact";
    private Vector3 _start;
    private float _combinedRadius;
    private float _minimumSeparation = float.PositiveInfinity;
    private bool _finished;
    private double _elapsed;

    public override void _Ready()
    {
        try
        {
            _scenario = HarnessArgs.ReadArg(OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray(),
                "--harness-scenario", "contact");
            Require(_scenario is "contact" or "no-contact", $"unknown scenario '{_scenario}'");
            _fixture = new ContactHarnessSeam { Name = "ContactFixture" };
            AddChild(_fixture);
            _combinedRadius = CapsuleRadius(_fixture.First) + CapsuleRadius(_fixture.Second);
            Require(_fixture.First.MoveSpeed > 0, "driver must have a positive shipped speed");
            // Start at full speed to make the free endpoint an independent distance
            // calculation, unaffected by acceleration. In 30 ticks it reaches Z=0.
            _start = new Vector3(0, 0, (float)(TargetTicks * _fixture.FixedDelta * _fixture.First.MoveSpeed));
            Require(_start.Z > _combinedRadius + GeometryTolerance, "players must begin separated");
            _fixture.Reset(_start, Mathf.Pi, new Vector3(0, 0, -_fixture.First.MoveSpeed),
                Vector3.Zero, 0, Vector3.Zero);
            Require(!_fixture.First.IsPhysicsProcessing() && !_fixture.Second.IsPhysicsProcessing(),
                "players must not automatically double-step");
            if (_scenario == "no-contact")
            {
                // Masks choose what a body scans; layers choose who can detect it.
                // Zero BOTH on BOTH players, so the control cannot collide in either direction.
                // Source: https://docs.godotengine.org/en/4.7/classes/class_collisionobject3d.html#class-collisionobject3d-property-collision-mask
                _fixture.First.CollisionLayer = _fixture.First.CollisionMask = 0;
                _fixture.Second.CollisionLayer = _fixture.Second.CollisionMask = 0;
            }
            else
            {
                Require((_fixture.First.CollisionMask & _fixture.Second.CollisionLayer) != 0
                    && (_fixture.Second.CollisionMask & _fixture.First.CollisionLayer) != 0,
                    "shipped players must mutually detect each other's collision layers");
            }
        }
        catch (Exception exception) { Finish(false, exception.Message); }
    }

    private static float CapsuleRadius(PlayerController player)
    {
        var collision = player.GetNodeOrNull<CollisionShape3D>("CollisionShape3D");
        // Source: https://docs.godotengine.org/en/4.7/classes/class_collisionshape3d.html#class-collisionshape3d-property-disabled
        Require(collision != null && !collision.Disabled && collision.Shape is CapsuleShape3D,
            $"{player.Name} requires an enabled shipped capsule");
        Require(player.Scale.IsEqualApprox(Vector3.One) && collision.Scale.IsEqualApprox(Vector3.One)
            && collision.Rotation.IsEqualApprox(Vector3.Zero), "capsules must be upright with unit scale");
        Require(Mathf.Abs(collision.Position.X) < GeometryTolerance && Mathf.Abs(collision.Position.Z) < GeometryTolerance,
            "capsules must be horizontally centered on their bodies");
        float radius = ((CapsuleShape3D)collision.Shape).Radius;
        // Source: https://docs.godotengine.org/en/4.7/classes/class_capsuleshape3d.html#class-capsuleshape3d-property-radius
        Require(float.IsFinite(radius) && radius > 0, "capsule radius must be positive and finite");
        return radius;
    }

    public override void _Process(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        if (_elapsed > 10) Finish(false, "timed out waiting for fixed physics steps");
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        try
        {
            Require(Math.Abs(delta - _fixture.FixedDelta) < 1e-6, "physics delta must match fixture fixed delta");
            var snapshot = _fixture.Step(new Vector2(0, -1), Vector2.Zero);
            _minimumSeparation = Mathf.Min(_minimumSeparation, snapshot.HorizontalSeparation);
            Require(snapshot.Second.Position.DistanceTo(Vector3.Zero) < GeometryTolerance,
                "stationary blocker must remain at the origin");
            Require(Mathf.Abs(snapshot.First.Position.X) < GeometryTolerance
                && Mathf.Abs(snapshot.First.Position.Y) < GeometryTolerance,
                "driver must not escape laterally or vertically");
            if (_fixture.StepCount < TargetTicks) return;
            Require(_fixture.StepCount == TargetTicks, "must step exactly the target number of physics ticks");
            float advance = _start.Z - snapshot.First.Position.Z;
            Require(advance > (_start.Z - _combinedRadius) * 0.8f, "driver must meaningfully approach the blocker");
            // The SAME geometric predicate is evaluated for the contact and control
            // runs. A disabled solver cannot pass merely because nobody moved.
            bool contactGate = HasContactSeparation(snapshot);
            if (_scenario == "contact")
                Require(contactGate, "contact separation gate failed");
            else
            {
                Require(!contactGate, "no-contact control must fail the ordinary contact separation gate");
                Vector3 freeEndpoint = _start + new Vector3(0, 0,
                    (float)(-TargetTicks * _fixture.FixedDelta * _fixture.First.MoveSpeed));
                Require(snapshot.First.Position.DistanceTo(freeEndpoint) < GeometryTolerance,
                    $"control must reach independently predicted free endpoint {freeEndpoint}");
            }
            Finish(true, $"scenario={_scenario} ticks={_fixture.StepCount} min-separation={_minimumSeparation:F6} "
                + $"radius-sum={_combinedRadius:F6} first={snapshot.First.Position} contact-gate={contactGate}");
        }
        catch (Exception exception) { Finish(false, exception.Message); }
    }

    private bool HasContactSeparation(ContactHarnessSeam.PairSnapshot snapshot) =>
        _minimumSeparation >= _combinedRadius - GeometryTolerance
        && snapshot.First.Position.Z - snapshot.Second.Position.Z >= _combinedRadius - GeometryTolerance
        && snapshot.First.Position.Z - snapshot.Second.Position.Z <= _combinedRadius + 2 * GeometryTolerance;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private void Finish(bool success, string message)
    {
        if (_finished) return;
        _finished = true;
        if (success) GD.Print($"[harness] PASS ContactFixtureTest: {message}");
        else GD.PrintErr($"[harness] FAIL ContactFixtureTest: {message}; ticks={_fixture?.StepCount} min-separation={_minimumSeparation}");
        GetTree().Quit(success ? 0 : 1);
    }
}
