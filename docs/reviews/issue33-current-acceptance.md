# Issue #33 current implementation and acceptance audit

Date: 2026-09-26. Original audit baseline: reviewed main `21636ed2417790c7aa79ca8a37fb71f7e0d9049a`.
PR #43 and PR #44 were subsequently reviewed and merged. This audit does not
replace the remaining hands-on acceptance for Issue #33.

## Finding

Live MCP is implemented. Issue #33 remaining open does not indicate that the
editor lacks MCP. The original implementation was merged in PR #34; PR #37
moved transport to Core and PR #41 adopted managed connection lifecycle.
Current code uses **Flamoris.Mcp.Core 1.1.0 / official SDK 2.2.0 / MCP 2026-07-28**.
The SDK 1.0.0 / 2025-03-26 statements in the original #33 review describe that
historical implementation, not today's protocol contract.

No missing required editing operation was found in the inspected #33 surface.
Full acceptance remains open because the manual checks below have no complete
recorded result. Do not close #33 solely because its implementation PR merged.

## Criteria mapped to current evidence

| Requirement | Source / executable regression evidence |
|---|---|
| One running document/session/history | `MainWindow.Mcp.cs` injects the existing session; `CutworkMcpHost` dispatches to WPF; bridge only forwards through Core |
| Monotonic revision plus transient document token | `EditorSession`, `LiveMcpTests`: UI edit → Undo does not revive an old guard; same-instance reopen changes the token and revokes |
| Human gesture/preview/inline/file exclusion | `HumanBusy`, Core admission and `LiveMcpTests`; external requests do not commit/cancel human pending work |
| Bounded original/composite/Part/mask/ROI queries | `LiveImages`, `LiveImageTests`: captured identity, PNG/edge bounds and nearest-pixel coordinate mapping |
| Pure deterministic fitting and ordinary Part creation | `LiveEditor`, `LiveImageTests`, `LivePreparationTests`: no preview history; source/revision recheck before disclosure/commit |
| Layer metadata, mask, Fixed/Offset Clone, Patch | Closed `LiveSchema`; `LiveEditor` delegates to existing commands/kernels; `LiveMcpTests` tests ownership, IDs, exact pixels and explicit targets |
| Atomic batches, rollback and shared UI/MCP history | `EditTransaction`, `LiveMcpTests`, published smoke; prior redo survives failed batches |
| Read-only enforcement and fail-closed unknown operations | `CutworkMcpTools`, Core boundary, `LiveMcpTests`; no arbitrary file/shell/raw-raster operations |
| Stop/downgrade/reopen/shutdown revocation | `CutworkMcpHost`, `MainWindow.Mcp.cs`, barrier preparation tests and managed-connection tests |
| Local Windows trust boundary | `LiveBoundaryTests` inspects protected owner DACL and remote-client-reject flag; separate real token/machine matrix is still manual |
| Bounded work, images, frames and idle semantics | `LiveLimits`, schema tests, `LiveTransportTests`; healthy idle survives timeout, partial frames do not |
| Published self-contained editor/bridge and official client | Existing `eng/package-windows.ps1` and `Cutwork.WindowsSmoke`; no parallel packaging path or new runtime dependency |
| Current-v2 save/reopen, ownership/order and artwork | Published WPF file dialog smoke plus persistence regression suite; no schema change |

The current scope deliberately leaves Blur/Smudge, selection/tool state and
filesystem/Open/Save/export-to-path UI-only, as #33 required. The follow-up
candidate UX (#38 / PR #44) and human Guriguri transition (#39 / PR #43) are
implemented. They extend the original single-step preview/create surface.

## Executed Windows evidence

- [Original implementation run 35431742100](https://github.com/flamoris-jp/flamoris-cutwork/actions/runs/35431742100):
  PR #34 records 204 passing tests and the original published official-client smoke.
- [Managed connection run 35730052058](https://github.com/flamoris-jp/flamoris-cutwork/actions/runs/35730052058):
  PR #41 records 223 passing tests, release build and published package smoke.
- [Current baseline plus #39 run 36205883584](https://github.com/flamoris-jp/flamoris-cutwork/actions/runs/36205883584):
  observed successful restore, build, test, package verification and published
  editor/bridge official-client smoke on 2026-09-26. This run includes PR #43's
  transition guard, before candidate work from PR #44 was merged.
- [Candidate run 36206652132](https://github.com/flamoris-jp/flamoris-cutwork/actions/runs/36206652132):
  234 passing tests, release build, package verification and published editor/bridge
  official-client smoke with candidate preview/create.

This Linux workspace has no .NET SDK or Windows desktop. Local source/diff checks
are not substituted for the linked Windows executions. Existing workflow
concurrency and three-day artifact retention are unchanged.

## Remaining acceptance on #33

- [ ] Representative real artwork: mask/Clone/Patch seams, quality and painting latency.
- [ ] Actual keyboard Ctrl+Z/Ctrl+Y with canvas focus after external edits;
  automated WPF toolbar history is already covered.
- [ ] Japanese/English connection-copy flow and 100/125/150/200% DPI inspection.
- [ ] Different Windows users/elevation combinations and second-machine SMB
  attempts, reported as a real matrix rather than inferred from same-user tests.

The user has already exercised live editing in the intended client. That report
is useful hands-on evidence, but it does not prove the complete matrix above.
Record results here/on #33 before declaring those criteria accepted. No new
framework, UI refactor, CI redesign, merge or release is part of this audit.
