using System;
using System.Collections.Generic;

namespace Hooper.Networking;

/// <summary>
/// Pure command-line parsing for the dedicated-server launch flags — no Godot
/// Node inheritance, no engine singletons. Separated from DedicatedServerBootstrap
/// (which extends Node and only does engine glue) so this decision logic is
/// unit-testable via the GodotSharp-NuGet test seam, the same split the Ball uses
/// (BallStateMachine pure vs BallController node). See ADR-0007.
///
/// Callers pass the combined argument list (user args after "--", plus engine
/// args) — the bootstrap merges OS.GetCmdlineUserArgs() and OS.GetCmdlineArgs()
/// to accept either invocation style; see DedicatedServerBootstrap for why.
/// </summary>
public static class DedicatedServerArgs
{
	/// <summary>Selects the headless dedicated-server path.</summary>
	public const string DedicatedFlag = "--dedicated";

	/// <summary>Overrides the listen port: "--port 7777" (two tokens) or "--port=7777".</summary>
	public const string PortFlag = "--port";

	/// <summary>True if <see cref="DedicatedFlag"/> appears anywhere in <paramref name="args"/>.</summary>
	public static bool IsDedicated(IReadOnlyList<string> args)
	{
		if (args == null) return false;
		for (int i = 0; i < args.Count; i++)
			if (args[i] == DedicatedFlag)
				return true;
		return false;
	}

	/// <summary>
	/// Reads the port from "--port=7777" (joined) or "--port 7777" (separate
	/// token). Returns <paramref name="fallback"/> if the flag is absent, has no
	/// value, or the value is not a valid 1..65535 port. The first occurrence wins.
	/// </summary>
	public static int ParsePort(IReadOnlyList<string> args, int fallback)
	{
		if (args == null) return fallback;

		for (int i = 0; i < args.Count; i++)
		{
			string a = args[i];

			if (a.StartsWith(PortFlag + "=", StringComparison.Ordinal))
				return ValidPortOr(a.Substring(PortFlag.Length + 1), fallback);

			if (a == PortFlag && i + 1 < args.Count)
				return ValidPortOr(args[i + 1], fallback);
		}
		return fallback;
	}

	private static int ValidPortOr(string text, int fallback) =>
		int.TryParse(text, out int port) && port is > 0 and < 65536 ? port : fallback;
}
