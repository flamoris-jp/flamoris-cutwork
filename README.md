# Manual Part Editor Spike

Issue: #53

A disposable, standalone, non-ML experiment for quickly decomposing a human illustration into the few parts needed by a short moving-picture clip. This is a Phase 5 feasibility tool, not FLAMORIS production layer architecture and not a general image editor.

## Intended use

The primary use is **human figure / body-part decomposition for approximately eight-second moving-picture clips**:

- eye / eyelid material for blinking
- face, head, or selected hair sections
- arms, hands, and legs
- chest, shoulders, or upper body for breathing motion
- selected skirt or clothing sections
- manually sampled hidden-area patches

Static chairs, instruments, props, and backgrounds do not need semantic separation unless the intended motion exposes them.

## Quality target

Evaluate this spike by:

> **perceptual sufficiency × editing speed**

Pixel-perfect segmentation, single-hair accuracy, and defects visible only at extreme zoom are not the goal. Do not make the author solve a problem that a viewer cannot see at normal playback.

## Current workflow

1. **Open Image**. The decoded RGB source is copied once and remains immutable.
2. Choose **Part Polygon**, click exact vertices, then press **Enter** or double-click. **Esc** cancels.
3. If needed, use **Mask Add** / **Mask Erase** to correct the pending binary selection directly. These brushes do not invoke GrabCut.
4. Choose or type a semantic name, then press **Create Part Layer**.
5. Repeat the polygon/create step for every part that will move.
6. Select **Patch Source**, polygon-select a clean cheek/forehead/nearby region, and finalize it. The patch is sampled from the immutable original.
7. Use **Move Layer** plus the right-panel Scale and Rotation controls to cover a Base hole.
8. If a visible seam remains, apply a small **Blur Brush** or gentle **Smudge Brush** to the active derived layer.
9. Toggle, rename, select, delete, or reorder layers in the right panel.
10. Export a flattened RGBA PNG and/or one transparent PNG per layer. Save the active mask when useful.

Part Polygon is the final binary selection: inside is foreground, outside is background. It never runs GrabCut. Patch Source also uses a polygon, but creates a transformable sample layer instead of a part mask.

## Layer model

The panel is shown top-to-bottom. New content defaults to this visual stack:

1. Part layers (top)
2. Base
3. Patch layers (bottom)

The corresponding drawing order is **Patch → Base → Part**. Patch therefore appears through a hole removed from Base and stays behind the moving part.

Every result is derived from:

- immutable original source
- exact binary part masks or original-source patch polygons
- patch transform (translate, 10–1000% scale, -180–180° rotation)
- optional per-layer local Blur/Smudge edit operations

Base holes are the union of all part masks. A Patch is affine-warped directly into the output canvas, so 1000% scale does not allocate a 10× intermediate bitmap. Patch alpha receives a small edge feather.

Semantic name presets live in [`config/part-names.json`](config/part-names.json). The combo remains editable, so project-specific names do not require code changes.

## Viewport and layer controls

- Mouse wheel: cursor-centered zoom
- **Fit** / **100%**: standard view resets
- **Pan** drag or middle-button drag: move viewport
- Layer row checkmark: visibility
- Up / Down: reorder active layer
- Semantic name combo: preset or free-text rename
- Masks / Show active mask overlay: inspect active mask
- Patch Scale: 10–1000%
- Patch Rotation: -180–180°
- **Undo Local Edit**: removes the last Blur/Smudge operation from the active layer
- **Mask Add / Mask Erase**: manually corrects the pending polygon or active Part mask
- **Undo Mask Stroke**: restores the mask from before the last brush stroke

Selection, movement, and brush coordinates are converted back to original image space after zoom and pan.

Interactive redraw caches unchanged layer rasters and the current composite. Blur and Smudge operate on a small brush-local ROI instead of filtering or translating the full source image on every pointer event; slider redraw is briefly coalesced.

## Earlier experiment findings

- GrabCut was fast on a full figure against a simple, contrasting background, but did not understand semantic identity when a person touched a chair, guitar, hair, shadow, or similar-color background.
- Exact Polygon selection was more predictable for eye and body-part work than repeatedly steering GrabCut. Box, GrabCut, FG/BG Brush, and Refine were removed from this narrower editor spike.
- OpenCV Telea inpainting pulled dark hair, eyebrows, eyelashes, and shadow streaks into blink-oriented eye holes.
- Flat Skin Fill avoided dark smearing but looked like a uniform skin-colored patch.
- Gradient Skin Fill preserved broad shading but still looked synthetic and could not recreate useful local texture.
- Manual Patch Fill was the most controllable classical alternative: the author chooses plausible original pixels and places them behind the hole.
- Patch transform plus small local Blur/Smudge edits is a pragmatic blink-substrate workflow; it is not hidden-face reconstruction.

The removed fill methods are documented here as rejected experiment branches. They are intentionally not kept as runtime buttons or state.

## Run on Windows

```powershell
cd experiments/classical-cutout
py -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
python app.py
```

## Lightweight checks

```powershell
python -m py_compile app.py model.py image_ops.py layer_panel.py test_image_ops.py
python -m unittest -v test_image_ops.py
```

## Recommended real-image sequence

1. Create separate left/right eye or eyelid layers from a face close-up.
2. Toggle each part to confirm Base holes exactly match the polygons.
3. Polygon-sample cheek or forehead skin into one or more Patch layers.
4. Confirm the default **Patch → Base → Part** drawing order, then test manual reordering.
5. Move, scale, and rotate a patch until it covers the eye hole with Cutout hidden.
6. Test a small Blur/Smudge pass only where the seam is visible at normal playback.
7. Zoom and pan during selection and confirm exported masks still land on the intended original pixels.
8. Export individual PNGs and assemble a short blink or breathing motion outside this spike.

## Known limitations

- Polygon edges are binary; this is not alpha matting for hair or translucent material.
- Smudge is deliberately simple translation-based pixel pushing, not a paint engine.
- Many accumulated local edit operations can make redraw slower on large source images.
- Patches use scale, rotation, and translation only; there is no perspective warp.
- State is not persisted as a FLAMORIS Project and there is no undo/redo architecture beyond the last local edit.
- PSD export is not included. Individual transparent PNGs avoid adding a new PSD dependency/license decision to this disposable spike.
- No AI/ML, face landmarks, body recognition, hidden-part generation, animation, Scene mutation, or production integration is included.

## Decision gate

After testing real artwork, record whether exact manual parts plus transformed source patches are fast and plausible enough for normal-playback blink/breathing clips, and which operations deserve a Phase 5 product design.
