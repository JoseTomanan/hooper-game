using Hooper.Ball;
using Hooper.Moves;

namespace Hooper.Player;

// Generic harness seam for beginning a defensive committed move headless
// (issue #216 original body row 3, consolidating what used to be
// StealHarnessSeam.BeginStealForHarness and BlockHarnessSeam.
// BeginBlockForHarness — same shape, "begin this move via the real
// production choke point," differing only in which CommittedMove instance
// is passed. Contest (#99) would otherwise have grown a third copy of this
// exact file).
//
// This is a `partial` of PlayerController that lives DELIBERATELY under
// tests/integration/ — the game .csproj compiles tests/integration/**/*.cs
// into the game assembly (it is engine code referenced by .tscn scenes), so
// a partial declared here shares the same class and can reach the private
// BeginCommittedMove, yet production scripts/Player/PlayerController.cs
// stays free of any test hook.
//
// WHY a seam is unavoidable: a single offline headless instance cannot drive
// a defensive move through the shipped path. The begin is input-or-RPC
// gated by design — RequestBeginMove is [Rpc(AnyPeer, CallLocal=false)] and
// sender-id gated, so it never runs locally and there is no remote peer to
// deliver it; SampleMoveInput reads hardware input headless does not
// provide, and its one-frame "just pressed" edge is undocumented/flaky
// headless.
//
// Hard rule (hooper-architecture-contract invariant #11): a seam MUST call
// the production choke point BeginCommittedMove(...), never _machine.Begin()
// directly. BlockHarnessSeam's own history is the cautionary tale: an
// earlier version called _machine.Begin() on the theory that BlockMove has
// no BeginCommittedMove-gated side effect (unlike JumpShot's dribble cradle)
// — that reasoning MISSED that BeginCommittedMove unconditionally clears the
// in-place-pivot latch on every successful Begin() regardless of move type
// (#172), so the harness was silently not exercising the real begin path.
// StealHarnessSeam's predecessor had the SAME gap (it called _machine.Begin()
// directly); this consolidation fixes it as a side effect of routing both
// moves through the one correct path — inert for every existing scenario
// (none of them have an in-progress pivot at the moment the defensive move
// begins, so clearing an already-clear latch changes nothing observable),
// verified by the unchanged BlockTurnoverTest/StealTurnoverTest results.
public partial class PlayerController
{
    /// <summary>
    /// Test-only: begins the given committed move on this player's machine
    /// via the SAME BeginCommittedMove path production input reaches,
    /// bypassing only the input/RPC layer the fix under test does not touch.
    /// Returns BeginCommittedMove's result (false if a move is already
    /// running, or — for Crossover/Hesitation/BehindTheBack only — the
    /// dead-dribble gate rejected it; neither StealMove nor BlockMove is
    /// subject to that gate).
    ///
    /// internal, not public: this seam only needs to be visible to other
    /// files compiled into the same game assembly (the *TurnoverTest.cs
    /// harnesses, via the .csproj's explicit re-include — see this file's
    /// class doc). There is no reason for it to be part of the production
    /// public API surface.
    /// </summary>
    internal bool BeginMoveForHarness(CommittedMove move) => BeginCommittedMove(move);

    /// <summary>
    /// Test-only (issue #254): begins a steal with its TargetHand DERIVED
    /// FROM AIM INPUT via the REAL production mapping (ResolveStealTargetHand
    /// -> HandStateResolver.TargetHandFromAim), not constructed directly with
    /// <c>new StealMove(HandSide.X)</c> — that direct construction is exactly
    /// the gap #254 identified: it bypasses the aim→hand facing transform
    /// entirely, so a regression there could never be caught by a harness
    /// that only ever hand-picks the target.
    ///
    /// Mirrors BeginMoveForHarness's rationale: bypasses only the input/RPC
    /// layer (hardware Input.GetVector, the RequestBeginMove RPC round-trip),
    /// never the mapping/resolution logic itself, which is the exact thing
    /// under test here. Routes through the SAME BeginCommittedMove choke
    /// point (hooper-architecture-contract invariant #11).
    /// </summary>
    /// <param name="aimSign">
    /// The defender's raw aim sign, same convention SampleMoveInput computes
    /// from aim.X (positive = defender's own body-right).
    /// </param>
    /// <param name="ball">The live BallController, so the holder can be
    /// looked up exactly like ResolveStealTargetHand does in production.</param>
    internal bool BeginStealFromAimForHarness(float aimSign, BallController ball) =>
        BeginCommittedMove(new StealMove(ResolveStealTargetHand(aimSign, ball)));

    // NOTE: the Heading setup seam this test needs (SetHeadingForHarness) is
    // now provided by PlayerHarnessSeam.cs, which #243's fadeaway harness added
    // to main with an identical `internal void SetHeadingForHarness(float) =>
    // Heading = value;` body. This file used to declare its own copy; that was
    // removed when #254 merged main to avoid a CS0111 duplicate-member clash
    // (both are partials of PlayerController). The rationale that lived on the
    // old copy — a bare headless second node has no input path that would ever
    // advance Heading via Move()->HeadingMath.Step, so scenario SETUP must
    // force it — still applies; it just lives on the shared PlayerHarnessSeam
    // declaration now.
}
