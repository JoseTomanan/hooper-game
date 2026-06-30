using Godot;

namespace HOOPERGAME.Tests.Integration;

// Headless integration test for issue #101: Input Map entries — def_steal,
// def_block & def_contest.
//
// WHY InputMap, not just a headless load:
//   A malformed [input] block can survive the INI-level parse but simply drop
//   the entry, leaving InputMap silent and empty for that key. A bare --quit
//   load proves the file parsed without crashing, but not that the three
//   actions are actually registered. InputMap.HasAction() checks the engine's
//   live registry, which is exactly the contract these entries must satisfy.
//
//   Run:  godot --headless --path . res://tests/integration/InputMapDefensiveActionsTest.tscn
//   Exit: 0 = PASS, 1 = FAIL  (via GetTree().Quit(exitCode))
public partial class InputMapDefensiveActionsTest : Node
{
    private static readonly string[] RequiredActions =
    [
        "def_steal",
        "def_block",
        "def_contest",
    ];

    private int _failures;

    public override void _Ready()
    {
        GD.Print("[harness] InputMapDefensiveActionsTest — checking defensive Input Map actions…");

        foreach (string action in RequiredActions)
        {
            if (InputMap.HasAction(action))
            {
                GD.Print($"[harness]   OK  InputMap.HasAction(\"{action}\") = true");
            }
            else
            {
                _failures++;
                GD.PrintErr($"[harness] FAIL InputMap.HasAction(\"{action}\") = false — action not registered");
            }
        }

        if (_failures == 0)
        {
            GD.Print("[harness] PASS — all three defensive actions registered in InputMap.");
        }
        else
        {
            GD.PrintErr($"[harness] RESULT: FAIL — {_failures} action(s) missing from InputMap.");
        }

        // Exit 0 = pass, 1 = fail (ADR-0016 harness convention).
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
