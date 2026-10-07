namespace HOOPERGAME.Tests.Integration;

// Formatting only: callers own messages, verdicts and SceneTree.Quit(exitCode).
public sealed class HarnessReport
{
    public string Prefix { get; }
    public string Scenario { get; }
    public HarnessReport(string prefix, string scenario) { Prefix = prefix; Scenario = scenario; }
    public string Failure(string message) => $"[{Prefix}] FAIL: {message}";
    public string Result(int exitCode) => $"[{Prefix}] RESULT: {(exitCode == 0 ? "PASS" : "FAIL")} (exit {exitCode})";
}
