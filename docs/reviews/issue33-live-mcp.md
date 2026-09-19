# Issue #33: implementation self-review

Reviewed against `aec08a2f49acee8331ae250210188d7887eb2f55` and Issue #33.
This is author self-review, not independent approval or permission to merge.

## Authority and lifecycle

- The WPF-owned session is injected into the adapter. The bridge references no
  Core/Imaging assembly and only forwards bytes. No alternate document/history
  or mutable editing snapshot is introduced.
- Every persistent MCP call uses the transient document token plus the existing
  `CutworkDocument.Revision`. `CurrentRevision` and `SavedRevision` retain their
  original meaning. UI edit → Undo back to the same history marker is tested.
- The final shared `EditorSession.Open` boundary revokes after validation/Claim
  and before installation, including same-instance/same-file reopen. Failed
  decoding cannot reach that boundary. Existing history cannot remove or replace
  this session's document; same-document Undo/Redo does not revoke the grant.
- Authority stays on the WPF dispatcher. Busy includes gesture/deferred work,
  unfinished fitting/Patch and inline metadata, modal and file coordination.
  Remote work owns an ordinary transaction, disables competing controls and
  leaves Stop/mode changes available. Yield and precommit guards roll back
  cancelled work; delayed image results revalidate the original grant.

## Operations, data and resource review

- Mask dabs and compositor pixels were extracted from the existing implementation
  and are called by both paths. Existing fitter, Clone mapping/kernel/sampler,
  Patch sampler, command restrictions and history are reused.
- Complete typed schemas drive discovery and validation. No raw command/raster,
  selection, filesystem, Open/Save/export, shell or future-operation fallback is
  exposed. Read only is enforced for direct calls as well as discovery.
- Original pixels, semantic Part order and owned Repair IDs are unchanged. The
  v2 writer and v1 reader/migration are untouched. Save/reopen remains a UI action.
- Review fixes reserve cumulative retained history before expensive preparation,
  charge the full dirty-region union for rollback/Undo redraw (including distant
  edits and delete cascades), and bound ordinary history travel. The existing
  session budget is still the final authority; it is not enlarged.
- Input/output framing, depth, pending calls, dabs, fences, expanded surfaces,
  work, estimated allocations, PNG and deadlines have finite limits. Focused
  tests cover boundary values, cumulative rejection, blocked read/write
  cancellation, EOF, malformed UTF-8/JSON and excess pipelining.
- Native pipe creation applies a protected owner-only DACL and
  `PIPE_REJECT_REMOTE_CLIENTS` atomically. The native ACL is inspected in a Windows
  test. The pipe address is not treated as a credential. See the manual matrix
  below; a same-user test does not prove every Windows token configuration.

## Package and protocol review

Official C# SDK 1.0.0; declared and negotiated MCP 2025-03-26. No 2026 protocol claim.
The package contains self-contained editor and bridge plus production SDK/license
files. The external official test client is outside the package. Existing package
inventory/ZIP checks and three-day artifact retention remain in place.

The smoke launches the freshly published processes outside the repository with
System32-only child PATH. It opens synthetic artwork through WPF, tests actual
image/edit traffic, checks WPF layer projection and desktop canvas pixels, and
uses WPF Undo/Redo. It then exercises a real checkbox click, Save/v2 reopen,
permission/document lifecycle and bridge EOF. UI Automation Toggle alone was
replaced because WPF's Toggle provider does not raise the ordinary Click event.

## Evidence and outstanding acceptance

Final executed CI evidence is linked in PR #34. Linux workspace checks are source
review, repository/tree comparison and `git diff --check`; no local Windows or
local dotnet test execution is claimed. Earlier failed CI attempts exposed test
harness issues; they are not counted as complete packaged acceptance.

Not performed by this change's author:

- Human acceptance on real artwork: seams, mask/Clone/Patch quality and latency.
- Actual keyboard Ctrl+Z/Ctrl+Y with canvas focus (the automated smoke uses WPF
  toolbar commands and verifies rendered pixel restoration).
- Japanese/English copy-flow and 100/125/150/200% DPI inspection.
- Different Windows users, elevation combinations, and second-machine SMB matrix.
- Setup in Akino's intended local AI client (official SDK interoperability is a
  separate proof). Cloud Chat/Work connectivity is not provided.

These remain explicit human/reviewer checks. Do not auto-merge.
