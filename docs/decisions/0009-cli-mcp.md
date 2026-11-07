# ADR 0009: Headless CLI MCP host

The CLI owns one production EditorSession for its process lifetime. `mcp INPUT`
loads an image or flimg once at startup, then reuses Cutwork.Mcp's typed tools,
LiveEditor, limits, revision guards, history and checkpoint implementation. A WPF
Dispatcher on the STA main thread serializes access without creating a window.
This is a new host for the existing engine, not another editing implementation.

Explicit launch selects readOnly (default) or edit. The stdio route internally
uses Core's authenticated same-user LocalMcpEndpoint and StdioBridge; no custom
JSON-RPC implementation. The alternative --hub route uses Core's outbound WSS
HubConnectionProvider and a dedicated environment credential. It must use a
separate Hub upstream/namespace from the desktop host. No listener is published.

INPUT and optional --save-to are startup-only filesystem authority. MCP cannot
open arbitrary files, change targets or run shell commands. --save-to creates a
new flimg copy without replacing an existing file, opens that copy in the
production ProjectWorkspace and authorizes its existing checkpoint tools. An
Edit grant alone does not authorize saving. Export uses ordinary CLI composite
and handoff commands against a completed saved project. No autosave on exit.

Stopping revokes the grant before transport teardown and waits for any pending
checkpoint completion while pumping the dispatcher. Existing checkpoint commit
and partial-success semantics remain authoritative. Stdio EOF stops the host;
Hub mode runs until Ctrl+C. Connection/registration and edit success are distinct.
No transport may replay an ambiguous mutation.

`mcp-catalog` emits a credential-free dedicated Hub descriptor from the actual
tool registry. It omits desktop capture tools. Deploying the descriptor and
credential is an operator step, independent of implementing this host.

Windows/.NET 10 is still required. Issue #68 owns portable codecs/Linux support.
Acceptance: actual CLI process + official MCP client, read-only discovery and
direct denial, preview/Part/history, stale guard rejection, checkpoint save and
reload, source preservation, EOF teardown and non-overwriting startup targets.
Hub deployment and perceptual artwork acceptance are separate from CI.
