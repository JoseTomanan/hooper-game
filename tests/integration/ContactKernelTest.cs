using System;
using System.Linq;
using Godot;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

/// <summary>
/// #369 KERNEL adapter proof on #356's scene-loaded players. Does not call
/// Move/MoveAndSlide and therefore proves neither production wiring nor Jolt.
/// Each physics tick prescribes velocities, freezes BOTH snapshots, calculates
/// both corrections, then writes endpoints. The ordinary fixture is unchanged.
/// </summary>
public partial class ContactKernelTest : Node3D
{
    private const float Epsilon = 0.0002f;
    private ContactHarnessSeam _fixture;
    private string _scenario;
    private int _ticks;
    private bool _finished;
    private float _radiusSum;
    private float _minimumSeparation = float.PositiveInfinity;
    private double _elapsed;

    public override void _Ready()
    {
        try
        {
            _scenario = HarnessArgs.ReadArg(OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray(),
                "--harness-scenario", "set-drive");
            Require(_scenario is "set-drive" or "unset-drive" or "disabled-kernel"
                or "overlap" or "coincident" or "crossing" or "dimensions", "unknown scenario");
            _fixture = new ContactHarnessSeam { Name = "KernelFixture" };
            AddChild(_fixture);
            _fixture.Reset(new Vector3(0, 0, 3), MathF.PI, Vector3.Zero,
                Vector3.Zero, _scenario == "unset-drive" ? MathF.PI : 0, Vector3.Zero);
            Require(!_fixture.First.IsPhysicsProcessing() && !_fixture.Second.IsPhysicsProcessing(),
                "fixture players must not automatically step");
            if (_scenario is "overlap" or "coincident")
                _fixture.Reset(new Vector3(0, 0, _scenario == "overlap" ? 0.25f : 0), MathF.PI,
                    Vector3.Zero, Vector3.Zero, 0, Vector3.Zero);
            if (_scenario == "dimensions")
            {
                // Resource sizes, never node scale; separate resources avoid shared
                // scene-resource mutation. Height includes hemispheres.
                // Source: https://docs.godotengine.org/en/4.7/classes/class_capsuleshape3d.html#class-capsuleshape3d-property-height
                Shape(_fixture.First).Shape = new CapsuleShape3D { Radius = 0.25f, Height = 1.2f };
                Shape(_fixture.Second).Shape = new CapsuleShape3D { Radius = 0.6f, Height = 2.4f };
            }
            _radiusSum = Capture(_fixture.First, 1).CapsuleRadius + Capture(_fixture.Second, 2).CapsuleRadius;
            Require(_radiusSum > 0 && _radiusSum < 3, "valid measured scene dimensions required");
        }
        catch (Exception error) { Finish(false, error.Message); }
    }

    private static CollisionShape3D Shape(PlayerController player) => player.GetNode<CollisionShape3D>("CollisionShape3D");

    private static ContactOpponent Capture(PlayerController player, long peerId)
    {
        var collision = Shape(player);
        // Source: https://docs.godotengine.org/en/4.7/classes/class_collisionshape3d.html#class-collisionshape3d-property-shape
        Require(!collision.Disabled && collision.Shape is CapsuleShape3D, "real enabled capsule required");
        Require(player.Scale.IsEqualApprox(Vector3.One) && collision.Scale.IsEqualApprox(Vector3.One)
            && player.Rotation.IsEqualApprox(Vector3.Zero) && collision.Rotation.IsEqualApprox(Vector3.Zero)
            && collision.Position.X == 0 && collision.Position.Z == 0, "adapter supports upright centered unit-scale capsules");
        var capsule = (CapsuleShape3D)collision.Shape;
        return new ContactOpponent(player.GlobalPosition, player.Velocity, player.Heading,
            capsule.Radius, capsule.Height, collision.Position.Y, peerId);
    }

    public override void _Process(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        if (_elapsed > 10) Finish(false, "kernel adapter timed out");
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        try
        {
            Require(Math.Abs(delta - _fixture.FixedDelta) < 1e-6, "fixed physics delta required");
            bool stationary = _scenario is "overlap" or "coincident";
            float speed = _scenario == "crossing" ? 1200 : _fixture.First.MoveSpeed;
            _fixture.First.Velocity = stationary ? Vector3.Zero : new Vector3(0, 0, -speed);
            _fixture.Second.Velocity = Vector3.Zero;
            // Freeze before any writes. Reverse-role evaluation on these SAME
            // snapshots must return the same per-player results, including ties.
            var first = Capture(_fixture.First, 1);
            var second = Capture(_fixture.Second, 2);
            var correction = _scenario == "disabled-kernel" ? default : ContactMath.Resolve(first, second, delta);
            if (_scenario != "disabled-kernel")
            {
                var reverse = ContactMath.Resolve(second, first, delta);
                Require(correction.Self == reverse.Opponent && correction.Opponent == reverse.Self,
                    "frozen pair role/order symmetry failed");
                Require(correction == ContactMath.Resolve(first, second, delta), "identical inputs did not repeat exactly");
            }
            // Test adapter writes transforms directly, without solver response.
            // Source: https://docs.godotengine.org/en/4.7/classes/class_node3d.html#class-node3d-property-global-position
            _fixture.First.GlobalPosition = first.Position + first.Velocity * (float)delta + correction.Self.Position;
            _fixture.Second.GlobalPosition = second.Position + second.Velocity * (float)delta + correction.Opponent.Position;
            _fixture.First.Velocity = first.Velocity + correction.Self.Velocity;
            _fixture.Second.Velocity = second.Velocity + correction.Opponent.Velocity;
            _ticks++;
            var snapshot = _fixture.Snapshot;
            Require(snapshot.First.Position.IsFinite() && snapshot.Second.Position.IsFinite()
                && snapshot.First.Velocity.IsFinite() && snapshot.Second.Velocity.IsFinite(), "nonfinite result");
            _minimumSeparation = MathF.Min(_minimumSeparation, snapshot.HorizontalSeparation);
            if (_scenario != "disabled-kernel")
                Require(_minimumSeparation >= _radiusSum - Epsilon, "kernel non-overlap gate failed");
            if (!stationary && _scenario != "crossing" && _ticks < 30) return;

            bool absorptionGate = _minimumSeparation >= _radiusSum - Epsilon
                && MathF.Abs(snapshot.First.Position.Z - _radiusSum) < Epsilon
                && snapshot.Second.Position.Length() < Epsilon
                && snapshot.First.Velocity.Length() < Epsilon;
            switch (_scenario)
            {
                case "set-drive": case "crossing": case "dimensions":
                    Require(absorptionGate, "set absorption gate failed");
                    Require(snapshot.First.Position.Z < 3 - Epsilon, "driver must have approached");
                    break;
                case "unset-drive":
                    Require(!absorptionGate && snapshot.Second.Position.Z < -0.4f
                        && snapshot.First.Position.Z < _radiusSum - 0.4f,
                        "wrong-facing opponent must yield under the same drive");
                    Require(snapshot.Second.Velocity.Z < -2.9f, "yield must change opponent velocity");
                    break;
                case "disabled-kernel":
                    Require(!absorptionGate && _minimumSeparation < _radiusSum - 0.5f,
                        "disabled kernel must fail the SAME contact gate");
                    Require(snapshot.First.Position.Length() < Epsilon && snapshot.Second.Position == Vector3.Zero,
                        "control must independently reach its free endpoint at origin");
                    break;
                default:
                    Require(MathF.Abs(snapshot.HorizontalSeparation - _radiusSum) < Epsilon,
                        "initial overlap must recover fully in one tick");
                    Require(snapshot.First.Velocity == Vector3.Zero && snapshot.Second.Velocity == Vector3.Zero,
                        "stationary recovery must not invent velocity");
                    break;
            }
            Finish(true, $"kernel-only scenario={_scenario} ticks={_ticks} min-separation={_minimumSeparation:F6} "
                + $"radius-sum={_radiusSum:F6} first={snapshot.First.Position} second={snapshot.Second.Position} absorption-gate={absorptionGate}");
        }
        catch (Exception error) { Finish(false, error.Message); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private void Finish(bool success, string message)
    {
        if (_finished) return;
        _finished = true;
        if (success) GD.Print($"[harness] PASS ContactKernelTest: {message}");
        else GD.PrintErr($"[harness] FAIL ContactKernelTest: {message}; kernel-only ticks={_ticks}");
        // Source: https://docs.godotengine.org/en/4.7/classes/class_scenetree.html#class-scenetree-method-quit
        GetTree().Quit(success ? 0 : 1);
    }
}
