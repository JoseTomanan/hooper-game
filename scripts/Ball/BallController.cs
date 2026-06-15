using Godot;

namespace Hooper.Ball;

/// <summary>
/// Thin Godot node that owns the BallStateMachine and will (in later issues)
/// drive it each physics tick and expose it to the network layer.
///
/// ── Why so thin? ────────────────────────────────────────────────────────
/// ADR-0004 mandates that the ball's math/logic be unit-testable without a
/// running Godot instance.  All non-trivial logic lives in the pure classes:
///   BallState         — the enum
///   BallStateMachine  — the transition graph + state
///
/// This node does only what MUST be a Node: participates in the scene tree,
/// has a world-space transform, and will wire into _PhysicsProcess in
/// issues #10–#11 once the arc and rim math exist.
///
/// ── Future work (do not add now) ────────────────────────────────────────
/// Issue #10 — attach arc/dribble math; drive StateMachine from _PhysicsProcess.
/// Issue #11 — rim/backboard intersection; drive GoLoose() on miss.
/// Issue #13 — RPC wiring; server drives the StateMachine, clients predict.
/// </summary>
public partial class BallController : Node3D
{
    // ── Composed state machine ────────────────────────────────────────────

    /// <summary>
    /// The pure state machine that tracks Held / Dribbling / InFlight / Loose.
    /// Exposed read-only so the network layer (and tests of the node itself)
    /// can observe the current state without poking internals.
    /// </summary>
    public BallStateMachine StateMachine { get; private set; }

    // ── Lifecycle ─────────────────────────────────────────────────────────

    public override void _Ready()
    {
        // Ball starts Held by no-one (0) at tipoff; NetworkManager will call
        // the appropriate transition once the game-start handoff is decided.
        StateMachine = new BallStateMachine(initialHolderPeerId: 0);
    }

    // ── State accessor (convenience) ──────────────────────────────────────

    /// <summary>
    /// The current BallState.  Shorthand so callers don't have to navigate
    /// BallController.StateMachine.Current everywhere.
    /// </summary>
    public BallState State => StateMachine.Current;
}
