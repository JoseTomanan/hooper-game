using System;

namespace Hooper.Player;

/// <summary>
/// Pure C# heading-arc function for the spin move (issue #201) — no Godot
/// Node inheritance, no engine singletons, no _PhysicsProcess, no RPCs, no
/// Godot types at all.
///
/// ── The ADR-0010 sanctioned exception ───────────────────────────────────────
/// ADR-0010 governs ordinary heading updates: a bounded, non-linear turn rate
/// integrated into Move() via HeadingMath.RotateToward/Step. That model is
/// explicitly NOT what a spin needs — the ADR's own "Consequences" section
/// already anticipates this exact case: "Because Heading is updated in
/// Move() but not in TickCommittedMoveBehavior, the heading does not advance
/// during the Active or Recovery phases of a committed move... If a future
/// move requires heading updates during Active/Recovery, RotateToward can be
/// called explicitly in TickCommittedMoveBehavior for that phase." A spin's
/// ~180° rotation is deliberately FASTER than RotateToward's bounded rate
/// allows (the whole point is a scripted, committed rotation, not a
/// player-steerable turn), so this function does NOT call RotateToward at
/// all — it computes the arc directly. This is the sanctioned exception the
/// ADR-0010 amendment (docs/adr/0010-authoritative-heading.md, same commit as
/// this file) records on the record, bounded to a Spin's Active phase only.
///
/// ── Why this MUST be a pure function of local state, not live input ────────
/// ADR-0010's Heading is jointly deterministic across the server and every
/// predicting/observing role BECAUSE it is driven by shared, already-
/// synchronized state (the committed move's own frame counter and the
/// heading it was anchored to at Active-entry) — never by a per-role live
/// read. Contrast CrossoverBurstMath's exitVectorSample, which genuinely IS
/// sourced differently per role (ReadInput() vs _pendingRawStick vs rawStick)
/// and therefore has a known, accepted, OPEN divergence under jitter/packet
/// loss (issue #210). If this arc read ANY live per-tick input, the
/// predicting client's own tick and the server's tick (for its own player, or
/// its copy of a remote player) would compute different arcs whenever their
/// live reads didn't coincide — visibly desyncing the spin between the
/// predicting holder's own screen and the remote opponent's view of them
/// (the exact failure class this issue's doubt-driven pass targets). This
/// function takes ONLY the entry heading (captured once, at JustEnteredActive,
/// from whichever role's own already-synchronized Heading was current then —
/// see PlayerController.TickCommittedMoveBehavior's Spin branch), the spin
/// direction (part of the CommittedMove instance, itself reconstructed
/// identically on every role from the SAME wire payload), and the frame
/// counter (CommittedMoveMachine's FrameInPhase, which the machine itself
/// keeps deterministic and identical across roles by construction — see
/// CommittedMoveMachine's own class doc). No Vector2, no Input, no Godot
/// singleton — there is nothing here FOR a role to disagree about.
/// </summary>
public static class SpinHeadingMath
{
    /// <summary>
    /// Computes the scripted heading for one Active-phase tick of a spin.
    ///
    /// <paramref name="frameInPhase"/> is 0-based: 0 on the tick the machine
    /// enters Active (JustEnteredActive), <paramref name="activeFrames"/> - 1
    /// on the LAST Active tick (CommittedMoveMachine.Tick's own contract — see
    /// its class doc: FrameInPhase increments before the phase-transition
    /// check, so the Active branch of TickCommittedMoveBehavior only ever
    /// observes FrameInPhase in [0, activeFrames-1] across activeFrames total
    /// ticks). Progress is therefore <c>(frameInPhase + 1) / activeFrames</c>,
    /// ranging from a small positive fraction (first tick) up to EXACTLY 1.0
    /// on the last tick — the full ~180° arc is reached on the SAME tick the
    /// hand swap fires (PlayerController's Spin branch gates the swap on that
    /// same <c>frameInPhase == activeFrames - 1</c> condition), so "the
    /// rotation completes" and "the hand swaps" are the same tick by
    /// construction, never two separate events that could drift apart.
    /// </summary>
    /// <param name="entryHeading">
    /// The player's authoritative heading in radians at the instant Active
    /// began (captured once, at JustEnteredActive — see class doc). Never
    /// re-read from a live Heading value mid-arc.
    /// </param>
    /// <param name="direction">
    /// Spin direction sign: &gt;= 0 rotates toward +π (the player's right,
    /// clockwise viewed from above), &lt; 0 rotates toward -π (left,
    /// counter-clockwise). Mirrors System.Math.Sign(Spin.SpinDirection) —
    /// a caller-supplied exact 0 resolves to the &gt;= 0 (right) branch,
    /// the same "zero defaults to the family's existing sign convention"
    /// behavior every other burst-direction sign in this codebase already
    /// has (e.g. Crossover's flickSign==0 case is likewise never specially
    /// guarded) — not a new risk class this move introduces.
    /// </param>
    /// <param name="frameInPhase">
    /// CommittedMoveMachine.FrameInPhase for the current tick, 0-based.
    /// </param>
    /// <param name="activeFrames">
    /// The move's total Active-phase tick count (MoveFrameData.ActiveFrames).
    /// Guaranteed &gt;= 1 by MoveFrameData's own constructor validation; this
    /// function still guards against a non-positive value defensively (see
    /// below) since it is an independently unit-tested pure function that
    /// should not trust a caller invariant it cannot itself enforce.
    /// </param>
    /// <returns>The new heading in radians, normalized to [-π, π].</returns>
    public static float ArcHeading(float entryHeading, int direction, int frameInPhase, int activeFrames)
    {
        // Defensive guard (doubt-cycle finding, #201): MoveFrameData's
        // constructor already rejects activeFrames < 1, so this branch is
        // unreachable through any real committed move today — but a
        // standalone pure function should not propagate a divide-by-zero
        // (Infinity/NaN) into Heading if that invariant is ever violated by a
        // future caller. Returning the unrotated entry heading is the same
        // "degrade rather than corrupt state" posture MoveAnimResolver's
        // unknown-phase fallback already uses elsewhere in this codebase.
        if (activeFrames <= 0)
            return NormalizeAngle(entryHeading);

        float progress = (frameInPhase + 1f) / activeFrames;
        float delta = direction >= 0 ? MathF.PI * progress : -MathF.PI * progress;
        return NormalizeAngle(entryHeading + delta);
    }

    /// <summary>
    /// Wraps <paramref name="angle"/> into the range [-π, π] — same
    /// normalization convention as HeadingMath's own internal helper, kept as
    /// a private duplicate here (not shared) since this class is intentionally
    /// self-contained and has no dependency on HeadingMath.
    /// </summary>
    private static float NormalizeAngle(float angle)
    {
        while (angle > MathF.PI) angle -= 2f * MathF.PI;
        while (angle < -MathF.PI) angle += 2f * MathF.PI;
        return angle;
    }
}
