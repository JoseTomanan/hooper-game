using Hooper.Moves;

namespace Hooper.Player;

/// <summary>
/// Pure C# resolver deciding WHICH committed-move phase a player node should
/// DISPLAY (animation + lean), given this peer's role for that node — no Godot
/// Node inheritance, no engine singletons.
///
/// Extracted for M7b (issue #69), the load-bearing netcode-display fix. The bug
/// it closes: on a client, the copy of the OPPONENT player never advances its
/// local CommittedMoveMachine (TickClientRemotePlayer only lerps position), so
/// its _machine.Phase is permanently Inactive. Reading that local phase for the
/// opponent's visuals means the opponent's startup → active → recovery arc — and
/// the M7a burst lean — never render on your screen, silently breaking ADR-0003's
/// promise that BOTH players can see the opponent's commitment in real time.
///
/// The fix is to display the BROADCAST phase (already on the wire every server
/// tick via ReceiveState) for that one role, while every other role keeps reading
/// its locally-simulated machine.
///
/// ── Role matrix (the entire decision) ────────────────────────────────────────
///
///   Role                         IsServer  IsLocalPlayer  Local machine driven?  Display source
///   Host's own player               T           T                 yes              local _machine
///   Server's copy of remote         T           F                 yes (server      local _machine
///                                                                   ticks all)
///   Client's own player             F           T                 yes (predicted)  local _machine
///   Client's copy of remote         F           F                 NO               broadcast phase
///
/// The local machine is the correct display source exactly when this peer
/// SIMULATES the node — true in three of four roles. The lone exception is the
/// client's copy of the opponent, the one role where (IsServer || IsLocalPlayer)
/// is false. Hence the whole decision collapses to that single predicate.
///
/// Cosmetic-only discipline (ADR-0002/0004): this only selects which phase the
/// renderer reads. It is a pure read; nothing here writes _machine, prediction,
/// or any replicated state. Reading the broadcast for display does NOT touch
/// reconciliation — the reconcile path keeps consulting _serverMovePhase exactly
/// as before.
/// </summary>
public static class DisplayPhaseResolver
{
    /// <summary>
    /// True when the node's local CommittedMoveMachine reflects the truth this
    /// peer should render — i.e. this peer simulates the node. False only for the
    /// client's copy of a remote player, which never advances its local machine
    /// and must therefore display the server's broadcast phase instead.
    /// </summary>
    public static bool LocalMachineDrivesDisplay(bool isServer, bool isLocalPlayer)
        => isServer || isLocalPlayer;

    /// <summary>
    /// Selects the phase to display: the locally-simulated <paramref name="localPhase"/>
    /// for every role this peer simulates, or the broadcast
    /// <paramref name="serverPhase"/> for the client's copy of a remote player.
    /// </summary>
    public static MovePhase Resolve(bool isServer, bool isLocalPlayer,
        MovePhase localPhase, MovePhase serverPhase)
        => LocalMachineDrivesDisplay(isServer, isLocalPlayer) ? localPhase : serverPhase;
}
