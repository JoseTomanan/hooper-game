namespace Hooper.Player;

/// <summary>
/// Pure C# resolver deciding whether to DISPLAY a defender's whiff-punish
/// "beaten" cue (issue #100's blow-by lane, made visible for issue #102) — no
/// Godot Node inheritance, no engine singletons.
///
/// This is a SEPARATE predicate from <see cref="DisplayPhaseResolver"/>'s
/// <c>LocalMachineDrivesDisplay</c>, deliberately, because the two truths are
/// known by a different set of roles:
///
///   - A committed move's PHASE is locally simulated (and therefore locally
///     true) in three of four roles — host own, server's copy of a remote
///     player, and the client's own predicted player (DisplayPhaseResolver's
///     <c>isServer || isLocalPlayer</c>).
///
///   - The BEATEN window's TRUTH is judged in exactly ONE role: the server.
///     <c>BallController.ResolveBeatenWindowTriggers</c> — the only caller of
///     <see cref="PlayerController.TriggerBeatenWindow"/> — runs inside
///     <c>BallController._PhysicsProcess</c>'s <c>if (IsServer)</c> block, so
///     a defender's own <c>_beaten</c> field is populated ONLY on the process
///     where <c>IsServer</c> is true. This mirrors the "never predict a
///     server-only outcome" rule that already governs steal/block success
///     itself (ADR-0018 §4, hooper-netcode-reference §11): whether a
///     committed defensive move just whiffed is a judgment only the server
///     can make (it alone evaluates <c>JustWhiffedDefensiveMove</c> against
///     every player, every tick), so a CLIENT — even predicting its OWN
///     player — cannot locally know it was just ruled beaten. It must always
///     read the broadcast, for both the opponent's node AND its own.
///
/// Concretely: <see cref="LocalStateIsAuthoritative"/> collapses to
/// <c>isServer</c> alone (not <c>isServer || isLocalPlayer</c>) — narrower
/// than DisplayPhaseResolver's predicate by exactly the "client's own player"
/// role, which DisplayPhaseResolver treats as locally-true but this resolver
/// does not.
///
/// Cosmetic-only discipline (ADR-0002/0004): this only selects which value
/// the renderer treats as "am I currently beaten" for display. It never
/// writes <c>_beaten</c>, and the underlying gameplay suppression (issue
/// #100's contest/proximity-factor suppression in
/// <c>BallController.ApplyShootLocally</c>) already reads the server's own
/// local <see cref="PlayerController.IsBeaten"/> directly — this resolver
/// exists purely so every peer's SCREEN can show the cue too.
/// </summary>
public static class BeatenDisplayResolver
{
    /// <summary>
    /// True only for the server — the sole role whose local
    /// <see cref="PlayerController.IsBeaten"/> reflects a judgment that peer
    /// actually made. Every other role (including a client's own predicted
    /// player) must read the broadcast value instead.
    /// </summary>
    public static bool LocalStateIsAuthoritative(bool isServer) => isServer;

    /// <summary>
    /// Selects the beaten flag to display: the locally-judged
    /// <paramref name="localIsBeaten"/> on the server, or the broadcast
    /// <paramref name="serverIsBeaten"/> everywhere else.
    /// </summary>
    public static bool Resolve(bool isServer, bool localIsBeaten, bool serverIsBeaten)
        => LocalStateIsAuthoritative(isServer) ? localIsBeaten : serverIsBeaten;
}
