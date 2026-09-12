# Phase 4: Mask Brush and Patch workflow

Phase 4 extends the existing Phase 2 editing spine and Phase 3 input router. It does not add a second document, compositor, history, or input path.

## Mask Brush contract

- One `MaskBrushController` owns the active pointer gesture and receives document coordinates from `CanvasInputRouter`.
- `StrokeSampler` suppresses duplicate/noise input and inserts deterministic samples at half-radius spacing (minimum 0.5 document pixels).
- Radius is measured in document pixels. The canvas only projects that radius for the independent circular cursor overlay.
- Normal drag uses the configured primary Add or Erase polarity. Holding Alt temporarily inverts the polarity; releasing Alt restores the primary setting.
- Every sampled change is an existing local `MaskPatch` inside one `EditTransaction`. Pointer-up commits one Undo entry; Escape or lost capture cancels the transaction and restores the exact before bytes.
- Mask changes invalidate their local region for both composite pixels and the Base-hole union. Part visibility remains independent from hole geometry.

Pointer movement and polarity/cursor changes affect only the overlay. They do not revise the document or rebuild the presented bitmap.

## Patch contract

`PatchToolController` is one source-to-placement workflow:

1. Click a document-space polygon on Original.
2. Press Enter or double-click after at least three points.
3. `PatchSourceSampler` rasterizes the polygon and freezes straight-alpha, patch-local BGRA from immutable Original.
4. Drag to translate; edit center, scale, and rotation in Properties.
5. Commit exactly one `PatchLayer` and one Undo entry, or cancel without authored changes.

Committed pixels are authoritative. Source coordinates are provenance only and are never sampled again during display. Uniform scale is limited to 1–1000%, and the transformed patch must remain inside the document.

`PatchLayer` remains in the existing underpaint band below Base. The WPF-independent compositor inverse-maps document pixel centers into its frozen local RGBA. A transform command invalidates the conservative union of old and new bounds; the existing composite cache and `WriteableBitmapSurface` transfer only that dirty rectangle.

## Windows hands-on checklist

Automated CI builds and tests deterministic boundaries, but it cannot validate subjective pointer feel. Before merge on a Windows desktop:

- open real PNG and JPEG artwork and create/select a Part;
- draw fast Add and Erase strokes, then hold/release Alt during one stroke;
- compare the circular cursor to affected pixels at Fit, 100%, and high zoom;
- cancel by Escape and by lost pointer capture, then verify exact restoration;
- Undo/Redo an entire stroke as one action and inspect the Base hole with the Part hidden;
- fence a Patch source, place it, set scale up to 1000%, and rotate it;
- verify the preview stays responsive and comes only from immutable Original;
- commit, inspect the patch through a Base hole, and verify one-step Undo/Redo;
- cancel an uncommitted Patch and confirm no layer or unsaved change appears;
- repeat zoom/pan while authoring and confirm no coordinate drift or canvas corruption.

Clone, Blur, Smudge, persistence, export, packaging, GPU rendering, and AI remain deferred.
