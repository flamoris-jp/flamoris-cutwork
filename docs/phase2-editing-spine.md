# Phase 2 editing spine

Implements Issue #12 within the accepted production architecture. No tool or file
format is added.

- Stack order is top-to-bottom; Base is the unique divider. Part masks use
  document-space, half-open bounds. The 8-bit mask union is the pointwise maximum;
  visibility never changes that geometry. Source-over uses integer BGRA8
  premultiplication with `(value * alpha + 127) / 255` rounding.
- Part pixels sample Original. Patch and Repair own straight-alpha local BGRA8
  data with a fixed document-space origin in Phase 2. Affine Patch authoring and
  resampling belong to Phase 4, not to this foundation.
- Commands are the only mutation path. A transaction applies ordered edits,
  retains inverse metadata or before/after ROI bytes, and rolls back all applied
  edits if any member fails. One committed transaction is one Undo entry.
- Document and touched-layer revisions increase on commit, Undo and Redo. Session
  current/saved revisions identify *authored history states*, so Undo to a saved
  state becomes clean without reusing a cache revision. Preview and viewport
  changes affect neither. MarkSaved is coordination only; there is no Save UI yet.
- History defaults to a configurable 128 MiB retained-payload budget, evicting
  oldest complete transactions. An individual transaction exceeding the budget
  is rejected and rolled back. Accounting includes retained structural layer
  buffers, metadata, and ROI before/after bytes; it is not a CLR heap measurement.
- Document change notifications contain a composite dirty rectangle and a
  separate Base-hole dirty rectangle. A single cached composite and hole mask
  consume these changes. Metadata-only edits do no imaging work. No per-layer
  raster cache is allocated before a layer requires expensive resampling.
- The WPF adapter copies only the updated composite rectangle into the existing
  WriteableBitmap. Original preview uses a separate immutable-source presentation;
  edits while it is visible accumulate until Composite is requested. Overlay and
  navigation never recomposite. Display scheduling coalesces pending changes.

The developer fixture command adds a rectangular Part and two overlapping,
colored underpaint layers using the same session transaction boundary as normal
edits. It is explicitly a diagnostic fixture, not a Part or Patch Tool.
