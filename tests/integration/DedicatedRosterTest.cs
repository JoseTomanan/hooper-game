using Godot;
using Hooper.Player;
using Hooper.Systems;

namespace HOOPERGAME.Tests.Integration;

/// <summary>
/// Focused regression for the dedicated-server roster correction in #366.
/// A dedicated server has no player at peer 1: its two real players are the
/// first two remote IDs. This fixture uses those real PlayerController nodes,
/// rather than mocking the roster, then proves the second slot disappears as
/// soon as its node is queued for deletion.
/// </summary>
public partial class DedicatedRosterTest : Node
{
	private const double TimeoutSeconds = 3.0;

	private readonly Node3D _players = new() { Name = "Players" };
	private readonly PlayerController _firstRemote = new() { Name = "2" };
	private readonly PlayerController _secondRemote = new() { Name = "3" };
	private readonly GameManager _gameManager = new() { Name = "GameManager" };

	private double _elapsed;
	private int _phase;
	private bool _finished;

	public override void _Ready()
	{
		AddChild(_players);
		_players.AddChild(_firstRemote);
		_players.AddChild(_secondRemote);
		AddChild(_gameManager);
		GD.Print("[dedicated-roster] booted with remote peers 2 and 3; awaiting live roster snapshot.");
	}

	public override void _PhysicsProcess(double delta)
	{
		if (_finished) return;
		_elapsed += delta;

		if (_phase == 0
			&& _gameManager.OpponentPeerIdFor(2) == 3
			&& _gameManager.OpponentPeerIdFor(3) == 2)
		{
			GD.Print("[dedicated-roster] PASS pair: remotes 2 and 3 are each other's opponent (no phantom peer 1).");
			_secondRemote.QueueFree();
			_phase = 1;
		}
		else if (_phase == 1 && _gameManager.OpponentPeerIdFor(2) == 0)
		{
			GD.Print("[dedicated-roster] PASS shrink: queued remote 3 was removed from the live roster.");
			GD.Print("[dedicated-roster] RESULT: PASS (exit 0)");
			Finish(0);
			return;
		}

		if (_elapsed > TimeoutSeconds)
		{
			GD.PrintErr($"[dedicated-roster] FAIL: timed out in phase {_phase}; opponent(2)={_gameManager.OpponentPeerIdFor(2)}, opponent(3)={_gameManager.OpponentPeerIdFor(3)}.");
			Finish(1);
		}
	}

	private void Finish(int code)
	{
		_finished = true;
		GetTree().Quit(code);
	}
}
