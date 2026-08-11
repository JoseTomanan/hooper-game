using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// LIVE-EDITOR (Godot .NET MCP) verification probe for the Blender-authored clip
// families. This is a DIAGNOSTIC INSTRUMENT, not a CI gate — it renders numbers
// and lets a human (or an agent reading MCP output) judge them. Nothing here
// calls GetTree().Quit(), and it is deliberately NOT registered in ci.yml.
//
//   mcp__godot__run_project scene=tests/integration/AuthoredClipMcpProbe.tscn
//   mcp__godot__get_debug_output   (poll; see "The hold protocol" below)
//
// ── Why this exists alongside the per-move AnimTest harnesses ────────────────
// #303/#319/#322/#282/#326/#327/#332 each merged with headless-harness evidence
// only. Every one of those harnesses runs under `godot --headless`, which means
// the whole batch shares one unexamined assumption: that the headless import and
// scene-load path is the same one the editor uses. This probe closes that gap by
// running the SAME rig through the LIVE EDITOR's import pipeline and renderer,
// where import errors, `couldn't resolve track` warnings and material/shader
// failures actually surface.
//
// It does NOT duplicate the AnimTest gates. Those assert per-move SEMANTICS (the
// block leaves the ground, the contest does not, the steal reaches the right
// hand). This probe asserts the layer underneath all of them, uniformly, for
// every authored family at once: the clip exists, is cut to its tick window,
// binds to the real rig, covers the rig's non-leaf bones, and physically moves
// the skeleton when its state is entered.
//
// ── The hold protocol ───────────────────────────────────────────────────────
// The godot-dotnet-mcp addon DISCARDS its output buffer when the debugged
// process exits, and get_debug_output returns a snapshot rather than a stream.
// A scene that runs its checks and quits is therefore a scene whose verdict is
// unreadable over MCP — the first poll catches the boot lines and the next one
// errors with "No active Godot process."
//
// So this probe never exits. Once every family has been measured it enters a
// hold and RE-PRINTS the full summary on a fixed interval, so any single poll,
// at any time after the first cycle, captures the complete result. Stop it with
// mcp__godot__stop_project when done.
//
// ── What is measured, and why each check is separate ────────────────────────
// 1. EXISTS + DURATION. Tick windows come from each move's DefaultFrameData, not
//    hardcoded, so a #238 retune that forgets to re-run the rebuild tool shows up
//    here (#276 rule 4 / #295).
//
// 2. NODE-PATH BINDING (README trap 13/15). AnimationTree.root_node is
//    ../CharacterModel, so a healthy track path is "Skeleton3D:mixamorig_Hips".
//    Blender's exporter wraps the skeleton in an Armature object and emits
//    "Armature/Skeleton3D:mixamorig_Hips", which resolves to a CharacterModel/
//    Armature/... that does not exist. Godot LOGS and carries on: the clip binds
//    to nothing, the mesh never moves, and the state machine still enters the
//    right state with the right duration. #281 shipped exactly that shape.
//
//    This is checked SEPARATELY from the bone-name check below because #281
//    proved a clip can pass bone-name matching on every track while failing node
//    -path binding on every track. Bone-name matching alone is structurally blind
//    to trap 13. A subname-less path is REJECTED rather than skipped — skipping
//    it is how #281's rebuild reported "unresolved=[]" while all 198 tracks
//    failed (trap 15).
//
// 3. ROTATION COVERAGE, counting ROTATION_3D ONLY (#330). The mixer drives
//    position/rotation/scale as independent channels, so a bone whose only track
//    is SCALE_3D still has its ROTATION written from skeleton rest — the a45bd1d
//    trap. A type-blind count scores such a bone as covered. Leaf bones are
//    excluded: Y Bot is 65 bones but only 52 non-leaf, and the gap count is
//    dominated by inert finger terminators, so the LIST matters more than the
//    count and material gaps are spine/limb/foot bones.
//
// 4. FINAL-TICK DEPARTURE FROM REST. #281 mutation-tested two naive metrics
//    against deliberately unbound clips and BOTH PASSED on the defect:
//    max-departure-across-the-arc (an unbound clip still reads ~110 deg, because
//    the Y Bot rest is a T-pose and the arms sit off it either way) and
//    max-change-across-the-arc (also ~110 deg, because an unbound clip's COLLAPSE
//    to rest is itself a large change — "it moved" does not imply "this clip
//    moved it"). Only the LAST-tick reading separates them: a bound clip holds
//    its pose through the phase, an unbound one collapsed to rest within a tick
//    of entry. So this samples the last observed tick of each phase, not the max.
//
//    THE PHASE LABEL LEADS THE POSE BY ONE TICK, even with
//    CallbackModeProcess=Physics (README trap 6 fixes the LABEL, not the pose).
//    Each phase's first observed tick is dropped and the tick count is reported,
//    so a one-tick phase is visible as such rather than silently empty.
//
// ── Cosmetic-only ───────────────────────────────────────────────────────────
// Moves are begun via BeginMoveForHarness — downstream of every gameplay gate,
// the same seam BlockAnimTest/ContestAnimTest/LayupAnimTest use — precisely so
// this can never become a second, weaker test of any gameplay coupling. Nothing
// here reads DefensiveResolution, possession, or scoring.
public partial class AuthoredClipMcpProbe : Node
{
    // Ticks allowed per family before giving up and moving to the next one.
    // > the longest family (steal/block: 8+8+20 and 10+8+20) with slack.
    private const int ObserveFramesPerFamily = 80;
    private const int ArmFrames = 2;   // ticks for TryAssignTipoffHolder
    private const int SettleFrames = 2; // ticks for position/heading to settle
    private const double HoldReprintSeconds = 6.0;

    private static readonly Vector3 ActorSpot = new(0f, 0f, 2f);
    private static readonly Vector3 OtherSpot = new(0.9f, 0f, 2f); // within steal reach
    private static readonly Vector3 RimCenter = new(0f, 3.05f, 0f);

    /// <summary>
    /// One Blender-authored clip family: the PR that landed it, the clips it
    /// owns, and how to make its states play live.
    /// </summary>
    /// <param name="Clips">
    /// locomotion.res clip name -> the tick window it must be cut to. A null
    /// window means the clip is a looping STANCE with no phase arc (the dribble
    /// family), so the duration check is skipped rather than faked.
    /// </param>
    /// <param name="MakeMove">
    /// Null for a family that is not a committed move — the dribble stance is
    /// reached through possession, not through BeginCommittedMove.
    /// </param>
    private sealed record Family(
        string Id,
        string Label,
        string SourceFbx,
        (string Clip, int? Ticks)[] Clips,
        string[] States,
        Func<CommittedMove> MakeMove);

    private static Family[] BuildFamilies()
    {
        var btb = BehindTheBack.DefaultFrameData;
        var steal = StealMove.DefaultFrameData;
        var layup = Layup.DefaultFrameData;
        var contest = ContestMove.DefaultFrameData;
        var block = BlockMove.DefaultFrameData;
        var jabstep = JabStep.DefaultFrameData;
        var inandout = InAndOut.DefaultFrameData;
        var retreatdribble = RetreatDribble.DefaultFrameData;

        return new[]
        {
            // #303 — the reference implementation. A looping stance, not a phase
            // arc, so it carries no tick windows; #324 split it into hand sides.
            new Family("dribblemove", "Moving dribble (PR #303 / #300, split by PR #324 / #294)",
                "assets/dribble_move_authored.fbx",
                new (string, int?)[] { ("dribblemoveleft", null), ("dribblemoveright", null) },
                new[] { "DribbleLeft", "DribbleRight" },
                null),

            new Family("behindtheback", "Behind-the-back (PR #322 / #281)",
                "assets/behindtheback_authored.fbx",
                new (string, int?)[]
                {
                    ("behindthebackstartupleft",  btb.StartupFrames),
                    ("behindthebackstartupright", btb.StartupFrames),
                    ("behindthebackactiveleft",   btb.ActiveFrames),
                    ("behindthebackactiveright",  btb.ActiveFrames),
                    ("behindthebackrecoveryleft", btb.RecoveryFrames),
                    ("behindthebackrecoveryright",btb.RecoveryFrames),
                },
                new[] { "BehindTheBackStartupLeft", "BehindTheBackActiveLeft", "BehindTheBackRecoveryLeft" },
                () => new BehindTheBack(1f)),

            // #282 merged as a BARE BRANCH (1510d68) with no PR at all, so it has
            // never had a review surface of any kind.
            new Family("steal", "Steal (branch merge 1510d68 / #282 — no PR)",
                "assets/steal_authored.fbx",
                new (string, int?)[]
                {
                    ("stealstartupleft",  steal.StartupFrames),
                    ("stealstartupright", steal.StartupFrames),
                    ("stealactiveleft",   steal.ActiveFrames),
                    ("stealactiveright",  steal.ActiveFrames),
                    ("stealrecoveryleft", steal.RecoveryFrames),
                    ("stealrecoveryright",steal.RecoveryFrames),
                },
                new[] { "StealStartupLeft", "StealActiveLeft", "StealRecoveryLeft" },
                () => new StealMove(HandSide.Left, -1f)),

            new Family("layup", "Layup (PR #326 / #313)",
                "assets/layup_authored.fbx",
                new (string, int?)[]
                {
                    ("layupstartup",  layup.StartupFrames),
                    ("layupactive",   layup.ActiveFrames),
                    ("layuprecovery", layup.RecoveryFrames),
                },
                new[] { "LayupStartup", "LayupActive", "LayupRecovery" },
                () => new Layup()),

            new Family("contest", "Contest (PR #327 / #314)",
                "assets/contest_authored.fbx",
                new (string, int?)[]
                {
                    ("conteststartup", contest.StartupFrames),
                    ("contestactive",  contest.ActiveFrames),
                    ("contestrecovery",contest.RecoveryFrames),
                },
                new[] { "ContestStartup", "ContestActive", "ContestRecovery" },
                () => new ContestMove()),

            new Family("block", "Block (PR #332 / #283)",
                "assets/block_authored.fbx",
                new (string, int?)[]
                {
                    ("blockstartup",  block.StartupFrames),
                    ("blockactive",   block.ActiveFrames),
                    ("blockrecovery", block.RecoveryFrames),
                },
                new[] { "BlockStartup", "BlockActive", "BlockRecovery" },
                () => new BlockMove()),

            // #336 — added late. Jab step (PR #334 / #304) landed on THIS branch
            // one commit before the probe itself and was still missed, which is
            // why the probe's "every authored clip family" claim was false on
            // arrival. Unhanded, so no left/right split. Note the real moveId is
            // "jab", not "jabstep" (JabStep.cs:71) — the clip names take the
            // longer form, the resolver keys on the shorter one.
            new Family("jabstep", "Jab step (PR #334 / #304)",
                "assets/jabstep_authored.fbx",
                new (string, int?)[]
                {
                    ("jabstepstartup",  jabstep.StartupFrames),
                    ("jabstepactive",   jabstep.ActiveFrames),
                    ("jabsteprecovery", jabstep.RecoveryFrames),
                },
                new[] { "JabStepStartup", "JabStepActive", "JabStepRecovery" },
                () => new JabStep()),

            // #308 (PR #349). Added in the SAME follow-up that #336's comment
            // above predicted would be needed: that comment records jab step
            // being missed on arrival, and in-and-out was then missed the exact
            // same way -- the family list is a hand-maintained duplicate of
            // MoveAnimResolver.ClippedMovePrefixes, so nothing fails when the
            // two drift and the probe just reports a smaller "every family".
            //
            // Unhanded, and unlike every other unhanded family here the ball
            // NEVER swaps hands (InAndOut.cs) -- which is why it must stay out
            // of MoveAnimResolver.HandedMoves. The ctor takes a burstDirection
            // (the move carries a burst payload even though it is not
            // hand-directional); +1f is the right-side burst, matching the
            // fixed polarity the clip is baked to.
            new Family("inandout", "In-and-out (PR #349 / #308)",
                "assets/inandout_authored.fbx",
                new (string, int?)[]
                {
                    ("inandoutstartup",  inandout.StartupFrames),
                    ("inandoutactive",   inandout.ActiveFrames),
                    ("inandoutrecovery", inandout.RecoveryFrames),
                },
                new[] { "InAndOutStartup", "InAndOutActive", "InAndOutRecovery" },
                () => new InAndOut(1f)),

            // #305. Registered ON ARRIVAL rather than in a follow-up, which is
            // the whole point of the comment above: jab step was missed, then
            // in-and-out was missed the same way, because this list is a
            // hand-maintained duplicate of MoveAnimResolver.ClippedMovePrefixes
            // and nothing fails when the two drift.
            //
            // JAB STEP'S TWIN — same 3/2/4 ticks off the same
            // assets/Dribble.fbx, separated only by torso lean sign. Reading
            // the two families' output side by side in this probe is the
            // fastest way to eyeball that they have not converged; the
            // automated version is JabStepAnimTest's
            // jabstep-differs-from-retreatdribble (#333).
            //
            // Unhanded (the ball never leaves the dribbling hand), so it must
            // stay out of MoveAnimResolver.HandedMoves. The ctor takes no burst
            // direction at all — unlike in-and-out, the retreat is a fixed hop
            // straight back along Heading, applied by PlayerController rather
            // than carried in the clip. It DOES need the live dribble
            // StartLiveDribble establishes: RetreatDribble sits inside
            // BeginCommittedMove's dead-dribble gate.
            new Family("retreatdribble", "Retreat dribble (#305)",
                "assets/retreatdribble_authored.fbx",
                new (string, int?)[]
                {
                    ("retreatdribblestartup",  retreatdribble.StartupFrames),
                    ("retreatdribbleactive",   retreatdribble.ActiveFrames),
                    ("retreatdribblerecovery", retreatdribble.RecoveryFrames),
                },
                new[] { "RetreatDribbleStartup", "RetreatDribbleActive", "RetreatDribbleRecovery" },
                () => new RetreatDribble()),
        };
    }

    private Family[] _families;
    private BallController _ball;
    private PlayerController _actor;
    private PlayerController _other;

    private int _frame;
    private int _familyIndex;
    private int _familyDeadlineFrame;
    private bool _staticDone;
    private bool _allDone;
    private double _holdElapsed;

    private enum Step { AwaitTipoff, ConfirmDribble, StartFamily, ObserveFamily, Hold }
    private Step _step = Step.AwaitTipoff;

    // Live-setup state. The tipoff holder and whether the ball actually reached
    // Dribbling — two families depend on it (see StartLiveDribble).
    private int _holderId;
    private bool _dribbleLive;

    // Frame on which the dribble family flips the ball hand, or 0 outside it.
    private int _handFlipFrame;

    // Per-family live readings, keyed by state name: the LAST-tick departure from
    // rest and how many pose-valid ticks that phase was observed for.
    private readonly Dictionary<string, (float Degrees, int Ticks)> _liveByState = new();
    private string _currentState = "";
    private int _currentStateTicks;

    // The accumulated report. Built once, re-printed forever (see hold protocol).
    private readonly List<string> _report = new();

    public override void _Ready()
    {
        _families = BuildFamilies();

        // The engine version goes INTO the evidence. The MCP editor on this
        // machine is 4.6.3 while project.godot declares 4.7 and the csproj pins
        // Godot.NET.Sdk/4.7.1, so any result read off this probe carries that
        // caveat and it must not be reconstructed from memory later.
        Line("");
        Line("================================================================");
        Line("  AUTHORED-CLIP MCP PROBE — live-editor verification");
        Line($"  engine   : {Engine.GetVersionInfo()["string"]}");
        Line($"  ticks/s  : {Engine.PhysicsTicksPerSecond}");
        Line("================================================================");

        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        _actor = scene.Instantiate<PlayerController>();
        _actor.Name = "1";
        _other = scene.Instantiate<PlayerController>();
        _other.Name = "2";

        // README trap 6 — under the default Idle callback GetCurrentNode() lags
        // or skips, so the phase label would not be in lockstep with the pose
        // being sampled on the same tick.
        foreach (var p in new[] { _actor, _other })
            p.GetNode<AnimationTree>("AnimationTree").CallbackModeProcess =
                AnimationMixer.AnimationCallbackModeProcess.Physics;

        var players = new Node3D { Name = "Players" };
        players.AddChild(_actor);
        players.AddChild(_other);

        _ball = new BallController { Name = "Ball", Players = players };

        AddChild(players); // matches scenes/Main.tscn ordering: Players, then Ball
        AddChild(_ball);
    }

    public override void _PhysicsProcess(double delta)
    {
        _frame++;

        switch (_step)
        {
            case Step.AwaitTipoff:
                if (_frame < ArmFrames) break;
                _actor.GlobalPosition = ActorSpot;
                _other.GlobalPosition = OtherSpot;
                _actor.SetHeadingForHarness(
                    Mathf.Atan2(RimCenter.X - ActorSpot.X, RimCenter.Z - ActorSpot.Z));
                if (_frame < ArmFrames + SettleFrames) break;

                // Advance the step BEFORE the fallible work, and catch. Godot
                // logs an exception thrown out of _PhysicsProcess and calls the
                // node again next tick, so a throw here with the step advanced
                // afterwards re-runs the pass every single frame — 25k lines of
                // one stack trace, which is how the 4.6.3/4.7.1 ABI split below
                // first presented. A probe that cannot finish must still report.
                _step = Step.ConfirmDribble;
                _familyDeadlineFrame = _frame + SettleFrames;

                // Before the static pass, because the static pass is the fallible
                // half: the live setup must happen even if the ABI split below
                // kills the resource inspection.
                StartLiveDribble();

                try
                {
                    RunStaticPass();
                    _staticDone = true;
                }
                catch (Exception ex)
                {
                    // The likeliest cause by far, so it is named rather than left
                    // for the reader: this assembly is compiled against the
                    // csproj's pinned Godot.NET.Sdk (4.7.1) and several GodotSharp
                    // signatures changed width in 4.7 — Animation.Length is float
                    // in 4.6.3 and double in 4.7. Running under the older binary
                    // throws MissingMethodException on the FIRST such call.
                    Line("");
                    Line($"FATAL during the static pass: {ex.GetType().Name}: {ex.Message}");
                    Line("If this is a MissingMethodException, the running Godot binary does not match the");
                    Line("csproj's pinned Godot.NET.Sdk version. Check the engine line at the top of this");
                    Line("report against <Project Sdk=\"Godot.NET.Sdk/...\"> in \"HOOPER GAME.csproj\".");
                }
                break;

            case Step.ConfirmDribble:
                if (_frame < _familyDeadlineFrame) break;
                ConfirmDribble();
                _step = Step.StartFamily;
                break;

            case Step.StartFamily:
                StartFamily();
                break;

            case Step.ObserveFamily:
                ObserveFamily();
                break;

            case Step.Hold:
                _holdElapsed += delta;
                if (_holdElapsed >= HoldReprintSeconds)
                {
                    _holdElapsed = 0.0;
                    ReprintReport();
                }
                break;
        }
    }

    // ── Live setup: get the ball off the tipoff and onto a live dribble ──────
    //
    // #193's dead-dribble gate is the single reason TWO families were previously
    // unmeasurable here, and both failures presented as something else:
    //
    //   * BehindTheBack is a dribble-family move, so BeginCommittedMove refuses
    //     it outright while the holder's ball is Held. That surfaced as
    //     "BeginMoveForHarness returned false (machine was not Inactive)" — a
    //     misleading message, since the machine WAS Inactive.
    //   * DribbleLeft/DribbleRight are not committed-move states at all. They
    //     resolve off POSSESSION (holder + BallState.Dribbling), so no amount of
    //     beginning moves reaches them.
    //
    // #193's tipoff deliberately starts the ball Held, so a probe that only waits
    // for TryAssignTipoffHolder never gets a live dribble. TryStartDribble is the
    // same production call BehindTheBackAnimTest/CrossoverAnimTest use.
    private void StartLiveDribble()
    {
        _holderId = _ball.StateMachine.HolderPeerId;
        if (_holderId == 0) return;   // reported in ConfirmDribble, not swallowed
        _ball.TryStartDribble(_holderId);
    }

    private void ConfirmDribble()
    {
        Line("");
        if (_holderId == 0)
        {
            Line("LIVE SETUP: tipoff never assigned a holder — the dribble stance and " +
                 "behind-the-back families cannot run. Every OTHER family is unaffected " +
                 "(they are not possession-gated).");
            return;
        }

        _dribbleLive = _ball.State == BallState.Dribbling;
        Line($"LIVE SETUP: tipoff holder = peer {_holderId}; the actor is peer '{_actor.Name}'. " +
             $"After TryStartDribble the ball is {_ball.State}.");

        // Stated rather than assumed: if the tipoff ever hands the ball to peer 2,
        // the actor is not the holder and the two possession-gated families would
        // read as absent clips rather than as a setup miss.
        if (_holderId.ToString() != _actor.Name)
            Line("   !! the actor is NOT the holder — DribbleLeft/Right and BehindTheBack " +
                 "readings below describe a player without the ball and are not evidence.");

        if (!_dribbleLive)
            Line("   !! not Dribbling — DribbleLeft/Right are unreachable and BehindTheBack " +
                 "cannot Begin (#193 dead-dribble gate). Their LIVE lines below are a SETUP " +
                 "failure, not a clip defect.");
    }

    // ── Static pass: resource + binding inspection, no live playback ─────────
    private void RunStaticPass()
    {
        var lib = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        if (lib == null)
        {
            Line("FATAL: assets/locomotion.res failed to load — nothing below is meaningful.");
            return;
        }

        var rig = FindSkeleton(_actor);
        var trackBase = _actor.GetNodeOrNull<Node>("CharacterModel");
        if (rig == null || trackBase == null)
        {
            Line("FATAL: no Skeleton3D / CharacterModel under the instantiated Player.tscn — " +
                 "every binding number below would be a vacuous zero, so they are not printed.");
            return;
        }

        var nonLeaf = NonLeafBones(rig);
        Line("");
        Line($"RIG: {rig.GetBoneCount()} bones, {nonLeaf.Count} non-leaf " +
             $"(leaf terminators are inert; a gap only matters on a spine/limb/foot bone).");
        Line($"TRACK BASE: AnimationTree.root_node -> '{_actor.GetNode<AnimationTree>("AnimationTree").RootNode}' " +
             $"= {trackBase.Name}; a healthy track path is 'Skeleton3D:mixamorig_<Bone>'.");

        RunBindingSelfTest(lib, trackBase, rig);

        double tps = Engine.PhysicsTicksPerSecond;

        foreach (var fam in _families)
        {
            Line("");
            Line($"── {fam.Label}");
            Line($"   source: {fam.SourceFbx}");

            foreach (var (clipName, ticks) in fam.Clips)
            {
                if (!lib.HasAnimation(clipName))
                {
                    Line($"   [MISSING] '{clipName}' is not in locomotion.res.");
                    continue;
                }

                Animation anim = lib.GetAnimation(clipName);

                // ── duration ──
                // NOT anim.Length. This probe's whole point is to run inside
                // whatever editor the human has open, and Animation.Length is
                // `float` in 4.6.x but `double` in 4.7 — a C# SIGNATURE change, so
                // an assembly compiled against the csproj's pinned 4.7.1 SDK
                // throws MissingMethodException on the first call under a 4.6
                // editor and the entire static pass is lost. The Variant accessor
                // is width-agnostic and binds correctly under both.
                double length = anim.Get("length").AsDouble();

                string durationNote;
                if (ticks is int want)
                {
                    double expected = want / tps;
                    double deviation = Math.Abs(length - expected);
                    // 1e-3 s is ~100x the observed slice noise and ~17x tighter
                    // than the smallest retune (one tick = 1/60 s) that could
                    // occur, so it is a noise band, not a drift allowance.
                    string flag = deviation > 1e-3 ? "  <-- OFF WINDOW" : "";
                    durationNote = $"len={length:F6}s want={expected:F6}s ({want}t) dev={deviation:F6}s{flag}";
                }
                else
                {
                    durationNote = $"len={length:F6}s (looping stance — no tick window)";
                }

                // ── binding + coverage ──
                var (rot, boundPaths, unresolved, tracked) = InspectTracks(anim, trackBase, rig);
                var missing = nonLeaf.Where(b => !tracked.Contains(b)).ToList();

                string bindFlag = unresolved.Count > 0 ? "  <-- UNRESOLVED (trap 13)" : "";
                Line($"   {clipName,-28} {durationNote}");
                Line($"   {"",-28} rot3D={rot} bound={boundPaths} unresolved={unresolved.Count}{bindFlag} " +
                     $"coverage={nonLeaf.Count - missing.Count}/{nonLeaf.Count} non-leaf");

                if (unresolved.Count > 0)
                    Line($"   {"",-28} unresolved paths: [{string.Join(", ", unresolved.Take(6))}" +
                         $"{(unresolved.Count > 6 ? ", …" : "")}]");
                if (missing.Count > 0)
                    Line($"   {"",-28} uncovered non-leaf: [{string.Join(", ", missing)}]");
            }
        }
    }

    /// <summary>
    /// CONTROL for every "unresolved=0" this probe prints.
    ///
    /// A binding checker that cannot go red is not evidence, it is decoration —
    /// and this repo has already shipped exactly that: #281's rebuild reported
    /// "unresolved=[]" while all 198 of the clip's tracks failed to bind, because
    /// the check skipped the very shape that was broken (README trap 15).
    ///
    /// So take a real shipped clip, apply trap 13's defect to an in-memory copy
    /// (Blender's exporter wraps the skeleton in an Armature object and emits
    /// "Armature/Skeleton3D:mixamorig_Hips", which resolves to nothing under
    /// CharacterModel), and run the SAME InspectTracks over both. The healthy copy
    /// must read clean and the mutated one must read fully broken. If they do not
    /// SEPARATE, every binding number in this report is worthless and says so.
    ///
    /// Duplicate(true) — the library's own copy is never touched.
    /// </summary>
    private void RunBindingSelfTest(AnimationLibrary lib, Node trackBase, Skeleton3D rig)
    {
        const string ProbeClip = "blockstartup";

        Line("");
        if (!lib.HasAnimation(ProbeClip))
        {
            Line($"SELF-TEST: SKIPPED — '{ProbeClip}' is not in locomotion.res, so the binding " +
                 "numbers below have NO control and must not be read as evidence.");
            return;
        }

        Animation healthy = lib.GetAnimation(ProbeClip);
        var (_, healthyBound, healthyUnresolved, _) = InspectTracks(healthy, trackBase, rig);

        var mutated = (Animation)healthy.Duplicate(true);
        for (int t = 0; t < mutated.GetTrackCount(); t++)
            mutated.TrackSetPath(t, new NodePath("Armature/" + mutated.TrackGetPath(t)));
        var (_, mutatedBound, mutatedUnresolved, _) = InspectTracks(mutated, trackBase, rig);

        bool separates = healthyUnresolved.Count == 0 && healthyBound > 0
                         && mutatedBound == 0 && mutatedUnresolved.Count > 0;

        Line($"SELF-TEST (control for every unresolved=0 below): '{ProbeClip}' as shipped reads " +
             $"bound={healthyBound} unresolved={healthyUnresolved.Count}; the SAME clip with trap 13's " +
             $"\"Armature/\" prefix injected reads bound={mutatedBound} unresolved={mutatedUnresolved.Count}.");
        Line(separates
            ? "           -> the detector separates bound from unbound, so the zeros below are evidence."
            : "           -> *** THE DETECTOR DID NOT SEPARATE THEM. Every binding number below is " +
              "vacuous — do not report this run as a pass. ***");
    }

    /// <summary>
    /// Counts ROTATION_3D tracks and splits them into bound vs unresolved
    /// against the LIVE rig, and collects the bone names actually covered.
    ///
    /// Two independent failure modes, deliberately not collapsed into one
    /// number: a path can name a real BONE while its NODE PATH resolves to
    /// nothing (README trap 13 — #281 shipped a clip that failed this on all
    /// 198 tracks while passing bone-name matching on all of them).
    /// </summary>
    private static (int Rot, int Bound, List<string> Unresolved, HashSet<string> Tracked)
        InspectTracks(Animation anim, Node trackBase, Skeleton3D rig)
    {
        int rot = 0, bound = 0;
        var unresolved = new List<string>();
        var tracked = new HashSet<string>();

        for (int t = 0; t < anim.GetTrackCount(); t++)
        {
            // ROTATION_3D only (#330): a SCALE-only bone still has its rotation
            // written from rest, which is the whole a45bd1d trap, so counting it
            // as covered would be a false negative.
            if (anim.TrackGetType(t) != Animation.TrackType.Rotation3D) continue;
            rot++;

            NodePath path = anim.TrackGetPath(t);

            // REJECTED, not skipped (trap 15). Skipping a subname-less path is
            // how #281's rebuild reported "unresolved=[]" while every track
            // failed to bind — the exemption covered exactly the broken tracks.
            if (path.GetSubNameCount() == 0)
            {
                unresolved.Add($"{path} (no bone subname)");
                continue;
            }

            string boneName = path.GetSubName(0);
            string nodePart = path.GetConcatenatedNames();

            // The node-path half. "Armature/Skeleton3D" resolves to nothing under
            // CharacterModel, and Godot only LOGS that — the clip plays as a
            // silent no-op that every duration and reachability gate still passes.
            Node target = string.IsNullOrEmpty(nodePart) ? trackBase : trackBase.GetNodeOrNull(nodePart);
            if (target is not Skeleton3D skel)
            {
                unresolved.Add($"{path} (node '{nodePart}' is not a Skeleton3D under {trackBase.Name})");
                continue;
            }

            // The bone-name half.
            if (skel.FindBone(boneName) < 0)
            {
                unresolved.Add($"{path} (no bone '{boneName}' on the rig)");
                continue;
            }

            bound++;
            tracked.Add(boneName);
        }

        return (rot, bound, unresolved, tracked);
    }

    // ── Live pass: play each family and read the rig ─────────────────────────
    private void StartFamily()
    {
        if (_familyIndex >= _families.Length)
        {
            EnterHold();
            return;
        }

        var fam = _families[_familyIndex];
        _liveByState.Clear();
        _currentState = "";
        _currentStateTicks = 0;

        if (fam.MakeMove == null)
        {
            // The dribble stance is reached through POSSESSION, not through
            // BeginCommittedMove — MoveAnimResolver's #294 hand branch maps
            // "holder + Dribbling" straight to DribbleLeft/DribbleRight, ahead of
            // the per-move gate. Nothing to begin.
            //
            // Only ONE polarity is current at a time, so this family is observed
            // in two halves with a hand flip between them. Observing it once would
            // always print "NOT OBSERVED" for the other side, which reads exactly
            // like a missing state — a false alarm indistinguishable from the real
            // defect this probe hunts.
            _actor.SetHandSideForHarness(HandSide.Left);
            _handFlipFrame = _frame + ObserveFramesPerFamily / 2;
            _step = Step.ObserveFamily;
            _familyDeadlineFrame = _frame + ObserveFramesPerFamily;
            return;
        }

        _handFlipFrame = 0;

        // Pin the origin hand before every committed move. BehindTheBack suffixes
        // its state names by ORIGIN hand, and the family above leaves the actor on
        // Right — without this reset the behind-the-back family would run
        // right-origin while looking up its three *Left states, and report all
        // three as missing. Inert for steal/layup/contest/block, whose state names
        // do not come from the actor's ball hand.
        _actor.SetHandSideForHarness(HandSide.Left);

        if (!_actor.BeginMoveForHarness(fam.MakeMove()))
        {
            Line("");
            Line($"!! {fam.Label}: BeginMoveForHarness returned false — either a move was already " +
                 $"running, or a begin gate rejected it (ball state = {_ball.State}; a dribble-family " +
                 "move needs Dribbling, #193). This family's LIVE readings are unavailable; its " +
                 "static numbers above still stand.");
            _familyIndex++;
            return;
        }

        _step = Step.ObserveFamily;
        _familyDeadlineFrame = _frame + ObserveFramesPerFamily;
    }

    private void ObserveFamily()
    {
        // Halfway through the dribble family, swap the ball to the other hand so
        // the second stance state becomes current (see StartFamily).
        if (_handFlipFrame != 0 && _frame == _handFlipFrame)
            _actor.SetHandSideForHarness(HandSide.Right);

        string node = _actor.ActiveAnimNodeForHarness;
        var skel = FindSkeleton(_actor);

        if (skel != null && !string.IsNullOrEmpty(node))
        {
            if (node != _currentState)
            {
                _currentState = node;
                _currentStateTicks = 0;
            }

            _currentStateTicks++;

            // Drop the phase's FIRST observed tick — the label leads the pose by
            // one tick even under the Physics callback, so tick 1 still holds the
            // PREVIOUS phase's pose. Recording it would attribute the outgoing
            // phase's geometry to the incoming one.
            if (_currentStateTicks > 1)
            {
                float deg = MaxDepartureFromRestDegrees(skel);
                // Overwrite, never max: only the LAST-tick reading separates a
                // bound clip (holds its pose) from an unbound one (collapsed to
                // rest within a tick of entry). See the header's metric note.
                _liveByState[node] = (deg, _currentStateTicks - 1);
            }
        }

        if (_frame >= _familyDeadlineFrame)
        {
            ReportFamilyLive(_families[_familyIndex]);
            _familyIndex++;
            _step = Step.StartFamily;
        }
    }

    private void ReportFamilyLive(Family fam)
    {
        Line("");
        Line($"── LIVE: {fam.Label}");

        foreach (string state in fam.States)
        {
            if (!_liveByState.TryGetValue(state, out var reading))
            {
                Line($"   {state,-30} NOT OBSERVED — the tree never entered this state during " +
                     $"{ObserveFramesPerFamily} ticks.");
                continue;
            }

            // ~0 deg on the last tick of a phase is the trap-13 signature: the
            // state was entered, the duration was right, and the clip drove
            // nothing. A bound clip reads tens-to-180 deg here.
            string flag = reading.Degrees < 1.0f ? "  <-- AT REST (clip drove nothing)" : "";
            string thin = reading.Ticks < 2 ? "  <-- only 1 pose-valid tick" : "";
            Line($"   {state,-30} last-tick departure from rest = {reading.Degrees,7:F2} deg " +
                 $"over {reading.Ticks} pose-valid tick(s){flag}{thin}");
        }

        // States the tree visited that this family did not expect. A generic
        // "Startup"/"Active"/"Recovery" here is the #296 fallback leaking, which
        // means the move never reached its own clips at all.
        var unexpected = _liveByState.Keys
            .Where(k => !fam.States.Contains(k) && k != "Locomotion" && k != "DribbleLeft" && k != "DribbleRight")
            .ToList();
        if (unexpected.Count > 0)
            Line($"   also visited: [{string.Join(", ", unexpected)}]" +
                 (unexpected.Any(u => u is "Startup" or "Active" or "Recovery")
                     ? "  <-- GENERIC #296 FALLBACK LEAKED"
                     : ""));
    }

    private void EnterHold()
    {
        _allDone = true;
        _step = Step.Hold;
        Line("");
        Line("================================================================");
        Line("  PROBE COMPLETE — holding. Re-printing every " +
             $"{HoldReprintSeconds:F0}s so any MCP poll catches the full report.");
        Line("  Stop with mcp__godot__stop_project.");
        Line("================================================================");
        ReprintReport();
    }

    private void ReprintReport()
    {
        // The whole report, not a tail. get_debug_output is a snapshot, so a
        // partial re-print would be a partial verdict.
        GD.Print($"[probe] ===== REPORT (re-print, static={_staticDone} complete={_allDone}) =====");
        foreach (string line in _report) GD.Print($"[probe] {line}");
    }

    // ── Geometry ────────────────────────────────────────────────────────────
    //
    // The angle between each bone's CURRENT pose rotation and its REST rotation,
    // maxed across non-leaf bones. Measured against the LIVE rig's rest (which
    // BlendRestAnchor has already re-anchored for two bones) on purpose: rest is
    // literally what the mixer writes for an untracked or unbound bone, so the
    // live rest is the correct reference for "did this clip move anything".
    //
    // NaN, never 0, when there is no rig — a dead instrument must be legible as
    // dead rather than reporting a perfectly-at-rest 0.00 that reads exactly like
    // the trap-13 defect this probe is hunting.
    private static float MaxDepartureFromRestDegrees(Skeleton3D skel)
    {
        if (skel.GetBoneCount() == 0) return float.NaN;

        float max = 0f;
        foreach (int b in NonLeafBoneIndices(skel))
        {
            Quaternion rest = skel.GetBoneRest(b).Basis.GetRotationQuaternion().Normalized();
            Quaternion pose = skel.GetBonePoseRotation(b).Normalized();
            float deg = Mathf.RadToDeg(rest.AngleTo(pose));
            if (deg > max) max = deg;
        }
        return max;
    }

    /// <summary>
    /// Bones with at least one child. Y Bot is 65 bones but only 52 non-leaf;
    /// the leaf terminators carry no geometry of their own, so including them
    /// dilutes every coverage number with inert finger tips (#316's lesson).
    /// </summary>
    private static List<int> NonLeafBoneIndices(Skeleton3D skel)
    {
        var hasChild = new bool[skel.GetBoneCount()];
        for (int b = 0; b < skel.GetBoneCount(); b++)
        {
            int parent = skel.GetBoneParent(b);
            if (parent >= 0) hasChild[parent] = true;
        }
        return Enumerable.Range(0, skel.GetBoneCount()).Where(b => hasChild[b]).ToList();
    }

    private static List<string> NonLeafBones(Skeleton3D skel) =>
        NonLeafBoneIndices(skel).Select(skel.GetBoneName).ToList();

    private static Skeleton3D FindSkeleton(Node root)
    {
        if (root is Skeleton3D s) return s;
        foreach (Node child in root.GetChildren())
        {
            Skeleton3D found = FindSkeleton(child);
            if (found != null) return found;
        }
        return null;
    }

    private void Line(string text)
    {
        _report.Add(text);
        GD.Print($"[probe] {text}");
    }
}
