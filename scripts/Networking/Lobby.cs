using Godot;

namespace Hooper.Networking;

/// <summary>
/// Drives the editor-authored Lobby UI (scenes/Lobby.tscn).
///
/// Responsibilities:
///   • Wire button Pressed signals to host/join actions (in code — the human
///     only assigns node references in the Inspector, not signals).
///   • Read IP/port from the LineEdit fields and hand them to NetworkManager.
///   • Hide the lobby layer once the game is ready.
///
/// All node references are [Export] so they are assigned in the Godot Inspector
/// by the human (editor step, issue #7). This script must NOT be touched for
/// different IP/port — those are runtime inputs from the LineEdits.
///
/// Scene wiring required (editor, issue #7):
///   1. Create scenes/Lobby.tscn with root CanvasLayer.
///   2. Children: LineEdit "IpField", LineEdit "PortField",
///                Button "HostButton", Button "JoinButton".
///   3. Attach this script to the root CanvasLayer node.
///   4. In the Inspector, assign IpField, PortField, HostButton, JoinButton,
///      and NetworkManager (from Main.tscn's NetworkManager node).
///   5. Instance Lobby.tscn as a child of Main in Main.tscn.
/// </summary>
public partial class Lobby : CanvasLayer
{
    // ── Exports: assigned in the Godot Inspector ─────────────────────────────

    [Export] public LineEdit IpField { get; set; }
    [Export] public LineEdit PortField { get; set; }
    [Export] public Button HostButton { get; set; }
    [Export] public Button JoinButton { get; set; }

    /// <summary>
    /// The NetworkManager node in Main.tscn. Assign in the Inspector.
    /// Lobby delegates all transport decisions to it.
    /// </summary>
    [Export] public NetworkManager NetworkManager { get; set; }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    public override void _Ready()
    {
        // Validate exports early — missing a wiring is the most common new-dev
        // mistake and a null-ref mid-game is harder to diagnose.
        if (!ValidateExports()) return;

        // Pre-fill sensible defaults so the user can just press Host/Join
        // for a localhost test without typing anything.
        IpField.Text   = "127.0.0.1";
        PortField.Text = NetworkManager.DefaultPort.ToString();

        // Wire signals in code: the human assigns node refs in Inspector only.
        // This avoids the "Connect Signal" editor step which is error-prone for
        // newcomers and produces invisible coupling in the .tscn file.
        HostButton.Pressed += OnHostPressed;
        JoinButton.Pressed += OnJoinPressed;

        // React to NetworkManager's outcome signals.
        NetworkManager.GameReady        += OnGameReady;
        NetworkManager.ConnectionFailed += OnConnectionFailed;
    }

    /// <summary>
    /// Unsubscribe event handlers to avoid dangling references if the lobby
    /// node is freed (e.g., scene reload).
    /// (Found in doubt-driven review, cycle 1.)
    /// </summary>
    public override void _ExitTree()
    {
        if (HostButton     != null) HostButton.Pressed     -= OnHostPressed;
        if (JoinButton     != null) JoinButton.Pressed     -= OnJoinPressed;
        if (NetworkManager != null)
        {
            NetworkManager.GameReady        -= OnGameReady;
            NetworkManager.ConnectionFailed -= OnConnectionFailed;
        }
    }

    // ── Button handlers ──────────────────────────────────────────────────────

    private void OnHostPressed()
    {
        int port = ParsePort();
        GD.Print("[Lobby] Hosting on port ", port);
        DisableButtons();
        NetworkManager.HostGame(port);
    }

    private void OnJoinPressed()
    {
        string ip   = IpField.Text.Trim();
        int    port = ParsePort();

        if (string.IsNullOrEmpty(ip))
        {
            GD.PrintErr("[Lobby] IP field is empty.");
            return;
        }

        GD.Print("[Lobby] Joining ", ip, ":", port);
        DisableButtons();
        NetworkManager.JoinGame(ip, port);
    }

    // ── NetworkManager callbacks ─────────────────────────────────────────────

    private void OnGameReady()
    {
        // Hide the lobby overlay — the players are in the scene, game is live.
        Visible = false;
        GD.Print("[Lobby] Game ready — lobby hidden.");
    }

    private void OnConnectionFailed()
    {
        // Re-enable the buttons so the user can retry.
        GD.PrintErr("[Lobby] Connection failed — re-enabling buttons.");
        EnableButtons();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private int ParsePort()
    {
        if (int.TryParse(PortField.Text.Trim(), out int port) && port is > 0 and < 65536)
            return port;

        GD.Print("[Lobby] Invalid port; using default ", NetworkManager.DefaultPort);
        return NetworkManager.DefaultPort;
    }

    private void DisableButtons()
    {
        HostButton.Disabled = true;
        JoinButton.Disabled = true;
    }

    private void EnableButtons()
    {
        HostButton.Disabled = false;
        JoinButton.Disabled = false;
    }

    private bool ValidateExports()
    {
        bool ok = true;
        if (IpField        == null) { GD.PrintErr("[Lobby] IpField not assigned.");        ok = false; }
        if (PortField      == null) { GD.PrintErr("[Lobby] PortField not assigned.");       ok = false; }
        if (HostButton     == null) { GD.PrintErr("[Lobby] HostButton not assigned.");      ok = false; }
        if (JoinButton     == null) { GD.PrintErr("[Lobby] JoinButton not assigned.");      ok = false; }
        if (NetworkManager == null) { GD.PrintErr("[Lobby] NetworkManager not assigned.");  ok = false; }
        return ok;
    }
}
