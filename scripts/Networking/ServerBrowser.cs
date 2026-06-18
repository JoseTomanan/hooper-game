using System.Collections.Generic;
using Godot;

namespace Hooper.Networking;

/// <summary>
/// The LAN server browser (ADR-0007): lists servers discovered by a
/// DiscoveryListener and joins the selected one via the existing
/// NetworkManager.JoinGame. The CS-1.6-style replacement for typing an IP.
///
/// It adds NO new connection path — discovery only supplies an IP:port that the
/// same JoinGame the Lobby uses then dials. It reuses NetworkManager's GameReady
/// / ConnectionFailed signals to hide on success and resume on failure, exactly
/// like the Lobby.
///
/// Convention (same as Lobby): node references are [Export]s assigned in the
/// Inspector; signals are wired here in code, not in the editor.
///
/// Scene wiring (editor, M6 — see EDITOR_TASKS.md):
///   - ServerBrowser.tscn: root CanvasLayer (this script) with an ItemList child
///     and a child DiscoveryListener node.
///   - Assign Discovery, NetworkManager, and ServerListUi in the Inspector.
/// </summary>
public partial class ServerBrowser : CanvasLayer
{
	// ── Exports: assigned in the Godot Inspector ─────────────────────────────

	/// <summary>The discovery listener that collects beacons. Assign in Inspector.</summary>
	[Export] public DiscoveryListener Discovery { get; set; }

	/// <summary>NetworkManager — JoinGame target + outcome signals. Assign in Inspector.</summary>
	[Export] public NetworkManager NetworkManager { get; set; }

	/// <summary>ItemList control that displays one row per discovered server.</summary>
	[Export] public ItemList ServerListUi { get; set; }

	/// <summary>How often (seconds) to rebuild the visible list from discovery.</summary>
	[Export] public float RefreshInterval { get; set; } = 0.5f;

	// ── State ─────────────────────────────────────────────────────────────────

	/// <summary>
	/// Servers parallel to ItemList rows, so an activated row index maps back to
	/// the IP/port to dial. Rebuilt each refresh from the discovery snapshot.
	/// </summary>
	private readonly List<ServerListEntry> _rows = new();
	private double _sinceRefresh;

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		if (!ValidateExports()) return;

		// Double-click / Enter on a row joins it.
		ServerListUi.ItemActivated += OnItemActivated;

		NetworkManager.GameReady        += OnGameReady;
		NetworkManager.ConnectionFailed += OnConnectionFailed;

		// Begin collecting beacons while the browser is open.
		Discovery.StartListening();
	}

	public override void _ExitTree()
	{
		if (ServerListUi != null) ServerListUi.ItemActivated -= OnItemActivated;
		if (NetworkManager != null)
		{
			NetworkManager.GameReady        -= OnGameReady;
			NetworkManager.ConnectionFailed -= OnConnectionFailed;
		}
	}

	public override void _Process(double delta)
	{
		if (Discovery == null) return;

		_sinceRefresh += delta;
		if (_sinceRefresh < RefreshInterval) return;
		_sinceRefresh = 0.0;

		RefreshRows();
	}

	// ── List rendering ──────────────────────────────────────────────────────

	/// <summary>
	/// Rebuilds the ItemList from the current discovery snapshot. A full rebuild
	/// each interval is fine for a LAN-sized list and keeps the row→server mapping
	/// trivially correct without diffing.
	/// </summary>
	private void RefreshRows()
	{
		_rows.Clear();
		ServerListUi.Clear();

		foreach (ServerListEntry e in Discovery.DiscoveredServers)
		{
			_rows.Add(e);
			ServerListUi.AddItem($"{e.Name}   ({e.CurPlayers}/{e.MaxPlayers})   {e.Ip}:{e.GamePort}");
		}
	}

	// ── Handlers ──────────────────────────────────────────────────────────────

	private void OnItemActivated(long index)
	{
		int i = (int)index;
		if (i < 0 || i >= _rows.Count) return;

		ServerListEntry e = _rows[i];
		GD.Print($"[ServerBrowser] Joining {e.Name} at {e.Ip}:{e.GamePort}");

		// Stop listening before we join — we're leaving the browser, and the bound
		// discovery port should be released.
		Discovery.StopListening();
		NetworkManager.JoinGame(e.Ip, e.GamePort);
	}

	private void OnGameReady()
	{
		Visible = false;
		Discovery.StopListening();
	}

	private void OnConnectionFailed()
	{
		// Join failed — stay on the browser and resume discovery so the user can
		// pick again (StartListening is idempotent if it never stopped).
		GD.PrintErr("[ServerBrowser] Join failed — resuming discovery.");
		Discovery.StartListening();
	}

	// ── Helpers ──────────────────────────────────────────────────────────────

	private bool ValidateExports()
	{
		bool ok = true;
		if (Discovery      == null) { GD.PrintErr("[ServerBrowser] Discovery not assigned.");      ok = false; }
		if (NetworkManager == null) { GD.PrintErr("[ServerBrowser] NetworkManager not assigned."); ok = false; }
		if (ServerListUi   == null) { GD.PrintErr("[ServerBrowser] ServerListUi not assigned.");    ok = false; }
		return ok;
	}
}
