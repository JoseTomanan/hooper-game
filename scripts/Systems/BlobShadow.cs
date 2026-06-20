using Godot;

namespace Hooper.Systems;

/// <summary>
/// Keeps a flat shadow disc on the floor plane regardless of how high the parent
/// node is. Each physics tick the node teleports its GlobalPosition to the
/// parent's XZ projected onto FloorY, so the disc always reads as a ground
/// shadow whether the parent is on the floor, in the air, or at the rim.
///
/// Visual-only: no collision shape, no game logic, cast_shadow disabled on
/// the child MeshInstance3D in the scene so the disc itself never casts a
/// shadow on top of itself.
/// </summary>
public partial class BlobShadow : Node3D
{
    /// <summary>World-space Y at which the disc sits (just above the floor to avoid z-fighting).</summary>
    [Export] public float FloorY { get; set; } = 0.005f;

    public override void _PhysicsProcess(double delta)
    {
        if (GetParent() is not Node3D parent) return;
        GlobalPosition = new Vector3(parent.GlobalPosition.X, FloorY, parent.GlobalPosition.Z);
    }
}
