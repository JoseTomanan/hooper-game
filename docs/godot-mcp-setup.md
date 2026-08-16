# Enabling the in-editor Godot MCP server

`addons/godot_dotnet_mcp/` runs an HTTP MCP server *inside the running Godot
editor process*, so a Claude Code session can read live editor/scene/runtime state
(selected node, output, diagnostics) that a filesystem snapshot can't see.

It is only useful while the editor is open, which is why these steps live here
rather than in `CLAUDE.md` — you need them once per machine, not once per session.

## Steps

1. Open the project in the **Godot .NET editor**.
2. Enable the plugin under `Project Settings > Plugins`.
3. Open the `MCPDock` panel and start the service (default
   `http://127.0.0.1:3000/mcp`).
4. From a Claude Code session whose cwd is this repo, run:

   ```
   claude mcp add --transport http --scope local godot-mcp http://127.0.0.1:3000/mcp
   ```

5. A newly added server needs a session restart (or `/mcp`) before its tools load.

## Gotchas

- Enabling the plugin writes an `[autoload]` singleton and an
  `[editor_plugins]/enabled` entry into `project.godot`. That is **expected**, not
  a stray edit — don't revert it.
- The `dotnet_bridge/` subproject is excluded from the game assembly the same way
  `tests/` is (see `HOOPER GAME.csproj`). **Never remove that exclusion** — the
  bridge references Roslyn packages the game assembly doesn't have.
- The MCP server resolves its **own** `GODOT_PATH` at spawn time from
  `~/.claude.json`, independent of the `$GODOT` your shell uses. If versions look
  wrong, check `mcp__godot__get_godot_version` first; reconnecting is a human
  action.
- Per `CLAUDE.md` §4: this is a third-party plugin whose author states its code is
  100% AI-generated, and it gets write access to scenes and scripts through the
  live editor. Don't run editor-mutating MCP tool calls with uncommitted work you
  would hate to lose.
