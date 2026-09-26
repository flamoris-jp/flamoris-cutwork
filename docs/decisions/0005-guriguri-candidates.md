# ADR 0005: bounded Guriguri candidate previews

Issue #38 extends the AI-facing fitting query; human Mask/Part semantics and
the single EditorSession/document/history authority remain unchanged.

- `part_candidates` accepts one `fence`, two or three strictly increasing
  integer `steps` in 0..100, and `maxEdge` in 1..1024. Start with `[0,4,8]`;
  steps are the existing 0.88-per-step shrink schedule, not percentages.
- One existing Guriguri fitting session prepares all masks off the WPF lane.
  Each candidate includes a random ID, actual step, bounds, retained/fence pixel
  counts, retained ratio, SHA-256 of the full-resolution mask, and an Original
  cutout PNG with the existing exact image-coordinate mapping.
- One latest successful set, at most three masks / 196608 retained mask bytes,
  is held by the existing LiveEditor. This is immutable prepared output, never
  a second editable document or cross-call transaction. IDs expire after five
  minutes (monotonic clock), on another successful candidate query, any document
  revision/token change, or revocation/rotation of the issuing capability.
  Expired sets are discarded on access; idle retained memory remains bounded.
- Generation validates the existing fence/ROI/work limits before fitting and
  charges aggregate mask/preview allocations and work. Each PNG remains at most
  1 MiB; aggregate PNG bytes are at most 2 MiB, below the 4 MiB encoded frame
  boundary. No filesystem, image upload, semantic ranking or model is added.
- Preparation re-enters the Core guarded read lane before installing the set or
  disclosing pixels. It rechecks issuing capability identity, document token,
  revision, cancellation and human busy state.
- `edit` accepts either the existing `part.create(fence,step,name)` or
  `part.create(candidateId,name)` operation. Mixed forms are rejected. Candidate
  masks are resolved on the guarded commit lane before any batch mutation;
  the stored bytes are installed through ordinary AddLayer commands. Several
  candidates may be used in one atomic batch, with ordinary `@N` references.
- Read only permits candidate queries, never creation. Unknown, superseded,
  expired or incompatible candidates return `stale_candidate`; existing request
  guards can reject stale revision/document/authorization earlier. Query anew
  after conflicts. Do not retry a mutation automatically.
- Existing `part_preview` and fence/step creation remain compatible.

Verification: candidate count/step/ROI limits, preview purity, deterministic
masks, exact commit without refitting, atomic rollback/shared history, changed
revision including Undo, same-document reopen, expiry, capability rotation,
read-only denial, and barrier-controlled revoke/replacement during preparation.
The existing official-client Windows package smoke exercises candidate traffic.
