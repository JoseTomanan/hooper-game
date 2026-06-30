namespace Hooper.Ball;

/// <summary>
/// Pure decision helper for the loose-ball out-of-bounds rule
/// (issue #63, ADR-0008 §Amendment 2026-06-28).
///
/// ── Why pure / why here ───────────────────────────────────────────────────
/// Mirrors the headless-seam discipline of CourtBounds, ClearLine, and
/// ReboundContest (ADR-0004): no Godot Node, no engine singletons.
/// BallController.TickLoose already owns all engine lookups (IsServer,
/// OtherPlayerPeerId) and passes their RESULTS in; this helper encodes only
/// the three-branch decision table so it can be unit-tested without a Godot
/// runtime.
///
/// ── Decision table (ADR-0008 §Amendment 2026-06-28) ──────────────────────
///
///   isOob  isServer  recipient  → Action
///   ──────────────────────────────────────────────────────────
///   false  *         *          → NoOp        (ball is in bounds; nothing to do)
///   true   true      != 0       → Award        (dead ball: give to the opponent)
///   true   true      == 0       → ClampFallback (solo test: no opponent, stay in play)
///   true   false     *          → ClampFallback (client: clamp and wait for ReceiveState)
///
/// ── Why Award is server-gated ────────────────────────────────────────────
/// OOB is a dead-ball ruling, not a live scramble. There is no 50/50
/// proximity contest to predict (unlike rebounds). Gating on isServer
/// eliminates prediction-flip risk — two clients briefly disagreeing on the
/// new holder — for zero gameplay cost. Mirrors the same authority boundary
/// as ResolveServerMake (ADR-0008 §Decision-1).
///
/// ── Why ClampFallback exists ─────────────────────────────────────────────
/// Two cases land here by design:
///   1. No opponent (solo editor test, recipient == 0): with nobody to award
///      the ball to, letting it fly OOB forever would break solo testing.
///      The clamp keeps it in play, consistent with ResolveServerMake's
///      defender==0 "leave loose" pattern.
///   2. Non-server client: the client keeps the old unconditional clamp so
///      no regression occurs on the non-authoritative path. The server's
///      authoritative ReceiveState broadcast corrects any divergence — same
///      as all other possession changes (ADR-0002).
/// </summary>
public static class OobResolution
{
    /// <summary>
    /// Discriminated-union result from <see cref="Resolve"/>.
    ///
    /// <list type="bullet">
    ///   <item><term><see cref="Action.NoOp"/></term>
    ///     <description>Ball is in bounds. Caller continues normal TickLoose processing.</description>
    ///   </item>
    ///   <item><term><see cref="Action.Award"/></term>
    ///     <description>
    ///       Server awards possession to <see cref="RecipientPeerId"/>.
    ///       Caller calls AwardPossession(RecipientPeerId) and returns early
    ///       (skipping the rebound step — the play is dead).
    ///     </description>
    ///   </item>
    ///   <item><term><see cref="Action.ClampFallback"/></term>
    ///     <description>
    ///       No authoritative award this tick. Caller falls through to
    ///       CourtBounds.Clamp and continues rebound processing.
    ///     </description>
    ///   </item>
    /// </list>
    /// </summary>
    public enum Action
    {
        /// <summary>Ball is in bounds; nothing to do.</summary>
        NoOp,

        /// <summary>
        /// Server awards possession to <see cref="Result.RecipientPeerId"/>.
        /// Caller should call AwardPossession and return early.
        /// </summary>
        Award,

        /// <summary>
        /// No award possible this tick (non-server, or no opponent present).
        /// Caller falls through to the CourtBounds.Clamp path.
        /// </summary>
        ClampFallback
    }

    /// <summary>
    /// Immutable result from <see cref="Resolve"/>: the <see cref="Action"/>
    /// the caller must take, plus the recipient peer id (only meaningful when
    /// <see cref="Action"/> is <see cref="Action.Award"/>).
    /// </summary>
    public readonly struct Result
    {
        /// <summary>The action BallController.TickLoose must take.</summary>
        public readonly Action Action;

        /// <summary>
        /// The peer id to hand possession to.  Defined only when
        /// <see cref="Action"/> is <see cref="Action.Award"/>; callers MUST
        /// NOT use this value for other actions.
        /// </summary>
        public readonly int RecipientPeerId;

        internal Result(Action action, int recipientPeerId = 0)
        {
            Action = action;
            RecipientPeerId = recipientPeerId;
        }
    }

    /// <summary>
    /// Applies the three-branch OOB decision table and returns the action
    /// BallController.TickLoose must take.
    ///
    /// All engine-facing lookups (IsServer, OtherPlayerPeerId) are resolved
    /// by the CALLER before this call — keeping this helper engine-free and
    /// independently testable (ADR-0004 headless-seam discipline).
    /// </summary>
    /// <param name="isOutOfBounds">
    /// True when the ball's XZ position has crossed the play-court boundary
    /// (result of CourtBounds.IsOutOfBounds).
    /// </param>
    /// <param name="isServer">
    /// True when the calling peer is the server (Multiplayer.IsServer()).
    /// Only the server issues authoritative possession awards.
    /// </param>
    /// <param name="resolvedRecipient">
    /// The peer id of the opponent — the result of
    /// OtherPlayerPeerId(_lastShooterPeerId) called by the caller.
    /// Pass 0 when no opponent is present (solo editor test).
    /// </param>
    /// <returns>
    /// A <see cref="Result"/> describing the action TickLoose must take.
    /// See <see cref="Action"/> for the per-case semantics.
    /// </returns>
    /// <summary>
    /// Resolves WHO a dead loose ball is awarded to, from the last player to
    /// have TOUCHED (possessed) the ball — the streetball "last-toucher-out →
    /// other ball" rule (ADR-0008 §Amendment 2026-06-30, issue #118 part 1).
    ///
    /// The caller maintains <paramref name="lastToucherPeerId"/> across EVERY
    /// possession change (tipoff, rebound, catch, make-it-take-it, turnover) —
    /// not just on a shot — so a ball fumbled OOB after a rebound is awarded
    /// opposite the REBOUNDER, never handed back to the player who knocked it
    /// out. (Before #118 the recipient was keyed off the last SHOOTER, which a
    /// rebound never updated, so a rebounder's fumble could return the ball to
    /// them — the bug this rule fixes.)
    ///
    /// Pre-touch short-circuit (issue #118 part 2): when nobody has touched the
    /// ball yet (<paramref name="lastToucherPeerId"/> == 0, the pre-tipoff
    /// window), there is no possession history to award opposite of, so return
    /// 0. <see cref="Resolve"/> maps recipient 0 to <see cref="Action.ClampFallback"/>:
    /// the ball stays in play rather than teleporting to a spawn-order-arbitrary
    /// player. A loose ball with no possession history is nobody's turnover.
    /// </summary>
    /// <param name="lastToucherPeerId">
    /// Peer id of the last player to possess the ball, or 0 if none yet.
    /// </param>
    /// <param name="opponentOfToucher">
    /// The peer id opposite the toucher — the caller's
    /// OtherPlayerPeerId(lastToucherPeerId) result. Ignored when the toucher is
    /// 0 (pre-touch short-circuit), so the caller may pass any value there.
    /// </param>
    public static int ResolveRecipient(int lastToucherPeerId, int opponentOfToucher)
        => lastToucherPeerId == 0 ? 0 : opponentOfToucher;

    public static Result Resolve(bool isOutOfBounds, bool isServer, int resolvedRecipient)
    {
        // In-bounds: nothing to do; caller continues normal TickLoose processing.
        if (!isOutOfBounds)
            return new Result(Action.NoOp);

        // Out of bounds, server, opponent present: dead ball.
        // Award possession to the player opposite the last shooter (ADR-0008
        // §Amendment 2026-06-28 "opposite the last shooter").  Caller must
        // call AwardPossession(RecipientPeerId) and return immediately so the
        // rebound step is skipped — the play is already resolved.
        if (isServer && resolvedRecipient != 0)
            return new Result(Action.Award, resolvedRecipient);

        // Out of bounds, but either: (a) no opponent present (recipient == 0)
        // — solo editor test where there is nobody to give the ball to — or
        // (b) this is a non-server client peer.  Either way, fall through to
        // the CourtBounds.Clamp path so the ball stays in play and the server's
        // authoritative ReceiveState broadcast corrects any divergence.
        return new Result(Action.ClampFallback);
    }
}
