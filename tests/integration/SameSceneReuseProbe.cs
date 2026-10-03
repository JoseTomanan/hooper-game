using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace HOOPERGAME.Tests.Integration;

// Disposable, non-authoritative process-reuse probe for issue #388. The real
// catalog still launches one process per HarnessCase. This scene exists only to
// measure what survives SceneTree.ChangeSceneToFile in Godot 4.7.
//
// Sources:
// - Scene replacement is deferred and invalidates outgoing node references:
//   https://docs.godotengine.org/en/4.7/classes/class_scenetree.html#class-scenetree-method-change-scene-to-file
// - Input is a global singleton:
//   https://docs.godotengine.org/en/4.7/classes/class_input.html
// - ResourceLoader reuses cached instances by default:
//   https://docs.godotengine.org/en/4.7/classes/class_resourceloader.html#enum-resourceloader-cachemode
public partial class SameSceneReuseProbe : Node
{
    private const string ScenePath = "res://tests/integration/SameSceneReuseProbe.tscn";
    private const string ResourcePath = "res://tests/integration/SameSceneReuseProbeResource.tres";
    private const string InputAction = "move_left";
    private const string LeakMeta = "same_scene_probe_leak";
    private const string FaceToFaceId = "steal-facing-mapping-test-face-to-face";
    private const string SideBySideId = "steal-facing-mapping-test-side-by-side";

    private static string[] _caseIds = [];
    private static string _eventPath = "";
    private static string _intentionalLeak = "none";
    private static string _failureMode = "none";
    private static int _caseIndex;
    private static int _staticSentinel;
    private static int _timerSignals;
    private static int _deferredSignals;
    private static ulong _previousRootId;
    private static Node _cachedRoot;
    private static bool _initialized;
    private static bool _leakInjected;
    private static bool _anyFailure;

    private List<string> _currentResidues = [];

    public override async void _Ready()
    {
        try
        {
            InitializeOnce();
            await RunCurrentCase();
        }
        catch (Exception exception)
        {
            string caseId = CurrentCaseId();
            WriteEvent("result", caseId, "fail", $"probe exception: {exception.Message}");
            ClearAllLeakSurfaces();
            GetTree().Quit(1);
        }
    }

    private void InitializeOnce()
    {
        if (_initialized)
            return;

        string[] args = OS.GetCmdlineUserArgs();
        _caseIds = HarnessArgs.ReadArg(args, "--probe-sequence", $"{FaceToFaceId},{SideBySideId},{FaceToFaceId}")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _eventPath = ProjectSettings.GlobalizePath(HarnessArgs.ReadArg(args, "--probe-events", "res://.godot/same-scene-probe.jsonl"));
        _intentionalLeak = HarnessArgs.ReadArg(args, "--probe-leak", "none");
        _failureMode = HarnessArgs.ReadArg(args, "--probe-failure", "none");

        if (_caseIds.Length == 0)
            throw new InvalidOperationException("--probe-sequence must contain at least one stable case ID");

        Directory.CreateDirectory(Path.GetDirectoryName(_eventPath) ?? ".");
        File.WriteAllText(_eventPath, "");
        _initialized = true;
    }

    private async Task RunCurrentCase()
    {
        string caseId = CurrentCaseId();
        WriteEvent("start", caseId);

        if (_failureMode == "timeout" && _caseIndex == 0)
            return; // The parent must time out, retain the log, and name this active ID.
        if (_failureMode == "crash" && _caseIndex == 0)
        {
            GetTree().Quit(23); // Abrupt exit before a result simulates crash accounting portably.
            return;
        }

        // Scene changes finish at frame end. Two frames also give intentional
        // zero-delay SceneTreeTimer/deferred mutations a chance to expose that
        // they live outside the outgoing scene.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        _currentResidues = InspectResidues();
        var harness = new StealFacingMappingTest { Name = "CharacterizedHarnessCase" };
        harness.ConfigureForReuseProbe(ScenarioFor(caseId), OnHarnessFinished);
        AddChild(harness);
    }

    private void OnHarnessFinished(StealFacingProbeResult result)
    {
        try
        {
            string caseId = CurrentCaseId();
            bool forcedAssertion = _failureMode == "assert" && _caseIndex == 0;
            bool passed = result.ExitCode == 0 && _currentResidues.Count == 0 && !forcedAssertion;
            List<string> failures = [.. _currentResidues];
            if (result.ExitCode != 0)
                failures.Add($"embedded harness exited {result.ExitCode}");
            if (forcedAssertion)
                failures.Add("intentional assertion failure");
            string message = string.Join("; ", failures);
            Dictionary<string, object> observations = new()
            {
                ["scenario"] = result.Scenario,
                ["ever_loose"] = result.EverLoose,
                ["final_state"] = result.FinalState.ToString(),
                ["holder_peer_id"] = result.HolderPeerId,
                ["toucher_at_steal"] = result.ToucherAtSteal,
                ["verdict_frame"] = result.VerdictFrame,
            };

            if (!(_failureMode == "missing" && _caseIndex == 0))
            {
                WriteEvent("result", caseId, passed ? "pass" : "fail", message, observations);
                if (_failureMode == "duplicate" && _caseIndex == 0)
                    WriteEvent("result", caseId, passed ? "pass" : "fail", message, observations);
            }
            _anyFailure |= !passed;

            FinishOrAdvance();
        }
        catch (Exception exception)
        {
            WriteEvent("result", CurrentCaseId(), "fail", $"probe callback exception: {exception.Message}");
            ClearAllLeakSurfaces();
            GetTree().Quit(1);
        }
    }

    private void FinishOrAdvance()
    {

        // Recover even after an intentional leak so A->B->A proves the third
        // scene does not inherit the detected residue.
        ClearAllLeakSurfaces();
        if (_caseIndex + 1 >= _caseIds.Length)
        {
            int exitCode = _anyFailure ? 1 : 0;
            ResetCoordinator();
            GetTree().Quit(exitCode);
            return;
        }

        PrepareBoundaryMutation();
        _previousRootId = GetInstanceId();
        _caseIndex++;
        Error changeError = GetTree().ChangeSceneToFile(ScenePath);
        if (changeError != Error.Ok)
            throw new InvalidOperationException($"ChangeSceneToFile failed: {changeError}");
    }

    private static string ScenarioFor(string caseId) => caseId switch
    {
        FaceToFaceId => "face-to-face",
        SideBySideId => "side-by-side",
        _ => throw new InvalidOperationException($"Uncharacterized catalog case ID: {caseId}"),
    };

    private List<string> InspectResidues()
    {
        List<string> residues = [];
        Node autoload = GetTree().Root.GetNodeOrNull<Node>("MCPRuntimeBridge");
        Resource resource = ResourceLoader.Load<Resource>(ResourcePath);

        if (_previousRootId != 0 && _previousRootId == GetInstanceId())
            residues.Add("scene root instance was reused");
        if (_staticSentinel != 0)
            residues.Add("static C# state leaked");
        if (Input.IsActionPressed(InputAction))
            residues.Add("Input singleton state leaked");
        if (resource.HasMeta(LeakMeta))
            residues.Add("ResourceLoader cache mutation leaked");
        if (autoload is null)
            residues.Add("autoload fixture missing");
        else if (autoload.HasMeta(LeakMeta))
            residues.Add("autoload state leaked");
        if (_timerSignals != 0)
            residues.Add("SceneTreeTimer callback leaked");
        if (_deferredSignals != 0)
            residues.Add("deferred callback leaked");
        if (_cachedRoot is not null)
            residues.Add($"cached node reference leaked (native_valid={GodotObject.IsInstanceValid(_cachedRoot)})");

        return residues;
    }

    private void PrepareBoundaryMutation()
    {
        _staticSentinel = 1;
        Input.ActionPress(InputAction);
        ResourceLoader.Load<Resource>(ResourcePath).SetMeta(LeakMeta, true);
        GetTree().Root.GetNodeOrNull<Node>("MCPRuntimeBridge")?.SetMeta(LeakMeta, true);
        _cachedRoot = this;

        string omittedReset = _leakInjected ? "none" : _intentionalLeak;
        _leakInjected = true;
        if (omittedReset == "timer")
            GetTree().CreateTimer(0.0).Timeout += () => _timerSignals++;
        if (omittedReset == "deferred")
            Callable.From(() => _deferredSignals++).CallDeferred();

        if (omittedReset != "static")
            _staticSentinel = 0;
        if (omittedReset != "input")
            Input.ActionRelease(InputAction);
        if (omittedReset != "resource")
            ResourceLoader.Load<Resource>(ResourcePath).RemoveMeta(LeakMeta);
        if (omittedReset != "autoload")
            GetTree().Root.GetNodeOrNull<Node>("MCPRuntimeBridge")?.RemoveMeta(LeakMeta);
        if (omittedReset != "cached-node")
            _cachedRoot = null;
    }

    private static void ClearAllLeakSurfaces()
    {
        _staticSentinel = 0;
        _timerSignals = 0;
        _deferredSignals = 0;
        _cachedRoot = null;
        Input.ActionRelease(InputAction);
        ResourceLoader.Load<Resource>(ResourcePath).RemoveMeta(LeakMeta);
        (Engine.GetMainLoop() as SceneTree)?.Root.GetNodeOrNull<Node>("MCPRuntimeBridge")?.RemoveMeta(LeakMeta);
    }

    private static void ResetCoordinator()
    {
        // Do not erase _anyFailure until the caller has chosen the exit code.
        _caseIds = [];
        _eventPath = "";
        _intentionalLeak = "none";
        _failureMode = "none";
        _caseIndex = 0;
        _previousRootId = 0;
        _initialized = false;
        _leakInjected = false;
        _anyFailure = false;
    }

    private static string CurrentCaseId() =>
        _caseIndex < _caseIds.Length ? _caseIds[_caseIndex] : "probe-bootstrap";

    private static void WriteEvent(
        string eventKind,
        string caseId,
        string status = null,
        string message = null,
        Dictionary<string, object> observations = null)
    {
        Dictionary<string, object> payload = new()
        {
            ["event"] = eventKind,
            ["case_id"] = caseId,
            ["invocation"] = _caseIndex + 1,
            ["status"] = status,
            ["message"] = message,
            ["observations"] = observations,
            ["unix_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        File.AppendAllText(_eventPath, JsonSerializer.Serialize(payload) + System.Environment.NewLine);
    }
}
