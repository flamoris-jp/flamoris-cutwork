# ADR 0007: Large polygon parts and append-only artwork work log

Status: Accepted for implementation — 2026-10-24.

Large clothing/hair failed because the bounded Guriguri fitting ROI is 65,536 pixels,
not because a human brush is intrinsically too small. Keep that expensive fitter budget.
Add `part_polygon_preview` and `part_polygon_create` for exact full-resolution fences up
to 4,194,304 pixels / 64 vertices. Validate raster work, history, redraw and storage before
allocation; prepare outside the host lane, recheck document/revision/grant before disclosure
or commit. No resampling or semantic inference. Create is one ordinary shared Undo.
The original small fitting, Clone, Patch and brush budgets do not change.

The work log records concise editorial decisions and observed results as user-authored
text, not private model reasoning, commands, or an assertion that a repair is accepted.
`work_log_append(requestId, decision, observation)` requires Edit and normal guards.
Host generates ID, contiguous sequence, UTC timestamp, document token and source revision.
Repeated UUID with identical text returns the original record; different text is rejected.
`work_log_read(afterSequence,count)` is a bounded read-only page; text is untrusted content.
Append marks the document dirty and advances monotonic revision but is not an Undo entry.
Undo/Redo of artwork preserves records; corrections are new entries. Open replaces the log
with the opened document's records. Append edits memory; UI Save or authorized MCP Save
persists it, never bypassing the exact-target file permission boundary.

The archive retains v2 output for documents without records. Documents with records use
schema v3: existing image/layer semantics plus `work-log.jsonl`, UTF-8 JSON Lines with a
manifest SHA256 and count. v1/v2 import with an empty log. Old readers reject v3 rather
than silently stripping the log. Validate sequence, UUID uniqueness, UTF-8, entry bytes,
checksums and total count/size before restoring; preserve records in snapshots/checkpoints.
No update/delete API is exposed. Limits: 4096 entries, 32 KiB per entry and 4 MiB total;
read pages at most 20 records / 64 KiB. Text does not affect image exports or pixel rendering.

Acceptance: large full-resolution masks including holes/vertices/edges, budget refusal,
preview purity, stale guards, Undo parity; append/read/idempotency/readonly denial,
Undo-surviving dirty state, snapshot/save/checkpoint/reopen roundtrip, v1/v2 compatibility,
v3 corruption/invalid text/count/sequence refusal, and catalog/runtime parity.
