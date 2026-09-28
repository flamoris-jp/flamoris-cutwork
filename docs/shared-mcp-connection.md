# Shared desktop MCP connection

This app consumes `Flamoris.Mcp.Core` and `Flamoris.Mcp.Wpf` 1.2.0.
The common implementation lives in flamoris-jp/flamoris-mcp-core#13; the server
counterpart is flamoris-jp/flamoris-mcp-hub#16. Release Core/Wpf first, then merge
this consumer. The PR's CI explicitly checks out the reviewed Core candidate and
sets `FlamorisMcpSourceRoot`; ordinary released builds use NuGet.

MCP / AI contains only Connect..., Stop, Settings... in the current UI language.
Connection always confirms the selected method and previous permission. Settings
owns method, permission, auto-connect (OFF by default), and secure credentials.
Saving changes prompts to reconnect; declining leaves the current live permission
unchanged. New settings apply on the next connection. There is no cursor override.

One bottom-right red/green icon means disconnected/connected. Chipsy appears at
window center for 500 ms after actual connection or successful AI state changes,
48 DIP (roughly twice Cutwork's tool glyph), without intercepting input. Reads,
health/status, failed calls and validation errors do not trigger it.

Manual and OpenAI tunnel-client keep the same-user local bridge. Hub uses outbound
WSS with a per-upstream credential in Windows Credential Manager. The host's current
session, revision guards, permission checks and Undo/Redo remain authoritative.
Document replacement invalidates old grants before reconnecting. Stop suppresses
reconnect until a new explicit connection; auto-connect runs when a document is ready.

## Windows acceptance

- Confirm the identical three-item menu and settings in Japanese and English.
- With a fresh settings profile, startup must remain disconnected (red).
- Cancel connection: no new grant. Confirm read-only: no edits permitted.
- Save Edit permission while connected, decline reconnect: current grant stays
  read-only. Accept reconnect: old grant rejected, new grant can edit.
- Auto-connect ON restores the saved permission after restart; OFF does not.
- Manual and tunnel connect through the packaged bridge; Hub registers to the
  configured WSS endpoint. No desktop LAN listener is created.
- Only connection and successful edits show centered Chipsy; verify actual scale
  and timing by eye. Queries, errors and cancellation must not show it.
- Verify only the bottom-right indicator remains, and the mouse cursor is unchanged.
- Stop, document replacement and exit revoke the previous client. Hub loss leaves
  manual editing and history intact and never replays a mutation.

The generic provider regression tests now live in MCP Core. Packaged app smoke tests
still exercise the application-owned tools/history through the shared connection UI.
