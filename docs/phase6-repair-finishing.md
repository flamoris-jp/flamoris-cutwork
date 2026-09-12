# Phase 6: Blur, Smudge, and repair finishing

Phase 6 adds Blur and Smudge to the existing production brush path. Both tools use `CanvasInputRouter`, document-space `StrokeSampler` samples, `EditTransaction`/`RasterPatch` history, local compositor invalidation, and the shared overlay cursor. They do not introduce another input, raster, history, or rendering model.

## Eligible authored target

Only the selected `RepairLayer` is eligible in Phase 6. Original is immutable, Base has no authored raster, and Part owns mask geometry rather than repair pixels. `PatchLayer` is also intentionally ineligible: its frozen pixels live in patch-local coordinates and are projected by an authored affine transform. Editing them safely would require either baking the transform or introducing a source-local edit/history path. Both choices exceed this phase and could weaken Patch identity and deterministic transform semantics.

An ineligible selection rejects pointer-down before a transaction begins. It leaves layer bytes, document revision, dirty state, and history unchanged.

## Brush and kernel semantics

Radius is measured in document pixels and is limited to 0.5–512. Strength is limited to 1–100 percent. Wheel forward/up reduces radius by one document pixel, wheel backward/down increases it, and `Ctrl+wheel` remains viewport zoom through `CanvasInputRouter` precedence.

Blur applies a deterministic 3×3 box sample inside a hard circular footprint. The write region is the circle's conservative rectangle intersected with the selected Repair bounds. The kernel reads one additional pixel of halo and blends each straight BGRA channel toward the box result by the selected strength. Pixels outside the write ROI remain byte-exact.

Smudge transports pixels in the sampled stroke direction. For each emitted point, the source coordinate is the destination pixel minus that sample's movement vector. Pixel-center bilinear sampling preserves subpixel movement from the shared sampler. The kernel snapshots only the union of the destination footprint and shifted source bounds plus its one-pixel sampling halo before it writes, then blends toward that source by strength. Samples are applied sequentially in `StrokeSampler` order, making a fixed document-space path independent of sparse versus dense pointer delivery. This is a small directional repair operation, not wet-paint simulation.

## History, rendering, and overlays

Pointer-down through pointer-up is one live transaction and therefore one Undo entry. Each sample records exact before/after bytes for its local ROI. Escape and pointer-capture loss cancel the transaction in reverse order, restoring exact bytes without adding history. Redo applies the exact recorded after bytes.

Every raster patch publishes its local document rectangle to `CompositeCache`; the existing presentation adapter updates that region of the existing `WriteableBitmap`. No tool event allocates a document-sized image. Repair bounds do not grow during these finishing strokes, because the tools modify only already-authored Repair pixels.

The circular cursor uses the same document-space radius as the kernel and projects it with `ViewportTransform`. Hover, radius changes, Fit, Actual Size, zoom, and pan update the overlay only; they do not revise the document or composite pixels.

## Windows hands-on checklist

Windows CI covers deterministic contracts but does not verify subjective brush feel. Before merge on a Windows desktop:

- open representative PNG and JPEG artwork containing a Repair layer;
- activate Blur and confirm Base, Part, and Patch selections show the ineligible guidance;
- blur a hard Repair seam at Fit, 100%, and high zoom and compare the cursor with the affected circle;
- vary radius and strength and confirm wheel forward makes the brush smaller, wheel backward makes it larger, and `Ctrl+wheel` only zooms;
- smudge a visible edge in both directions and confirm the result follows the drag direction;
- make fast sparse strokes and dense strokes over the same path and check for comparable continuity;
- Undo and Redo each whole stroke once;
- cancel an active stroke with Escape and with capture loss, confirming exact restoration;
- inspect Original and unrelated layers, then exercise Mask, Patch, Clone, Part hole, zoom, and pan regressions.

Persistence, export, packaging, GPU filters, AI repair, a generic filter/painting engine, and transformed Patch pixel editing remain deferred.
