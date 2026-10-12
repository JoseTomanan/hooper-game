using System;
using Godot;
using Hooper.Ball;
using Hooper.Player;
using Hooper.Moves;

namespace HOOPERGAME.Tests.Integration;

public partial class PostPlayTest : Node3D
{
    private PlayerController _holder;
    private BallController _ball;
    private int _ticks;
    private string _scenario;
    private int _releasePulses;
    private float _fixedTarget;
    private PlayerController _neutral, _defender;
    private BallController _controlBall;
    private int _stage;
    private Vector3 _origin, _neutralOrigin;
    private ContactHarnessSeam _contact, _contactControl;
    private double Dt => 1.0 / Engine.PhysicsTicksPerSecond;
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    public override void _Ready()
    {
        _scenario = HarnessArgs.ReadArg(OS.GetCmdlineUserArgs(), "--harness-scenario", "entry");
        var players = new Node3D { Name = "Players" }; AddChild(players);
        _holder = new PlayerController { Name = "1", Position = new Vector3(0, 0, 10) };
        players.AddChild(_holder);
        _holder.SetPhysicsProcess(false); _holder.SetProcess(false);
        _ball = new BallController { Name = "Ball", Players = players, RimCenter = new Vector3(0, 3, 0) };
        AddChild(_ball); _ball.SetPhysicsProcess(false); _ball.SetProcess(false);
        _ball.StateMachine.ForceState(BallState.Dribbling, 1);
        _holder.SprintBindBallForHarness(_ball);
        _holder.ResetContactStateForHarness(new Vector3(0, 0, 10), MathF.PI, new Vector3(2, 0, 0));
        Input.ActionPress("move_post");
        if (_scenario is "turnaround" or "accuracy" or "accuracy-unsquared" or "lost-payload" or "turnaround-steal" or "turnaround-oob")
        {
            _holder.ResetContactStateForHarness(new Vector3(0, 0, 10), 0, Vector3.Zero);
            _holder.SprintTickHostForHarness(Dt);
            Require(_holder.PostEstablishedForHarness, "post did not establish while facing away");
            Input.ActionPress(_ball.ShootAction);
        }
        if (_scenario is "speed" or "replay" or "turnaround-replay" or "turnaround-rejected")
        {
            Input.ActionRelease("move_post");
            var neutralRoot = new Node3D { Name = "Neutral" }; AddChild(neutralRoot);
            _neutral = new PlayerController { Name = "3" }; neutralRoot.AddChild(_neutral);
            _neutral.SetPhysicsProcess(false); _neutral.SetProcess(false);
            _origin = new Vector3(0, 0, 10); _neutralOrigin = new Vector3(20, 0, 10);
            if (_scenario == "speed") ResetSpeed();
        }
        if (_scenario is "accuracy" or "accuracy-unsquared")
        {
            _defender = new PlayerController { Name = "2", Position = new Vector3(1, 0, 10) };
            players.AddChild(_defender); _defender.SetPhysicsProcess(false); _defender.SetProcess(false);
            _holder.Velocity = new Vector3(1.2f, 0, 0);
        }
        if (_scenario is "lost-payload" or "turnaround-steal" or "turnaround-oob")
        {
            var root = new Node3D { Name = "Control" }; AddChild(root);
            var controlPlayers = new Node3D { Name = "Players" }; root.AddChild(controlPlayers);
            _neutral = new PlayerController { Name = "1", Position = new Vector3(2, 0, 10) };
            controlPlayers.AddChild(_neutral); _neutral.SetPhysicsProcess(false); _neutral.SetProcess(false);
            _controlBall = new BallController { Name = "Ball", Players = controlPlayers, RimCenter = new Vector3(2,3,0) };
            root.AddChild(_controlBall); _controlBall.SetPhysicsProcess(false); _controlBall.SetProcess(false);
            _controlBall.StateMachine.ForceState(BallState.Dribbling,1);
            _neutral.SprintBindBallForHarness(_controlBall);
            _neutral.SprintTickHostForHarness(Dt);
            Require(_neutral.PostEstablishedForHarness,"control post not established");
            if(_scenario != "lost-payload")
            {
                _defender = new PlayerController { Name="2",Position=new Vector3(-2,0,10) };
                players.AddChild(_defender);_defender.SetPhysicsProcess(false);_defender.SetProcess(false);
            }
        }
        if (_scenario is "contact-set" or "contact-unset") SetupContact();
        if (_scenario is "entry-side" or "entry-away" or "entry-coincident")
        {
            float heading = _scenario == "entry-side" ? MathF.PI / 2 : .7f;
            if (_scenario == "entry-away") heading = 0;
            _holder.ResetContactStateForHarness(new Vector3(0,0,10), heading, Vector3.Zero);
            if (_scenario == "entry-coincident") _ball.RimCenter = new Vector3(0,3,10);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        try
        {
            if (_scenario is "turnaround" or "accuracy" or "accuracy-unsquared" or "lost-payload" or "turnaround-steal" or "turnaround-oob") { Turnaround(); return; }
            if (_scenario == "speed") { Speed(); return; }
            if (_scenario == "gates") { Gates(); return; }
            if (_scenario == "bindings") { Bindings(); return; }
            if (_scenario == "pivot") { Pivot(); return; }
            if (_scenario == "dispatch") { Dispatch(); return; }
            if (_scenario == "replay") { Replay(); return; }
            if (_scenario == "turnaround-replay") { TurnaroundReplay(); return; }
            if (_scenario == "turnaround-rejected") { TurnaroundRejected(); return; }
            if (_scenario is "contact-set" or "contact-unset") { Contact(); return; }
            if (_scenario is "entry-side" or "entry-away" or "entry-coincident") { EntryVariants(); return; }
            float before = _holder.Heading;
            float speedBefore = _holder.Velocity.Length();
            _holder.SprintTickHostForHarness(Dt);
            float turned = Math.Abs(Mathf.AngleDifference(before, _holder.Heading));
            Require(turned > 0 && turned <= 6f * MathF.PI / 180f + .00001f, "post entry did not bounded-turn without stick input");
            if (_ticks == 0)
                Require(_holder.Velocity.Length() > 0 && _holder.Velocity.Length() < speedBefore, "post entry discarded momentum rather than braking");
            _ticks++;
            if (_ticks == 3) Finish(true, "bounded post entry with finite braking");
        }
        catch (Exception error) { Finish(false, error.Message); }
    }

    private void EntryVariants()
    {
        float before = _holder.Heading;
        Input.ActionPress("move_right");
        _holder.SprintTickHostForHarness(Dt);
        _ticks++;
        Require(_holder.IsPosting, "valid held entry did not activate post");
        Require(float.IsFinite(_holder.Heading) && _holder.Velocity.IsFinite(), "entry produced nonfinite motion");
        float turned = Math.Abs(Mathf.AngleDifference(before, _holder.Heading));
        if (_scenario == "entry-side")
        {
            Require(turned > 0 && turned <= 6f*MathF.PI/180f + 1e-5f, "side entry did not bounded turn");
            Require(!_holder.PostEstablishedForHarness && _holder.Velocity.Length() == 0, "side entry translated before planted");
        }
        else
        {
            Require(turned < 1e-5f && _holder.PostEstablishedForHarness, "away/coincident entry changed heading or failed to establish");
            Require(_holder.Velocity.X > 0 && Math.Abs(_holder.Velocity.Z) < 1e-5f, "established entry lost world-space input");
        }
        if (_ticks == (_scenario == "entry-side" ? 3 : 1)) Finish(true, _scenario + " actual host input and motion");
    }

    private void Turnaround()
    {
        bool loss = _scenario is "lost-payload" or "turnaround-steal" or "turnaround-oob";
        if (loss) _neutral.SprintTickHostForHarness(Dt);
        _holder.SprintTickHostForHarness(Dt);
        _ticks++;
        Require(_holder.PostMoveForHarness == "turnaround" || _ticks == 72, "fresh post shoot edge did not select turnaround");
        if (_ticks == 1)
        {
            _fixedTarget = _holder.PostTargetForHarness;
            Require(_ball.State == BallState.Held, "turnaround failed to cradle live dribble at Begin");
            Require(!_holder.IsPosting && !_holder.IsProtecting && !_holder.IsSprinting, "commitment retained posture/pace");
            Input.ActionRelease(_ball.ShootAction); Input.ActionRelease("move_post");
            if (_scenario == "turnaround") _ball.RimCenter = new Vector3(3, 3, 0);
            if (_scenario == "accuracy-unsquared") _holder.TurnaroundTurnRateDeg = 0;
            if (_scenario == "lost-payload") _ball.StateMachine.GoLoose();
            if (_scenario == "turnaround-steal")
            {
                Vector2 forward=HeadingMath.Forward(_holder.Heading);
                var hand=new Vector3(-forward.Y,0,forward.X)*(_holder.HandSide==HandSide.Right?1f:-1f);
                _defender.GlobalPosition=_holder.GlobalPosition+hand*.4f;
                Require(_defender.BeginMoveForHarness(new StealMove(_holder.HandSide,1f)),"steal event failed Begin");
                _defender.SprintEnterActiveForHarness();
                _ball.PostTickHeldForHarness();
                _ball.ProtectionResolveHeldForHarness();
                Require(_ball.State==BallState.Loose && _ball.LastToucherPeerIdForHarness==2,"real held steal event failed");
            }
            if (_scenario == "turnaround-oob")
            {
                _holder.GlobalPosition=new Vector3(100,0,10);
                _ball.PostResolveOobForHarness();
                Require(_ball.StateMachine.HolderPeerId==2 && _ball.State==BallState.Held,"real OOB turnover event failed");
            }
        }
        if (_scenario is "accuracy" or "accuracy-unsquared")
        {
            if (_ticks == 42) Require(_defender.StanceBeginForHarness(new ContestMove()), "contest control failed Begin");
            _defender.SprintTickServerForHarness(Dt);
        }
        Require(!_holder.PostFeintForHarness(), "turnaround allowed flow cancel");
        Require(Math.Abs(Mathf.AngleDifference(_fixedTarget, _holder.PostTargetForHarness)) < 1e-5f,
            "rim/input altered committed target");
        if (_holder.PostReleaseForHarness)
        {
            _releasePulses++;
            _ball.PostTickHeldForHarness();
            if (_scenario is "accuracy" or "accuracy-unsquared") Accuracy();
        }
        if (loss)
        {
            if (_neutral.PostReleaseForHarness)
            {
                _controlBall.PostTickHeldForHarness();
            }
            Require(_neutral.PostPhaseForHarness == _holder.PostPhaseForHarness
                && _neutral.PostFrameForHarness == _holder.PostFrameForHarness, "lost payload changed machine timeline");
        }
        if (_ticks < 48) Require(_holder.PostPhaseForHarness == Hooper.Moves.MovePhase.Startup, "startup ended early");
        else if (_ticks < 52) Require(_holder.PostPhaseForHarness == Hooper.Moves.MovePhase.Active, "active phase wrong");
        else if (_ticks < 72) Require(_holder.PostPhaseForHarness == Hooper.Moves.MovePhase.Recovery, "recovery wrong");
        else
        {
            Require(_holder.PostPhaseForHarness == Hooper.Moves.MovePhase.Inactive, "did not finish at tick72");
            Require(_releasePulses == 1, "release pulse count=" + _releasePulses);
            if (loss)
                Require(_ball.SuccessfulShotReleasesForHarness == 0 && _controlBall.SuccessfulShotReleasesForHarness == 1,
                    "lost-payload control did not distinguish successful release");
            else Require(_ball.SuccessfulShotReleasesForHarness == 1, "pulse did not become exactly one actual ball release");
            Finish(true, "turnaround fixed target full timeline and actual release control");
        }
        if (_ticks >= 30 && _scenario != "accuracy-unsquared") Require(Math.Abs(Mathf.AngleDifference(_fixedTarget, _holder.Heading)) < 1e-4f,
            "turnaround did not finish bounded turn before release");
    }

    private void Accuracy()
    {
        Require(_ball.State == BallState.InFlight && _ball.SuccessfulShotReleasesForHarness == 1, "accuracy lacks actual release premise");
        float facing = ShotFacing.Multiplier(_holder.Heading, _holder.GlobalPosition, _ball.ShotTarget, _ball.FacingScatterK);
        float movement = ShotMovement.Factor(_holder.ShotInitiationSpeed, _holder.MoveSpeed, _holder.SprintSpeed, _ball.MovementScatterK);
        float distance = MathF.Sqrt(DefensiveResolution.DistanceXZSquared(_defender.GlobalPosition, _holder.GlobalPosition));
        float contest = 1f + _ball.ContestScatterK * Math.Clamp(1f-distance/_ball.ContestRange,0,1);
        var active = _defender.ActiveMove<ContestMove>();
        Require(active.HasValue, "accuracy lacks committed contest premise");
        int tick = unchecked((int)Engine.GetPhysicsFrames());
        int start = tick - active.Value.FrameInPhase;
        float contestMove = DefensiveResolution.ContestMoveFactor(
            DefensiveResolution.ContestAppliesAt(start,start+active.Value.Move.FrameData.ActiveFrames,tick), _ball.ContestMoveScatterK);
        float product = facing * movement * contest * contestMove;
        Require(movement>1 && contest>1 && contestMove>1,"accuracy penalty control inactive");
        if (_scenario == "accuracy-unsquared") Require(facing>1.5f,"unsquared facing control inactive");
        else Require(facing<1.01f,"normal turnaround did not square before release");
        Require(Math.Abs(_ball.LastFacingFactorForHarness-facing)<1e-5f,"actual facing term differs");
        Require(Math.Abs(_ball.LastAccuracyMultiplierForHarness-product)<1e-5f,"actual product differs/no extra post bonus allowed");
        var rng = new Random(_ball.ShotScatterSeed);
        float shotDistance = MathF.Sqrt(DefensiveResolution.DistanceXZSquared(_ball.LastReleasePositionForHarness,_ball.ShotTarget));
        var expectedAim = ShotScatter.Scatter(_ball.ShotTarget,shotDistance,(float)rng.NextDouble(),(float)rng.NextDouble(),
            _ball.ShotScatterPerMeter,_ball.MaxShotScatter,product);
        Require(_ball.LastAimTargetForHarness.DistanceTo(expectedAim)<1e-5f,"actual applied scatter target differs");
    }

    private void ResetSpeed()
    {
        Vector2 dir = HeadingMath.Forward(_stage*MathF.PI/4f);
        _holder.ResetContactStateForHarness(_origin,0,Vector3.Zero);
        _neutral.ResetContactStateForHarness(_neutralOrigin,0,Vector3.Zero);
        _ticks=0;
    }
    private void Speed()
    {
        Vector2 dir = HeadingMath.Forward(_stage*MathF.PI/4f);
        _holder.Move(dir,Dt,postHeld:true,postEligible:true,rimSample:Vector3.Zero);
        _neutral.Move(dir,Dt);
        var projected = new Vector2(_holder.Velocity.X,_holder.Velocity.Z);
        Require(projected.Dot(dir)>0,"world-space post direction reversed");
        Require(Math.Abs(projected.Cross(dir))<1e-4f,"world-space post direction rotated");
        _ticks++;
        if (_ticks<80) return;
        Require(Math.Abs(_holder.Velocity.Length()-_holder.PostSpeed)<1e-4f,"post ceiling not sustained");
        Require(Math.Abs(_neutral.Velocity.Length()-_neutral.MoveSpeed)<1e-4f,"neutral control not at sustained ceiling");
        Vector3 away=_holder.GlobalPosition;
        Require(Math.Abs(Mathf.AngleDifference(_holder.Heading,MathF.Atan2(away.X,away.Z))) <= _holder.PostEntryToleranceDeg*MathF.PI/180f,
            "sustained post no longer faces away from rim");
        Require(_neutral.GlobalPosition.DistanceTo(_neutralOrigin)>_holder.GlobalPosition.DistanceTo(_origin)+2f,"post not materially slower than matched neutral");
        _stage++;
        if(_stage==8) Finish(true,"eight world-space directions post slower than matched neutral");
        else ResetSpeed();
    }

    private void Bindings()
    {
        bool physicalT = false, dpadDown = false;
        foreach (var binding in InputMap.ActionGetEvents("move_post"))
        {
            physicalT |= binding is InputEventKey key && key.PhysicalKeycode == Key.T;
            dpadDown |= binding is InputEventJoypadButton pad && pad.ButtonIndex == JoyButton.DpadDown;
        }
        Require(physicalT && dpadDown, "real InputMap lacks physical T or D-pad Down");
        Input.ActionRelease("move_post");
        // Synthetic device events pass through the shipped binding rather than ActionPress.
        // https://docs.godotengine.org/en/4.7/classes/class_input.html#class-input-method-parse-input-event
        foreach (bool keyboard in new[] { true, false })
        {
            _ball.StateMachine.ForceState(BallState.Dribbling,1);
            _holder.ResetContactStateForHarness(new Vector3(0,0,10),0,Vector3.Zero);
            foreach (bool pressed in new[] { true, false })
            {
                InputEvent deviceEvent = keyboard
                    ? new InputEventKey { PhysicalKeycode = Key.T, Pressed = pressed }
                    : new InputEventJoypadButton { Device = 0, ButtonIndex = JoyButton.DpadDown, Pressed = pressed };
                Input.ParseInputEvent(deviceEvent);
                Input.FlushBufferedEvents();
                Require(Input.IsActionPressed("move_post") == pressed, "device event did not resolve shipped held binding");
                _holder.SprintTickHostForHarness(Dt);
                Require(_holder.IsPosting == pressed, "bound device hold/release did not reach host post state");
                Require(_holder.PostMoveForHarness == "", "binding alone began committed move");
            }
        }
        Finish(true, "physical T and D-pad Down parse through InputMap into held host post state");
    }

    private void Gates()
    {
        _holder.ResetContactStateForHarness(new Vector3(0,0,10),0,Vector3.Zero);
        Input.ActionPress("move_protect"); Input.ActionPress("move_sprint");
        _holder.SprintTickHostForHarness(Dt);
        Require(_holder.IsPosting && !_holder.IsProtecting && !_holder.IsSprinting,"post precedence failed");
        _ball.CradleForShotStartup(1);
        Require(_ball.HasDribbled, "dead-dribble premise absent");
        _holder.SprintTickHostForHarness(Dt);
        _holder.PostDispatchForHarness("turnaround", 7);
        Require(!_holder.IsPosting && _holder.PostMoveForHarness == "" && _ball.State == BallState.Held
            && _ball.HasDribbled, "dead Held post started dribble/turnaround or erased debt");
        _ball.PostAwardForHarness(1);
        Require(!_ball.HasDribbled && _ball.State == BallState.Held, "possession reset did not restore live Held source");
        _holder.SprintTickHostForHarness(Dt);
        _holder.PostDispatchForHarness("turnaround", 7);
        Require(!_holder.IsPosting && _holder.PostMoveForHarness == "" && _ball.State == BallState.Held,
            "post modifier alone activated post/shot or auto-started dribble after possession reset");
        foreach(var state in new[]{BallState.Held,BallState.Loose,BallState.InFlight})
        {
            _ball.StateMachine.ForceState(state,state==BallState.Held ? 1 : 0);
            Require(!_holder.IsPosting && !_holder.DisplayPostEstablished,"stale post visible after ball state changed");
            _holder.SprintTickHostForHarness(Dt);
            Require(!_holder.IsPosting,"non-dribbling state activated post");
        }
        _ball.StateMachine.ForceState(BallState.Dribbling,2);
        _holder.SprintTickHostForHarness(Dt);
        Require(!_holder.IsPosting,"nonholder activated post");
        _ball.StateMachine.ForceState(BallState.Dribbling,1);
        _holder.SprintTickHostForHarness(Dt);
        Require(_holder.IsPosting,"valid post did not resume");
        Input.ActionRelease("move_post");
        _holder.SprintTickHostForHarness(Dt);
        Require(!_holder.IsPosting && _holder.IsProtecting,"protection did not resume on post release");
        Finish(true,"live eligibility and precedence gates");
    }

    private void Pivot()
    {
        if(_ticks==0)
        {
            _holder.ResetContactStateForHarness(new Vector3(0,0,10),0,new Vector3(2,0,0));
            _holder.Move(new Vector2(0,-1),Dt);
            Require(_holder.IsPivotingInPlace,"owed pivot premise absent");
            _fixedTarget=_holder.StanceOwedYawForHarness;
            _holder.Velocity=new Vector3(2,0,0);
        }
        // Releasing/re-pressing the modifier while a pivot is owed must not erase debt.
        bool held = _ticks % 2 == 0;
        if (held) Input.ActionPress("move_post"); else Input.ActionRelease("move_post");
        bool owed = _holder.IsPivotingInPlace;
        float before=_holder.Heading;
        var expected=HeadingMath.Step(before,new HeadingMath.PivotState(owed,_fixedTarget),Vector2.Zero,Dt,
            _holder.MaxTurnRateDeg,_holder.BackTurnSlowFactor,_holder.PivotThresholdDeg);
        _holder.SprintTickHostForHarness(Dt);
        _ticks++;
        if(owed)
        {
            Require(Math.Abs(Mathf.AngleDifference(expected.NewYaw,_holder.Heading))<1e-5f,"post redirected owed pivot");
            Require(!_holder.PostEstablishedForHarness,"pivot completion tick established post");
            _holder.PostDispatchForHarness("turnaround");
            Require(_holder.PostMoveForHarness=="","turnaround cancelled pivot debt");
            if (expected.Pivot.HasLatch) Require(_holder.IsPivotingInPlace,"post toggle erased owed pivot");
            if(_ticks==1) Require(_holder.Velocity.Length()>0,"owed pivot in post instant-zeroed momentum");
        }
        else if(_ticks>20 && _holder.PostEstablishedForHarness) Finish(true,"owed pivot retained, completion tick gated, entry then converged");
        Require(_ticks<90,"post never established after owed pivot completed");
    }

    private void Dispatch()
    {
        _holder.ResetContactStateForHarness(new Vector3(0,0,10),MathF.PI,Vector3.Zero);
        _holder.SprintTickHostForHarness(Dt);
        _holder.PostDispatchForHarness("turnaround",7);
        Require(_holder.PostMoveForHarness=="","early entry dispatch began shot");
        Input.ActionPress(_ball.ShootAction);
        _holder.SprintTickHostForHarness(Dt);
        Require(_holder.PostMoveForHarness=="","early input silently fell back to ordinary shot");
        Input.ActionRelease(_ball.ShootAction);
        _ball.StateMachine.ForceState(BallState.Dribbling,2);
        _holder.PostDispatchForHarness("turnaround",7);
        Require(_holder.PostMoveForHarness=="","nonholder dispatch began turnaround");
        _ball.StateMachine.ForceState(BallState.Dribbling,1);
        _holder.ResetContactStateForHarness(new Vector3(0,0,10),0,Vector3.Zero);
        _holder.SprintTickHostForHarness(Dt);
        Require(_holder.PostEstablishedForHarness,"valid dispatch not established");
        _holder.PostDispatchForHarness("turnaround",7);
        Require(_holder.PostMoveForHarness=="turnaround" && _holder.PostTokenForHarness==7,"valid dispatch rejected/token lost");
        Require(Math.Abs(Mathf.AngleDifference(_holder.PostTargetForHarness,MathF.PI))<1e-5f,"incoming fake aim was trusted");
        Finish(true,"real input/dispatch early rejection and fixed authoritative target");
    }

    private void Replay()
    {
        Vector3 origin = new(0,0,10), velocity=new(2,0,0), historicalRim=Vector3.Zero;
        _neutral.ResetContactStateForHarness(origin,MathF.PI,velocity);
        var dirs=new[]{Vector2.Right,Vector2.Right,Vector2.Zero,Vector2.Left};
        for(int i=0;i<dirs.Length;i++)
        {
            bool held=i is 1 or 2;
            _holder.PostRecordForHarness(dirs[i],held,true,historicalRim);
            _neutral.Move(dirs[i],Dt,postHeld:held,postEligible:true,rimSample:historicalRim);
        }
        // Current context intentionally disagrees: history must use sampled eligibility/rim.
        _ball.StateMachine.ForceState(BallState.Loose,0); _ball.RimCenter=new Vector3(40,3,40);
        _holder.PostReconcileForHarness(origin,velocity,MathF.PI,0,false,false,Dt);
        Require(_holder.GlobalPosition.DistanceTo(_neutral.GlobalPosition)<1e-4f,"mixed post replay position differs");
        Require(_holder.Velocity.DistanceTo(_neutral.Velocity)<1e-4f,"mixed post replay velocity differs");
        Require(Math.Abs(Mathf.AngleDifference(_holder.Heading,_neutral.Heading))<1e-5f,"mixed post replay heading used live context");
        Require(!_holder.IsPosting,"historical replay resurrected live post query after ball loss");
        Finish(true,"historical mixed post inputs survive live context changes");
    }

    private void TurnaroundReplay()
    {
        Input.ActionPress("move_post");
        _holder.ResetContactStateForHarness(new Vector3(0,0,10),0,Vector3.Zero);
        _ball.StateMachine.ForceState(BallState.Dribbling,1);
        _holder.SprintTickHostForHarness(Dt);
        _holder.PostDispatchForHarness("turnaround",7);
        Require(_holder.PostMoveForHarness=="turnaround","replay lacks real accepted Begin");
        int frame=_holder.PostFrameForHarness; var phase=_holder.PostPhaseForHarness;
        Vector3 origin=new(0,0,10), velocity=new(2,0,0);
        _neutral.ResetContactStateForHarness(origin,0,velocity);
        for(int i=0;i<4;i++)
        {
            _holder.PostRecordForHarness(Vector2.Zero,false,false,Vector3.Zero,MovePhase.Startup,-1f,7);
            _neutral.PostTurnMotionForHarness(MovePhase.Startup,.7f,Dt);
        }
        // A future instance's independently predicted target must survive this confirmation.
        _holder.PostRecordForHarness(Vector2.Zero,false,false,Vector3.Zero,MovePhase.Startup,-1f,12);
        _neutral.PostTurnMotionForHarness(MovePhase.Startup,-1f,Dt);
        _holder.PostReconcileForHarness(origin,velocity,0,0,false,false,Dt,.7f,7);
        Require(_holder.GlobalPosition.DistanceTo(_neutral.GlobalPosition)<1e-4f,"turn replay position differs");
        Require(_holder.Velocity.DistanceTo(_neutral.Velocity)<1e-4f,"turn replay braking differs");
        Require(Math.Abs(Mathf.AngleDifference(_holder.Heading,_neutral.Heading))<1e-5f,"confirmed target was not instance-correlated");
        Require(Math.Abs(_holder.PostTargetForHarness-.7f)<1e-5f,"live same-instance target not repaired");
        Require(_holder.PostFrameForHarness==frame && _holder.PostPhaseForHarness==phase && !_holder.PostReleaseForHarness,
            "replay changed machine counter/phase or release pulse");
        Finish(true,"same-instance confirmed target repairs historical/live motion without events");
    }

    private void TurnaroundRejected()
    {
        Input.ActionPress("move_post");
        _holder.ResetContactStateForHarness(new Vector3(0,0,10),0,Vector3.Zero);
        _holder.SprintTickHostForHarness(Dt);
        _holder.PostDispatchForHarness("turnaround",7);
        Require(_holder.PostMoveForHarness=="turnaround","rejected replay lacks predicted Begin premise");
        _holder.SprintEnterActiveForHarness();
        Require(_holder.PostPhaseForHarness==MovePhase.Active,"rejection grace control not past Startup");
        for(int i=0;i<4;i++) _holder.PostRecordForHarness(Vector2.Zero,false,false,Vector3.Zero,MovePhase.Startup,2f,7);
        _holder.PostReconcileForHarness(new Vector3(0,0,10),Vector3.Zero,.7f,0,false,false,Dt);
        Require(_holder.PostMoveForHarness=="" && _holder.PostPhaseForHarness==MovePhase.Inactive,"confirmed rejection retained machine identity");
        Require(Math.Abs(_holder.Heading-.7f)<1e-5f,"rejected historical turn rewrote restored authoritative heading");
        Require(!_holder.PostReleaseForHarness,"rejected replay released phantom payload");
        Finish(true,"confirmed rejection invalidates correlated motion samples");
    }

    private void SetupContact()
    {
        _holder.QueueFree();
        Input.ActionRelease("move_post");
        _contact=new ContactHarnessSeam { Name="Contact",FirstName="11",SecondName="12" }; AddChild(_contact);
        _contactControl=new ContactHarnessSeam { Name="NoContact",FirstName="13",SecondName="14" }; AddChild(_contactControl);
        var velocity=_scenario=="contact-unset" ? new Vector3(3,0,0) : Vector3.Zero;
        _contact.Reset(new Vector3(0,0,1.1f),MathF.PI,new Vector3(0,0,-6),Vector3.Zero,0,velocity);
        _contactControl.Reset(new Vector3(20,0,1.1f),MathF.PI,new Vector3(0,0,-6),new Vector3(20,0,0),0,velocity);
        foreach(var fixture in new[]{_contact,_contactControl})
        {
            var ball=new BallController { Name="PostBall",Players=fixture.First.GetParent(),RimCenter=fixture.First.GlobalPosition+new Vector3(0,3,10) };
            fixture.AddChild(ball); ball.SetPhysicsProcess(false);ball.SetProcess(false);
            ball.StateMachine.ForceState(BallState.Dribbling,fixture==_contact ? 11 : 13);
            fixture.First.SprintBindBallForHarness(ball);fixture.Second.SprintBindBallForHarness(ball);
            fixture.First.PostPendingForHarness(true,new Vector2(0,-1));
        }
        _contactControl.First.CollisionLayer=_contactControl.First.CollisionMask=0;
        _contactControl.Second.CollisionLayer=_contactControl.Second.CollisionMask=0;
        _origin=_contact.Second.GlobalPosition;
    }
    private void Contact()
    {
        var input=_scenario=="contact-unset" ? Vector2.Right : Vector2.Zero;
        _contact.First.StepProductionContactPairForHarness(_contact.Second,Dt,new Vector2(0,-1),input);
        _contactControl.First.StepProductionContactPairForHarness(_contactControl.Second,Dt,new Vector2(0,-1),input);
        _ticks++;
        Require(_contact.First.IsPosting && _contactControl.First.IsPosting,"matched contact controls lost post eligibility");
        if(_scenario=="contact-unset") Require(_contact.Second.Velocity.Length()>ContactSettings.Default.MaxSetSpeed,"unset speed premise absent");
        if(_ticks<6) return;
        Require(_contact.First.GlobalPosition.Z>_contactControl.First.GlobalPosition.Z+.02f,"post contact failed to change paired motion vs disabled control");
        if(_scenario=="contact-set") Require(_contact.Second.GlobalPosition.DistanceTo(_origin)<.01f,"set defender displaced");
        else Require(_contact.Second.GlobalPosition.Z<-.01f,"unset defender did not share impulse");
        Finish(true,"post uses production set/unset contact with matched disabled control");
    }

    private void Finish(bool pass, string reason)
    {
        Input.ActionRelease("move_post"); Input.ActionRelease("move_right");
        Input.ActionRelease(_ball.ShootAction);
        Input.ActionRelease("move_protect"); Input.ActionRelease("move_sprint");
        GD.Print($"[harness] {(pass ? "PASS" : "FAIL")}: PostPlayTest {reason}");
        GetTree().Quit(pass ? 0 : 1); SetPhysicsProcess(false);
    }
}


