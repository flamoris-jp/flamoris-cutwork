# Shared desktop MCP connection

This app consumes the published `Flamoris.Mcp.Core` and `Flamoris.Mcp.Wpf` 1.4.0
through ordinary NuGet restore. No Core source checkout, custom feed or
`FlamorisMcpSourceRoot` override is required. See [startup and independent consent](mcp-startup.md).

MCP / AI contains only Connect..., Stop, Settings... in the current UI language.
Manual Connect confirms the selected method and previous permission. Settings
owns method, permission, auto-connect and secure credentials. New preferences enable
local read-only Manual auto-connect at application startup; saved OFF remains OFF.
Saving changes prompts to reconnect; declining leaves the current live permission
unchanged. New settings apply on the next connection. There is no cursor override.

One bottom-right red/green icon means disconnected/connected. Chipsy appears at
window center for 500 ms after actual connection or successful AI state changes,
48 DIP (roughly twice Cutwork's tool glyph), without intercepting input. Reads,
health/status, failed calls and validation errors do not trigger it.

Manual and OpenAI tunnel-client keep the same-user local bridge. Hub uses outbound
WSS with a per-upstream credential in Windows Credential Manager. The host's current
session, revision guards, permission checks and Undo/Redo remain authoritative.
The application-lifetime grant survives document open/close/replacement, including
reopening the same file. Each target gets a fresh document token; stale guards and
pending results cannot follow it. `mcp.context` and discovery work without a file;
document tools return `no_document`. Stop suppresses reconnect until an explicit
connection; auto-connect waits for the application runtime, not an open document.

## Windows acceptance

- Confirm the identical three-item menu and settings in Japanese and English.
- With a fresh settings profile, startup enables the local read-only Manual endpoint
  before opening a file. Green requires an authenticated/registered client; a waiting
  local endpoint alone is not green. Verify context/discovery and `no_document`.
- Cancel connection: no new grant. Confirm read-only: no edits permitted.
- Save Edit permission while connected, decline reconnect: current grant stays
  read-only. Accept reconnect: old grant rejected, new grant can edit.
- Auto-connect ON restores the saved permission after restart; OFF does not.
- Manual and tunnel connect through the packaged bridge; Hub registers to the
  configured WSS endpoint. No desktop LAN listener is created.
- Only connection and successful edits show centered Chipsy; verify actual scale
  and timing by eye. Queries, errors and cancellation must not show it.
- Verify only the bottom-right indicator remains, and the mouse cursor is unchanged.
- Stop, permission changes and exit revoke the previous grant. Document replacement
  keeps the transport, changes the target token and rejects old guards/results.
  Hub loss leaves manual editing and history intact and never replays a mutation.

The generic provider regression tests now live in MCP Core. Packaged app smoke tests
still exercise the application-owned tools/history through the shared connection UI.
