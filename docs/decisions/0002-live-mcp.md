# ADR 0002: Cutwork live MCP

Supersession note: Issue #36 moves the shared transport, capability and protocol
infrastructure to `Flamoris.Mcp.Core` 1.0.1. Cutwork-specific authority and tool
semantics below remain current; see [ADR 0003](0003-mcp-core-migration.md).

Issue #33; reviewed baseline aec08a2. Implementation decision, subject to PR review.

## Inventory and authority

MainWindow owns one EditorSession, shared by workspace, canvas and controllers.
EditTransaction owns rollback/history; CutworkDocument.Revision increases on edits,
rollback and history travel. CurrentRevision identifies a saved/authored history
state and is NOT a concurrency generation. A transient document token changes at
EditorSession.Open, including same-instance/same-file opens. A pre-install event
revokes attachment after validation/Claim and before replacing the document.
Normal history does not replace the document. No document, history, or engine is
created by the bridge or adapter. All document access executes on WPF's dispatcher.

Reference audit: 2D #102/#103/ADR0010 and Kachinco #13/#14/ADR0003,
including #14's document-loss follow-up (0100406). Bind a lease to the actual
session AND document reference AND transient token, observe shared lifecycle,
and never revive it on Redo. Failed file decoding leaves the grant intact. All
authoritative or mutable document access executes on WPF's dispatcher; bounded
pure preparation may use an immutable Original captured through that lane.

## Protocol and topology

Local official MCP client → self-contained stdio bridge → explicitly chosen local
named pipe → official C# SDK protocol server → typed adapter → existing session.
The original implementation used ModelContextProtocol.Core 1.0.0 with explicit
2025-03-26 compatibility. Issue #36 replaces that local protocol layer with
Flamoris.Mcp.Core 1.0.1 / official SDK 2.2.0 and its reviewed 2026-07-28 plus
legacy initialization support. The official SDK
owns initialize, cancellation, protocol errors and discovery. No HTTP, Node,
Hub/Relay, filesystem tool, raw command dispatcher or editable snapshot exists.

Primary sources inspected 2026-09-18: current stdio specification
https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio ;
official SDK and pinned StreamServerTransport/McpServerOptions sources
https://github.com/modelcontextprotocol/csharp-sdk ; native pipe flags
https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createnamedpipea .
A bounded validating stream surrounds SDK framing; SDK logging is disabled.
The bridge is byte transport only and never automatically retries an edit.

## Admission and cancellation

Default Off; enable requires an existing document. Read only grants artwork pixels
and metadata; Edit adds the closed typed edit registry and shared Undo/Redo.
Disable, permission change, Open/replacement, shutdown and dispatcher loss revoke,
cancel and close the pipe. Fresh enable gets a fresh address; it cannot revive an
old lease. Same-user/elevation trust follows Kachinco's owner-only protected DACL;
PIPE_REJECT_REMOTE_CLIENTS is separately applied in native dwPipeMode. A random
pipe name is an address, not authentication. One connected client per enable.
No external paths, import/Open/Save/MarkSaved/export/shell are admitted.

Every mutation needs Core's runtime/document guard plus decimal-string expectedRevision. Check on
the dispatcher before opening a transaction; recheck lease/token/cancellation
before each chunk and immediately before Commit (not the original revision after
our own edits). One complete request owns one transaction; no cross-call cursor.
A failed batch cancels in reverse order, preserving selection and earlier history;
rollback legitimately advances Document.Revision. No-op changes add no history.

Busy includes all human strokes/deferred work, fences/fitting/Patch previews,
inline metadata editing and modal/file coordination. Reads except status reject
busy. Remote chunked work disables human editing surfaces, leaves Stop enabled,
through the Core commit gate. Bounded operation/stroke stages check cancellation;
Core rechecks the grant before installation or disclosure. Bounded Guriguri
preparation uses a captured immutable Original outside the WPF serialization
lane; final preview disclosure and Part installation re-enter Core's guarded
read/commit gates. All
pixel readback is tagged with its captured token/revision. Cancellation after
commit does not Undo; query before retrying an ambiguous response.

## Typed surface

- context: bounded status, coordinates, selection, dirty/history, token/revision.
- layers: paged metadata (64 rows), semantic partOrder separate from stack index.
- image: Original/Composite/Part/mask PNG and document ROI mapping.
- part_preview: one fence/adjustment step, pure deterministic existing fitter.
- edit: one to 64 operations, complete discriminated schemas; part.create,
  layer.rename/semantic/visible/reorder/delete, mask.stroke, clone.stroke,
  patch.create/transform. Earlier created IDs may be referenced as @0 .. @63.
- undo/redo: ordinary shared history, same preconditions.

UI controllers and MCP share MaskBrushKernel, StrokeSampler, CloneStrokeMapping,
CloneRepairKernel, GuriguriPartFitter, PatchSourceSampler, commands and compositor
pixel rules. Clone remains current underpaint semantics: hole visibility comes
from Base/Part composition, not a new brush mode. Original stays immutable.
Blur/Smudge, selection/viewport/tool manipulation, arbitrary raster uploads,
internal commands, file lifecycle and future operations are intentionally omitted.

## Conservative limits

Central limits: UTF-8 frame 4 MiB, depth 64, pending protocol requests 8, one active
product call, 64 operations, 4096 path points, 64 fence vertices, coordinate range
within current canvas, radius 0.5–64, 1024 interpolated dabs, expanded authored
surface 262144 pixels, fitting ROI 65536 pixels, 8 million kernel work units and
64 MiB cumulative estimated working allocations per request; transaction retained
payload also obeys the existing (default 128 MiB) history budget. Metadata strings
256 chars, previews at most 1024 longest edge / 1 MiB PNG, one image per call.
Image work ≤8 million output-pixel/layer visits. No full-canvas copy is required.
Fitting estimate reserves 256 bytes/ROI pixel for boundary/queue work; brush cost
counts bounded batch ROI × sample count and growth copies. Dirty-region unions
also reserve compositor work for cancellation and shared history, including gaps
between distant operations and owned-repair cascade deletion. Retained history
cost accumulates before fitter/raster preparation; the session remains the final
history-budget authority. Validation precedes
kernel allocation; budgets accumulate over the batch. Read deadline 120 s starts
only after a frame's first bytes arrive; ordinary idle time between frames is
unbounded. Request deadline is 15 s and write deadline is 5 s; partial/slow frames
terminate the connection while revocation/shutdown still interrupt idle reads. Malformed
UTF-8, duplicate/unknown properties, depth, enums and non-object roots fail closed.
Limits are deliberately smaller than desktop authoring. Ask for a smaller ROI or
shorter stroke; do not silently enlarge budgets. Estimates are resource guards,
not a claim of exact CLR heap size or measured latency.

## Packaging and proof

Existing Windows publish packages editor and mcp/Flamoris.Mcp.Bridge.exe self-contained,
with production SDK dependencies/notices only. Keep artifact retention 3 days.
Use the actual published processes and official SDK client in Windows acceptance,
not a mock editor or rename-only smoke. Unit/CI results, human visual/DPI/keyboard
acceptance, and different-user/elevation/SMB matrix must be reported separately.
Local attachment does not connect a cloud-hosted Chat/Work client to this PC.
No schema change: v2 writer, v1 migration, stable IDs/ownership/order remain.
