using System;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Player;
using Hooper.Moves;
namespace HOOPERGAME.Tests.Integration;

public partial class DefensiveStanceTest : Node3D
{
    private PlayerController _defender, _attacker, _neutral;
    private BallController _ball;
    private Node3D _players;
    private string _scenario;
    private int _ticks, _stage, _stanceCross, _neutralCross;
    private Vector3 _origin, _neutralOrigin;
    private float _owedYaw;
    private readonly bool[] _observedPhases = new bool[4];
    private ContactHarnessSeam _contact, _contactControl;
    private double Dt => 1.0 / Engine.PhysicsTicksPerSecond;
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    public override void _Ready()
    {
        try
        {
            _scenario = HarnessArgs.ReadArg(OS.GetCmdlineUserArgs(), "--harness-scenario", "slides-left");
            _players = new Node3D { Name = "Players" }; AddChild(_players);
            _defender = AddPlayer(_players, "1", Vector3.Zero, true);
            _attacker = AddPlayer(_players, "2", new Vector3(0,0,10), true);
            _ball = new BallController { Name = "Ball", Players = _players }; AddChild(_ball);
            _ball.SetPhysicsProcess(false); _ball.SetProcess(false);
            _ball.StateMachine.ForceState(BallState.Dribbling, 2);
            _defender.SprintBindBallForHarness(_ball); _attacker.SprintBindBallForHarness(_ball);
            _defender.ResetContactStateForHarness(new Vector3(0,0,5), Mathf.Pi/2, Vector3.Zero);
            _attacker.ResetContactStateForHarness(new Vector3(0,0,10), 0, Vector3.Zero);
            Input.ActionPress("move_protect");
            if (_scenario == "slides-left") Input.ActionPress("move_left");
            if (_scenario is "slides-right" or "bounded-moving") Input.ActionPress("move_right");
            if (_scenario == "response") SetupResponse();
            if (_scenario == "pivot")
            {
                _defender.ResetContactStateForHarness(new Vector3(0,0,5),0,Vector3.Zero);
                _defender.Move(new Vector2(0,-1),Dt);
                Require(_defender.IsPivotingInPlace,"ordinary reverse did not create owed pivot");
                _owedYaw = _defender.StanceOwedYawForHarness; _origin = _defender.GlobalPosition;
            }
            if (_scenario == "committed")
            {
                var controlParent = new Node3D { Name = "MoveControl" }; AddChild(controlParent);
                _neutral = AddPlayer(controlParent,"3",new Vector3(20,0,5),true);
                _defender.ResetContactStateForHarness(new Vector3(0,0,5),0,Vector3.Zero);
                _neutral.ResetContactStateForHarness(new Vector3(20,0,5),0,Vector3.Zero);
                _origin = _defender.GlobalPosition; _neutralOrigin = _neutral.GlobalPosition;
                BeginPairMove();
            }
            if (_scenario is "contact-slow" or "contact-fast") SetupContact();
        }
        catch (Exception error) { Finish(false,error.Message); }
    }
    private static PlayerController AddPlayer(Node parent,string name,Vector3 position,bool shipped)
    {
        // https://docs.godotengine.org/en/4.7/classes/class_packedscene.html#class-packedscene-method-instantiate
        var p = shipped ? ResourceLoader.Load<PackedScene>("res://scenes/Player.tscn").Instantiate<PlayerController>() : new PlayerController();
        if (!shipped) p.AddChild(new CollisionShape3D { Name="CollisionShape3D", Shape=new CapsuleShape3D { Radius=.5f,Height=2 },Position=new Vector3(0,1,0) });
        p.Name=name; p.Position=position; parent.AddChild(p);
        p.SetPhysicsProcess(false); p.SetProcess(false);
        var animation=p.GetNodeOrNull<AnimationTree>("AnimationTree"); if(animation!=null) animation.Active=false;
        return p;
    }
    public override void _PhysicsProcess(double delta)
    {
        try
        {
            _ticks++;
            switch (_scenario)
            {
                case "slides-left": case "slides-right": case "idle-facing": case "bounded-moving": Facing(); break;
                case "response": Response(); break;
                case "gates": Gates(); break;
                case "pivot": Pivot(); break;
                case "committed": Committed(); break;
                case "contact-slow": case "contact-fast": Contact(); break;
                default: throw new InvalidOperationException("unknown scenario "+_scenario);
            }
        }
        catch(Exception error) { Finish(false,error.Message); }
    }
    private void Facing()
    {
        if (_scenario == "bounded-moving") _attacker.GlobalPosition=new Vector3(MathF.Sin(_ticks*.4f)*3,0,_ticks<20 ? 0 : 10);
        float before=_defender.Heading; var position=_defender.GlobalPosition;
        _defender.SprintTickHostForHarness(Dt);
        Require(Math.Abs(Mathf.AngleDifference(before,_defender.Heading)) <= _defender.StanceTurnRateDeg*Mathf.Pi/180*(float)Dt+.00001f,"unbounded stance heading");
        Require(!_defender.IsPivotingInPlace && _defender.IsInDefensiveStance,"effective stance did not suppress movement-facing pivot");
        if (_scenario=="idle-facing") Require(_defender.GlobalPosition.DistanceTo(position)<.00001f,"idle stance translated");
        if (_ticks<30) return;
        if (_scenario=="slides-left") Require(_defender.GlobalPosition.X<-.5f,"left slide failed");
        if (_scenario is "slides-right" or "bounded-moving") Require(_defender.GlobalPosition.X>.5f,"right slide failed");
        if (_scenario!="bounded-moving")
        {
            var target=_attacker.GlobalPosition-_defender.GlobalPosition;
            Require(Math.Abs(Mathf.AngleDifference(_defender.Heading,MathF.Atan2(target.X,target.Z)))<.04f,"failed to face attacker");
        }
        // Positive turn premise above; a coincident target must not invent a heading.
        _attacker.GlobalPosition=_defender.GlobalPosition; before=_defender.Heading;
        Input.ActionRelease("move_left"); Input.ActionRelease("move_right");
        _defender.SprintTickHostForHarness(Dt);
        Require(float.IsFinite(_defender.Heading) && Math.Abs(Mathf.AngleDifference(before,_defender.Heading))<.00001f,"coincident target changed heading");
        Finish(true,$"x={_defender.GlobalPosition.X} heading={_defender.Heading}");
    }
    private ContactOpponent FarTarget(PlayerController p)=>new(p.GlobalPosition+new Vector3(0,0,100),Vector3.Zero,0,.5f,2,1,99);
    private void SetupResponse()
    {
        _defender.QueueFree(); _attacker.QueueFree();
        var response=new Node3D { Name="Response" }; AddChild(response);
        _defender=AddPlayer(response,"11",Vector3.Zero,false);
        var control=new Node3D { Name="ResponseControl" }; AddChild(control);
        _neutral=AddPlayer(control,"12",new Vector3(20,0,0),false);
        ResetResponse(Vector3.Zero);
    }
    private void ResetResponse(Vector3 velocity)
    {
        _defender.ResetContactStateForHarness(Vector3.Zero,Mathf.Pi/2,velocity);
        _neutral.ResetContactStateForHarness(new Vector3(20,0,0),Mathf.Pi/2,velocity);
        if (_stage==2) { _defender.ResetContactStateForHarness(Vector3.Zero,-Mathf.Pi/2,velocity); _neutral.ResetContactStateForHarness(new Vector3(20,0,0),-Mathf.Pi/2,velocity); }
        _stanceCross=_neutralCross=0; _ticks=0;
    }
    private void Response()
    {
        var input=_stage==1 ? Vector2.Zero : _stage==2 ? Vector2.Left : Vector2.Right;
        _defender.Move(input,Dt,FarTarget(_defender),stance:_stage!=4);
        _neutral.Move(input,Dt,FarTarget(_neutral));
        float a=_defender.Velocity.X,b=_neutral.Velocity.X;
        bool Cross(float v)=>_stage==1 ? Math.Abs(v)<.001f : _stage==2 ? v < -2.25001f : v>2.25001f;
        if (_stanceCross==0 && Cross(a)) _stanceCross=_ticks;
        if (_neutralCross==0 && Cross(b)) _neutralCross=_ticks;
        if (_stage==4)
        {
            Require(a>3 && a<6 && float.IsFinite(a) && Math.Abs(a-(3+_defender.Accel*(float)Dt))<.00001f,"stance exit discarded momentum or instant full speed");
            Finish(true,$"entry/exit finite exit={a}"); return;
        }
        if (_stage==3)
        {
            Require(a>3 && a<6 && float.IsFinite(a) && Math.Abs(a-(6-_defender.StanceAccel*(float)Dt))<.00001f,"stance entry discarded momentum or instant cap");
            _stage++; Require(Math.Abs(b-6)<.00001f,"neutral full-speed control changed");
            _defender.ResetContactStateForHarness(Vector3.Zero,Mathf.Pi/2,new Vector3(3,0,0));
            return;
        }
        if (_ticks<60) return;
        GD.Print($"[stance] response stage={_stage} stance-threshold={_stanceCross} neutral-threshold={_neutralCross} coverage={_defender.GlobalPosition.X}/{_neutral.GlobalPosition.X-20}");
        Require(_stanceCross>0 && _neutralCross>0 && _stanceCross<_neutralCross,"stance response was not quicker against common threshold");
        if (_stage==0) Require(_defender.GlobalPosition.X < _neutral.GlobalPosition.X-20,"stance full-second coverage not lower");
        _stage++; ResetResponse(new Vector3(_stage==3 ? 6 : 2.4f,0,0));
    }
    private void Gates()
    {
        Require(Math.Abs(_defender.StanceSpeed-3)<.00001f && Math.Abs(_defender.StanceAccel-60)<.00001f
            && Math.Abs(_defender.StanceDecel-90)<.00001f && Math.Abs(_defender.StanceTurnRateDeg-360)<.00001f,
            "shipped stance export overrides changed provisional defaults");
        Require(Math.Abs(_defender.MoveSpeed-6)<.00001f && Math.Abs(_defender.Accel-45)<.00001f
            && Math.Abs(_defender.Decel-70)<.00001f,"shipped neutral tuning changed");
        var bindings=InputMap.ActionGetEvents("move_protect");
        Require(bindings.Any(e=>e is InputEventKey k && k.PhysicalKeycode==Key.Ctrl),"Ctrl binding missing");
        Require(bindings.Any(e=>e is InputEventJoypadMotion j && j.Axis==JoyAxis.TriggerLeft && j.AxisValue>0),"LT binding missing");
        Input.ActionPress("move_sprint");
        _defender.SprintTickHostForHarness(Dt); _attacker.SprintTickHostForHarness(Dt);
        Require(_defender.IsInDefensiveStance && !_defender.IsProtecting && !_defender.IsSprinting,"off-ball role or sprint precedence failed");
        Require(_attacker.IsProtecting && !_attacker.IsInDefensiveStance && !_attacker.IsSprinting,"holder role precedence failed");
        _ball.StateMachine.ForceState(BallState.Held,2); _defender.SprintTickHostForHarness(Dt);
        Require(_defender.IsInDefensiveStance,"Held attacker did not allow stance");
        foreach(var state in new[]{BallState.Loose,BallState.InFlight})
        {
            _ball.StateMachine.ForceState(state,2); _defender.SprintTickHostForHarness(Dt);
            Require(!_defender.IsInDefensiveStance && !_defender.IsProtecting,"free ball posture benefit");
        }
        _ball.StateMachine.ForceState(BallState.Dribbling,2); _defender.SprintTickHostForHarness(Dt);
        Require(_defender.IsInDefensiveStance,"request did not resume without rearm");
        _players.RemoveChild(_attacker); _defender.SprintTickHostForHarness(Dt);
        Require(!_defender.IsInDefensiveStance && !_defender.IsProtecting,"missing opponent allowed posture");
        _players.AddChild(_attacker); _defender.SprintTickHostForHarness(Dt);
        Require(_defender.IsInDefensiveStance,"opponent return did not resume request"); Finish(true,"role eligibility and precedence");
    }
    private void Pivot()
    {
        _defender.StanceRequestForHarness(_ticks%2==1,Vector2.Zero);
        bool owed=_defender.IsPivotingInPlace;
        _defender.StanceTickAuthorityForHarness(Dt);
        if (_defender.IsPivotingInPlace)
        {
            Require(!_defender.IsInDefensiveStance,"owed pivot enabled stance");
            Require(_defender.GlobalPosition.DistanceTo(_origin)<.00001f,"owed pivot translated");
            Require(Math.Abs(Mathf.AngleDifference(_owedYaw,_defender.StanceOwedYawForHarness))<.00001f,"posture toggle erased owed target");
        }
        if (_defender.IsPivotingInPlace && _ticks<300) return;
        Require(owed && Math.Abs(Mathf.AngleDifference(_owedYaw,_defender.Heading))<.001f,"pivot did not resolve owed target");
        _defender.StanceRequestForHarness(true,Vector2.Zero); _defender.StanceTickAuthorityForHarness(Dt);
        Require(_defender.IsInDefensiveStance,"stance did not resume after owed pivot"); Finish(true,"owed target resolved through toggles");
    }
    private CommittedMove CreateMove()=>_stage==0 ? new StealMove(HandSide.Left,1) : _stage==1 ? new BlockMove() : new ContestMove();
    private void BeginPairMove()
    {
        Array.Clear(_observedPhases);
        Require(_defender.StanceBeginForHarness(CreateMove()) && _neutral.StanceBeginForHarness(CreateMove()),"real commitment rejected");
    }
    private void Committed()
    {
        _defender.StanceRequestForHarness(true,Vector2.Right); _neutral.StanceRequestForHarness(false,Vector2.Right);
        _defender.StanceTickAuthorityForHarness(Dt); _neutral.StanceTickAuthorityForHarness(Dt);
        Require(_defender.StancePhaseForHarness==_neutral.StancePhaseForHarness && _defender.StanceFrameForHarness==_neutral.StanceFrameForHarness,"posture changed commitment timeline");
        if (_defender.StancePhaseForHarness!=MovePhase.Inactive)
        {
            _observedPhases[(int)_defender.StancePhaseForHarness]=true;
            Require(!_defender.IsInDefensiveStance,"stance effective during commitment");
            Require(Math.Abs(Mathf.AngleDifference(_defender.Heading,_neutral.Heading))<.00001f,"stance altered commitment heading");
            Require((_defender.GlobalPosition-_origin).DistanceTo(_neutral.GlobalPosition-_neutralOrigin)<.0001f,"stance altered commitment displacement"); return;
        }
        Require(_observedPhases[(int)MovePhase.Startup] && _observedPhases[(int)MovePhase.Active] && _observedPhases[(int)MovePhase.Recovery],"missing committed phase positive premise");
        Require(_defender.IsInDefensiveStance,"stance did not resume after completion");
        _stage++; if (_stage==3) { Finish(true,"steal block contest phase/displacement controls"); return; }
        _defender.ResetContactStateForHarness(_origin,0,Vector3.Zero); _neutral.ResetContactStateForHarness(_neutralOrigin,0,Vector3.Zero); BeginPairMove();
    }
    private void SetupContact()
    {
        _defender.QueueFree(); _attacker.QueueFree();
        _contact=new ContactHarnessSeam { Name="Contact", FirstName="1",SecondName="2" }; AddChild(_contact);
        _contactControl=new ContactHarnessSeam { Name="ContactControl",FirstName="3",SecondName="4" }; AddChild(_contactControl);
        var fast=_scenario=="contact-fast"; var velocity=fast ? new Vector3(3,0,0) : Vector3.Zero;
        _contact.Reset(new Vector3(0,0,1.1f),Mathf.Pi,new Vector3(0,0,-6),Vector3.Zero,0,velocity);
        _contactControl.Reset(new Vector3(20,0,1.1f),Mathf.Pi,new Vector3(0,0,-6),new Vector3(20,0,0),0,velocity);
        var ball=new BallController { Name="ContactBall",Players=_contact.First.GetParent() }; _contact.AddChild(ball); ball.SetPhysicsProcess(false);
        ball.StateMachine.ForceState(BallState.Dribbling,1);
        _contact.First.SprintBindBallForHarness(ball); _contact.Second.SprintBindBallForHarness(ball);
        // No production ContactEnabled switch exists: collision-layer-zero is the shipped discovery control.
        // https://docs.godotengine.org/en/4.7/classes/class_collisionobject3d.html#class-collisionobject3d-property-collision-layer
        _contactControl.First.CollisionLayer=_contactControl.First.CollisionMask=0;
        _contactControl.Second.CollisionLayer=_contactControl.Second.CollisionMask=0;
        _origin=_contact.Second.GlobalPosition;
    }
    private void Contact()
    {
        var input=_scenario=="contact-fast" ? Vector2.Right : Vector2.Zero;
        _contact.Second.StanceRequestForHarness(true,input);
        _contactControl.Second.StanceRequestForHarness(true,input);
        _contact.First.StepProductionContactPairForHarness(_contact.Second,Dt,new Vector2(0,-1),input);
        _contactControl.First.StepProductionContactPairForHarness(_contactControl.Second,Dt,new Vector2(0,-1),input);
        Require(_contact.Second.IsInDefensiveStance,"real remote defender not in stance");
        if(_scenario=="contact-fast") Require(_contact.Second.Velocity.Length()>ContactSettings.Default.MaxSetSpeed,"fast defender lost unset-speed premise");
        if(_ticks<6) return;
        GD.Print($"[stance] contact {_scenario} defender={_contact.Second.GlobalPosition} drive-z={_contact.First.GlobalPosition.Z} disabled-z={_contactControl.First.GlobalPosition.Z}");
        Require(_contact.First.GlobalPosition.Z>_contactControl.First.GlobalPosition.Z+.02f,"positive paired contact effect absent");
        if(_scenario=="contact-slow") Require(_contact.Second.GlobalPosition.DistanceTo(_origin)<.01f,"slow set stance displaced");
        else Require(_contact.Second.GlobalPosition.Z<-.01f,"unset fast stance did not share impulse");
        Finish(true,"contact speed-set compatibility");
    }
    private void Finish(bool pass,string details)
    { GD.Print($"[stance] {(pass ? "PASS" : "FAIL")} {_scenario}: {details}"); SetPhysicsProcess(false); GetTree().Quit(pass?0:1); }
}
