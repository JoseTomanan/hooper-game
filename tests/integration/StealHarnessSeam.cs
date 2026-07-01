using Hooper.Moves;

namespace Hooper.Player;

// Harness-only seam for the steal-turnover integration test (ADR-0016, issue #96
// remediation). This is a `partial` of PlayerController that lives DELIBERATELY
// under tests/integration/ — the game .csproj compiles tests/integration/**/*.cs
// into the game assembly (it is engine code referenced by .tscn scenes), so a
// partial declared here shares the same class and can reach the private _machine,
// yet production scripts/Player/PlayerController.cs stays free of any test hook.
//
// WHY a seam is unavoidable: a single offline headless instance cannot drive a
// steal through the shipped path. The begin is input-or-RPC gated by design —
// RequestBeginMove is [Rpc(AnyPeer, CallLocal=false)] and sender-id gated, so it
// never runs locally and there is no remote peer to deliver it; SampleMoveInput
// reads hardware input that headless does not provide, and its one-frame
// "just pressed" edge is undocumented/flaky headless. This seam calls the
// IDENTICAL _machine.Begin(new StealMove(target)) that RequestBeginMove's "steal"
// branch uses (PlayerController.cs), so the harness still exercises the real code
// the #96 fix touches: ActiveStealTargetHand → BallController.ResolveStealAttempts.
// It is never called by gameplay.
public partial class PlayerController
{
    /// <summary>
    /// Test-only: begin a StealMove on this player's committed-move machine,
    /// bypassing the input/RPC layer the fix does not touch. Returns Begin()'s
    /// result (false if a move is already running — same guard as production).
    ///
    /// internal, not public: this seam only needs to be visible to other files
    /// compiled into the same game assembly (tests/integration/StealTurnoverTest.cs,
    /// via the .csproj's explicit re-include — see this file's class doc). There is
    /// no reason for it to be part of the production public API surface.
    /// </summary>
    internal bool BeginStealForHarness(HandSide targetHand) =>
        _machine.Begin(new StealMove(targetHand));
}
