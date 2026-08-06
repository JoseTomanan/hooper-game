using System;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #313 (ADR-0016): proves the THREE
// LAYUP ANIMATION STATES (LayupStartup / LayupActive / LayupRecovery) wired
// into scenes/Player.tscn are real — entered end-to-end by a real Layup, bound
// to the right clips, cut to the right windows, and actually MOVING the rig.
//
// Before #313 "layup" fell through MoveAnimResolver.ResolveStateName's default
// case onto the shared generic Startup/Active/Recovery states, which per #296
// play locomotion/idle for BOTH Startup and Recovery (pixel-identical — an
// opponent cannot tell "committing" from "in the punish window") and a looping
// locomotion/run for Active. On a rim attack that is an outright false read.
//
//   godot --headless --path . res://tests/integration/LayupAnimTest.tscn -- --harness-scenario=layup-phases
//   …=layup-no-placeholder-leak | layup-segment-lengths | layup-startup-differs-from-recovery
//   …=layup-edges | layup-airborne-active | control-layup-grounded-startup
//   …=layup-arm-extends-overhead | control-layup-arm-low-startup
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//
// ── Cosmetic-only (ADR-0022) ────────────────────────────────────────────────
// #313 is a CLIP issue. It does not observe or feed LayupRangeResolver, #236's
// layup-range fallback, or ADR-0023's authoritative-gate tolerance. This
// harness deliberately begins the move via BeginMoveForHarness — i.e.
// DOWNSTREAM of the server's range gate — precisely so it cannot accidentally
// become a second, weaker test of gating that LayupTest's range-gate-* scenarios
// already own through RequestMoveForHarness.
//
// ── Why ActiveAnimNodeForHarness, not the resolver's return value (#257) ─────
// Travel() to a missing/misnamed state only LOGS; it never throws. Asserting
// MoveAnimResolver.ResolveStateName(...) == "LayupActive" would pass on a
// Player.tscn with no Layup states at all, since the resolver has no notion the
// .tscn exists. Only the live AnimationNodeStateMachinePlayback proves wiring.
//
// ── What "layup-edges" adds, and why it is not the forbidden edge assertion ──
// README trap 8 / #279 established that a DELETED transition edge is invisible
// to GetCurrentNode(): Travel() is a pathfinder, so it routes around the gap and
// still arrives. That verdict is specific to RUNTIME OBSERVATION, and
// JumpshotAnimTest's header names the instrument that would work but does not
// build it — "inspecting the AnimationNodeStateMachine resource's transition
// list directly." That is what layup-edges does: it reads
// GetTransitionCount()/GetTransitionFrom()/GetTransitionTo() off the resource,
// where a missing edge is simply absent. Unlike the reachability scenarios, this
// one DOES redden when an edge is deleted (mutation-verified, see the PR).
//
// ── Why the geometric gates re-measure what Blender already checked ──────────
// tools/author_layup.py runs its own verify_airborne()/verify_grounded() gates,
// and tools/rebuild_layup_clips.gd re-runs them on the sliced clips. Both read
// the BLENDER-side or RESOURCE-side pose. Neither can see the failure that
// actually shipped in #281: a clip whose track paths carry Blender's "Armature/"
// object wrapper binds to NOTHING on this rig (whose skeleton sits at
// "Skeleton3D"), so Godot logs "couldn't resolve track", carries on, and the
// clip is a SILENT no-op — reachability, durations and state->clip mapping all
// still pass. These scenarios read the LIVE Skeleton3D mid-move, which is the
// only place that failure is visible.
public partial class LayupAnimTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int ArmFrames = 2;      // ticks for TryAssignTipoffHolder to run
    private const int ActFrames = 2;      // ticks after tipoff before Begin (position/heading settle)
    private const int ObserveFrames = 60; // > startup(8)+active(4)+recovery(14)=26, with generous slack

    // 2 m from RimCenter's XZ — inside LayupRange (default 4.0 m), matching
    // LayupTest's convention. Not load-bearing here (BeginMoveForHarness is
    // downstream of the range gate) but keeps the scenario physically coherent.
    private static readonly Vector3 ShooterSpot = new(0f, 0f, 2f);
    private static readonly Vector3 FarSpot = new(12f, 0f, 12f); // out of PickupRadius
    private static readonly Vector3 RimCenter = new(0f, 3.05f, 0f); // BallController.DefaultRimCenter

    // Upper-body bones for the Startup-vs-Recovery pose comparison. Same set
    // BehindTheBackAnimTest uses, and for its reason: these are the bones a
    // spectator reads a commitment arc off.
    private static readonly string[] UpperBodyBones =
    {
        "mixamorig_LeftArm", "mixamorig_RightArm",
        "mixamorig_LeftForeArm", "mixamorig_RightForeArm",
        "mixamorig_Spine",
    };

    // Thresholds are MEASURED, not assumed — each is set well inside the
    // observed working value and well outside the observed broken value; the
    // per-scenario comments carry both numbers.
    private const float StartupVsRecoveryMinDeg = 15.0f;
    private const float AirborneMinRise = 0.15f;    // skeleton-local units, see MeasureHipY
    private const float GroundedMaxRise = 0.08f;    // the control's ceiling
    // The wind-up hand must be lower than the release hand by a real margin, not
    // merely "<". A bare inequality would accept a clip holding the arm overhead
    // for the entire move (startup 0.250 vs active 0.258 "passes"), which is
    // exactly the un-telegraphed pose ADR-0003 forbids — the wind-up has to be
    // readable as a wind-up.
    private const float ArmRiseMinMargin = 0.10f;

    private static readonly string[] KnownScenarios =
    {
        "layup-phases",
        "layup-no-placeholder-leak",
        "layup-segment-lengths",
        "layup-startup-differs-from-recovery",
        "layup-edges",
        "layup-airborne-active",
        "control-layup-grounded-startup",
        "layup-arm-extends-overhead",
        "control-layup-arm-low-startup",
    };

    // Scenarios that need no live tree — pure resource/scene inspection.
    private static readonly string[] StaticScenarios =
    {
        "layup-no-placeholder-leak", "layup-segment-lengths", "layup-edges",
    };

    private string _scenario = "layup-phases";

    private BallController _ball;
    private PlayerController _shooter; // peer "1" — the tipoff holder (ADR-0007)
    private PlayerController _other;   // peer "2"

    private int _frame;
    private double _elapsed;
    private bool _finished;

    private enum Step { AwaitTipoff, Act, Observe }
    private Step _step = Step.AwaitTipoff;
    private int _stepDeadlineFrame;

    // Latched (event-time) observations. The three phase latches can only turn
    // true in order — each guard requires the previous already latched — so
    // "saw all three" IS "saw them in order."
    private bool _sawLayupStartup;
    private bool _sawLayupActive;
    private bool _sawLayupRecovery;
    private bool _sawGenericPlaceholder;

    // Geometry, latched at event time (never recomputed at verdict time — by
    // then the move is over and the rig has returned to Locomotion).
    private bool _haveHipBaseline;
    private float _hipBaselineY;          // hip height in the PRE-MOVE stance (see Observe)
    private float _maxHipRiseDuringStartup;
    private float _maxHipRiseDuringActive;
    // NegativeInfinity, not 0 — a wrist BELOW the head is a legitimate (and for
    // Startup, expected) reading, and seeding these at 0 would floor it there,
    // printing a confident "0.0000" for a hand that is actually well below the
    // head and weakening the control's margin to nothing.
    private float _maxWristAboveHeadDuringStartup = float.NegativeInfinity;
    private float _maxWristAboveHeadDuringActive = float.NegativeInfinity;
    private Quaternion[] _poseAtLastStartupTick;
    private Quaternion[] _poseAtLastRecoveryTick;

    // How many ticks Observe() named each phase on — INCLUDING the first, which
    // the geometry latches then drop (#340; see Observe). The verdicts gate on
    // "> 1" so that a phase shortened to a single tick fails loudly instead of
    // silently measuring nothing, which is the failure mode a phase-localised
    // gate with no samples would otherwise have.
    private int _startupTicksObserved;
    private int _activeTicksObserved;
    private int _recoveryTicksObserved;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "layup-phases");
        GD.Print($"[layup-anim] scenario={_scenario} booting headless…");

        if (!KnownScenarios.Contains(_scenario))
        {
            // A ci.yml typo must be a RED run, not a silently-defaulted green one.
            Fail($"unknown scenario '{_scenario}'.");
            Finish();
            return;
        }

        if (StaticScenarios.Contains(_scenario))
        {
            RunStaticCheck();
            return;
        }

        // Real Player.tscn instances (live AnimationTree + Skeleton3D), named
        // "1"/"2" so the OfflineMultiplayerPeer makes unique_id 1 both IsServer
        // and IsLocalPlayer — the full TickServerOwnPlayer -> ApplyAnimation
        // chain runs every tick, same as DribbleLoopTest/JumpshotAnimTest.
        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        _shooter = scene.Instantiate<PlayerController>();
        _shooter.Name = "1";
        _other = scene.Instantiate<PlayerController>();
        _other.Name = "2";

        // Physics-callback lockstep so GetCurrentNode() reflects the same-tick
        // Travel() (README trap 6 — the default Idle callback lags headless).
        foreach (var p in new[] { _shooter, _other })
            p.GetNode<AnimationTree>("AnimationTree").CallbackModeProcess =
                AnimationMixer.AnimationCallbackModeProcess.Physics;

        var players = new Node3D { Name = "Players" };
        players.AddChild(_shooter);
        players.AddChild(_other);

        _ball = new BallController { Name = "Ball", Players = players };

        AddChild(players); // matches scenes/Main.tscn: Players before Ball
        AddChild(_ball);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        switch (_step)
        {
            case Step.AwaitTipoff:
                if (_frame < ArmFrames) break;
                if (_ball.StateMachine.HolderPeerId != 1)
                {
                    Fail($"{_scenario}: tipoff did not assign holder 1 (got {_ball.StateMachine.HolderPeerId}).");
                    Finish();
                    return;
                }
                _shooter.GlobalPosition = ShooterSpot;
                _other.GlobalPosition = FarSpot;
                // Face the rim — a layup is a rim attack; squaring up keeps the
                // pose physically coherent even though nothing here reads facing.
                _shooter.SetHeadingForHarness(
                    Mathf.Atan2(RimCenter.X - ShooterSpot.X, RimCenter.Z - ShooterSpot.Z));
                _step = Step.Act;
                _stepDeadlineFrame = _frame + ActFrames;
                break;

            case Step.Act:
                if (_frame < _stepDeadlineFrame) break;
                // The real production choke point (BeginCommittedMove), reached
                // via the generic seam — deliberately downstream of the range
                // gate, which LayupTest owns.
                if (!_shooter.BeginMoveForHarness(new Layup()))
                {
                    Fail($"{_scenario}: BeginMoveForHarness(new Layup()) returned false — " +
                         "the shooter's machine was not Inactive at begin.");
                    Finish();
                    return;
                }
                _step = Step.Observe;
                _stepDeadlineFrame = _frame + ObserveFrames;
                break;

            case Step.Observe:
                Observe();
                if (_frame >= _stepDeadlineFrame) RenderVerdict();
                break;
        }

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame}, scenario={_scenario}, step={_step}, " +
                 $"lastAnimNode={_shooter?.ActiveAnimNodeForHarness}, sawStartup={_sawLayupStartup}, " +
                 $"sawActive={_sawLayupActive}, sawRecovery={_sawLayupRecovery}.");
            Finish();
        }
    }

    private void Observe()
    {
        string node = _shooter.ActiveAnimNodeForHarness;

        if (!_sawLayupStartup && node == "LayupStartup") _sawLayupStartup = true;
        if (_sawLayupStartup && !_sawLayupActive && node == "LayupActive") _sawLayupActive = true;
        if (_sawLayupActive && !_sawLayupRecovery && node == "LayupRecovery") _sawLayupRecovery = true;
        if (node == "Startup" || node == "Active" || node == "Recovery") _sawGenericPlaceholder = true;

        // ── Geometry, sampled at event time ──────────────────────────────────
        var skel = FindSkeleton(_shooter);
        if (skel == null) return;

        if (node == "LayupStartup" || node == "LayupActive" || node == "LayupRecovery")
        {
            float hipY = MeasureHipY(skel);
            // The baseline is captured on the first layup-named tick — and per
            // #316 that tick still holds the pose from BEFORE the move, because
            // the AnimationTree names the state it travelled to a tick before
            // the mixer writes that state's clip to the Skeleton3D. So this is
            // the standing stance the attempt began from, which is exactly what
            // JumpshotAnimTest latches deliberately (its LatchToeBaseline() runs
            // on the last tick before BeginJumpShotForHarness). Measuring every
            // rise against it keeps the gates relative to this attempt's own
            // footing rather than to a rest pose the clip may never visit.
            //
            // #340 kept this timing rather than moving the latch pre-move: the
            // two are the same pose, and the tick it consumes is one this method
            // now excludes from every phase measurement below anyway.
            if (!_haveHipBaseline)
            {
                _hipBaselineY = hipY;
                _haveHipBaseline = true;
            }
            float rise = hipY - _hipBaselineY;
            float wristAboveHead = MeasureWristAboveHead(skel);

            // #340 / #316: DROP EACH PHASE'S FIRST OBSERVED TICK. For the same
            // reason the baseline above is the pre-move stance, the first tick
            // of Active carries Startup's pose and the first tick of Recovery
            // carries Active's. Folding those into a per-phase Math.Max
            // attributes a neighbouring phase's pose to this one, and on a
            // LOWER-bound gate the leaked pose can satisfy the gate by itself.
            //
            // MEASURED here, not assumed (#340's mutation A/B): with
            // LayupStartup and LayupActive's clips swapped in Player.tscn — so
            // Active plays the GROUNDED startup clip — layup-airborne-active
            // still read active=0.1660 against its 0.15 floor and PASSED. That
            // reading was Startup's leaked airborne pose. With the drop it reads
            // ~0 and fails, which is the correct verdict for a grounded release.
            //
            // Startup/Active/Recovery run 7/4/14 ticks on the current clips, so
            // the drop leaves 6/3/13. If a retune ever shortens a phase to a
            // single tick the latch is left unset and the verdicts' matching
            // *TicksObserved > 1 premises fail loudly, rather than silently
            // measuring nothing. (JabStepAnimTest's 2-tick Active is the
            // tightest case in the game — one sample survives there.)
            if (node == "LayupStartup")
            {
                _startupTicksObserved++;
                if (_startupTicksObserved > 1)
                {
                    _maxHipRiseDuringStartup = Math.Max(_maxHipRiseDuringStartup, rise);
                    _maxWristAboveHeadDuringStartup =
                        Math.Max(_maxWristAboveHeadDuringStartup, wristAboveHead);
                    _poseAtLastStartupTick = SampleUpperBody(skel);
                }
            }
            else if (node == "LayupActive")
            {
                _activeTicksObserved++;
                if (_activeTicksObserved > 1)
                {
                    _maxHipRiseDuringActive = Math.Max(_maxHipRiseDuringActive, rise);
                    _maxWristAboveHeadDuringActive =
                        Math.Max(_maxWristAboveHeadDuringActive, wristAboveHead);
                }
            }
            else
            {
                _recoveryTicksObserved++;
                if (_recoveryTicksObserved > 1)
                {
                    // Overwritten each Recovery tick, so it ends up holding the
                    // LAST one — the same "sample the final tick" discipline
                    // BehindTheBackAnimTest arrived at by mutation: an UNBOUND
                    // clip collapses the rig to rest within a tick (no xfade on
                    // any edge), so the final tick is where bound and unbound
                    // actually separate. The first-tick drop cannot cost us that
                    // final sample unless Recovery is a single tick long, in
                    // which case _poseAtLastRecoveryTick stays null and
                    // layup-startup-differs-from-recovery fails on its existing
                    // null premise.
                    _poseAtLastRecoveryTick = SampleUpperBody(skel);
                }
            }
        }
    }

    private void RenderVerdict()
    {
        switch (_scenario)
        {
            case "layup-phases":                        VerdictPhases(); break;
            case "layup-startup-differs-from-recovery": VerdictStartupDiffersFromRecovery(); break;
            case "layup-airborne-active":               VerdictAirborneActive(); break;
            case "control-layup-grounded-startup":      VerdictControlGroundedStartup(); break;
            case "layup-arm-extends-overhead":          VerdictArmExtendsOverhead(); break;
            case "control-layup-arm-low-startup":       VerdictControlArmLowStartup(); break;
        }
    }

    // ── Scenario: layup-phases (positive) ───────────────────────────────────
    private void VerdictPhases()
    {
        bool pass = _sawLayupStartup && _sawLayupActive && _sawLayupRecovery;
        if (pass)
            GD.Print("[layup-anim] PASS layup-phases — the tree was observed on \"LayupStartup\", then " +
                     "\"LayupActive\", then \"LayupRecovery\", in that order (the .tscn states and their " +
                     "transitions are live).");
        else
            Fail($"layup-phases: expected LayupStartup -> LayupActive -> LayupRecovery, in order; got " +
                 $"sawStartup={_sawLayupStartup}, sawActive={_sawLayupActive}, " +
                 $"sawRecovery={_sawLayupRecovery}, lastAnimNode={_shooter.ActiveAnimNodeForHarness}.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: layup-startup-differs-from-recovery ───────────────────────
    // #296's ACTUAL complaint. On the generic fallback both phases played
    // locomotion/idle, so the wind-up and the punish window were pixel-identical
    // and an opponent could not tell which one they were looking at. Comparing
    // two SAMPLED in-move poses (rather than either against rest) is the honest
    // question here: a bound clip poses them differently; an unbound clip
    // collapses both to rest and the delta goes to ~0.
    //
    // MEASURED, and the floor is placed from the measurements rather than taste:
    //
    //   correct wiring                          39.43 deg   pass
    //   Recovery repointed at the STARTUP clip  21.41 deg   PASS (see below)
    //   both states -> mv277ph (the literal
    //   #296 defect: shared locomotion/idle)     1.52 deg   fail
    //
    // So the 15 deg floor sits an order of magnitude above the defect this
    // scenario is named for, and this scenario genuinely catches it.
    //
    // What it does NOT catch, stated plainly rather than left for a reader to
    // assume: pointing Recovery at the Startup CLIP still measures 21.41 deg and
    // passes here. The two states then play the same clip but sample it at
    // different times (Recovery's 14-tick window outruns the 8-tick clip, which
    // is LOOP_NONE and holds its final frame), so the poses legitimately differ.
    // That mutation is caught by layup-no-placeholder-leak instead, which pins
    // each state to its own clip by name and went red on exactly that edit.
    // Coverage is complete across the two scenarios; neither covers it alone.
    //
    // The floor was deliberately NOT raised above 21.41 to absorb that case:
    // doing so would leave only a ~24% margin under the working 39.43 and turn
    // any future re-author of the clip into a spurious red, in exchange for
    // duplicating coverage another scenario already provides honestly.
    private void VerdictStartupDiffersFromRecovery()
    {
        if (_poseAtLastStartupTick == null || _poseAtLastRecoveryTick == null)
        {
            Fail("layup-startup-differs-from-recovery: never sampled both a Startup and a Recovery tick " +
                 $"(sawStartup={_sawLayupStartup}, sawRecovery={_sawLayupRecovery}) — the premise for " +
                 "comparing them never held, so this fails rather than passes.");
            Finish(1);
            return;
        }

        float worst = 0f;
        for (int i = 0; i < _poseAtLastStartupTick.Length; i++)
            worst = Math.Max(worst, Mathf.RadToDeg(
                _poseAtLastStartupTick[i].AngleTo(_poseAtLastRecoveryTick[i])));

        GD.Print($"[layup-anim]   worst upper-body Startup-vs-Recovery delta = {worst:F2} deg " +
                 $"(floor {StartupVsRecoveryMinDeg:F1}), " +
                 $"ticks su/ac/re = {_startupTicksObserved}/{_activeTicksObserved}/{_recoveryTicksObserved}");

        bool pass = _sawLayupStartup && _sawLayupRecovery && worst >= StartupVsRecoveryMinDeg;
        if (pass)
            GD.Print("[layup-anim] PASS layup-startup-differs-from-recovery — the last Startup pose and the " +
                     $"last Recovery pose differ by {worst:F2} deg on the upper body, so the wind-up and the " +
                     "punish window are visibly distinct silhouettes (#296).");
        else
            Fail($"layup-startup-differs-from-recovery: worst upper-body delta {worst:F2} deg < " +
                 $"{StartupVsRecoveryMinDeg:F1}. Either the two states point at the same clip, or the clips " +
                 "bind to nothing on this rig (check for Blender's 'Armature/' track-path prefix) and both " +
                 "poses collapsed to rest.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: layup-airborne-active (positive) ──────────────────────────
    // A layup that never leaves the ground is a floor-level reach, which reads
    // as nothing at all — and no other assertion in this file would notice.
    // Paired with control-layup-grounded-startup, which asserts the SAME
    // measurement stays near zero during the plant; without that control this
    // could pass on a rig that simply drifts upward for unrelated reasons.
    private void VerdictAirborneActive()
    {
        GD.Print($"[layup-anim]   hip rise: startup={_maxHipRiseDuringStartup:F4} " +
                 $"active={_maxHipRiseDuringActive:F4} (floor {AirborneMinRise:F2}), " +
                 $"ticks su/ac/re = {_startupTicksObserved}/{_activeTicksObserved}/{_recoveryTicksObserved}");

        // _activeTicksObserved > 1 is the post-drop premise: Active's first tick
        // is discarded as Startup's pose (#340), so one observed tick leaves the
        // latch untouched at its seed and this must fail rather than measure it.
        bool pass = _sawLayupActive && _activeTicksObserved > 1
                    && _maxHipRiseDuringActive >= AirborneMinRise;
        if (pass)
            GD.Print($"[layup-anim] PASS layup-airborne-active — the hips rose {_maxHipRiseDuringActive:F4} " +
                     "above the pose the move began from during the Active (release) phase, so the finish " +
                     "genuinely leaves the ground.");
        else
            Fail($"layup-airborne-active: hip rise during Active was {_maxHipRiseDuringActive:F4}, " +
                 $"need >= {AirborneMinRise:F2} (sawActive={_sawLayupActive}). The layup is not leaving the " +
                 "ground — either the clip's Hips position track is missing/unbound, or the slice window " +
                 "does not cover the apex.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: control-layup-grounded-startup (control) ──────────────────
    private void VerdictControlGroundedStartup()
    {
        GD.Print($"[layup-anim]   hip rise: startup={_maxHipRiseDuringStartup:F4} " +
                 $"active={_maxHipRiseDuringActive:F4} (startup ceiling {GroundedMaxRise:F2}), " +
                 $"ticks su/ac/re = {_startupTicksObserved}/{_activeTicksObserved}/{_recoveryTicksObserved}");

        // Premise: the Startup phase must genuinely have been observed, AND the
        // Active phase must genuinely have risen — otherwise "Startup stayed
        // down" is trivially true of a rig where nothing moved at all, which is
        // exactly the vacuous pass this control exists to rule out.
        bool premise = _sawLayupStartup && _startupTicksObserved > 1
                       && _activeTicksObserved > 1
                       && _maxHipRiseDuringActive >= AirborneMinRise;
        bool pass = premise && _maxHipRiseDuringStartup <= GroundedMaxRise;
        if (pass)
            GD.Print($"[layup-anim] PASS control-layup-grounded-startup — the plant stayed down " +
                     $"({_maxHipRiseDuringStartup:F4} <= {GroundedMaxRise:F2}) while the SAME measurement " +
                     $"read {_maxHipRiseDuringActive:F4} during Active, so layup-airborne-active is " +
                     "measuring a real, phase-localised rise rather than a constant offset.");
        else
            Fail($"control-layup-grounded-startup: startupRise={_maxHipRiseDuringStartup:F4} " +
                 $"(ceiling {GroundedMaxRise:F2}), activeRise={_maxHipRiseDuringActive:F4} " +
                 $"(premise floor {AirborneMinRise:F2}), sawStartup={_sawLayupStartup}. If the premise " +
                 "broke, 'the plant stayed grounded' proves nothing, so this fails rather than passes.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: layup-arm-extends-overhead (positive) ─────────────────────
    // The actual content of the finish pose: the ball hand goes ABOVE the head.
    private void VerdictArmExtendsOverhead()
    {
        GD.Print($"[layup-anim]   wrist-above-head: startup={_maxWristAboveHeadDuringStartup:F4} " +
                 $"active={_maxWristAboveHeadDuringActive:F4}, " +
                 $"ticks su/ac/re = {_startupTicksObserved}/{_activeTicksObserved}/{_recoveryTicksObserved}");

        bool pass = _sawLayupActive && _activeTicksObserved > 1
                    && _maxWristAboveHeadDuringActive > 0f;
        if (pass)
            GD.Print($"[layup-anim] PASS layup-arm-extends-overhead — a wrist reached " +
                     $"{_maxWristAboveHeadDuringActive:F4} ABOVE the head bone during Active, so the " +
                     "release is an overhead finish rather than a floor-level reach.");
        else
            Fail($"layup-arm-extends-overhead: best wrist-above-head during Active was " +
                 $"{_maxWristAboveHeadDuringActive:F4}, need > 0 (sawActive={_sawLayupActive}).");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: control-layup-arm-low-startup (control) ───────────────────
    private void VerdictControlArmLowStartup()
    {
        GD.Print($"[layup-anim]   wrist-above-head: startup={_maxWristAboveHeadDuringStartup:F4} " +
                 $"active={_maxWristAboveHeadDuringActive:F4}, " +
                 $"ticks su/ac/re = {_startupTicksObserved}/{_activeTicksObserved}/{_recoveryTicksObserved}");

        // Premise: Active must genuinely have gone overhead, or "Startup did not"
        // is trivially satisfied by a clip in which the arm never moves.
        bool premise = _sawLayupStartup && _startupTicksObserved > 1
                       && _activeTicksObserved > 1
                       && _maxWristAboveHeadDuringActive > 0f;
        float margin = _maxWristAboveHeadDuringActive - _maxWristAboveHeadDuringStartup;
        bool pass = premise && margin >= ArmRiseMinMargin;
        if (pass)
            GD.Print($"[layup-anim] PASS control-layup-arm-low-startup — the wind-up kept the hand " +
                     $"{margin:F4} lower (startup {_maxWristAboveHeadDuringStartup:F4} vs release " +
                     $"{_maxWristAboveHeadDuringActive:F4}, floor {ArmRiseMinMargin:F2}), so the overhead " +
                     "extension is a phase-localised event rather than a pose the clip holds throughout.");
        else
            Fail($"control-layup-arm-low-startup: startup={_maxWristAboveHeadDuringStartup:F4}, " +
                 $"active={_maxWristAboveHeadDuringActive:F4}, margin={margin:F4} " +
                 $"(need >= {ArmRiseMinMargin:F2}), sawStartup={_sawLayupStartup}. If the premise broke, " +
                 "'the arm was low in Startup' proves nothing, so this fails rather than passes.");
        Finish(pass ? 0 : 1);
    }

    // ── Static scenarios: no live tree, pure resource/scene inspection ──────
    private void RunStaticCheck()
    {
        switch (_scenario)
        {
            case "layup-no-placeholder-leak": RunNoPlaceholderLeakCheck(); break;
            case "layup-segment-lengths":     RunSegmentLengthsCheck(); break;
            case "layup-edges":               RunEdgesCheck(); break;
        }
    }

    // ── Scenario: layup-segment-lengths ─────────────────────────────────────
    // #276 rule 4 / #295. Tick windows are read from Layup.DefaultFrameData, NOT
    // hardcoded, so a future #238 retune that forgets to re-run
    // tools/rebuild_layup_clips.gd goes red here and names the tool.
    private void RunSegmentLengthsCheck()
    {
        var lib = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        if (lib == null)
        {
            Fail("assets/locomotion.res failed to load — cannot evaluate layup-segment-lengths.");
            Finish(1);
            return;
        }

        double tps = Engine.PhysicsTicksPerSecond;
        var frames = Layup.DefaultFrameData;
        // NOT one tick. A one-tick bar cannot catch a one-tick retune — bumping
        // StartupFrames 8 → 9 deviates by exactly 1/60 s, slips under, and reports
        // green while layupstartup is still cut to 8 ticks and no longer covers the
        // move's Startup window. That is precisely the staleness this scenario says
        // it catches, so the loose bar voided its own stated purpose (#314 review).
        // Measured deviation on all three layup clips is 0.000000s (re-checkable:
        // the scenario prints deviation= per clip on every run, pass or fail) — the slice is
        // exact and the tolerance only has to absorb float32 `Animation.Length`
        // representation noise (~5e-9 s here). It is a NOISE BAND, not a drift
        // allowance: 1e-3 s is ~17x tighter than the smallest possible retune.
        // If a clip ever lands genuinely near a tick, that is a slice bug to fix,
        // NOT a tolerance to widen back.
        const double ToleranceSeconds = 1e-3;

        (string Clip, int Ticks)[] windows =
        {
            ("layupstartup",  frames.StartupFrames),
            ("layupactive",   frames.ActiveFrames),
            ("layuprecovery", frames.RecoveryFrames),
        };

        bool pass = true;
        foreach (var (clipName, ticks) in windows)
        {
            if (!lib.HasAnimation(clipName))
            {
                Fail($"AnimationLibrary has no clip '{clipName}' — run tools/rebuild_layup_clips.gd.");
                pass = false;
                continue;
            }

            double expectedSeconds = ticks / tps;
            // Variant accessor, NOT .Length: that property is `float` in Godot 4.6.x
            // and `double` in 4.7, so a 4.7.1-built assembly throws
            // MissingMethodException under a stale 4.6 binary — and it throws
            // inside _PhysicsProcess, BEFORE the timeout check, so the scenario
            // HANGS instead of failing (#339 measured all 8 of these). The
            // Variant accessor binds correctly under both. See AuthoredClipMcpProbe.
            double actualSeconds = lib.GetAnimation(clipName).Get("length").AsDouble();
            double deviationSeconds = Math.Abs(actualSeconds - expectedSeconds);
            GD.Print($"[layup-anim]   '{clipName}': length={actualSeconds:F6}s " +
                     $"expected={expectedSeconds:F6}s ({ticks} ticks @ {tps} tps), " +
                     $"deviation={deviationSeconds:F6}s");

            if (deviationSeconds > ToleranceSeconds)
            {
                Fail($"clip '{clipName}' is {actualSeconds:F6}s, expected {expectedSeconds:F6}s " +
                     $"({ticks} ticks at {tps} tps — Layup.DefaultFrameData), a deviation of " +
                     $"{deviationSeconds:F6}s exceeds the float-noise tolerance ({ToleranceSeconds:F6}s). " +
                     "Re-run tools/rebuild_layup_clips.gd after retuning the move's frame data.");
                pass = false;
            }
        }

        if (pass)
            GD.Print("[layup-anim] PASS layup-segment-lengths — all three clips' durations match " +
                     "Layup.DefaultFrameData's Startup/Active/Recovery windows to within float noise " +
                     $"({ToleranceSeconds:F6}s). A one-tick retune of any window goes red here.");
        else
            GD.PrintErr("[layup-anim] FAIL layup-segment-lengths — see per-clip deviations above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: layup-no-placeholder-leak ─────────────────────────────────
    // The direct statement that #296 is closed for this move. An ALLOWLIST, not
    // a placeholder blocklist: a blocklist ("is it locomotion/idle or
    // locomotion/run?") closes #296 but waves through the likelier slip — these
    // three sub-resources were hand-authored directly beneath the steal/block
    // ones, so a state pointing at locomotion/stealactiveright is a real,
    // non-placeholder clip that a blocklist accepts and GetCurrentNode() cannot
    // see either (the STATE name would still read "LayupActive").
    private void RunNoPlaceholderLeakCheck()
    {
        var stateMachine = LoadStateMachine();
        if (stateMachine == null)
        {
            Fail("could not read an AnimationNodeStateMachine off scenes/Player.tscn's AnimationTree " +
                 "tree_root — the state<->clip mapping is unverified.");
            Finish(1);
            return;
        }

        (string State, string Clip)[] states =
        {
            ("LayupStartup",  "locomotion/layupstartup"),
            ("LayupActive",   "locomotion/layupactive"),
            ("LayupRecovery", "locomotion/layuprecovery"),
        };
        string[] placeholderClips = { "locomotion/idle", "locomotion/run" };

        bool pass = true;
        foreach (var (stateName, expectedClip) in states)
        {
            if (!stateMachine.HasNode(stateName))
            {
                Fail($"scenes/Player.tscn's state machine has no state '{stateName}'.");
                pass = false;
                continue;
            }
            if (stateMachine.GetNode(stateName) is not AnimationNodeAnimation animNode)
            {
                Fail($"state '{stateName}' is not an AnimationNodeAnimation — a per-move state must be a " +
                     "single-clip node.");
                pass = false;
                continue;
            }

            string actualClip = animNode.Animation.ToString();
            GD.Print($"[layup-anim]   {stateName} -> {actualClip}");

            if (actualClip != expectedClip)
            {
                string extra = placeholderClips.Contains(actualClip)
                    ? " — this is the #296 GENERIC PLACEHOLDER; the state was never repointed at its own clip."
                    : " — a real clip, but the wrong one (copy-paste from a neighbouring move's sub-resource).";
                Fail($"state '{stateName}' points at '{actualClip}', expected '{expectedClip}'{extra}");
                pass = false;
            }
        }

        if (pass)
            GD.Print("[layup-anim] PASS layup-no-placeholder-leak — all three Layup states point at their " +
                     "OWN per-move clips, not the shared locomotion/idle placeholder #313 moved them off of.");
        else
            GD.PrintErr("[layup-anim] FAIL layup-no-placeholder-leak — see per-state mismatches above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: layup-edges ───────────────────────────────────────────────
    // See the header for why this is legitimate where a GetCurrentNode()-based
    // edge assertion is not. Layup is an OFFENSIVE move, so it needs the six
    // standard edges AND the dribble-family entries/exits — doubled since #294
    // split Dribble into DribbleLeft/DribbleRight. A live-dribbling holder sits
    // in a Dribble state, not Locomotion, so without those a layup off the
    // dribble would have to path through Locomotion to start.
    private void RunEdgesCheck()
    {
        var sm = LoadStateMachine();
        if (sm == null)
        {
            Fail("could not read an AnimationNodeStateMachine off scenes/Player.tscn's AnimationTree tree_root.");
            Finish(1);
            return;
        }

        (string From, string To)[] required =
        {
            // The six standard edges.
            ("Locomotion", "LayupStartup"),
            ("LayupStartup", "LayupActive"),
            ("LayupActive", "LayupRecovery"),
            ("LayupRecovery", "Locomotion"),
            ("LayupStartup", "LayupRecovery"), // feint / early-out
            ("LayupStartup", "Locomotion"),    // abort
            // The dribble family, doubled by #294.
            ("DribbleLeft", "LayupStartup"),
            ("DribbleRight", "LayupStartup"),
            ("LayupRecovery", "DribbleLeft"),
            ("LayupRecovery", "DribbleRight"),
            ("LayupStartup", "DribbleLeft"),
            ("LayupStartup", "DribbleRight"),
        };

        var present = new System.Collections.Generic.HashSet<string>();
        for (int i = 0; i < sm.GetTransitionCount(); i++)
            present.Add($"{sm.GetTransitionFrom(i)}->{sm.GetTransitionTo(i)}");

        bool pass = true;
        foreach (var (from, to) in required)
        {
            bool here = present.Contains($"{from}->{to}");
            GD.Print($"[layup-anim]   edge {from} -> {to}: {(here ? "present" : "MISSING")}");
            if (!here)
            {
                Fail($"scenes/Player.tscn has no transition '{from}' -> '{to}'. Travel()'s pathfinder will " +
                     "route around the gap, so NO runtime scenario can catch this — only this resource-level " +
                     "check can.");
                pass = false;
            }
        }

        if (pass)
            GD.Print($"[layup-anim] PASS layup-edges — all {required.Length} required transitions are present " +
                     "(6 standard + 6 dribble-family, the latter doubled by #294's DribbleLeft/DribbleRight split).");
        else
            GD.PrintErr("[layup-anim] FAIL layup-edges — see missing transitions above.");

        Finish(pass ? 0 : 1);
    }

    private static AnimationNodeStateMachine LoadStateMachine()
    {
        var playerScene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        var sceneState = playerScene.GetState();
        for (int i = 0; i < sceneState.GetNodeCount(); i++)
        {
            if (sceneState.GetNodeType(i) != "AnimationTree") continue;
            for (int p = 0; p < sceneState.GetNodePropertyCount(i); p++)
            {
                if (sceneState.GetNodePropertyName(i, p) != "tree_root") continue;
                return sceneState.GetNodePropertyValue(i, p).As<AnimationNodeStateMachine>();
            }
        }
        return null;
    }

    // ── Geometry helpers ────────────────────────────────────────────────────
    //
    // All three read GLOBAL bone poses (GetBoneGlobalPose), i.e. positions in the
    // Skeleton3D's own space, and every assertion is a DIFFERENCE between two
    // such readings. That matters: PlayerRigScaler rewrites bone pose SCALE at
    // runtime and the Y Bot import carries its own unit scale, so an absolute
    // metre threshold would be measuring the rig setup as much as the clip.
    // A difference in one consistent space is scale-stable and is what the
    // legibility claim actually rests on ("the hips went UP relative to where
    // this attempt started").

    private static float MeasureHipY(Skeleton3D skel)
    {
        int idx = skel.FindBone("mixamorig_Hips");
        return idx < 0 ? 0f : skel.GetBoneGlobalPose(idx).Origin.Y;
    }

    // Best (highest) wrist relative to the head bone. Unhanded clip, so BOTH
    // wrists are checked and the higher wins — the finishing side is a property
    // of the authored pose, not of any runtime hand-side state, and asserting a
    // specific side here would be asserting handedness the clip deliberately
    // does not have.
    private static float MeasureWristAboveHead(Skeleton3D skel)
    {
        int head = skel.FindBone("mixamorig_Head");
        if (head < 0) return float.NegativeInfinity;
        float headY = skel.GetBoneGlobalPose(head).Origin.Y;

        float best = float.NegativeInfinity;
        foreach (string wrist in new[] { "mixamorig_LeftHand", "mixamorig_RightHand" })
        {
            int idx = skel.FindBone(wrist);
            if (idx < 0) continue;
            best = Math.Max(best, skel.GetBoneGlobalPose(idx).Origin.Y - headY);
        }
        return best;
    }

    private static Quaternion[] SampleUpperBody(Skeleton3D skel)
    {
        var poses = new Quaternion[UpperBodyBones.Length];
        for (int i = 0; i < UpperBodyBones.Length; i++)
        {
            int idx = skel.FindBone(UpperBodyBones[i]);
            poses[i] = idx < 0
                ? Quaternion.Identity
                : skel.GetBonePose(idx).Basis.GetRotationQuaternion().Normalized();
        }
        return poses;
    }

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

    private void Fail(string message) => GD.PrintErr($"[layup-anim] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[layup-anim] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
