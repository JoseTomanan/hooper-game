using System.Linq;
using Godot;

namespace Hooper.Networking;

/// <summary>
/// Selects the dedicated-server entry path at launch (ADR-0007).
///
/// Main.tscn is shared by all three roles — client, listen-server host, and
/// headless dedicated server. This node is what distinguishes the third: if the
/// process was launched with the user argument <c>--dedicated</c>, it hides the
/// Lobby overlay and calls NetworkManager.StartDedicatedServer(port). Otherwise
/// it does nothing, and the normal Lobby (Host/Join) flow runs unchanged.
///
/// ── Why user args, not engine args ────────────────────────────────────────
/// We read OS.GetCmdlineUserArgs() — the arguments that appear AFTER a standalone
/// "--" separator. Godot ignores everything after "--" and reserves it for the
/// game, so our flags can never collide with an engine flag (current or future).
/// The full launch line for a headless dedicated server is:
///
///     "HOOPER GAME.exe" --headless -- --dedicated --port 7777
///
///   • --headless is an ENGINE flag (before "--"): it expands to
///     "--display-driver headless --audio-driver Dummy", so no window or audio
///     device is opened. The authoritative simulation runs with no display.
///   • --dedicated and --port are OUR user args (after "--"), read here.
///
/// Sources (verified against live Godot docs, ADR-0001; Context7 MCP was not
/// connected this session, so the official docs were used directly):
///   - https://docs.godotengine.org/en/stable/classes/class_os.html
///     (GetCmdlineUserArgs returns the PackedStringArray after the "--" separator)
///   - https://docs.godotengine.org/en/stable/tutorials/editor/command_line_tutorial.html
///     (--headless = --display-driver headless --audio-driver Dummy)
///   - https://docs.godotengine.org/en/stable/tutorials/export/exporting_for_dedicated_servers.html
///     (since 4.0 a dedicated server is "a Godot binary on any platform with the
///     --headless argument, or a project exported as dedicated server"; the
///     --server-flag-in-_ready pattern this class mirrors is from that page)
///
/// Scene wiring (editor, M6 — see EDITOR_TASKS.md):
///   - Add a Node named "DedicatedServerBootstrap" under Main, attach this script.
///   - Assign NetworkManager (the Main.tscn NetworkManager node) and Lobby (the
///     instanced Lobby) in the Inspector.
/// </summary>
public partial class DedicatedServerBootstrap : Node
{
	// ── Exports: assigned in the Godot Inspector ─────────────────────────────

	/// <summary>The Main.tscn NetworkManager. Assign in the Inspector.</summary>
	[Export] public NetworkManager NetworkManager { get; set; }

	/// <summary>
	/// The Lobby overlay to remove on the dedicated path. Optional — a dedicated
	/// server has no use for it, but if left unassigned the server still starts
	/// (the lobby just stays visible to no one, since there is no display).
	/// </summary>
	[Export] public CanvasLayer Lobby { get; set; }

	public override void _Ready()
	{
		// Merge BOTH arg arrays before parsing. GetCmdlineUserArgs() is the
		// documented home for args after the "--" separator (class_os.html) and is
		// the canonical launch form. But the official dedicated-server tutorial
		// shows "godot --headless --server" reading --server from user args WITHOUT
		// a "--" — a routing nuance that varies, so we also include GetCmdlineArgs()
		// to accept either invocation style rather than bet on one. User args go
		// first so an explicit post-"--" value wins.
		string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();

		if (!DedicatedServerArgs.IsDedicated(args))
			return; // Normal client/host launch — leave the Lobby flow untouched.

		if (NetworkManager == null)
		{
			GD.PrintErr("[DedicatedServerBootstrap] --dedicated given but NetworkManager is not assigned. Cannot start server.");
			return;
		}

		int port = DedicatedServerArgs.ParsePort(args, NetworkManager.DefaultPort);

		// Remove the lobby — a headless server never interacts with it. QueueFree
		// (not just hide) so it stops processing entirely.
		Lobby?.QueueFree();

		// Defer one frame so the entire Main scene tree (NetworkManager's exports,
		// the MultiplayerSpawner sibling) is fully ready before we bring the
		// server up and start accepting peers. _Ready order across siblings is not
		// something we should depend on for transport bringup.
		CallDeferred(MethodName.StartServerDeferred, port);
	}

	private void StartServerDeferred(int port)
	{
		GD.Print("[DedicatedServerBootstrap] --dedicated detected; starting headless server on port ", port);
		NetworkManager.StartDedicatedServer(port);
	}
}
