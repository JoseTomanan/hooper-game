using System.Linq;
using System.Text.RegularExpressions;
using Godot;
using Hooper.Ball;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness adjudicating hitl #119 (issue #179, audit #177
// action A1, ADR-0016). Unit tests already pin OobResolution's pure decision
// table; what they CANNOT reach is BallController.ResolvePlayerOutOfBounds
// (~1043-1065), the server glue that samples a REAL player's world position
// each tick and calls AwardPossession — exactly the surface the human's
// "still broken" report points at.
//
//   godot --headless --path . res://tests/integration/OobTurnoverTest.tscn -- --harness-scenario=held-turnover
//   godot --headless --path . res://tests/integration/OobTurnoverTest.tscn -- --harness-scenario=defender-exempt
//   godot --headless --path . res://tests/integration/OobTurnoverTest.tscn -- --harness-scenario=both-oob
//   godot --headless --path . res://tests/integration/OobTurnoverTest.tscn -- --harness-scenario=wall-placement
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//
// ── Why a single offline instance is the server ───────────────────────────
// Same reasoning as StealTurnoverTest: with no MultiplayerPeer assigned, Godot
// uses OfflineMultiplayerPeer (is_server() hardcoded true, unique_id 1), so
// BallController.IsServer is true and ResolvePlayerOutOfBounds runs every tick.
//
// ── The scenarios ──────────────────────────────────────────────────────────
// held-turnover:    the ball's HOLDER is walked past the court line. Asserts
//                    possession flips to the opponent, the ball settles in
//                    BallState.Dribbling (AwardPossession's Turnover branch
//                    lands on Held then unconditionally calls StartDribble()
//                    before returning — the same settle-to-Dribbling every
//                    other AwardPossession caller, e.g. a rebound, reaches),
//                    and the new possession starts UNCLEARED (IsCleared ==
//                    false) — issue #119 criterion 2.
// defender-exempt:  the player WITHOUT the ball is walked past the line while
//                    the holder stays put. Asserts no turnover fires — #119 criterion 3.
// both-oob:         BOTH players cross the line. This is audit #177's named
//                    repro for "still broken": ResolvePlayerOutOfBounds gates the
//                    award on the RECIPIENT also being in-bounds (recipientCanReceive),
//                    so an also-OOB opponent yields recipient=0 -> OobResolution
//                    returns ClampFallback -> no award this tick, deliberately (see
//                    commit 1dade1a's "Why the recipient must be present AND
//                    in-bounds" doc: avoids a 60Hz possession strobe). Asserts BOTH
//                    #119 criterion 4 (no strobe: holder never changes across the
//                    whole window) AND pins the ACTUAL both-OOB outcome (holder
//                    keeps the ball indefinitely while OOB) so the PR can report
//                    exactly what a human running this repro would see.
// wall-placement:   parses the live scenes/Main.tscn text for the four
//                    Walls/WallCollision* shapes and asserts each sits entirely
//                    outside the CourtMin/CourtMax rectangle (read live off a
//                    bare BallController instance, not hardcoded) — #119 criterion 5.
public partial class OobTurnoverTest : Node
{
    private const double TimeoutSeconds = 10.0;

    // Ticks to let TryAssignTipoffHolder assign the initial holder before we
    // start manipulating positions. Tipoff fires the very first tick Players
    // has a nonzero-named child (BallController._Ready runs before the first
    // _PhysicsProcess), so 2 frames is ample margin, not a tight race.
    private const int ArmFrames = 2;

    // Ticks to hold a scenario's final position and watch for a delayed/incorrect
    // transition (strobe detection needs a real window, not a single sample).
    private const int StabilityFrames = 60;

    // Extra ticks after moving a player OOB before reading the verdict, so the
    // same-tick ResolvePlayerOutOfBounds call has definitely run.
    private const int VerdictMarginFrames = 3;

    // Beyond CourtMax.X/CourtMin.X (default ±4.88) but well inside the far-
    // backstop walls (±10) — a realistic "stepped past the sideline" position,
    // not an extreme deep in the parking lot.
    private static readonly Vector3 OobPositiveX = new(6.5f, 0f, 5f);
    private static readonly Vector3 OobNegativeX = new(-6.5f, 0f, 5f);

    private string _scenario = "held-turnover";

    private BallController _ball;
    private PlayerController _p1;
    private PlayerController _p2;

    private int _frame;
    private double _elapsed;
    private bool _finished;

    // Scenario working state.
    private bool _armed;
    private bool _oobApplied;
    private int _originalHolderId;
    private int _otherPeerId;
    private int _verdictFrame;
    private bool _strobed;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "held-turnover");
        GD.Print($"[oob-turnover] scenario={_scenario} booting headless…");

        if (_scenario == "wall-placement")
        {
            RunWallPlacementCheck();
            return;
        }

        // ── Build the minimal authoritative scene entirely in code ──────────
        // Same code-built-tree pattern as StealTurnoverTest (avoids fragile
        // .tscn ext_resource/uid wiring for a throwaway harness). Tree
        // pre-order Root -> Players -> "1" -> "2" -> Ball matches
        // scenes/Main.tscn's declaration order.
        var players = new Node3D { Name = "Players" };
        _p1 = new PlayerController { Name = "1" };
        _p2 = new PlayerController { Name = "2" };
        players.AddChild(_p1);
        players.AddChild(_p2);

        _ball = new BallController { Name = "Ball", Players = players };

        AddChild(players);
        AddChild(_ball);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        if (!_armed)
        {
            if (_frame >= ArmFrames)
            {
                if (_ball.StateMachine.HolderPeerId == 0)
                {
                    Fail($"tipoff never assigned a holder by frame {_frame}.");
                    Finish();
                    return;
                }

                _originalHolderId = _ball.StateMachine.HolderPeerId;
                _otherPeerId = _originalHolderId == 1 ? 2 : 1;
                _armed = true;
                GD.Print($"[oob-turnover] armed at frame {_frame}: holder={_originalHolderId}");
            }
            else
            {
                return;
            }
        }

        switch (_scenario)
        {
            case "held-turnover": TickHeldTurnover(); break;
            case "defender-exempt": TickDefenderExempt(); break;
            case "both-oob": TickBothOob(); break;
            default:
                Fail($"unknown scenario '{_scenario}'.");
                Finish();
                return;
        }

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame} without reaching a verdict.");
            Finish();
        }
    }

    private PlayerController NodeForPeer(int peerId) => peerId == 1 ? _p1 : _p2;

    // ── Scenario: held-ball OOB turnover (#119 criterion 2) ────────────────
    private void TickHeldTurnover()
    {
        if (!_oobApplied)
        {
            NodeForPeer(_originalHolderId).GlobalPosition = OobPositiveX;
            _oobApplied = true;
            _verdictFrame = _frame + VerdictMarginFrames;
            GD.Print($"[oob-turnover] frame {_frame}: walked holder {_originalHolderId} to {OobPositiveX}");
        }

        if (_frame < _verdictFrame) return;

        bool flipped = _ball.StateMachine.HolderPeerId == _otherPeerId;
        bool settledDribbling = _ball.State == BallState.Dribbling;
        bool uncleared = !_ball.IsCleared;
        bool pass = flipped && settledDribbling && uncleared;

        if (pass)
            GD.Print($"[oob-turnover] PASS held-turnover — holder flipped {_originalHolderId}->{_ball.StateMachine.HolderPeerId}, state={_ball.State}, cleared={_ball.IsCleared}.");
        else
            Fail($"held-turnover expected holder={_otherPeerId}, state=Dribbling, cleared=false; got holder={_ball.StateMachine.HolderPeerId}, state={_ball.State}, cleared={_ball.IsCleared}.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: defender crossing the line is a no-op (#119 criterion 3) ─
    private void TickDefenderExempt()
    {
        if (!_oobApplied)
        {
            NodeForPeer(_otherPeerId).GlobalPosition = OobPositiveX;
            _oobApplied = true;
            _verdictFrame = _frame + StabilityFrames;
            GD.Print($"[oob-turnover] frame {_frame}: walked DEFENDER {_otherPeerId} (not holder) to {OobPositiveX}");
        }

        if (_ball.StateMachine.HolderPeerId != _originalHolderId)
        {
            Fail($"defender-exempt: holder changed to {_ball.StateMachine.HolderPeerId} at frame {_frame} even though only the non-holder crossed the line.");
            Finish();
            return;
        }

        if (_frame < _verdictFrame) return;

        GD.Print($"[oob-turnover] PASS defender-exempt — holder stayed {_originalHolderId} across {StabilityFrames} ticks with defender OOB.");
        Finish(0);
    }

    // ── Scenario: both players OOB (#119 criterion 4 + the audited repro) ──
    private void TickBothOob()
    {
        if (!_oobApplied)
        {
            NodeForPeer(_originalHolderId).GlobalPosition = OobPositiveX;
            NodeForPeer(_otherPeerId).GlobalPosition = OobNegativeX;
            _oobApplied = true;
            _verdictFrame = _frame + StabilityFrames;
            GD.Print($"[oob-turnover] frame {_frame}: walked BOTH players OOB (holder={_originalHolderId} -> {OobPositiveX}, other={_otherPeerId} -> {OobNegativeX})");
            return;
        }

        // No-strobe assertion: once both are OOB, the holder must never change,
        // in either direction, for the whole observation window — a strobe
        // would show up as HolderPeerId ping-ponging tick to tick.
        if (_ball.StateMachine.HolderPeerId != _originalHolderId)
        {
            _strobed = true;
            GD.PrintErr($"[oob-turnover] frame {_frame}: holder changed to {_ball.StateMachine.HolderPeerId} while both players are OOB — this IS the strobe #119 criterion 4 forbids.");
        }

        if (_frame < _verdictFrame) return;

        // The actual designed both-OOB outcome, per commit 1dade1a's
        // "Why the recipient must be present AND in-bounds" doc: the
        // recipient-in-bounds gate makes ResolvePlayerOutOfBounds a NO-OP the
        // entire time both players are OOB (recipient=0 -> ClampFallback), so
        // the holder keeps the ball indefinitely while standing out of bounds.
        // This DOES look like "nothing happens" from a human's seat — that is
        // documented, reviewed (doubt-driven review, see the commit body), and
        // intentional to prevent a 60Hz turnover ping-pong, NOT the #119 bug.
        bool neverStrobed = !_strobed;
        bool holderUnchangedThroughout = _ball.StateMachine.HolderPeerId == _originalHolderId;
        bool pass = neverStrobed && holderUnchangedThroughout;

        if (pass)
        {
            GD.Print("[oob-turnover] PASS both-oob — no strobe observed; CONFIRMED designed behavior: " +
                     $"while both players are OOB, ResolvePlayerOutOfBounds is a no-op (recipient gate fails) " +
                     $"and holder {_originalHolderId} keeps the ball unchanged for {StabilityFrames} ticks. " +
                     "This is the exact 'looks like nothing happens' case named in issue #179/#177 — it is " +
                     "intentional anti-strobe behavior, not the reported bug.");
        }
        else
        {
            Fail($"both-oob expected holder to stay {_originalHolderId} with no strobe; got strobed={_strobed}, finalHolder={_ball.StateMachine.HolderPeerId}.");
        }

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: wall placement (#119 criterion 5) ─────────────────────────
    // Reads the LIVE CourtMin/CourtMax off a bare (never-added-to-tree)
    // BallController — its exported fields carry their default values from
    // the C# property initializer at construction, independent of _Ready — so
    // this check tracks the real source of truth instead of a hardcoded
    // duplicate that could silently drift from BallController.cs.
    private void RunWallPlacementCheck()
    {
        var courtRef = new BallController();
        Vector2 courtMin = courtRef.CourtMin;
        Vector2 courtMax = courtRef.CourtMax;

        string tscnPath = "res://scenes/Main.tscn";
        string text = FileAccess.GetFileAsString(tscnPath);
        if (string.IsNullOrEmpty(text))
        {
            Fail($"could not read {tscnPath} (FileAccess.GetFileAsString returned empty).");
            Finish();
            return;
        }

        var shapeSizes = new System.Collections.Generic.Dictionary<string, Vector3>();
        foreach (Match m in Regex.Matches(text,
                     @"\[sub_resource type=""BoxShape3D"" id=""(?<id>[\w]+)""\]\s*size = Vector3\((?<v>[^)]+)\)"))
        {
            shapeSizes[m.Groups["id"].Value] = ParseVector3(m.Groups["v"].Value);
        }

        var wallMatches = Regex.Matches(text,
            @"\[node name=""(?<name>WallCollision\w+)"" type=""CollisionShape3D"" parent=""Walls""[^\]]*\]\s*" +
            @"transform = Transform3D\((?<t>[^)]+)\)\s*shape = SubResource\(""(?<shape>[\w]+)""\)");

        if (wallMatches.Count != 4)
        {
            Fail($"expected 4 Walls/WallCollision* CollisionShape3D nodes in {tscnPath}, found {wallMatches.Count}.");
            Finish();
            return;
        }

        bool allOutside = true;
        foreach (Match m in wallMatches)
        {
            string name = m.Groups["name"].Value;
            Vector3 pos = ParseTransformPosition(m.Groups["t"].Value);
            if (!shapeSizes.TryGetValue(m.Groups["shape"].Value, out Vector3 size))
            {
                Fail($"{name} references unknown shape id '{m.Groups["shape"].Value}'.");
                allOutside = false;
                continue;
            }

            // Back/Front walls are thin along Z (the near/far court edges);
            // Left/Right walls are thin along X (the side court edges) — the
            // axis a wall guards is always its own thinnest dimension.
            bool isZAxis = name.Contains("Back") || name.Contains("Front");
            float posOnAxis = isZAxis ? pos.Z : pos.X;
            float half = (isZAxis ? size.Z : size.X) / 2f;
            float min = isZAxis ? courtMin.Y : courtMin.X; // CourtMin.Y holds the Z bound (CourtBounds "Why XZ only")
            float max = isZAxis ? courtMax.Y : courtMax.X;

            bool fullyOutside = (posOnAxis + half) < min || (posOnAxis - half) > max;
            GD.Print($"[oob-turnover] wall {name}: axis={(isZAxis ? "Z" : "X")} interval=[{posOnAxis - half:F3},{posOnAxis + half:F3}] court=[{min:F3},{max:F3}] outside={fullyOutside}");

            if (!fullyOutside)
            {
                Fail($"{name} is NOT fully outside the court rectangle on its guarded axis (interval=[{posOnAxis - half:F3},{posOnAxis + half:F3}], court=[{min:F3},{max:F3}]).");
                allOutside = false;
            }
        }

        if (allOutside)
            GD.Print("[oob-turnover] PASS wall-placement — all 4 Walls/WallCollision* shapes sit outside CourtMin/CourtMax.");

        Finish(allOutside ? 0 : 1);
    }

    private static Vector3 ParseVector3(string csv)
    {
        float[] v = csv.Split(',').Select(s => float.Parse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return new Vector3(v[0], v[1], v[2]);
    }

    // A Transform3D literal is "basis(9 floats), origin(3 floats)" — the last
    // three numbers are always the world-space position regardless of rotation.
    private static Vector3 ParseTransformPosition(string csv)
    {
        float[] v = csv.Split(',').Select(s => float.Parse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return new Vector3(v[^3], v[^2], v[^1]);
    }

    private void Fail(string message) => GD.PrintErr($"[oob-turnover] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[oob-turnover] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
