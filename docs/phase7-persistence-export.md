# Phase 7 persistence and export

Phase 7 connects the authored `CutworkDocument` to durable project files and deterministic output. The schema authority is [`.flimg` schema v1](flimg-schema-v1.md).

## Runtime boundaries

- `FlimgArchiveCodec` maps the existing domain to and from a validated ZIP stream. It does not own a second document model.
- `FlimgProjectStore` performs sibling-temp writes, validates the closed archive, and atomically replaces the destination.
- `ProjectWorkspace` coordinates project path and `EditorSession.MarkSaved()`. Neither path nor history is authored document state.
- `DocumentExportService` exports the compositor result or the documented neutral handoff package without consulting the viewport.
- `MainWindow` only coordinates conventional dialogs, unsaved-change decisions, tool reset, and canvas presentation.

Open always validates and constructs a fresh document before replacing the live session. A successful project Open clears Undo/Redo, resets preview and viewport session state, deactivates the active tool, clears the Clone source, and Fits the document. A failed Open leaves the current document and project path unchanged.

Save and Save As update the project path and saved revision only after the atomic replacement succeeds. Undo/Redo history is intentionally never serialized.

## File commands

File provides:

- Open Image for PNG/JPEG import into a new unsaved project;
- Open Project for `.flimg` v1;
- Save, using the current project path or Save As when none exists; and
- Save As, selecting a new `.flimg` path.

Export provides:

- Composite PNG: full document-size source-over output from `CompositeCache`; and
- Layer Handoff: atomic ZIP containing deterministic JSON and lossless layer assets.

Before a dirty document is replaced or the window closes, the Japanese-first prompt offers Save, Don't Save, or Cancel. Zoom, pan, hover, overlay, preview source, and pending tools do not make the document dirty.

## Windows hands-on checklist

An interactive Windows desktop is required for this check; CI does not claim it.

1. Open a PNG and create Part, Patch, and Repair edits.
2. Save As `.flimg`, edit again, then Save. Confirm the unsaved marker clears only after success.
3. Close and reopen the project. Check layer order, IDs through developer inspection, names, visibility, masks, Patch transform, and Repair pixels.
4. Confirm Undo and Redo are empty after Open and the canvas is Fit.
5. With unsaved authored edits, exercise Save / Don't Save / Cancel for both Open and Close.
6. Move/zoom heavily, export Composite PNG, and confirm exported dimensions and pixels are viewport-independent.
7. Export Layer Handoff and inspect `handoff.json`, Part crops, frozen Patch-local pixels, and Repair crops.
8. Try a damaged or unsupported `.flimg`; confirm a localized error and no replacement of the current live document.
