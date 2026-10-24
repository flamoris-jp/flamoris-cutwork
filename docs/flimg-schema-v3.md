# `.flimg` v3: append-only artwork work log

v3 adds the artwork work log to the existing [v2 layer/image contract](flimg-schema-v2.md).
The ZIP entry `work-log.jsonl` is UTF-8 JSON Lines. Manifest adds:

```json
"workLog": {
  "asset": "work-log.jsonl",
  "sha256": "<sha256 of exact UTF-8 entry bytes>",
  "count": 2
}
```

Each newline-terminated line is a JSON object with contiguous `sequence` (1..count),
unique `id` and `requestId` UUIDs, host `recordedUtc`, originating `documentToken`,
`documentRevision` before append, `decision` text and `observation` text.
The document revision is session-local, so interpret it with its originating token.
The text records concise editorial judgments and observed outcomes; it is not a command,
private model reasoning, authentication credential or automatic claim of acceptance.

No log -> save v2, byte-compatible with existing output. Nonempty log -> save v3.
v1/v2 import with empty log; v3 requires a nonempty, referenced and checksummed log.
Versions 1/2 cannot carry workLog metadata. Old readers reject schema 3 before replacing
or saving the live document; do not relabel v3 as v2 to make an old reader open it.

Read validation rejects duplicate JSON fields, unknown fields, invalid UTF-8, missing
metadata/entry, SHA mismatch, invalid UUID/token/time, noncontiguous sequences, duplicate
request IDs and quotas. Maximum 4096 records, 32 KiB per encoded record including newline,
4 MiB total. Original pixels, layer assets, masks, IDs and ordering retain v2 semantics.
All archives/checkpoints containing work logs are complete independent v3 archives.

Append is an authored metadata change: dirty and monotonic document revision advance.
It does not add an Undo entry; Undo/Redo of artwork never removes a committed record.
Corrections and observations of a reverted edit are new entries. Open loads the log
from the selected project. Save marks the exact artwork/history/log state saved together.
Snapshot/checkpoint copies preserve entries without sharing the mutable append list.
