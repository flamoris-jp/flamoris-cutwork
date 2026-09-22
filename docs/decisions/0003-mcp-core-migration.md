# ADR 0003: Migrate live MCP to Flamoris.Mcp.Core 1.0.1

Status: accepted for Issue #36 implementation. Baseline `7791306a`.
Managed connection lifecycle additions are recorded separately in ADR 0004.

## Decision

Cutwork consumes `Flamoris.Mcp.Core` 1.0.1 and keeps the reviewed local topology:

```text
external MCP client
  -> Flamoris.Mcp.Bridge stdio process
  -> authenticated same-user/local-only named pipe
  -> running Cutwork WPF process
  -> existing EditorSession / CutworkDocument / EditTransaction / history
```

`CutworkMcpHost` is a thin `IMcpHost` adapter over the `MainWindow` dispatcher and
the existing `EditorSession`. It does not construct, load or retain another editable
document. Its snapshot projects Cutwork's transient `DocumentToken` and monotonic
`CutworkDocument.Revision`. `EditorSession.CurrentRevision` remains a separate
authored-history state marker and may move backward during Undo.

Cutwork registers closed `HostTool<T>` adapters for the reviewed context, layer,
image, Guriguri preview, edit, Undo and Redo operations. Core owns transport,
capability authentication, permission filtering, request admission, timeout and
cancellation, common stale guards, protocol framing, diagnostics and connection /
foreground activity projection. Cutwork continues to own schemas and DTO validation,
busy interaction detection, raster/ROI budgets, commands, transactions, visual
projection and `.flimg` behavior.

## Commit and cancellation boundary

Every persistent request enters `RequestContext.CommitAsync` exactly once. The
callback runs synchronously on the WPF dispatcher and performs one ordinary Cutwork
transaction/history operation. Cutwork checks the request cancellation token at
bounded operation, stroke-batch and pre-commit boundaries. Failure disposes the
ordinary `EditTransaction`, which rolls authored state back through the existing
path; rollback may still advance `CutworkDocument.Revision`.

Core rechecks product/runtime/document/revision, permission, revocation and human
busy state immediately before the callback. `DocumentReplacing` publishes
`IMcpHost.Invalidating` before a replacement is installed. Disable, permission
change, document replacement and shutdown revoke the current capability and stop
the endpoint. A fresh enable creates a fresh pipe address and 256-bit credential.

Guriguri boundary fitting is the bounded preparation exception to dispatcher-only
domain work. Cutwork first captures the immutable Original, dimensions, token and
revision through `RequestContext.ReadAsync`, then performs the pure fitting and PNG
preparation on a worker. Preview disclosure re-enters `ReadAsync` and explicitly
rechecks the captured revision; authored Part installation re-enters
`CommitAsync`, whose guard rechecks grant/runtime/document/revision/cancellation.
Only the ordinary transaction/history commit occurs inside that callback. Stop,
replacement or a concurrent authored edit therefore cannot disclose or install an
old prepared result.

Core 1.0.1 treats `ReadTimeoutMs` as a started-frame deadline, not an idle-session
timeout. Waiting for the first byte of the next request is unbounded, while a
partial/stalled frame remains bounded and grant revocation or shutdown still
cancels an idle read immediately.

## Wire and image compatibility

Core 1.0.1 owns official SDK 2.2.0 protocol handling and the common guarded tool
envelope. Existing Cutwork operation payloads remain the typed `input`; runtime,
document and expected revision move to Core's `guard` object. `mcp.context` exposes
the common identity while Cutwork's `context` tool retains dimensions, coordinate,
dirty, selection and shared-history metadata.

`HostTool<T>` returns structured JSON. Therefore bounded PNG results are represented
as `mimeType: image/png` plus `pngBase64`, with the same source/crop/output mapping
and 1 MiB encoded-PNG limit. No path or hidden disk write is introduced.

## Bridge and packaging

The existing small bridge project becomes only a launcher for Core's
`StdioBridge`. Its product output is named `Flamoris.Mcp.Bridge.exe`; it reads the
credential from `FLAMORIS_MCP_CAPABILITY`, removes that variable immediately, and
never receives the credential in argv. The Windows package continues to publish
the bridge self-contained beneath `mcp/` and contains no second editor authority.

## Removed duplication

After parity tests cover the Core path, Cutwork removes its local capability lease,
named-pipe ACL helper, bounded protocol stream/reader, official SDK server adapter
and transport diagnostics. `LiveEditor`, `LiveSchema`, `LiveLimits` and
`LiveImages` remain Cutwork-owned because they encode application semantics.

## Non-goals

No tool redesign, image-processing feature, filesystem authority, save/open tool,
MCP Hub, HTTP/LAN/cloud listener, `.flimg` schema change or unrelated UI cleanup is
included.
