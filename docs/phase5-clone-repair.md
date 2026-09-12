# Phase 5: Point-source Clone Repair

Phase 5 adds one production Clone Repair tool to the Phase 2–4 editing spine. It does not add a second input, brush, history, raster, or compositor path.

## Interaction contract

- Alt+left click stores one source anchor in document space. It remains stored across strokes and viewport changes until replaced.
- Ordinary left drag starts a stroke. Its destination anchor is the pointer-down position and its fixed offset is `source - destination`.
- A later stroke reuses the source anchor and calculates a new offset from its own destination anchor.
- If the selected layer is a `RepairLayer`, the stroke edits it. Otherwise the first stroke explicitly creates and selects one compact Repair layer in the same transaction.
- Wheel forward/up reduces radius by 1 document pixel; wheel backward/down increases it by 1. Radius is clamped to 0.5–512 document pixels.
- `Ctrl+wheel` remains viewport zoom because `CanvasInputRouter` gives that global mapping precedence.

The source is always immutable `OriginalAsset`. Pixels authored earlier in the same stroke, other Repair layers, Patch layers, and the current Composite are never clone sources.

## Kernel, dirty regions, and history

`CloneRepairController` owns only gesture state and sends document-space samples from the shared `StrokeSampler` to `ICloneRepairKernel`. `CloneRepairKernel` computes a hard circular footprint, clips source and destination coordinates, and returns straight-alpha BGRA only for the conservative destination ROI.

`RepairLayer` keeps compact raster bounds. Existing `RasterPatch` history grows those bounds only to the touched ROI and records exact transparent-before/authored-after bytes. Each live sample publishes a local dirty document rectangle through the existing cache and `WriteableBitmap` path. Pointer-up seals all local patches as one history transaction. Escape or pointer-capture loss reverses them and restores the exact prior pixels and bounds without a history entry.

The brush cursor and stored source marker are projected from document coordinates onto the independent overlay. Pointer movement, source selection, and radius changes do not revise the document or rebuild the presented bitmap.

## Windows hands-on checklist

Windows CI covers deterministic boundaries but not subjective pointer feel. Before merge on a Windows desktop:

- open representative PNG and JPEG artwork with a Part hole;
- activate Clone, Alt+click a visible source, and confirm the source marker;
- paint into the hole and confirm the circular cursor matches the hard footprint at Fit, 100%, and high zoom;
- make sparse/fast strokes and check that interpolation has no visible gaps;
- confirm the source-relative mapping remains fixed through a stroke and is recomputed on a second stroke;
- verify wheel forward makes the brush smaller, wheel backward makes it larger, and Ctrl+wheel only zooms;
- cancel with Escape and forced capture loss, then verify no pixels or layer remain;
- Undo/Redo a complete stroke in one step and confirm layer identity, pixels, and compact bounds;
- repeat zoom/pan between source selection and painting and check for coordinate drift;
- inspect Composite and verify Repair remains under Base and is visible through authored Part holes.

Blur, Smudge, Hole Only UI, persistence, export, packaging, GPU rendering, AI, and FLAMORIS 2D integration remain deferred.
