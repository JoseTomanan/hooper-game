using System;
using Godot;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration
{
    /// <summary>
    /// Two real shipped players, stepped in First-then-Second order by a harness's
    /// physics callback. No contact policy is implemented here: MoveAndSlide owns
    /// the collision response until the ADR-0025 implementation lands.
    /// </summary>
    public partial class ContactHarnessSeam : Node3D
    {
        internal PlayerController First { get; private set; }
        internal PlayerController Second { get; private set; }
        internal int StepCount { get; private set; }
        internal string FirstName { get; set; } = "1";
        internal string SecondName { get; set; } = "2";
        internal double FixedDelta => 1.0 / Engine.PhysicsTicksPerSecond;
        private ulong _lastPhysicsFrame = ulong.MaxValue;

        internal readonly record struct PlayerSnapshot(Vector3 Position, Vector3 Velocity, float Heading);
        internal readonly record struct PairSnapshot(PlayerSnapshot First, PlayerSnapshot Second)
        {
            internal float HorizontalSeparation => new Vector2(
                First.Position.X - Second.Position.X, First.Position.Z - Second.Position.Z).Length();
        }

        internal PairSnapshot Snapshot => new(
            new(First.GlobalPosition, First.Velocity, First.Heading),
            new(Second.GlobalPosition, Second.Velocity, Second.Heading));

        public override void _Ready()
        {
            var players = new Node3D { Name = "Players" };
            AddChild(players);
            // Instantiate the authored scene so tests inherit its capsule and exports.
            // Source: https://docs.godotengine.org/en/4.7/classes/class_packedscene.html#class-packedscene-method-instantiate
            var scene = ResourceLoader.Load<PackedScene>("res://scenes/Player.tscn")
                ?? throw new InvalidOperationException("Player.tscn could not be loaded");
            First = AddPlayer(scene, players, FirstName, new Vector3(0, 0, 10));
            Second = AddPlayer(scene, players, SecondName, Vector3.Zero);
        }

        private static PlayerController AddPlayer(PackedScene scene, Node parent, string name, Vector3 position)
        {
            var player = scene.Instantiate<PlayerController>();
            player.Name = name;
            player.Position = position;
            parent.AddChild(player);
            // _Ready enables processing, so disable AFTER parenting. Collision bodies
            // remain in the world; only automatic _PhysicsProcess is suppressed.
            // Source: https://docs.godotengine.org/en/4.7/classes/class_node.html#class-node-method-set-physics-process
            player.SetPhysicsProcess(false);
            player.SetProcess(false);
            // Source: https://docs.godotengine.org/en/4.7/classes/class_animationmixer.html#class-animationmixer-property-active
            var animation = player.GetNodeOrNull<AnimationTree>("AnimationTree");
            if (animation != null)
                animation.Active = false;
            return player;
        }

        internal void Reset(Vector3 firstPosition, float firstHeading, Vector3 firstVelocity,
            Vector3 secondPosition, float secondHeading, Vector3 secondVelocity)
        {
            First.ResetContactStateForHarness(firstPosition, firstHeading, firstVelocity);
            Second.ResetContactStateForHarness(secondPosition, secondHeading, secondVelocity);
            StepCount = 0;
        }

        internal PairSnapshot Step(Vector2 firstInput, Vector2 secondInput)
        {
            // Source: https://docs.godotengine.org/en/4.7/classes/class_engine.html#class-engine-method-is-in-physics-frame
            // Source: https://docs.godotengine.org/en/4.7/classes/class_engine.html#class-engine-method-get-physics-frames
            ulong frame = Engine.GetPhysicsFrames();
            if (!Engine.IsInPhysicsFrame() || frame == _lastPhysicsFrame)
                throw new InvalidOperationException("Contact fixture requires one step per real physics frame");
            _lastPhysicsFrame = frame;
            // MoveAndSlide consumes the engine physics delta internally: stepping in
            // a real physics callback is essential, even though Move takes a delta.
            // Source: https://docs.godotengine.org/en/4.7/classes/class_characterbody3d.html#class-characterbody3d-method-move-and-slide
            First.Move(firstInput, FixedDelta);
            Second.Move(secondInput, FixedDelta);
            StepCount++;
            return Snapshot;
        }
    }
}

namespace Hooper.Player
{
    public partial class PlayerController
    {
        internal void ResetContactStateForHarness(Vector3 position, float heading, Vector3 velocity)
        {
            GlobalPosition = position;
            SetHeadingForHarness(heading);
            Velocity = velocity;
            _pivot = HeadingMath.PivotState.None;
        }
    }
}
