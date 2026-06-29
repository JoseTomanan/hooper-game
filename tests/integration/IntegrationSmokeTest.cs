using Godot;

namespace HOOPERGAME.Tests.Integration;

// Headless integration SMOKE TEST — the Phase-1 go/no-go proof for ADR-0016.
//
// This is the first member of the headless verification harness. Its job is not
// to test gameplay yet, but to prove the harness MECHANISM works end to end on
// CI: a real Godot .NET engine boots under `--headless`, loads a real scene,
// pumps its fixed-tick physics loop, runs our compiled C#, asserts engine state,
// and reports pass/fail to CI purely through the process exit code.
//
//   Run:  godot --headless --path . res://tests/integration/SmokeTest.tscn
//   Exit: 0 = PASS, 1 = FAIL  (via GetTree().Quit(exitCode))
//
// WHY a frame-pumped SCENE and not a `--script` SceneTree:
//   Spike #87 (docs/spikes/0011-…) found that a `--script`/SceneTree run exits in
//   _init() BEFORE any frame is processed, so _PhysicsProcess never fires. Running
//   an actual scene starts the main loop, which ticks physics every frame even
//   headless — the same loop a dedicated server runs. That frame pump is exactly
//   what lets the harness step the deterministic sim (ADR-0002/0004) and assert
//   exact state, which is the capability unit tests structurally cannot reach
//   (they exclude every Node-derived type).
//
// WHAT this asserts: the fixed-tick invariant the whole deterministic sim and
// netcode prediction lean on — every physics delta is exactly
// 1 / physics_ticks_per_second, and N ticks accumulate to N * that delta with no
// drift. If that invariant ever broke, prediction/reconciliation and the ball
// mini-physics would silently desync; here it is asserted in the real engine.
public partial class IntegrationSmokeTest : Node
{
    // 30 fixed ticks ≈ 0.5 s at the default 60 Hz — enough to prove the loop
    // pumps repeatably without making CI wait.
    private const int TargetTicks = 30;

    // Per-tick float tolerance: physics delta is a float reciprocal, so allow
    // a small epsilon rather than demanding bit-exactness.
    private const double TickTolerance = 1e-6;
    private const double TotalTolerance = 1e-4;

    private int _ticks;
    private double _accumulatedDelta;
    private int _failures;
    private bool _finished;

    public override void _Ready()
    {
        GD.Print("[harness] IntegrationSmokeTest booting headless…");

        // Hosting proof: we are a real Node inside a live SceneTree (the thing a
        // `--script` SceneTree run cannot give us a frame pump for).
        if (GetTree() == null)
        {
            Fail("GetTree() was null — not running inside a SceneTree");
            Finish();
            return;
        }

        GD.Print($"[harness] physics_ticks_per_second = {Engine.PhysicsTicksPerSecond}");
        GD.Print($"[harness] godot version = {Engine.GetVersionInfo()["string"]}");
    }

    public override void _PhysicsProcess(double delta)
    {
        // GetTree().Quit() requests termination at frame end, but Godot does not
        // strictly guarantee zero further _PhysicsProcess calls. Without this
        // guard a stray extra tick would re-enter the TargetTicks branch, where
        // expectedTotal is pinned to TargetTicks but _accumulatedDelta has grown,
        // failing the drift check and flipping the exit code 0→1 spuriously.
        if (_finished)
        {
            return;
        }

        _ticks++;
        _accumulatedDelta += delta;

        double expected = 1.0 / Engine.PhysicsTicksPerSecond;
        if (Mathf.Abs(delta - expected) > TickTolerance)
        {
            Fail($"non-fixed physics delta at tick {_ticks}: {delta:R} != {expected:R}");
        }

        if (_ticks >= TargetTicks)
        {
            double expectedTotal = TargetTicks * expected;
            if (Mathf.Abs(_accumulatedDelta - expectedTotal) > TotalTolerance)
            {
                Fail($"accumulated-delta drift over {TargetTicks} ticks: " +
                     $"{_accumulatedDelta:R} != {expectedTotal:R}");
            }
            Finish();
        }
    }

    private void Fail(string message)
    {
        _failures++;
        GD.PrintErr($"[harness] FAIL: {message}");
    }

    private void Finish()
    {
        _finished = true;
        if (_failures == 0)
        {
            GD.Print($"[harness] PASS — {_ticks} fixed ticks, deterministic, " +
                     $"accumulated {_accumulatedDelta:R}s.");
        }
        else
        {
            GD.PrintErr($"[harness] RESULT: FAIL — {_failures} failure(s).");
        }

        // The exit code IS the CI signal (ADR-0016): 0 pass, 1 fail.
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
