# ADR 0004: Cutwork-owned managed MCP connections

Status: accepted for Issue #40 implementation.

## Decision

Cutwork consumes `Flamoris.Mcp.Core` 1.1.0 and copies/adapts the Core
`samples/wpf-managed-connection` reference into application-owned production code.
The existing authority remains unchanged:

```text
external MCP client
  -> optional Cutwork-owned tunnel-client helper
  -> Flamoris.Mcp.Bridge
  -> same-user local named pipe
  -> McpBoundary / CutworkMcpHost
  -> existing EditorSession / CutworkDocument / Undo / Redo
```

Cutwork owns provider selection, stable preferences, localization, credential
storage, process ownership, profile generation and status UX. Core owns only the
provider-neutral serialized lifecycle and its bounded state/error contract.
Manual connection remains the default and does not start a helper.

## Independent state axes

The application projects three independent facts:

1. a current MCP grant exists;
2. the managed helper is stopped, starting, running, refreshing, stopping or faulted;
3. an authenticated external client is connected to `McpBoundary`.

Provider transitions do not overwrite the authenticated-client measurement.
A manual client can therefore remain connected while helper state is stopped.
The existing red/green boundary status and Chipsy activity continue to come from
the real `McpBoundary.Status`, not from process existence.

## Lifecycle ordering

Enable issues the real boundary grant first, marks Core lifecycle enabled, then
optionally starts the helper. Permission changes replace the old lifecycle by
revoking the old grant, stopping its owned helper, creating a fresh pipe/grant and
starting against the latest material. Document replacement and shutdown revoke
before helper cleanup.

After a grant issue or rotation callback succeeds, lifecycle/provider reconciliation
uses a non-cancellable completion boundary. Caller cancellation cannot leave a live
grant untracked or a helper running with superseded pipe/capability material.

Stopping only the helper preserves the grant for Manual fallback. Provider start,
refresh, crash and stop failures publish bounded fault state and do not disable
ordinary Cutwork editing.

## tunnel-client 0.0.14 boundary

The supported provider invokes the configured executable directly with
`UseShellExecute=false` and fixed argument-list entries:

```text
run --profile <validated profile name>
```

The generated YAML uses the verified v0.0.14 schema and contains the bridge
executable plus current pipe in `mcp.commands[].command`. It refers to the API key
as `env:CONTROL_PLANE_API_KEY`. The actual control-plane key and transient
`FLAMORIS_MCP_CAPABILITY` are injected into the exact owned child environment and
removed from the local `ProcessStartInfo` after launch.

The capability and API key never enter YAML, argv, settings, logs or exception
details. The API key is stored in the current user's Windows Credential Manager.
Only stable non-secret preferences are serialized beneath the Cutwork local
application-data directory.

Executable, bridge and profile paths are validated; identifiers are bounded; the
health listener must be explicit loopback. Cutwork tracks and stops only the
`IOwnedProcess` returned by its launcher. It never scans or kills unrelated
`tunnel-client` processes.

## Consequences

- `.flimg`, MCP tools, raster processing and document/history semantics are unchanged.
- The app contains provider-specific code by design; MCP Core remains provider-neutral.
- A future provider can implement the same Core interface with its own settings and
  security review, but no speculative provider is included here.
- Manual connection JSON remains visible and copyable for recovery and diagnostics.
