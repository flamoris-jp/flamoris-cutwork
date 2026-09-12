# Parts Guriguri experiment

This spike explores human-guided selection and repair for the classical-cutout experiment. Original artwork pixels are never regenerated.

The current workflow is intentionally simple: use **Polygon Guriguri** to make a fast cutout, then use a constrained **Clone Repair Brush** to extend nearby original texture into the hole left behind.

## Run the latest experiment

From `experiments/classical-cutout`:

```bash
python app_clone_guriguri.py
```

Selection-only and comparison prototypes remain available:

```bash
python app_polygon_guriguri.py    # loose polygon -> outside-in Guriguri
python app_boundary_guriguri.py   # seed -> boundary-priority outward growth
python app_guriguri.py            # seed -> colour-continuity flood fill
```

## Polygon Guriguri interaction

1. Open an image.
2. Choose **Polygon Guriguri**.
3. Draw a deliberately loose polygon around the intended part. Precision is not required; the polygon only defines the search fence.
4. Press `Enter` or double-click to close the polygon.
5. Wheel up to shrink the mask inward.
6. Wheel down to restore toward the original loose polygon.
7. `Ctrl+wheel` keeps normal viewport zoom.
8. Stop when the magenta mask reaches a useful visual boundary.
9. Use **Create Part Layer** to commit it.
10. Use Mask Add / Mask Erase or exact Part Polygon when manual correction is faster.

## Clone Repair Brush interaction

After creating a Part layer, the Base automatically has a hole under that Part. Hide the Part layer in the layer panel when you want to inspect the repair directly.

1. Choose **Clone Source**.
2. Drag a green rectangle over clean source texture in the immutable original image.
3. Choose **Clone Paint**.
4. Drag through the cutout hole in the direction you want the texture to continue.
5. The source cursor moves by the same offset as the destination stroke, so a vertical hair stroke samples vertically and a horizontal skin stroke samples horizontally.
6. **Hole Only** is enabled by default, preventing clone paint from changing pixels outside existing Part holes.
7. A `repair` layer is created automatically below Base so the operation is non-destructive.
8. Use **Undo Clone Stroke** to revert a whole clone stroke.
9. Existing Blur Brush / Smudge Brush can be used on the repair layer for light cleanup.

The green source rectangle is a hard sampling fence. If the aligned source cursor reaches outside that rectangle, those destination pixels are not painted. Choose another source patch rather than wrapping or inventing pixels.

## Why the direction was reversed

The colour-continuity prototype could escape through chains of locally similar pixels. The seed-based boundary prototype improved directionality but still had to answer a difficult question: from a point inside an eye, which strong internal boundaries belong to the same semantic part, and which boundary is the desired outside edge?

Polygon Guriguri gives that semantic responsibility back to the human in a cheap form. The user says only **"the part is somewhere inside this rough fence"**. The software never needs to infer whether the click meant pupil, iris, sclera, eyelash, hair, or skin.

## Outer-in algorithm

`polygon_guriguri.py` treats the finalized loose polygon as a hard upper bound. Pixels outside the polygon can never enter the result.

Deletion begins on the polygon's inside edge and walks inward with a deterministic Dijkstra-style search:

- flat or gently shaded areas are cheap to peel away;
- strong line-art and colour boundaries are expensive to cross;
- every intermediate result is a nested subset of the original polygon;
- wheel-down restores exactly the same previous masks;
- the wheel controls **how much** is peeled, avoiding one-threshold percolation cliffs.

The intended success case is not automatic perfect segmentation. It is reducing a careful 20-50 point final polygon to a rough 4-10 point fence plus a few wheel notches.

## Clone repair algorithm

`clone_brush.py` performs an aligned clone operation using only immutable original pixels.

For each destination pixel in the brush stroke:

```text
source = source_anchor + (destination - destination_anchor)
```

The source must remain inside the user-drawn green rectangle. The destination can optionally be restricted to the union of existing Part masks, which is the Base hole region. The brush writes into a separate RGBA repair layer below Base, preserving the source image and cutout layers.

## Design principles

- Human decides meaning and texture direction.
- Polygon provides the semantic/search scope.
- Algorithm performs boundary fitting, not semantic segmentation.
- Clone repair copies source pixels instead of generating replacement art.
- Repairs are non-destructive and live on their own layer.
- AI, GrabCut, and generative redraw are unnecessary for the core interaction.
- Exact Polygon and Mask Add / Erase remain fallbacks.
- Selection preview is magenta; clone source guidance is green.

## Not included yet

- multi-polygon union/subtract
- user-placed hard keep/remove hints
- one-pixel mask Guriguri refinement
- automatic source-patch suggestion
- clone-source rotation/scale
- automatic front/back decisions
- semantic segmentation
- performance optimization for very large loose polygons
