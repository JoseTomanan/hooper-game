namespace HOOPERGAME.Tests.Integration;

// Shared command-line arg reader for the headless harnesses (ADR-0016). Every
// harness scene reads its scenario/role/port flags off OS.GetCmdlineUserArgs()
// the same way; this was duplicated byte-for-byte across NetHandshakeTest,
// NetStateSyncTest, NetNodeReplicationTest, and StealTurnoverTest until the
// issue #96 remediation review flagged the third copy — extracted here so a
// future harness (and #98/#99's block/contest scenes) reuse it instead of
// pasting a fourth.
internal static class HarnessArgs
{
    /// <summary>
    /// Supports "--flag value" (two tokens) and "--flag=value" (joined), mirroring
    /// DedicatedServerArgs' tolerance for both spellings.
    /// </summary>
    public static string ReadArg(string[] args, string flag, string fallback)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == flag && i + 1 < args.Length)
                return args[i + 1];
            if (args[i].StartsWith(flag + "="))
                return args[i].Substring(flag.Length + 1);
        }
        return fallback;
    }
}
